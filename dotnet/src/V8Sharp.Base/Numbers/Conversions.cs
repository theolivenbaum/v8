// Port of src/numbers/conversions.h, conversions-inl.h and conversions.cc:
// the string/number conversions that do not need heap objects.

using System.Runtime.CompilerServices;
using V8Sharp.Base.Strings;

namespace V8Sharp.Base.Numbers;

/// <summary>
/// Flags for string-to-number conversions. V8's current
/// <c>ConversionFlag</c> is { NO_CONVERSION_FLAG, ALLOW_NON_DECIMAL_PREFIX,
/// ALLOW_TRAILING_JUNK }; this keeps V8's older bit-flag spelling so callers
/// can ask for the prefixes separately. <see cref="AllowNonDecimalPrefix"/> is
/// V8's ALLOW_NON_DECIMAL_PREFIX.
/// </summary>
[Flags]
public enum ConversionFlag
{
    NoConversionFlag = 0,
    AllowHex = 1,
    AllowOctal = 2,
    /// <summary>Legacy "0777" octal. V8 no longer offers this in
    /// StringToDouble (see <see cref="Conversions.ImplicitOctalStringToDouble"/>);
    /// kept for callers that want the old behaviour.</summary>
    AllowImplicitOctal = 4,
    AllowBinary = 8,
    AllowTrailingJunk = 16,
    AllowNonDecimalPrefix = AllowHex | AllowOctal | AllowBinary,
}

public static partial class Conversions
{
    // uint64_t constants prefixed with kFP64 are bit patterns of doubles.
    // uint64_t constants prefixed with kFP16 are bit patterns of doubles encoding
    // limits of half-precision floating point values.
    public const int kFP64ExponentBits = 11;
    public const int kFP64MantissaBits = 52;
    public const ulong kFP64ExponentBias = 1023;
    public const ulong kFP64SignMask = 1UL << (kFP64ExponentBits + kFP64MantissaBits);
    public const ulong kFP64Infinity = 2047UL << kFP64MantissaBits;
    public const ulong kFP16InfinityAndNaNInfimum = (kFP64ExponentBias + 16) << kFP64MantissaBits;
    public const ulong kFP16MinExponent = kFP64ExponentBias - 14;
    public const ulong kFP16DenormalThreshold = kFP16MinExponent << kFP64MantissaBits;
    public const int kFP16MantissaBits = 10;
    public const ushort kFP16qNaN = 0x7e00;
    public const ushort kFP16Infinity = 0x7c00;
    // A value that, when added, has the effect that if any of the lower 41 bits of
    // the mantissa are set, the 11th mantissa bit from the front becomes set. Used
    // for rounding when converting from double to half-precision.
    public const ulong kFP64To16RoundingAddend = (1UL << ((kFP64MantissaBits - kFP16MantissaBits) - 1)) - 1;
    // A value that, when added, rebiases the exponent of a double to the range of
    // the half precision and performs rounding as described above in
    // kFP64To16RoundingAddend. Note that 15-kFP64ExponentBias overflows into the
    // sign bit, but that bit is implicitly cut off when assigning the 64-bit double
    // to a 16-bit output.
    public const ulong kFP64To16RebiasExponentAndRound =
        unchecked(((15UL - kFP64ExponentBias) << kFP64MantissaBits) + kFP64To16RoundingAddend);
    // A magic value that aligns 10 mantissa bits at the bottom of the double when
    // added to a double using floating point addition. Depends on floating point
    // addition being round-to-nearest-even.
    public const ulong kFP64To16DenormalMagic = (kFP16MinExponent + (kFP64MantissaBits - kFP16MantissaBits)) << kFP64MantissaBits;

    public const uint kFP32WithoutSignMask = 0x7fffffff;
    public const uint kFP32MinFP16ZeroRepresentable = 0x33000000;
    public const uint kFP32MaxFP16Representable = 0x47800000;
    public const uint kFP32SubnormalThresholdOfFP16 = 0x38800000;

