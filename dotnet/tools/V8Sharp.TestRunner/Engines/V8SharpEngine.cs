// IJsEngine over V8Sharp: one V8Sharp.Isolate per IJsIsolate, one native
// context per realm. The d8 globals come from D8Shell and the d8 shim, as for
// the oracle.
using V8Sharp.Builtins;
using V8Sharp.Codegen;
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
            // d8 ignores flags it does not know.
            try { _flags.SetFlagsFromCommandLine([f]); } catch (Exception) { }
        }
    }

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
    internal readonly VIsolate Isolate;
    internal readonly IJsHost Host;
    readonly List<V8SharpRealm> _realms = [];
    internal volatile bool Terminating;

    public IJsRealm MainRealm { get; }

    public V8SharpJsIsolate(V8SharpEngine engine, IJsHost host, FlagList flags)
    {
        Host = host;
        Isolate = VIsolate.New(flags);
        Isolate.PromiseRejectCallback = OnPromiseReject;
        var main = new V8SharpRealm(this, Isolate.InitialNativeContext!);
        _realms.Add(main);
        MainRealm = main;
    }

    internal VIsolate.IsolateScope Enter() => Isolate.Enter();

    public IJsRealm CreateRealm(IJsRealm? shareSecurityTokenWith)
    {
        NativeContext context;
        using (Enter())
        {
            context = Bootstrapper.CreateEnvironment(Isolate);
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
        using (Enter()) return Isolate.RunPendingTasks();
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
        Host.OnPromiseRejection(realm, kind, new V8SharpHandle(promise), V8SharpRealm.ToHost(value));
    }

    public void Dispose()
    {
    }
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
            try
            {
                JSValue result = action();
                VExecution.PerformMicrotaskCheckpoint(Isolate);
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
        }
    }

    JsExceptionInfo ExceptionInfo(JavaScriptException e)
    {
        object? exception = ToHost(e.Value);
        bool isSyntax = e.Value.HeapObjectOrNull is JSObject o &&
            ReferenceEquals(o.Map, Context.SyntaxErrorFunction.InitialMap);
        JSMessageObject? message = e.MessageObject;
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
        JSFunction function = Compiler.CompileScript(Isolate, Isolate.Factory.NewStringFromUtf16(source),
            Isolate.Factory.NewStringFromUtf16(name));
        return Compiler.RunScript(Isolate, function);
    });

    public Completion RunModule(string source, string name) =>
        new(CompletionKind.Throw, Exception: new JsExceptionInfo("V8Sharp: ES modules are not supported yet"));

    public Completion Call(object function, object? receiver, params object?[] args) => Execute(() =>
    {
        var jsArgs = new JSValue[args.Length];
        for (int i = 0; i < args.Length; i++) jsArgs[i] = ToScript(args[i]);
        return VExecution.Call(Isolate, ToScript(function), ToScript(receiver ?? JsUndefined.Value), jsArgs);
    });

    public Completion GetProperty(object target, string name) => Execute(() =>
        ObjectOps.GetProperty(Isolate, ToScript(target), Isolate.Factory.InternalizeString(name)));

    public object CreateFunction(string name, JsHostFunction function)
    {
        using (owner.Enter())
        using (Isolate.EnterContext(Context))
        {
            var adapter = new HostFunctionAdapter(this, function);
            var data = new FunctionTemplateInfo(adapter.Invoke) { Length = 0 };
            SharedFunctionInfo info = Isolate.Factory.NewSharedFunctionInfo(Isolate.Factory.InternalizeString(name), data,
                Builtin.HandleApiCallOrConstruct, 0, false);
            info.BuiltinId = Builtin.HandleApiCallOrConstruct;
            info.LanguageMode = LanguageMode.Strict;
            info.Native = true;
            info.UpdateFunctionMapIndex();
            return new V8SharpHandle(Isolate.Factory.NewFunction(info, Context, Context.StrictFunctionWithoutPrototypeMap));
        }
    }

    sealed class HostFunctionAdapter(V8SharpRealm realm, JsHostFunction function)
    {
        public JSValue Invoke(VIsolate isolate, in BuiltinArguments args)
        {
            var list = new object?[args.ArgcWithoutReceiver];
            for (int i = 0; i < list.Length; i++) list[i] = ToHost(args.Arguments[i]);
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

    // Deviation: V8Sharp has no detached global proxies yet; Realm.detachGlobal
    // and Realm.navigate leave the old global reachable.
    public void DetachGlobal()
    {
    }

    public void Dispose()
    {
    }
}
