// IJsEngine over the oracle: real V8 14.7 through ClearScript. One V8Runtime
// per isolate, one V8ScriptEngine per realm (all engines of a runtime share
// the isolate, and ClearScript passes their objects to each other natively).
using System.Globalization;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using V8Sharp.Oracle;

namespace V8Sharp.TestRunner.Engines;

public sealed class OracleEngine : IJsEngine
{
    public string Name => "oracle";
    public string Version => ReferenceV8.Version;
    public string? UnavailableReason => null;

    /// <summary>Flags V8 did not recognise (V8 14.7 lacks some of this tree's 15.6 flags).</summary>
    public List<string> UnknownFlags { get; } = [];

    bool _allowAtomicsWait = true;

    public void SetFlags(IReadOnlyList<string> flags)
    {
        foreach (var f in flags)
        {
            // d8's --no-can-block is Isolate::SetAllowAtomicsWait(false).
            if (f is "--no-can-block") { _allowAtomicsWait = false; continue; }
            // One flag per call: V8 stops at the first unrecognised flag of a
            // string, and d8 ignores unrecognised flags.
            ReferenceV8.SetFlags(f);
        }
    }

    internal bool AllowAtomicsWait => _allowAtomicsWait;

    public IJsIsolate CreateIsolate(IJsHost host) => new OracleIsolate(this, host);
}

sealed class OracleIsolate : IJsIsolate
{
    readonly V8Runtime _runtime;
    readonly List<OracleRealm> _realms = [];
    internal IJsHost Host { get; }
    internal OracleEngine Engine { get; }
    internal volatile bool Terminating;
    IntPtr _nativeIsolate;

    public IJsRealm MainRealm { get; }

    public OracleIsolate(OracleEngine engine, IJsHost host)
    {
        Engine = engine;
        Host = host;
        _runtime = new V8Runtime(V8RuntimeFlags.EnableDynamicModuleImports);
        var main = new OracleRealm(this, _runtime.CreateScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports | V8ScriptEngineFlags.DisableGlobalMembers));
        _realms.Add(main);
        MainRealm = main;
        _nativeIsolate = main.NativeIsolate();
        if (!engine.AllowAtomicsWait) main.NativeOp("disallowAtomicsWait");
    }

    public IJsRealm CreateRealm(IJsRealm? shareSecurityTokenWith)
    {
        var realm = new OracleRealm(this, _runtime.CreateScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports | V8ScriptEngineFlags.DisableGlobalMembers));
        _realms.Add(realm);
        if (shareSecurityTokenWith is OracleRealm from)
        {
            // Nested so that the token's Local stays alive: inside a callback
            // running in `from`, read its token, then call into the new realm
            // and set it there.
            from.PendingTokenTarget = realm;
            from.NativeOp("shareToken");
        }
        return realm;
    }

    public void TerminateExecution()
    {
        Terminating = true;
        if (_nativeIsolate != IntPtr.Zero) V8Api.TerminateExecution(_nativeIsolate);
        foreach (var r in _realms.ToArray())
        {
            try { r.Engine.Interrupt(); } catch { }
        }
    }

    public void CancelTerminateExecution()
    {
        Terminating = false;
        foreach (var r in _realms.ToArray())
        {
            try { r.Engine.CancelInterrupt(); } catch { }
        }
    }

    public void CollectGarbage() => _runtime.CollectGarbage(true);

    public void Dispose()
    {
        foreach (var r in _realms) r.Dispose();
        _runtime.Dispose();
    }
}

sealed class OracleRealm : IJsRealm
{
    const string ThrowSentinel = "\u0000v8sharp-host-throw\u0000";

    internal V8ScriptEngine Engine { get; }
    readonly OracleIsolate _isolate;
    readonly ScriptObject _makeFunction;
    readonly ScriptObject _nativeCall;
    readonly List<JsHostFunction> _functions = [];
    PendingThrow? _pendingThrow;
    internal OracleRealm? PendingTokenTarget;
    static IntPtr s_pendingToken;

    public IJsIsolate Isolate => _isolate;

    /// <summary>What a host function asked the wrapper to throw.</summary>
    public sealed class PendingThrow
    {
        public bool IsValue { get; init; }
        public string Type { get; init; } = "Error";
        public string Message { get; init; } = "";
        public object? Value { get; init; }
    }

