// Keyed loads of string characters (TryBuildElementAccessOnString,
// maglev-graph-builder.cc): flat and cons strings, two-byte characters and
// surrogates, out-of-bounds, negative, fractional and string keys.
namespace V8Sharp.Tests.Maglev;

public class MaglevStringAtTest
{
    public static TheoryData<string> Snippets => new()
    {
        """
        (function() {
          function at(s, i) { return s[i]; }
          function scan(s) { var r = ''; for (var i = 0; i < s.length; i++) r += s[i] == 'a' ? 'A' : s[i]; return r; }
          var cons = 'abc'; for (var k = 0; k < 10; k++) cons = cons + 'xa' + k;
          var out = [];
          for (var i = 0; i < 150; i++) {
            out.push(at('hello world', i % 11), scan(i % 2 ? cons : 'banana' + i));
            if (i == 120) out.push(String(at('abc', 5)), String(at('abc', -1)), at('é中😀', 2), String(at('abc', 1.5)), at('abc', '1'));
          }
          return out.join('|');
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);
}
