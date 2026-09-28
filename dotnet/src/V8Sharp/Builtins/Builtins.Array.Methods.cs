// Port of the element-rearranging and searching Array builtins:
//   src/builtins/array-slice.tq, array-splice.tq, array-copywithin.tq,
//   array-reverse.tq, array-lastindexof.tq, array-flat.tq,
//   array-to-reversed.tq, array-to-spliced.tq, array-with.tq
//   src/builtins/builtins-array-gen.cc  ArrayIncludes / ArrayIndexOf
//     (ArrayIncludesIndexofAssembler), with the generic paths of
//     src/runtime/runtime-array.cc Runtime_ArrayIncludes_Slow and
//     Runtime_ArrayIndexOf
// The fast paths that V8 takes for fast JSArrays are unobservable; the ones
// that matter for speed (slice/splice of fast arrays, the typed searches of
// includes/indexOf) are kept, the rest run the generic algorithm with the
// fast element probe of Builtins.Array.Iterating.cs.
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArrayMethods()
    {
        Register(Builtin.ArrayPrototypeSlice, BuiltinsArray.ArrayPrototypeSlice);
        Register(Builtin.ArrayPrototypeSplice, BuiltinsArray.ArrayPrototypeSplice);
        Register(Builtin.ArrayPrototypeCopyWithin, BuiltinsArray.ArrayPrototypeCopyWithin);
        Register(Builtin.ArrayPrototypeReverse, BuiltinsArray.ArrayPrototypeReverse);
        Register(Builtin.ArrayIncludes, BuiltinsArray.ArrayIncludes);
        Register(Builtin.ArrayIndexOf, BuiltinsArray.ArrayIndexOf);
        Register(Builtin.ArrayPrototypeLastIndexOf, BuiltinsArray.ArrayPrototypeLastIndexOf);
        Register(Builtin.ArrayPrototypeFlat, BuiltinsArray.ArrayPrototypeFlat);
        Register(Builtin.ArrayPrototypeFlatMap, BuiltinsArray.ArrayPrototypeFlatMap);
        Register(Builtin.ArrayPrototypeToReversed, BuiltinsArray.ArrayPrototypeToReversed);
        Register(Builtin.ArrayPrototypeToSpliced, BuiltinsArray.ArrayPrototypeToSpliced);
        Register(Builtin.ArrayPrototypeWith, BuiltinsArray.ArrayPrototypeWith);
    }
}

public static partial class BuiltinsArray
{
    /// <summary>Relative-index clamping of slice/splice: max(len + rel, 0) or min(rel, len).</summary>
    static double ClampStart(double relative, double len) =>
        relative < 0 ? Math.Max(len + relative, 0) : Math.Min(relative, len);

    /// <summary>ExtractFastJSArray: a new array of [start, start + count) with the same elements kind.</summary>
    static JSArray ExtractFastJSArray(Isolate isolate, JSArray array, int start, int count)
    {
        ElementsKind kind = array.GetElementsKind();
        if (count == 0) return isolate.Factory.NewJSArray(kind);
        FixedArrayBase elements = CopyElementsRange(array.Elements, start, count, count);
        return isolate.Factory.NewJSArrayWithElements(elements, kind, count);
    }

