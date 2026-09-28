// The int32 fast paths of the bitwise bytecode handlers
// (BinaryOpAssembler::Generate_BitwiseBinaryOpWithFeedback: both operands
// Smis, or numbers that truncate without loss). One specialization per
// operator (a struct type argument) so the handler does no operator dispatch;
// anything else takes InterpreterOps.Bitwise.
using System.Runtime.CompilerServices;
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
