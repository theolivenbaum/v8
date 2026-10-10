// Port of src/builtins/array-from-async.tq: Array.fromAsync and its resume
// closures. As in V8, there is no await in the builtin: the algorithm is a
// state machine whose state lives in a synthetic function context, and each
// await point resolves the awaited value with %Promise% and resumes the
// machine from ArrayFromAsync{Iterable,ArrayLike}On{Fulfilled,Rejected}.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArrayFromAsync()
    {
        Register(Builtin.ArrayFromAsync, BuiltinsArrayFromAsync.ArrayFromAsync);
        Register(Builtin.ArrayFromAsyncIterableOnFulfilled, BuiltinsArrayFromAsync.ArrayFromAsyncIterableOnFulfilled);
        Register(Builtin.ArrayFromAsyncIterableOnRejected, BuiltinsArrayFromAsync.ArrayFromAsyncIterableOnRejected);
        Register(Builtin.ArrayFromAsyncArrayLikeOnFulfilled, BuiltinsArrayFromAsync.ArrayFromAsyncArrayLikeOnFulfilled);
        Register(Builtin.ArrayFromAsyncArrayLikeOnRejected, BuiltinsArrayFromAsync.ArrayFromAsyncArrayLikeOnRejected);
    }
}

public static class BuiltinsArrayFromAsync
{
    /// <summary>ArrayBuiltins::ArrayFromAsyncLabels.</summary>
    enum Label
    {
        kGetIteratorStep,
        kCheckIteratorValueAndMapping,
        kIteratorMapping,
        kGetIteratorValueWithMapping,
        kAddIteratorValueToTheArray,
        kGetArrayLikeValue,
        kCheckArrayLikeValueAndMapping,
        kGetArrayLikeValueWithMapping,
        kAddArrayLikeValueToTheArray,
        kDoneAndResolvePromise,
        kCloseAsyncIterator,
        kRejectPromise,
    }

    const int kMin = (int)Context.Field.MIN_CONTEXT_SLOTS;

    // ArrayBuiltins::ArrayFromAsyncIterableResolveContextSlots.
    const int kIterStepSlot = kMin;
    const int kIterAwaitedValueSlot = kMin + 1;
    const int kIterIndexSlot = kMin + 2;
    const int kIterPromiseSlot = kMin + 3;
    const int kIterPromiseFunctionSlot = kMin + 4;
    const int kIterOnFulfilledSlot = kMin + 5;
    const int kIterOnRejectedSlot = kMin + 6;
    const int kIterResultArraySlot = kMin + 7;
    const int kIterIteratorSlot = kMin + 8;
    const int kIterNextMethodSlot = kMin + 9;
    const int kIterErrorSlot = kMin + 10;
    const int kIterMapfnSlot = kMin + 11;
    const int kIterThisArgSlot = kMin + 12;
    const int kIterLength = kMin + 13;

    // ArrayBuiltins::ArrayFromAsyncArrayLikeResolveContextSlots.
    const int kALStepSlot = kMin;
    const int kALAwaitedValueSlot = kMin + 1;
    const int kALLenSlot = kMin + 2;
    const int kALIndexSlot = kMin + 3;
    const int kALPromiseSlot = kMin + 4;
    const int kALPromiseFunctionSlot = kMin + 5;
    const int kALOnFulfilledSlot = kMin + 6;
    const int kALOnRejectedSlot = kMin + 7;
    const int kALResultArraySlot = kMin + 8;
    const int kALArrayLikeSlot = kMin + 9;
    const int kALErrorSlot = kMin + 10;
    const int kALMapfnSlot = kMin + 11;
    const int kALThisArgSlot = kMin + 12;
    const int kALLength = kMin + 13;

    static Label Step(Context context, int slot) => (Label)(int)context[slot].Number;

    static void SetStep(Context context, int slot, Label step) => context[slot] = JSValue.FromInt((int)step);

    /// <summary>ArrayFromAsyncAwaitPoint: PromiseResolve(%Promise%, value).then(onFulfilled, onRejected).</summary>
    static JSValue AwaitPoint(Isolate isolate, Context context, int stepSlot, int promiseFunSlot, int resolveSlot, int rejectSlot,
        Label step, JSValue value)
    {
        SetStep(context, stepSlot, step);
        var promiseFun = context[promiseFunSlot].As<JSReceiver>();
        JSValue resultPromise = PromiseBuiltins.PromiseResolve(isolate, promiseFun, value);
        PromiseBuiltins.PerformPromiseThenImpl(isolate, resultPromise.As<JSPromise>(), context[resolveSlot], context[rejectSlot],
            JSValue.Undefined);
        return JSValue.Undefined;
    }

