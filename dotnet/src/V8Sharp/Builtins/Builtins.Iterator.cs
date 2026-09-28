// Port of src/builtins/builtins-iterator-gen.{h,cc} (IteratorBuiltinsAssembler:
// GetIterator, IteratorStep, IteratorComplete, IteratorValue, Iterate,
// IterableToList, IterableToFixedArray, StringListFromIterable,
// FastIterableToList, IterableToListWithSymbolLookup) and
// src/builtins/iterator.tq (IteratorRecord, IteratorStepValue,
// IteratorClose[OnException], the Iterator constructor, the
// Iterator.prototype accessors, [Symbol.dispose] and
// %AsyncIteratorPrototype%[Symbol.asyncDispose]).
using System.Runtime.CompilerServices;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterIterator()
    {
        Register(Builtin.IteratorConstructor, IteratorBuiltins.IteratorConstructor);
        Register(Builtin.IteratorPrototypeGetToStringTag, IteratorBuiltins.IteratorPrototypeGetToStringTag);
        Register(Builtin.IteratorPrototypeSetToStringTag, IteratorBuiltins.IteratorPrototypeSetToStringTag);
        Register(Builtin.IteratorPrototypeGetConstructor, IteratorBuiltins.IteratorPrototypeGetConstructor);
        Register(Builtin.IteratorPrototypeSetConstructor, IteratorBuiltins.IteratorPrototypeSetConstructor);
        Register(Builtin.IteratorPrototypeDispose, IteratorBuiltins.IteratorPrototypeDispose);
        Register(Builtin.AsyncIteratorPrototypeAsyncDispose, IteratorBuiltins.AsyncIteratorPrototypeAsyncDispose);
        Register(Builtin.AsyncIteratorPrototypeAsyncDisposeResolveClosure,
            IteratorBuiltins.AsyncIteratorPrototypeAsyncDisposeResolveClosure);
        RegisterIteratorHelpers();
        RegisterAsyncFromSyncIterator();
    }

    static partial void RegisterIteratorHelpers();
    static partial void RegisterAsyncFromSyncIterator();
}

/// <summary>Torque's iterator::IteratorRecord: [[Iterator]] and [[NextMethod]].</summary>
public readonly record struct IteratorRecord(JSReceiver Object, JSValue Next);

/// <summary>
/// V8's IteratorBuiltinsAssembler and the iterator namespace of iterator.tq:
/// the iteration protocol as used by engine code.
/// </summary>
public static partial class IteratorBuiltins
{
    // ---- GetIterator ---------------------------------------------------------------------------

    /// <summary>GetIteratorMethod: object[Symbol.iterator].</summary>
    public static JSValue GetIteratorMethod(Isolate isolate, JSValue obj) =>
        ObjectOps.GetProperty(isolate, obj, ReadOnlyRoots.iterator_symbol);

    /// <summary>GetIterator(object) (ES #sec-getiterator, sync).</summary>
    public static IteratorRecord GetIterator(Isolate isolate, JSValue obj)
    {
        JSValue method = GetIteratorMethod(isolate, obj);
        return GetIterator(isolate, obj, method);
    }

