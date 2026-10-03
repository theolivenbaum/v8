// Port of src/base/numbers/fast-dtoa.h and fast-dtoa.cc (Grisu3).

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Numbers;

public enum FastDtoaMode
{
    /// <summary>Computes the shortest representation of the given input. The
    /// returned result will be the most accurate number of this length.
    /// Longer representations might be more accurate.</summary>
    FAST_DTOA_SHORTEST,
    /// <summary>Computes a representation where the precision (number of
    /// digits) is given as input. The precision is independent of the decimal
    /// point.</summary>
    FAST_DTOA_PRECISION,
}

public static partial class DoubleConversion
{
    public const int kFastDtoaMaximalLength = 17;

    // The minimal and maximal target exponent define the range of w's binary
    // exponent, where 'w' is the result of multiplying the input by a cached power
    // of ten.
    const int kMinimalTargetExponent = -60;
    const int kMaximalTargetExponent = -32;

    // Adjusts the last digit of the generated number, and screens out generated
    // solutions that may be inaccurate. A solution may be inaccurate if it is
    // outside the safe interval, or if we cannot prove that it is closer to the
    // input than a neighboring representation of the same length.
    //
    // Input: * buffer containing the digits of too_high / 10^kappa
    //        * distance_too_high_w == (too_high - w).f() * unit
    //        * unsafe_interval == (too_high - too_low).f() * unit
    //        * rest = (too_high - buffer * 10^kappa).f() * unit
    //        * ten_kappa = 10^kappa * unit
    //        * unit = the common multiplier
    // Output: returns true if the buffer is guaranteed to contain the closest
    //    representable number to the input.
    //  Modifies the generated digits in the buffer to approach (round towards) w.
    static bool RoundWeed(ref char lastDigit, ulong distanceTooHighW, ulong unsafeInterval,
                          ulong rest, ulong tenKappa, ulong unit)
    {
        ulong smallDistance = distanceTooHighW - unit;
        ulong bigDistance = distanceTooHighW + unit;
        // Let w_low  = too_high - big_distance, and
        //     w_high = too_high - small_distance.
        // Note: w_low < w < w_high
        //
        // By generating the digits of too_high we got the largest (closest to
        // too_high) buffer that is still in the unsafe interval. In the case where
        // w_high < buffer < too_high we try to decrement the buffer.
        // There are 3 conditions that stop the decrementation process:
        //   1) the buffer is already below w_high
        //   2) decrementing the buffer would make it leave the unsafe interval
        //   3) decrementing the buffer would yield a number below w_high and farther
        //      away than the current number. In other words:
        //              (buffer{-1} < w_high) && w_high - buffer{-1} > buffer - w_high
        // Instead of using the buffer directly we use its distance to too_high.
        // Conceptually rest ~= too_high - buffer
        // We need to do the following tests in this order to avoid over- and
        // underflows.
        Debug.Assert(rest <= unsafeInterval);
        while (rest < smallDistance &&                // Negated condition 1
               unsafeInterval - rest >= tenKappa &&  // Negated condition 2
               (rest + tenKappa < smallDistance ||   // buffer{-1} > w_high
                smallDistance - rest >= rest + tenKappa - smallDistance))
        {
            lastDigit--;
            rest += tenKappa;
        }

        // We have approached w+ as much as possible. We now test if approaching w-
        // would require changing the buffer. If yes, then we have two possible
        // representations close to w, but we cannot decide which one is closer.
        if (rest < bigDistance && unsafeInterval - rest >= tenKappa &&
            (rest + tenKappa < bigDistance ||
             bigDistance - rest > rest + tenKappa - bigDistance))
        {
            return false;
        }

        // Weeding test.
        //   The safe interval is [too_low + 2 ulp; too_high - 2 ulp]
        //   Since too_low = too_high - unsafe_interval this is equivalent to
        //      [too_high - unsafe_interval + 4 ulp; too_high - 2 ulp]
        //   Conceptually we have: rest ~= too_high - buffer
        return (2 * unit <= rest) && (rest <= unsafeInterval - 4 * unit);
    }

