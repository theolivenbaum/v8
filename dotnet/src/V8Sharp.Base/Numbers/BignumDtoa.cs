// Port of src/base/numbers/bignum-dtoa.h and bignum-dtoa.cc.

namespace V8Sharp.Base.Numbers;

public enum BignumDtoaMode
{
    /// <summary>Return the shortest correct representation. For example the
    /// output of 0.299999999999999988897 is (the less accurate but correct)
    /// 0.3.</summary>
    BIGNUM_DTOA_SHORTEST,
    /// <summary>Return a fixed number of digits after the decimal point. For
    /// instance fixed(0.1, 4) becomes 0.1000. If the input number is big, the
    /// output will be big.</summary>
    BIGNUM_DTOA_FIXED,
    /// <summary>Return a fixed number of digits, no matter what the exponent is.</summary>
    BIGNUM_DTOA_PRECISION,
}

public static partial class DoubleConversion
{
    static int NormalizedExponent(ulong significand, int exponent)
    {
        Debug.Assert(significand != 0);
        while ((significand & Double.kHiddenBit) == 0)
        {
            significand <<= 1;
            exponent--;
        }
        return exponent;
    }

    /// <summary>
    /// Converts the given double 'v' to ASCII. The result should be
    /// interpreted as buffer * 10^(point-length). The buffer will be
    /// null-terminated. The input v must be > 0 and different from NaN, and
    /// Infinity.
    /// <list type="bullet">
    /// <item>SHORTEST: produce the least amount of digits for which the
    /// internal identity requirement is still satisfied. The buffer will
    /// choose the representation that is closest to 'v'. If there are two at
    /// the same distance, than the number is round up. In this mode the
    /// 'requested_digits' parameter is ignored.</item>
    /// <item>FIXED: produces digits necessary to print a given number with
    /// 'requested_digits' digits after the decimal point. The produced digits
    /// might be too short in which case the caller has to fill the gaps with
    /// '0's. Halfway cases are rounded up.</item>
    /// <item>PRECISION: produces 'requested_digits' where the first digit is
    /// not '0'. The function is allowed to return fewer digits, in which case
    /// the caller has to fill the missing digits with '0's. Halfway cases are
    /// again rounded up.</item>
    /// </list>
    /// </summary>
    public static void BignumDtoa(double v, BignumDtoaMode mode, int requestedDigits,
                                  Span<char> buffer, out int length, out int decimalPoint)
    {
        Debug.Assert(v > 0);
        Debug.Assert(!new Double(v).IsSpecial);
        ulong significand = new Double(v).Significand;
        bool isEven = (significand & 1) == 0;
        int exponent = new Double(v).Exponent;
        int normalizedExponent = NormalizedExponent(significand, exponent);
        // estimated_power might be too low by 1.
        int estimatedPower = EstimatePower(normalizedExponent);

        // Shortcut for Fixed.
        // The requested digits correspond to the digits after the point. If the
        // number is much too small, then there is no need in trying to get any
        // digits.
        if (mode == BignumDtoaMode.BIGNUM_DTOA_FIXED && -estimatedPower - 1 > requestedDigits)
        {
            buffer[0] = '\0';
            length = 0;
            // Set decimal-point to -requested_digits. This is what Gay does.
            // Note that it should not have any effect anyways since the string is
            // empty.
            decimalPoint = -requestedDigits;
            return;
        }

        Bignum numerator = new();
        Bignum denominator = new();
        Bignum deltaMinus = new();
        Bignum deltaPlus = new();
        // Make sure the bignum can grow large enough. The smallest double equals
        // 4e-324. In this case the denominator needs fewer than 324*4 binary digits.
        // The maximum double is 1.7976931348623157e308 which needs fewer than
        // 308*4 binary digits.
        bool needBoundaryDeltas = mode == BignumDtoaMode.BIGNUM_DTOA_SHORTEST;
        InitialScaledStartValues(v, estimatedPower, needBoundaryDeltas, numerator, denominator, deltaMinus, deltaPlus);
        // We now have v = (numerator / denominator) * 10^estimated_power.
        FixupMultiply10(estimatedPower, isEven, out decimalPoint, numerator, denominator, deltaMinus, deltaPlus);
        // We now have v = (numerator / denominator) * 10^(decimal_point-1), and
        //  1 <= (numerator + delta_plus) / denominator < 10
        switch (mode)
        {
            case BignumDtoaMode.BIGNUM_DTOA_SHORTEST:
                GenerateShortestDigits(numerator, denominator, deltaMinus, deltaPlus, isEven, buffer, out length);
                break;
            case BignumDtoaMode.BIGNUM_DTOA_FIXED:
                BignumToFixed(requestedDigits, ref decimalPoint, numerator, denominator, buffer, out length);
                break;
            case BignumDtoaMode.BIGNUM_DTOA_PRECISION:
                GenerateCountedDigits(requestedDigits, ref decimalPoint, numerator, denominator, buffer, out length);
                break;
            default:
                throw new UnreachableException();
        }
        buffer[length] = '\0';
    }

