// The equivalent of tools/run-tests.py.
using System.Globalization;
using System.Text.Json;
using V8Sharp.TestRunner.Execution;
using V8Sharp.TestRunner.Suites;

namespace V8Sharp.TestRunner;

public static class Program
{
    const string Usage = """
        usage: V8Sharp.TestRunner <suite|suite/path>... [options]
               V8Sharp.TestRunner shell [--engine E] [d8 flags and files...]

        suites: mjsunit test262 message webkit mozilla (a path such as mjsunit/es6 filters)

          --engine oracle|v8sharp    engine to test (default v8sharp)
          --filter GLOB              only tests whose suite/name or name matches (repeatable;
                                     ** spans directories; no wildcard = path prefix)
          --jobs N                   parallel worker slots (default min(cores-1, 3))
          --timeout S                per-test timeout in seconds (default 60; x4 for SLOW)
          --update-expectations      rewrite expectations/<suite>.<engine>.txt from this run
          --test262-root DIR         test262 checkout (default test/test262/data, then
                                     /home/user/theolivenbaum/test262)
          --json FILE                results file (default dotnet/artifacts/testrunner/<engine>-<suites>.json)
          --run-skipped              also run tests the status files mark SKIP
          --show-failures N          list at most N new failures (default 20)
          --rerun-failures N         run unexpected results again N times (default 1; flaky = passes on rerun)
          --list                     list the selected tests and their d8 command lines

        shell runs one d8 command line in-process, like d8 itself (run from the V8 root).
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--worker") return WorkerMain(args);
            if (args.Length > 0 && args[0] == "shell") return ShellMain(args[1..]);
            return RunnerMain(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    static int WorkerMain(string[] args)
    {
        string engine = "oracle", cwd = Environment.CurrentDirectory;
        string[] flags = [];
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine": engine = args[++i]; break;
                case "--cwd": cwd = args[++i]; break;
                case "--worker-flags": flags = JsonSerializer.Deserialize(args[++i], WorkerJson.Default.StringArray) ?? []; break;
            }
        }
        return Worker.Serve(engine, flags, cwd);
    }

    static int ShellMain(string[] args)
    {
        string engineName = "oracle";
        var rest = new List<string>();
        double timeout = 60;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--engine" && i + 1 < args.Length) engineName = args[++i];
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeout = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else rest.Add(args[i]);
        }
        var engine = EngineFactory.Create(engineName);
        engine.SetFlags(Worker.ProcessFlags(Shell.D8Options.Parse(rest)));
        var r = Worker.RunOne(engine, [.. rest], Environment.CurrentDirectory, TimeSpan.FromSeconds(timeout));
        Console.Out.Write(r.Stdout);
        Console.Error.Write(r.Stderr);
        if (r.TimedOut) Console.Error.WriteLine("(timed out)");
        return r.ExitCode;
    }

    static int RunnerMain(string[] args)
    {
        var o = new RunnerOptions { V8Root = Runner.FindV8Root(Environment.CurrentDirectory) };
        o.ExpectationsDirectory = Path.Combine(o.V8Root, "dotnet", "tools", "V8Sharp.TestRunner", "expectations");
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--engine": o.Engine = Next(); break;
                case "--filter": o.Filters.Add(Next()); break;
                case "--jobs" or "-j": o.Jobs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--timeout": o.TimeoutSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--update-expectations": o.UpdateExpectations = true; break;
                case "--test262-root": o.Test262Root = Next(); break;
                case "--json": o.JsonPath = Path.GetFullPath(Next()); break;
                case "--run-skipped": o.RunSkipped = true; break;
                case "--show-failures": o.ShowFailures = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--rerun-failures": o.RerunFailures = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--list": o.ListOnly = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"unknown option {a}");
                    // "mjsunit/es6/foo" selects a suite and filters by path, as run-tests.py does.
                    int slash = a.IndexOf('/');
                    string suite = slash < 0 ? a : a[..slash];
                    if (Array.IndexOf(TestSuite.AllSuites, suite) < 0) throw new ArgumentException($"unknown suite '{suite}'");
                    if (!o.Suites.Contains(suite)) o.Suites.Add(suite);
                    if (slash >= 0) o.Filters.Add(a);
                    break;
            }
        }
        if (o.Engine is not ("oracle" or "v8sharp")) throw new ArgumentException($"unknown engine '{o.Engine}'");
        if (o.Suites.Count == 0) throw new ArgumentException("no suite given");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return new Runner(o).RunAsync(cts.Token).GetAwaiter().GetResult();
    }
}
