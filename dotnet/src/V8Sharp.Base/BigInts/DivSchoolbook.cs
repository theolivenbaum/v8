// Port of src/bigint/div-schoolbook.cc.
// "Schoolbook" division. This is loosely based on Go's implementation
// found at https://golang.org/src/math/big/nat.go, licensed as follows:
//
// Copyright 2009 The Go Authors. All rights reserved.
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file [1].
//
// [1] https://golang.org/LICENSE

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.BigInts;

public static partial class Bigint
{
    /// <summary>Returns whether (factor1 * factor2) > (high &lt;&lt; kDigitBits) + low.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ProductGreaterThan(ulong factor1, ulong factor2, ulong high, ulong low)
    {
        ulong resultLow = digit_mul(factor1, factor2, out ulong resultHigh);
        return resultHigh > high || (resultHigh == high && resultLow > low);
    }
}

/// <summary>
/// DivideSchoolbook performs 2-by-1 digit divisions with a divisor that is
/// always the same, so it precomputes the divisor's modular multiplicative
/// inverse and replaces the division with a multiplication-based sequence.
/// This relies on the divisor being left-shifted such that its most
/// significant bit is set.
/// </summary>
readonly struct MultiplicativeDigitDiv
{
    readonly ulong _divisor;
    readonly ulong _inverse;

    public MultiplicativeDigitDiv(ulong divisor)
    {
        Debug.Assert((divisor >> (Bigint.kDigitBits - 1)) == 1);
        _divisor = divisor;
        _inverse = ComputeInverse(divisor);
    }

    // Reference:
    // https://gmplib.org/~tege/division-paper.pdf, "Algorithm 4".
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong div(ulong high, ulong low, out ulong remainder)
    {
        // Paper: u1 = high, u0 = low, d = divisor, v = inverse,
        // "mod beta" means "truncate to digit_t width".
        // 1. <q1, q0> <- v*u1
        ulong q0 = Bigint.digit_mul(high, _inverse, out ulong q1);
        // 2. <q1, q0> <- <q1, q0> + <u1, u0>
        q0 = Bigint.digit_add2(q0, low, out ulong carry);
        q1 = Bigint.digit_add3(q1, high, carry, out _);
        // 3. q1 <- (q1 + 1) mod beta
        q1++;
        // 4. r <- (u0 - q1*d) mod beta
        ulong r = low - q1 * _divisor;
        // 5. if r > q0:  // Unpredictable condition
        if (r > q0)
        {
            // 6. q1 <- (q1 - 1) mod beta
            q1--;
            // 7. r <- (r + d) mod beta
            r += _divisor;
        }
        // 8 if r >= d:  // Unlikely condition
        if (r >= _divisor)
        {
            // 9. q1 <- q1 + 1
            q1++;
            // 10. r <- r - d
            r -= _divisor;
        }
        // 11. return q1, r
        remainder = r;
        return q1;
    }

    static ulong ComputeInverse(ulong divisor)
    {
        ulong high = ~divisor;
        ulong low = ~0UL;
        return (ulong)((((UInt128)high << Bigint.kDigitBits) | low) / divisor);
    }
}

