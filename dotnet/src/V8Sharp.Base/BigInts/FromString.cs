// Port of src/bigint/fromstring.cc and the FromStringAccumulator of
// bigint-inl.h.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.BigInts;

/// <summary>
/// A container object for all metadata required for parsing a BigInt from a
/// string. Aggressively optimized not to waste instructions for small cases,
/// while also scaling transparently to huge cases.
/// </summary>
/// <remarks>
/// Usage: (1) create an instance; (2) call Parse to read all characters;
/// (3) check Result and ResultLength; (4) call Processor.FromString to
/// retrieve the digits into a span of that length.
/// </remarks>
public sealed class FromStringAccumulator
{
    public enum Result { kOk, kMaxSizeExceeded }

    /// <summary>Users may wish to align when to use stack or heap memory.</summary>
    public const int kStackParts = 8;

    // Numerical value of the first 127 ASCII characters, using 255 as sentinel
    // for "invalid".
    static ReadOnlySpan<byte> kCharValue => [
        255, 255, 255, 255, 255, 255, 255, 255,  // 0..7
        255, 255, 255, 255, 255, 255, 255, 255,  // 8..15
        255, 255, 255, 255, 255, 255, 255, 255,  // 16..23
        255, 255, 255, 255, 255, 255, 255, 255,  // 24..31
        255, 255, 255, 255, 255, 255, 255, 255,  // 32..39
        255, 255, 255, 255, 255, 255, 255, 255,  // 40..47
        0, 1, 2, 3, 4, 5, 6, 7,                  // 48..55    '0' == 48
        8, 9, 255, 255, 255, 255, 255, 255,      // 56..63    '9' == 57
        255, 10, 11, 12, 13, 14, 15, 16,         // 64..71    'A' == 65
        17, 18, 19, 20, 21, 22, 23, 24,          // 72..79
        25, 26, 27, 28, 29, 30, 31, 32,          // 80..87
        33, 34, 35, 255, 255, 255, 255, 255,     // 88..95    'Z' == 90
        255, 10, 11, 12, 13, 14, 15, 16,         // 96..103   'a' == 97
        17, 18, 19, 20, 21, 22, 23, 24,          // 104..111
        25, 26, 27, 28, 29, 30, 31, 32,          // 112..119
        33, 34, 35, 255, 255, 255, 255, 255,     // 120..127  'z' == 122
    ];

    // A space- and time-efficient way to map {2,4,8,16,32} to {1,2,3,4,5}.
    static ReadOnlySpan<byte> kCharBits => [1, 2, 3, 0, 4, 0, 0, 0, 5];

    internal readonly ulong[] _stackParts = new ulong[kStackParts];
    // A minimal subset of std::vector (V8's GrowableDigitsVector).
    internal ulong[]? _heapParts;
    internal int _heapPartsSize;
    internal ulong _maxMultiplier;
    internal ulong _lastMultiplier;
    readonly int _maxDigits;
    Result _result = Result.kOk;
    internal int _stackPartsUsed;
    internal bool _inlineEverything;
    internal byte _radix;

    /// <summary>
    /// maxDigits is only used for refusing to grow beyond a given size. It
    /// does not cause pre-allocation, so feel free to specify a large maximum.
    /// </summary>
    public FromStringAccumulator(int maxDigits) => _maxDigits = Math.Max(maxDigits, kStackParts);

    public Result result => _result;

    /// <summary>The required allocation size of the result (&lt;= maxDigits).</summary>
    public int ResultLength() => Math.Max(_stackPartsUsed, _heapPartsSize);

