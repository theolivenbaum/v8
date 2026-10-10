// The d8 command line: Shell::SetOptions in src/d8/d8.cc separates d8's own
// options from V8 flags and from the files to run.
namespace V8Sharp.TestRunner.Shell;

/// <summary>One script to run: a file (classic or module) or <c>-e</c> source.</summary>
public sealed record ShellSource(string? Path, bool IsModule = false, string? EvalSource = null, bool IsJson = false);

public sealed class D8Options
{
    /// <summary>V8 flags, in order, for <see cref="Engines.IJsEngine.SetFlags"/>.</summary>
    public List<string> V8Flags { get; } = [];

    public List<ShellSource> Sources { get; } = [];

    /// <summary>--throws: the run succeeds when it throws.</summary>
    public bool ExpectedToThrow { get; set; }
    public bool IgnoreUnhandledPromises { get; set; }
    public bool NoArguments { get; set; }
    public bool OmitQuit { get; set; }
    public bool QuietLoad { get; set; }
    public bool NoFail { get; set; }
    public bool NoCanBlock { get; set; }
    public bool InvokeWeakCallbacks { get; set; }
    /// <summary>d8's --bundle: a script file may be a bundle of scripts and modules (TryExecuteBundle).</summary>
    public bool Bundle { get; set; }
    /// <summary>d8's --compile-only: scripts and modules are compiled, not run.</summary>
    public bool CompileOnly { get; set; }
    /// <summary>--enable-tracing.</summary>
    public bool EnableTracing { get; set; }
    /// <summary>--trace-config=FILE: read and parsed with --enable-tracing (tracing itself is not implemented).</summary>
    public string? TraceConfig { get; set; }

    /// <summary>Options d8 understands that the host ignores (reported for diagnostics).</summary>
    public List<string> Ignored { get; } = [];

    // d8 options that take no value and only matter to the real d8 binary.
    static readonly HashSet<string> s_ignoredD8Options = new(StringComparer.Ordinal)
    {
        "--test", "--notest", "--no-test", "--send-idle-notification", "--no-wait-for-background-tasks",
        "--dump-counters", "--dump-counters-nvp", "--dump-system-memory-stats", "--streaming-compile",
        "--no-streaming-compile", "--nostreaming-compile", "--enable-inspector",
        "--disable-in-process-stack-traces", "--enable-os-system", "--no-apply-priority", "--stress-delay-tasks",
        "--cpu-profiler", "--cpu-profiler-print", "--stress-deserialize",
        "--no-fuzzy-module-file-extensions", "--enable-etw-stack-walking", "--enable-system-instrumentation",
        "--expose-fast-api", "--flush-denormals", "--isolate", "--simulate-errors", "--shell",
        "--disallow-unsafe-flags", "--run-as-security-poc", "--run-as-sandbox-security-poc", "--sandbox-fuzzing",
        "--wasm-trap-handler", "--no-wasm-trap-handler",
    };

    // d8 options of the form --name=value.
    static readonly string[] s_ignoredD8OptionsWithValue =
    [
        "--icu-data-file=", "--icu-locale=", "--snapshot_blob=", "--cache=", "--trace-path=",
        "--lcov=", "--thread-pool-size=", "--repeat-compile=", "--max-serializer-memory=", "--perf-ctl-fd=",
        "--perf-ack-fd=", "--read-from-tcp-port=", "--scope-linux-perf-to-mark-measure=",
    ];

    /// <summary>Parses d8's argv. Files are paths as given (relative to the
    /// working directory); <c>--module f</c> and <c>*.mjs</c> are modules.</summary>
    public static D8Options Parse(IReadOnlyList<string> args)
    {
        var o = new D8Options();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--")
            {
                break;
            }
            if (a == "-e" && i + 1 < args.Count)
            {
                o.Sources.Add(new ShellSource(null, EvalSource: args[++i]));
                continue;
            }
            if (a == "--module" && i + 1 < args.Count)
            {
                o.Sources.Add(new ShellSource(args[++i], IsModule: true));
                continue;
            }
            if (a == "--json" && i + 1 < args.Count)
            {
                // Treat the next file as a JSON file.
                o.Sources.Add(new ShellSource(args[++i], IsJson: true));
                continue;
            }
            if (!a.StartsWith('-'))
            {
                o.Sources.Add(new ShellSource(a, IsModule: a.EndsWith(".mjs", StringComparison.Ordinal)));
                continue;
            }
            string n = a.Replace('_', '-');
            switch (n)
            {
                case "--throws": o.ExpectedToThrow = true; continue;
                case "--ignore-unhandled-promises": o.IgnoreUnhandledPromises = true; continue;
                case "--no-arguments": o.NoArguments = true; continue;
                case "--omit-quit": o.OmitQuit = true; continue;
                case "--quiet-load": o.QuietLoad = true; continue;
                case "--no-fail": o.NoFail = true; continue;
                case "--no-can-block": o.NoCanBlock = true; continue;
                case "--invoke-weak-callbacks": o.InvokeWeakCallbacks = true; continue;
                case "--bundle": o.Bundle = true; continue;
                case "--compile-only": o.CompileOnly = true; continue;
                case "--enable-tracing": o.EnableTracing = true; continue;
            }
            if (n.StartsWith("--trace-config=", StringComparison.Ordinal))
            {
                o.TraceConfig = a["--trace-config=".Length..];
                continue;
            }
            if (s_ignoredD8Options.Contains(n) || Array.Exists(s_ignoredD8OptionsWithValue, p => n.StartsWith(p, StringComparison.Ordinal)))
            {
                o.Ignored.Add(a);
                continue;
            }
            // --fuzzing is both a d8 option and a V8 flag.
            o.V8Flags.Add(a);
        }
        return o;
    }
}
