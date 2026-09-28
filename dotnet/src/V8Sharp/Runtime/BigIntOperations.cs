// Port of the BigInt operators of src/objects/bigint.cc (BigInt::Add,
// Subtract, Multiply, Divide, Remainder, Exponentiate, UnaryMinus, BitwiseNot,
// Increment, Decrement) and of src/builtins/bigint.tq (BigIntBitwiseAnd/Or/Xor,
// BigIntShiftLeft, BigIntShiftRight, BigIntUnsignedShiftRight), used by the
// interpreter's arithmetic and the BigInt runtime functions.
//
// TODO(merge): the digit arithmetic goes through BigInt's System.Numerics
// bridge (BigIntOps.cs). V8Sharp.Base.BigInts has the ported digit algorithms;
// switch to them together with the rest of BigIntOps. Results and errors are
// V8's (kMaxLengthBits limits, RangeError/TypeError messages).
using System.Numerics;
using V8Sharp.Common;

namespace V8Sharp.Runtime;

public static class BigIntOperations
{
    static BigInt Make(Isolate isolate, BigInteger value) => BigInt.FromBigInteger(isolate, value);

    static JSValue ThrowTooBig(Isolate isolate) => isolate.ThrowRangeError(MessageTemplate.BigIntTooBig);

    /// <summary>BigInt::UnaryMinus.</summary>
    public static BigInt UnaryMinus(Isolate isolate, BigInt x) =>
        x.IsZero ? x : new BigInt(!x.Sign, x.Digits);

    /// <summary>BigInt::BitwiseNot.</summary>
    public static BigInt BitwiseNot(Isolate isolate, BigInt x) => Make(isolate, -x.ToBigInteger() - 1);

    /// <summary>BigInt::Increment.</summary>
    public static BigInt Increment(Isolate isolate, BigInt x) => Make(isolate, x.ToBigInteger() + 1);

    /// <summary>BigInt::Decrement.</summary>
    public static BigInt Decrement(Isolate isolate, BigInt x) => Make(isolate, x.ToBigInteger() - 1);