    // ---- slice ----------------------------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.slice.</summary>
    public static JSValue ArrayPrototypeSlice(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3-4.
        JSValue start = args.AtOrUndefined(1);
        double relativeStart = ObjectOps.IntegerValue(isolate, start);
        double k = ClampStart(relativeStart, len);
        // 5.
        JSValue end = args.AtOrUndefined(2);
        double relativeEnd = end.IsUndefined ? len : ObjectOps.IntegerValue(isolate, end);

        // Handle array cloning case if the receiver is a fast array.
        if ((start.IsUndefined || (start.IsSmi && start.Number == 0 && !double.IsNegative(start.Number))) && end.IsUndefined &&
            IsFastJSArrayForCopy(isolate, receiver, out JSArray fastReceiver))
        {
            return CloneFastJSArray(isolate, fastReceiver);
        }

        // 6.
        double final = ClampStart(relativeEnd, len);
        // 7. Let count be max(final - k, 0).
        double count = Math.Max(final - k, 0);

        // HandleFastSlice (fast arrays; arguments objects take the generic path,
        // which produces the same array).
        if (k <= JSValue.SmiMaxValue && count <= JSValue.SmiMaxValue &&
            IsFastJSArrayForCopy(isolate, o, out JSArray fastArray) && k + count <= fastArray.Length.Number)
        {
            return ExtractFastJSArray(isolate, fastArray, (int)k, (int)count);
        }

        // 8. Let A be ? ArraySpeciesCreate(O, count).
        JSReceiver a = ArraySpeciesCreate(isolate, o, count);
        // 9. Let n be 0.
        double n = 0;
        // 10. Repeat, while k < final
        while (k < final)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, a, n, kValue);
            }
            k++;
            n++;
        }
        // 11. Perform ? Set(A, "length", n, true).
        ArrayBuiltinsUtils.SetPropertyLength(isolate, a, n);
        return a;
    }

    // ---- splice ---------------------------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.splice.</summary>
    public static JSValue ArrayPrototypeSplice(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3-4.
        double relativeStart = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(1));
        double actualStart = ClampStart(relativeStart, len);

        int argc = args.ArgcWithoutReceiver;
        int insertCount;
        double actualDeleteCount;
        if (argc == 0)
        {
            insertCount = 0;
            actualDeleteCount = 0;
        }
        else if (argc == 1)
        {
            insertCount = 0;
            actualDeleteCount = len - actualStart;
        }
        else
        {
            insertCount = argc - 2;
            double dc = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(2));
            actualDeleteCount = Math.Min(Math.Max(dc, 0), len - actualStart);
        }

        // 8. If len + insertCount - actualDeleteCount > 2^53-1, throw a TypeError exception.
        double newLength = len + insertCount - actualDeleteCount;
        if (newLength > ArrayBuiltinsUtils.kMaxSafeInteger)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidArrayLength, JSValue.FromNumber(newLength));
        }

        ReadOnlySpan<JSValue> items = argc > 2 ? args.Arguments[2..] : [];
        if (TryFastArraySplice(isolate, o, len, actualStart, items, actualDeleteCount, out JSArray? deleted))
        {
            return deleted!;
        }
        return SlowSplice(isolate, o, len, actualStart, items, actualDeleteCount);
    }

    /// <summary>FastArraySplice; false on Bailout.</summary>
    static bool TryFastArraySplice(Isolate isolate, JSReceiver o, double originalLengthNumber, double actualStartNumber,
        ReadOnlySpan<JSValue> items, double actualDeleteCountNumber, out JSArray? deletedResult)
    {
        deletedResult = null;
        if (originalLengthNumber > JSValue.SmiMaxValue) return false;
        if (o is not JSArray a) return false;
        Map map = a.Map;
        if (!IsPrototypeInitialArrayPrototype(isolate, map)) return false;
        if (!Protectors.IsNoElementsIntact(isolate)) return false;
        if (!Protectors.IsArraySpeciesLookupChainIntact(isolate)) return false;
        // EnsureArrayPushable.
        if (map.IsDictionaryMap || !map.IsExtensible || JSArray.HasReadOnlyLength(a)) return false;
        if (!ElementsKinds.IsFastElementsKind(map.ElementsKind)) return false;

        int originalLength = (int)originalLengthNumber;
        int actualStart = (int)actualStartNumber;
        int actualDeleteCount = (int)actualDeleteCountNumber;
        int insertCount = items.Length;
        int newLength = originalLength + insertCount - actualDeleteCount;

        // TransitionElementsKindForInsertionIfNeeded.
        MatchArrayElementsKindToArguments(isolate, a, items);

        int length = (int)a.Length.Number;
        if (originalLength != length) return false;

        deletedResult = ExtractFastJSArray(isolate, a, actualStart, actualDeleteCount);

        if (newLength == 0)
        {
            a.Elements = FixedArray.Empty;
            a.Length = JSValue.Zero;
            return true;
        }

        JSObject.EnsureWritableFastElements(isolate, a);
        FixedArrayBase elements = a.Elements;
        if (insertCount != actualDeleteCount)
        {
            int dstIndex = actualStart + insertCount;
            int srcIndex = actualStart + actualDeleteCount;
            int count = length - actualDeleteCount - actualStart;
            if (insertCount < actualDeleteCount)
            {
                MoveElements(elements, dstIndex, srcIndex, count);
                StoreHoles(elements, newLength, length);
            }
            else if (newLength <= elements.Length)
            {
                MoveElements(elements, dstIndex, srcIndex, count);
            }
            else
            {
                int capacity = JSObject.NewElementsCapacity(newLength);
                FixedArrayBase newElements = elements.Length == 0 && ElementsKinds.IsDoubleElementsKind(a.GetElementsKind())
                    ? FixedDoubleArray.NewWithHoles(capacity)
                    : CopyElementsRange(elements, 0, actualStart, capacity);
                if (elements.Length > 0) CopyElementsBetween(elements, srcIndex, newElements, dstIndex, count);
                a.Elements = newElements;
                elements = newElements;
            }
        }

        // InsertArgumentsIntoFastPackedArray.
        int k = actualStart;
        if (elements is FixedDoubleArray doubles)
        {
            foreach (JSValue e in items) doubles.Set(k++, e.Number);
        }
        else
        {
            JSValue[] data = ((FixedArray)elements).Data;
            foreach (JSValue e in items) data[k++] = e;
        }
        a.Length = JSValue.FromInt(newLength);
        return true;
    }

    static void MoveElements(FixedArrayBase elements, int dstIndex, int srcIndex, int count)
    {
        if (count <= 0) return;
        if (elements is FixedDoubleArray d) Array.Copy(d.Data, srcIndex, d.Data, dstIndex, count);
        else Array.Copy(((FixedArray)elements).Data, srcIndex, ((FixedArray)elements).Data, dstIndex, count);
    }

    static void CopyElementsBetween(FixedArrayBase source, int srcIndex, FixedArrayBase target, int dstIndex, int count)
    {
        if (count <= 0) return;
        if (source is FixedDoubleArray d) Array.Copy(d.Data, srcIndex, ((FixedDoubleArray)target).Data, dstIndex, count);
        else Array.Copy(((FixedArray)source).Data, srcIndex, ((FixedArray)target).Data, dstIndex, count);
    }

    static void StoreHoles(FixedArrayBase elements, int from, int to)
    {
        if (elements is FixedDoubleArray d) d.FillWithHoles(from, to);
        else ((FixedArray)elements).FillWithHoles(from, to);
    }

    /// <summary>SlowSplice.</summary>
    static JSValue SlowSplice(Isolate isolate, JSReceiver o, double len, double actualStart, ReadOnlySpan<JSValue> items,
        double actualDeleteCount)
    {
        // 9. Let A be ? ArraySpeciesCreate(O, actualDeleteCount).
        JSReceiver a = ArraySpeciesCreate(isolate, o, actualDeleteCount);
        double itemCount = items.Length;

        // FillDeletedElementsArray (steps 10-12).
        for (double k = 0; k < actualDeleteCount; k++)
        {
            double from = actualStart + k;
            if (ArrayBuiltinsUtils.HasProperty(isolate, o, from))
            {
                JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, o, from);
                ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, a, k, fromValue);
            }
        }
        ArrayBuiltinsUtils.SetPropertyLength(isolate, a, actualDeleteCount);

        if (itemCount < actualDeleteCount)
        {
            // HandleForwardCase.
            double k = actualStart;
            while (k < len - actualDeleteCount)
            {
                double from = k + actualDeleteCount;
                double to = k + itemCount;
                if (ArrayBuiltinsUtils.HasProperty(isolate, o, from))
                {
                    JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, o, from);
                    ArrayBuiltinsUtils.SetProperty(isolate, o, to, fromValue);
                }
                else
                {
                    ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, o, to);
                }
                k++;
            }
            k = len;
            while (k > len - actualDeleteCount + itemCount)
            {
                ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, o, k - 1);
                k--;
            }
        }
        else if (itemCount > actualDeleteCount)
        {
            // HandleBackwardCase.
            double k = len - actualDeleteCount;
            while (k > actualStart)
            {
                double from = k + actualDeleteCount - 1;
                double to = k + itemCount - 1;
                if (ArrayBuiltinsUtils.HasProperty(isolate, o, from))
                {
                    JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, o, from);
                    ArrayBuiltinsUtils.SetProperty(isolate, o, to, fromValue);
                }
                else
                {
                    ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, o, to);
                }
                k--;
            }
        }

        double index = actualStart;
        foreach (JSValue e in items)
        {
            ArrayBuiltinsUtils.SetProperty(isolate, o, index, e);
            index++;
        }
        ArrayBuiltinsUtils.SetPropertyLength(isolate, o, len - actualDeleteCount + itemCount);
        return a;
    }

    // ---- copyWithin -----------------------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.copyWithin.</summary>
    public static JSValue ArrayPrototypeCopyWithin(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        // 3-4.
        double relativeTarget = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(1));
        double to = ClampStart(relativeTarget, length);
        // 5-6.
        double relativeStart = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(2));
        double from = ClampStart(relativeStart, length);
        // 7-8.
        JSValue end = args.AtOrUndefined(3);
        double relativeEnd = end.IsUndefined ? length : ObjectOps.IntegerValue(isolate, end);
        double final = ClampStart(relativeEnd, length);
        // 9. Let count be min(final-from, len-to).
        double count = Math.Min(final - from, length - to);

        if (TryFastArrayCopyWithin(isolate, args.Receiver, to, from, count)) return obj;

        // 10-11.
        double direction = 1;
        if (from < to && to < from + count)
        {
            direction = -1;
            from = from + count - 1;
            to = to + count - 1;
        }
        // 12. Repeat, while count > 0
        while (count > 0)
        {
            if (ArrayBuiltinsUtils.HasProperty(isolate, obj, from))
            {
                JSValue fromVal = ArrayBuiltinsUtils.GetProperty(isolate, obj, from);
                ArrayBuiltinsUtils.SetProperty(isolate, obj, to, fromVal);
            }
            else
            {
                ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, to);
            }
            from += direction;
            to += direction;
            --count;
        }
        return obj;
    }

    /// <summary>TryFastArrayCopyWithin.</summary>
    static bool TryFastArrayCopyWithin(Isolate isolate, JSValue receiver, double to, double from, double count)
    {
        if (count <= 0) return true;
        if (!IsFastJSArray(isolate, receiver, out JSArray array)) return false;
        if (to > JSValue.SmiMaxValue || from > JSValue.SmiMaxValue || count > JSValue.SmiMaxValue) return false;
        double length = array.Length.Number;
        if (to + count > length || from + count > length) return false;
        // IsFastJSArray already requires the initial prototype and the NoElements protector.
        JSObject.EnsureWritableFastElements(isolate, array);
        MoveElements(array.Elements, (int)to, (int)from, (int)count);
        return true;
    }

    // ---- reverse --------------------------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.reverse.</summary>
    public static JSValue ArrayPrototypeReverse(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        // TryFastPackedArrayReverse.
        if (IsFastJSArray(isolate, receiver, out JSArray array))
        {
            JSObject.EnsureWritableFastElements(isolate, array);
            int length = (int)array.Length.Number;
            if (array.Elements is FixedDoubleArray d) d.Data.AsSpan(0, length).Reverse();
            else ((FixedArray)array.Elements).Data.AsSpan(0, length).Reverse();
            return receiver;
        }
        return GenericArrayReverse(isolate, receiver);
    }

    /// <summary>GenericArrayReverse.</summary>
    static JSValue GenericArrayReverse(Isolate isolate, JSValue receiver)
    {
        JSReceiver obj = ObjectOps.ToObject(isolate, receiver);
        double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        double lower = 0;
        double upper = length - 1;
        while (lower < upper)
        {
            JSValue lowerValue = JSValue.Undefined;
            JSValue upperValue = JSValue.Undefined;
            bool lowerExists = ArrayBuiltinsUtils.HasProperty(isolate, obj, lower);
            if (lowerExists) lowerValue = ArrayBuiltinsUtils.GetProperty(isolate, obj, lower);
            bool upperExists = ArrayBuiltinsUtils.HasProperty(isolate, obj, upper);
            if (upperExists) upperValue = ArrayBuiltinsUtils.GetProperty(isolate, obj, upper);

            if (lowerExists && upperExists)
            {
                ArrayBuiltinsUtils.SetProperty(isolate, obj, lower, upperValue);
                ArrayBuiltinsUtils.SetProperty(isolate, obj, upper, lowerValue);
            }
            else if (!lowerExists && upperExists)
            {
                ArrayBuiltinsUtils.SetProperty(isolate, obj, lower, upperValue);
                ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, upper);
            }
            else if (lowerExists && !upperExists)
            {
                ArrayBuiltinsUtils.DeletePropertyOrThrow(isolate, obj, lower);
                ArrayBuiltinsUtils.SetProperty(isolate, obj, upper, lowerValue);
            }
            ++lower;
            --upper;
        }
        return obj;
    }

    // ---- includes / indexOf -----------------------------------------------------------------------------

    /// <summary>The length of O for includes/indexOf: a JSArray's length is read directly.</summary>
    static double IncludesIndexOfLength(Isolate isolate, JSReceiver o) =>
        o is JSArray array ? array.Length.Number : ArrayBuiltinsUtils.GetLengthProperty(isolate, o);

    /// <summary>ES #sec-array.prototype.includes.</summary>
    public static JSValue ArrayIncludes(Isolate isolate, in BuiltinArguments args)
    {
        JSValue searchElement = args.AtOrUndefined(1);
        JSValue fromIndex = args.AtOrUndefined(2);
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        double len = IncludesIndexOfLength(isolate, o);
        if (len == 0) return JSValue.False;

        double index = 0;
        if (!fromIndex.IsUndefined)
        {
            double startFrom = ObjectOps.IntegerValue(isolate, fromIndex);
            if (startFrom >= len) return JSValue.False;
            if (double.IsFinite(startFrom))
            {
                index = startFrom < 0 ? Math.Max(startFrom + len, 0) : startFrom;
            }
        }

        if (IsFastJSArrayForRead(isolate, o, out JSArray array) && len <= array.Elements.Length)
        {
            return JSValue.FromBoolean(FastIncludes(array.Elements, searchElement, (int)index, (int)len));
        }

        for (; index < len; ++index)
        {
            JSValue elementK = GetElementValue(isolate, o, index);
            if (ObjectOps.SameValueZero(searchElement, elementK)) return JSValue.True;
        }
        return JSValue.False;
    }

    /// <summary>ArrayIncludesIndexofAssembler (kIncludes): SameValueZero, holes read as undefined.</summary>
    static bool FastIncludes(FixedArrayBase elements, JSValue search, int from, int len)
    {
        if (elements is FixedDoubleArray doubles)
        {
            ReadOnlySpan<double> data = doubles.Data.AsSpan(from, len - from);
            if (search.IsUndefined)
            {
                // Only a hole reads as undefined.
                for (int i = 0; i < data.Length; i++)
                {
                    if (FixedDoubleArray.IsHoleBits(data[i])) return true;
                }
                return false;
            }
            if (!search.IsNumber) return false;
            double s = search.Number;
            if (double.IsNaN(s))
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (double.IsNaN(data[i]) && !FixedDoubleArray.IsHoleBits(data[i])) return true;
                }
                return false;
            }
            // double.Equals treats +0 and -0 as equal, and the hole NaN never equals s.
            return data.IndexOf(s) >= 0;
        }
        ReadOnlySpan<JSValue> values = ((FixedArray)elements).Data.AsSpan(from, len - from);
        if (search.IsUndefined)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].IsUndefined || values[i].IsTheHole) return true;
            }
            return false;
        }
        for (int i = 0; i < values.Length; i++)
        {
            JSValue v = values[i];
            if (!v.IsTheHole && ObjectOps.SameValueZero(search, v)) return true;
        }
        return false;
    }

    /// <summary>ES #sec-array.prototype.indexof.</summary>
    public static JSValue ArrayIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        JSValue searchElement = args.AtOrUndefined(1);
        JSValue fromIndex = args.AtOrUndefined(2);
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver, "Array.prototype.indexOf");
        double len = IncludesIndexOfLength(isolate, o);
        if (len == 0) return JSValue.FromInt(-1);

        double fp = ObjectOps.IntegerValue(isolate, fromIndex);
        if (fp > len) return JSValue.FromInt(-1);
        double index = fp >= 0 ? fp : Math.Max(len + fp, 0);

        if (IsFastJSArrayForRead(isolate, o, out JSArray array) && len <= array.Elements.Length)
        {
            return JSValue.FromInt(FastIndexOf(array.Elements, searchElement, (int)index, (int)len));
        }

        for (; index < len; ++index)
        {
            if (TryGetPresentElement(isolate, o, index, out JSValue elementK) && ObjectOps.StrictEquals(searchElement, elementK))
            {
                return JSValue.FromNumber(index);
            }
        }
        return JSValue.FromInt(-1);
    }

    /// <summary>ArrayIncludesIndexofAssembler (kIndexOf): strict equality, holes skipped.</summary>
    static int FastIndexOf(FixedArrayBase elements, JSValue search, int from, int len)
    {
        if (from >= len) return -1;
        if (elements is FixedDoubleArray doubles)
        {
            if (!search.IsNumber) return -1;
            double s = search.Number;
            if (double.IsNaN(s)) return -1;
            int i = doubles.Data.AsSpan(from, len - from).IndexOf(s);
            return i < 0 ? -1 : from + i;
        }
        ReadOnlySpan<JSValue> values = ((FixedArray)elements).Data.AsSpan(from, len - from);
        for (int i = 0; i < values.Length; i++)
        {
            JSValue v = values[i];
            if (!v.IsTheHole && ObjectOps.StrictEquals(search, v)) return from + i;
        }
        return -1;
    }

    // ---- lastIndexOf ------------------------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.lastIndexOf.</summary>
    public static JSValue ArrayPrototypeLastIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        // 3. If len is 0, return -1.
        if (length == 0) return JSValue.FromInt(-1);
        // 4-6. GetFromIndex.
        double n = args.ArgcWithoutReceiver < 2 ? length - 1 : ObjectOps.IntegerValue(isolate, args.AtOrUndefined(2));
        double from = n >= 0 ? Math.Min(n, length - 1) : length + n;
        JSValue searchElement = args.AtOrUndefined(1);

        // GenericArrayLastIndexOf (with the fast element probe).
        for (double k = from; k >= 0; --k)
        {
            if (TryGetPresentElement(isolate, obj, k, out JSValue element) && ObjectOps.StrictEquals(searchElement, element))
            {
                return JSValue.FromNumber(k);
            }
        }
        return JSValue.FromInt(-1);
    }

    // ---- flat / flatMap -----------------------------------------------------------------------------------

    /// <summary>FlattenIntoArray (the slow path; the fast one differs only in speed).</summary>
    static double FlattenIntoArray(Isolate isolate, JSReceiver target, JSReceiver source, double sourceLength, double start,
        int depth, bool hasMapper, JSValue mapfn, JSValue thisArgs)
    {
        isolate.StackGuard.StackCheck(isolate);
        double targetIndex = start;
        for (double sourceIndex = 0; sourceIndex < sourceLength; sourceIndex++)
        {
            if (!TryGetPresentElement(isolate, source, sourceIndex, out JSValue element)) continue;
            if (hasMapper)
            {
                element = Execution.Call(isolate, mapfn, thisArgs, [element, JSValue.FromNumber(sourceIndex), source]);
            }
            bool shouldFlatten = depth > 0 && ObjectOps.IsArray(isolate, element);
            if (shouldFlatten)
            {
                var elementReceiver = element.As<JSReceiver>();
                double elementLength = ArrayBuiltinsUtils.GetLengthProperty(isolate, elementReceiver);
                targetIndex = FlattenIntoArray(isolate, target, elementReceiver, elementLength, targetIndex, depth - 1,
                    false, JSValue.Undefined, JSValue.Undefined);
            }
            else
            {
                if (targetIndex >= ArrayBuiltinsUtils.kMaxSafeInteger)
                {
                    isolate.ThrowTypeError(MessageTemplate.FlattenPastSafeLength, JSValue.FromNumber(sourceLength),
                        JSValue.FromNumber(targetIndex));
                }
                ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, target, targetIndex, element);
                targetIndex++;
            }
        }
        return targetIndex;
    }

    /// <summary>ES #sec-array.prototype.flat.</summary>
    public static JSValue ArrayPrototypeFlat(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let sourceLen be ? ToLength(? Get(O, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3-4. Let depthNum be 1, or ? ToInteger(depth).
        double depthNum = 1;
        JSValue depth = args.AtOrUndefined(1);
        if (!depth.IsUndefined) depthNum = ObjectOps.IntegerValue(isolate, depth);
        int depthSmi = depthNum <= 0 ? 0 : depthNum > JSValue.SmiMaxValue ? JSValue.SmiMaxValue : (int)depthNum;
        // 5. Let A be ? ArraySpeciesCreate(O, 0).
        JSReceiver a = ArraySpeciesCreate(isolate, o, 0);
        // 6. Perform ? FlattenIntoArray(A, O, sourceLen, 0, depthNum).
        FlattenIntoArray(isolate, a, o, len, 0, depthSmi, false, JSValue.Undefined, JSValue.Undefined);
        return a;
    }

    /// <summary>ES #sec-array.prototype.flatMap.</summary>
    public static JSValue ArrayPrototypeFlatMap(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let sourceLen be ? ToLength(? Get(O, "length")).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3. If IsCallable(mapperFunction) is false, throw a TypeError exception.
        JSValue mapfn = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(mapfn)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, mapfn);
        // 4. If thisArg is present, let T be thisArg; else let T be undefined.
        JSValue t = args.AtOrUndefined(2);
        // 5. Let A be ? ArraySpeciesCreate(O, 0).
        JSReceiver a = ArraySpeciesCreate(isolate, o, 0);
        // 6. Perform ? FlattenIntoArray(A, O, sourceLen, 0, 1, mapperFunction, T).
        FlattenIntoArray(isolate, a, o, len, 0, 1, true, mapfn, t);
        return a;
    }

    // ---- toReversed / toSpliced / with --------------------------------------------------------------------

    /// <summary>ES #sec-array.prototype.toReversed.</summary>
    public static JSValue ArrayPrototypeToReversed(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        // TryFastArrayToReversed: a packed copy of a fast array.
        if (IsFastJSArray(isolate, receiver, out JSArray fast) && fast.Length.Number <= JSArray.kMaxFastArrayLength)
        {
            JSArray copy = CloneFastJSArrayFillingHoles(isolate, fast);
            int length = (int)copy.Length.Number;
            if (copy.Elements is FixedDoubleArray d) d.Data.AsSpan(0, length).Reverse();
            else ((FixedArray)copy.Elements).Data.AsSpan(0, length).Reverse();
            return copy;
        }

        // GenericArrayToReversed.
        JSReceiver obj = ObjectOps.ToObject(isolate, receiver);
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        JSArray result = ArrayBuiltinsUtils.ArrayCreate(isolate, len);
        for (double k = 0; k < len; ++k)
        {
            double from = len - k - 1;
            JSValue fromValue = ArrayBuiltinsUtils.GetProperty(isolate, obj, from);
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, result, k, fromValue);
        }
        return result;
    }

    /// <summary>ES #sec-array.prototype.toSpliced.</summary>
    public static JSValue ArrayPrototypeToSpliced(Isolate isolate, in BuiltinArguments args)
    {
        JSValue start = args.AtOrUndefined(1);
        JSValue deleteCount = args.AtOrUndefined(2);
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? LengthOfArrayLike(O).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3-5.
        double relativeStart = ObjectOps.IntegerValue(isolate, start);
        double actualStart = ClampStart(relativeStart, len);

        int argc = args.ArgcWithoutReceiver;
        int insertCount;
        double actualDeleteCount;
        if (argc == 0)
        {
            insertCount = 0;
            actualDeleteCount = 0;
        }
        else if (argc == 1)
        {
            insertCount = 0;
            actualDeleteCount = len - actualStart;
        }
        else
        {
            insertCount = argc - 2;
            double dc = ObjectOps.IntegerValue(isolate, deleteCount);
            actualDeleteCount = Math.Min(Math.Max(0, dc), len - actualStart);
        }

        // 10. Let newLen be len + insertCount - actualSkipCount.
        double newLen = len + insertCount - actualDeleteCount;
        // 11. If newLen > 2^53 - 1, throw a TypeError exception.
        if (newLen > ArrayBuiltinsUtils.kMaxSafeInteger)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidArrayLength, JSValue.FromNumber(newLen));
        }
        if (newLen == 0) return ArrayBuiltinsUtils.ArrayCreate(isolate, 0);

        // GenericArrayToSpliced. (V8's fast path for packed arrays builds the same array.)
        // 12. Let A be ? ArrayCreate(newLen).
        JSArray copy = ArrayBuiltinsUtils.ArrayCreate(isolate, newLen);
        double i = 0;
        double r = actualStart + actualDeleteCount;
        while (i < actualStart)
        {
            JSValue iValue = GetElementValue(isolate, o, i);
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, copy, i, iValue);
            ++i;
        }
        if (argc > 2)
        {
            ReadOnlySpan<JSValue> items = args.Arguments[2..];
            foreach (JSValue e in items)
            {
                ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, copy, i, e);
                ++i;
            }
        }
        while (i < newLen)
        {
            JSValue fromValue = GetElementValue(isolate, o, r);
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, copy, i, fromValue);
            ++i;
            ++r;
        }
        return copy;
    }

    /// <summary>ES #sec-array.prototype.with.</summary>
    public static JSValue ArrayPrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        JSValue index = args.AtOrUndefined(1);
        JSValue value = args.AtOrUndefined(2);
        // 1. Let O be ? ToObject(this value).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? LengthOfArrayLike(O).
        double len = ArrayBuiltinsUtils.GetLengthProperty(isolate, obj);
        // 3. Let relativeIndex be ? ToIntegerOrInfinity(index).
        double relativeIndex = ObjectOps.IntegerValue(isolate, index);
        // 4-5. ConvertRelativeIndex.
        double actualIndex = relativeIndex >= 0 ? relativeIndex : len + relativeIndex;
        // 6. If actualIndex >= len or actualIndex < 0, throw a RangeError exception.
        if (actualIndex >= len || actualIndex < 0)
        {
            return isolate.ThrowRangeError(MessageTemplate.Invalid, ArrayBuiltinsUtils.NewString(isolate, "index"), index);
        }

        // GenericArrayWith. (V8's fast path for packed arrays builds the same array.)
        // 7. Let A be ? ArrayCreate(len).
        JSArray copy = ArrayBuiltinsUtils.ArrayCreate(isolate, len);
        for (double k = 0; k < len; ++k)
        {
            JSValue fromValue = k == actualIndex ? value : GetElementValue(isolate, obj, k);
            ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, copy, k, fromValue);
        }
        return copy;
    }
}
