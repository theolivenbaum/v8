// The lean call path of the leaf builtins (Math.*, String.prototype.charCodeAt,
// indexOf, slice ..., Array.prototype.indexOf/includes, Number.prototype.toString,
// String(x)). In V8 these are TFJ/Torque builtins, entered from the Call
// builtin with a plain JS-linkage frame; their fast paths (a Smi or
// HeapNumber argument, a String receiver, a fast JSArray) run no JavaScript
// and throw nothing, so their frame is never seen by a stack walk.
//
// Deviation: V8Sharp's CallBuiltin pushes a frame record (V8's builtin frame,
// so builtins appear in stack traces and Error.captureStackTrace), switches
// to the builtin's context and reserves register-stack slots for recursion.
// When the receiver and the arguments are such that the builtin cannot call
// back into JavaScript, throw, or allocate in a realm (FramelessCondition),
// none of that is observable, and the builtin is invoked directly.
using System.Runtime.CompilerServices;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

/// <summary>When a builtin runs without a frame record (see the file header).</summary>
public enum FramelessCondition : byte
{
    /// <summary>Always gets a frame.</summary>
    None,
    /// <summary>Every argument converts with ToNumber/ToString/ToIntegerOrInfinity without side effects.</summary>
    PrimitiveArguments,
    /// <summary>A String receiver and primitive arguments (String.prototype methods).</summary>
    StringReceiver,
    /// <summary>A packed fast JSArray receiver, any first argument, primitive others (indexOf/includes).</summary>
    PackedArrayReceiverAnySearch,
    /// <summary>A packed fast JSArray receiver and primitive arguments.</summary>
    PackedArrayReceiver,
    /// <summary>A Number receiver and no radix, or a radix in [2, 36] (Number.prototype.toString).</summary>
    NumberReceiverRadix,
}

public static partial class BuiltinRegistry
{
    static readonly FramelessCondition[] s_frameless = CreateFramelessConditions();

    static FramelessCondition[] CreateFramelessConditions()
    {
        var table = new FramelessCondition[kBuiltinCount];
        // math.tq: ToNumber_Inline of Smis and HeapNumbers.
        Builtin[] math =
        [
            Builtin.MathAbs, Builtin.MathCeil, Builtin.MathFloor, Builtin.MathRound, Builtin.MathTrunc, Builtin.MathPow,
            Builtin.MathMax, Builtin.MathMin, Builtin.MathAcos, Builtin.MathAcosh, Builtin.MathAsin, Builtin.MathAsinh,
            Builtin.MathAtan, Builtin.MathAtan2, Builtin.MathAtanh, Builtin.MathCbrt, Builtin.MathClz32, Builtin.MathCos,
            Builtin.MathCosh, Builtin.MathExp, Builtin.MathExpm1, Builtin.MathFround, Builtin.MathF16round, Builtin.MathImul,
            Builtin.MathLog, Builtin.MathLog1p, Builtin.MathLog10, Builtin.MathLog2, Builtin.MathSin, Builtin.MathSign,
            Builtin.MathSinh, Builtin.MathSqrt, Builtin.MathTan, Builtin.MathTanh, Builtin.MathHypot,
            Builtin.StringFromCharCode, Builtin.StringConstructor, Builtin.NumberConstructor, Builtin.NumberParseFloat,
            Builtin.NumberParseInt, Builtin.NumberIsInteger, Builtin.NumberIsNaN, Builtin.GlobalIsNaN, Builtin.GlobalIsFinite,
        ];
        foreach (Builtin b in math) table[(int)b] = FramelessCondition.PrimitiveArguments;
        // String.prototype methods that neither allocate in a realm nor throw
        // for a String receiver and primitive arguments.
        Builtin[] strings =
        [
            Builtin.StringPrototypeCharAt, Builtin.StringPrototypeCharCodeAt, Builtin.StringPrototypeCodePointAt,
            Builtin.StringPrototypeAt, Builtin.StringPrototypeIndexOf, Builtin.StringPrototypeLastIndexOf,
            Builtin.StringPrototypeIncludes, Builtin.StringPrototypeStartsWith, Builtin.StringPrototypeEndsWith,
            Builtin.StringPrototypeSlice, Builtin.StringPrototypeSubstring, Builtin.StringPrototypeSubstr,
            Builtin.StringPrototypeToLowerCase, Builtin.StringPrototypeToUpperCase, Builtin.StringPrototypeTrim,
            Builtin.StringPrototypeTrimStart, Builtin.StringPrototypeTrimEnd, Builtin.StringPrototypeToString,
            Builtin.StringPrototypeValueOf,
        ];
        foreach (Builtin b in strings) table[(int)b] = FramelessCondition.StringReceiver;
        table[(int)Builtin.ArrayIndexOf] = FramelessCondition.PackedArrayReceiverAnySearch;
        table[(int)Builtin.ArrayIncludes] = FramelessCondition.PackedArrayReceiverAnySearch;
        table[(int)Builtin.ArrayPrototypeLastIndexOf] = FramelessCondition.PackedArrayReceiverAnySearch;
        table[(int)Builtin.ArrayPrototypeAt] = FramelessCondition.PackedArrayReceiver;
        table[(int)Builtin.NumberPrototypeToString] = FramelessCondition.NumberReceiverRadix;
        return table;
    }

    /// <summary>
    /// True when a [[Call]] of <paramref name="builtin"/> with these values cannot
    /// run JavaScript, throw, or depend on the current context, so it needs no
    /// frame record (the file header).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanCallWithoutFrame(Builtin builtin, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        FramelessCondition condition = s_frameless[(int)builtin];
        if (condition == FramelessCondition.None) return false;
        return CanCallWithoutFrameSlow(condition, receiver, args);
    }

    static bool CanCallWithoutFrameSlow(FramelessCondition condition, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        int first = 0;
        switch (condition)
        {
            case FramelessCondition.PrimitiveArguments:
                break;
            case FramelessCondition.StringReceiver:
                if (!receiver.IsString) return false;
                break;
            case FramelessCondition.PackedArrayReceiverAnySearch:
            case FramelessCondition.PackedArrayReceiver:
                if (receiver._obj is not JSArray array || !array.Map.HasFastPackedElements) return false;
                if (condition == FramelessCondition.PackedArrayReceiverAnySearch) first = 1;
                break;
            case FramelessCondition.NumberReceiverRadix:
                if (!receiver.IsNumber) return false;
                if (args.Length == 0 || args[0].IsUndefined) return true;
                return args[0].IsNumber && args[0]._num >= 2 && args[0]._num <= 36;
            default:
                return false;
        }
        for (int i = first; i < args.Length; i++)
        {
            if (!IsPrimitiveWithoutSideEffects(args[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// undefined, null, booleans, numbers and strings: ToNumber, ToString and
    /// ToIntegerOrInfinity of these run no JavaScript and cannot throw
    /// (symbols and BigInts can throw).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsPrimitiveWithoutSideEffects(JSValue value)
    {
        HeapObject? o = value._obj;
        return o is null || ReferenceEquals(o, NumberTag.Instance) || o.IsString || o is Oddball;
    }
}
