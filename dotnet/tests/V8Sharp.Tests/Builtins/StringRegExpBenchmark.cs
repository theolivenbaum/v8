// Explicit benchmark of hot String/RegExp builtin paths against the oracle:
//   dotnet test -c Release tests/V8Sharp.Tests --filter "FullyQualifiedName~StringRegExpBenchmark" -- xUnit.Explicit=only
using System.Diagnostics;
using V8Sharp.Oracle;

namespace V8Sharp.Tests.Builtins;

public class StringRegExpBenchmark(ITestOutputHelper output) : BuiltinsTestBase
{
    const int kIterations = 200;

    double Time(Action a)
    {
        a();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < kIterations; i++) a();
        return sw.Elapsed.TotalMilliseconds / kIterations;
    }

    [Fact(Explicit = true)]
    public void Run()
    {
        string text = string.Concat(Enumerable.Repeat("The quick brown fox, jumps over the lazy dog; 12345 times. ", 2000));
        JSValue s = S(text);
        var cases = new (string Name, string Js, Action Action)[]
        {
            ("replace /o/g", "s.replace(/o/g,'0')", () => StrCall(s, "replace", Re("o", "g"), S("0"))),
            ("replace /(\\w+)/g $1", "s.replace(/(\\w+)/g,'[$1]')", () => StrCall(s, "replace", Re("(\\w+)", "g"), S("[$1]"))),
            ("split /[,;] ?/", "s.split(/[,;] ?/)", () => StrCall(s, "split", Re("[,;] ?"))),
            ("split ' '", "s.split(' ')", () => StrCall(s, "split", S(" "))),
            ("match /\\d+/g", "s.match(/\\d+/g)", () => StrCall(s, "match", Re("\\d+", "g"))),
            ("exec loop /fox/g", "(function(){var r=/fox/g,n=0;while(r.exec(s))n++;return n;})()",
                () => { JSValue r = Re("fox", "g"); while (!ReCall(r, "exec", s).IsNull) { } }),
            ("indexOf last", "s.indexOf('dog; 12345 times. The quick brown fox, jumps over the lazy cat')",
                () => StrCall(s, "indexOf", S("dog; 12345 times. The quick brown fox, jumps over the lazy cat"))),
            ("toUpperCase", "s.toUpperCase()", () => StrCall(s, "toUpperCase")),
            ("replaceAll ' '", "s.replaceAll(' ','_')", () => StrCall(s, "replaceAll", S(" "), S("_"))),
        };

        using var v8 = new ReferenceV8();
        v8.Run("var s = " + ReferenceV8.JsQuote(text) + ";");
        foreach (var (name, js, action) in cases)
        {
            double ours = Time(action);
            string o = v8.Run($"var t0=Date.now();for(var i=0;i<{kIterations};i++){js};print((Date.now()-t0)/{kIterations});");
            output.WriteLine($"{name,-24} v8sharp {ours,8:F3} ms   oracle {o.Trim(),8} ms");
        }
    }
}