    /// <summary>RejectArrayFromAsyncPromise.</summary>
    static JSValue RejectArrayFromAsyncPromise(Isolate isolate, Context context, int errorSlot, int promiseSlot) =>
        PromiseBuiltins.RejectPromise(isolate, context[promiseSlot].As<JSPromise>(), context[errorSlot], false);

    // ---- Iterable path -----------------------------------------------------------------------

    static Context CreateIterableResolveContext(Isolate isolate, JSPromise promise, JSReceiver promiseFun, JSReceiver iterator,
        JSValue next, JSReceiver arr, JSValue mapfn, JSValue thisArg, NativeContext nativeContext)
    {
        Context resolveContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext, kIterLength);
        SetStep(resolveContext, kIterStepSlot, Label.kGetIteratorStep);
        resolveContext[kIterAwaitedValueSlot] = JSValue.Undefined;
        resolveContext[kIterIndexSlot] = JSValue.Zero;
        resolveContext[kIterPromiseSlot] = promise;
        resolveContext[kIterPromiseFunctionSlot] = promiseFun;
        resolveContext[kIterOnFulfilledSlot] = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
            Builtin.ArrayFromAsyncIterableOnFulfilled, resolveContext, nativeContext);
        resolveContext[kIterOnRejectedSlot] = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
            Builtin.ArrayFromAsyncIterableOnRejected, resolveContext, nativeContext);
        resolveContext[kIterResultArraySlot] = arr;
        resolveContext[kIterIteratorSlot] = iterator;
        resolveContext[kIterNextMethodSlot] = next;
        resolveContext[kIterErrorSlot] = JSValue.Undefined;
        resolveContext[kIterMapfnSlot] = mapfn;
        resolveContext[kIterThisArgSlot] = thisArg;
        return resolveContext;
    }

    static IteratorRecord IteratorRecordOf(Context context) =>
        new(context[kIterIteratorSlot].As<JSReceiver>(), context[kIterNextMethodSlot]);

    static JSValue IterableAwaitPoint(Isolate isolate, Context context, Label step, JSValue value) =>
        AwaitPoint(isolate, context, kIterStepSlot, kIterPromiseFunctionSlot, kIterOnFulfilledSlot, kIterOnRejectedSlot, step, value);

    /// <summary>CreateArrayFromIterableAsynchronously.</summary>
    static JSValue CreateArrayFromIterableAsynchronously(Isolate isolate, Context context)
    {
        try
        {
            Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
            JSValue mapfn = context[kIterMapfnSlot];
            JSValue thisArg = context[kIterThisArgSlot];
            var arr = context[kIterResultArraySlot].As<JSReceiver>();

            Label step = Step(context, kIterStepSlot);
            JSValue awaitedValue = context[kIterAwaitedValueSlot];
            double index = context[kIterIndexSlot].Number;

            JSValue mappedValue = JSValue.Undefined;
            JSValue nextValue = JSValue.Undefined;

            while (true)
            {
                switch (step)
                {
                    case Label.kGetIteratorStep:
                    {
                        IteratorRecord iteratorRecord = IteratorRecordOf(context);
                        // 3. Let nextResult be ? Call(iteratorRecord.[[NextMethod]],
                        //    iteratorRecord.[[Iterator]]).
                        // 4. Set nextResult to ? Await(nextResult).
                        JSValue next = Execution.Call(isolate, iteratorRecord.Next, iteratorRecord.Object, []);
                        return IterableAwaitPoint(isolate, context, Label.kCheckIteratorValueAndMapping, next);
                    }
                    case Label.kCheckIteratorValueAndMapping:
                    {
                        // 5. If nextResult is not an Object, throw a TypeError exception.
                        if (awaitedValue.HeapObjectOrNull is not JSReceiver nextJSReceiver)
                        {
                            return isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject,
                                ArrayBuiltinsUtils.NewString(isolate, "Array.fromAsync"));
                        }
                        // 6. Let done be ? IteratorComplete(nextResult).
                        if (IteratorBuiltins.IteratorComplete(isolate, nextJSReceiver, fastIteratorResultMap))
                        {
                            // 7. If done is true,
                            //    a. Perform ? Set(A, "length", 𝔽(k), true).
                            //    b. Return Completion Record { [[Type]]: return, [[Value]]: A,
                            //    [[Target]]: empty }.
                            step = Label.kDoneAndResolvePromise;
                            break;
                        }
                        // 8. Let nextValue be ? IteratorValue(nextResult).
                        nextValue = IteratorBuiltins.IteratorValue(isolate, nextJSReceiver, fastIteratorResultMap);
                        // When mapfn is not undefined, it is guaranteed to be callable as
                        // checked upon entry.
                        // 9. If mapping is true, then
                        if (!mapfn.IsUndefined)
                        {
                            step = Label.kIteratorMapping;
                        }
                        else
                        {
                            // 10. Else, let mappedValue be nextValue.
                            mappedValue = nextValue;
                            step = Label.kAddIteratorValueToTheArray;
                        }
                        break;
                    }
                    case Label.kIteratorMapping:
                    {
                        // a. Let mappedValue be Call(mapfn, thisArg, « nextValue, 𝔽(k) »).
                        // b. IfAbruptCloseAsyncIterator(mappedValue, iteratorRecord).
                        JSValue mapResult = Execution.Call(isolate, mapfn, thisArg, [nextValue, JSValue.FromNumber(index)]);
                        // c. Set mappedValue to Await(mappedValue).
                        // d. IfAbruptCloseAsyncIterator(mappedValue, iteratorRecord).
                        return IterableAwaitPoint(isolate, context, Label.kGetIteratorValueWithMapping, mapResult);
                    }
                    case Label.kGetIteratorValueWithMapping:
                        mappedValue = awaitedValue;
                        step = Label.kAddIteratorValueToTheArray;
                        break;
                    case Label.kAddIteratorValueToTheArray:
                        // 11. Let defineStatus be CreateDataPropertyOrThrow(A, Pk, mappedValue).
                        // 12. If defineStatus is an abrupt completion, return ?
                        //     AsyncIteratorClose(iteratorRecord, defineStatus).
                        ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, arr, index, mappedValue);
                        // 13. Set k to k + 1.
                        index++;
                        context[kIterIndexSlot] = JSValue.FromNumber(index);
                        step = Label.kGetIteratorStep;
                        break;
                    case Label.kDoneAndResolvePromise:
                        ArrayBuiltinsUtils.SetPropertyLength(isolate, arr, index);
                        PromiseBuiltins.ResolvePromise(isolate, context[kIterPromiseSlot].As<JSPromise>(), arr);
                        return JSValue.Undefined;
                    case Label.kCloseAsyncIterator:
                        step = Label.kRejectPromise;
                        if (ArrayFromAsyncAsyncIteratorCloseOnException(isolate, context, IteratorRecordOf(context)))
                        {
                            return JSValue.Undefined;
                        }
                        // Do nothing so the codeflow continues to the kRejectPromise label.
                        break;
                    case Label.kRejectPromise:
                        return RejectArrayFromAsyncPromise(isolate, context, kIterErrorSlot, kIterPromiseSlot);
                    default:
                        throw new InvalidOperationException("unreachable");
                }
            }
        }
        catch (JavaScriptException e)
        {
            context[kIterErrorSlot] = e.Value;
            if (!ArrayFromAsyncAsyncIteratorCloseOnException(isolate, context, IteratorRecordOf(context)))
            {
                return RejectArrayFromAsyncPromise(isolate, context, kIterErrorSlot, kIterPromiseSlot);
            }
        }
        return JSValue.Undefined;
    }

    /// <summary>
    /// ArrayFromAsyncAsyncIteratorCloseOnException (IfAbruptCloseAsyncIterator):
    /// false is its RejectPromise label (no return method); true when the
    /// return method was called and awaited (or threw, which is swallowed).
    /// </summary>
    static bool ArrayFromAsyncAsyncIteratorCloseOnException(Isolate isolate, Context context, IteratorRecord iterator)
    {
        try
        {
            // 3. Let innerResult be GetMethod(iterator, "return").
            JSValue method = ObjectOps.GetProperty(isolate, iterator.Object, ReadOnlyRoots.return_string);
            // 4. If innerResult.[[Type]] is normal, then
            //   a. Let return be innerResult.[[Value]].
            //   b. If return is undefined, return Completion(completion).
            if (method.IsNullOrUndefined) return false;
            //   c. Set innerResult to Call(return, iterator).
            // If an exception occurs, the original exception remains bound
            JSValue innerResult = Execution.Call(isolate, method, iterator.Object, []);
            //   d. If innerResult.[[Type]] is normal, set innerResult to
            //   Completion(Await(innerResult.[[Value]])).
            IterableAwaitPoint(isolate, context, Label.kRejectPromise, innerResult);
        }
        catch (JavaScriptException)
        {
            // Swallow the exception.
        }
        // (5. If completion.[[Type]] is throw) return Completion(completion).
        return true;
    }

    /// <summary>ArrayFromAsyncIterableOnFulfilled.</summary>
    public static JSValue ArrayFromAsyncIterableOnFulfilled(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        context[kIterAwaitedValueSlot] = args.AtOrUndefined(1);
        return CreateArrayFromIterableAsynchronously(isolate, context);
    }

    /// <summary>ArrayFromAsyncIterableOnRejected.</summary>
    public static JSValue ArrayFromAsyncIterableOnRejected(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        SetStep(context, kIterStepSlot, Label.kCloseAsyncIterator);
        context[kIterErrorSlot] = args.AtOrUndefined(1);
        return CreateArrayFromIterableAsynchronously(isolate, context);
    }

    // ---- Array-like path ---------------------------------------------------------------------

    static Context CreateArrayLikeResolveContext(Isolate isolate, double len, JSPromise promise, JSReceiver promiseFun,
        JSReceiver arrayLike, JSReceiver arr, JSValue mapfn, JSValue thisArg, NativeContext nativeContext)
    {
        Context resolveContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext, kALLength);
        SetStep(resolveContext, kALStepSlot, Label.kGetArrayLikeValue);
        resolveContext[kALAwaitedValueSlot] = JSValue.Undefined;
        resolveContext[kALLenSlot] = JSValue.FromNumber(len);
        resolveContext[kALIndexSlot] = JSValue.Zero;
        resolveContext[kALPromiseSlot] = promise;
        resolveContext[kALPromiseFunctionSlot] = promiseFun;
        resolveContext[kALOnFulfilledSlot] = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
            Builtin.ArrayFromAsyncArrayLikeOnFulfilled, resolveContext, nativeContext);
        resolveContext[kALOnRejectedSlot] = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
            Builtin.ArrayFromAsyncArrayLikeOnRejected, resolveContext, nativeContext);
        resolveContext[kALResultArraySlot] = arr;
        resolveContext[kALArrayLikeSlot] = arrayLike;
        resolveContext[kALErrorSlot] = JSValue.Undefined;
        resolveContext[kALMapfnSlot] = mapfn;
        resolveContext[kALThisArgSlot] = thisArg;
        return resolveContext;
    }

    static JSValue ArrayLikeAwaitPoint(Isolate isolate, Context context, Label step, JSValue value) =>
        AwaitPoint(isolate, context, kALStepSlot, kALPromiseFunctionSlot, kALOnFulfilledSlot, kALOnRejectedSlot, step, value);

    /// <summary>CreateArrayFromArrayLikeAsynchronously.</summary>
    static JSValue CreateArrayFromArrayLikeAsynchronously(Isolate isolate, Context context)
    {
        try
        {
            JSValue mapfn = context[kALMapfnSlot];
            JSValue thisArg = context[kALThisArgSlot];
            var arr = context[kALResultArraySlot].As<JSReceiver>();

            Label step = Step(context, kALStepSlot);
            JSValue awaitedValue = context[kALAwaitedValueSlot];
            double len = context[kALLenSlot].Number;
            double index = context[kALIndexSlot].Number;

            while (true)
            {
                switch (step)
                {
                    case Label.kGetArrayLikeValue:
                    {
                        var arrayLike = context[kALArrayLikeSlot].As<JSReceiver>();
                        // vii. Repeat, while k < len,
                        //   1. Let Pk be ! ToString(𝔽(k)).
                        if (index < len)
                        {
                            //   2. Let kValue be ? Get(arrayLike, Pk).
                            JSValue kValue = ObjectOps.GetPropertyOrElement(isolate, arrayLike,
                                new PropertyKey(isolate, index));
                            //   3. Set kValue to ? Await(kValue).
                            return ArrayLikeAwaitPoint(isolate, context, Label.kCheckArrayLikeValueAndMapping, kValue);
                        }
                        // viii. Perform ? Set(A, "length", 𝔽(len), true).
                        // ix. Return Completion Record { [[Type]]: return, [[Value]]: A,
                        // [[Target]]: empty }.
                        step = Label.kDoneAndResolvePromise;
                        break;
                    }
                    case Label.kCheckArrayLikeValueAndMapping:
                        // When mapfn is not undefined, it is guaranteed to be callable as
                        // checked upon entry.
                        // 4. If mapping is true, then
                        step = !mapfn.IsUndefined ? Label.kGetArrayLikeValueWithMapping : Label.kAddArrayLikeValueToTheArray;
                        break;
                    case Label.kGetArrayLikeValueWithMapping:
                    {
                        // a. Let mappedValue be ? Call(mapfn, thisArg, « kValue, 𝔽(k) »).
                        // b. Set mappedValue to ? Await(mappedValue).
                        JSValue mapResult = Execution.Call(isolate, mapfn, thisArg, [awaitedValue, JSValue.FromNumber(index)]);
                        return ArrayLikeAwaitPoint(isolate, context, Label.kAddArrayLikeValueToTheArray, mapResult);
                    }
                    case Label.kAddArrayLikeValueToTheArray:
                        // 5. Else, let mappedValue be kValue.
                        // 6. Perform ? CreateDataPropertyOrThrow(A, Pk, mappedValue).
                        ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, arr, index, awaitedValue);
                        index++;
                        context[kALIndexSlot] = JSValue.FromNumber(index);
                        step = Label.kGetArrayLikeValue;
                        break;
                    case Label.kDoneAndResolvePromise:
                        ArrayBuiltinsUtils.SetPropertyLength(isolate, arr, index);
                        PromiseBuiltins.ResolvePromise(isolate, context[kALPromiseSlot].As<JSPromise>(), arr);
                        return JSValue.Undefined;
                    case Label.kRejectPromise:
                        return RejectArrayFromAsyncPromise(isolate, context, kALErrorSlot, kALPromiseSlot);
                    default:
                        throw new InvalidOperationException("unreachable");
                }
            }
        }
        catch (JavaScriptException e)
        {
            context[kALErrorSlot] = e.Value;
            return RejectArrayFromAsyncPromise(isolate, context, kALErrorSlot, kALPromiseSlot);
        }
    }

    /// <summary>ArrayFromAsyncArrayLikeOnFulfilled.</summary>
    public static JSValue ArrayFromAsyncArrayLikeOnFulfilled(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        context[kALAwaitedValueSlot] = args.AtOrUndefined(1);
        return CreateArrayFromArrayLikeAsynchronously(isolate, context);
    }

    /// <summary>ArrayFromAsyncArrayLikeOnRejected.</summary>
    public static JSValue ArrayFromAsyncArrayLikeOnRejected(Isolate isolate, in BuiltinArguments args)
    {
        Context context = args.Target.Context;
        SetStep(context, kALStepSlot, Label.kRejectPromise);
        context[kALErrorSlot] = args.AtOrUndefined(1);
        return CreateArrayFromArrayLikeAsynchronously(isolate, context);
    }

    // ---- Array.fromAsync ---------------------------------------------------------------------

    /// <summary>
    /// GetMethod(items, symbol): undefined for null/undefined methods; the
    /// NotCallable label throws <paramref name="notCallable"/>.
    /// </summary>
    static JSValue GetMethod(Isolate isolate, JSValue items, Symbol symbol, MessageTemplate notCallable)
    {
        if (items.IsNullOrUndefined) ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, items, symbol);
        JSValue method = ObjectOps.GetPropertyOrElement(isolate, items, symbol);
        if (method.IsNullOrUndefined) return JSValue.Undefined;
        if (!ObjectOps.IsCallable(method))
        {
            isolate.ThrowTypeError(notCallable, ArrayBuiltinsUtils.NewString(isolate, "Array.fromAsync"));
        }
        return method;
    }

    /// <summary>https://tc39.es/proposal-array-from-async/#sec-array.fromAsync</summary>
    public static JSValue ArrayFromAsync(Isolate isolate, in BuiltinArguments args)
    {
        NativeContext context = isolate.NativeContext;
        // 1. Let C be the this value.
        JSValue c = isolate.Flags.builtin_subclassing ? args.Receiver : context.ArrayFunction;

        JSValue items = args.AtOrUndefined(1);
        JSValue mapfn = args.AtOrUndefined(2);
        JSValue thisArg = args.AtOrUndefined(3);

        // 2. Let promiseCapability be ! NewPromiseCapability(%Promise%).
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        JSReceiver promiseFun = context.PromiseFunction;

        // 3. Let fromAsyncClosure be a new Abstract Closure with no parameters that
        // captures C, mapfn, and thisArg and performs the following steps when
        // called:
        try
        {
            // i. If IsCallable(mapfn) is false, throw a TypeError exception.
            if (!mapfn.IsUndefined && !ObjectOps.IsCallable(mapfn)) isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, mapfn);

            // c. Let usingAsyncIterator be ? GetMethod(asyncItems, @@asyncIterator).
            JSValue usingAsyncIterator = GetMethod(isolate, items, ReadOnlyRoots.async_iterator_symbol,
                MessageTemplate.FirstArgumentAsyncIteratorSymbolNonCallable);
            JSValue usingSyncIterator = JSValue.Undefined;
            if (usingAsyncIterator.IsUndefined)
            {
                // d. If usingAsyncIterator is undefined, then
                //   i. Let usingSyncIterator be ? GetMethod(asyncItems, @@iterator).
                usingSyncIterator = GetMethod(isolate, items, ReadOnlyRoots.iterator_symbol,
                    MessageTemplate.FirstArgumentIteratorSymbolNonCallable);
                if (usingSyncIterator.IsUndefined)
                {
                    // i. Else, (iteratorRecord is undefined)
                    //   i. NOTE: asyncItems is neither an AsyncIterable nor an
                    //   Iterable so assume it is an array-like object.
                    //   ii. Let arrayLike be ! ToObject(asyncItems).
                    JSReceiver arrayLike = ObjectOps.ToObject(isolate, items);
                    //   iii. Let len be ? LengthOfArrayLike(arrayLike).
                    double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, arrayLike);
                    //   iv. If IsConstructor(C) is true, then
                    //     1. Let A be ? Construct(C, « 𝔽(len) »).
                    //   v. Else,
                    //     1. Let A be ? ArrayCreate(len).
                    JSReceiver arrFromArrayLike = ArrayBuiltinsUtils.IsConstructor(c)
                        ? Execution.New(isolate, c, [JSValue.FromNumber(len)]).As<JSReceiver>()
                        : ArrayBuiltinsUtils.ArrayCreate(isolate, len);
                    //   vi. Let k be 0.
                    Context arrayLikeResolveContext = CreateArrayLikeResolveContext(isolate, len, promise, promiseFun, arrayLike,
                        arrFromArrayLike, mapfn, thisArg, context);
                    CreateArrayFromArrayLikeAsynchronously(isolate, arrayLikeResolveContext);
                    return promise;
                }
            }

            // e. Let iteratorRecord be undefined.
            // f. If usingAsyncIterator is not undefined, then
            //    i. Set iteratorRecord to ? GetIterator(asyncItems, async, usingAsyncIterator).
            // g. Else if usingSyncIterator is not undefined, then
            //    i. Set iteratorRecord to ?
            //    CreateAsyncFromSyncIterator(GetIterator(asyncItems, sync, usingSyncIterator)).
            IteratorRecord iteratorRecord = !usingAsyncIterator.IsUndefined
                ? IteratorBuiltins.GetIterator(isolate, items, usingAsyncIterator)
                : AsyncFromSyncIteratorBuiltins.GetIteratorRecordAfterCreateAsyncFromSyncIterator(isolate,
                    IteratorBuiltins.GetIterator(isolate, items, usingSyncIterator));

            // h. If iteratorRecord is not undefined, then
            //   i. If IsConstructor(C) is true, then
            //     1. Let A be ? Construct(C).
            //   ii. Else,
            //     1. Let A be ! ArrayCreate(0).
            JSReceiver arr = ArrayBuiltinsUtils.IsConstructor(c)
                ? Execution.New(isolate, c, []).As<JSReceiver>()
                : ArrayBuiltinsUtils.ArrayCreate(isolate, 0);

            Context iterableResolveContext = CreateIterableResolveContext(isolate, promise, promiseFun, iteratorRecord.Object,
                iteratorRecord.Next, arr, mapfn, thisArg, context);
            CreateArrayFromIterableAsynchronously(isolate, iterableResolveContext);
            return promise;
        }
        catch (JavaScriptException e)
        {
            PromiseBuiltins.RejectPromise(isolate, promise, e.Value, false);
            return promise;
        }
    }
}