    // The procedure starts generating digits from the left to the right and stops
    // when the generated digits yield the shortest decimal representation of v. A
    // decimal representation of v is a number lying closer to v than to any other
    // double, so it converts to v when read.
    //
    // This is true if d, the decimal representation, is between m- and m+, the
    // upper and lower boundaries. d must be strictly between them if !is_even.
    //           m- := (numerator - delta_minus) / denominator
    //           m+ := (numerator + delta_plus) / denominator
    //
    // Precondition: 0 <= (numerator+delta_plus) / denominator < 10.
    //   If 1 <= (numerator+delta_plus) / denominator < 10 then no leading 0 digit
    //   will be produced. This should be the standard precondition.
    static void GenerateShortestDigits(Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus,
                                       bool isEven, Span<char> buffer, out int length)
    {
        // Small optimization: if delta_minus and delta_plus are the same just reuse
        // one of the two bignums.
        if (Bignum.Equal(deltaMinus, deltaPlus)) deltaPlus = deltaMinus;
        length = 0;
        while (true)
        {
            ushort d = numerator.DivideModuloIntBignum(denominator);
            Debug.Assert(d <= 9);
            // digit = numerator / denominator (integer division).
            // numerator = numerator % denominator.
            buffer[length++] = (char)(d + '0');

            // Can we stop already?
            // If the remainder of the division is less than the distance to the lower
            // boundary we can stop. In this case we simply round down (discarding the
            // remainder).
            // Similarly we test if we can round up (using the upper boundary).
            bool inDeltaRoomMinus = isEven
                ? Bignum.LessEqual(numerator, deltaMinus)
                : Bignum.Less(numerator, deltaMinus);
            bool inDeltaRoomPlus = isEven
                ? Bignum.PlusCompare(numerator, deltaPlus, denominator) >= 0
                : Bignum.PlusCompare(numerator, deltaPlus, denominator) > 0;
            if (!inDeltaRoomMinus && !inDeltaRoomPlus)
            {
                // Prepare for next iteration.
                numerator.Times10();
                deltaMinus.Times10();
                // We optimized delta_plus to be equal to delta_minus (if they share the
                // same value). So don't multiply delta_plus if they point to the same
                // object.
                if (!ReferenceEquals(deltaMinus, deltaPlus)) deltaPlus.Times10();
            }
            else if (inDeltaRoomMinus && inDeltaRoomPlus)
            {
                // Let's see if 2*numerator < denominator.
                // If yes, then the next digit would be < 5 and we can round down.
                int compare = Bignum.PlusCompare(numerator, numerator, denominator);
                if (compare < 0)
                {
                    // Remaining digits are less than .5. -> Round down (== do nothing).
                }
                else if (compare > 0)
                {
                    // Remaining digits are more than .5 of denominator. -> Round up.
                    // Note that the last digit could not be a '9' as otherwise the whole
                    // loop would have stopped earlier.
                    Debug.Assert(buffer[length - 1] != '9');
                    buffer[length - 1]++;
                }
                else
                {
                    // Halfway case.
                    // TODO(floitsch): need a way to solve half-way cases.
                    //   For now let's round towards even (since this is what Gay seems to
                    //   do).
                    if ((buffer[length - 1] - '0') % 2 == 0)
                    {
                        // Round down => Do nothing.
                    }
                    else
                    {
                        Debug.Assert(buffer[length - 1] != '9');
                        buffer[length - 1]++;
                    }
                }
                return;
            }
            else if (inDeltaRoomMinus)
            {
                // Round down (== do nothing).
                return;
            }
            else
            {
                // in_delta_room_plus
                // Round up.
                // Note again that the last digit could not be '9' since this would have
                // stopped the loop earlier.
                Debug.Assert(buffer[length - 1] != '9');
                buffer[length - 1]++;
                return;
            }
        }
    }