    /// <summary>BigInt::Add.</summary>
    public static BigInt Add(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero) return y;
        if (y.IsZero) return x;
        return Make(isolate, x.ToBigInteger() + y.ToBigInteger());
    }

    /// <summary>BigInt::Subtract.</summary>
    public static BigInt Subtract(Isolate isolate, BigInt x, BigInt y)
    {
        if (y.IsZero) return x;
        if (x.IsZero) return UnaryMinus(isolate, y);
        return Make(isolate, x.ToBigInteger() - y.ToBigInteger());
    }

    /// <summary>BigInt::Multiply.</summary>
    public static BigInt Multiply(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero) return x;
        if (y.IsZero) return y;
        if ((long)x.Length + y.Length > BigInt.kMaxLength + 1) ThrowTooBig(isolate);
        return Make(isolate, x.ToBigInteger() * y.ToBigInteger());
    }

    /// <summary>BigInt::Divide.</summary>
    public static BigInt Divide(Isolate isolate, BigInt x, BigInt y)
    {
        // 1. If y is 0n, throw a RangeError exception.
        if (y.IsZero) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // 2. Let quotient be the mathematical value of x divided by y.
        // 3. Return a BigInt representing quotient rounded towards 0 to the next
        //    integral value.
        return Make(isolate, BigInteger.Divide(x.ToBigInteger(), y.ToBigInteger()));
    }

    /// <summary>BigInt::Remainder.</summary>
    public static BigInt Remainder(Isolate isolate, BigInt x, BigInt y)
    {
        // 1. If y is 0n, throw a RangeError exception.
        if (y.IsZero) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // 2. Return the BigInt representing x modulo y.
        return Make(isolate, BigInteger.Remainder(x.ToBigInteger(), y.ToBigInteger()));
    }

    /// <summary>BigInt::Exponentiate.</summary>
    public static BigInt Exponentiate(Isolate isolate, BigInt baseValue, BigInt exponent)
    {
        // 1. If exponent is < 0, throw a RangeError exception.
        if (exponent.Sign)
        {
            isolate.ThrowRangeError(MessageTemplate.MustBePositive, isolate.Factory.NewStringFromAsciiChecked("Exponent"));
        }
        // 2. If base is 0n and exponent is 0n, return 1n.
        if (exponent.IsZero) return BigInt.FromInt(isolate, 1);
        // 3. Return a BigInt representing the mathematical value of base raised
        //    to the power exponent.
        if (baseValue.IsZero) return baseValue;
        if (baseValue.Length == 1 && baseValue.Digits[0] == 1)
        {
            // (-1) ** even_number == 1.
            if (baseValue.Sign && (exponent.Digits[0] & 1) == 0) return UnaryMinus(isolate, baseValue);
            // (-1) ** odd_number == -1; 1 ** anything == 1.
            return baseValue;
        }
        // For all bases >= 2, very large exponents would lead to unrepresentable
        // results.
        if (exponent.Length > 1) ThrowTooBig(isolate);
        ulong expValue = exponent.Digits[0];
        if (expValue == 1) return baseValue;
        if (expValue >= BigInt.kMaxLengthBits) ThrowTooBig(isolate);
        int n = (int)expValue;
        // The result has about n * bitlength(base) bits.
        long bits = (long)n * (long)baseValue.ToBigInteger().GetBitLength();
        if (bits > (long)BigInt.kMaxLengthBits + 64) ThrowTooBig(isolate);
        return Make(isolate, BigInteger.Pow(baseValue.ToBigInteger(), n));
    }

    /// <summary>BigIntBitwiseAnd (bigint.tq).</summary>
    public static BigInt BitwiseAnd(Isolate isolate, BigInt x, BigInt y) => Make(isolate, x.ToBigInteger() & y.ToBigInteger());

    /// <summary>BigIntBitwiseOr (bigint.tq).</summary>
    public static BigInt BitwiseOr(Isolate isolate, BigInt x, BigInt y) => Make(isolate, x.ToBigInteger() | y.ToBigInteger());

    /// <summary>BigIntBitwiseXor (bigint.tq).</summary>
    public static BigInt BitwiseXor(Isolate isolate, BigInt x, BigInt y) => Make(isolate, x.ToBigInteger() ^ y.ToBigInteger());

    /// <summary>BigIntShiftLeft (bigint.tq): a negative shift is a right shift.</summary>
    public static BigInt ShiftLeft(Isolate isolate, BigInt x, BigInt y)
    {
        if (y.Sign) return RightShiftByAbsolute(isolate, x, y);
        return LeftShiftByAbsolute(isolate, x, y);
    }

    /// <summary>BigIntShiftRight (bigint.tq): a negative shift is a left shift.</summary>
    public static BigInt ShiftRight(Isolate isolate, BigInt x, BigInt y)
    {
        if (y.Sign) return LeftShiftByAbsolute(isolate, x, y);
        return RightShiftByAbsolute(isolate, x, y);
    }

    /// <summary>BigIntUnsignedShiftRight: BigInts have no unsigned right shift.</summary>
    public static BigInt UnsignedShiftRight(Isolate isolate, BigInt x, BigInt y)
    {
        isolate.ThrowTypeError(MessageTemplate.BigIntShr);
        return x;
    }

    static BigInt LeftShiftByAbsolute(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero || y.IsZero) return x;
        if (y.Length > 1 || y.Digits[0] > (ulong)BigInt.kMaxLengthBits) ThrowTooBig(isolate);
        int shift = (int)y.Digits[0];
        long resultBits = x.ToBigInteger().GetBitLength() + (long)shift;
        if (resultBits > BigInt.kMaxLengthBits) ThrowTooBig(isolate);
        return Make(isolate, x.ToBigInteger() << shift);
    }

    static BigInt RightShiftByAbsolute(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero || y.IsZero) return x;
        bool sign = x.Sign;
        if (y.Length > 1 || y.Digits[0] > (ulong)BigInt.kMaxLengthBits)
        {
            // Shifting by a huge amount: 0 for non-negative, -1 for negative.
            return sign ? BigInt.FromInt(isolate, -1) : BigInt.Zero;
        }
        int shift = (int)y.Digits[0];
        // BigInteger's >> rounds towards negative infinity, as the spec requires.
        return Make(isolate, x.ToBigInteger() >> shift);
    }

    /// <summary>Whether the BigInt fits in a signed 64-bit integer (BigInt64 feedback).</summary>
    public static bool FitsInInt64(BigInt x)
    {
        BigIntOps.AsInt64(x, out bool lossless);
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
