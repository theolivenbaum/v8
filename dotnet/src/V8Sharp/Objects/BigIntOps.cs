// Port of src/objects/bigint.cc: the heap-level BigInt operations (arithmetic,
// comparison, conversions from and to strings, numbers and 64-bit integers,
// asIntN/asUintN), the MutableBigInt_* helpers the Torque builtins call, and
// the operator bodies of src/builtins/builtins-bigint.tq (add, subtract,
// multiply, divide, modulus, bitwise and/or/xor, shifts, unary minus,
// comparisons) that the interpreter's BigInt paths use. The digit algorithms
// are V8Sharp.Base.BigInts (src/bigint).
using System.Runtime.CompilerServices;
using V8Sharp.Base.BigInts;
using V8Sharp.Base.Numbers;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Objects;

public sealed partial class BigInt
{
    static readonly BigInt s_zero = new(false, []);

    /// <summary>A canonical 0n (V8 allocates a fresh one; BigInt identity is not observable).</summary>
    public static BigInt Zero => s_zero;

    static readonly ConditionalWeakTable<Isolate, Processor> s_processors = new();

    /// <summary>Isolate::bigint_processor: interruptible by TerminateExecution.</summary>
    public static Processor GetProcessor(Isolate isolate) =>
        s_processors.GetValue(isolate, static i => new Processor(i.StackGuard.CheckTerminateExecution));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowBigIntTooBig(Isolate isolate) => isolate.ThrowRangeError(MessageTemplate.BigIntTooBig);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static BigInt Terminate(Isolate isolate)
    {
        isolate.TerminateExecution();
        return null!;
    }

    // ---------------------------------------------------------------------
    // Construction.

