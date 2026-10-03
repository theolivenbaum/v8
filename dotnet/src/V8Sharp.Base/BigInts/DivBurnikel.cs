// Port of src/bigint/div-burnikel.cc.
// Burnikel-Ziegler division.
// Reference: "Fast Recursive Division" by Christoph Burnikel and Joachim
// Ziegler, found at http://cr.yp.to/bib/1998/burnikel.ps

namespace V8Sharp.Base.BigInts;

public sealed partial class Processor
{
    // Compares [a_high, A] with B.
    // Returns:
    // - a value < 0 if [a_high, A] < B
    // - 0           if [a_high, A] == B
    // - a value > 0 if [a_high, A] > B.
    static int SpecialCompare(ulong aHigh, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        B = Bigint.Normalize(B);
        int aLen;
        if (aHigh == 0)
        {
            A = Bigint.Normalize(A);
            aLen = A.Length;
        }
        else
        {
            aLen = A.Length + 1;
        }
        int diff = aLen - B.Length;
        if (diff != 0) return diff;
        int i = aLen - 1;
        if (aHigh != 0)
        {
            if (aHigh > B[i]) return 1;
            if (aHigh < B[i]) return -1;
            i--;
        }
        while (i >= 0 && A[i] == B[i]) i--;
        if (i < 0) return 0;
        return A[i] > B[i] ? 1 : -1;
    }

    // Since the Burnikel-Ziegler method is inherently recursive, V8 puts
    // non-changing data into a container object (class BZ); here that is the
    // scratch memory passed along.
    void BZDivideBasecase(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        A = Bigint.Normalize(A);
        B = Bigint.Normalize(B);
        Debug.Assert(B.Length > 0);
        int cmp = Bigint.Compare(A, B);
        if (cmp <= 0)
        {
            Q.Clear();
            if (cmp == 0)
            {
                // If A == B, then Q=1, R=0.
                R.Clear();
                Q[0] = 1;
            }
            else
            {
                // If A < B, then Q=0, R=A.
                Bigint.PutAt(R, A, R.Length);
            }
            return;
        }
        if (B.Length == 1)
        {
            Bigint.DivideSingle(Q, out R[0], A, B[0]);
            AddWorkEstimate((ulong)A.Length);
            return;
        }
        DivideSchoolbook(Q, R, A, B);
    }

    // Algorithm 2 from the paper. Variable names same as there.
    // Returns Q(uotient) and R(emainder) for A/B, with B having two thirds
    // the size of A = [A1, A2, A3].
    void BZD3n2n(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A1A2, ReadOnlySpan<ulong> A3, ReadOnlySpan<ulong> B,
                 ulong[] scratchMem)
    {
        Debug.Assert((B.Length & 1) == 0);
        int n = B.Length / 2;
        Debug.Assert(A1A2.Length == 2 * n);
        // Actual condition is stricter than length: A < B * 2^(kDigitBits * n)
        Debug.Assert(Bigint.Compare(A1A2, B) < 0);
        Debug.Assert(A3.Length == n);
        Debug.Assert(Q.Length == n);
        Debug.Assert(R.Length == 2 * n);
        // 1. Split A into three parts A = [A1, A2, A3] with Ai < 2^(kDigitBits * n).
        ReadOnlySpan<ulong> A1 = Bigint.Slice(A1A2, n, n);
        // 2. Split B into two parts B = [B1, B2] with Bi < 2^(kDigitBits * n).
        ReadOnlySpan<ulong> B1 = Bigint.Slice(B, n, n);
        ReadOnlySpan<ulong> B2 = Bigint.Slice(B, 0, n);
        // 3. Distinguish the cases A1 < B1 or A1 >= B1.
        Span<ulong> Qhat = Q;
        Span<ulong> R1 = Bigint.Slice(R, n, n);
        ulong r1High = 0;
        if (Bigint.Compare(A1, B1) < 0)
        {
            // 3a. If A1 < B1, compute Qhat = floor([A1, A2] / B1) with remainder R1
            //     using algorithm D2n1n.
            BZD2n1n(Qhat, R1, A1A2, B1, scratchMem);
            if (should_terminate()) return;
        }
        else
        {
            // 3b. If A1 >= B1, set Qhat = 2^(kDigitBits * n) - 1 and set
            //     R1 = [A1, A2] - [B1, 0] + [0, B1]
            Qhat.Fill(ulong.MaxValue);
            // Step 1: compute A1 - B1, which can't underflow because of the comparison
            // guarding this else-branch, and always has a one-digit result because
            // of this function's preconditions.
            Span<ulong> temp = R1;
            Bigint.SubtractWithNormalization(temp, A1, B1);
            temp = Bigint.Normalize(temp);
            Debug.Assert(temp.Length <= 1);
            if (temp.Length > 0) r1High = temp[0];
            // Step 2: compute A2 + B1.
            ReadOnlySpan<ulong> A2 = Bigint.Slice(A1A2, 0, n);
            r1High += Bigint.AddAndReturnCarry(R1, A2, B1);
        }
        // 4. Compute D = Qhat * B2 using (Karatsuba) multiplication.
        Span<ulong> D = scratchMem.AsSpan(0, 2 * n);
        Multiply(D, Qhat, B2);
        if (should_terminate()) return;

        // 5. Compute Rhat = R1*2^(kDigitBits * n) + A3 - D = [R1, A3] - D.
        Bigint.PutAt(R, A3, n);
        // 6. As long as Rhat < 0, repeat:
        while (SpecialCompare(r1High, R, D) < 0)
        {
            // 6a. Rhat = Rhat + B
            r1High += Bigint.InplaceAddAndReturnCarry(R, B);
            // 6b. Qhat = Qhat - 1
            Bigint.Subtract(Qhat, 1);
        }
        // 5. Compute Rhat = R1*2^(kDigitBits * n) + A3 - D = [R1, A3] - D.
        ulong borrow = Bigint.InplaceSubAndReturnBorrow(R, D);
        Debug.Assert(borrow == r1High);
        Debug.Assert(Bigint.Compare(R, B) < 0);
        // 7. Return R = Rhat, Q = Qhat.
    }

