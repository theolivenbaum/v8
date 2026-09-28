// Tests of the promise builtins and the microtask queue (Builtins.Promise*.cs,
// Execution/MicrotaskQueue.cs). Until scripts run, JavaScript callbacks are
// API functions and the microtask queue is drained explicitly; the expected
// orders were recorded with the oracle from the equivalent scripts.
namespace V8Sharp.Tests.Builtins;

public class PromiseBuiltinsTest : CoreBuiltinsTest
{
    readonly List<string> _log = [];

    JSValue Promise => G("Promise");

    /// <summary>A callback that logs <paramref name="text"/> (plus its argument) and returns <paramref name="result"/>.</summary>
    JSFunction Log(string text, JSValue result = default, bool withArg = false) => Fn((_, a) =>
    {
        _log.Add(withArg ? text + ":" + S(a.Length > 0 ? a[0] : JSValue.Undefined) : text);
        return result;
    });

    JSValue Then(JSValue promise, JSValue onFulfilled, JSValue onRejected = default) =>
        CallMethod(promise, "then", onFulfilled, onRejected);

    void RunMicrotasks() => i_isolate.DefaultMicrotaskQueue.PerformCheckpoint(i_isolate);

    string Drain()
    {
        RunMicrotasks();
        string result = string.Join(",", _log);
        _log.Clear();
        return result;
    }

    static PromiseState StateOf(JSValue p) => ((JSPromise)p.Object).Status;

    [Fact]
    public void ConstructorAndThen()
    {
        JSValue resolveFn = default;
        JSValue p = New("Promise", Fn((_, a) =>
        {
            resolveFn = a[0];
            return JSValue.Undefined;
        }));
        Assert.Equal(PromiseState.kPending, StateOf(p));
        JSValue q = Then(p, Log("fulfilled", Num(2), withArg: true));
        Execution.Call(i_isolate, resolveFn, JSValue.Undefined, [Num(1)]);
        Assert.Equal(PromiseState.kFulfilled, StateOf(p));
        Assert.Equal("", string.Join(",", _log));
        Assert.Equal("fulfilled:1", Drain());
        Assert.Equal(PromiseState.kFulfilled, StateOf(q));
        Assert.Equal("2", S(((JSPromise)q.Object).Result));
    }

    [Fact]
    public void ConstructorErrors()
    {
        Assert.Equal("TypeError: Promise constructor cannot be invoked without 'new'", Throws(() => Call("Promise", Fn((_, _) => default))));
        Assert.Equal("TypeError: Promise resolver 1 is not a function", Throws(() => New("Promise", Num(1))));
        Assert.Equal("TypeError: Method Promise.prototype.then called on incompatible receiver 1",
            Throws(() => CallOn("Promise.prototype.then", Num(1))));
    }

    [Fact]
    public void ExecutorThrowRejects()
    {
        JSValue p = New("Promise", Fn((_, _) => i_isolate.Throw(Str("boom"))));
        Assert.Equal(PromiseState.kRejected, StateOf(p));
        Then(p, JSValue.Undefined, Log("rejected", withArg: true));
        Assert.Equal("rejected:boom", Drain());
    }

    [Fact]
    public void TickOrder()
    {
        // var p1 = Promise.resolve();
        // p1.then(a1).then(a2).then(a3).then(a4);
        // new Promise(r => r(Promise.resolve())).then(b);
        // Promise.all([1,2]).then(v => 'all:' + v);
        // The oracle logs a1,a2,all:1,2,a3,b,a4.
        JSValue p1 = CallOn("Promise.resolve", Promise);
        JSValue chain = Then(p1, Log("a1"));
        chain = Then(chain, Log("a2"));
        chain = Then(chain, Log("a3"));
        Then(chain, Log("a4"));
        JSValue inner = CallOn("Promise.resolve", Promise);
        JSValue outer = New("Promise", Fn((_, a) => Execution.Call(i_isolate, a[0], JSValue.Undefined, [inner])));
        Then(outer, Log("b"));
        Then(CallOn("Promise.all", Promise, Iterable(Num(1), Num(2))), Log("all", withArg: true));
        Assert.Equal("a1,a2,all:1,2,a3,b,a4", Drain());
    }

