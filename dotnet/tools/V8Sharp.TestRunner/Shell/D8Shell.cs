// The d8 shell's run loop over an IJsEngine: Shell::Main / RunMain /
// RunMainIsolate / SourceGroup::Execute / FinishExecuting /
// ReportException / the Realm and setTimeout machinery of src/d8/d8.cc.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using V8Sharp.TestRunner.Engines;

namespace V8Sharp.TestRunner.Shell;

/// <summary>What one d8 run produced.</summary>
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Duration);

/// <summary>
/// Runs a d8 command line on an engine, like one d8 process: the files in
/// order in the main realm until one throws, then the task queue, then the
/// unhandled-rejection check. Output goes to in-memory stdout/stderr.
/// </summary>
public sealed partial class D8Shell : IJsHost
{
    static readonly string s_shimSource = LoadShim();

    readonly IJsEngine _engine;
    readonly D8Options _options;
    readonly string _workingDirectory;
    // Shared by the main shell and its Worker shells (one d8 process); writers lock _stdout.
    readonly StringBuilder _stdout;
    readonly StringBuilder _stderr;
    readonly Stopwatch _clock;

    IJsIsolate? _isolate;
    readonly List<RealmState?> _realms = [];
    readonly List<int> _realmStack = [];
    int _realmCurrent;
    int _realmSwitch;
    object? _realmSharedBox;
    // The isolate's foreground task runner: setTimeout callbacks and Worker
    // message tasks, posted from any thread, run in order on the shell's thread.
    readonly ConcurrentQueue<Action> _tasks = new();
    readonly SemaphoreSlim _taskSignal = new(0);
    readonly List<(object Promise, object? Value, RealmState Realm, JsExceptionInfo? Message)> _unhandled = [];
    readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _performanceMarks = new(StringComparer.Ordinal);

    volatile bool _timedOut;
    int? _quitCode;
    bool _terminateRequested;

    sealed class RealmState(IJsRealm realm, object helpers)
    {
        public IJsRealm Realm { get; } = realm;
        public object Helpers { get; } = helpers;
    }

    public D8Shell(IJsEngine engine, D8Options options, string workingDirectory)
    {
        _engine = engine;
        _options = options;
        _workingDirectory = workingDirectory;
        _stdout = new();
        _stderr = new();
        _clock = new();
        _root = this;
    }

    /// <summary>A Worker's shell: the same process (output, options, clock) with its own isolate.</summary>
    D8Shell(D8Shell parent, WorkerState worker)
    {
        _engine = parent._engine;
        _options = parent._options;
        _workingDirectory = parent._workingDirectory;
        _stdout = parent._stdout;
        _stderr = parent._stderr;
        _clock = parent._clock;
        _root = parent._root;
        _worker = worker;
    }

    /// <summary>PostTask on the foreground task runner (thread-safe).</summary>
    void PostTask(Action task)
    {
        _tasks.Enqueue(task);
        _taskSignal.Release();
    }

    /// <summary>Runs one posted task; false when there was none.</summary>
    bool RunOneTask()
    {
        if (!_tasks.TryDequeue(out Action? task)) return false;
        task();
        return true;
    }

    void Out(string text)
    {
        lock (_stdout) _stdout.Append(text);
    }

