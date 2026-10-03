// Tests of WeakRef and FinalizationRegistry (Builtins.WeakRefs.cs,
// Objects/JSWeakRefs.cs): targets are held by CLR weak references, the kept
// objects of a job are cleared at the microtask checkpoint, and
// Isolate.CollectGarbage (d8's gc()) clears dead targets and posts the
// cleanup task that the embedder runs with Isolate.RunPendingTasks.
using System.Runtime.CompilerServices;

namespace V8Sharp.Tests.Builtins;

public class WeakRefsBuiltinsTest : CoreBuiltinsTest
{
    void EndJob() => i_isolate.DefaultMicrotaskQueue.PerformCheckpoint(i_isolate);

    [MethodImpl(MethodImplOptions.NoInlining)]
    JSValue NewWeakRefToGarbage() => New("WeakRef", Obj());

    [MethodImpl(MethodImplOptions.NoInlining)]
    void RegisterGarbage(JSValue registry, JSValue holdings, JSValue token = default)
    {
        if (token.IsUndefined) CallMethod(registry, "register", Obj(), holdings);
        else CallMethod(registry, "register", Obj(), holdings, token);
    }

    // Separate frames, so that no stack slot of the test keeps the target alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    bool DerefIsAlive(JSValue weakRef) => !CallMethod(weakRef, "deref").IsUndefined;

    [Fact]
    public void WeakRefKeepsTargetDuringJob()
    {
        JSObject target = Obj();
        JSValue weakRef = New("WeakRef", target);
        Assert.Same(target, CallMethod(weakRef, "deref").Object);
        Assert.Equal("[object WeakRef]", S(CallOn("Object.prototype.toString", weakRef)));
        Assert.True(i_isolate.HasKeptObjects);
        EndJob();
        Assert.False(i_isolate.HasKeptObjects);
        GC.KeepAlive(target);
    }

    [Fact]
    public void WeakRefIsClearedAfterGC()
    {
        JSValue weakRef = NewWeakRefToGarbage();
        // Within the job the target is kept alive.
        i_isolate.CollectGarbage();
        Assert.True(DerefIsAlive(weakRef));
        EndJob();
        i_isolate.CollectGarbage();
        Assert.True(CallMethod(weakRef, "deref").IsUndefined);
    }

    [Fact]
    public void FinalizationRegistryCleanup()
    {
        var cleaned = new List<string>();
        JSValue registry = New("FinalizationRegistry", Fn((_, a) =>
        {
            cleaned.Add(S(a[0]));
            return default;
        }));
        RegisterGarbage(registry, Str("held"));
        EndJob();
        i_isolate.CollectGarbage();
        Assert.Empty(cleaned);
        Assert.True(i_isolate.HasPendingTasks);
        i_isolate.RunPendingTasks();
        Assert.Equal("held", string.Join(",", cleaned));
        Assert.False(i_isolate.HasPendingTasks);
    }

    [Fact]
    public void FinalizationRegistryUnregister()
    {
        var cleaned = new List<string>();
        JSValue registry = New("FinalizationRegistry", Fn((_, a) =>
        {
            cleaned.Add(S(a[0]));
            return default;
        }));
        JSObject token = Obj();
        RegisterGarbage(registry, Str("a"), token);
        RegisterGarbage(registry, Str("b"), token);
        RegisterGarbage(registry, Str("c"));
        Assert.True(CallMethod(registry, "unregister", token).IsTrue);
        Assert.False(CallMethod(registry, "unregister", token).IsTrue);
        EndJob();
        i_isolate.CollectGarbage();
        i_isolate.RunPendingTasks();
        Assert.Equal("c", string.Join(",", cleaned));
        GC.KeepAlive(token);
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("TypeError: WeakRef: invalid target", Throws(() => New("WeakRef", Num(1))));
        Assert.Equal("TypeError: FinalizationRegistry: cleanup must be callable", Throws(() => New("FinalizationRegistry", Num(1))));
        JSValue registry = New("FinalizationRegistry", Fn((_, _) => default));
        Assert.Equal("TypeError: FinalizationRegistry.prototype.register: invalid target",
            Throws(() => CallMethod(registry, "register", Num(1))));
        JSObject o = Obj();
        Assert.Equal("TypeError: FinalizationRegistry.prototype.register: target and holdings must not be same",
            Throws(() => CallMethod(registry, "register", o, o)));
        Assert.Equal("TypeError: Invalid unregisterToken ('1')", Throws(() => CallMethod(registry, "unregister", Num(1))));
        // Non-registered symbols can be held weakly.
        JSValue symbol = Call("Symbol", Str("s"));
        Assert.Same(symbol.Object, CallMethod(New("WeakRef", symbol), "deref").Object);
        Assert.Equal("TypeError: WeakRef: invalid target", Throws(() => New("WeakRef", Call("Symbol.for", Str("s")))));
    }
}