    // Rounds the buffer upwards if the result is closer to v by possibly adding
    // 1 to the buffer. If the precision of the calculation is not sufficient to
    // round correctly, return false.
    // The rounding might shift the whole buffer in which case the kappa is
    // adjusted. For example "99", kappa = 3 might become "10", kappa = 4.
    //
    // If 2*rest > ten_kappa then the buffer needs to be round up.
    // rest can have an error of +/- 1 unit. This function accounts for the
    // imprecision and returns false, if the rounding direction cannot be
    // unambiguously determined.
    //
    // Precondition: rest < ten_kappa.
    static bool RoundWeedCounted(Span<char> buffer, int length, ulong rest, ulong tenKappa,
                                 ulong unit, ref int kappa)
    {
        Debug.Assert(rest < tenKappa);
        // If the unit is too big, then we don't know which way to round. For example
        // a unit of 50 means that the real number lies within rest +/- 50. If
        // 10^kappa == 40 then there is no way to tell which way to round.
        if (unit >= tenKappa) return false;
        // Even if unit is just half the size of 10^kappa we are already completely
        // lost. (And after the previous test we know that the expression will not
        // over/underflow.)
        if (tenKappa - unit <= unit) return false;
        // If 2 * (rest + unit) <= 10^kappa we can safely round down.
        if ((tenKappa - rest > rest) && (tenKappa - 2 * rest >= 2 * unit)) return true;
        // If 2 * (rest - unit) >= 10^kappa, then we can safely round up.
        if ((rest > unit) && (tenKappa - (rest - unit) <= (rest - unit)))
        {
            // Increment the last digit recursively until we find a non '9' digit.
            buffer[length - 1]++;
            for (int i = length - 1; i > 0; --i)
            {
                if (buffer[i] != '0' + 10) break;
                buffer[i] = '0';
                buffer[i - 1]++;
            }
            // If the first digit is now '0'+ 10 we had a buffer with all '9's. With the
            // exception of the first digit all digits are now '0'. Simply switch the
            // first digit to '1' and adjust the kappa. Example: "99" becomes "10" and
            // the power (the kappa) is increased.
            if (buffer[0] == '0' + 10)
            {
                buffer[0] = '1';
                kappa += 1;
            }
            return true;
        }
        return false;
    }

    const uint kTen4 = 10000;
    const uint kTen5 = 100000;
    const uint kTen6 = 1000000;
    const uint kTen7 = 10000000;
    const uint kTen8 = 100000000;
    const uint kTen9 = 1000000000;

    // This table was computed by libdivide. Essentially, the shift is
    // floor(log2(x)), and the mul is 2^(33 + shift) / x, rounded up and truncated
    // to 32 bits.
    static ReadOnlySpan<uint> DivMagicMul => [
        0,           // Not used, since 1 is not supported by the algorithm.
        0x9999999a,  // 10
        0x47ae147b,  // 100
        0x0624dd30,  // 1000
        0xa36e2eb2,  // 10000
        0x4f8b588f,  // 100000
        0x0c6f7a0c,  // 1000000
        0xad7f29ac,  // 10000000
        0x5798ee24,  // 100000000
    ];

    static ReadOnlySpan<byte> DivMagicShift => [0, 3, 6, 9, 13, 16, 19, 23, 26];

    // Returns val / divisor, and does val %= divisor. This algorithm is exactly
    // libdivide's branch-free u32 algorithm, except that we add back a branch
    // anyway to support 1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint FastDivmod(ref uint val, uint divisor, uint divisorExponent)
    {
        if (divisor == 1)
        {
            uint d = val;
            val = 0;
            return d;
        }
        uint q = (uint)(((ulong)val * DivMagicMul[(int)divisorExponent]) >> 32);
        uint t = ((val - q) >> 1) + q;
        uint digit = t >> DivMagicShift[(int)divisorExponent];
        val -= digit * divisor;
        return digit;
    }

