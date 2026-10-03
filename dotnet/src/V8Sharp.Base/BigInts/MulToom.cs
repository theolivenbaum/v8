// Port of src/bigint/mul-toom.cc.
// Toom-Cook multiplication.
// Reference: https://en.wikipedia.org/wiki/Toom%E2%80%93Cook_multiplication

namespace V8Sharp.Base.BigInts;

public sealed partial class Processor
{
    static void TimesTwo(Span<ulong> X)
    {
        ulong carry = 0;
        for (int i = 0; i < X.Length; i++)
        {
            ulong d = X[i];
            X[i] = (d << 1) | carry;
            carry = d >> (Bigint.kDigitBits - 1);
        }
    }

    static void DivideByTwo(Span<ulong> X)
    {
        ulong carry = 0;
        for (int i = X.Length; i-- > 0;)
        {
            ulong d = X[i];
            X[i] = (d >> 1) | carry;
            carry = d << (Bigint.kDigitBits - 1);
        }
    }

    static void DivideByThree(Span<ulong> X)
    {
        ulong remainder = 0;
        for (int i = X.Length; i-- > 0;)
        {
            ulong d = X[i];
            ulong upper = (remainder << Bigint.kHalfDigitBits) | (d >> Bigint.kHalfDigitBits);
            ulong uResult = upper / 3;
            remainder = upper - 3 * uResult;
            ulong lower = (remainder << Bigint.kHalfDigitBits) | (d & Bigint.kHalfDigitMask);
            ulong lResult = lower / 3;
            remainder = lower - 3 * lResult;
            X[i] = (uResult << Bigint.kHalfDigitBits) | lResult;
        }
    }