    /// <summary>The limit for the fractionDigits/precision for toFixed,
    /// toPrecision and toExponential.</summary>
    public const int kMaxFractionDigits = 100;
    public const int kDoubleToFixedMaxDigitsBeforePoint = 21;
    // Leave room in the result for appending a minus and a period.
    public const int kDoubleToFixedMaxChars = kDoubleToFixedMaxDigitsBeforePoint + kMaxFractionDigits + 2;
    // Leave room in the result for appending a minus, for a period, up to 5 zeros
    // padding after the period and a zero in front of the period.
    public const int kDoubleToPrecisionMaxChars = kMaxFractionDigits + 8;
    // Leave room in the result for one digit before the period, a minus, a period,
    // the letter 'e', a minus or a plus depending on the exponent, and a three
    // digit exponent.
    public const int kDoubleToExponentialMaxChars = kMaxFractionDigits + 8;
    // The algorithm starts with the decimal point in the middle and writes to the
    // left for the integer part and to the right for the fractional part.
    // 1024 characters for the exponent and 52 for the mantissa either way, with
    // additional space for sign and decimal point.
    public const int kDoubleToRadixMaxChars = 2200;
    public const int kDoubleToStringMinBufferSize = 100;

    public const int kMaxInt = int.MaxValue;
    public const int kMinInt = int.MinValue;
    public const uint kMaxUInt32 = uint.MaxValue;
    public const double kMaxSafeInteger = 9007199254740991.0;  // 2^53-1
    public const double kMinSafeInteger = -9007199254740991.0;
    public const ulong kMaxSafeIntegerUint64 = 9007199254740991;
    /// <summary>Smi range with 31-bit Smis (pointer compression), see architecture.md.</summary>
    public const int kSmiMinValue = -(1 << 30);
    public const int kSmiMaxValue = (1 << 30) - 1;

    // ---- conversions.h / conversions-inl.h -----------------------------------

    /// <summary>If x is NaN, the result is INT_MIN. Otherwise the result is the
    /// argument x, clamped to [INT_MIN, INT_MAX] and then rounded to an integer.</summary>
    public static int FastD2IChecked(double x)
    {
        if (!(x >= int.MinValue)) return int.MinValue;  // Negation to catch NaNs.
        if (x > int.MaxValue) return int.MaxValue;
        return (int)x;
    }

    /// <summary>The result is undefined if x is infinite or NaN, or if the
    /// rounded integer value is outside the range of type int.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FastD2I(double x)
    {
        Debug.Assert(x <= int.MaxValue);
        Debug.Assert(x >= int.MinValue);
        return (int)x;
    }