    /// <summary>
    /// Reads characters of str from start; returns the index where an invalid
    /// character was encountered (or str.Length).
    /// </summary>
    public int Parse(ReadOnlySpan<char> str, int start, ulong radix)
    {
        Debug.Assert(2 <= radix && radix <= 36);
        int current = start;
        int end = str.Length;
        // The max supported radix is 36, and Math.log2(36) == 5.169..., so we
        // need at most 5.17 bits per char.
        const int kInlineThreshold = kStackParts * Bigint.kDigitBits * 100 / 517;
        Debug.Assert(end >= start);
        if (current == end) return current;  // V8 never passes an empty range.
        _inlineEverything = end - start <= kInlineThreshold;
        if (!_inlineEverything && (radix & (radix - 1)) == 0) return ParsePowerTwo(str, start, radix);
        bool done = false;
        do
        {
            ulong multiplier = 1;
            ulong part = 0;
            while (true)
            {
                uint c = str[current];
                ulong d;  // Numeric value of the current character {c}.
                if (c > 127 || (d = kCharValue[(int)c]) >= radix)
                {
                    done = true;
                    break;
                }

                ulong newMultiplierHigh = Math.BigMul(multiplier, radix, out ulong newMultiplier);
                if (newMultiplierHigh != 0) break;
                multiplier = newMultiplier;
                part = part * radix + d;

                ++current;
                if (current == end)
                {
                    done = true;
                    break;
                }
            }
            if (!AddPart(multiplier, part, done)) return current;
        } while (!done);
        return current;
    }

    public int Parse(ReadOnlySpan<char> str, ulong radix) => Parse(str, 0, radix);

    int ParsePowerTwo(ReadOnlySpan<char> str, int current, ulong radix)
    {
        int end = str.Length;
        _radix = (byte)radix;
        int charBits = kCharBits[(int)(radix >> 2)];
        int bitsLeft;
        bool done = false;
        do
        {
            ulong part = 0;
            bitsLeft = Bigint.kDigitBits;
            while (true)
            {
                uint c = str[current];
                ulong d;  // Numeric value of the current character {c}.
                if (c > 127 || (d = kCharValue[(int)c]) >= radix)
                {
                    done = true;
                    break;
                }

                if (bitsLeft < charBits) break;
                bitsLeft -= charBits;
                part = (part << charBits) | d;

                ++current;
                if (current == end)
                {
                    done = true;
                    break;
                }
            }
            if (!AddPart(part)) return current;
        } while (!done);
        // We use the unused {last_multiplier_} field to
        // communicate how many bits are unused in the last part.
        _lastMultiplier = (ulong)bitsLeft;
        return current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool AddPart(ulong multiplier, ulong part, bool isLast)
    {
        if (_inlineEverything)
        {
            // Inlined version of {MultiplySingle}.
            ulong carry = part;
            ulong high = 0;
            for (int i = 0; i < _stackPartsUsed; i++)
            {
                ulong newHigh = Math.BigMul(_stackParts[i], multiplier, out ulong low);
                UInt128 result = (UInt128)low + high + carry;
                carry = (ulong)(result >> Bigint.kDigitBits);
                _stackParts[i] = (ulong)result;
                high = newHigh;
            }
            high += carry;
            if (high != 0) _stackParts[_stackPartsUsed++] = high;
            Debug.Assert(_stackPartsUsed <= kStackParts);
            return true;
        }
        if (isLast)
        {
            _lastMultiplier = multiplier;
        }
        else
        {
            Debug.Assert(_maxMultiplier == 0 || _maxMultiplier == multiplier);
            _maxMultiplier = multiplier;
        }
        return AddPart(part);
    }

    bool AddPart(ulong part)
    {
        if (_stackPartsUsed < kStackParts)
        {
            _stackParts[_stackPartsUsed++] = part;
            return true;
        }
        if (_heapPartsSize == 0)
        {
            // Initialize heap storage. Copy the stack part to make things easier later.
            _heapParts = new ulong[kStackParts * 2];
            for (int i = 0; i < kStackParts; i++) _heapParts[i] = _stackParts[i];
            _heapPartsSize = kStackParts;
        }
        if (_heapPartsSize >= _maxDigits)
        {
            _result = Result.kMaxSizeExceeded;
            return false;
        }
        if (_heapPartsSize == _heapParts!.Length)
        {
            // Grow by 2x.
            Array.Resize(ref _heapParts, _heapParts.Length * 2);
        }
        _heapParts[_heapPartsSize++] = part;
        return true;
    }
}

public sealed partial class Processor
{
    /// <summary>The classic algorithm: for every part, multiply the
    /// accumulator with the appropriate multiplier, and add the part. O(n^2)
    /// overall.</summary>
    public void FromStringClassic(Span<ulong> Z, FromStringAccumulator accumulator)
    {
        // We always have at least one part to process.
        Debug.Assert(accumulator._stackPartsUsed > 0);
        Z[0] = accumulator._stackParts[0];
        int alreadySetLen = 1;
        for (int i = 1; i < Z.Length; i++) Z[i] = 0;

        // The {FromStringAccumulator} uses stack-allocated storage for the first
        // few parts; if heap storage is used at all then all parts are copied there.
        int numStackParts = accumulator._stackPartsUsed;
        if (numStackParts == 1) return;
        int numHeapParts = accumulator._heapPartsSize;
        // All multipliers are the same, except possibly for the last.
        ulong maxMultiplier = accumulator._maxMultiplier;

        if (numHeapParts == 0)
        {
            for (int i = 1; i < numStackParts - 1; i++)
            {
                Bigint.MultiplySingle(Z, Z[..alreadySetLen], maxMultiplier);
                Bigint.Add(Z, accumulator._stackParts[i]);
                alreadySetLen++;
            }
            Bigint.MultiplySingle(Z, Z[..alreadySetLen], accumulator._lastMultiplier);
            Bigint.Add(Z, accumulator._stackParts[numStackParts - 1]);
            return;
        }
        // Parts are stored on the heap.
        ulong[] heapParts = accumulator._heapParts!;
        for (int i = 1; i < numHeapParts - 1; i++)
        {
            Bigint.MultiplySingle(Z, Z[..alreadySetLen], maxMultiplier);
            Bigint.Add(Z, heapParts[i]);
            alreadySetLen++;
        }
        Bigint.MultiplySingle(Z, Z[..alreadySetLen], accumulator._lastMultiplier);
        Bigint.Add(Z, heapParts[numHeapParts - 1]);
    }

