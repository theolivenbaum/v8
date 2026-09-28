// The helpers the Array and TypedArray builtins share, ported from the Torque
// and CSA macros they use: GetLengthProperty / SetPropertyLength (base.tq,
// builtins-array.cc), property access by Number index (GetProperty /
// SetProperty / HasProperty / DeleteProperty / FastCreateDataProperty),
// ConvertAndClampRelativeIndex (convert.tq), ArrayCreate, the iterator
// protocol pieces of builtins-iterator-gen.cc (GetIterator, IteratorStep,
// IteratorValue, IteratorClose, IterableToList) and the JSIteratorResult
// allocation of the CSA (AllocateJSIteratorResult[ForEntry]).
using System.Runtime.CompilerServices;

namespace V8Sharp.Builtins;


/// <summary>Helpers shared by the Array, TypedArray and ArrayBuffer builtins.</summary>
public static class ArrayBuiltinsUtils
{
    /// <summary>2^53 - 1.</summary>
    public const double kMaxSafeInteger = 9007199254740991.0;

    public static JSString NewString(Isolate isolate, string s) => isolate.Factory.NewStringFromAsciiChecked(s);

    /// <summary>GetLengthProperty: ToLength(Get(o, "length")), with the JSArray fast path.</summary>
    public static double GetLengthProperty(Isolate isolate, JSReceiver o)
    {
        if (o is JSArray array) return array.Length.Number;
        return ObjectOps.GetLengthFromArrayLike(isolate, o).Number;
    }

    /// <summary>SetPropertyLength (base.tq): Set(o, "length", length, true), with the JSArray fast path.</summary>
    public static void SetPropertyLength(Isolate isolate, JSReceiver o, double length)
    {
        if (o is JSArray array && !JSArray.HasReadOnlyLength(array) && length <= uint.MaxValue)
        {
            JSArray.SetLength(isolate, array, (uint)length);
            return;
        }
        ObjectOps.SetProperty(isolate, o, ReadOnlyRoots.length_string, JSValue.FromNumber(length), StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
    }

    /// <summary>GetProperty(o, k) for a Number key k (an integer index or larger).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue GetProperty(Isolate isolate, JSValue o, double k)
    {
        var key = new PropertyKey(isolate, k);
        var it = new LookupIterator(isolate, o, key);
        return ObjectOps.GetProperty(ref it);
    }

    public static JSValue GetProperty(Isolate isolate, JSValue o, JSValue key)
    {
        var propertyKey = new PropertyKey(isolate, ObjectOps.ToPropertyKey(isolate, key));
        var it = new LookupIterator(isolate, o, propertyKey);
        return ObjectOps.GetProperty(ref it);
    }

    /// <summary>SetProperty(o, k, v) with throw-on-error (Set(O, ! ToString(k), v, true)).</summary>
    public static void SetProperty(Isolate isolate, JSValue o, double k, JSValue value)
    {
        var key = new PropertyKey(isolate, k);
        var it = new LookupIterator(isolate, o, key);
        ObjectOps.SetProperty(ref it, value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
    }

    /// <summary>HasProperty(o, k).</summary>
    public static bool HasProperty(Isolate isolate, JSReceiver o, double k)
    {
        var key = new PropertyKey(isolate, k);
        var it = new LookupIterator(isolate, o, key, o);
        return JSReceiver.HasProperty(ref it);
    }

    /// <summary>DeletePropertyOrThrow(o, k).</summary>
    public static void DeletePropertyOrThrow(Isolate isolate, JSReceiver o, double k)
    {
        var key = new PropertyKey(isolate, k);
        JSReceiver.DeletePropertyOrElement(isolate, o, key, LanguageMode.Strict);
    }

    /// <summary>CreateDataPropertyOrThrow(o, k, v) (FastCreateDataProperty).</summary>
    public static void CreateDataPropertyOrThrow(Isolate isolate, JSReceiver o, double k, JSValue value)
    {
        var key = new PropertyKey(isolate, k);
        JSReceiver.CreateDataProperty(isolate, o, key, value, ShouldThrow.ThrowOnError);
    }

    /// <summary>
    /// ConvertAndClampRelativeIndex (convert.tq): ToIntegerOrInfinity, then
    /// relative-to-length clamping into [0, length].
    /// </summary>
    public static ulong ConvertAndClampRelativeIndex(Isolate isolate, JSValue index, ulong length)
    {
        double indexNumber = ObjectOps.IntegerValue(isolate, index);
        return ClampRelativeIndex(indexNumber, length);
    }

    /// <summary>ConvertAndClampRelativeIndex(Number, uintptr).</summary>
    public static ulong ClampRelativeIndex(double relativeIndex, ulong length)
    {
        if (relativeIndex < 0)
        {
            double added = length + relativeIndex;
            return added > 0 ? (ulong)added : 0;
        }
        return relativeIndex < length ? (ulong)relativeIndex : length;
    }

    /// <summary>ArrayCreate(length) (ES #sec-arraycreate), as the runtime's NewArray with a length.</summary>
    public static JSArray ArrayCreate(Isolate isolate, double length)
    {
        if (length > JSArray.kMaxArrayLength) isolate.ThrowRangeError(MessageTemplate.InvalidArrayLength);
        if (length == 0) return isolate.Factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS);
        JSArray array = isolate.Factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS);
        // A holey array of the given length; large lengths use dictionary elements.
        JSArray.SetLength(isolate, array, (uint)length);
        return array;
    }

