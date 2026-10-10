// Port of src/objects/js-disposable-stack.{h,cc}, js-disposable-stack-inl.h
// and js-disposable-stack.tq: JSDisposableStackBase (with JSSyncDisposableStack
// and JSAsyncDisposableStack), Add, CheckValueAndGetDisposeMethod,
// HandleErrorInDisposal, DisposeResources, ResolveAPromiseWithValueAndReturnIt
// and JSAsyncDisposableStack::NextDisposeAsyncIteration, together with
// JSDisposableStackBase::InitializeJSDisposableStackBase (objects.cc) and
// Factory::NewSuppressedErrorAtDisposal.
//
// These are also the runtime of `using` / `await using` declarations: the
// interpreter creates a JSDisposableStackBase with the js_disposable_stack_map.
using V8Sharp.Builtins;

namespace V8Sharp.Objects;

/// <summary>DisposableStackState.</summary>
public enum DisposableStackState { kDisposed, kPending }

/// <summary>DisposeMethodCallType.</summary>
public enum DisposeMethodCallType { kValueIsReceiver = 0, kValueIsArgument = 1 }

/// <summary>DisposeMethodHint.</summary>
public enum DisposeMethodHint { kSyncDispose = 0, kAsyncDispose = 1 }

/// <summary>DisposableStackResourcesType.</summary>
public enum DisposableStackResourcesType { kAllSync, kAtLeastOneAsync }

/// <summary>
/// V8's JSDisposableStackBase: the [[DisposeCapability]] of a DisposableStack,
/// an AsyncDisposableStack or a block with using declarations. The stack
/// holds triples [value, method, (call type | hint &lt;&lt; 1)].
/// </summary>
public class JSDisposableStackBase(Map map) : JSObject(map)
{
    /// <summary>AsyncDisposableStackContextSlots.</summary>
    public const int kAsyncDisposableStackContextStackSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    public const int kAsyncDisposableStackContextOuterPromiseSlot = kAsyncDisposableStackContextStackSlot + 1;
    public const int kAsyncDisposableStackContextLength = kAsyncDisposableStackContextOuterPromiseSlot + 1;

    /// <summary>AsyncDisposeFromSyncDisposeContextSlots.</summary>
    public const int kAsyncDisposeFromSyncDisposeContextMethodSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    public const int kAsyncDisposeFromSyncDisposeContextLength = kAsyncDisposeFromSyncDisposeContextMethodSlot + 1;

    public FixedArray Stack = FixedArray.Empty;
    public DisposableStackState State = DisposableStackState.kPending;
    public bool NeedsAwait;
    public bool HasAwaited;
    public bool SuppressedErrorCreated;
    public int Length;
    /// <summary>The completion so far: the uninitialized hole when normal.</summary>
    public JSValue Error = JSValue.FromObject(Oddball.Uninitialized);
    /// <summary>The message of <see cref="Error"/> (V8's error_message), or null.</summary>
    public JSMessageObject? ErrorMessage;

