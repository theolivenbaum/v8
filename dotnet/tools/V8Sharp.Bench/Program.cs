// Benchmark driver: runs Octane and test/js-perf-test on V8Sharp and on the
// real V8 (the oracle) in its different tiering modes, one process per
// measurement, and prints a comparison table.
//
//   V8Sharp.Bench compare [--suites octane,perf:Operators,...] [--engines v8:jit,v8:jitless,v8sharp] [--runs N]
//   V8Sharp.Bench run --engine v8:jitless --suite octane:richards      (one measurement; used by compare)
//   V8Sharp.Bench list
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace V8Sharp.Bench;

public static partial class Program
{
    /// <summary>Octane 2.0 benchmarks, in the order of Octane's run.js.</summary>
    static readonly string[] OctaneBenchmarks =
    [
        "richards", "deltablue", "crypto", "raytrace", "earley-boyer", "regexp", "splay",
        "navier-stokes", "pdfjs", "mandreel", "gbemu", "code-load", "box2d", "zlib", "typescript",
    ];

    /// <summary>The oracle's modes: V8 flags for each tier configuration.</summary>
    static readonly Dictionary<string, string> V8Modes = new()
    {
        ["jit"] = "",                                   // the full pipeline, as shipped
        ["jitless"] = "--jitless",                      // Ignition only: phase 1's yardstick
        ["sparkplug"] = "--no-maglev --no-turbofan",    // Ignition + Sparkplug
        ["maglev"] = "--no-turbofan",                   // up to Maglev
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "run" => RunOne(args[1..]),
                "compare" => Compare(args[1..]),
                "list" => List(),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 2;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              V8Sharp.Bench compare [--suites s1,s2] [--engines e1,e2] [--runs N] [--timeout sec]
              V8Sharp.Bench run --engine <engine> --suite <suite>
              V8Sharp.Bench list
            engines: v8:jit, v8:jitless, v8:sparkplug, v8:maglev, v8sharp
            suites:  octane (all), octane:<name>, perf:<js-perf-test dir>
            Octane is fetched by tools/V8Sharp.Bench/fetch-octane.sh into dotnet/artifacts/octane.
            """);
        return 1;
    }

    static int List()
    {
        foreach (var b in OctaneBenchmarks) Console.WriteLine("octane:" + b);
        foreach (var d in Directory.GetDirectories(Paths.JsPerfTest).Order(StringComparer.Ordinal))
            if (File.Exists(Path.Combine(d, "run.js"))) Console.WriteLine("perf:" + Path.GetFileName(d));
        return 0;
    }

    // ---- One measurement (child process) -----------------------------------

    static int RunOne(string[] args)
    {
        string engine = Arg(args, "--engine") ?? throw new ArgumentException("--engine");
        string suite = Arg(args, "--suite") ?? throw new ArgumentException("--suite");
        var (workDir, files, driver) = Workload(suite);

        IBenchHost host = CreateHost(engine, workDir);
        var sw = Stopwatch.StartNew();
        foreach (var f in files) host.LoadFile(f);
        if (driver is not null) host.Execute(driver, "driver.js");
        sw.Stop();
        Console.WriteLine($"@wall-ms {sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
        host.Dispose();
        return 0;
    }

    static IBenchHost CreateHost(string engine, string workDir)
    {
        if (engine.StartsWith("v8:", StringComparison.Ordinal))
        {
            string mode = engine[3..];
            if (!V8Modes.TryGetValue(mode, out var flags)) throw new ArgumentException("unknown v8 mode " + mode);
            return new OracleHost(flags, workDir);
        }
        if (engine == "v8sharp") return new V8SharpHost(workDir);
        throw new ArgumentException("unknown engine " + engine);
    }

    /// <summary>Returns (working directory, files to load, driver source) for a suite.</summary>
    static (string WorkDir, string[] Files, string? Driver) Workload(string suite)
    {
        if (suite.StartsWith("octane:", StringComparison.Ordinal))
        {
            string name = suite[7..];
            string dir = Paths.Octane;
            if (!File.Exists(Path.Combine(dir, "base.js")))
                throw new FileNotFoundException("Octane is missing; run tools/V8Sharp.Bench/fetch-octane.sh", dir);
            string[] files = name switch
            {
                "gbemu" => ["base.js", "gbemu-part1.js", "gbemu-part2.js"],
                "typescript" => ["base.js", "typescript.js", "typescript-input.js", "typescript-compiler.js"],
                "zlib" => ["base.js", "zlib.js", "zlib-data.js"],
                _ => ["base.js", name + ".js"],
            };
            const string driver = """
                BenchmarkSuite.RunSuites({
                  NotifyResult: function (name, result) { print(name + '(Score): ' + result); },
                  NotifyError: function (name, error) { print(name + '(Error): ' + error); },
                  NotifyScore: function (score) { }
                });
                """;
            return (dir, files.Select(f => Path.Combine(dir, f)).ToArray(), driver);
        }
        if (suite.StartsWith("perf:", StringComparison.Ordinal))
        {
            string dir = Path.Combine(Paths.JsPerfTest, suite[5..]);
            return (dir, [Path.Combine(dir, "run.js")], null);
        }
        throw new ArgumentException("unknown suite " + suite);
    }

    // ---- Comparison (parent process) ---------------------------------------

    static int Compare(string[] args)
    {
        var suites = (Arg(args, "--suites") ?? "octane").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(s => s == "octane" ? OctaneBenchmarks.Select(b => "octane:" + b) : [s]).ToList();
        var engines = (Arg(args, "--engines") ?? "v8:jit,v8:jitless,v8sharp").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int runs = int.Parse(Arg(args, "--runs") ?? "1", CultureInfo.InvariantCulture);
        int timeout = int.Parse(Arg(args, "--timeout") ?? "600", CultureInfo.InvariantCulture);

        var results = new List<Measurement>();
        foreach (var suite in suites)
            foreach (var engine in engines)
                for (int r = 0; r < runs; r++)
                {
                    var m = Measure(engine, suite, timeout);
                    results.Add(m);
                    Console.Error.WriteLine($"{suite,-28} {engine,-14} {m.Summary}");
                }

        PrintTable(results, suites, engines);
        string outDir = Path.Combine(Paths.DotnetRoot, "artifacts", "bench");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"bench-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nresults: {outFile}");
        return 0;
    }

    public sealed record Measurement(string Suite, string Engine, Dictionary<string, double> Scores, double WallMs, string? Error)
    {
        public string Summary => Error is not null ? "ERROR " + Error
            : string.Join(" ", Scores.Select(kv => $"{kv.Key}={kv.Value.ToString("G6", CultureInfo.InvariantCulture)}")) + $" wall={WallMs:F0}ms";
    }

    [GeneratedRegex(@"^(?<name>.+?)\(Score\): (?<score>[0-9.eE+-]+)\s*$")]
    private static partial Regex ScoreLine();

    [GeneratedRegex(@"^(?<name>.+?)\(Error\): (?<err>.*)$")]
    private static partial Regex ErrorLine();

    static Measurement Measure(string engine, string suite, int timeoutSec)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Environment.ProcessPath is the apphost; when run as `dotnet V8Sharp.Bench.dll`
        // it is `dotnet` and the dll has to be passed along.
        if (Path.GetFileNameWithoutExtension(psi.FileName) == "dotnet")
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var a in new[] { "run", "--engine", engine, "--suite", suite }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            p.Kill(entireProcessTree: true);
            return new(suite, engine, [], 0, $"timeout after {timeoutSec}s");
        }
        p.WaitForExit();

        var scores = new Dictionary<string, double>();
        double wall = 0;
        string? error = null;
        foreach (var line in stdout.ToString().Split('\n'))
        {
            var l = line.TrimEnd('\r');
            var m = ScoreLine().Match(l);
            if (m.Success && double.TryParse(m.Groups["score"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                scores[m.Groups["name"].Value] = s;
            var e = ErrorLine().Match(l);
            if (e.Success) error ??= e.Groups["name"].Value + ": " + e.Groups["err"].Value;
            if (l.StartsWith("@wall-ms ", StringComparison.Ordinal))
                wall = double.Parse(l[9..], CultureInfo.InvariantCulture);
        }
        if (p.ExitCode != 0)
        {
            string err = stderr.ToString().Trim();
            error ??= $"exit {p.ExitCode}: " + (err.Length > 300 ? err[..300] : err);
        }
        return new(suite, engine, scores, wall, error);
    }

    static void PrintTable(List<Measurement> results, List<string> suites, string[] engines)
    {
        Console.WriteLine();
        var header = new StringBuilder($"{"benchmark",-36}");
        foreach (var e in engines) header.Append($"{e,14}");
        if (engines.Length > 1) header.Append("   ratio(last/first)");
        Console.WriteLine(header);

        // Scores are "higher is better"; rows are per reported score name.
        var names = results.SelectMany(r => r.Scores.Keys.Select(k => (r.Suite, k))).Distinct().ToList();
        var geo = new double[engines.Length];
        var geoCount = new int[engines.Length];
        foreach (var (suite, name) in names)
        {
            var row = new StringBuilder($"{name,-36}");
            var vals = new double?[engines.Length];
            for (int i = 0; i < engines.Length; i++)
            {
                var ms = results.Where(r => r.Suite == suite && r.Engine == engines[i] && r.Scores.ContainsKey(name)).ToList();
                if (ms.Count == 0) { row.Append($"{"-",14}"); continue; }
                double v = ms.Average(r => r.Scores[name]);
                vals[i] = v;
                row.Append($"{v.ToString("G5", CultureInfo.InvariantCulture),14}");
            }
            if (engines.Length > 1 && vals[0] is double first && vals[^1] is double last && first > 0)
                row.Append($"   {(last / first).ToString("P1", CultureInfo.InvariantCulture)}");
            Console.WriteLine(row);
            for (int i = 0; i < engines.Length; i++)
                if (vals[i] is double v && v > 0) { geo[i] += Math.Log(v); geoCount[i]++; }
        }
        var total = new StringBuilder($"{"geomean",-36}");
        for (int i = 0; i < engines.Length; i++)
            total.Append(geoCount[i] > 0 ? $"{Math.Exp(geo[i] / geoCount[i]).ToString("G5", CultureInfo.InvariantCulture),14}" : $"{"-",14}");
        Console.WriteLine(total);
        foreach (var r in results.Where(r => r.Error is not null))
            Console.WriteLine($"  {r.Suite} on {r.Engine}: {r.Error}");
    }

    static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}

static class Paths
{
    /// <summary>The dotnet/ directory, found by walking up from the binary.</summary>
    public static readonly string DotnetRoot = FindDotnetRoot();
    public static string RepoRoot => Path.GetDirectoryName(DotnetRoot)!;
    public static string JsPerfTest => Path.Combine(RepoRoot, "test", "js-perf-test");
    public static string Octane => Path.Combine(DotnetRoot, "artifacts", "octane");

    static string FindDotnetRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "V8Sharp.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("cannot find dotnet/V8Sharp.slnx above " + AppContext.BaseDirectory);
    }
}
