// The conformance gates: the runner against the oracle on a handful of tests
// (the infrastructure works), and V8Sharp on the curated list of tests it must
// keep passing (curated/v8sharp.txt, grown as the port lands).
using V8Sharp.TestRunner;

namespace V8Sharp.Conformance.Tests;

public class ConformanceGates
{
    /// <summary>Tests the oracle passes; they cover the harnesses (mjsunit.js,
    /// test262 includes, async, modules, negative tests, realms, message output).</summary>
    public static readonly string[] OracleSmokeTests =
    [
        "mjsunit/array-sort",
        "mjsunit/array-splice",
        "mjsunit/array-concat",
        "mjsunit/array-join",
        "mjsunit/string-replace",
        "mjsunit/json",
        "mjsunit/regexp-global",
        "mjsunit/math-min-max",
        "mjsunit/number-tostring",
        "mjsunit/object-define-property",
        "mjsunit/es6/promise-all",
        "mjsunit/es6/generators-objects",
        "mjsunit/es6/classes",
        "mjsunit/es6/proxies",
        "mjsunit/es6/typedarray",
        "mjsunit/harmony/modules-import-1",
        "mjsunit/es6/templates",
        "mjsunit/cross-realm-global-prototype",
        "mjsunit/realm-property-access",
        "mjsunit/es8/async-await-basic",
        "test262/built-ins/Array/prototype/map/15.4.4.19-1-1",
        "test262/built-ins/Array/prototype/map/create-species",
        "test262/built-ins/Array/from/iter-cstm-ctor",
        "test262/built-ins/Promise/all/resolve-element-function-nonconstructor",
        "test262/built-ins/Promise/prototype/then/resolve-pending-fulfilled-non-obj",
        "test262/built-ins/Proxy/get/trap-is-undefined",
        "test262/built-ins/ArrayBuffer/prototype/slice/end-default-if-absent",
        "test262/built-ins/TypedArray/prototype/fill/detached-buffer",
        "test262/built-ins/RegExp/prototype/exec/S15.10.6.2_A10",
        "test262/built-ins/JSON/parse/text-negative-zero",
        "test262/built-ins/Function/prototype/toString/method-class-expression",
        "test262/built-ins/Object/defineProperty/15.2.3.6-4-1",
        "test262/built-ins/Symbol/for/cross-realm",
        "test262/language/expressions/async-arrow-function/await-as-param-ident-nested-arrow-parameter-position",
        "test262/language/statements/class/definition/methods",
        "test262/language/module-code/eval-this",
        "test262/language/module-code/instn-once",
        "test262/language/statements/for-of/iterator-next-reference",
        "test262/annexB/built-ins/String/prototype/substr/start-and-length-as-numbers",
        "test262/language/types/number/S8.5_A2.1",
        "test262/language/expressions/await/async-await-interleaved",
        "test262/language/expressions/await/early-errors-await-not-simple-assignment-target",
        "message/fail/array-spread-non-iterable-object",
        "message/fail/arrow-bare-rest-param",
        "message/fail/simple-throw",
        "webkit/fast/js/basic-strict-mode",
    ];

    [Fact]
    public async Task OracleRunsSmokeTests()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "v8sharp-conformance-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        try
        {
            var output = new StringWriter();
            var options = new RunnerOptions
            {
                Engine = "oracle",
                V8Root = TestPaths.V8Root,
                ExpectationsDirectory = scratch,
                JsonPath = Path.Combine(scratch, "results.json"),
                Jobs = 2,
                TimeoutSeconds = 60,
                Out = output,
            };
            options.Suites.AddRange(["mjsunit", "test262", "message", "webkit"]);
            options.Filters.AddRange(OracleSmokeTests);
            var runner = new Runner(options);
            await runner.RunAsync(TestContext.Current.CancellationToken);

            var results = runner.Reports.SelectMany(r => r.Results).Where(r => !r.Skipped).ToList();
            var ran = results.Select(r => r.Test.Suite + "/" + r.Test.Name).ToHashSet();
            foreach (var t in OracleSmokeTests) Assert.True(ran.Contains(t), $"{t} did not run\n{output}");
            var unexpected = results.Where(r => r.Unexpected).Select(r => $"{r.Test.FullId}: {r.Outcome}\n{r.Output!.Stdout}").ToList();
            Assert.True(unexpected.Count == 0, string.Join("\n", unexpected) + "\n" + output);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The curated tests V8Sharp passes (curated/v8sharp.txt: one
    /// <c>suite/test</c> id per line). Empty until the engine runs JavaScript.</summary>
    public static TheoryData<string> V8SharpCurated()
    {
        var data = new TheoryData<string>();
        string file = Path.Combine(TestPaths.ProjectDirectory, "curated", "v8sharp.txt");
        foreach (var raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            data.Add(line);
        }
        return data;
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(V8SharpCurated))]
    public async Task V8SharpPasses(string test)
    {
        var output = new StringWriter();
        var options = new RunnerOptions
        {
            Engine = "v8sharp",
            V8Root = TestPaths.V8Root,
            ExpectationsDirectory = Path.Combine(TestPaths.V8Root, "dotnet", "tools", "V8Sharp.TestRunner", "expectations"),
            JsonPath = Path.Combine(Path.GetTempPath(), $"v8sharp-curated-{Environment.ProcessId}.json"),
            Jobs = 1,
            Out = output,
        };
        int at = test.IndexOf('@');
        string id = at < 0 ? test : test[..at];
        options.Suites.Add(id[..id.IndexOf('/')]);
        options.Filters.Add(test);
        var runner = new Runner(options);
        await runner.RunAsync(TestContext.Current.CancellationToken);
        var results = runner.Reports.SelectMany(r => r.Results).Where(r => !r.Skipped && r.Test.FullId == test).ToList();
        Assert.True(results.Count == 1, $"{test} did not run\n{output}");
        Assert.False(results[0].Unexpected, $"{test}: {results[0].Outcome}\n{results[0].Output!.Stdout}");
    }
}