    // Let v = numerator / denominator < 10.
    // Then we generate 'count' digits of d = x.xxxxx... (without the decimal point)
    // from left to right. Once 'count' digits have been produced we decide wether
    // to round up or down. Remainders of exactly .5 round upwards. Numbers such
    // as 9.999999 propagate a carry all the way, and change the
    // exponent (decimal_point), when rounding upwards.
    static void GenerateCountedDigits(int count, ref int decimalPoint, Bignum numerator, Bignum denominator,
                                      Span<char> buffer, out int length)
    {
        Debug.Assert(count >= 0);
        for (int i = 0; i < count - 1; ++i)
        {
            ushort d = numerator.DivideModuloIntBignum(denominator);
            Debug.Assert(d <= 9);
            // digit = numerator / denominator (integer division).
            // numerator = numerator % denominator.
            buffer[i] = (char)(d + '0');
            // Prepare for next iteration.
            numerator.Times10();
        }
        // Generate the last digit.
        ushort last = numerator.DivideModuloIntBignum(denominator);
        if (Bignum.PlusCompare(numerator, numerator, denominator) >= 0) last++;
        buffer[count - 1] = (char)(last + '0');
        // Correct bad digits (in case we had a sequence of '9's). Propagate the
        // carry until we hat a non-'9' or til we reach the first digit.
        for (int i = count - 1; i > 0; --i)
        {
            if (buffer[i] != '0' + 10) break;
            buffer[i] = '0';
            buffer[i - 1]++;
        }
        if (buffer[0] == '0' + 10)
        {
            // Propagate a carry past the top place.
            buffer[0] = '1';
            decimalPoint++;
        }
        length = count;
    }

    // Generates 'requested_digits' after the decimal point. It might omit
    // trailing '0's. If the input number is too small then no digits at all are
    // generated (ex.: 2 fixed digits for 0.00001).
    //
    // Input verifies:  1 <= (numerator + delta) / denominator < 10.
    static void BignumToFixed(int requestedDigits, ref int decimalPoint, Bignum numerator, Bignum denominator,
                              Span<char> buffer, out int length)
    {
        // Note that we have to look at more than just the requested_digits, since
        // a number could be rounded up. Example: v=0.5 with requested_digits=0.
        // Even though the power of v equals 0 we can't just stop here.
        if (-decimalPoint > requestedDigits)
        {
            // The number is definitively too small.
            // Ex: 0.001 with requested_digits == 1.
            // Set decimal-point to -requested_digits. This is what Gay does.
            // Note that it should not have any effect anyways since the string is
            // empty.
            decimalPoint = -requestedDigits;
            length = 0;
        }
        else if (-decimalPoint == requestedDigits)
        {
            // We only need to verify if the number rounds down or up.
            // Ex: 0.04 and 0.06 with requested_digits == 1.
            // Initially the fraction lies in range (1, 10]. Multiply the denominator
            // by 10 so that we can compare more easily.
            denominator.Times10();
            if (Bignum.PlusCompare(numerator, numerator, denominator) >= 0)
            {
                // If the fraction is >= 0.5 then we have to include the rounded
                // digit.
                buffer[0] = '1';
                length = 1;
                decimalPoint++;
            }
            else
            {
                // Note that we caught most of similar cases earlier.
                length = 0;
            }
        }
        else
        {
            // The requested digits correspond to the digits after the point.
            // The variable 'needed_digits' includes the digits before the point.
            int neededDigits = decimalPoint + requestedDigits;
            GenerateCountedDigits(neededDigits, ref decimalPoint, numerator, denominator, buffer, out length);
        }
    }

