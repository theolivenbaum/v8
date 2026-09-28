// Port of the callback-driven Array builtins:
//   src/builtins/array-foreach.tq, array-every.tq, array-some.tq,
//   array-filter.tq, array-map.tq, array-reduce.tq, array-reduce-right.tq,
//   array-find.tq, array-findindex.tq, array-findlast.tq,
//   array-findlastindex.tq
// V8 runs each through a FastArray* loop over fast JSArrays that bails out
// into the generic *LoopContinuation when the array changes shape. The fast
// loops are not observable, so V8Sharp runs the continuation directly with a
// fast element probe (TryGetFastElement) in place of HasProperty/Get for
// fast JSArrays whose prototype chain has no elements.
using System.Runtime.CompilerServices;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterArrayIterating()
    {
        Register(Builtin.ArrayForEach, BuiltinsArray.ArrayForEach);
        Register(Builtin.ArrayEvery, BuiltinsArray.ArrayEvery);
        Register(Builtin.ArraySome, BuiltinsArray.ArraySome);
        Register(Builtin.ArrayFilter, BuiltinsArray.ArrayFilter);
        Register(Builtin.ArrayMap, BuiltinsArray.ArrayMap);
        Register(Builtin.ArrayReduce, BuiltinsArray.ArrayReduce);
        Register(Builtin.ArrayReduceRight, BuiltinsArray.ArrayReduceRight);
        Register(Builtin.ArrayPrototypeFind, BuiltinsArray.ArrayPrototypeFind);
        Register(Builtin.ArrayPrototypeFindIndex, BuiltinsArray.ArrayPrototypeFindIndex);
        Register(Builtin.ArrayPrototypeFindLast, BuiltinsArray.ArrayPrototypeFindLast);
        Register(Builtin.ArrayPrototypeFindLastIndex, BuiltinsArray.ArrayPrototypeFindLastIndex);
    }
}

