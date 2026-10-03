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
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

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
