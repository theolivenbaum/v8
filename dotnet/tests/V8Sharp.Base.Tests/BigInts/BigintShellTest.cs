// Port of test/bigint/bigint-shell.cc: the bigint library's self-checks,
// comparing each advanced algorithm against its simpler reference. Each of
// the shell's tests ("karatsuba", "toom", ...) is a Fact; the random seed is
// the shell's default (--random-seed 314159265359).

using System.Text;
using V8Sharp.Base.BigInts;
using static V8Sharp.Base.BigInts.Bigint;

namespace V8Sharp.Base.Tests.BigInts;

public class BigintShellTest
{
    sealed class RNG
    {
        ulong _state0;
        ulong _state1;

        public RNG(long seed)
        {
            _state0 = MurmurHash3((ulong)seed);
            _state1 = MurmurHash3(~_state0);
            Assert.True(_state0 != 0 || _state1 != 0);
        }

        public ulong NextUint64()
        {
            XorShift128(ref _state0, ref _state1);
            return _state0 + _state1;
        }

        static void XorShift128(ref ulong state0, ref ulong state1)
        {
            ulong s1 = state0;
            ulong s0 = state1;
            state0 = s0;
            s1 ^= s1 << 23;
            s1 ^= s1 >> 17;
            s1 ^= s0;
            s1 ^= s0 >> 26;
            state1 = s1;
        }

        static ulong MurmurHash3(ulong h)
        {
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCD;
            h ^= h >> 33;
            h *= 0xC4CEB9FE1A85EC53;
            h ^= h >> 33;
            return h;
        }
    }

    const long kRandomSeed = 314159265359;
    readonly RNG _rng = new(kRandomSeed);
    readonly Processor _processor = new();

    static void GenerateRandomBits(RNG rng, Span<ulong> Z)
    {
        for (int i = 0; i < Z.Length; i++) Z[i] = rng.NextUint64();
        // Special case: we don't want the MSD to be zero.
        while (Z[^1] == 0) Z[^1] = rng.NextUint64();
    }

    static void GenerateRandom(RNG rng, Span<ulong> Z)
    {
        if (Z.Length == 0) return;
        int mode = (int)(rng.NextUint64() & 3);
        if (mode == 0)
        {
            GenerateRandomBits(rng, Z);
            return;
        }
        if (mode == 1)
        {
            // Generate a power of 2, with the lone 1-bit somewhere in the MSD.
            int bitInMsd = (int)(rng.NextUint64() % kDigitBits);
            Z[^1] = 1UL << bitInMsd;
            for (int i = 0; i < Z.Length - 1; i++) Z[i] = 0;
            return;
        }
        // For mode == 2 and mode == 3, generate a random number of 1-bits in the
        // MSD, aligned to the least-significant end.
        int bitsInMsd = (int)(rng.NextUint64() % kDigitBits);
        ulong msd = (1UL << bitsInMsd) - 1;
        if (msd == 0) msd = ~0UL;
        Z[^1] = msd;
        if (mode == 2)
        {
            // The non-MSD digits are all 1-bits.
            for (int i = 0; i < Z.Length - 1; i++) Z[i] = ~0UL;
        }
        else
        {
            // mode == 3
            // Each non-MSD digit is either all ones or all zeros.
            ulong random = 0;
            int randomBits = 0;
            for (int i = 0; i < Z.Length - 1; i++)
            {
                if (randomBits == 0)
                {
                    random = rng.NextUint64();
                    randomBits = 64;
                }
                Z[i] = (random & 1) != 0 ? ~0UL : 0UL;
                random >>= 1;
                randomBits--;
            }
        }
    }

    void GenerateRandom(Span<ulong> X) => GenerateRandom(_rng, X);

