// Port of src/bigint/tostring.cc.
// The output is written right-to-left into a char span, as V8 writes into a
// char buffer; "out" is an index into that span.

namespace V8Sharp.Base.BigInts;

public static partial class Bigint
{
    // Lookup table for the maximum number of bits required per character of a
    // base-N string representation of a number. To increase accuracy, the array
    // value is the actual value multiplied by 32. To generate this table:
    // for (var i = 0; i <= 36; i++) { print(Math.ceil(Math.log2(i) * 32) + ","); }
    internal static ReadOnlySpan<byte> kMaxBitsPerChar => [
        0, 0, 32, 51, 64, 75, 83, 90, 96,          // 0..8
        102, 107, 111, 115, 119, 122, 126, 128,    // 9..16
        131, 134, 136, 139, 141, 143, 145, 147,    // 17..24
        149, 151, 153, 154, 156, 158, 159, 160,    // 25..32
        162, 163, 165, 166,                        // 33..36
    ];

    internal const int kBitsPerCharTableShift = 5;
    internal const int kBitsPerCharTableMultiplier = 1 << kBitsPerCharTableShift;

    internal const string kConversionChars = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>In DEBUG builds, the result of ToString is initialized to this value.</summary>
    public const char kStringZapValue = '?';

    /// <summary>Raises base to the power of exponent. Does not check for overflow.</summary>
    internal static ulong digit_pow(ulong @base, ulong exponent)
    {
        ulong result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) != 0) result *= @base;
            exponent >>= 1;
            @base *= @base;
        }
        return result;
    }

    /// <summary>The (maximum) number of characters X needs in the given radix.</summary>
    public static int ToStringResultLength(ReadOnlySpan<ulong> X, int radix, bool sign)
    {
        int bitLength = BitLength(X);
        int result;
        if (IsPowerOfTwo(radix))
        {
            int bitsPerChar = CountTrailingZeros((uint)radix);
            result = DIV_CEIL(bitLength, bitsPerChar) + (sign ? 1 : 0);
        }
        else
        {
            // Maximum number of bits we can represent with one character.
            byte maxBitsPerChar = kMaxBitsPerChar[radix];
            // For estimating the result length, we have to be pessimistic and work with
            // the minimum number of bits one character can represent.
            byte minBitsPerChar = (byte)(maxBitsPerChar - 1);
            // Perform the following computation with uint64_t to avoid overflows.
            ulong charsRequired = (ulong)bitLength;
            charsRequired *= kBitsPerCharTableMultiplier;
            charsRequired = (charsRequired - 1) / minBitsPerChar + 1;
            Debug.Assert(charsRequired < uint.MaxValue);
            result = (int)charsRequired;
        }
        result += sign ? 1 : 0;
        return result;
    }
}

// ToStringFormatter and RecursionLevel.
sealed class ToStringFormatter
{
    readonly ulong[] _digits;  // Normalized copy-free view is not possible on the heap; see ctor.
    readonly int _digitsLen;
    readonly int _radix;
    int _chunkChars;
    readonly bool _sign;
    readonly char[] _outBuf;
    readonly int _outStart;
    readonly int _outEnd;
    int _out;
    ulong _chunkDivisor;
    readonly Processor _processor;

    public ToStringFormatter(ulong[] digits, int digitsLen, int radix, bool sign, char[] outBuf, int outStart, int charsAvailable, Processor processor)
    {
        _digits = digits;
        _digitsLen = digitsLen;
        _radix = radix;
        _sign = sign;
        _outBuf = outBuf;
        _outStart = outStart;
        _outEnd = outStart + charsAvailable;
        _out = _outEnd;
        _processor = processor;
    }

    ReadOnlySpan<ulong> Digits => _digits.AsSpan(0, _digitsLen);