    /// <summary>The fast double-to-unsigned-int conversion routine does not
    /// guarantee rounding towards zero, or any reasonable value if the argument
    /// is larger than what fits in an unsigned 32-bit integer.</summary>
    public static uint FastD2UI(double x)
    {
        // Convert "small enough" doubles to uint32_t by fixing the 32
        // least significant non-fractional bits in the low 32 bits of the
        // double, and reading them from there.
        const double k2Pow52 = 4503599627370496.0;
        bool negative = x < 0;
        if (negative) x = -x;
        if (x < k2Pow52)
        {
            x += k2Pow52;
            // Copy least significant 32 bits of mantissa.
            uint result = (uint)BitConverter.DoubleToUInt64Bits(x);
            return negative ? ~result + 1 : result;
        }
        // Large number (outside uint32 range), Infinity or NaN.
        return 0x80000000u;  // Return integer indefinite.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastI2D(int x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastUI2D(uint x) => x;

    /// <summary>Truncates x to IEEE 754-2019 binary16 using roundTiesToEven.
    /// Adopted from https://gist.github.com/rygorous/2156668</summary>
    public static ushort DoubleToFloat16(double value)
    {
        ulong @in = BitConverter.DoubleToUInt64Bits(value);
        ushort @out;

        // Take the absolute value of the input.
        ulong sign = @in & kFP64SignMask;
        @in ^= sign;

        if (@in >= kFP16InfinityAndNaNInfimum)
        {
            // Result is infinity or NaN.
            @out = (@in > kFP64Infinity) ? kFP16qNaN : kFP16Infinity;
        }
        else
        {
            // Result is a (de)normalized number or zero.
            if (@in < kFP16DenormalThreshold)
            {
                // Result is a denormal or zero. Use the magic value and FP addition to
                // align 10 mantissa bits at the bottom of the float. Depends on FP
                // addition being round-to-nearest-even.
                double temp = BitConverter.UInt64BitsToDouble(@in) + BitConverter.UInt64BitsToDouble(kFP64To16DenormalMagic);
                @out = (ushort)(BitConverter.DoubleToUInt64Bits(temp) - kFP64To16DenormalMagic);
            }
            else
            {
                // Result is not a denormal.
                // Remember if the result mantissa will be odd before rounding.
                ulong mantOdd = (@in >> (kFP64MantissaBits - kFP16MantissaBits)) & 1;

                // Update the exponent and round to nearest even.
                //
                // Rounding to nearest even is handled in two parts. First, adding
                // kFP64To16RebiasExponentAndRound has the effect of rebiasing the
                // exponent and that if any of the lower 41 bits of the mantissa are set,
                // the 11th mantissa bit from the front becomes set. Second, adding
                // mant_odd ensures ties are rounded to even.
                @in = unchecked(@in + kFP64To16RebiasExponentAndRound);
                @in += mantOdd;

                @out = (ushort)(@in >> (kFP64MantissaBits - kFP16MantissaBits));
            }
        }

        @out |= (ushort)(sign >> 48);
        return @out;
    }

    /// <summary>Matches the exact semantics of ECMA-262 20.2.2.17 (Math.fround).</summary>
    public static float DoubleToFloat32(double x)
    {
        if (x > float.MaxValue)
        {
            // kRoundingThreshold is the maximum double that rounds down to
            // the maximum representable float. Its mantissa bits are:
            // 1111111111111111111111101111111111111111111111111111
            // [<--- float range --->]
            // Note the zero-bit right after the float mantissa range, which
            // determines the rounding-down.
            const double kRoundingThreshold = 3.4028235677973362e+38;
            if (x <= kRoundingThreshold) return float.MaxValue;
            return float.PositiveInfinity;
        }
        if (x < float.MinValue)
        {
            // Same as above, mirrored to negative numbers.
            const double kRoundingThreshold = -3.4028235677973362e+38;
            if (x >= kRoundingThreshold) return float.MinValue;
            return float.NegativeInfinity;
        }
        return (float)x;
    }

    public static float DoubleToFloat32_NoInline(double x) => DoubleToFloat32(x);

    /// <summary>https://tc39.es/ecma262/#sec-tointegerorinfinity</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DoubleToInteger(double x)
    {
        // ToIntegerOrInfinity normalizes -0 to +0. Special case 0 for performance.
        if (double.IsNaN(x) || x == 0.0) return 0;
        if (!double.IsFinite(x)) return x;
        // Add 0.0 in the truncation case to ensure this doesn't return -0.
        return ((x > 0) ? Math.Floor(x) : Math.Ceiling(x)) + 0.0;
    }

    /// <summary>Implements most of https://tc39.es/ecma262/#sec-toint32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DoubleToInt32(double x)
    {
        if (double.IsFinite(x) && x <= int.MaxValue && x >= int.MinValue)
        {
            // All doubles within these limits are trivially convertable to an int.
            return (int)x;
        }
        return DoubleToInt32Slow(x);
    }

    static int DoubleToInt32Slow(double x)
    {
        Double d = new(x);
        int exponent = d.Exponent;
        ulong bits;
        if (exponent < 0)
        {
            if (exponent <= -Double.kSignificandSize) return 0;
            bits = d.Significand >> -exponent;
        }
        else
        {
            if (exponent > 31) return 0;
            // Masking to a 32-bit value ensures that the result of the
            // cast to long below is not the minimal long value,
            // which would overflow on multiplication with d.Sign.
            bits = (d.Significand << exponent) & 0xFFFFFFFFUL;
        }
        return unchecked((int)(d.Sign * (long)bits));
    }

    public static int DoubleToInt32_NoInline(double x) => DoubleToInt32(x);

    /// <summary>ECMA-262 9.6 (ToUint32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DoubleToUint32(double x) => unchecked((uint)DoubleToInt32(x));

    /// <summary>Implements https://heycam.github.io/webidl/#abstract-opdef-converttoint
    /// for the general case (step 1 and steps 8 to 12).</summary>
    public static long DoubleToWebIDLInt64(double x)
    {
        if (double.IsFinite(x) && x <= kMaxSafeInteger && x >= kMinSafeInteger)
        {
            // All doubles within these limits are trivially convertable to an int.
            return (long)x;
        }
        Double d = new(x);
        int exponent = d.Exponent;
        ulong bits;
        if (exponent < 0)
        {
            if (exponent <= -Double.kSignificandSize) return 0;
            bits = d.Significand >> -exponent;
        }
        else
        {
            if (exponent > 63) return 0;
            bits = d.Significand << exponent;
            long bitsInt64 = unchecked((long)bits);
            if (bitsInt64 == long.MinValue) return bitsInt64;
        }
        return unchecked(d.Sign * (long)bits);
    }

    public static ulong DoubleToWebIDLUint64(double x) => unchecked((ulong)DoubleToWebIDLInt64(x));

    /// <summary>64-bit counterpart of DoubleToInt32 (V8's DoubleToInt64 is
    /// declared in conversions.h; it has WebIDL semantics).</summary>
    public static long DoubleToInt64(double x) => DoubleToWebIDLInt64(x);

    public static ulong DoubleToUint64(double x) => DoubleToWebIDLUint64(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMinusZero(double value) =>
        BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits(-0.0);

    /// <summary>Returns true if value can be converted to a Smi (31-bit), and
    /// returns the resulting integer value.</summary>
    public static bool DoubleToSmiInteger(double value, out int smiIntValue)
    {
        if (!IsSmiDouble(value))
        {
            smiIntValue = 0;
            return false;
        }
        smiIntValue = FastD2I(value);
        return true;
    }

    public static bool IsSmiDouble(double value) =>
        value >= kSmiMinValue && value <= kSmiMaxValue && !IsMinusZero(value) && value == FastI2D(FastD2I(value));

    /// <summary>Integer32 is an integer that can be represented as a signed
    /// 32-bit integer, excluding -0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInt32Double(double value) =>
        value >= kMinInt && value <= kMaxInt && !IsMinusZero(value) && value == FastI2D(FastD2I(value));

    /// <summary>UInteger32 is an integer that can be represented as an
    /// unsigned 32-bit integer, excluding -0.</summary>
    public static bool IsUint32Double(double value) =>
        !IsMinusZero(value) && value >= 0 && value <= kMaxUInt32 && value == FastUI2D(FastD2UI(value));

    /// <summary>Tries to convert value to a uint32. If the output does not
    /// compare equal to the input, returns false and the value in
    /// uint32Value is left unspecified. Used for conversions such as in
    /// ECMA-262 15.4.2.2, which check "ToUint32(len) is equal to len".</summary>
    public static bool DoubleToUint32IfEqualToSelf(double value, out uint uint32Value)
    {
        const double k2Pow52 = 4503599627370496.0;
        const uint kValidTopBits = 0x43300000;
        const ulong kBottomBitMask = 0x0000_0000_FFFF_FFFF;

        // Add 2^52 to the double, to place valid uint32 values in the low-significant
        // bits of the exponent, by effectively setting the (implicit) top bit of the
        // significand. Note that this addition also normalises 0.0 and -0.0.
        double shiftedValue = value + k2Pow52;

        // At this point, a valid uint32 valued double will be represented as:
        //
        // sign = 0
        // exponent = 52
        // significand = 1. 00...00 <value>
        //       implicit^          ^^^^^^^ 32 bits
        //                  ^^^^^^^^^^^^^^^ 52 bits
        //
        // Therefore, we can first check the top 32 bits to make sure that the sign,
        // exponent and remaining significand bits are valid, and only then check the
        // value in the bottom 32 bits.
        ulong result = BitConverter.DoubleToUInt64Bits(shiftedValue);
        if ((result >> 32) == kValidTopBits)
        {
            uint32Value = (uint)(result & kBottomBitMask);
            return FastUI2D((uint)(result & kBottomBitMask)) == value;
        }
        uint32Value = 0;
        return false;
    }

    // The Number-object helpers of conversions-inl.h, over the number's value
    // (the caller unwraps the Smi or HeapNumber).

    public static int NumberToInt32(double number) => DoubleToInt32(number);
    public static uint NumberToUint32(double number) => DoubleToUint32(number);

    public static uint PositiveNumberToUint32(double value)
    {
        // Catch all values smaller than 1 and use the double-negation trick for NANs.
        if (!(value >= 1)) return 0;
        const uint max = uint.MaxValue;
        if (value < max) return (uint)value;
        return max;
    }

    public static long NumberToInt64(double d)
    {
        if (double.IsNaN(d)) return 0;
        if (d >= long.MaxValue) return long.MaxValue;
        if (d <= long.MinValue) return long.MinValue;
        return (long)d;
    }

    public static ulong PositiveNumberToUint64(double value)
    {
        // Catch all values smaller than 1 and use the double-negation trick for NANs.
        if (!(value >= 1)) return 0;
        const ulong max = ulong.MaxValue;
        if (value < max) return (ulong)value;
        return max;
    }

    public static bool TryNumberToSize(double value, out nuint result)
    {
        // If value is compared directly to the limit, the limit will be
        // casted to a double and could end up as limit + 1,
        // because a double might not have enough mantissa bits for it.
        // So we might as well cast the limit first, and use < instead of <=.
        double maxSize = nuint.MaxValue;
        if (value >= 0 && value < maxSize)
        {
            result = (nuint)value;
            return true;
        }
        result = 0;
        return false;
    }

    public static nuint NumberToSize(double number)
    {
        if (!TryNumberToSize(number, out nuint result)) throw new InvalidOperationException("NumberToSize");
        return result;
    }
}
