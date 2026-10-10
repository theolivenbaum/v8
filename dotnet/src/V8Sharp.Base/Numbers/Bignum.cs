// Port of src/base/numbers/bignum.h and bignum.cc.

namespace V8Sharp.Base.Numbers;

/// <summary>
/// A fixed-capacity arbitrary-precision unsigned integer with a bigit
/// exponent, used by the slow paths of dtoa and strtod.
/// </summary>
public sealed class Bignum
{
    // 3584 = 128 * 28. We can represent 2^3584 > 10^1000 accurately.
    // This bignum can encode much bigger numbers, since it contains an
    // exponent.
    public const int kMaxSignificantBits = 3584;

    const int kChunkSize = sizeof(uint) * 8;
    const int kDoubleChunkSize = sizeof(ulong) * 8;
    // With bigit size of 28 we loose some bits, but a double still fits easily
    // into two chunks, and more importantly we can use the Comba multiplication.
    const int kBigitSize = 28;
    const uint kBigitMask = (1u << kBigitSize) - 1;
    // Every instance allocates kBigitLength chunks. Bignums cannot grow.
    const int kBigitCapacity = kMaxSignificantBits / kBigitSize;

    readonly uint[] _bigits = new uint[kBigitCapacity];
    int _usedDigits;
    // The Bignum's value equals value(bigits_) * 2^(exponent_ * kBigitSize).
    int _exponent;

    static void EnsureCapacity(int size)
    {
        if (size > kBigitCapacity) throw new UnreachableException("Bignum capacity exceeded");
    }

    /// <summary>Guaranteed to lie in one Bigit.</summary>
    public void AssignUInt16(ushort value)
    {
        Zero();
        if (value == 0) return;
        EnsureCapacity(1);
        _bigits[0] = value;
        _usedDigits = 1;
    }

    public void AssignUInt64(ulong value)
    {
        const int kUInt64Size = 64;
        Zero();
        if (value == 0) return;
        int neededBigits = kUInt64Size / kBigitSize + 1;
        EnsureCapacity(neededBigits);
        for (int i = 0; i < neededBigits; ++i)
        {
            _bigits[i] = (uint)(value & kBigitMask);
            value >>= kBigitSize;
        }
        _usedDigits = neededBigits;
        Clamp();
    }

    public void AssignBignum(Bignum other)
    {
        _exponent = other._exponent;
        for (int i = 0; i < other._usedDigits; ++i) _bigits[i] = other._bigits[i];
        // Clear the excess digits (if there were any).
        for (int i = other._usedDigits; i < _usedDigits; ++i) _bigits[i] = 0;
        _usedDigits = other._usedDigits;
    }

    static ulong ReadUInt64(ReadOnlySpan<char> buffer, int from, int digitsToRead)
    {
        ulong result = 0;
        int to = from + digitsToRead;
        for (int i = from; i < to; ++i)
        {
            int d = buffer[i] - '0';
            Debug.Assert(0 <= d && d <= 9);
            result = result * 10 + (ulong)d;
        }
        return result;
    }

    public void AssignDecimalString(ReadOnlySpan<char> value)
    {
        // 2^64 = 18446744073709551616 > 10^19
        const int kMaxUint64DecimalDigits = 19;
        Zero();
        int length = value.Length;
        int pos = 0;
        // Let's just say that each digit needs 4 bits.
        while (length >= kMaxUint64DecimalDigits)
        {
            ulong chunk = ReadUInt64(value, pos, kMaxUint64DecimalDigits);
            pos += kMaxUint64DecimalDigits;
            length -= kMaxUint64DecimalDigits;
            MultiplyByPowerOfTen(kMaxUint64DecimalDigits);
            AddUInt64(chunk);
        }
        ulong rest = ReadUInt64(value, pos, length);
        MultiplyByPowerOfTen(length);
        AddUInt64(rest);
        Clamp();
    }

