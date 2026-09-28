// Port of the %TypedArray%.prototype methods and %TypedArray%.from/of:
//   src/builtins/typed-array-{at,every,filter,find,findindex,findlast,
//     findlastindex,foreach,reduce,reduceright,some,set,slice,subarray,sort,
//     to-reversed,to-sorted,with,from,of}.tq
//   src/builtins/builtins-array-gen.cc  TypedArrayPrototypeMap
//   src/builtins/builtins-typed-array.cc  copyWithin, fill, includes, indexOf,
//     lastIndexOf, reverse
//   src/runtime/runtime-typedarray.cc  TypedArraySortFast, TypedArraySet
using System.Runtime.InteropServices;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterTypedArrayMethods()
    {
        Register(Builtin.TypedArrayPrototypeAt, BuiltinsTypedArray.TypedArrayPrototypeAt);
        Register(Builtin.TypedArrayPrototypeEvery, BuiltinsTypedArray.TypedArrayPrototypeEvery);
        Register(Builtin.TypedArrayPrototypeSome, BuiltinsTypedArray.TypedArrayPrototypeSome);
        Register(Builtin.TypedArrayPrototypeFind, BuiltinsTypedArray.TypedArrayPrototypeFind);
        Register(Builtin.TypedArrayPrototypeFindIndex, BuiltinsTypedArray.TypedArrayPrototypeFindIndex);
        Register(Builtin.TypedArrayPrototypeFindLast, BuiltinsTypedArray.TypedArrayPrototypeFindLast);
        Register(Builtin.TypedArrayPrototypeFindLastIndex, BuiltinsTypedArray.TypedArrayPrototypeFindLastIndex);
        Register(Builtin.TypedArrayPrototypeForEach, BuiltinsTypedArray.TypedArrayPrototypeForEach);
        Register(Builtin.TypedArrayPrototypeFilter, BuiltinsTypedArray.TypedArrayPrototypeFilter);
        Register(Builtin.TypedArrayPrototypeMap, BuiltinsTypedArray.TypedArrayPrototypeMap);
        Register(Builtin.TypedArrayPrototypeReduce, BuiltinsTypedArray.TypedArrayPrototypeReduce);
        Register(Builtin.TypedArrayPrototypeReduceRight, BuiltinsTypedArray.TypedArrayPrototypeReduceRight);
        Register(Builtin.TypedArrayPrototypeCopyWithin, BuiltinsTypedArray.TypedArrayPrototypeCopyWithin);
        Register(Builtin.TypedArrayPrototypeFill, BuiltinsTypedArray.TypedArrayPrototypeFill);
        Register(Builtin.TypedArrayPrototypeIncludes, BuiltinsTypedArray.TypedArrayPrototypeIncludes);
        Register(Builtin.TypedArrayPrototypeIndexOf, BuiltinsTypedArray.TypedArrayPrototypeIndexOf);
        Register(Builtin.TypedArrayPrototypeLastIndexOf, BuiltinsTypedArray.TypedArrayPrototypeLastIndexOf);
        Register(Builtin.TypedArrayPrototypeReverse, BuiltinsTypedArray.TypedArrayPrototypeReverse);
        Register(Builtin.TypedArrayPrototypeSet, BuiltinsTypedArray.TypedArrayPrototypeSet);
        Register(Builtin.TypedArrayPrototypeSlice, BuiltinsTypedArray.TypedArrayPrototypeSlice);
        Register(Builtin.TypedArrayPrototypeSubArray, BuiltinsTypedArray.TypedArrayPrototypeSubArray);
        Register(Builtin.TypedArrayPrototypeSort, BuiltinsTypedArray.TypedArrayPrototypeSort);
        Register(Builtin.TypedArrayPrototypeToReversed, BuiltinsTypedArray.TypedArrayPrototypeToReversed);
        Register(Builtin.TypedArrayPrototypeToSorted, BuiltinsTypedArray.TypedArrayPrototypeToSorted);
        Register(Builtin.TypedArrayPrototypeWith, BuiltinsTypedArray.TypedArrayPrototypeWith);
        Register(Builtin.TypedArrayFrom, BuiltinsTypedArray.TypedArrayFrom);
        Register(Builtin.TypedArrayOf, BuiltinsTypedArray.TypedArrayOf);
        Register(Builtin.TypedArrayPrototypeJoin, BuiltinsArray.TypedArrayPrototypeJoin);
        Register(Builtin.TypedArrayPrototypeToLocaleString, BuiltinsArray.TypedArrayPrototypeToLocaleString);
    }
}

public static partial class BuiltinsTypedArray
{
    // ---- Shared macros ---------------------------------------------------------------------

