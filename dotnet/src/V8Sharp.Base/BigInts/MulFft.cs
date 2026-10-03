// Port of src/bigint/mul-fft.cc.
// FFT-based multiplication, due to Schönhage and Strassen.
// This implementation mostly follows the description given in:
// Christoph Lüders: Fast Multiplication of Large Integers,
// http://arxiv.org/abs/1503.04955

namespace V8Sharp.Base.BigInts;

// Part 1: Functions for "mod F_n" arithmetic.
// F_n is of the shape 2^K + 1, and for convenience we use K to count the
// number of digits rather than the number of bits, so F_n (or K) are implicit
// and deduced from the length {len} of the digits array.
static class FftModFn
{
    const int kDigitBits = Bigint.kDigitBits;

    // Helper function for {ModFn} below.
    static void ModFn_Helper(Span<ulong> x, int len, long high)
    {
        if (high > 0)
        {
            ulong borrow = (ulong)high;
            x[len - 1] = 0;
            for (int i = 0; i < len; i++)
            {
                x[i] = Bigint.digit_sub(x[i], borrow, out borrow);
                if (borrow == 0) break;
            }
        }
        else
        {
            ulong carry = (ulong)(-high);
            x[len - 1] = 0;
            for (int i = 0; i < len; i++)
            {
                x[i] = Bigint.digit_add2(x[i], carry, out carry);
                if (carry == 0) break;
            }
        }
    }

    // {x} := {x} mod F_n, assuming that {x} is "slightly" larger than F_n (e.g.
    // after addition of two numbers that were mod-F_n-normalized before).
    public static void ModFn(Span<ulong> x, int len)
    {
        int K = len - 1;
        long high = (long)x[K];
        if (high == 0) return;
        ModFn_Helper(x, len, high);
        high = (long)x[K];
        if (high == 0) return;
        Debug.Assert(high == 1 || high == -1);
        ModFn_Helper(x, len, high);
        high = (long)x[K];
        if (high == -1) ModFn_Helper(x, len, high);
    }

    // {dest} := {src} mod F_n, assuming that {src} is about twice as long as F_n
    // (e.g. after multiplication of two numbers that were mod-F_n-normalized
    // before).
    // {len} is length of {dest}; {src} is twice as long.
    public static void ModFnDoubleWidth(Span<ulong> dest, ReadOnlySpan<ulong> src, int len)
    {
        int K = len - 1;
        ulong borrow = 0;
        for (int i = 0; i < K; i++) dest[i] = Bigint.digit_sub2(src[i], src[i + K], borrow, out borrow);
        dest[K] = Bigint.digit_sub2(0, src[2 * K], borrow, out borrow);
        // {borrow} may be non-zero here, that's OK as {ModFn} will take care of it.
        ModFn(dest, len);
    }

    // Sets {sum} := {a} + {b} and {diff} := {a} - {b}, which is more efficient
    // than computing sum and difference separately. Applies "mod F_n" normalization
    // to both results.
    public static void SumDiff(Span<ulong> sum, Span<ulong> diff, ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, int len)
    {
        ulong carry = 0;
        ulong borrow = 0;
        for (int i = 0; i < len; i++)
        {
            // Read both values first, because inputs and outputs can overlap.
            ulong ai = a[i];
            ulong bi = b[i];
            sum[i] = Bigint.digit_add3(ai, bi, carry, out carry);
            diff[i] = Bigint.digit_sub2(ai, bi, borrow, out borrow);
        }
        ModFn(sum, len);
        ModFn(diff, len);
    }