    static int HexCharValue(char c)
    {
        if ('0' <= c && c <= '9') return c - '0';
        if ('a' <= c && c <= 'f') return 10 + c - 'a';
        if ('A' <= c && c <= 'F') return 10 + c - 'A';
        throw new UnreachableException();
    }

    public void AssignHexString(ReadOnlySpan<char> value)
    {
        Zero();
        int length = value.Length;
        int neededBigits = length * 4 / kBigitSize + 1;
        EnsureCapacity(neededBigits);
        int stringIndex = length - 1;
        for (int i = 0; i < neededBigits - 1; ++i)
        {
            // These bigits are guaranteed to be "full".
            uint currentBigit = 0;
            for (int j = 0; j < kBigitSize / 4; j++)
            {
                currentBigit += (uint)HexCharValue(value[stringIndex--]) << (j * 4);
            }
            _bigits[i] = currentBigit;
        }
        _usedDigits = neededBigits - 1;

        uint mostSignificantBigit = 0;  // Could be = 0;
        for (int j = 0; j <= stringIndex; ++j)
        {
            mostSignificantBigit <<= 4;
            mostSignificantBigit += (uint)HexCharValue(value[j]);
        }
        if (mostSignificantBigit != 0)
        {
            _bigits[_usedDigits] = mostSignificantBigit;
            _usedDigits++;
        }
        Clamp();
    }

    public void AddUInt16(ushort operand) => AddUInt64(operand);

    public void AddUInt64(ulong operand)
    {
        if (operand == 0) return;
        Bignum other = new();
        other.AssignUInt64(operand);
        AddBignum(other);
    }

    public void AddBignum(Bignum other)
    {
        Debug.Assert(IsClamped());
        Debug.Assert(other.IsClamped());

        // If this has a greater exponent than other append zero-bigits to this.
        // After this call exponent_ <= other.exponent_.
        Align(other);

        // There are two possibilities:
        //   aaaaaaaaaaa 0000  (where the 0s represent a's exponent)
        //     bbbbb 00000000
        //   ----------------
        //   ccccccccccc 0000
        // or
        //    aaaaaaaaaa 0000
        //  bbbbbbbbb 0000000
        //  -----------------
        //  cccccccccccc 0000
        // In both cases we might need a carry bigit.

        EnsureCapacity(1 + Math.Max(BigitLength, other.BigitLength) - _exponent);
        uint carry = 0;
        int bigitPos = other._exponent - _exponent;
        Debug.Assert(bigitPos >= 0);
        for (int i = 0; i < other._usedDigits; ++i)
        {
            uint sum = _bigits[bigitPos] + other._bigits[i] + carry;
            _bigits[bigitPos] = sum & kBigitMask;
            carry = sum >> kBigitSize;
            bigitPos++;
        }

        while (carry != 0)
        {
            uint sum = _bigits[bigitPos] + carry;
            _bigits[bigitPos] = sum & kBigitMask;
            carry = sum >> kBigitSize;
            bigitPos++;
        }
        _usedDigits = Math.Max(bigitPos, _usedDigits);
        Debug.Assert(IsClamped());
    }

    /// <summary>Precondition: this >= other.</summary>
    public void SubtractBignum(Bignum other)
    {
        Debug.Assert(IsClamped());
        Debug.Assert(other.IsClamped());
        // We require this to be bigger than other.
        Debug.Assert(LessEqual(other, this));

        Align(other);

        int offset = other._exponent - _exponent;
        uint borrow = 0;
        int i;
        for (i = 0; i < other._usedDigits; ++i)
        {
            Debug.Assert(borrow == 0 || borrow == 1);
            uint difference = _bigits[i + offset] - other._bigits[i] - borrow;
            _bigits[i + offset] = difference & kBigitMask;
            borrow = difference >> (kChunkSize - 1);
        }
        while (borrow != 0)
        {
            uint difference = _bigits[i + offset] - borrow;
            _bigits[i + offset] = difference & kBigitMask;
            borrow = difference >> (kChunkSize - 1);
            ++i;
        }
        Clamp();
    }

