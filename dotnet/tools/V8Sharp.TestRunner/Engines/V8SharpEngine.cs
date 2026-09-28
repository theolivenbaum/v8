// IJsEngine over V8Sharp: one V8Sharp.Isolate per IJsIsolate, one native
// context per realm. The d8 globals come from D8Shell and the d8 shim, as for
// the oracle.
using V8Sharp.Builtins;
using V8Sharp.Codegen;
using V8Sharp.D8;
using V8Sharp.Common;
using V8Sharp.Init;
using V8Sharp.Objects;
using VIsolate = V8Sharp.Isolate;
using VExecution = V8Sharp.Execution;

namespace V8Sharp.TestRunner.Engines;

public sealed class V8SharpEngine : IJsEngine
{
    public string Name => "v8sharp";

    /// <summary>The V8 revision being ported (dotnet/UPSTREAM.md).</summary>
    public string Version => "15.6.0";

    public string? UnavailableReason => null;

    readonly FlagList _flags = FlagList.Default.Clone();

    public void SetFlags(IReadOnlyList<string> flags)
    {
        foreach (var f in flags)
        {
            // d8's --no-can-block is Isolate::SetAllowAtomicsWait(false).
            if (f is "--no-can-block") { AllowAtomicsWait = false; continue; }
            // d8 ignores flags it does not know.
            try { _flags.SetFlagsFromCommandLine([f]); } catch (Exception) { }
        }
    }

    internal bool AllowAtomicsWait { get; private set; } = true;

    public IJsIsolate CreateIsolate(IJsHost host) => new V8SharpJsIsolate(this, host, _flags.Clone());
}

/// <summary>An opaque handle for a JS value that is not a primitive the host understands.</summary>
public sealed class V8SharpHandle(JSValue value)
{
    public JSValue Value { get; } = value;

    public override bool Equals(object? obj) => obj is V8SharpHandle h && h.Value.IsIdenticalTo(Value);

    public override int GetHashCode() => Value.HeapObjectOrNull is { } o ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o) : 0;
}

sealed class V8SharpJsIsolate : IJsIsolate
{
    /// <summary>Nesting depth of script executions (v8's CallDepthScope for the microtask policy).</summary>
    internal int ExecuteDepth;

    internal readonly VIsolate Isolate;
    internal readonly IJsHost Host;
    readonly List<V8SharpRealm> _realms = [];
    internal volatile bool Terminating;

    public IJsRealm MainRealm { get; }

    public V8SharpJsIsolate(V8SharpEngine engine, IJsHost host, FlagList flags)
    {
        Host = host;
        Isolate = VIsolate.New(flags);
        Isolate.AllowAtomicsWait = engine.AllowAtomicsWait;
        // d8's Shell::HostCreateShadowRealmContext: a plain new context in the
        // initiator's origin (same security token), with its own module map.
        Isolate.HostCreateShadowRealmContextCallback = ModuleLoader.HostCreateShadowRealmContext;
        Isolate.PromiseRejectCallback = OnPromiseReject;
        // d8's stdout and its D8Console, routed to the shell's output.
        Isolate.StdOut = new HostWriter(host.WriteStdout);
        Isolate.ConsoleDelegate = new D8Console(host.WriteStdout, host.WriteStderr);
        Isolate.DefaultMicrotaskQueue.UncaughtException += OnMessage;
        var main = new V8SharpRealm(this, Isolate.InitialNativeContext!);
        _realms.Add(main);
        MainRealm = main;
    }

    internal VIsolate.IsolateScope Enter() => Isolate.Enter();

    public IJsRealm CreateRealm(IJsRealm? shareSecurityTokenWith, bool ownMicrotaskQueue = false)
    {
        NativeContext context;
        using (Enter())
        {
            // d8 creates the queue with v8::MicrotaskQueue::New (kExplicit): nothing flushes it.
            MicrotaskQueue? queue = null;
            if (ownMicrotaskQueue)
            {
                queue = new MicrotaskQueue(Isolate);
                queue.UncaughtException += OnMessage;
            }
            context = Bootstrapper.CreateEnvironment(Isolate, queue);
        }
        if (shareSecurityTokenWith is V8SharpRealm from) context.SecurityToken = from.Context.SecurityToken;
        var realm = new V8SharpRealm(this, context);
        _realms.Add(realm);
        return realm;
    }

