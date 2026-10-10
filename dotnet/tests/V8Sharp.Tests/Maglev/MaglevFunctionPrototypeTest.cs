// "prototype" of a constant function folded to its prototype
// (TryBuildNamedAccess, maglev-graph-builder.cc; MaglevGraphBuilder.TryFoldFunctionPrototype):
// a new prototype deoptimizes the code (InitialMapChanged); functions in the
// non-instance prototype mode or without an initial map are not folded.
namespace V8Sharp.Tests.Maglev;

public class MaglevFunctionPrototypeTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          var NS = { V: function (x) { this.x = x; }, W: function () {}, N: function () {} };
          NS.V.prototype.get = function () { return this.x; };
          NS.V.prototype.twice = function (v) { return v.x * 2; };
          NS.W.prototype = 5;
          new NS.V(1);
          function use(i) {
            var r = NS.V.prototype.twice(new NS.V(i)) + NS.V.prototype.get.call({ x: i });
            return r + ':' + NS.W.prototype + ':' + typeof NS.N.prototype;
          }
          var out = [];
          for (var i = 0; i < 150; i++) {
            out.push(use(i));
            if (i == 100) NS.V.prototype = { twice: function () { return 'new'; }, get: function () { return '!'; } };
            if (i == 125) { NS.W.prototype = { w: 1 }; NS.N = function () {}; }
          }
          return out.join(',');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void ANewPrototypeDeoptimizes()
    {
        Assert.Equal("1,true,2,false", MaglevCompilerTest.Run("--maglev --no-concurrent-recompilation", """
            function F() {} F.prototype.v = 1; new F();
            var holder = { F: F };
            function f() { return holder.F.prototype.v; }
            %PrepareFunctionForOptimization(f);
            f(); f();
            %OptimizeFunctionOnNextCall(f);
            var r = [f(), %ActiveTierIsMaglev(f)];
            F.prototype = { v: 2 };
            r.push(f(), %ActiveTierIsMaglev(f));
            r.join();
            """));
    }
}
