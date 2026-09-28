// Port of src/builtins/builtins-disposable-stack.cc and
// src/builtins/builtins-async-disposable-stack.cc: the DisposableStack and
// AsyncDisposableStack constructors and prototype methods, and the internal
// closures AsyncDisposableStackOnFulfilled / OnRejected and
// AsyncDisposeFromSyncDispose. The disposal algorithm itself is
// JSDisposableStackBase (Objects/JSDisposableStack.cs).
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterDisposableStack()
    {
        Register(Builtin.DisposableStackConstructor, DisposableStackBuiltins.DisposableStackConstructor);
        Register(Builtin.DisposableStackPrototypeUse, DisposableStackBuiltins.DisposableStackPrototypeUse);
        Register(Builtin.DisposableStackPrototypeDispose, DisposableStackBuiltins.DisposableStackPrototypeDispose);
        Register(Builtin.DisposableStackPrototypeGetDisposed, DisposableStackBuiltins.DisposableStackPrototypeGetDisposed);
        Register(Builtin.DisposableStackPrototypeAdopt, DisposableStackBuiltins.DisposableStackPrototypeAdopt);
        Register(Builtin.DisposableStackPrototypeDefer, DisposableStackBuiltins.DisposableStackPrototypeDefer);
        Register(Builtin.DisposableStackPrototypeMove, DisposableStackBuiltins.DisposableStackPrototypeMove);

        Register(Builtin.AsyncDisposableStackConstructor, DisposableStackBuiltins.AsyncDisposableStackConstructor);
        Register(Builtin.AsyncDisposableStackPrototypeUse, DisposableStackBuiltins.AsyncDisposableStackPrototypeUse);
        Register(Builtin.AsyncDisposableStackPrototypeDisposeAsync, DisposableStackBuiltins.AsyncDisposableStackPrototypeDisposeAsync);
        Register(Builtin.AsyncDisposableStackPrototypeGetDisposed, DisposableStackBuiltins.AsyncDisposableStackPrototypeGetDisposed);
        Register(Builtin.AsyncDisposableStackPrototypeAdopt, DisposableStackBuiltins.AsyncDisposableStackPrototypeAdopt);
        Register(Builtin.AsyncDisposableStackPrototypeDefer, DisposableStackBuiltins.AsyncDisposableStackPrototypeDefer);
        Register(Builtin.AsyncDisposableStackPrototypeMove, DisposableStackBuiltins.AsyncDisposableStackPrototypeMove);
        Register(Builtin.AsyncDisposableStackOnFulfilled, DisposableStackBuiltins.AsyncDisposableStackOnFulfilled);
        Register(Builtin.AsyncDisposableStackOnRejected, DisposableStackBuiltins.AsyncDisposableStackOnRejected);
        Register(Builtin.AsyncDisposeFromSyncDispose, DisposableStackBuiltins.AsyncDisposeFromSyncDispose);
    }
}

