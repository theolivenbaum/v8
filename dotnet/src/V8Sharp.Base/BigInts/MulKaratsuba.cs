// Port of src/bigint/mul-karatsuba.cc.
// Karatsuba multiplication. This is loosely based on Go's implementation
// found at https://golang.org/src/math/big/nat.go, licensed as follows:
//
// Copyright 2009 The Go Authors. All rights reserved.
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file [1].
//
// [1] https://golang.org/LICENSE

namespace V8Sharp.Base.BigInts;

public sealed partial class Processor
{
    // With V8_ADVANCED_BIGINT_ALGORITHMS (the configuration ported here),
    // Karatsuba is only used for sufficiently small chunks that checking for
    // termination requests is not necessary (MAYBE_TERMINATE is empty).

    // The Karatsuba algorithm sometimes finishes more quickly when the
    // input length is rounded up a bit. This method encodes some heuristics
    // to accomplish this. The details have been determined experimentally.
    static int RoundUpLen(int len)
    {
        if (len <= 36) return Bigint.RoundUp(len, 2);
        // Keep the 4 or 5 most significant non-zero bits.
        int shift = Bigint.BitLength(len) - 5;
        if ((len >> shift) >= 0x18) shift++;
        // Round up, unless we're only just above the threshold. This smoothes
        // the steps by which time goes up as input size increases.
        int additive = (1 << shift) - 1;
        if (shift >= 2 && (len & additive) < (1 << (shift - 2))) return len;
        return ((len + additive) >> shift) << shift;
    }

    // This method makes the final decision how much to bump up the input size.
    static int KaratsubaLength(int n)
    {
        n = RoundUpLen(n);
        int i = 0;
        while (n > BigintConfig.kKaratsubaThreshold)
        {
            n >>= 1;
            i++;
        }
        return n << i;
    }

    // Performs the specific subtraction required by {KaratsubaMain} below.
    static void KaratsubaSubtractionHelper(Span<ulong> result, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, ref int sign)
    {
        X = Bigint.Normalize(X);
        Y = Bigint.Normalize(Y);
        ulong borrow = 0;
        int i = 0;
        if (!Bigint.GreaterThanOrEqual(X, Y))
        {
            sign = -sign;
            ReadOnlySpan<ulong> t = X;
            X = Y;
            Y = t;
        }
        for (; i < Y.Length; i++) result[i] = Bigint.digit_sub2(X[i], Y[i], borrow, out borrow);
        for (; i < X.Length; i++) result[i] = Bigint.digit_sub(X[i], borrow, out borrow);
        Debug.Assert(borrow == 0);
        for (; i < result.Length; i++) result[i] = 0;
    }

    public void MultiplyKaratsuba(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(X.Length >= Y.Length);
        Debug.Assert(Y.Length >= BigintConfig.kKaratsubaThreshold);
        Debug.Assert(Z.Length >= X.Length + Y.Length);
        int k = KaratsubaLength(Y.Length);
        int scratchLen = 4 * k;
        ulong[] scratch = new ulong[scratchLen];
        KaratsubaStart(Z, X, Y, scratch, k);
    }

    // Entry point for Karatsuba-based multiplication, takes care of inputs
    // with unequal lengths by chopping the larger into chunks.
    void KaratsubaStart(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, Span<ulong> scratch, int k)
    {
        KaratsubaMain(Z, X, Y, scratch, k);
        for (int i = 2 * k; i < Z.Length; i++) Z[i] = 0;
        if (k < Y.Length || X.Length != Y.Length)
        {
            ulong[] T = new ulong[2 * k];
            // Add X0 * Y1 * b.
            ReadOnlySpan<ulong> X0 = Bigint.Slice(X, 0, k);
            ReadOnlySpan<ulong> Y1 = Y[Math.Min(k, Y.Length)..];
            if (Y1.Length > 0)
            {
                KaratsubaChunk(T, X0, Y1, scratch);
                Bigint.AddAndReturnOverflow(Z[k..], T);  // Can't overflow.
            }

            // Add Xi * Y0 << i and Xi * Y1 * b << (i + k).
            ReadOnlySpan<ulong> Y0 = Bigint.Slice(Y, 0, k);
            for (int i = k; i < X.Length; i += k)
            {
                ReadOnlySpan<ulong> Xi = Bigint.Slice(X, i, k);
                KaratsubaChunk(T, Xi, Y0, scratch);
                Bigint.AddAndReturnOverflow(Z[i..], T);  // Can't overflow.
                if (Y1.Length > 0)
                {
                    KaratsubaChunk(T, Xi, Y1, scratch);
                    Bigint.AddAndReturnOverflow(Z[(i + k)..], T);  // Can't overflow.
                }
            }
        }
    }