    public void TerminateExecution()
    {
        Terminating = true;
        Isolate.RequestTerminateExecution();
    }

    public void CancelTerminateExecution()
    {
        Terminating = false;
        Isolate.CancelTerminateExecution();
    }

    public void CollectGarbage()
    {
        using (Enter()) Isolate.CollectGarbage();
    }

    public bool PumpMessageLoop()
    {
        if (Terminating || !Isolate.HasPendingTasks) return false;
        using (Enter())
        {
            try
            {
                // Wait for a delayed task in short slices so the shell's own task
                // queue (setTimeout, Worker messages) keeps running; pending
                // delayed tasks keep the shell's message loop alive.
                return Isolate.RunPendingTasks(10) || Isolate.HasPendingTasks;
            }
            catch (TerminationException)
            {
                // A task's callbacks called quit() or d8.terminate().
                return false;
            }
        }
    }

    void OnPromiseReject(JSPromise promise, JSValue value, PromiseRejectEvent e)
    {
        PromiseRejectionKind kind;
        if (e == PromiseRejectEvent.kPromiseRejectWithNoHandler) kind = PromiseRejectionKind.RejectedWithoutHandler;
        else if (e == PromiseRejectEvent.kPromiseHandlerAddedAfterReject) kind = PromiseRejectionKind.HandlerAddedAfterReject;
        else return;
        NativeContext? owner = promise.GetCreationContext();
        V8SharpRealm realm = _realms[0];
        foreach (var r in _realms)
        {
            if (ReferenceEquals(r.Context, owner)) realm = r;
        }
        JsExceptionInfo? message = null;
        if (kind == PromiseRejectionKind.RejectedWithoutHandler)
        {
            // Shell::PromiseRejectCallback: objects get v8::Exception::CreateMessage
            // (with the stack trace capture d8 turns on, every object's message has
            // a stack trace); other values are replaced by a fresh
            // Error("Unhandled Promise.").
            JSValue exception = value;
            if (value.HeapObjectOrNull is not JSReceiver)
            {
                exception = Isolate.Factory.NewError(realm.Context.ErrorFunction,
                    Isolate.Factory.NewStringFromUtf16("Unhandled Promise."));
            }
            message = realm.ExceptionInfo(exception, Isolate.CreateMessage(exception, null));
        }
        Host.OnPromiseRejection(realm, kind, new V8SharpHandle(promise), V8SharpRealm.ToHost(value), message);
    }

    /// <summary>The message listener d8 installs (PrintMessageCallback).</summary>
    void OnMessage(VIsolate isolate, JavaScriptException e)
    {
        NativeContext? current = isolate.Context?.NativeContext;
        V8SharpRealm realm = _realms[0];
        foreach (var r in _realms)
        {
            if (ReferenceEquals(r.Context, current)) realm = r;
        }
        Host.ReportMessage(realm, realm.ExceptionInfo(e.Value, e.MessageObject));
    }

    /// <summary>A TextWriter over the host's stdout (Isolate.StdOut).</summary>
    sealed class HostWriter(Action<string> write) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => write(value.ToString());
        public override void Write(string? value)
        {
            if (value is not null) write(value);
        }
    }

    public void Dispose() => Isolate.Deinit();
}

sealed class V8SharpRealm(V8SharpJsIsolate owner, NativeContext context) : IJsRealm
{
    internal NativeContext Context { get; } = context;
    internal V8SharpJsIsolate Owner => owner;
    VIsolate Isolate => owner.Isolate;

    IJsIsolate IJsRealm.Isolate => owner;

    public object GlobalObject => new V8SharpHandle(Context.GlobalProxyObject);

    internal static object? ToHost(JSValue v)
    {
        if (v.IsUndefined) return JsUndefined.Value;
        if (v.IsNull) return null;
        if (v.IsBoolean) return v.IsTrue;
        if (v.IsNumber) return v.Number;
        if (v.HeapObjectOrNull is JSString s) return s.ToString();
        return new V8SharpHandle(v);
    }

