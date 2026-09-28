// Port of the heap-level operations in src/objects/bigint.cc: comparison,
// equality, hashing, conversions from and to strings, numbers and 64-bit
// integers.
//
// TODO(merge): V8Sharp.Base.BigInts. The digit algorithms (src/bigint) are
// being ported into V8Sharp.Base; until that lands, the arithmetic here goes
// through System.Numerics.BigInteger. Results are identical, only the
// algorithms differ. Replace the BigInteger bridge with the ported digit ops.
using System.Numerics;

namespace V8Sharp.Objects;

public sealed partial class BigInt
{
    private static readonly BigInt s_zero = new(false, []);

    public static BigInt Zero => s_zero;

    // ---------------------------------------------------------------------
    // BigInteger bridge (TODO(merge): V8Sharp.Base.BigInts).

    internal BigInteger ToBigInteger()
    {
        if (IsZero) return BigInteger.Zero;
        Span<byte> bytes = Digits.Length <= 32 ? stackalloc byte[Digits.Length * 8] : new byte[Digits.Length * 8];
        for (int i = 0; i < Digits.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[(i * 8)..], Digits[i]);
        }
        var magnitude = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        return Sign ? -magnitude : magnitude;
    }

    internal static BigInt FromBigInteger(Isolate isolate, BigInteger value)
    {
        if (value.IsZero) return s_zero;
        bool sign = value.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(value);
        byte[] bytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: false);
        int length = (bytes.Length + 7) / 8;
        if (length > kMaxLength)
        {
            isolate.ThrowRangeError(MessageTemplate.BigIntTooBig);
        }
        var digits = new ulong[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            digits[i / 8] |= (ulong)bytes[i] << (8 * (i % 8));
        }
        return new BigInt(sign, digits);
    }

    // ---------------------------------------------------------------------
    // Construction.

    /// <summary>MutableBigInt::NewFromInt.</summary>
    public static BigInt FromInt(Isolate isolate, int value)
    {
        if (value == 0) return s_zero;
        return new BigInt(value < 0, [(ulong)Math.Abs((long)value)]);
    }

    /// <summary>BigInt::FromInt64.</summary>
    public static BigInt FromInt64(Isolate isolate, long n)
    {
        if (n == 0) return s_zero;
        ulong magnitude = n > 0 ? (ulong)n : n == long.MinValue ? 1UL << 63 : (ulong)(-n);
        return new BigInt(n < 0, [magnitude]);
    }

    /// <summary>BigInt::FromUint64.</summary>
    public static BigInt FromUint64(Isolate isolate, ulong n)
    {
        if (n == 0) return s_zero;
        return new BigInt(false, [n]);
    }

    /// <summary>
    /// BigInt::FromNumber: ES #sec-numbertobigint. Throws a RangeError for
    /// non-integral numbers.
    /// </summary>
    public static BigInt FromNumber(Isolate isolate, JSValue number)
    {
        Debug.Assert(number.IsNumber);
        double value = number.Number;
        if (!double.IsFinite(value) || Math.Truncate(value) != value)
        {
            isolate.ThrowRangeError(MessageTemplate.BigIntFromNumber, number);
        }
        return FromDouble(isolate, value);
    }

    private static BigInt FromDouble(Isolate isolate, double value)
    {
        if (value == 0) return s_zero;
        return FromBigInteger(isolate, new BigInteger(value));
    }

    /// <summary>BigInt::FromObject: ES #sec-tobigint.</summary>
    public static BigInt FromObject(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is JSReceiver receiver)
        {
            obj = JSReceiver.ToPrimitive(isolate, receiver, ToPrimitiveHint.Number);
        }

        if (obj.IsBoolean)
        {
            return FromInt(isolate, obj.BooleanValue ? 1 : 0);
        }
        if (obj.HeapObjectOrNull is BigInt bigint)
        {
            return bigint;
        }
        if (obj.HeapObjectOrNull is JSString str)
        {
            BigInt? n = StringToBigInt(isolate, str);
            if (n is null)
            {
                const int kMaxRenderedLength = 1000;
                if (str.Length > kMaxRenderedLength)
                {
                    JSString prefix = isolate.Factory.NewProperSubString(str, 0, kMaxRenderedLength);
                    str = isolate.Factory.NewConsString(prefix, isolate.Factory.NewStringFromUtf16("…"));
                }
                isolate.ThrowSyntaxError(MessageTemplate.BigIntFromObject, str);
            }
            return n;
        }

        isolate.ThrowTypeError(MessageTemplate.BigIntFromObject, obj);
        return null!;
    }

    /// <summary>
    /// StringToBigInt (src/numbers/conversions.cc): ES #sec-stringtobigint.
    /// Returns null where V8 returns an empty handle without an exception
    /// (the string is not a StringIntegerLiteral).
    /// </summary>
    public static BigInt? StringToBigInt(Isolate isolate, JSString str)
    {
        ReadOnlySpan<char> s = str.FlatSpan();
        int start = 0, end = s.Length;
        while (start < end && V8Sharp.Base.Strings.CharPredicates.IsWhiteSpaceOrLineTerminator(s[start])) start++;
        while (end > start && V8Sharp.Base.Strings.CharPredicates.IsWhiteSpaceOrLineTerminator(s[end - 1])) end--;
        s = s[start..end];
        if (s.IsEmpty) return s_zero;

        int radix = 10;
        bool negative = false;
        if (s.Length >= 2 && s[0] == '0' && (s[1] | 0x20) is 'x' or 'o' or 'b')
        {
            radix = (s[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
            s = s[2..];
            if (s.IsEmpty) return null;
        }
        else if (s[0] is '+' or '-')
        {
            negative = s[0] == '-';
            s = s[1..];
            if (s.IsEmpty) return null;
        }

        BigInteger result = BigInteger.Zero;
        foreach (char c in s)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 99,
            };
            if (d >= radix) return null;
            result = result * radix + d;
        }
        return FromBigInteger(isolate, negative ? -result : result);
    }

    // ---------------------------------------------------------------------
    // Conversions out.

    /// <summary>BigInt::AsInt64: the value modulo 2^64 as a signed integer.</summary>
    public static long AsInt64(BigInt x, out bool lossless)
    {
        ulong raw = AsUint64(x, out _);
        long result = unchecked((long)raw);
        lossless = x.Length <= 1 && (result < 0) == x.Sign;
        if (x.IsZero) lossless = true;
        return result;
    }

    /// <summary>BigInt::AsUint64: the value modulo 2^64.</summary>
    public static ulong AsUint64(BigInt x, out bool lossless)
    {
        if (x.IsZero)
        {
            lossless = true;
            return 0;
        }
        ulong raw = x.Digits[0];
        lossless = x.Length == 1 && !x.Sign;
        return x.Sign ? unchecked(0 - raw) : raw;
    }

    /// <summary>BigInt::ToNumber.</summary>
    public static JSValue ToNumber(Isolate isolate, BigInt x)
    {
        if (x.IsZero) return JSValue.Zero;
        return JSValue.FromNumber((double)x.ToBigInteger());
    }

    /// <summary>BigInt::ToString.</summary>
    public static JSString ToString(Isolate isolate, BigInt bigint, int radix = 10)
    {
        if (bigint.IsZero) return ReadOnlyRoots.zero_string;
        return isolate.Factory.NewStringFromAsciiChecked(FormatRadix(bigint.ToBigInteger(), radix));
    }

    /// <summary>BigInt::NoSideEffectsToString.</summary>
    public static JSString NoSideEffectsToString(Isolate isolate, BigInt bigint)
    {
        if (bigint.IsZero) return ReadOnlyRoots.zero_string;
        // The threshold is chosen such that the operation will be fast enough to
        // not need interrupt checks. This function is meant for producing human-
        // readable error messages, so super-long results aren't useful anyway.
        if (bigint.Length > 100)
        {
            return isolate.Factory.NewStringFromAsciiChecked("<a very large BigInt>");
        }
        return isolate.Factory.NewStringFromAsciiChecked(FormatRadix(bigint.ToBigInteger(), 10));
    }

    private static string FormatRadix(BigInteger value, int radix)
    {
        if (radix == 10) return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool negative = value.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(value);
        var chars = new System.Text.StringBuilder();
        while (!magnitude.IsZero)
        {
            int d = (int)(magnitude % radix);
            chars.Insert(0, (char)(d < 10 ? '0' + d : 'a' + d - 10));
            magnitude /= radix;
        }
        if (negative) chars.Insert(0, '-');
        return chars.ToString();
    }

    // ---------------------------------------------------------------------
    // Comparison.

    private static ComparisonResult UnequalSign(bool leftNegative) =>
        leftNegative ? ComparisonResult.LessThan : ComparisonResult.GreaterThan;

    private static ComparisonResult AbsoluteGreater(bool bothNegative) =>
        bothNegative ? ComparisonResult.LessThan : ComparisonResult.GreaterThan;

    private static ComparisonResult AbsoluteLess(bool bothNegative) =>
        bothNegative ? ComparisonResult.GreaterThan : ComparisonResult.LessThan;

    /// <summary>BigInt::CompareToBigInt.</summary>
    public static ComparisonResult CompareToBigInt(BigInt x, BigInt y)
    {
        bool xSign = x.Sign;
        if (xSign != y.Sign) return UnequalSign(xSign);
        int result = CompareDigits(x.Digits, y.Digits);
        if (result > 0) return AbsoluteGreater(xSign);
        if (result < 0) return AbsoluteLess(xSign);
        return ComparisonResult.Equal;
    }

    private static int CompareDigits(ulong[] a, ulong[] b)
    {
        if (a.Length != b.Length) return a.Length > b.Length ? 1 : -1;
        for (int i = a.Length - 1; i >= 0; i--)
        {
            if (a[i] != b[i]) return a[i] > b[i] ? 1 : -1;
        }
        return 0;
    }

    /// <summary>BigInt::EqualToBigInt.</summary>
    public static bool EqualToBigInt(BigInt x, BigInt y)
    {
        if (x.Sign != y.Sign) return false;
        return CompareDigits(x.Digits, y.Digits) == 0;
    }

    /// <summary>BigInt::CompareToString. Throws only through StringToBigInt.</summary>
    public static ComparisonResult CompareToString(Isolate isolate, BigInt x, JSString y)
    {
        // a. Let ny be StringToBigInt(y);
        BigInt? ny = StringToBigInt(isolate, y);
        // b. If ny is NaN, return undefined.
        if (ny is null) return ComparisonResult.Undefined;
        // c. Return BigInt::lessThan(x, ny).
        return CompareToBigInt(x, ny);
    }

    /// <summary>BigInt::EqualToString.</summary>
    public static bool EqualToString(Isolate isolate, BigInt x, JSString y)
    {
        // a. Let n be StringToBigInt(y).
        BigInt? n = StringToBigInt(isolate, y);
        // b. If n is NaN, return false.
        if (n is null) return false;
        // c. Return the result of x == n.
        return EqualToBigInt(x, n);
    }

    /// <summary>BigInt::EqualToNumber.</summary>
    public static bool EqualToNumber(BigInt x, JSValue y)
    {
        Debug.Assert(y.IsNumber);
        // a. If x or y are any of NaN, +inf, or -inf, return false.
        // b. If the mathematical value of x is equal to the mathematical value of y,
        //    return true, otherwise return false.
        return CompareToDouble(x, y.Number) == ComparisonResult.Equal;
    }

    /// <summary>BigInt::CompareToNumber.</summary>
    public static ComparisonResult CompareToNumber(BigInt x, JSValue y)
    {
        Debug.Assert(y.IsNumber);
        return CompareToDouble(x, y.Number);
    }

    /// <summary>BigInt::CompareToDouble.</summary>
    public static ComparisonResult CompareToDouble(BigInt x, double y)
    {
        if (double.IsNaN(y)) return ComparisonResult.Undefined;
        if (y == double.PositiveInfinity) return ComparisonResult.LessThan;
        if (y == double.NegativeInfinity) return ComparisonResult.GreaterThan;
        bool xSign = x.Sign;
        // Note that this is different from the double's sign bit for -0. That's
        // intentional because -0 must be treated like 0.
        bool ySign = y < 0;
        if (xSign != ySign) return UnequalSign(xSign);
        if (y == 0)
        {
            return x.IsZero ? ComparisonResult.Equal : ComparisonResult.GreaterThan;
        }
        if (x.IsZero) return ComparisonResult.LessThan;
        // Exact comparison: split y into integral and fractional parts.
        double integral = Math.Truncate(y);
        int cmp = x.ToBigInteger().CompareTo(new BigInteger(integral));
        if (cmp != 0) return cmp > 0 ? ComparisonResult.GreaterThan : ComparisonResult.LessThan;
        if (integral == y) return ComparisonResult.Equal;
        // |x| == |trunc(y)| < |y|.
        return AbsoluteLess(xSign);
    }

    /// <summary>BigInt::Hash (bigint.h).</summary>
    public static uint Hash(BigInt bigint) =>
        Hashing.Hash32((uint)bigint.Length | (bigint.Sign ? 1u << 30 : 0u)) ^
        (uint)Hashing.Hash64(bigint.IsZero ? 0 : bigint.Digits[0]);
}
