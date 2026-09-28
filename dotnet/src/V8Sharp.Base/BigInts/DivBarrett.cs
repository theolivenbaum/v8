// Port of src/bigint/div-barrett.cc.
// Barrett division, finding the inverse with Newton's method.
// Reference: "Fast Division of Large Integers" by Karl Hasselström,
// found at https://treskal.com/s/masters-thesis.pdf

// Many thanks to Karl Wiberg, k@w5.se, for both writing up an
// understandable theoretical description of the algorithm and privately
// providing a demo implementation, on which the implementation in this file is
// based.

namespace V8Sharp.Base.BigInts;

public sealed partial class Processor
{
    [Conditional("DEBUG")]
    static void DcheckIntegerPartRange(ReadOnlySpan<ulong> X, ulong min, ulong max)
    {
        ulong integerPart = X[^1];
        Debug.Assert(integerPart >= min);
        Debug.Assert(integerPart <= max);
    }

    /// <summary>Z := (the fractional part of) 1/V, via naive division.
    /// See comments at Invert and InvertNewton below for details.</summary>
    public void InvertBasecase(Span<ulong> Z, ReadOnlySpan<ulong> V, Span<ulong> scratch)
    {
        Debug.Assert(Z.Length > V.Length);
        Debug.Assert(V.Length > 0);
        Debug.Assert(scratch.Length >= 2 * V.Length);
        int n = V.Length;
        Span<ulong> X = Bigint.Slice(scratch, 0, 2 * n);
        ulong borrow = 0;
        int i = 0;
        for (; i < n; i++) X[i] = 0;
        for (; i < 2 * n; i++) X[i] = Bigint.digit_sub2(0, V[i - n], borrow, out borrow);
        Debug.Assert(borrow == 1);
        // We don't need the remainder.
        if (n < BigintConfig.kBurnikelThreshold)
        {
            DivideSchoolbook(Z, [], X, V);
        }
        else
        {
            DivideBurnikelZiegler(Z, [], X, V);
        }
    }

