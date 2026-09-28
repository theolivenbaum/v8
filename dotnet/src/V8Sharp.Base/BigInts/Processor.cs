// Port of src/bigint/bigint-internal.h, bigint-internal.cc and the Processor
// parts of bigint.h / bigint-inl.h (CachedMod).
//
// V8 splits the class into Processor (public) and ProcessorImpl (internal);
// its own TODO suggests merging them, which is what this port does. The
// Platform (allocator + interrupt hook) becomes plain managed allocation and an
// optional InterruptRequested callback.

namespace V8Sharp.Base.BigInts;

public sealed partial class Processor
{
    /// <summary>Queried every now and then by long-running operations, giving
    /// embedders the ability to interrupt them (Platform::InterruptRequested).</summary>
    public Func<bool>? InterruptRequested { get; set; }

    Status _status = Status.kOk;
    ulong _workEstimate;

    public Processor(Func<bool>? interruptRequested = null) => InterruptRequested = interruptRequested;

    // Number of digits to keep around, to reduce the number of allocations.
    // Arbitrarily chosen; should be large enough to hold a few scratch areas
    // for commonly-occurring BigInt sizes. In particular, make it large enough
    // for the needs of {CachedMod}.
    public const int kSmallScratchSize = 100;

    // Modulo division that's optimized for small inputs and repeatedly-used
    // divisors. 32 digits is enough for 2048 bit divisors on a 64-bit platform.
    public const int kMaxCachedModDivisorSize = 32;

    ulong[]? _smallScratch;
    ulong[]? _cachedInverseStorage;
    int _cachedDivisorLength;
    int _cachedInverseLength;
    int _cachedInverseAllocatedLength;
    int _divisorCount;
    ulong _cachedModFoldFactor;

    Span<ulong> GetSmallScratch() => (_smallScratch ??= new ulong[kSmallScratchSize]);

    Span<ulong> GetSmallScratch_NoCheck()
    {
        Debug.Assert(_smallScratch != null);
        return _smallScratch;
    }

    /// <summary>Each unit is supposed to represent approximately one CPU mul
    /// instruction. We check for interrupt requests every now and then.</summary>
    public const ulong kWorkEstimateThreshold = 5000000;

    public Status get_and_clear_status()
    {
        Status result = _status;
        _status = Status.kOk;
        return result;
    }

    public bool should_terminate() => _status == Status.kInterrupted;

    public void AddWorkEstimate(ulong estimate)
    {
        _workEstimate += estimate;
        if (_workEstimate >= kWorkEstimateThreshold)
        {
            _workEstimate = 0;
            if (InterruptRequested?.Invoke() == true) _status = Status.kInterrupted;
        }
    }

    /// <summary>Callers that want to ensure that the next small operation
    /// won't be the one that exhausts the interrupt check budget can reset it.</summary>
    public void ResetInterruptCheckBudget() => _workEstimate = 0;

    public int inc_divisor_count() => ++_divisorCount;
    public void reset_divisor_count() => _divisorCount = 1;

    // ---- multiplication dispatch -----------------------------------------------

    public void Multiply(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        X = Bigint.Normalize(X);
        Y = Bigint.Normalize(Y);
        if (X.Length == 0 || Y.Length == 0)
        {
            Z.Clear();
            return;
        }
        if (X.Length < Y.Length)
        {
            ReadOnlySpan<ulong> t = X;
            X = Y;
            Y = t;
        }
        if (Y.Length == 1)
        {
            Bigint.MultiplySingle(Z, X, Y[0]);
            AddWorkEstimate((ulong)X.Length);
            return;
        }
        if (Y.Length < BigintConfig.kKaratsubaThreshold)
        {
            Bigint.MultiplySchoolbook(Z, X, Y);
            AddWorkEstimate((ulong)X.Length * (ulong)Y.Length);
            return;
        }
        MultiplyLargeImpl(Z, X, Y);
    }