    [Fact]
    public void ResolveWithThenable()
    {
        JSObject thenable = Obj(("then", Fn((_, a) =>
        {
            _log.Add("then");
            return Execution.Call(i_isolate, a[0], JSValue.Undefined, [Num(42)]);
        })));
        JSValue p = CallOn("Promise.resolve", Promise, thenable);
        Then(p, Log("value", withArg: true));
        Assert.Equal("then,value:42", Drain());
    }

    [Fact]
    public void RejectAndCatchAndFinally()
    {
        JSValue p = CallOn("Promise.reject", Promise, Str("e"));
        JSValue c = CallMethod(p, "catch", Log("catch", Num(5), withArg: true));
        JSValue f = CallMethod(c, "finally", Log("finally", withArg: true));
        Then(f, Log("after", withArg: true));
        Assert.Equal("catch:e,finally:undefined,after:5", Drain());

        JSValue r = CallOn("Promise.reject", Promise, Str("x"));
        JSValue rf = CallMethod(r, "finally", Log("finally2"));
        Then(rf, JSValue.Undefined, Log("rejected", withArg: true));
        Assert.Equal("finally2,rejected:x", Drain());
    }

    [Fact]
    public void SelfResolutionIsTypeError()
    {
        JSValue resolveFn = default;
        JSValue p = New("Promise", Fn((_, a) =>
        {
            resolveFn = a[0];
            return JSValue.Undefined;
        }));
        Execution.Call(i_isolate, resolveFn, JSValue.Undefined, [p]);
        Assert.Equal(PromiseState.kRejected, StateOf(p));
        Assert.Equal("TypeError: Chaining cycle detected for promise #<Promise>",
            ErrorUtils.ToString(i_isolate, ((JSPromise)p.Object).Result).ToString());
    }

    [Fact]
    public void Combinators()
    {
        JSValue rejected = CallOn("Promise.reject", Promise, Str("no"));
        Then(CallOn("Promise.allSettled", Promise, Iterable(Num(1), rejected)), Fn((_, a) =>
        {
            JSValue list = a[0];
            JSValue first = JSReceiver.GetElement(i_isolate, list.As<JSReceiver>(), 0);
            JSValue second = JSReceiver.GetElement(i_isolate, list.As<JSReceiver>(), 1);
            _log.Add(S(Get(first, "status")) + ":" + S(Get(first, "value")) + "/" + S(Get(second, "status")) + ":" +
                     S(Get(second, "reason")));
            return default;
        }));
        Assert.Equal("fulfilled:1/rejected:no", Drain());

        Then(CallOn("Promise.any", Promise, Iterable(rejected, Num(7))), Log("any", withArg: true));
        Assert.Equal("any:7", Drain());

        Then(CallOn("Promise.any", Promise, Iterable(rejected, rejected)), JSValue.Undefined, Fn((_, a) =>
        {
            _log.Add(ErrorUtils.ToString(i_isolate, a[0]).ToString() + ":" + S(Get(a[0], "errors")));
            return default;
        }));
        Assert.Equal("AggregateError: All promises were rejected:no,no", Drain());

        Then(CallOn("Promise.race", Promise, Iterable(Num(3), rejected)), Log("race", withArg: true));
        Assert.Equal("race:3", Drain());

        Then(CallOn("Promise.all", Promise, Iterable(Num(1), rejected)), JSValue.Undefined, Log("allRejected", withArg: true));
        Assert.Equal("allRejected:no", Drain());

        // Not iterable: the promise is rejected.
        Then(CallOn("Promise.all", Promise, Num(1)), JSValue.Undefined, Fn((_, a) =>
        {
            _log.Add(ErrorUtils.ToString(i_isolate, a[0]).ToString());
            return default;
        }));
        Assert.StartsWith("TypeError: ", Drain());
    }

    [Fact]
    public void TryAndWithResolvers()
    {
        JSValue t = CallOn("Promise.try", Promise, Fn((_, a) => Num(a.Length)), Num(1), Num(2));
        Then(t, Log("try", withArg: true));
        Assert.Equal("try:2", Drain());
        JSValue tt = CallOn("Promise.try", Promise, Fn((_, _) => i_isolate.Throw(Str("t"))));
        Then(tt, JSValue.Undefined, Log("tryThrow", withArg: true));
        Assert.Equal("tryThrow:t", Drain());

        JSValue wr = CallOn("Promise.withResolvers", Promise);
        Assert.Equal("promise,resolve,reject", Keys(Call("Object.keys", wr)));
        Then(Get(wr, "promise"), Log("wr", withArg: true));
        Execution.Call(i_isolate, Get(wr, "resolve"), JSValue.Undefined, [Str("ok")]);
        Assert.Equal("wr:ok", Drain());
    }