    static string FormatHex(ReadOnlySpan<ulong> X)
    {
        X = Normalize(X);
        if (X.Length == 0) return "0";
        StringBuilder sb = new();
        sb.Append(X[^1].ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        for (int i = X.Length - 2; i >= 0; i--) sb.Append(X[i].ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static void AssertEquals(ReadOnlySpan<ulong> input1, ReadOnlySpan<ulong> input2, ReadOnlySpan<ulong> expected, ReadOnlySpan<ulong> actual)
    {
        if (Compare(expected, actual) == 0) return;
        Assert.Fail($"Input 1:  {FormatHex(input1)}\nInput 2:  {FormatHex(input2)}\nExpected: {FormatHex(expected)}\nActual:   {FormatHex(actual)}");
    }

    [Fact]
    public void Karatsuba()
    {
        // Calling {MultiplyKaratsuba} directly is only valid if
        // left_size >= right_size and right_size >= kKaratsubaThreshold.
        const int kMin = BigintConfig.kKaratsubaThreshold;
        const int kMax = 3 * BigintConfig.kKaratsubaThreshold;
        for (int rightSize = kMin; rightSize <= kMax; rightSize++)
        {
            for (int leftSize = rightSize; leftSize <= kMax; leftSize++)
            {
                ulong[] A = new ulong[leftSize];
                ulong[] B = new ulong[rightSize];
                int resultLen = MultiplyResultLength(A, B);
                ulong[] result = new ulong[resultLen];
                ulong[] resultSchoolbook = new ulong[resultLen];
                GenerateRandom(A);
                GenerateRandom(B);
                _processor.MultiplyKaratsuba(result, A, B);
                MultiplySchoolbook(resultSchoolbook, A, B);
                AssertEquals(A, B, resultSchoolbook, result);
            }
        }
    }

    // Running Toom3 with less than 3 digits makes no sense.
    const int kMinToomDigits = 3;

    [Fact]
    public void Toom()
    {
        // {MultiplyToomCook} works fine even below the threshold, so we can
        // save some time by starting small.
        const int kMin = BigintConfig.kToomThreshold - 60;
        const int kMax = BigintConfig.kToomThreshold + 10;
        Assert.True(BigintConfig.kToomThreshold > 60 + kMinToomDigits);
        for (int rightSize = kMin; rightSize <= kMax; rightSize++)
        {
            for (int leftSize = rightSize; leftSize <= kMax; leftSize++)
            {
                ulong[] A = new ulong[leftSize];
                ulong[] B = new ulong[rightSize];
                int resultLen = MultiplyResultLength(A, B);
                ulong[] result = new ulong[resultLen];
                ulong[] resultKaratsuba = new ulong[resultLen];
                GenerateRandom(A);
                GenerateRandom(B);
                _processor.MultiplyToomCook(result, A, B);
                // Using Karatsuba as reference.
                _processor.MultiplyKaratsuba(resultKaratsuba, A, B);
                AssertEquals(A, B, resultKaratsuba, result);
            }
        }
    }

    [Fact]
    public void FFT()
    {
        // Larger multiplications are slower, so to keep individual runs fast,
        // we test a few random samples.
        ulong randomBits = _rng.NextUint64();
        int min = BigintConfig.kFftThreshold - (int)(randomBits & 511);
        randomBits >>= 10;
        int max = BigintConfig.kFftThreshold + (int)(randomBits & 1023);
        randomBits >>= 10;
        // If delta is too small, then this run gets too slow. If it happened
        // to be zero, we'd even loop forever!
        int delta = 10 + (int)(randomBits & 127);
        for (int rightSize = min; rightSize <= max; rightSize += delta)
        {
            for (int leftSize = rightSize; leftSize <= max; leftSize += delta)
            {
                ulong[] A = new ulong[leftSize];
                ulong[] B = new ulong[rightSize];
                int resultLen = MultiplyResultLength(A, B);
                ulong[] result = new ulong[resultLen];
                ulong[] resultToom = new ulong[resultLen];
                GenerateRandom(A);
                GenerateRandom(B);
                _processor.MultiplyFFT(result, A, B);
                // Using Toom-Cook as reference.
                _processor.MultiplyToomCook(resultToom, A, B);
                AssertEquals(A, B, resultToom, result);
            }
        }
    }

    [Fact]
    public void Burnikel()
    {
        // Start small to save test execution time.
        const int kMin = BigintConfig.kBurnikelThreshold / 2;
        const int kMax = 2 * BigintConfig.kBurnikelThreshold;
        for (int rightSize = kMin; rightSize <= kMax; rightSize++)
        {
            for (int leftSize = rightSize; leftSize <= kMax; leftSize++)
            {
                ulong[] A = new ulong[leftSize];
                ulong[] B = new ulong[rightSize];
                GenerateRandom(A);
                GenerateRandom(B);
                int quotientLen = DivideResultLength(A, B);
                int remainderLen = rightSize;
                ulong[] quotient = new ulong[quotientLen];
                ulong[] quotientSchoolbook = new ulong[quotientLen];
                ulong[] remainder = new ulong[remainderLen];
                ulong[] remainderSchoolbook = new ulong[remainderLen];
                _processor.DivideBurnikelZiegler(quotient, remainder, A, B);
                _processor.DivideSchoolbook(quotientSchoolbook, remainderSchoolbook, A, B);
                AssertEquals(A, B, quotientSchoolbook, quotient);
                AssertEquals(A, B, remainderSchoolbook, remainder);
            }
        }
    }

    [Fact]
    public void CachedMod()
    {
        // We could support b_len == 1, but it's not relevant in practice and
        // the implementation would have to special-case it.
        for (int bLen = 2; bLen <= 20; bLen++)
        {
            ulong[] B = new ulong[bLen];
            ulong[] R = new ulong[bLen];
            ulong[] RExp = new ulong[bLen];
            GenerateRandom(B);
            _processor.CachedMod_MakeInverse(B);
            // The length of the cached inverse determines the maximum {a_len} we
            // can handle.
            for (int aLen = bLen; aLen <= 2 * bLen; aLen++)
            {
                ulong[] A = new ulong[aLen];
                for (int j = 0; j < 200; j++)
                {
                    GenerateRandom(A);
                    _processor.CachedMod(R, A);

                    var (done, _) = ModuloSmall(RExp, A, B);
                    if (!done) _processor.ModuloLarge(RExp, A, B);

                    AssertEquals(A, B, RExp, R);
                }
            }
        }
    }

    void TestBarrett_Internal(int leftSize, int rightSize)
    {
        ulong[] A = new ulong[leftSize];
        ulong[] B = new ulong[rightSize];
        GenerateRandom(A);
        GenerateRandom(B);
        int quotientLen = DivideResultLength(A, B);
        // {DivideResultLength} doesn't expect to be called for sizes below
        // {kBarrettThreshold} (which we do here to save time), so we have to
        // manually adjust the allocated result length.
        if (B.Length < BigintConfig.kBarrettThreshold) quotientLen++;
        int remainderLen = rightSize;
        ulong[] quotient = new ulong[quotientLen];
        ulong[] quotientBurnikel = new ulong[quotientLen];
        ulong[] remainder = new ulong[remainderLen];
        ulong[] remainderBurnikel = new ulong[remainderLen];
        _processor.DivideBarrett(quotient, remainder, A, B);
        _processor.DivideBurnikelZiegler(quotientBurnikel, remainderBurnikel, A, B);
        AssertEquals(A, B, quotientBurnikel, quotient);
        AssertEquals(A, B, remainderBurnikel, remainder);
    }

    [Fact]
    public void Barrett()
    {
        // We pick a range around kBurnikelThreshold (instead of kBarrettThreshold)
        // to save test execution time.
        const int kMin = BigintConfig.kBurnikelThreshold / 2;
        const int kMax = 2 * BigintConfig.kBurnikelThreshold;
        // {DivideBarrett(A, B)} requires that A.len > B.len!
        for (int rightSize = kMin; rightSize <= kMax; rightSize++)
        {
            for (int leftSize = rightSize + 1; leftSize <= kMax; leftSize++)
            {
                TestBarrett_Internal(leftSize, rightSize);
            }
        }
        // We also test one random large case.
        ulong randomBits = _rng.NextUint64();
        int rs = BigintConfig.kBarrettThreshold + (int)(randomBits & 0x3FF);
        randomBits >>= 10;
        int ls = rs + 1 + (int)(randomBits & 0x3FFF);
        TestBarrett_Internal(ls, rs);
    }

    [Fact]
    public void ToString_()  // "tostring"; ToString() would hide object.ToString.
    {
        const int kMin = BigintConfig.kToStringFastThreshold / 2;
        const int kMax = BigintConfig.kToStringFastThreshold * 2;
        for (int size = kMin; size < kMax; size++)
        {
            ulong[] X = new ulong[size];
            GenerateRandom(X);
            for (int radix = 2; radix <= 36; radix++)
            {
                int charsRequired = ToStringResultLength(X, radix, false);
                int resultLen = charsRequired;
                int referenceLen = charsRequired;
                char[] result = new char[resultLen];
                char[] reference = new char[referenceLen];
                _processor.ToStringImpl(result, ref resultLen, X, radix, false, true);
                _processor.ToStringImpl(reference, ref referenceLen, X, radix, false, false);
                Assert.Equal(new string(reference, 0, referenceLen), new string(result, 0, resultLen));
            }
        }
    }

    void GenerateRandomString(char[] str, int len, int radix)
    {
        if (len == 0) return;
        ulong random = 0;
        int availableBits = 0;
        int charBits = BitLength(radix - 1);
        ulong charMask = (1UL << charBits) - 1UL;
        for (int i = 0; i < len; i++)
        {
            while (true)
            {
                if (availableBits < charBits)
                {
                    random = _rng.NextUint64();
                    availableBits = 64;
                }
                int nextChar = (int)(random & charMask);
                random >>= charBits;
                availableBits -= charBits;
                if (nextChar >= radix) continue;
                str[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[nextChar];
                break;
            }
        }
    }

    [Fact]
    public void FromString()
    {
        const int kMaxDigits = 1 << 20;  // Any large-enough value will do.
        const int kMin = BigintConfig.kFromStringLargeThreshold / 2;
        const int kMax = BigintConfig.kFromStringLargeThreshold * 2;
        for (int size = kMin; size < kMax; size++)
        {
            // To keep test execution times low, test one random radix every time.
            // Generally, radixes 2 through 36 (inclusive) are supported; however
            // the functions {FromStringLarge} and {FromStringClassic} can't deal
            // with the data format that {Parse} creates for power-of-two radixes,
            // so we skip power-of-two radixes here (and test them separately below).
            byte[] radixes = [3, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
                              28, 29, 30, 31, 33, 34, 35, 36, 10, 10];
            int radixIndex = (int)(_rng.NextUint64() & 31);
            int radix = radixes[radixIndex];
            int numChars = (int)Math.Round(size * kDigitBits / Math.Log2(radix));
            char[] chars = new char[numChars];
            GenerateRandomString(chars, numChars, radix);
            FromStringAccumulator accumulator = new(kMaxDigits);
            FromStringAccumulator refAccumulator = new(kMaxDigits);
            accumulator.Parse(chars, (ulong)radix);
            refAccumulator.Parse(chars, (ulong)radix);
            ulong[] result = new ulong[accumulator.ResultLength()];
            ulong[] reference = new ulong[refAccumulator.ResultLength()];
            _processor.FromStringLarge(result, accumulator);
            _processor.FromStringClassic(reference, refAccumulator);
            AssertEquals(result, reference, reference, result);
        }
    }

    [Fact]
    public void FromStringBase2()
    {
        const int kMaxDigits = 1 << 20;  // Any large-enough value will do.
        const int kMin = 1;
        const int kMax = 100;
        for (int size = kMin; size < kMax; size++)
        {
            ulong[] X = new ulong[size];
            GenerateRandom(X);
            for (int bits = 1; bits <= 5; bits++)
            {
                int radix = 1 << bits;
                int charsRequired = ToStringResultLength(X, radix, false);
                int stringLen = charsRequired;
                char[] chars = new char[stringLen];
                _processor.ToStringImpl(chars, ref stringLen, X, radix, false, true);
                // Fill any remaining allocated characters with garbage to test that
                // too.
                for (int i = stringLen; i < charsRequired; i++) chars[i] = '?';
                FromStringAccumulator accumulator = new(kMaxDigits);
                accumulator.Parse(chars, (ulong)radix);
                ulong[] result = new ulong[accumulator.ResultLength()];
                _processor.FromString(result, accumulator);
                AssertEquals(X, X, X, result);
            }
        }
    }
}
