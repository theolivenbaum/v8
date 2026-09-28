// Port of src/base/numbers/strtod.h and strtod.cc.

namespace V8Sharp.Base.Numbers;

public static partial class DoubleConversion
{
    // 2^53 = 9007199254740992.
    // Any integer with at most 15 decimal digits will hence fit into a double
    // (which has a 53bit significand) without loss of precision.
    const int kMaxExactDoubleIntegerDecimalDigits = 15;
    // 2^64 = 18446744073709551616 > 10^19
    const int kMaxUint64DecimalDigits = 19;

    // Max double: 1.7976931348623157 x 10^308
    // Min non-zero double: 4.9406564584124654 x 10^-324
    // Any x >= 10^309 is interpreted as +infinity.
    // Any x <= 10^-324 is interpreted as 0.
    // Note that 2.5e-324 (despite being smaller than the min double) will be read
    // as non-zero (equal to the min non-zero double).
    const int kMaxDecimalPower = 309;
    const int kMinDecimalPower = -324;

    const ulong kMaxUint64 = 0xFFFF_FFFF_FFFF_FFFF;

    static ReadOnlySpan<double> ExactPowersOfTen => [
        1.0,  // 10^0
        10.0,
        100.0,
        1000.0,
        10000.0,
        100000.0,
        1000000.0,
        10000000.0,
        100000000.0,
        1000000000.0,
        10000000000.0,  // 10^10
        100000000000.0,
        1000000000000.0,
        10000000000000.0,
        100000000000000.0,
        1000000000000000.0,
        10000000000000000.0,
        100000000000000000.0,
        1000000000000000000.0,
        10000000000000000000.0,
        100000000000000000000.0,  // 10^20
        1000000000000000000000.0,
        // 10^22 = 0x21E19E0C9BAB2400000 = 0x878678326EAC9 * 2^22
        10000000000000000000000.0,
    ];

    const int kExactPowersOfTenSize = 23;

    // Maximum number of significant digits in the decimal representation.
    // In fact the value is 772 (see conversions.cc), but to give us some margin
    // we round up to 780.
    public const int kMaxSignificantDecimalDigits = 780;

