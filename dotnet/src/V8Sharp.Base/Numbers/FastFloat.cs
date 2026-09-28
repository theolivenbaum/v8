// The decimal-to-double conversion V8 performs with fast_float
// (third_party/fast_float, fast_float::from_chars): Clinger's fast path for
// small exponents, then the Eisel-Lemire algorithm for up to 19 significant
// digits.
//
// Deviation: fast_float's sources are not part of this checkout; this is an
// implementation of the same published algorithms (Clinger 1990; Lemire,
// "Number Parsing at a Gigabyte per Second", 2021). Where fast_float falls
// back to its big-integer digit comparison, and in the few cases where this
// implementation cannot bound the rounding (results within two units of the
// 128-bit product's last place of a rounding boundary, subnormal and
// near-overflow results), the caller uses the port of base::Strtod. All
// three are correctly rounded, so the results are fast_float's.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Numbers;

internal static partial class FastFloat
{
    // 10^0 .. 10^22, exactly representable.
    static ReadOnlySpan<double> ExactPowersOfTen =>
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
        1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    ];

    static ReadOnlySpan<ulong> ExactIntegerPowersOfTen =>
    [
        1UL, 10UL, 100UL, 1000UL, 10000UL, 100000UL, 1000000UL, 10000000UL, 100000000UL,
        1000000000UL, 10000000000UL, 100000000000UL, 1000000000000UL, 10000000000000UL,
        100000000000000UL, 1000000000000000UL, 10000000000000000UL,
    ];

    const ulong kMaxExactMantissa = 1UL << 53;

    /// <summary>
    /// w * 10^q correctly rounded, for w != 0 exactly the significant digits
    /// (w below 10^19). Returns false when the caller must use the slow path.
    /// </summary>
    public static bool TryCompute(long q, ulong w, out double result)
    {
        // Clinger's fast path: both operands exact, one rounding.
        if (w <= kMaxExactMantissa && q >= -22 && q <= 22)
        {
            double d = w;
            result = q < 0 ? d / ExactPowersOfTen[(int)-q] : d * ExactPowersOfTen[(int)q];
            return true;
        }
        // fast_float's extension: move part of a larger exponent into w when
        // it stays exact.
        if (q > 22 && q <= 22 + 16)
        {
            ulong scale = ExactIntegerPowersOfTen[(int)(q - 22)];
            ulong high = Math.BigMul(w, scale, out ulong product);
            if (high == 0 && product <= kMaxExactMantissa)
            {
                result = (double)product * 1e22;
                return true;
            }
        }
        return TryEiselLemire(q, w, out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryEiselLemire(long q, ulong w, out double result)
    {
        result = 0;
        if (q < kSmallestPowerOfFive || q > kLargestPowerOfFive) return false;
        int lz = System.Numerics.BitOperations.LeadingZeroCount(w);
        w <<= lz;
        int index = 2 * (int)(q - kSmallestPowerOfFive);
        ReadOnlySpan<ulong> table = PowersOfFive;
        // The top 128 bits of the 192-bit product w * 5^q (both halves of the
        // table entry, so that hi:lo is within two units of its last place).
        ulong hi = Math.BigMul(w, table[index], out ulong lo);
        ulong secondHigh = Math.BigMul(w, table[index + 1], out _);
        lo += secondHigh;
        if (lo < secondHigh) hi++;

        int upperBit = (int)(hi >> 63);
        int shift = upperBit + 9;
        ulong mantissa = hi >> shift;  // 54 bits: 53 plus the rounding bit
        // The bits below the rounding bit, hi's low part and lo, may be off
        // by up to two units of lo: give up if that could change the rounding.
        ulong mask = (1UL << shift) - 1;
        ulong sticky = hi & mask;
        if ((sticky == 0 && lo < 2) || (sticky == mask && lo > ulong.MaxValue - 2)) return false;

        // Biased exponent of mantissa / 2 * 2^(power2 - 1075 + 1).
        long power2 = ((217706 * q) >> 16) + 63 + upperBit - lz + 1023;
        if (power2 <= 0 || power2 >= 0x7FE) return false;  // Subnormal or near overflow: slow path.

        // Round to nearest; an exact tie was excluded above, so the rounding
        // bit decides.
        mantissa = (mantissa + 1) >> 1;
        if (mantissa >= 1UL << 53)
        {
            mantissa >>= 1;
            power2++;
        }
        result = BitConverter.UInt64BitsToDouble(((ulong)power2 << 52) | (mantissa & ((1UL << 52) - 1)));
        return true;
    }
}
