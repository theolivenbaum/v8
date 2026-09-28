// The standard runner: tools/run-tests.py / standard_runner.py for the
// default variant. Loads suites and status files, filters, runs the tests on
// worker processes, compares outcomes with the status file and with the
// committed expectation files, and reports.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using V8Sharp.TestRunner.Execution;
using V8Sharp.TestRunner.OutProc;
using V8Sharp.TestRunner.Results;
using V8Sharp.TestRunner.Status;
using V8Sharp.TestRunner.Suites;

namespace V8Sharp.TestRunner;

public sealed class RunnerOptions
{
    public List<string> Suites { get; } = [];
    public List<string> Filters { get; } = [];
    public string Engine { get; set; } = "v8sharp";
    public int Jobs { get; set; } = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 3));
    public double TimeoutSeconds { get; set; } = 60;
    public bool UpdateExpectations { get; set; }
    public bool RunSkipped { get; set; }
    public bool ListOnly { get; set; }
    public int ShowFailures { get; set; } = 20;

    /// <summary>Unexpected outcomes are run again this many times; a test that
    /// then behaves as expected counts as passing and is marked flaky.</summary>
    public int RerunFailures { get; set; } = 1;
    public string? Test262Root { get; set; }
    public string? JsonPath { get; set; }
    public string V8Root { get; set; } = "";
    public string ExpectationsDirectory { get; set; } = "";
    public TextWriter Out { get; set; } = Console.Out;
}

/// <summary>The outcome of one test run.</summary>
public sealed record TestResult(TestCase Test, string Outcome, RunOutput? Output, bool Unexpected, bool Known)
{
    public bool Skipped => Output is null;

    /// <summary>The first run was unexpected, a rerun was not.</summary>
    public bool Flaky { get; init; }
}

public sealed class SuiteReport
{
    public required string Suite { get; init; }
    public string? Unavailable { get; init; }
    public List<TestResult> Results { get; } = [];
    public List<string> NewlyFailing { get; } = [];
    public List<string> NewlyPassing { get; } = [];
    public ExpectationsFile? Expectations { get; set; }
    public int Passed => Results.Count(r => !r.Skipped && !r.Unexpected);
    public int Failed => Results.Count(r => !r.Skipped && r.Unexpected);
    public int Skipped => Results.Count(r => r.Skipped);
}

public sealed class Runner(RunnerOptions options)
{
    public List<SuiteReport> Reports { get; } = [];