    // Prepares data for {Classic}. Not needed for {BasePowerOfTwo}.
    public void Start()
    {
        int maxBitsPerChar = Bigint.kMaxBitsPerChar[_radix];
        _chunkChars = Bigint.kDigitBits * Bigint.kBitsPerCharTableMultiplier / maxBitsPerChar;
        _chunkDivisor = Bigint.digit_pow((ulong)_radix, (ulong)_chunkChars);
        // By construction of chunk_chars_, there can't have been overflow.
        Debug.Assert(_chunkDivisor != 0);
    }

    public int Finish()
    {
        Debug.Assert(_out >= _outStart);
        Debug.Assert(_out < _outEnd);  // At least one character was written.
        while (_out < _outEnd && _outBuf[_out] == '0') _out++;
        if (_sign) _outBuf[--_out] = '-';
        int excess = 0;
        if (_out > _outStart)
        {
            int actualLength = _outEnd - _out;
            excess = _out - _outStart;
            Array.Copy(_outBuf, _out, _outBuf, _outStart, actualLength);
        }
        return excess;
    }

    // A variant of BasecaseLast, specialized for radix 10 in V8
    // (BasecaseFixedLast<10>); the division by a constant is the same here.
    int BasecaseLast(ulong digit, int @out)
    {
        if (_radix == 10)
        {
            while (digit != 0)
            {
                _outBuf[--@out] = (char)('0' + (digit % 10));
                digit /= 10;
            }
            return @out;
        }
        do
        {
            _outBuf[--@out] = Bigint.kConversionChars[(int)(digit % (ulong)_radix)];
            digit /= (ulong)_radix;
        } while (digit > 0);
        return @out;
    }

    // When processing a middle (non-most significant) digit, always write the
    // same number of characters (as many '0' as necessary).
    int BasecaseMiddle(ulong digit, int @out)
    {
        for (int i = 0; i < _chunkChars; i++)
        {
            _outBuf[--@out] = Bigint.kConversionChars[(int)(digit % (ulong)_radix)];
            digit /= (ulong)_radix;
        }
        Debug.Assert(digit == 0);
        return @out;
    }

    // DivideByMagic<10>: divides by the radix-10 chunk divisor on half digits
    // and writes out the resulting chunk.
    int DivideByMagic10(Span<ulong> rest, ReadOnlySpan<ulong> input, int output)
    {
        const int maxBitsPerChar = 107;  // kMaxBitsPerChar[10]
        const int chunkChars = Bigint.kHalfDigitBits * Bigint.kBitsPerCharTableMultiplier / maxBitsPerChar;
        const ulong chunkDivisor = 1000000000;  // 10^chunkChars
        Debug.Assert(chunkChars == 9);
        ulong remainder = 0;
        for (int i = input.Length; i-- > 0;)
        {
            ulong d = input[i];
            ulong upper = (remainder << Bigint.kHalfDigitBits) | (d >> Bigint.kHalfDigitBits);
            ulong uResult = upper / chunkDivisor;
            remainder = upper % chunkDivisor;
            ulong lower = (remainder << Bigint.kHalfDigitBits) | (d & Bigint.kHalfDigitMask);
            ulong lResult = lower / chunkDivisor;
            remainder = lower % chunkDivisor;
            rest[i] = (uResult << Bigint.kHalfDigitBits) | lResult;
        }
        // {remainder} is now the current chunk to be written out.
        for (int i = 0; i < chunkChars; i++)
        {
            _outBuf[--output] = (char)('0' + (remainder % 10));
            remainder /= 10;
        }
        Debug.Assert(remainder == 0);
        return output;
    }

    public void Classic()
    {
        ReadOnlySpan<ulong> digits = Digits;
        if (digits.Length == 0)
        {
            _outBuf[--_out] = '0';
            return;
        }
        if (digits.Length == 1)
        {
            _out = BasecaseLast(digits[0], _out);
            return;
        }
        // {rest} holds the part of the BigInt that we haven't looked at yet.
        // Not to be confused with "remainder"!
        ulong[] restStorage = new ulong[digits.Length];
        Span<ulong> rest = restStorage;
        // In the first round, divide the input, allocating a new BigInt for
        // the result == rest; from then on divide the rest in-place.
        ReadOnlySpan<ulong> dividend = digits;
        do
        {
            if (_radix == 10)
            {
                // Faster but costs binary size, so we optimize the most common case.
                _out = DivideByMagic10(rest, dividend, _out);
            }
            else
            {
                Bigint.DivideSingle(rest, out ulong chunk, dividend, _chunkDivisor);
                _out = BasecaseMiddle(chunk, _out);
            }
            rest = Bigint.Normalize(rest);
            dividend = rest;
        } while (rest.Length > 1);
        _out = BasecaseLast(rest[0], _out);
    }