    void MultiplyLargeImpl(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(Bigint.IsDigitNormalized(X));
        Debug.Assert(Bigint.IsDigitNormalized(Y));
        Debug.Assert(X.Length >= Y.Length);
        if (X.Length > Bigint.kMaxNumDigits) throw new InvalidOperationException("BigInt too large");
        if (Y.Length < BigintConfig.kToomThreshold)
        {
            MultiplyKaratsuba(Z, X, Y);
            return;
        }
        if (Y.Length < BigintConfig.kFftThreshold)
        {
            MultiplyToomCook(Z, X, Y);
            return;
        }
        MultiplyFFT(Z, X, Y);
    }

    /// <summary>Z := X * Y, for inputs MultiplySmall declined.</summary>
    public Status MultiplyLarge(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        MultiplyLargeImpl(Z, X, Y);
        return get_and_clear_status();
    }

    /// <summary>Q := A / B, for inputs DivideSmall declined.</summary>
    public Status DivideLarge(Span<ulong> Q, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(Bigint.IsDigitNormalized(A));
        Debug.Assert(Bigint.IsDigitNormalized(B));
        Debug.Assert(B.Length > 1);
        Span<ulong> R0 = [];
        if (B.Length < BigintConfig.kBurnikelThreshold)
        {
            DivideSchoolbook(Q, R0, A, B);
            return get_and_clear_status();
        }
        if (A.Length > Bigint.kMaxNumDigits) throw new InvalidOperationException("BigInt too large");
        if (B.Length < BigintConfig.kBarrettThreshold || A.Length == B.Length)
        {
            DivideBurnikelZiegler(Q, R0, A, B);
        }
        else
        {
            ulong[] R = new ulong[B.Length];
            DivideBarrett(Q, R, A, B);
        }
        return get_and_clear_status();
    }

    /// <summary>R := A % B, for inputs ModuloSmall declined.</summary>
    public Status ModuloLarge(Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(Bigint.IsDigitNormalized(A));
        Debug.Assert(Bigint.IsDigitNormalized(B));
        Debug.Assert(B.Length > 1);
        if (B.Length < BigintConfig.kBurnikelThreshold)
        {
            DivideSchoolbook([], R, A, B);
            return get_and_clear_status();
        }
        if (A.Length >= Bigint.kMaxNumDigits) throw new InvalidOperationException("BigInt too large");
        int qLen = Bigint.DivideResultLength(A, B);
        ulong[] Q = new ulong[qLen];
        if (B.Length < BigintConfig.kBarrettThreshold || A.Length == B.Length)
        {
            DivideBurnikelZiegler(Q, R, A, B);
        }
        else
        {
            DivideBarrett(Q, R, A, B);
        }
        return get_and_clear_status();
    }

    // ---- CachedMod ---------------------------------------------------------------

    Span<ulong> CachedDivisor => _cachedInverseStorage.AsSpan(0, _cachedDivisorLength);
    Span<ulong> CachedInverse => _cachedInverseStorage.AsSpan(_cachedDivisorLength, _cachedInverseLength);

    /// <summary>The divisor given to CachedMod_MakeInverse.</summary>
    public ReadOnlySpan<ulong> GetCachedDivisor()
    {
        Debug.Assert(_cachedInverseStorage != null);
        return CachedDivisor;
    }

    Span<ulong> AllocateCachedInverseFor(ReadOnlySpan<ulong> divisor)
    {
        int divisorLen = divisor.Length;
        int inverseLen = divisorLen + 1;
        int len = divisorLen + inverseLen;
        if (len > _cachedInverseAllocatedLength)
        {
            _cachedInverseStorage = new ulong[len];
            _cachedInverseAllocatedLength = len;
        }
        _cachedDivisorLength = divisorLen;
        _cachedInverseLength = inverseLen;
        divisor.CopyTo(CachedDivisor);
        return CachedInverse;
    }