    JSValue ToScript(object? v) => v switch
    {
        null => JSValue.Null,
        JsUndefined => JSValue.Undefined,
        bool b => JSValue.FromBoolean(b),
        double d => JSValue.FromNumber(d),
        int i => JSValue.FromInt(i),
        string s => Isolate.Factory.NewStringFromUtf16(s),
        V8SharpHandle h => h.Value,
        _ => throw new ArgumentException("not a V8Sharp value: " + v.GetType()),
    };

    Completion Execute(Func<JSValue> action)
    {
        if (owner.Terminating) return Completion.Terminated;
        using (owner.Enter())
        using (Isolate.EnterContext(Context))
        {
            owner.ExecuteDepth++;
            try
            {
                JSValue result = action();
                // v8::MicrotasksPolicy::kAuto: the checkpoint runs when the call
                // depth returns to zero, so a nested Realm.eval does not run the
                // microtasks its script queued.
                if (owner.ExecuteDepth == 1) VExecution.PerformMicrotaskCheckpoint(Isolate);
                return Completion.Of(ToHost(result));
            }
            catch (TerminationException)
            {
                return Completion.Terminated;
            }
            catch (JavaScriptException e)
            {
                if (owner.Terminating) return Completion.Terminated;
                return new Completion(CompletionKind.Throw, Exception: ExceptionInfo(e));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // An engine failure (an unported runtime function, an internal
                // assertion): reported like an uncaught exception so the test's
                // output names it.
                string text = "V8Sharp internal error: " + e.GetType().Name + ": " + e.Message;
                if (Environment.GetEnvironmentVariable("V8SHARP_DEBUG_ERRORS") is not null) text += "\n" + e;
                return new Completion(CompletionKind.Throw, Exception: new JsExceptionInfo(text));
            }
            finally
            {
                owner.ExecuteDepth--;
            }
        }
    }

    JsExceptionInfo ExceptionInfo(JavaScriptException e) => ExceptionInfo(e.Value, e.MessageObject);

    internal JsExceptionInfo ExceptionInfo(JSValue value, JSMessageObject? message)
    {
        object? exception = ToHost(value);
        bool isSyntax = value.HeapObjectOrNull is JSObject o &&
            ReferenceEquals(o.Map, Context.SyntaxErrorFunction.InitialMap);
        if (message is null) return new JsExceptionInfo(exception, IsSyntaxError: isSyntax);
        string name = message.Script.Name.HeapObjectOrNull is JSString n ? n.ToString() : "undefined";
        int line = message.GetLineNumber();
        if (message.StartPosition < 0) return new JsExceptionInfo(exception, name, line, IsSyntaxError: isSyntax);
        int start = message.GetColumnNumber();
        int end = start + (message.EndPosition - message.StartPosition);
        string sourceLine = message.GetSourceLine(Isolate).ToString();
        return new JsExceptionInfo(exception, name, line, start, end, sourceLine, isSyntax);
    }

    public Completion RunScript(string source, string name) => Execute(() =>
    {
        // Shell::ExecuteString: the script's name is the origin for resolving
        // imports that have no referrer (ShadowRealm.prototype.importValue).
        _moduleLoader.Origin = name;
        JSFunction function = Compiler.CompileScript(Isolate, Isolate.Factory.NewStringFromUtf16(source),
            Isolate.Factory.NewStringFromUtf16(name));
        return Compiler.RunScript(Isolate, function);
    });

    /// <summary>The realm's module map and callbacks (d8's ModuleEmbedderData), created with the realm.</summary>
    readonly ModuleLoader _moduleLoader = new(owner.Isolate, context, new HostModuleSourceProvider(owner.Host));

    /// <summary>The root module and evaluation promise of the last RunModule, for FinishModule.</summary>
    (SourceTextModule Module, JSPromise Promise)? _lastModule;

    public Completion RunModule(string source, string name) => Execute(() =>
    {
        _lastModule = null;
        (SourceTextModule module, JSPromise promise) = _moduleLoader.StartModule(name, new ModuleSourceText(name, source));
        _lastModule = (module, promise);
        VExecution.PerformMicrotaskCheckpoint(Isolate);
        // A module's evaluation returns a promise (top-level await); d8 reports
        // its rejection as an uncaught exception, thrown afresh (ThrowException)
        // so the message's location comes from the exception or is empty.
        if (promise.Status == PromiseState.kRejected) Isolate.Throw(promise.Result);
        return JSValue.Undefined;
    });

