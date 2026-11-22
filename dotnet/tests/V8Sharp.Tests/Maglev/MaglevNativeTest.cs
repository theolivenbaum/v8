// Tests of the native Maglev backend (MaglevNativeCodeGenerator,
// MaglevNativeCode): every script runs in the interpreter, with the IL
// backend (--no-maglev-native) and with the native backend, and the
// transcripts must be the same; the native runs must have compiled the
// functions natively. Random programs (as Ryu's random-program differential
// test of its native code generator) cover the arithmetic, the checks, the
// deopts and the phi moves; V8SHARP_NATIVE_FUZZ=n runs n programs.
using System.Globalization;
using System.Text;
using V8Sharp.Codegen;
using V8Sharp.Maglev;

namespace V8Sharp.Tests.Maglev;

public class MaglevNativeTest
{
    const string kNative = "--maglev --no-sparkplug --no-concurrent-recompilation --maglev-native --no-maglev-native-loops-only";
    const string kIL = "--maglev --no-sparkplug --no-concurrent-recompilation --no-maglev-native";
    const string kInterpreter = "--no-maglev --no-sparkplug";

    static readonly object s_counterLock = new();

    /// <summary>The transcripts of the three configurations, and the native compilations of the native run.</summary>
    static (string Interpreted, string IL, string Native, int NativeCompiles) RunAll(string source)
    {
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        string il = MaglevCompilerTest.Run(kIL, source);
        // (The counter is process-wide; the lock keeps parallel tests out of each other's counts.)
        lock (s_counterLock)
        {
            int before = MaglevNativeCodeGenerator.Generated;
            string native = MaglevCompilerTest.Run(kNative, source);
            return (interpreted, il, native, MaglevNativeCodeGenerator.Generated - before);
        }
    }

    static void AssertSame(string source, bool expectNative = true)
    {
        (string interpreted, string il, string native, int compiles) = RunAll(source);
        Assert.True(interpreted == il, $"IL backend differs:\n  interpreted: {interpreted}\n  IL:          {il}");
        Assert.True(interpreted == native, $"native backend differs:\n  interpreted: {interpreted}\n  native:      {native}\n{source}");
        if (expectNative && ExecutableMemory.Supported) Assert.True(compiles > 0, "nothing was compiled natively");
    }

    const string kPrelude = """
        function opt(f, ...warm) {
          %PrepareFunctionForOptimization(f);
          for (const w of warm) f(...w);
          for (const w of warm) f(...w);
          %OptimizeFunctionOnNextCall(f);
        }
        const out = [];
        function rec(v) { out.push(Object.is(v, -0) ? "-0" : String(v)); }
        """;