    public void ShiftLeft(int shiftAmount)
    {
        if (_usedDigits == 0) return;
        _exponent += shiftAmount / kBigitSize;
        int localShift = shiftAmount % kBigitSize;
        EnsureCapacity(_usedDigits + 1);
        BigitsShiftLeft(localShift);
    }

    public void MultiplyByUInt32(uint factor)
    {
        if (factor == 1) return;
        if (factor == 0)
        {
            Zero();
            return;
        }
        if (_usedDigits == 0) return;

        // The product of a bigit with the factor is of size kBigitSize + 32.
        // Assert that this number + 1 (for the carry) fits into double chunk.
        Debug.Assert(kDoubleChunkSize >= kBigitSize + 32 + 1);
        ulong carry = 0;
        for (int i = 0; i < _usedDigits; ++i)
        {
            ulong product = (ulong)factor * _bigits[i] + carry;
            _bigits[i] = (uint)(product & kBigitMask);
            carry = product >> kBigitSize;
        }
        while (carry != 0)
        {
            EnsureCapacity(_usedDigits + 1);
            _bigits[_usedDigits] = (uint)(carry & kBigitMask);
            _usedDigits++;
            carry >>= kBigitSize;
        }
    }

    public void MultiplyByUInt64(ulong factor)
    {
        if (factor == 1) return;
        if (factor == 0)
        {
            Zero();
            return;
        }
        ulong carry = 0;
        ulong low = factor & 0xFFFFFFFF;
        ulong high = factor >> 32;
        for (int i = 0; i < _usedDigits; ++i)
        {
            ulong productLow = low * _bigits[i];
            ulong productHigh = high * _bigits[i];
            ulong tmp = (carry & kBigitMask) + productLow;
            _bigits[i] = (uint)(tmp & kBigitMask);
            carry = (carry >> kBigitSize) + (tmp >> kBigitSize) + (productHigh << (32 - kBigitSize));
        }
        while (carry != 0)
        {
            EnsureCapacity(_usedDigits + 1);
            _bigits[_usedDigits] = (uint)(carry & kBigitMask);
            _usedDigits++;
            carry >>= kBigitSize;
        }
    }

    static ReadOnlySpan<uint> kFive1_to_12 => [
        5, 25, 125, 625, 3125, 15625, 78125, 390625, 1953125, 9765625, 48828125, 244140625];

    public void MultiplyByPowerOfTen(int exponent)
    {
        const ulong kFive27 = 0x6765_C793_FA10_079D;
        const uint kFive13 = 1220703125;

        Debug.Assert(exponent >= 0);
        if (exponent == 0) return;
        if (_usedDigits == 0) return;

        // We shift by exponent at the end just before returning.
        int remainingExponent = exponent;
        while (remainingExponent >= 27)
        {
            MultiplyByUInt64(kFive27);
            remainingExponent -= 27;
        }
        while (remainingExponent >= 13)
        {
            MultiplyByUInt32(kFive13);
            remainingExponent -= 13;
        }
        if (remainingExponent > 0)
        {
            MultiplyByUInt32(kFive1_to_12[remainingExponent - 1]);
        }
        ShiftLeft(exponent);
    }

    public void Times10() => MultiplyByUInt32(10);