    // Algorithm 1 from the paper. Variable names same as there.
    // Returns Q(uotient) and (R)emainder for A/B, with A twice the size of B.
    void BZD2n1n(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B, ulong[] scratchMem)
    {
        int n = B.Length;
        Debug.Assert(A.Length <= 2 * n);
        Debug.Assert(Q.Length == n);
        Debug.Assert(R.Length == n);
        // 1. If n is odd or smaller than some convenient constant, compute Q and R
        //    by school division and return.
        if ((n & 1) == 1 || n < BigintConfig.kBurnikelThreshold)
        {
            BZDivideBasecase(Q, R, A, B);
            return;
        }
        // 2. Split A into four parts A = [A1, ..., A4] with
        //    Ai < 2^(kDigitBits * n / 2). Split B into two parts [B2, B1] with
        //    Bi < 2^(kDigitBits * n / 2).
        ReadOnlySpan<ulong> A1A2 = Bigint.Slice(A, n, n);
        ReadOnlySpan<ulong> A3 = Bigint.Slice(A, n / 2, n / 2);
        ReadOnlySpan<ulong> A4 = Bigint.Slice(A, 0, n / 2);
        // 3. Compute the high part Q1 of floor(A/B) as
        //    Q1 = floor([A1, A2, A3] / [B1, B2]) with remainder R1 = [R11, R12],
        //    using algorithm D3n2n.
        Span<ulong> Q1 = Bigint.Slice(Q, n / 2, n / 2);
        ulong[] R1 = new ulong[n];
        BZD3n2n(Q1, R1, A1A2, A3, B, scratchMem);
        if (should_terminate()) return;
        // 4. Compute the low part Q2 of floor(A/B) as
        //    Q2 = floor([R11, R12, A4] / [B1, B2]) with remainder R, using
        //    algorithm D3n2n.
        Span<ulong> Q2 = Bigint.Slice(Q, 0, n / 2);
        BZD3n2n(Q2, R, R1, A4, B, scratchMem);
        // 5. Return Q = [Q1, Q2] and R.
    }