    // {result} := ({input} << shift) mod F_n, where shift >= K.
    static void ShiftModFn_Large(Span<ulong> result, ReadOnlySpan<ulong> input, int digitShift, int bitsShift, int K)
    {
        // If {digit_shift} is greater than K, we use the following transformation
        // (where, since everything is mod 2^K + 1, we are allowed to add or
        // subtract any multiple of 2^K + 1 at any time):
        //      x * 2^{K+m}   mod 2^K + 1
        //   == x * 2^K * 2^m - (2^K + 1)*(x * 2^m)   mod 2^K + 1
        //   == x * 2^K * 2^m - x * 2^K * 2^m - x * 2^m   mod 2^K + 1
        //   == -x * 2^m   mod 2^K + 1
        // So the flow is the same as for m < K, but we invert the subtraction's
        // operands. In order to avoid underflow, we virtually initialize the
        // result to 2^K + 1:
        //   input  =  [ iK ][iK-1] ....  .... [ i1 ][ i0 ]
        //   result =  [   1][0000] ....  .... [0000][0001]
        //            +                  [ iK ] .... [ iX ]
        //            -      [iX-1] .... [ i0 ]
        Debug.Assert(digitShift >= K);
        digitShift -= K;
        ulong borrow = 0;
        if (bitsShift == 0)
        {
            ulong carry = 1;
            for (int i = 0; i < digitShift; i++) result[i] = Bigint.digit_add2(input[i + K - digitShift], carry, out carry);
            result[digitShift] = Bigint.digit_sub(input[K] + carry, input[0], out borrow);
            for (int i = digitShift + 1; i < K; i++)
            {
                ulong d = input[i - digitShift];
                result[i] = Bigint.digit_sub2(0, d, borrow, out borrow);
            }
        }
        else
        {
            ulong addCarry = 1;
            ulong inputCarry = input[K - digitShift - 1] >> (kDigitBits - bitsShift);
            for (int i = 0; i < digitShift; i++)
            {
                ulong d = input[i + K - digitShift];
                ulong summand = (d << bitsShift) | inputCarry;
                result[i] = Bigint.digit_add2(summand, addCarry, out addCarry);
                inputCarry = d >> (kDigitBits - bitsShift);
            }
            {
                // result[digit_shift] = (add_carry + iK_part) - i0_part
                ulong d = input[K];
                ulong iKPart = (d << bitsShift) | inputCarry;
                ulong iKCarry = d >> (kDigitBits - bitsShift);
                ulong sum = Bigint.digit_add2(addCarry, iKPart, out addCarry);
                // {iK_carry} is less than a full digit, so we can merge {add_carry}
                // into it without overflow.
                iKCarry += addCarry;
                d = input[0];
                ulong i0Part = d << bitsShift;
                result[digitShift] = Bigint.digit_sub(sum, i0Part, out borrow);
                inputCarry = d >> (kDigitBits - bitsShift);
                if (digitShift + 1 < K)
                {
                    d = input[1];
                    ulong subtrahend = (d << bitsShift) | inputCarry;
                    result[digitShift + 1] = Bigint.digit_sub2(iKCarry, subtrahend, borrow, out borrow);
                    inputCarry = d >> (kDigitBits - bitsShift);
                }
            }
            for (int i = digitShift + 2; i < K; i++)
            {
                ulong d = input[i - digitShift];
                ulong subtrahend = (d << bitsShift) | inputCarry;
                result[i] = Bigint.digit_sub2(0, subtrahend, borrow, out borrow);
                inputCarry = d >> (kDigitBits - bitsShift);
            }
        }
        // The virtual 1 in result[K] should be eliminated by {borrow}. If there
        // is no borrow, then the virtual initialization was too much. Subtract
        // 2^K + 1.
        result[K] = 0;
        if (borrow != 1)
        {
            borrow = 1;
            for (int i = 0; i < K; i++)
            {
                result[i] = Bigint.digit_sub(result[i], borrow, out borrow);
                if (borrow == 0) break;
            }
            if (borrow != 0)
            {
                // The result must be 2^K.
                for (int i = 0; i < K; i++) result[i] = 0;
                result[K] = 1;
            }
        }
    }