    public void Square()
    {
        Debug.Assert(IsClamped());
        int productLength = 2 * _usedDigits;
        EnsureCapacity(productLength);

        // Comba multiplication: compute each column separately.
        // Example: r = a2a1a0 * b2b1b0.
        //    r =  1    * a0b0 +
        //        10    * (a1b0 + a0b1) +
        //        100   * (a2b0 + a1b1 + a0b2) +
        //        1000  * (a2b1 + a1b2) +
        //        10000 * a2b2
        //
        // In the worst case we have to accumulate nb-digits products of digit*digit.
        //
        // Assert that the additional number of bits in a DoubleChunk are enough to
        // sum up used_digits of Bigit*Bigit.
        if ((1 << (2 * (kChunkSize - kBigitSize))) <= _usedDigits)
        {
            throw new NotImplementedException();
        }
        ulong accumulator = 0;
        // First shift the digits so we don't overwrite them.
        int copyOffset = _usedDigits;
        for (int i = 0; i < _usedDigits; ++i) _bigits[copyOffset + i] = _bigits[i];
        // We have two loops to avoid some 'if's in the loop.
        for (int i = 0; i < _usedDigits; ++i)
        {
            // Process temporary digit i with power i.
            // The sum of the two indices must be equal to i.
            int bigitIndex1 = i;
            int bigitIndex2 = 0;
            // Sum all of the sub-products.
            while (bigitIndex1 >= 0)
            {
                uint chunk1 = _bigits[copyOffset + bigitIndex1];
                uint chunk2 = _bigits[copyOffset + bigitIndex2];
                accumulator += (ulong)chunk1 * chunk2;
                bigitIndex1--;
                bigitIndex2++;
            }
            _bigits[i] = (uint)accumulator & kBigitMask;
            accumulator >>= kBigitSize;
        }
        for (int i = _usedDigits; i < productLength; ++i)
        {
            int bigitIndex1 = _usedDigits - 1;
            int bigitIndex2 = i - bigitIndex1;
            // Invariant: sum of both indices is again equal to i.
            // Inner loop runs 0 times on last iteration, emptying accumulator.
            while (bigitIndex2 < _usedDigits)
            {
                uint chunk1 = _bigits[copyOffset + bigitIndex1];
                uint chunk2 = _bigits[copyOffset + bigitIndex2];
                accumulator += (ulong)chunk1 * chunk2;
                bigitIndex1--;
                bigitIndex2++;
            }
            // The overwritten bigits_[i] will never be read in further loop iterations,
            // because bigit_index1 and bigit_index2 are always greater
            // than i - used_digits_.
            _bigits[i] = (uint)accumulator & kBigitMask;
            accumulator >>= kBigitSize;
        }
        // Since the result was guaranteed to lie inside the number the
        // accumulator must be 0 now.
        Debug.Assert(accumulator == 0);

        // Don't forget to update the used_digits and the exponent.
        _usedDigits = productLength;
        _exponent *= 2;
        Clamp();
    }

    public void AssignPowerUInt16(ushort @base, int powerExponent)
    {
        Debug.Assert(@base != 0);
        Debug.Assert(powerExponent >= 0);
        if (powerExponent == 0)
        {
            AssignUInt16(1);
            return;
        }
        Zero();
        int shifts = 0;
        // We expect base to be in range 2-32, and most often to be 10.
        // It does not make much sense to implement different algorithms for counting
        // the bits.
        while ((@base & 1) == 0)
        {
            @base >>= 1;
            shifts++;
        }
        int bitSize = 0;
        int tmpBase = @base;
        while (tmpBase != 0)
        {
            tmpBase >>= 1;
            bitSize++;
        }
        int finalSize = bitSize * powerExponent;
        // 1 extra bigit for the shifting, and one for rounded final_size.
        EnsureCapacity(finalSize / kBigitSize + 2);

        // Left to Right exponentiation.
        int mask = 1;
        while (powerExponent >= mask) mask <<= 1;

        // The mask is now pointing to the bit above the most significant 1-bit of
        // power_exponent.
        // Get rid of first 1-bit;
        mask >>= 2;
        ulong thisValue = @base;

        bool delayedMultiplication = false;
        const ulong max32bits = 0xFFFFFFFF;
        while (mask != 0 && thisValue <= max32bits)
        {
            thisValue *= thisValue;
            // Verify that there is enough space in this_value to perform the
            // multiplication.  The first bit_size bits must be 0.
            if ((powerExponent & mask) != 0)
            {
                ulong baseBitsMask = ~((1UL << (64 - bitSize)) - 1);
                bool highBitsZero = (thisValue & baseBitsMask) == 0;
                if (highBitsZero)
                {
                    thisValue *= @base;
                }
                else
                {
                    delayedMultiplication = true;
                }
            }
            mask >>= 1;
        }
        AssignUInt64(thisValue);
        if (delayedMultiplication) MultiplyByUInt32(@base);

        // Now do the same thing as a bignum.
        while (mask != 0)
        {
            Square();
            if ((powerExponent & mask) != 0) MultiplyByUInt32(@base);
            mask >>= 1;
        }

        // And finally add the saved shifts.
        ShiftLeft(shifts * powerExponent);
    }

