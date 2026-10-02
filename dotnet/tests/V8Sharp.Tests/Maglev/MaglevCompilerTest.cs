// Tests of the Maglev port (src/maglev): V8's coverage of Maglev is
// test/mjsunit/maglev and the optimization variants of the test suites, so
// these are differential: the same scripts run in the interpreter
// (--no-maglev) and with Maglev forced early (tiny invocation budgets, so
// functions are optimized with little feedback, deoptimize, and are
// re-optimized; loops OSR), and the results must agree. The facts check the
// tiering entry points (%OptimizeFunctionOnNextCall, %GetOptimizationStatus,
// %DeoptimizeFunction, OSR), eager and lazy deoptimization, inlined frames in
// stack traces, and code dependencies.
using V8Sharp.Codegen;
using V8Sharp.Tests.Baseline;

namespace V8Sharp.Tests.Maglev;

public class MaglevCompilerTest
{
    /// <summary>Runs <paramref name="source"/> in a fresh isolate with <paramref name="flags"/> and returns String(completion).</summary>
    internal static string Run(string flags, string source)
    {
        var flagList = new FlagList();
        flagList.SetFlagsFromString("--allow-natives-syntax " + flags);
        Isolate isolate = Isolate.New(flagList);
        using (isolate.Enter())
        {
            string result;
            try
            {
                JSValue value = Compiler.CompileAndRun(isolate, source);
                Execution.PerformMicrotaskCheckpoint(isolate);
                result = ObjectOps.ToString(isolate, value).ToString();
            }
            catch (JavaScriptException e)
            {
                result = "threw " + ObjectOps.ToString(isolate, e.Value);
            }
            return result;
        }
    }

    /// <summary>The forced-optimization configurations (as V8's stress variants).</summary>
    public static readonly string[] StressConfigurations =
    [
        "--maglev --no-sparkplug --invocation-count-for-maglev=1 --invocation-count-for-feedback-allocation=1",
        "--maglev --no-sparkplug --invocation-count-for-maglev=3 --invocation-count-for-feedback-allocation=2",
        "--maglev --sparkplug --no-baseline-batch-compilation --invocation-count-for-maglev=2 --invocation-count-for-feedback-allocation=1",
    ];

    static void AssertSameWhenOptimized(string source)
    {
        string interpreted = Run("--no-maglev --no-sparkplug", source);
        foreach (string flags in StressConfigurations)
        {
            string optimized = Run(flags, source);
            Assert.True(interpreted == optimized, $"{flags}:\n  interpreted: {interpreted}\n  optimized:   {optimized}");
        }
    }

