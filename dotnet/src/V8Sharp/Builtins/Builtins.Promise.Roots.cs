// Port of the *_shared_fun roots of src/heap/setup-heap-internal.cc
// (CreateSharedFunctionInfo and the internal SharedFunctionInfos of the
// promise, async function/generator, async iterator, async-from-sync
// iterator and async disposable stack closures), and of
// CodeStubAssembler::AllocateRootFunctionWithContext.
//
// V8 creates these SharedFunctionInfos once in the read-only heap. V8Sharp
// creates each lazily, once per isolate, on first use.
namespace V8Sharp.Builtins
{

/// <summary>
/// The builtin closures V8 allocates from a root SharedFunctionInfo
/// (AllocateRootFunctionWithContext): anonymous strict builtins with a fixed
/// length, closed over a synthetic function context.
/// </summary>
public static class RootSharedFunctions
{
    /// <summary>
    /// The root SharedFunctionInfo of <paramref name="builtin"/>
    /// (setup-heap-internal.cc CreateSharedFunctionInfo: empty name, strict,
    /// kAdapt). The length and kind are the ones setup-heap-internal.cc uses.
    /// </summary>
    public static SharedFunctionInfo Get(Isolate isolate, Builtin builtin)
    {
        Dictionary<Builtin, SharedFunctionInfo> cache = isolate.RootSharedFunctionInfos;
        if (cache.TryGetValue(builtin, out SharedFunctionInfo? info)) return info;
        info = Create(isolate, builtin);
        cache.Add(builtin, info);
        return info;
    }

    static SharedFunctionInfo Create(Isolate isolate, Builtin builtin)
    {
        (int length, FunctionKind kind, bool native, bool withoutPrototypeMap) = builtin switch
        {
            // Async functions:
            Builtin.AsyncFunctionAwaitRejectClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncFunctionAwaitResolveClosure => (1, FunctionKind.NormalFunction, false, false),
            // Async generators:
            Builtin.AsyncGeneratorAwaitResolveClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncGeneratorAwaitRejectClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncGeneratorYieldWithAwaitResolveClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncGeneratorReturnResolveClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncGeneratorReturnClosedResolveClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncGeneratorReturnClosedRejectClosure => (1, FunctionKind.NormalFunction, false, false),
            // AsyncIterator:
            Builtin.AsyncIteratorValueUnwrap => (1, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncIteratorPrototypeAsyncDisposeResolveClosure => (0, FunctionKind.NormalFunction, false, false),
            // AsyncFromSyncIterator:
            Builtin.AsyncFromSyncIteratorCloseSyncAndRethrow => (1, FunctionKind.NormalFunction, false, false),
            // Promises:
            Builtin.PromiseCapabilityDefaultResolve => (1, FunctionKind.ConciseMethod, true, true),
            Builtin.PromiseCapabilityDefaultReject => (1, FunctionKind.ConciseMethod, true, true),
            Builtin.PromiseGetCapabilitiesExecutor => (2, FunctionKind.NormalFunction, false, false),
            // Promises / finally:
            Builtin.PromiseThenFinally => (1, FunctionKind.NormalFunction, true, false),
            Builtin.PromiseCatchFinally => (1, FunctionKind.NormalFunction, true, false),
            Builtin.PromiseValueThunkFinally => (0, FunctionKind.NormalFunction, false, false),
            Builtin.PromiseThrowerFinally => (0, FunctionKind.NormalFunction, false, false),
            // Promise combinators:
            Builtin.PromiseAllResolveElementClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.PromiseAllSettledResolveElementClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.PromiseAllSettledRejectElementClosure => (1, FunctionKind.NormalFunction, false, false),
            Builtin.PromiseAnyRejectElementClosure => (1, FunctionKind.NormalFunction, false, false),
            // ShadowRealm:
            Builtin.ShadowRealmImportValueFulfilled => (1, FunctionKind.NormalFunction, false, false),
            // SourceTextModule:
            Builtin.CallAsyncModuleFulfilled => (0, FunctionKind.NormalFunction, false, false),
            Builtin.CallAsyncModuleRejected => (0, FunctionKind.NormalFunction, false, false),
            // Array.fromAsync:
            Builtin.ArrayFromAsyncIterableOnFulfilled => (1, FunctionKind.NormalFunction, false, false),
            Builtin.ArrayFromAsyncIterableOnRejected => (1, FunctionKind.NormalFunction, false, false),
            Builtin.ArrayFromAsyncArrayLikeOnFulfilled => (1, FunctionKind.NormalFunction, false, false),
            Builtin.ArrayFromAsyncArrayLikeOnRejected => (1, FunctionKind.NormalFunction, false, false),
            // Async Disposable Stack
            Builtin.AsyncDisposableStackOnFulfilled => (0, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncDisposableStackOnRejected => (0, FunctionKind.NormalFunction, false, false),
            Builtin.AsyncDisposeFromSyncDispose => (0, FunctionKind.NormalFunction, false, false),
            // ProxyRevoke:
            Builtin.ProxyRevoke => (0, FunctionKind.NormalFunction, false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(builtin), builtin, "not a root SharedFunctionInfo"),
        };

        SharedFunctionInfo shared = isolate.Factory.NewSharedFunctionInfoForBuiltin(ReadOnlyRoots.empty_string, builtin,
            length, adapt: true, kind);
        shared.LanguageMode = LanguageMode.Strict;
        shared.UpdateFunctionMapIndex();
        if (native) shared.Native = true;
        if (withoutPrototypeMap) shared.FunctionMapIndex = (int)Context.Field.STRICT_FUNCTION_WITHOUT_PROTOTYPE_MAP_INDEX;
        return shared;
    }

    /// <summary>
    /// CodeStubAssembler::AllocateRootFunctionWithContext: a closure of the
    /// root SharedFunctionInfo of <paramref name="builtin"/> in
    /// <paramref name="context"/>, with the strict function map without prototype.
    /// </summary>
    public static JSFunction AllocateRootFunctionWithContext(Isolate isolate, Builtin builtin, Context context,
        NativeContext nativeContext)
    {
        SharedFunctionInfo shared = Get(isolate, builtin);
        return isolate.Factory.NewFunction(shared, context, nativeContext.StrictFunctionWithoutPrototypeMap);
    }

    /// <summary>AllocateSyntheticFunctionContext: a function context of <paramref name="length"/> slots.</summary>
    public static Context AllocateSyntheticFunctionContext(Isolate isolate, NativeContext nativeContext, int length) =>
        isolate.Factory.NewBuiltinContext(nativeContext, length);
}

}

namespace V8Sharp
{
public sealed partial class Isolate
{
    /// <summary>The *_shared_fun roots (setup-heap-internal.cc), created on first use (RootSharedFunctions).</summary>
    internal readonly Dictionary<Builtin, SharedFunctionInfo> RootSharedFunctionInfos = [];
}
}