    /// <summary>
    /// Pseudocode: int result = this / other; this = this % other;
    /// In the worst case this function is in O(this/other).
    /// Precondition: this/other &lt; 16bit.
    /// </summary>
    public ushort DivideModuloIntBignum(Bignum other)
    {
        Debug.Assert(IsClamped());
        Debug.Assert(other.IsClamped());
        Debug.Assert(other._usedDigits > 0);

        // Easy case: if we have less digits than the divisor than the result is 0.
        // Note: this handles the case where this == 0, too.
        if (BigitLength < other.BigitLength) return 0;

        Align(other);

        ushort result = 0;

        // Start by removing multiples of 'other' until both numbers have the same
        // number of digits.
        while (BigitLength > other.BigitLength)
        {
            // This naive approach is extremely inefficient if the this divided other
            // might be big. This function is implemented for doubleToString where
            // the result should be small (less than 10).
            Debug.Assert(other._bigits[other._usedDigits - 1] >= ((1 << kBigitSize) / 16));
            // Remove the multiples of the first digit.
            // Example this = 23 and other equals 9. -> Remove 2 multiples.
            result += (ushort)_bigits[_usedDigits - 1];
            SubtractTimes(other, (int)_bigits[_usedDigits - 1]);
        }

        Debug.Assert(BigitLength == other.BigitLength);

        // Both bignums are at the same length now.
        // Since other has more than 0 digits we know that the access to
        // bigits_[used_digits_ - 1] is safe.
        uint thisBigit = _bigits[_usedDigits - 1];
        uint otherBigit = other._bigits[other._usedDigits - 1];

        if (other._usedDigits == 1)
        {
            // Shortcut for easy (and common) case.
            int quotient = (int)(thisBigit / otherBigit);
            _bigits[_usedDigits - 1] = thisBigit - otherBigit * (uint)quotient;
            result += (ushort)quotient;
            Clamp();
            return result;
        }

        int divisionEstimate = (int)(thisBigit / (otherBigit + 1));
        result += (ushort)divisionEstimate;
        SubtractTimes(other, divisionEstimate);

        if (otherBigit * (uint)(divisionEstimate + 1) > thisBigit)
        {
            // No need to even try to subtract. Even if other's remaining digits were 0
            // another subtraction would be too much.
            return result;
        }

        while (LessEqual(other, this))
        {
            SubtractBignum(other);
            result++;
        }
        return result;
    }

    static int SizeInHexChars(uint number)
    {
        Debug.Assert(number > 0);
        int result = 0;
        while (number != 0)
        {
            number >>= 4;
            result++;
        }
        return result;
    }

    static char HexCharOfValue(int value) => value < 10 ? (char)(value + '0') : (char)(value - 10 + 'A');

