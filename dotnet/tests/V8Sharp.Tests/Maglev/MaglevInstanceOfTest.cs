// Tests of instanceof in Maglev (TryBuildFastOrdinaryHasInstance's
// HasInPrototypeChain, maglev-reducer-inl.h): receivers of every kind, a
// subclass, a proxy on the chain, a constructor whose prototype changes, a
// bound function and a custom @@hasInstance; a deopt makes the feedback
// megamorphic.
namespace V8Sharp.Tests.Maglev;

public class MaglevInstanceOfTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          function Pair(a, b) { this.car = a; this.cdr = b; }
          function Other() {}
          function Sub() {} Sub.prototype = Object.create(Pair.prototype);
          function isPair(o) { return o instanceof Pair; }
          function count(xs) { var n = 0; for (var i = 0; i < xs.length; i++) if (xs[i] instanceof Pair) n++; return n; }
          var vals = [new Pair(1, 2), new Other(), new Sub(), 1, 'str', null, undefined, {}, [], function () {}, Object.create(null)];
          var out = [];
          for (var i = 0; i < 150; i++) {
            out.push(isPair(vals[i % vals.length]) ? 1 : 0);
            if (i % 25 == 0) out.push(count(vals));
            if (i == 100) {
              var p = new Proxy({}, { getPrototypeOf: function () { return Pair.prototype; } });
              out.push(isPair(p), isPair(Object.create(p)), count([p]));
            }
            if (i == 125) { Pair.prototype = { changed: true }; vals[0] = new Pair(3, 4); }
          }
          var bound = Pair.bind(null);
          out.push(new Pair(1, 1) instanceof bound, isPair(new Pair(5, 6)));
          Object.defineProperty(Pair, Symbol.hasInstance, { value: function () { return 'custom'; } });
          out.push(isPair(1), isPair({}));
          return out.join('');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void ADeoptMakesTheFeedbackMegamorphic()
    {
        // isPair is optimized with HasInPrototypeChain; the constructor's new
        // prototype deoptimizes it, and the next optimization takes the
        // generic path, so it does not deoptimize again.
        Assert.Equal("true,false,false,true,false", MaglevCompilerTest.Run("--maglev --no-concurrent-recompilation", """
            function Pair() {}
            function isPair(o) { return o instanceof Pair; }
            %PrepareFunctionForOptimization(isPair);
            isPair(new Pair()); isPair({});
            %OptimizeFunctionOnNextCall(isPair);
            var r = [isPair(new Pair())];
            Pair.prototype = {};
            r.push(isPair(new (function () {})()));
            r.push(%ActiveTierIsMaglev(isPair));
            %OptimizeFunctionOnNextCall(isPair);
            r.push(isPair(new Pair()), isPair({}));
            r.join();
            """));
    }
}