    public void BasePowerOfTwo()
    {
        ReadOnlySpan<ulong> digits = Digits;
        int bitsPerChar = Bigint.CountTrailingZeros((uint)_radix);
        int charMask = _radix - 1;
        ulong digit = 0;
        // Keeps track of how many unprocessed bits there are in {digit}.
        int availableBits = 0;
        for (int i = 0; i < digits.Length - 1; i++)
        {
            ulong newDigit = digits[i];
            // Take any leftover bits from the last iteration into account.
            int current = (int)((digit | (newDigit << availableBits)) & (ulong)charMask);
            _outBuf[--_out] = Bigint.kConversionChars[current];
            int consumedBits = bitsPerChar - availableBits;
            digit = newDigit >> consumedBits;
            availableBits = Bigint.kDigitBits - consumedBits;
            while (availableBits >= bitsPerChar)
            {
                _outBuf[--_out] = Bigint.kConversionChars[(int)(digit & (ulong)charMask)];
                digit >>= bitsPerChar;
                availableBits -= bitsPerChar;
            }
        }
        // Take any leftover bits from the last iteration into account.
        ulong msd = digits[^1];
        int cur = (int)((digit | (msd << availableBits)) & (ulong)charMask);
        _outBuf[--_out] = Bigint.kConversionChars[cur];
        digit = msd >> (bitsPerChar - availableBits);
        while (digit != 0)
        {
            _outBuf[--_out] = Bigint.kConversionChars[(int)(digit & (ulong)charMask)];
            digit >>= bitsPerChar;
        }
    }

    // "Fast" divide-and-conquer conversion to string. The basic idea is to
    // recursively cut the BigInt in half (using a division with remainder,
    // the divisor being ~half as large (in bits) as the current dividend).
    //
    // As preparation, we build up a linked list of metadata for each recursion
    // level. We do this bottom-up, i.e. start with the level that will produce
    // two halves that are register-sized and bail out to the base case.
    // Each higher level (executed earlier, prepared later) uses a divisor that is
    // the square of the previously-created "next" level's divisor. Preparation
    // terminates when the current divisor is at least half as large as the bigint.
    // We also precompute each level's divisor's inverse, so we can use
    // Barrett division later.
    //
    // Example: say we want to format 1234567890123, and we can fit two decimal
    // digits into a register for the base case.
    //
    //              1234567890123
    //                    |
    //               %100000000 (a)              // RecursionLevel 2,
    //             /            \                // is_toplevel_ == true.
    //         12345            67890123
    //           |                  |
    //    (e) %10000             %10000 (b)      // RecursionLevel 1
    //        /    \            /      \
    //       1     2345      6789      0123
    //       |   (f) |         | (d)     |
    // (g) %100    %100      %100      %100 (c)  // RecursionLevel 0
    //     / \     /   \     /   \     /   \
    //    00 01   23   45   67   89   01   23
    //        |    |    |    |    |    |    |    // Base case.
    //       "1" "23" "45" "67" "89" "01" "23"
    //
    // We start building RecursionLevels in order 0 -> 1 -> 2, performing the
    // squarings 100^2 = 10000 and 10000^2 = 100000000 each only once. Execution
    // then happens in order (a) through (g); lower-level divisors are used
    // repeatedly. We build the string from right to left.
    // Note that we can skip the division at (g) and fall through directly.
    // Also, note that there are two chunks with value 1: one of them must produce
    // a leading "0" in its string representation, the other must not.
    //
    // In this example, {base_divisor} is 100 and {base_char_count} is 2.
    public void Fast()
    {
        RecursionLevel? recursionLevels = RecursionLevel.CreateLevels(_chunkDivisor, _chunkChars, Bigint.BitLength(Digits), _processor);
        if (_processor.should_terminate()) return;
        _out = ProcessLevel(recursionLevels, Digits, isInputDigits: true, _out, true);
    }