    // Returns the biggest power of ten that is less than or equal than the given
    // number. We furthermore receive the maximum number of bits 'number' has.
    // If number_bits == 0 then 0^-1 is returned
    // The number of bits must be <= 32.
    // Precondition: number < (1 << (number_bits + 1)).
    static void BiggestPowerTen(uint number, int numberBits, out uint power, out uint exponent)
    {
        // The C++ switch falls through from the case for numberBits downwards;
        // each "level" is entered only when numberBits is large enough.
        if (numberBits >= 30 && kTen9 <= number) { power = kTen9; exponent = 9; return; }
        if (numberBits >= 27 && kTen8 <= number) { power = kTen8; exponent = 8; return; }
        if (numberBits >= 24 && kTen7 <= number) { power = kTen7; exponent = 7; return; }
        if (numberBits >= 20 && kTen6 <= number) { power = kTen6; exponent = 6; return; }
        if (numberBits >= 17 && kTen5 <= number) { power = kTen5; exponent = 5; return; }
        if (numberBits >= 14 && kTen4 <= number) { power = kTen4; exponent = 4; return; }
        if (numberBits >= 10 && 1000 <= number) { power = 1000; exponent = 3; return; }
        if (numberBits >= 7 && 100 <= number) { power = 100; exponent = 2; return; }
        if (numberBits >= 4 && 10 <= number) { power = 10; exponent = 1; return; }
        if (numberBits >= 1 && 1 <= number) { power = 1; exponent = 0; return; }
        power = 0;
        exponent = unchecked((uint)-1);
    }

    // Generates the digits of input number w.
    // w is a floating-point number (DiyFp), consisting of a significand and an
    // exponent. Its exponent is bounded by kMinimalTargetExponent and
    // kMaximalTargetExponent.
    //       Hence -60 <= w.e() <= -32.
    //
    // Returns false if it fails, in which case the generated digits in the buffer
    // should not be used.
    // Preconditions:
    //  * low, w and high are correct up to 1 ulp (unit in the last place). That
    //    is, their error must be less than a unit of their last digits.
    //  * low.e() == w.e() == high.e()
    //  * low < w < high, and taking into account their error: low~ <= high~
    //  * kMinimalTargetExponent <= w.e() <= kMaximalTargetExponent
    // Postconditions: returns false if procedure fails.
    //   otherwise:
    //     * buffer is not null-terminated, but len contains the number of digits.
    //     * buffer contains the shortest possible decimal digit-sequence
    //       such that LOW < buffer * 10^kappa < HIGH, where LOW and HIGH are the
    //       correct values of low and high (without their error).
    //     * if more than one decimal representation gives the minimal number of
    //       decimal digits then the one closest to W (where W is the correct value
    //       of w) is chosen.
    // Remark: this procedure takes into account the imprecision of its input
    //   numbers. If the precision is not enough to guarantee all the postconditions
    //   then false is returned. This usually happens rarely (~0.5%).
    static bool DigitGen(DiyFp low, DiyFp w, DiyFp high, Span<char> buffer, ref int outpos, out int kappa)
    {
        Debug.Assert(low.E == w.E && w.E == high.E);
        Debug.Assert(low.F + 1 <= high.F - 1);
        Debug.Assert(kMinimalTargetExponent <= w.E && w.E <= kMaximalTargetExponent);
        // low, w and high are imprecise, but by less than one ulp (unit in the last
        // place).
        // If we remove (resp. add) 1 ulp from low (resp. high) we are certain that
        // the new numbers are outside of the interval we want the final
        // representation to lie in.
        // Inversely adding (resp. removing) 1 ulp from low (resp. high) would yield
        // numbers that are certain to lie in the interval. We will use this fact
        // later on.
        // We will now start by generating the digits within the uncertain
        // interval. Later we will weed out representations that lie outside the safe
        // interval and thus _might_ lie outside the correct interval.
        ulong unit = 1;
        DiyFp tooLow = new(low.F - unit, low.E);
        DiyFp tooHigh = new(high.F + unit, high.E);
        // too_low and too_high are guaranteed to lie outside the interval we want the
        // generated number in.
        DiyFp unsafeInterval = DiyFp.Minus(tooHigh, tooLow);
        // We now cut the input number into two parts: the integral digits and the
        // fractionals. We will not write any decimal separator though, but adapt
        // kappa instead.
        // Reminder: we are currently computing the digits (stored inside the buffer)
        // such that:   too_low < buffer * 10^kappa < too_high
        // We use too_high for the digit_generation and stop as soon as possible.
        // If we stop early we effectively round down.
        DiyFp one = new(1UL << -w.E, w.E);
        // Division by one is a shift.
        uint integrals = (uint)(tooHigh.F >> -one.E);
        // Modulo by one is an and.
        ulong fractionals = tooHigh.F & (one.F - 1);
        BiggestPowerTen(integrals, DiyFp.kSignificandSize - (-one.E), out uint divisor, out uint divisorExponent);
        kappa = (int)(divisorExponent + 1);
        // Loop invariant: buffer = too_high / 10^kappa  (integer division)
        // The invariant holds for the first iteration: kappa has been initialized
        // with the divisor exponent + 1. And the divisor is the biggest power of ten
        // that is smaller than integrals.
        while (kappa > 0)
        {
            uint digit = FastDivmod(ref integrals, divisor, divisorExponent);
            buffer[outpos++] = (char)('0' + digit);
            kappa--;
            // Note that kappa now equals the exponent of the divisor and that the
            // invariant thus holds again.
            ulong rest = ((ulong)integrals << -one.E) + fractionals;
            // Invariant: too_high = buffer * 10^kappa + DiyFp(rest, one.e())
            // Reminder: unsafe_interval.e() == one.e()
            if (rest < unsafeInterval.F)
            {
                // Rounding down (by not emitting the remaining digits) yields a number
                // that lies within the unsafe interval.
                return RoundWeed(ref buffer[outpos - 1], DiyFp.Minus(tooHigh, w).F,
                                 unsafeInterval.F, rest, (ulong)divisor << -one.E, unit);
            }
            if (kappa <= 0) break;
            divisor /= 10;
            --divisorExponent;
        }

        // The integrals have been generated. We are at the point of the decimal
        // separator. In the following loop we simply multiply the remaining digits by
        // 10 and divide by one. We just need to pay attention to multiply associated
        // data (like the interval or 'unit'), too.
        // Note that the multiplication by 10 does not overflow, because w.e >= -60
        // and thus one.e >= -60.
        Debug.Assert(one.E >= -60);
        Debug.Assert(fractionals < one.F);
        Debug.Assert(0xFFFF_FFFF_FFFF_FFFF / 10 >= one.F);
        while (true)
        {
            fractionals *= 10;
            unit *= 10;
            unsafeInterval.F *= 10;
            // Integer division by one.
            int digit = (int)(fractionals >> -one.E);
            buffer[outpos++] = (char)('0' + digit);
            fractionals &= one.F - 1;  // Modulo by one.
            kappa--;
            if (fractionals < unsafeInterval.F)
            {
                return RoundWeed(ref buffer[outpos - 1], DiyFp.Minus(tooHigh, w).F * unit,
                                 unsafeInterval.F, fractionals, one.F, unit);
            }
        }
    }