    /// <summary>MutableBigInt::NewFromInt.</summary>
    public static BigInt FromInt(Isolate isolate, int value)
    {
        if (value == 0) return s_zero;
        BigInt result = MutableBigInt.NewUnchecked(1);
        bool sign = value < 0;
        MutableBigInt.SetSign(result, sign);
        MutableBigInt.SetDigit(result, 0, sign ? (ulong)(-(long)value) : (ulong)value);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>MutableBigInt::NewFromDouble: {value} must be integral and finite.</summary>
    static BigInt NewFromDouble(double value)
    {
        Debug.Assert(value == Math.Floor(value));
        if (value == 0) return s_zero;

        bool sign = value < 0;  // -0 was already handled above.
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        int rawExponent = (int)(doubleBits >> Double.kPhysicalSignificandSize) & 0x7FF;
        Debug.Assert(rawExponent != 0x7FF);
        Debug.Assert(rawExponent >= 0x3FF);
        int exponent = rawExponent - 0x3FF;
        int digits = exponent / kDigitBits + 1;
        BigInt result = MutableBigInt.NewUnchecked(digits);
        MutableBigInt.SetSign(result, sign);

        // We construct a BigInt from the double {value} by shifting its mantissa
        // according to its exponent and mapping the bit pattern onto digits.
        //
        //               <----------- bitlength = exponent + 1 ----------->
        //                <----- 52 ------> <------ trailing zeroes ------>
        // mantissa:     1yyyyyyyyyyyyyyyyy 0000000000000000000000000000000
        // digits:    0001xxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx
        //                <-->          <------>
        //          msd_topbit         kDigitBits
        //
        ulong mantissa = (doubleBits & Double.kSignificandMask) | Double.kHiddenBit;
        const int kMantissaTopBit = Double.kSignificandSize - 1;  // 0-indexed.
        // 0-indexed position of most significant bit in the most significant digit.
        int msdTopbit = exponent % kDigitBits;
        // Number of unused bits in {mantissa}. We'll keep them shifted to the
        // left (i.e. most significant part) of the underlying uint64_t.
        int remainingMantissaBits = 0;
        // Next digit under construction.
        ulong digit;

        // First, build the MSD by shifting the mantissa appropriately.
        if (msdTopbit < kMantissaTopBit)
        {
            remainingMantissaBits = kMantissaTopBit - msdTopbit;
            digit = mantissa >> remainingMantissaBits;
            mantissa <<= 64 - remainingMantissaBits;
        }
        else
        {
            digit = mantissa << (msdTopbit - kMantissaTopBit);
            mantissa = 0;
        }
        MutableBigInt.SetDigit(result, digits - 1, digit);
        // Then fill in the rest of the digits.
        for (int digitIndex = digits - 2; digitIndex >= 0; digitIndex--)
        {
            if (remainingMantissaBits > 0)
            {
                remainingMantissaBits -= kDigitBits;
                digit = mantissa;
                mantissa = 0;
            }
            else
            {
                digit = 0;
            }
            MutableBigInt.SetDigit(result, digitIndex, digit);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>MutableBigInt::Copy.</summary>
    static BigInt Copy(BigInt source)
    {
        BigInt result = MutableBigInt.NewUnchecked(source.Length);
        source.Digits.CopyTo(MutableBigInt.RwDigits(result));
        return result;
    }

    /// <summary>BigInt::FromInt64.</summary>
    public static BigInt FromInt64(Isolate isolate, long n)
    {
        if (n == 0) return s_zero;
        BigInt result = MutableBigInt.NewUnchecked(1);
        bool sign = n < 0;
        MutableBigInt.SetSign(result, sign);
        ulong absolute = !sign ? (ulong)n : n == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)(-n);
        MutableBigInt.SetDigit(result, 0, absolute);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::FromUint64.</summary>
    public static BigInt FromUint64(Isolate isolate, ulong n)
    {
        if (n == 0) return s_zero;
        BigInt result = MutableBigInt.NewUnchecked(1);
        MutableBigInt.SetDigit(result, 0, n);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::FromWords64.</summary>
    public static BigInt FromWords64(Isolate isolate, bool signBit, ReadOnlySpan<ulong> words)
    {
        if (words.Length > kMaxLength) ThrowBigIntTooBig(isolate);
        if (words.Length == 0) return s_zero;
        BigInt result = MutableBigInt.New(isolate, words.Length);
        MutableBigInt.SetSign(result, signBit);
        words.CopyTo(MutableBigInt.RwDigits(result));
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::Words64Count.</summary>
    public int Words64Count() => Length;

    /// <summary>BigInt::ToWordsArray64: returns the sign bit; fills as many words as fit.</summary>
    public bool ToWordsArray64(Span<ulong> words, out int words64Count)
    {
        words64Count = Words64Count();
        for (int i = 0; i < Length && i < words.Length; ++i) words[i] = _digits[i];
        return Sign;
    }

    /// <summary>
    /// BigInt::FromNumber: ES #sec-numbertobigint. Throws a RangeError for
    /// non-integral numbers.
    /// </summary>
    public static BigInt FromNumber(Isolate isolate, JSValue number)
    {
        Debug.Assert(number.IsNumber);
        if (number.IsSmi) return FromInt(isolate, (int)number.Number);
        double value = number.Number;
        if (!double.IsFinite(value) || Conversions.DoubleToInteger(value) != value)
        {
            isolate.ThrowRangeError(MessageTemplate.BigIntFromNumber, number);
        }
        return NewFromDouble(value);
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
    /// (the string is not a StringIntegerLiteral, or it is too big).
    /// </summary>
    public static BigInt? StringToBigInt(Isolate isolate, JSString str)
    {
        BigIntParseStatus status = Conversions.StringToBigInt(str.FlatSpan(), out bool negative, out ulong[] digits, GetProcessor(isolate));
        if (GetProcessor(isolate).should_terminate())
        {
            GetProcessor(isolate).get_and_clear_status();
            return Terminate(isolate);
        }
        if (status != BigIntParseStatus.kOk) return null;
        if (digits.Length == 0) return s_zero;
        return new BigInt(negative, digits);
    }

    /// <summary>
    /// BigIntLiteral (src/numbers/conversions.cc): the value of a BigInt
    /// literal's text as the scanner accepted it (radix prefixes, a sign).
    /// </summary>
    public static BigInt BigIntLiteral(Isolate isolate, ReadOnlySpan<char> str)
    {
        BigIntParseStatus status = Conversions.BigIntLiteral(str, out bool negative, out ulong[] digits, GetProcessor(isolate));
        if (status == BigIntParseStatus.kMaxSizeExceeded) ThrowBigIntTooBig(isolate);
        if (status != BigIntParseStatus.kOk) throw new ArgumentException("invalid BigInt literal", nameof(str));
        return digits.Length == 0 ? s_zero : new BigInt(negative, digits);
    }

    // ---------------------------------------------------------------------
    // Conversions out.

    /// <summary>MutableBigInt::GetRawBits: the least significant 64 bits in two's complement.</summary>
    static ulong GetRawBits(BigInt x, out bool lossless)
    {
        lossless = true;
        if (x.IsZero) return 0;
        if (x.Length > 1) lossless = false;
        ulong raw = x._digits[0];
        // Simulate two's complement.
        return x.Sign ? (~raw) + 1u : raw;
    }

    /// <summary>BigInt::AsInt64: the value modulo 2^64 as a signed integer.</summary>
    public static long AsInt64(BigInt x, out bool lossless)
    {
        ulong raw = GetRawBits(x, out lossless);
        long result = unchecked((long)raw);
        if ((result < 0) != x.Sign) lossless = false;
        return result;
    }

    /// <summary>BigInt::AsUint64: the value modulo 2^64.</summary>
    public static ulong AsUint64(BigInt x, out bool lossless)
    {
        ulong result = GetRawBits(x, out lossless);
        if (x.Sign) lossless = false;
        return result;
    }

    /// <summary>BigInt::ToNumber.</summary>
    public static JSValue ToNumber(Isolate isolate, BigInt x)
    {
        if (x.IsZero) return JSValue.Zero;
        if (x.Length == 1 && x._digits[0] < (ulong)JSValue.SmiMaxValue)
        {
            int value = (int)x._digits[0];
            if (x.Sign) value = -value;
            return JSValue.FromInt(value);
        }
        return JSValue.FromNumber(ToDouble(x));
    }

    enum Rounding { kRoundDown, kTie, kRoundUp }

    /// <summary>MutableBigInt::ToDouble.</summary>
    public static double ToDouble(BigInt x)
    {
        if (x.IsZero) return 0.0;
        int xLength = x.Length;
        ulong xMsd = x._digits[xLength - 1];
        int msdLeadingZeros = System.Numerics.BitOperations.LeadingZeroCount(xMsd);
        long xBitlength = (long)xLength * kDigitBits - msdLeadingZeros;
        if (xBitlength > 1024) return x.Sign ? double.NegativeInfinity : double.PositiveInfinity;
        ulong exponent = (ulong)(xBitlength - 1);
        // We need the most significant bit shifted to the position of a double's
        // "hidden bit". We also need to hide that MSB, so we shift it out.
        ulong currentDigit = xMsd;
        int digitIndex = xLength - 1;
        int shift = msdLeadingZeros + 1 + (64 - kDigitBits);
        Debug.Assert(1 <= shift && shift <= 64);
        ulong mantissa = shift == 64 ? 0 : currentDigit << shift;
        mantissa >>= 12;
        int mantissaBitsUnset = shift - 12;
        // If not all mantissa bits are defined yet, get more digits as needed.
        if (mantissaBitsUnset >= kDigitBits && digitIndex > 0)
        {
            digitIndex--;
            currentDigit = x._digits[digitIndex];
            mantissa |= currentDigit << (mantissaBitsUnset - kDigitBits);
            mantissaBitsUnset -= kDigitBits;
        }
        if (mantissaBitsUnset > 0 && digitIndex > 0)
        {
            Debug.Assert(mantissaBitsUnset < kDigitBits);
            digitIndex--;
            currentDigit = x._digits[digitIndex];
            mantissa |= currentDigit >> (kDigitBits - mantissaBitsUnset);
            mantissaBitsUnset -= kDigitBits;
        }
        // If there are unconsumed digits left, we may have to round.
        Rounding rounding = DecideRounding(x, mantissaBitsUnset, digitIndex, currentDigit);
        if (rounding == Rounding.kRoundUp || (rounding == Rounding.kTie && (mantissa & 1) == 1))
        {
            mantissa++;
            // Incrementing the mantissa can overflow the mantissa bits. In that case
            // the new mantissa will be all zero (plus hidden bit).
            if ((mantissa >> Double.kPhysicalSignificandSize) != 0)
            {
                mantissa = 0;
                exponent++;
                // Incrementing the exponent can overflow too.
                if (exponent > 1023) return x.Sign ? double.NegativeInfinity : double.PositiveInfinity;
            }
        }
        // Assemble the result.
        ulong signBit = x.Sign ? 1UL << 63 : 0;
        exponent = (exponent + 0x3FF) << Double.kPhysicalSignificandSize;
        ulong doubleBits = signBit | exponent | mantissa;
        return BitConverter.UInt64BitsToDouble(doubleBits);
    }

    /// <summary>MutableBigInt::DecideRounding.</summary>
    static Rounding DecideRounding(BigInt x, int mantissaBitsUnset, int digitIndex, ulong currentDigit)
    {
        if (mantissaBitsUnset > 0) return Rounding.kRoundDown;
        int topUnconsumedBit;
        if (mantissaBitsUnset < 0)
        {
            // There are unconsumed bits in {current_digit}.
            topUnconsumedBit = -mantissaBitsUnset - 1;
        }
        else
        {
            Debug.Assert(mantissaBitsUnset == 0);
            // {current_digit} fit the mantissa exactly; look at the next digit.
            if (digitIndex == 0) return Rounding.kRoundDown;
            digitIndex--;
            currentDigit = x._digits[digitIndex];
            topUnconsumedBit = kDigitBits - 1;
        }
        // If the most significant remaining bit is 0, round down.
        ulong bitmask = 1UL << topUnconsumedBit;
        if ((currentDigit & bitmask) == 0) return Rounding.kRoundDown;
        // If any other remaining bit is set, round up.
        bitmask -= 1;
        if ((currentDigit & bitmask) != 0) return Rounding.kRoundUp;
        while (digitIndex > 0)
        {
            digitIndex--;
            if (x._digits[digitIndex] != 0) return Rounding.kRoundUp;
        }
        return Rounding.kTie;
    }

    /// <summary>BigInt::ToString.</summary>
    public static JSString ToString(Isolate isolate, BigInt bigint, int radix = 10) =>
        ToString(isolate, bigint, radix, ShouldThrow.ThrowOnError)!;

    /// <summary>BigInt::ToString; null (no exception) when the result is too long and should_throw is kDontThrow.</summary>
    public static JSString? ToString(Isolate isolate, BigInt bigint, int radix, ShouldThrow shouldThrow)
    {
        if (bigint.IsZero) return ReadOnlyRoots.zero_string;
        bool sign = bigint.Sign;
        if (bigint.Length == 1 && radix == 10)
        {
            // Fast path for the most common case, to avoid call/dispatch overhead.
            ulong digit = bigint._digits[0];
            Span<char> chars = stackalloc char[21];
            int pos = chars.Length;
            while (digit != 0)
            {
                chars[--pos] = (char)('0' + (int)(digit % 10));
                digit /= 10;
            }
            if (sign) chars[--pos] = '-';
            return isolate.Factory.NewStringFromUtf16(chars[pos..]);
        }

        // Generic path, handles anything.
        Debug.Assert(radix >= 2 && radix <= 36);
        int charsAllocated = Bigint.ToStringResultLength(bigint.Digits, radix, sign);
        if (charsAllocated > JSString.kMaxLength)
        {
            if (shouldThrow == ShouldThrow.ThrowOnError)
            {
                isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
            }
            return null;
        }
        char[] characters = System.Buffers.ArrayPool<char>.Shared.Rent(charsAllocated);
        try
        {
            int charsWritten = charsAllocated;
            Processor processor = GetProcessor(isolate);
            Status status = processor.ToString(characters, ref charsWritten, bigint.Digits, radix, sign);
            if (status == Status.kInterrupted)
            {
                isolate.TerminateExecution();
                return null;
            }
            return isolate.Factory.NewStringFromUtf16(characters.AsSpan(0, charsWritten));
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(characters);
        }
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
        // Resetting the budget should be enough to make sure the following ToString()
        // can complete without checking for (termination) interrupt requests.
        Processor processor = GetProcessor(isolate);
        processor.ResetInterruptCheckBudget();
        string s = processor.ToString(bigint.Digits, 10, bigint.Sign);
        processor.get_and_clear_status();
        return isolate.Factory.NewStringFromAsciiChecked(s);
    }

    // ---------------------------------------------------------------------
    // Arithmetic (bigint.cc).

    /// <summary>BigInt::UnaryMinus.</summary>
    public static BigInt UnaryMinus(Isolate isolate, BigInt x)
    {
        // Special case: There is no -0n.
        if (x.IsZero) return x;
        BigInt result = Copy(x);
        MutableBigInt.SetSign(result, !x.Sign);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::BitwiseNot.</summary>
    public static BigInt BitwiseNot(Isolate isolate, BigInt x)
    {
        BigInt result;
        if (x.Sign)
        {
            // ~(-x) == ~(~(x-1)) == x-1
            result = AbsoluteSubOne(x);
        }
        else
        {
            // ~x == -x-1 == -(x+1)
            result = AbsoluteAddOne(isolate, x, true);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::Exponentiate.</summary>
    public static BigInt Exponentiate(Isolate isolate, BigInt @base, BigInt exponent)
    {
        // 1. If exponent is < 0, throw a RangeError exception.
        if (exponent.Sign)
        {
            isolate.ThrowRangeError(MessageTemplate.MustBePositive, isolate.Factory.NewStringFromAsciiChecked("Exponent"));
        }
        // 2. If base is 0n and exponent is 0n, return 1n.
        if (exponent.IsZero) return FromInt(isolate, 1);
        // 3. Return a BigInt representing the mathematical value of base raised
        //    to the power exponent.
        if (@base.IsZero) return @base;
        if (@base.Length == 1 && @base._digits[0] == 1)
        {
            // (-1) ** even_number == 1.
            if (@base.Sign && (exponent._digits[0] & 1) == 0) return UnaryMinus(isolate, @base);
            // (-1) ** odd_number == -1; 1 ** anything == 1.
            return @base;
        }
        // For all bases >= 2, very large exponents would lead to unrepresentable
        // results.
        if (exponent.Length > 1) ThrowBigIntTooBig(isolate);
        ulong expValue = exponent._digits[0];
        if (expValue == 1) return @base;
        if (expValue >= kMaxBits) ThrowBigIntTooBig(isolate);
        uint n = (uint)expValue;
        if (@base.Length == 1 && @base._digits[0] == 2)
        {
            // Fast path for 2^n.
            int neededDigits = 1 + (int)(n / kDigitBits);
            BigInt r = MutableBigInt.New(isolate, neededDigits);
            // All bits are zero. Now set the n-th bit.
            ulong msd = 1UL << (int)(n % kDigitBits);
            MutableBigInt.SetDigit(r, neededDigits - 1, msd);
            // Result is negative for odd powers of -2n.
            if (@base.Sign) MutableBigInt.SetSign(r, (n & 1) != 0);
            return MutableBigInt.MakeImmutable(r);
        }
        BigInt? result = null;
        BigInt runningSquare = @base;
        // This implicitly sets the result's sign correctly.
        if ((n & 1) != 0) result = @base;
        n >>= 1;
        for (; n != 0; n >>= 1)
        {
            runningSquare = Multiply(isolate, runningSquare, runningSquare);
            if ((n & 1) != 0)
            {
                result = result is null ? runningSquare : Multiply(isolate, result, runningSquare);
            }
        }
        return result!;
    }

    /// <summary>BigInt::Multiply.</summary>
    public static BigInt Multiply(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero) return x;
        if (y.IsZero) return y;
        int resultLength = Bigint.MultiplyResultLength(x.Digits, y.Digits);
        BigInt result = MutableBigInt.New(isolate, resultLength);
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(result);
        bool resultSign = x.Sign != y.Sign;
        (bool success, ulong topDigit) = Bigint.MultiplySmall(Z, X, Y);
        if (success)
        {
            MutableBigInt.SetSign(result, resultSign);
            return MutableBigInt.MakeImmutable(result, Z.Length, topDigit);
        }
        Status status = GetProcessor(isolate).MultiplyLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        MutableBigInt.SetSign(result, resultSign);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::Divide.</summary>
    public static BigInt Divide(Isolate isolate, BigInt x, BigInt y)
    {
        // 1. If y is 0n, throw a RangeError exception.
        if (y.IsZero) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // 2. Let quotient be the mathematical value of x divided by y.
        // 3. Return a BigInt representing quotient rounded towards 0 to the next
        //    integral value.
        if (Bigint.Compare(x.Digits, y.Digits) < 0) return s_zero;
        bool resultSign = x.Sign != y.Sign;
        if (y.Length == 1 && y._digits[0] == 1)
        {
            return resultSign == x.Sign ? x : UnaryMinus(isolate, x);
        }
        int resultLength = Bigint.DivideResultLength(x.Digits, y.Digits);
        BigInt quotient = MutableBigInt.New(isolate, resultLength);
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(quotient);
        (bool success, ulong topDigit) = Bigint.DivideSmall(Z, X, Y);
        if (success)
        {
            MutableBigInt.SetSign(quotient, resultSign);
            return MutableBigInt.MakeImmutable(quotient, Z.Length, topDigit);
        }
        Status status = GetProcessor(isolate).DivideLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        MutableBigInt.SetSign(quotient, resultSign);
        return MutableBigInt.MakeImmutable(quotient);
    }

    /// <summary>BigInt::Remainder.</summary>
    public static BigInt Remainder(Isolate isolate, BigInt x, BigInt y)
    {
        // 1. If y is 0n, throw a RangeError exception.
        if (y.IsZero) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // 2. Return the BigInt representing x modulo y.
        // See https://github.com/tc39/proposal-bigint/issues/84 though.
        if (Bigint.Compare(x.Digits, y.Digits) < 0) return x;
        if (y.Length == 1 && y._digits[0] == 1) return s_zero;
        int resultLength = Bigint.ModuloResultLength(y.Digits);
        BigInt remainder = MutableBigInt.New(isolate, resultLength);
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(remainder);
        (bool success, ulong topDigit) = Bigint.ModuloSmall(Z, X, Y);
        if (success)
        {
            MutableBigInt.SetSign(remainder, x.Sign);
            return MutableBigInt.MakeImmutable(remainder, Z.Length, topDigit);
        }
        Status status = GetProcessor(isolate).ModuloLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        MutableBigInt.SetSign(remainder, x.Sign);
        return MutableBigInt.MakeImmutable(remainder);
    }

    /// <summary>BigInt::Add.</summary>
    public static BigInt Add(Isolate isolate, BigInt x, BigInt y)
    {
        if (x.IsZero) return y;
        if (y.IsZero) return x;
        bool xsign = x.Sign;
        bool ysign = y.Sign;
        int resultLength = Bigint.AddSignedResultLength(x.Length, y.Length, xsign == ysign);
        // Allocation fails when {result_length} exceeds the max BigInt size.
        BigInt result = MutableBigInt.New(isolate, resultLength);
        bool resultSign = Bigint.AddSigned(MutableBigInt.RwDigits(result), x.Digits, xsign, y.Digits, ysign);
        MutableBigInt.SetSign(result, resultSign);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::Subtract.</summary>
    public static BigInt Subtract(Isolate isolate, BigInt x, BigInt y)
    {
        if (y.IsZero) return x;
        if (x.IsZero) return UnaryMinus(isolate, y);
        bool xsign = x.Sign;
        bool ysign = y.Sign;
        int resultLength = Bigint.SubtractSignedResultLength(x.Length, y.Length, xsign == ysign);
        BigInt result = MutableBigInt.New(isolate, resultLength);
        bool resultSign = Bigint.SubtractSigned(MutableBigInt.RwDigits(result), x.Digits, xsign, y.Digits, ysign);
        MutableBigInt.SetSign(result, resultSign);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::Increment.</summary>
    public static BigInt Increment(Isolate isolate, BigInt x)
    {
        if (x.Sign)
        {
            BigInt result = AbsoluteSubOne(x);
            MutableBigInt.SetSign(result, true);
            return MutableBigInt.MakeImmutable(result);
        }
        return MutableBigInt.MakeImmutable(AbsoluteAddOne(isolate, x, false));
    }

    /// <summary>BigInt::Decrement.</summary>
    public static BigInt Decrement(Isolate isolate, BigInt x)
    {
        BigInt result;
        if (x.Sign)
        {
            result = AbsoluteAddOne(isolate, x, true);
        }
        else if (x.IsZero)
        {
            return FromInt(isolate, -1);
        }
        else
        {
            result = AbsoluteSubOne(x);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>
    /// MutableBigInt::AbsoluteAddOne: adds 1 to the absolute value of {x} and
    /// sets the result's sign to {sign}.
    /// </summary>
    static BigInt AbsoluteAddOne(Isolate isolate, BigInt x, bool sign)
    {
        int inputLength = x.Length;
        // The addition will overflow into a new digit if all existing digits are
        // at maximum.
        bool willOverflow = true;
        for (int i = 0; i < inputLength; i++)
        {
            if (!Bigint.digit_ismax(x._digits[i]))
            {
                willOverflow = false;
                break;
            }
        }
        int resultLength = inputLength + (willOverflow ? 1 : 0);
        BigInt result = MutableBigInt.New(isolate, resultLength);
        if (inputLength == 0)
        {
            MutableBigInt.SetDigit(result, 0, 1);
        }
        else if (inputLength == 1 && !willOverflow)
        {
            MutableBigInt.SetDigit(result, 0, x._digits[0] + 1);
        }
        else
        {
            Bigint.AddOne(MutableBigInt.RwDigits(result), x.Digits);
        }
        MutableBigInt.SetSign(result, sign);
        return result;
    }

    /// <summary>MutableBigInt::AbsoluteSubOne: {x} must not be zero.</summary>
    static BigInt AbsoluteSubOne(BigInt x)
    {
        Debug.Assert(!x.IsZero);
        int length = x.Length;
        BigInt result = MutableBigInt.NewUnchecked(length);
        if (length == 1)
        {
            MutableBigInt.SetDigit(result, 0, x._digits[0] - 1);
        }
        else
        {
            Bigint.SubtractOne(MutableBigInt.RwDigits(result), x.Digits);
        }
        return result;
    }

    /// <summary>BigInt::AsIntN.</summary>
    public static BigInt AsIntN(Isolate isolate, ulong n, BigInt x)
    {
        if (x.IsZero || n > kMaxBits) return x;
        if (n == 0) return s_zero;
        int neededLength = Bigint.AsIntNResultLength(x.Digits, x.Sign, (int)n);
        if (neededLength < 0) return x;
        BigInt result = MutableBigInt.NewUnchecked(neededLength);
        bool negative = Bigint.AsIntN(MutableBigInt.RwDigits(result), x.Digits, x.Sign, (int)n);
        MutableBigInt.SetSign(result, negative);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigInt::AsUintN.</summary>
    public static BigInt AsUintN(Isolate isolate, ulong n, BigInt x)
    {
        if (x.IsZero) return x;
        if (n == 0) return s_zero;
        BigInt result;
        if (x.Sign)
        {
            if (n > kMaxBits) ThrowBigIntTooBig(isolate);
            int resultLength = Bigint.AsUintN_Neg_ResultLength((int)n);
            result = MutableBigInt.NewUnchecked(resultLength);
            Bigint.AsUintN_Neg(MutableBigInt.RwDigits(result), x.Digits, (int)n);
        }
        else
        {
            if (n >= kMaxBits) return x;
            int resultLength = Bigint.AsUintN_Pos_ResultLength(x.Digits, (int)n);
            if (resultLength < 0) return x;
            result = MutableBigInt.NewUnchecked(resultLength);
            Bigint.AsUintN_Pos(MutableBigInt.RwDigits(result), x.Digits, (int)n);
        }
        Debug.Assert(!result.Sign);
        return MutableBigInt.MakeImmutable(result);
    }

    // ---------------------------------------------------------------------
    // builtins-bigint.tq: the operator bodies (BigInt*Impl macros). Each
    // throws V8's exception where the Torque builtin jumps to its label.

    const int kPositiveSign = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static BigInt AllocateEmptyBigInt(Isolate isolate, bool sign, int length)
    {
        BigInt result = MutableBigInt.New(isolate, length);
        MutableBigInt.SetSign(result, sign);
        return result;
    }

    /// <summary>BigIntCompareAbsolute: r &lt; 0 if |x| &lt; |y|; r &gt; 0 if |x| &gt; |y|; 0 if equal.</summary>
    static int BigIntCompareAbsolute(BigInt x, BigInt y)
    {
        int diff = x.Length - y.Length;
        if (diff != 0) return diff;
        for (int i = x.Length - 1; i >= 0; --i)
        {
            ulong xdigit = x._digits[i], ydigit = y._digits[i];
            if (xdigit != ydigit) return xdigit > ydigit ? 1 : -1;
        }
        return 0;
    }

    static BigInt MutableBigIntAbsoluteSub(Isolate isolate, BigInt x, BigInt y, bool resultSign)
    {
        Debug.Assert(BigIntCompareAbsolute(x, y) >= 0);
        if (x.Length == 0) return x;
        if (y.Length == 0) return resultSign == x.Sign ? x : UnaryMinus(isolate, x);
        BigInt result = AllocateEmptyBigInt(isolate, resultSign, x.Length);
        // MutableBigInt_AbsoluteSubAndCanonicalize.
        Span<ulong> r = MutableBigInt.RwDigits(result);
        ulong topDigit = Bigint.Subtract(r, x.Digits, y.Digits);
        return MutableBigInt.MakeImmutable(result, r.Length, topDigit);
    }

    static BigInt MutableBigIntAbsoluteAdd(Isolate isolate, BigInt x, BigInt y, bool resultSign)
    {
        if (x.Length < y.Length) (x, y) = (y, x);  // Swap x and y so that x is longer.
        // case: 0n + 0n
        if (x.Length == 0) return x;
        // case: x + 0n
        if (y.Length == 0) return resultSign == x.Sign ? x : UnaryMinus(isolate, x);
        // case: x + y
        BigInt result = AllocateEmptyBigInt(isolate, resultSign, x.Length + 1);
        // MutableBigInt_AbsoluteAddAndCanonicalize.
        Span<ulong> r = MutableBigInt.RwDigits(result);
        ulong topDigit = Bigint.Add(r, x.Digits, y.Digits);
        return MutableBigInt.MakeImmutable(result, r.Length, topDigit);
    }

    /// <summary>BigIntAddImpl (builtins-bigint.tq).</summary>
    public static BigInt AddImpl(Isolate isolate, BigInt x, BigInt y)
    {
        bool xsign = x.Sign;
        // x + y == x + y; -x + -y == -(x + y)
        if (xsign == y.Sign) return MutableBigIntAbsoluteAdd(isolate, x, y, xsign);
        // x + -y == x - y == -(y - x); -x + y == y - x == -(x - y)
        if (BigIntCompareAbsolute(x, y) >= 0) return MutableBigIntAbsoluteSub(isolate, x, y, xsign);
        return MutableBigIntAbsoluteSub(isolate, y, x, !xsign);
    }

    /// <summary>BigIntSubtractImpl (builtins-bigint.tq).</summary>
    public static BigInt SubtractImpl(Isolate isolate, BigInt x, BigInt y)
    {
        bool xsign = x.Sign;
        // x - (-y) == x + y; (-x) - y == -(x + y)
        if (xsign != y.Sign) return MutableBigIntAbsoluteAdd(isolate, x, y, xsign);
        // x - y == -(y - x); (-x) - (-y) == y - x == -(x - y)
        if (BigIntCompareAbsolute(x, y) >= 0) return MutableBigIntAbsoluteSub(isolate, x, y, xsign);
        return MutableBigIntAbsoluteSub(isolate, y, x, !xsign);
    }

    /// <summary>BigIntMultiplyImpl (builtins-bigint.tq).</summary>
    public static BigInt MultiplyImpl(Isolate isolate, BigInt x, BigInt y)
    {
        // case: 0n * y
        if (x.Length == 0) return x;
        // case: x * 0n
        if (y.Length == 0) return y;
        BigInt result = AllocateEmptyBigInt(isolate, x.Sign != y.Sign, x.Length + y.Length);
        // Ordering the bigger input first is faster to do here than on the C++ side.
        if (x.Length < y.Length) (x, y) = (y, x);
        // MutableBigInt_AbsoluteMulAndCanonicalize.
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(result);
        (bool success, ulong topDigit) = Bigint.MultiplySmall(Z, X, Y);
        if (success) return MutableBigInt.MakeImmutable(result, Z.Length, topDigit);
        Status status = GetProcessor(isolate).MultiplyLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntDivideImpl (builtins-bigint.tq).</summary>
    public static BigInt DivideImpl(Isolate isolate, BigInt x, BigInt y)
    {
        // case: x / 0n
        if (y.Length == 0) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // case: x / y, where x < y
        if (BigIntCompareAbsolute(x, y) < 0) return s_zero;
        // case: x / 1n
        bool resultSign = x.Sign != y.Sign;
        if (y.Length == 1 && y._digits[0] == 1) return resultSign == x.Sign ? x : UnaryMinus(isolate, x);
        // case: x / y
        int resultLength = x.Length - y.Length + 1;
        // This implies a *very* conservative estimate that kBarrettThreshold > 10.
        if (y.Length > 10) resultLength++;
        BigInt result = AllocateEmptyBigInt(isolate, resultSign, resultLength);
        // MutableBigInt_AbsoluteDivAndCanonicalize.
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(result);
        (bool success, ulong topDigit) = Bigint.DivideSmall(Z, X, Y);
        if (success) return MutableBigInt.MakeImmutable(result, Z.Length, topDigit);
        Status status = GetProcessor(isolate).DivideLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntModulusImpl (builtins-bigint.tq).</summary>
    public static BigInt ModulusImpl(Isolate isolate, BigInt x, BigInt y)
    {
        // case: x % 0n
        if (y.Length == 0) isolate.ThrowRangeError(MessageTemplate.BigIntDivZero);
        // case: x % y, where x < y
        if (BigIntCompareAbsolute(x, y) < 0) return x;
        // case: x % 1n or x % -1n
        if (y.Length == 1 && y._digits[0] == 1) return s_zero;
        // case: x % y
        BigInt result = AllocateEmptyBigInt(isolate, x.Sign, y.Length);
        // MutableBigInt_AbsoluteModAndCanonicalize (without the cached-divisor
        // fast path, which needs the heap's cached_bigint_divisor roots).
        ReadOnlySpan<ulong> X = x.Digits, Y = y.Digits;
        Span<ulong> Z = MutableBigInt.RwDigits(result);
        (bool success, ulong topDigit) = Bigint.ModuloSmall(Z, X, Y);
        if (success) return MutableBigInt.MakeImmutable(result, Z.Length, topDigit);
        Status status = GetProcessor(isolate).ModuloLarge(Z, X, Y);
        if (status == Status.kInterrupted) return Terminate(isolate);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntBitwiseAndImpl (builtins-bigint.tq).</summary>
    public static BigInt BitwiseAnd(Isolate isolate, BigInt x, BigInt y)
    {
        int xlength = x.Length, ylength = y.Length;
        // case: 0n & y
        if (xlength == 0) return x;
        // case: x & 0n
        if (ylength == 0) return y;
        BigInt result;
        if (!x.Sign && !y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, false, Math.Min(xlength, ylength));
            Bigint.BitwiseAnd_PosPos(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (x.Sign && y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, true, Math.Max(xlength, ylength) + 1);
            Bigint.BitwiseAnd_NegNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (!x.Sign)
        {
            result = AllocateEmptyBigInt(isolate, false, xlength);
            Bigint.BitwiseAnd_PosNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else
        {
            result = AllocateEmptyBigInt(isolate, false, ylength);
            Bigint.BitwiseAnd_PosNeg(MutableBigInt.RwDigits(result), y.Digits, x.Digits);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntBitwiseOrImpl (builtins-bigint.tq).</summary>
    public static BigInt BitwiseOr(Isolate isolate, BigInt x, BigInt y)
    {
        int xlength = x.Length, ylength = y.Length;
        // case: 0n | y
        if (xlength == 0) return y;
        // case: x | 0n
        if (ylength == 0) return x;
        int resultLength = Math.Max(xlength, ylength);
        BigInt result;
        if (!x.Sign && !y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, false, resultLength);
            Bigint.BitwiseOr_PosPos(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (x.Sign && y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, true, resultLength);
            Bigint.BitwiseOr_NegNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (!x.Sign)
        {
            result = AllocateEmptyBigInt(isolate, true, resultLength);
            Bigint.BitwiseOr_PosNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else
        {
            result = AllocateEmptyBigInt(isolate, true, resultLength);
            Bigint.BitwiseOr_PosNeg(MutableBigInt.RwDigits(result), y.Digits, x.Digits);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntBitwiseXorImpl (builtins-bigint.tq).</summary>
    public static BigInt BitwiseXor(Isolate isolate, BigInt x, BigInt y)
    {
        int xlength = x.Length, ylength = y.Length;
        // case: 0n ^ y
        if (xlength == 0) return y;
        // case: x ^ 0n
        if (ylength == 0) return x;
        BigInt result;
        if (!x.Sign && !y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, false, Math.Max(xlength, ylength));
            Bigint.BitwiseXor_PosPos(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (x.Sign && y.Sign)
        {
            result = AllocateEmptyBigInt(isolate, false, Math.Max(xlength, ylength));
            Bigint.BitwiseXor_NegNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else if (!x.Sign)
        {
            result = AllocateEmptyBigInt(isolate, true, Math.Max(xlength, ylength) + 1);
            Bigint.BitwiseXor_PosNeg(MutableBigInt.RwDigits(result), x.Digits, y.Digits);
        }
        else
        {
            result = AllocateEmptyBigInt(isolate, true, Math.Max(xlength, ylength) + 1);
            Bigint.BitwiseXor_PosNeg(MutableBigInt.RwDigits(result), y.Digits, x.Digits);
        }
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>MutableBigIntLeftShiftByAbsolute (builtins-bigint.tq).</summary>
    static BigInt LeftShiftByAbsolute(Isolate isolate, BigInt x, BigInt y)
    {
        int xlength = x.Length, ylength = y.Length;
        // case: 0n << y
        if (xlength == 0) return x;
        // case: x << 0n
        if (ylength == 0) return x;
        // Depends on kBigIntMaxBits <= (1 << kBigIntDigitSize).
        if (ylength > 1) ThrowBigIntTooBig(isolate);
        ulong shiftAbs = y._digits[0];
        if (shiftAbs > kMaxBits) ThrowBigIntTooBig(isolate);

        // {shift} is positive.
        long shift = (long)shiftAbs;
        long resultLength = xlength + shift / kDigitBits;
        int bitsShift = (int)(shift % kDigitBits);
        ulong xmsd = x._digits[xlength - 1];
        if (bitsShift != 0 && (xmsd >> (kDigitBits - bitsShift)) != 0) resultLength++;
        if (resultLength > kMaxLength) ThrowBigIntTooBig(isolate);
        BigInt result = AllocateEmptyBigInt(isolate, x.Sign, (int)resultLength);
        // MutableBigInt_LeftShiftAndCanonicalize.
        Bigint.LeftShift(MutableBigInt.RwDigits(result), x.Digits, (ulong)shift);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>RightShiftByMaximum (builtins-bigint.tq).</summary>
    static BigInt RightShiftByMaximum(Isolate isolate, bool sign) => sign ? FromInt(isolate, -1) : s_zero;

    /// <summary>MutableBigIntRightShiftByAbsolute (builtins-bigint.tq).</summary>
    static BigInt RightShiftByAbsolute(Isolate isolate, BigInt x, BigInt y)
    {
        int xlength = x.Length, ylength = y.Length;
        // case: 0n >> y
        if (xlength == 0) return x;
        // case: x >> 0n
        if (ylength == 0) return x;
        bool sign = x.Sign;
        // Depends on kBigIntMaxBits <= (1 << kBigIntDigitSize).
        if (ylength > 1) return RightShiftByMaximum(isolate, sign);
        ulong shiftAbs = y._digits[0];
        if (shiftAbs > kMaxBits) return RightShiftByMaximum(isolate, sign);

        // {shift} is positive.
        int resultLength = Bigint.RightShift_ResultLength(x.Digits, sign, shiftAbs, out RightShiftState state);
        if (resultLength == 0) return RightShiftByMaximum(isolate, sign);
        BigInt result = AllocateEmptyBigInt(isolate, sign, resultLength);
        // MutableBigInt_RightShiftAndCanonicalize.
        Bigint.RightShift(MutableBigInt.RwDigits(result), x.Digits, shiftAbs, in state);
        return MutableBigInt.MakeImmutable(result);
    }

    /// <summary>BigIntShiftLeftImpl (builtins-bigint.tq).</summary>
    public static BigInt LeftShift(Isolate isolate, BigInt x, BigInt y) =>
        y.Sign ? RightShiftByAbsolute(isolate, x, y) : LeftShiftByAbsolute(isolate, x, y);

    /// <summary>BigIntShiftRightImpl (builtins-bigint.tq).</summary>
    public static BigInt SignedRightShift(Isolate isolate, BigInt x, BigInt y) =>
        y.Sign ? LeftShiftByAbsolute(isolate, x, y) : RightShiftByAbsolute(isolate, x, y);

    /// <summary>BigInt >>> BigInt: always a TypeError (kBigIntShr).</summary>
    public static BigInt UnsignedRightShift(Isolate isolate, BigInt x, BigInt y)
    {
        isolate.ThrowTypeError(MessageTemplate.BigIntShr);
        return null!;
    }

    /// <summary>BigIntUnaryMinus (builtins-bigint.tq).</summary>
    public static BigInt UnaryMinusBuiltin(Isolate isolate, BigInt bigint)
    {
        // There is no -0n.
        if (bigint.Length == 0) return bigint;
        BigInt result = AllocateEmptyBigInt(isolate, !bigint.Sign, bigint.Length);
        bigint.Digits.CopyTo(MutableBigInt.RwDigits(result));
        return result;
    }

    // ---------------------------------------------------------------------
    // Comparison.

    static ComparisonResult UnequalSign(bool leftNegative) =>
        leftNegative ? ComparisonResult.LessThan : ComparisonResult.GreaterThan;

    static ComparisonResult AbsoluteGreater(bool bothNegative) =>
        bothNegative ? ComparisonResult.LessThan : ComparisonResult.GreaterThan;

    static ComparisonResult AbsoluteLess(bool bothNegative) =>
        bothNegative ? ComparisonResult.GreaterThan : ComparisonResult.LessThan;

    /// <summary>BigInt::CompareToBigInt (never returns Undefined).</summary>
    public static ComparisonResult CompareToBigInt(BigInt x, BigInt y)
    {
        bool xSign = x.Sign;
        if (xSign != y.Sign) return UnequalSign(xSign);
        int result = Bigint.Compare(x.Digits, y.Digits);
        if (result > 0) return AbsoluteGreater(xSign);
        if (result < 0) return AbsoluteLess(xSign);
        return ComparisonResult.Equal;
    }

    /// <summary>BigInt::EqualToBigInt.</summary>
    public static bool EqualToBigInt(BigInt x, BigInt y)
    {
        if (x.Sign != y.Sign) return false;
        if (x.Length != y.Length) return false;
        return x.Digits.SequenceEqual(y.Digits);
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
        // a. If x or y are any of NaN, +∞, or -∞, return false.
        // b. If the mathematical value of x is equal to the mathematical value of y,
        //    return true, otherwise return false.
        if (y.IsSmi)
        {
            int value = (int)y.Number;
            if (value == 0) return x.IsZero;
            // Any multi-digit BigInt is bigger than a Smi.
            return x.Length == 1 && x.Sign == (value < 0) && x._digits[0] == (ulong)Math.Abs((long)value);
        }
        return CompareToDouble(x, y.Number) == ComparisonResult.Equal;
    }

    /// <summary>BigInt::CompareToNumber.</summary>
    public static ComparisonResult CompareToNumber(BigInt x, JSValue y)
    {
        Debug.Assert(y.IsNumber);
        if (y.IsSmi)
        {
            bool xSign = x.Sign;
            int yValue = (int)y.Number;
            bool ySign = yValue < 0;
            if (xSign != ySign) return UnequalSign(xSign);

            if (x.IsZero)
            {
                Debug.Assert(!ySign);
                return yValue == 0 ? ComparisonResult.Equal : ComparisonResult.LessThan;
            }
            // Any multi-digit BigInt is bigger than a Smi.
            if (x.Length > 1) return AbsoluteGreater(xSign);

            ulong absValue = (ulong)Math.Abs((long)yValue);
            ulong xDigit = x._digits[0];
            if (xDigit > absValue) return AbsoluteGreater(xSign);
            if (xDigit < absValue) return AbsoluteLess(xSign);
            return ComparisonResult.Equal;
        }
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
            Debug.Assert(!xSign);
            return x.IsZero ? ComparisonResult.Equal : ComparisonResult.GreaterThan;
        }
        if (x.IsZero)
        {
            Debug.Assert(!ySign);
            return ComparisonResult.LessThan;
        }
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(y);
        int rawExponent = (int)(doubleBits >> Double.kPhysicalSignificandSize) & 0x7FF;
        ulong mantissa = doubleBits & Double.kSignificandMask;
        // Non-finite doubles are handled above.
        Debug.Assert(rawExponent != 0x7FF);
        int exponent = rawExponent - 0x3FF;
        if (exponent < 0)
        {
            // The absolute value of the double is less than 1. Only 0n has an
            // absolute value smaller than that, but we've already covered that case.
            return AbsoluteGreater(xSign);
        }
        int xLength = x.Length;
        ulong xMsd = x._digits[xLength - 1];
        int msdLeadingZeros = System.Numerics.BitOperations.LeadingZeroCount(xMsd);
        long xBitlength = (long)xLength * kDigitBits - msdLeadingZeros;
        long yBitlength = exponent + 1;
        if (xBitlength < yBitlength) return AbsoluteLess(xSign);
        if (xBitlength > yBitlength) return AbsoluteGreater(xSign);

        // At this point, we know that signs and bit lengths (i.e. position of
        // the most significant bit in exponent-free representation) are identical.
        // {x} is not zero, {y} is finite and not denormal.
        // Now we virtually convert the double to an integer by shifting its
        // mantissa according to its exponent, so it will align with the BigInt {x},
        // and then we compare them bit for bit until we find a difference or the
        // least significant bit.
        //                    <----- 52 ------> <-- virtual trailing zeroes -->
        // y / mantissa:     1yyyyyyyyyyyyyyyyy 0000000000000000000000000000000
        // x / digits:    0001xxxx xxxxxxxx xxxxxxxx ...
        //                    <-->          <------>
        //              msd_topbit         kDigitBits
        //
        mantissa |= Double.kHiddenBit;
        const int kMantissaTopBit = 52;  // 0-indexed.
        // 0-indexed position of {x}'s most significant bit within the {msd}.
        int msdTopbit = kDigitBits - 1 - msdLeadingZeros;
        // Shifted chunk of {mantissa} for comparing with {digit}.
        ulong compareMantissa;
        // Number of unprocessed bits in {mantissa}. We'll keep them shifted to
        // the left (i.e. most significant part) of the underlying uint64_t.
        int remainingMantissaBits = 0;

        // First, compare the most significant digit against the beginning of
        // the mantissa.
        if (msdTopbit < kMantissaTopBit)
        {
            remainingMantissaBits = kMantissaTopBit - msdTopbit;
            compareMantissa = mantissa >> remainingMantissaBits;
            mantissa <<= 64 - remainingMantissaBits;
        }
        else
        {
            compareMantissa = mantissa << (msdTopbit - kMantissaTopBit);
            mantissa = 0;
        }
        if (xMsd > compareMantissa) return AbsoluteGreater(xSign);
        if (xMsd < compareMantissa) return AbsoluteLess(xSign);

        // Then, compare additional digits against any remaining mantissa bits.
        for (int digitIndex = xLength - 2; digitIndex >= 0; digitIndex--)
        {
            if (remainingMantissaBits > 0)
            {
                remainingMantissaBits -= kDigitBits;
                compareMantissa = mantissa;
                mantissa = 0;
            }
            else
            {
                compareMantissa = 0;
            }
            ulong digit = x._digits[digitIndex];
            if (digit > compareMantissa) return AbsoluteGreater(xSign);
            if (digit < compareMantissa) return AbsoluteLess(xSign);
        }

        // Integer parts are equal; check whether {y} has a fractional part.
        if (mantissa != 0)
        {
            Debug.Assert(remainingMantissaBits > 0);
            return AbsoluteLess(xSign);
        }
        return ComparisonResult.Equal;
    }

    /// <summary>BigInt::Hash (bigint.h).</summary>
    public static uint Hash(BigInt bigint) =>
        Hashing.Hash32((uint)bigint.Length | (bigint.Sign ? 1u << 30 : 0u)) ^
        (uint)Hashing.Hash64(bigint.IsZero ? 0 : bigint._digits[0]);
}