    public static TheoryData<string> Snippets => new()
    {
        // Int32 arithmetic with overflow into doubles, -0, division, modulus.
        """
        (function() {
          function f(a, b) { return [a + b, a - b, a * b, a / b, a % b, -a, a | b, a & b, a ^ b, a << 3, a >> 1, a >>> 1, ~a].join(); }
          var r = [];
          for (var i = 0; i < 40; i++) r.push(f(i - 20, (i % 7) - 3));
          r.push(f(2147483647, 1), f(-2147483648, -1), f(0, -5), f(1.5, 2), f('3', 4), f(null, undefined));
          return r.join('|');
        })()
        """,
        // Float loops, Math, comparisons.
        """
        (function() {
          function g(n) { var s = 0.5; for (var i = 0; i < n; i++) { s = s * 1.0001 + Math.sqrt(i) - Math.floor(s / 3); if (s > 1e6) s = 0; } return s; }
          var r = [];
          for (var k = 0; k < 30; k++) r.push(g(k * 10).toFixed(6));
          function cmp(a, b) { return (a < b) + ':' + (a <= b) + ':' + (a > b) + ':' + (a >= b) + ':' + (a == b) + ':' + (a === b); }
          var vs = [0, -0, 1, 2.5, NaN, Infinity, -1];
          for (var a of vs) for (var b of vs) r.push(cmp(a, b));
          return r.join();
        })()
        """,
        // Objects: constructors, field stores and loads, polymorphism, transitions, deopts on shape change.
        """
        (function() {
          function P(x, y) { this.x = x; this.y = y; }
          function Q(x) { this.y = 0; this.x = x; this.z = 1; }
          function sum(o) { return o.x + o.y; }
          function bump(o) { o.x = o.x + 1; return o; }
          var r = 0, objs = [];
          for (var i = 0; i < 50; i++) objs.push(i % 3 == 0 ? new P(i, 1) : i % 3 == 1 ? new Q(i) : {x: i, y: 2});
          for (var k = 0; k < 4; k++) for (var o of objs) { r += sum(bump(o)); }
          objs[5].x = 'str';
          r += sum(objs[5]);
          var d = {x: 1.5, y: 2}; r += sum(d); d.x = 3; r += sum(d);
          return r;
        })()
        """,
        // Arrays: packed, holey, doubles, growth with push and stores past the end, out-of-bounds reads.
        """
        (function() {
          function fill(a, n) { for (var i = 0; i < n; i++) a[i] = i * 2; return a; }
          function total(a) { var s = 0; for (var i = 0; i < a.length; i++) s += a[i]; return s; }
          var r = [];
          for (var k = 0; k < 20; k++) { var a = fill([], k); r.push(total(a)); }
          var d = fill([], 10); d[3] = 1.5; r.push(total(d));
          var h = [1, , 3]; r.push(total(h));
          var o = fill(new Array(5), 3); r.push(total(o), o.length);
          function read(a, i) { return a[i]; }
          for (var j = 0; j < 30; j++) read([1, 2, 3], j % 3);
          r.push(read([1, 2, 3], 5), read([1, 2, 3], -1), read({0: 'x'}, 0));
          var p = []; for (var q = 0; q < 100; q++) p.push(q); r.push(total(p));
          return r.join();
        })()
        """,
        // Calls, inlining, receivers, closures, recursion.
        """
        (function() {
          function add(a, b) { return a + b; }
          function twice(f, x) { return f(f(x, 1), 1); }
          function Counter() { this.n = 0; }
          Counter.prototype.inc = function (d) { this.n += d; return this; };
          var c = new Counter(), s = 0;
          for (var i = 0; i < 200; i++) { s += twice(add, i); c.inc(i % 5); }
          function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
          var mk = function (k) { return function (x) { return x * k; }; };
          var fs = [mk(2), mk(3)];
          for (var j = 0; j < 50; j++) s += fs[j % 2](j);
          function sloppy() { return this === undefined ? 'u' : typeof this; }
          function strict() { 'use strict'; return this === undefined ? 'u' : typeof this; }
          var t = ''; for (var q = 0; q < 20; q++) t = sloppy() + strict();
          return [s, c.n, fib(18), t].join();
        })()
        """,
        // Strings, string compares, charCodeAt, string keys.
        """
        (function() {
          function h(str) { var x = 0; for (var i = 0; i < str.length; i++) x = (x * 31 + str.charCodeAt(i)) | 0; return x; }
          var r = [], o = {};
          for (var i = 0; i < 50; i++) { var k = 'k' + (i % 7); o[k] = (o[k] | 0) + i; r.push(h(k + i)); }
          function lt(a, b) { return a < b; }
          for (var j = 0; j < 30; j++) r.push(lt('a' + j, 'a' + (30 - j)));
          return r.join() + JSON.stringify(o);
        })()
        """,
        // Globals, property cells, constants changing.
        """
        (function() {
          globalThis.G = 1; globalThis.H = {v: 1};
          function rg() { return G + H.v; }
          var s = 0;
          for (var i = 0; i < 100; i++) { s += rg(); if (i == 50) { G = 2; H = {v: 10, w: 1}; } if (i == 70) G = 'x'; }
          return s;
        })()
        """,
        // Exceptions thrown through optimized frames (no handlers in the optimized function).
        """
        (function() {
          function check(x) { if (x > 10) throw new RangeError('big:' + x); return x * 2; }
          function wrap(x) { return check(x) + 1; }
          var r = [];
          for (var i = 0; i < 30; i++) { try { r.push(wrap(i)); } catch (e) { r.push(e.message); } }
          return r.join();
        })()
        """,
        // Loops with OSR, nested loops, break/continue, switch.
        """
        (function() {
          var s = 0;
          for (var i = 0; i < 3000; i++) {
            for (var j = 0; j < 10; j++) { if (j == 7) continue; if (i % 1000 == 999 && j == 3) break; s = (s + i * j) | 0; }
            switch (i & 3) { case 0: s += 1; break; case 1: s -= 2; break; default: s ^= 5; }
          }
          var t = 0, k = 0; while (true) { k++; if (k > 5000) break; t += k % 3; }
          return s + ':' + t;
        })()
        """,
        // Contexts: let in loops, closures capturing, block contexts.
        """
        (function() {
          var fs = [];
          for (let i = 0; i < 40; i++) { let j = i * 3; fs.push(() => i + j); }
          var s = 0; for (var q = 0; q < 3; q++) for (var f of fs) s += f();
          function outer(a) { var b = a + 1; function inner(c) { return a + b + c; } var t = 0; for (var i = 0; i < 20; i++) t += inner(i); return t; }
          for (var k = 0; k < 20; k++) s += outer(k);
          return s;
        })()
        """,
        // Deopt in inlined functions (the materialised frames continue in the interpreter).
        """
        (function() {
          function leaf(o) { return o.a + 1; }
          function mid(o) { var t = leaf(o); return t * 2; }
          function top(o) { var u = mid(o); return u + 1; }
          var r = 0;
          for (var i = 0; i < 100; i++) r += top({a: i});
          r += top({b: 1, a: 1.5});
          r += top({a: 'x'});
          for (var j = 0; j < 100; j++) r += top({a: j});
          return r;
        })()
        """,
        // Bitwise / int-heavy code (crypto-like).
        """
        (function() {
          function am(x, w, c, n) { var a = [0x3fff, 0x1234, 0xffff, 7]; var r = 0; for (var i = 0; i < n; i++) { var l = x & 0x3fff, h = x >> 14; var m = h * a[i & 3] + w * l; l = l * a[i & 3] + ((m & 0x3fff) << 14) + c; c = (l >> 28) + (m >> 14) + h * w; r ^= l & 0xfffffff; x = (x * 1103515245 + 12345) & 0x7fffffff; } return r + c; }
          var s = 0; for (var k = 0; k < 50; k++) s = (s + am(k * 7919, k, 0, 40)) | 0;
          return s;
        })()
        """,
        // Double and holey element loads (holes are undefined), Math.clz32 of holes.
        """
        (function() {
          function copy(x, x0) { for (var j = 1; j <= 2; j++) { var r = j * 3; for (var i = 0; i < 2; i++) { x[r] = x0[r]; ++r; } } }
          var x = new Array(9).fill(1.5), x0 = new Array(9).fill(2.5), out = [];
          for (var k = 0; k < 20; k++) { x.fill(1.5); copy(x, x0); out.push(x.join()); }
          function h(a, i) { return Math.clz32(a[i]) + ':' + a[i]; }
          var holey = [-4.2, , 4.2, 42], tagged = [{}, , 'x'];
          for (var k = 0; k < 20; k++) out.push(h(holey, k & 3), h(tagged, k % 3));
          return out.join('|');
        })()
        """,
        // Nested loops entered by OSR in the inner loop (the outer back edge exits).
        """
        (function() {
          function nest(n) { var s = 0; for (var k = 0; k < n; k++) { for (var j = 0; j < 2000; j++) s = (s + j * k) | 0; s ^= k; } return s; }
          var r = [];
          for (var q = 0; q < 6; q++) r.push(nest(5 + q));
          return r.join();
        })()
        """,
        // try/catch/finally in optimized code: catch blocks with exception phis,
        // finally's token switch, nested handlers, catch contexts, break/continue.
        """
        (function() {
          function thrower(x) { if (x & 1) throw new Error("e" + x); return x * 2; }
          function f1(n) { let s = 0; for (let i = 0; i < n; i++) { try { s += thrower(i); } catch (e) { s += e.message.length; } } return s; }
          function f2(x) { let log = []; try { try { log.push("a"); thrower(x); log.push("b"); } finally { log.push("f"); } } catch (e) { log.push("c:" + e.message); } return log.join(","); }
          function f3(x) { let r = 0; try { r = 1; if (x) throw 42; r = 2; } catch (e) { r += e; } finally { r *= 10; } return r; }
          function f4(x) { try { return thrower(x); } catch (e) { try { throw e.message; } catch (e2) { return "nested " + e2; } } }
          function f5(x) { let a = x | 0, b = x * 1.5; try { thrower(x); a += 1; b += 0.5; } catch (e) { return a + b + 0.25; } return a + b; }
          function f6(x) { try { let y = x; { let z = y + 1; (() => z)(); thrower(z); } } catch ({message}) { return message; } return "none"; }
          function f7(x) { for (var i = 0; i < 3; i++) { try { if (i == x) continue; if (i == 2) break; thrower(1); } catch (e) { x += 10; } finally { x += 100; } } return x; }
          var out = [];
          for (var k = 0; k < 12; k++) out.push(f1(10 + k), f2(k), f3(k & 1), f4(k), f5(k), f6(k), f7(k % 3));
          return out.join("|");
        })()
        """,
        // Element access: string and double keys, out-of-bounds loads, polymorphic
        // arrays and objects, growing and holey stores, empty double arrays.
        """
        (function() {
          function ld(a, i) { return a[i]; }
          function st(a, i, v) { a[i] = v; return a.length; }
          var smi = [1, 2, 3, 4], dbl = [1.5, 2.5], obj = {0: 'a', 1: 'b', length: 2}, out = [];
          for (var k = 0; k < 30; k++) {
            out.push(ld(smi, k % 6), ld(dbl, String(k % 3)), ld(obj, k & 1), ld(smi, k == 29 ? 1.5 : 0));
            var g = []; for (var j = 0; j < (k % 5); j++) st(g, j, j * 0.5);
            out.push(st(g, k % 7, 'x'), g.join());
            var e = []; e[1] = 4.2; out.push(e[0] ?? 'u');
          }
          function cc(s, i) { return s.charCodeAt(i); }
          for (var k = 0; k < 30; k++) out.push(cc("abc", k % 5 - 1));
          return out.join();
        })()
        """,
        // Typed array loads and stores of every number kind (truncation,
        // clamping, out of bounds), f.apply(this, arguments) with megamorphic
        // feedback.
        """
        (function() {
          var kinds = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, Float32Array, Float64Array];
          var out = [];
          for (var k = 0; k < kinds.length; k++) {
            var C = kinds[k];
            var f = new Function('a', 'v', 'for (var i = -1; i <= a.length; i++) a[i] = v + i * 1.5; var s = 0; ' +
              'for (var i = 0; i < a.length + 2; i++) { var x = a[i]; s += x === undefined ? 1000 : x; } return s;');
            for (var r = 0; r < 25; r++) out.push(f(new C(7), r * 300 - 4000), f(new C(3), 0.5 - r));
          }
          function Class() { return function() { this.init.apply(this, arguments); }; }
          var cs = [];
          for (var i = 0; i < 6; i++) { var c = Class(); c.prototype.init = new Function('a', 'b', 'this.v = a * ' + i + ' + (b | 0);'); cs.push(c); }
          for (var r = 0; r < 40; r++) out.push(new cs[r % 6](r, r & 1).v);
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => AssertSameWhenOptimized(source);

    /// <summary>The baseline tier's snippets (generators, exceptions, eval ...) under forced optimization.</summary>
    [Theory]
    [MemberData(nameof(BaselineCompilerTest.Snippets), MemberType = typeof(BaselineCompilerTest))]
    public void BaselineSnippetsSameResultWhenOptimized(string source) => AssertSameWhenOptimized(source);

    const int kOptimized = 1 << 3;
    const int kMaglevved = 1 << 4;
    const int kInterpreted = 1 << 6;
    const int kTopmostFrameIsMaglev = 1 << 18;

    [Fact]
    public void OptimizeFunctionOnNextCallOptimizesToMaglev()
    {
        string result = Run("--maglev", """
            function f(x) { return x + 1; }
            %PrepareFunctionForOptimization(f);
            f(1); f(2);
            %OptimizeFunctionOnNextCall(f);
            var v = f(3);
            [v, %GetOptimizationStatus(f), %ActiveTierIsMaglev(f), %IsMaglevEnabled()].join();
            """);
        string[] parts = result.Split(',');
        Assert.Equal("4", parts[0]);
        int status = int.Parse(parts[1]);
        Assert.True((status & kOptimized) != 0 && (status & kMaglevved) != 0, "status " + status);
        Assert.Equal("true", parts[2]);
        Assert.Equal("true", parts[3]);
    }

    [Fact]
    public void EagerDeoptReturnsToTheInterpreter()
    {
        string result = Run("--maglev", """
            function f(x) { return x + 1; }
            %PrepareFunctionForOptimization(f);
            f(1); f(2);
            %OptimizeFunctionOnNextCall(f);
            f(3);
            var before = %GetOptimizationStatus(f);
            var v = f(1.5);
            [v, before, %GetOptimizationStatus(f)].join();
            """);
        string[] parts = result.Split(',');
        Assert.Equal("2.5", parts[0]);
        Assert.True((int.Parse(parts[1]) & kOptimized) != 0);
        int after = int.Parse(parts[2]);
        Assert.True((after & kOptimized) == 0 && (after & kInterpreted) != 0, "after " + after);
    }

    [Fact]
    public void LazyDeoptAfterCallInvalidatesTheCode()
    {
        // The callee changes a global the optimized caller embedded as a
        // constant: the caller's code is invalidated during the call and its
        // frame continues in the interpreter after the call returns.
        Assert.Equal("2,11,true", Run("--maglev", """
            globalThis.K = 1;
            function change() { K = 10; }
            function f(c) { var a = K; if (c) change(); return a + K; }
            %PrepareFunctionForOptimization(f);
            f(false); f(false);
            %OptimizeFunctionOnNextCall(f);
            var r1 = f(false);
            var r2 = f(true);
            [r1, r2, (%GetOptimizationStatus(f) & 8) == 0].join();
            """));
    }

    [Fact]
    public void DeoptimizeFunctionMarksTheCode()
    {
        string result = Run("--maglev", """
            function f() { return %GetOptimizationStatus(f); }
            %PrepareFunctionForOptimization(f);
            f(); f();
            %OptimizeFunctionOnNextCall(f);
            var inside = f();
            %DeoptimizeFunction(f);
            [inside, %GetOptimizationStatus(f)].join();
            """);
        string[] parts = result.Split(',');
        Assert.True((int.Parse(parts[0]) & kTopmostFrameIsMaglev) != 0, "inside " + parts[0]);
        Assert.True((int.Parse(parts[1]) & kOptimized) == 0, "after " + parts[1]);
    }

    [Fact]
    public void OsrIntoMaglev()
    {
        string result = Run("--maglev", """
            function f() {
              var s = 0, st = [];
              for (var i = 0; i < 100; i++) {
                if (i == 10) %OptimizeOsr();
                s += i;
                st.push(%GetOptimizationStatus(f));
              }
              return s + ';' + st[5] + ';' + st[99];
            }
            %PrepareFunctionForOptimization(f);
            f();
            """);
        string[] parts = result.Split(';');
        Assert.Equal("4950", parts[0]);
        Assert.True((int.Parse(parts[1]) & kTopmostFrameIsMaglev) == 0, "before " + parts[1]);
        Assert.True((int.Parse(parts[2]) & kTopmostFrameIsMaglev) != 0, "after " + parts[2]);
    }

    [Fact]
    public void InlinedFramesAppearInStackTraces()
    {
        string result = Run("--maglev", """
            function inner(x) { if (x > 5) return new Error('e').stack; return x; }
            function outer(x) { return inner(x); }
            %PrepareFunctionForOptimization(inner);
            %PrepareFunctionForOptimization(outer);
            outer(1); outer(2);
            %OptimizeFunctionOnNextCall(outer);
            outer(3);
            var s = outer(10);
            s.split('\n').slice(1, 3).map(l => l.trim().split(' ')[1]).join();
            """);
        Assert.Equal("inner,outer", result);
    }

    [Fact]
    public void FreezingTheGlobalObjectDeoptimizesGlobalStores()
    {
        // PropertyCell::UpdatePropertyDetailsExceptCellType: a cell becoming
        // read-only deoptimizes the code that stores to it.
        Assert.Equal("0,true,0", Run("--maglev", """
            glo = 0;
            function write_glo(x) { glo = x }
            %PrepareFunctionForOptimization(write_glo);
            write_glo({});
            write_glo(1);
            %OptimizeFunctionOnNextCall(write_glo);
            write_glo(0);
            var r = [glo];
            Object.freeze(this);
            r.push((%GetOptimizationStatus(write_glo) & 8) == 0);
            write_glo(1);
            r.push(glo);
            r.join();
            """));
    }

    [Fact]
    public void NeverOptimizeFunction()
    {
        Assert.Equal("0", Run("--maglev", """
            function f() { return 1; }
            %NeverOptimizeFunction(f);
            %PrepareFunctionForOptimization(f);
            f();
            %OptimizeFunctionOnNextCall(f);
            f();
            String(%GetOptimizationStatus(f) & 8);
            """));
    }

    [Fact]
    public void CatchBlocksStayOptimized()
    {
        Assert.Equal("34,187,0,8", Run("--maglev", """
            function h(x) { if (x) { throw 1; } else { return 17; } }
            %NeverOptimizeFunction(h);
            function f(a, b) { let r = a; try { r = h(a); return h(b) + r; } catch { return r * b; } }
            %PrepareFunctionForOptimization(f);
            f(0, 0); f(0, 11); f(7, 0);
            %OptimizeFunctionOnNextCall(f);
            [f(0, 0), f(0, 11), f(7, 0), %GetOptimizationStatus(f) & 8].join();
            """));
    }

    [Fact]
    public void DeoptInBuiltinReductionDisallowsSpeculation()
    {
        // The second compilation calls Math.abs generically (the call's
        // feedback no longer allows speculation) and does not deoptimize.
        Assert.Equal("100,8", Run("--maglev", """
            function f(a) { return Math.abs(a); }
            %PrepareFunctionForOptimization(f);
            f(1); f(1);
            %OptimizeFunctionOnNextCall(f);
            f("100");
            %PrepareFunctionForOptimization(f);
            %OptimizeFunctionOnNextCall(f);
            [f("100"), %GetOptimizationStatus(f) & 8].join();
            """));
    }

    [Fact]
    public void GenericCallsDoNotPreventOptimization()
    {
        // Megamorphic call sites of every call bytecode become generic call
        // nodes (the baseline tier's call paths), not bailouts.
        Assert.Equal("ok,8", Run("--maglev", """
            var fs = [];
            for (var i = 0; i < 6; i++) fs.push(new Function('a', 'b', 'return (a | 0) + (b | 0) + ' + i + ';'));
            function f(k) {
              var o = { m: fs[k % 6] }, g = fs[(k + 1) % 6];
              return o.m() + o.m(1) + o.m(1, 2) + o.m(1, 2, 3) + g() + g(1) + g(1, 2) + g(1, 2, 3);
            }
            %PrepareFunctionForOptimization(f);
            var expect = [];
            for (var k = 0; k < 12; k++) expect.push(f(k));
            %OptimizeFunctionOnNextCall(f);
            var same = true;
            for (var k = 0; k < 12; k++) same = same && f(k) === expect[k];
            [same ? "ok" : "differs", %GetOptimizationStatus(f) & 8].join();
            """));
    }

    [Fact]
    public void MaglevIsOffByDefault()
    {
        Assert.Equal("false,false", Run("", """
            function f() { return 1; }
            %PrepareFunctionForOptimization(f);
            f();
            %OptimizeFunctionOnNextCall(f);
            f();
            [%ActiveTierIsMaglev(f), %IsMaglevEnabled()].join();
            """));
    }

    [Fact]
    public void TieringOptimizesHotFunctions()
    {
        Assert.Equal("true", Run("--maglev --invocation-count-for-maglev=5", """
            function f(x) { return x * 2; }
            for (var i = 0; i < 10000; i++) f(i);
            String(%ActiveTierIsMaglev(f));
            """));
    }
}