    public static TheoryData<string> Snippets => new()
    {
        // Int32 arithmetic: overflow, -0, division and modulus deopts.
        """
        function mul(a, b) { return a * b; }
        opt(mul, [3, 4]); rec(mul(5, 6)); rec(mul(0, -5)); rec(mul(1 << 20, 1 << 20)); rec(mul(1.5, 2));
        function dv(a, b) { return a / b; }
        opt(dv, [10, 2]); rec(dv(12, 3)); rec(dv(7, 2)); rec(dv(1, 0)); rec(dv(0, -3)); rec(dv(-2147483648, -1));
        function md(a, b) { return a % b; }
        opt(md, [10, 3]); for (const [a, b] of [[17, 5], [-17, 5], [-10, 5], [4, 0], [7, -1], [-7, -1], [-2147483648, -2147483648], [5, -2147483648]]) rec(md(a, b));
        function neg(x) { return -x; }
        opt(neg, [5]); rec(neg(7)); rec(neg(0)); rec(neg(-2147483648));
        function bits(x, y) { return ((x << 3) ^ (y >> 1)) + (x >>> 28) + (~y & 255) | (x & y); }
        opt(bits, [5, 9]); rec(bits(123456, 98765)); rec(bits(-5, -9)); rec(bits(1.5, 2.5)); rec(bits(-1, 31)); rec(bits(1, 32));
        """,
        // Loops: int32 sums with overflow into doubles, a long loop (poll exits), OSR.
        """
        function sum(n) { let s = 0; for (let i = 0; i < n; i++) s += i; return s; }
        opt(sum, [10]); rec(sum(100)); rec(sum(100000)); rec(sum(5));
        function long(n) { let s = 0; for (let i = 0; i < n; i++) { s = (s + i * 7) % 1000003; } return s; }
        opt(long, [10]); rec(long(3000000));
        function osr(n) { let s = 0; for (let i = 0; i < n; i++) { s += i & 3; } return s; }
        rec(osr(2000000));
        function nested(n) { let s = 0; for (let i = 0; i < n; i++) for (let j = 0; j < i; j++) s = (s + i * j) | 0; return s; }
        opt(nested, [5]); rec(nested(300));
        """,
        // Doubles: arithmetic, Math, comparisons with NaN, min/max with -0.
        """
        function fl(a) { let s = 0.5; for (let i = 0; i < a.length; i++) s += a[i] * 1.5 - a[i] / 3; return s; }
        let darr = []; for (let i = 0; i < 50; i++) darr.push(i * 0.75);
        opt(fl, [darr]); rec(fl(darr)); rec(fl([1, 2, 3])); rec(fl({length: 2, 0: 1, 1: 2}));
        function mathy(x) { return Math.floor(x) + Math.ceil(x) * 2 + Math.round(x) * 3 + Math.abs(x) + Math.sqrt(Math.abs(x)) + Math.min(x, 1) + Math.max(x, -1); }
        opt(mathy, [1.25], [-2.5]); for (const x of [1.25, -2.5, 2.5, -0.5, 0.5, 0.49999999999999994, 1e300, -0, NaN, 3, -Infinity]) rec(mathy(x));
        function rnd(x) { return Math.round(x); }
        opt(rnd, [1.5], [-2.25]); for (const x of [0.5, -0.5, 1.5, -1.5, 2.5, -0, 0.49999999999999994, -0.49999999999999994, 4503599627370495.5, NaN]) rec(rnd(x));
        function mn(a, b) { return Math.min(a, b); } function mx(a, b) { return Math.max(a, b); }
        opt(mn, [1.5, 2.5]); opt(mx, [1.5, 2.5]);
        for (const [a, b] of [[0, -0], [-0, 0], [NaN, 1], [1, NaN], [3.5, -2], [-7, -7.5], [-0, -0]]) { rec(mn(a, b)); rec(mx(a, b)); }
        function cmp(a, b) { let c = 0; if (a < b) c += 1; if (a <= b) c += 2; if (a > b) c += 4; if (a >= b) c += 8; if (a == b) c += 16; if (a != b) c += 32; return c; }
        opt(cmp, [1.5, 2.5], [3, 3]); for (const [a, b] of [[1.5, 2.5], [2, 2], [NaN, 1], [1, NaN], [NaN, NaN], [5.5, -1], [-0, 0]]) rec(cmp(a, b));
        function cmpv(a, b) { return [a < b, a <= b, a > b, a >= b, a === b]; }
        function c1(a, b) { return a < b; } function c2(a, b) { return a === b; }
        opt(c1, [1.5, 2.5]); opt(c2, [1.5, 2.5]); for (const [a, b] of [[1, 2], [NaN, NaN], [2, 1], [3, 3]]) { rec(c1(a, b)); rec(c2(a, b)); }
        """,
        // Float64Modulus (fmod): signs, zeros, infinities, NaN, huge quotients.
        """
        function fm(a, b) { return a % b; }
        opt(fm, [5.5, 2], [-7.25, 3]);
        for (const [a, b] of [[5.5, 2], [-5.5, 2], [5.5, -2], [-0, 3], [0, -3], [1, 0], [Infinity, 2], [2, Infinity], [-2, -Infinity], [NaN, 2],
                              [2, NaN], [1e308, 3.3], [-1e300, 1e-300], [0.1, 0.01], [7, 7], [2 ** 60 + 1, 3]]) rec(fm(a, b));
        function fml(n) { let s = 0.5; for (let i = 1; i < n; i++) s = (s * 7.3 + i) % (i * 0.37 + 1); return s; }
        opt(fml, [10]); rec(fml(1000));
        """,
        // ToInt32 of doubles in and out of int64 range (TruncateFloat64ToInt32).
        """
        function trunc(x) { return x | 0; }
        opt(trunc, [1.5], [2.7]);
        for (const x of [1.5, -2.7, 4294967296.5, -4294967297, 1e20, -1e20, 2 ** 63, -(2 ** 63), 2 ** 64 + 2 ** 40, 2 ** 84 + 2 ** 33, 2 ** 1023,
                         NaN, Infinity, -Infinity, 2147483648, -2147483649, 1e-300, -0]) rec(trunc(x));
        function sh(x) { return x >>> 0; }
        opt(sh, [1.5]); for (const x of [-1.5, 4294967295.5, -4294967296.25, 2 ** 40 + 7.5]) rec(sh(x));
        """,
        // Arrays: double stores (NaN canonicalized), holes, element kinds that deopt.
        """
        function arrst(a) { for (let i = 0; i < a.length; i++) a[i] = a[i] * 2 + 0.5; return a[3]; }
        let sa = [1.5, 2.5, 3.5, 4.5, 5.5]; opt(arrst, [sa.slice()]); rec(arrst(sa)); rec(sa.join());
        function nanst(a, v) { a[1] = v; return a[1]; }
        let na = [1.5, 2.5, 3.5]; opt(nanst, [na, 0.5]); rec(nanst(na, NaN)); rec(nanst(na, -0)); rec(Object.is(na[1], NaN));
        let f64 = new Float64Array(1); function bitsOf(a) { nanst(a, 0 / 0); f64[0] = a[1]; return new Uint32Array(f64.buffer).join(); }
        rec(bitsOf([1.5, 2.5, 3.5]));
        function elems(a) { let s = 0; for (let i = 0; i < a.length; i++) s += a[i]; return s; }
        let ia = [1, 2, 3, 4, 5, 6]; opt(elems, [ia]); rec(elems(ia)); rec(elems([1, , 3])); rec(elems([1, "a"])); rec(elems([1.5, 2]));
        function holey(a) { let s = 0; for (let i = 0; i < a.length; i++) { const v = a[i]; if (v !== undefined) s += v; } return s; }
        opt(holey, [[1.5, , 2.5]]); rec(holey([1.5, , 2.5, , 4])); rec(holey([1, 2]));
        """,
        // Tagged phis: values of several kinds through a loop, returned and in deopt frames.
        """
        function phi(a, n) { let x = a; for (let i = 0; i < n; i++) { if (i == 3) x = i; } return x; }
        opt(phi, [{}, 5], [null, 2]); rec(phi("str", 2)); rec(phi("s", 5)); rec(phi(undefined, 1)); rec(phi(true, 0)); rec(phi(1.5, 9));
        function sel(c, a, b) { const r = c ? a : b; return r; }
        function pick(k) { let r = 0; for (let i = 0; i < k; i++) r = (i & 1) ? r + 1.5 : r - 1; return r; }
        opt(pick, [5]); rec(pick(10)); rec(pick(0)); rec(pick(1000001));
        function eq(a, b) { return a === b; }
        opt(eq, [1, 1], [1, 2]); rec(eq(1, 1)); rec(eq(NaN, NaN)); rec(eq(0, -0)); rec(eq(1.5, 1.5)); rec(eq("a", "a")); rec(eq(null, undefined));
        """,
        // A returned value rebuilt by repeating its load: the load's index must
        // stay live to the Return (review finding: it was reused by k + 1).
        """
        function ret(a, i, j) { const x = a[i | 0]; const k = j | 0; if (((k + 1) | 0) > 0) return x; return 0; }
        const strs = ["s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7"];
        opt(ret, [strs, 1, 2], [strs, 3, 4]); for (const [i, j] of [[1, 5], [2, 0], [0, 6], [6, -1], [3, 2]]) rec(ret(strs, i, j));
        function retf(o, n) { const x = o.p; let s = 0; for (let i = 0; i < n; i++) s = (s + i) | 0; return s > 3 ? x : 0; }
        opt(retf, [{p: "a", q: "b"}, 3]); rec(retf({p: "x", q: "y"}, 5)); rec(retf({p: "x", q: "y"}, 1));
        """,
        // Phi cycles: swaps and rotations at the back edge (parallel moves).
        """
        function swap(x, y, n) { let a = x, b = y; for (let i = 0; i < n; i++) { const t = a; a = b; b = t; } return a * 10 + b; }
        opt(swap, [1, 2, 3]); for (const n of [0, 1, 2, 3, 7]) rec(swap(1, 2, n));
        function rot(n) { let a = 1, b = 2, c = 3; for (let i = 0; i < n; i++) { const t = a; a = b; b = c; c = t; } return a * 100 + b * 10 + c; }
        opt(rot, [4]); for (const n of [0, 1, 2, 5, 9]) rec(rot(n));
        function dswap(n) { let a = 0.5, b = 1.25, k = 0; for (let i = 0; i < n; i++) { const t = a; a = b + k; b = t; k = (k + 1) | 0; } return a - b * 3; }
        opt(dswap, [3]); for (const n of [0, 1, 4, 11]) rec(dswap(n));
        """,
        // Untagged frame-state values that die at a deopting operation: the
        // deopt must see them (no register reuse at that position).
        """
        function dying(x, y) { const a = x | 0, b = y | 0; return a * b; }
        opt(dying, [3, 4]); rec(dying(65536, 65536)); rec(dying(-1, 0)); rec(dying(7, 6));
        function dyingAdd(x, y) { const a = (x | 0) * 2, b = (y | 0) + 1; return a + b; }
        opt(dyingAdd, [3, 4]); rec(dyingAdd(1073741823, 1073741823)); rec(dyingAdd(5, 6));
        function dying2(x, y) { const a = x | 0, b = y | 0; return a * b + b; }
        opt(dying2, [3, 4]); rec(dying2(65536, 65536)); rec(dying2(-3, 0)); rec(dying2(9, 9));
        function dying3(x, y) { const a = (x | 0) + 1, b = y | 0; const c = a * b; return c + b * 2; }
        opt(dying3, [3, 4]); rec(dying3(65535, 65536)); rec(dying3(-1, -5)); rec(dying3(4, 5));
        """,
        // A value whose only use after a loop is the frame state of a deopt
        // (an unexecuted branch): it must stay in its location through the loop.
        """
        function late(x, n) { const a = (x | 0) * 3; let s = 0, t = 1; for (let i = 0; i < n; i++) { s = (s + i * t) | 0; t = (t ^ i) & 15; } if (n > 1000) return a; return s; }
        opt(late, [5, 10]); rec(late(5, 20)); rec(late(7, 2000)); rec(late(-4, 1500));
        """,
        // Managed nodes (run by the managed side, then the native code resumes):
        // calls and generic operations before a native loop.
        """
        function helper(x) { return x * 2 + 1; }
        function withCall(a, n) { const k = helper(a.length) | 0; const t = String(n).length; let s = 0; for (let i = 0; i < n; i++) s = (s + i * k + t) | 0; return s; }
        opt(withCall, [[1, 2], 5]); rec(withCall([1, 2, 3], 10)); rec(withCall([], 1000)); rec(withCall([1], 0));
        function tb(v, n) { const b = !v; const c = typeof v === "string"; let s = 0; for (let i = 0; i < n; i++) s += i; return [b, c, s].join(); }
        opt(tb, [0, 2], ["", 3], [{}, 4]); for (const v of [0, 1, "", "a", null, undefined, {}, NaN, -0, 2n]) rec(tb(v, 5));
        """,
        // Stores in loops: a number over a number inline, anything else through the managed side.
        """
        function P() { this.x = 0; this.y = 1.5; }
        function fields(o, n) { for (let i = 0; i < n; i++) { o.x = o.x + i; o.y = o.y * 0.5 + i; } return o.x + o.y; }
        opt(fields, [new P(), 3]); rec(fields(new P(), 100)); const po = new P(); po.x = "s"; rec(fields(po, 3)); rec(po.x);
        function elems(a, n) { for (let i = 0; i < n; i++) a[i % a.length] = a[(i + 1) % a.length] + i; return a.join(); }
        opt(elems, [[1, 2, 3], 4]); rec(elems([1, 2, 3, 4], 20)); rec(elems([1, "x", 3], 5)); rec(elems([1, {}, 3], 2));
        let counter = 0;
        function ctx(n) { for (let i = 0; i < n; i++) counter = counter + i; return counter; }
        opt(ctx, [3]); rec(ctx(100)); counter = "c"; rec(ctx(3)); counter = 0.5; rec(ctx(10));
        function closure() { let acc = 0; return function (n) { for (let i = 0; i < n; i++) acc += i * 2; return acc; }; }
        const cl = closure(); opt(cl, [3]); rec(cl(50)); rec(cl(5));
        """,
        // Globals (property cells), typed array lengths.
        """
        var g1 = 5;
        function glob(n) { let s = g1; for (let i = 0; i < n; i++) s = (s + i) | 0; g1 = s; return s; }
        opt(glob, [3]); rec(glob(100)); rec(g1); g1 = 1.5; rec(glob(2));
        function tlen(t, n) { const len = t.length; let s = 0; for (let i = 0; i < n; i++) s = (s + len + i) | 0; return s; }
        opt(tlen, [new Float64Array(8), 3]); rec(tlen(new Float64Array(5), 10)); rec(tlen(new Uint8Array(3), 4));
        """,
        // Typed arrays: loads and stores of every element kind, clamping, Float32
        // rounding, a detached buffer (deopt), a length-tracking view (the managed path).
        """
        function tsum(t, n) { let s = 0; for (let i = 0; i < n; i++) s += t[i]; return s; }
        function tfill(t, n, v) { for (let i = 0; i < n; i++) t[i] = v * i - 3; return t.join(); }
        const kinds = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, Float32Array, Float64Array];
        for (const K of kinds) {
          const t = new K(16);
          opt(tfill, [new K(4), 4, 3]); rec(tfill(t, 16, 37));
          opt(tsum, [new K(4), 4]); rec(tsum(t, 16));
        }
        function tf(t, n, v) { for (let i = 0; i < n; i++) t[i] = v / (i + 1); return t.join(); }
        opt(tf, [new Float32Array(4), 4, 1.5]); rec(tf(new Float32Array(8), 8, 0.1)); rec(tf(new Float64Array(8), 8, 0.1));
        function tlen2(t) { let s = 0; for (let i = 0; i < t.length; i++) s += t[i]; return s; }
        const tb = new Float64Array(10).fill(1.5); opt(tlen2, [tb]); rec(tlen2(tb));
        const rab = new ArrayBuffer(16, { maxByteLength: 64 }); const lt = new Float64Array(rab); lt.fill(2);
        rec(tlen2(lt)); rab.resize(32); lt.fill(3); rec(tlen2(lt));
        const det = new Float64Array(4).fill(1); rec(tlen2(det)); %ArrayBufferDetach(det.buffer); rec(tlen2(det));
        """,
        // Rebuilt values across stores and resumes (mjsunit regress-100409,
        // turbolev/holey-float64-deopt): a context slot loaded, then stored
        // inline, then returned; a holey double's hole through managed calls.
        """
        function outer() { var val = 0; function foo() { val = 0; val; var z = false; var y = true;
          if (!z) { while (z = !z) { if (y) val++; } } return val++; } return foo; }
        const fo = outer(); %PrepareFunctionForOptimization(fo); rec(fo()); rec(fo()); %OptimizeFunctionOnNextCall(fo); rec(fo()); rec(fo());
        const harr = [, 1.1, 2.2];
        function hol(a, x) { const v1 = a[0]; for (let i = 0; i < 10; i++) {} const v2 = x * 10; return [v1, v2]; }
        opt(hol, [harr, 4]); rec(JSON.stringify(hol(harr, 42))); rec(JSON.stringify(hol(harr, 1000000000)));
        """,
        // A managed node whose own value is in its lazy deopt frame (it must not
        // look live before the node: mjsunit compiler/keyed-load-on-string).
        """
        var str = "hello";
        function at4() { return str[4]; }
        function at5() { return str[5]; }
        opt(at4, []); rec(at4()); opt(at5, []); rec(at5()); str = "hi"; rec(at4());
        """,
        // A call that invalidates the caller's code (a map it depends on changes): lazy deopt after it.
        """
        const proto = { v: 1 };
        function Obj() {}
        Obj.prototype = proto;
        function poke(k) { if (k === 7) proto.v = 2.5; return k; }
        function lazy(o, k) { const r = poke(k); let s = 0; for (let i = 0; i < 10; i++) s += o.v; return s + r; }
        opt(lazy, [new Obj(), 1]); rec(lazy(new Obj(), 3)); rec(lazy(new Obj(), 7)); rec(lazy(new Obj(), 4));
        """,
        // Deopts from inside loops with live values of each representation (the frame state rebuilt).
        """
        function walk(a, n, k) {
          let s = 0, d = 0.5, t = a;
          for (let i = 0; i < n; i++) { s = (s + a[i % a.length] * k) | 0; d = d * 1.5 + s; if (d > 1e9) d = 0.25; }
          return [s, d, t === a];
        }
        let wa = []; for (let i = 0; i < 20; i++) wa.push(i);
        opt(walk, [wa, 30, 3]); rec(walk(wa, 50, 3)); wa[7] = 0.5; rec(walk(wa, 50, 3)); wa[8] = 'x'; rec(walk(wa, 50, 3)); rec(walk(wa, 50, 1.5));
        function ovf(n) { let s = 1; for (let i = 0; i < n; i++) s = s * 3; return s; }
        opt(ovf, [3]); rec(ovf(5)); rec(ovf(30)); rec(ovf(40));
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void NativeCodeGivesTheInterpretersResults(string body) => AssertSame(kPrelude + body + "\nout.join('|')");

    [Fact]
    public void DirectEntryDeoptsIntoAFrame()
    {
        // A leaf called from optimized code through its frameless direct entry,
        // deoptimizing (overflow, a non-Smi) inside it: the frame is built at the
        // deopt and the call returns the interpreter's result.
        AssertSame(kPrelude + """
            function leaf(a, b) { return a * b + 1; }
            function caller(n) { let s = 0; for (let i = 0; i < n; i++) s = s + leaf(i, i) | 0; return s; }
            opt(leaf, [2, 3]);
            rec(leaf(3, 4));
            rec(caller(100));
            rec(leaf(65536, 65536)); rec(leaf(1.5, 2));
            opt(leaf, [2, 3]); rec(leaf(1, 2)); rec(leaf("a", 2));
            out.join('|')
            """);
    }

    [Fact]
    public void InterruptsAreServedAndTheLoopResumes()
    {
        // API interrupts requested from another thread while a native loop
        // runs: served at the loop's interrupt check, after which the loop goes
        // on in the native code (it is not invalidated).
        Assert.SkipUnless(ExecutableMemory.Supported, "native code needs Linux on x64");
        var flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --maglev-native --no-maglev-native-loops-only --no-concurrent-recompilation");
        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            Compiler.CompileAndRun(isolate, """
                function spin(n, o) { var s = 0; for (var i = 0; i < n; i++) s = (s + i * 7 + o.length) | 0; return s; }
                %PrepareFunctionForOptimization(spin);
                spin(10, []); spin(10, []);
                %OptimizeMaglevOnNextCall(spin);
                spin(10, []);
                """);
            int served = 0;
            using var stop = new CancellationTokenSource();
            var requester = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    isolate.StackGuard.RequestApiInterrupt((_, _) => Interlocked.Increment(ref served), null);
                    Thread.Sleep(1);
                }
            });
            requester.Start();
            string result;
            try
            {
                result = ObjectOps.ToString(isolate, Compiler.CompileAndRun(isolate,
                    "var r = []; var o = [1, 2]; for (var k = 0; k < 10; k++) r.push(spin(3000000, o)); r.push(%GetOptimizationStatus(spin) & 8); r.join()")).ToString();
            }
            finally
            {
                stop.Cancel();
                requester.Join();
            }
            string expected = ObjectOps.ToString(isolate, Compiler.CompileAndRun(isolate, """
                var s = 0; for (var i = 0; i < 3000000; i++) s = (s + i * 7 + 2) | 0;
                var e = []; for (var k = 0; k < 10; k++) e.push(s); e.push(8); e.join()
                """)).ToString();
            Assert.Equal(expected, result);
            Assert.True(served > 0);
            var spin = (JSFunction)Compiler.CompileAndRun(isolate, "spin").Object;
            Assert.True(((FeedbackVector)spin.RawFeedbackCell.Value!).MaglevCode!.IsNative);
        }
    }

    [Fact]
    public void UnsupportedGraphsUseTheILBackend()
    {
        // A graph with a call in a loop is generated as IL; --no-maglev-native generates IL for every graph.
        var flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --maglev-native --no-maglev-native-loops-only --no-concurrent-recompilation");
        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            JSValue result = Compiler.CompileAndRun(isolate, """
                function g(x) { let s = 0; for (let i = 0; i < x; i++) s += String(i).length; return s; }
                function h(x) { return x + 1; }
                %PrepareFunctionForOptimization(g); %PrepareFunctionForOptimization(h);
                g(1); g(2); h(1); h(2);
                %OptimizeFunctionOnNextCall(g); %OptimizeFunctionOnNextCall(h);
                g(3); h(3);
                [g, h]
                """);
            var array = (JSArray)result.Object;
            MaglevCode Code(int i) => ((FeedbackVector)((JSFunction)JSReceiver.GetElement(isolate, array, (uint)i).Object).RawFeedbackCell.Value!).MaglevCode!;
            Assert.False(Code(0).IsNative);
            Assert.Equal(ExecutableMemory.Supported, Code(1).IsNative);
        }
        flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --no-maglev-native --no-concurrent-recompilation");
        isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            JSValue h = Compiler.CompileAndRun(isolate, """
                function h(x) { return x + 1; }
                %PrepareFunctionForOptimization(h); h(1); h(2); %OptimizeFunctionOnNextCall(h); h(3);
                h
                """);
            Assert.False(((FeedbackVector)((JSFunction)h.Object).RawFeedbackCell.Value!).MaglevCode!.IsNative);
        }
    }

    // ---- Random programs ------------------------------------------------------------------------------------

    /// <summary>A random function over int32, double and array values with a loop, branches and stores.</summary>
    internal static string RandomProgram(int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        string[] vars = ["x0", "x1", "x2", "x3", "x4"];
        string V() => vars[rng.Next(vars.Length)];
        string C() => rng.Next(8) switch
        {
            0 => "0",
            1 => "-1",
            2 => "1.5",
            3 => "2147483647",
            4 => "-0.5",
            5 => rng.Next(1, 40).ToString(CultureInfo.InvariantCulture),
            6 => "0x7fff",
            _ => (rng.Next(-1000, 1000) / 8.0).ToString("R", CultureInfo.InvariantCulture),
        };
        string Operand() => rng.Next(4) == 0 ? C() : V();
        string[] binary = ["+", "-", "*", "/", "%", "|", "&", "^", "<<", ">>", ">>>"];
        string[] compare = ["<", "<=", ">", ">=", "===", "!=="];
        string[] unary = ["Math.floor", "Math.ceil", "Math.round", "Math.abs", "Math.sqrt", "-", "~", "+"];
        void Statement(int depth)
        {
            switch (rng.Next(depth > 1 ? 9 : 11))
            {
                case 0:
                case 1:
                case 2:
                    sb.Append($"{V()} = {Operand()} {binary[rng.Next(binary.Length)]} {Operand()};");
                    break;
                case 3:
                    sb.Append($"{V()} = ({Operand()} {binary[rng.Next(binary.Length)]} {Operand()}) | 0;");
                    break;
                case 4:
                    sb.Append($"{V()} = {unary[rng.Next(unary.Length)]}({Operand()});");
                    break;
                case 5:
                    sb.Append($"{V()} = Math.{(rng.Next(2) == 0 ? "min" : "max")}({Operand()}, {Operand()});");
                    break;
                case 6:
                    sb.Append($"{V()} = a[i % a.length];");
                    break;
                case 7:
                    sb.Append($"d[i % d.length] = {Operand()};");
                    break;
                case 8:
                    if (rng.Next(2) == 0)
                    {
                        sb.Append($"{V()} = {V()} % 1000;");
                    }
                    else
                    {
                        // A swap (a phi cycle at the back edge).
                        string a = V(), b = V();
                        sb.Append($"t = {a}; {a} = {b}; {b} = t;");
                    }
                    break;
                default:
                    sb.Append($"if ({Operand()} {compare[rng.Next(compare.Length)]} {Operand()}) {{ ");
                    Statement(depth + 1);
                    sb.Append(" } else { ");
                    Statement(depth + 1);
                    sb.Append(" }");
                    break;
            }
            sb.Append(' ');
        }
        sb.Append("function f(p, q, a, d, n) { let x0 = p, x1 = q, x2 = 0, x3 = 1.5, x4 = n, i = 0, t = 0; ");
        int pre = rng.Next(3);
        for (int k = 0; k < pre; k++) Statement(0);
        sb.Append("for (i = 0; i < n; i++) { ");
        int body = rng.Next(2, 7);
        for (int k = 0; k < body; k++) Statement(0);
        sb.Append("} ");
        int post = rng.Next(2);
        for (int k = 0; k < post; k++) Statement(0);
        sb.Append($"return {V()}; }}\n");
        return sb.ToString();
    }

    static string RandomScript(int seed) => RandomProgram(seed) + """
        function show(v) { return Object.is(v, -0) ? "-0" : String(v); }
        const out = [];
        const ints = [3, 1, 4, 1, 5, 9, 2, 6], doubles = [0.5, 1.25, -2.5, 3.75];
        %PrepareFunctionForOptimization(f);
        for (let k = 0; k < 3; k++) out.push(show(f(k, 7 - k, ints, doubles.slice(), 8)));
        %OptimizeFunctionOnNextCall(f);
        const inputs = [[1, 2, 8], [0, -0, 5], [2147483647, 1, 20], [-2147483648, -1, 3], [1.5, 2.5, 9], [NaN, 3, 4], [1e10, -7, 6],
                        [5, 3, 1000], [65536, 65536, 7], [-3, 0, 0]];
        for (const [p, q, n] of inputs) out.push(show(f(p, q, ints, doubles.slice(), n)) + "/" + show(f(p, q, doubles, ints.slice(), n)));
        out.push(show(f(1, 2, [1, , 3], [0.5, 1.5], 9)), show(f(1, 2, [1, "s", 3], [0.5, 1.5], 9)), show(f(1, 2, ints, [1, 2], 9)));
        out.join('|')
        """;

    static int FuzzCount() => int.TryParse(Environment.GetEnvironmentVariable("V8SHARP_NATIVE_FUZZ"), out int n) ? n : 150;

    public static TheoryData<int> FuzzBatches()
    {
        var data = new TheoryData<int>();
        int batches = Math.Max(1, FuzzCount() / 50);
        for (int b = 0; b < batches; b++) data.Add(b);
        return data;
    }

    [Theory]
    [MemberData(nameof(FuzzBatches))]
    public void RandomProgramsGiveTheInterpretersResults(int batch)
    {
        int native = 0;
        for (int k = 0; k < 50; k++)
        {
            int seed = batch * 50 + k;
            string script = RandomScript(seed);
            if (Environment.GetEnvironmentVariable("V8SHARP_NATIVE_FUZZ_DUMP") is { } dump) File.WriteAllText(Path.Combine(dump, $"fuzz{seed}.js"), script);
            (string interpreted, string il, string nativeResult, int compiles) = RunAll(script);
            Assert.True(interpreted == il, $"seed {seed}: IL backend differs\n  interpreted: {interpreted}\n  IL:          {il}\n{RandomProgram(seed)}");
            Assert.True(interpreted == nativeResult, $"seed {seed}: native backend differs\n  interpreted: {interpreted}\n  native:      {nativeResult}\n{RandomProgram(seed)}");
            if (compiles > 0) native++;
        }
        // Most programs compile natively (some graphs have nodes it does not support).
        if (ExecutableMemory.Supported)
        {
            Assert.True(native >= 25, $"only {native} of 50 programs compiled natively; declined: " +
                                      string.Join(", ", MaglevNativeCodeGenerator.DeclineReasons.Select(static e => e.Key + " x" + e.Value)));
        }
    }
}