    /// <summary>
    /// This is Algorithm 4.2 from the paper.
    /// Computes the inverse of V, shifted by kDigitBits * 2 * V.len, accurate
    /// to V.len+1 digits. The V.len low digits of the result digits will be
    /// written to Z, plus there is an implicit top digit with value 1.
    /// Needs InvertNewtonScratchSpace(V.len) of scratch space.
    /// The result is either correct or off by one (about half the time it is
    /// correct, half the time it is one too much, and in the corner case where
    /// V is minimal and the implicit top digit would have to be 2 it is one
    /// too little). Barrett's division algorithm can handle that.
    /// </summary>
    public void InvertNewton(Span<ulong> Z, ReadOnlySpan<ulong> V, Span<ulong> scratch)
    {
        int vn = V.Length;
        Debug.Assert(Z.Length >= vn);
        Debug.Assert(scratch.Length >= Bigint.InvertNewtonScratchSpace(vn));
        const int kSOffset = 0;
        const int kWOffset = 0;  // S and W can share their scratch space.
        int kUOffset = vn + Bigint.kInvertNewtonExtraSpace;

        // The base case won't work otherwise.
        Debug.Assert(V.Length >= 3);

        int basecasePrecision = Math.Min(BigintConfig.kNewtonInversionThreshold - 1, Bigint.DIV_CEIL(vn, 2));
        // V must have more digits than the basecase.
        Debug.Assert(V.Length > basecasePrecision);
        Debug.Assert(Bigint.IsBitNormalized(V));

        // Step (1): Setup.
        // Calculate precision required at each step.
        // {k} is the number of fraction bits for the current iteration.
        int k = vn * Bigint.kDigitBits;
        Span<int> targetFractionBits = stackalloc int[8 * sizeof(uint)];  // "k_i" in the paper.
        int iteration = -1;  // "i" in the paper, except inverted to run downwards.
        while (k > basecasePrecision * Bigint.kDigitBits)
        {
            iteration++;
            targetFractionBits[iteration] = k;
            k = Bigint.DIV_CEIL(k, 2);
        }
        // At this point, k <= kBasecasePrecision*kDigitBits is the number of
        // fraction bits to use in the base case. {iteration} is the highest index
        // in use for f[].

        // Step (2): Initial approximation.
        int initialDigits = Bigint.DIV_CEIL(k + 1, Bigint.kDigitBits);
        ReadOnlySpan<ulong> topPartOfV = Bigint.Slice(V, vn - initialDigits, initialDigits);
        InvertBasecase(Z, topPartOfV, scratch);
        Z[initialDigits] = Z[initialDigits] + 1;  // Implicit top digit.
        // From now on, we'll keep Z.len updated to the part that's already computed.
        Span<ulong> zFull = Z;
        Z = zFull[..(initialDigits + 1)];

        // Step (3): Precision doubling loop.
        while (true)
        {
            DcheckIntegerPartRange(Z, 1, 2);

            // (3b): S = Z^2
            Span<ulong> S = Bigint.Slice(scratch, kSOffset, 2 * Z.Length);
            Multiply(S, Z, Z);
            if (should_terminate()) return;
            S = S[..^1];  // Top digit of S is unused.
            DcheckIntegerPartRange(S, 1, 4);

            // (3c): T = V, truncated so that at least 2k+3 fraction bits remain.
            int fractionDigits = Bigint.DIV_CEIL(2 * k + 3, Bigint.kDigitBits);
            int tLen = Math.Min(V.Length, fractionDigits);
            ReadOnlySpan<ulong> T = Bigint.Slice(V, V.Length - tLen, tLen);

            // (3d): U = T * S, truncated so that at least 2k+1 fraction bits remain
            // (U has one integer digit, which might be zero).
            fractionDigits = Bigint.DIV_CEIL(2 * k + 1, Bigint.kDigitBits);
            Span<ulong> U = Bigint.Slice(scratch, kUOffset, S.Length + T.Length);
            Debug.Assert(U.Length > fractionDigits);
            Multiply(U, S, T);
            if (should_terminate()) return;
            U = U[(U.Length - (1 + fractionDigits))..];
            DcheckIntegerPartRange(U, 0, 3);

            // (3e): W = 2 * Z, padded with "0" fraction bits so that it has the
            // same number of fraction bits as U.
            Debug.Assert(U.Length >= Z.Length);
            Span<ulong> W = Bigint.Slice(scratch, kWOffset, U.Length);
            int paddingDigits = U.Length - Z.Length;
            for (int i = 0; i < paddingDigits; i++) W[i] = 0;
            Bigint.LeftShift(W[paddingDigits..], Z, 1);
            DcheckIntegerPartRange(W, 2, 4);

            // (3f): Z = W - U.
            // This check is '<=' instead of '<' because U's top digit is its
            // integer part, and we want vn fraction digits.
            if (U.Length <= vn)
            {
                // Normal subtraction.
                // This is not the last iteration.
                Debug.Assert(iteration > 0);
                Z = zFull[..U.Length];
                ulong borrow = Bigint.SubtractAndReturnBorrow(Z, W, U);
                Debug.Assert(borrow == 0);
                DcheckIntegerPartRange(Z, 1, 2);
            }
            else
            {
                // Truncate some least significant digits so that we get vn
                // fraction digits, and compute the integer digit separately.
                // This is the last iteration.
                Debug.Assert(iteration == 0);
                Z = zFull[..vn];
                ReadOnlySpan<ulong> WPart = Bigint.Slice(W, W.Length - vn - 1, vn);
                ReadOnlySpan<ulong> UPart = Bigint.Slice(U, U.Length - vn - 1, vn);
                ulong borrow = Bigint.SubtractAndReturnBorrow(Z, WPart, UPart);
                ulong integerPart = W[^1] - U[^1] - borrow;
                Debug.Assert(integerPart == 1 || integerPart == 2);
                if (integerPart == 2)
                {
                    // This is the rare case where the correct result would be 2.0, but
                    // since we can't express that by returning only the fractional part
                    // with an implicit 1-digit, we have to return [1.]9999... instead.
                    for (int i = 0; i < Z.Length; i++) Z[i] = ~0UL;
                }
                break;
            }
            // (3g, 3h): Update local variables and loop.
            k = targetFractionBits[iteration];
            iteration--;
        }
    }