public sealed partial class Processor
{
    /// <summary>
    /// Computes Q(uotient) and R(emainder) for A/B, such that
    /// Q = (A - R) / B, with 0 &lt;= R &lt; B.
    /// Both Q and R are optional: callers that are only interested in one of
    /// them can pass the other with len == 0.
    /// If Q is present, its length must be at least A.len - B.len + 1.
    /// If R is present, its length must be at least B.len.
    /// See Knuth, Volume 2, section 4.3.1, Algorithm D.
    /// </summary>
    public void DivideSchoolbook(Span<ulong> Q, Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(B.Length >= 2);        // Use DivideSingle otherwise.
        Debug.Assert(A.Length >= B.Length);  // No-op otherwise.
        Debug.Assert(R.Length == 0 || R.Length >= B.Length);
        // The unusual variable names inside this function are consistent with
        // Knuth's book, as well as with Go's implementation of this algorithm.
        // Maintaining this consistency is probably more useful than trying to
        // come up with more descriptive names for them.
        int n = B.Length;
        int m = A.Length - n;

        // Try to avoid allocations by caching some scratch memory on the processor.
        int qhatvLen = n + 1;
        int bNormalizedStorageLen = n;
        int ULen = A.Length + 1;
        int neededScratchSpace = qhatvLen + bNormalizedStorageLen + ULen;
        Span<ulong> scratch = neededScratchSpace <= kSmallScratchSize
            ? GetSmallScratch()
            : new ulong[neededScratchSpace];

        // In each iteration, {qhatv} holds {divisor} * {current quotient digit}.
        // "v" is the book's name for {divisor}, "qhat" the current quotient digit.
        Span<ulong> qhatv = Bigint.Slice(scratch, 0, qhatvLen);

        // D1.
        // Left-shift inputs so that the divisor's MSB is set. This is necessary
        // to prevent the digit-wise divisions (see {vn1_divisor.div()} below) from
        // overflowing (they take a two digits wide input, and return a one digit
        // result).
        Span<ulong> bNormalizedStorage = Bigint.Slice(scratch, qhatvLen, bNormalizedStorageLen);
        // {BN} means "B normalized". Don't use {B} past this point!
        ShiftedDigits bnHolder = new(B, bNormalizedStorage, out ReadOnlySpan<ulong> BN);
        // U holds the (continuously updated) remaining part of the dividend, which
        // eventually becomes the remainder.
        Span<ulong> U = Bigint.Slice(scratch, qhatvLen + bNormalizedStorageLen, ULen);
        Bigint.LeftShift(U, A, bnHolder.Shift);

        // D2.
        // Iterate over the dividend's digits (like the "grad school" algorithm).
        // {vn1} is the divisor's most significant digit.
        ulong vn1 = BN[n - 1];
        MultiplicativeDigitDiv vn1Divisor = new(vn1);
        for (int j = m; j >= 0; j--)
        {
            // D3.
            // Estimate the current iteration's quotient digit (see Knuth for details).
            // {qhat} is the current quotient digit.
            ulong qhat = ulong.MaxValue;
            // {ujn} is the dividend's most significant remaining digit.
            ulong ujn = U[j + n];
            if (ujn != vn1)
            {
                // Estimate the current quotient digit by dividing the most significant
                // digits of dividend and divisor. The result will not be too small,
                // but could be a bit too large.
                qhat = vn1Divisor.div(ujn, U[j + n - 1], out ulong rhat);

                // Decrement the quotient estimate as needed by looking at the next
                // digit, i.e. by testing whether
                // qhat * v_{n-2} > (rhat << kDigitBits) + u_{j+n-2}.
                ulong vn2 = BN[n - 2];
                ulong ujn2 = U[j + n - 2];
                while (Bigint.ProductGreaterThan(qhat, vn2, rhat, ujn2))
                {
                    qhat--;
                    ulong prevRhat = rhat;
                    rhat += vn1;
                    // v[n-1] >= 0, so this tests for overflow.
                    if (rhat < prevRhat) break;
                }
            }

            // D4.
            // Multiply the divisor with the current quotient digit, and subtract
            // it from the dividend. If there was "borrow", then the quotient digit
            // was one too high, so we must correct it and undo one subtraction of
            // the (shifted) divisor.
            if (qhat != 0)
            {
                Bigint.MultiplySingle(qhatv, BN, qhat);
                AddWorkEstimate((ulong)n);
                ulong c = Bigint.InplaceSubAndReturnBorrow(U[j..], qhatv);
                if (c != 0)
                {
                    c = Bigint.InplaceAddAndReturnCarry(U[j..], BN);
                    U[j + n] = U[j + n] + c;
                    qhat--;
                }
            }

            if (Q.Length != 0)
            {
                if (j >= Q.Length)
                {
                    Debug.Assert(qhat == 0);
                }
                else
                {
                    Q[j] = qhat;
                }
            }
        }
        if (R.Length != 0) Bigint.RightShift(R, U, bnHolder.Shift);
        // If Q has extra storage, clear it.
        for (int i = m + 1; i < Q.Length; i++) Q[i] = 0;
    }
}