    static bool IsUninitializedHole(JSValue value) => ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized);

    /// <summary>JSDisposableStackBase::InitializeJSDisposableStackBase.</summary>
    public static void InitializeJSDisposableStackBase(Isolate isolate, JSDisposableStackBase disposableStack)
    {
        disposableStack.Stack = FixedArray.Empty;
        disposableStack.NeedsAwait = false;
        disposableStack.HasAwaited = false;
        disposableStack.SuppressedErrorCreated = false;
        disposableStack.Length = 0;
        disposableStack.State = DisposableStackState.kPending;
        disposableStack.Error = JSValue.FromObject(Oddball.Uninitialized);
        disposableStack.ErrorMessage = null;
    }

    /// <summary>Factory::NewJSDisposableStackBase: the stack of a block with using declarations.</summary>
    public static JSDisposableStackBase NewJSDisposableStackBase(Isolate isolate)
    {
        Map map = isolate.NativeContext.JSDisposableStackMap;
        return (JSDisposableStackBase)isolate.Factory.NewJSObjectFromMap(map);
    }

    /// <summary>JSDisposableStackBase::Add.</summary>
    public static void Add(Isolate isolate, JSDisposableStackBase disposableStack, JSValue value, JSValue method,
        DisposeMethodCallType type, DisposeMethodHint hint)
    {
        int length = disposableStack.Length;
        int stackType = (int)type | ((int)hint << 1);

        FixedArray array = disposableStack.Stack;
        array = FixedArray.SetAndGrow(array, length++, value);
        array = FixedArray.SetAndGrow(array, length++, method);
        array = FixedArray.SetAndGrow(array, length++, JSValue.FromInt(stackType));

        disposableStack.Length = length;
        disposableStack.Stack = array;
    }

    /// <summary>
    /// JSDisposableStackBase::CheckValueAndGetDisposeMethod (GetDisposeMethod):
    /// the dispose method of <paramref name="value"/>; for async-dispose, a
    /// null or undefined value yields undefined and a sync @@dispose is wrapped
    /// in AsyncDisposeFromSyncDispose.
    /// </summary>
    public static JSValue CheckValueAndGetDisposeMethod(Isolate isolate, JSValue value, DisposeMethodHint hint)
    {
        JSValue method;
        if (hint == DisposeMethodHint.kSyncDispose)
        {
            // We has already returned from the caller if V is null or undefined, when
            // hint is `kSyncDispose`.
            Debug.Assert(!value.IsNullOrUndefined);

            //   b. Else,
            //    i. If V is not an Object, throw a TypeError exception.
            if (!value.IsJSReceiver) isolate.ThrowTypeError(MessageTemplate.ExpectAnObjectWithUsing);

            //   ii. Set method to ? GetDisposeMethod(V, hint).
            method = ObjectOps.GetProperty(isolate, value, ReadOnlyRoots.dispose_symbol);
            //   (GetMethod)3. If IsCallable(func) is false, throw a TypeError
            //   exception.
            if (!ObjectOps.IsCallable(method)) isolate.ThrowTypeError(MessageTemplate.NotCallable, ReadOnlyRoots.dispose_symbol);
        }
        else
        {
            //   a. If V is either null or undefined, then
            //    i. Set V to undefined.
            //    ii. Set method to undefined.
            if (value.IsNullOrUndefined) return JSValue.Undefined;

            //   b. Else,
            //    i. If V is not an Object, throw a TypeError exception.
            if (!value.IsJSReceiver) isolate.ThrowTypeError(MessageTemplate.ExpectAnObjectWithUsing);

            // 1. If hint is async-dispose, then
            //   a. Let method be ? GetMethod(V, @@asyncDispose).
            method = ObjectOps.GetProperty(isolate, value, ReadOnlyRoots.async_dispose_symbol);
            //   b. If method is undefined, then
            if (method.IsNullOrUndefined)
            {
                //    i. Set method to ? GetMethod(V, @@dispose).
                method = ObjectOps.GetProperty(isolate, value, ReadOnlyRoots.dispose_symbol);
                //   (GetMethod)3. If IsCallable(func) is false, throw a TypeError
                //   exception.
                if (!ObjectOps.IsCallable(method)) isolate.ThrowTypeError(MessageTemplate.NotCallable, ReadOnlyRoots.dispose_symbol);
                //    ii. If method is not undefined, then
                //      1. Let closure be a new Abstract Closure with no parameters that
                //      captures method and performs the following steps when called: ...
                //      3. Return CreateBuiltinFunction(closure, 0, "", « »).
                NativeContext nativeContext = isolate.NativeContext;
                Context closureContext = isolate.Factory.NewBuiltinContext(nativeContext,
                    kAsyncDisposeFromSyncDisposeContextLength);
                closureContext[kAsyncDisposeFromSyncDisposeContextMethodSlot] = method;
                SharedFunctionInfo shared = RootSharedFunctions.Get(isolate, Builtin.AsyncDisposeFromSyncDispose);
                method = isolate.Factory.NewFunction(shared, closureContext);
            }
            //   (GetMethod)3. If IsCallable(func) is false, throw a TypeError
            //   exception.
            if (!ObjectOps.IsCallable(method)) isolate.ThrowTypeError(MessageTemplate.NotCallable, ReadOnlyRoots.async_dispose_symbol);
        }
        return method;
    }

    /// <summary>Factory::NewSuppressedErrorAtDisposal.</summary>
    public static JSObject NewSuppressedErrorAtDisposal(Isolate isolate, JSValue error, JSValue suppressedError)
    {
        JSObject err = isolate.Factory.NewSuppressedError(MessageTemplate.SuppressedErrorDuringDisposal);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, err, ReadOnlyRoots.error_string, error, PropertyAttributes.DONT_ENUM);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, err, ReadOnlyRoots.suppressed_string, suppressedError,
            PropertyAttributes.DONT_ENUM);
        return err;
    }

    /// <summary>JSDisposableStackBase::HandleErrorInDisposal.</summary>
    public static void HandleErrorInDisposal(Isolate isolate, JSDisposableStackBase disposableStack, JSValue currentError,
        JSMessageObject? currentErrorMessage)
    {
        JSValue maybeError = disposableStack.Error;

        //   i. If completion is a throw completion, then
        if (!IsUninitializedHole(maybeError))
        {
            //    1. Set result to result.[[Value]].
            //    2. Let suppressed be completion.[[Value]].
            //    3. Let error be a newly created SuppressedError object.
            //    4. Perform CreateNonEnumerableDataPropertyOrThrow(error, "error",
            //    result).
            //    5. Perform CreateNonEnumerableDataPropertyOrThrow(error,
            //    "suppressed", suppressed).
            //    6. Set completion to ThrowCompletion(error).
            maybeError = NewSuppressedErrorAtDisposal(isolate, currentError, maybeError);
            disposableStack.SuppressedErrorCreated = true;
        }
        else
        {
            //   ii. Else,
            //    1. Set completion to result.
            maybeError = currentError;
        }

        disposableStack.Error = maybeError;
        disposableStack.ErrorMessage = currentErrorMessage;
    }

    /// <summary>
    /// JSDisposableStackBase::DisposeResources (#sec-disposeresources): calls
    /// the dispose methods in reverse order. Returns true when done, or the
    /// promise to await before continuing (async disposal); throws the final
    /// completion if it is a throw completion.
    /// </summary>
    public static JSValue DisposeResources(Isolate isolate, JSDisposableStackBase disposableStack,
        DisposableStackResourcesType resourcesType)
    {
        Debug.Assert(disposableStack.State == DisposableStackState.kDisposed);

        FixedArray stack = disposableStack.Stack;

        // 1. Let needsAwait be false.
        // 2. Let hasAwaited be false.
        // `false` is assigned to both in the initialization of the DisposableStack.

        int length = disposableStack.Length;

        // 3. For each element resource of
        // disposeCapability.[[DisposableResourceStack]], in reverse list order, do
        while (length > 0)
        {
            //  a. Let value be resource.[[ResourceValue]].
            //  b. Let hint be resource.[[Hint]].
            //  c. Let method be resource.[[DisposeMethod]].
            int stackTypeCase = (int)stack[--length].Number;
            JSValue method = stack[--length];
            JSValue value = stack[--length];

            var callType = (DisposeMethodCallType)(stackTypeCase & 1);
            var hint = (DisposeMethodHint)((stackTypeCase >> 1) & 1);

            //  d. If hint is sync-dispose and needsAwait is true and hasAwaited is
            //  false, then
            //    i. Perform ! Await(undefined).
            //    ii. Set needsAwait to false.
            if (hint == DisposeMethodHint.kSyncDispose && disposableStack.NeedsAwait && !disposableStack.HasAwaited)
            {
                disposableStack.NeedsAwait = false;
                return ResolveAPromiseWithValueAndReturnIt(isolate, JSValue.Undefined);
            }

            //  e. If method is not undefined, then
            if (!method.IsUndefined)
            {
                //    i. Let result be Completion(Call(method, value)).
                JSValue result;
                try
                {
                    result = callType == DisposeMethodCallType.kValueIsReceiver
                        ? Execution.Call(isolate, method, value, [])
                        : Execution.Call(isolate, method, JSValue.Undefined, [value]);
                }
                catch (JavaScriptException e)
                {
                    HandleErrorInDisposal(isolate, disposableStack, e.Value, e.MessageObject);
                    continue;
                }

                //    ii. If result is a normal completion and hint is async-dispose, then
                //      1. Set result to Completion(Await(result.[[Value]])).
                //      2. Set hasAwaited to true.
                if (hint == DisposeMethodHint.kAsyncDispose)
                {
                    Debug.Assert(resourcesType != DisposableStackResourcesType.kAllSync);
                    disposableStack.Length = length;
                    disposableStack.HasAwaited = true;
                    try
                    {
                        return ResolveAPromiseWithValueAndReturnIt(isolate, result);
                    }
                    catch (JavaScriptException e)
                    {
                        //    iii. If result is a throw completion, then ...
                        HandleErrorInDisposal(isolate, disposableStack, e.Value, e.MessageObject);
                    }
                }
            }
            else
            {
                //  Else,
                //    i. Assert: hint is async-dispose.
                Debug.Assert(hint == DisposeMethodHint.kAsyncDispose);
                //    ii. Set needsAwait to true.
                //    iii. NOTE: This can only indicate a case where either null or
                //    undefined was the initialized value of an await using declaration.
                disposableStack.Length = length;
                disposableStack.NeedsAwait = true;
            }
        }

        // 4. If needsAwait is true and hasAwaited is false, then
        //   a. Perform ! Await(undefined).
        if (disposableStack.NeedsAwait && !disposableStack.HasAwaited)
        {
            disposableStack.Length = length;
            disposableStack.HasAwaited = true;
            return ResolveAPromiseWithValueAndReturnIt(isolate, JSValue.Undefined);
        }

        // 5. NOTE: After disposeCapability has been disposed, it will never be used
        // again. The contents of disposeCapability.[[DisposableResourceStack]] can be
        // discarded in implementations, such as by garbage collection, at this point.
        // 6. Set disposeCapability.[[DisposableResourceStack]] to a new empty List.
        disposableStack.Stack = FixedArray.Empty;
        disposableStack.Length = 0;

        JSValue existingError = disposableStack.Error;
        JSMessageObject? existingErrorMessage = disposableStack.ErrorMessage;
        disposableStack.Error = JSValue.FromObject(Oddball.Uninitialized);
        disposableStack.ErrorMessage = null;

        // 7. Return ? completion.
        if (!IsUninitializedHole(existingError))
        {
            if (disposableStack.SuppressedErrorCreated)
            {
                isolate.Throw(existingError);
            }
            else
            {
                isolate.ReThrow(existingError, existingErrorMessage);
            }
        }
        return JSValue.True;
    }

    /// <summary>JSDisposableStackBase::ResolveAPromiseWithValueAndReturnIt: Promise.resolve(value).</summary>
    public static JSValue ResolveAPromiseWithValueAndReturnIt(Isolate isolate, JSValue value)
    {
        NativeContext nativeContext = isolate.NativeContext;
        return Execution.CallBuiltin(isolate, nativeContext.PromiseResolve, nativeContext.PromiseFunction, [value]);
    }
}

