// Explicit benchmark of JSON.parse / JSON.stringify on a generated document:
//   dotnet test -c Release tests/V8Sharp.Tests --filter "FullyQualifiedName~JsonBenchmark" -- xUnit.Explicit=only
using System.Diagnostics;
using System.Text;
using V8Sharp.Tests.Builtins;

namespace V8Sharp.Tests.Json;

public class JsonBenchmark : IntrinsicsTestBase
{
    static string Document(int records)
    {
        var sb = new StringBuilder("[");
        var rng = new Random(1);
        for (int i = 0; i < records; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i)
              .Append(",\"name\":\"user").Append(i).Append("\",\"score\":").Append((rng.NextDouble() * 1000).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"active\":").Append(i % 2 == 0 ? "true" : "false")
              .Append(",\"tags\":[\"a\",\"b\",\"c\"],\"text\":\"line\\nwith \\\"quotes\\\" and \\u00e9\"}");
        }
        return sb.Append(']').ToString();
    }

    [Fact(Explicit = true)]
    public void ParseAndStringify()
    {
        string doc = Document(10000);
        JSValue source = Str(doc);
        JSValue parsed = default;
        for (int i = 0; i < 3; i++) parsed = CallStatic("JSON.parse", source);
        var sw = Stopwatch.StartNew();
        const int n = 10;
        for (int i = 0; i < n; i++) parsed = CallStatic("JSON.parse", source);
        double parseMs = sw.Elapsed.TotalMilliseconds / n;
        for (int i = 0; i < 3; i++) CallStatic("JSON.stringify", parsed);
        sw.Restart();
        JSValue text = default;
        for (int i = 0; i < n; i++) text = CallStatic("JSON.stringify", parsed);
        double stringifyMs = sw.Elapsed.TotalMilliseconds / n;
        string msg = $"JSON: {doc.Length / 1024} KB; parse {parseMs:F2} ms ({doc.Length / parseMs / 1000:F1} MB/s), stringify {stringifyMs:F2} ms";
        TestContext.Current.SendDiagnosticMessage(msg);
        Console.WriteLine(msg);
        Assert.Equal(doc.Length - 5 * 10000, S(text).Length);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "v8sharp-json-bench.txt"), msg);
    }
}