    public OracleRealm(OracleIsolate isolate, V8ScriptEngine engine)
    {
        _isolate = isolate;
        Engine = engine;
        engine.DocumentSettings.Loader = new ModuleLoader(this);
        engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
        engine.PromiseRejectionCallback = OnPromiseRejection;
        // The plumbing lives in a closure; nothing is left on the global.
        var factory = (ScriptObject)engine.Evaluate(new DocumentInfo("v8sharp-host.js") { Flags = DocumentFlags.None }, """
            (function (dispatch, take, native, sentinel) {
              'use strict';
              // ClearScript's own helper object is a non-configurable global;
              // freeze it so tests that clobber every global cannot break the
              // host. (Shallow: a deep freeze leaves prototype/constructor
              // cycles that such tests then recurse through forever.)
              Object.freeze(EngineInternal);
              const errors = { Error, TypeError, RangeError, SyntaxError, ReferenceError, EvalError, URIError };
              function rethrow() {
                if (take('isValue')) { const v = take('value'); take('clear'); throw v; }
                const type = take('type'), message = take('message');
                take('clear');
                throw new (errors[type] || Error)(message);
              }
              return [
                function make(id, name) {
                  return { [name](...args) {
                    const r = dispatch(id, args);
                    if (r === sentinel) rethrow();
                    return r;
                  } }[name];
                },
                function nativeCall(op) { return native(op); },
              ];
            })
            """);
        var pair = (ScriptObject)factory.InvokeAsFunction(
            new Func<object, object, object?>(Dispatch),
            new Func<object, object?>(TakeThrow),
            new Func<object, object?>(Native),
            ThrowSentinel);
        _makeFunction = (ScriptObject)pair.GetProperty(0);
        _nativeCall = (ScriptObject)pair.GetProperty(1);
    }

    public object GlobalObject => (ScriptObject)Engine.Script;

    internal IntPtr NativeIsolate()
    {
        NativeOp("isolate");
        return _isolateHandle;
    }

    IntPtr _isolateHandle;

    internal void NativeOp(string op) => _nativeCall.InvokeAsFunction(op);

    object? Native(object op)
    {
        switch (op as string)
        {
            case "isolate":
                _isolateHandle = V8Api.GetCurrentIsolate();
                return Undefined.Value;
            case "disallowAtomicsWait":
                V8Api.SetAllowAtomicsWait(V8Api.GetCurrentIsolate(), false);
                return Undefined.Value;
            case "shareToken":
            {
                var target = PendingTokenTarget!;
                PendingTokenTarget = null;
                s_pendingToken = V8Api.GetSecurityToken(V8Api.GetCurrentContext());
                target.NativeOp("adoptToken");
                s_pendingToken = IntPtr.Zero;
                return Undefined.Value;
            }
            case "adoptToken":
                V8Api.SetSecurityToken(V8Api.GetCurrentContext(), s_pendingToken);
                return Undefined.Value;
            case "detachGlobal":
                V8Api.DetachGlobal(V8Api.GetCurrentContext());
                return Undefined.Value;
        }
        return Undefined.Value;
    }

    /// <summary>Host objects of non-public types are opaque to script, so the
    /// wrapper reads the pending throw one field at a time.</summary>
    object? TakeThrow(object field)
    {
        var t = _pendingThrow;
        switch (field as string)
        {
            case "isValue": return t?.IsValue ?? false;
            case "value": return t?.Value;
            case "type": return t?.Type ?? "Error";
            case "message": return t?.Message ?? "";
            default: _pendingThrow = null; return Undefined.Value;
        }
    }

    object? Dispatch(object id, object args)
    {
        var fn = _functions[Convert.ToInt32(id, CultureInfo.InvariantCulture)];
        var jsArgs = (ScriptObject)args;
        int n = Convert.ToInt32(jsArgs.GetProperty("length"), CultureInfo.InvariantCulture);
        var list = new object?[n];
        for (int i = 0; i < n; i++) list[i] = ToHost(jsArgs.GetProperty(i));
        try
        {
            return ToScript(fn(this, list));
        }
        catch (JsHostError e)
        {
            _pendingThrow = new PendingThrow { Type = e.ErrorType, Message = e.Message };
            return ThrowSentinel;
        }
        catch (JsThrowValue e)
        {
            _pendingThrow = new PendingThrow { IsValue = true, Value = ToScript(e.Value) };
            return ThrowSentinel;
        }
        catch (JsTermination)
        {
            // ClearScript cancels termination when a nested call unwinds; ask again.
            _isolate.TerminateExecution();
            return Undefined.Value;
        }
    }

    /// <summary>ClearScript values to the IJsEngine convention.</summary>
    internal static object? ToHost(object? v) => v switch
    {
        null => null,
        Undefined => JsUndefined.Value,
        string or bool => v,
        int i => (double)i,
        double d => d,
        float f => (double)f,
        long l => (double)l,
        uint u => (double)u,
        short or ushort or byte or sbyte or decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => v, // ScriptObject, BigInteger: opaque
    };

    internal static object? ToScript(object? v) => v switch
    {
        JsUndefined => Undefined.Value,
        _ => v,
    };

