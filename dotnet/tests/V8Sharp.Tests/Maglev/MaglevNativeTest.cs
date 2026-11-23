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
        // Stores to global variables inline (StorePropertyCellValue): numbers,
        // objects (the write barrier), and reads back after collections.
        """
        var gN = 0, gO = null, gS = 'a';
        function gst(n) { for (let i = 0; i < n; i++) { gN = gN + i; gO = { i: i, prev: gO }; if (i % 3 == 0) gS = gS + i; } return gN + ':' + gO.i + ':' + (gO.prev ? gO.prev.i : -1) + ':' + gS.length; }
        opt(gst, [3]);
        rec(gst(10)); %SimulateNewspaceFull(); rec(gst(200)); gO = null; rec(gst(5)); let d = 0; for (let o = gO; o; o = o.prev) d++; rec(d);
        """,
        // Assigned parameters written to the frame inline (EmitStoreRegisterFastPath)
        // before calls that can read them (function.arguments).
        """
        function peek() { return String(asg.arguments[0]) + '/' + String(asg.arguments[1]); }
        function asg(a, b) { let r = ''; for (let i = 0; i < 3; i++) { a = a + i; b = i % 2 ? { k: i } : 'z' + i; r += peek() + ';'; } return r + (typeof b === 'object' ? b.k : b); }
        opt(asg, [1, 'x'], [2.5, 'y']);
        rec(asg(10, 'q')); rec(asg(0.5, null)); rec(asg('s', 1));
        """,
        // == of receivers inline (EmitReceiverEqualityFastPath), feedback that
        // covers receivers (or not), and other values on the managed side.
        """
        const A = { n: 1 }, B = { n: 2 }, F = function () {}, Arr = [1];
        function eqo(x, y) { let c = 0; for (let i = 0; i < 4; i++) { if (x == y) c++; if (x != y) c += 10; } return c; }
        opt(eqo, [A, B], [A, A]);
        for (const [x, y] of [[A, A], [A, B], [F, F], [Arr, Arr], [A, null], [null, undefined], [A, 'x'], [1, '1'], [{ valueOf() { return 1; } }, 1], [Arr, '1'], [NaN, NaN]]) rec(eqo(x, y));
        function eqm(x, y) { return (x == y) + ',' + (x === y); }
        opt(eqm, [A, B], [1, 1], ['a', 'a'], [null, undefined]);
        for (const [x, y] of [[A, A], [B, A], [F, A], [1, 1], ['a', 'b'], [A, undefined]]) rec(eqm(x, y));
        """,
        // new Array() from an allocation site's pool (EmitArrayFromSiteFastPath),
        // the site's kind changing (smi, double, object): fresh arrays every time.
        """
        function Coll() { this.elms = new Array(); }
        function fill(n, k) { const c = new Coll(); for (let i = 0; i < n; i++) c.elms.push(k === 0 ? i : k === 1 ? i + 0.5 : { v: i }); return c; }
        function sum(n, k) { let s = 0, t = 0; for (let j = 0; j < 40; j++) { const c = fill(n, k); t += c.elms.length; const e = c.elms; for (let i = 0; i < e.length; i++) s += typeof e[i] === 'object' ? e[i].v : e[i]; } return s + ':' + t; }
        opt(Coll, []); opt(fill, [3, 0]); opt(sum, [2, 0]);
        rec(sum(5, 0)); rec(sum(5, 1)); rec(sum(5, 2)); rec(sum(0, 0)); rec(sum(7, 0));
        const all = []; for (let i = 0; i < 70; i++) all.push(new Coll().elms); all[3].push(1); rec(all[4].length + ',' + all[3].length + ',' + (all[5] === all[6]));
        """,
        // Array.prototype.pop inline (EmitArrayPopFastPath): objects, holes, empty
        // arrays, copy-on-write literals and double arrays (the managed side).
        """
        function drain(a) { let s = 0, n = 0; for (;;) { const v = a.pop(); if (a.length < 0 || n++ > 20) break; if (v === undefined && a.length === 0) break; s += v === undefined ? 100 : typeof v === 'object' ? v.k : v; } return s + ':' + a.length; }
        opt(drain, [[{ k: 1 }, { k: 2 }]], [[1, 2, 3]]);
        rec(drain([{ k: 1 }, { k: 2 }, { k: 3 }])); rec(drain([1, , 3, , 5])); rec(drain([])); rec(drain([1.5, 2.5]));
        function cow() { return [7, 8, 9]; }
        rec(drain(cow())); rec(drain(cow()));
        const big = []; for (let i = 0; i < 50; i++) big.push({ k: i }); rec(drain(big)); rec(big.length);
        function pops(a, n) { let r = ''; for (let i = 0; i < n; i++) { const v = a.pop(); r += v === undefined ? 'u' : v.k; } return r + a.length; }
        opt(pops, [[{ k: 1 }], 2]);
        const objs = []; for (let i = 0; i < 30; i++) objs.push({ k: i % 10 });
        rec(pops(objs, 35)); rec(pops([{ k: 'a' }, , { k: 'b' }], 4));
        """,
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
        // Elided allocations (escape analysis): deopts inside the loop materialize
        // the captured objects (nested ones too), which the interpreter then reads.
        """
        function Pt(x, y) { this.x = x; this.y = y; }
        function ea(a, n) { let s = 0; for (let i = 0; i < n; i++) { const p = {x: i, y: a[i % a.length]}; const q = new Pt(p.x * 2, p.y); const t = q.y | 0; s += q.x + t + p.y; } return s; }
        let ear = [1, 2, 3, 4, {}]; ear.pop(); opt(ea, [ear, 10]); rec(ea(ear, 100)); ear[2] = 1.5; rec(ea(ear, 100)); ear[1] = 'x'; rec(ea(ear, 50));
        function ea2(a, n) { let s = 0; for (let i = 0; i < n; i++) { const box = {v: i, w: {u: i * 2}}; const t = a[i] + 1; s += box.v + box.w.u + t; } return s; }
        const ea2a = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, {}]; ea2a.pop(); opt(ea2, [ea2a, 10]); rec(ea2(ea2a, 10)); ea2a[5] = 'z'; rec(ea2(ea2a, 10)); ea2a[6] = 0.5; rec(ea2(ea2a, 10));
        """,
        // A number return of a value tagged before a loop (a context slot store),
        // the loop long enough for poll exits: the untagged value must stay live
        // (and be saved and reloaded at the resume) until the return.
        """
        const bmp = (function () {
          var cnt = 0, sink = 0;
          return function (n, k) { cnt = cnt + k; let s = 0; for (let i = 0; i < n; i++) s = (s + (i ^ k)) | 0; sink = s; return cnt; };
        })();
        opt(bmp, [10, 1]); rec(bmp(10, 2)); rec(bmp(3000000, 7)); rec(bmp(2500000, 9));
        """,
        // Type tests, strings, lengths, indices and element growth inline (with the
        // managed side for ropes, wrappers, growth, copy-on-write and deopts).
        """
        function ty(a) { let c = 0; for (let i = 0; i < a.length; i++) { const v = a[i];
          if (typeof v === 'number') c += 1; if (typeof v === 'string') c += 10; if (typeof v === 'undefined') c += 100;
          if (typeof v === 'function') c += 1000; if (typeof v === 'object') c += 10000; if (typeof v === 'boolean') c += 100000;
          if (typeof v === 'symbol') c += 1000000; if (v == null) c += 3; } return c; }
        const tya = [1, 'a', undefined, null, () => 1, {}, true, Symbol(), 2.5, [], 'b' + Math.random(), false];
        opt(ty, [tya]); rec(ty(tya)); rec(ty([1, 2, 3])); rec(ty([null, null, 'x']));
        function sl(s) { let t = 0; for (let i = 0; i < s.length; i++) t = (t * 31 + s.charCodeAt(i)) | 0; return t + s.length; }
        opt(sl, ['hello']); rec(sl('hello world')); rec(sl('abcdefghijklmnop' + String(Math.PI))); rec(sl(''));
        function sn(s, i) { return s.charCodeAt(i); } opt(sn, ['abc', 1]); rec(sn('abc', 2)); rec(sn('abc', 7)); rec(sn('abc', -1));
        function pu(n) { const a = []; for (let i = 0; i < n; i++) a.push(i * 2); let s = 0; for (let i = 0; i < a.length; i++) s += a[i]; return s + a.length; }
        opt(pu, [5]); rec(pu(10)); rec(pu(1000)); rec(pu(0));
        function ap(a, n) { for (let i = 0; i < n; i++) a[a.length] = i; return a.length; }
        opt(ap, [[1], 3]); rec(ap([1, 2], 50)); rec(ap([1.5, 2.5], 5)); rec(ap([], 2));
        function cw(n) { const a = [1, 2, 3]; for (let i = 0; i < n; i++) a[i % 3] = i; return a.join(); }
        opt(cw, [4]); rec(cw(10)); rec(cw(2));
        function tr(a, n) { for (let i = 0; i < n; i++) a[i] = i + 0.5; return a.length; }
        opt(tr, [[1, 2, 3], 3]); rec(tr([1, 2, 3, 4], 4)); rec(tr(['x', 'y'], 2));
        function Pt2(x, y) { this.x = x; this.y = y; this.z = x * y; }
        function mk2(n) { let s = 0; for (let i = 0; i < n; i++) s += i; const p = new Pt2(s, 2); return p.z + p.x; }
        opt(mk2, [3]); rec(mk2(10)); rec(mk2(0));
        function io(a) { let c = 0; for (let i = 0; i < a.length; i++) if (a[i] instanceof Pt2) c++; return c; }
        const ioa = [new Pt2(1, 2), {}, new Pt2(3, 4), 5]; opt(io, [ioa]); rec(io(ioa)); rec(io([1, 2]));
        function ki(o, ks) { let s = 0; for (let i = 0; i < ks.length; i++) s += o[ks[i]] | 0; return s; }
        const kio = [10, 20, 30, 40]; opt(ki, [kio, [0, 1, 2]]); rec(ki(kio, [0, 1, 2, 3])); rec(ki(kio, ['1', '2'])); rec(ki(kio, [1.5, 2]));
        function mp(a) { let s = 0; for (let i = 0; i < a.length; i++) s += Math.pow(a[i], 1.5) + Math.sin(a[i]); return s; }
        opt(mp, [[1, 2]]); rec(mp([1, 2, 3, 4.5]));
        """,
        // Calls in inlined functions: the managed side pushes the inlined frames
        // around the call (stack traces, throws through them, a lazy deopt of
        // the caller's code from inside the inlined function).
        """
        function g(x) {
          if (x === -1) { const st = new Error('e').stack.split('\n'); return st.length * 1000 + (st[2].indexOf('mid') >= 0 ? 100 : 0) + (st[3].indexOf('outer') >= 0 ? 10 : 0); }
          if (x === -2) throw new RangeError('r' + x);
          let s = 0; for (let i = 0; i < 3; i++) s += i * x; s = s * 3 + 1; s = s ^ 5; s = (s + x) | 0; s = s % 1000; s = s * 2 - x; s += [1, 2, 3].length;
          s = s + String(x).length; s = s + (x > 10 ? 1 : 2); s = s + (x & 3); s = s + (x | 8); s = s + (x >> 1); return s;
        }
        function mid(x, y) { const r = g(x) * 2; return r + y; }
        function outer(x) { const a = mid(x, 3); const b = mid(x + 1, 4); return a + b; }
        opt(outer, [1], [2]); rec(outer(5)); rec(outer(-1));
        try { outer(-2); } catch (e) { rec(e.message + ' ' + e.stack.split('\n').slice(1, 4).map(l => l.trim().split(' ')[1]).join(',')); }
        rec(outer(7));
        const pr = { v: 1 }; function Ob() {} Ob.prototype = pr;
        function g2(k) {
          if (k === 9) pr.v = 2.5;
          let s = 0; for (let i = 0; i < 3; i++) s += i * k; s = s * 3 + 1; s = s ^ 5; s = (s + k) | 0; s = s % 1000; s = s * 2 - k;
          s = s + String(k).length; s = s + (k > 10 ? 1 : 2); s = s + (k & 3); s = s + (k | 8); s = s + (k >> 1); return k + (s & 0);
        }
        function mid2(o, k) { const r = g2(k); return o.v + r; }
        function outer2(o, k) { return mid2(o, k) * 2; }
        opt(outer2, [new Ob(), 1], [new Ob(), 2]); rec(outer2(new Ob(), 3)); rec(outer2(new Ob(), 9)); rec(outer2(new Ob(), 4));
        """,
        // Frame writes (assigned parameters flushed before a call that can read
        // them through f.arguments; block contexts) and CheckMaps with migration.
        """
        function cal(x) {
          const ar = sr.arguments; let s = ar ? ar[0] * 1000 + ar[1] : -1;
          for (let i = 0; i < 3; i++) s += i; s = s * 3 + 1; s = s ^ 5; s = (s + x) | 0; s = s % 100000; s = s + String(x).length; s = s + (x > 10 ? 1 : 2); s = s + (x & 3); s = s + (x | 8); return s;
        }
        function sr(a, b) { a = a * 2; b = (b | 0) + 1; const r = cal(a); return a + b + r; }
        opt(sr, [1, 2], [3, 4]); rec(sr(5, 6)); rec(sr(7, 'x')); rec(sr(2.5, 1));
        function bc(n) { let s = 0; { let k = n * 2; const f = () => k + 1; s += f(); k = 3; s += f(); } { let m = s; const g = () => m * 2; s += g(); } return s; }
        opt(bc, [1], [2]); rec(bc(5)); rec(bc(10));
        function mo(o) { return o.x + o.y; }
        const mo1 = {x: 1, y: 2}, mo2 = {x: 3, y: 4};
        opt(mo, [mo1], [mo2]); rec(mo(mo1)); mo2.x = 1.5; rec(mo(mo2)); rec(mo(mo1)); opt(mo, [mo1], [mo2]); rec(mo({x: 2, y: 1})); rec(mo(mo1));
        """,
        // Allocations (managed nodes), object literals, double field stores (inline over a number).
        """
        function C(a) { this.a = a; }
        function mk(n) { const o = {x: n, y: 2}; const c = new C(o); let s = 0; for (let i = 0; i < n; i++) s += i & 7; o.x = s; return c; }
        opt(mk, [5]); rec(JSON.stringify(mk(100))); rec(mk(10).a.x); rec(JSON.stringify(mk(3)));
        function lit(n) { const r = {k: 1.5, j: 'a'}; let s = 0; for (let i = 0; i < n; i++) s += r.k; r.k = s; return r; }
        opt(lit, [5]); rec(JSON.stringify(lit(10))); rec(JSON.stringify(lit(0)));
        function dbl(o, n) { for (let i = 0; i < n; i++) o.d = o.d * 0.5 + i; return o.d; }
        const od = {d: 1.5}; opt(dbl, [od, 3]); rec(dbl(od, 100)); rec(dbl({d: NaN}, 3)); rec(dbl({d: 2.5}, 0)); rec(dbl({d: 'x'}, 2));
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
    public void NativeCallersCallNativeCalleesDirectly()
    {
        // Calls that are not inlined, from native code to native code: number
        // returns continue the caller at once; a callee that deopts, returns
        // something else or polls in a long loop continues on the managed side
        // (MaglevNativeCode.ContinueFrameless), then the caller resumes.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function leaf(a, b) { return a * b + 1; }
            function obj(a) { return a > 500 ? undefined : a; }
            function deo(a) { return a + 1; }
            function loopy(n) { let s = 0; for (let i = 0; i < n; i++) s += i; return s; }
            function noargs(a, b, c) { return c === undefined ? a : b; }
            function c1(n) { let s = 0; for (let i = 0; i < n; i++) s = (s + leaf(i, 3)) | 0; return s; }
            function c2(n) { let s = 0; for (let i = 0; i < n; i++) s += obj(i) === undefined ? 1 : 2; return s; }
            function c3(n, x) { let s = 0; for (let i = 0; i < n; i++) s += deo(i === n - 1 ? x : i); return s; }
            function c4(n, m) { let s = 0; for (let i = 0; i < n; i++) s += loopy(i < 2 ? m : 10); return s; }
            function c5(n) { let s = 0.5; for (let i = 0; i < n; i++) s += noargs(i, 2.5); return s; }
            // Registers live across the call in the caller, clobbered in the callee.
            function heavy(a, b) {
              const x = a * 1.5, y = b * 2.25, z = x * y, w = x + y, u = z - w, v = u * 0.5 + x, t = v - y * 3;
              const i = (a | 0) + 3, j = (b | 0) * 5, k = i ^ j, m = (k + i) | 0;
              return x + y + z + w + u + v + t + i + j + k + m;
            }
            function c6(n) {
              let s = 0.25, d1 = 1.5, d2 = -2.75, d3 = 0.125, p = 7, q = 11, r = 13;
              for (let i = 0; i < n; i++) {
                s += heavy(i * 0.5, i + 1) * 0.001;
                d1 = d1 * 1.0001 + d2; d2 = d2 - d3 * 0.5; d3 = d3 + 0.25; p = (p + q) | 0; q = (q ^ r) + 1; r = (r + p) & 1023;
              }
              return [s, d1, d2, d3, p, q, r].join(",");
            }
            for (const f of [leaf, obj, deo, loopy, noargs, heavy]) %PrepareFunctionForOptimization(f);
            leaf(1, 2); obj(1); deo(1); loopy(3); noargs(1, 2); heavy(1.5, 2);
            for (const f of [leaf, obj, deo, loopy, noargs, heavy]) %OptimizeFunctionOnNextCall(f);
            leaf(1, 2); obj(1); deo(1); loopy(3); noargs(1, 2); heavy(1.5, 2);
            opt(c1, [10]); opt(c2, [10]); opt(c3, [10, 1]); opt(c4, [3, 4]); opt(c5, [3]); opt(c6, [3]);
            rec(c6(200));
            rec(c1(1000)); rec(c2(1000)); rec(c3(100, 1)); rec(c3(100, "s")); rec(c4(50, 3000000)); rec(c5(100));
            rec(leaf(1.5, 2)); rec(c1(10));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        string il = MaglevCompilerTest.Run(kIL + kNoInline, source);
        long before = Interlocked.Read(ref MaglevNativeCode.s_continuedCallees);
        string native = MaglevCompilerTest.Run(kNative + kNoInline, source);
        Assert.Equal(interpreted, il);
        Assert.Equal(interpreted, native);
        if (ExecutableMemory.Supported) Assert.True(Interlocked.Read(ref MaglevNativeCode.s_continuedCallees) > before, "no native callee was continued");
    }

    [Fact]
    public void FramelessCodeRunsItsToBooleanBranches()
    {
        // A leaf entered through its frameless direct entry whose ToBoolean
        // branch goes to the managed side (strings): the branch takes its
        // result before the code resumes.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function aw(cond, k) { if (!cond) return k * 2; return k + 1; }
            function caller(vals) { let s = 0; for (let i = 0; i < vals.length; i++) s += aw(vals[i], i); return s; }
            opt(aw, [true, 1], [false, 2], [1, 3], [0, 4]);
            const vals = [true, false, 1, 0, 'a', '', null, undefined, {}, NaN, 2.5, [], -0, 'xyz', ''];
            opt(caller, [vals]); rec(caller(vals)); rec(caller(['a', '', 'b', '']));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kIL + kNoInline, source));
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
    }

    [Fact]
    public void ObjectsInNativeHomesSurviveCollections()
    {
        // GC-visible homes: objects native code holds across calls that collect
        // garbage (moving them) stay its values; a phi of several objects.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function mk(i) { return { v: i, s: 'x' + i, arr: [i, i + 1] }; }
            function gcAndMake(i) { if (i % 7 === 0) { const junk = []; for (let k = 0; k < 2000; k++) junk.push({ k }); %CollectGarbage(0); } return mk(i); }
            function hold(n) {
              let a = mk(1), b = mk(2), s = 0;
              for (let i = 0; i < n; i++) { const c = gcAndMake(i); s += a.v + b.v + c.v + a.arr[1] + b.arr[0]; if (i % 3 === 0) a = c; else b = c; }
              return s + a.s.length * 1000 + b.s.length;
            }
            opt(hold, [5]); rec(hold(60)); rec(hold(200));
            function pick(o1, o2, k) { let r = k > 2 ? o1 : o2; const x = gcAndMake(k); return r.v + x.v + r.s.length; }
            opt(pick, [mk(3), mk(4), 1], [mk(5), mk(6), 3]); for (let k = 0; k < 20; k++) rec(pick(mk(10), mk(20), k));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kIL + kNoInline, source));
        lock (s_counterLock)
        {
            int before = MaglevNativeCodeGenerator.Generated;
            Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
            if (ExecutableMemory.Supported) Assert.True(MaglevNativeCodeGenerator.Generated > before, "nothing was compiled natively");
        }
    }

    [Fact]
    public void NativeReferenceStoresGoThroughTheWriteBarrier()
    {
        // Young objects stored by native code into fields and elements of old
        // objects and into a global's cell (WriteBarrier): only those old objects refer to them, and
        // compacting young collections must keep them and update the slots.
        Assert.SkipUnless(ExecutableMemory.Supported && WriteBarrier.Address != 0, "native reference stores need the write barrier");
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            const holder = { a: 1, b: 2 }; const arr = [1, 2, 3, 4];
            var gv = null;
            let ctxv = 0; function setc(x) { ctxv = x; }
            function fresh(n, k) { const l = []; for (let i = 0; i < n; i++) l.push({ v: i * k, s: 'p' + i + '_' + k }); return l; }
            function st(list) { for (let i = 0; i < list.length; i++) { const o = list[i]; holder.a = o; arr[i & 3] = o; holder.b = list[list.length - 1 - i]; gv = list[(i + 5) % list.length]; } return list.length; }
            %CollectGarbage(0); %CollectGarbage(0);
            opt(st, [fresh(8, 1)], [fresh(8, 2)]);
            for (let round = 0; round < 6; round++) {
              st(fresh(40, round + 3));
              for (let g = 0; g < 3; g++) { const junk = []; for (let k = 0; k < 3000; k++) junk.push({ k }); %SimulateNewspaceFull(); }
              rec(holder.a.v + ':' + holder.a.s + ':' + holder.b.s + ':' + arr[0].s + ':' + arr[3].v + ':' + gv.s);
            }

            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        lock (s_counterLock)
        {
            int before = MaglevNativeCodeGenerator.Generated;
            Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
            Assert.True(MaglevNativeCodeGenerator.Generated > before, "nothing was compiled natively");
        }
    }

    [Fact]
    public void NativeCodeAllocatesFromItsPools()
    {
        // Allocations in a loop taken from the pools (constructors with map
        // transitions, object literals with object fields stored through the
        // write barrier), the objects surviving young collections.
        Assert.SkipUnless(ExecutableMemory.Supported && WriteBarrier.Address != 0, "native allocation needs the write barrier");
        string source = kPrelude + """
            const shared = { tag: 'shared' };
            function Pt(x, y) { this.x = x; this.y = y; this.tag = shared; }
            function build(n) {
              const list = [];
              for (let i = 0; i < n; i++) {
                const p = new Pt(i, 'y' + i);
                const lit = { a: 1.5, b: 'lit', c: shared, p: p };
                list.push(lit);
                if (i % 100 === 99) { const junk = []; for (let k = 0; k < 2000; k++) junk.push([k]); %SimulateNewspaceFull(); }
              }
              return list;
            }
            opt(build, [3], [5]);
            for (let r = 0; r < 3; r++) {
              const l = build(450);
              %SimulateNewspaceFull(); %CollectGarbage(0);
              let s = 0; for (const e of l) s += e.p.x + e.a + e.p.y.length + (e.c === shared ? 1 : 0) + (e.p.tag === shared ? 1 : 0);
              rec(s + ':' + l[449].p.y + ':' + l[0].b + ':' + Object.keys(l[7]).join(','));
            }

            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        lock (s_counterLock)
        {
            int before = MaglevNativeCodeGenerator.Generated;
            Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative, source));
            Assert.True(MaglevNativeCodeGenerator.Generated > before, "nothing was compiled natively");
        }
    }

    [Fact]
    public void DirectlyCalledCodeGetsItsFrameWhenItExits()
    {
        // Native callees that need an interpreter frame (managed nodes, calls,
        // throws, stack traces, deopts, a long loop's interrupts), called
        // directly by native code: the frame is built when the callee exits
        // (MaglevNativeCode.ContinueFramed), and stack walks see it.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function name(o, k) { return o.n + k; }                         // a string concat: a managed node
            function st(k) { if (k === 13) return new Error('e').stack.split('\n').length + ':' + (new Error('e').stack.indexOf('caller') > 0); return k; }
            function thr(k) { if (k === 7) throw new RangeError('t' + k); return k * 2; }
            function deo(o) { return o.v + 1; }
            function lp(n) { let s = 0; for (let i = 0; i < n; i++) s = (s + i * 3) | 0; return s + ''; }
            let stacks = '';
            function caller(n, o) {
              let a = '', b = 0, c = 0;
              for (let i = 0; i < n; i++) {
                a = name(o, i);
                const t = st(i); if (typeof t === 'string') stacks += t + ';';
                b += i === 7 ? 1000 : thr(i);
                c += deo(i === n - 2 ? { v: 'x' } : o);
              }
              return a + '|' + b + '|' + c + '|' + lp(3000000);
            }
            function tcall(n) { try { return thr(n); } catch (e) { return e.message + ':' + e.stack.split('\n').length; } }
            const o = { n: 'nm', v: 2 };
            for (const f of [name, st, thr, deo, lp]) %PrepareFunctionForOptimization(f);
            name(o, 1); st(1); thr(1); deo(o); lp(3); name(o, 2); st(2); thr(2); deo(o); lp(3);
            for (const f of [name, st, thr, deo, lp]) %OptimizeFunctionOnNextCall(f);
            name(o, 1); st(1); thr(1); deo(o); lp(3);
            opt(caller, [3, o]); rec(caller(20, o)); rec(caller(30, o)); rec(stacks); rec(tcall(7)); rec(tcall(3));

            out.join(' # ')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        long before = Interlocked.Read(ref MaglevNativeCode.s_continuedCallees);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
        if (ExecutableMemory.Supported) Assert.True(Interlocked.Read(ref MaglevNativeCode.s_continuedCallees) > before, "no callee was continued");
    }

    [Fact]
    public void ExitingCalleesGetTheArgumentsTheCallPassed()
    {
        // Arguments computed into caller-saved registers that die at a direct
        // call: a callee that exits (a managed node) continues from a frame
        // built from the call's inputs, so they must survive the call.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            // (A loop whose phis use every allocatable register, then an exit, then the parameters again.)
            function leaf(a, b, o, c, d, e) {
              // (The parameters only after the exit: read in their frame slots, not copied into phis.)
              let p = 1, q = 2, r = 3, s = 4, t = 5, z = 0;
              for (let k = 0; k < 4; k++) { p = (p * 3 + q) & 1023; q = (q * 5 + r) & 1023; r = (r * 7 + s) & 1023; s = (s * 11 + t) & 1023; t = (t * 13 + p) & 1023; z = (z + p + q + r + s + t) | 0; }
              const str = o.n + z;
              return str.length * 1000 + a + b * 3 + c * 5 + d * 7 + e * 11 + z;
            }
            // (Int32 phis passed as they are, their last use the call after their loop: one of
            // them in rdi, which the call sequence itself writes, Octane Crypto's failure.)
            function caller(n, o) {
              // (bnpSquareTo's shape: the counter indexes an array in the loop and goes to the call after it.)
              var xa = o.xa, r = o.r, i;
              for (i = 0; i < xa.length - 1; ++i) {
                var c = leaf(i, xa[i], o, 2 * i, 0, 1);
                r[i] += leaf(i + 1, 2 * xa[i], o, 2 * i + 1, c, xa.length - i - 1);
              }
              r[0] += leaf(i, xa[i], o, 2 * i, 0, 1);
              return r.join(',');
            }
            // (Now and then an object of another map: the callee deopts, and the interpreter
            // goes on from the frame built from the call's inputs.)
            function many(n, o, o2) { let h = 0; for (let k = 0; k < n; k++) { const s = caller(k & 15, k % 7 === 3 ? o2 : o); for (let q = 0; q < s.length; q++) h = (h * 31 + s.charCodeAt(q)) | 0; } return h; }
            const o = { n: 'nm', xa: [3, 1, 4, 1, 5, 9, 2, 6], r: [0, 0, 0, 0, 0, 0, 0, 0] };
            %PrepareFunctionForOptimization(leaf); leaf(1, 2, o, 3, 4, 5); leaf(2, 3, o, 4, 5, 6);
            %OptimizeFunctionOnNextCall(leaf); leaf(1, 2, o, 3, 4, 5);
            opt(caller, [3, o]); rec(caller(200, o)); rec(caller(1000, { n: 'other', xa: [7, 7, 7], r: [1, 2, 3] })); rec(many(300, o, { z: 1, n: 'm2', xa: [2, 7, 1, 8, 2, 8, 1, 8], r: [9, 9, 9, 9, 9, 9, 9, 9] }));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        long before = Interlocked.Read(ref MaglevNativeCode.s_continuedCallees);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
        if (ExecutableMemory.Supported) Assert.True(Interlocked.Read(ref MaglevNativeCode.s_continuedCallees) > before, "no callee was continued");
    }

    [Fact]
    public void NestedDirectCallsContinueTheirPendingCallees()
    {
        // a calls b1 directly and b1 calls c1 directly; c1 exits (a string
        // concatenation), so b1 and a exit too, and a continues b1 from a copy
        // of its context that must hold c1's pending context as well. b2 shares
        // the call area and calls code that is never optimized (the slow way).
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function c1(o) { return o.n + 'x'; }
            function b1(o) { return c1(o); }
            function unopt(o) { return o.v * 2; }
            %NeverOptimizeFunction(unopt);
            function b2(o) { return unopt(o) + 1; }
            function a(o, n) { let s = 0; for (let i = 0; i < n; i++) { s += b1(o).length; s += b2(o); } return s; }
            const o = { n: 'nm', v: 3 };
            for (const f of [c1, b1, b2]) %PrepareFunctionForOptimization(f);
            c1(o); b1(o); b2(o); c1(o); b1(o); b2(o);
            for (const f of [c1, b1, b2]) %OptimizeFunctionOnNextCall(f);
            c1(o); b1(o); b2(o);
            opt(a, [o, 2]); rec(a(o, 50)); rec(a(o, 200));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
    }

    [Fact]
    public void ManagedCallersEnterCodeWithCallsWithoutAFrame()
    {
        // Native code that calls native code directly, entered by managed
        // callers (the interpreter, IL code, constructs) through its lazy-frame
        // entry (MaglevNativeCode.RunLazy): its frame is built from the
        // pseudo-frame only when it exits (stack traces, throws, deopts, managed
        // nodes), and a construct's frame is a constructor's.
        const string kNoInline = " --max-maglev-inlined-bytecode-size=0 --max-maglev-inlined-bytecode-size-small=0";
        string source = kPrelude + """
            function leaf(a, k) { return a.v + k; }
            function mid(a, k) {
              const r = leaf(a, k);
              if (k === 13) return new Error('e').stack.split('\n').length + ':' + (new Error('e').stack.indexOf('mid') > 0);
              if (k === 7) throw new RangeError('t' + k);
              if (k === 9) return a.n + r;
              return r * 2;
            }
            function C(a, k) { this.r = mid(a, k); this.t = new.target === C; }
            // (No new.target register: entered lazily by constructs, exiting past the call.)
            function D(a, k) { this.r = mid(a, k); if (k === 15) { this.t = a.n + k; return { o: this.r }; } if (k === 14) this.t = a.n + k; }
            const o = { v: 2, n: 'nm' };
            // (Constructs from optimized code: ConstructWithReceiver takes the callee's direct entry.)
            function mkC(a, k) { return new C(a, k); }
            function mkD(a, k) { return new D(a, k); }
            for (const f of [leaf, mid, C, D, mkC, mkD]) %PrepareFunctionForOptimization(f);
            for (let i = 0; i < 3; i++) { leaf(o, i); mid(o, i); mkC(o, i); mkD(o, i); }
            for (const f of [leaf, mid, C, D, mkC, mkD]) %OptimizeFunctionOnNextCall(f);
            leaf(o, 1); mid(o, 1); mkC(o, 1); mkD(o, 1);
            for (let k = 0; k < 16; k++) {
              try { rec(mid(o, k)); } catch (e) { rec(e.message); }
              try { const c = mkC(o, k); rec(c.r + ',' + c.t); } catch (e) { rec('C:' + e.message); }
              try { const d = mkD(o, k); rec(d.r + ',' + d.t + ',' + d.o); } catch (e) { rec('D:' + e.message); }
            }
            rec(mid({ v: 'x', n: 1 }, 2)); rec(mid({ v: 1.5 }, 3));
            out.join('|')
            """;
        string interpreted = MaglevCompilerTest.Run(kInterpreter, source);
        long before = Interlocked.Read(ref MaglevNativeCode.s_lazyFrames);
        Assert.Equal(interpreted, MaglevCompilerTest.Run(kNative + kNoInline, source));
        if (ExecutableMemory.Supported) Assert.True(Interlocked.Read(ref MaglevNativeCode.s_lazyFrames) > before, "no lazy-frame entry built its frame");
    }

    [Fact]
    public void InterruptsInInlinedFunctionsPushTheirFrames()
    {
        // A loop with calls in an inlined function: its interrupt check runs the
        // runtime with the inlined frames pushed (as managed nodes in inlined
        // functions), served while another thread requests API interrupts.
        Assert.SkipUnless(ExecutableMemory.Supported, "native code needs Linux on x64");
        var flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --maglev-native --no-maglev-native-loops-only --no-concurrent-recompilation");
        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            Compiler.CompileAndRun(isolate, """
                function f(i) { return (i * 7) & 1023; }
                %NeverOptimizeFunction(f);
                function inner(n, o) { var s = 0; for (var i = 0; i < n; i++) s = (s + f(i) + o.length) | 0; return s; }
                function outer(n, o) { return inner(n, o) + 1; }
                %PrepareFunctionForOptimization(inner); %PrepareFunctionForOptimization(outer);
                outer(10, []); outer(10, []);
                %OptimizeMaglevOnNextCall(outer);
                outer(10, []);
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
                    "var r = []; for (var k = 0; k < 5; k++) r.push(outer(300000, [1, 2])); r.join()")).ToString();
            }
            finally
            {
                stop.Cancel();
                requester.Join();
            }
            string expected = ObjectOps.ToString(isolate, Compiler.CompileAndRun(isolate, """
                var s = 0; for (var i = 0; i < 300000; i++) s = (s + ((i * 7) & 1023) + 2) | 0;
                var e = []; for (var k = 0; k < 5; k++) e.push(s + 1); e.join()
                """)).ToString();
            Assert.Equal(expected, result);
            Assert.True(served > 0);
            var outer = (JSFunction)Compiler.CompileAndRun(isolate, "outer").Object;
            Assert.True(((FeedbackVector)outer.RawFeedbackCell.Value!).MaglevCode!.IsNative);
        }
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
        // A graph with a catch block is generated as IL; --no-maglev-native generates IL for every graph.
        var flags = new FlagList();
        flags.SetFlagsFromString("--allow-natives-syntax --maglev --maglev-native --no-maglev-native-loops-only --no-concurrent-recompilation");
        Isolate isolate = Isolate.New(flags);
        using (isolate.Enter())
        {
            JSValue result = Compiler.CompileAndRun(isolate, """
                function t(i) { if (i < 0) throw new Error('x'); return i; }
                function g(x) { let s = 0; for (let i = 0; i < x; i++) { try { s += t(i); } catch (e) { s = -1; } } return s; }
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
