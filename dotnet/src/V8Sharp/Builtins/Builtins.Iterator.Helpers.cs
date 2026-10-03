// Port of src/builtins/iterator-helpers.tq (%IteratorHelperPrototype%.next
// and .return; Iterator.prototype.map, filter, take, drop, flatMap, reduce,
// toArray, forEach, some, every, find, join, includes; Iterator.concat,
// Iterator.zip and Iterator.zipKeyed) and src/builtins/iterator-from.tq
// (Iterator.from, GetIteratorFlattenable, %WrapForValidIteratorPrototype%).
//
// Iterator helpers are specified as generators; V8 implements them as direct
// iterators whose state (JSIteratorHelperState) plays the generator state.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterIteratorHelpers()
    {
        Register(Builtin.IteratorHelperPrototypeNext, IteratorBuiltins.IteratorHelperPrototypeNext);
        Register(Builtin.IteratorHelperPrototypeReturn, IteratorBuiltins.IteratorHelperPrototypeReturn);
        Register(Builtin.IteratorPrototypeMap, IteratorBuiltins.IteratorPrototypeMap);
        Register(Builtin.IteratorPrototypeFilter, IteratorBuiltins.IteratorPrototypeFilter);
        Register(Builtin.IteratorPrototypeTake, IteratorBuiltins.IteratorPrototypeTake);
        Register(Builtin.IteratorPrototypeDrop, IteratorBuiltins.IteratorPrototypeDrop);
        Register(Builtin.IteratorPrototypeFlatMap, IteratorBuiltins.IteratorPrototypeFlatMap);
        Register(Builtin.IteratorPrototypeReduce, IteratorBuiltins.IteratorPrototypeReduce);
        Register(Builtin.IteratorPrototypeToArray, IteratorBuiltins.IteratorPrototypeToArray);
        Register(Builtin.IteratorPrototypeForEach, IteratorBuiltins.IteratorPrototypeForEach);
        Register(Builtin.IteratorPrototypeSome, IteratorBuiltins.IteratorPrototypeSome);
        Register(Builtin.IteratorPrototypeEvery, IteratorBuiltins.IteratorPrototypeEvery);
        Register(Builtin.IteratorPrototypeFind, IteratorBuiltins.IteratorPrototypeFind);
        Register(Builtin.IteratorPrototypeJoin, IteratorBuiltins.IteratorPrototypeJoin);
        Register(Builtin.IteratorPrototypeIncludes, IteratorBuiltins.IteratorPrototypeIncludes);
        Register(Builtin.IteratorConcat, IteratorBuiltins.IteratorConcat);
        Register(Builtin.IteratorZip, IteratorBuiltins.IteratorZip);
        Register(Builtin.IteratorZipKeyed, IteratorBuiltins.IteratorZipKeyed);
        Register(Builtin.IteratorFrom, IteratorBuiltins.IteratorFrom);
        Register(Builtin.WrapForValidIteratorPrototypeNext, IteratorBuiltins.WrapForValidIteratorPrototypeNext);
        Register(Builtin.WrapForValidIteratorPrototypeReturn, IteratorBuiltins.WrapForValidIteratorPrototypeReturn);
    }
}

public static partial class IteratorBuiltins
{
    // ---- Utilities -------------------------------------------------------------------------------