    /// <summary>
    /// Algorithm 3 from the paper. Returns Q(uotient) and R(emainder) for A/B
    /// (no size restrictions). R is optional, Q is not.
    /// </summary>
    public void DivideBurnikelZiegler(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(A.Length >= B.Length);
        Debug.Assert(R.Length == 0 || R.Length >= B.Length);
        Debug.Assert(Q.Length > A.Length - B.Length);
        int r = A.Length;
        int s = B.Length;
        // The requirements are:
        // - n >= s, n as small as possible.
        // - m must be a power of two.
        // 1. Set m = min {2^k | 2^k * kBurnikelThreshold > s}.
        int m = 1 << Bigint.BitLength(s / BigintConfig.kBurnikelThreshold);
        // 2. Set j = roundup(s/m) and n = j * m.
        int j = Bigint.DIV_CEIL(s, m);
        int n = j * m;
        // 3. Set sigma = max{tao | 2^tao * B < 2^(kDigitBits * n)}.
        int sigma = Bigint.CountLeadingZeros(B[s - 1]);
        int digitShift = n - s;
        // 4. Set B = B * 2^sigma to normalize B. Shift A by the same amount.
        // Usage of temp: B[n], Z[2n], Ri[n], Qi[n].
        ulong[] temp = new ulong[n * 5];
        Span<ulong> BShifted = temp.AsSpan(0, n);
        Bigint.LeftShift(BShifted[digitShift..], B, sigma);
        for (int i = 0; i < digitShift; i++) BShifted[i] = 0;
        B = BShifted;
        // We need an extra digit if A's top digit does not have enough space for
        // the left-shift by {sigma}. Additionally, the top bit of A must be 0
        // (see "-1" in step 5 below), which combined with B being normalized (i.e.
        // B's top bit is 1) ensures the preconditions of the helper functions.
        int extraDigit = Bigint.CountLeadingZeros(A[r - 1]) < (sigma + 1) ? 1 : 0;
        r = A.Length + digitShift + extraDigit;
        ulong[] AShifted = new ulong[r];
        Bigint.LeftShift(AShifted.AsSpan(digitShift), A, sigma);
        for (int i = 0; i < digitShift; i++) AShifted[i] = 0;
        A = AShifted;
        // 5. Set t = min{t >= 2 | A < 2^(kDigitBits * t * n - 1)}.
        int t = Math.Max(Bigint.DIV_CEIL(r, n), 2);
        // 6. Split A conceptually into t blocks.
        // 7. Set Z_(t-2) = [A_(t-1), A_(t-2)].
        int zLen = n * 2;
        Span<ulong> Z = temp.AsSpan(n, zLen);
        Bigint.PutAt(Z, Bigint.Slice(A, n * (t - 2), A.Length), zLen);
        // 8. For i from t-2 downto 0 do:
        ulong[] scratchMem = new ulong[n >= BigintConfig.kBurnikelThreshold ? n : 0];
        Span<ulong> Ri = temp.AsSpan(3 * n, n);
        {
            // First iteration unrolled and specialized.
            // We might not have n digits at the top of Q, so use temporary storage
            // for Qi...
            Span<ulong> Qi = temp.AsSpan(4 * n, n);
            BZD2n1n(Qi, Ri, Z, B, scratchMem);
            if (should_terminate()) return;
            // ...but there *will* be enough space for any non-zero result digits!
            Qi = Bigint.Normalize(Qi);
            Span<ulong> target = Q[(n * (t - 2))..];
            Debug.Assert(Qi.Length <= target.Length);
            Bigint.PutAt(target, Qi, target.Length);
        }
        // Now loop over any remaining iterations.
        for (int i = t - 3; i >= 0; i--)
        {
            // 8b. If i > 0, set Z_(i-1) = [Ri, A_(i-1)].
            // (De-duped with unrolled first iteration, hence reading A_(i).)
            Bigint.PutAt(Z[n..], Ri, n);
            Bigint.PutAt(Z, Bigint.Slice(A, n * i, A.Length), n);
            // 8a. Using algorithm D2n1n compute Qi, Ri such that Zi = B*Qi + Ri.
            Span<ulong> Qi = Bigint.Slice(Q, i * n, n);
            BZD2n1n(Qi, Ri, Z, B, scratchMem);
            if (should_terminate()) return;
        }
        // 9. Return Q = [Q_(t-2), ..., Q_0] and R = R_0 * 2^(-sigma).
        if (R.Length != 0)
        {
            ReadOnlySpan<ulong> RiPart = Bigint.Normalize(Bigint.Slice(Ri, digitShift, Ri.Length));
            Debug.Assert(RiPart.Length <= R.Length);
            Bigint.RightShift(R, RiPart, sigma);
        }
    }
}