    [Fact]
    public void SubclassingUsesNewPromiseCapability()
    {
        // A constructor whose executor records the resolving functions.
        int executorCalls = 0;
        JSFunction ctor = Fn((_, a) =>
        {
            executorCalls++;
            return Execution.New(i_isolate, G("Promise"), [a[0]]);
        });
        ((JSFunction)ctor).Map.IsConstructor = true;
        PromiseCapability capability = PromiseBuiltins.NewPromiseCapability(i_isolate, ctor, true);
        Assert.Equal(1, executorCalls);
        Assert.True(ObjectOps.IsCallable(capability.Resolve));
        Assert.True(ObjectOps.IsCallable(capability.Reject));
        Assert.Equal("TypeError: 1 is not a constructor", Throws(() => PromiseBuiltins.NewPromiseCapability(i_isolate, Num(1), true)));
    }

    [Fact]
    public void QueueMicrotaskAndReporting()
    {
        var queue = i_isolate.DefaultMicrotaskQueue;
        var reported = new List<string>();
        void OnUncaught(Isolate isolate, JavaScriptException e) => reported.Add(S(e.Value));
        queue.UncaughtException += OnUncaught;
        try
        {
            PromiseBuiltins.EnqueueMicrotaskForFunction(i_isolate, Log("task"));
            PromiseBuiltins.EnqueueMicrotaskForFunction(i_isolate, Fn((_, _) => i_isolate.Throw(Str("oops"))));
            PromiseBuiltins.EnqueueMicrotaskForFunction(i_isolate, Log("after"));
            Assert.Equal("task,after", Drain());
            Assert.Equal("oops", string.Join(",", reported));
        }
        finally
        {
            queue.UncaughtException -= OnUncaught;
        }
    }

    [Fact]
    public void RejectionTracking()
    {
        var events = new List<string>();
        i_isolate.PromiseRejectCallback = (p, v, e) => events.Add(e.ToString());
        JSValue p = CallOn("Promise.reject", Promise, Str("x"));
        Assert.Equal("kPromiseRejectWithNoHandler", string.Join(",", events));
        Then(p, JSValue.Undefined, Log("handled"));
        Assert.Equal("kPromiseRejectWithNoHandler,kPromiseHandlerAddedAfterReject", string.Join(",", events));
        Assert.Equal("handled", Drain());
    }

    [Fact]
    public void ResolveProtector()
    {
        Assert.True(Protectors.IsPromiseResolveLookupChainIntact(i_isolate));
        Assert.Same(ReadOnlyRoots.resolve_string, factory.InternalizeString("resolve"));
        Assert.False(i_isolate.BootstrapperActive);
        Assert.NotNull(((JSFunction)G("Promise").Object).GetCreationContext());
        LookupIterator.UpdateProtector(i_isolate, G("Promise"), ReadOnlyRoots.resolve_string);
        Assert.False(Protectors.IsPromiseResolveLookupChainIntact(i_isolate));
        Assert.Equal(InstanceType.JSPromiseConstructorType, ((JSFunction)G("Promise").Object).Map.InstanceType);
        Set(G("Promise"), "resolve", Fn((_, _) => default));
        Assert.False(Protectors.IsPromiseResolveLookupChainIntact(i_isolate));
    }

    [Fact]
    public void ContextPromiseHooks()
    {
        var hooks = new List<string>();
        JSFunction Hook(string name) => Fn((_, a) =>
        {
            hooks.Add(name);
            return default;
        });
        PromiseBuiltins.SetPromiseHooks(i_isolate, NC, Hook("init"), Hook("before"), Hook("after"), Hook("resolve"));
        JSValue p = CallOn("Promise.resolve", Promise, Num(1));
        Then(p, Log("then"));
        RunMicrotasks();
        Assert.Equal("init,resolve,init,before,resolve,after", string.Join(",", hooks));
    }
}