    public bool ToHexString(Span<char> buffer)
    {
        Debug.Assert(IsClamped());
        // Each bigit must be printable as separate hex-character.
        const int kHexCharsPerBigit = kBigitSize / 4;
        int bufferSize = buffer.Length;

        if (_usedDigits == 0)
        {
            if (bufferSize < 2) return false;
            buffer[0] = '0';
            buffer[1] = '\0';
            return true;
        }
        // We add 1 for the terminating '\0' character.
        int neededChars = (BigitLength - 1) * kHexCharsPerBigit + SizeInHexChars(_bigits[_usedDigits - 1]) + 1;
        if (neededChars > bufferSize) return false;
        int stringIndex = neededChars - 1;
        buffer[stringIndex--] = '\0';
        for (int i = 0; i < _exponent; ++i)
        {
            for (int j = 0; j < kHexCharsPerBigit; ++j) buffer[stringIndex--] = '0';
        }
        for (int i = 0; i < _usedDigits - 1; ++i)
        {
            uint currentBigit = _bigits[i];
            for (int j = 0; j < kHexCharsPerBigit; ++j)
            {
                buffer[stringIndex--] = HexCharOfValue((int)(currentBigit & 0xF));
                currentBigit >>= 4;
            }
        }
        // And finally the last bigit.
        uint mostSignificantBigit = _bigits[_usedDigits - 1];
        while (mostSignificantBigit != 0)
        {
            buffer[stringIndex--] = HexCharOfValue((int)(mostSignificantBigit & 0xF));
            mostSignificantBigit >>= 4;
        }
        return true;
    }

    uint BigitAt(int index)
    {
        if (index >= BigitLength) return 0;
        if (index < _exponent) return 0;
        return _bigits[index - _exponent];
    }

    public static int Compare(Bignum a, Bignum b)
    {
        Debug.Assert(a.IsClamped());
        Debug.Assert(b.IsClamped());
        int bigitLengthA = a.BigitLength;
        int bigitLengthB = b.BigitLength;
        if (bigitLengthA < bigitLengthB) return -1;
        if (bigitLengthA > bigitLengthB) return +1;
        for (int i = bigitLengthA - 1; i >= Math.Min(a._exponent, b._exponent); --i)
        {
            uint bigitA = a.BigitAt(i);
            uint bigitB = b.BigitAt(i);
            if (bigitA < bigitB) return -1;
            if (bigitA > bigitB) return +1;
            // Otherwise they are equal up to this digit. Try the next digit.
        }
        return 0;
    }

    public static bool Equal(Bignum a, Bignum b) => Compare(a, b) == 0;
    public static bool LessEqual(Bignum a, Bignum b) => Compare(a, b) <= 0;
    public static bool Less(Bignum a, Bignum b) => Compare(a, b) < 0;

    /// <summary>Returns Compare(a + b, c).</summary>
    public static int PlusCompare(Bignum a, Bignum b, Bignum c)
    {
        Debug.Assert(a.IsClamped());
        Debug.Assert(b.IsClamped());
        Debug.Assert(c.IsClamped());
        if (a.BigitLength < b.BigitLength) return PlusCompare(b, a, c);
        if (a.BigitLength + 1 < c.BigitLength) return -1;
        if (a.BigitLength > c.BigitLength) return +1;
        // The exponent encodes 0-bigits. So if there are more 0-digits in 'a' than
        // 'b' has digits, then the bigit-length of 'a'+'b' must be equal to the one
        // of 'a'.
        if (a._exponent >= b.BigitLength && a.BigitLength < c.BigitLength) return -1;

        uint borrow = 0;
        // Starting at min_exponent all digits are == 0. So no need to compare them.
        int minExponent = Math.Min(Math.Min(a._exponent, b._exponent), c._exponent);
        for (int i = c.BigitLength - 1; i >= minExponent; --i)
        {
            uint chunkA = a.BigitAt(i);
            uint chunkB = b.BigitAt(i);
            uint chunkC = c.BigitAt(i);
            uint sum = chunkA + chunkB;
            if (sum > chunkC + borrow)
            {
                return +1;
            }
            borrow = chunkC + borrow - sum;
            if (borrow > 1) return -1;
            borrow <<= kBigitSize;
        }
        if (borrow == 0) return 0;
        return -1;
    }