/// <summary>The DisposableStack and AsyncDisposableStack builtins.</summary>
public static class DisposableStackBuiltins
{
    static T CheckReceiver<T>(Isolate isolate, JSValue receiver, string methodName) where T : JSDisposableStackBase
    {
        if (receiver.HeapObjectOrNull is T stack) return stack;
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, isolate.Factory.NewStringFromAsciiChecked(methodName),
            receiver);
        return null!;
    }

    static void ThrowIfDisposed(Isolate isolate, JSDisposableStackBase stack, string methodName)
    {
        if (stack.State == DisposableStackState.kDisposed)
        {
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.DisposableStackIsDisposed,
                isolate.Factory.NewStringFromAsciiChecked(methodName)));
        }
    }

    // ---- DisposableStack (builtins-disposable-stack.cc) -------------------------------------------

    /// <summary>DisposableStack ( ).</summary>
    public static JSValue DisposableStackConstructor(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kExplicitResourceManagement");

        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction,
                isolate.Factory.NewStringFromAsciiChecked("DisposableStack"));
        }

        // 2. Let disposableStack be ? OrdinaryCreateFromConstructor(NewTarget,
        //    "%DisposableStack.prototype%", « [[DisposableState]],
        //    [[DisposeCapability]] »).
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        var disposableStack = (JSSyncDisposableStack)isolate.Factory.NewJSObjectFromMap(map);
        // 3. Set disposableStack.[[DisposableState]] to pending.
        // 4. Set disposableStack.[[DisposeCapability]] to NewDisposeCapability().
        JSDisposableStackBase.InitializeJSDisposableStackBase(isolate, disposableStack);
        // 5. Return disposableStack.
        return disposableStack;
    }

    /// <summary>DisposableStack.prototype.use ( value ).</summary>
    public static JSValue DisposableStackPrototypeUse(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "DisposableStack.prototype.use";
        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, kMethodName);
        JSValue value = args.AtOrUndefined(1);

        // 3. If disposableStack.[[DisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, disposableStack, kMethodName);

        // 4. Perform ? AddDisposableResource(disposableStack.[[DisposeCapability]],
        // value, sync-dispose).
        //    (a. If V is either null or undefined and hint is sync-dispose, then
        //       i. Return unused.)
        if (value.IsNullOrUndefined) return value;

        JSValue method = JSDisposableStackBase.CheckValueAndGetDisposeMethod(isolate, value, DisposeMethodHint.kSyncDispose);
        JSDisposableStackBase.Add(isolate, disposableStack, value, method, DisposeMethodCallType.kValueIsReceiver,
            DisposeMethodHint.kSyncDispose);

        // 5. Return value.
        return value;
    }

    /// <summary>DisposableStack.prototype.dispose ( ).</summary>
    public static JSValue DisposableStackPrototypeDispose(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, "DisposableStack.prototype.dispose");

        // 3. If disposableStack.[[DisposableState]] is disposed, return undefined.
        if (disposableStack.State == DisposableStackState.kDisposed) return JSValue.Undefined;

        // 4. Set disposableStack.[[DisposableState]] to disposed.
        disposableStack.State = DisposableStackState.kDisposed;

        // 5. Return ? DisposeResources(disposableStack.[[DisposeCapability]],
        //    NormalCompletion(undefined)).
        JSDisposableStackBase.DisposeResources(isolate, disposableStack, DisposableStackResourcesType.kAllSync);
        return JSValue.Undefined;
    }

    /// <summary>get DisposableStack.prototype.disposed.</summary>
    public static JSValue DisposableStackPrototypeGetDisposed(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, "get DisposableStack.prototype.disposed");
        // 3. If disposableStack.[[DisposableState]] is disposed, return true.
        // 4. Otherwise, return false.
        return JSValue.FromBoolean(disposableStack.State == DisposableStackState.kDisposed);
    }

    /// <summary>DisposableStack.prototype.adopt ( value, onDispose ).</summary>
    public static JSValue DisposableStackPrototypeAdopt(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "DisposableStack.prototype.adopt";
        JSValue value = args.AtOrUndefined(1);
        JSValue onDispose = args.AtOrUndefined(2);

        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If disposableStack.[[DisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, disposableStack, kMethodName);

        // 4. If IsCallable(onDispose) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(onDispose)) return isolate.ThrowTypeError(MessageTemplate.NotCallable, onDispose);

        // 5.-7. Instead of creating an abstract closure and a function, we pass
        // DisposeMethodCallType::kArgument so at the time of disposal, the value will
        // be passed as the argument to the method.
        JSDisposableStackBase.Add(isolate, disposableStack, value, onDispose, DisposeMethodCallType.kValueIsArgument,
            DisposeMethodHint.kSyncDispose);

        // 8. Return value.
        return value;
    }

    /// <summary>DisposableStack.prototype.defer ( onDispose ).</summary>
    public static JSValue DisposableStackPrototypeDefer(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "DisposableStack.prototype.defer";
        JSValue onDispose = args.AtOrUndefined(1);

        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If disposableStack.[[DisposableState]] is disposed, throw a
        // ReferenceError exception.
        ThrowIfDisposed(isolate, disposableStack, kMethodName);

        // 4. If IsCallable(onDispose) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(onDispose)) return isolate.ThrowTypeError(MessageTemplate.NotCallable, onDispose);

        // 5. Perform ? AddDisposableResource(disposableStack.[[DisposeCapability]],
        // undefined, sync-dispose, onDispose).
        JSDisposableStackBase.Add(isolate, disposableStack, JSValue.Undefined, onDispose, DisposeMethodCallType.kValueIsReceiver,
            DisposeMethodHint.kSyncDispose);

        // 6. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>DisposableStack.prototype.move ( ).</summary>
    public static JSValue DisposableStackPrototypeMove(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "DisposableStack.prototype.move";
        // 1. Let disposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(disposableStack, [[DisposableState]]).
        var disposableStack = CheckReceiver<JSSyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If disposableStack.[[DisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, disposableStack, kMethodName);

        // 4. Let newDisposableStack be ?
        //    OrdinaryCreateFromConstructor(%DisposableStack%,
        //    "%DisposableStack.prototype%", « [[DisposableState]],
        //     [[DisposeCapability]] »).
        // 5. Set newDisposableStack.[[DisposableState]] to pending.
        Map map = isolate.NativeContext.JSDisposableStackFunction.InitialMap;
        var newDisposableStack = (JSSyncDisposableStack)isolate.Factory.NewJSObjectFromMap(map);
        MoveDisposeCapability(disposableStack, newDisposableStack);

        // 9. Return newDisposableStack.
        return newDisposableStack;
    }

    /// <summary>
    /// Steps 6-8 of DisposableStack.prototype.move / AsyncDisposableStack.prototype.move:
    /// the new stack takes the dispose capability; the old one gets a new one and is disposed.
    /// </summary>
    static void MoveDisposeCapability(JSDisposableStackBase from, JSDisposableStackBase to)
    {
        // 6. Set newDisposableStack.[[DisposeCapability]] to
        //    disposableStack.[[DisposeCapability]].
        to.Stack = from.Stack;
        to.Length = from.Length;
        to.State = DisposableStackState.kPending;
        to.Error = JSValue.FromObject(Oddball.Uninitialized);
        to.ErrorMessage = null;

        // 7. Set disposableStack.[[DisposeCapability]] to NewDisposeCapability().
        from.Stack = FixedArray.Empty;
        from.Length = 0;
        from.Error = JSValue.FromObject(Oddball.Uninitialized);
        from.ErrorMessage = null;

        // 8. Set disposableStack.[[DisposableState]] to disposed.
        from.State = DisposableStackState.kDisposed;
    }

    // ---- AsyncDisposableStack (builtins-async-disposable-stack.cc) --------------------------------

    /// <summary>AsyncDisposableStackOnFulfilled: continues the disposal after an awaited dispose.</summary>
    public static JSValue AsyncDisposableStackOnFulfilled(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        var stack = context[JSDisposableStackBase.kAsyncDisposableStackContextStackSlot].As<JSDisposableStackBase>();
        var promise = context[JSDisposableStackBase.kAsyncDisposableStackContextOuterPromiseSlot].As<JSPromise>();
        JSAsyncDisposableStack.NextDisposeAsyncIteration(isolate, stack, promise);
        return JSValue.Undefined;
    }

    /// <summary>AsyncDisposableStackOnRejected: records the rejection and continues the disposal.</summary>
    public static JSValue AsyncDisposableStackOnRejected(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        var stack = context[JSDisposableStackBase.kAsyncDisposableStackContextStackSlot].As<JSDisposableStackBase>();
        var promise = context[JSDisposableStackBase.kAsyncDisposableStackContextOuterPromiseSlot].As<JSPromise>();

        JSValue rejectionError = args.AtOrUndefined(1);
        // (TODO:rezvan): Pass the correct pending message.
        JSDisposableStackBase.HandleErrorInDisposal(isolate, stack, rejectionError, null);

        JSAsyncDisposableStack.NextDisposeAsyncIteration(isolate, stack, promise);
        return JSValue.Undefined;
    }

    /// <summary>
    /// AsyncDisposeFromSyncDispose: the closure GetDisposeMethod wraps a sync
    /// @@dispose in for async disposal (#sec-getdisposemethod).
    /// </summary>
    public static JSValue AsyncDisposeFromSyncDispose(Isolate isolate, in BuiltinArguments args)
    {
        //        a. Let O be the this value.
        //        b. Let promiseCapability be ! NewPromiseCapability(%Promise%).
        JSValue receiver = args.Receiver;
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);

        //        c. Let result be Completion(Call(method, O)).
        JSValue syncMethod = args.Target.Context[JSDisposableStackBase.kAsyncDisposeFromSyncDisposeContextMethodSlot];
        try
        {
            Execution.Call(isolate, syncMethod, receiver, []);
        }
        catch (JavaScriptException e)
        {
            //        d. IfAbruptRejectPromise(result, promiseCapability).
            JSPromise.Reject(isolate, promise, e.Value);
            return promise;
        }

        //        e. Perform ? Call(promiseCapability.[[Resolve]], undefined, «
        //        undefined »).
        JSPromise.Resolve(isolate, promise, JSValue.Undefined);

        //        f. Return promiseCapability.[[Promise]].
        return promise;
    }

    /// <summary>AsyncDisposableStack ( ).</summary>
    public static JSValue AsyncDisposableStackConstructor(Isolate isolate, in BuiltinArguments args)
    {
        isolate.CountUsage("kExplicitResourceManagement");

        // 1. If NewTarget is undefined, throw a TypeError exception.
        if (!args.NewTarget.IsJSReceiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction,
                isolate.Factory.NewStringFromAsciiChecked("AsyncDisposableStack"));
        }

        // 2. Let asyncDisposableStack be ? OrdinaryCreateFromConstructor(NewTarget,
        //    "%AsyncDisposableStack.prototype%", « [[AsyncDisposableState]],
        //    [[DisposeCapability]] »).
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        var asyncDisposableStack = (JSAsyncDisposableStack)isolate.Factory.NewJSObjectFromMap(map);
        // 3. Set asyncDisposableStack.[[AsyncDisposableState]] to pending.
        // 4. Set asyncDisposableStack.[[DisposeCapability]] to
        // NewDisposeCapability().
        JSDisposableStackBase.InitializeJSDisposableStackBase(isolate, asyncDisposableStack);
        // 5. Return asyncDisposableStack.
        return asyncDisposableStack;
    }

    /// <summary>AsyncDisposableStack.prototype.use ( value ).</summary>
    public static JSValue AsyncDisposableStackPrototypeUse(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "AsyncDisposableStack.prototype.use";
        // 1. Let asyncDisposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(asyncDisposableStack,
        // [[AsyncDisposableState]]).
        var asyncDisposableStack = CheckReceiver<JSAsyncDisposableStack>(isolate, args.Receiver, kMethodName);
        JSValue value = args.AtOrUndefined(1);

        // 3. If asyncDisposableStack.[[AsyncDisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, asyncDisposableStack, kMethodName);

        // 4. Perform ?
        // AddDisposableResource(asyncDisposableStack.[[DisposeCapability]],
        // value, async-dispose).
        JSValue method = JSDisposableStackBase.CheckValueAndGetDisposeMethod(isolate, value, DisposeMethodHint.kAsyncDispose);
        JSDisposableStackBase.Add(isolate, asyncDisposableStack, value.IsNullOrUndefined ? JSValue.Undefined : value, method,
            DisposeMethodCallType.kValueIsReceiver, DisposeMethodHint.kAsyncDispose);

        // 5. Return value.
        return value;
    }

    /// <summary>AsyncDisposableStack.prototype.disposeAsync ( ).</summary>
    public static JSValue AsyncDisposableStackPrototypeDisposeAsync(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let asyncDisposableStack be the this value.
        JSValue receiver = args.Receiver;

        // 2. Let promiseCapability be ! NewPromiseCapability(%Promise%).
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);

        // 3. If asyncDisposableStack does not have an [[AsyncDisposableState]]
        // internal slot, then
        if (receiver.HeapObjectOrNull is not JSAsyncDisposableStack asyncDisposableStack)
        {
            //    a. Perform ! Call(promiseCapability.[[Reject]], undefined, « a newly
            //    created TypeError object »).
            JSPromise.Reject(isolate, promise, isolate.Factory.NewTypeError(MessageTemplate.NotAnAsyncDisposableStack));
            //   b. Return promiseCapability.[[Promise]].
            return promise;
        }

        // 4. If asyncDisposableStack.[[AsyncDisposableState]] is disposed, then
        if (asyncDisposableStack.State == DisposableStackState.kDisposed)
        {
            //    a. Perform ! Call(promiseCapability.[[Resolve]], undefined, «
            //    undefined »).
            JSPromise.Resolve(isolate, promise, JSValue.Undefined);
            //    b. Return promiseCapability.[[Promise]].
            return promise;
        }

        // 5. Set asyncDisposableStack.[[AsyncDisposableState]] to disposed.
        asyncDisposableStack.State = DisposableStackState.kDisposed;

        // 6. Let result be
        //   DisposeResources(asyncDisposableStack.[[DisposeCapability]],
        //   NormalCompletion(undefined)).
        // 7. IfAbruptRejectPromise(result, promiseCapability).
        // 8. Perform ! Call(promiseCapability.[[Resolve]], undefined, « result
        // »).
        // 9. Return promiseCapability.[[Promise]].
        JSAsyncDisposableStack.NextDisposeAsyncIteration(isolate, asyncDisposableStack, promise);
        return promise;
    }

    /// <summary>get AsyncDisposableStack.prototype.disposed.</summary>
    public static JSValue AsyncDisposableStackPrototypeGetDisposed(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let AsyncdisposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(asyncDisposableStack,
        // [[AsyncDisposableState]]).
        var asyncDisposableStack = CheckReceiver<JSAsyncDisposableStack>(isolate, args.Receiver,
            "get AsyncDisposableStack.prototype.disposed");
        // 3. If AsyncdisposableStack.[[AsyncDisposableState]] is disposed, return
        // true.
        // 4. Otherwise, return false.
        return JSValue.FromBoolean(asyncDisposableStack.State == DisposableStackState.kDisposed);
    }

    /// <summary>AsyncDisposableStack.prototype.adopt ( value, onDisposeAsync ).</summary>
    public static JSValue AsyncDisposableStackPrototypeAdopt(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "AsyncDisposableStack.prototype.adopt";
        JSValue value = args.AtOrUndefined(1);
        JSValue onDisposeAsync = args.AtOrUndefined(2);

        // 1. Let asyncDisposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(asyncDisposableStack,
        // [[AsyncDisposableState]]).
        var asyncDisposableStack = CheckReceiver<JSAsyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If asyncDisposableStack.[[AsyncDisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, asyncDisposableStack, kMethodName);

        // 4. If IsCallable(onDisposeAsync) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(onDisposeAsync)) return isolate.ThrowTypeError(MessageTemplate.NotCallable, onDisposeAsync);

        // 5.-7. Instead of creating an abstract closure and a function, we pass
        // DisposeMethodCallType::kArgument so at the time of disposal, the value will
        // be passed as the argument to the method.
        JSDisposableStackBase.Add(isolate, asyncDisposableStack, value, onDisposeAsync, DisposeMethodCallType.kValueIsArgument,
            DisposeMethodHint.kAsyncDispose);

        // 8. Return value.
        return value;
    }

    /// <summary>AsyncDisposableStack.prototype.defer ( onDisposeAsync ).</summary>
    public static JSValue AsyncDisposableStackPrototypeDefer(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "AsyncDisposableStack.prototype.defer";
        JSValue onDisposeAsync = args.AtOrUndefined(1);

        // 1. Let asyncDisposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(asyncDisposableStack,
        // [[AsyncDisposableState]]).
        var asyncDisposableStack = CheckReceiver<JSAsyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If asyncDisposableStack.[[AsyncDisposableState]] is disposed, throw a
        // ReferenceError exception.
        ThrowIfDisposed(isolate, asyncDisposableStack, kMethodName);

        // 4. If IsCallable(onDisposeAsync) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(onDisposeAsync)) return isolate.ThrowTypeError(MessageTemplate.NotCallable, onDisposeAsync);

        // 5. Perform ?
        // AddDisposableResource(asyncDisposableStack.[[DisposeCapability]],
        // undefined, async-dispose, onDisposeAsync).
        JSDisposableStackBase.Add(isolate, asyncDisposableStack, JSValue.Undefined, onDisposeAsync,
            DisposeMethodCallType.kValueIsReceiver, DisposeMethodHint.kAsyncDispose);

        // 6. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>AsyncDisposableStack.prototype.move ( ).</summary>
    public static JSValue AsyncDisposableStackPrototypeMove(Isolate isolate, in BuiltinArguments args)
    {
        const string kMethodName = "AsyncDisposableStack.prototype.move";
        // 1. Let asyncDisposableStack be the this value.
        // 2. Perform ? RequireInternalSlot(asyncDisposableStack,
        // [[AsyncDisposableState]]).
        var asyncDisposableStack = CheckReceiver<JSAsyncDisposableStack>(isolate, args.Receiver, kMethodName);

        // 3. If asyncDisposableStack.[[AsyncDisposableState]] is disposed, throw a
        //    ReferenceError exception.
        ThrowIfDisposed(isolate, asyncDisposableStack, kMethodName);

        // 4. Let newAsyncDisposableStack be ?
        //    OrdinaryCreateFromConstructor(%AsyncDisposableStack%,
        //    "%AsyncDisposableStack.prototype%", « [[AsyncDisposableState]],
        //     [[DisposeCapability]] »).
        // 5. Set newAsyncDisposableStack.[[AsyncDisposableState]] to pending.
        Map map = isolate.NativeContext.JSAsyncDisposableStackFunction.InitialMap;
        var newAsyncDisposableStack = (JSAsyncDisposableStack)isolate.Factory.NewJSObjectFromMap(map);
        MoveDisposeCapability(asyncDisposableStack, newAsyncDisposableStack);

        // 9. Return newDisposableStack.
        return newAsyncDisposableStack;
    }
}
