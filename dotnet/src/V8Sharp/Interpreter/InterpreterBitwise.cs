// The int32 fast paths of the bitwise bytecode handlers
// (BinaryOpAssembler::Generate_BitwiseBinaryOpWithFeedback: both operands
// Smis, or numbers that truncate without loss). One specialization per
// operator (a struct type argument) so the handler does no operator dispatch;
// anything else takes InterpreterOps.Bitwise.
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V8Sharp.Common;
using BOF = V8Sharp.Interpreter.BinaryOperationFeedback;

namespace V8Sharp.Interpreter;

internal interface IInt32BitwiseOp
{
    static abstract Operation Operation { get; }

    /// <summary>The result of the operator on int32 operands (a number: >>> is unsigned).</summary>
    static abstract double Apply(int lhs, int rhs);
}

internal struct BitwiseAndOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.BitwiseAnd;
    public static double Apply(int lhs, int rhs) => lhs & rhs;
}

internal struct BitwiseOrOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.BitwiseOr;
    public static double Apply(int lhs, int rhs) => lhs | rhs;
}

internal struct BitwiseXorOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.BitwiseXor;
    public static double Apply(int lhs, int rhs) => lhs ^ rhs;
}

internal struct ShiftLeftOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.ShiftLeft;
    public static double Apply(int lhs, int rhs) => lhs << (rhs & 0x1F);
}

internal struct ShiftRightOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.ShiftRight;
    public static double Apply(int lhs, int rhs) => lhs >> (rhs & 0x1F);
}

internal struct ShiftRightLogicalOp : IInt32BitwiseOp
{
    public static Operation Operation => Operation.ShiftRightLogical;
    public static double Apply(int lhs, int rhs) => (uint)lhs >> (rhs & 0x1F);
}

internal static class InterpreterBitwise
{
    /// <summary>Whether <paramref name="i"/> is in the Smi range (31-bit Smis).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool InSmiRange(int i) => (uint)(i - JSValue.SmiMinValue) <= (uint)(JSValue.SmiMaxValue - JSValue.SmiMinValue);

    /// <summary>A bitwise operator with embedded feedback.</summary>
    public static JSValue Binary<TOp>(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) where TOp : struct, IInt32BitwiseOp
    {
        if (lhs._obj == NumberTag.Instance && rhs._obj == NumberTag.Instance)
        {
            double ld = lhs._num, rd = rhs._num;
            int l = (int)ld, r = (int)rd;
            if (l == ld && r == rd)
            {
                double result = TOp.Apply(l, r);
                // kSignedSmall when both inputs and the result are Smis (-0 is not).
                bool smi = InSmiRange(l) && InSmiRange(r) && result <= JSValue.SmiMaxValue && result >= JSValue.SmiMinValue &&
                           (l != 0 || !double.IsNegative(ld)) && (r != 0 || !double.IsNegative(rd));
                InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
                return JSValue.FromNumber(result);
            }
        }
        return InterpreterOps.Bitwise(isolate, TOp.Operation, lhs, rhs, ref feedback);
    }