    public Completion FinishModule()
    {
        if (_lastModule is not { } last) return Completion.Of(JsUndefined.Value);
        _lastModule = null;
        return Execute(() =>
        {
            if (last.Promise.Status == PromiseState.kRejected)
            {
                // If the exception has been caught by the promise pipeline, we
                // rethrow here in order to ReportException.
                Isolate.Throw(last.Promise.Result);
            }
            var stalled = last.Module.GetStalledTopLevelAwaitMessages(Isolate);
            if (stalled.Count > 0)
            {
                JSMessageObject message = stalled[0].Message;
                JSString text = MessageFormatter.Format(Isolate, message.Type, [message.Argument]);
                throw new JavaScriptException(Isolate.Factory.NewError(Context.ErrorFunction, text), message);
            }
            return JSValue.Undefined;
        });
    }

    public Completion JsonParse(string source) => Execute(() =>
        V8Sharp.Json.JsonParser.Parse(Isolate, Isolate.Factory.NewStringFromUtf16(source), JSValue.Undefined));

    public Completion ThrowError(string message) => Execute(() =>
        Isolate.Throw(Isolate.Factory.NewError(Context.ErrorFunction, Isolate.Factory.NewStringFromUtf16(message))));

    public bool HasConsoleDelegate => true;

    public Completion Compile(string source, string name, bool isModule) => Execute(() =>
    {
        if (isModule)
        {
            // Shell::ExecuteModule with options.compile_only: fetch the tree only.
            Module module = _moduleLoader.FetchModuleTree(null, name, ModuleType.kJavaScript, new ModuleSourceText(name, source));
            _moduleLoader.Origin = _moduleLoader.GetModuleSpecifier(module);
        }
        else
        {
            _moduleLoader.Origin = name;
            Compiler.CompileScript(Isolate, Isolate.Factory.NewStringFromUtf16(source), Isolate.Factory.NewStringFromUtf16(name));
        }
        return JSValue.Undefined;
    });

    /// <summary>Module resolution and reading through the embedding shell (IJsHost.LoadModule).</summary>
    sealed class HostModuleSourceProvider(IJsHost host) : IModuleSourceProvider
    {
        public ModuleSourceText Load(string specifier, string referrer, ModuleType type)
        {
            try
            {
                ModuleSource m = host.LoadModule(specifier, referrer,
                    type switch { ModuleType.kJSON => "json", ModuleType.kText => "text", ModuleType.kBytes => "bytes", _ => null });
                return new ModuleSourceText(m.Name, m.Source, m.Bytes);
            }
            catch (JsHostError e)
            {
                throw new ModuleLoadException(e.Message, e.ErrorType);
            }
        }
    }

    public Completion Call(object function, object? receiver, params object?[] args) => Execute(() =>
    {
        var jsArgs = new JSValue[args.Length];
        for (int i = 0; i < args.Length; i++) jsArgs[i] = ToScript(args[i]);
        return VExecution.Call(Isolate, ToScript(function), ToScript(receiver ?? JsUndefined.Value), jsArgs);
    });

    public Completion GetProperty(object target, string name) => Execute(() =>
        ObjectOps.GetProperty(Isolate, ToScript(target), Isolate.Factory.InternalizeString(name)));

    public object CreateFunction(string name, JsHostFunction function) => CreateFunction(name, function, false);

    public object? CreateStringArgumentsFunction(string name, JsHostFunction function) => CreateFunction(name, function, true);

    object CreateFunction(string name, JsHostFunction function, bool stringArguments)
    {
        using (owner.Enter())
        using (Isolate.EnterContext(Context))
        {
            var adapter = new HostFunctionAdapter(this, function, stringArguments);
            var data = new FunctionTemplateInfo(adapter.Invoke) { Length = 0 };
            SharedFunctionInfo info = Isolate.Factory.NewSharedFunctionInfo(Isolate.Factory.InternalizeString(name), data,
                Builtin.HandleApiCallOrConstruct, 0, false);
            info.BuiltinId = Builtin.HandleApiCallOrConstruct;
            // FunctionTemplate functions are sloppy natives (no receiver conversion).
            info.LanguageMode = LanguageMode.Sloppy;
            info.Native = true;
            info.UpdateFunctionMapIndex();
            return new V8SharpHandle(Isolate.Factory.NewFunction(info, Context, Context.StrictFunctionWithoutPrototypeMap));
        }
    }