    /// <summary>
    /// Computes the inverse of V, shifted by kDigitBits * 2 * V.len, accurate
    /// to V.len+1 digits. The V.len low digits of the result digits will be
    /// written to Z, plus there is an implicit top digit with value 1.
    /// (Corner case: if V is minimal, the implicit digit should be 2; in that
    /// case we return one less than the correct answer. DivideBarrett can
    /// handle that.) Needs InvertScratchSpace(V.len) digits of scratch space.
    /// </summary>
    public void Invert(Span<ulong> Z, ReadOnlySpan<ulong> V, Span<ulong> scratch)
    {
        Debug.Assert(Z.Length > V.Length);
        Debug.Assert(V.Length >= 1);
        Debug.Assert(Bigint.IsBitNormalized(V));
        Debug.Assert(scratch.Length >= Bigint.InvertScratchSpace(V.Length));

        int vn = V.Length;
        if (vn >= BigintConfig.kNewtonInversionThreshold)
        {
            InvertNewton(Z, V, scratch);
            return;
        }
        if (vn == 1)
        {
            ulong d = V[0];
            Z[0] = Bigint.digit_div(~d, ~0UL, d, out _);
            Z[1] = 0;
        }
        else
        {
            InvertBasecase(Z, V, scratch);
            if (Z[vn] == 1)
            {
                for (int i = 0; i < vn; i++) Z[i] = ~0UL;
                Z[vn] = 0;
            }
        }
    }

    /// <summary>
    /// This is algorithm 3.5 from the paper.
    /// Computes Q(uotient) and R(emainder) for A/B using I, which is a
    /// precomputed approximation of 1/B (e.g. with Invert() above).
    /// Needs DivideBarrettScratchSpace(A.len) scratch space.
    /// </summary>
    public void DivideBarrett(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B,
                              ReadOnlySpan<ulong> I, Span<ulong> scratch)
    {
        Debug.Assert(Q.Length > A.Length - B.Length);
        Debug.Assert(R.Length >= B.Length);
        Debug.Assert(A.Length > B.Length);  // Careful: This is *not* '>=' !
        Debug.Assert(A.Length <= 2 * B.Length);
        Debug.Assert(B.Length > 0);
        Debug.Assert(Bigint.IsBitNormalized(B));
        Debug.Assert(I.Length == A.Length - B.Length);
        Debug.Assert(scratch.Length >= Bigint.DivideBarrettScratchSpace(A.Length));

        Span<ulong> qFull = Q;

        // (1): A1 = A with B.len fewer digits.
        ReadOnlySpan<ulong> A1 = A[B.Length..];
        Debug.Assert(A1.Length == I.Length);

        // (2): Q = A1*I with I.len fewer digits.
        // {I} has an implicit high digit with value 1, so we add {A1} to the high
        // part of the multiplication result.
        Span<ulong> K = Bigint.Slice(scratch, 0, 2 * I.Length);
        Multiply(K, A1, I);
        if (should_terminate()) return;
        Q = qFull[..(I.Length + 1)];
        Bigint.Add(Q, K[I.Length..], A1);
        // K is no longer used, can reuse {scratch} for P.

        // (3): R = A - B*Q (approximate remainder).
        Span<ulong> P = Bigint.Slice(scratch, 0, A.Length + 1);
        Multiply(P, B, Q);
        if (should_terminate()) return;
        ulong borrow = Bigint.SubtractAndReturnBorrow(R, A, P[..B.Length]);
        // R may be allocated wider than B, zero out any extra digits if so.
        for (int i = B.Length; i < R.Length; i++) R[i] = 0;
        ulong rHigh = A[B.Length] - P[B.Length] - borrow;

        // Adjust R and Q so that they become the correct remainder and quotient.
        // The number of iterations is guaranteed to be at most some very small
        // constant, unless the caller gave us a bad approximate quotient.
        if (rHigh >> (Bigint.kDigitBits - 1) == 1)
        {
            // (5b): R < 0, so R += B
            ulong qSub = 0;
            do
            {
                rHigh += Bigint.InplaceAddAndReturnCarry(R, B);
                qSub++;
                Debug.Assert(qSub <= 5);
            } while (rHigh != 0);
            Bigint.Subtract(Q, qSub);
        }
        else
        {
            ulong qAdd = 0;
            while (rHigh != 0 || Bigint.GreaterThanOrEqual(R, B))
            {
                // (5c): R >= B, so R -= B
                rHigh -= Bigint.InplaceSubAndReturnBorrow(R, B);
                qAdd++;
                Debug.Assert(qAdd <= 5);
            }
            Bigint.Add(Q, qAdd);
        }
        // (5a): Return.
        int finalQLen = Q.Length;
        for (int i = finalQLen; i < qFull.Length; i++) qFull[i] = 0;
    }