    public void Toom3Main(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(Z.Length >= X.Length + Y.Length);
        // Phase 1: Splitting.
        int i = Bigint.DIV_CEIL(Math.Max(X.Length, Y.Length), 3);
        ReadOnlySpan<ulong> X0 = Bigint.Slice(X, 0, i);
        ReadOnlySpan<ulong> X1 = Bigint.Slice(X, i, i);
        ReadOnlySpan<ulong> X2 = Bigint.Slice(X, 2 * i, i);
        ReadOnlySpan<ulong> Y0 = Bigint.Slice(Y, 0, i);
        ReadOnlySpan<ulong> Y1 = Bigint.Slice(Y, i, i);
        ReadOnlySpan<ulong> Y2 = Bigint.Slice(Y, 2 * i, i);

        // Temporary storage.
        int pLen = i + 1;      // For all px, qx below.
        int rLen = 2 * pLen;  // For all r_x, Rx below.
        ulong[] tempStorage = new ulong[4 * rLen];
        // We will use the same variable names as the Wikipedia article, as much as
        // C++ lets us: our "p_m1" is their "p(-1)" etc. For consistency with other
        // algorithms, we use X and Y where Wikipedia uses m and n.
        // We will use and reuse the temporary storage as follows:
        //
        //   chunk                  | -------- time ----------->
        //   [0 .. i]               |( po )( p_m1 ) ( r_m2  )
        //   [i+1 .. rlen-1]        |( qo )( q_m1 ) ( r_m2  )
        //   [rlen .. rlen+i]       | (p_1 ) ( p_m2 ) (r_inf)
        //   [rlen+i+1 .. 2*rlen-1] | (q_1 ) ( q_m2 ) (r_inf)
        //   [2*rlen .. 3*rlen-1]   |      (   r_1          )
        //   [3*rlen .. 4*rlen-1]   |             (  r_m1   )
        //
        // This requires interleaving phases 2 and 3 a bit: after computing
        // r_1 = p_1 * q_1, we can reuse p_1's storage for p_m2, and so on.
        Span<ulong> t = tempStorage;
        Span<ulong> po = t.Slice(0, pLen);
        Span<ulong> qo = t.Slice(pLen, pLen);
        Span<ulong> p_1 = t.Slice(rLen, pLen);
        Span<ulong> q_1 = t.Slice(rLen + pLen, pLen);
        Span<ulong> r_1 = t.Slice(2 * rLen, rLen);
        Span<ulong> r_m1 = t.Slice(3 * rLen, rLen);

        // We can also share the  backing stores of Z, r_0, R0.
        Debug.Assert(Z.Length >= rLen);
        Span<ulong> r_0 = Bigint.Slice(Z, 0, rLen);

        // Phase 2a: Evaluation, steps 0, 1, m1.
        // po = X0 + X2
        Bigint.Add(po, X0, X2);
        // p_0 = X0
        // p_1 = po + X1
        Bigint.Add(p_1, po, X1);
        // p_m1 = po - X1
        Span<ulong> p_m1 = po;
        bool p_m1_sign = Bigint.SubtractSigned(p_m1, po, false, X1, false);

        // qo = Y0 + Y2
        Bigint.Add(qo, Y0, Y2);
        // q_0 = Y0
        // q_1 = qo + Y1
        Bigint.Add(q_1, qo, Y1);
        // q_m1 = qo - Y1
        Span<ulong> q_m1 = qo;
        bool q_m1_sign = Bigint.SubtractSigned(q_m1, qo, false, Y1, false);

        // Phase 3a: Pointwise multiplication, steps 0, 1, m1.
        Multiply(r_0, X0, Y0);
        Multiply(r_1, p_1, q_1);
        Multiply(r_m1, p_m1, q_m1);
        bool r_m1_sign = p_m1_sign != q_m1_sign;

        // Phase 2b: Evaluation, steps m2 and inf.
        // p_m2 = (p_m1 + X2) * 2 - X0
        Span<ulong> p_m2 = p_1;
        bool p_m2_sign = Bigint.AddSigned(p_m2, p_m1, p_m1_sign, X2, false);
        TimesTwo(p_m2);
        p_m2_sign = Bigint.SubtractSigned(p_m2, p_m2, p_m2_sign, X0, false);
        // p_inf = X2

        // q_m2 = (q_m1 + Y2) * 2 - Y0
        Span<ulong> q_m2 = q_1;
        bool q_m2_sign = Bigint.AddSigned(q_m2, q_m1, q_m1_sign, Y2, false);
        TimesTwo(q_m2);
        q_m2_sign = Bigint.SubtractSigned(q_m2, q_m2, q_m2_sign, Y0, false);
        // q_inf = Y2

        // Phase 3b: Pointwise multiplication, steps m2 and inf.
        Span<ulong> r_m2 = t.Slice(0, rLen);
        Multiply(r_m2, p_m2, q_m2);
        bool r_m2_sign = p_m2_sign != q_m2_sign;

        Span<ulong> r_inf = t.Slice(rLen, rLen);
        Multiply(r_inf, X2, Y2);

        // Phase 4: Interpolation.
        ReadOnlySpan<ulong> R0 = r_0;
        ReadOnlySpan<ulong> R4 = r_inf;
        // R3 <- (r_m2 - r_1) / 3
        Span<ulong> R3 = r_m2;
        bool R3_sign = Bigint.SubtractSigned(R3, r_m2, r_m2_sign, r_1, false);
        DivideByThree(R3);
        // R1 <- (r_1 - r_m1) / 2
        Span<ulong> R1 = r_1;
        bool R1_sign = Bigint.SubtractSigned(R1, r_1, false, r_m1, r_m1_sign);
        DivideByTwo(R1);
        // R2 <- r_m1 - r_0
        Span<ulong> R2 = r_m1;
        bool R2_sign = Bigint.SubtractSigned(R2, r_m1, r_m1_sign, R0, false);
        // R3 <- (R2 - R3) / 2 + 2 * r_inf
        R3_sign = Bigint.SubtractSigned(R3, R2, R2_sign, R3, R3_sign);
        DivideByTwo(R3);
        R3_sign = Bigint.AddSigned(R3, R3, R3_sign, r_inf, false);
        R3_sign = Bigint.AddSigned(R3, R3, R3_sign, r_inf, false);
        // R2 <- R2 + R1 - R4
        R2_sign = Bigint.AddSigned(R2, R2, R2_sign, R1, R1_sign);
        R2_sign = Bigint.SubtractSigned(R2, R2, R2_sign, R4, false);
        // R1 <- R1 - R3
        R1_sign = Bigint.SubtractSigned(R1, R1, R1_sign, R3, R3_sign);

        Debug.Assert(!R1_sign || Bigint.Normalize(R1).Length == 0);
        Debug.Assert(!R2_sign || Bigint.Normalize(R2).Length == 0);
        Debug.Assert(!R3_sign || Bigint.Normalize(R3).Length == 0);

        // Phase 5: Recomposition. R0 is already in place. Overflow can't happen.
        for (int j = R0.Length; j < Z.Length; j++) Z[j] = 0;
        Bigint.AddAndReturnOverflow(Z[i..], R1);
        Bigint.AddAndReturnOverflow(Z[(2 * i)..], R2);
        Bigint.AddAndReturnOverflow(Z[(3 * i)..], R3);
        Bigint.AddAndReturnOverflow(Z[(4 * i)..], R4);
    }

    public void MultiplyToomCook(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(X.Length >= Y.Length);
        int k = Y.Length;
        ReadOnlySpan<ulong> X0 = Bigint.Slice(X, 0, k);
        Toom3Main(Z, X0, Y);
        if (X.Length > Y.Length)
        {
            ulong[] T = new ulong[2 * k];
            for (int i = k; i < X.Length; i += k)
            {
                ReadOnlySpan<ulong> Xi = Bigint.Slice(X, i, k);
                Toom3Main(T, Xi, Y);
                Bigint.AddAndReturnOverflow(Z[i..], T);  // Can't overflow.
            }
        }
    }
}