    /// <summary>
    /// The number case of <see cref="WithSmi"/> for the dispatch loop: the
    /// result, or undefined (never a bitwise result) when the left operand is
    /// not a number that is an int32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSValue TryWithSmi<TOp>(JSValue lhs, int rhs, ref byte feedback) where TOp : struct, IInt32BitwiseOp
    {
        if (lhs._obj != NumberTag.Instance) return default;
        double ld = lhs._num;
        int l = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(ld)) : (int)ld;
        if (l != ld) return default;
        double result = TOp.Apply(l, rhs);
        bool smi = InSmiRange(l) && result <= JSValue.SmiMaxValue && result >= JSValue.SmiMinValue &&
                   (l != 0 || !double.IsNegative(ld));
        InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>The number case of <see cref="Binary"/> for the dispatch loop (undefined when it does not apply).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSValue TryBinary<TOp>(JSValue lhs, JSValue rhs, ref byte feedback) where TOp : struct, IInt32BitwiseOp
    {
        if (lhs._obj != NumberTag.Instance || rhs._obj != NumberTag.Instance) return default;
        double ld = lhs._num, rd = rhs._num;
        int l = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(ld)) : (int)ld;
        int r = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(rd)) : (int)rd;
        if (l != ld || r != rd) return default;
        double result = TOp.Apply(l, r);
        bool smi = InSmiRange(l) && InSmiRange(r) && result <= JSValue.SmiMaxValue && result >= JSValue.SmiMinValue &&
                   (l != 0 || !double.IsNegative(ld)) && (r != 0 || !double.IsNegative(rd));
        InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>
    /// The int32 operation of the bitwise bytecode <paramref name="op"/> (the
    /// register or the Smi form). The dispatch loop handles all of them with
    /// one inlined call site each for the register and the immediate forms:
    /// every inlined call site costs RyuJIT locals, and the loop's limit (512)
    /// decides which handlers get their helpers inlined.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Apply(Bytecode op, int l, int r) => op switch
    {
        Bytecode.BitwiseOr or Bytecode.BitwiseOrSmi => l | r,
        Bytecode.BitwiseXor or Bytecode.BitwiseXorSmi => l ^ r,
        Bytecode.BitwiseAnd or Bytecode.BitwiseAndSmi => l & r,
        Bytecode.ShiftLeft or Bytecode.ShiftLeftSmi => l << (r & 0x1F),
        Bytecode.ShiftRight or Bytecode.ShiftRightSmi => l >> (r & 0x1F),
        _ => (uint)l >> (r & 0x1F),
    };

    /// <summary>
    /// <see cref="TryBinary{TOp}"/> and <see cref="TryWithSmi{TOp}"/> for any
    /// bitwise bytecode <paramref name="op"/>: the number case, or undefined
    /// when it does not apply. <paramref name="rhsIsSmi"/>: the right operand
    /// is the bytecode's immediate (always a Smi).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSValue TryAny(Bytecode op, in JSValue lhs, double rd, bool rhsIsSmi, ref byte feedback)
    {
        if (lhs._obj != NumberTag.Instance) return default;
        double ld = lhs._num;
        int l = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(ld)) : (int)ld;
        int r = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(rd)) : (int)rd;
        bool integral = l == ld && r == rd;
        if (!integral)
        {
            // A fraction or an int32 overflow (asm.js `x | 0` of a sum):
            // TruncateTaggedToWord32 of a HeapNumber, Number feedback.
            l = TruncateToWord32(ld);
            r = TruncateToWord32(rd);
        }
        double result = Apply(op, l, r);
        bool smi = integral && InSmiRange(l) && (rhsIsSmi || InSmiRange(r)) && result <= JSValue.SmiMaxValue &&
                   result >= JSValue.SmiMinValue && (l != 0 || !double.IsNegative(ld)) &&
                   (rhsIsSmi || r != 0 || !double.IsNegative(rd));
        InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>
    /// <see cref="TryAny"/> for the Smi forms (BitwiseOrSmi .. ShiftRightLogicalSmi),
    /// whose right operand is the bytecode's immediate: the left operand
    /// converted once, the operator chosen by branches and computed on ints,
    /// and the Smi test for the feedback skipped when the feedback already
    /// covers Number (UpdateBinaryFeedback would leave it as it is).
    /// </summary>
    // TryAny took the immediate as a double and converted it back, chose the
    // operator through a jump table and tested the result's range in
    // doubles: a third of Octane zlib's dispatch loop samples were there.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSValue TryAnySmi(Bytecode op, in JSValue lhs, int r, ref byte feedback)
    {
        if (lhs._obj != NumberTag.Instance) return default;
        double ld = lhs._num;
        int l = Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(Vector128.CreateScalarUnsafe(ld)) : (int)ld;
        // -0 converts to 0 and compares equal; NaN and out-of-range values do
        // not. The other cases return from their own method, so no value of
        // this path lives across a call (RyuJIT would keep it in memory).
        if (l != ld) return TruncatingSmi(op, ld, r, ref feedback);
        bool minusZero = l == 0 && BitConverter.DoubleToInt64Bits(ld) < 0;
        long result = ApplySmi(op, l, r);
        if (!InterpreterOps.IsNumberFeedbackSaturated(feedback))
        {
            // kSignedSmall when the input and the result are Smis (-0 is not).
            bool smi = InSmiRange(l) && (ulong)(result - JSValue.SmiMinValue) <= (ulong)(JSValue.SmiMaxValue - JSValue.SmiMinValue) && !minusZero;
            InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
        }
        return JSValue.FromNumber(result);
    }

    /// <summary>The operator of a Smi-form bitwise bytecode on int32 operands, chosen by branches.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long ApplySmi(Bytecode op, int l, int r) =>
        op == Bytecode.BitwiseAndSmi ? l & r
        : op == Bytecode.BitwiseOrSmi ? l | r
        : op == Bytecode.ShiftRightSmi ? l >> (r & 0x1F)
        : op == Bytecode.ShiftLeftSmi ? l << (r & 0x1F)
        : op == Bytecode.BitwiseXorSmi ? l ^ r
        : (uint)l >> (r & 0x1F);

    /// <summary>
    /// <see cref="TryAnySmi"/> for a left operand that is not an int32 (a
    /// fraction or an int32 overflow, asm.js `x | 0` of a sum):
    /// TruncateTaggedToWord32 of a HeapNumber, Number feedback.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TruncatingSmi(Bytecode op, double ld, int r, ref byte feedback)
    {
        long result = ApplySmi(op, V8Sharp.Base.Numbers.Conversions.DoubleToInt32(ld), r);
        InterpreterOps.UpdateBinaryFeedback(ref feedback, BOF.TypeIndex.Number);
        return JSValue.FromNumber(result);
    }

    /// <summary>ToInt32 of a Number (DoubleToInt32), out of line for the dispatch loop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TruncateToWord32(double d) => V8Sharp.Base.Numbers.Conversions.DoubleToInt32(d);

    /// <summary>A bitwise operator with a Smi immediate right operand (BitwiseAndSmi ...).</summary>
    public static JSValue WithSmi<TOp>(Isolate isolate, JSValue lhs, int rhs, ref byte feedback) where TOp : struct, IInt32BitwiseOp
    {
        if (lhs._obj == NumberTag.Instance)
        {
            double ld = lhs._num;
            int l = (int)ld;
            if (l == ld)
            {
                double result = TOp.Apply(l, rhs);
                bool smi = InSmiRange(l) && result <= JSValue.SmiMaxValue && result >= JSValue.SmiMinValue &&
                           (l != 0 || !double.IsNegative(ld));
                InterpreterOps.UpdateBinaryFeedback(ref feedback, smi ? BOF.TypeIndex.SignedSmall : BOF.TypeIndex.Number);
                return JSValue.FromNumber(result);
            }
        }
        return InterpreterOps.Bitwise(isolate, TOp.Operation, lhs, JSValue.FromInt(rhs), ref feedback);
    }
}