public static partial class BuiltinsArray
{
    /// <summary>RequireObjectCoercible(receiver, methodName): kCalledOnNullOrUndefined.</summary>
    internal static void RequireObjectCoercible(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.IsNullOrUndefined)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined, ArrayBuiltinsUtils.NewString(isolate, methodName));
        }
    }

    /// <summary>
    /// The element k of a fast JSArray without elements on its prototype chain:
    /// true when the answer is known without a lookup, with present = false for a
    /// hole (then HasProperty is false and Get is undefined).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetFastElement(Isolate isolate, JSReceiver o, double k, out JSValue value, out bool present)
    {
        if (o is JSArray array && k < array.Elements.Length &&
            array.Map.ElementsKind <= ElementsKind.LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND &&
            IsPrototypeInitialArrayPrototype(isolate, array.Map) && Protectors.IsNoElementsIntact(isolate))
        {
            int index = (int)k;
            if (array.Elements is FixedDoubleArray d)
            {
                present = !d.IsTheHole(index);
                value = present ? JSValue.FromNumber(d.GetScalar(index)) : JSValue.Undefined;
            }
            else
            {
                JSValue v = ((FixedArray)array.Elements).Data[index];
                present = !v.IsTheHole;
                value = present ? v : JSValue.Undefined;
            }
            return true;
        }
        value = JSValue.Undefined;
        present = false;
        return false;
    }

    /// <summary>HasProperty(O, Pk), then Get(O, Pk) when present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryGetPresentElement(Isolate isolate, JSReceiver o, double k, out JSValue value)
    {
        if (TryGetFastElement(isolate, o, k, out value, out bool present)) return present;
        if (!ArrayBuiltinsUtils.HasProperty(isolate, o, k)) return false;
        value = ArrayBuiltinsUtils.GetProperty(isolate, o, k);
        return true;
    }

    /// <summary>Get(O, Pk).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSValue GetElementValue(Isolate isolate, JSReceiver o, double k)
    {
        if (TryGetFastElement(isolate, o, k, out JSValue value, out _)) return value;
        return ArrayBuiltinsUtils.GetProperty(isolate, o, k);
    }

    /// <summary>Steps 1-3 shared by the callback builtins: O, len and callbackfn.</summary>
    static JSReceiver PrepareCallbackBuiltin(Isolate isolate, in BuiltinArguments args, string methodName,
        out double len, out JSValue callbackfn)
    {
        RequireObjectCoercible(isolate, args.Receiver, methodName);
        // 1. Let O be ? ToObject(this value).
        JSReceiver o = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Let len be ? ToLength(? Get(O, "length")).
        len = ArrayBuiltinsUtils.GetLengthProperty(isolate, o);
        // 3. If IsCallable(callbackfn) is false, throw a TypeError exception.
        callbackfn = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(callbackfn)) ArrayBuiltinsUtils.ThrowCalledNonCallable(isolate, callbackfn);
        return o;
    }

    /// <summary>ES #sec-array.prototype.foreach (ArrayForEachLoopContinuation).</summary>
    public static JSValue ArrayForEach(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.forEach", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                Execution.Call(isolate, callbackfn, thisArg, [kValue, JSValue.FromNumber(k), o]);
            }
        }
        return JSValue.Undefined;
    }

    /// <summary>ES #sec-array.prototype.every (ArrayEveryLoopContinuation).</summary>
    public static JSValue ArrayEvery(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.every", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                JSValue result = Execution.Call(isolate, callbackfn, thisArg, [kValue, JSValue.FromNumber(k), o]);
                if (!ObjectOps.BooleanValue(result)) return JSValue.False;
            }
        }
        return JSValue.True;
    }

    /// <summary>ES #sec-array.prototype.some (ArraySomeLoopContinuation).</summary>
    public static JSValue ArraySome(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.some", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                JSValue result = Execution.Call(isolate, callbackfn, thisArg, [kValue, JSValue.FromNumber(k), o]);
                if (ObjectOps.BooleanValue(result)) return JSValue.True;
            }
        }
        return JSValue.False;
    }

    /// <summary>ES #sec-array.prototype.filter (ArrayFilterLoopContinuation).</summary>
    public static JSValue ArrayFilter(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.filter", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        // FastFilterSpeciesCreate / ArraySpeciesCreate(O, 0).
        JSReceiver output = ArraySpeciesCreate(isolate, args.Receiver, 0);
        double to = 0;
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                JSValue result = Execution.Call(isolate, callbackfn, thisArg, [kValue, JSValue.FromNumber(k), o]);
                if (ObjectOps.BooleanValue(result))
                {
                    // 1. Perform ? CreateDataPropertyOrThrow(A, ToString(to), kValue).
                    ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, output, to, kValue);
                    to++;
                }
            }
        }
        return output;
    }

    /// <summary>ES #sec-array.prototype.map (ArrayMapLoopContinuation).</summary>
    public static JSValue ArrayMap(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.map", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        // 5. Let A be ? ArraySpeciesCreate(O, len).
        JSReceiver array = ArraySpeciesCreate(isolate, args.Receiver, len);
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue kValue))
            {
                JSValue mappedValue = Execution.Call(isolate, callbackfn, thisArg, [kValue, JSValue.FromNumber(k), o]);
                // iii. Perform ? CreateDataPropertyOrThrow(A, Pk, mapped_value).
                ArrayBuiltinsUtils.CreateDataPropertyOrThrow(isolate, array, k, mappedValue);
            }
        }
        return array;
    }

    /// <summary>ES #sec-array.prototype.reduce (ArrayReduceLoopContinuation).</summary>
    public static JSValue ArrayReduce(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.reduce", out double len, out JSValue callbackfn);
        bool hasAccumulator = args.ArgcWithoutReceiver > 1;
        JSValue accumulator = hasAccumulator ? args.AtOrUndefined(2) : JSValue.Undefined;
        for (double k = 0; k < len; k++)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue value))
            {
                if (!hasAccumulator)
                {
                    accumulator = value;
                    hasAccumulator = true;
                }
                else
                {
                    accumulator = Execution.Call(isolate, callbackfn, JSValue.Undefined,
                        [accumulator, value, JSValue.FromNumber(k), o]);
                }
            }
        }
        if (!hasAccumulator)
        {
            return isolate.ThrowTypeError(MessageTemplate.ReduceNoInitial, ArrayBuiltinsUtils.NewString(isolate, "Array.prototype.reduce"));
        }
        return accumulator;
    }

    /// <summary>ES #sec-array.prototype.reduceright (ArrayReduceRightLoopContinuation).</summary>
    public static JSValue ArrayReduceRight(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.reduceRight", out double len, out JSValue callbackfn);
        bool hasAccumulator = args.ArgcWithoutReceiver > 1;
        JSValue accumulator = hasAccumulator ? args.AtOrUndefined(2) : JSValue.Undefined;
        for (double k = len - 1; k >= 0; k--)
        {
            if (TryGetPresentElement(isolate, o, k, out JSValue value))
            {
                if (!hasAccumulator)
                {
                    accumulator = value;
                    hasAccumulator = true;
                }
                else
                {
                    accumulator = Execution.Call(isolate, callbackfn, JSValue.Undefined,
                        [accumulator, value, JSValue.FromNumber(k), o]);
                }
            }
        }
        if (!hasAccumulator)
        {
            return isolate.ThrowTypeError(MessageTemplate.ReduceNoInitial, ArrayBuiltinsUtils.NewString(isolate, "Array.prototype.reduceRight"));
        }
        return accumulator;
    }

    /// <summary>ES #sec-array.prototype.find (ArrayFindLoopContinuation).</summary>
    public static JSValue ArrayPrototypeFind(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.find", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = 0; k < len; k++)
        {
            JSValue value = GetElementValue(isolate, o, k);
            JSValue testResult = Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), o]);
            if (ObjectOps.BooleanValue(testResult)) return value;
        }
        return JSValue.Undefined;
    }

    /// <summary>ES #sec-array.prototype.findIndex (ArrayFindIndexLoopContinuation).</summary>
    public static JSValue ArrayPrototypeFindIndex(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.findIndex", out double len, out JSValue callbackfn);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = 0; k < len; k++)
        {
            JSValue value = GetElementValue(isolate, o, k);
            JSValue testResult = Execution.Call(isolate, callbackfn, thisArg, [value, JSValue.FromNumber(k), o]);
            if (ObjectOps.BooleanValue(testResult)) return JSValue.FromNumber(k);
        }
        return JSValue.FromInt(-1);
    }

    /// <summary>ES #sec-array.prototype.findlast (ArrayFindLastLoopContinuation).</summary>
    public static JSValue ArrayPrototypeFindLast(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.findLast", out double len, out JSValue predicate);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = len - 1; k >= 0; k--)
        {
            JSValue value = GetElementValue(isolate, o, k);
            JSValue testResult = Execution.Call(isolate, predicate, thisArg, [value, JSValue.FromNumber(k), o]);
            if (ObjectOps.BooleanValue(testResult)) return value;
        }
        return JSValue.Undefined;
    }

    /// <summary>ES #sec-array.prototype.findlastindex (ArrayFindLastIndexLoopContinuation).</summary>
    public static JSValue ArrayPrototypeFindLastIndex(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver o = PrepareCallbackBuiltin(isolate, args, "Array.prototype.findLastIndex", out double len, out JSValue predicate);
        JSValue thisArg = args.AtOrUndefined(2);
        for (double k = len - 1; k >= 0; k--)
        {
            JSValue value = GetElementValue(isolate, o, k);
            JSValue testResult = Execution.Call(isolate, predicate, thisArg, [value, JSValue.FromNumber(k), o]);
            if (ObjectOps.BooleanValue(testResult)) return JSValue.FromNumber(k);
        }
        return JSValue.FromInt(-1);
    }
}