    /// <summary>Computes Q(uotient) and R(emainder) for A/B, using Barrett division.</summary>
    public void DivideBarrett(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(Q.Length > A.Length - B.Length);
        Debug.Assert(R.Length >= B.Length);
        Debug.Assert(A.Length > B.Length);  // Careful: This is *not* '>=' !
        Debug.Assert(B.Length > 0);

        // Normalize B, and shift A by the same amount.
        ShiftedDigits bNormalized = new(B);
        ShiftedDigits aNormalized = new(A, bNormalized.Shift);
        // Keep the code below more concise.
        B = bNormalized.Digits;
        A = aNormalized.Digits;

        // The core DivideBarrett function above only supports A having at most
        // twice as many digits as B. We generalize this to arbitrary inputs
        // similar to Burnikel-Ziegler division by performing a t-by-1 division
        // of B-sized chunks. It's easy to special-case the situation where we
        // don't need to bother.
        int barrettDividendLength = A.Length <= 2 * B.Length ? A.Length : 2 * B.Length;
        int iLen = barrettDividendLength - B.Length;
        // +1 is for temporary use by Invert().
        ulong[] IStorage = new ulong[iLen + 1];
        int scratchLen = Math.Max(Bigint.InvertScratchSpace(iLen), Bigint.DivideBarrettScratchSpace(barrettDividendLength));
        ulong[] scratch = new ulong[scratchLen];
        Invert(IStorage, Bigint.Slice(B, B.Length - iLen, iLen), scratch);
        if (should_terminate()) return;
        Debug.Assert(IStorage[iLen] == 0);
        ReadOnlySpan<ulong> I = IStorage.AsSpan(0, iLen);  // I.TrimOne()
        if (A.Length > 2 * B.Length)
        {
            // This follows the variable names and and algorithmic steps of
            // DivideBurnikelZiegler().
            int n = B.Length;  // Chunk length.
            // (5): {t} is the number of B-sized chunks of A.
            int t = Bigint.DIV_CEIL(A.Length, n);
            Debug.Assert(t >= 3);
            // (6)/(7): Z is used for the current 2-chunk block to be divided by B,
            // initialized to the two topmost chunks of A.
            int zLen = n * 2;
            ulong[] Z = new ulong[zLen];
            Bigint.PutAt(Z, A[(n * (t - 2))..], zLen);
            // (8): For i from t-2 downto 0 do
            int qiLen = n + 1;
            ulong[] Qi = new ulong[qiLen];
            ulong[] Ri = new ulong[n];
            // First iteration unrolled and specialized.
            {
                int i = t - 2;
                DivideBarrett(Qi, Ri, Z, B, I, scratch);
                if (should_terminate()) return;
                Span<ulong> target = Q[(n * i)..];
                // In the first iteration, all qi_len = n + 1 digits may be used.
                int toCopy = Math.Min(qiLen, target.Length);
                for (int j = 0; j < toCopy; j++) target[j] = Qi[j];
                for (int j = toCopy; j < target.Length; j++) target[j] = 0;
            }
            // Now loop over any remaining iterations.
            for (int i = t - 3; i >= 0; i--)
            {
                // (8b): If i > 0, set Z_(i-1) = [Ri, A_(i-1)].
                // (De-duped with unrolled first iteration, hence reading A_(i).)
                Bigint.PutAt(Z.AsSpan(n), Ri, n);
                Bigint.PutAt(Z, A[(n * i)..], n);
                // (8a): Compute Qi, Ri such that Zi = B*Qi + Ri.
                DivideBarrett(Qi, Ri, Z, B, I, scratch);
                Debug.Assert(Qi[qiLen - 1] == 0);
                if (should_terminate()) return;
                // (9): Return Q = [Q_(t-2), ..., Q_0]...
                Bigint.PutAt(Q[(n * i)..], Qi, n);
            }
            ReadOnlySpan<ulong> riNormalized = Bigint.Normalize((ReadOnlySpan<ulong>)Ri);
            Debug.Assert(riNormalized.Length <= R.Length);
            // (9): ...and R = R_0 * 2^(-leading_zeros).
            Bigint.RightShift(R, riNormalized, bNormalized.Shift);
        }
        else
        {
            DivideBarrett(Q, R, A, B, I, scratch);
            if (should_terminate()) return;
            Bigint.RightShift(R, R, bNormalized.Shift);
        }
    }
}
