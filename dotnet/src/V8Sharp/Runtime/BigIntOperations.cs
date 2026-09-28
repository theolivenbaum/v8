// The BigInt operators the interpreter and the BigInt runtime functions call.
// They forward to the port of src/objects/bigint.cc (Objects/BigIntOps.cs),
// which runs on V8Sharp.Base.BigInts (V8's digit algorithms).
using V8Sharp.Common;

namespace V8Sharp.Runtime;

public static class BigIntOperations
{
    /// <summary>BigInt::UnaryMinus.</summary>
    public static BigInt UnaryMinus(Isolate isolate, BigInt x) => BigInt.UnaryMinus(isolate, x);

    /// <summary>BigInt::BitwiseNot.</summary>
    public static BigInt BitwiseNot(Isolate isolate, BigInt x) => BigInt.BitwiseNot(isolate, x);

    /// <summary>BigInt::Increment.</summary>
    public static BigInt Increment(Isolate isolate, BigInt x) => BigInt.Increment(isolate, x);

    /// <summary>BigInt::Decrement.</summary>
    public static BigInt Decrement(Isolate isolate, BigInt x) => BigInt.Decrement(isolate, x);

    /// <summary>BigInt::Add.</summary>
    public static BigInt Add(Isolate isolate, BigInt x, BigInt y) => BigInt.Add(isolate, x, y);

    /// <summary>BigInt::Subtract.</summary>
    public static BigInt Subtract(Isolate isolate, BigInt x, BigInt y) => BigInt.Subtract(isolate, x, y);

    /// <summary>BigInt::Multiply.</summary>
    public static BigInt Multiply(Isolate isolate, BigInt x, BigInt y) => BigInt.Multiply(isolate, x, y);

    /// <summary>BigInt::Divide.</summary>
    public static BigInt Divide(Isolate isolate, BigInt x, BigInt y) => BigInt.Divide(isolate, x, y);

    /// <summary>BigInt::Remainder.</summary>
    public static BigInt Remainder(Isolate isolate, BigInt x, BigInt y) => BigInt.Remainder(isolate, x, y);

    /// <summary>BigInt::Exponentiate.</summary>
    public static BigInt Exponentiate(Isolate isolate, BigInt baseValue, BigInt exponent) =>
        BigInt.Exponentiate(isolate, baseValue, exponent);

    /// <summary>BigIntBitwiseAnd (bigint.tq).</summary>
    public static BigInt BitwiseAnd(Isolate isolate, BigInt x, BigInt y) => BigInt.BitwiseAnd(isolate, x, y);

    /// <summary>BigIntBitwiseOr (bigint.tq).</summary>
    public static BigInt BitwiseOr(Isolate isolate, BigInt x, BigInt y) => BigInt.BitwiseOr(isolate, x, y);

    /// <summary>BigIntBitwiseXor (bigint.tq).</summary>
    public static BigInt BitwiseXor(Isolate isolate, BigInt x, BigInt y) => BigInt.BitwiseXor(isolate, x, y);

    /// <summary>BigIntShiftLeft (bigint.tq).</summary>
    public static BigInt ShiftLeft(Isolate isolate, BigInt x, BigInt y) => BigInt.LeftShift(isolate, x, y);

    /// <summary>BigIntShiftRight (bigint.tq).</summary>
    public static BigInt ShiftRight(Isolate isolate, BigInt x, BigInt y) => BigInt.SignedRightShift(isolate, x, y);

    /// <summary>BigIntUnsignedShiftRight: always a TypeError.</summary>
    public static BigInt UnsignedShiftRight(Isolate isolate, BigInt x, BigInt y) => BigInt.UnsignedRightShift(isolate, x, y);

    /// <summary>Whether the BigInt fits in a signed 64-bit integer (BigInt64 feedback).</summary>
    public static bool FitsInInt64(BigInt x)
    {
        BigInt.AsInt64(x, out bool lossless);
        return lossless;
    }

    /// <summary>The BigInt binary operation for <paramref name="op"/>.</summary>
    public static BigInt BinaryOp(Isolate isolate, Operation op, BigInt x, BigInt y) => op switch
    {
        Operation.Add => Add(isolate, x, y),
        Operation.Subtract => Subtract(isolate, x, y),
        Operation.Multiply => Multiply(isolate, x, y),
        Operation.Divide => Divide(isolate, x, y),
        Operation.Modulus => Remainder(isolate, x, y),
        Operation.Exponentiate => Exponentiate(isolate, x, y),
        Operation.BitwiseAnd => BitwiseAnd(isolate, x, y),
        Operation.BitwiseOr => BitwiseOr(isolate, x, y),
        Operation.BitwiseXor => BitwiseXor(isolate, x, y),
        Operation.ShiftLeft => ShiftLeft(isolate, x, y),
        Operation.ShiftRight => ShiftRight(isolate, x, y),
        Operation.ShiftRightLogical => UnsignedShiftRight(isolate, x, y),
        _ => throw new UnreachableException(),
    };
}