    // Sets {result} := {input} * 2^{power_of_two} mod 2^{K} + 1.
    // This function is highly relevant for overall performance.
    public static void ShiftModFn(Span<ulong> result, ReadOnlySpan<ulong> input, int powerOfTwo, int K, int zeroAbove = 0x7FFFFFFF)
    {
        // The modulo-reduction amounts to a subtraction, which we combine
        // with the shift as follows:
        //   input  =  [ iK ][iK-1] ....  .... [ i1 ][ i0 ]
        //   result =        [iX-1] .... [ i0 ] <---------- shift by {power_of_two}
        //            -                  [ iK ] .... [ iX ]
        // where "X" is the index "K - digit_shift".
        int digitShift = powerOfTwo / kDigitBits;
        int bitsShift = powerOfTwo % kDigitBits;
        // By an analogous construction to the "digit_shift >= K" case,
        // it turns out that:
        //    x * 2^{2K+m} == x * 2^m   mod 2^K + 1.
        while (digitShift >= 2 * K) digitShift -= 2 * K;  // Faster than '%'!
        if (digitShift >= K)
        {
            ShiftModFn_Large(result, input, digitShift, bitsShift, K);
            return;
        }
        ulong borrow = 0;
        if (bitsShift == 0)
        {
            // We do a single pass over {input}, starting by copying digits [i1] to
            // [iX-1] to result indices digit_shift+1 to K-1.
            int i = 1;
            // Read input digits unless we know they are zero.
            int cap = Math.Min(K - digitShift, zeroAbove);
            for (; i < cap; i++) result[i + digitShift] = input[i];
            // Any remaining work can hard-code the knowledge that input[i] == 0.
            for (; i < K - digitShift; i++)
            {
                Debug.Assert(input[i] == 0);
                result[i + digitShift] = 0;
            }
            // Second phase: subtract input digits [iX] to [iK] from (virtually) zero-
            // initialized result indices 0 to digit_shift-1.
            cap = Math.Min(K, zeroAbove);
            for (; i < cap; i++)
            {
                ulong d = input[i];
                result[i - K + digitShift] = Bigint.digit_sub2(0, d, borrow, out borrow);
            }
            // Any remaining work can hard-code the knowledge that input[i] == 0.
            for (; i < K; i++)
            {
                Debug.Assert(input[i] == 0);
                result[i - K + digitShift] = Bigint.digit_sub(0, borrow, out borrow);
            }
            // Last step: subtract [iK] from [i0] and store at result index digit_shift.
            result[digitShift] = Bigint.digit_sub2(input[0], input[K], borrow, out borrow);
        }
        else
        {
            // Same flow as before, but taking bits_shift != 0 into account.
            // First phase: result indices digit_shift+1 to K.
            ulong carry = 0;
            int i = 0;
            // Read input digits unless we know they are zero.
            int cap = Math.Min(K - digitShift, zeroAbove);
            for (; i < cap; i++)
            {
                ulong d = input[i];
                result[i + digitShift] = (d << bitsShift) | carry;
                carry = d >> (kDigitBits - bitsShift);
            }
            // Any remaining work can hard-code the knowledge that input[i] == 0.
            for (; i < K - digitShift; i++)
            {
                Debug.Assert(input[i] == 0);
                result[i + digitShift] = carry;
                carry = 0;
            }
            // Second phase: result indices 0 to digit_shift - 1.
            cap = Math.Min(K, zeroAbove);
            for (; i < cap; i++)
            {
                ulong d = input[i];
                result[i - K + digitShift] = Bigint.digit_sub2(0, (d << bitsShift) | carry, borrow, out borrow);
                carry = d >> (kDigitBits - bitsShift);
            }
            // Any remaining work can hard-code the knowledge that input[i] == 0.
            if (i < K)
            {
                Debug.Assert(input[i] == 0);
                result[i - K + digitShift] = Bigint.digit_sub2(0, carry, borrow, out borrow);
                carry = 0;
                i++;
            }
            for (; i < K; i++)
            {
                Debug.Assert(input[i] == 0);
                result[i - K + digitShift] = Bigint.digit_sub(0, borrow, out borrow);
            }
            // Last step: compute result[digit_shift].
            ulong dk = input[K];
            result[digitShift] = Bigint.digit_sub2(result[digitShift], (dk << bitsShift) | carry, borrow, out borrow);
            // No carry left.
            Debug.Assert((dk >> (kDigitBits - bitsShift)) == 0);
        }
        result[K] = 0;
        for (int i = digitShift + 1; i <= K && borrow > 0; i++) result[i] = Bigint.digit_sub(result[i], borrow, out borrow);
        if (borrow > 0)
        {
            // Underflow means we subtracted too much. Add 2^K + 1.
            ulong carry = 1;
            for (int i = 0; i <= K; i++)
            {
                result[i] = Bigint.digit_add2(result[i], carry, out carry);
                if (carry == 0) break;
            }
            result[K] = Bigint.digit_add2(result[K], 1, out carry);
        }
    }
}