    /// <summary>The "is a constructor" check of Torque's Cast&lt;Constructor&gt;.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsConstructor(JSValue value) => value.HeapObjectOrNull is JSReceiver r && r.Map.IsConstructor;

    /// <summary>ThrowCalledNonCallable.</summary>
    public static JSValue ThrowCalledNonCallable(Isolate isolate, JSValue value) =>
        isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, value));

    // ---- Iterator results --------------------------------------------------------------

    /// <summary>AllocateJSIteratorResult (CreateIterResultObject).</summary>
    public static JSObject CreateIterResultObject(Isolate isolate, JSValue value, bool done)
    {
        JSObject result = isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.IteratorResultMap);
        result.RawFields[0] = value;
        result.RawFields[1] = JSValue.FromBoolean(done);
        return result;
    }

    /// <summary>AllocateJSIteratorResultForEntry: {value: [key, value], done: false}.</summary>
    public static JSObject CreateIterResultObjectForEntry(Isolate isolate, JSValue key, JSValue value)
    {
        FixedArray elements = isolate.Factory.NewFixedArray(2);
        elements[0] = key;
        elements[1] = value;
        JSArray array = isolate.Factory.NewJSArrayWithElements(elements, ElementsKind.PACKED_ELEMENTS, 2);
        return CreateIterResultObject(isolate, array, false);
    }

    // ---- Iterator protocol (builtins-iterator-gen.cc) ----------------------------------------

    /// <summary>GetIteratorMethod: Get(object, @@iterator).</summary>
    public static JSValue GetIteratorMethod(Isolate isolate, JSValue obj) =>
        ObjectOps.GetPropertyOrElement(isolate, obj, new PropertyKey(isolate, ReadOnlyRoots.iterator_symbol));

    /// <summary>GetIterator(object, method).</summary>
    public static IteratorRecord GetIterator(Isolate isolate, JSValue obj, JSValue method)
    {
        if (!ObjectOps.IsCallable(method))
        {
            // Runtime::kThrowIteratorError
            isolate.Throw(ErrorUtils.NewIteratorError(isolate, obj));
        }
        JSValue iterator = Execution.Call(isolate, method, obj, []);
        if (iterator.HeapObjectOrNull is not JSReceiver iteratorObject)
        {
            // Runtime::kThrowSymbolIteratorInvalid
            isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);
            return default;
        }
        JSValue next = ObjectOps.GetProperty(isolate, iteratorObject, ReadOnlyRoots.next_string);
        return new IteratorRecord(iteratorObject, next);
    }

    /// <summary>
    /// IteratorStep: calls next; returns the result object, or null when the
    /// iterator is done.
    /// </summary>
    public static JSReceiver? IteratorStep(Isolate isolate, in IteratorRecord iterator)
    {
        // IteratorStep is used at the top of iterator loops, so check for stack
        // overflow and process pending interrupts here.
        isolate.StackGuard.StackCheck(isolate);
        // 1. a. Let result be ? Invoke(iterator, "next", « »).
        JSValue result = Execution.Call(isolate, iterator.Next, iterator.Object, []);
        // 3. If Type(result) is not Object, throw a TypeError exception.
        if (result.HeapObjectOrNull is not JSReceiver resultObject)
        {
            // Runtime::kThrowIteratorResultNotAnObject
            isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, result);
            return null;
        }
        // IteratorComplete: 2. Return ToBoolean(? Get(iterResult, "done")).
        JSValue done = ObjectOps.GetProperty(isolate, resultObject, ReadOnlyRoots.done_string);
        return ObjectOps.BooleanValue(done) ? null : resultObject;
    }

    /// <summary>IteratorValue: Get(result, "value").</summary>
    public static JSValue IteratorValue(Isolate isolate, JSReceiver result) =>
        ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.value_string);

    /// <summary>
    /// IteratorCloseOnException: calls iterator.return and ignores any result
    /// or exception from it (the original exception is rethrown by the caller).
    /// </summary>
    public static void IteratorCloseOnException(Isolate isolate, JSReceiver iterator)
    {
        try
        {
            JSValue method = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.return_string);
            if (method.IsNullOrUndefined) return;
            Execution.Call(isolate, method, iterator, []);
        }
        catch (JavaScriptException)
        {
            // Swallow: the pending exception of the caller wins.
        }
    }

    /// <summary>IteratorClose: calls iterator.return and checks that it returns an object.</summary>
    public static void IteratorClose(Isolate isolate, JSReceiver iterator)
    {
        JSValue method = ObjectOps.GetProperty(isolate, iterator, ReadOnlyRoots.return_string);
        if (method.IsNullOrUndefined) return;
        JSValue result = Execution.Call(isolate, method, iterator, []);
        if (!result.IsJSReceiver) isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, result);
    }

    /// <summary>
    /// IterableToList (ES #sec-iterabletolist): the values of the iterable, in
    /// a new PACKED_ELEMENTS JSArray. The iterator is closed when collecting a
    /// value throws (Iterate's exception handler).
    /// </summary>
    public static JSArray IterableToList(Isolate isolate, JSValue iterable, JSValue iteratorFn)
    {
        IteratorRecord iteratorRecord = GetIterator(isolate, iterable, iteratorFn);
        var values = new GrowableFixedArray();
        while (true)
        {
            JSReceiver? next = IteratorStep(isolate, iteratorRecord);
            if (next is null) break;
            JSValue nextValue = IteratorValue(isolate, next);
            values.Push(nextValue);
        }
        return values.ToJSArray(isolate);
    }
}