    // Generates (at most) requested_digits of input number w.
    // w is a floating-point number (DiyFp), consisting of a significand and an
    // exponent. Its exponent is bounded by kMinimalTargetExponent and
    // kMaximalTargetExponent.
    //       Hence -60 <= w.e() <= -32.
    //
    // Returns false if it fails, in which case the generated digits in the buffer
    // should not be used.
    // Preconditions:
    //  * w is correct up to 1 ulp (unit in the last place). That
    //    is, its error must be strictly less than a unit of its last digit.
    //  * kMinimalTargetExponent <= w.e() <= kMaximalTargetExponent
    //
    // Postconditions: returns false if procedure fails.
    //   otherwise:
    //     * buffer is not null-terminated, but length contains the number of
    //       digits.
    //     * the representation in buffer is the most precise representation of
    //       requested_digits digits.
    //     * buffer contains at most requested_digits digits of w. If there are less
    //       than requested_digits digits then some trailing '0's have been removed.
    //     * kappa is such that
    //            w = buffer * 10^kappa + eps with |eps| < 10^kappa / 2.
    //
    // Remark: This procedure takes into account the imprecision of its input
    //   numbers. If the precision is not enough to guarantee all the postconditions
    //   then false is returned. This usually happens rarely, but the failure-rate
    //   increases with higher requested_digits.
    static bool DigitGenCounted(DiyFp w, int requestedDigits, Span<char> buffer, out int length, out int kappa)
    {
        Debug.Assert(kMinimalTargetExponent <= w.E && w.E <= kMaximalTargetExponent);
        // w is assumed to have an error less than 1 unit. Whenever w is scaled we
        // also scale its error.
        ulong wError = 1;
        // We cut the input number into two parts: the integral digits and the
        // fractional digits. We don't emit any decimal separator, but adapt kappa
        // instead. Example: instead of writing "1.2" we put "12" into the buffer and
        // increase kappa by 1.
        DiyFp one = new(1UL << -w.E, w.E);
        // Division by one is a shift.
        uint integrals = (uint)(w.F >> -one.E);
        // Modulo by one is an and.
        ulong fractionals = w.F & (one.F - 1);
        BiggestPowerTen(integrals, DiyFp.kSignificandSize - (-one.E), out uint divisor, out uint divisorExponent);
        kappa = (int)(divisorExponent + 1);
        length = 0;

        // Loop invariant: buffer = w / 10^kappa  (integer division)
        // The invariant holds for the first iteration: kappa has been initialized
        // with the divisor exponent + 1. And the divisor is the biggest power of ten
        // that is smaller than 'integrals'.
        while (kappa > 0)
        {
            uint digit = FastDivmod(ref integrals, divisor, divisorExponent);
            buffer[length] = (char)('0' + digit);
            length++;
            requestedDigits--;
            kappa--;
            // Note that kappa now equals the exponent of the divisor and that the
            // invariant thus holds again.
            if (requestedDigits == 0) break;
            divisor /= 10;
            --divisorExponent;
        }

        if (requestedDigits == 0)
        {
            ulong rest = ((ulong)integrals << -one.E) + fractionals;
            return RoundWeedCounted(buffer, length, rest, (ulong)divisor << -one.E, wError, ref kappa);
        }

        // The integrals have been generated. We are at the point of the decimal
        // separator. In the following loop we simply multiply the remaining digits by
        // 10 and divide by one. We just need to pay attention to multiply associated
        // data (the 'unit'), too.
        // Note that the multiplication by 10 does not overflow, because w.e >= -60
        // and thus one.e >= -60.
        Debug.Assert(one.E >= -60);
        Debug.Assert(fractionals < one.F);
        while (requestedDigits > 0 && fractionals > wError)
        {
            fractionals *= 10;
            wError *= 10;
            // Integer division by one.
            int digit = (int)(fractionals >> -one.E);
            buffer[length] = (char)('0' + digit);
            length++;
            requestedDigits--;
            fractionals &= one.F - 1;  // Modulo by one.
            kappa--;
        }
        if (requestedDigits != 0) return false;
        return RoundWeedCounted(buffer, length, fractionals, one.F, wError, ref kappa);
    }