// Part 2: FFT-based multiplication is very sensitive to appropriate choice
// of parameters. The following functions choose the parameters that the
// subsequent actual computation will use. This is partially based on formal
// constraints and partially on experimentally-determined heuristics.
struct FftParameters
{
    public int m;
    public int K;
    public int n;
    public int s;
    public int r;

    // Computes parameters for the main calculation, given a bit length {N} and
    // an {m}. See the paper for details.
    public static void ComputeParameters(int N, int m, out FftParameters p)
    {
        p = default;
        N *= Bigint.kDigitBits;
        int n = 1 << m;  // 2^m
        int nhalf = n >> 1;
        int s = (N + n - 1) >> m;  // ceil(N/n)
        s = Bigint.RoundUp(s, Bigint.kDigitBits);
        int K = m + 2 * s + 1;  // K must be at least this big...
        K = Bigint.RoundUp(K, nhalf);       // ...and a multiple of n/2.
        int r = K >> (m - 1);   // Which multiple?

        // We want recursive calls to make progress, so force K to be a multiple
        // of 8 if it's above the recursion threshold. Otherwise, K must be a
        // multiple of kDigitBits.
        int threshold = (K + 1 >= BigintConfig.kFftInnerThreshold * Bigint.kDigitBits)
            ? 3 + Bigint.kLog2DigitBits
            : Bigint.kLog2DigitBits;
        int K_tz = Bigint.CountTrailingZeros((uint)K);
        while (K_tz < threshold)
        {
            K += 1 << K_tz;
            r = K >> (m - 1);
            K_tz = Bigint.CountTrailingZeros((uint)K);
        }

        Debug.Assert(K % Bigint.kDigitBits == 0);
        Debug.Assert(s % Bigint.kDigitBits == 0);
        p.K = K / Bigint.kDigitBits;
        p.s = s / Bigint.kDigitBits;
        p.n = n;
        p.r = r;
    }

    // Computes parameters for recursive invocations ("inner layer").
    public static void ComputeParameters_Inner(int N, out FftParameters p)
    {
        p = default;
        int maxM = Bigint.CountTrailingZeros((uint)N);
        int NBits = Bigint.BitLength(N);
        int m = NBits - 4;  // Don't let s get too small.
        m = Math.Min(maxM, m);
        N *= Bigint.kDigitBits;
        int n = 1 << m;  // 2^m
        // We can't round up s in the inner layer, because N = n*s is fixed.
        int s = N >> m;
        Debug.Assert(N == s * n);
        int K = m + 2 * s + 1;  // K must be at least this big...
        K = Bigint.RoundUp(K, n);           // ...and a multiple of n and kDigitBits.
        K = Bigint.RoundUp(K, Bigint.kDigitBits);
        p.r = K >> m;  // Which multiple?
        Debug.Assert(K % Bigint.kDigitBits == 0);
        Debug.Assert(s % Bigint.kDigitBits == 0);
        p.K = K / Bigint.kDigitBits;
        p.s = s / Bigint.kDigitBits;
        p.n = n;
        p.m = m;
    }

    static int PredictInnerK(int N)
    {
        ComputeParameters_Inner(N, out FftParameters p);
        return p.K;
    }

    // Applies heuristics to decide whether {m} should be decremented, by looking
    // at what would happen to {K} and {s} if {m} was decremented.
    static bool ShouldDecrementM(in FftParameters current, in FftParameters next, in FftParameters afterNext)
    {
        // K == 64 seems to work particularly well.
        if (current.K == 64 && next.K >= 112) return false;
        // Small values for s are never efficient.
        if (current.s < 6) return true;
        // The time is roughly determined by K * n. When we decrement m, then
        // n always halves, and K usually gets bigger, by up to 2x.
        // For not-quite-so-small s, look at how much bigger K would get: if
        // the K increase is small enough, making n smaller is worth it.
        // Empirically, it's most meaningful to look at the K *after* next.
        // The specific threshold values have been chosen by running many
        // benchmarks on inputs of many sizes, and manually selecting thresholds
        // that seemed to produce good results.
        double factor = (double)afterNext.K / current.K;
        if ((current.s == 6 && factor < 3.85) ||
            (current.s == 7 && factor < 3.73) ||
            (current.s == 8 && factor < 3.55) ||
            (current.s == 9 && factor < 3.50) ||
            factor < 3.4)
        {
            return true;
        }
        // If K is just below the recursion threshold, make sure we do recurse,
        // unless doing so would be particularly inefficient (large inner_K).
        // If K is just above the recursion threshold, doubling it often makes
        // the inner call more efficient.
        if (current.K >= 160 && current.K < 250 && PredictInnerK(next.K) < 28) return true;
        // If we found no reason to decrement, keep m as large as possible.
        return false;
    }