    sealed class HostFunctionAdapter(V8SharpRealm realm, JsHostFunction function, bool stringArguments)
    {
        public JSValue Invoke(VIsolate isolate, in BuiltinArguments args)
        {
            var list = new object?[args.ArgcWithoutReceiver];
            for (int i = 0; i < list.Length; i++)
            {
                JSValue arg = args.Arguments[i];
                if (stringArguments)
                {
                    // Shell::WriteToFile: symbols print their description.
                    if (arg.HeapObjectOrNull is Symbol symbol) arg = symbol.Description;
                    list[i] = ObjectOps.ToString(isolate, arg).ToString();
                }
                else
                {
                    list[i] = ToHost(arg);
                }
            }
            object? result;
            try
            {
                result = function(realm, list);
            }
            catch (JsHostError e)
            {
                JSFunction constructor = e.ErrorType switch
                {
                    "TypeError" => realm.Context.TypeErrorFunction,
                    "RangeError" => realm.Context.RangeErrorFunction,
                    "SyntaxError" => realm.Context.SyntaxErrorFunction,
                    "ReferenceError" => realm.Context.ReferenceErrorFunction,
                    "EvalError" => realm.Context.EvalErrorFunction,
                    "URIError" => realm.Context.UriErrorFunction,
                    _ => realm.Context.ErrorFunction,
                };
                return isolate.Throw(isolate.Factory.NewError(constructor, isolate.Factory.NewStringFromUtf16(e.Message)));
            }
            catch (JsThrowValue e)
            {
                return isolate.Throw(realm.ToScript(e.Value));
            }
            catch (JsTermination)
            {
                realm.Owner.TerminateExecution();
                throw new TerminationException();
            }
            // quit() and d8.terminate() stop the script right after the call returns.
            if (realm.Owner.Terminating) throw new TerminationException();
            return realm.ToScript(result);
        }
    }

    // --- structured clone (d8's Worker messages and d8.serializer) ---

    public bool SupportsSerialization => true;

    /// <summary>Runs <paramref name="action"/> from inside a host call: no microtask checkpoint.</summary>
    Completion ExecuteNested(Func<object?> action)
    {
        if (owner.Terminating) return Completion.Terminated;
        using (owner.Enter())
        using (Isolate.EnterContext(Context))
        {
            try
            {
                return Completion.Of(action());
            }
            catch (TerminationException)
            {
                return Completion.Terminated;
            }
            catch (JavaScriptException e)
            {
                if (owner.Terminating) return Completion.Terminated;
                return new Completion(CompletionKind.Throw, Exception: ExceptionInfo(e));
            }
        }
    }

    public Completion SerializeValue(object? value, object? transfer) => ExecuteNested(() =>
        V8Sharp.D8.D8Serialization.SerializeValue(Isolate, ToScript(value), ToScript(transfer)));

    public Completion DeserializeValue(object message) => ExecuteNested(() =>
        ToHost(V8Sharp.D8.D8Serialization.DeserializeValue(Isolate, (V8Sharp.D8.SerializationData)message)));

    public Completion SerializerSerialize(object?[] values) => ExecuteNested(() =>
    {
        var jsValues = new JSValue[values.Length];
        for (int i = 0; i < values.Length; i++) jsValues[i] = ToScript(values[i]);
        return ToHost(V8Sharp.D8.D8Serialization.SerializerSerialize(Isolate, jsValues));
    });

    public Completion SerializerDeserialize(object? buffer) => ExecuteNested(() =>
        ToHost(V8Sharp.D8.D8Serialization.SerializerDeserialize(Isolate, ToScript(buffer))));

    public void DetachGlobal()
    {
        using (owner.Enter())
        {
            Isolate.DetachGlobal(Context);
        }
    }

    public void Dispose()
    {
    }
}