    public static bool PlusEqual(Bignum a, Bignum b, Bignum c) => PlusCompare(a, b, c) == 0;
    public static bool PlusLessEqual(Bignum a, Bignum b, Bignum c) => PlusCompare(a, b, c) <= 0;
    public static bool PlusLess(Bignum a, Bignum b, Bignum c) => PlusCompare(a, b, c) < 0;

    void Clamp()
    {
        while (_usedDigits > 0 && _bigits[_usedDigits - 1] == 0) _usedDigits--;
        if (_usedDigits == 0)
        {
            // Zero.
            _exponent = 0;
        }
    }

    bool IsClamped() => _usedDigits == 0 || _bigits[_usedDigits - 1] != 0;

    void Zero()
    {
        for (int i = 0; i < _usedDigits; ++i) _bigits[i] = 0;
        _usedDigits = 0;
        _exponent = 0;
    }

    void Align(Bignum other)
    {
        if (_exponent > other._exponent)
        {
            // If "X" represents a "hidden" digit (by the exponent) then we are in the
            // following case (a == this, b == other):
            // a:  aaaaaaXXXX   or a:   aaaaaXXX
            // b:     bbbbbbX      b: bbbbbbbbXX
            // We replace some of the hidden digits (X) of a with 0 digits.
            // a:  aaaaaa000X   or a:   aaaaa0XX
            int zeroDigits = _exponent - other._exponent;
            EnsureCapacity(_usedDigits + zeroDigits);
            for (int i = _usedDigits - 1; i >= 0; --i) _bigits[i + zeroDigits] = _bigits[i];
            for (int i = 0; i < zeroDigits; ++i) _bigits[i] = 0;
            _usedDigits += zeroDigits;
            _exponent -= zeroDigits;
            Debug.Assert(_usedDigits >= 0);
            Debug.Assert(_exponent >= 0);
        }
    }

    // Requires this to have enough capacity (no tests done).
    // Updates used_digits_ if necessary.
    // shift_amount must be < kBigitSize.
    void BigitsShiftLeft(int shiftAmount)
    {
        Debug.Assert(shiftAmount < kBigitSize);
        Debug.Assert(shiftAmount >= 0);
        if (shiftAmount == 0) return;  // C++ shifts by kBigitSize here, which yields a zero carry anyway.
        uint carry = 0;
        for (int i = 0; i < _usedDigits; ++i)
        {
            uint newCarry = _bigits[i] >> (kBigitSize - shiftAmount);
            _bigits[i] = ((_bigits[i] << shiftAmount) + carry) & kBigitMask;
            carry = newCarry;
        }
        if (carry != 0)
        {
            _bigits[_usedDigits] = carry;
            _usedDigits++;
        }
    }

    /// <summary>BigitLength includes the "hidden" digits encoded in the exponent.</summary>
    int BigitLength => _usedDigits + _exponent;

    void SubtractTimes(Bignum other, int factor)
    {
        Debug.Assert(_exponent <= other._exponent);
        if (factor < 3)
        {
            for (int i = 0; i < factor; ++i) SubtractBignum(other);
            return;
        }
        uint borrow = 0;
        int exponentDiff = other._exponent - _exponent;
        for (int i = 0; i < other._usedDigits; ++i)
        {
            ulong product = (ulong)factor * other._bigits[i];
            ulong remove = borrow + product;
            uint difference = _bigits[i + exponentDiff] - (uint)(remove & kBigitMask);
            _bigits[i + exponentDiff] = difference & kBigitMask;
            borrow = (uint)((difference >> (kChunkSize - 1)) + (remove >> kBigitSize));
        }
        for (int i = other._usedDigits + exponentDiff; i < _usedDigits; ++i)
        {
            if (borrow == 0) return;
            uint difference = _bigits[i] - borrow;
            _bigits[i] = difference & kBigitMask;
            borrow = difference >> (kChunkSize - 1);
        }
        Clamp();
    }
}
