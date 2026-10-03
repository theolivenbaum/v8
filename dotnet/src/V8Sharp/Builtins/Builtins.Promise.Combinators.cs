// Port of src/builtins/promise-all.tq, promise-all-element-closure.tq,
// promise-any.tq and promise-race.tq: Promise.all, Promise.allSettled,
// Promise.any, Promise.race and their element closures.
namespace V8Sharp.Builtins;

public static partial class PromiseBuiltins
{
    /// <summary>PropertyArray::HashField::kMax: the limit on combinator elements (indices live in the identity hash).</summary>
    const int kPropertyArrayHashFieldMax = (1 << 21) - 1;

    /// <summary>The kind of element closure a combinator creates (the functors of promise-all.tq).</summary>
    enum CombinatorKind { All, AllSettled }

    /// <summary>CreatePromiseAllResolveElementContext.</summary>
    static Context CreatePromiseAllResolveElementContext(Isolate isolate, PromiseCapability capability, NativeContext nativeContext)
    {
        Context resolveContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
            kPromiseAllResolveElementLength);
        resolveContext[kPromiseAllResolveElementRemainingSlot] = JSValue.FromInt(1);
        resolveContext[kPromiseAllResolveElementCapabilitySlot] = capability;
        resolveContext[kPromiseAllResolveElementValuesSlot] = FixedArray.Empty;
        return resolveContext;
    }

    /// <summary>
    /// CreatePromiseAllResolveElementFunction: the element closure; its index
    /// (1-based) is stored in its identity hash, as in V8.
    /// </summary>
    static JSFunction CreatePromiseAllResolveElementFunction(Isolate isolate, Context resolveElementContext, int index,
        Builtin resolveFunction)
    {
        Debug.Assert(index > 0 && index < kPropertyArrayHashFieldMax);
        JSFunction resolve = RootSharedFunctions.AllocateRootFunctionWithContext(isolate, resolveFunction, resolveElementContext,
            resolveElementContext.NativeContext);
        resolve.SetIdentityHash(index);
        return resolve;
    }

    /// <summary>
    /// JSPromise::PerformPromiseAll (src/objects/objects.cc): Promise.all over a
    /// list of native promises, for the engine's own use (import defer).
    /// </summary>
    public static JSPromise PerformPromiseAll(Isolate isolate, ReadOnlySpan<JSPromise> promises)
    {
        NativeContext nativeContext = isolate.NativeContext;
        PromiseCapability capability = NewPromiseCapability(isolate, nativeContext.PromiseFunction, false);
        var capabilityPromise = capability.Promise.As<JSPromise>();
        Context resolveElementContext = CreatePromiseAllResolveElementContext(isolate, capability, nativeContext);

        int length = promises.Length;
        if (length == 0)
        {
            JSArray emptyArray = isolate.Factory.NewJSArrayWithElements(FixedArray.Empty);
            Execution.Call(isolate, capability.Resolve, JSValue.Undefined, [emptyArray]);
            return capabilityPromise;
        }

        if (length >= kPropertyArrayHashFieldMax)
        {
            JSObject error = isolate.Factory.NewRangeError(MessageTemplate.TooManyElementsInPromiseCombinator,
                isolate.Factory.NewStringFromAsciiChecked("all"));
            Execution.Call(isolate, capability.Reject, JSValue.Undefined, [error]);
            return capabilityPromise;
        }

        resolveElementContext[kPromiseAllResolveElementValuesSlot] = FixedArray.NewWithHoles(length);
        resolveElementContext[kPromiseAllResolveElementRemainingSlot] = JSValue.FromInt(length);
        for (int i = 0; i < length; i++)
        {
            JSFunction resolveElement = CreatePromiseAllResolveElementFunction(isolate, resolveElementContext, i + 1,
                Builtin.PromiseAllResolveElementClosure);
            try
            {
                PerformPromiseThen(isolate, promises[i], resolveElement, capability.Reject, JSValue.Undefined);
            }
            catch (JavaScriptException e)
            {
                Execution.Call(isolate, capability.Reject, JSValue.Undefined, [e.Value]);
                return capabilityPromise;
            }
        }
        return capabilityPromise;
    }

    /// <summary>PerformPromiseAll (ES #sec-performpromiseall / #sec-performpromiseallsettled).</summary>
    static JSValue PerformPromiseAll(Isolate isolate, NativeContext nativeContext, in IteratorRecord iter, JSReceiver constructor,
        PromiseCapability capability, JSValue promiseResolveFunction, CombinatorKind kind, out bool rejected, out JSValue rejectReason)
    {
        rejected = false;
        rejectReason = JSValue.Undefined;
        JSValue promise = capability.Promise;
        JSValue resolve = capability.Resolve;

        Context resolveElementContext = CreatePromiseAllResolveElementContext(isolate, capability, nativeContext);

        int index = 1;
        Map fastIteratorResultMap = nativeContext.IteratorResultMap;
        try
        {
            while (true)
            {
                JSValue nextValue;
                try
                {
                    // Let next be IteratorStep(iteratorRecord.[[Iterator]]).
                    // If next is an abrupt completion, set iteratorRecord.[[Done]] to
                    // true. ReturnIfAbrupt(next).
                    if (!IteratorBuiltins.IteratorStep(isolate, iter, out JSReceiver next, fastIteratorResultMap)) break;

                    // Let nextValue be IteratorValue(next).
                    // If nextValue is an abrupt completion, set iteratorRecord.[[Done]]
                    // to true.
                    // ReturnIfAbrupt(nextValue).
                    nextValue = IteratorBuiltins.IteratorValue(isolate, next, fastIteratorResultMap);
                }
                catch (JavaScriptException e)
                {
                    rejected = true;
                    rejectReason = e.Value;
                    return JSValue.Undefined;
                }

                // Check if we reached the limit.
                if (index == kPropertyArrayHashFieldMax)
                {
                    // If there are too many elements (currently more than 2**21-1),
                    // raise a RangeError here (which is caught below and turned into
                    // a rejection of the resulting promise).
                    isolate.ThrowRangeError(MessageTemplate.TooManyElementsInPromiseCombinator,
                        isolate.Factory.NewStringFromAsciiChecked("all"));
                }

                // Set remainingElementsCount.[[Value]] to
                //     remainingElementsCount.[[Value]] + 1.
                resolveElementContext[kPromiseAllResolveElementRemainingSlot] =
                    JSValue.FromInt((int)resolveElementContext[kPromiseAllResolveElementRemainingSlot].Number + 1);

                // Let resolveElement be CreateBuiltinFunction(steps, ...).
                JSValue resolveElementFun;
                JSValue rejectElementFun;
                if (kind == CombinatorKind.All)
                {
                    resolveElementFun = CreatePromiseAllResolveElementFunction(isolate, resolveElementContext, index,
                        Builtin.PromiseAllResolveElementClosure);
                    rejectElementFun = capability.Reject;
                }
                else
                {
                    resolveElementFun = CreatePromiseAllResolveElementFunction(isolate, resolveElementContext, index,
                        Builtin.PromiseAllSettledResolveElementClosure);
                    rejectElementFun = CreatePromiseAllResolveElementFunction(isolate, resolveElementContext, index,
                        Builtin.PromiseAllSettledRejectElementClosure);
                }

                // We can skip the "then" lookup on the result of the "resolve" call and
                // immediately chain the continuation onto the {next_value} if:
                //
                //   (a) The {constructor} is the intrinsic %Promise% function, and
                //       looking up "resolve" on {constructor} yields the initial
                //       Promise.resolve() builtin, and
                //   (b) the promise @@species protector cell is valid, meaning that
                //       no one messed with the Symbol.species property on any
                //       intrinsic promise or on the Promise.prototype, and
                //   (c) the {next_value} is a JSPromise whose [[Prototype]] field
                //       contains the intrinsic %PromisePrototype%, and
                //   (d) we're not running with async_hooks or DevTools enabled.
                //
                // In that case we also don't need to allocate a chained promise for
                // the PromiseReaction (aka we can pass undefined to
                // PerformPromiseThen), since this is only necessary for DevTools and
                // PromiseHooks.
                if (!promiseResolveFunction.IsUndefined || NeedsAnyPromiseHooks(isolate) ||
                    !Protectors.IsPromiseSpeciesLookupChainIntact(isolate) ||
                    nextValue.HeapObjectOrNull is not JSReceiver nextReceiver ||
                    !IsPromiseThenLookupChainIntact(isolate, nativeContext, nextReceiver.Map))
                {
                    // Let nextPromise be ? Call(constructor, _promiseResolve_, «
                    // nextValue »).
                    JSValue nextPromise = CallResolve(isolate, constructor, promiseResolveFunction, nextValue);

                    // Perform ? Invoke(nextPromise, "then", « resolveElement,
                    //                  resultCapability.[[Reject]] »).
                    JSValue then = ObjectOps.GetProperty(isolate, nextPromise, ReadOnlyRoots.then_string);
                    Execution.Call(isolate, then, nextPromise, [resolveElementFun, rejectElementFun]);
                }
                else
                {
                    PerformPromiseThenImpl(isolate, (JSPromise)nextReceiver, resolveElementFun, rejectElementFun, JSValue.Undefined);
                }

                // Set index to index + 1.
                index += 1;
            }
        }
        catch (JavaScriptException e)
        {
            IteratorBuiltins.IteratorCloseOnException(isolate, iter.Object);
            rejected = true;
            rejectReason = e.Value;
            return JSValue.Undefined;
        }

        // Set iteratorRecord.[[Done]] to true.
        // Set remainingElementsCount.[[Value]] to
        //    remainingElementsCount.[[Value]] - 1.
        int remainingElementsCount = (int)resolveElementContext[kPromiseAllResolveElementRemainingSlot].Number - 1;
        resolveElementContext[kPromiseAllResolveElementRemainingSlot] = JSValue.FromInt(remainingElementsCount);
        Debug.Assert(remainingElementsCount >= 0);

        var values = resolveElementContext[kPromiseAllResolveElementValuesSlot].As<FixedArray>();
        if (remainingElementsCount > 0)
        {
            // Pre-allocate the backing store for the {values} to the desired
            // capacity. We may already have elements in "values" - this happens
            // when the Thenable calls the resolve callback immediately.
            // 'index' is a 1-based index and incremented after every Promise. Later we
            // use 'values' as a 0-based array, so capacity 'index - 1' is enough.
            int newCapacity = index - 1;
            int oldCapacity = values.Length;
            if (oldCapacity < newCapacity)
            {
                resolveElementContext[kPromiseAllResolveElementValuesSlot] = ExtractFixedArrayWithHoles(values, newCapacity);
            }
        }
        else
        {
            // If remainingElementsCount.[[Value]] is 0, then
            //     Let valuesArray be CreateArrayFromList(values).
            //     Perform ? Call(resultCapability.[[Resolve]], undefined,
            //                    « valuesArray »).
            // After this point, values escapes to user code. Clear the slot.
            resolveElementContext[kPromiseAllResolveElementValuesSlot] = FixedArray.Empty;
            JSArray valuesArray = isolate.Factory.NewJSArrayWithElements(values);
            Execution.Call(isolate, resolve, JSValue.Undefined, [valuesArray]);
        }

        // Return resultCapability.[[Promise]].
        return promise;
    }

    /// <summary>ExtractFixedArray(values, 0, old, newCapacity, PromiseHole): a copy grown with holes.</summary>
    static FixedArray ExtractFixedArrayWithHoles(FixedArray values, int newCapacity)
    {
        var result = FixedArray.NewWithHoles(newCapacity);
        result.CopyElements(0, values, 0, values.Length);
        return result;
    }

    /// <summary>GeneratePromiseAll.</summary>
    static JSValue GeneratePromiseAll(Isolate isolate, JSValue receiverValue, JSValue iterable, CombinatorKind kind, string message)
    {
        NativeContext nativeContext = isolate.NativeContext;
        // Let C be the this value.
        // If Type(C) is not Object, throw a TypeError exception.
        if (receiverValue.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked(message));
        }

        // Let promiseCapability be ? NewPromiseCapability(C).
        // Don't fire debugEvent so that forwarding the rejection through all does
        // not trigger redundant ExceptionEvents
        PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);

        // NewPromiseCapability guarantees that receiver is Constructor.
        JSReceiver constructor = receiver;

        JSValue reason;
        try
        {
            // Let promiseResolve be GetPromiseResolve(C).
            // IfAbruptRejectPromise(promiseResolve, promiseCapability).
            JSValue promiseResolveFunction = GetPromiseResolve(isolate, nativeContext, constructor);

            // Let iterator be GetIterator(iterable).
            // IfAbruptRejectPromise(iterator, promiseCapability).
            IteratorRecord i = IteratorBuiltins.GetIterator(isolate, iterable);

            // Let result be PerformPromiseAll(iteratorRecord, C,
            // promiseCapability). If result is an abrupt completion, then
            //   If iteratorRecord.[[Done]] is false, let result be
            //       IteratorClose(iterator, result).
            //    IfAbruptRejectPromise(result, promiseCapability).
            JSValue result = PerformPromiseAll(isolate, nativeContext, i, constructor, capability, promiseResolveFunction, kind,
                out bool rejected, out reason);
            if (!rejected) return result;
        }
        catch (JavaScriptException e)
        {
            reason = e.Value;
        }
        // Reject:
        Execution.Call(isolate, capability.Reject, JSValue.Undefined, [reason]);
        return capability.Promise;
    }

    /// <summary>Promise.all ( iterable ).</summary>
    public static JSValue PromiseAll(Isolate isolate, in BuiltinArguments args) =>
        GeneratePromiseAll(isolate, args.Receiver, args.AtOrUndefined(1), CombinatorKind.All, "Promise.all");

    /// <summary>Promise.allSettled ( iterable ).</summary>
    public static JSValue PromiseAllSettled(Isolate isolate, in BuiltinArguments args) =>
        GeneratePromiseAll(isolate, args.Receiver, args.AtOrUndefined(1), CombinatorKind.AllSettled, "Promise.allSettled");

    // ---- The element closures (promise-all-element-closure.tq) -------------------------------------

    /// <summary>The wrapResultFunctor of the element closures.</summary>
    enum WrapResult { AsFulfilled, AllSettledFulfilled, AllSettledRejected }

    /// <summary>PromiseAllResolveElementClosure (the shared macro).</summary>
    static JSValue PromiseAllResolveElementClosure(Isolate isolate, JSValue value, JSFunction function, WrapResult wrap)
    {
        Context context = function.Context;
        // Determine the index from the {function}.
        int index = (int)function.GetIdentityHash().Number - 1;

        int remainingElementsCount = (int)context[kPromiseAllResolveElementRemainingSlot].Number;

        // If all promises were already resolved (and/or rejected for allSettled), the
        // remaining count will already be 0.
        if (remainingElementsCount == 0) return JSValue.Undefined;

        var values = context[kPromiseAllResolveElementValuesSlot].As<FixedArray>();
        int newCapacity = index + 1;
        if (newCapacity > values.Length)
        {
            // This happens only when the promises are resolved during iteration.
            values = ExtractFixedArrayWithHoles(values, newCapacity);
            context[kPromiseAllResolveElementValuesSlot] = values;
        }

        // Check whether a reject or resolve closure was already called for this
        // promise.
        if (!values[index].IsTheHole) return JSValue.Undefined;

        // Update the value depending on whether Promise.all or
        // Promise.allSettled is called.
        NativeContext nativeContext = context.NativeContext;
        JSValue updatedValue = wrap switch
        {
            WrapResult.AsFulfilled => value,
            WrapResult.AllSettledFulfilled => WrapAllSettledResult(isolate, nativeContext, "fulfilled", ReadOnlyRoots.value_string, value),
            _ => WrapAllSettledResult(isolate, nativeContext, "rejected", isolate.Factory.InternalizeString("reason"), value),
        };

        values[index] = updatedValue;

        remainingElementsCount -= 1;
        Debug.Assert(remainingElementsCount >= 0);
        context[kPromiseAllResolveElementRemainingSlot] = JSValue.FromInt(remainingElementsCount);
        if (remainingElementsCount == 0)
        {
            var capability = context[kPromiseAllResolveElementCapabilitySlot].As<PromiseCapability>();
            JSValue resolve = capability.Resolve;

            // After this point, values escapes to user code. Clear the slot.
            context[kPromiseAllResolveElementValuesSlot] = FixedArray.Empty;

            JSArray valuesArray = isolate.Factory.NewJSArrayWithElements(values);
            Execution.Call(isolate, resolve, JSValue.Undefined, [valuesArray]);
        }
        return JSValue.Undefined;
    }

    /// <summary>PromiseAllSettledWrapResultAs{Fulfilled,Rejected}Functor: { status, value | reason }.</summary>
    static JSObject WrapAllSettledResult(Isolate isolate, NativeContext nativeContext, string status, JSString key, JSValue value)
    {
        // 9. Let obj be ! ObjectCreate(%ObjectPrototype%).
        JSObject obj = isolate.Factory.NewJSObjectFromMap(nativeContext.ObjectFunction.InitialMap);
        // 10. Perform ! CreateDataProperty(obj, "status", "fulfilled" / "rejected").
        JSReceiver.CreateDataProperty(isolate, obj, isolate.Factory.InternalizeString("status"), isolate.Factory.InternalizeString(status),
            ShouldThrow.ThrowOnError);
        // 11. Perform ! CreateDataProperty(obj, "value" / "reason", x).
        JSReceiver.CreateDataProperty(isolate, obj, key, value, ShouldThrow.ThrowOnError);
        return obj;
    }

    /// <summary>Promise.all Resolve Element Functions.</summary>
    public static JSValue PromiseAllResolveElementClosure(Isolate isolate, in BuiltinArguments args) =>
        PromiseAllResolveElementClosure(isolate, args.AtOrUndefined(1), args.Target, WrapResult.AsFulfilled);

    /// <summary>Promise.allSettled Resolve Element Functions.</summary>
    public static JSValue PromiseAllSettledResolveElementClosure(Isolate isolate, in BuiltinArguments args) =>
        PromiseAllResolveElementClosure(isolate, args.AtOrUndefined(1), args.Target, WrapResult.AllSettledFulfilled);

    /// <summary>Promise.allSettled Reject Element Functions.</summary>
    public static JSValue PromiseAllSettledRejectElementClosure(Isolate isolate, in BuiltinArguments args) =>
        PromiseAllResolveElementClosure(isolate, args.AtOrUndefined(1), args.Target, WrapResult.AllSettledRejected);

    // ---- Promise.any (promise-any.tq) ----------------------------------------------------------------

    /// <summary>CreatePromiseAnyRejectElementContext.</summary>
    static Context CreatePromiseAnyRejectElementContext(Isolate isolate, PromiseCapability capability, NativeContext nativeContext)
    {
        Context rejectContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
            kPromiseAnyRejectElementLength);
        rejectContext[kPromiseAnyRejectElementRemainingSlot] = JSValue.FromInt(1);
        rejectContext[kPromiseAnyRejectElementCapabilitySlot] = capability;
        rejectContext[kPromiseAnyRejectElementErrorsSlot] = FixedArray.Empty;
        return rejectContext;
    }

    /// <summary>Promise.any Reject Element Functions (ES #sec-promise.any-reject-element-functions).</summary>
    public static JSValue PromiseAnyRejectElementClosure(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction target = args.Target;
        JSValue value = args.AtOrUndefined(1);
        Context context = target.Context;

        // 3. If alreadyCalled.[[Value]] is true, return undefined.
        //
        // We use the function's context as the marker to remember whether this
        // reject element closure was already called. It points to the reject
        // element context (which is a FunctionContext) until it was called the
        // first time, in which case we make it point to the native context here
        // to mark this reject element closure as done.
        if (context is NativeContext) return JSValue.Undefined;

        // 4. Set alreadyCalled.[[Value]] to true.
        NativeContext nativeContext = context.NativeContext;
        target.Context = nativeContext;

        // 5. Let index be F.[[Index]].
        int index = (int)target.GetIdentityHash().Number - 1;

        // 6. Let errors be F.[[Errors]].
        var errors = context[kPromiseAnyRejectElementErrorsSlot].As<FixedArray>();

        // 8. Let remainingElementsCount be F.[[RemainingElements]].
        int remainingElementsCount = (int)context[kPromiseAnyRejectElementRemainingSlot].Number;

        // 9. Set errors[index] to x.
        // The max computation below is an optimization to avoid excessive allocations
        // in the case of input promises being asynchronously rejected in ascending
        // index order.
        int newCapacity = Math.Max(remainingElementsCount - 1, index + 1);
        if (newCapacity > errors.Length)
        {
            errors = ExtractFixedArrayWithHoles(errors, newCapacity);
            context[kPromiseAnyRejectElementErrorsSlot] = errors;
        }
        errors[index] = value;

        // 10. Set remainingElementsCount.[[Value]] to
        // remainingElementsCount.[[Value]] - 1.
        remainingElementsCount -= 1;
        context[kPromiseAnyRejectElementRemainingSlot] = JSValue.FromInt(remainingElementsCount);

        // 11. If remainingElementsCount.[[Value]] is 0, then
        if (remainingElementsCount == 0)
        {
            //   a. Let error be a newly created AggregateError object.
            //   b. Set error.[[AggregateErrors]] to errors.
            JSObject error = ConstructAggregateError(isolate, errors);

            // After this point, errors escapes to user code. Clear the slot.
            context[kPromiseAnyRejectElementErrorsSlot] = FixedArray.Empty;

            //   c. Return ? Call(promiseCapability.[[Reject]], undefined, « error »).
            var capability = context[kPromiseAnyRejectElementCapabilitySlot].As<PromiseCapability>();
            Execution.Call(isolate, capability.Reject, JSValue.Undefined, [error]);
        }

        // 12. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>ConstructAggregateError: an AggregateError("All promises were rejected") with errors.</summary>
    static JSObject ConstructAggregateError(Isolate isolate, FixedArray errors)
    {
        // Runtime_ConstructInternalAggregateErrorHelper.
        JSFunction aggregateErrorFunction = isolate.NativeContext.AggregateErrorFunction;
        JSString message = MessageFormatter.Format(isolate, MessageTemplate.AllPromisesRejected, []);
        JSObject obj = ErrorUtils.Construct(isolate, aggregateErrorFunction, aggregateErrorFunction, message, JSValue.Undefined);
        // Holes (errors of elements not rejected yet cannot remain: all were rejected).
        JSArray errorsJSArray = isolate.Factory.NewJSArrayWithElements(errors);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, obj, ReadOnlyRoots.errors_string, errorsJSArray, PropertyAttributes.DONT_ENUM);
        return obj;
    }

    /// <summary>PerformPromiseAny.</summary>
    static JSValue PerformPromiseAny(Isolate isolate, NativeContext nativeContext, in IteratorRecord iteratorRecord,
        JSReceiver constructor, PromiseCapability resultCapability, JSValue promiseResolveFunction, out bool rejected,
        out JSValue rejectReason)
    {
        rejected = false;
        rejectReason = JSValue.Undefined;

        // 4. Let remainingElementsCount be a new Record { [[Value]]: 1 }.
        Context rejectElementContext = CreatePromiseAnyRejectElementContext(isolate, resultCapability, nativeContext);

        // 5. Let index be 0.
        //    (We subtract 1 in the PromiseAnyRejectElementClosure).
        int index = 1;
        Map fastIteratorResultMap = nativeContext.IteratorResultMap;
        try
        {
            // 8. Repeat,
            while (true)
            {
                JSValue nextValue;
                try
                {
                    // a. Let next be IteratorStep(iteratorRecord).
                    // b.-d.
                    if (!IteratorBuiltins.IteratorStep(isolate, iteratorRecord, out JSReceiver next, fastIteratorResultMap)) break;
                    // e. Let nextValue be IteratorValue(next).
                    // f.-g.
                    nextValue = IteratorBuiltins.IteratorValue(isolate, next, fastIteratorResultMap);
                }
                catch (JavaScriptException e)
                {
                    rejected = true;
                    rejectReason = e.Value;
                    return JSValue.Undefined;
                }

                // We store the indices as identity hash on the reject element
                // closures. Thus, we need this limit.
                if (index == kPropertyArrayHashFieldMax)
                {
                    isolate.ThrowRangeError(MessageTemplate.TooManyElementsInPromiseCombinator,
                        isolate.Factory.NewStringFromAsciiChecked("any"));
                }

                // h. Append undefined to errors. (Do nothing: errors is initialized
                // lazily when the first Promise rejects.)

                // i. Let nextPromise be ? Call(constructor, promiseResolve,
                // «nextValue »).
                JSValue nextPromise = CallResolve(isolate, constructor, promiseResolveFunction, nextValue);

                // j.-p.
                JSFunction rejectElement = CreatePromiseAllResolveElementFunction(isolate, rejectElementContext, index,
                    Builtin.PromiseAnyRejectElementClosure);
                // q. Set remainingElementsCount.[[Value]] to
                // remainingElementsCount.[[Value]] + 1.
                rejectElementContext[kPromiseAnyRejectElementRemainingSlot] =
                    JSValue.FromInt((int)rejectElementContext[kPromiseAnyRejectElementRemainingSlot].Number + 1);

                // r. Perform ? Invoke(nextPromise, "then", «
                // resultCapability.[[Resolve]], rejectElement »).
                JSValue then = ObjectOps.GetProperty(isolate, nextPromise, ReadOnlyRoots.then_string);
                Execution.Call(isolate, then, nextPromise, [resultCapability.Resolve, rejectElement]);

                // s. Increase index by 1.
                index += 1;
            }
        }
        catch (JavaScriptException e)
        {
            IteratorBuiltins.IteratorCloseOnException(isolate, iteratorRecord.Object);
            rejected = true;
            rejectReason = e.Value;
            return JSValue.Undefined;
        }

        // (8.d)
        //   i. Set iteratorRecord.[[Done]] to true.
        //  ii. Set remainingElementsCount.[[Value]] to
        //  remainingElementsCount.[[Value]] - 1.
        int remainingElementsCount = (int)rejectElementContext[kPromiseAnyRejectElementRemainingSlot].Number - 1;
        rejectElementContext[kPromiseAnyRejectElementRemainingSlot] = JSValue.FromInt(remainingElementsCount);

        // iii. If remainingElementsCount.[[Value]] is 0, then
        if (remainingElementsCount == 0)
        {
            // 1. Let error be a newly created AggregateError object.
            // 2. Set error.[[AggregateErrors]] to errors.

            // We may already have elements in "errors" - this happens when the
            // Thenable calls the reject callback immediately.
            var errors = rejectElementContext[kPromiseAnyRejectElementErrorsSlot].As<FixedArray>();

            // After this point, errors escapes to user code. Clear the slot.
            rejectElementContext[kPromiseAnyRejectElementErrorsSlot] = FixedArray.Empty;

            Debug.Assert(errors.Length == index - 1);
            JSObject error = ConstructAggregateError(isolate, errors);
            // 3. Return ThrowCompletion(error).
            rejected = true;
            rejectReason = error;
            return JSValue.Undefined;
        }
        // iv. Return resultCapability.[[Promise]].
        return resultCapability.Promise;
    }

    /// <summary>Promise.any ( iterable ).</summary>
    public static JSValue PromiseAny(Isolate isolate, in BuiltinArguments args)
    {
        NativeContext nativeContext = isolate.NativeContext;

        // 1. Let C be the this value.
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked("Promise.any"));
        }

        // 2. Let promiseCapability be ? NewPromiseCapability(C).
        PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);

        // NewPromiseCapability guarantees that receiver is Constructor.
        JSReceiver constructor = receiver;

        JSValue reason;
        try
        {
            // 3. Let promiseResolve be GetPromiseResolve(C).
            // 4. IfAbruptRejectPromise(promiseResolve, promiseCapability).
            JSValue promiseResolveFunction = GetPromiseResolve(isolate, nativeContext, constructor);

            // 5. Let iteratorRecord be GetIterator(iterable).
            // 6. IfAbruptRejectPromise(iteratorRecord, promiseCapability).
            IteratorRecord iteratorRecord = IteratorBuiltins.GetIterator(isolate, args.AtOrUndefined(1));

            // 7. Let result be PerformPromiseAny(iteratorRecord, C,
            // promiseCapability).
            // 8. If result is an abrupt completion, then ...
            // 9. Return Completion(result).
            JSValue result = PerformPromiseAny(isolate, nativeContext, iteratorRecord, constructor, capability, promiseResolveFunction,
                out bool rejected, out reason);
            if (!rejected) return result;
        }
        catch (JavaScriptException e)
        {
            reason = e.Value;
        }
        // Reject:
        Execution.Call(isolate, capability.Reject, JSValue.Undefined, [reason]);
        return capability.Promise;
    }

    // ---- Promise.race (promise-race.tq) --------------------------------------------------------------

    /// <summary>Promise.race ( iterable ).</summary>
    public static JSValue PromiseRace(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked("Promise.race"));
        }

        NativeContext nativeContext = isolate.NativeContext;

        // Let promiseCapability be ? NewPromiseCapability(C).
        // Don't fire debugEvent so that forwarding the rejection through all does
        // not trigger redundant ExceptionEvents
        PromiseCapability capability = NewPromiseCapability(isolate, receiver, false);
        JSValue resolve = capability.Resolve;
        JSValue reject = capability.Reject;
        JSValue promise = capability.Promise;

        // NewPromiseCapability guarantees that receiver is Constructor.
        JSReceiver constructor = receiver;

        JSValue exception;
        JSValue promiseResolveFunction;
        IteratorRecord i;
        try
        {
            // Let promiseResolve be GetPromiseResolve(C).
            // IfAbruptRejectPromise(promiseResolve, promiseCapability).
            promiseResolveFunction = GetPromiseResolve(isolate, nativeContext, constructor);

            // Let iterator be GetIterator(iterable).
            // IfAbruptRejectPromise(iterator, promiseCapability).
            i = IteratorBuiltins.GetIterator(isolate, args.AtOrUndefined(1));
        }
        catch (JavaScriptException e)
        {
            Execution.Call(isolate, reject, JSValue.Undefined, [e.Value]);
            return promise;
        }

        // Let result be PerformPromiseRace(iteratorRecord, C, promiseCapability).
        Map fastIteratorResultMap = nativeContext.IteratorResultMap;
        try
        {
            while (true)
            {
                JSValue nextValue;
                try
                {
                    // Let next be IteratorStep(iteratorRecord.[[Iterator]]).
                    // If next is an abrupt completion, set iteratorRecord.[[Done]] to
                    // true. ReturnIfAbrupt(next).
                    if (!IteratorBuiltins.IteratorStep(isolate, i, out JSReceiver next, fastIteratorResultMap)) return promise;

                    // Let nextValue be IteratorValue(next).
                    // If nextValue is an abrupt completion, set iteratorRecord.[[Done]]
                    // to true.
                    // ReturnIfAbrupt(nextValue).
                    nextValue = IteratorBuiltins.IteratorValue(isolate, next, fastIteratorResultMap);
                }
                catch (JavaScriptException e)
                {
                    Execution.Call(isolate, reject, JSValue.Undefined, [e.Value]);
                    return promise;
                }
                // Let nextPromise be ? Call(constructor, _promiseResolve_, «
                // nextValue »).
                JSValue nextPromise = CallResolve(isolate, constructor, promiseResolveFunction, nextValue);

                // Perform ? Invoke(nextPromise, "then", « resolveElement,
                //                  resultCapability.[[Reject]] »).
                JSValue then = ObjectOps.GetProperty(isolate, nextPromise, ReadOnlyRoots.then_string);
                Execution.Call(isolate, then, nextPromise, [resolve, reject]);
            }
        }
        catch (JavaScriptException e)
        {
            IteratorBuiltins.IteratorCloseOnException(isolate, i.Object);
            exception = e.Value;
        }
        // Reject:
        Execution.Call(isolate, reject, JSValue.Undefined, [exception]);
        return promise;
    }
}