    // Returns an estimation of k such that 10^(k-1) <= v < 10^k where
    // v = f * 2^exponent and 2^52 <= f < 2^53.
    // v is hence a normalized double with the given exponent. The output is an
    // approximation for the exponent of the decimal approimation .digits * 10^k.
    //
    // The result might undershoot by 1 in which case 10^k <= v < 10^k+1.
    // Note: this property holds for v's upper boundary m+ too.
    //    10^k <= m+ < 10^k+1.
    //
    // Examples:
    //  EstimatePower(0)   => 16
    //  EstimatePower(-52) => 0
    //
    // Note: e >= 0 => EstimatedPower(e) > 0. No similar claim can be made for e<0.
    static int EstimatePower(int exponent)
    {
        // This function estimates log10 of v where v = f*2^e (with e == exponent).
        // Note that 10^floor(log10(v)) <= v, but v <= 10^ceil(log10(v)).
        // Note that f is bounded by its container size. Let p = 53 (the double's
        // significand size). Then 2^(p-1) <= f < 2^p.
        //
        // Given that log10(v) == log2(v)/log2(10) and e+(len(f)-1) is quite close
        // to log2(v) the function is simplified to (e+(len(f)-1)/log2(10)).
        // The computed number undershoots by less than 0.631 (when we compute log3
        // and not log10).
        //
        // Since we want to avoid overshooting we decrement by 1e10 so that
        // floating-point imprecisions don't affect us.
        const double k1Log10 = 0.30102999566398114;  // 1/lg(10)

        // For doubles len(f) == 53 (don't forget the hidden bit).
        const int kSignificandSize = 53;
        double estimate = Math.Ceiling((exponent + kSignificandSize - 1) * k1Log10 - 1e-10);
        return (int)estimate;
    }

    // See comments for InitialScaledStartValues.
    static void InitialScaledStartValuesPositiveExponent(double v, int estimatedPower, bool needBoundaryDeltas,
        Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus)
    {
        // A positive exponent implies a positive power.
        Debug.Assert(estimatedPower >= 0);
        // Since the estimated_power is positive we simply multiply the denominator
        // by 10^estimated_power.

        // numerator = v.
        numerator.AssignUInt64(new Double(v).Significand);
        numerator.ShiftLeft(new Double(v).Exponent);
        // denominator = 10^estimated_power.
        denominator.AssignPowerUInt16(10, estimatedPower);

        if (needBoundaryDeltas)
        {
            // Introduce a common denominator so that the deltas to the boundaries are
            // integers.
            denominator.ShiftLeft(1);
            numerator.ShiftLeft(1);
            // Let v = f * 2^e, then m+ - v = 1/2 * 2^e; With the common
            // denominator (of 2) delta_plus equals 2^e.
            deltaPlus.AssignUInt16(1);
            deltaPlus.ShiftLeft(new Double(v).Exponent);
            // Same for delta_minus (with adjustments below if f == 2^p-1).
            deltaMinus.AssignUInt16(1);
            deltaMinus.ShiftLeft(new Double(v).Exponent);

            // If the significand (without the hidden bit) is 0, then the lower
            // boundary is closer than just half a ulp (unit in the last place).
            // There is only one exception: if the next lower number is a denormal then
            // the distance is 1 ulp. This cannot be the case for exponent >= 0 (but we
            // have to test it in the other function where exponent < 0).
            ulong vBits = new Double(v).AsUint64();
            if ((vBits & Double.kSignificandMask) == 0)
            {
                // The lower boundary is closer at half the distance of "normal" numbers.
                // Increase the common denominator and adapt all but the delta_minus.
                denominator.ShiftLeft(1);  // *2
                numerator.ShiftLeft(1);    // *2
                deltaPlus.ShiftLeft(1);    // *2
            }
        }
    }