    // Writes '0' characters right-to-left, starting at {out}-1, until the distance
    // from {right_boundary} to {out} equals the number of characters that {level}
    // is supposed to produce.
    int FillWithZeros(RecursionLevel? level, int rightBoundary, int @out, bool isLastOnLevel)
    {
        // Fill up with zeros up to the character count expected to be generated
        // on this level; unless this is the left edge of the result.
        if (isLastOnLevel) return @out;
        int chunkChars = level == null ? _chunkChars : level.CharCount * 2;
        int end = rightBoundary - chunkChars;
        Debug.Assert(@out >= end);
        while (@out > end) _outBuf[--@out] = '0';
        return @out;
    }

    int ProcessLevel(RecursionLevel? level, ReadOnlySpan<ulong> chunk, bool isInputDigits, int @out, bool isLastOnLevel)
    {
        // Step 0: if only one digit is left, bail out to the base case.
        ReadOnlySpan<ulong> normalized = Bigint.Normalize(chunk);
        if (normalized.Length <= 1)
        {
            int rightBoundary = @out;
            if (normalized.Length == 1) @out = BasecaseLast(normalized[0], @out);
            return FillWithZeros(level, rightBoundary, @out, isLastOnLevel);
        }

        Debug.Assert(level != null);
        // Step 1: If the chunk is guaranteed to remain smaller than the divisor
        // even after left-shifting, fall through to the next level immediately.
        if (normalized.Length < level.Divisor.Length)
        {
            int rightBoundary = @out;
            @out = ProcessLevel(level.Next, chunk, isInputDigits, @out, isLastOnLevel);
            return FillWithZeros(level, rightBoundary, @out, isLastOnLevel);
        }
        // Step 2: Prepare the chunk. V8 shifts the chunk in place when it is not
        // the input's own memory; here the shifted copy is always separate, which
        // makes V8's undo step ({Reset}) unnecessary.
        ReadOnlySpan<ulong> originalChunk = chunk;
        ShiftedDigits chunkShifted = new(chunk, level.LeadingZeroShift);
        chunk = Bigint.Normalize(chunkShifted.Digits);
        // Check (now precisely) if the chunk is smaller than the divisor.
        int comparison = Bigint.Compare(chunk, level.Divisor);
        if (comparison <= 0)
        {
            int rightBoundary = @out;
            if (comparison < 0)
            {
                // If the chunk is strictly smaller than the divisor, we can process
                // it directly on the next level as the right half, and know that the
                // left half is all '0'.
                chunk = originalChunk;
                @out = ProcessLevel(level.Next, chunk, isInputDigits, @out, isLastOnLevel);
            }
            else
            {
                Debug.Assert(comparison == 0);
                // If the chunk is equal to the divisor, we know that the right half
                // is all '0', and the left half is '...0001'.
                // Handling this case specially is an optimization; we could also
                // fall through to the generic "chunk > divisor" path below.
                @out = FillWithZeros(level.Next, rightBoundary, @out, false);
                _outBuf[--@out] = '1';
            }
            // In both cases, make sure the left half is fully written.
            return FillWithZeros(level, rightBoundary, @out, isLastOnLevel);
        }
        // Step 3: Allocate space for the results.
        // Allocate one extra digit so the next level can left-shift in-place.
        ulong[] right = new ulong[level.Divisor.Length + 1];
        // Allocate one extra digit because DivideBarrett requires it.
        ulong[] left = new ulong[chunk.Length - level.Divisor.Length + 1];

        // Step 4: Divide to split {chunk} into {left} and {right}.
        int inverseLen = chunk.Length - level.Divisor.Length;
        if (inverseLen == 0)
        {
            _processor.DivideSchoolbook(left, right, chunk, level.Divisor);
        }
        else if (level.Divisor.Length == 1)
        {
            Bigint.DivideSingle(left, out right[0], chunk, level.Divisor[0]);
            for (int i = 1; i < right.Length; i++) right[i] = 0;
        }
        else
        {
            ulong[] scratch = new ulong[Bigint.DivideBarrettScratchSpace(chunk.Length)];
            // The top level only computes its inverse when {chunk.len()} is
            // available. Other levels have precomputed theirs.
            if (level.IsToplevel)
            {
                level.ComputeInverse(_processor, chunk.Length);
                if (_processor.should_terminate()) return @out;
            }
            ReadOnlySpan<ulong> inverse = level.GetInverse(chunk.Length);
            _processor.DivideBarrett(left, right, chunk, level.Divisor, inverse, scratch);
            if (_processor.should_terminate()) return @out;
        }
        Bigint.RightShift(right, right, level.LeadingZeroShift);

        // Step 5: Recurse.
        int endOfRightPart = ProcessLevel(level.Next, right, false, @out, false);
        if (_processor.should_terminate()) return @out;
        // The recursive calls are required and hence designed to write exactly as
        // many characters as their level is responsible for.
        Debug.Assert(endOfRightPart == @out - level.CharCount);
        return ProcessLevel(level.Next, left, false, @out - level.CharCount, isLastOnLevel);
    }
}

