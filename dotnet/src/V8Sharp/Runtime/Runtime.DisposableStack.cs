// Port of the disposable-stack runtime functions of src/runtime/runtime-scopes.cc
// (Runtime_InitializeDisposableStack, Runtime_AddDisposableValue,
// Runtime_AddAsyncDisposableValue, Runtime_DisposeDisposableStack,
// Runtime_HandleExceptionsInDisposeDisposableStack): the dispose capability
// of blocks with `using` and `await using` declarations.
using V8Sharp.Interpreter;

namespace V8Sharp.Runtime;

public static class RuntimeDisposableStack
{
    /// <summary>Runtime_InitializeDisposableStack.</summary>
    public static JSValue InitializeDisposableStack(Isolate isolate)
    {
        JSDisposableStackBase disposableStack = JSDisposableStackBase.NewJSDisposableStackBase(isolate);
        JSDisposableStackBase.InitializeJSDisposableStackBase(isolate, disposableStack);
        return disposableStack;
    }

    static void AddToDisposableStack(Isolate isolate, JSDisposableStackBase stack, JSValue value, DisposeMethodCallType type,
        DisposeMethodHint hint)
    {
        JSValue method = JSDisposableStackBase.CheckValueAndGetDisposeMethod(isolate, value, hint);
        // Return the DisposableResource Record { [[ResourceValue]]: V, [[Hint]]:
        // hint, [[DisposeMethod]]: method }.
        JSDisposableStackBase.Add(isolate, stack, value, method, type, hint);
    }

    /// <summary>Runtime_AddDisposableValue.</summary>
    public static JSValue AddDisposableValue(Isolate isolate, JSDisposableStackBase stack, JSValue value)
    {
        // a. If V is either null or undefined and hint is sync-dispose, return
        // unused.
        if (!value.IsNullOrUndefined)
        {
            AddToDisposableStack(isolate, stack, value, DisposeMethodCallType.kValueIsReceiver, DisposeMethodHint.kSyncDispose);
        }
        return value;
    }

    /// <summary>Runtime_AddAsyncDisposableValue.</summary>
    public static JSValue AddAsyncDisposableValue(Isolate isolate, JSDisposableStackBase stack, JSValue value)
    {
        // CreateDisposableResource
        // 1. If method is not present, then
        //   a. If V is either null or undefined, then
        //     i. Set V to undefined.
        //     ii. Set method to undefined.
        AddToDisposableStack(isolate, stack, value.IsNullOrUndefined ? JSValue.Undefined : value,
            DisposeMethodCallType.kValueIsReceiver, DisposeMethodHint.kAsyncDispose);
        return value;
    }

    /// <summary>Runtime_DisposeDisposableStack.</summary>
    public static JSValue DisposeDisposableStack(Isolate isolate, JSDisposableStackBase disposableStack, int continuationToken,
        JSValue continuationError, JSValue continuationMessage, int hasAwaitUsing)
    {
        // If state is not kDisposed, then the disposing of the resources has
        // not started yet. So, if the continuation token is kRethrow we need
        // to set error and error message on the disposable stack.
        if (disposableStack.State != DisposableStackState.kDisposed &&
            continuationToken == (int)TryFinallyContinuationToken.RethrowToken)
        {
            disposableStack.Error = continuationError;
            disposableStack.ErrorMessage = continuationMessage.HeapObjectOrNull as JSMessageObject;
        }

        disposableStack.State = DisposableStackState.kDisposed;
        return JSDisposableStackBase.DisposeResources(isolate, disposableStack, (DisposableStackResourcesType)hasAwaitUsing);
    }

    /// <summary>Runtime_HandleExceptionsInDisposeDisposableStack.</summary>
    public static JSValue HandleExceptionsInDisposeDisposableStack(Isolate isolate, JSDisposableStackBase disposableStack,
        JSValue exception, JSValue message)
    {
        // Uncatchable exceptions (termination) never reach a handler in V8Sharp,
        // so the is_catchable_by_javascript check always passes here.
        JSDisposableStackBase.HandleErrorInDisposal(isolate, disposableStack, exception, message.HeapObjectOrNull as JSMessageObject);
        return disposableStack;
    }
}

public static partial class RuntimeTable
{
    static void RegisterDisposableStack()
    {
        Register(FunctionId.InitializeDisposableStack, static (i, a) => RuntimeDisposableStack.InitializeDisposableStack(i));
        Register(FunctionId.AddDisposableValue,
            static (i, a) => RuntimeDisposableStack.AddDisposableValue(i, a[0].As<JSDisposableStackBase>(), a[1]));
        Register(FunctionId.AddAsyncDisposableValue,
            static (i, a) => RuntimeDisposableStack.AddAsyncDisposableValue(i, a[0].As<JSDisposableStackBase>(), a[1]));
        Register(FunctionId.DisposeDisposableStack,
            static (i, a) => RuntimeDisposableStack.DisposeDisposableStack(i, a[0].As<JSDisposableStackBase>(), (int)a[1].Number,
                a[2], a[3], (int)a[4].Number));
        Register(FunctionId.HandleExceptionsInDisposeDisposableStack,
            static (i, a) => RuntimeDisposableStack.HandleExceptionsInDisposeDisposableStack(i, a[0].As<JSDisposableStackBase>(),
                a[1], a[2]));
    }
}