/// <summary>V8's JSSyncDisposableStack (DisposableStack).</summary>
public sealed class JSSyncDisposableStack(Map map) : JSDisposableStackBase(map);

/// <summary>V8's JSAsyncDisposableStack (AsyncDisposableStack).</summary>
public sealed class JSAsyncDisposableStack(Map map) : JSDisposableStackBase(map)
{
    /// <summary>
    /// JSAsyncDisposableStack::NextDisposeAsyncIteration: runs DisposeResources
    /// until it needs to await, then chains AsyncDisposableStackOnFulfilled /
    /// OnRejected on the awaited promise; settles <paramref name="outerPromise"/>
    /// at the end.
    /// </summary>
    public static void NextDisposeAsyncIteration(Isolate isolate, JSDisposableStackBase asyncDisposableStack,
        JSPromise outerPromise)
    {
        bool done;
        do
        {
            done = true;

            // 6. Let result be
            //   DisposeResources(asyncDisposableStack.[[DisposeCapability]],
            //   NormalCompletion(undefined)).
            JSValue result;
            try
            {
                result = DisposeResources(isolate, asyncDisposableStack, DisposableStackResourcesType.kAtLeastOneAsync);
            }
            catch (JavaScriptException e)
            {
                // 7. IfAbruptRejectPromise(result, promiseCapability).
                JSPromise.Reject(isolate, outerPromise, e.Value);
                return;
            }

            if (!result.IsTrue)
            {
                NativeContext nativeContext = isolate.NativeContext;
                Context asyncDisposableStackContext = isolate.Factory.NewBuiltinContext(nativeContext,
                    kAsyncDisposableStackContextLength);
                asyncDisposableStackContext[kAsyncDisposableStackContextStackSlot] = asyncDisposableStack;
                asyncDisposableStackContext[kAsyncDisposableStackContextOuterPromiseSlot] = outerPromise;

                JSFunction onFulfilled = isolate.Factory.NewFunction(
                    RootSharedFunctions.Get(isolate, Builtin.AsyncDisposableStackOnFulfilled), asyncDisposableStackContext);
                JSFunction onRejected = isolate.Factory.NewFunction(
                    RootSharedFunctions.Get(isolate, Builtin.AsyncDisposableStackOnRejected), asyncDisposableStackContext);

                try
                {
                    Execution.CallBuiltin(isolate, nativeContext.PerformPromiseThen, result, [onFulfilled, onRejected]);
                }
                catch (JavaScriptException e)
                {
                    HandleErrorInDisposal(isolate, asyncDisposableStack, e.Value, e.MessageObject);
                    done = false;
                }
            }
            else
            {
                // 8. Perform ! Call(promiseCapability.[[Resolve]], undefined, « result »).
                try
                {
                    JSPromise.Resolve(isolate, outerPromise, JSValue.Undefined);
                }
                catch (JavaScriptException e)
                {
                    HandleErrorInDisposal(isolate, asyncDisposableStack, e.Value, e.MessageObject);
                    done = false;
                }
            }
        } while (!done);
    }
}