    public object CreateFunction(string name, JsHostFunction function)
    {
        _functions.Add(function);
        return _makeFunction.InvokeAsFunction(_functions.Count - 1, name);
    }

    public Completion RunScript(string source, string name) =>
        Execute(() => Engine.Evaluate(DocumentFor(name, ModuleCategoryOrScript(false)), source), isModule: false);

    public Completion RunModule(string source, string name)
    {
        var c = Execute(() => Engine.Evaluate(DocumentFor(name, ModuleCategoryOrScript(true)), source), isModule: true);
        if (c.Kind != CompletionKind.Normal || c.Value is not ScriptObject promise) return c;
        // A module's evaluation returns a promise (top-level await); d8 reports
        // its rejection as an uncaught exception.
        var probe = (ScriptObject)Engine.Evaluate(new DocumentInfo("v8sharp-module-probe.js") { Flags = DocumentFlags.None },
            "(function (p) { const r = { state: 'pending', value: undefined }; " +
            "if (p && typeof p.then === 'function') p.then(v => { r.state = 'fulfilled'; }, e => { r.state = 'rejected'; r.value = e; }); " +
            "else r.state = 'fulfilled'; return r; })");
        var record = (ScriptObject)probe.InvokeAsFunction(promise);
        // Invoking a function is a microtask checkpoint: the reactions have run.
        Engine.Execute(new DocumentInfo("v8sharp-drain.js") { Flags = DocumentFlags.None }, "void 0");
        if (record.GetProperty("state") as string == "rejected")
        {
            var e = record.GetProperty("value");
            return new Completion(CompletionKind.Throw, Exception: new JsExceptionInfo(ToHost(e)));
        }
        return Completion.Of(JsUndefined.Value);
    }

    static DocumentCategory ModuleCategoryOrScript(bool module) => module ? ModuleCategory.Standard : DocumentCategory.Script;

    static DocumentInfo DocumentFor(string name, DocumentCategory category)
    {
        // Absolute paths become file: URIs so ClearScript keeps the whole path
        // (it keeps only the file name of a plain name) and module imports
        // resolve relative to them.
        if (Path.IsPathRooted(name))
        {
            return new DocumentInfo(new Uri(name)) { Category = category, Flags = DocumentFlags.None };
        }
        return new DocumentInfo(name) { Category = category, Flags = DocumentFlags.None };
    }

    public Completion Call(object function, object? receiver, params object?[] args)
    {
        var f = (ScriptObject)function;
        var scriptArgs = new object?[args.Length];
        for (int i = 0; i < args.Length; i++) scriptArgs[i] = ToScript(args[i]);
        if (receiver is null or JsUndefined)
        {
            return Execute(() => f.InvokeAsFunction(scriptArgs), false);
        }
        var call = (ScriptObject)Engine.Evaluate(new DocumentInfo("v8sharp-call.js") { Flags = DocumentFlags.None },
            "(function (f, r, a) { return Reflect.apply(f, r, a); })");
        var array = (ScriptObject)Engine.Evaluate(new DocumentInfo("v8sharp-array.js") { Flags = DocumentFlags.None }, "[]");
        for (int i = 0; i < scriptArgs.Length; i++) array.SetProperty(i, scriptArgs[i]);
        return Execute(() => call.InvokeAsFunction(f, ToScript(receiver), array), false);
    }

    public Completion GetProperty(object target, string name)
    {
        if (target is not ScriptObject o) return Completion.Of(JsUndefined.Value);
        return Execute(() => o.GetProperty(name), false);
    }

    public void DetachGlobal() => NativeOp("detachGlobal");

    Completion Execute(Func<object?> action, bool isModule)
    {
        if (_isolate.Terminating) return Completion.Terminated;
        try
        {
            return Completion.Of(ToHost(action()));
        }
        catch (ScriptInterruptedException)
        {
            return Completion.Terminated;
        }
        catch (ScriptEngineException e)
        {
            if (_isolate.Terminating) return Completion.Terminated;
            if (e.InnerException is ScriptInterruptedException) return Completion.Terminated;
            return new Completion(CompletionKind.Throw, Exception: ParseException(e));
        }
    }

