// Tests of DisposableStack and AsyncDisposableStack (Builtins.DisposableStack.cs,
// Objects/JSDisposableStack.cs). Expected results were recorded with the
// oracle from the equivalent scripts.
namespace V8Sharp.Tests.Builtins;

public class DisposableStackBuiltinsTest : CoreBuiltinsTest
{
    readonly List<string> _log = [];

    JSFunction Log(string text, bool withArg = false) => Fn((_, a) =>
    {
        _log.Add(withArg ? text + S(a.Length > 0 ? a[0] : JSValue.Undefined) : text);
        return default;
    });

    JSObject Disposable(string text)
    {
        JSObject o = Obj();
        ObjectOps.SetProperty(i_isolate, o, ReadOnlyRoots.dispose_symbol, Log(text), StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        return o;
    }

    [Fact]
    public void UseAdoptDeferMove()
    {
        JSValue stack = New("DisposableStack");
        CallMethod(stack, "use", Disposable("a"));
        CallMethod(stack, "adopt", Num(5), Log("b", withArg: true));
        CallMethod(stack, "defer", Log("c"));
        JSValue moved = CallMethod(stack, "move");
        Assert.True(Get(stack, "disposed").IsTrue);
        Assert.False(Get(moved, "disposed").IsTrue);
        CallMethod(moved, "dispose");
        Assert.Equal("c,b5,a", string.Join(",", _log));
        Assert.True(Get(moved, "disposed").IsTrue);
        // Disposing twice is a no-op.
        CallMethod(moved, "dispose");
        Assert.Equal("c,b5,a", string.Join(",", _log));
        // null and undefined are accepted by use() and ignored.
        JSValue stack2 = New("DisposableStack");
        Assert.True(CallMethod(stack2, "use", JSValue.Null).IsNull);
        CallMethod(stack2, "dispose");
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("TypeError: Constructor DisposableStack requires 'new'", Throws(() => Call("DisposableStack")));
        Assert.Equal("TypeError: An object is expected with `using` declarations",
            Throws(() => CallMethod(New("DisposableStack"), "use", Num(1))));
        JSValue disposed = New("DisposableStack");
        CallMethod(disposed, "dispose");
        Assert.Equal("ReferenceError: Cannot call DisposableStack.prototype.use on an already-disposed DisposableStack",
            Throws(() => CallMethod(disposed, "use", Obj())));
        Assert.Equal("[object DisposableStack]", S(CallOn("Object.prototype.toString", New("DisposableStack"))));
        Assert.Equal("[object AsyncDisposableStack]", S(CallOn("Object.prototype.toString", New("AsyncDisposableStack"))));
    }

    [Fact]
    public void SuppressedErrors()
    {
        JSValue stack = New("DisposableStack");
        CallMethod(stack, "defer", Fn((_, _) => i_isolate.Throw(Num(1))));
        CallMethod(stack, "defer", Fn((_, _) => i_isolate.Throw(Num(2))));
        try
        {
            CallMethod(stack, "dispose");
            Assert.Fail("expected an exception");
        }
        catch (JavaScriptException e)
        {
            Assert.Equal("SuppressedError: An error was suppressed during disposal", ErrorUtils.ToString(i_isolate, e.Value).ToString());
            Assert.Equal("1", S(Get(e.Value, "error")));
            Assert.Equal("2", S(Get(e.Value, "suppressed")));
        }
    }

    [Fact]
    public void IteratorDispose()
    {
        JSObject iterator = Obj(("return", Log("return")));
        JSObject.SetPrototype(i_isolate, iterator, NC.InitialIteratorPrototype, false, ShouldThrow.ThrowOnError);
        JSValue dispose = Get(iterator, ReadOnlyRoots.dispose_symbol);
        Assert.Equal("[Symbol.dispose]", S(Get(dispose, "name")));
        Execution.Call(i_isolate, dispose, iterator, []);
        Assert.Equal("return", string.Join(",", _log));
    }

    [Fact]
    public void AsyncDisposableStack()
    {
        JSValue stack = New("AsyncDisposableStack");
        CallMethod(stack, "use", Disposable("sync"));
        JSObject asyncResource = Obj();
        ObjectOps.SetProperty(i_isolate, asyncResource, ReadOnlyRoots.async_dispose_symbol, Fn((_, _) =>
        {
            _log.Add("async");
            return CallOn("Promise.resolve", G("Promise"), Num(1));
        }), StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        CallMethod(stack, "use", asyncResource);
        CallMethod(stack, "defer", Log("deferred"));
        JSValue promise = CallMethod(stack, "disposeAsync");
        Assert.IsType<JSPromise>(promise.Object);
        CallMethod(promise, "then", Log("done:", withArg: true));
        Assert.Equal("deferred", string.Join(",", _log));
        i_isolate.DefaultMicrotaskQueue.PerformCheckpoint(i_isolate);
        Assert.Equal("deferred,async,sync,done:undefined", string.Join(",", _log));
        Assert.True(Get(stack, "disposed").IsTrue);
    }
}