    /// <summary>Prepares CachedMod for the divisor B (canonicalized, 2 &lt;=
    /// B.len &lt;= kMaxCachedModDivisorSize).</summary>
    public void CachedMod_MakeInverse(ReadOnlySpan<ulong> B)
    {
        int n = B.Length;
        Span<ulong> Inv = AllocateCachedInverseFor(B);
        // Use the cached copy of B now, just to prevent any confusion arising
        // from concurrent mutation.
        ReadOnlySpan<ulong> cachedB = CachedDivisor;
        if (cachedB[^1] == 0) throw new ArgumentException("B must have been canonicalized");

        _cachedModFoldFactor = 0;

        // {CachedMod} relies on the "small scratch" having been allocated, so make
        // sure that has happened regardless of {DivideSchoolbook}'s internal
        // decisions, and of which of the two paths below we take.
        GetSmallScratch();

        // We can't use {GetSmallScratch()} for the working space below, because
        // {DivideSchoolbook} already does that (usually). One allocation serves both
        // the fold check below and the inverse computation afterwards, which needs
        // {n * 2} digits.
        ulong[] scratch = new ulong[n + 1 + 2 + n];

        {
            // T := 1 << (n * kDigitBits), i.e. the smallest power of the digit base
            // above the divisor. If T == q * B + C has a small q and a single-digit C,
            // then C is the factor {CachedModFold} folds the dividend's high half by.
            Span<ulong> T = scratch.AsSpan(0, n + 1);
            T.Clear();
            T[n] = 1;

            Span<ulong> Q = scratch.AsSpan(n + 1, 2);
            Span<ulong> R = scratch.AsSpan(n + 3, n);
            DivideSchoolbook(Q, R, T, cachedB);

            Q = Bigint.Normalize(Q);
            R = Bigint.Normalize(R);
            // Cap the quotient so the corrective while loop in {CachedModFold} doesn't
            // have to subtract the divisor too many times.
            const ulong kMaxFoldQuotient = 4;
            if (Q.Length == 1 && Q[0] <= kMaxFoldQuotient && R.Length == 1)
            {
                _cachedModFoldFactor = R[0];
                return;
            }
        }

        Span<ulong> A = scratch.AsSpan(0, n * 2);
        // The idea is to set A = 1 << 2*n, and then compute Inv = A / B. However,
        // having that lone "1" bit in A's top digit is inefficient and far-reaching:
        // it requires us to make Inv bigger too. So we use a trick: first we
        // set A = (1 << 2*n) - (B << n), which eliminates the top digit. Then
        // after the division we undo the subtraction by adding back 1 << n.
        // We also approximate the subtraction by using bit negation instead, which
        // avoids the need for propagating borrows and is almost the same in two's
        // complement: -x == ~x + 1. We model the +1 by setting the lower part of
        // A to all 1-bits (effectively: -x ~= ~x + 0.99999...).
        int i = 0;
        for (; i < n; i++) A[i] = ~0UL;
        for (; i < A.Length; i++) A[i] = ~cachedB[i - n];
        DivideSchoolbook(Inv, [], A, cachedB);
        Bigint.Add(Inv[n..], 1);

        // If we add 1 here, we increase our chances of getting lucky in the
        // corrective loop in {CachedDiv}. But don't do it when there's a risk
        // of overflowing {inv}.
        if (Inv[0] != ~0UL || Inv[^1] != ~0UL) Bigint.Add(Inv, 1);
    }

    // Crandall reduction, used by {CachedMod} when the cached divisor is close
    // enough to a power of the digit base for {c} to be armed.
    ulong CachedModFold(Span<ulong> R, ReadOnlySpan<ulong> A, ulong c)
    {
        ReadOnlySpan<ulong> B = CachedDivisor;
        int n = B.Length;
        Debug.Assert(n >= 2);
        Debug.Assert(n <= A.Length && A.Length <= 2 * n);
        Debug.Assert(R.Length == n);

        // Each column sums A[i] + A[n+i]*c + carry, which for digit base D is at
        // most (D-1) + (D-1)^2 + (D-1) == D^2 - 1. It therefore fits in two digits
        // and {carry} stays single-digit for any {c}, including ~digit_t{0}.
        ulong carry = 0;
        for (int i = 0; i < n; ++i)
        {
            ulong high = 0;
            // A is always at least n digits: the only caller is {BigIntModulusImpl},
            // which returns early when |x| < |y|.
            ulong low = A[i];
            if (n + i < A.Length)
            {
                ulong productLow = Bigint.digit_mul(A[n + i], c, out high);
                low = Bigint.digit_add2(low, productLow, out ulong addCarry);
                high += addCarry;
            }
            R[i] = Bigint.digit_add2(low, carry, out ulong sumCarry);
            Debug.Assert(high <= ~0UL - sumCarry);
            carry = high + sumCarry;
        }

        if (carry != 0)
        {
            ulong productLow = Bigint.digit_mul(carry, c, out ulong productHigh);
            R[0] = Bigint.digit_add2(R[0], productLow, out carry);
            carry += productHigh;
            for (int i = 1; i < n && carry != 0; ++i) R[i] = Bigint.digit_add2(R[i], carry, out carry);
        }

        while (carry != 0 || Bigint.GreaterThanOrEqual(R, B)) carry -= Bigint.InplaceSubAndReturnBorrow(R, B);

        return R[n - 1];
    }