    // Entry point for chunk-wise multiplications, selects an appropriate
    // algorithm for the inputs based on their sizes.
    void KaratsubaChunk(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, Span<ulong> scratch)
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
        int k = KaratsubaLength(Y.Length);
        Debug.Assert(scratch.Length >= 4 * k);
        KaratsubaStart(Z, X, Y, scratch, k);
    }

    // The main recursive Karatsuba method.
    void KaratsubaMain(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, Span<ulong> scratch, int n)
    {
        if (n < BigintConfig.kKaratsubaThreshold)
        {
            X = Bigint.Normalize(X);
            Y = Bigint.Normalize(Y);
            if (X.Length >= Y.Length)
            {
                Bigint.MultiplySchoolbook(Bigint.Slice(Z, 0, 2 * n), X, Y);
            }
            else
            {
                Bigint.MultiplySchoolbook(Bigint.Slice(Z, 0, 2 * n), Y, X);
            }
            AddWorkEstimate((ulong)X.Length * (ulong)Y.Length);
            return;
        }
        Debug.Assert(scratch.Length >= 4 * n);
        Debug.Assert((n & 1) == 0);
        int n2 = n >> 1;
        ReadOnlySpan<ulong> X0 = Bigint.Slice(X, 0, n2);
        ReadOnlySpan<ulong> X1 = Bigint.Slice(X, n2, n2);
        ReadOnlySpan<ulong> Y0 = Bigint.Slice(Y, 0, n2);
        ReadOnlySpan<ulong> Y1 = Bigint.Slice(Y, n2, n2);
        Span<ulong> scratchForRecursion = Bigint.Slice(scratch, 2 * n, 2 * n);
        Span<ulong> P0 = Bigint.Slice(scratch, 0, n);
        KaratsubaMain(P0, X0, Y0, scratchForRecursion, n2);
        for (int i = 0; i < n; i++) Z[i] = P0[i];
        Span<ulong> P2 = Bigint.Slice(scratch, n, n);
        KaratsubaMain(P2, X1, Y1, scratchForRecursion, n2);
        Span<ulong> Z2 = Z[n..];
        int end = Math.Min(Z2.Length, P2.Length);
        for (int i = 0; i < end; i++) Z2[i] = P2[i];
        // The intermediate result can be one digit too large; the subtraction
        // below will fix this.
        ulong overflow = Bigint.AddAndReturnOverflow(Z[n2..], P0);
        overflow += Bigint.AddAndReturnOverflow(Z[n2..], P2);
        Span<ulong> XDiff = Bigint.Slice(scratch, 0, n2);
        Span<ulong> YDiff = Bigint.Slice(scratch, n2, n2);
        int sign = 1;
        KaratsubaSubtractionHelper(XDiff, X1, X0, ref sign);
        KaratsubaSubtractionHelper(YDiff, Y0, Y1, ref sign);
        Span<ulong> P1 = Bigint.Slice(scratch, n, n);
        KaratsubaMain(P1, XDiff, YDiff, scratchForRecursion, n2);
        if (sign > 0)
        {
            overflow += Bigint.AddAndReturnOverflow(Z[n2..], P1);
        }
        else
        {
            overflow -= Bigint.SubAndReturnBorrow(Z[n2..], P1);
        }
        // The intermediate result may have been bigger, but the final result fits.
        Debug.Assert(overflow == 0);
    }
}
