// The fast paths of the hottest TFJ/Torque builtins, taken by the interpreter's
// call handlers before the generic call path, as V8's builtins take them on
// entry:
//   math.tq          MathAbs/Ceil/Floor/Round/Trunc/Sqrt: typeswitch on Smi and
//                    HeapNumber; MathMax/MathMin/MathPow/MathAtan2 with Number
//                    arguments.
//   builtins-string.tq  StringPrototypeCharCodeAt/CharAt/CodePointAt: a String
//                    receiver and a Smi position (ToInteger_Inline's fast case).
//   number.tq        NumberPrototypeToString without a radix: NumberToString
//                    (the number-string cache).
//   typed_array.tq   TypedArrayPrototypeLength (the length getter) of an
//                    attached fixed-length typed array.
//   array-push/pop/shift (builtins-array-gen.cc): BuiltinsArray.TryFastPush,
//                    TryFastPop, TryFastShift.
// Each returns false, having done nothing, when its fast case does not apply;
// the call then goes through InterpreterCalls.Call and the builtin itself.
// None of these cases can run JavaScript or throw, so they need no builtin
// frame record (see BuiltinFramelessCalls.cs).
using System.Runtime.CompilerServices;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Builtins;

public static class BuiltinFastPaths
{
    /// <summary>The fast paths of builtins called with no arguments.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCall0(Isolate isolate, JSValue callee, JSValue receiver, out JSValue result)
    {
        if (callee._obj is JSFunction function && function.Shared.BuiltinId != Builtin.NoBuiltinId)
        {
            return TryCall0(isolate, function.Shared.BuiltinId, receiver, out result);
        }
        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TryCall0(Isolate isolate, Builtin id, JSValue receiver, out JSValue result)
    {
        switch (id)
        {
            case Builtin.ArrayPrototypePop:
                return BuiltinsArray.TryFastPop(isolate, receiver, out result);
            case Builtin.ArrayPrototypeShift:
                return BuiltinsArray.TryFastShift(isolate, receiver, out result);
            case Builtin.NumberPrototypeToString:
                if (receiver._obj == NumberTag.Instance)
                {
                    result = isolate.Factory.NumberToString(receiver);
                    return true;
                }
                break;
            case Builtin.TypedArrayPrototypeLength:
                // The getter (typed_array.tq) on an attached fixed-length array:
                // JSTypedArray::length.
                if (receiver._obj is JSTypedArray typedArray && TypedArrayElementsOps.TryGetFixedLength(typedArray, out ulong length))
                {
                    result = JSValue.FromNumber(length);
                    return true;
                }
                break;
        }
        result = default;
        return false;
    }

    /// <summary>The fast paths of builtins called with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCall1(Isolate isolate, JSValue callee, JSValue receiver, JSValue arg0, out JSValue result)
    {
        if (callee._obj is JSFunction function && function.Shared.BuiltinId != Builtin.NoBuiltinId)
        {
            return TryCall1(isolate, function, receiver, arg0, out result);
        }
        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TryCall1(Isolate isolate, JSFunction function, JSValue receiver, JSValue arg0, out JSValue result)
    {
        Builtin id = function.Shared.BuiltinId;
        if (arg0._obj == NumberTag.Instance)
        {
            double x = arg0._num;
            switch (id)
            {
                case Builtin.MathFloor:
                    result = JSValue.FromNumber(Math.Floor(x));
                    return true;
                case Builtin.MathCeil:
                    result = JSValue.FromNumber(Math.Ceiling(x));
                    return true;
                case Builtin.MathRound:
                    result = JSValue.FromNumber(BuiltinsMath.Float64Round(x));
                    return true;
                case Builtin.MathTrunc:
                    result = JSValue.FromNumber(Math.Truncate(x));
                    return true;
                case Builtin.MathAbs:
                    result = JSValue.FromNumber(Math.Abs(x));
                    return true;
                case Builtin.MathSqrt:
                    result = JSValue.FromNumber(Math.Sqrt(x));
                    return true;
                case Builtin.StringPrototypeCharCodeAt:
                case Builtin.StringPrototypeCharAt:
                case Builtin.StringPrototypeCodePointAt:
                    if (receiver.StringOrNull is JSString s)
                    {
                        // GenerateStringAt with a Smi position.
                        int index = (int)x;
                        if (index != x) break;
                        if ((uint)index >= (uint)s.Length)
                        {
                            result = id switch
                            {
                                Builtin.StringPrototypeCharCodeAt => JSValue.NaN,
                                Builtin.StringPrototypeCharAt => ReadOnlyRoots.empty_string,
                                _ => JSValue.Undefined,
                            };
                            return true;
                        }
                        if (id == Builtin.StringPrototypeCodePointAt) break;
                        char c = s is SeqString seq ? seq.Value[index] : s.Get(index);
                        result = id == Builtin.StringPrototypeCharCodeAt
                            ? JSValue.FromInt(c)
                            : BuiltinsString.StringFromSingleCharCode(isolate, c);
                        return true;
                    }
                    break;
            }
        }
        if (id == Builtin.ArrayPrototypePush) return BuiltinsArray.TryFastPush(isolate, function, receiver, arg0, out result);
        result = default;
        return false;
    }

    /// <summary>The fast paths of builtins called with two arguments.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCall2(JSValue callee, JSValue arg0, JSValue arg1, out JSValue result)
    {
        if (callee._obj is JSFunction function && function.Shared.BuiltinId != Builtin.NoBuiltinId &&
            arg0._obj == NumberTag.Instance && arg1._obj == NumberTag.Instance)
        {
            return TryCall2(function.Shared.BuiltinId, arg0._num, arg1._num, out result);
        }
        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TryCall2(Builtin id, double x, double y, out JSValue result)
    {
        switch (id)
        {
            case Builtin.MathMax:
                result = JSValue.FromNumber(Math.Max(x, y));
                return true;
            case Builtin.MathMin:
                result = JSValue.FromNumber(Math.Min(x, y));
                return true;
            case Builtin.MathPow:
                result = JSValue.FromNumber(V8Sharp.Base.Numbers.InternalMath.pow(x, y));
                return true;
            case Builtin.MathAtan2:
                result = JSValue.FromNumber(V8Sharp.Base.Ieee754.atan2(x, y));
                return true;
        }
        result = default;
        return false;
    }
}