    // Decides what parameters to use for a given input bit length {N}.
    // Returns the chosen m.
    public static int GetParameters(int N, out FftParameters p)
    {
        int NBits = Bigint.BitLength(N);
        int maxM = NBits - 3;                   // Larger m make s too small.
        maxM = Math.Max(Bigint.kLog2DigitBits, maxM);  // Smaller m break the logic below.
        int m = maxM;
        ComputeParameters(N, m, out FftParameters current);
        ComputeParameters(N, m - 1, out FftParameters next);
        while (m > 2)
        {
            ComputeParameters(N, m - 2, out FftParameters afterNext);
            if (ShouldDecrementM(current, next, afterNext))
            {
                m--;
                current = next;
                next = afterNext;
            }
            else
            {
                break;
            }
        }
        p = current;
        return m;
    }
}

// Part 3: Fast Fourier Transformation.
sealed class FFTContainer
{
    readonly int _n;       // Number of parts.
    readonly int _K;       // Always length_ - 1.
    readonly int _length;  // Length of each part, in digits.
    readonly Processor _processor;
    readonly ulong[] _storage;  // Combined storage of all parts.
    readonly ulong[] _temp;     // Temporary storage with size 2 * length_.

    // {n} is the number of chunks, whose length is {K}+1.
    // {K} determines F_n = 2^(K * kDigitBits) + 1.
    public FFTContainer(int n, int K, Processor processor)
    {
        _n = n;
        _K = K;
        _length = K + 1;
        _processor = processor;
        _storage = new ulong[_length * n];
        _temp = new ulong[_length * 2];
    }

    Span<ulong> Part(int i) => _storage.AsSpan(i * _length, _length);

    static void CopyAndZeroExtend(Span<ulong> dst, ReadOnlySpan<ulong> src, int digitsToCopy)
    {
        src[..digitsToCopy].CopyTo(dst);
        dst[digitsToCopy..].Clear();
    }

    // Reads {X} into the FFTContainer's internal storage, dividing it into chunks
    // while doing so; then performs the forward FFT.
    public void Start_Default(ReadOnlySpan<ulong> X, int chunkSize, int theta, int omega)
    {
        int len = X.Length;
        int pointer = 0;
        int currentTheta = 0;
        int i = 0;
        for (; i < _n && len > 0; i++, currentTheta += theta)
        {
            chunkSize = Math.Min(chunkSize, len);
            // For invocations via MultiplyFFT_Inner, X.len() == n_ * chunk_size + 1,
            // because the outer layer's "K" is passed as the inner layer's "N".
            // Since X is (mod Fn)-normalized on the outer layer, there is the rare
            // corner case where X[n_ * chunk_size] == 1. Detect that case, and handle
            // the extra bit as part of the last chunk; we always have the space.
            if (i == _n - 1 && len == chunkSize + 1)
            {
                Debug.Assert(X[_n * chunkSize] <= 1);
                Debug.Assert(_length >= chunkSize + 1);
                chunkSize++;
            }
            if (currentTheta != 0)
            {
                // Multiply with theta^i, and reduce modulo 2^K + 1.
                // We pass theta as a shift amount; it really means 2^theta.
                CopyAndZeroExtend(_temp.AsSpan(0, _length), X[pointer..], chunkSize);
                FftModFn.ShiftModFn(Part(i), _temp, currentTheta, _K, chunkSize);
            }
            else
            {
                CopyAndZeroExtend(Part(i), X[pointer..], chunkSize);
            }
            pointer += chunkSize;
            len -= chunkSize;
        }
        Debug.Assert(len == 0);
        for (; i < _n; i++) Part(i).Clear();
        FFT_ReturnShuffledThreadsafe(0, _n, omega, _temp);
    }

