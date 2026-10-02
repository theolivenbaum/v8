// Tests of the baseline compiler (src/baseline): V8 has no unit tests for
// Sparkplug (its coverage is test/mjsunit/baseline and the --always-sparkplug
// variant), so these run the same scripts through the interpreter
// (--no-sparkplug) and through baseline code (--always-sparkplug) and compare
// the results, and check the tiering entry points (%CompileBaseline, the
// interrupt budget, OSR from Ignition, exception handlers, generators).
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Baseline;

public class BaselineCompilerTest
{
    /// <summary>Runs <paramref name="source"/> in a fresh isolate with <paramref name="flags"/> and returns String(completion).</summary>
    static string Run(string flags, string source)
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

    static void AssertSameInBothTiers(string source)
    {
        string interpreted = Run("--no-sparkplug", source);
        string baseline = Run("--always-sparkplug", source);
        Assert.Equal(interpreted, baseline);
    }

    public static TheoryData<string> Snippets => new()
    {
        // Constants, registers, arguments.
        "(function(a, b, c) { let x = 1, y = 'two', z = null, w; return [a, b, c, x, y, z, w, true, false, 1.5, -0].join(); })(1, 2)",
        // Arithmetic with feedback, Smi and double paths, overflow into doubles.
        """
        (function() {
          var r = [];
          for (var i = -3; i < 4; i++) {
            r.push(i + 1, i - 1, i * 3, i / 2, i % 2, i ** 2, i | 5, i & 3, i ^ 7, i << 2, i >> 1, i >>> 28, -i, ~i, i++ , i--);
          }
          r.push(2147483647 + 1, 1e308 * 10, 0.1 + 0.2, '1' + 2, 3 - '1', {} + 1, [1] * 2, 10n + 5n, 7n % 3n);
          return r.join();
        })()
        """,
        // Comparisons and tests.
        """
        (function() {
          var v = [0, 1, -1, 1.5, NaN, '1', 'a', null, undefined, true, {}, [], 2n, Symbol.iterator];
          var r = '';
          for (var a of v) for (var b of v) {
            try { r += (a == b ? 1 : 0) + '' + (a === b ? 1 : 0) + (a < b ? 1 : 0) + (a >= b ? 1 : 0); } catch (e) { r += 'E'; }
          }
          return r + typeof v + (typeof undefined == 'undefined') + (v instanceof Array) + ('length' in v) + (v[0] ?? 5) + (null ?? 7);
        })()
        """,
        // Property access, keyed access, polymorphism, elements.
        """
        (function() {
          function P(x) { this.x = x; this.y = x * 2; }
          var os = [new P(1), {x: 2, y: 3}, {y: 4, x: 5}, Object.create({x: 6, y: 7}), [8, 9]];
          var s = 0;
          for (var k = 0; k < 3; k++) for (var o of os) { s += o.x | 0; s += o['y'] | 0; o.z = s; o[0] = k; }
          var a = []; for (var i = 0; i < 10; i++) a[i] = i * i; a[20] = 1; delete a[3];
          return s + ':' + a.join() + ':' + os.map(o => o.z).join();
        })()
        """,
        // Monomorphic element loads (the inline fast path) of holes, doubles and out-of-bounds indices.
        """
        (function() {
          function get(a, i) { return a[i]; }
          var r = [];
          for (var a of [[,,,,0.5], [1.5, 2.5], [,1, 2], [1, 2, 3]]) {
            for (var k = 0; k < 3; k++) for (var i = -1; i < 6; i++) r.push(get(a, i), get(a, i + 0.5));
          }
          return r.join();
        })()
        """,
        // Closures, contexts, block scopes, TDZ.
        """
        (function() {
          var fs = [];
          for (let i = 0; i < 5; i++) { let j = i * 2; fs.push(() => i + j); }
          var r = fs.map(f => f()).join();
          try { (function() { x; let x = 1; })(); } catch (e) { r += e.constructor.name; }
          { let q = 3; const c = 4; r += q + c; }
          return r;
        })()
        """,
        // Exceptions: try/catch/finally in loops, rethrow, nested frames, messages.
        """
        (function() {
          var r = [];
          function thrower(n) { if (n % 3 == 0) throw new RangeError('r' + n); if (n % 3 == 1) null.x; return n; }
          for (var i = 0; i < 9; i++) {
            try { try { r.push(thrower(i)); } finally { r.push('f'); } }
            catch (e) { r.push(e.name + ':' + e.message); }
          }
          function f() { try { return 1; } finally { r.push('ret'); } }
          f();
          try { try { throw 1; } catch (e) { throw e + 1; } } catch (e) { r.push(e); }
          return r.join('|');
        })()
        """,
        // switch, labeled loops, for-in, for-of, spread, destructuring.
        """
        (function() {
          var r = '';
          for (var i = 0; i < 8; i++) {
            switch (i) { case 0: r += 'a'; break; case 1: case 2: r += 'b'; case 3: r += 'c'; break; case 7: r += 'z'; default: r += 'd'; }
          }
          outer: for (var a = 0; a < 3; a++) for (var b = 0; b < 3; b++) { if (b == 2) continue outer; if (a == 2) break outer; r += a + '' + b; }
          var o = {p: 1, q: 2, 3: 'x'}; for (var k in o) r += k;
          for (var [x, y] of [[1, 2], [3, 4]]) r += x * y;
          var {p, ...rest} = o; r += p + JSON.stringify(rest);
          r += Math.max(...[1, 5, 3]);
          switch ('s') { case 's': r += 'S'; }
          return r;
        })()
        """,
        // Generators, async functions, iterators.
        """
        (function() {
          function* g(n) { for (var i = 0; i < n; i++) { var x = yield i; if (x) i += x; } return 'done'; }
          var it = g(10), r = [];
          r.push(it.next().value, it.next().value, it.next(3).value, it.next().value, [...g(3)].join());
          function* d() { yield* g(2); yield* [7, 8]; }
          r.push([...d()].join());
          var log = [];
          async function af(x) { log.push('a' + x); await null; log.push('b' + x); try { await Promise.reject(x); } catch (e) { log.push('c' + e); } return x; }
          af(1); af(2);
          return r.join() + '/' + log.join();
        })()
        """,
        // Classes, super, private fields, getters, new.target, arguments.
        """
        (function() {
          class A { #p = 1; static s = 2; constructor(x) { this.x = x; } get px() { return this.#p + this.x; } m() { return 'A' + new.target; } }
          class B extends A { constructor() { super(10); } m() { return 'B' + super.m(); } get px() { return super.px * 2; } }
          var b = new B();
          function sloppy() { arguments[0] = 9; return [...arguments].join() + arguments.length; }
          function strict() { 'use strict'; arguments[0] = 9; return [...arguments].join(); }
          function rest(a, ...more) { return more.length; }
          return [b.px, b.m(), A.s, sloppy(1, 2), strict(1, 2), rest(1, 2, 3), Reflect.construct(A, [5]).x].join();
        })()
        """,
        // Templates, regexps, object and array literals, spreads in calls and new.
        """
        (function() {
          function tag(s, ...v) { return s.raw.join('_') + v.join(); }
          var o = {a: 1, ['b' + 1]: 2, get c() { return 3; }, ...{d: 4}};
          var re = /(\d+)-(\d+)/g;
          return [tag`x${1}y${2}`, `t${o.a}${o.b1}`, JSON.stringify(o), '12-34 5-6'.replace(re, '$2:$1'),
                  Math.min.apply(null, [3, 1, 2]), new Array(...[1, 2, 3]).length, [..."abc"].reverse().join('')].join();
        })()
        """,
        // eval, with, lookup slots, delete, global stores.
        """
        (function() {
          var r = [];
          var x = 1;
          r.push(eval('x + 1'));
          with ({x: 5}) { r.push(x); x = 6; }
          r.push(x);
          globalThis.gv = 3; gv++; r.push(gv, delete globalThis.gv, typeof gv);
          var o = {a: 1}; r.push(delete o.a, 'a' in o);
          return r.join();
        })()
        """,
        // Stack traces from baseline frames.
        """
        (function() {
          function inner() { return new Error('e').stack.split('\n').slice(0, 3).map(l => l.trim()).join('|'); }
          function outer() { return inner(); }
          return outer();
        })()
        """,
        // Deep recursion hits the stack limit in both tiers.
        "(function() { function d(n) { return n ? 1 + d(n - 1) : 0; } try { return d(1e6); } catch (e) { return e.name; } })()",
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultInBothTiers(string source) => AssertSameInBothTiers(source);

    [Fact]
    public void AlwaysSparkplugCompilesOnFirstCall()
    {
        Assert.Equal("true,true", Run("--always-sparkplug", """
            function f(x) { return x + 1; }
            f(1);
            [%ActiveTierIsSparkplug(f), %IsSparkplugEnabled()].join();
            """));
    }

    [Fact]
    public void NoSparkplugStaysInterpreted()
    {
        Assert.Equal("false,false", Run("--no-sparkplug", """
            function f(x) { return x + 1; }
            for (var i = 0; i < 100; i++) f(i);
            [%ActiveTierIsSparkplug(f), %IsSparkplugEnabled()].join();
            """));
    }

    [Fact]
    public void CompileBaselineNative()
    {
        // As test/mjsunit/baseline/test-baseline.js: run, %CompileBaseline, run again.
        Assert.Equal("false,true,42,20483", Run("--sparkplug --no-always-sparkplug", """
            function f(o) { return o.a; }
            var before = %ActiveTierIsSparkplug(f);
            f({a: 1});
            %CompileBaseline(f);
            [before, %ActiveTierIsSparkplug(f), f({a: 42}), %GetOptimizationStatus(f)].join();
            """));
    }

    [Fact]
    public void TieringUpAfterTheInterruptBudget()
    {
        // The first interrupt tick allocates the feedback vector and compiles
        // (no batching, so the single function is compiled right away).
        Assert.Equal("false,true", Run("--sparkplug --no-baseline-batch-compilation", """
            function f(x) { return x * 2 + 1; }
            f(1);
            var first = %ActiveTierIsSparkplug(f);
            for (var i = 0; i < 100; i++) f(i);
            [first, %ActiveTierIsSparkplug(f)].join();
            """));
    }

    [Fact]
    public void OsrFromIgnitionIntoBaseline()
    {
        // The loop starts in the interpreter; %BaselineOsr compiles the running
        // function, and the next JumpLoop continues the frame in baseline code.
        const int kTopmostFrameIsInterpreted = 1 << 15;
        const int kTopmostFrameIsBaseline = 1 << 16;
        string result = Run("--sparkplug --no-always-sparkplug --no-baseline-batch-compilation", """
            function f() {
              var statuses = [];
              var sum = 0;
              for (var i = 0; i < 10; i++) {
                if (i == 5) %BaselineOsr();
                statuses.push(%GetOptimizationStatus(f));
                sum += i;
              }
              return statuses.join(',') + ';' + sum;
            }
            %EnsureFeedbackVectorForFunction(f);
            f();
            """);
        string[] parts = result.Split(';');
        Assert.Equal("45", parts[1]);
        int[] statuses = Array.ConvertAll(parts[0].Split(','), int.Parse);
        Assert.Equal(10, statuses.Length);
        for (int i = 0; i < 5; i++) Assert.True((statuses[i] & kTopmostFrameIsInterpreted) != 0, "iteration " + i);
        // Iteration 5 runs its body in Ignition until the back edge.
        for (int i = 6; i < 10; i++) Assert.True((statuses[i] & kTopmostFrameIsBaseline) != 0, "iteration " + i);
    }

    [Fact]
    public void OsrInAnInlineFrameReturnsToTheInterpretedCaller()
    {
        // The caller runs in the interpreter and calls inner inline
        // (InterpreterInlineCalls); inner tiers up during its loop, continues in
        // baseline code, and returns (or throws) to the caller's dispatch loop.
        Assert.Equal("499500,499500,true,caught 7", Run("--sparkplug --no-baseline-batch-compilation", """
            function inner(n, t) {
              var s = 0;
              for (var i = 0; i < n; i++) { s += i; if (i == t) throw 'caught ' + t; }
              return s;
            }
            var r = [];
            r.push(inner(1000, -1));
            r.push(inner(1000, -1));
            r.push(%ActiveTierIsSparkplug(inner));
            try { inner(1000, 7); } catch (e) { r.push(e); }
            r.join();
            """));
    }

    [Fact]
    public void ExceptionFromCalleeReachesBaselineHandler()
    {
        Assert.Equal("caught:boom:3", Run("--always-sparkplug", """
            function callee(n) { if (n == 3) throw new Error('boom'); return n; }
            function caller() {
              var n = 0;
              try { for (;;) { callee(n); n++; } } catch (e) { return 'caught:' + e.message + ':' + n; }
            }
            caller();
            """));
    }

    [Fact]
    public void GeneratorSuspendsAndResumesInBaseline()
    {
        Assert.Equal("0,1,2,3,4|true", Run("--always-sparkplug", """
            function* g() { for (var i = 0; i < 5; i++) yield i; }
            var r = [...g()].join();
            r + '|' + %ActiveTierIsSparkplug(g);
            """));
    }

    [Fact]
    public void JitlessDisablesSparkplug()
    {
        Assert.Equal("false", Run("--jitless --always-sparkplug", """
            function f() { return 1; }
            f();
            String(%ActiveTierIsSparkplug(f));
            """));
    }

    [Fact]
    public void SparkplugFilter()
    {
        Assert.Equal("true,false", Run("--always-sparkplug --sparkplug-filter=yes*", """
            function yesPlease() { return 1; }
            function no() { return 1; }
            yesPlease(); no();
            [%ActiveTierIsSparkplug(yesPlease), %ActiveTierIsSparkplug(no)].join();
            """));
    }

    /// <summary>
    /// Runs <paramref name="source"/> (which evaluates to an array of functions)
    /// and describes the feedback the functions collected: the bytecode with its
    /// embedded feedback bytes, and each feedback vector slot (numbers by value,
    /// heap objects by type), so that two isolates can be compared.
    /// </summary>
    static string RunAndDescribeFeedback(string flags, string source)
    {
        var flagList = new FlagList();
        flagList.SetFlagsFromString("--allow-natives-syntax --no-lazy-feedback-allocation " + flags);
        Isolate isolate = Isolate.New(flagList);
        using (isolate.Enter())
        {
            JSValue value = Compiler.CompileAndRun(isolate, source);
            var sb = new System.Text.StringBuilder();
            var functions = (FixedArray)value.As<JSArray>().Elements;
            for (int i = 0; i < functions.Length; i++)
            {
                if (functions[i].HeapObjectOrNull is not JSFunction function) continue;
                sb.Append(function.Shared.Name()).Append(": ");
                sb.Append(Convert.ToHexString(((V8Sharp.Interpreter.BytecodeArray)function.Shared.FunctionData!).Bytecodes)).Append(' ');
                if (function.RawFeedbackCell.Value is FeedbackVector vector)
                {
                    foreach (JSValue slot in vector.Slots)
                    {
                        sb.Append(slot.IsNumber ? slot.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : slot.HeapObjectOrNull is { } o ? o.GetType().Name : "undefined").Append(',');
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    [Fact]
    public void SameFeedbackInBothTiers()
    {
        // The inline fast paths of baseline code skip the feedback update only
        // when it would not change anything: the embedded feedback bytes and
        // the feedback vector must end up as the interpreter leaves them.
        const string source = """
            function arith(a, b) {
              var r = a + b; r = r - a; r = r * 2; r = r | 0; r = r & 0xff; r = r ^ a; r = r << 1; r = r >> 1; r = r >>> 0;
              r = a + 1; r = b - 3; r = a * 4; r = a | 2; r++; r--;
              return (a < b) + (a > b) + (a <= b) + (a >= b) + (a == b) + (a === b);
            }
            function P(x) { this.x = x; this.y = x; }
            function props(o, k) { o.x = o.y; return o.x + o[k] + (o.length | 0); }
            function glob() { return Math.floor(gv) + gv; }
            var gv = 1;
            function calls(f, o) { return f(1) + o.m(2) + f(1, 2, 3); }
            var vals = [0, 1, -1, 1073741823, 1073741824, -0, 0.5, NaN, Infinity, '1', 'a', null, undefined, true, {}];
            for (var i = 0; i < vals.length; i++) {
              for (var j = 0; j < vals.length; j++) {
                arith(vals[i], vals[j]);
                if (i == 3) { for (var k = 0; k < 20; k++) arith(k, k + 1); }
              }
              props(new P(vals[i]), 'x'); props([1, 2, 3], i % 4); props({x: 1, y: 2}, 'y');
              gv = vals[i]; glob();
              calls(function (a) { return a; }, {m: function (b) { return b; }});
            }
            // Feedback that stays SignedSmall until one operation leaves the
            // Smi range (or produces -0, or gets a double operand).
            function add(a, b) { return a + b; }
            function sub(a, b) { return a - b; }
            function mul(a, b) { return a * b; }
            function addSmi(a) { return a + 5; }
            function inc(a) { return ++a; }
            function band(a, b) { return a & b; }
            function shr(a, b) { return a >>> b; }
            function lt(a, b) { return a < b; }
            function eq(a, b) { return a === b; }
            var ops = [add, sub, mul, addSmi, inc, band, shr, lt, eq];
            var probes = [[1073741823, 1], [-1073741824, 1], [0, -1], [-0, 0], [65536, 65536], [1073741820, 0], [0.5, 1], [-1, 0]];
            var clones = [];
            for (var p = 0; p < probes.length; p++) {
              for (var o = 0; o < ops.length; o++) {
                var g = eval('(' + ops[o] + ')');
                for (var k = 0; k < 10; k++) g(k, k + 1);
                g(probes[p][0], probes[p][1]);
                g(3, 4);
                clones.push(g);
              }
            }
            [arith, props, glob, calls, P].concat(clones);
            """;
        Assert.Equal(RunAndDescribeFeedback("--no-sparkplug", source), RunAndDescribeFeedback("--always-sparkplug", source));
    }
}