    /// <summary>GetIterator(object, method) (ES #sec-getiteratorfrommethod).</summary>
    public static IteratorRecord GetIterator(Isolate isolate, JSValue obj, JSValue method)
    {
        if (!ObjectOps.IsCallable(method))
        {
            // Runtime_ThrowIteratorError.
            isolate.Throw(ErrorUtils.NewIteratorError(isolate, obj));
        }
        JSValue iterator = Execution.Call(isolate, method, obj, []);
        if (iterator.HeapObjectOrNull is not JSReceiver iteratorReceiver)
        {
            // Runtime_ThrowSymbolIteratorInvalid.
            isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);
            return default;
        }
        JSValue next = JSReceiver.GetProperty(isolate, iteratorReceiver, ReadOnlyRoots.next_string);
        return new IteratorRecord(iteratorReceiver, next);
    }

    // ---- IteratorStep / IteratorComplete / IteratorValue ----------------------------------------

    /// <summary>
    /// IteratorStep: calls next and returns false (V8's Done label) when the
    /// result is done; otherwise <paramref name="result"/> is the result object.
    /// <paramref name="fastIteratorResultMap"/> is the iterator result map of
    /// the realm, whose done/value fields are read directly.
    /// </summary>
    public static bool IteratorStep(Isolate isolate, in IteratorRecord iterator, out JSReceiver result,
        Map? fastIteratorResultMap = null)
    {
        // IteratorStep is used at the top of iterator loops, so check for stack
        // overflow and process pending interrupts here.
        isolate.StackGuard.StackCheck(isolate);
        // 1. a. Let result be ? Invoke(iterator, "next", « »).
        JSValue value = Execution.Call(isolate, iterator.Next, iterator.Object, []);
        // 3. If Type(result) is not Object, throw a TypeError exception.
        if (value.HeapObjectOrNull is not JSReceiver receiver)
        {
            // Runtime_ThrowIteratorResultNotAnObject.
            isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, value);
            result = null!;
            return false;
        }
        result = receiver;
        // IteratorComplete
        // 2. Return ToBoolean(? Get(iterResult, "done")).
        return !IteratorComplete(isolate, receiver, fastIteratorResultMap);
    }

    /// <summary>IteratorComplete: ToBoolean(? Get(iterResult, "done")).</summary>
    public static bool IteratorComplete(Isolate isolate, JSReceiver iterResult, Map? fastIteratorResultMap = null)
    {
        JSValue done;
        if (fastIteratorResultMap is not null && ReferenceEquals(iterResult.Map, fastIteratorResultMap))
        {
            // Fast iterator result case.
            done = ((JSObject)iterResult).RawFields[kIteratorResultDoneIndex];
        }
        else
        {
            done = JSReceiver.GetProperty(isolate, iterResult, ReadOnlyRoots.done_string);
        }
        return ObjectOps.BooleanValue(done);
    }

    /// <summary>IteratorValue: ? Get(iterResult, "value").</summary>
    public static JSValue IteratorValue(Isolate isolate, JSReceiver result, Map? fastIteratorResultMap = null)
    {
        if (fastIteratorResultMap is not null && ReferenceEquals(result.Map, fastIteratorResultMap))
        {
            // Fast iterator result case.
            return ((JSObject)result).RawFields[kIteratorResultValueIndex];
        }
        return JSReceiver.GetProperty(isolate, result, ReadOnlyRoots.value_string);
    }

    /// <summary>
    /// IteratorStepValue (ES #sec-iteratorstepvalue): false when the iterator
    /// is done. Exceptions from next/done/value propagate (V8's
    /// DoneWithException label: the iterator counts as done, so callers must
    /// not close it).
    /// </summary>
    public static bool IteratorStepValue(Isolate isolate, in IteratorRecord iterated, Map? fastIteratorResultMap,
        out JSValue value)
    {
        // 1. Let result be ? IteratorStep(iteratorRecord)
        if (!IteratorStep(isolate, iterated, out JSReceiver result, fastIteratorResultMap))
        {
            value = JSValue.Undefined;
            return false;
        }
        // 3. Let value be Completion(IteratorValue(result))
        value = IteratorValue(isolate, result, fastIteratorResultMap);
        // 5. Return ? value
        return true;
    }

    /// <summary>The in-object field indices of a JSIteratorResult (the iterator_result_map: value, done).</summary>
    public const int kIteratorResultValueIndex = 0;
    public const int kIteratorResultDoneIndex = 1;

    /// <summary>
    /// CreateIterResultObject / Factory::NewJSIteratorResult: { value, done }
    /// with the realm's iterator result map.
    /// </summary>
    public static JSObject CreateIterResultObject(Isolate isolate, JSValue value, bool done)
    {
        JSObject result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorResultMap);
        JSValue[] fields = result.RawFields;
        fields[kIteratorResultValueIndex] = value;
        fields[kIteratorResultDoneIndex] = JSValue.FromBoolean(done);
        return result;
    }

    // ---- IteratorClose ----------------------------------------------------------------------------

    /// <summary>
    /// IteratorCloseOnException: calls "return" and swallows any exception it
    /// throws (the original exception remains bound).
    /// </summary>
    public static void IteratorCloseOnException(Isolate isolate, JSReceiver iteratorObject)
    {
        try
        {
            // 3. Let innerResult be GetMethod(iterator, "return").
            JSValue method = JSReceiver.GetProperty(isolate, iteratorObject, ReadOnlyRoots.return_string);
            // 4. If innerResult.[[Type]] is normal, then
            //   a. Let return be innerResult.[[Value]].
            //   b. If return is undefined, return Completion(completion).
            if (method.IsNullOrUndefined) return;
            //   c. Set innerResult to Call(return, iterator).
            // If an exception occurs, the original exception remains bound
            Execution.Call(isolate, method, iteratorObject, []);
        }
        catch (JavaScriptException)
        {
            // Swallow the exception.
        }
        // (5. If completion.[[Type]] is throw) return Completion(completion).
    }

    /// <summary>IteratorClose (ES #sec-iteratorclose) for a normal completion.</summary>
    public static void IteratorClose(Isolate isolate, in IteratorRecord iterator) => IteratorClose(isolate, iterator.Object);

    /// <summary>IteratorClose, also the IteratorClose builtin.</summary>
    public static void IteratorClose(Isolate isolate, JSReceiver iteratorObject)
    {
        // 3. Let innerResult be GetMethod(iterator, "return").
        JSValue method = JSReceiver.GetProperty(isolate, iteratorObject, ReadOnlyRoots.return_string);

        // 4. If innerResult.[[Type]] is normal, then
        //   a. Let return be innerResult.[[Value]].
        //   b. If return is undefined, return Completion(completion).
        if (method.IsNullOrUndefined) return;

        //   c. Set innerResult to Call(return, iterator).
        JSValue result = Execution.Call(isolate, method, iteratorObject, []);

        // 5. If completion.[[Type]] is throw, return Completion(completion).
        // It is handled in IteratorCloseOnException.

        // 7. If innerResult.[[Value]] is not an Object, throw a TypeError
        // exception.
        if (!result.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromUtf16("return"));
        }
    }

    // ---- Iterable to list --------------------------------------------------------------------------

    /// <summary>
    /// FillFixedArrayFromIterable via Iterate: the values of
    /// <paramref name="iterable"/>, iterated with <paramref name="iteratorFn"/>.
    /// </summary>
    static void FillListFromIterable(Isolate isolate, JSValue iterable, JSValue iteratorFn, ref ValueList values)
    {
        // 1. Let iteratorRecord be ? GetIterator(items, method).
        IteratorRecord iteratorRecord = GetIterator(isolate, iterable, iteratorFn);
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        // 4. Repeat, while next is not false
        //  a. Set next to ? IteratorStep(iteratorRecord).
        //  b. If next is not false, then
        //   i. Let nextValue be ? IteratorValue(next).
        //   ii. Append nextValue to the end of the List values.
        while (IteratorStep(isolate, iteratorRecord, out JSReceiver next, fastIteratorResultMap))
        {
            values.Add(IteratorValue(isolate, next, fastIteratorResultMap));
        }
    }

    /// <summary>IterableToList (ES #sec-iterabletolist).</summary>
    public static JSArray IterableToList(Isolate isolate, JSValue iterable, JSValue iteratorFn)
    {
        var values = new ValueList(16);
        FillListFromIterable(isolate, iterable, iteratorFn, ref values);
        return isolate.Factory.NewJSArrayWithElements(values.ToFixedArray());
    }

    /// <summary>IterableToFixedArray.</summary>
    public static FixedArray IterableToFixedArray(Isolate isolate, JSValue iterable, JSValue iteratorFn)
    {
        var values = new ValueList(16);
        FillListFromIterable(isolate, iterable, iteratorFn, ref values);
        return values.ToFixedArray();
    }

    /// <summary>IterableToFixedArrayWithSymbolLookupSlow.</summary>
    public static FixedArray IterableToFixedArrayWithSymbolLookupSlow(Isolate isolate, JSValue iterable) =>
        IterableToFixedArray(isolate, iterable, GetIteratorMethod(isolate, iterable));

    /// <summary>
    /// IterableToListWithSymbolLookup: the values of an iterable as a new
    /// JSArray, with fast paths for fast arrays (holes read as undefined) and
    /// for unmodified Map/Set iteration.
    /// </summary>
    public static JSArray IterableToListWithSymbolLookup(Isolate isolate, JSValue iterable)
    {
        if (FastIterableToList(isolate, iterable) is { } fast) return fast;
        JSValue iteratorFn = GetIteratorMethod(isolate, iterable);
        return IterableToList(isolate, iterable, iteratorFn);
    }

    /// <summary>
    /// FastIterableToList: null (V8's slow label) unless a fast path applies.
    /// The string fast path (StringToList) is not taken; the generic path
    /// produces the same list.
    /// </summary>
    public static JSArray? FastIterableToList(Isolate isolate, JSValue maybeIterable)
    {
        switch (maybeIterable.HeapObjectOrNull)
        {
            case JSArray array when IsFastJSArrayWithNoCustomIteration(isolate, array):
                // Fast path for fast JSArray.
                return CloneFastJSArrayFillingHoles(isolate, array);
            case JSObject obj:
                // Check IterableWithOriginalKeyOrValueMapIterator case.
                if (CollectionsBuiltins.IsIterableWithOriginalKeyOrValueMapIterator(isolate, obj))
                {
                    return CollectionsBuiltins.MapIteratorToList(isolate, (JSMapIterator)obj);
                }
                // Check IterableWithOriginalValueSetIterator case.
                if (CollectionsBuiltins.IsIterableWithOriginalValueSetIterator(isolate, obj))
                {
                    return CollectionsBuiltins.SetOrSetIteratorToList(isolate, obj);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>CloneFastJSArrayFillingHoles: a PACKED copy of a fast array, holes as undefined.</summary>
    public static JSArray CloneFastJSArrayFillingHoles(Isolate isolate, JSArray array)
    {
        int length = (int)array.Length.Number;
        var values = new FixedArray(length);
        switch (array.Elements)
        {
            case FixedArray elements:
            {
                int n = Math.Min(length, elements.Length);
                for (int k = 0; k < n; k++)
                {
                    JSValue v = elements[k];
                    values[k] = v.IsTheHole ? JSValue.Undefined : v;
                }
                break;
            }
            case FixedDoubleArray doubles:
            {
                int n = Math.Min(length, doubles.Length);
                for (int k = 0; k < n; k++)
                {
                    values[k] = doubles.IsTheHole(k) ? JSValue.Undefined : JSValue.FromNumber(doubles.GetScalar(k));
                }
                break;
            }
            default:
                for (int k = 0; k < length; k++) values[k] = JSReceiver.GetElement(isolate, array, (uint)k);
                break;
        }
        return isolate.Factory.NewJSArrayWithElements(values);
    }

    /// <summary>StringListFromIterable (ES #sec-createstringlistfromiterable).</summary>
    public static FixedArray StringListFromIterable(Isolate isolate, JSValue iterable)
    {
        // 1. If iterable is undefined, then
        //   a. Return a new empty List.
        if (iterable.IsUndefined) return FixedArray.Empty;

        // 2. Let iteratorRecord be ? GetIterator(items).
        IteratorRecord iteratorRecord = GetIterator(isolate, iterable);
        var list = new ValueList(8);
        // 5. Repeat, while next is not false.
        while (IteratorStep(isolate, iteratorRecord, out JSReceiver next))
        {
            JSValue nextValue = IteratorValue(isolate, next);
            try
            {
                //   ii. If Type(nextValue) is not String, then
                if (!nextValue.IsString)
                {
                    // 1. Let error be ThrowCompletion(a newly created TypeError object).
                    isolate.ThrowTypeError(MessageTemplate.IterableYieldedNonString, nextValue);
                }
            }
            catch (JavaScriptException)
            {
                // 2. Return ? IteratorClose(iteratorRecord, error).
                IteratorCloseOnException(isolate, iteratorRecord.Object);
                throw;
            }
            //   iii. Append nextValue to the end of the List list.
            list.Add(nextValue);
        }
        // 6. Return list.
        return list.ToFixedArray();
    }

    // ---- Fast array predicates (base.tq) -----------------------------------------------------------

    /// <summary>
    /// Cast&lt;FastJSArrayForRead&gt;: a JSArray with fast elements whose
    /// prototype is the initial Array.prototype and no elements on the
    /// prototype chain.
    /// </summary>
    public static bool IsFastJSArrayForRead(Isolate isolate, JSArray array)
    {
        Map map = array.Map;
        if (!ElementsKinds.IsFastElementsKind(map.ElementsKind)) return false;
        NativeContext nativeContext = isolate.NativeContext;
        if (!ReferenceEquals(map.Prototype, nativeContext.InitialArrayPrototype)) return false;
        return Protectors.IsNoElementsIntact(isolate);
    }

    /// <summary>
    /// Cast&lt;FastJSArrayWithNoCustomIteration&gt;: a fast JSArray for read
    /// whose iteration behaviour is the initial one (array iterator protector
    /// intact).
    /// </summary>
    public static bool IsFastJSArrayWithNoCustomIteration(Isolate isolate, JSArray array) =>
        IsFastJSArrayForRead(isolate, array) && Protectors.IsArrayIteratorLookupChainIntact(isolate);

    // ---- The Iterator constructor and Iterator.prototype accessors (iterator.tq) -------------------

    /// <summary>Iterator ( ) (ES #sec-iterator).</summary>
    public static JSValue IteratorConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSString methodName = isolate.Factory.NewStringFromUtf16("Iterator");
        // 1. If NewTarget is undefined or the active function object, throw a
        //    TypeError exception.
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction, methodName);
        }
        if (ReferenceEquals(args.NewTarget.HeapObjectOrNull, args.Target))
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructAbstractClass, methodName);
        }

        // 2. Return ? OrdinaryCreateFromConstructor(NewTarget,
        //    "%Iterator.prototype%").
        Map map = JSFunction.GetDerivedMap(isolate, args.Target, args.NewTarget.As<JSReceiver>());
        return JSObject.NewFastOrSlowJSObjectFromMap(isolate, map);
    }

    /// <summary>SetterThatIgnoresPrototypeProperties (ES #sec-SetterThatIgnoresPrototypeProperties).</summary>
    static void SetterThatIgnoresPrototypeProperties(Isolate isolate, JSValue receiver, JSObject home, Name key,
        JSValue value, string methodName)
    {
        // 1. If this is not an Object, then
        //    a. Throw a TypeError exception.
        if (receiver.HeapObjectOrNull is not JSReceiver o)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromUtf16(methodName));
            return;
        }

        // 2. If this is home, then
        //   a. NOTE: Throwing here emulates assignment to a non-writable data
        //   property on the home object in strict mode code. b. Throw a TypeError
        //   exception.
        if (ReferenceEquals(o, home))
        {
            isolate.ThrowTypeError(MessageTemplate.StrictReadOnlyProperty, key, ReadOnlyRoots.Object_string, home);
        }

        // 3. Let desc be ? this.[[GetOwnProperty]](p).
        bool hasOwn = JSReceiver.HasOwnProperty(isolate, o, key);

        // 4. If desc is undefined, then
        if (!hasOwn)
        {
            // a. Perform ? CreateDataPropertyOrThrow(this, p, v).
            JSReceiver.CreateDataProperty(isolate, o, key, value, ShouldThrow.ThrowOnError);
        }
        else
        {
            // 5. Else,
            //   a. Perform ? Set(this, p, v, true).
            ObjectOps.SetProperty(isolate, o, key, value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        }
    }

    /// <summary>get Iterator.prototype [ %Symbol.toStringTag% ].</summary>
    public static JSValue IteratorPrototypeGetToStringTag(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return "Iterator".
        ReadOnlyRoots.Iterator_string;

    /// <summary>set Iterator.prototype [ %Symbol.toStringTag% ].</summary>
    public static JSValue IteratorPrototypeSetToStringTag(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Perform ? SetterThatIgnoresPrototypeProperties(this value,
        // %Iterator.prototype%, %Symbol.toStringTag%, v).
        SetterThatIgnoresPrototypeProperties(isolate, args.Receiver, isolate.NativeContext.InitialIteratorPrototype,
            ReadOnlyRoots.to_string_tag_symbol, args.AtOrUndefined(1), "set Iterator.prototype[Symbol.toStringTag]");
        // 2. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>get Iterator.prototype.constructor.</summary>
    public static JSValue IteratorPrototypeGetConstructor(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return %Iterator%.
        isolate.NativeContext.IteratorFunction;

    /// <summary>set Iterator.prototype.constructor.</summary>
    public static JSValue IteratorPrototypeSetConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Perform ? SetterThatIgnoresPrototypeProperties(this value,
        // %Iterator.prototype%, "constructor", v).
        SetterThatIgnoresPrototypeProperties(isolate, args.Receiver, isolate.NativeContext.InitialIteratorPrototype,
            ReadOnlyRoots.constructor_string, args.AtOrUndefined(1), "set Iterator.prototype.constructor");
        // 2. Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>%Iterator.prototype% [ %Symbol.dispose% ] ( ).</summary>
    public static JSValue IteratorPrototypeDispose(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be the this value.
        // 2. Let return be ? GetMethod(O, "return").
        JSValue receiver = args.Receiver;
        JSValue returnMethod = GetMethod(isolate, receiver, ReadOnlyRoots.return_string);
        // 3. If return is not undefined, then
        //   a. Perform ? Call(return, O, « »).
        if (!returnMethod.IsUndefined) Execution.Call(isolate, returnMethod, receiver, []);
        // 4. Return NormalCompletion(undefined).
        return JSValue.Undefined;
    }

    /// <summary>
    /// GetMethod(V, P) (ES #sec-getmethod) on any value: undefined for
    /// undefined or null, TypeError for non-callables.
    /// </summary>
    public static JSValue GetMethod(Isolate isolate, JSValue receiver, Name name)
    {
        JSValue func = ObjectOps.GetProperty(isolate, receiver, name);
        if (func.IsNullOrUndefined) return JSValue.Undefined;
        if (!ObjectOps.IsCallable(func))
        {
            isolate.ThrowTypeError(MessageTemplate.PropertyNotFunction, func, name, receiver);
        }
        return func;
    }

    /// <summary>AsyncIteratorPrototypeAsyncDisposeResolveClosure: returns undefined.</summary>
    public static JSValue AsyncIteratorPrototypeAsyncDisposeResolveClosure(Isolate isolate, in BuiltinArguments args) =>
        JSValue.Undefined;

    /// <summary>%AsyncIteratorPrototype% [ %Symbol.asyncDispose% ] ( ).</summary>
    public static JSValue AsyncIteratorPrototypeAsyncDispose(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        // 1. Let O be the this value.
        // 2. Let promiseCapability be ! NewPromiseCapability(%Promise%).
        JSPromise capability = PromiseBuiltins.NewJSPromise(isolate);
        try
        {
            // 3. Let return be GetMethod(O, "return").
            // 4. IfAbruptRejectPromise(return, promiseCapability).
            JSValue returnMethod = GetMethod(isolate, receiver, ReadOnlyRoots.return_string);
            if (returnMethod.IsUndefined)
            {
                // 5. If return is undefined, then
                // a. Perform ! Call(promiseCapability.[[Resolve]], undefined, « undefined »).
                PromiseBuiltins.ResolvePromise(isolate, capability, JSValue.Undefined);
            }
            else
            {
                // 6. Else,
                // a. Let result be Call(return, O, « »).
                // b. IfAbruptRejectPromise(result, promiseCapability).
                JSValue result = Execution.Call(isolate, returnMethod, receiver, []);

                // c. Let resultWrapper be Completion(PromiseResolve(%Promise%, result)).
                // d. IfAbruptRejectPromise(resultWrapper, promiseCapability).
                NativeContext nativeContext = isolate.NativeContext;
                JSValue resultWrapper = PromiseBuiltins.PromiseResolve(isolate, nativeContext.PromiseFunction, result);

                // e. Let unwrap be a new Abstract Closure that performs the following
                // steps when called: i. Return undefined.
                // f. Let onFulfilled be CreateBuiltinFunction(unwrap, 1, "", « »).
                Context resolveContext = RootSharedFunctions.AllocateSyntheticFunctionContext(isolate, nativeContext,
                    (int)Context.Field.MIN_CONTEXT_SLOTS);
                JSFunction onFulfilled = RootSharedFunctions.AllocateRootFunctionWithContext(isolate,
                    Builtin.AsyncIteratorPrototypeAsyncDisposeResolveClosure, resolveContext, nativeContext);

                // g. Perform PerformPromiseThen(resultWrapper, onFulfilled, undefined,
                // promiseCapability).
                PromiseBuiltins.PerformPromiseThenImpl(isolate, resultWrapper.As<JSPromise>(), onFulfilled, JSValue.Undefined,
                    capability);
            }
            // 7. Return promiseCapability.[[Promise]].
            return capability;
        }
        catch (JavaScriptException e)
        {
            PromiseBuiltins.RejectPromise(isolate, capability, e.Value, false);
            return capability;
        }
    }
}

/// <summary>
/// GrowableFixedArray (growable-fixed-array-gen.h): an append-only list of
/// values that becomes a FixedArray.
/// </summary>
public struct ValueList(int initialCapacity)
{
    JSValue[] _data = initialCapacity == 0 ? [] : new JSValue[initialCapacity];
    int _count;

    public readonly int Count => _count;

    public readonly JSValue this[int index] => _data[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(JSValue value)
    {
        if (_count == _data.Length) Grow();
        _data[_count++] = value;
    }

    void Grow()
    {
        // GrowableFixedArray::NewCapacity: 1.5x + 16.
        int newCapacity = _data.Length + (_data.Length >> 1) + 16;
        Array.Resize(ref _data, newCapacity);
    }

    /// <summary>GrowableFixedArray::ToFixedArray.</summary>
    public readonly FixedArray ToFixedArray()
    {
        if (_count == 0) return FixedArray.Empty;
        if (_count == _data.Length) return new FixedArray(_data);
        var result = new JSValue[_count];
        Array.Copy(_data, result, _count);
        return new FixedArray(result);
    }
}

/// <summary>
/// The iteration helpers under their former name, for callers not yet
/// switched to <see cref="IteratorBuiltins"/>.
/// TODO(merge): remove once every area calls IteratorBuiltins.
/// </summary>
public static class IteratorHelpers
{
    public static IteratorRecord GetIterator(Isolate isolate, JSValue obj) => IteratorBuiltins.GetIterator(isolate, obj);

    public static IteratorRecord GetIterator(Isolate isolate, JSValue obj, JSValue method) =>
        IteratorBuiltins.GetIterator(isolate, obj, method);

    public static bool IteratorStep(Isolate isolate, in IteratorRecord iterator, out JSReceiver result) =>
        IteratorBuiltins.IteratorStep(isolate, iterator, out result);

    public static JSValue IteratorValue(Isolate isolate, JSReceiver result) => IteratorBuiltins.IteratorValue(isolate, result);

    public static void IteratorCloseOnException(Isolate isolate, JSReceiver iteratorObject) =>
        IteratorBuiltins.IteratorCloseOnException(isolate, iteratorObject);

    public static JSArray IterableToListWithSymbolLookup(Isolate isolate, JSValue iterable) =>
        IteratorBuiltins.IterableToListWithSymbolLookup(isolate, iterable);

    public static bool IsFastJSArrayForRead(Isolate isolate, JSArray array) => IteratorBuiltins.IsFastJSArrayForRead(isolate, array);

    public static bool IsFastJSArrayWithNoCustomIteration(Isolate isolate, JSArray array) =>
        IteratorBuiltins.IsFastJSArrayWithNoCustomIteration(isolate, array);
}
