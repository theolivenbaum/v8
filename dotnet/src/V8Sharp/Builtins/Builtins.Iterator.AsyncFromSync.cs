// Port of src/builtins/builtins-async-iterator-gen.cc (%AsyncFromSyncIteratorPrototype%),
// the unwrap closure of src/builtins/builtins-async-gen.cc (CreateUnwrapClosure,
// AsyncIteratorValueUnwrap) and CodeStubAssembler::CreateAsyncFromSyncIterator
// (src/codegen/code-stub-assembler.cc).
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterAsyncFromSyncIterator()
    {
        Register(Builtin.AsyncFromSyncIteratorPrototypeNext, AsyncFromSyncIteratorBuiltins.AsyncFromSyncIteratorPrototypeNext);
        Register(Builtin.AsyncFromSyncIteratorPrototypeReturn, AsyncFromSyncIteratorBuiltins.AsyncFromSyncIteratorPrototypeReturn);
        Register(Builtin.AsyncFromSyncIteratorPrototypeThrow, AsyncFromSyncIteratorBuiltins.AsyncFromSyncIteratorPrototypeThrow);
        Register(Builtin.AsyncFromSyncIteratorCloseSyncAndRethrow,
            AsyncFromSyncIteratorBuiltins.AsyncFromSyncIteratorCloseSyncAndRethrow);
        Register(Builtin.AsyncIteratorValueUnwrap, AsyncFromSyncIteratorBuiltins.AsyncIteratorValueUnwrap);
    }
}

public static class AsyncFromSyncIteratorBuiltins
{
    /// <summary>ValueUnwrapContext (builtins-async-gen.h).</summary>
    const int kDoneSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    const int kValueUnwrapContextLength = kDoneSlot + 1;

    /// <summary>AsyncFromSyncIteratorCloseSyncAndRethrowContext.</summary>
    const int kSyncIteratorSlot = (int)Context.Field.MIN_CONTEXT_SLOTS;
    const int kCloseSyncAndRethrowContextLength = kSyncIteratorSlot + 1;

    enum SyncMethod { Next, Return, Throw }

    enum CloseOnRejectionOption { kDoNotCloseOnRejection, kCloseOnRejection }