sealed class RecursionLevel
{
    public int LeadingZeroShift;
    // The number of characters generated by *each half* of this level.
    public readonly int CharCount;
    public bool IsToplevel = true;
    public readonly RecursionLevel? Next;
    ulong[] _divisor;
    int _divisorLen;
    ulong[]? _inverseStorage;
    int _inverseOffset;
    int _inverseLen;

    public ReadOnlySpan<ulong> Divisor => _divisor.AsSpan(0, _divisorLen);

    RecursionLevel(ulong baseDivisor, int baseCharCount)
    {
        CharCount = baseCharCount;
        _divisor = [baseDivisor];
        _divisorLen = 1;
    }

    RecursionLevel(RecursionLevel next)
    {
        CharCount = next.CharCount * 2;
        Next = next;
        _divisorLen = next._divisorLen * 2;
        _divisor = new ulong[_divisorLen];
        next.IsToplevel = false;
        if (CharCount >= int.MaxValue / 2) throw new InvalidOperationException("BigInt too large");
    }

    void LeftShiftDivisor()
    {
        LeadingZeroShift = Bigint.CountLeadingZeros(_divisor[_divisorLen - 1]);
        Span<ulong> d = _divisor.AsSpan(0, _divisorLen);
        Bigint.LeftShift(d, d, LeadingZeroShift);
    }

    public static RecursionLevel? CreateLevels(ulong baseDivisor, int baseCharCount, int targetBitLength, Processor processor)
    {
        RecursionLevel level = new(baseDivisor, baseCharCount);
        // We can stop creating levels when the next level's divisor, which is the
        // square of the current level's divisor, would be strictly bigger (in terms
        // of its numeric value) than the input we're formatting. Since computing that
        // next divisor is expensive, we want to predict the necessity based on bit
        // lengths. Bit lengths are an imperfect predictor of numeric value, so we
        // have to be careful:
        // - since we can't estimate which one of two numbers of equal bit length
        //   is bigger, we have to aim for a strictly bigger bit length.
        // - when squaring, the bit length sometimes doubles (e.g. 0b11^2 == 0b1001),
        //   but usually we "lose" a bit (e.g. 0b10^2 == 0b100).
        while (Bigint.BitLength(level.Divisor) * 2 - 1 <= targetBitLength)
        {
            RecursionLevel prev = level;
            level = new RecursionLevel(prev);
            processor.Multiply(level._divisor, prev.Divisor, prev.Divisor);
            if (processor.should_terminate()) return null;
            level._divisorLen = Bigint.Normalize((ReadOnlySpan<ulong>)level._divisor).Length;
            // Left-shifting the divisor must only happen after it's been used to
            // compute the next divisor.
            prev.LeftShiftDivisor();
            prev.ComputeInverse(processor);
        }
        level.LeftShiftDivisor();
        // Not calling info->ComputeInverse here so that it can take the input's
        // length into account to save some effort on inverse generation.
        return level;
    }