    // See comments for InitialScaledStartValues
    static void InitialScaledStartValuesNegativeExponentPositivePower(double v, int estimatedPower, bool needBoundaryDeltas,
        Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus)
    {
        ulong significand = new Double(v).Significand;
        int exponent = new Double(v).Exponent;
        // v = f * 2^e with e < 0, and with estimated_power >= 0.
        // This means that e is close to 0 (have a look at how estimated_power is
        // computed).

        // numerator = significand
        //  since v = significand * 2^exponent this is equivalent to
        //  numerator = v * / 2^-exponent
        numerator.AssignUInt64(significand);
        // denominator = 10^estimated_power * 2^-exponent (with exponent < 0)
        denominator.AssignPowerUInt16(10, estimatedPower);
        denominator.ShiftLeft(-exponent);

        if (needBoundaryDeltas)
        {
            // Introduce a common denominator so that the deltas to the boundaries are
            // integers.
            denominator.ShiftLeft(1);
            numerator.ShiftLeft(1);
            // Let v = f * 2^e, then m+ - v = 1/2 * 2^e; With the common
            // denominator (of 2) delta_plus equals 2^e.
            // Given that the denominator already includes v's exponent the distance
            // to the boundaries is simply 1.
            deltaPlus.AssignUInt16(1);
            // Same for delta_minus (with adjustments below if f == 2^p-1).
            deltaMinus.AssignUInt16(1);

            // If the significand (without the hidden bit) is 0, then the lower
            // boundary is closer than just one ulp (unit in the last place).
            // There is only one exception: if the next lower number is a denormal
            // then the distance is 1 ulp. Since the exponent is close to zero
            // (otherwise estimated_power would have been negative) this cannot happen
            // here either.
            ulong vBits = new Double(v).AsUint64();
            if ((vBits & Double.kSignificandMask) == 0)
            {
                // The lower boundary is closer at half the distance of "normal" numbers.
                // Increase the denominator and adapt all but the delta_minus.
                denominator.ShiftLeft(1);  // *2
                numerator.ShiftLeft(1);    // *2
                deltaPlus.ShiftLeft(1);    // *2
            }
        }
    }

    // See comments for InitialScaledStartValues
    static void InitialScaledStartValuesNegativeExponentNegativePower(double v, int estimatedPower, bool needBoundaryDeltas,
        Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus)
    {
        const ulong kMinimalNormalizedExponent = 0x0010_0000_0000_0000;
        ulong significand = new Double(v).Significand;
        int exponent = new Double(v).Exponent;
        // Instead of multiplying the denominator with 10^estimated_power we
        // multiply all values (numerator and deltas) by 10^-estimated_power.

        // Use numerator as temporary container for power_ten.
        Bignum powerTen = numerator;
        powerTen.AssignPowerUInt16(10, -estimatedPower);

        if (needBoundaryDeltas)
        {
            // Since power_ten == numerator we must make a copy of 10^estimated_power
            // before we complete the computation of the numerator.
            // delta_plus = delta_minus = 10^estimated_power
            deltaPlus.AssignBignum(powerTen);
            deltaMinus.AssignBignum(powerTen);
        }

        // numerator = significand * 2 * 10^-estimated_power
        //  since v = significand * 2^exponent this is equivalent to
        // numerator = v * 10^-estimated_power * 2 * 2^-exponent.
        // Remember: numerator has been abused as power_ten. So no need to assign it
        //  to itself.
        numerator.MultiplyByUInt64(significand);

        // denominator = 2 * 2^-exponent with exponent < 0.
        denominator.AssignUInt16(1);
        denominator.ShiftLeft(-exponent);

        if (needBoundaryDeltas)
        {
            // Introduce a common denominator so that the deltas to the boundaries are
            // integers.
            numerator.ShiftLeft(1);
            denominator.ShiftLeft(1);
            // With this shift the boundaries have their correct value, since
            // delta_plus = 10^-estimated_power, and
            // delta_minus = 10^-estimated_power.
            // These assignments have been done earlier.

            // The special case where the lower boundary is twice as close.
            // This time we have to look out for the exception too.
            ulong vBits = new Double(v).AsUint64();
            if ((vBits & Double.kSignificandMask) == 0 &&
                // The only exception where a significand == 0 has its boundaries at
                // "normal" distances:
                (vBits & Double.kExponentMask) != kMinimalNormalizedExponent)
            {
                numerator.ShiftLeft(1);    // *2
                denominator.ShiftLeft(1);  // *2
                deltaPlus.ShiftLeft(1);    // *2
            }
        }
    }