    static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '0') return buffer[i..];
        }
        return [];
    }

    static ReadOnlySpan<char> TrimTrailingZeros(ReadOnlySpan<char> buffer)
    {
        for (int i = buffer.Length - 1; i >= 0; --i)
        {
            if (buffer[i] != '0') return buffer[..(i + 1)];
        }
        return [];
    }

    static void TrimToMaxSignificantDigits(ReadOnlySpan<char> buffer, int exponent, Span<char> significantBuffer,
                                           out int significantExponent)
    {
        for (int i = 0; i < kMaxSignificantDecimalDigits - 1; ++i) significantBuffer[i] = buffer[i];
        // The input buffer has been trimmed. Therefore the last digit must be
        // different from '0'.
        Debug.Assert(buffer[^1] != '0');
        // Set the last digit to be non-zero. This is sufficient to guarantee
        // correct rounding.
        significantBuffer[kMaxSignificantDecimalDigits - 1] = '1';
        significantExponent = exponent + (buffer.Length - kMaxSignificantDecimalDigits);
    }

    // Reads digits from the buffer and converts them to a uint64.
    // Reads in as many digits as fit into a uint64.
    // When the string starts with "1844674407370955161" no further digit is read.
    // Since 2^64 = 18446744073709551616 it would still be possible read another
    // digit if it was less or equal than 6, but this would complicate the code.
    static ulong ReadUint64(ReadOnlySpan<char> buffer, out int numberOfReadDigits)
    {
        ulong result = 0;
        int i = 0;
        while (i < buffer.Length && result <= (kMaxUint64 / 10 - 1))
        {
            int d = buffer[i++] - '0';
            Debug.Assert(0 <= d && d <= 9);
            result = 10 * result + (ulong)d;
        }
        numberOfReadDigits = i;
        return result;
    }

    // Reads a DiyFp from the buffer.
    // The returned DiyFp is not necessarily normalized.
    // If remaining_decimals is zero then the returned DiyFp is accurate.
    // Otherwise it has been rounded and has error of at most 1/2 ulp.
    static void ReadDiyFp(ReadOnlySpan<char> buffer, out DiyFp result, out int remainingDecimals)
    {
        ulong significand = ReadUint64(buffer, out int readDigits);
        if (buffer.Length == readDigits)
        {
            result = new DiyFp(significand, 0);
            remainingDecimals = 0;
        }
        else
        {
            // Round the significand.
            if (buffer[readDigits] >= '5') significand++;
            // Compute the binary exponent.
            int exponent = 0;
            result = new DiyFp(significand, exponent);
            remainingDecimals = buffer.Length - readDigits;
        }
    }

    static bool DoubleStrtod(ReadOnlySpan<char> trimmed, int exponent, out double result)
    {
        result = 0;
        if (trimmed.Length <= kMaxExactDoubleIntegerDecimalDigits)
        {
            // The trimmed input fits into a double.
            // If the 10^exponent (resp. 10^-exponent) fits into a double too then we
            // can compute the result-double simply by multiplying (resp. dividing) the
            // two numbers.
            // This is possible because IEEE guarantees that floating-point operations
            // return the best possible approximation.
            if (exponent < 0 && -exponent < kExactPowersOfTenSize)
            {
                // 10^-exponent fits into a double.
                result = ReadUint64(trimmed, out _);
                result /= ExactPowersOfTen[-exponent];
                return true;
            }
            if (0 <= exponent && exponent < kExactPowersOfTenSize)
            {
                // 10^exponent fits into a double.
                result = ReadUint64(trimmed, out _);
                result *= ExactPowersOfTen[exponent];
                return true;
            }
            int remainingDigits = kMaxExactDoubleIntegerDecimalDigits - trimmed.Length;
            if ((0 <= exponent) && (exponent - remainingDigits < kExactPowersOfTenSize))
            {
                // The trimmed string was short and we can multiply it with
                // 10^remaining_digits. As a result the remaining exponent now fits
                // into a double too.
                result = ReadUint64(trimmed, out _);
                result *= ExactPowersOfTen[remainingDigits];
                result *= ExactPowersOfTen[exponent - remainingDigits];
                return true;
            }
        }
        return false;
    }

    // Returns 10^exponent as an exact DiyFp.
    // The given exponent must be in the range [1; kDecimalExponentDistance[.
    static DiyFp AdjustmentPowerOfTen(int exponent) => exponent switch
    {
        1 => new DiyFp(0xA000_0000_0000_0000, -60),
        2 => new DiyFp(0xC800_0000_0000_0000, -57),
        3 => new DiyFp(0xFA00_0000_0000_0000, -54),
        4 => new DiyFp(0x9C40_0000_0000_0000, -50),
        5 => new DiyFp(0xC350_0000_0000_0000, -47),
        6 => new DiyFp(0xF424_0000_0000_0000, -44),
        7 => new DiyFp(0x9896_8000_0000_0000, -40),
        _ => throw new UnreachableException(),
    };

    // If the function returns true then the result is the correct double.
    // Otherwise it is either the correct double or the double that is just below
    // the correct double.
    static bool DiyFpStrtod(ReadOnlySpan<char> buffer, int exponent, out double result)
    {
        ReadDiyFp(buffer, out DiyFp input, out int remainingDecimals);
        // Since we may have dropped some digits the input is not accurate.
        // If remaining_decimals is different than 0 than the error is at most
        // .5 ulp (unit in the last place).
        // We don't want to deal with fractions and therefore keep a common
        // denominator.
        const int kDenominatorLog = 3;
        const int kDenominator = 1 << kDenominatorLog;
        // Move the remaining decimals into the exponent.
        exponent += remainingDecimals;
        long error = remainingDecimals == 0 ? 0 : kDenominator / 2;

        int oldE = input.E;
        input.Normalize();
        error <<= oldE - input.E;

        Debug.Assert(exponent <= PowersOfTenCache.kMaxDecimalExponent);
        if (exponent < PowersOfTenCache.kMinDecimalExponent)
        {
            result = 0.0;
            return true;
        }
        PowersOfTenCache.GetCachedPowerForDecimalExponent(exponent, out DiyFp cachedPower, out int cachedDecimalExponent);

        if (cachedDecimalExponent != exponent)
        {
            int adjustmentExponent = exponent - cachedDecimalExponent;
            DiyFp adjustmentPower = AdjustmentPowerOfTen(adjustmentExponent);
            input.Multiply(adjustmentPower);
            if (kMaxUint64DecimalDigits - buffer.Length >= adjustmentExponent)
            {
                // The product of input with the adjustment power fits into a 64 bit
                // integer.
            }
            else
            {
                // The adjustment power is exact. There is hence only an error of 0.5.
                error += kDenominator / 2;
            }
        }

        input.Multiply(cachedPower);
        // The error introduced by a multiplication of a*b equals
        //   error_a + error_b + error_a*error_b/2^64 + 0.5
        // Substituting a with 'input' and b with 'cached_power' we have
        //   error_b = 0.5  (all cached powers have an error of less than 0.5 ulp),
        //   error_ab = 0 or 1 / kDenominator > error_a*error_b/ 2^64
        int errorB = kDenominator / 2;
        int errorAb = error == 0 ? 0 : 1;  // We round up to 1.
        int fixedError = kDenominator / 2;
        error += errorB + errorAb + fixedError;

        oldE = input.E;
        input.Normalize();
        error <<= oldE - input.E;

        // See if the double's significand changes if we add/subtract the error.
        int orderOfMagnitude = DiyFp.kSignificandSize + input.E;
        int effectiveSignificandSize = Double.SignificandSizeForOrderOfMagnitude(orderOfMagnitude);
        int precisionDigitsCount = DiyFp.kSignificandSize - effectiveSignificandSize;
        if (precisionDigitsCount + kDenominatorLog >= DiyFp.kSignificandSize)
        {
            // This can only happen for very small denormals. In this case the
            // half-way multiplied by the denominator exceeds the range of an uint64.
            // Simply shift everything to the right.
            int shiftAmount = (precisionDigitsCount + kDenominatorLog) - DiyFp.kSignificandSize + 1;
            input.F >>= shiftAmount;
            input.E += shiftAmount;
            // We add 1 for the lost precision of error, and kDenominator for
            // the lost precision of input.f().
            error = (error >> shiftAmount) + 1 + kDenominator;
            precisionDigitsCount -= shiftAmount;
        }
        // We use uint64_ts now. This only works if the DiyFp uses uint64_ts too.
        Debug.Assert(precisionDigitsCount < 64);
        const ulong one64 = 1;
        ulong precisionBitsMask = (one64 << precisionDigitsCount) - 1;
        ulong precisionBits = input.F & precisionBitsMask;
        ulong halfWay = one64 << (precisionDigitsCount - 1);
        precisionBits *= kDenominator;
        halfWay *= kDenominator;
        DiyFp roundedInput = new(input.F >> precisionDigitsCount, input.E + precisionDigitsCount);
        ulong uerror = (ulong)error;
        if (precisionBits >= halfWay + uerror) roundedInput.F++;
        // If the last_bits are too close to the half-way case than we are too
        // inaccurate and round down. In this case we return false so that we can
        // fall back to a more precise algorithm.

        result = new Double(roundedInput).Value;
        // Too imprecise. The caller will have to fall back to a slower version.
        // However the returned number is guaranteed to be either the correct
        // double, or the next-lower double.
        return !(halfWay - uerror < precisionBits && precisionBits < halfWay + uerror);
    }

    // Returns the correct double for the buffer*10^exponent.
    // The variable guess should be a close guess that is either the correct double
    // or its lower neighbor (the nearest double less than the correct one).
    // Preconditions:
    //   buffer.length() + exponent <= kMaxDecimalPower + 1
    //   buffer.length() + exponent > kMinDecimalPower
    //   buffer.length() <= kMaxDecimalSignificantDigits
    static double BignumStrtod(ReadOnlySpan<char> buffer, int exponent, double guess)
    {
        if (double.IsPositiveInfinity(guess)) return guess;

        DiyFp upperBoundary = new Double(guess).UpperBoundary();

        Debug.Assert(buffer.Length + exponent <= kMaxDecimalPower + 1);
        Debug.Assert(buffer.Length + exponent > kMinDecimalPower);
        Debug.Assert(buffer.Length <= kMaxSignificantDecimalDigits);
        // Make sure that the Bignum will be able to hold all our numbers.
        // Our Bignum implementation has a separate field for exponents. Shifts will
        // consume at most one bigit (< 64 bits).
        // ln(10) == 3.3219...
        Bignum input = new();
        Bignum boundary = new();
        input.AssignDecimalString(buffer);
        boundary.AssignUInt64(upperBoundary.F);
        if (exponent >= 0)
        {
            input.MultiplyByPowerOfTen(exponent);
        }
        else
        {
            boundary.MultiplyByPowerOfTen(-exponent);
        }
        if (upperBoundary.E > 0)
        {
            boundary.ShiftLeft(upperBoundary.E);
        }
        else
        {
            input.ShiftLeft(-upperBoundary.E);
        }
        int comparison = Bignum.Compare(input, boundary);
        if (comparison < 0) return guess;
        if (comparison > 0) return new Double(guess).NextDouble();
        if ((new Double(guess).Significand & 1) == 0)
        {
            // Round towards even.
            return guess;
        }
        return new Double(guess).NextDouble();
    }

    /// <summary>
    /// Returns the double closest to buffer * 10^exponent. The buffer must only
    /// contain digits in the range [0-9]. It must not contain a dot or a sign.
    /// </summary>
    public static double Strtod(ReadOnlySpan<char> buffer, int exponent)
    {
        ReadOnlySpan<char> leftTrimmed = TrimLeadingZeros(buffer);
        ReadOnlySpan<char> trimmed = TrimTrailingZeros(leftTrimmed);
        exponent += leftTrimmed.Length - trimmed.Length;
        if (trimmed.IsEmpty) return 0.0;
        if (trimmed.Length > kMaxSignificantDecimalDigits)
        {
            Span<char> significantBuffer = stackalloc char[kMaxSignificantDecimalDigits];
            TrimToMaxSignificantDigits(trimmed, exponent, significantBuffer, out int significantExponent);
            return Strtod(significantBuffer, significantExponent);
        }
        if (exponent + trimmed.Length - 1 >= kMaxDecimalPower) return double.PositiveInfinity;
        if (exponent + trimmed.Length <= kMinDecimalPower) return 0.0;

        if (DoubleStrtod(trimmed, exponent, out double guess) ||
            DiyFpStrtod(trimmed, exponent, out guess))
        {
            return guess;
        }
        return BignumStrtod(trimmed, exponent, guess);
    }
}