    /// <summary>
    /// CodeStubAssembler::CreateAsyncFromSyncIterator(context, sync_iterator):
    /// throws for a non-receiver, otherwise loads "next" and wraps.
    /// </summary>
    public static JSObject CreateAsyncFromSyncIterator(Isolate isolate, JSValue syncIterator)
    {
        if (!syncIterator.IsJSReceiver)
        {
            // Runtime_ThrowSymbolIteratorInvalid.
            isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);
        }
        JSValue next = ObjectOps.GetProperty(isolate, syncIterator, ReadOnlyRoots.next_string);
        return CreateAsyncFromSyncIterator(isolate, syncIterator.As<JSReceiver>(), next);
    }

    /// <summary>CodeStubAssembler::CreateAsyncFromSyncIterator(context, sync_iterator, next).</summary>
    public static JSObject CreateAsyncFromSyncIterator(Isolate isolate, JSReceiver syncIterator, JSValue next)
    {
        var iterator = (JSAsyncFromSyncIterator)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.AsyncFromSyncIteratorMap);
        iterator.SyncIterator = syncIterator;
        iterator.Next = next;
        return iterator;
    }

    /// <summary>
    /// iterator.tq GetIteratorRecordAfterCreateAsyncFromSyncIterator: wraps a
    /// sync iterator record into an async one.
    /// </summary>
    public static IteratorRecord GetIteratorRecordAfterCreateAsyncFromSyncIterator(Isolate isolate, in IteratorRecord asyncIterator)
    {
        JSObject iterator = CreateAsyncFromSyncIterator(isolate, asyncIterator.Object);
        JSValue nextMethod = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.next_string);
        return new IteratorRecord(iterator, nextMethod);
    }

    /// <summary>%AsyncFromSyncIteratorPrototype%.next.</summary>
    public static JSValue AsyncFromSyncIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args) =>
        GenerateAsyncFromSyncIteratorMethod(isolate, args, SyncMethod.Next, CloseOnRejectionOption.kCloseOnRejection);

    /// <summary>%AsyncFromSyncIteratorPrototype%.return.</summary>
    public static JSValue AsyncFromSyncIteratorPrototypeReturn(Isolate isolate, in BuiltinArguments args) =>
        GenerateAsyncFromSyncIteratorMethod(isolate, args, SyncMethod.Return, CloseOnRejectionOption.kDoNotCloseOnRejection);

    /// <summary>%AsyncFromSyncIteratorPrototype%.throw.</summary>
    public static JSValue AsyncFromSyncIteratorPrototypeThrow(Isolate isolate, in BuiltinArguments args) =>
        GenerateAsyncFromSyncIteratorMethod(isolate, args, SyncMethod.Throw, CloseOnRejectionOption.kCloseOnRejection);

    /// <summary>
    /// Generate_AsyncFromSyncIteratorMethod: the common steps of the
    /// prototype methods followed by AsyncFromSyncIteratorContinuation.
    /// The CSA callbacks (get_method, if_method_undefined) become switches
    /// on <paramref name="kind"/>.
    /// </summary>
    static JSValue GenerateAsyncFromSyncIteratorMethod(Isolate isolate, in BuiltinArguments args, SyncMethod kind,
        CloseOnRejectionOption closeOnRejection)
    {
        NativeContext nativeContext = args.Target.Context.NativeContext;
        JSValue sentValue = args.AtOrUndefined(1);
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);

        // At this time %AsyncFromSyncIterator% does not escape to user code, and
        // so cannot be called with an incompatible receiver (CSA_CHECK).
        if (args.Receiver.Object is not JSAsyncFromSyncIterator asyncIterator)
        {
            throw new InvalidOperationException("Check failed: receiver is a JSAsyncFromSyncIterator.");
        }
        JSReceiver syncIterator = asyncIterator.SyncIterator;

        // get_method. For return and throw this is a plain GetProperty outside
        // any exception handler in V8, so an abrupt lookup throws out of the
        // builtin rather than rejecting the promise.
        JSValue method = kind switch
        {
            SyncMethod.Next => asyncIterator.Next,
            SyncMethod.Return => ObjectOps.GetProperty(isolate, syncIterator, ReadOnlyRoots.return_string),
            _ => ObjectOps.GetProperty(isolate, syncIterator, ReadOnlyRoots.throw_string),
        };

        JSValue exception;
        bool done = false;
        if (kind != SyncMethod.Next && method.IsNullOrUndefined)
        {
            if (kind == SyncMethod.Return)
            {
                // If return is undefined, then
                // Let iterResult be ! CreateIterResultObject(value, true)
                JSObject iterResult = IteratorBuiltins.CreateIterResultObject(isolate, sentValue, true);
                // Perform ! Call(promiseCapability.[[Resolve]], undefined, « iterResult »).
                PromiseBuiltins.ResolvePromise(isolate, promise, iterResult);
                return promise;
            }

            // 8. If throw is undefined, then
            //   a. NOTE: If syncIterator does not have a `throw` method, close it
            //      to give it a chance to clean up before we reject the capability.
            //   c. Let result be Completion(IteratorClose(syncIteratorRecord, closeCompletion)).
            JSValue rejectValue;
            try
            {
                IteratorBuiltins.IteratorClose(isolate, syncIterator);
                // g. Perform ! Call(promiseCapability.[[Reject]], undefined,
                //    « a newly created TypeError object »).
                rejectValue = isolate.Factory.NewTypeError(MessageTemplate.ThrowMethodMissing);
            }
            catch (JavaScriptException e)
            {
                // d. IfAbruptRejectPromise(result, promiseCapability).
                rejectValue = e.Value;
            }
            PromiseBuiltins.RejectPromise(isolate, promise, rejectValue, true);
            return promise;
        }

        JSValue value;
        try
        {
            JSValue iterResult = args.ArgcWithoutReceiver > 0
                ? Execution.Call(isolate, method, syncIterator, [sentValue])
                : Execution.Call(isolate, method, syncIterator, []);
            if (!LoadIteratorResult(isolate, nativeContext, iterResult, out value, out done, out exception))
            {
                return MaybeCloseSyncThenRejectPromise(isolate, promise, syncIterator, exception, closeOnRejection);
            }
        }
        catch (JavaScriptException e)
        {
            return MaybeCloseSyncThenRejectPromise(isolate, promise, syncIterator, e.Value, closeOnRejection);
        }

        // 6. Let valueWrapper be PromiseResolve(%Promise%, « value »).
        //    IfAbruptRejectPromise(valueWrapper, promiseCapability).
        JSValue valueWrapper;
        try
        {
            valueWrapper = PromiseBuiltins.PromiseResolve(isolate, nativeContext.PromiseFunction, value);
        }
        catch (JavaScriptException e)
        {
            // maybe_close_sync_if_not_done_then_reject_promise.
            return MaybeCloseSyncThenRejectPromise(isolate, promise, syncIterator, e.Value,
                done ? CloseOnRejectionOption.kDoNotCloseOnRejection : closeOnRejection);
        }

        // 10. Let onFulfilled be CreateBuiltinFunction(unwrap, 1, "", « »).
        JSFunction onFulfilled = CreateUnwrapClosure(isolate, nativeContext, done);

        // 12. If done is true, or if closeOnRejection is false, then
        //   a. Let onRejected be undefined.
        // 13. Else,
        //   b. Let onRejected be CreateBuiltinFunction(closeIterator, 1, "", « »).
        JSValue onRejected = closeOnRejection == CloseOnRejectionOption.kCloseOnRejection && !done
            ? CreateAsyncFromSyncIteratorCloseSyncAndRethrowClosure(isolate, nativeContext, syncIterator)
            : JSValue.Undefined;

        // 14. Perform ! PerformPromiseThen(valueWrapper, onFulfilled, onRejected, promiseCapability).
        return PromiseBuiltins.PerformPromiseThen(isolate, valueWrapper.As<JSPromise>(), onFulfilled, onRejected, promise);
    }

    static JSValue MaybeCloseSyncThenRejectPromise(Isolate isolate, JSPromise promise, JSReceiver syncIterator,
        JSValue exception, CloseOnRejectionOption closeOnRejection)
    {
        if (closeOnRejection == CloseOnRejectionOption.kCloseOnRejection)
        {
            // 7. If valueWrapper is an abrupt completion, done is false, and
            //    closeOnRejection is true, then
            //   a. Set valueWrapper to Completion(IteratorClose(syncIteratorRecord, valueWrapper)).
            IteratorBuiltins.IteratorCloseOnException(isolate, syncIterator);
        }
        PromiseBuiltins.RejectPromise(isolate, promise, exception, true);
        return promise;
    }

    /// <summary>
    /// LoadIteratorResult: loads "done" then "value" (fast path for the
    /// initial iterator result map). Returns false with a TypeError in
    /// <paramref name="exception"/> for a non-object result; lookups that
    /// throw propagate to the caller's handler.
    /// </summary>
    static bool LoadIteratorResult(Isolate isolate, NativeContext nativeContext, JSValue iterResult, out JSValue value,
        out bool done, out JSValue exception)
    {
        if (!iterResult.IsJSReceiver)
        {
            // Sync iterator result is not an object: produce a TypeError.
            exception = isolate.Factory.NewTypeError(MessageTemplate.IteratorResultNotAnObject, iterResult);
            value = JSValue.Undefined;
            done = false;
            return false;
        }
        var result = iterResult.As<JSReceiver>();
        JSValue doneValue;
        if (ReferenceEquals(result.Map, nativeContext.IteratorResultMap))
        {
            var fast = (JSObject)result;
            doneValue = fast.InObjectPropertyRef(IteratorBuiltins.kIteratorResultDoneIndex);
            value = fast.InObjectPropertyRef(IteratorBuiltins.kIteratorResultValueIndex);
        }
        else
        {
            // Let nextDone be IteratorComplete(nextResult).
            doneValue = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.done_string);
            // Let nextValue be IteratorValue(nextResult).
            value = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.value_string);
        }
        // Ensure `iterResult.done` is a Boolean.
        done = ObjectOps.BooleanValue(doneValue);
        exception = JSValue.Undefined;
        return true;
    }

    /// <summary>AsyncBuiltinsAssembler::CreateUnwrapClosure.</summary>
    public static JSFunction CreateUnwrapClosure(Isolate isolate, NativeContext nativeContext, bool done)
    {
        Context closureContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext, kValueUnwrapContextLength);
        closureContext[kDoneSlot] = JSValue.FromBoolean(done);
        return RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.AsyncIteratorValueUnwrap, closureContext,
            nativeContext);
    }

    static JSFunction CreateAsyncFromSyncIteratorCloseSyncAndRethrowClosure(Isolate isolate, NativeContext nativeContext,
        JSReceiver syncIterator)
    {
        Context closureContext =
            RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext, kCloseSyncAndRethrowContextLength);
        closureContext[kSyncIteratorSlot] = syncIterator;
        return RootSharedFunctions.AllocateRootFunctionWithContext(isolate, Builtin.AsyncFromSyncIteratorCloseSyncAndRethrow,
            closureContext, nativeContext);
    }

    /// <summary>AsyncIteratorValueUnwrap (ES #sec-async-iterator-value-unwrap-functions).</summary>
    public static JSValue AsyncIteratorValueUnwrap(Isolate isolate, in BuiltinArguments args)
    {
        JSValue done = args.Target.Context[kDoneSlot];
        return IteratorBuiltins.CreateIterResultObject(isolate, args.AtOrUndefined(1), done.IsTrue);
    }

    /// <summary>
    /// AsyncFromSyncIteratorCloseSyncAndRethrow: closeIterator of
    /// AsyncFromSyncIteratorContinuation step 13.a.
    /// </summary>
    public static JSValue AsyncFromSyncIteratorCloseSyncAndRethrow(Isolate isolate, in BuiltinArguments args)
    {
        JSValue error = args.AtOrUndefined(1);
        var syncIterator = args.Target.Context[kSyncIteratorSlot].As<JSReceiver>();
        // i. Return ? IteratorClose(syncIteratorRecord, ThrowCompletion(error)).
        IteratorBuiltins.IteratorCloseOnException(isolate, syncIterator);
        return isolate.ReThrow(error);
    }
}