    // This version of Start is optimized for the case where ~half of the
    // container will be filled with padding zeros.
    public void Start(ReadOnlySpan<ulong> X, int chunkSize, int theta, int omega)
    {
        int len = X.Length;
        if (len > _n * chunkSize / 2)
        {
            Start_Default(X, chunkSize, theta, omega);
            return;
        }
        Debug.Assert(theta == 0);
        int pointer = 0;
        int nhalf = _n / 2;
        // Unrolled first iteration.
        chunkSize = Math.Min(chunkSize, len);
        CopyAndZeroExtend(Part(0), X[pointer..], chunkSize);
        CopyAndZeroExtend(Part(nhalf), X[pointer..], chunkSize);
        pointer += chunkSize;
        len -= chunkSize;
        int i = 1;
        for (; i < nhalf && len > 0; i++)
        {
            chunkSize = Math.Min(chunkSize, len);
            CopyAndZeroExtend(Part(i), X[pointer..], chunkSize);
            int w = omega * i;
            FftModFn.ShiftModFn(Part(i + nhalf), Part(i), w, _K, chunkSize);
            pointer += chunkSize;
            len -= chunkSize;
        }
        for (; i < nhalf; i++)
        {
            Part(i).Clear();
            Part(i + nhalf).Clear();
        }
        FFT_Recurse(0, nhalf, omega, _temp);
    }

    // Forward transformation.
    // We use the "DIF" aka "decimation in frequency" transform, because it
    // leaves the result in "bit reversed" order, which is precisely what we
    // need as input for the "DIT" aka "decimation in time" backwards transform.
    void FFT_ReturnShuffledThreadsafe(int start, int len, int omega, ulong[] temp)
    {
        Debug.Assert((len & 1) == 0);  // {len} must be even.
        int half = len / 2;
        FftModFn.SumDiff(Part(start), Part(start + half), Part(start), Part(start + half), _length);
        for (int k = 1; k < half; k++)
        {
            FftModFn.SumDiff(Part(start + k), temp, Part(start + k), Part(start + half + k), _length);
            int w = omega * k;
            FftModFn.ShiftModFn(Part(start + half + k), temp, w, _K);
        }
        FFT_Recurse(start, half, omega, temp);
    }

    // Recursive step of the above, factored out for additional callers.
    void FFT_Recurse(int start, int half, int omega, ulong[] temp)
    {
        if (half > 1)
        {
            FFT_ReturnShuffledThreadsafe(start, half, 2 * omega, temp);
            FFT_ReturnShuffledThreadsafe(start + half, half, 2 * omega, temp);
        }
    }

    // Backward transformation.
    // We use the "DIT" aka "decimation in time" transform here, because it
    // turns bit-reversed input into normally sorted output.
    public void BackwardFFT(int start, int len, int omega) => BackwardFFT_Threadsafe(start, len, omega, _temp);

    void BackwardFFT_Threadsafe(int start, int len, int omega, ulong[] temp)
    {
        Debug.Assert((len & 1) == 0);  // {len} must be even.
        int half = len / 2;
        // Don't recurse for half == 2, as PointwiseMultiply already performed
        // the first level of the backwards FFT.
        if (half > 2)
        {
            BackwardFFT_Threadsafe(start, half, 2 * omega, temp);
            BackwardFFT_Threadsafe(start + half, half, 2 * omega, temp);
        }
        FftModFn.SumDiff(Part(start), Part(start + half), Part(start), Part(start + half), _length);
        for (int k = 1; k < half; k++)
        {
            int w = omega * (len - k);
            FftModFn.ShiftModFn(temp, Part(start + half + k), w, _K);
            FftModFn.SumDiff(Part(start + k), Part(start + half + k), Part(start + k), temp, _length);
        }
    }

    // Recombines the result's parts into {Z}, after backwards FFT.
    public void NormalizeAndRecombine(int omega, int m, Span<ulong> Z, int chunkSize)
    {
        Z.Clear();
        int zIndex = 0;
        int shift = _n * omega - m;
        for (int i = 0; i < _n; i++, zIndex += chunkSize)
        {
            FftModFn.ShiftModFn(_temp, Part(i), shift, _K);
            ulong carry = 0;
            int zi = zIndex;
            int j = 0;
            for (; j < _length && zi < Z.Length; j++, zi++) Z[zi] = Bigint.digit_add3(Z[zi], _temp[j], carry, out carry);
            for (; j < _length; j++) Debug.Assert(_temp[j] == 0);
            if (carry != 0) Z[zi] = carry;
        }
    }

