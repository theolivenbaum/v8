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
    /// <summary>
    /// CPU time of the calling thread in milliseconds (Linux: the first field
    /// of /proc/thread-self/schedstat, in nanoseconds), or NaN elsewhere.
    /// </summary>
    public static double ThreadCpuTimeMs()
    {
        try
        {
            string stat = File.ReadAllText("/proc/thread-self/schedstat");
            int space = stat.IndexOf(' ');
            return long.Parse(space < 0 ? stat : stat[..space], CultureInfo.InvariantCulture) / 1e6;
        }
        catch (IOException)
        {
            return double.NaN;
        }
    }

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

    /// <summary>V8Sharp's modes: its V8 flags for each tier configuration.</summary>
    static readonly Dictionary<string, string> V8SharpModes = new()
    {
        [""] = "",                                      // as configured by default: Ignition + baseline IL
        ["jitless"] = "--jitless",                      // the interpreter only
        ["sparkplug"] = "--sparkplug",                  // Ignition + baseline IL (tiering with V8's budgets)
        ["no-sparkplug"] = "--no-sparkplug",            // the interpreter only, with compiled regexps
        ["sync-sparkplug"] = "--sparkplug --no-concurrent-sparkplug",      // baseline compiled on the main thread
        ["concurrent-sparkplug"] = "--sparkplug --concurrent-sparkplug",   // baseline compiled on a background thread
        ["always-sparkplug"] = "--always-sparkplug",    // baseline IL from the first call
        ["maglev"] = "--maglev",                        // + the optimizing tier (Maglev, IL)
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
            engines: v8:jit, v8:jitless, v8:sparkplug, v8:maglev, v8sharp, v8sharp:jitless, v8sharp:sparkplug,
                     v8sharp:always-sparkplug, v8sharp:maglev (V8SHARP_BENCH_FLAGS adds V8 flags to v8sharp runs);
                     <engine>@<dir> runs it with the V8Sharp.Bench build in <dir> (another revision, a publish);
                     d8sharp[:mode]@<dir> runs the d8sharp shell built or published in <dir> as its own process
            suites:  octane (all), octane:<name>, octane-cpu (all) | octane-cpu:<name> (fixed work, scored
                     by thread CPU time; V8SHARP_BENCH_SCALE divides the iterations, default 50),
                     octane-steady (all) | octane-steady:<name> (octane-cpu after one unmeasured pass),
                     perf:<js-perf-test dir>, micro:<name> | micro:all
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
        using var allocationProfile = Environment.GetEnvironmentVariable("V8SHARP_BENCH_ALLOCPROFILE") == "1"
            ? new AllocationProfile() : null;

        IBenchHost host = CreateHost(engine, workDir);
        var sw = Stopwatch.StartNew();
        foreach (var f in files) host.LoadFile(f);
        if (driver is not null) host.Execute(driver, "driver.js");
        sw.Stop();
        Console.WriteLine($"@wall-ms {sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
        if (suite.StartsWith("octane-cpu:", StringComparison.Ordinal))
        {
            // The whole process: start-up, the JIT's background compilation, GC.
            double cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            Console.WriteLine($"{suite[11..]}.process(Score): {(1e6 / cpu).ToString("G6", CultureInfo.InvariantCulture)}");
            if (engine.StartsWith("v8sharp", StringComparison.Ordinal))
            {
                // The CLR heap's share (V8Sharp only; the oracle allocates in V8's
                // heap): MB allocated by the process and the GCs per generation,
                // start-up included. Printed as scores, so lower is better here.
                string n = suite[11..];
                double mb = GC.GetTotalAllocatedBytes(precise: true) / 1048576.0;
                Console.WriteLine($"{n}.allocMB(Score): {mb.ToString("F1", CultureInfo.InvariantCulture)}");
                for (int g = 0; g <= 2; g++)
                    Console.WriteLine($"{n}.gen{g}(Score): {GC.CollectionCount(g).ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"{n}.gcPauseMs(Score): {GC.GetTotalPauseDuration().TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}");
            }
        }
        host.Dispose();
        allocationProfile?.Print(Console.Out);
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
        if (engine == "v8sharp" || engine.StartsWith("v8sharp:", StringComparison.Ordinal))
        {
            string mode = engine == "v8sharp" ? "" : engine[8..];
            if (!V8SharpModes.TryGetValue(mode, out var flags)) throw new ArgumentException("unknown v8sharp mode " + mode);
            string? extra = Environment.GetEnvironmentVariable("V8SHARP_BENCH_FLAGS");
            if (!string.IsNullOrEmpty(extra)) flags = (flags + " " + extra).Trim();
            return new V8SharpHost(flags, workDir);
        }
        throw new ArgumentException("unknown engine " + engine);
    }

    /// <summary>Returns (working directory, files to load, driver source) for a suite.</summary>
    static (string WorkDir, string[] Files, string? Driver) Workload(string suite)
    {
        bool steady = suite.StartsWith("octane-steady:", StringComparison.Ordinal);
        bool fixedWork = steady || suite.StartsWith("octane-cpu:", StringComparison.Ordinal);
        if (suite.StartsWith("octane:", StringComparison.Ordinal) || fixedWork)
        {
            string name = suite[(suite.IndexOf(':') + 1)..];
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
            // octane-cpu: a fixed amount of work (Octane's deterministic mode with
            // the iteration counts divided by V8SHARP_BENCH_SCALE, default 50, at
            // least minIterations) scored by the CPU time of the thread running
            // it: 1e6 / cpu-ms, higher is better. On a shared, loaded machine the
            // thread's CPU time varies much less than the wall time Octane scores.
            const string fixedDriver = """
                (function () {
                  var scale = __benchScale;
                  BenchmarkSuite.config.doDeterministic = true;
                  for (var s = 0; s < BenchmarkSuite.suites.length; s++) {
                    var bs = BenchmarkSuite.suites[s].benchmarks;
                    for (var b = 0; b < bs.length; b++) {
                      bs[b].deterministicIterations =
                          Math.max(bs[b].minIterations, Math.ceil(bs[b].deterministicIterations / scale));
                    }
                  }
                  if (__benchSteady) {
                    // octane-steady: one unmeasured pass first, so the measured
                    // one runs warm (tier-1 code, filled caches and feedback).
                    BenchmarkSuite.RunSuites({
                      NotifyResult: function (name, result) { },
                      NotifyError: function (name, error) { print(name + '(Error): ' + error); },
                      NotifyScore: function (score) { }
                    });
                  }
                  var last = cpuTimeMs();
                  BenchmarkSuite.RunSuites({
                    NotifyResult: function (name, result) {
                      var now = cpuTimeMs();
                      print(name + '(Score): ' + (1e6 / (now - last)));
                      last = now;
                    },
                    NotifyError: function (name, error) { print(name + '(Error): ' + error); },
                    NotifyScore: function (score) { }
                  });
                })();
                """;
            string scale = Environment.GetEnvironmentVariable("V8SHARP_BENCH_SCALE") ?? "50";
            return (dir, files.Select(f => Path.Combine(dir, f)).ToArray(),
                fixedWork
                    ? "var __benchScale = " + int.Parse(scale, CultureInfo.InvariantCulture) + ", __benchSteady = " +
                      (steady ? "true" : "false") + ";\n" + fixedDriver
                    : driver);
        }
        if (suite.StartsWith("micro:", StringComparison.Ordinal))
        {
            // tools/V8Sharp.Bench/micro/<name>.js (or "micro:all"): interpreter
            // micro-benchmarks that print "<name>(Score): <ops per ms>".
            string dir = Path.Combine(Paths.DotnetRoot, "tools", "V8Sharp.Bench", "micro");
            string name = suite[6..];
            string[] names = name == "all"
                ? Directory.GetFiles(dir, "*.js").Select(f => Path.GetFileName(f)).Where(f => f != "harness.js").Order(StringComparer.Ordinal).ToArray()
                : [name + ".js"];
            return (dir, [Path.Combine(dir, "harness.js"), .. names.Select(n => Path.Combine(dir, n))], null);
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
            .SelectMany(s => s is "octane" or "octane-cpu" or "octane-steady" ? OctaneBenchmarks.Select(b => s + ":" + b) : [s]).ToList();
        var engines = (Arg(args, "--engines") ?? "v8:jit,v8:jitless,v8sharp").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int runs = int.Parse(Arg(args, "--runs") ?? "1", CultureInfo.InvariantCulture);
        int timeout = int.Parse(Arg(args, "--timeout") ?? "600", CultureInfo.InvariantCulture);

        var results = new List<Measurement>();
        // Runs are the outer loop so the engines (and builds) are interleaved:
        // on a shared machine, load changes then hit every column alike.
        foreach (var suite in suites)
            for (int r = 0; r < runs; r++)
                foreach (var engine in engines)
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
        // "<engine>@<dir>" runs the measurement with the V8Sharp.Bench build in
        // <dir> (another revision, or a ReadyToRun publish), so old and new
        // builds can be interleaved in one comparison.
        string childEngine = engine;
        string? buildDir = null;
        int at = engine.IndexOf('@');
        if (at >= 0)
        {
            childEngine = engine[..at];
            buildDir = Path.GetFullPath(engine[(at + 1)..]);
        }
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var wallClock = Stopwatch.StartNew();
        if (childEngine == "d8sharp" || childEngine.StartsWith("d8sharp:", StringComparison.Ordinal))
        {
            // "d8sharp[:mode]@<dir>": the d8sharp shell published in <dir> (e.g.
            // with ReadyToRun), as its own process: the files and the driver on
            // its command line, as run-tests.py runs d8.
            if (buildDir is null) throw new ArgumentException("d8sharp needs @<dir> (a d8sharp build or publish)");
            string mode = childEngine == "d8sharp" ? "" : childEngine[8..];
            if (!V8SharpModes.TryGetValue(mode, out var flags)) throw new ArgumentException("unknown v8sharp mode " + mode);
            if (suite.StartsWith("octane-cpu:", StringComparison.Ordinal) || suite.StartsWith("octane-steady:", StringComparison.Ordinal))
                throw new ArgumentException("octane-cpu and octane-steady need the in-process hosts (cpuTimeMs)");
            string shell = Path.Combine(buildDir, OperatingSystem.IsWindows() ? "d8sharp.exe" : "d8sharp");
            var (workDir, files, driver) = Workload(suite);
            psi.FileName = File.Exists(shell) ? shell : "dotnet";
            if (!File.Exists(shell)) psi.ArgumentList.Add(Path.Combine(buildDir, "d8sharp.dll"));
            psi.WorkingDirectory = workDir;
            foreach (string flag in (flags + " " + Environment.GetEnvironmentVariable("V8SHARP_BENCH_FLAGS")).Split(' ',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(flag);
            }
            foreach (string f in files) psi.ArgumentList.Add(f);
            if (driver is not null)
            {
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(driver);
            }
        }
        else if (buildDir is not null)
        {
            string apphost = Path.Combine(buildDir, OperatingSystem.IsWindows() ? "V8Sharp.Bench.exe" : "V8Sharp.Bench");
            if (File.Exists(apphost))
            {
                psi.FileName = apphost;
            }
            else
            {
                psi.FileName = "dotnet";
                psi.ArgumentList.Add(Path.Combine(buildDir, "V8Sharp.Bench.dll"));
            }
            // A build outside the tree finds dotnet/ (Octane, micro/) through this.
            psi.Environment["V8SHARP_BENCH_ROOT"] = Paths.DotnetRoot;
        }
        // Environment.ProcessPath is the apphost; when run as `dotnet V8Sharp.Bench.dll`
        // it is `dotnet` and the dll has to be passed along.
        else if (Path.GetFileNameWithoutExtension(psi.FileName) == "dotnet")
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        if (!childEngine.StartsWith("d8sharp", StringComparison.Ordinal))
            foreach (var a in new[] { "run", "--engine", childEngine, "--suite", suite }) psi.ArgumentList.Add(a);

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
        // The shell prints no @wall-ms: the process's wall time (start-up included).
        if (wall == 0) wall = wallClock.Elapsed.TotalMilliseconds;
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
        string? root = Environment.GetEnvironmentVariable("V8SHARP_BENCH_ROOT");
        if (!string.IsNullOrEmpty(root)) return Path.GetFullPath(root);
        // The binary's directory, then the working directory (for a build or
        // publish outside the tree).
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "V8Sharp.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("cannot find dotnet/V8Sharp.slnx above " + AppContext.BaseDirectory +
            " or the working directory; set V8SHARP_BENCH_ROOT");
    }
}
