// Tests of the Maglev port's code quality work: what the optimized code
// keeps untagged or specialized (elements kind transitions of keyed loads,
// parameter assignments kept out of the frame, loop entry values untagged
// before the loop, int32 conversions), and that each gives the
// interpreter's results and deoptimizes where it must.
namespace V8Sharp.Tests.Maglev;

public class MaglevCodeQualityTest
{
    public static TheoryData<string> Snippets => new()
    {
        // Keyed loads whose feedback transitions packed arrays to holey ones
        // (LoadHandler::TransitionAndLoadElement), out of bounds and holes.
        """
        (function() {
          function at(a, i) { return a[i]; }
          var out = [];
          for (var k = 0; k < 40; k++) {
            var packed = [1, 2, 3], holey = [1, , 3], dbl = [1.5, 2.5], hdbl = [1.5, , 2.5];
            out.push(at(packed, k % 3), at(holey, k % 3), at(dbl, k % 2), at(hdbl, k % 3), at(packed, 5));
          }
          out.push(at({0: 'o'}, 0), at('str', 1), at([[1]], 0));
          return out.join();
        })()
        """,
        // Parameters assigned in loops and before calls: function.arguments
        // sees the current values whatever the tier.
        """
        (function() {
          function peek() { return outer.arguments[0] + ':' + outer.arguments[1]; }
          function outer(a, b) {
            var r = [];
            for (var i = 0; i < 3; i++) { a = a + 1; b += 'x'; }
            r.push(peek());
            if (a & 1) a = -a;
            r.push(peek());
            a = a * 2;
            try { b = b.length; r.push(peek()); } catch (e) { r.push('caught'); }
            return r.join('/');
          }
          var out = [];
          for (var k = 0; k < 30; k++) out.push(outer(k, 'y' + k));
          return out.join(';');
        })()
        """,
        // Loop counters that come from parameters (untagged before the loop),
        // then a double or a non-number: the check before the loop deopts.
        """
        (function() {
          function sum(i, n, step) { var s = 0; for (; i < n; i += step) s += i; return s; }
          function count(i, n) { var c = 0; while (i < n) { i++; c++; } return c + ':' + i; }
          var out = [];
          for (var k = 0; k < 40; k++) out.push(sum(k, k + 20, 3), count(k, 30));
          out.push(sum(0.5, 10, 1), sum('1', 5, 1), count(1.5, 4), count(-0, 2), count(undefined, 3));
          out.push(sum(2147483640, 2147483650, 3), count(2147483646, 2147483649));
          for (var k = 0; k < 20; k++) out.push(sum(k * 0.25, 10, 1));
          return out.join();
        })()
        """,
        // Int32 conversions: -0, NaN, fractions, values beyond int32, Smi range ends.
        """
        (function() {
          function t(x) { return [x | 0, x >> 1, x >>> 0, ~x, x & 255].join(); }
          function idx(a, x) { return a[x]; }
          var vals = [0, -0, 1, -1, 0.5, -0.5, NaN, Infinity, -Infinity, 2147483647, 2147483648, -2147483648, -2147483649,
                      4294967296, 1e20, -1e20, 1073741823, 1073741824, -1073741824, -1073741825, 4294967295.5];
          var out = [];
          for (var k = 0; k < 30; k++) for (var v of vals) out.push(t(v));
          var a = [10, 20, 30];
          for (var k = 0; k < 30; k++) out.push(idx(a, k % 3), idx(a, -0), idx(a, 1.0));
          out.push(idx(a, 0.5), idx(a, -1), idx(a, NaN));
          return out.join(';');
        })()
        """,
        // Object literals with nested object and array boilerplates: each
        // evaluation is a fresh deep copy (mutating one copy, its nested
        // arrays or objects, never reaches the next), field type changes.
        """
        (function() {
          function make(k) { return { a: 1, b: { c: [1, 2, 3], d: { e: 'x' } }, f: [1.5, 2.5], g: k }; }
          var out = [], prev = null;
          for (var k = 0; k < 40; k++) {
            var o = make(k);
            out.push(JSON.stringify(o), prev === null || (prev.b !== o.b && prev.b.c !== o.b.c && prev.f !== o.f));
            o.b.c.push(k); o.b.d.e = k; o.f[0] = 'str'; o.a = 'changed'; o.b.c[0] = 0.5;
            if (k == 25) { o.b.d.z = 1; o.b.d = 7; }
            prev = o;
          }
          return out.join(';');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    public static TheoryData<string> LoadEliminationSnippets => new()
    {
        // instanceof with the feedback's constructor: prototype reassignment,
        // primitives, Symbol.hasInstance, proxies, bound functions.
        """
        (function() {
          function A() {} function B() {} B.prototype = Object.create(A.prototype);
          function isA(x) { return x instanceof A; }
          var out = [], a = new A(), b = new B();
          for (var k = 0; k < 40; k++) {
            out.push(isA(a), isA(b), isA({}), isA(k), isA(null), isA('s'));
            if (k == 20) A.prototype = {};
            if (k == 30) Object.defineProperty(A, Symbol.hasInstance, { value: function(v) { return v === 5; } });
          }
          out.push(isA(5), isA(new Proxy(b, {})));
          function isF(x, F) { return x instanceof F; }
          var bound = A.bind(null);
          for (var k = 0; k < 20; k++) out.push(isF(b, B), isF(b, k > 10 ? bound : B));
          return out.join();
        })()
        """,
        // Loaded fields, lengths and context slots reused across a loop whose
        // body stores some of them (the loop's effects), through aliases, and
        // with growing arrays and calls.
        """
        (function() {
          var width = 4, height = 3;
          function f(o, p, a) {
            var s = 0;
            for (var i = 0; i < width; i++) {
              s += o.x + p.x + a.length + a[i % a.length];
              p.x = p.x + 1;
              if (i == 2) a.push(i);
              if (i == 3) width = 3;
            }
            width = 4;
            return s + ':' + o.x + ':' + p.x + ':' + a.length;
          }
          function g(o, n) { var t = 0; for (var i = 0; i < n; i++) { t += o.y; o.y = i; height++; t += height; } return t; }
          var out = [];
          for (var k = 0; k < 40; k++) {
            var o = { x: k, y: 1 }, p = (k & 1) ? o : { x: 2 * k, y: 2 }, a = [1, 2, 3];
            out.push(f(o, p, a), g(o, 5), g(p, 3));
          }
          return out.join();
        })()
        """,
        // Context slots an "immutable" load reads while a call assigns them:
        // a generator's var after its resumption, a derived constructor's
        // this read by an arrow before and after super().
        """
        (function() {
          function* f() { yield function g() { return '' + test + (gen.next(), test); }; var test = 10; }
          var gen, out = [];
          for (var k = 0; k < 40; k++) { gen = f(); out.push(gen.next().value()); }
          class B { constructor() { this.b = 1; } }
          class D extends B {
            constructor(k) {
              var get = () => { try { return this.b; } catch (e) { return 'tdz'; } };
              var before = get();
              super();
              this.r = before + ':' + get();
            }
          }
          for (var k = 0; k < 40; k++) out.push(new D(k).r);
          return out.join();
        })()
        """,
        """
        (function() {
          function sum(a, n) { var s = 0; for (var j = 0; j < n; j++) for (var i = 0; i < a.length; i++) s += a[i]; return s; }
          function mutate(a, n) { var s = 0; for (var i = 0; i < n; i++) { s += a.length; if (i % 3 == 0) a.pop(); else a.push(i); } return s + ':' + a.length; }
          function poly(objs) { var s = 0; for (var i = 0; i < objs.length; i++) { var o = objs[i]; s += o.v; o.v = s; } return s; }
          var out = [];
          for (var k = 0; k < 40; k++) {
            out.push(sum([1, 2, 3, k], 3), sum([1.5, 2], 2), mutate([1, 2, 3], 10));
            var a = { v: 1 }, b = { w: 0, v: 2 };
            out.push(poly([a, b, a, b, a]), a.v, b.v);
          }
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(LoadEliminationSnippets))]
    public void LoadEliminationGivesTheSameResults(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void UndetectableComparesFollowTheProtector()
    {
        // x == null: null and undefined only while no undetectable object
        // exists (NoUndetectableObjects protector); creating one deoptimizes
        // the code, which then also tests the map bit.
        string source = """
            function isNull(x) { return x == null; }
            function nn(x) { return x != null ? 1 : 0; }
            function tb(o) { var r = o; if (o && o.k) r = o.k; return r ? 1 : 0; }
            var vals = [null, undefined, 0, '', {}, [], false, NaN];
            var out = [];
            for (var k = 0; k < 30; k++) for (var v of vals) out.push(isNull(v), nn(v), tb(v), tb({ k: v }), tb(k & 1 ? null : { k: k }));
            var u = %GetUndetectable();
            out.push(isNull(u), nn(u), isNull({}), nn(null));
            for (var k = 0; k < 30; k++) out.push(isNull(u), isNull(k), nn(u), tb(u), tb({ k: u }), tb(k & 1 ? null : { k: k }));
            out.join();
            """;
        string interpreted = MaglevCompilerTest.Run("--no-maglev --no-sparkplug", source);
        foreach (string flags in MaglevCompilerTest.StressConfigurations)
        {
            Assert.Equal(interpreted, MaglevCompilerTest.Run(flags, source));
        }
    }

    [Fact]
    public void KeyedLoadsWithElementsKindTransitionsStayOptimized()
    {
        // The IC's transitioning handler for the packed map: the optimized
        // load transitions the array to holey and loads (no generic access).
        Assert.Equal("1,,3,8,true", MaglevCompilerTest.Run("--maglev", """
            function at(a, i) { return a[i]; }
            %PrepareFunctionForOptimization(at);
            at([1, 2], 0); at([1, , 2], 0); at([1, 2], 1); at([3, , 4], 1);
            %OptimizeMaglevOnNextCall(at);
            var p = [1, 2, 3];
            var r = [at(p, 0), at([1, , 2], 1), at(p, 2), %GetOptimizationStatus(at) & 8, %HasHoleyElements(p)];
            r.join();
            """));
    }

    [Fact]
    public void FunctionArgumentsSeesAssignedParameters()
    {
        // The assignments stay in IL locals in the loop and are written to
        // the frame before the calls that can read them.
        Assert.Equal("5,2|8;8", MaglevCompilerTest.Run("--maglev", """
            function read() { return f.arguments[0] + ',' + f.arguments.length; }
            function f(a, n) { for (var i = 0; i < n; i++) a++; var r = read(); a += 3; return r + '|' + read().split(',')[0]; }
            %PrepareFunctionForOptimization(f);
            f(0, 2); f(1, 2);
            %OptimizeMaglevOnNextCall(f);
            [f(2, 3), %GetOptimizationStatus(f) & 8].join(';');
            """));
    }

    [Fact]
    public void FailedLoopEntryUntaggingDeoptsOnceThenStaysTagged()
    {
        // A loop counter from a parameter is untagged before the loop; a
        // double makes that check deopt, and the next compile keeps the phi
        // tagged (no deopt loop).
        Assert.Equal("45,8,50,0,50,8", MaglevCompilerTest.Run("--maglev", """
            function sum(i, n) { var s = 0; for (; i < n; i++) s += i; return s; }
            %PrepareFunctionForOptimization(sum);
            sum(0, 10); sum(1, 10);
            %OptimizeMaglevOnNextCall(sum);
            var r = [sum(0, 10), %GetOptimizationStatus(sum) & 8, sum(0.5, 10), %GetOptimizationStatus(sum) & 8];
            %OptimizeMaglevOnNextCall(sum);
            r.push(sum(0.5, 10), %GetOptimizationStatus(sum) & 8);
            r.join();
            """));
    }
}
