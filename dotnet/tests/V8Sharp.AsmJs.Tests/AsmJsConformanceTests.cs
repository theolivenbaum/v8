// The asm.js mjsunit and message tests of V8 14.7.173.23 (test/mjsunit/asm,
// test/mjsunit/regress/asm, test/mjsunit/wasm/asm-*.js,
// test/mjsunit/asm-directive.js, test/message/asm-*.js), run by the
// TestRunner with the v8sharp engine against v8-14.7/test.
using V8Sharp.TestRunner;

namespace V8Sharp.AsmJs.Tests;

public class AsmJsConformanceTests
{
    [Theory]
    [InlineData("mjsunit")]
    [InlineData("message")]
    public async Task V8SharpPassesSuite(string suite)
    {
        var output = new StringWriter();
        var options = new RunnerOptions
        {
            Engine = "v8sharp",
            V8Root = TestPaths.V8Root147,
            ExpectationsDirectory = Path.Combine(TestPaths.ProjectDirectory, "expectations"),
            JsonPath = Path.Combine(Path.GetTempPath(), $"v8sharp-asmjs-{suite}-{Environment.ProcessId}.json"),
            Jobs = 2,
            TimeoutSeconds = 300,
            Out = output,
        };
        options.Suites.Add(suite);
        var runner = new Runner(options);
        await runner.RunAsync(TestContext.Current.CancellationToken);

        var results = runner.Reports.SelectMany(r => r.Results).Where(r => !r.Skipped).ToList();
        Assert.True(results.Count > 0, $"no {suite} tests ran\n{output}");
        // An unexpected outcome listed in expectations/<suite>.v8sharp.txt is known.
        var unexpected = results.Where(r => r.Unexpected && !r.Known)
            .Select(r => $"{r.Test.FullId}: {r.Outcome}\n{r.Output?.Stdout}").ToList();
        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected));
    }
}
