// The d8 shell's run loop over an IJsEngine: Shell::Main / RunMain /
// RunMainIsolate / SourceGroup::Execute / FinishExecuting /
// ReportException / the Realm and setTimeout machinery of src/d8/d8.cc.
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
public sealed class D8Shell : IJsHost
{
    static readonly string s_shimSource = LoadShim();

    readonly IJsEngine _engine;
    readonly D8Options _options;
    readonly string _workingDirectory;
    readonly StringBuilder _stdout = new();
    readonly StringBuilder _stderr = new();
    readonly Stopwatch _clock = new();

    IJsIsolate? _isolate;
    readonly List<RealmState?> _realms = [];
    readonly List<int> _realmStack = [];
    int _realmCurrent;
    int _realmSwitch;
    object? _realmSharedBox;
    readonly Queue<(RealmState Realm, object Callback)> _tasks = new();
    readonly List<(object Promise, object? Value, RealmState Realm)> _unhandled = [];
    readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

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
            _stdout.Append("V8Sharp.TestRunner: host error: ").Append(e).Append('\n');
            exit = 1;
        }
        finally
        {
            try { _isolate?.Dispose(); } catch { }
        }
        if (_timedOut) exit = -1;
        return new ShellResult(exit, _stdout.ToString(), _stderr.ToString(), _timedOut, _clock.Elapsed);
    }

    int RunMain()
    {
        if (_engine.UnavailableReason is { } why)
        {
            _stdout.Append(why).Append('\n');
            return 1;
        }
        _isolate = _engine.CreateIsolate(this);
        _realms.Add(Install(_isolate.MainRealm, isMain: true));
        bool success = ExecuteSources();
        if (_quitCode is { } q) return q;
        if (!_timedOut && !FinishExecuting()) success = false;
        if (_quitCode is { } q2) return q2;

        if (_unhandledCount > 0)
        {
            _stdout.Append(CultureInfo.InvariantCulture, $"{_unhandledCount} pending unhandled Promise rejection(s) detected.\n");
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
            ", maxFixedArrayCapacity: 134217725, maxFastArrayLength: 33554432 })", "d8-options.js"), "d8 options");
        var helpers = Check(realm.Call(installer!, JsUndefined.Value, host, isMain, options), "d8 install");
        return new RealmState(realm, helpers!);
    }

    static object? Check(Completion c, string what) => c.Kind == CompletionKind.Normal
        ? c.Value
        : throw new InvalidOperationException($"{what} failed: {c.Kind} {c.Exception?.Exception}");

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
            else
            {
                string? text = ReadSourceFile(src.Path!);
                if (text is null)
                {
                    _stdout.Append(CultureInfo.InvariantCulture, $"Error reading '{src.Path}'\n");
                    _quitCode = 1;
                    return false;
                }
                string name = src.IsModule ? NormalizePath(src.Path!, _workingDirectory) : src.Path!;
                c = RunInCurrentRealm(text, name, src.IsModule);
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

    bool Stopped => _timedOut || _quitCode is not null || _terminateRequested;

    RealmState CurrentRealm => _realms[_realmCurrent] ?? _realms[0]!;

    /// <summary>ExecuteSource: runs in realm_current_, then applies Realm.switch.</summary>
    Completion RunInCurrentRealm(string source, string name, bool isModule)
    {
        var realm = CurrentRealm;
        _sources[name] = source;
        if (isModule) _modules.Add(name);
        var c = isModule ? realm.Realm.RunModule(source, name) : realm.Realm.RunScript(source, name);
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
        while (_tasks.Count > 0 && !Stopped)
        {
            var (realm, callback) = _tasks.Dequeue();
            var c = realm.Realm.Call(callback, JsUndefined.Value);
            if (c.Kind == CompletionKind.Throw)
            {
                ReportException(realm, c.Exception!);
                success = false;
            }
        }
        if (Stopped) return success;
        if (_options.InvokeWeakCallbacks) _isolate!.CollectGarbage();
        if (!_options.IgnoreUnhandledPromises && _unhandled.Count > 0)
        {
            var pending = _unhandled.ToArray();
            _unhandled.Clear();
            foreach (var (_, value, realm) in pending)
            {
                if (Stopped) break;
                ReportException(realm, MessageForValue(realm, value));
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

    public void OnPromiseRejection(IJsRealm realm, PromiseRejectionKind kind, object promise, object? value)
    {
        if (_options.IgnoreUnhandledPromises) return;
        if (kind == PromiseRejectionKind.HandlerAddedAfterReject)
        {
            int i = _unhandled.FindIndex(u => ReferenceEquals(u.Promise, promise) || u.Promise.Equals(promise));
            if (i >= 0) _unhandled.RemoveAt(i);
            return;
        }
        var state = _realms.Find(r => r is not null && ReferenceEquals(r.Realm, realm)) ?? _realms[0]!;
        _unhandled.Add((promise, value, state));
    }

    // --- Shell::ReportException ---

    void ReportException(RealmState realm, JsExceptionInfo info)
    {
        if (_timedOut || _quitCode is not null) return;
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
                _stdout.Append(A(0) as string).Append('\n');
                return JsUndefined.Value;
            case "printErr":
                _stderr.Append(A(0) as string).Append('\n');
                return JsUndefined.Value;
            case "write":
                _stdout.Append(A(0) as string);
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
                _quitCode ??= ToInt(A(0));
                _isolate!.TerminateExecution();
                return JsUndefined.Value;
            case "terminate":
            case "terminateNow":
                _terminateRequested = true;
                _isolate!.TerminateExecution();
                return JsUndefined.Value;
            case "setTimeout":
                _tasks.Enqueue((StateOf(caller), A(0)!));
                return JsUndefined.Value;
            case "performanceNow":
                return _clock.Elapsed.TotalMilliseconds;
            case "realmCurrent":
                return (double)_realmCurrent;
            case "realmOwner":
                return RealmOwner(A(0));
            case "realmGlobal":
                return RealmAt(A(0)).Realm.GlobalObject;
            case "realmCreate":
            {
                bool allowCrossRealm = A(0) is true;
                var realm = _isolate!.CreateRealm(allowCrossRealm ? CurrentRealm.Realm : null);
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
