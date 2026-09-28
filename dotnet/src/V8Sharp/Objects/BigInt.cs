// Port of src/objects/bigint.h (representation). The digit arithmetic is
// V8Sharp.Base.BigInts (the port of src/bigint); this class holds sign and
// digits as V8's BigInt does (64-bit digits, little-endian, no leading zero
// digits, no -0n).
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

public sealed partial class BigInt : HeapObject
{
    // kMaxLength and kMaxBits for 64-bit targets (bigint.h).
    public const int kMaxBitsBits = 30;
    public const int kMaxLength = ((1 << kMaxBitsBits) - 1) / (sizeof(ulong) * 8);
    public const int kMaxBits = kMaxLength * sizeof(ulong) * 8;  // ~1 billion.
    public const int kDigitBits = 64;
    public const int kDigitSize = sizeof(ulong);

    ulong[] _digits;
    int _length;
    bool _sign;

    /// <summary>A BigInt over <paramref name="digits"/>, which must be normalized.</summary>
    public BigInt(bool sign, ulong[] digits) : base(InstanceType.BigIntType)
    {
        _digits = digits;
        _length = digits.Length;
        _sign = sign && digits.Length != 0;
        Debug.Assert(_length == 0 || _digits[_length - 1] != 0);
    }

    /// <summary>A MutableBigInt of <paramref name="length"/> zero digits (Factory::NewBigInt).</summary>
    BigInt(int length) : base(InstanceType.BigIntType)
    {
        _digits = length == 0 ? [] : new ulong[length];
        _length = length;
    }

    /// <summary>True for negative values. Zero is never negative.</summary>
    public bool Sign => _sign;

    /// <summary>The number of digits (V8's length()).</summary>
    public int Length => _length;

    /// <summary>Magnitude, least significant digit first, normalized (V8's digits()).</summary>
    public ReadOnlySpan<ulong> Digits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_digits, 0, _length);
    }

    /// <summary>V8's digit(n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Digit(int n) => _digits[n];

    public bool IsZero => _length == 0;

    public override string ToString() => (_sign ? "-" : "") + (_length == 0 ? "0n" : "0x" + string.Join("_", DigitsHex()) + "n");

    IEnumerable<string> DigitsHex()
    {
        for (int i = _length - 1; i >= 0; i--) yield return _digits[i].ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// V8's MutableBigInt: a BigInt under construction. Step-by-step
    /// construction happens here; the result passes through MakeImmutable and
    /// is not modified further afterwards.
    /// </summary>
    internal static class MutableBigInt
    {
        /// <summary>MutableBigInt::New: throws kBigIntTooBig beyond kMaxLength.</summary>
        public static BigInt New(Isolate isolate, int length)
        {
            if ((uint)length > kMaxLength) ThrowBigIntTooBig(isolate);
            return new BigInt(length);
        }

        /// <summary>MutableBigInt::New that cannot throw (the caller checked the length).</summary>
        public static BigInt NewUnchecked(int length) => new(length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<ulong> RwDigits(BigInt result) => new(result._digits, 0, result._length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSign(BigInt result, bool sign) => result._sign = sign;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetDigit(BigInt result, int n, ulong value) => result._digits[n] = value;

        /// <summary>MutableBigInt::MakeImmutable: canonicalizes and returns the BigInt.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BigInt MakeImmutable(BigInt result)
        {
            Canonicalize(result);
            return result;
        }

        /// <summary>MutableBigInt::MakeImmutable(result, length, top_digit).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BigInt MakeImmutable(BigInt result, int length, ulong topDigit)
        {
            Debug.Assert(length == result._length);
            if (topDigit == 0) CanonicalizeSlow(result, length);
            return result;
        }

        /// <summary>MutableBigInt::Canonicalize: right-trims leading zero digits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Canonicalize(BigInt result)
        {
            int oldLength = result._length;
            if (oldLength > 0 && result._digits[oldLength - 1] != 0) return;
            CanonicalizeSlow(result, oldLength);
        }

        /// <summary>MutableBigInt::CanonicalizeSlow.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CanonicalizeSlow(BigInt result, int oldLength)
        {
            int newLength = oldLength;
            if (newLength > 0)
            {
                Debug.Assert(result._digits[newLength - 1] == 0);
                newLength--;
                while (newLength > 0 && result._digits[newLength - 1] == 0) newLength--;
            }
            if (newLength != oldLength)
            {
                result._length = newLength;
                // Canonicalize -0n.
                if (newLength == 0) result._sign = false;
            }
        }
    }
}