    // Provides a decimal representation of v.
    // Returns true if it succeeds, otherwise the result cannot be trusted.
    // There will be *length digits inside the buffer (not null-terminated).
    // If the function returns true then
    //        v == (double) (buffer * 10^decimal_exponent).
    // The digits in the buffer are the shortest representation possible: no
    // 0.09999999999999999 instead of 0.1. The shorter representation will even be
    // chosen even if the longer one would be closer to v.
    // The last digit will be closest to the actual v. That is, even if several
    // digits might correctly yield 'v' when read again, the closest will be
    // computed.
    static bool Grisu3(double v, Span<char> buffer, out int length, out int decimalExponent)
    {
        DiyFp w = new Double(v).AsNormalizedDiyFp();
        // boundary_minus and boundary_plus are the boundaries between v and its
        // closest floating-point neighbors. Any number strictly between
        // boundary_minus and boundary_plus will round to v when convert to a double.
        // Grisu3 will never output representations that lie exactly on a boundary.
        new Double(v).NormalizedBoundaries(out DiyFp boundaryMinus, out DiyFp boundaryPlus);
        Debug.Assert(boundaryPlus.E == w.E);
        int tenMkMinimalBinaryExponent = kMinimalTargetExponent - (w.E + DiyFp.kSignificandSize);
        int tenMkMaximalBinaryExponent = kMaximalTargetExponent - (w.E + DiyFp.kSignificandSize);
        PowersOfTenCache.GetCachedPowerForBinaryExponentRange(
            tenMkMinimalBinaryExponent, tenMkMaximalBinaryExponent, out DiyFp tenMk, out int mk);
        // Note that ten_mk is only an approximation of 10^-k. A DiyFp only contains a
        // 64 bit significand and ten_mk is thus only precise up to 64 bits.

        // The DiyFp::Times procedure rounds its result, and ten_mk is approximated
        // too. The variable scaled_w (as well as scaled_boundary_minus/plus) are now
        // off by a small amount.
        // In fact: scaled_w - w*10^k < 1ulp (unit in the last place) of scaled_w.
        // In other words: let f = scaled_w.f() and e = scaled_w.e(), then
        //           (f-1) * 2^e < w*10^k < (f+1) * 2^e
        DiyFp scaledW = DiyFp.Times(w, tenMk);
        Debug.Assert(scaledW.E == boundaryPlus.E + tenMk.E + DiyFp.kSignificandSize);
        DiyFp scaledBoundaryMinus = DiyFp.Times(boundaryMinus, tenMk);
        DiyFp scaledBoundaryPlus = DiyFp.Times(boundaryPlus, tenMk);

        // DigitGen will generate the digits of scaled_w. Therefore we have
        // v == (double) (scaled_w * 10^-mk).
        // Set decimal_exponent == -mk and pass it to DigitGen. If scaled_w is not an
        // integer than it will be updated. For instance if scaled_w == 1.23 then
        // the buffer will be filled with "123" und the decimal_exponent will be
        // decreased by 2.
        length = 0;
        bool result = DigitGen(scaledBoundaryMinus, scaledW, scaledBoundaryPlus, buffer, ref length, out int kappa);
        decimalExponent = -mk + kappa;
        return result;
    }