    /// <summary>
    /// Cast&lt;JSTypedArray&gt; + EnsureValidAndReadLength of the iterating
    /// builtins: TypeError kNotTypedArray / <paramref name="failMessage"/>.
    /// </summary>
    static JSTypedArray EnsureValidAndReadLength(Isolate isolate, JSValue receiver, string name, out ulong length,
        MessageTemplate failMessage = MessageTemplate.TypedArrayValidateErrorOperation)
    {
        if (receiver.HeapObjectOrNull is not JSTypedArray array)
        {
            isolate.ThrowTypeError(MessageTemplate.NotTypedArray, ArrayBuiltinsUtils.NewString(isolate, name));
            length = 0;
            return null!;
        }
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out length))
        {
            isolate.ThrowTypeError(failMessage, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        return array;
    }

    /// <summary>Cast&lt;Callable&gt;(arguments[0]) otherwise ThrowCalledNonCallable.</summary>
    static JSValue CallableOrThrow(Isolate isolate, JSValue callback)
    {
        if (!ObjectOps.IsCallable(callback)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, callback);
        return callback;
    }

    /// <summary>
    /// AttachedJSTypedArrayWitness: RecheckIndex + Load. kValue is undefined
    /// when the buffer became detached or the index out of bounds.
    /// </summary>
    static JSValue WitnessLoad(Isolate isolate, JSTypedArray array, ulong k)
    {
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out ulong length) || k >= length)
        {
            return JSValue.Undefined;
        }
        return TypedArrayElementsOps.Load(isolate, array, k);
    }

    // ---- at (typed-array-at.tq) --------------------------------------------------------------

    /// <summary>https://tc39.es/proposal-item-method/#sec-%typedarray%.prototype.at</summary>
    public static JSValue TypedArrayPrototypeAt(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be the this value.
        // 2. Perform ? ValidateTypedArray(O).
        // 3. Let len be IntegerIndexedObjectLength(O).
        ValidateTypedArrayAndGetLength(isolate, args.Receiver, "%TypedArray%.prototype.at", TypedArrayAccessMode.kRead,
            out ulong length);
        double len = length;

        // 4. Let relativeIndex be ? ToInteger(index).
        double relativeIndex = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(1));
        // 5. If relativeIndex ≥ 0, then
        //   a. Let k be relativeIndex.
        // 6. Else,
        //   a. Let k be len + relativeIndex.
        double k = relativeIndex >= 0 ? relativeIndex : len + relativeIndex;
        // 7. If k < 0 or k ≥ len, then return undefined.
        if (k < 0 || k >= len) return JSValue.Undefined;
        // 8. Return ? Get(O, ! ToString(k)).
        return ArrayBuiltinsUtils.GetProperty(isolate, args.Receiver, k);
    }

    // ---- every / some / find / findIndex / findLast / findLastIndex / forEach ------------------

    /// <summary>ES #sec-%typedarray%.prototype.every.</summary>
    public static JSValue TypedArrayPrototypeEvery(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.every";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        // 5. Let k be 0.
        // 6. Repeat, while k < len
        for (ulong k = 0; k < length; k++)
        {
            // 6b. Let kValue be ! Get(O, Pk).
            JSValue value = WitnessLoad(isolate, array, k);
            // 6c. Let testResult be ! ToBoolean(? Call(callbackfn, thisArg, « kValue, 𝔽(k), O »)).
            JSValue result = Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), array]);
            // 6d. If testResult is false, return false.
            if (!ObjectOps.BooleanValue(result)) return JSValue.False;
        }
        // 7. Return true.
        return JSValue.True;
    }

    /// <summary>ES #sec-%typedarray%.prototype.some.</summary>
    public static JSValue TypedArrayPrototypeSome(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.some";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        for (ulong k = 0; k < length; k++)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            JSValue result = Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), array]);
            if (ObjectOps.BooleanValue(result)) return JSValue.True;
        }
        return JSValue.False;
    }

    /// <summary>ES #sec-%typedarray%.prototype.find.</summary>
    public static JSValue TypedArrayPrototypeFind(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.find";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue predicate = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        for (ulong k = 0; k < length; k++)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            JSValue result = Execution.Call(isolate, predicate, thisArg, [value, JSValue.FromNumber(k), array]);
            if (ObjectOps.BooleanValue(result)) return value;
        }
        return JSValue.Undefined;
    }

    /// <summary>ES #sec-%typedarray%.prototype.findIndex.</summary>
    public static JSValue TypedArrayPrototypeFindIndex(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.findIndex";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue predicate = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        for (ulong k = 0; k < length; k++)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            JSValue indexNumber = JSValue.FromNumber(k);
            JSValue result = Execution.Call(isolate, predicate, thisArg, [value, indexNumber, array]);
            if (ObjectOps.BooleanValue(result)) return indexNumber;
        }
        return JSValue.FromInt(-1);
    }

    /// <summary>https://tc39.es/proposal-array-find-from-last/index.html#sec-%typedarray%.prototype.findlast</summary>
    public static JSValue TypedArrayPrototypeFindLast(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.findLast";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue predicate = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        // 5. Let k be len - 1.
        // 6. Repeat, while k ≥ 0
        for (ulong k = length; k-- > 0;)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            JSValue result = Execution.Call(isolate, predicate, thisArg, [value, JSValue.FromNumber(k), array]);
            if (ObjectOps.BooleanValue(result)) return value;
        }
        return JSValue.Undefined;
    }

    /// <summary>https://tc39.es/proposal-array-find-from-last/index.html#sec-%typedarray%.prototype.findlastindex</summary>
    public static JSValue TypedArrayPrototypeFindLastIndex(Isolate isolate, in BuiltinArguments args)
    {
        // (V8's builtin name constant is '%TypedArray%.prototype.findIndexLast'.)
        const string name = "%TypedArray%.prototype.findIndexLast";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue predicate = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        for (ulong k = length; k-- > 0;)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            JSValue indexNumber = JSValue.FromNumber(k);
            JSValue result = Execution.Call(isolate, predicate, thisArg, [value, indexNumber, array]);
            if (ObjectOps.BooleanValue(result)) return indexNumber;
        }
        return JSValue.FromInt(-1);
    }

    /// <summary>ES #sec-%typedarray%.prototype.foreach.</summary>
    public static JSValue TypedArrayPrototypeForEach(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.forEach";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue thisArg = args.AtOrUndefined(2);
        for (ulong k = 0; k < length; k++)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), array]);
        }
        return JSValue.Undefined;
    }

    // ---- filter / map ---------------------------------------------------------------------------

    /// <summary>ES #sec-%typedarray%.prototype.filter.</summary>
    public static JSValue TypedArrayPrototypeFilter(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.filter";
        // 1. Let O be the this value.
        // 2. Perform ? ValidateTypedArray(O).
        // 3. Let len be IntegerIndexedObjectLength(O).
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        // 4. If IsCallable(callbackfn) is false, throw a TypeError exception.
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        // 5. If thisArg is present, let T be thisArg; else let T be undefined.
        JSValue thisArg = args.AtOrUndefined(2);

        // 6. Let kept be a new empty List.
        var kept = new GrowableFixedArray();
        // 7. Let k be 0.
        // 8. Let captured be 0.
        // 9. Repeat, while k < len
        for (ulong k = 0; k < length; k++)
        {
            // a. Let Pk be ! ToString(k).
            // b. Let kValue be ? Get(O, Pk).
            JSValue value = WitnessLoad(isolate, array, k);
            // c. Let selected be ToBoolean(? Call(callbackfn, T, « kValue, k, O »)).
            JSValue selected = Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), array]);
            // d. If selected is true, then
            //    i. Append kValue to the end of kept.
            //   ii. Increase captured by 1.
            if (ObjectOps.BooleanValue(selected)) kept.Push(value);
        }

        // 10. Let A be ? TypedArraySpeciesCreate(O, captured).
        JSTypedArray typedArray = TypedArraySpeciesCreateByLength(isolate, name, array, (ulong)kept.Length,
            TypedArrayAccessMode.kWrite);

        // 11. Let n be 0.
        // 12. For each element e of kept, do
        //   a. Perform ! Set(A, ! ToString(n), e, true).
        //   b. Increment n by 1.
        TypedArrayElementsOps.CopyElementsHandle(isolate, kept.ToJSArray(isolate), typedArray, (ulong)kept.Length, 0);

        // 13. Return A.
        return typedArray;
    }

    /// <summary>ES #sec-%typedarray%.prototype.map (builtins-array-gen.cc TypedArrayPrototypeMap).</summary>
    public static JSValue TypedArrayPrototypeMap(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.map";
        JSValue receiver = args.Receiver;
        JSValue callbackfn = args.AtOrUndefined(1);
        JSValue thisArg = args.AtOrUndefined(2);

        // ValidateTypedArray: https://tc39.es/ecma262/#sec-validatetypedarray
        if (receiver.HeapObjectOrNull is not JSTypedArray typedArray)
        {
            return isolate.ThrowTypeError(MessageTemplate.NotTypedArray);
        }
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(typedArray, TypedArrayAccessMode.kRead, out ulong length))
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        if (!ObjectOps.IsCallable(callbackfn)) return isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, callbackfn);

        // TypedArrayMapResultGenerator:
        // 6. Let A be ? TypedArraySpeciesCreate(O, len).
        JSTypedArray a = TypedArraySpeciesCreateByLength(isolate, name, typedArray, length, TypedArrayAccessMode.kWrite);
        ElementsKind sourceElementsKind = typedArray.Kind;
        // TODO(v8:11111): Make storing fast when the elements kinds only differ
        // because of their RAB/GSABness.
        bool fastTypedArrayTarget = sourceElementsKind == a.Kind;
        bool isRabGsab = ElementsKinds.IsRabGsabTypedArrayElementsKind(sourceElementsKind);

        // VisitAllTypedArrayElements
        for (ulong k = 0; k < length; k++)
        {
            JSValue value;
            if (isRabGsab)
            {
                // If `index` is out of bounds, Get returns undefined.
                value = WitnessLoad(isolate, typedArray, k);
            }
            else
            {
                value = typedArray.Buffer.WasDetached ? JSValue.Undefined : TypedArrayElementsOps.Load(isolate, typedArray, k);
            }

            // TypedArrayMapProcessor:
            // 7c. Let mapped_value be ? Call(callbackfn, T, « kValue, k, O »).
            JSValue kNumber = JSValue.FromNumber(k);
            JSValue mappedValue = Execution.Call(isolate, callbackfn, thisArg, [value, kNumber, typedArray]);

            // 7d. Perform ? Set(A, Pk, mapped_value, true).
            if (fastTypedArrayTarget)
            {
                // https://tc39.es/ecma262/#sec-integerindexedelementset
                // 2. If arrayTypeName is "BigUint64Array" or "BigInt64Array", let
                // numValue be ? ToBigInt(v).
                // 3. Otherwise, let numValue be ? ToNumber(value).
                JSValue numValue = TypedArrayElementsOps.PrepareValue(isolate, sourceElementsKind, mappedValue);
                // EmitElementStore(kInBounds) bails out on a detached, out of bounds
                // or immutable target, which throws.
                if (!TypedArrayElementsOps.TryGetLengthAndValidate(a, TypedArrayAccessMode.kWrite, out ulong aLength) ||
                    k >= aLength)
                {
                    return isolate.ThrowTypeError(JSTypedArray.ValidateErrorMessage(isolate, TypedArrayAccessMode.kWrite),
                        ArrayBuiltinsUtils.NewString(isolate, name));
                }
                TypedArrayElementsOps.StoreNumeric(a, k, numValue);
            }
            else
            {
                // SetPropertyStrict(a, k, mapped_value)
                ArrayBuiltinsUtils.SetProperty(isolate, a, k, mappedValue);
            }
        }
        return a;
    }

    // ---- reduce / reduceRight -------------------------------------------------------------------

    /// <summary>ES #sec-%typedarray%.prototype.reduce.</summary>
    public static JSValue TypedArrayPrototypeReduce(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.reduce";
        // (V8 reports the detached message for reduce.)
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length,
            MessageTemplate.TypedArrayDetachedErrorOperation);
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue accumulator = args.Length >= 3 ? args[2] : JSValue.TheHole;
        for (ulong k = 0; k < length; k++)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            accumulator = accumulator.IsTheHole
                ? value
                : Execution.Call(isolate, callbackfn, JSValue.Undefined, [accumulator, value, JSValue.FromNumber(k), array]);
        }
        if (accumulator.IsTheHole)
        {
            return isolate.ThrowTypeError(MessageTemplate.ReduceNoInitial, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        return accumulator;
    }

    /// <summary>ES #sec-%typedarray%.prototype.reduceright.</summary>
    public static JSValue TypedArrayPrototypeReduceRight(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.reduceRight";
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong length);
        JSValue callbackfn = CallableOrThrow(isolate, args.AtOrUndefined(1));
        JSValue accumulator = args.Length >= 3 ? args[2] : JSValue.TheHole;
        for (ulong k = length; k-- > 0;)
        {
            JSValue value = WitnessLoad(isolate, array, k);
            accumulator = accumulator.IsTheHole
                ? value
                : Execution.Call(isolate, callbackfn, JSValue.Undefined, [accumulator, value, JSValue.FromNumber(k), array]);
        }
        if (accumulator.IsTheHole)
        {
            return isolate.ThrowTypeError(MessageTemplate.ReduceNoInitial, ArrayBuiltinsUtils.NewString(isolate, name));
        }
        return accumulator;
    }

    // ---- builtins-typed-array.cc -----------------------------------------------------------------

    static long CapRelativeIndex(double relative, long minimum, long maximum)
    {
        Debug.Assert(!double.IsNaN(relative));
        return (long)(relative < 0 ? Math.Max(relative + maximum, minimum) : Math.Min(relative, maximum));
    }

    /// <summary>ES #sec-%typedarray%.prototype.copywithin.</summary>
    public static JSValue TypedArrayPrototypeCopyWithin(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "%TypedArray%.prototype.copyWithin";
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, methodName, TypedArrayAccessMode.kWrite);

        long len = (long)array.GetLength();
        long to = 0;
        long from = 0;
        long final = len;

        if (args.Length > 1)
        {
            double num = ObjectOps.IntegerValue(isolate, args[1]);
            to = CapRelativeIndex(num, 0, len);

            if (args.Length > 2)
            {
                num = ObjectOps.IntegerValue(isolate, args[2]);
                from = CapRelativeIndex(num, 0, len);

                JSValue end = args.AtOrUndefined(3);
                if (!end.IsUndefined)
                {
                    num = ObjectOps.IntegerValue(isolate, end);
                    final = CapRelativeIndex(num, 0, len);
                }
            }
        }

        long count = Math.Min(final - from, len - to);
        if (count <= 0) return array;

        // TypedArray buffer may have been transferred/detached during parameter
        // processing above.
        if (array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        if (array.IsBackedByRab)
        {
            long newLen = (long)array.GetLengthOrOutOfBounds(out bool outOfBounds);
            if (outOfBounds)
            {
                return isolate.ThrowTypeError(MessageTemplate.TypedArrayOOBErrorOperation,
                    ArrayBuiltinsUtils.NewString(isolate, methodName));
            }
            if (newLen < len)
            {
                // We don't need to account for growing, since we only copy an already
                // determined number of elements and growing won't change it. If to >
                // new_len or from > new_len, the count below will be < 0, so we don't
                // need to check them separately.
                if (final > newLen) final = newLen;
                count = Math.Min(final - from, newLen - to);
                if (count <= 0) return array;
            }
        }

        int elementSize = array.ElementSize;
        Span<byte> data = array.DataSpan();
        // std::memmove / base::Relaxed_Memmove: Span.CopyTo handles the overlap.
        data.Slice((int)(from * elementSize), (int)(count * elementSize)).CopyTo(data.Slice((int)(to * elementSize)));
        return array;
    }

    /// <summary>ES #sec-%typedarray%.prototype.fill.</summary>
    public static JSValue TypedArrayPrototypeFill(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "%TypedArray%.prototype.fill";
        // 1. Let O be the this value.
        // 2. Let taRecord be ? ValidateTypedArray(O, seq-cst).
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, methodName, TypedArrayAccessMode.kWrite);
        ElementsKind kind = array.Kind;

        // 3. Let len be TypedArrayLength(taRecord).
        long len = (long)array.GetLength();

        // 4. If O.[[ContentType]] is bigint, set value to ? ToBigInt(value).
        // 5. Otherwise, set value to ? ToNumber(value).
        JSValue objValue = TypedArrayElementsOps.PrepareValue(isolate, kind, args.AtOrUndefined(1));

        long start = 0;
        long end = len;

        if (args.Length > 2)
        {
            // 6. Let relativeStart be ? ToIntegerOrInfinity(start).
            double doubleNum = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(2));
            // 7-9.
            start = CapRelativeIndex(doubleNum, 0, len);

            // 10. If end is undefined, let relativeEnd be len; else let relativeEnd be
            //     ? ToIntegerOrInfinity(end).
            JSValue num = args.AtOrUndefined(3);
            if (!num.IsUndefined)
            {
                // 11-13.
                doubleNum = ObjectOps.IntegerValue(isolate, num);
                end = CapRelativeIndex(doubleNum, 0, len);
            }
        }

        // 14. Set taRecord to MakeTypedArrayWithBufferWitnessRecord(O, seq-cst).
        // 15. If IsTypedArrayOutOfBounds(taRecord) is true, throw a TypeError
        // exception.
        if (array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayDetachedErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        if (array.IsVariableLength)
        {
            if (array.IsOutOfBounds)
            {
                return isolate.ThrowTypeError(MessageTemplate.TypedArrayOOBErrorOperation,
                    ArrayBuiltinsUtils.NewString(isolate, methodName));
            }
            // 16. Set len to TypedArrayLength(taRecord).
            // 17. Set endIndex to min(endIndex, len).
            end = Math.Min(end, (long)array.GetLength());
        }

        long count = end - start;
        if (count <= 0) return array;

        // 19. Repeat, while k < endIndex, ... Set(O, Pk, value, true).
        TypedArrayElementsOps.Fill(array, objValue, (ulong)start, (ulong)end);
        return array;
    }

    /// <summary>ES #sec-%typedarray%.prototype.includes.</summary>
    public static JSValue TypedArrayPrototypeIncludes(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, "%TypedArray%.prototype.includes",
            TypedArrayAccessMode.kRead);

        if (args.Length < 2) return JSValue.False;

        long len = (long)array.GetLength();
        if (len == 0) return JSValue.False;

        long index = 0;
        if (args.Length > 2)
        {
            double num = ObjectOps.IntegerValue(isolate, args[2]);
            index = CapRelativeIndex(num, 0, len);
        }

        JSValue searchElement = args.AtOrUndefined(1);
        ElementsAccessor elements = array.GetElementsAccessor();
        bool result = elements.IncludesValue(isolate, array, searchElement, (ulong)index, (ulong)len);
        return JSValue.FromBoolean(result);
    }

    /// <summary>ES #sec-%typedarray%.prototype.indexof.</summary>
    public static JSValue TypedArrayPrototypeIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, "%TypedArray%.prototype.indexOf",
            TypedArrayAccessMode.kRead);

        long len = (long)array.GetLength();
        if (len == 0) return JSValue.FromInt(-1);

        long index = 0;
        if (args.Length > 2)
        {
            double num = ObjectOps.IntegerValue(isolate, args[2]);
            index = CapRelativeIndex(num, 0, len);
        }

        if (array.WasDetached) return JSValue.FromInt(-1);
        if (array.IsVariableLength && array.IsOutOfBounds) return JSValue.FromInt(-1);

        JSValue searchElement = args.AtOrUndefined(1);
        ElementsAccessor elements = array.GetElementsAccessor();
        long result = elements.IndexOfValue(isolate, array, searchElement, (ulong)index, (ulong)len);
        return JSValue.FromNumber(result);
    }

    /// <summary>ES #sec-%typedarray%.prototype.lastindexof.</summary>
    public static JSValue TypedArrayPrototypeLastIndexOf(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, "%TypedArray%.prototype.lastIndexOf",
            TypedArrayAccessMode.kRead);

        long len = (long)array.GetLength();
        if (len == 0) return JSValue.FromInt(-1);

        long index = len - 1;
        if (args.Length > 2)
        {
            double num = ObjectOps.IntegerValue(isolate, args[2]);
            // Set a negative value (-1) for returning -1 if num is negative and
            // len + num is still negative. Upper bound is len - 1.
            index = Math.Min(CapRelativeIndex(num, -1, len), len - 1);
        }

        if (index < 0) return JSValue.FromInt(-1);

        if (array.WasDetached) return JSValue.FromInt(-1);
        if (array.IsVariableLength && array.IsOutOfBounds) return JSValue.FromInt(-1);

        JSValue searchElement = args.AtOrUndefined(1);
        ElementsAccessor elements = array.GetElementsAccessor();
        long result = elements.LastIndexOfValue(array, searchElement, (ulong)index);
        return JSValue.FromNumber(result);
    }

    /// <summary>ES #sec-%typedarray%.prototype.reverse.</summary>
    public static JSValue TypedArrayPrototypeReverse(Isolate isolate, in BuiltinArguments args)
    {
        JSTypedArray array = JSTypedArray.Validate(isolate, args.Receiver, "%TypedArray%.prototype.reverse",
            TypedArrayAccessMode.kWrite);
        ReverseElements(array);
        return array;
    }

    /// <summary>TypedElementsAccessor::ReverseImpl, vectorized with MemoryExtensions.Reverse.</summary>
    static void ReverseElements(JSTypedArray array)
    {
        ulong len = array.GetLength();
        if (len < 2) return;
        Span<byte> data = array.DataSpan(0, len);
        switch (array.ElementSize)
        {
            case 1: data.Reverse(); break;
            case 2: MemoryMarshal.Cast<byte, ushort>(data).Reverse(); break;
            case 4: MemoryMarshal.Cast<byte, uint>(data).Reverse(); break;
            default: MemoryMarshal.Cast<byte, ulong>(data).Reverse(); break;
        }
    }

    // ---- set (typed-array-set.tq) -----------------------------------------------------------

    /// <summary>ES #sec-%typedarray%.prototype.set-overloaded-offset.</summary>
    public static JSValue TypedArrayPrototypeSet(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.set";
        // 2. Let target be the this value.
        // 3. Perform ? RequireInternalSlot(target, [[TypedArrayName]]).
        if (args.Receiver.HeapObjectOrNull is not JSTypedArray target)
        {
            return isolate.ThrowTypeError(MessageTemplate.NotTypedArray, ArrayBuiltinsUtils.NewString(isolate, name));
        }

        ulong targetOffset = 0;
        bool targetOffsetOverflowed = false;
        ulong targetLength;

        // If the offset argument is not provided then the targetOffset is 0.
        JSValue offsetArg = args.Length > 2 ? args[2] : JSValue.Zero;
        if (offsetArg.IsSmi)
        {
            // 5. Let targetOffset be ? ToIntegerOrInfinity(offset).
            // 6. If targetOffset < 0, throw a RangeError exception.
            double offsetSmi = offsetArg.Number;
            if (offsetSmi < 0)
            {
                // For negative Smi offsets, integer conversion has no side effects,
                // but we still need to perform step 4 before throwing a RangeError.
                if (target.Buffer.IsImmutable) return ThrowSetValidateError(isolate, name);
                return isolate.ThrowRangeError(MessageTemplate.TypedArraySetOffsetOutOfBounds);
            }
            targetOffset = (ulong)offsetSmi;
            // 4, 7-9: EnsureValidAndReadLength(target, kWrite).
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(target, TypedArrayAccessMode.kWrite, out targetLength))
            {
                return ThrowSetValidateError(isolate, name);
            }
        }
        else
        {
            // 4. If IsImmutableBuffer(target.[[ViewedArrayBuffer]]) is true, throw
            //    a TypeError exception.
            if (target.Buffer.IsImmutable) return ThrowSetValidateError(isolate, name);

            // 5. Let targetOffset be ? ToIntegerOrInfinity(offset).
            // 6. If targetOffset < 0, throw a RangeError exception.
            double offsetNumber = ObjectOps.IntegerValue(isolate, offsetArg);
            if (offsetNumber < 0) return isolate.ThrowRangeError(MessageTemplate.TypedArraySetOffsetOutOfBounds);
            if (offsetNumber > ArrayBuiltinsUtils.kMaxSafeInteger)
            {
                // On UintPtr or SafeInteger range overflow throw RangeError after
                // performing observable steps to follow the spec.
                targetOffsetOverflowed = true;
            }
            else
            {
                targetOffset = (ulong)offsetNumber;
            }

            // 7. Let targetBuffer be target.[[ViewedArrayBuffer]].
            // 8. If IsDetachedBuffer(targetBuffer) is true, throw a TypeError
            //    exception.
            // 9. Let targetLength be target.[[ArrayLength]].
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(target, TypedArrayAccessMode.kWrite, out targetLength))
            {
                return ThrowSetValidateError(isolate, name);
            }
        }

        JSValue overloadedArg = args.AtOrUndefined(1);
        if (overloadedArg.HeapObjectOrNull is JSTypedArray typedArray)
        {
            // 4. Let srcBuffer be typedArray.[[ViewedArrayBuffer]].
            // 5. If IsDetachedBuffer(srcBuffer) is true, throw a TypeError
            //   exception.
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(typedArray, TypedArrayAccessMode.kRead, out ulong srcLength))
            {
                return ThrowSetValidateError(isolate, name);
            }
            if (!TypedArrayPrototypeSetTypedArray(isolate, target, targetLength, typedArray, srcLength, targetOffset,
                    targetOffsetOverflowed))
            {
                return isolate.ThrowRangeError(MessageTemplate.TypedArraySetOffsetOutOfBounds);
            }
            return JSValue.Undefined;
        }

        if (!TypedArrayPrototypeSetArray(isolate, target, targetLength, overloadedArg, targetOffset, targetOffsetOverflowed))
        {
            return isolate.ThrowRangeError(MessageTemplate.TypedArraySetOffsetOutOfBounds);
        }
        return JSValue.Undefined;
    }

    static JSValue ThrowSetValidateError(Isolate isolate, string name) =>
        // (Uses the write message's text; see JSTypedArray.ValidateErrorMessage.)
        ThrowValidateError(isolate, name, TypedArrayAccessMode.kWrite);

    /// <summary>
    /// CheckIntegerIndexAdditionOverflow: false when srcLength + targetOffset
    /// exceeds targetLength.
    /// </summary>
    static bool CheckIntegerIndexAdditionOverflow(ulong srcLength, ulong targetOffset, ulong targetLength) =>
        srcLength <= targetLength && targetOffset <= targetLength - srcLength;

    /// <summary>SetTypedArrayFromArrayLike (ES #sec-settypedarrayfromarraylike). False: IfOffsetOutOfBounds.</summary>
    static bool TypedArrayPrototypeSetArray(Isolate isolate, JSTypedArray target, ulong targetLength, JSValue arrayArg,
        ulong targetOffset, bool targetOffsetOverflowed)
    {
        // 4. Let src be ? ToObject(source).
        JSReceiver src = ObjectOps.ToObject(isolate, arrayArg);

        // 5. Let srcLength be ? LengthOfArrayLike(src).
        double srcLengthNum = ArrayBuiltinsUtils.GetLengthProperty(isolate, src);

        // 6. If targetOffset is +∞, throw a RangeError exception.
        if (targetOffsetOverflowed) return false;

        // 7. If srcLength + targetOffset > targetLength, throw a RangeError
        //   exception.
        ulong srcLength = (ulong)srcLengthNum;
        if (!CheckIntegerIndexAdditionOverflow(srcLength, targetOffset, targetLength)) return false;

        // All the obvervable side effects are executed, so there's nothing else
        // to do with the empty source array.
        if (srcLength == 0) return true;

        // Fast path: CopyFastNumberJSArrayElementsToTypedArray for fast numeric
        // JSArrays, else Runtime_TypedArraySet. Both end up in CopyElementsHandle,
        // which takes the same fast path.
        TypedArrayElementsOps.CopyElementsHandle(isolate, src, target, srcLength, targetOffset);
        return true;
    }

    /// <summary>SetTypedArrayFromTypedArray (ES #sec-settypedarrayfromtypedarray). False: IfOffsetOutOfBounds.</summary>
    static bool TypedArrayPrototypeSetTypedArray(Isolate isolate, JSTypedArray target, ulong targetLength, JSTypedArray source,
        ulong srcLength, ulong targetOffset, bool targetOffsetOverflowed)
    {
        // Steps 6-14 are not observable, so we can handle offset overflow
        // at step 15 here.
        if (targetOffsetOverflowed) return false;

        // 16. If srcLength + targetOffset > targetLength, throw a RangeError
        //   exception.
        if (!CheckIntegerIndexAdditionOverflow(srcLength, targetOffset, targetLength)) return false;

        ElementsKind srcKind = source.Kind;
        ElementsKind targetKind = target.Kind;

        // 17. If target.[[ContentType]] is not equal to
        //   source.[[ContentType]], throw a TypeError exception.
        if (ElementsKinds.IsBigIntTypedArrayElementsKind(srcKind) != ElementsKinds.IsBigIntTypedArrayElementsKind(targetKind))
        {
            // (V8 checks this only on its slow path, i.e. when the kinds differ,
            // which they always do in this case.)
            isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
        }

        // All the obvervable side effects are executed, so there's nothing else
        // to do with the empty source array.
        if (srcLength == 0) return true;

        // memmove when the kinds match (modulo Uint8/Uint8Clamped), otherwise
        // CallCCopyTypedArrayElementsToTypedArray; both handle overlapping regions.
        TypedArrayElementsOps.CopyElementsFromTypedArray(source, target, srcLength, targetOffset);
        return true;
    }

    // ---- slice / subarray ---------------------------------------------------------------------

    /// <summary>ES #sec-%typedarray%.prototype.slice.</summary>
    public static JSValue TypedArrayPrototypeSlice(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.slice";
        // 1. Let O be the this value.
        // 2. Perform ? ValidateTypedArray(O).
        // 3. Let len be O.[[ArrayLength]].
        JSTypedArray src = ValidateTypedArrayAndGetLength(isolate, args.Receiver, name, TypedArrayAccessMode.kRead, out ulong len);

        // 4. Let relativeStart be ? ToInteger(start).
        // 5. If relativeStart < 0, let k be max((len + relativeStart), 0);
        //    else let k be min(relativeStart, len).
        JSValue start = args.AtOrUndefined(1);
        ulong k = !start.IsUndefined ? ArrayBuiltinsUtils.ConvertAndClampRelativeIndex(isolate, start, len) : 0;

        // 6. If end is undefined, let relativeEnd be len;
        //    else let relativeEnd be ? ToInteger(end).
        // 7. If relativeEnd < 0, let final be max((len + relativeEnd), 0);
        //    else let final be min(relativeEnd, len).
        JSValue end = args.AtOrUndefined(2);
        ulong final = !end.IsUndefined ? ArrayBuiltinsUtils.ConvertAndClampRelativeIndex(isolate, end, len) : len;

        // 8. Let count be max(final - k, 0).
        ulong count = final > k ? final - k : 0;

        // 9. Let A be ? TypedArraySpeciesCreate(O, « count »).
        JSTypedArray dest = TypedArraySpeciesCreateByLength(isolate, name, src, count, TypedArrayAccessMode.kWrite);

        if (count > 0)
        {
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(src, TypedArrayAccessMode.kRead, out ulong newLength))
            {
                return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation, ArrayBuiltinsUtils.NewString(isolate, name));
            }
            // If the backing buffer is a RAB, it's possible that the length has
            // decreased since the last time we loaded it.
            if (k >= newLength) return dest;
            if (final > newLength)
            {
                final = newLength;
                count = final > k ? final - k : 0;
            }
            // FastCopy: dest could be a different type from src or share the same
            // buffer with the src because of custom species constructor.
            if (src.Kind == dest.Kind && !ReferenceEquals(dest.Buffer, src.Buffer))
            {
                int elementSize = src.ElementSize;
                src.DataSpan(k, count).CopyTo(dest.DataSpan(0, count));
                _ = elementSize;
            }
            else
            {
                // SlowCopy
                if (ElementsKinds.IsBigIntTypedArrayElementsKind(src.Kind) != ElementsKinds.IsBigIntTypedArrayElementsKind(dest.Kind))
                {
                    return isolate.ThrowTypeError(MessageTemplate.BigIntMixedTypes);
                }
                TypedArrayElementsOps.CopyTypedArrayElementsSlice(src, dest, k, final);
            }
        }

        return dest;
    }

    /// <summary>ES %TypedArray%.prototype.subarray.</summary>
    public static JSValue TypedArrayPrototypeSubArray(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "%TypedArray%.prototype.subarray";

        // 1. Let O be the this value.
        // 2. Perform ? RequireInternalSlot(O, [[TypedArrayName]]).
        if (args.Receiver.HeapObjectOrNull is not JSTypedArray source)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                ArrayBuiltinsUtils.NewString(isolate, methodName), args.Receiver);
        }

        // 3. Assert: O has a [[ViewedArrayBuffer]] internal slot.
        // 4. Let buffer be O.[[ViewedArrayBuffer]].
        JSArrayBuffer buffer = source.Buffer;

        // 5. Let getSrcBufferByteLength be
        //    MakeIdempotentArrayBufferByteLengthGetter(SeqCst).
        // 6. Let srcLength be IntegerIndexedObjectLength(O, getSrcBufferByteLength).
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(source, TypedArrayAccessMode.kRead, out ulong srcLength)) srcLength = 0;

        // 8-11.
        JSValue arg0 = args.AtOrUndefined(1);
        ulong begin = !arg0.IsUndefined ? ArrayBuiltinsUtils.ConvertAndClampRelativeIndex(isolate, arg0, srcLength) : 0;

        // 12. If O.[[ArrayLength]] is auto and end is undefined, then
        JSValue arg1 = args.AtOrUndefined(2);
        bool endIsDefined = !arg1.IsUndefined;

        JSValue newLength;
        if (source.IsLengthTracking && !endIsDefined)
        {
            // a. Let newLength be undefined.
            newLength = JSValue.Undefined;
        }
        else
        {
            // 13. Else, a-d.
            ulong end = endIsDefined ? ArrayBuiltinsUtils.ConvertAndClampRelativeIndex(isolate, arg1, srcLength) : srcLength;
            //   e. Let newLength be max(endIndex - beginIndex, 0).
            newLength = JSValue.FromNumber(end > begin ? end - begin : 0);
        }

        // 14-15.
        ElementsKind kind = source.Kind;

        // 16. Let srcByteOffset be O.[[ByteOffset]].
        ulong srcByteOffset = source.ByteOffset;

        // 17. Let beginByteOffset be srcByteOffset + beginIndex × elementSize.
        if (!CalculateByteLength(kind, begin, out ulong beginBytes))
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidArrayBufferLength);
        }
        ulong beginByteOffset = srcByteOffset + beginBytes;

        // 18-20. Return ? TypedArraySpeciesCreate(O, argumentsList).
        return TypedArraySpeciesCreateByBuffer(isolate, methodName, source, buffer, beginByteOffset, newLength,
            TypedArrayAccessMode.kRead);
    }

    // ---- sort / toSorted (typed-array-sort.tq, runtime-typedarray.cc) --------------------------

    /// <summary>CallCompare: ToNumber(Call(comparefn, undefined, a, b)), NaN as +0.</summary>
    static double CallCompare(Isolate isolate, JSValue comparefn, JSValue a, JSValue b)
    {
        // a. Let v be ? ToNumber(? Call(comparefn, undefined, x, y)).
        double v = ObjectOps.ToNumber(isolate, Execution.Call(isolate, comparefn, JSValue.Undefined, [a, b])).Number;
        // b. If v is NaN, return +0.
        if (double.IsNaN(v)) return 0;
        // c. return v.
        return v;
    }

    /// <summary>TypedArrayMerge: merges [from, middle) and [middle, to) from source into target.</summary>
    static void TypedArrayMerge(Isolate isolate, JSValue comparefn, JSValue[] source, int from, int middle, int to, JSValue[] target)
    {
        int left = from;
        int right = middle;

        for (int targetIndex = from; targetIndex < to; ++targetIndex)
        {
            if (left < middle && right >= to)
            {
                // If the left run has elements, but the right does not, we take
                // from the left.
                target[targetIndex] = source[left++];
            }
            else if (left < middle)
            {
                // If both have elements, we need to compare.
                JSValue leftElement = source[left];
                JSValue rightElement = source[right];
                if (CallCompare(isolate, comparefn, leftElement, rightElement) <= 0)
                {
                    target[targetIndex] = leftElement;
                    left++;
                }
                else
                {
                    target[targetIndex] = rightElement;
                    right++;
                }
            }
            else
            {
                // No elements on the left, but the right does, so we take
                // from the right.
                Debug.Assert(left == middle);
                target[targetIndex] = source[right++];
            }
        }
    }

    /// <summary>TypedArrayMergeSort.</summary>
    static void TypedArrayMergeSort(Isolate isolate, JSValue[] source, int from, int to, JSValue[] target, JSValue comparefn)
    {
        Debug.Assert(to - from > 1);
        int middle = from + ((to - from) >>> 1);

        // On the next recursion step source becomes target and vice versa.
        // This saves the copy of the relevant range from the original
        // array into a work array on each recursion step.
        if (middle - from > 1) TypedArrayMergeSort(isolate, target, from, middle, source, comparefn);
        if (to - middle > 1) TypedArrayMergeSort(isolate, target, middle, to, source, comparefn);

        TypedArrayMerge(isolate, comparefn, source, from, middle, to, target);
    }

    /// <summary>TypedArraySortCommon: shared between sort and toSorted.</summary>
    static JSTypedArray TypedArraySortCommon(Isolate isolate, JSTypedArray array, ulong len, JSValue comparefnArg, bool isSort)
    {
        // Arrays of length 1 or less are considered sorted.
        if (len < 2) return array;

        // Default sorting is done in C++ using std::sort
        if (comparefnArg.IsUndefined) return TypedArraySortFast(array);

        // Throw rather than crash if the TypedArray's size exceeds max FixedArray
        // size (which we need below).
        if (len > FixedArrayBase.kMaxLength) isolate.ThrowTypeError(MessageTemplate.TypedArrayTooLargeToSort);

        // Prepare the two work arrays. All numbers are converted to tagged
        // objects first, and merge sorted between the two FixedArrays.
        // The result is then written back into the JSTypedArray.
        int n = (int)len;
        var work1 = new JSValue[n];
        var work2 = new JSValue[n];
        for (int i = 0; i < n; ++i)
        {
            JSValue element = TypedArrayElementsOps.Load(isolate, array, (ulong)i);
            work1[i] = element;
            work2[i] = element;
        }

        TypedArrayMergeSort(isolate, work2, 0, n, work1, comparefnArg);

        // If this is TypedArray.prototype.sort, reload the length; it's possible the
        // backing ArrayBuffer has been resized to be OOB or detached, in which case
        // treat it as length 0.
        //
        // This is not possible in TypedArray.prototype.toSorted as the array being
        // sorted is a copy that has not yet escaped to user script.
        ulong writebackLen = len;
        if (isSort)
        {
            if (TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out ulong newLen))
            {
                if (newLen < writebackLen) writebackLen = newLen;
            }
            else
            {
                writebackLen = 0;
            }
        }

        // work1 contains the sorted numbers. Write them back.
        for (ulong i = 0; i < writebackLen; ++i) TypedArrayElementsOps.StoreNumeric(array, i, work1[i]);

        return array;
    }

    /// <summary>Runtime_TypedArraySortFast: numeric ascending order, -0 before +0, NaN last.</summary>
    internal static JSTypedArray TypedArraySortFast(JSTypedArray array)
    {
        Debug.Assert(!array.WasDetached && !array.IsOutOfBounds);
        // After reading the byte length, avoid reading the bytelength or length
        // again. If the buffer is shared, it might have been grown by a
        // background thread. In that case we should ignore the new length and just
        // sort the old elements and write them into the beginning of the array.
        ulong byteLength = array.GetByteLength();
        Span<byte> data = array.Buffer.BackingStoreBuffer.AsSpan((int)array.ByteOffset, (int)byteLength);

        // In case of a SAB, V8 sorts a copy (std::sort may crash on concurrently
        // modified data); Span.Sort is safe on shared memory, so the copy is
        // only kept for the same observable result under races.
        byte[]? copy = array.Buffer.IsShared ? data.ToArray() : null;
        Span<byte> target = copy is null ? data : copy;

        switch (TypedArrayElementsOps.BaseKind(array.Kind))
        {
            case ElementsKind.INT8_ELEMENTS: MemoryMarshal.Cast<byte, sbyte>(target).Sort(); break;
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.UINT8_CLAMPED_ELEMENTS: target.Sort(); break;
            case ElementsKind.INT16_ELEMENTS: MemoryMarshal.Cast<byte, short>(target).Sort(); break;
            case ElementsKind.UINT16_ELEMENTS: MemoryMarshal.Cast<byte, ushort>(target).Sort(); break;
            case ElementsKind.INT32_ELEMENTS: MemoryMarshal.Cast<byte, int>(target).Sort(); break;
            case ElementsKind.UINT32_ELEMENTS: MemoryMarshal.Cast<byte, uint>(target).Sort(); break;
            case ElementsKind.BIGINT64_ELEMENTS: MemoryMarshal.Cast<byte, long>(target).Sort(); break;
            case ElementsKind.BIGUINT64_ELEMENTS: MemoryMarshal.Cast<byte, ulong>(target).Sort(); break;
            case ElementsKind.FLOAT64_ELEMENTS: SortFloats(MemoryMarshal.Cast<byte, double>(target)); break;
            case ElementsKind.FLOAT32_ELEMENTS: SortFloats(MemoryMarshal.Cast<byte, float>(target)); break;
            case ElementsKind.FLOAT16_ELEMENTS: SortFloat16(MemoryMarshal.Cast<byte, ushort>(target)); break;
        }

        copy?.AsSpan().CopyTo(data);
        return array;
    }

    /// <summary>
    /// std::sort with CompareNum over floating point elements: NaNs last, -0
    /// before +0. .NET's IComparable order puts NaN first and does not order
    /// the zeros, so NaNs are partitioned out and the zero run fixed up.
    /// </summary>
    static void SortFloats<T>(Span<T> data) where T : struct, System.Numerics.IFloatingPointIeee754<T>
    {
        // Partition the NaNs to the end.
        int end = data.Length;
        for (int i = 0; i < end;)
        {
            if (T.IsNaN(data[i]))
            {
                end--;
                (data[i], data[end]) = (data[end], data[i]);
            }
            else
            {
                i++;
            }
        }
        Span<T> numbers = data[..end];
        numbers.Sort();
        // -0.0 is less than +0.0: rewrite the run of zeros.
        int firstZero = -1, zeroCount = 0, negativeZeros = 0;
        for (int i = 0; i < numbers.Length; i++)
        {
            if (T.IsZero(numbers[i]))
            {
                if (firstZero < 0) firstZero = i;
                zeroCount++;
                if (T.IsNegative(numbers[i])) negativeZeros++;
            }
            else if (firstZero >= 0)
            {
                break;
            }
        }
        if (negativeZeros > 0)
        {
            numbers.Slice(firstZero, negativeZeros).Fill(T.NegativeZero);
            numbers.Slice(firstZero + negativeZeros, zeroCount - negativeZeros).Fill(T.Zero);
        }
    }

    /// <summary>std::sort with LessThanFloat16RawBits.</summary>
    static void SortFloat16(Span<ushort> data)
    {
        // Sort as doubles (exact) and convert back: equal values keep their bit
        // patterns except among NaNs (whose order std::sort leaves unspecified).
        var values = new double[data.Length];
        for (int i = 0; i < data.Length; i++) values[i] = TypedArrayScalars.Float16ToDouble(data[i]);
        var bits = data.ToArray();
        var order = new int[data.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            double a = values[x], b = values[y];
            if (CompareNumLess(a, b)) return -1;
            if (CompareNumLess(b, a)) return 1;
            return x.CompareTo(y);
        });
        for (int i = 0; i < order.Length; i++) data[i] = bits[order[i]];
    }

    /// <summary>CompareNum&lt;T&gt; (runtime-typedarray.cc) for floating point values.</summary>
    static bool CompareNumLess(double x, double y)
    {
        if (x < y) return true;
        if (x > y) return false;
        if (x == 0 && x == y) return double.IsNegative(x) && !double.IsNegative(y);  // -0.0 is less than +0.0
        return !double.IsNaN(x) && double.IsNaN(y);  // number is less than NaN
    }

    /// <summary>ES #sec-%typedarray%.prototype.sort.</summary>
    public static JSValue TypedArrayPrototypeSort(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If comparefn is not undefined and IsCallable(comparefn) is false,
        //    throw a TypeError exception.
        JSValue comparefnObj = args.AtOrUndefined(1);
        if (!comparefnObj.IsUndefined && !ObjectOps.IsCallable(comparefnObj))
        {
            return isolate.ThrowTypeError(MessageTemplate.BadSortComparisonFunction, comparefnObj);
        }

        // 2. Let obj be the this value.
        // 3. Let buffer be ? ValidateTypedArray(obj).
        // 4. Let len be IntegerIndexedObjectLength(obj).
        JSTypedArray array = ValidateTypedArrayAndGetLength(isolate, args.Receiver, "%TypedArray%.prototype.sort",
            TypedArrayAccessMode.kWrite, out ulong len);
        return TypedArraySortCommon(isolate, array, len, comparefnObj, isSort: true);
    }

    /// <summary>https://tc39.es/proposal-change-array-by-copy/#sec-%typedarray%.prototype.toSorted</summary>
    public static JSValue TypedArrayPrototypeToSorted(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If comparefn is not undefined and IsCallable(comparefn) is false,
        //    throw a TypeError exception.
        JSValue comparefnObj = args.AtOrUndefined(1);
        if (!comparefnObj.IsUndefined && !ObjectOps.IsCallable(comparefnObj))
        {
            return isolate.ThrowTypeError(MessageTemplate.BadSortComparisonFunction, comparefnObj);
        }

        // 2. Let O be the this value.
        // 3. Perform ? ValidateTypedArray(O).
        // 4. Let buffer be obj.[[ViewedArrayBuffer]].
        // 5. Let len be O.[[ArrayLength]].
        JSTypedArray array = ValidateTypedArrayAndGetLength(isolate, args.Receiver, "%TypedArray%.prototype.toSorted",
            TypedArrayAccessMode.kRead, out ulong len);

        // 6. Let A be ? TypedArrayCreateSameType(O, « 𝔽(len) »).
        JSTypedArray copy = TypedArrayCreateSameType(isolate, array, len);

        // Perform the sorting by copying the source TypedArray and sorting the copy
        // in-place using the same code that as TypedArray.prototype.sort
        if (len > 0) array.DataSpan(0, len).CopyTo(copy.DataSpan(0, len));

        return TypedArraySortCommon(isolate, copy, len, comparefnObj, isSort: false);
    }

    // ---- toReversed / with ----------------------------------------------------------------------

    /// <summary>https://tc39.es/proposal-change-array-by-copy/#sec-%typedarray%.prototype.toReversed</summary>
    public static JSValue TypedArrayPrototypeToReversed(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be the this value.
        // 2. Perform ? ValidateTypedArray(O).
        // 3. Let length be O.[[ArrayLength]].
        JSTypedArray src = ValidateTypedArrayAndGetLength(isolate, args.Receiver, "%TypedArray%.prototype.toReversed",
            TypedArrayAccessMode.kRead, out ulong len);

        // 4. Let A be ? TypedArrayCreateSameType(O, « 𝔽(length) »).
        JSTypedArray copy = TypedArrayCreateSameType(isolate, src, len);

        // 5-6. Copy element length - k - 1 to k (the bit patterns are preserved,
        // as LoadNumeric/StoreNumeric of the same kind do).
        if (len > 0)
        {
            src.DataSpan(0, len).CopyTo(copy.DataSpan(0, len));
            ReverseElements(copy);
        }

        // 7. Return A.
        return copy;
    }

    /// <summary>ES #sec-%typedarray%.prototype.with.</summary>
    public static JSValue TypedArrayPrototypeWith(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.prototype.with";
        // 1. Let O be the this value.
        // 2. Let taRecord be ? ValidateTypedArray(O, seq-cst).
        // 3. Let len be TypedArrayLength(taRecord).
        JSTypedArray array = EnsureValidAndReadLength(isolate, args.Receiver, name, out ulong originalLength);

        // 4. Let relativeIndex be ? ToIntegerOrInfinity(index).
        double relativeIndex = ObjectOps.IntegerValue(isolate, args.AtOrUndefined(1));

        // 5. If relativeIndex ≥ 0, let actualIndex be relativeIndex.
        // 6. Else, let actualIndex be len + relativeIndex.
        double actualIndex = relativeIndex >= 0 ? relativeIndex : originalLength + relativeIndex;

        // 7. If O.[[ContentType]] is BigInt, set value to ? ToBigInt(value).
        // 8. Else, set value to ? ToNumber(value).
        JSValue value = TypedArrayElementsOps.PrepareValue(isolate, array.Kind, args.AtOrUndefined(2));

        // 9. If IsValidIntegerIndex(O, 𝔽(actualIndex)) is false, throw a
        // RangeError exception.
        if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out ulong currentLength) ||
            actualIndex < 0 || actualIndex >= currentLength)
        {
            return isolate.ThrowRangeError(MessageTemplate.InvalidTypedArrayIndex);
        }

        // 10. Let A be ? TypedArrayCreateSameType(O, « 𝔽(len) »).
        JSTypedArray copy = TypedArrayCreateSameType(isolate, array, originalLength);
        ulong fastCopyableLength = Math.Min(originalLength, currentLength);

        // Steps 11-12's copy loop implemented by memmove.
        if (fastCopyableLength > 0) array.DataSpan(0, fastCopyableLength).CopyTo(copy.DataSpan(0, fastCopyableLength));

        // b. If k is actualIndex, then
        //   i. Perform ? Set(A, Pk, value, true).
        ulong actualIndexUint = (ulong)actualIndex;
        if (actualIndexUint < originalLength) TypedArrayElementsOps.StoreNumeric(copy, actualIndexUint, value);

        // Fill the remainder with undefined, in case of resize during parameter
        // conversion. This is not the same as doing nothing because:
        // - Undefined convert to NaN, which is observable when stored into
        //   Float32 and Float64Arrays
        // - Undefined cannot convert to BigInt and throws
        ulong copyLength = copy.GetLength();
        for (ulong k = fastCopyableLength; k < copyLength; ++k)
        {
            if (!TypedArrayElementsOps.StoreJSAny(isolate, copy, k, JSValue.Undefined))
            {
                throw new InvalidOperationException("StoreJSAnyInBounds failed");
            }
        }

        // 11. Return A.
        return copy;
    }

    // ---- from / of (typed-array-from.tq, typed-array-of.tq) -----------------------------------

    /// <summary>ES #sec-%typedarray%.from.</summary>
    public static JSValue TypedArrayFrom(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.from";
        JSValue source = args.AtOrUndefined(1);
        JSValue mapfnObj = args.AtOrUndefined(2);
        JSValue thisArg = args.AtOrUndefined(3);

        // 1. Let C be the this value.
        // 2. If IsConstructor(C) is false, throw a TypeError exception.
        JSValue constructor = args.Receiver;
        if (!ArrayBuiltinsUtils.IsConstructor(constructor)) return isolate.ThrowTypeError(MessageTemplate.NotConstructor, constructor);

        // 3. If mapfn is undefined, then let mapping be false.
        // 4. Else,
        //   a. If IsCallable(mapfn) is false, throw a TypeError exception.
        //   b. Let mapping be true.
        bool mapping = !mapfnObj.IsUndefined;
        if (mapping && !ObjectOps.IsCallable(mapfnObj)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, mapfnObj);

        ulong finalLength;
        JSValue finalSource;

        // 5. Let usingIterator be ? GetMethod(source, @@iterator).
        JSValue usingIterator = source.IsNullOrUndefined
            ? ObjectOps.GetPropertyOrElement(isolate, source, new PropertyKey(isolate, ReadOnlyRoots.iterator_symbol))
            : ArrayBuiltinsUtils.GetIteratorMethod(isolate, source);
        if (!usingIterator.IsNullOrUndefined)
        {
            if (!ObjectOps.IsCallable(usingIterator))
            {
                return isolate.ThrowTypeError(MessageTemplate.FirstArgumentIteratorSymbolNonCallable,
                    ArrayBuiltinsUtils.NewString(isolate, name));
            }
            if (!TryFromFastSource(isolate, source, usingIterator, mapping, out finalSource, out finalLength))
            {
                // 6. If usingIterator is not undefined, then
                //  a. Let values be ? IterableToList(source, usingIterator).
                //  b. Let len be the number of elements in values.
                JSArray values = ArrayBuiltinsUtils.IterableToList(isolate, source, usingIterator);
                finalLength = (ulong)values.Length.Number;
                finalSource = values;
            }
        }
        else
        {
            // 7. NOTE: source is not an Iterable so assume it is already an
            // array-like object.

            // 8. Let arrayLike be ! ToObject(source).
            JSReceiver arrayLike = ObjectOps.ToObject(isolate, source);

            // 9. Let len be ? LengthOfArrayLike(arrayLike).
            double length = ArrayBuiltinsUtils.GetLengthProperty(isolate, arrayLike);
            finalLength = (ulong)length;
            finalSource = arrayLike;
        }

        double finalLengthNum = finalLength;

        // 6c/10. Let targetObj be ? TypedArrayCreate(C, «len»).
        JSTypedArray targetObj = TypedArrayCreateByLength(isolate, constructor, finalLengthNum, name, TypedArrayAccessMode.kWrite);

        if (!mapping)
        {
            // Fast path.
            if (finalLength != 0) TypedArrayElementsOps.CopyElementsHandle(isolate, finalSource, targetObj, finalLength, 0);
            return targetObj;
        }
        // Slow path.

        // 6d-6e and 11-12.
        // 11. Let k be 0.
        // 12. Repeat, while k < len
        for (ulong k = 0; k < finalLength; k++)
        {
            // 12a. Let Pk be ! ToString(k).
            JSValue kNum = JSValue.FromNumber(k);

            // 12b. Let kValue be ? Get(arrayLike, Pk).
            JSValue kValue = ArrayBuiltinsUtils.GetProperty(isolate, finalSource, (double)k);

            // 12c. If mapping is true, then
            //   i. Let mappedValue be ? Call(mapfn, T, « kValue, k »).
            JSValue mappedValue = Execution.Call(isolate, mapfnObj, thisArg, [kValue, kNum]);

            // 12e. Perform ? Set(targetObj, Pk, mappedValue, true).
            // The buffer may be detached or the target TypedArray may go out of
            // bounds during executing ToNumber/ToBigInt or when we executed the
            // mapper function above.
            TypedArrayElementsOps.StoreJSAny(isolate, targetObj, k, mappedValue);
        }
        return targetObj;
    }

    /// <summary>
    /// The %TypedArray%.from fast paths: a source with its built-in iterator
    /// and an intact %ArrayIteratorPrototype%.next is used directly.
    /// </summary>
    static bool TryFromFastSource(Isolate isolate, JSValue source, JSValue usingIterator, bool mapping, out JSValue finalSource,
        out ulong finalLength)
    {
        finalSource = default;
        finalLength = 0;
        // If there is a mapping, we need to gather the values from the
        // iterables before applying the mapping.
        if (mapping) return false;
        if (usingIterator.HeapObjectOrNull is not JSFunction) return false;
        // Check that the ArrayIterator prototype's "next" method hasn't been
        // overridden.
        if (!Protectors.IsArrayIteratorLookupChainIntact(isolate)) return false;

        switch (source.HeapObjectOrNull)
        {
            case JSArray sourceArray:
            {
                // Check that the iterator function is exactly
                // Builtin::kArrayPrototypeValues.
                if (!IsBuiltinFunction(usingIterator, Builtin.ArrayPrototypeValues)) return false;
                // Source is a JSArray with unmodified iterator behavior. Use the
                // source object directly, taking advantage of the special-case code
                // in TypedArrayCopyElements
                finalLength = (ulong)sourceArray.Length.Number;
                ElementsKind kind = sourceArray.GetElementsKind();
                if (ElementsKinds.IsSmiElementsKind(kind) || ElementsKinds.IsDoubleElementsKind(kind))
                {
                    finalSource = sourceArray;
                    return true;
                }
                // iterator::FastIterableToList
                if (!BuiltinsArray.IsFastJSArrayWithNoCustomIteration(isolate, sourceArray)) return false;
                finalSource = BuiltinsArray.CloneFastJSArrayFillingHoles(isolate, sourceArray);
                return true;
            }
            case JSTypedArray sourceTypedArray:
            {
                if (!TypedArrayElementsOps.TryGetLengthAndValidate(sourceTypedArray, TypedArrayAccessMode.kRead, out finalLength))
                {
                    return false;
                }
                // Check that the iterator function is exactly
                // Builtin::kTypedArrayPrototypeValues.
                if (!IsBuiltinFunction(usingIterator, Builtin.TypedArrayPrototypeValues)) return false;
                finalSource = source;
                return true;
            }
        }
        // (V8 also has a fast path for JSSets with the original values
        // iterator; it produces the same result as iterating.)
        return false;
    }

    /// <summary>ES #sec-%typedarray%.of.</summary>
    public static JSValue TypedArrayOf(Isolate isolate, in BuiltinArguments args)
    {
        const string name = "%TypedArray%.of";
        // 1. Let len be the actual number of arguments passed to this function.
        int len = args.ArgcWithoutReceiver;

        // 3. Let C be the this value.
        // 4. If IsConstructor(C) is false, throw a TypeError exception.
        JSValue constructor = args.Receiver;
        if (!ArrayBuiltinsUtils.IsConstructor(constructor)) return isolate.ThrowTypeError(MessageTemplate.NotConstructor, constructor);

        // 5. Let newObj be ? TypedArrayCreate(C, len).
        JSTypedArray newObj = TypedArrayCreateByLength(isolate, constructor, len, name, TypedArrayAccessMode.kWrite);

        // 6. Let k be 0.
        // 7. Repeat, while k < len
        ReadOnlySpan<JSValue> items = args.Arguments;
        for (int k = 0; k < len; k++)
        {
            // 7a. Let kValue be items[k].
            // 7b. Let Pk be ! ToString(k).
            // 7c. Perform ? Set(newObj, Pk, kValue, true).
            // Buffer may be detached during executing ToNumber/ToBigInt.
            TypedArrayElementsOps.StoreJSAny(isolate, newObj, (ulong)k, items[k]);
        }

        // 8. Return newObj.
        return newObj;
    }
}