    /// <summary>ClearScript's error details are
    /// "<c>message\n    at [fn (]name:line:col[)] -> source line\n    at ...</c>":
    /// the first frame is v8::Message's location (the error's creation point or
    /// the throw site), with the source line after "<c> -> </c>".</summary>
    static JsExceptionInfo ParseException(ScriptEngineException e)
    {
        object? exception = ToHost(e.ScriptExceptionAsObject);
        string details = e.ErrorDetails ?? "";
        foreach (var raw in details.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int at = line.IndexOf("    at ", StringComparison.Ordinal);
            if (at != 0) continue;
            string frame = line[7..];
            string? sourceLine = null;
            int arrow = frame.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                sourceLine = frame[(arrow + 4)..];
                frame = frame[..arrow];
            }
            string loc = frame.EndsWith(')') && frame.Contains(" (", StringComparison.Ordinal)
                ? frame[(frame.LastIndexOf(" (", StringComparison.Ordinal) + 2)..^1]
                : frame;
            int c2 = loc.LastIndexOf(':');
            int c1 = c2 > 0 ? loc.LastIndexOf(':', c2 - 1) : -1;
            if (c1 <= 0 || !int.TryParse(loc[(c1 + 1)..c2], out int ln) || !int.TryParse(loc[(c2 + 1)..], out int col)) break;
            string name = loc[..c1];
            // ExecutionStarted is false for a compile (parse) error.
            bool isSyntax = !e.ExecutionStarted || (exception is ScriptObject so && IsCompileError(so, details));
            int start = col - 1;
            int end = start + 1;
            if (isSyntax && sourceLine is not null) end = start + TokenLength(sourceLine, start);
            return new JsExceptionInfo(exception, name, ln, start, end, sourceLine, isSyntax);
        }
        return new JsExceptionInfo(exception);
    }

    static bool IsCompileError(ScriptObject error, string details)
    {
        try
        {
            if (error.GetProperty("name") is not string n || n != "SyntaxError") return false;
            // A parse error has one pseudo-frame (the message location), no call frames.
            int frames = 0;
            foreach (var l in details.Split('\n')) if (l.StartsWith("    at ", StringComparison.Ordinal)) frames++;
            return frames <= 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The length of the JS token at <paramref name="start"/>, for the
    /// <c>^^^</c> underline of a parse error (v8::Message's end column).</summary>
    internal static int TokenLength(string line, int start)
    {
        if (start >= line.Length) return 1;
        char c = line[start];
        int i = start;
        if (char.IsLetter(c) || c is '_' or '$' or '\\' || char.IsSurrogate(c))
        {
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '$' or '\\' || char.IsSurrogate(line[i]))) i++;
            return i - start;
        }
        if (char.IsDigit(c))
        {
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '_')) i++;
            return i - start;
        }
        if (c is '"' or '\'' or '`')
        {
            i++;
            while (i < line.Length && line[i] != c) { if (line[i] == '\\') i++; i++; }
            return Math.Min(line.Length, i + 1) - start;
        }
        string[] puncts = [">>>=", "...", "===", "!==", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=",
            "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "**"];
        foreach (var p in puncts)
        {
            if (string.CompareOrdinal(line, start, p, 0, p.Length) == 0) return p.Length;
        }
        return 1;
    }

    void OnPromiseRejection(V8PromiseRejectionEventKind kind, object promise, object value)
    {
        var k = kind switch
        {
            V8PromiseRejectionEventKind.RejectedWithoutHandler => PromiseRejectionKind.RejectedWithoutHandler,
            V8PromiseRejectionEventKind.HandlerAddedAfterRejection => PromiseRejectionKind.HandlerAddedAfterReject,
            _ => (PromiseRejectionKind?)null,
        };
        if (k is { } kk) _isolate.Host.OnPromiseRejection(this, kk, promise, ToHost(value));
    }

    public void Dispose()
    {
        try { Engine.Dispose(); } catch { }
    }

    /// <summary>Routes ClearScript's module loads to IJsHost.LoadModule.</summary>
    sealed class ModuleLoader(OracleRealm realm) : DocumentLoader
    {
        readonly Dictionary<string, Document> _cache = new(StringComparer.Ordinal);

        public override Task<Document> LoadDocumentAsync(DocumentSettings settings, DocumentInfo? sourceInfo, string specifier,
            DocumentCategory category, DocumentContextCallback contextCallback)
        {
            string referrer = sourceInfo?.Uri is { IsFile: true } u ? u.LocalPath : sourceInfo?.Name ?? "";
            try
            {
                var m = realm._isolate.Host.LoadModule(specifier, referrer, specifier.EndsWith(".json", StringComparison.Ordinal) ? "json" : null);
                if (!_cache.TryGetValue(m.Name, out var doc))
                {
                    var cat = m.IsJson ? DocumentCategory.Json : category;
                    doc = new StringDocument(new DocumentInfo(new Uri(m.Name)) { Category = cat, Flags = DocumentFlags.None }, m.Source);
                    _cache[m.Name] = doc;
                }
                return Task.FromResult(doc);
            }
            catch (JsHostError e)
            {
                return Task.FromException<Document>(new FileNotFoundException(e.Message));
            }
        }
    }
}
