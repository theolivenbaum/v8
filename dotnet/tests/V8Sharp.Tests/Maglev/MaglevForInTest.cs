// Tests of Maglev's for-in (MaglevGraphBuilder.ForIn.cs; maglev-graph-builder.cc
// VisitForInPrepare, VisitForInNext, TryBuildGetKeyedPropertyWithEnumeratedKey):
// enum cache keys and indices, map checks after side effects, properties
// added and deleted in the loop, nested loops, receivers without an enum
// cache (arrays, strings, dictionary mode), OSR.
namespace V8Sharp.Tests.Maglev;

public class MaglevForInTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          function sum(o) { var s = ''; for (var k in o) s += k + '=' + o[k] + ';'; return s; }
          function sumOther(o, p) { var s = ''; for (var k in o) s += k + ':' + p[k] + ';'; return s; }
          function mutate(o, i) {
            var s = '';
            for (var k in o) {
              s += k + o[k];
              if (i % 5 == 3 && k == 'b') o.z = i;
              if (i % 7 == 4 && k == 'a') delete o.c;
              s += o[k];
            }
            return s;
          }
          function nested(o) {
            var s = '';
            for (var k in o) { for (var j in o) s += k + j + o[j]; s += o[k]; }
            return s;
          }
          function withCall(o, f) { var s = ''; for (var k in o) { f(o); s += o[k] + ','; } return s; }
          function Pt(x, y) { this.x = x; this.y = y; }
          Pt.prototype.proto = 'P';
          function big() { var o = {}; for (var i = 0; i < 20; i++) o['f' + i] = i * 1.5; return o; }
          function dict() { var o = { a: 1, b: 2 }; delete o.a; o.c = 3; return o; }
          var out = [];
          var shapes = [{ a: 1, b: 2, c: 3 }, new Pt(1, 2), new Pt(3.5, 'y'), big(), dict(), [1, 2, 3], 'str', { a: 1.5, b: 'two' }];
          for (var i = 0; i < 120; i++) {
            var o = shapes[i % shapes.length];
            out.push(sum(o));
            out.push(sumOther(shapes[0], i % 3 == 0 ? shapes[0] : { a: 'A', b: 'B', c: 'C' }));
            out.push(mutate({ a: 1, b: 2, c: 3 }, i));
            out.push(nested(i % 2 ? shapes[1] : shapes[0]));
            out.push(withCall({ a: 1, b: 2 }, i == 100 ? function (q) { q.extra = 1; q.b = 'changed'; } : function () {}));
          }
          return out.join('|');
        })()
        """,
        """
        (function() {
          var bigObj = {}; for (var i = 0; i < 30; i++) bigObj['k' + i] = i;
          var acc = 0;
          for (var rep = 0; rep < 500; rep++) for (var k in bigObj) { acc += bigObj[k]; if (rep == 400 && k == 'k3') bigObj.k31 = 1; }
          return acc;
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void EnumeratedKeyedLoadsStayOptimized()
    {
        // The enum cache path (no generic ForInNext call): the code stays
        // optimized through the loop.
        Assert.Equal("a1b2c3,true", MaglevCompilerTest.Run("--maglev --no-concurrent-recompilation", """
            function f(o) { var s = ''; for (var k in o) s += k + o[k]; return s; }
            var o = { a: 1, b: 2, c: 3 };
            %PrepareFunctionForOptimization(f);
            f(o); f(o);
            %OptimizeFunctionOnNextCall(f);
            var r = f(o);
            [r, %ActiveTierIsMaglev(f)].join();
            """));
    }
}
