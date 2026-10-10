// Port of src/runtime/runtime-promise.cc, runtime-collections.cc and
// runtime-weak-refs.cc, and of the protector queries of runtime-test.cc
// (%MapIteratorProtector, ...): the runtime functions of the promise,
// collection and weak reference builtins, bound into the runtime table.
namespace V8Sharp.Runtime;

public static partial class RuntimeTable
{
    static void RegisterPromiseCollectionsAndWeakRefs()
    {
        // runtime-promise.cc
        Register(FunctionId.PromiseRejectEventFromStack, static (i, a) => RuntimePromise.PromiseRejectEventFromStack(i, a[0], a[1]));
        Register(FunctionId.PromiseRevokeReject, static (i, a) => RuntimePromise.PromiseRevokeReject(i, a[0]));
        Register(FunctionId.EnqueueMicrotask, static (i, a) => RuntimePromise.EnqueueMicrotask(i, a[0]));
        Register(FunctionId.PromiseHookInit, static (i, a) => RuntimePromise.PromiseHookInit(i, a[0], a[1]));
        Register(FunctionId.PromiseHookBefore, static (i, a) => RuntimePromise.PromiseHookBeforeOrAfter(i, PromiseHookType.kBefore, a[0]));
        Register(FunctionId.PromiseHookAfter, static (i, a) => RuntimePromise.PromiseHookBeforeOrAfter(i, PromiseHookType.kAfter, a[0]));
        Register(FunctionId.RejectPromise,
            static (i, a) => JSPromise.Reject(i, a[0].As<JSPromise>(), a[1], ObjectOps.BooleanValue(a[2])));
        Register(FunctionId.ResolvePromise, static (i, a) => JSPromise.Resolve(i, a[0].As<JSPromise>(), a[1]));

        // runtime-collections.cc
        Register(FunctionId.TheHole, static (_, _) => JSValue.TheHole);
        Register(FunctionId.WeakCollectionDelete, static (i, a) =>
            JSValue.FromBoolean(JSWeakCollection.Delete(a[0].As<JSWeakCollection>(), a[1])));
        Register(FunctionId.WeakCollectionSet, static (i, a) =>
        {
            // (weak_collection, key, value, hash): the hash is V8's table detail.
            var collection = a[0].As<JSWeakCollection>();
            JSWeakCollection.Set(collection, a[1], a[2]);
            return collection;
        });

        // runtime-weak-refs.cc
        Register(FunctionId.JSWeakRefAddToKeptObjects, static (i, a) =>
        {
            i.KeepDuringJob(a[0].Object);
            return JSValue.Undefined;
        });

        // runtime-test.cc: protector queries.
        Register(FunctionId.ArrayIteratorProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsArrayIteratorLookupChainIntact(i)));
        Register(FunctionId.ArraySpeciesProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsArraySpeciesLookupChainIntact(i)));
        Register(FunctionId.IsConcatSpreadableProtector,
            static (i, _) => JSValue.FromBoolean(Protectors.IsIsConcatSpreadableLookupChainIntact(i)));
        Register(FunctionId.MapIteratorProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsMapIteratorLookupChainIntact(i)));
        Register(FunctionId.NoElementsProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsNoElementsIntact(i)));
        Register(FunctionId.PromiseSpeciesProtector,
            static (i, _) => JSValue.FromBoolean(Protectors.IsPromiseSpeciesLookupChainIntact(i)));
        Register(FunctionId.RegExpSpeciesProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsRegExpSpeciesLookupChainIntact(i)));
        Register(FunctionId.SetIteratorProtector, static (i, _) => JSValue.FromBoolean(Protectors.IsSetIteratorLookupChainIntact(i)));
        Register(FunctionId.StringIteratorProtector,
            static (i, _) => JSValue.FromBoolean(Protectors.IsStringIteratorLookupChainIntact(i)));
        Register(FunctionId.StringWrapperToPrimitiveProtector,
            static (i, _) => JSValue.FromBoolean(Protectors.IsStringWrapperToPrimitiveIntact(i)));
        Register(FunctionId.TypedArraySpeciesProtector,
            static (i, _) => JSValue.FromBoolean(Protectors.IsTypedArraySpeciesLookupChainIntact(i)));
        Register(FunctionId.GetWeakCollectionSize, static (i, a) =>
            JSValue.FromInt(JSWeakCollection.GetEntries(a[0].As<JSWeakCollection>(), 0).Length /
                            (a[0].Object is JSWeakMap ? 2 : 1)));
    }
}

/// <summary>The bodies of the runtime-promise.cc functions.</summary>
public static class RuntimePromise
{
    /// <summary>Runtime_PromiseRejectEventFromStack.</summary>
    public static JSValue PromiseRejectEventFromStack(Isolate isolate, JSValue promiseValue, JSValue value)
    {
        var promise = promiseValue.As<JSPromise>();
        PromiseBuiltins.RunAllPromiseHooks(isolate, PromiseHookType.kResolve, promise, JSValue.Undefined);
        // Report only if we don't actually have a handler.
        if (!promise.HasHandler) isolate.ReportPromiseReject(promise, value, PromiseRejectEvent.kPromiseRejectWithNoHandler);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_PromiseRevokeReject.</summary>
    public static JSValue PromiseRevokeReject(Isolate isolate, JSValue promiseValue)
    {
        var promise = promiseValue.As<JSPromise>();
        // At this point, no revocation has been issued before
        if (promise.HasHandler) throw new InvalidOperationException("Check failed: !promise->has_handler()");
        isolate.ReportPromiseReject(promise, JSValue.Undefined, PromiseRejectEvent.kPromiseHandlerAddedAfterReject);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_EnqueueMicrotask.</summary>
    public static JSValue EnqueueMicrotask(Isolate isolate, JSValue functionValue)
    {
        PromiseBuiltins.EnqueueMicrotaskForFunction(isolate, functionValue.As<JSFunction>());
        return JSValue.Undefined;
    }

    /// <summary>Runtime_PromiseHookInit.</summary>
    public static JSValue PromiseHookInit(Isolate isolate, JSValue promise, JSValue parent)
    {
        isolate.PromiseHook?.Invoke(PromiseHookType.kInit, promise.As<JSPromise>(), parent);
        return JSValue.Undefined;
    }

    /// <summary>Runtime_PromiseHookBefore / Runtime_PromiseHookAfter.</summary>
    public static JSValue PromiseHookBeforeOrAfter(Isolate isolate, PromiseHookType type, JSValue promise)
    {
        if (promise.HeapObjectOrNull is JSPromise p) isolate.PromiseHook?.Invoke(type, p, JSValue.Undefined);
        return JSValue.Undefined;
    }
}