    // Let v = significand * 2^exponent.
    // Computes v / 10^estimated_power exactly, as a ratio of two bignums, numerator
    // and denominator. The functions GenerateShortestDigits and
    // GenerateCountedDigits will then convert this ratio to its decimal
    // representation d, with the required accuracy.
    // Then d * 10^estimated_power is the representation of v.
    // (Note: the fraction and the estimated_power might get adjusted before
    // generating the decimal representation.)
    //
    // Let ep == estimated_power, then the returned values will satisfy:
    //  v / 10^ep = numerator / denominator.
    //  v's boundarys m- and m+:
    //    m- / 10^ep == v / 10^ep - delta_minus / denominator
    //    m+ / 10^ep == v / 10^ep + delta_plus / denominator
    //
    // Since 10^(k-1) <= v < 10^k    (with k == estimated_power)
    //  or       10^k <= v < 10^(k+1)
    //  we then have 0.1 <= numerator/denominator < 1
    //           or    1 <= numerator/denominator < 10
    //
    // The boundary-deltas are only filled if need_boundary_deltas is set.
    static void InitialScaledStartValues(double v, int estimatedPower, bool needBoundaryDeltas,
        Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus)
    {
        if (new Double(v).Exponent >= 0)
        {
            InitialScaledStartValuesPositiveExponent(v, estimatedPower, needBoundaryDeltas, numerator, denominator, deltaMinus, deltaPlus);
        }
        else if (estimatedPower >= 0)
        {
            InitialScaledStartValuesNegativeExponentPositivePower(v, estimatedPower, needBoundaryDeltas, numerator, denominator, deltaMinus, deltaPlus);
        }
        else
        {
            InitialScaledStartValuesNegativeExponentNegativePower(v, estimatedPower, needBoundaryDeltas, numerator, denominator, deltaMinus, deltaPlus);
        }
    }

    // This routine multiplies numerator/denominator so that its values lies in the
    // range 1-10. That is after a call to this function we have:
    //    1 <= (numerator + delta_plus) /denominator < 10.
    // Let numerator the input before modification and numerator' the argument
    // after modification, then the output-parameter decimal_point is such that
    //  numerator / denominator * 10^estimated_power ==
    //    numerator' / denominator' * 10^(decimal_point - 1)
    // In some cases estimated_power was too low, and this is already the case. We
    // then simply adjust the power so that 10^(k-1) <= v < 10^k (with k ==
    // estimated_power) but do not touch the numerator or denominator.
    // Otherwise the routine multiplies the numerator and the deltas by 10.
    static void FixupMultiply10(int estimatedPower, bool isEven, out int decimalPoint,
        Bignum numerator, Bignum denominator, Bignum deltaMinus, Bignum deltaPlus)
    {
        // For IEEE doubles half-way cases (in decimal system numbers ending with 5)
        // are rounded to the closest floating-point number with even significand.
        bool inRange = isEven
            ? Bignum.PlusCompare(numerator, deltaPlus, denominator) >= 0
            : Bignum.PlusCompare(numerator, deltaPlus, denominator) > 0;
        if (inRange)
        {
            // Since numerator + delta_plus >= denominator we already have
            // 1 <= numerator/denominator < 10. Simply update the estimated_power.
            decimalPoint = estimatedPower + 1;
        }
        else
        {
            decimalPoint = estimatedPower;
            numerator.Times10();
            if (Bignum.Equal(deltaMinus, deltaPlus))
            {
                deltaMinus.Times10();
                deltaPlus.AssignBignum(deltaMinus);
            }
            else
            {
                deltaMinus.Times10();
                deltaPlus.Times10();
            }
        }
    }
}