    static string LoadShim()
    {
        using var s = typeof(D8Shell).Assembly.GetManifestResourceStream("V8Sharp.TestRunner.Shell.d8-shim.js")
            ?? throw new InvalidOperationException("d8-shim.js resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The watchdog: terminates the running script. Thread-safe.</summary>
    public void Timeout()
    {
        _timedOut = true;
        _isolate?.TerminateExecution();
        TerminateAllWorkers();
    }

    public ShellResult Run()
    {
        _clock.Start();
        int exit;
        try
        {
            exit = RunMain();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Out("V8Sharp.TestRunner: host error: " + e + "\n");
            exit = 1;
        }
        finally
        {
            // Shell::WaitForRunningWorkers: workers still running are terminated.
            WaitForRunningWorkers();
            try { _isolate?.Dispose(); } catch { }
        }
        if (_timedOut) exit = -1;
        return new ShellResult(exit, _stdout.ToString(), _stderr.ToString(), _timedOut, _clock.Elapsed);
    }

    int RunMain()
    {
        if (_engine.UnavailableReason is { } why)
        {
            Out(why + "\n");
            return 1;
        }
        _isolate = _engine.CreateIsolate(this);
        if (_options.EnableTracing && _options.TraceConfig is { } traceConfig && !LoadTraceConfig(traceConfig)) return 1;
        _realms.Add(Install(_isolate.MainRealm, isMain: true));
        bool success = ExecuteSources();
        if (QuitCode is { } q) return q;
        if (!_timedOut && !FinishExecuting()) success = false;
        if (QuitCode is { } q2) return q2;

        if (_unhandledCount > 0)
        {
            Out(string.Create(CultureInfo.InvariantCulture, $"{_unhandledCount} pending unhandled Promise rejection(s) detected.\n"));
            success = false;
        }
        if (_options.NoFail) return 0;
        return success == _options.ExpectedToThrow ? 1 : 0;
    }

    int _unhandledCount;

    RealmState Install(IJsRealm realm, bool isMain)
    {
        var installer = Check(realm.RunScript(s_shimSource, "d8-shim.js"), "d8 shim");
        var host = realm.CreateFunction("host", Dispatch);
        var options = Check(realm.RunScript(
            "({ omitQuit: " + (_options.OmitQuit ? "true" : "false") +
            ", noArguments: " + (_options.NoArguments ? "true" : "false") +
            ", isWorker: " + (_worker is not null && isMain ? "true" : "false") +
            ", serialization: " + (realm.SupportsSerialization ? "true" : "false") +
            ", nativeConsole: " + (realm.HasConsoleDelegate ? "true" : "false") +
            ", maxFixedArrayCapacity: 134217725, maxFastArrayLength: 33554432 })", "d8-options.js"), "d8 options");
        // print, printErr and write are C++ functions in d8 (Shell::Print ...): when
        // the engine can convert the arguments itself, no JS frame shows in stack traces.
        object print = realm.CreateStringArgumentsFunction("print", (r, a) => Dispatch(r, ["print", JoinArguments(a)]))
            ?? JsUndefined.Value;
        object printErr = realm.CreateStringArgumentsFunction("printErr", (r, a) => Dispatch(r, ["printErr", JoinArguments(a)]))
            ?? JsUndefined.Value;
        object write = realm.CreateStringArgumentsFunction("write", (r, a) => Dispatch(r, ["write", JoinArguments(a)]))
            ?? JsUndefined.Value;
        var helpers = Check(realm.Call(installer!, JsUndefined.Value, host, isMain, options, print, printErr, write), "d8 install");
        return new RealmState(realm, helpers!);
    }

    static string JoinArguments(object?[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i != 0) sb.Append(' ');
            sb.Append(args[i] as string);
        }
        return sb.ToString();
    }

    static object? Check(Completion c, string what) => c.Kind switch
    {
        CompletionKind.Normal => c.Value,
        // Installing into a realm Realm.create made can throw (a stack overflow
        // when Realm.create runs at the stack limit); it is the exception of
        // the Realm.create call, as in d8.
        CompletionKind.Throw => throw new JsThrowValue(c.Exception?.Exception),
        _ => throw new InvalidOperationException($"{what} failed: {c.Kind} {c.Exception?.Exception}"),
    };

    // --- SourceGroup::Execute ---

    bool ExecuteSources()
    {
        foreach (var src in _options.Sources)
        {
            if (Stopped) return true;
            Completion c;
            if (src.EvalSource is { } code)
            {
                c = RunInCurrentRealm(code, "unnamed", isModule: false);
            }
            else if (src.IsJson)
            {
                if (!LoadJson(src.Path!)) return false;
                continue;
            }
            else
            {
                string? text = ReadSourceFile(src.Path!);
                if (text is null)
                {
                    if (_options.Bundle)
                    {
                        Out($"Error reading '{src.Path}'\n");
                        _quitCode = 1;
                        return false;
                    }
                    // Shell::ExecuteSource of Source::FromFile: ReadFile throws
                    // "Error loading file: <name>" from C++, reported like any
                    // uncaught exception (no script: "undefined:0").
                    c = CurrentRealm.Realm.ThrowError("Error loading file: " + src.Path);
                    if (c.Kind == CompletionKind.Throw) ReportException(CurrentRealm, c.Exception!);
                    return false;
                }
                string name = src.IsModule ? NormalizePath(src.Path!, _workingDirectory) : src.Path!;
                if (_options.Bundle && !src.IsModule && TryExecuteBundle(text, name, out bool bundleSuccess))
                {
                    if (!bundleSuccess) return false;
                    continue;
                }
                c = RunInCurrentRealm(text, name, src.IsModule);
                if (src.IsModule && c.Kind == CompletionKind.Normal && !_options.CompileOnly)
                {
                    // Shell::ExecuteModule: EmptyMessageQueues, then a rejected
                    // or stalled top-level await is reported.
                    EmptyMessageQueues();
                    if (Stopped) return true;
                    c = CurrentRealm.Realm.FinishModule();
                }
            }
            if (c.Kind == CompletionKind.Terminated) return true;
            if (c.Kind == CompletionKind.Throw)
            {
                ReportException(CurrentRealm, c.Exception!);
                return false;
            }
        }
        return true;
    }

    /// <summary>Shell::EmptyMessageQueues: runs the pending tasks without waiting for new ones.</summary>
    void EmptyMessageQueues()
    {
        while (!Stopped)
        {
            if (_isolate!.PumpMessageLoop()) continue;
            if (RunOneTask()) continue;
            break;
        }
    }

    /// <summary>
    /// Shell::LoadJSON (--json FILE): every line of the file is parsed as a
    /// JSON value in the current realm; a parse error is reported like an
    /// uncaught exception.
    /// </summary>
    bool LoadJson(string fileName)
    {
        if (fileName.StartsWith("data:", StringComparison.Ordinal))
        {
            Out("d8: --json does not support data URLs\n");
            _quitCode = 1;
            return false;
        }
        string? data = ReadSourceFile(fileName);
        if (data is null)
        {
            Out($"Error reading '{fileName}'\n");
            _quitCode = 1;
            return false;
        }
        // std::getline: a trailing newline does not start another line.
        string[] lines = data.Split('\n');
        int count = data.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < count; i++)
        {
            Completion c = CurrentRealm.Realm.JsonParse(lines[i]);
            if (c.Kind == CompletionKind.Terminated) return true;
            if (c.Kind == CompletionKind.Throw)
            {
                ReportException(CurrentRealm, c.Exception!);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// --enable-tracing --trace-config=FILE (Shell::Main): the file is read and
    /// parsed as JSON in a new context (TraceConfigParser::FillTraceConfig);
    /// tracing itself is not implemented, so the categories are not used.
    /// </summary>
    bool LoadTraceConfig(string path)
    {
        string? json = ReadSourceFile(path);
        if (json is null)
        {
            Out($"Failed to read trace config from '{path}'\n");
            return false;
        }
        IJsRealm context = _isolate!.CreateRealm(null);
        Completion c = context.JsonParse(json);
        if (c.Kind == CompletionKind.Throw)
        {
            Out("Failed to parse trace config.\n\n");
            ReportException(Install(context, isMain: false), c.Exception!);
            return false;
        }
        return true;
    }

    /// <summary>bundle_module_files: normalized module name to source, while a bundle runs.</summary>
    readonly Dictionary<string, string> _bundleModuleFiles = new(StringComparer.Ordinal);

    /// <summary>
    /// d8's TryExecuteBundle: registers the modules of a bundle (D8Bundle),
    /// then runs its scripts and module entry points in order. Returns false
    /// when the file is not a bundle.
    /// </summary>
    bool TryExecuteBundle(string content, string fileName, out bool success)
    {
        success = true;
        V8Sharp.D8.D8Bundle? bundle = V8Sharp.D8.D8Bundle.TryParse(content, _workingDirectory);
        if (bundle is null) return false;
        foreach (string warning in bundle.Warnings) Out(warning);

        _bundleModuleFiles.Clear();
        foreach (var (name, source) in bundle.ModuleFiles) _bundleModuleFiles[name] = source;

        // Second pass: Execution
        foreach (var (isScript, contentOrName) in bundle.ExecutionOrder)
        {
            if (Stopped) break;
            Completion c = isScript
                ? RunInCurrentRealm(contentOrName, fileName, isModule: false)
                : RunInCurrentRealm(_bundleModuleFiles[contentOrName], contentOrName, isModule: true);
            if (c.Kind == CompletionKind.Terminated) break;
            if (c.Kind == CompletionKind.Throw)
            {
                ReportException(CurrentRealm, c.Exception!);
                success = false;
            }
        }
        _bundleModuleFiles.Clear();
        return true;  // Bundle handled successfully (even if execution failed)
    }

    bool Stopped => _timedOut || QuitCode is not null || _terminateRequested || _root._timedOut;

    /// <summary>quit() exits the whole d8 process, from any worker too.</summary>
    int? QuitCode => _root._quitCode;

    RealmState CurrentRealm => _realms[_realmCurrent] ?? _realms[0]!;

    /// <summary>ExecuteSource: runs in realm_current_, then applies Realm.switch.</summary>
    Completion RunInCurrentRealm(string source, string name, bool isModule)
    {
        var realm = CurrentRealm;
        _sources[name] = source;
        if (isModule) _modules.Add(name);
        var c = _options.CompileOnly ? realm.Realm.Compile(source, name, isModule)
            : isModule ? realm.Realm.RunModule(source, name) : realm.Realm.RunScript(source, name);
        _realmCurrent = _realmSwitch;
        return c;
    }

    string? ReadSourceFile(string path)
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(_workingDirectory, path);
        if (!File.Exists(full)) return null;
        return ReadText(full);
    }

    /// <summary>d8 reads files as UTF-8.</summary>
    static string ReadText(string fullPath) => File.ReadAllText(fullPath, Encoding.UTF8);

    // --- FinishExecuting: the message loop, then unhandled rejections ---

    bool FinishExecuting()
    {
        bool success = true;
        _taskFailed = false;
        while (!Stopped)
        {
            // Engine-posted foreground tasks run before the next d8 task.
            if (_isolate!.PumpMessageLoop()) continue;
            if (RunOneTask()) continue;
            // CompleteMessageLoop: wait for work while workers we listen to run.
            if (!HasRunningSubscribedWorkers()) break;
            _taskSignal.Wait(10);
        }
        if (_taskFailed) success = false;
        if (Stopped) return success;
        if (_options.InvokeWeakCallbacks) _isolate!.CollectGarbage();
        if (!_options.IgnoreUnhandledPromises && _unhandled.Count > 0)
        {
            var pending = _unhandled.ToArray();
            _unhandled.Clear();
            foreach (var (_, value, realm, message) in pending)
            {
                if (Stopped) break;
                ReportException(realm, message ?? MessageForValue(realm, value));
            }
            _unhandledCount += pending.Length;
            success &= pending.Length == 0;
        }
        return success;
    }

    /// <summary>Shell::PromiseRejectCallback: a non-Error value is reported
    /// as <c>Error: Unhandled Promise.</c>.</summary>
    JsExceptionInfo MessageForValue(RealmState realm, object? value)
    {
        string? stack = CallHelper(realm, "exceptionStack", value) as string;
        if (stack is null)
        {
            var err = realm.Realm.RunScript("new Error('Unhandled Promise.')", "(d8)");
            return new JsExceptionInfo(err.Value);
        }
        return new JsExceptionInfo(value);
    }

    public void WriteStdout(string text) => Out(text);

    public void ReportMessage(IJsRealm realm, JsExceptionInfo exception)
    {
        var state = _realms.Find(r => r is not null && ReferenceEquals(r.Realm, realm)) ?? _realms[0]!;
        ReportException(state, exception);
    }

    public void WriteStderr(string text)
    {
        lock (_stdout) _stderr.Append(text);
    }

    public void OnPromiseRejection(IJsRealm realm, PromiseRejectionKind kind, object promise, object? value, JsExceptionInfo? message = null)
    {
        if (_options.IgnoreUnhandledPromises) return;
        if (kind == PromiseRejectionKind.HandlerAddedAfterReject)
        {
            int i = _unhandled.FindIndex(u => ReferenceEquals(u.Promise, promise) || u.Promise.Equals(promise));
            if (i >= 0) _unhandled.RemoveAt(i);
            return;
        }
        var state = _realms.Find(r => r is not null && ReferenceEquals(r.Realm, realm)) ?? _realms[0]!;
        _unhandled.Add((promise, value, state, message));
    }

    // --- Shell::ReportException ---

    void ReportException(RealmState realm, JsExceptionInfo info)
    {
        if (_timedOut || QuitCode is not null || _root._timedOut) return;
        lock (_stdout) ReportExceptionLocked(realm, info);
    }

    void ReportExceptionLocked(RealmState realm, JsExceptionInfo info)
    {
        string? text = CallHelper(realm, "exceptionToString", info.Exception) as string;
        var loc = info.Line >= 0 ? info : LocationFromStack(realm, info);
        if (loc.Line >= 0 && CallHelper(realm, "callOnError", "Uncaught " + text, loc.ResourceName, (double)loc.Line,
                (double)(loc.StartColumn + 1), info.Exception) is true)
        {
            return;
        }
        if (text is null) return;
        if (loc.Line < 0)
        {
            _stdout.Append(text).Append('\n');
        }
        else
        {
            _stdout.Append(loc.ResourceName).Append(':').Append(loc.Line.ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(text).Append('\n');
            if (loc.SourceLine is { } line)
            {
                _stdout.Append(line).Append('\n');
                int start = Math.Max(0, loc.StartColumn);
                int end = Math.Max(start + 1, loc.EndColumn);
                _stdout.Append(' ', start).Append('^', end - start).Append('\n');
            }
        }
        if (CallHelper(realm, "exceptionStack", info.Exception) is string stack)
        {
            _stdout.Append(stack).Append('\n');
        }
        _stdout.Append('\n');
    }

    /// <summary>When the engine does not give a message location (a rejected
    /// promise's value), v8::Exception::CreateMessage takes it from the error's
    /// stack trace: the first "at ... (name:line:col)" frame.</summary>
    JsExceptionInfo LocationFromStack(RealmState realm, JsExceptionInfo info)
    {
        if (CallHelper(realm, "exceptionStack", info.Exception) is not string stack) return info;
        foreach (var raw in stack.Split('\n'))
        {
            string l = raw.Trim();
            if (!l.StartsWith("at ", StringComparison.Ordinal)) continue;
            string loc = l.EndsWith(')') && l.Contains('(') ? l[(l.LastIndexOf('(') + 1)..^1] : l[3..];
            int c2 = loc.LastIndexOf(':');
            int c1 = c2 > 0 ? loc.LastIndexOf(':', c2 - 1) : -1;
            if (c1 <= 0 || !int.TryParse(loc[(c1 + 1)..c2], out int line) || !int.TryParse(loc[(c2 + 1)..], out int col)) return info;
            string name = loc[..c1];
            string? sourceLine = null;
            if (_sources.TryGetValue(name, out var src))
            {
                var lines = src.Split('\n');
                if (line - 1 < lines.Length) sourceLine = lines[line - 1].TrimEnd('\r');
            }
            return info with { ResourceName = name, Line = line, StartColumn = col - 1, EndColumn = col, SourceLine = sourceLine };
        }
        return info;
    }

    object? CallHelper(RealmState realm, string name, params object?[] args)
    {
        var f = realm.Realm.GetProperty(realm.Helpers, name);
        if (f.Kind != CompletionKind.Normal || f.Value is null or JsUndefined) return null;
        var c = realm.Realm.Call(f.Value, realm.Helpers, args);
        return c.Kind == CompletionKind.Normal ? c.Value : null;
    }

    // --- the host dispatcher behind every d8 global ---

    object? Dispatch(IJsRealm caller, object?[] args)
    {
        string op = args.Length > 0 ? args[0] as string ?? "" : "";
        object? A(int i) => i + 1 < args.Length ? args[i + 1] : JsUndefined.Value;
        switch (op)
        {
            case "print":
                Out(A(0) as string + "\n");
                return JsUndefined.Value;
            case "printErr":
                lock (_stdout) _stderr.Append(A(0) as string).Append('\n');
                return JsUndefined.Value;
            case "write":
                Out(A(0) as string ?? "");
                return JsUndefined.Value;
            case "version":
                return _engine.Version;
            case "read":
            {
                string name = (string)A(0)!;
                string full = Path.IsPathRooted(name) ? name : Path.Combine(_workingDirectory, name);
                if (!File.Exists(full)) throw new JsHostError("Error", "Error loading file: " + name);
                return ReadText(full);
            }
            case "readbuffer":
            {
                string name = (string)A(0)!;
                string full = Path.IsPathRooted(name) ? name : Path.Combine(_workingDirectory, name);
                if (!File.Exists(full))
                {
                    string shown = name.Length > 50 ? name[..50] + "[...]" : name;
                    throw new JsHostError("Error", $"Error reading file \"{shown}\"");
                }
                return Encoding.Latin1.GetString(File.ReadAllBytes(full));
            }
            case "exists":
            {
                string name = (string)A(0)!;
                return File.Exists(Path.IsPathRooted(name) ? name : Path.Combine(_workingDirectory, name));
            }
            case "load":
                return Load(caller, (string)A(0)!);
            case "quit":
                _root.Quit(ToInt(A(0)));
                return JsUndefined.Value;
            case "terminate":
            case "terminateNow":
                _terminateRequested = true;
                _isolate!.TerminateExecution();
                return JsUndefined.Value;
            case "setTimeout":
            {
                var realm = StateOf(caller);
                object callback = A(0)!;
                PostTask(() => RunTimeoutTask(realm, callback));
                return JsUndefined.Value;
            }
            case "performanceNow":
                return _clock.Elapsed.TotalMilliseconds;
            case "performanceMark":
            {
                // PerIsolateData::performance_mark_map_.
                double timestamp = _clock.Elapsed.TotalMilliseconds;
                _performanceMarks[(string)A(0)!] = timestamp;
                return timestamp;
            }
            case "performanceMarkLookup":
                return _performanceMarks.TryGetValue((string)A(0)!, out double markTime) ? markTime : JsUndefined.Value;
            case "realmCurrent":
                return (double)_realmCurrent;
            case "realmOwner":
                return RealmOwner(A(0));
            case "realmGlobal":
                return RealmAt(A(0)).Realm.GlobalObject;
            case "realmCreate":
            {
                bool allowCrossRealm = A(0) is true;
                var realm = _isolate!.CreateRealm(allowCrossRealm ? CurrentRealm.Realm : null, ownMicrotaskQueue: A(1) is true);
                _realms.Add(Install(realm, isMain: false));
                return (double)(_realms.Count - 1);
            }
            case "realmNavigate":
            {
                int index = RestrictedRealmIndex(A(0));
                bool sameOrigin = A(1) is true;
                // d8 reuses the global proxy; the host creates a fresh realm in the slot.
                var old = _realms[index]!;
                old.Realm.DetachGlobal();
                var realm = _isolate!.CreateRealm(sameOrigin ? old.Realm : null);
                _realms[index] = Install(realm, isMain: false);
                return JsUndefined.Value;
            }
            case "realmDetachGlobal":
                _realms[RestrictedRealmIndex(A(0))]!.Realm.DetachGlobal();
                return JsUndefined.Value;
            case "realmDispose":
            {
                int index = RestrictedRealmIndex(A(0));
                _realms[index]!.Realm.DetachGlobal();
                _realms[index] = null;
                return JsUndefined.Value;
            }
            case "realmSwitch":
                _realmSwitch = RealmIndex(A(0));
                return JsUndefined.Value;
            case "realmEval":
            {
                int index = RealmIndex(A(0));
                if (A(1) is not string source) throw new JsHostError("Error", "Invalid argument");
                var target = _realms[index]!;
                _realmStack.Add(_realmCurrent);
                int saved = _realmCurrent;
                _realmCurrent = index;
                Completion c;
                try
                {
                    c = target.Realm.RunScript(source, "(d8)");
                }
                finally
                {
                    _realmCurrent = saved;
                    _realmStack.RemoveAt(_realmStack.Count - 1);
                }
                return c.Kind switch
                {
                    CompletionKind.Normal => c.Value,
                    CompletionKind.Throw => throw new JsThrowValue(c.Exception!.Exception),
                    _ => throw new JsTermination(),
                };
            }
            case "realmSharedBox":
                _realmSharedBox ??= Check(_realms[0]!.Realm.RunScript("({ value: undefined })", "(d8)"), "Realm.shared");
                return _realmSharedBox;
            default:
                if (DispatchWorker(caller, op, args, out object? result)) return result;
                break;
        }
        switch (op)
        {
            case "unsupported":
                throw new JsHostError("Error", $"{A(0)} is not supported by the V8Sharp test host");
            default:
                throw new JsHostError("Error", "unknown d8 host operation " + op);
        }
    }

    object? Load(IJsRealm caller, string name)
    {
        if (Stopped) return JsUndefined.Value;
        string full = Path.IsPathRooted(name) ? name : Path.Combine(_workingDirectory, name);
        if (!File.Exists(full)) throw new JsHostError("Error", "Error loading file: " + name);
        string source = ReadText(full);
        _sources[name] = source;
        var realm = StateOf(caller);
        var c = realm.Realm.RunScript(source, name);
        switch (c.Kind)
        {
            case CompletionKind.Normal:
                return JsUndefined.Value;
            case CompletionKind.Terminated:
                throw new JsTermination();
            default:
                if (!_options.QuietLoad) ReportException(realm, c.Exception!);
                throw new JsHostError("Error", $"Error executing file: \"{name}\"");
        }
    }

    RealmState StateOf(IJsRealm realm) =>
        _realms.Find(r => r is not null && ReferenceEquals(r.Realm, realm)) ?? CurrentRealm;

    object? RealmOwner(object? o)
    {
        if (o is null or JsUndefined or string or double or bool) throw new JsHostError("Error", "Invalid argument");
        for (int i = 0; i < _realms.Count; i++)
        {
            if (_realms[i] is { } r && CallHelper(r, "isOwnedHere", o) is true) return (double)i;
        }
        return JsUndefined.Value;
    }

    RealmState RealmAt(object? arg) => _realms[RealmIndex(arg)]!;

    int RealmIndex(object? arg)
    {
        if (arg is not double d) throw new JsHostError("Error", "Invalid argument");
        int index = double.IsFinite(d) ? (int)d : -1;
        if (index < 0 || index >= _realms.Count || _realms[index] is null) throw new JsHostError("Error", "Invalid realm index");
        return index;
    }

    int RestrictedRealmIndex(object? arg)
    {
        int index = RealmIndex(arg);
        if (index == 0 || index == _realmCurrent || index == _realmSwitch || _realmStack.Contains(index))
        {
            throw new JsHostError("Error", "Invalid realm index");
        }
        return index;
    }

    static int ToInt(object? v) => v switch
    {
        double d when double.IsFinite(d) => (int)d,
        _ => 0,
    };

    // --- modules: d8's path-based resolution (NormalizePath / DirName) ---

    const string DataUrlPrefix = "data:text/javascript,";

    /// <summary>NormalizeModuleSpecifier + Shell::FetchModuleSource: data URLs
    /// carry their source; anything else must resolve (against the referrer's
    /// directory, or the working directory for a relative script name) to a
    /// local absolute path.</summary>
    public ModuleSource LoadModule(string specifier, string referrer, string? type)
    {
        string resolved;
        if (specifier.StartsWith(DataUrlPrefix, StringComparison.Ordinal) ||
            specifier.StartsWith("http://", StringComparison.Ordinal) || specifier.StartsWith("https://", StringComparison.Ordinal))
        {
            resolved = specifier;
        }
        else
        {
            // DirName(NormalizeModuleSpecifier(referrer, cwd)): a relative script
            // name is taken from the working directory; a data: or http(s): referrer
            // resolves against the working directory.
            string dir = _workingDirectory;
            if (referrer.Length > 0 && !referrer.StartsWith(DataUrlPrefix, StringComparison.Ordinal) &&
                !referrer.StartsWith("http://", StringComparison.Ordinal) && !referrer.StartsWith("https://", StringComparison.Ordinal))
            {
                string abs = NormalizePath(referrer, _workingDirectory);
                dir = abs[..abs.LastIndexOf('/')];
            }
            resolved = NormalizePath(specifier, dir);
        }
        string importedBy = referrer.Length > 0 && _modules.Contains(referrer) ? "\n    imported by " + referrer : "";
        if (_options.Bundle && _bundleModuleFiles.TryGetValue(resolved, out string? bundled))
        {
            _modules.Add(resolved);
            _sources[resolved] = bundled;
            return new ModuleSource(resolved, bundled, IsJson: type == "json");
        }
        if (resolved.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
        {
            _modules.Add(resolved);
            return new ModuleSource(resolved, resolved[DataUrlPrefix.Length..], IsJson: type == "json");
        }
        if (!resolved.StartsWith('/'))
        {
            throw new JsHostError("Error", $"d8: Reading module from {resolved} is not supported.{importedBy}");
        }
        if (!File.Exists(resolved)) throw new JsHostError("Error", $"d8: Error reading module from {resolved}{importedBy}");
        if (type == "bytes")
        {
            // d8 reads a bytes module's file as raw data.
            _modules.Add(resolved);
            return new ModuleSource(resolved, "", Bytes: File.ReadAllBytes(resolved));
        }
        string source = ReadText(resolved);
        _sources[resolved] = source;
        _modules.Add(resolved);
        return new ModuleSource(resolved, source, IsJson: type == "json");
    }

    readonly HashSet<string> _modules = new(StringComparer.Ordinal);

    static string NormalizePath(string path, string dir)
    {
        string p = path.Replace('\\', '/');
        string abs = p.StartsWith('/') ? p : dir.TrimEnd('/') + "/" + p;
        var parts = new List<string>();
        foreach (var seg in abs.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == ".." && parts.Count > 0) { parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }
}