    /// <summary>
    /// The fast algorithm: combine parts in a balanced-binary-tree like order:
    /// Multiply-and-add neighboring pairs of parts, then loop, until only one
    /// part is left. The benefit is that the multiplications will have inputs
    /// of similar sizes, which makes them amenable to fast multiplication
    /// algorithms. See fromstring.cc for the memory-reuse scheme with three
    /// rotating buffers.
    /// </summary>
    public void FromStringLarge(Span<ulong> Z, FromStringAccumulator accumulator)
    {
        int numParts = accumulator._heapPartsSize;
        Debug.Assert(numParts >= 2);
        Debug.Assert(Z.Length >= numParts);
        Span<ulong> parts = accumulator._heapParts.AsSpan(0, numParts);
        ulong[] tempStorage = new ulong[numParts * 2];
        Span<ulong> multipliers = tempStorage.AsSpan(0, numParts);
        Span<ulong> temp = tempStorage.AsSpan(numParts, numParts);
        // Unrolled and specialized first iteration: part_len == 1, so instead of
        // Digits sub-vectors we have individual digit_t values, and the multipliers
        // are known up front.
        {
            ulong maxMultiplier = accumulator._maxMultiplier;
            ulong lastMultiplier = accumulator._lastMultiplier;
            Span<ulong> newParts = temp;
            Span<ulong> newMultipliers = parts;
            int i = 0;
            for (; i + 1 < numParts; i += 2)
            {
                ulong pIn = parts[i];
                ulong pIn2 = parts[i + 1];
                ulong mIn = maxMultiplier;
                ulong mIn2 = i == numParts - 2 ? lastMultiplier : maxMultiplier;
                // p[j] = p[i] * m[i+1] + p[i+1]
                ulong pLow = Bigint.digit_mul(pIn, mIn2, out ulong pHigh);
                newParts[i] = Bigint.digit_add2(pLow, pIn2, out ulong carry);
                newParts[i + 1] = pHigh + carry;
                // m[j] = m[i] * m[i+1]
                if (i > 0)
                {
                    if (i > 2 && mIn2 != lastMultiplier)
                    {
                        newMultipliers[i] = newMultipliers[i - 2];
                        newMultipliers[i + 1] = newMultipliers[i - 1];
                    }
                    else
                    {
                        newMultipliers[i] = Bigint.digit_mul(mIn, mIn2, out ulong mHigh);
                        newMultipliers[i + 1] = mHigh;
                    }
                }
            }
            // Trailing last part (if {num_parts} was odd).
            if (i < numParts)
            {
                newParts[i] = parts[i];
                newMultipliers[i] = lastMultiplier;
                i += 2;
            }
            numParts = i >> 1;
            Span<ulong> newTemp = multipliers;
            parts = newParts;
            multipliers = newMultipliers;
            temp = newTemp;
            AddWorkEstimate((ulong)numParts);
        }
        int partLen = 2;

        // Remaining iterations.
        while (numParts > 1)
        {
            // In the very last iteration, write into {Z}.
            Span<ulong> newParts = numParts == 2 ? Z : temp;
            Span<ulong> newMultipliers = parts;
            int newPartLen = partLen * 2;
            int i = 0;
            for (; i + 1 < numParts; i += 2)
            {
                int start = i * partLen;
                ReadOnlySpan<ulong> pIn = Bigint.Slice(parts, start, partLen);
                ReadOnlySpan<ulong> pIn2 = Bigint.Slice(parts, start + partLen, partLen);
                ReadOnlySpan<ulong> mIn = Bigint.Slice(multipliers, start, partLen);
                ReadOnlySpan<ulong> mIn2 = Bigint.Slice(multipliers, start + partLen, partLen);
                Span<ulong> pOut = Bigint.Slice(newParts, start, newPartLen);
                Span<ulong> mOut = Bigint.Slice(newMultipliers, start, newPartLen);
                // p[j] = p[i] * m[i+1] + p[i+1]
                Multiply(pOut, pIn, mIn2);
                if (should_terminate()) return;
                ulong overflow = Bigint.AddAndReturnOverflow(pOut, pIn2);
                Debug.Assert(overflow == 0);
                // m[j] = m[i] * m[i+1]
                if (i > 0)
                {
                    bool copied = false;
                    if (i > 2)
                    {
                        int prevStart = (i - 2) * partLen;
                        ReadOnlySpan<ulong> mInPrev = Bigint.Slice(multipliers, prevStart, partLen);
                        ReadOnlySpan<ulong> mIn2Prev = Bigint.Slice(multipliers, prevStart + partLen, partLen);
                        if (Bigint.Compare(mIn, mInPrev) == 0 && Bigint.Compare(mIn2, mIn2Prev) == 0)
                        {
                            copied = true;
                            ReadOnlySpan<ulong> mOutPrev = Bigint.Slice(newMultipliers, prevStart, newPartLen);
                            for (int k = 0; k < newPartLen; k++) mOut[k] = mOutPrev[k];
                        }
                    }
                    if (!copied)
                    {
                        Multiply(mOut, mIn, mIn2);
                        if (should_terminate()) return;
                    }
                }
            }
            // Trailing last part (if {num_parts} was odd).
            if (i < numParts)
            {
                ReadOnlySpan<ulong> pIn = Bigint.Slice(parts, i * partLen, partLen);
                ReadOnlySpan<ulong> mIn = Bigint.Slice(multipliers, i * partLen, partLen);
                Span<ulong> pOut = Bigint.Slice(newParts, i * partLen, newPartLen);
                Span<ulong> mOut = Bigint.Slice(newMultipliers, i * partLen, newPartLen);
                int k = 0;
                for (; k < pIn.Length; k++) pOut[k] = pIn[k];
                for (; k < pOut.Length; k++) pOut[k] = 0;
                k = 0;
                for (; k < mIn.Length; k++) mOut[k] = mIn[k];
                for (; k < mOut.Length; k++) mOut[k] = 0;
                i += 2;
            }
            numParts = i >> 1;
            partLen = newPartLen;
            Span<ulong> newTemp = multipliers;
            parts = newParts;
            multipliers = newMultipliers;
            temp = newTemp;
        }
        // Z might be bigger than we requested; be robust towards that.
        for (int i = partLen; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>
    /// Specialized algorithm for power-of-two radixes, designed to work with
    /// ParsePowerTwo: max_multiplier_ isn't saved, but radix_ is, and
    /// last_multiplier_ is the number of unpopulated bits in the last part.
    /// The parts are in reversed order and already hold correct bit sequences;
    /// we just have to put them together in the right way.
    /// </summary>
    public void FromStringBasePowerOfTwo(Span<ulong> Z, FromStringAccumulator accumulator)
    {
        int numParts = accumulator.ResultLength();
        Debug.Assert(numParts >= 1);
        Debug.Assert(Z.Length >= numParts);
        ReadOnlySpan<ulong> parts = accumulator._heapPartsSize == 0 ? accumulator._stackParts : accumulator._heapParts;
        byte radix = accumulator._radix;
        Debug.Assert(radix == 2 || radix == 4 || radix == 8 || radix == 16 || radix == 32);
        int charBits = Bigint.BitLength(radix - 1);
        int unusedLastPartBits = (int)accumulator._lastMultiplier;
        int unusedPartBits = Bigint.kDigitBits % charBits;
        int maxPartBits = Bigint.kDigitBits - unusedPartBits;
        int zIndex = 0;
        int partIndex = numParts - 1;

        // If the last part is fully populated, then all parts must be, and we can
        // simply copy them (in reversed order).
        if (unusedLastPartBits == 0)
        {
            Debug.Assert(Bigint.kDigitBits % charBits == 0);
            while (partIndex >= 0) Z[zIndex++] = parts[partIndex--];
            for (; zIndex < Z.Length; zIndex++) Z[zIndex] = 0;
            return;
        }

        // Otherwise we have to shift parts contents around as needed.
        // Holds the next Z digit that we want to store...
        ulong digit = parts[partIndex--];
        // ...and the number of bits (at the right end) we already know.
        int digitBits = Bigint.kDigitBits - unusedLastPartBits;
        while (partIndex >= 0)
        {
            // Holds the last part that we read from {parts}...
            ulong part = 0;
            // ...and the number of bits (at the right end) that we haven't used yet.
            int partBits = 0;
            while (digitBits < Bigint.kDigitBits)
            {
                part = parts[partIndex--];
                partBits = maxPartBits;
                digit |= part << digitBits;
                int partShift = Bigint.kDigitBits - digitBits;
                if (partShift > partBits)
                {
                    digitBits += partBits;
                    part = 0;
                    partBits = 0;
                    if (partIndex < 0) break;
                }
                else
                {
                    digitBits = Bigint.kDigitBits;
                    part >>= partShift;
                    partBits -= partShift;
                }
            }
            Z[zIndex++] = digit;
            digit = part;
            digitBits = partBits;
        }
        if (digitBits > 0) Z[zIndex++] = digit;
        for (; zIndex < Z.Length; zIndex++) Z[zIndex] = 0;
    }

    /// <summary>Z := the contents of accumulator.</summary>
    public Status FromString(Span<ulong> Z, FromStringAccumulator accumulator)
    {
        FromStringInternal(Z, accumulator);
        return get_and_clear_status();
    }

    void FromStringInternal(Span<ulong> Z, FromStringAccumulator accumulator)
    {
        if (accumulator._inlineEverything)
        {
            int i = 0;
            for (; i < accumulator._stackPartsUsed; i++) Z[i] = accumulator._stackParts[i];
            for (; i < Z.Length; i++) Z[i] = 0;
        }
        else if (accumulator._stackPartsUsed == 0)
        {
            for (int i = 0; i < Z.Length; i++) Z[i] = 0;
        }
        else if (Bigint.IsPowerOfTwo(accumulator._radix))
        {
            FromStringBasePowerOfTwo(Z, accumulator);
        }
        else if (accumulator.ResultLength() < BigintConfig.kFromStringLargeThreshold)
        {
            FromStringClassic(Z, accumulator);
        }
        else
        {
            FromStringLarge(Z, accumulator);
        }
    }
}
