// Polymorphic keyed loads whose maps mix arrays and typed arrays
// (TryBuildPolymorphicElementAccess, maglev-graph-builder.cc): each map's
// arm, out-of-bounds deopts, detached buffers and a new map after
// optimization.
namespace V8Sharp.Tests.Maglev;

public class MaglevPolymorphicElementTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          function sum(a) { var s = 0; for (var i = 0; i < a.length; i++) s += a[i]; return s; }
          var u8 = new Uint8Array([1, 2, 250]), f64 = new Float64Array([0.5, -1.25]), arr = [3, 4, 5], dbl = [1.5, 2.5];
          var i32 = new Int32Array([-7, 1 << 30]);
          var out = [];
          for (var k = 0; k < 150; k++) out.push(sum(k & 1 ? u8 : arr), sum(k % 3 ? f64 : dbl));
          function at(a, i) { return a[i]; }
          for (var k = 0; k < 150; k++) out.push(at(k & 1 ? u8 : arr, k % 3));
          out.push(String(at(u8, 7)), String(at(arr, 9)), at(i32, 1), sum(i32), sum(['x', 'y']));
          var buf = new ArrayBuffer(8), view = new Uint16Array(buf); view[1] = 9;
          out.push(at(view, 1));
          return out.slice(-12).join(',');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);
}