    // Helper function for {CounterWeightAndRecombine} below.
    static bool ShouldBeNegative(ReadOnlySpan<ulong> x, int xlen, ulong threshold, int s)
    {
        if (x[2 * s] >= threshold) return true;
        for (int i = 2 * s + 1; i < xlen; i++)
        {
            if (x[i] > 0) return true;
        }
        return false;
    }

    // Same as {NormalizeAndRecombine} above, but for the needs of the recursive
    // invocation ("inner layer") of FFT multiplication, where an additional
    // counter-weighting step is required.
    public void CounterWeightAndRecombine(int theta, int m, Span<ulong> Z, int s)
    {
        Z.Clear();
        int zIndex = 0;
        for (int k = 0; k < _n; k++, zIndex += s)
        {
            int shift = -theta * k - m;
            if (shift < 0) shift += 2 * _n * theta;
            Debug.Assert(shift >= 0);
            FftModFn.ShiftModFn(_temp, Part(k), shift, _K);
            int remainingZ = Z.Length - zIndex;
            if (ShouldBeNegative(_temp, _length, (ulong)(k + 1), s))
            {
                // Subtract F_n from input before adding to result. We use the following
                // transformation (knowing that X < F_n):
                // Z + (X - F_n) == Z - (F_n - X)
                ulong borrowZ;
                ulong borrowFn;
                {
                    // i == 0:
                    ulong d = Bigint.digit_sub(1, _temp[0], out borrowFn);
                    Z[zIndex] = Bigint.digit_sub(Z[zIndex], d, out borrowZ);
                }
                int i = 1;
                for (; i < _K && i < remainingZ; i++)
                {
                    ulong d = Bigint.digit_sub2(0, _temp[i], borrowFn, out borrowFn);
                    Z[zIndex + i] = Bigint.digit_sub2(Z[zIndex + i], d, borrowZ, out borrowZ);
                }
                Debug.Assert(i == _K && _K == _length - 1);
                for (; i < _length && i < remainingZ; i++)
                {
                    ulong d = Bigint.digit_sub2(1, _temp[i], borrowFn, out borrowFn);
                    Z[zIndex + i] = Bigint.digit_sub2(Z[zIndex + i], d, borrowZ, out borrowZ);
                }
                Debug.Assert(borrowFn == 0);
                for (; borrowZ > 0 && i < remainingZ; i++) Z[zIndex + i] = Bigint.digit_sub(Z[zIndex + i], borrowZ, out borrowZ);
            }
            else
            {
                ulong carry = 0;
                int i = 0;
                for (; i < _length && i < remainingZ; i++) Z[zIndex + i] = Bigint.digit_add3(Z[zIndex + i], _temp[i], carry, out carry);
                for (; i < _length; i++) Debug.Assert(_temp[i] == 0);
                for (; carry > 0 && i < remainingZ; i++) Z[zIndex + i] = Bigint.digit_add2(Z[zIndex + i], carry, out carry);
                // {carry} might be != 0 here if Z was negative before. That's fine.
            }
        }
    }

    // Main FFT function for recursive invocations ("inner layer").
    static void MultiplyFFT_Inner(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, in FftParameters p, Processor processor)
    {
        int omega = 2 * p.r;  // really: 2^(2r)
        int theta = p.r;      // really: 2^r

        FFTContainer a = new(p.n, p.K, processor);
        a.Start_Default(X, p.s, theta, omega);
        FFTContainer b = new(p.n, p.K, processor);
        b.Start_Default(Y, p.s, theta, omega);

        a.PointwiseMultiply(b);
        if (processor.should_terminate()) return;

        FFTContainer c = a;
        c.BackwardFFT(0, p.n, omega);

        c.CounterWeightAndRecombine(theta, p.m, Z, p.s);
    }