    // The "counted" version of grisu3 (see above) only generates requested_digits
    // number of digits. This version does not generate the shortest representation,
    // and with enough requested digits 0.1 will at some point print as 0.9999999...
    // Grisu3 is too imprecise for real halfway cases (1.5 will not work) and
    // therefore the rounding strategy for halfway cases is irrelevant.
    static bool Grisu3Counted(double v, int requestedDigits, Span<char> buffer, out int length, out int decimalExponent)
    {
        DiyFp w = new Double(v).AsNormalizedDiyFp();
        int tenMkMinimalBinaryExponent = kMinimalTargetExponent - (w.E + DiyFp.kSignificandSize);
        int tenMkMaximalBinaryExponent = kMaximalTargetExponent - (w.E + DiyFp.kSignificandSize);
        PowersOfTenCache.GetCachedPowerForBinaryExponentRange(
            tenMkMinimalBinaryExponent, tenMkMaximalBinaryExponent, out DiyFp tenMk, out int mk);
        DiyFp scaledW = DiyFp.Times(w, tenMk);
        // We now have (double) (scaled_w * 10^-mk).
        // DigitGen will generate the first requested_digits digits of scaled_w and
        // return together with a kappa such that scaled_w ~= buffer * 10^kappa. (It
        // will not always be exactly the same since DigitGenCounted only produces a
        // limited number of digits.)
        bool result = DigitGenCounted(scaledW, requestedDigits, buffer, out length, out int kappa);
        decimalExponent = -mk + kappa;
        return result;
    }

    /// <summary>
    /// Provides a decimal representation of v. The result should be
    /// interpreted as buffer * 10^(point - length). Returns false if the fast
    /// algorithm cannot guarantee a correct result; the buffer is then
    /// unusable. On success the buffer is null-terminated (it needs room for
    /// kFastDtoaMaximalLength + 1 characters in shortest mode).
    /// </summary>
    public static bool FastDtoa(double v, FastDtoaMode mode, int requestedDigits,
                                Span<char> buffer, out int length, out int decimalPoint)
    {
        Debug.Assert(v > 0);
        Debug.Assert(!new Double(v).IsSpecial);

        bool result;
        int decimalExponent;
        switch (mode)
        {
            case FastDtoaMode.FAST_DTOA_SHORTEST:
                result = Grisu3(v, buffer, out length, out decimalExponent);
                break;
            case FastDtoaMode.FAST_DTOA_PRECISION:
                result = Grisu3Counted(v, requestedDigits, buffer, out length, out decimalExponent);
                break;
            default:
                throw new UnreachableException();
        }
        decimalPoint = 0;
        if (result)
        {
            decimalPoint = length + decimalExponent;
            buffer[length] = '\0';
        }
        return result;
    }
}
