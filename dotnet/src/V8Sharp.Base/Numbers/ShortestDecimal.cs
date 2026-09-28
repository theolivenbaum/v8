// The shortest decimal representation of a double, the role of
// jkj::dragonbox::to_decimal in src/numbers/conversions.cc
// (DoubleToStringView).
//
// Deviation: dragonbox's sources are not part of this checkout. This is an
// implementation of Schubfach (Raffaello Giulietti, "The Schubfach way to
// render doubles", 2020), the algorithm dragonbox is derived from. Both
// return the unique shortest decimal in the rounding interval of the double
// (boundaries included when the significand is even), and of several
// shortest candidates the one closest to the double, ties to an even digit,
// with trailing zeros removed; so the digits and exponent are dragonbox's.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Numbers;

internal static partial class ShortestDecimal
{
    const int P = 53;               // precision
    const int QMin = -1074;         // minimum exponent of the significand's unit
    const long CMin = 1L << (P - 1);
    const long kSmallSubnormal = 1000;
    const ulong Mask63 = (1UL << 63) - 1;

    // floor(q log10(2)), floor(log10(3/4 * 2^q)) and floor(e log2(10)); the
    // table generator checks them over their ranges.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FloorLog10Pow2(int q) => (int)((q * 661971961083L) >> 41);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FloorLog10ThreeQuartersPow2(int q) => (int)((q * 661971961083L - 274743187321L) >> 41);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FloorLog2Pow10(int e) => (int)((e * 913124641741L) >> 38);

    /// <summary>
    /// |v| = significand * 10^exponent with the fewest digits (at most 17)
    /// that still round to v, for finite non-zero v. The significand has no
    /// trailing zeros.
    /// </summary>
    public static void ToDecimal(double v, out ulong significand, out int exponent)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        long t = (long)(bits & ((1UL << (P - 1)) - 1));
        int bq = (int)(bits >> (P - 1)) & 0x7FF;
        Debug.Assert(bq < 0x7FF && (bq != 0 || t != 0));
        long f;
        int e;
        if (bq != 0)
        {
            // Normal value; mq = -q.
            int mq = -QMin + 1 - bq;
            long c = CMin | t;
            // Integers below 2^53: the value itself is the shortest decimal.
            if (0 < mq && mq < P && (c >> mq) << mq == c)
            {
                f = c >> mq;
                e = 0;
            }
            else
            {
                Compute(-mq, c, 0, out f, out e);
            }
        }
        else if (t < kSmallSubnormal)
        {
            // Schubfach looks for a result with at most one digit fewer than
            // the double's own precision, but the smallest subnormals, whose
            // rounding intervals are wide, can have much shorter ones: use
            // the bignum-backed shortest conversion for them.
            Span<char> digits = stackalloc char[DoubleConversion.kBase10MaximalLength + 1];
            DoubleConversion.DoubleToAscii(Math.Abs(v), DtoaMode.DTOA_SHORTEST, 0, digits, out _, out int length, out int point);
            f = 0;
            for (int i = 0; i < length; i++) f = f * 10 + (digits[i] - '0');
            e = point - length;
        }
        else
        {
            Compute(QMin, t, 0, out f, out e);
        }
        // Remove trailing zeros.
        while (f % 10 == 0)
        {
            f /= 10;
            e++;
        }
        significand = (ulong)f;
        exponent = e;
    }

    static void Compute(int q, long c, int dk, out long f, out int e)
    {
        int outside = (int)c & 1;
        long cb = c << 2;
        long cbr = cb + 2;
        long cbl;
        int k;
        if (c != CMin || q == QMin)
        {
            cbl = cb - 2;
            k = FloorLog10Pow2(q);
        }
        else
        {
            cbl = cb - 1;
            k = FloorLog10ThreeQuartersPow2(q);
        }
        int h = q + FloorLog2Pow10(-k) + 2;
        ReadOnlySpan<ulong> g = G;
        int index = 2 * (k - kKMin);
        ulong g1 = g[index], g0 = g[index + 1];
        long vb = RoundToOdd(g1, g0, (ulong)(cb << h));
        long vbl = RoundToOdd(g1, g0, (ulong)(cbl << h));
        long vbr = RoundToOdd(g1, g0, (ulong)(cbr << h));

        long s = vb >> 2;
        if (s >= 100)
        {
            // One digit fewer: the multiples of ten around s.
            long sp10 = 10 * (long)Math.BigMul((ulong)s, 1844674407370955168UL, out _);
            long tp10 = sp10 + 10;
            bool upin = vbl + outside <= sp10 << 2;
            bool wpin = (tp10 << 2) + outside <= vbr;
            if (upin != wpin)
            {
                f = upin ? sp10 : tp10;
                e = k;
                return;
            }
        }
        long tt = s + 1;
        bool uin = vbl + outside <= s << 2;
        bool win = (tt << 2) + outside <= vbr;
        e = k + dk;
        if (uin != win)
        {
            f = uin ? s : tt;
            return;
        }
        // Both in the interval: the closer one, ties to even.
        long cmp = vb - ((s + tt) << 1);
        f = cmp < 0 || (cmp == 0 && (s & 1) == 0) ? s : tt;
    }

    // The product g * cp / 2^127 rounded to odd.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long RoundToOdd(ulong g1, ulong g0, ulong cp)
    {
        ulong x1 = Math.BigMul(g0, cp, out _);
        ulong y1 = Math.BigMul(g1, cp, out ulong y0);
        ulong z = (y0 >> 1) + x1;
        ulong vbp = y1 + (z >> 63);
        return (long)(vbp | (((z & Mask63) + Mask63) >> 63));
    }
}