    public static string FindV8Root(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "include", "v8-version.h")) &&
                Directory.Exists(Path.Combine(dir.FullName, "test", "mjsunit")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"cannot find the V8 root above {start}");
    }

    /// <summary>--test262-root, else test/test262/data, else the shared checkout.</summary>
    public static string FindTest262Root(string v8Root, string? explicitRoot)
    {
        if (explicitRoot is not null) return Path.GetFullPath(explicitRoot);
        string inTree = Path.Combine(v8Root, "test", "test262", "data");
        if (Directory.Exists(Path.Combine(inTree, "test"))) return inTree;
        return "/home/user/theolivenbaum/test262";
    }

    public static IReadOnlyDictionary<string, object?> LoadBuildConfig(string engine)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "buildconfig", engine + ".json");
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_')) continue;
                vars[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => p.Value.GetDouble(),
                    JsonValueKind.String => p.Value.GetString(),
                    _ => null,
                };
            }
        }
        // base_runner.py _get_statusfile_variables: the runner's own variables.
        vars["all_arm64_features"] = false;
        vars["byteorder"] = BitConverter.IsLittleEndian ? "little" : "big";
        vars["num_fuzzer"] = false;
        vars["deopt_fuzzer"] = false;
        vars["device_type"] = null;
        vars["endurance_fuzzer"] = false;
        vars["gc_fuzzer"] = false;
        vars["gc_stress"] = false;
        vars["isolates"] = false;
        vars["interrupt_fuzzer"] = false;
        vars["mode"] = "release";
        vars["no_harness"] = false;
        vars["no_simd_hardware"] = false;
        vars["novfp3"] = false;
        vars["optimize_for_size"] = false;
        vars["system"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return vars;
    }

    /// <summary>A --filter glob: <c>**</c> spans directories, <c>*</c> does not,
    /// <c>?</c> is one character. Matched against <c>suite/name</c> and
    /// <c>name</c>; a pattern without wildcards is a path prefix (as
    /// run-tests.py takes <c>mjsunit/es6</c>).</summary>
    public static Func<TestCase, bool> CompileFilter(IReadOnlyList<string> globs)
    {
        if (globs.Count == 0) return _ => true;
        var regexes = new List<Regex>();
        foreach (var g in globs)
        {
            string glob = g.Replace('\\', '/');
            if (glob.EndsWith(".js", StringComparison.Ordinal) || glob.EndsWith(".mjs", StringComparison.Ordinal))
            {
                glob = glob[..glob.LastIndexOf('.')];
            }
            var sb = new StringBuilder("^");
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            // No wildcard: a prefix of whole path segments.
            sb.Append(glob.IndexOfAny(['*', '?']) < 0 ? "(/.*|@.*)?$" : "(@.*)?$");
            regexes.Add(new Regex(sb.ToString(), RegexOptions.CultureInvariant));
        }
        return t => regexes.Exists(r => r.IsMatch(t.FullId) || r.IsMatch(t.Id));
    }

    public async Task<int> RunAsync(CancellationToken cancel = default)
    {
        var buildVars = LoadBuildConfig(options.Engine);
        var context = new SuiteContext(options.V8Root, FindTest262Root(options.V8Root, options.Test262Root), buildVars);
        var filter = CompileFilter(options.Filters);
        var all = new List<(TestSuite Suite, List<TestCase> Tests)>();
        foreach (var name in options.Suites)
        {
            var suite = TestSuite.Create(name, context);
            if (suite.UnavailableReason is { } why)
            {
                options.Out.WriteLine($">>> {name}: skipped, {why}");
                Reports.Add(new SuiteReport { Suite = name, Unavailable = why });
                continue;
            }
            var tests = suite.LoadTests().Where(filter).ToList();
            foreach (var w in suite.Status!.Warnings.Distinct()) options.Out.WriteLine($">>> warning: {w}");
            all.Add((suite, tests));
        }
        if (options.ListOnly)
        {
            foreach (var (suite, tests) in all)
            {
                var expectations = new ExpectationsFile(options.ExpectationsDirectory, suite.Name, options.Engine);
                foreach (var t in tests)
                {
                    if (t.SkipReason is null && expectations.SkipReasonFor(t.Id) is { } engineSkip) t.SkipReason = engineSkip;
                    options.Out.WriteLine($"{t.FullId}  [{string.Join(' ', t.CommandLine)}]{(t.SkipReason is null ? "" : "  SKIP: " + t.SkipReason)}");
                }
            }
            return 0;
        }

        var toRun = new List<TestCase>();
        var reportsBySuite = new Dictionary<string, SuiteReport>(StringComparer.Ordinal);
        foreach (var (suite, tests) in all)
        {
            var report = new SuiteReport
            {
                Suite = suite.Name,
                Expectations = new ExpectationsFile(options.ExpectationsDirectory, suite.Name, options.Engine),
            };
            Reports.Add(report);
            reportsBySuite[suite.Name] = report;
            foreach (var t in tests)
            {
                // An expectation glob whose reason starts with "SKIP" does not run on this engine.
                if (t.SkipReason is null && report.Expectations.SkipReasonFor(t.Id) is { } engineSkip) t.SkipReason = engineSkip;
                if (t.SkipReason is not null && !options.RunSkipped) report.Results.Add(new TestResult(t, Outcome.Skip, null, false, false));
                else toRun.Add(t);
            }
        }
        // Slow tests first (testsuite.py: heavy, slow, remaining), then by name.
        toRun = [.. toRun.OrderBy(t => t.IsSlow ? 0 : 1).ThenBy(t => t.FullId, StringComparer.Ordinal)];

        int total = toRun.Count, done = 0, unexpected = 0, known = 0;
        var clock = Stopwatch.StartNew();
        var progressLock = new object();
        bool tty = !Console.IsOutputRedirected && options.Out == Console.Out;
        var lastProgress = TimeSpan.Zero;
        options.Out.WriteLine($">>> Running {total} tests ({options.Engine}, {options.Jobs} jobs, timeout {options.TimeoutSeconds}s)");

        void OnResult(TestCase t, RunOutput output)
        {
            string outcome = t.OutProc.GetOutcome(output);
            bool isUnexpected = !t.ExpectedOutcomes.Contains(outcome);
            var report = reportsBySuite[t.Suite];
            bool isKnown = isUnexpected && report.Expectations!.IsKnownFailure(t.Id);
            lock (progressLock)
            {
                report.Results.Add(new TestResult(t, outcome, output, isUnexpected, isKnown));
                done++;
                if (isUnexpected) { if (isKnown) known++; else unexpected++; }
                if (isUnexpected && !isKnown && report.Expectations!.Exists && unexpected <= options.ShowFailures)
                {
                    if (tty) options.Out.Write("\r\x1b[K");
                    options.Out.WriteLine($"=== {t.FullId}: {outcome} (expected {string.Join('/', t.ExpectedOutcomes)})");
                }
                var now = clock.Elapsed;
                if (tty || now - lastProgress > TimeSpan.FromSeconds(15) || done == total)
                {
                    lastProgress = now;
                    string line = string.Create(CultureInfo.InvariantCulture,
                        $"[{now:hh\\:mm\\:ss}|{100.0 * done / Math.Max(1, total),5:0.0}%|+{done - unexpected - known,6}|-{unexpected,5}|known {known,5}] {t.FullId}");
                    if (tty) options.Out.Write("\r\x1b[K" + (line.Length > Console.WindowWidth - 1 && Console.WindowWidth > 20 ? line[..(Console.WindowWidth - 1)] : line));
                    else options.Out.WriteLine(line);
                }
            }
        }

        var executor = new Executor(options.Engine, options.V8Root, options.Jobs, TimeSpan.FromSeconds(options.TimeoutSeconds));
        await executor.RunAsync(toRun, OnResult, cancel).ConfigureAwait(false);
        if (tty) options.Out.WriteLine();
        await RerunFailuresAsync(executor, reportsBySuite, cancel).ConfigureAwait(false);

        foreach (var report in Reports)
        {
            if (report.Unavailable is not null) continue;
            report.Results.Sort((a, b) => string.CompareOrdinal(a.Test.Id, b.Test.Id));
            var ranIds = new HashSet<string>(StringComparer.Ordinal);
            var failing = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in report.Results)
            {
                if (r.Skipped) continue;
                ranIds.Add(r.Test.Id);
                if (r.Unexpected) failing[r.Test.Id] = r.Outcome;
                if (r.Unexpected && !r.Known) report.NewlyFailing.Add(r.Test.Id);
            }
            foreach (var id in report.Expectations!.Failing.Keys)
            {
                if (ranIds.Contains(id) && !failing.ContainsKey(id)) report.NewlyPassing.Add(id);
            }
            // A glob none of whose tests fail any more is reported as a whole.
            foreach (var (glob, _, regex) in report.Expectations.Patterns)
            {
                bool any = false, anyFailing = false;
                foreach (var id in ranIds)
                {
                    if (!regex.IsMatch(id)) continue;
                    any = true;
                    if (failing.ContainsKey(id)) { anyFailing = true; break; }
                }
                if (any && !anyFailing) report.NewlyPassing.Add(glob);
            }
            if (options.UpdateExpectations) report.Expectations.Update(ranIds, failing);
        }

        PrintSummary(clock.Elapsed);
        WriteJson(clock.Elapsed);
        bool anyNewFailures = Reports.Any(r => r.NewlyFailing.Count > 0);
        return options.UpdateExpectations || !anyNewFailures ? 0 : 1;
    }

    /// <summary>run-tests.py --rerun-failures-count: runs unexpected results
    /// again (not the ones a glob line of the expectation file covers).</summary>
    async Task RerunFailuresAsync(Executor executor, Dictionary<string, SuiteReport> reports, CancellationToken cancel)
    {
        for (int pass = 0; pass < options.RerunFailures; pass++)
        {
            var index = new Dictionary<TestCase, (SuiteReport Report, int Index)>();
            foreach (var report in reports.Values)
            {
                for (int i = 0; i < report.Results.Count; i++)
                {
                    var r = report.Results[i];
                    if (r.Unexpected && report.Expectations!.MatchingPattern(r.Test.Id) is null) index[r.Test] = (report, i);
                }
            }
            if (index.Count == 0 || cancel.IsCancellationRequested) return;
            options.Out.WriteLine($">>> Rerunning {index.Count} unexpected results");
            var sync = new object();
            int flaky = 0;
            await executor.RunAsync([.. index.Keys], (t, output) =>
            {
                string outcome = t.OutProc.GetOutcome(output);
                if (!t.ExpectedOutcomes.Contains(outcome))
                {
                    return;
                }
                lock (sync)
                {
                    var (report, i) = index[t];
                    report.Results[i] = new TestResult(t, outcome, output, false, false) { Flaky = true };
                    flaky++;
                }
            }, cancel).ConfigureAwait(false);
            options.Out.WriteLine($">>> {flaky} of them behaved as expected on rerun (flaky)");
        }
    }

    static string DirectoryOf(TestCase t)
    {
        var parts = t.Name.Split('/');
        int depth = t.Suite == "test262" ? 2 : 1;
        return parts.Length > depth ? string.Join('/', parts[..depth]) : ".";
    }

    void PrintSummary(TimeSpan elapsed)
    {
        var o = options.Out;
        o.WriteLine();
        o.WriteLine($"=== Summary ({options.Engine}, {elapsed:hh\\:mm\\:ss})");
        foreach (var r in Reports)
        {
            if (r.Unavailable is not null)
            {
                o.WriteLine($"{r.Suite}: not run ({r.Unavailable})");
                continue;
            }
            int ran = r.Passed + r.Failed;
            o.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Suite}: {ran} run, {r.Passed} as expected ({100.0 * r.Passed / Math.Max(1, ran):0.00}%), {r.Failed} unexpected, {r.Skipped} skipped"));
            if (r.Expectations!.Exists || options.UpdateExpectations)
            {
                o.WriteLine($"  vs {Path.GetFileName(r.Expectations.Path)}: {r.NewlyFailing.Count} newly failing, {r.NewlyPassing.Count} newly passing");
            }
            var byDir = r.Results.Where(x => !x.Skipped).GroupBy(x => DirectoryOf(x.Test)).OrderBy(g => g.Key, StringComparer.Ordinal);
            var rows = byDir.Select(g => (Dir: g.Key, Total: g.Count(), Fail: g.Count(x => x.Unexpected))).Where(x => x.Fail > 0).ToList();
            if (rows.Count > 0)
            {
                o.WriteLine("  directory                                   run   unexpected");
                foreach (var (dir, tot, fail) in rows.OrderByDescending(x => x.Fail).Take(25))
                {
                    o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {dir,-40} {tot,6} {fail,8}"));
                }
                if (rows.Count > 25) o.WriteLine($"  ... {rows.Count - 25} more directories (see the JSON results)");
            }
            foreach (var id in r.NewlyFailing.Take(options.ShowFailures))
            {
                var res = r.Results.First(x => x.Test.Id == id);
                o.WriteLine($"  NEW FAIL {r.Suite}/{id}: {res.Outcome}");
            }
            if (r.NewlyFailing.Count > options.ShowFailures) o.WriteLine($"  ... and {r.NewlyFailing.Count - options.ShowFailures} more newly failing");
            foreach (var id in r.NewlyPassing.Take(options.ShowFailures)) o.WriteLine($"  NEW PASS {r.Suite}/{id}");
            if (r.NewlyPassing.Count > options.ShowFailures) o.WriteLine($"  ... and {r.NewlyPassing.Count - options.ShowFailures} more newly passing");
        }
    }

    void WriteJson(TimeSpan elapsed)
    {
        string path = options.JsonPath ?? Path.Combine(options.V8Root, "dotnet", "artifacts", "testrunner",
            $"{options.Engine}-{string.Join('+', options.Suites)}.json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new JsonObject
        {
            ["engine"] = options.Engine,
            ["durationSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
            ["filters"] = new JsonArray([.. options.Filters.Select(f => (JsonNode?)f)]),
        };
        var suites = new JsonArray();
        foreach (var r in Reports)
        {
            var s = new JsonObject { ["suite"] = r.Suite };
            if (r.Unavailable is not null)
            {
                s["unavailable"] = r.Unavailable;
                suites.Add(s);
                continue;
            }
            s["passed"] = r.Passed;
            s["unexpected"] = r.Failed;
            s["skipped"] = r.Skipped;
            s["newlyFailing"] = new JsonArray([.. r.NewlyFailing.Select(x => (JsonNode?)x)]);
            s["newlyPassing"] = new JsonArray([.. r.NewlyPassing.Select(x => (JsonNode?)x)]);
            var dirs = new JsonObject();
            foreach (var g in r.Results.GroupBy(x => DirectoryOf(x.Test)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                dirs[g.Key] = new JsonObject
                {
                    ["run"] = g.Count(x => !x.Skipped),
                    ["unexpected"] = g.Count(x => x.Unexpected),
                    ["skipped"] = g.Count(x => x.Skipped),
                };
            }
            s["directories"] = dirs;
            var tests = new JsonArray();
            foreach (var t in r.Results)
            {
                var j = new JsonObject
                {
                    ["id"] = t.Test.Id,
                    ["outcome"] = t.Outcome,
                    ["expected"] = string.Join('/', t.Test.ExpectedOutcomes),
                };
                if (t.Skipped)
                {
                    j["skipReason"] = t.Test.SkipReason;
                }
                else
                {
                    j["unexpected"] = t.Unexpected;
                    if (t.Flaky) j["flaky"] = true;
                    j["ms"] = Math.Round(t.Output!.Duration.TotalMilliseconds);
                    if (t.Unexpected)
                    {
                        j["known"] = t.Known;
                        j["exitCode"] = t.Output.ExitCode;
                        j["command"] = string.Join(' ', t.Test.CommandLine);
                        j["stdout"] = Truncate(t.Output.Stdout);
                        if (t.Output.Stderr.Length > 0) j["stderr"] = Truncate(t.Output.Stderr);
                        if (t.Test.OutProc.ErrorDetails(t.Output) is { } details) j["details"] = Truncate(details);
                    }
                }
                tests.Add(j);
            }
            s["tests"] = tests;
            suites.Add(s);
        }
        root["suites"] = suites;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        options.Out.WriteLine($"Results: {path}");
    }

    static string Truncate(string s) => s.Length <= 4000 ? s : s[..2000] + "\n[...]\n" + s[^2000..];
}