/// <summary>
/// GrowableFixedArray (growable-fixed-array.tq): a list of values that ends
/// up as the elements of a JSArray.
/// </summary>
public struct GrowableFixedArray
{
    JSValue[]? _array;
    int _length;

    /// <summary>A growable array starting from <paramref name="initial"/> as its (empty) storage.</summary>
    public GrowableFixedArray(JSValue[] initial)
    {
        _array = initial;
        _length = 0;
    }

    public readonly int Length => _length;

    /// <summary>The backing storage (may be longer than Length).</summary>
    public readonly JSValue[] RawArray => _array ?? [];

    public void Push(JSValue value)
    {
        if (_array is null)
        {
            _array = new JSValue[16];
        }
        else if (_length == _array.Length)
        {
            if (_length >= FixedArrayBase.kMaxLength) throw new InvalidOperationException("GrowableFixedArray: too long");
            // NewCapacity: capacity + (capacity >> 1) + 16, like CalculateNewElementsCapacity.
            int newCapacity = Math.Min(JSObject.NewElementsCapacity(_length), FixedArrayBase.kMaxLength);
            Array.Resize(ref _array, newCapacity);
        }
        _array[_length++] = value;
    }

    public readonly JSValue this[int index] => _array![index];

    /// <summary>ToFixedArray: a FixedArray of exactly the pushed values.</summary>
    public readonly FixedArray ToFixedArray()
    {
        if (_length == 0) return FixedArray.Empty;
        var data = new JSValue[_length];
        _array.AsSpan(0, _length).CopyTo(data);
        return new FixedArray(data);
    }

    /// <summary>ToJSArray: a PACKED_ELEMENTS array of the pushed values.</summary>
    public readonly JSArray ToJSArray(Isolate isolate)
    {
        FixedArray elements = ToFixedArray();
        return isolate.Factory.NewJSArrayWithElements(elements, ElementsKind.PACKED_ELEMENTS, _length);
    }
}