    // Actual implementation of pointwise multiplications.
    void DoPointwiseMultiplication(FFTContainer other, int start, int end, ulong[] temp)
    {
        // The (K_ & 3) != 0 condition makes sure that the inner FFT gets
        // to split the work into at least 4 chunks.
        bool useFft = _length >= BigintConfig.kFftInnerThreshold && (_K & 3) == 0;
        FftParameters p = default;
        if (useFft) FftParameters.ComputeParameters_Inner(_K, out p);
        Span<ulong> result = temp.AsSpan(0, 2 * _length);
        for (int i = start; i < end; i++)
        {
            ReadOnlySpan<ulong> A = Part(i);
            ReadOnlySpan<ulong> B = other.Part(i);
            if (useFft)
            {
                MultiplyFFT_Inner(result, A, B, p, _processor);
            }
            else
            {
                _processor.Multiply(result, A, B);
            }
            if (_processor.should_terminate()) return;
            FftModFn.ModFnDoubleWidth(Part(i), result, _length);
            // To improve cache friendliness, we perform the first level of the
            // backwards FFT here.
            if ((i & 1) == 1) FftModFn.SumDiff(Part(i - 1), Part(i), Part(i - 1), Part(i), _length);
        }
    }

    // Convenient entry point for pointwise multiplications.
    public void PointwiseMultiply(FFTContainer other)
    {
        Debug.Assert(_n == other._n);
        DoPointwiseMultiplication(other, 0, _n, _temp);
    }
}

public sealed partial class Processor
{
    // Part 4: Tying everything together into a multiplication algorithm.
    public void MultiplyFFT(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        const int kAsymmetricChunkingThreshold = 100;

        FftParameters p;
        if (Bigint.SameDigits(X, Y))
        {
            // Squaring.
            int m = FftParameters.GetParameters(X.Length * 2, out p);
            int omega = p.r;  // really: 2^r
            FFTContainer a = new(p.n, p.K, this);
            a.Start(X, p.s, 0, omega);
            a.PointwiseMultiply(a);
            if (should_terminate()) return;
            a.BackwardFFT(0, p.n, omega);
            a.NormalizeAndRecombine(omega, m, Z, p.s);
        }
        else if (X.Length > Y.Length * kAsymmetricChunkingThreshold)
        {
            // Asymmetric input sizes. Proceed in chunks. See {MultiplyToomCook()}.
            int k = Y.Length;
            int m = FftParameters.GetParameters(k * 2, out p);
            int omega = p.r;  // really: 2^r
            // The container {b} only needs to be initialized once, whereas {a} will
            // be reused for each chunk.
            FFTContainer b = new(p.n, p.K, this);
            b.Start(Y, p.s, 0, omega);
            FFTContainer a = new(p.n, p.K, this);
            // Unroll the first iteration to initialize {Z}.
            ReadOnlySpan<ulong> X0 = Bigint.Slice(X, 0, k);
            a.Start(X0, p.s, 0, omega);
            a.PointwiseMultiply(b);
            if (should_terminate()) return;
            a.BackwardFFT(0, p.n, omega);
            a.NormalizeAndRecombine(omega, m, Z, p.s);
            // Then loop for the remaining chunks.
            ulong[] T = new ulong[2 * k];
            for (int i = k; i < X.Length; i += k)
            {
                ReadOnlySpan<ulong> Xi = Bigint.Slice(X, i, k);
                a.Start(Xi, p.s, 0, omega);
                a.PointwiseMultiply(b);
                if (should_terminate()) return;
                a.BackwardFFT(0, p.n, omega);
                a.NormalizeAndRecombine(omega, m, T, p.s);
                Bigint.AddAndReturnOverflow(Z[i..], T);  // Can't overflow.
            }
        }
        else
        {
            // Similar-ish sized inputs. Handle them in one go.
            int m = FftParameters.GetParameters(X.Length + Y.Length, out p);
            int omega = p.r;  // really: 2^r

            FFTContainer a = new(p.n, p.K, this);
            a.Start(X, p.s, 0, omega);
            FFTContainer b = new(p.n, p.K, this);
            b.Start(Y, p.s, 0, omega);
            a.PointwiseMultiply(b);
            if (should_terminate()) return;
            a.BackwardFFT(0, p.n, omega);
            a.NormalizeAndRecombine(omega, m, Z, p.s);
        }
    }
}