    /// <summary>R := A % (the divisor given to CachedMod_MakeInverse), for
    /// B.len &lt;= A.len &lt;= 2 * B.len. R.len must equal B.len. Returns R's
    /// top digit.</summary>
    public ulong CachedMod(Span<ulong> R, ReadOnlySpan<ulong> A)
    {
        ulong c = _cachedModFoldFactor;
        if (c != 0)
        {
            // A fold-armed divisor leaves {cached_inverse_} allocated but stale; this
            // path never reads it.
            return CachedModFold(R, A, c);
        }

        ReadOnlySpan<ulong> B = CachedDivisor;
        ReadOnlySpan<ulong> inv = CachedInverse;
        int n = B.Length;
        Debug.Assert(n >= 2);
        Debug.Assert(n <= A.Length && A.Length <= 2 * n);
        Debug.Assert(inv.Length == n + 1);
        Debug.Assert(R.Length == n);

        int scratchSpace = A.Length + inv.Length;
        // We rely on {CachedMod_MakeInverse} having allocated the scratch space,
        // and on that scratch space being big enough for our needs:
        // Since A.len() <= 2*n and inv.len == n+1, scratch_space == 3n + 1,
        // and {n} is capped by the max cached divisor size.
        Span<ulong> scratch = GetSmallScratch_NoCheck()[..scratchSpace];

        // Perform multiplication with inverse to get an estimated quotient.
        // Q lives at offset 2n; only columns [2n - 2, ...) are needed to
        // recover it. The two skipped columns of carry leave Q at most 1
        // below the true quotient; the correction loop further down recovers
        // the off-by-one.
        int startPosition = 2 * n - 2;
        if (A.Length >= inv.Length)
        {
            Bigint.MultiplySpecialHighInternal(scratch, A, inv, startPosition);
        }
        else
        {
            Bigint.MultiplySpecialHighInternal(scratch, inv, A, startPosition);
        }
        ReadOnlySpan<ulong> Q = scratch[(2 * n)..];

        // Perform correction.
        // We don't need to compute the high part of the product because it's
        // identical to A's high part anyway.
        // The length of Q is at least 1 and at most n + 1. The length of B is n.
        Span<ulong> productLow = Bigint.Slice(scratch, 0, n + 1);
        Bigint.MultiplySpecialLowInternal(productLow, B, Q);

        ulong borrow = Bigint.SubtractAndReturnBorrow(R, A, productLow[..n]);
        ulong An = A.Length > n ? A[n] : 0;
        ulong rHigh = An - productLow[n] - borrow;
        if ((rHigh & (1UL << (Bigint.kDigitBits - 1))) != 0)
        {
            do
            {
                rHigh += Bigint.InplaceAddAndReturnCarry(R, B);
            } while (rHigh != 0);
        }
        else
        {
            while (rHigh != 0 || Bigint.GreaterThanOrEqual(R, B)) rHigh -= Bigint.InplaceSubAndReturnBorrow(R, B);
        }
        return R[n - 1];
    }
}

public static partial class Bigint
{
    internal static void MultiplySpecialHighInternal(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, int startPosition) =>
        MultiplySpecialHigh(Z, X, Y, startPosition);

    internal static void MultiplySpecialLowInternal(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y) =>
        MultiplySpecialLow(Z, X, Y);
}