    static JSValue ThrowCalledOnNonObject(Isolate isolate, string methodName) =>
        isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked(methodName));

    /// <summary>ThrowIfIteratorHelperExecuting (#sec-generatorvalidate step 6).</summary>
    static void ThrowIfIteratorHelperExecuting(Isolate isolate, JSIteratorHelper helper)
    {
        if (helper.State == JSIteratorHelperState.kExecuting) isolate.ThrowTypeError(MessageTemplate.GeneratorRunning);
    }

    static void MarkIteratorHelperAsExecuting(JSIteratorHelper helper)
    {
        Debug.Assert(helper.State != JSIteratorHelperState.kExecuting);
        helper.State = JSIteratorHelperState.kExecuting;
    }

    static void MarkIteratorHelperAsFinishedExecuting(JSIteratorHelper helper)
    {
        Debug.Assert(helper.State == JSIteratorHelperState.kExecuting);
        helper.State = JSIteratorHelperState.kSuspendedYield;
    }

    static void MarkIteratorHelperAsExhausted(JSIteratorHelper helper) => helper.State = JSIteratorHelperState.kCompleted;

    /// <summary>GetIteratorDirect (ES #sec-getiteratordirect).</summary>
    public static IteratorRecord GetIteratorDirect(Isolate isolate, JSReceiver obj)
    {
        // 1. Let nextMethod be ? Get(obj, "next").
        JSValue nextMethod = JSReceiver.GetProperty(isolate, obj, ReadOnlyRoots.next_string);
        // 2. Let iteratorRecord be Record { [[Iterator]]: obj, [[NextMethod]]:
        //    nextMethod, [[Done]]: false }.
        // 3. Return iteratorRecord.
        return new IteratorRecord(obj, nextMethod);
    }

    /// <summary>GetIteratorFlattenable (ES #sec-getiteratorflattenable); obj is an object or (iterate-strings) a string.</summary>
    public static IteratorRecord GetIteratorFlattenable(Isolate isolate, JSValue obj)
    {
        // 2. Let method be ? GetMethod(obj, @@iterator).
        JSValue method = GetMethod(isolate, obj, ReadOnlyRoots.iterator_symbol);
        // 3. If method is undefined, then
        //  a. Let iterator be obj.
        // 4. Else (method is not undefined),
        //  a. Let iterator be ? Call(method, obj).
        JSValue iterator = method.IsUndefined ? obj : Execution.Call(isolate, method, obj, []);

        // 5. If iterator is not an Object, throw a TypeError exception.
        if (iterator.HeapObjectOrNull is not JSReceiver iteratorObj)
        {
            isolate.ThrowTypeError(MessageTemplate.NotIterable, obj);
            return default;
        }

        // 6. Return ? GetIteratorDirect(iterator).
        return GetIteratorDirect(isolate, iteratorObj);
    }

    /// <summary>CloseIteratorAndThrow: IteratorClose(iterated, TypeError "x is not a function").</summary>
    static JSValue CloseIteratorAndThrowCalledNonCallable(Isolate isolate, JSReceiver iteratorObject, JSValue value)
    {
        IteratorCloseOnException(isolate, iteratorObject);
        return isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, value));
    }

    // ---- %IteratorHelperPrototype% -------------------------------------------------------------------

    /// <summary>%IteratorHelperPrototype%.next ( ).</summary>
    public static JSValue IteratorHelperPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Return ? GeneratorResume(this value, undefined, "Iterator Helper").
        if (args.Receiver.HeapObjectOrNull is not JSIteratorHelper helper)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("Iterator Helper.prototype.next"), args.Receiver);
        }

        ThrowIfIteratorHelperExecuting(isolate, helper);

        if (helper.State == JSIteratorHelperState.kCompleted) return CreateIterResultObject(isolate, JSValue.Undefined, true);

        return helper switch
        {
            JSIteratorMapHelper mapHelper => IteratorMapHelperNext(isolate, mapHelper),
            JSIteratorFilterHelper filterHelper => IteratorFilterHelperNext(isolate, filterHelper),
            JSIteratorTakeHelper takeHelper => IteratorTakeHelperNext(isolate, takeHelper),
            JSIteratorDropHelper dropHelper => IteratorDropHelperNext(isolate, dropHelper),
            JSIteratorFlatMapHelper flatMapHelper => IteratorFlatMapHelperNext(isolate, flatMapHelper),
            JSIteratorConcatHelper concatHelper => IteratorConcatHelperNext(isolate, concatHelper),
            JSIteratorZipKeyedHelper zipKeyedHelper => IteratorZipKeyedHelperNext(isolate, zipKeyedHelper),
            JSIteratorZipHelper zipHelper => IteratorZipHelperNext(isolate, zipHelper),
            _ => throw new InvalidOperationException("unreachable"),
        };
    }

    /// <summary>%IteratorHelperPrototype%.return ( ).</summary>
    public static JSValue IteratorHelperPrototypeReturn(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be this value.
        // 2. Perform ? RequireInternalSlot(O, [[UnderlyingIterator]]).
        // 3. Assert: O has a [[GeneratorState]] slot.
        if (args.Receiver.HeapObjectOrNull is not JSIteratorHelper helper)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("Iterator Helper.prototype.return"), args.Receiver);
        }

        // Iterator helpers are specified as generators. The net effect of this
        // method is to close the underlying and return { value: undefined, done:
        // true }. We first handle the easy cases, where the state is EXECUTING or
        // COMPLETED.
        ThrowIfIteratorHelperExecuting(isolate, helper);
        if (helper.State == JSIteratorHelperState.kCompleted) return CreateIterResultObject(isolate, JSValue.Undefined, true);

        // 4. If O.[[GeneratorState]] is suspendedStart, then
        if (helper.State == JSIteratorHelperState.kSuspendedStart)
        {
            // a. Set O.[[GeneratorState]] to completed.
            MarkIteratorHelperAsExhausted(helper);
            // c. Perform ? IteratorCloseAll(O.[[UnderlyingIterators]],
            //    NormalCompletion(unused)).
            switch (helper)
            {
                case JSIteratorConcatHelper:
                    // The JSIteratorConcatHelper completed before ever starting.
                    // In this case, there is no underlying iterator.
                    break;
                case JSIteratorHelperSimple simpleHelper:
                    // Simple iterator helpers have just one underlying iterator.
                    IteratorClose(isolate, simpleHelper.UnderlyingObject!);
                    break;
                case JSIteratorZipHelper zipHelper:
                    IteratorZipCloseAll(isolate, zipHelper.UnderlyingIterators, true);
                    break;
            }
            // d. Return CreateIterResultObject(undefined, true).
            return CreateIterResultObject(isolate, JSValue.Undefined, true);
        }

        // 5. Let C be ReturnCompletion(undefined).
        // 6. Return ? GeneratorResumeAbrupt(O, C, "Iterator Helper").
        // The following is executed as part of GeneratorResumeAbrupt.
        MarkIteratorHelperAsExecuting(helper);
        try
        {
            switch (helper)
            {
                case JSIteratorFlatMapHelper flatMapHelper:
                    try
                    {
                        // Iterator.prototype.flatMap, step 6.b.viii.4.b:
                        // i. Let backupCompletion be
                        //    Completion(IteratorClose(innerIterator, completion)).
                        IteratorClose(isolate, flatMapHelper.InnerObject ?? flatMapHelper.UnderlyingObject!);
                    }
                    catch (JavaScriptException)
                    {
                        // ii. IfAbruptCloseIterator(backupCompletion, iterated).
                        IteratorCloseOnException(isolate, flatMapHelper.UnderlyingObject!);
                        MarkIteratorHelperAsExhausted(flatMapHelper);
                        throw;
                    }
                    // iii. Return ? IteratorClose(iterated, completion).
                    IteratorClose(isolate, flatMapHelper.UnderlyingObject!);
                    break;
                case JSIteratorHelperSimple simpleHelper:
                    // Standard generator body cleanup logic for simple iterator helpers
                    // and abrupt completions:
                    // Return ? IteratorClose(iterated, completion).
                    IteratorClose(isolate, simpleHelper.UnderlyingObject!);
                    break;
                case JSIteratorZipHelper zipHelper:
                    IteratorZipCloseAll(isolate, zipHelper.UnderlyingIterators, true);
                    break;
            }
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }

        MarkIteratorHelperAsExhausted(helper);
        return CreateIterResultObject(isolate, JSValue.Undefined, true);
    }

    // ---- map ---------------------------------------------------------------------------------------

    /// <summary>Iterator.prototype.map ( mapper ).</summary>
    public static JSValue IteratorPrototypeMap(Isolate isolate, in BuiltinArguments args)
    {
        JSValue mapper = args.AtOrUndefined(1);
        // 1. Let O be the this value.
        // 2. If O is not an Object, throw a TypeError exception.
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.map");

        // 4. If IsCallable(mapper) is false, then
        //     a. Let error be ThrowCompletion(a newly created TypeError object).
        //     b. Return ? IteratorClose(iterated , error).
        if (!ObjectOps.IsCallable(mapper)) return CloseIteratorAndThrowCalledNonCallable(isolate, o, mapper);

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        // 7. Let result be CreateIteratorFromClosure(closure, "Iterator Helper",
        //    %IteratorHelperPrototype%).
        // 8. Set result.[[UnderlyingIterator]] to iterated.
        // 9. Return result.
        var helper = (JSIteratorMapHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorMapHelperMap);
        helper.UnderlyingIterator = iterated;
        helper.Mapper = mapper;
        helper.Counter = 0;
        return helper;
    }

    static JSValue IteratorMapHelperNext(Isolate isolate, JSIteratorMapHelper helper)
    {
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        MarkIteratorHelperAsExecuting(helper);
        double counter = helper.Counter;
        try
        {
            // b. Repeat,
            // i. Let next be ? IteratorStep(iterated).
            if (!IteratorStep(isolate, helper.UnderlyingIterator, out JSReceiver next, fastIteratorResultMap))
            {
                // ii. If next is false, return undefined.
                MarkIteratorHelperAsExhausted(helper);
                return CreateIterResultObject(isolate, JSValue.Undefined, true);
            }

            // iii. Let value be ? IteratorValue(next).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);

            JSValue mapped;
            try
            {
                // iv. Let mapped be Completion(
                //     Call(mapper, undefined, « value, 𝔽(counter) »)).
                mapped = Execution.Call(isolate, helper.Mapper, JSValue.Undefined, [value, JSValue.FromNumber(counter)]);
            }
            catch (JavaScriptException)
            {
                // v. IfAbruptCloseIterator(mapped, iterated).
                IteratorCloseOnException(isolate, helper.UnderlyingObject!);
                throw;
            }

            // viii. Set counter to counter + 1.
            // (Done out of order. Iterator helpers are specified as generators with
            // yields but we implement them as direct iterators.)
            helper.Counter = counter + 1;

            // vi. Let completion be Completion(Yield(mapped)).
            MarkIteratorHelperAsFinishedExecuting(helper);
            return CreateIterResultObject(isolate, mapped, false);
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    // ---- filter ------------------------------------------------------------------------------------

    /// <summary>Iterator.prototype.filter ( predicate ).</summary>
    public static JSValue IteratorPrototypeFilter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue predicate = args.AtOrUndefined(1);
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.filter");
        if (!ObjectOps.IsCallable(predicate)) return CloseIteratorAndThrowCalledNonCallable(isolate, o, predicate);

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        var helper = (JSIteratorFilterHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorFilterHelperMap);
        helper.UnderlyingIterator = iterated;
        helper.Predicate = predicate;
        helper.Counter = 0;
        return helper;
    }

    static JSValue IteratorFilterHelperNext(Isolate isolate, JSIteratorFilterHelper helper)
    {
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        MarkIteratorHelperAsExecuting(helper);
        try
        {
            while (true)
            {
                double counter = helper.Counter;

                // b. Repeat,
                // i. Let next be ? IteratorStep(iterated).
                if (!IteratorStep(isolate, helper.UnderlyingIterator, out JSReceiver next, fastIteratorResultMap))
                {
                    // ii. If next is false, return undefined.
                    MarkIteratorHelperAsExhausted(helper);
                    return CreateIterResultObject(isolate, JSValue.Undefined, true);
                }

                // iii. Let value be ? IteratorValue(next).
                JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);

                JSValue selected;
                try
                {
                    // iv. Let selected be Completion(
                    //     Call(predicate, undefined, « value, 𝔽(counter) »)).
                    selected = Execution.Call(isolate, helper.Predicate, JSValue.Undefined, [value, JSValue.FromNumber(counter)]);
                }
                catch (JavaScriptException)
                {
                    // v. IfAbruptCloseIterator(selected, iterated).
                    IteratorCloseOnException(isolate, helper.UnderlyingObject!);
                    throw;
                }

                // vii. Set counter to counter + 1.
                helper.Counter = counter + 1;

                // vi. If ToBoolean(selected) is true, then
                if (ObjectOps.BooleanValue(selected))
                {
                    // 1. Let completion be Completion(Yield(value)).
                    MarkIteratorHelperAsFinishedExecuting(helper);
                    return CreateIterResultObject(isolate, value, false);
                }
            }
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    // ---- take / drop -------------------------------------------------------------------------------

    /// <summary>
    /// Steps 4-9 of take and drop: ToNumber(limit) (closing on error), then
    /// the NaN / > 2^53-1 / negative RangeError checks (closing the iterator).
    /// </summary>
    static double GetIntegerLimit(Isolate isolate, JSReceiver o, JSValue limit)
    {
        double numLimit;
        try
        {
            // 4. Let numLimit be Completion(ToNumber(limit)).
            numLimit = ObjectOps.ToNumber(isolate, limit).Number;
        }
        catch (JavaScriptException)
        {
            // 5. IfAbruptCloseIterator(numLimit, iterated).
            IteratorCloseOnException(isolate, o);
            throw;
        }

        // 6. If numLimit is NaN, then ... RangeError.
        // 7. If numLimit is finite and numLimit > 𝔽(2^53 - 1), then ... RangeError.
        // 8. Let integerLimit be ! ToIntegerOrInfinity(numLimit).
        // 9. If integerLimit < 0, then ... RangeError.
        double integerLimit = double.IsNaN(numLimit) ? 0 : Math.Truncate(numLimit);
        if (double.IsNaN(numLimit) || (!double.IsInfinity(numLimit) && numLimit > kMaxSafeInteger) || integerLimit < 0)
        {
            IteratorCloseOnException(isolate, o);
            isolate.ThrowRangeError(MessageTemplate.MustBePositive, limit);
        }
        return integerLimit == 0 ? 0 : integerLimit;
    }

    /// <summary>kMaxSafeInteger: 2^53 - 1.</summary>
    const double kMaxSafeInteger = 9007199254740991;

    /// <summary>Iterator.prototype.take ( limit ).</summary>
    public static JSValue IteratorPrototypeTake(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.take");
        double integerLimit = GetIntegerLimit(isolate, o, args.AtOrUndefined(1));

        // 10. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        var helper = (JSIteratorTakeHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorTakeHelperMap);
        helper.UnderlyingIterator = iterated;
        helper.Remaining = integerLimit;
        return helper;
    }

    static JSValue IteratorTakeHelperNext(Isolate isolate, JSIteratorTakeHelper helper)
    {
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        MarkIteratorHelperAsExecuting(helper);
        double remaining = helper.Remaining;
        try
        {
            // i. If remaining is 0, then
            if (remaining == 0)
            {
                // 1. Return ? IteratorClose(iterated, NormalCompletion(undefined)).
                IteratorClose(isolate, helper.UnderlyingObject!);
                MarkIteratorHelperAsExhausted(helper);
                return CreateIterResultObject(isolate, JSValue.Undefined, true);
            }

            // ii. If remaining is not +∞, then
            //   1. Set remaining to remaining - 1.
            if (!double.IsInfinity(remaining)) helper.Remaining = remaining - 1;

            // iii. Let next be ? IteratorStep(iterated).
            if (!IteratorStep(isolate, helper.UnderlyingIterator, out JSReceiver next, fastIteratorResultMap))
            {
                // iv. If next is false, return undefined.
                MarkIteratorHelperAsExhausted(helper);
                return CreateIterResultObject(isolate, JSValue.Undefined, true);
            }

            // v. Let completion be Completion(Yield(? IteratorValue(next))).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);
            MarkIteratorHelperAsFinishedExecuting(helper);
            return CreateIterResultObject(isolate, value, false);
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    /// <summary>Iterator.prototype.drop ( limit ).</summary>
    public static JSValue IteratorPrototypeDrop(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.drop");
        double integerLimit = GetIntegerLimit(isolate, o, args.AtOrUndefined(1));

        // 10. Let iterated be ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        var helper = (JSIteratorDropHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorDropHelperMap);
        helper.UnderlyingIterator = iterated;
        helper.Remaining = integerLimit;
        return helper;
    }

    static JSValue IteratorDropHelperNext(Isolate isolate, JSIteratorDropHelper helper)
    {
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        MarkIteratorHelperAsExecuting(helper);
        double remaining = helper.Remaining;
        try
        {
            // b. Repeat, while remaining > 0,
            while (remaining > 0)
            {
                // i. If remaining is not +∞, then
                if (!double.IsInfinity(remaining))
                {
                    // 1. Set remaining to remaining - 1.
                    remaining = remaining - 1;
                    helper.Remaining = remaining;
                }

                // ii. Let next be ? IteratorStep(iterated).
                if (!IteratorStep(isolate, helper.UnderlyingIterator, out _, fastIteratorResultMap))
                {
                    MarkIteratorHelperAsExhausted(helper);
                    return CreateIterResultObject(isolate, JSValue.Undefined, true);
                }
            }

            // c. Repeat,
            // i. Let next be ? IteratorStep(iterated).
            if (!IteratorStep(isolate, helper.UnderlyingIterator, out JSReceiver next, fastIteratorResultMap))
            {
                // ii. If next is false, return undefined.
                MarkIteratorHelperAsExhausted(helper);
                return CreateIterResultObject(isolate, JSValue.Undefined, true);
            }

            // iii. Let completion be Completion(Yield(? IteratorValue(next))).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);
            MarkIteratorHelperAsFinishedExecuting(helper);
            return CreateIterResultObject(isolate, value, false);
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    // ---- flatMap -----------------------------------------------------------------------------------

    const string kFlatMapMethodName = "Iterator.prototype.flatMap";

    /// <summary>Iterator.prototype.flatMap ( mapper ).</summary>
    public static JSValue IteratorPrototypeFlatMap(Isolate isolate, in BuiltinArguments args)
    {
        JSValue mapper = args.AtOrUndefined(1);
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, kFlatMapMethodName);
        if (!ObjectOps.IsCallable(mapper))
        {
            IteratorCloseOnException(isolate, o);
            return isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, isolate.Factory.NewStringFromAsciiChecked(kFlatMapMethodName));
        }

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        var helper = (JSIteratorFlatMapHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorFlatMapHelperMap);
        helper.UnderlyingIterator = iterated;
        helper.Mapper = mapper;
        helper.Counter = 0;
        helper.InnerObject = iterated.Object;
        helper.InnerNext = iterated.Next;
        return helper;
    }

    static JSValue IteratorFlatMapHelperNext(Isolate isolate, JSIteratorFlatMapHelper helper)
    {
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        bool innerAlive = helper.State != JSIteratorHelperState.kSuspendedStart;
        MarkIteratorHelperAsExecuting(helper);
        try
        {
            while (true)
            {
                double counter = helper.Counter;
                // b. Repeat,
                if (!innerAlive)
                {
                    // i. Let next be ? IteratorStep(iterated).
                    if (!IteratorStep(isolate, helper.UnderlyingIterator, out JSReceiver next, fastIteratorResultMap))
                    {
                        // ii. If next is false, return undefined.
                        MarkIteratorHelperAsExhausted(helper);
                        return CreateIterResultObject(isolate, JSValue.Undefined, true);
                    }

                    // iii. Let value be ? IteratorValue(next).
                    JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);

                    try
                    {
                        // iv. Let mapped be Completion(
                        //     Call(mapper, undefined, « value, 𝔽(counter) »)).
                        JSValue mapped = Execution.Call(isolate, helper.Mapper, JSValue.Undefined, [value, JSValue.FromNumber(counter)]);
                        if (!mapped.IsJSReceiver) ThrowCalledOnNonObject(isolate, kFlatMapMethodName);

                        // vi. Let innerIterator be Completion(GetIteratorFlattenable(mapped,
                        //     reject-strings)).
                        IteratorRecord inner = GetIteratorFlattenable(isolate, mapped);
                        helper.InnerObject = inner.Object;
                        helper.InnerNext = inner.Next;

                        // viii. Let innerAlive be true.
                        innerAlive = true;
                    }
                    catch (JavaScriptException)
                    {
                        // v. IfAbruptCloseIterator(mapped, iterated)
                        IteratorCloseOnException(isolate, helper.UnderlyingObject!);
                        throw;
                    }
                    // x. Set counter to counter + 1.
                    helper.Counter = counter + 1;
                }

                // ix. Repeat, while innerAlive is true,
                JSValue innerValue;
                try
                {
                    // 1. Let innerNext be Completion(IteratorStep(innerIterator)).
                    var innerRecord = new IteratorRecord(helper.InnerObject!, helper.InnerNext);
                    if (!IteratorStep(isolate, innerRecord, out JSReceiver innerNext, fastIteratorResultMap))
                    {
                        // 3. If innerNext is false, then
                        //    a. Set innerAlive to false.
                        innerAlive = false;
                        continue;
                    }

                    // 4. Else,
                    //    a. Let innerValue be Completion(IteratorValue(innerNext)).
                    innerValue = IteratorValue(isolate, innerNext, fastIteratorResultMap);
                }
                catch (JavaScriptException)
                {
                    // 2. IfAbruptCloseIterator(innerNext, iterated)
                    IteratorCloseOnException(isolate, helper.UnderlyingObject!);
                    throw;
                }

                // c. Let completion be Completion(Yield(innerValue)).
                MarkIteratorHelperAsFinishedExecuting(helper);
                return CreateIterResultObject(isolate, innerValue, false);
            }
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    // ---- reduce / toArray / forEach / some / every / find --------------------------------------------

    /// <summary>Iterator.prototype.reduce ( reducer [ , initialValue ] ).</summary>
    public static JSValue IteratorPrototypeReduce(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Iterator.prototype.reduce";
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, methodName);

        JSValue reducer = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(reducer)) return CloseIteratorAndThrowCalledNonCallable(isolate, o, reducer);

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        JSValue accumulator;
        double counter;

        // 6. If initialValue is not present, then
        if (args.ArgcWithoutReceiver <= 1)
        {
            //   a. Let next be ? IteratorStep(iterated).
            //   b. If next is false, throw a TypeError exception.
            if (!IteratorStep(isolate, iterated, out JSReceiver first, fastIteratorResultMap))
            {
                return isolate.ThrowTypeError(MessageTemplate.IteratorReduceNoInitial,
                    isolate.Factory.NewStringFromAsciiChecked(methodName));
            }
            //   c. Let accumulator be ? IteratorValue(next).
            accumulator = IteratorValue(isolate, first, fastIteratorResultMap);
            //   d. Let counter be 1.
            counter = 1;
        }
        else
        {
            // 7. Else,
            //   a. Let accumulator be initialValue.
            accumulator = args[2];
            //   b. Let counter be 0.
            counter = 0;
        }

        // 8. Repeat,
        while (true)
        {
            //  a. Let next be ? IteratorStep(iterated).
            //  b. If next is false, return accumulator.
            if (!IteratorStep(isolate, iterated, out JSReceiver next, fastIteratorResultMap)) return accumulator;

            //  c. Let value be ? IteratorValue(next).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);

            try
            {
                //  d. Let result be Completion(Call(reducer, undefined, « accumulator,
                //  value, 𝔽(counter) »)).
                //  f. Set accumulator to result.[[Value]].
                accumulator = Execution.Call(isolate, reducer, JSValue.Undefined, [accumulator, value, JSValue.FromNumber(counter)]);
            }
            catch (JavaScriptException)
            {
                //  e. IfAbruptCloseIterator(result, iterated).
                IteratorCloseOnException(isolate, o);
                throw;
            }

            //  g. Set counter to counter + 1.
            counter = counter + 1;
        }
    }

    /// <summary>Iterator.prototype.toArray ( ).</summary>
    public static JSValue IteratorPrototypeToArray(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.toArray");

        // 3. Let iterated be ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);
        // 4. Let items be a new empty List.
        var items = new ValueList(8);

        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 5. Repeat,
        //  a. Let next be ? IteratorStep(iterated).
        while (IteratorStep(isolate, iterated, out JSReceiver next, fastIteratorResultMap))
        {
            //  c. Let value be ? IteratorValue(next).
            //  d. Append value to items.
            items.Add(IteratorValue(isolate, next, fastIteratorResultMap));
        }
        //  b. If next is false, return CreateArrayFromList(items).
        return isolate.Factory.NewJSArrayWithElements(items.ToFixedArray());
    }

    /// <summary>Iterator.prototype.forEach ( fn ).</summary>
    public static JSValue IteratorPrototypeForEach(Isolate isolate, in BuiltinArguments args)
    {
        JSValue fn = args.AtOrUndefined(1);
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.forEach");
        if (!ObjectOps.IsCallable(fn)) return CloseIteratorAndThrowCalledNonCallable(isolate, o, fn);

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        // 6. Let counter be 0.
        double counter = 0;
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 7. Repeat,
        //  a. Let next be ? IteratorStep(iterated).
        //  b. If next is false, return undefined.
        while (IteratorStep(isolate, iterated, out JSReceiver next, fastIteratorResultMap))
        {
            //  c. Let value be ? IteratorValue(next).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);
            try
            {
                //  d. Let result be Completion(Call(fn, undefined, « value, 𝔽(counter)
                //  »)).
                Execution.Call(isolate, fn, JSValue.Undefined, [value, JSValue.FromNumber(counter)]);
            }
            catch (JavaScriptException)
            {
                //  e. IfAbruptCloseIterator(result, iterated).
                IteratorCloseOnException(isolate, o);
                throw;
            }
            //  f. Set counter to counter + 1.
            counter = counter + 1;
        }
        return JSValue.Undefined;
    }

    /// <summary>The kind of short-circuiting search (some, every, find).</summary>
    enum SearchKind { Some, Every, Find }

    /// <summary>Iterator.prototype.some / every / find.</summary>
    static JSValue IteratorPrototypeSearch(Isolate isolate, JSValue receiver, JSValue predicate, SearchKind kind, string methodName)
    {
        if (receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, methodName);
        if (!ObjectOps.IsCallable(predicate)) return CloseIteratorAndThrowCalledNonCallable(isolate, o, predicate);

        // 5. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        // 6. Let counter be 0.
        double counter = 0;
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 7. Repeat,
        while (true)
        {
            //  a. Let next be ? IteratorStep(iterated).
            if (!IteratorStep(isolate, iterated, out JSReceiver next, fastIteratorResultMap))
            {
                //  b. If next is false, return false / true / undefined.
                return kind switch
                {
                    SearchKind.Some => JSValue.False,
                    SearchKind.Every => JSValue.True,
                    _ => JSValue.Undefined,
                };
            }

            //  c. Let value be ? IteratorValue(next).
            JSValue value = IteratorValue(isolate, next, fastIteratorResultMap);

            JSValue result;
            try
            {
                //  d. Let result be Completion(Call(predicate, undefined, « value,
                //  𝔽(counter) »)).
                result = Execution.Call(isolate, predicate, JSValue.Undefined, [value, JSValue.FromNumber(counter)]);
            }
            catch (JavaScriptException)
            {
                //  e. IfAbruptCloseIterator(result, iterated).
                IteratorCloseOnException(isolate, o);
                throw;
            }

            //  f. If ToBoolean(result) is true (false for every), return ?
            //  IteratorClose(iterated, NormalCompletion(...)).
            bool test = ObjectOps.BooleanValue(result);
            if (kind == SearchKind.Every ? !test : test)
            {
                IteratorClose(isolate, iterated);
                return kind switch
                {
                    SearchKind.Some => JSValue.True,
                    SearchKind.Every => JSValue.False,
                    _ => value,
                };
            }

            //  g. Set counter to counter + 1.
            counter = counter + 1;
        }
    }

    /// <summary>Iterator.prototype.some ( predicate ).</summary>
    public static JSValue IteratorPrototypeSome(Isolate isolate, in BuiltinArguments args) =>
        IteratorPrototypeSearch(isolate, args.Receiver, args.AtOrUndefined(1), SearchKind.Some, "Iterator.prototype.some");

    /// <summary>Iterator.prototype.every ( predicate ).</summary>
    public static JSValue IteratorPrototypeEvery(Isolate isolate, in BuiltinArguments args) =>
        IteratorPrototypeSearch(isolate, args.Receiver, args.AtOrUndefined(1), SearchKind.Every, "Iterator.prototype.every");

    /// <summary>Iterator.prototype.find ( predicate ).</summary>
    public static JSValue IteratorPrototypeFind(Isolate isolate, in BuiltinArguments args) =>
        IteratorPrototypeSearch(isolate, args.Receiver, args.AtOrUndefined(1), SearchKind.Find, "Iterator.prototype.find");

    // ---- join / includes ----------------------------------------------------------------------------

    /// <summary>Iterator.prototype.join ( separator ) (https://tc39.es/proposal-iterator-join).</summary>
    public static JSValue IteratorPrototypeJoin(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.join");

        JSValue separator = args.AtOrUndefined(1);
        // 4. If separator is undefined, then
        //   a. Let sep be ",".
        // 5. Else,
        //   a. Let sep be Completion(ToString(separator)).
        //   b. IfAbruptCloseIterator(sep, iterated).
        JSString sep;
        if (separator.IsUndefined)
        {
            sep = isolate.Factory.InternalizeString(",");
        }
        else
        {
            try
            {
                sep = ObjectOps.ToString(isolate, separator);
            }
            catch (JavaScriptException)
            {
                IteratorCloseOnException(isolate, o);
                throw;
            }
        }

        // 6. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);

        // 7. Let R be the empty String.
        JSString result = ReadOnlyRoots.empty_string;

        // 8. Let first be true.
        bool first = true;
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 9. Repeat,
        //   a. Let value be ? IteratorStepValue(iterated).
        //   b. If value is DONE, return R.
        while (IteratorStepValue(isolate, iterated, fastIteratorResultMap, out JSValue value))
        {
            // c. If first is true, then
            //   i. Set first to false.
            // d. Else,
            //   i. Set R to the string-concatenation of R and sep.
            if (first) first = false;
            else result = ConcatStrings(isolate, result, sep);

            // e. If value is neither undefined nor null, then
            if (!value.IsNullOrUndefined)
            {
                JSString s;
                try
                {
                    // i. Let S be Completion(ToString(value)).
                    s = ObjectOps.ToString(isolate, value);
                }
                catch (JavaScriptException)
                {
                    // ii. IfAbruptCloseIterator(S, iterated).
                    IteratorCloseOnException(isolate, o);
                    throw;
                }
                // A RangeError does not close the iterator.
                // iii. Set R to the string-concatenation of R and S.
                result = ConcatStrings(isolate, result, s);
            }
        }
        return result;
    }

    /// <summary>StringAdd: R + S, throwing the invalid-string-length RangeError on overflow.</summary>
    static JSString ConcatStrings(Isolate isolate, JSString left, JSString right)
    {
        if (left.Length + (long)right.Length > JSString.kMaxLength) isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
        return isolate.Factory.NewConsString(left, right);
    }

    /// <summary>Iterator.prototype.includes ( searchElement [ , skippedElements ] ).</summary>
    public static JSValue IteratorPrototypeIncludes(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver o) return ThrowCalledOnNonObject(isolate, "Iterator.prototype.includes");

        JSValue searchElement = args.AtOrUndefined(1);
        JSValue skippedElements = args.AtOrUndefined(2);

        double toSkip;
        // 4. If skippedElements is undefined, then
        if (skippedElements.IsUndefined)
        {
            // a. Let toSkip be +0𝔽.
            toSkip = 0;
        }
        else
        {
            // 5. a. If skippedElements is not one of +∞𝔽, -∞𝔽, or an integral Number,
            //       then ... TypeError (closing the iterator).
            if (!skippedElements.IsNumber ||
                !(double.IsInfinity(skippedElements.Number) || skippedElements.Number == Math.Truncate(skippedElements.Number)))
            {
                IteratorCloseOnException(isolate, o);
                return isolate.ThrowTypeError(MessageTemplate.ArgumentIsNotUndefinedOrInteger,
                    isolate.Factory.NewStringFromAsciiChecked("skippedElements"));
            }
            // b. Let toSkip be skippedElements.
            toSkip = skippedElements.Number;
        }

        // 6. If toSkip < -0𝔽, then ... RangeError.
        // 7. If toSkip is finite and toSkip > 𝔽(2^53 - 1), then ... RangeError.
        if (toSkip < 0 || (!double.IsInfinity(toSkip) && toSkip > kMaxSafeInteger))
        {
            IteratorCloseOnException(isolate, o);
            return isolate.ThrowRangeError(MessageTemplate.MustBePositive, skippedElements);
        }

        // 8. Let skipped be +0𝔽.
        double skipped = 0;

        // 9. Set iterated to ? GetIteratorDirect(O).
        IteratorRecord iterated = GetIteratorDirect(isolate, o);
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 9. Repeat,
        //   a. Let value be ? IteratorStepValue(iterated).
        //   b. If value is done, return false.
        while (IteratorStepValue(isolate, iterated, fastIteratorResultMap, out JSValue value))
        {
            // c. If skipped < toSkip, then
            if (skipped < toSkip)
            {
                // i. Set skipped to skipped + 1𝔽.
                skipped = skipped + 1;
            }
            else if (ObjectOps.SameValueZero(value, searchElement))
            {
                // d. Else if SameValueZero(value, searchElement) is true, then
                // i. Return ? IteratorClose(iterated, NormalCompletion(true)).
                IteratorClose(isolate, iterated);
                return JSValue.True;
            }
        }
        return JSValue.False;
    }

    // ---- Iterator.concat ------------------------------------------------------------------------------

    const string kConcatMethodName = "Iterator.concat";

    /// <summary>Iterator.concat ( ...items ) (https://tc39.es/proposal-iterator-sequencing/#sec-iterator.concat).</summary>
    public static JSValue IteratorConcat(Isolate isolate, in BuiltinArguments args)
    {
        ReadOnlySpan<JSValue> items = args.Arguments;
        // 1. Let iterables be a new empty List.
        FixedArray iterables = items.Length == 0 ? FixedArray.Empty : new FixedArray(2 * items.Length);

        // 2. For each element item of items, do
        for (int i = 0; i < items.Length; ++i)
        {
            JSValue item = items[i];
            // a. If item is not an Object, throw a TypeError exception.
            if (!item.IsJSReceiver) return ThrowCalledOnNonObject(isolate, kConcatMethodName);

            // b. Let method be ? GetMethod(item, %Symbol.iterator%).
            // c. If method is undefined, throw a TypeError exception.
            JSValue method = GetMethod(isolate, item, ReadOnlyRoots.iterator_symbol);
            if (method.IsUndefined) return isolate.ThrowTypeError(MessageTemplate.NotIterable, item);

            // d. Append the Record { [[OpenMethod]]: method, [[Iterable]]: item } to
            // iterables.
            iterables[2 * i] = method;
            iterables[2 * i + 1] = item;
        }

        // 4. Let gen be CreateIteratorFromClosure(closure, "Iterator Helper",
        //    %IteratorHelperPrototype%, « [[UnderlyingIterators]] »).
        // 5. Set gen.[[UnderlyingIterators]] to a new empty List.
        // 6. Return gen.
        var helper = (JSIteratorConcatHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorConcatHelperMap);
        helper.UnderlyingIterator = new IteratorRecord(isolate.NativeContext.InitialIteratorPrototype, JSValue.Undefined);
        helper.Iterables = iterables;
        helper.Current = 0;
        return helper;
    }

    static JSValue IteratorConcatHelperNext(Isolate isolate, JSIteratorConcatHelper helper)
    {
        // 3. Let closure be a new Abstract Closure with no parameters that captures
        //    iterables and performs the following steps when called:
        bool innerAlive = helper.State != JSIteratorHelperState.kSuspendedStart;
        MarkIteratorHelperAsExecuting(helper);
        try
        {
            // a. For each Record iterable of iterables, do
            while (true)
            {
                if (!innerAlive)
                {
                    if (helper.Current >= helper.Iterables.Length)
                    {
                        MarkIteratorHelperAsExhausted(helper);
                        return CreateIterResultObject(isolate, JSValue.Undefined, true);
                    }

                    // i. Let iter be ? Call(iterable.[[OpenMethod]],
                    // iterable.[[Iterable]]).
                    JSValue method = helper.Iterables[helper.Current];
                    JSValue iterable = helper.Iterables[helper.Current + 1];
                    JSValue iter = Execution.Call(isolate, method, iterable, []);

                    // ii. If iter is not an Object, throw a TypeError exception.
                    if (iter.HeapObjectOrNull is not JSReceiver iterObj)
                    {
                        return isolate.ThrowTypeError(MessageTemplate.NotIterable, iter);
                    }

                    // iii. Let iteratorRecord be ? GetIteratorDirect(iter).
                    helper.UnderlyingIterator = GetIteratorDirect(isolate, iterObj);

                    // iv. Let innerAlive be true.
                    innerAlive = true;
                }

                Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
                // v. Repeat, while innerAlive is true,
                //    1. Let innerValue be ? IteratorStepValue(iteratorRecord).
                bool hasValue;
                JSValue value;
                try
                {
                    hasValue = IteratorStepValue(isolate, helper.UnderlyingIterator, fastIteratorResultMap, out value);
                }
                catch (JavaScriptException)
                {
                    // label DoneWithException: V8 closes the inner iterator
                    // (IteratorCloseOnException) even though its next() threw.
                    IteratorCloseOnException(isolate, helper.UnderlyingIterator.Object);
                    throw;
                }
                if (!hasValue)
                {
                    // 2. If innerValue is done, then
                    //    a. Set innerAlive to false.
                    innerAlive = false;
                    helper.Current += 2;
                    continue;
                }

                // 3. Else,
                //    a. Let completion be Completion(Yield(innerValue)).
                MarkIteratorHelperAsFinishedExecuting(helper);
                return CreateIterResultObject(isolate, value, false);
            }
        }
        catch (JavaScriptException)
        {
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    // ---- Iterator.zip / Iterator.zipKeyed -------------------------------------------------------------

    /// <summary>
    /// IteratorZipCloseAll: closes the open iterators (the [iterator, next]
    /// pairs whose iterator is not the hole) in reverse order. With
    /// <paramref name="propagate"/>, the first exception is rethrown after
    /// all are closed; otherwise exceptions are swallowed.
    /// </summary>
    static void IteratorZipCloseAll(Isolate isolate, FixedArray iterators, bool propagate)
    {
        JavaScriptException? firstError = null;
        for (int j = iterators.Length - 2; j >= 0; j -= 2)
        {
            if (iterators[j].IsTheHole) continue;
            var it = (JSReceiver)iterators[j].Object;
            iterators[j] = JSValue.TheHole;
            if (propagate)
            {
                try
                {
                    IteratorClose(isolate, it);
                }
                catch (JavaScriptException e)
                {
                    firstError ??= e;
                }
            }
            else
            {
                IteratorCloseOnException(isolate, it);
            }
        }
        if (firstError is not null) isolate.ReThrow(firstError.Value, firstError.MessageObject);
    }

    /// <summary>GetOptionsObject (base.tq): undefined becomes a null-prototype object; non-objects throw.</summary>
    static JSReceiver GetOptionsObject(Isolate isolate, JSValue options)
    {
        // 1. If options is undefined, then
        //   a. Return OrdinaryObjectCreate(null).
        if (options.IsUndefined) return isolate.Factory.NewSlowJSObjectWithNullProto();
        // 2. If options is an Object, then
        //    a. Return options.
        // 3. Throw a TypeError exception.
        if (options.HeapObjectOrNull is JSReceiver receiver) return receiver;
        ThrowCalledOnNonObject(isolate, "options");
        return null!;
    }

    /// <summary>Steps 2-7 of Iterator.zip / zipKeyed: the mode and the padding option.</summary>
    static JSIteratorZipHelperMode GetZipModeAndPadding(Isolate isolate, JSValue optionsArg, string methodName, out JSValue paddingOption)
    {
        // 2. Set options to ? GetOptionsObject(options).
        JSReceiver options = GetOptionsObject(isolate, optionsArg);

        // 3. Let mode be ? Get(options, "mode").
        // 4. If mode is undefined, set mode to "shortest".
        // 5. If mode is not one of "shortest", "longest", or "strict", throw a
        //    TypeError exception.
        JSIteratorZipHelperMode zipMode = JSIteratorZipHelperMode.kShortest;
        JSValue modeVal = JSReceiver.GetProperty(isolate, options, isolate.Factory.InternalizeString("mode"));
        if (!modeVal.IsUndefined)
        {
            string? mode = modeVal.HeapObjectOrNull is JSString modeStr ? modeStr.ToString() : null;
            switch (mode)
            {
                case "shortest":
                    zipMode = JSIteratorZipHelperMode.kShortest;
                    break;
                case "longest":
                    zipMode = JSIteratorZipHelperMode.kLongest;
                    break;
                case "strict":
                    zipMode = JSIteratorZipHelperMode.kStrict;
                    break;
                default:
                    isolate.ThrowTypeError(MessageTemplate.InvalidIteratorZipMode, isolate.Factory.NewStringFromAsciiChecked(methodName));
                    break;
            }
        }

        // 6. Let paddingOption be undefined.
        // 7. If mode is "longest", then
        //     a. Set paddingOption to ? Get(options, "padding").
        //     b. If paddingOption is not undefined and paddingOption is not an
        //        Object, throw a TypeError exception.
        paddingOption = JSValue.Undefined;
        if (zipMode == JSIteratorZipHelperMode.kLongest)
        {
            paddingOption = JSReceiver.GetProperty(isolate, options, isolate.Factory.InternalizeString("padding"));
            if (!paddingOption.IsUndefined && !paddingOption.IsJSReceiver) ThrowCalledOnNonObject(isolate, "padding");
        }
        return zipMode;
    }

    /// <summary>Iterator.zip ( iterables [ , options ] ) (https://tc39.es/proposal-joint-iteration/#sec-iterator.zip).</summary>
    public static JSValue IteratorZip(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Iterator.zip";

        // 1. If iterables is not an Object, throw a TypeError exception.
        JSValue iterables = args.AtOrUndefined(1);
        if (!iterables.IsJSReceiver) return ThrowCalledOnNonObject(isolate, methodName);

        JSIteratorZipHelperMode zipMode = GetZipModeAndPadding(isolate, args.AtOrUndefined(2), methodName, out JSValue paddingOption);

        // 8.  Let iters be a new empty List.
        // 9.  Let padding be a new empty List.
        var iters = new ValueList(8);
        FixedArray padding = FixedArray.Empty;
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        // 10. Let inputIter be ? GetIterator(iterables, sync).
        IteratorRecord inputIter = GetIterator(isolate, iterables);

        // 11. Let next be not-started.
        // 12. Repeat, while next is not done,
        while (true)
        {
            // a. Set next to Completion(IteratorStepValue(inputIter)).
            JSValue next;
            try
            {
                if (!IteratorStepValue(isolate, inputIter, fastIteratorResultMap, out next)) break;
            }
            catch (JavaScriptException)
            {
                // b. IfAbruptCloseIterators(next, iters).
                IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                throw;
            }

            // c. If next is not done, then
            //     i. Let iter be Completion(GetIteratorFlattenable(next,
            //        reject-primitives)).
            IteratorRecord iter;
            try
            {
                if (!next.IsJSReceiver) isolate.ThrowTypeError(MessageTemplate.NotIterable, next);
                iter = GetIteratorFlattenable(isolate, next);
            }
            catch (JavaScriptException)
            {
                // ii. IfAbruptCloseIterators(iter, the list-concatenation of
                //     « inputIter » and iters).
                IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                IteratorCloseOnException(isolate, inputIter.Object);
                throw;
            }
            // iii. Append iter to iters.
            iters.Add(iter.Object);
            iters.Add(iter.Next);
        }

        // 13. Let iterCount be the number of elements in iters.
        int iterCount = iters.Count / 2;

        // 14. If mode is "longest", then
        if (zipMode == JSIteratorZipHelperMode.kLongest)
        {
            // a. If paddingOption is undefined, then
            //     i. Perform the following steps iterCount times:
            //         1. Append undefined to padding.
            padding = new FixedArray(iterCount);
            // b. Else,
            if (!paddingOption.IsUndefined)
            {
                // i. Let paddingIter be Completion(GetIterator(paddingOption, sync)).
                IteratorRecord paddingIter;
                try
                {
                    paddingIter = GetIterator(isolate, paddingOption);
                }
                catch (JavaScriptException)
                {
                    // ii. IfAbruptCloseIterators(paddingIter, iters).
                    IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                    throw;
                }
                // iii. Let usingIterator be true.
                bool usingPaddingIterator = true;
                // iv. Perform the following steps iterCount times:
                for (int j = 0; j < iterCount; ++j)
                {
                    // 1. If usingIterator is true, then
                    if (!usingPaddingIterator) continue;
                    try
                    {
                        // a. Set next to Completion(IteratorStepValue(paddingIter)).
                        // d. Else,
                        //     i. Append next to padding.
                        if (IteratorStepValue(isolate, paddingIter, fastIteratorResultMap, out JSValue paddingValue))
                        {
                            padding[j] = paddingValue;
                        }
                        else
                        {
                            // c. If next is done, then
                            //     i. Set usingIterator to false.
                            usingPaddingIterator = false;
                        }
                    }
                    catch (JavaScriptException)
                    {
                        // b. IfAbruptCloseIterators(next, iters).
                        IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                        throw;
                    }
                    // 2. If usingIterator is false, append undefined to padding.
                    // (The array is initialized with undefined elements.)
                }
                // v. If usingIterator is true, then
                if (usingPaddingIterator)
                {
                    try
                    {
                        // 1. Let completion be Completion(IteratorClose(paddingIter,
                        //    NormalCompletion(unused))).
                        IteratorClose(isolate, paddingIter);
                    }
                    catch (JavaScriptException)
                    {
                        // 2. IfAbruptCloseIterators(completion, iters).
                        IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                        throw;
                    }
                }
            }
        }

        // Steps 15 and 16 are implemented in IteratorZipHelperNext.
        var helper = (JSIteratorZipHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorZipHelperMap);
        InitializeZipHelper(helper, iters.ToFixedArray(), padding, zipMode);
        return helper;
    }

    static void InitializeZipHelper(JSIteratorZipHelper helper, FixedArray iterators, FixedArray padding, JSIteratorZipHelperMode mode)
    {
        helper.UnderlyingIterators = iterators;
        helper.Mode = mode;
        helper.ActiveCount = iterators.Length / 2;
        helper.Padding = padding;
    }

    /// <summary>
    /// IteratorZipHelperNextCommon: the next list of results; null when the
    /// zip is done (V8's Done label). Exceptions propagate (DoneWithException).
    /// </summary>
    static FixedArray? IteratorZipHelperNextCommon(Isolate isolate, JSIteratorZipHelper helper, string methodName)
    {
        // a. If iterCount = 0, return ReturnCompletion(undefined).
        if (helper.UnderlyingIterators.Length == 0)
        {
            MarkIteratorHelperAsExhausted(helper);
            return null;
        }

        int iterCount = helper.UnderlyingIterators.Length / 2;
        JSIteratorZipHelperMode mode = helper.Mode;
        MarkIteratorHelperAsExecuting(helper);
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;

        try
        {
            // i. Let results be a new empty List.
            var results = new FixedArray(iterCount);
            // ii. Assert: openIters is not empty.
            Debug.Assert(helper.ActiveCount > 0);
            // iii. For each integer i such that 0 ≤ i < iterCount, in ascending order,
            //      do
            for (int i = 0; i < iterCount; ++i)
            {
                JSValue result;
                FixedArray iterators = helper.UnderlyingIterators;
                JSValue iterObject = iterators[2 * i];
                JSValue nextMethod = iterators[2 * i + 1];
                // 1. Let iter be iters[i].
                // 2. If iter is null, then
                if (iterObject.IsTheHole)
                {
                    // a. Assert: mode is "longest".
                    Debug.Assert(mode == JSIteratorZipHelperMode.kLongest);
                    // b. Let result be padding[i].
                    result = helper.Padding[i];
                }
                else
                {
                    // 3. Else,
                    var iter = (JSReceiver)iterObject.Object;
                    bool gotValue;
                    try
                    {
                        // a. Let result be Completion(IteratorStepValue(iter)).
                        // c. Set result to ! result.
                        gotValue = IteratorStepValue(isolate, new IteratorRecord(iter, nextMethod), fastIteratorResultMap,
                            out result);
                    }
                    catch (JavaScriptException)
                    {
                        // b. If result is an abrupt completion, then
                        //    i. Remove iter from openIters.
                        //    ii. Return ? IteratorCloseAll(openIters, result).
                        iterators[2 * i] = JSValue.TheHole;
                        IteratorZipCloseAll(isolate, iterators, false);
                        MarkIteratorHelperAsExhausted(helper);
                        throw;
                    }
                    if (!gotValue)
                    {
                        // d. If result is done, then
                        //    i. Remove iter from openIters.
                        iterators[2 * i] = JSValue.TheHole;
                        helper.ActiveCount--;

                        if (mode == JSIteratorZipHelperMode.kShortest)
                        {
                            // ii. If mode is "shortest", then
                            //     i. Return ? IteratorCloseAll(openIters,
                            //        ReturnCompletion(undefined)).
                            IteratorZipCloseAll(isolate, iterators, true);
                            MarkIteratorHelperAsExhausted(helper);
                            return null;
                        }
                        else if (mode == JSIteratorZipHelperMode.kStrict)
                        {
                            // iii. Else if mode is "strict", then
                            if (i != 0)
                            {
                                // i. If i ≠ 0, then
                                //    i. Return ? IteratorCloseAll(openIters, ThrowCompletion(a
                                //       newly created TypeError object)).
                                IteratorZipCloseAll(isolate, iterators, false);
                                MarkIteratorHelperAsExhausted(helper);
                                isolate.ThrowTypeError(MessageTemplate.IteratorZipStrictMismatch,
                                    isolate.Factory.NewStringFromAsciiChecked(methodName));
                            }
                            // ii. For each integer k such that 1 ≤ k < iterCount, in ascending
                            //     order, do
                            for (int k = 1; k < iterCount; ++k)
                            {
                                var innerIterObject = (JSReceiver)iterators[2 * k].Object;
                                JSValue innerNextMethod = iterators[2 * k + 1];
                                bool open;
                                try
                                {
                                    // ii. Let open be Completion(IteratorStep(iters[k])).
                                    open = IteratorStep(isolate, new IteratorRecord(innerIterObject, innerNextMethod), out _,
                                        fastIteratorResultMap);
                                }
                                catch (JavaScriptException)
                                {
                                    // iii. If open is an abrupt completion, then
                                    //      i. Remove iters[k] from openIters.
                                    //      ii. Return ? IteratorCloseAll(openIters, open).
                                    iterators[2 * k] = JSValue.TheHole;
                                    IteratorZipCloseAll(isolate, iterators, false);
                                    MarkIteratorHelperAsExhausted(helper);
                                    throw;
                                }
                                if (open)
                                {
                                    // vi. Else,
                                    //     i. Return ? IteratorCloseAll(openIters, ThrowCompletion(a
                                    //        newly created TypeError object)).
                                    IteratorZipCloseAll(isolate, iterators, false);
                                    MarkIteratorHelperAsExhausted(helper);
                                    isolate.ThrowTypeError(MessageTemplate.IteratorZipStrictMismatch,
                                        isolate.Factory.NewStringFromAsciiChecked(methodName));
                                }
                                // v. If open is done, then
                                //    i. Remove iters[k] from openIters.
                                iterators[2 * k] = JSValue.TheHole;
                            }
                            // iii. Return ReturnCompletion(undefined).
                            MarkIteratorHelperAsExhausted(helper);
                            return null;
                        }
                        else
                        {
                            // iv. Else,
                            //     i. Assert: mode is "longest".
                            Debug.Assert(mode == JSIteratorZipHelperMode.kLongest);
                            if (helper.ActiveCount == 0)
                            {
                                // ii. If openIters is empty, return ReturnCompletion(undefined).
                                MarkIteratorHelperAsExhausted(helper);
                                return null;
                            }
                            // iii. Set iters[i] to null.
                            // iv. Set result to padding[i].
                            result = helper.Padding[i];
                        }
                    }
                }
                // 4. Append result to results.
                results[i] = result;
            }
            // (Steps iv and v are implemented in the "next" methods.)
            return results;
        }
        catch (JavaScriptException)
        {
            // vi. If completion is an abrupt completion, then
            //     1. Return ? IteratorCloseAll(openIters, completion).
            MarkIteratorHelperAsExhausted(helper);
            throw;
        }
    }

    static JSValue IteratorZipHelperNext(Isolate isolate, JSIteratorZipHelper helper)
    {
        FixedArray? results = IteratorZipHelperNextCommon(isolate, helper, "Iterator.zip");
        if (results is null) return CreateIterResultObject(isolate, JSValue.Undefined, true);

        // iv. Set results to finishResults(results).
        // Iterator.zip, abstract closure, step 15:
        //   a. Return CreateArrayFromList(results).
        JSArray finalResult = isolate.Factory.NewJSArrayWithElements(results);

        // v. Let completion be Completion(Yield(finalResult)).
        MarkIteratorHelperAsFinishedExecuting(helper);
        return CreateIterResultObject(isolate, finalResult, false);
    }

    /// <summary>Iterator.zipKeyed ( iterables [ , options ] ) (https://tc39.es/proposal-joint-iteration/#sec-iterator.zipkeyed).</summary>
    public static JSValue IteratorZipKeyed(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Iterator.zipKeyed";

        // 1. If iterables is not an Object, throw a TypeError exception.
        if (args.AtOrUndefined(1).HeapObjectOrNull is not JSReceiver iterablesObj) return ThrowCalledOnNonObject(isolate, methodName);

        JSIteratorZipHelperMode zipMode = GetZipModeAndPadding(isolate, args.AtOrUndefined(2), methodName, out JSValue paddingOption);

        // 8. Let iters be a new empty List.
        // 9. Let padding be a new empty List.
        var iters = new ValueList(8);
        FixedArray padding = FixedArray.Empty;

        // 10. Let allKeys be ? iterables.[[OwnPropertyKeys]]().
        FixedArray allKeys = KeyAccumulator.GetKeys(isolate, iterablesObj, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.ConvertToString);

        // 11. Let keys be a new empty List.
        var keys = new ValueList(8);

        // 12. For each element key of allKeys, do
        for (int i = 0; i < allKeys.Length; ++i)
        {
            JSValue key = allKeys[i];
            try
            {
                // a. Let desc be Completion(iterables.[[GetOwnProperty]](key)).
                // b. IfAbruptCloseIterators(desc, iters).
                var desc = new PropertyDescriptor();
                bool found = JSReceiver.GetOwnPropertyDescriptor(isolate, iterablesObj, key, ref desc);
                // c. If desc is not undefined and desc.[[Enumerable]] is true, then
                if (found && desc.HasEnumerable && desc.Enumerable)
                {
                    // i. Let value be Completion(Get(iterables, key)).
                    // ii. IfAbruptCloseIterators(value, iters).
                    JSValue value = ObjectOps.GetPropertyOrElement(isolate, iterablesObj, key.As<Name>());
                    // iii. If value is not undefined, then
                    if (!value.IsUndefined)
                    {
                        // 1. Append key to keys.
                        keys.Add(key);
                        // 2. Let iter be Completion(GetIteratorFlattenable(value,
                        //    reject-primitives)).
                        // 3. IfAbruptCloseIterators(iter, iters).
                        if (!value.IsJSReceiver) isolate.ThrowTypeError(MessageTemplate.NotIterable, value);
                        IteratorRecord iter = GetIteratorFlattenable(isolate, value);
                        // 4. Append iter to iters.
                        iters.Add(iter.Object);
                        iters.Add(iter.Next);
                    }
                }
            }
            catch (JavaScriptException)
            {
                // Steps 12.b, 12.c.ii, and 12.c.iii.3:
                // IfAbruptCloseIterators(..., iters).
                IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                throw;
            }
        }

        // 13. Let iterCount be the number of elements in iters.
        int iterCount = iters.Count / 2;

        // 14. If mode is "longest", then
        if (zipMode == JSIteratorZipHelperMode.kLongest)
        {
            // a. If paddingOption is undefined, then ... Append undefined to padding.
            padding = new FixedArray(iterCount);
            // b. Else,
            if (!paddingOption.IsUndefined)
            {
                // i. For each element key of keys, do
                for (int j = 0; j < iterCount; ++j)
                {
                    try
                    {
                        // 1. Let value be Completion(Get(paddingOption, key)).
                        // 3. Append value to padding.
                        padding[j] = ObjectOps.GetPropertyOrElement(isolate, paddingOption, keys[j].As<Name>());
                    }
                    catch (JavaScriptException)
                    {
                        // 2. IfAbruptCloseIterators(value, iters).
                        IteratorZipCloseAll(isolate, iters.ToFixedArray(), false);
                        throw;
                    }
                }
            }
        }

        // Steps 15 and 16 are implemented in IteratorZipKeyedHelperNext.
        var helper = (JSIteratorZipKeyedHelper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorZipKeyedHelperMap);
        InitializeZipHelper(helper, iters.ToFixedArray(), padding, zipMode);
        helper.Keys = keys.ToFixedArray();
        return helper;
    }

    static JSValue IteratorZipKeyedHelperNext(Isolate isolate, JSIteratorZipKeyedHelper helper)
    {
        FixedArray? results = IteratorZipHelperNextCommon(isolate, helper, "Iterator.zipKeyed");
        if (results is null) return CreateIterResultObject(isolate, JSValue.Undefined, true);

        // iv. Set results to finishResults(results).
        // Iterator.zipKeyed, abstract closure, step 15:
        // a. Let obj be OrdinaryObjectCreate(null).
        JSObject obj = isolate.Factory.NewSlowJSObjectWithNullProto();
        FixedArray keys = helper.Keys;
        // b. For each integer i such that 0 ≤ i < iterCount, in ascending order,
        //    do
        //    i. Perform ! CreateDataPropertyOrThrow(obj, keys[i], results[i]).
        for (int j = 0; j < keys.Length; ++j)
        {
            JSReceiver.CreateDataProperty(isolate, obj, keys[j].As<Name>(), results[j], ShouldThrow.ThrowOnError);
        }

        // v. Let completion be Completion(Yield(finalResult)).
        MarkIteratorHelperAsFinishedExecuting(helper);
        return CreateIterResultObject(isolate, obj, false);
    }

    // ---- Iterator.from (iterator-from.tq) --------------------------------------------------------------

    /// <summary>Iterator.from ( O ) (ES #sec-iterator.from).</summary>
    public static JSValue IteratorFrom(Isolate isolate, in BuiltinArguments args)
    {
        // GetIteratorFlattenable below accepts either Objects or Strings (without
        // wrapping) with the iterate-strings parameter.
        JSValue obj = args.AtOrUndefined(1);
        if (!obj.IsJSReceiver && !obj.IsString) return ThrowCalledOnNonObject(isolate, "Iterator.from");

        // 1. Let iteratorRecord be ? GetIteratorFlattenable(O, iterate-strings).
        IteratorRecord iteratorRecord = GetIteratorFlattenable(isolate, obj);

        // 2. Let hasInstance be ? OrdinaryHasInstance(%Iterator%,
        //    iteratorRecord.[[Iterator]]).
        bool hasInstance = ObjectOps.OrdinaryHasInstance(isolate, isolate.NativeContext.IteratorFunction, iteratorRecord.Object);

        // 3. If hasInstance is true, then
        //   a. Return iteratorRecord.[[Iterator]].
        if (hasInstance) return iteratorRecord.Object;

        // 4. Let wrapper be OrdinaryObjectCreate(%WrapForValidIteratorPrototype%, «
        //    [[Iterated]] »).
        // 5. Set wrapper.[[Iterated]] to iteratorRecord.
        // 6. Return wrapper.
        var wrapper = (JSValidIteratorWrapper)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.ValidIteratorWrapperMap);
        wrapper.UnderlyingObject = iteratorRecord.Object;
        wrapper.UnderlyingNext = iteratorRecord.Next;
        return wrapper;
    }

    /// <summary>%WrapForValidIteratorPrototype%.next ( ).</summary>
    public static JSValue WrapForValidIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be this value.
        // 2. Perform ? RequireInternalSlot(O, [[Iterated]]).
        if (args.Receiver.HeapObjectOrNull is not JSValidIteratorWrapper o)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("%WrapForValidIteratorPrototype%.next"), args.Receiver);
        }
        // 3. Let iteratorRecord be O.[[Iterated]].
        // 4. Return ? Call(iteratorRecord.[[NextMethod]],
        //    iteratorRecord.[[Iterator]]).
        return Execution.Call(isolate, o.UnderlyingNext, o.UnderlyingObject, []);
    }

    /// <summary>%WrapForValidIteratorPrototype%.return ( ).</summary>
    public static JSValue WrapForValidIteratorPrototypeReturn(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be this value.
        // 2. Perform ? RequireInternalSlot(O, [[Iterated]]).
        if (args.Receiver.HeapObjectOrNull is not JSValidIteratorWrapper o)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("%WrapForValidIteratorPrototype%.return"), args.Receiver);
        }

        // 3. Let iterator be O.[[Iterated]].[[Iterator]].
        JSReceiver iterator = o.UnderlyingObject;

        // 4. Assert: iterator is an Object.
        // 5. Let returnMethod be ? GetMethod(iterator, "return").
        JSValue returnMethod = GetMethod(isolate, iterator, ReadOnlyRoots.return_string);

        // 6. If returnMethod is undefined, then
        //   a. Return CreateIterResultObject(undefined, true).
        if (returnMethod.IsUndefined) return CreateIterResultObject(isolate, JSValue.Undefined, true);

        // 7. Return ? Call(returnMethod, iterator).
        return Execution.Call(isolate, returnMethod, iterator, []);
    }
}