    // The top level might get by with a smaller inverse than we could maximally
    // compute, so the caller should provide the dividend length.
    public void ComputeInverse(Processor processor, int dividendLength = 0)
    {
        int inverseLen = _divisorLen;
        if (dividendLength != 0)
        {
            inverseLen = dividendLength - _divisorLen;
            Debug.Assert(inverseLen <= _divisorLen);
        }
        int scratchLen = Bigint.InvertScratchSpace(inverseLen);
        ulong[] scratch = new ulong[scratchLen];
        ulong[] invStorage = new ulong[inverseLen + 1];
        ReadOnlySpan<ulong> input = Bigint.Slice(Divisor, _divisorLen - inverseLen, inverseLen);
        processor.Invert(invStorage, input, scratch);
        Debug.Assert(invStorage[inverseLen] == 0);  // TrimOne
        _inverseStorage = invStorage;
        _inverseOffset = 0;
        _inverseLen = inverseLen;
    }

    public ReadOnlySpan<ulong> GetInverse(int dividendLength)
    {
        Debug.Assert(_inverseLen != 0);
        int inverseLen = dividendLength - _divisorLen;
        Debug.Assert(inverseLen <= _inverseLen);
        return _inverseStorage.AsSpan(_inverseOffset + (_inverseLen - inverseLen), inverseLen);
    }
}

public sealed partial class Processor
{
    /// <summary>
    /// Writes X in the given radix (with a leading '-' if sign) into
    /// out[0..outLength). outLength initially contains the allocated capacity
    /// of out (at least ToStringResultLength), and upon return is set to the
    /// actual length of the result string.
    /// </summary>
    public Status ToString(char[] @out, ref int outLength, ReadOnlySpan<ulong> X, int radix, bool sign)
    {
        ToStringInternal(@out, ref outLength, X, radix, sign);
        return get_and_clear_status();
    }

    void ToStringInternal(char[] @out, ref int outLength, ReadOnlySpan<ulong> X, int radix, bool sign)
    {
        bool useFastAlgorithm = X.Length >= BigintConfig.kToStringFastThreshold;
        ToStringImpl(@out, ref outLength, X, radix, sign, useFastAlgorithm);
    }

    /// <summary>Factored out so that tests can call it.</summary>
    public void ToStringImpl(char[] @out, ref int outLength, ReadOnlySpan<ulong> X, int radix, bool sign, bool fast)
    {
        ReadOnlySpan<ulong> normalized = Bigint.Normalize(X);
        Debug.Assert(outLength >= Bigint.ToStringResultLength(normalized, radix, sign));
        ToStringFormatter formatter = new(normalized.ToArray(), normalized.Length, radix, sign, @out, 0, outLength, this);
        if (Bigint.IsPowerOfTwo(radix))
        {
            formatter.BasePowerOfTwo();
        }
        else if (fast)
        {
            formatter.Start();
            formatter.Fast();
            if (should_terminate()) return;
        }
        else
        {
            formatter.Start();
            formatter.Classic();
        }
        int excess = formatter.Finish();
        outLength -= excess;
        Array.Clear(@out, outLength, excess);
    }

    /// <summary>Convenience wrapper: X in the given radix as a string.</summary>
    public string ToString(ReadOnlySpan<ulong> X, int radix, bool sign)
    {
        if (Bigint.Normalize(X).Length == 0) return "0";
        int len = Bigint.ToStringResultLength(Bigint.Normalize(X), radix, sign);
        char[] buffer = new char[len];
        ToStringInternal(buffer, ref len, X, radix, sign);
        return new string(buffer, 0, len);
    }
}
