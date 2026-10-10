// Port of src/bigint/bigint.h, bigint-inl.h, vector-arithmetic-inl.h and
// util.h: the digit-vector arithmetic of V8's bigint library.
//
// V8's Digits / RWDigits (a pointer and a length, little-endian digits) are
// ReadOnlySpan<ulong> / Span<ulong> here. Digits are 64 bits (V8's 64-bit
// configuration). "Digits::Normalize()" mutates the view's length in C++; in
// C# it returns the trimmed view (X = Normalize(X)). C++'s slice constructor
// Digits(src, offset, len) clamps to the source; that is Slice(src, offset, len).
// The free functions of namespace v8::bigint are static members of Bigint.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base.BigInts;

public enum Status { kOk, kInterrupted }

/// <summary>Carries RightShift_ResultLength's findings to RightShift.</summary>
public struct RightShiftState
{
    public bool must_round_down;
}

/// <summary>Tuning thresholds (namespace config), 64-bit values.</summary>
public static class BigintConfig
{
    // The thresholds between schoolbook algorithms and the next best alternative
    // are very clear-cut on all platforms.
    public const int kKaratsubaThreshold = 34;
    public const int kBurnikelThreshold = 57;
    public const int kNewtonInversionThreshold = 25;

    // On x64, Toom and Karatsuba have very similar performance between about
    // 200-400 digits; on ia32, Toom tends to win for more than about 200.
    public const int kToomThreshold = 210;

    public const int kFftThreshold = 720;
    public const int kFftInnerThreshold = 200;

    // Burnikel and Barrett take turns at winning between 9K-13K digits, above
    // that Barrett is a solid choice.
    public const int kBarrettThreshold = 13000;

    public const int kToStringFastThreshold = 23;
    public const int kFromStringLargeThreshold = 25;
}

public static partial class Bigint
{
    public const int kLog2DigitBits = 6;
    public const int kDigitBits = 1 << kLog2DigitBits;
    public const int kHalfDigitBits = kDigitBits / 2;
    public const ulong kHalfDigitBase = 1UL << kHalfDigitBits;
    public const ulong kHalfDigitMask = kHalfDigitBase - 1;

    /// <summary>Prevent computations of scratch space and number of bits from overflowing.</summary>
    public const int kMaxNumDigits = int.MaxValue / kDigitBits;

    // ---- Digits view helpers --------------------------------------------------

    /// <summary>Digits::Normalize: drops leading zero digits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<ulong> Normalize(ReadOnlySpan<ulong> x)
    {
        int len = x.Length;
        while (len > 0 && x[len - 1] == 0) len--;
        return x[..len];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<ulong> Normalize(Span<ulong> x)
    {
        int len = x.Length;
        while (len > 0 && x[len - 1] == 0) len--;
        return x[..len];
    }

    /// <summary>Digits(src, offset, len): a slice clamped to the source.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<ulong> Slice(ReadOnlySpan<ulong> src, int offset, int len)
    {
        if (offset >= src.Length) return [];
        return src.Slice(offset, Math.Min(len, src.Length - offset));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<ulong> Slice(Span<ulong> src, int offset, int len)
    {
        if (offset >= src.Length) return [];
        return src.Slice(offset, Math.Min(len, src.Length - offset));
    }

    /// <summary>Digits "pointer equality" (same memory and length).</summary>
    public static bool SameDigits(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b) =>
        a.Length == b.Length && (a.Length == 0 || (a.Overlaps(b, out int offset) && offset == 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Msd(ReadOnlySpan<ulong> x) => x[^1];

    // ---- util.h ---------------------------------------------------------------

    /// <summary>Rounds up x to a multiple of y (a power of two).</summary>
    public static int RoundUp(int x, int y) => (x + y - 1) & -y;

    public static int CountTrailingZeros(uint value) => BitOperations.TrailingZeroCount(value);

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountLeadingZeros(ulong value) => BitOperations.LeadingZeroCount(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountLeadingZeros(uint value) => BitOperations.LeadingZeroCount(value);

    public static int BitLength(int n) => 32 - CountLeadingZeros((uint)n);

    /// <summary>Integer division, rounding up.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DIV_CEIL(int x, int y) => (x - 1) / y + 1;

    // ---- digit arithmetic ---------------------------------------------------------

    public static bool digit_ismax(ulong x) => ~x == 0;

    /// <summary>carry will be set to 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong digit_add2(ulong a, ulong b, out ulong carry)
    {
        ulong result = a + b;
        carry = result < a ? 1UL : 0UL;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong digit_add3(ulong a, ulong b, ulong c, out ulong carry)
    {
        ulong result = a + b;
        carry = result < a ? 1UL : 0UL;
        result += c;
        if (result < c) carry++;
        return result;
    }

    /// <summary>borrow will be set to 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong digit_sub(ulong a, ulong b, out ulong borrow)
    {
        ulong result = a - b;
        borrow = result > a ? 1UL : 0UL;
        return result;
    }

    /// <summary>borrow_out will be set to 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong digit_sub2(ulong a, ulong b, ulong borrowIn, out ulong borrowOut)
    {
        ulong result = a - b;
        borrowOut = result > a ? 1UL : 0UL;
        if (result < borrowIn) borrowOut++;
        result -= borrowIn;
        return result;
    }

    /// <summary>Returns the low half of the result. High half is in high.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong digit_mul(ulong a, ulong b, out ulong high)
    {
        high = Math.BigMul(a, b, out ulong low);
        return low;
    }

    /// <summary>
    /// Returns the quotient: quotient = (high &lt;&lt; kDigitBits + low - remainder) / divisor.
    /// Requires high &lt; divisor. Adapted from Warren, Hacker's Delight, p. 152
    /// (V8's portable path; V8 uses the divq instruction on x64).
    /// </summary>
    public static ulong digit_div(ulong high, ulong low, ulong divisor, out ulong remainder)
    {
        Debug.Assert(high < divisor);
        Debug.Assert(divisor != 0);
        int s = CountLeadingZeros(divisor);
        divisor <<= s;

        ulong vn1 = divisor >> kHalfDigitBits;
        ulong vn0 = divisor & kHalfDigitMask;
        // {s} can be 0. {low >> kDigitBits} would be undefined behavior, so
        // we mask the shift amount with {kShiftMask}, and the result with
        // {s_zero_mask} which is 0 if s == 0 and all 1-bits otherwise.
        const int kShiftMask = kDigitBits - 1;
        ulong sZeroMask = (ulong)((long)-s >> (kDigitBits - 1));
        ulong un32 = (high << s) | ((low >> ((kDigitBits - s) & kShiftMask)) & sZeroMask);
        ulong un10 = low << s;
        ulong un1 = un10 >> kHalfDigitBits;
        ulong un0 = un10 & kHalfDigitMask;
        ulong q1 = un32 / vn1;
        ulong rhat = un32 - q1 * vn1;

        while (q1 >= kHalfDigitBase || q1 * vn0 > rhat * kHalfDigitBase + un1)
        {
            q1--;
            rhat += vn1;
            if (rhat >= kHalfDigitBase) break;
        }

        ulong un21 = un32 * kHalfDigitBase + un1 - q1 * divisor;
        ulong q0 = un21 / vn1;
        rhat = un21 - q0 * vn1;

        while (q0 >= kHalfDigitBase || q0 * vn0 > rhat * kHalfDigitBase + un0)
        {
            q0--;
            rhat += vn1;
            if (rhat >= kHalfDigitBase) break;
        }

        remainder = (un21 * kHalfDigitBase + un0 - q0 * divisor) >> s;
        return q1 * kHalfDigitBase + q0;
    }

    // ---- comparisons ---------------------------------------------------------------

    public static bool GreaterThanOrEqual(ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B) => Compare(A, B) >= 0;

    public static bool IsDigitNormalized(ReadOnlySpan<ulong> X) => X.Length == 0 || X[^1] != 0;

    public static int CompareNoNormalize(ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        int diff = A.Length - B.Length;
        if (diff != 0) return diff;
        int i = A.Length - 1;
        while (i >= 0 && A[i] == B[i]) i--;
        if (i < 0) return 0;
        return A[i] > B[i] ? 1 : -1;
    }

    /// <summary>Returns r such that r &lt; 0 if A &lt; B; r &gt; 0 if A &gt; B; r == 0 if A == B.</summary>
    public static int Compare(ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B) =>
        CompareNoNormalize(Normalize(A), Normalize(B));

    // ---- addition / subtraction --------------------------------------------------------

    /// <summary>Z := X + Y. Returns Z's top digit.</summary>
    public static ulong Add(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        if (X.Length < Y.Length)
        {
            ReadOnlySpan<ulong> t = X;
            X = Y;
            Y = t;
        }
        Debug.Assert(Z.Length >= X.Length);
        int i = 0;
        ulong carry = 0;
        ulong top = 0;
        for (; i < Y.Length; i++) Z[i] = top = digit_add3(X[i], Y[i], carry, out carry);
        for (; i < X.Length; i++) Z[i] = top = digit_add2(X[i], carry, out carry);
        for (; i < Z.Length; i++)
        {
            Z[i] = top = carry;
            carry = 0;
        }
        return top;
    }

    /// <summary>Z := X - Y. Requires X >= Y. Returns Z's top digit.</summary>
    public static ulong Subtract(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(IsDigitNormalized(X));
        Debug.Assert(IsDigitNormalized(Y));
        Debug.Assert(Z.Length >= X.Length && X.Length >= Y.Length);
        int i = 0;
        ulong borrow = 0;
        ulong top = 0;
        for (; i < Y.Length; i++) Z[i] = top = digit_sub2(X[i], Y[i], borrow, out borrow);
        for (; i < X.Length; i++) Z[i] = top = digit_sub(X[i], borrow, out borrow);
        Debug.Assert(borrow == 0);
        for (; i < Z.Length; i++) Z[i] = top = 0;
        return top;
    }

    public static void SubtractWithNormalization(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y) =>
        Subtract(Z, Normalize(X), Normalize(Y));

    /// <summary>Addition of signed integers. Returns true if the result is negative.</summary>
    public static bool AddSigned(Span<ulong> Z, ReadOnlySpan<ulong> X, bool xNegative, ReadOnlySpan<ulong> Y, bool yNegative)
    {
        if (xNegative == yNegative)
        {
            Add(Z, X, Y);
            return xNegative;
        }
        X = Normalize(X);
        Y = Normalize(Y);
        if (CompareNoNormalize(X, Y) >= 0)
        {
            Subtract(Z, X, Y);
            return xNegative;
        }
        Subtract(Z, Y, X);
        return !xNegative;
    }

    /// <summary>Subtraction of signed integers. Returns true if the result is negative.</summary>
    public static bool SubtractSigned(Span<ulong> Z, ReadOnlySpan<ulong> X, bool xNegative, ReadOnlySpan<ulong> Y, bool yNegative)
    {
        if (xNegative != yNegative)
        {
            Add(Z, X, Y);
            return xNegative;
        }
        X = Normalize(X);
        Y = Normalize(Y);
        if (CompareNoNormalize(X, Y) >= 0)
        {
            Subtract(Z, X, Y);
            return xNegative;
        }
        Subtract(Z, Y, X);
        return !xNegative;
    }

    /// <summary>Z := X + 1</summary>
    public static void AddOne(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        ulong carry = 1;
        int i = 0;
        for (; carry > 0 && i < X.Length; i++) Z[i] = digit_add2(X[i], carry, out carry);
        if (carry > 0) Z[i++] = carry;
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>Z := X - 1</summary>
    public static void SubtractOne(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        ulong borrow = 1;
        int i = 0;
        for (; borrow > 0; i++) Z[i] = digit_sub(X[i], borrow, out borrow);
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>X += y.</summary>
    public static void Add(Span<ulong> X, ulong y)
    {
        ulong carry = y;
        int i = 0;
        do
        {
            X[i] = digit_add2(X[i], carry, out carry);
            i++;
        } while (carry != 0);
    }

    /// <summary>X -= y.</summary>
    public static void Subtract(Span<ulong> X, ulong y)
    {
        ulong borrow = y;
        int i = 0;
        do
        {
            X[i] = digit_sub(X[i], borrow, out borrow);
            i++;
        } while (borrow != 0);
    }

    /// <summary>Z += X. Returns the "carry" (0 or 1) after adding all of X's digits.</summary>
    public static ulong InplaceAddAndReturnCarry(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        ulong carry = 0;
        for (int i = 0; i < X.Length; i++) Z[i] = digit_add3(Z[i], X[i], carry, out carry);
        return carry;
    }

    /// <summary>Z -= X. Returns the "borrow" (0 or 1) after subtracting all of X's digits.</summary>
    public static ulong InplaceSubAndReturnBorrow(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        ulong borrow = 0;
        for (int i = 0; i < X.Length; i++) Z[i] = digit_sub2(Z[i], X[i], borrow, out borrow);
        return borrow;
    }

    /// <summary>Adds exactly Y's digits to the matching digits in X, storing
    /// the result in (part of) Z, and returns the carry.</summary>
    public static ulong AddAndReturnCarry(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(Z.Length >= Y.Length && X.Length >= Y.Length);
        ulong carry = 0;
        for (int i = 0; i < Y.Length; i++) Z[i] = digit_add3(X[i], Y[i], carry, out carry);
        return carry;
    }

    public static ulong SubtractAndReturnBorrow(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(Z.Length >= Y.Length && X.Length >= Y.Length);
        ulong borrow = 0;
        for (int i = 0; i < Y.Length; i++) Z[i] = digit_sub2(X[i], Y[i], borrow, out borrow);
        return borrow;
    }

    // vector-arithmetic-inl.h

    /// <summary>Z += X. Returns carry on overflow.</summary>
    public static ulong AddAndReturnOverflow(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        X = Normalize(X);
        if (X.Length == 0) return 0;
        Debug.Assert(Z.Length >= X.Length);
        ulong carry = 0;
        int i = 0;
        for (; i < X.Length; i++) Z[i] = digit_add3(Z[i], X[i], carry, out carry);
        for (; i < Z.Length && carry != 0; i++) Z[i] = digit_add2(Z[i], carry, out carry);
        return carry;
    }

    /// <summary>Z -= X. Returns borrow on overflow.</summary>
    public static ulong SubAndReturnBorrow(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        X = Normalize(X);
        if (X.Length == 0) return 0;
        Debug.Assert(Z.Length >= X.Length);
        ulong borrow = 0;
        int i = 0;
        for (; i < X.Length; i++) Z[i] = digit_sub2(Z[i], X[i], borrow, out borrow);
        for (; i < Z.Length && borrow != 0; i++) Z[i] = digit_sub(Z[i], borrow, out borrow);
        return borrow;
    }

    public static bool IsBitNormalized(ReadOnlySpan<ulong> X) => (X[^1] >> (kDigitBits - 1)) == 1;

    public static int BitLength(ReadOnlySpan<ulong> X) => X.Length * kDigitBits - CountLeadingZeros(X[^1]);

    // ---- multiplication -------------------------------------------------------------------

    /// <summary>Z := X * y, where y is a single digit. Returns Z's top digit.</summary>
    public static ulong MultiplySingle(Span<ulong> Z, ReadOnlySpan<ulong> X, ulong y)
    {
        Debug.Assert(y != 0);
        ulong carry = 0;
        ulong high = 0;
        for (int i = 0; i < X.Length; i++)
        {
            ulong low = digit_mul(X[i], y, out ulong newHigh);
            Z[i] = digit_add3(low, high, carry, out carry);
            high = newHigh;
        }
        ulong top = carry + high;
        Z[X.Length] = top;
        for (int i = X.Length + 1; i < Z.Length; i++) Z[i] = top = 0;
        return top;
    }

    // The BODY(min, max) macro of bigint-inl.h.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MulColumn(ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, int i, int min, int max, ulong zi,
                           ref ulong carry, ref ulong next, ref ulong nextCarry)
    {
        for (int j = min; j <= max; j++)
        {
            ulong low = digit_mul(X[j], Y[i - j], out ulong high);
            zi = digit_add2(zi, low, out ulong carrybit);
            carry += carrybit;
            next = digit_add2(next, high, out carrybit);
            nextCarry += carrybit;
        }
        return zi;
    }

    public static ulong MultiplySchoolbook(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(IsDigitNormalized(X));
        Debug.Assert(IsDigitNormalized(Y));
        Debug.Assert(X.Length >= Y.Length);
        Debug.Assert(Z.Length >= X.Length + Y.Length);
        if (X.Length == 0 || Y.Length == 0)
        {
            Z.Clear();
            return 0;
        }
        ulong next, nextCarry = 0, carry = 0;
        // Unrolled first iteration: it's trivial.
        Z[0] = digit_mul(X[0], Y[0], out next);
        int i = 1;
        // Unrolled second iteration: a little less setup.
        if (i < Y.Length)
        {
            ulong zi = next;
            next = 0;
            Z[i] = MulColumn(X, Y, i, 0, 1, zi, ref carry, ref next, ref nextCarry);
            i++;
        }
        // Main part: since X.len() >= Y.len() > i, no bounds checks are needed.
        for (; i < Y.Length; i++)
        {
            ulong zi = digit_add2(next, carry, out carry);
            next = nextCarry + carry;
            carry = 0;
            nextCarry = 0;
            Z[i] = MulColumn(X, Y, i, 0, i, zi, ref carry, ref next, ref nextCarry);
        }
        // Last part: i exceeds Y now, we have to be careful about bounds.
        int loopEnd = X.Length + Y.Length - 2;
        for (; i <= loopEnd; i++)
        {
            int maxXIndex = Math.Min(i, X.Length - 1);
            int maxYIndex = Y.Length - 1;
            int minXIndex = i - maxYIndex;
            ulong zi = digit_add2(next, carry, out carry);
            next = nextCarry + carry;
            carry = 0;
            nextCarry = 0;
            Z[i] = MulColumn(X, Y, i, minXIndex, maxXIndex, zi, ref carry, ref next, ref nextCarry);
        }
        // Write the last digit, and zero out any extra space in Z.
        ulong top = digit_add2(next, carry, out carry);
        Z[i++] = top;
        Debug.Assert(carry == 0);
        for (; i < Z.Length; i++) Z[i] = top = 0;
        return top;
    }

    /// <summary>For the needs of CachedMod, computes product digits in columns
    /// [start_position, X.len() + Y.len()). Lower columns are skipped, so
    /// inbound carries are dropped and the result is approximate: the
    /// accumulated error is bounded by 2 * B^(start_position + 1).</summary>
    static void MultiplySpecialHigh(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y, int startPosition)
    {
        Debug.Assert(X.Length >= Y.Length);
        Debug.Assert(Y.Length >= 1);
        // The shrinking-phase formula below requires {i >= Y.len() - 1}.
        Debug.Assert(startPosition >= Y.Length - 1);
        Debug.Assert(Z.Length >= X.Length + Y.Length);

        ulong next = 0, nextCarry = 0, carry = 0;
        int loopEnd = X.Length + Y.Length - 2;
        int i = startPosition;
        for (; i <= loopEnd; i++)
        {
            int maxYIndex = Y.Length - 1;
            int minXIndex = i - maxYIndex;
            int maxXIndex = Math.Min(i, X.Length - 1);
            ulong zi = digit_add2(next, carry, out carry);
            next = nextCarry + carry;
            carry = 0;
            nextCarry = 0;
            Z[i] = MulColumn(X, Y, i, minXIndex, maxXIndex, zi, ref carry, ref next, ref nextCarry);
        }
        Z[i] = digit_add2(next, carry, out carry);
        Debug.Assert(carry == 0);
    }

    /// <summary>For the needs of CachedMod, computes only the low Z.len() digits of X*Y.</summary>
    static void MultiplySpecialLow(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(Y.Length >= 1);
        Debug.Assert(X.Length >= 2);
        Debug.Assert(X.Length >= Y.Length - 1);

        ulong next, nextCarry = 0, carry = 0;
        // Unrolled first iteration: it's trivial.
        Z[0] = digit_mul(X[0], Y[0], out next);
        int i = 1;
        // Unrolled second iteration: a little less setup.
        if (i < Y.Length)
        {
            ulong zi = next;
            next = 0;
            Z[i] = MulColumn(X, Y, i, 0, 1, zi, ref carry, ref next, ref nextCarry);
            i++;
        }
        // Main part: no bounds checks in the loop.
        int loopEnd = Z.Length - 1;
        int mainEnd = Math.Min(Math.Min(X.Length, Y.Length), loopEnd);
        for (; i < mainEnd; i++)
        {
            ulong zi = digit_add2(next, carry, out carry);
            next = nextCarry + carry;
            carry = 0;
            nextCarry = 0;
            Z[i] = MulColumn(X, Y, i, 0, i, zi, ref carry, ref next, ref nextCarry);
        }
        // Last part: we have to be careful about bounds.
        for (; i <= loopEnd; i++)
        {
            int maxXIndex = Math.Min(i, X.Length - 1);
            int maxYIndex = Math.Min(i, Y.Length - 1);
            int minXIndex = i - maxYIndex;
            ulong zi = digit_add2(next, carry, out carry);
            next = nextCarry + carry;
            carry = 0;
            nextCarry = 0;
            Z[i] = MulColumn(X, Y, i, minXIndex, maxXIndex, zi, ref carry, ref next, ref nextCarry);
        }
    }

    /// <summary>"Small" variant of Z := X * Y. Returns (true, top digit) if it
    /// handled the input, (false, 0) otherwise (use Processor.MultiplyLarge).</summary>
    public static (bool, ulong) MultiplySmall(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        Debug.Assert(IsDigitNormalized(X));
        Debug.Assert(IsDigitNormalized(Y));
        if (X.Length == 0 || Y.Length == 0)
        {
            Z.Clear();
            return (true, 0);
        }
        if (X.Length < Y.Length)
        {
            ReadOnlySpan<ulong> t = X;
            X = Y;
            Y = t;
        }
        if (Y.Length == 1) return (true, MultiplySingle(Z, X, Y[0]));
        if (Y.Length >= BigintConfig.kKaratsubaThreshold) return (false, 0);
        return (true, MultiplySchoolbook(Z, X, Y));
    }

    // ---- division ----------------------------------------------------------------------

    /// <summary>Computes Q(uotient) and remainder for A/b, such that
    /// Q = (A - remainder) / b, with 0 &lt;= remainder &lt; b. Returns Q's top
    /// digit. Q may be the same as A for an in-place division.</summary>
    public static ulong DivideSingle(Span<ulong> Q, out ulong remainder, ReadOnlySpan<ulong> A, ulong b)
    {
        Debug.Assert(b != 0);
        Debug.Assert(A.Length > 0);
        ulong rem = 0;
        ulong top;
        int length = A.Length;
        if (A[length - 1] >= b)
        {
            Debug.Assert(Q.Length >= A.Length);
            int i = length - 1;
            Q[i] = top = digit_div(rem, A[i], b, out rem);
            while (i-- > 0) Q[i] = digit_div(rem, A[i], b, out rem);
            for (int j = length; j < Q.Length; j++) Q[j] = top = 0;
        }
        else
        {
            Debug.Assert(Q.Length >= A.Length - 1);
            rem = A[length - 1];
            top = 0;
            int i = length - 2;
            if (i >= 0)
            {
                Q[i] = top = digit_div(rem, A[i], b, out rem);
                while (i-- > 0) Q[i] = digit_div(rem, A[i], b, out rem);
            }
            for (int j = length - 1; j < Q.Length; j++) Q[j] = top = 0;
        }
        remainder = rem;
        return top;
    }

    public static ulong ModSingle(ReadOnlySpan<ulong> A, ulong b)
    {
        Debug.Assert(b != 0);
        Debug.Assert(A.Length > 0);
        ulong rem = 0;
        int length = A.Length;
        if (A[length - 1] >= b)
        {
            for (int i = length; i-- > 0;) digit_div(rem, A[i], b, out rem);
        }
        else
        {
            rem = A[length - 1];
            for (int i = length - 1; i-- > 0;) digit_div(rem, A[i], b, out rem);
        }
        return rem;
    }

    /// <summary>"Small" variant of Q := A / B.</summary>
    public static (bool, ulong) DivideSmall(Span<ulong> Q, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(IsDigitNormalized(A));
        Debug.Assert(IsDigitNormalized(B));
        Debug.Assert(B.Length > 0);
        int cmp = CompareNoNormalize(A, B);
        if (cmp < 0)
        {
            Q.Clear();
            return (true, 0);
        }
        if (cmp == 0)
        {
            ulong top = 1;
            Q[0] = 1;
            for (int i = 1; i < Q.Length; i++) Q[i] = top = 0;
            return (true, top);
        }
        if (B.Length == 1) return (true, DivideSingle(Q, out _, A, B[0]));
        return (false, 0);
    }

    /// <summary>"Small" variant of R := A % B.</summary>
    public static (bool, ulong) ModuloSmall(Span<ulong> R, ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        Debug.Assert(IsDigitNormalized(A));
        Debug.Assert(IsDigitNormalized(B));
        Debug.Assert(B.Length > 0);
        int cmp = CompareNoNormalize(A, B);
        if (cmp < 0)
        {
            ulong top = 0;
            for (int i = 0; i < A.Length; i++) R[i] = top = A[i];
            for (int i = A.Length; i < R.Length; i++) R[i] = top = 0;
            return (true, top);
        }
        if (cmp == 0)
        {
            R.Clear();
            return (true, 0);
        }
        if (B.Length == 1)
        {
            ulong top = ModSingle(A, B[0]);
            R[0] = top;
            for (int i = 1; i < R.Length; i++) R[i] = top = 0;
            return (true, top);
        }
        return (false, 0);
    }

    // ---- result lengths -------------------------------------------------------------------

    public static int AddResultLength(int xLength, int yLength) => Math.Max(xLength, yLength) + 1;

    public static int AddSignedResultLength(int xLength, int yLength, bool sameSign) =>
        sameSign ? AddResultLength(xLength, yLength) : Math.Max(xLength, yLength);

    public static int SubtractResultLength(int xLength, int yLength) => xLength;

    public static int SubtractSignedResultLength(int xLength, int yLength, bool sameSign) =>
        sameSign ? Math.Max(xLength, yLength) : AddResultLength(xLength, yLength);

    public static int MultiplyResultLength(ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y) => X.Length + Y.Length;

    public static int DivideResultLength(ReadOnlySpan<ulong> A, ReadOnlySpan<ulong> B)
    {
        // The Barrett division algorithm needs one extra digit for temporary use.
        int kBarrettExtraScratch = B.Length >= BigintConfig.kBarrettThreshold ? 1 : 0;
        return A.Length - B.Length + 1 + kBarrettExtraScratch;
    }

    public static int ModuloResultLength(ReadOnlySpan<ulong> B) => B.Length;

    // ---- bitwise operations -----------------------------------------------------------------
    // The bitwise operations assume that negative BigInts are represented as
    // sign+magnitude. Their behavior depends on the sign of the inputs: negative
    // inputs perform an implicit conversion to two's complement representation.

    /// <summary>Z := X &amp; Y</summary>
    public static void BitwiseAnd_PosPos(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        int pairs = Math.Min(X.Length, Y.Length);
        Debug.Assert(Z.Length >= pairs);
        int i = 0;
        for (; i < pairs; i++) Z[i] = X[i] & Y[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>Call this for a BigInt x = (magnitude=X, negative=true).</summary>
    public static void BitwiseAnd_NegNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // (-x) & (-y) == ~(x-1) & ~(y-1)
        //             == ~((x-1) | (y-1))
        //             == -(((x-1) | (y-1)) + 1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong xBorrow = 1;
        ulong yBorrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = digit_sub(X[i], xBorrow, out xBorrow) | digit_sub(Y[i], yBorrow, out yBorrow);
        // (At least) one of the next two loops will perform zero iterations:
        for (; i < X.Length; i++) Z[i] = digit_sub(X[i], xBorrow, out xBorrow);
        for (; i < Y.Length; i++) Z[i] = digit_sub(Y[i], yBorrow, out yBorrow);
        Debug.Assert(xBorrow == 0);
        Debug.Assert(yBorrow == 0);
        for (; i < Z.Length; i++) Z[i] = 0;
        Add(Z, 1);
    }

    /// <summary>Positive X, negative Y. Callers must swap arguments as needed.</summary>
    public static void BitwiseAnd_PosNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // x & (-y) == x & ~(y-1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong borrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = X[i] & ~digit_sub(Y[i], borrow, out borrow);
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    public static void BitwiseOr_PosPos(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        int pairs = Math.Min(X.Length, Y.Length);
        int i = 0;
        for (; i < pairs; i++) Z[i] = X[i] | Y[i];
        // (At least) one of the next two loops will perform zero iterations:
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Y.Length; i++) Z[i] = Y[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    public static void BitwiseOr_NegNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // (-x) | (-y) == ~(x-1) | ~(y-1)
        //             == ~((x-1) & (y-1))
        //             == -(((x-1) & (y-1)) + 1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong xBorrow = 1;
        ulong yBorrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = digit_sub(X[i], xBorrow, out xBorrow) & digit_sub(Y[i], yBorrow, out yBorrow);
        // Any leftover borrows don't matter, the '&' would drop them anyway.
        for (; i < Z.Length; i++) Z[i] = 0;
        Add(Z, 1);
    }

    public static void BitwiseOr_PosNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // x | (-y) == x | ~(y-1) == ~((y-1) &~ x) == -(((y-1) &~ x) + 1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong borrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = digit_sub(Y[i], borrow, out borrow) & ~X[i];
        for (; i < Y.Length; i++) Z[i] = digit_sub(Y[i], borrow, out borrow);
        Debug.Assert(borrow == 0);
        for (; i < Z.Length; i++) Z[i] = 0;
        Add(Z, 1);
    }

    public static void BitwiseXor_PosPos(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        int pairs = X.Length;
        if (Y.Length < X.Length)
        {
            ReadOnlySpan<ulong> t = X;
            X = Y;
            Y = t;
            pairs = X.Length;
        }
        Debug.Assert(X.Length <= Y.Length);
        int i = 0;
        for (; i < pairs; i++) Z[i] = X[i] ^ Y[i];
        for (; i < Y.Length; i++) Z[i] = Y[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    public static void BitwiseXor_NegNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // (-x) ^ (-y) == ~(x-1) ^ ~(y-1) == (x-1) ^ (y-1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong xBorrow = 1;
        ulong yBorrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = digit_sub(X[i], xBorrow, out xBorrow) ^ digit_sub(Y[i], yBorrow, out yBorrow);
        // (At least) one of the next two loops will perform zero iterations:
        for (; i < X.Length; i++) Z[i] = digit_sub(X[i], xBorrow, out xBorrow);
        for (; i < Y.Length; i++) Z[i] = digit_sub(Y[i], yBorrow, out yBorrow);
        Debug.Assert(xBorrow == 0);
        Debug.Assert(yBorrow == 0);
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    public static void BitwiseXor_PosNeg(Span<ulong> Z, ReadOnlySpan<ulong> X, ReadOnlySpan<ulong> Y)
    {
        // x ^ (-y) == x ^ ~(y-1) == ~(x ^ (y-1)) == -((x ^ (y-1)) + 1)
        int pairs = Math.Min(X.Length, Y.Length);
        ulong borrow = 1;
        int i = 0;
        for (; i < pairs; i++) Z[i] = X[i] ^ digit_sub(Y[i], borrow, out borrow);
        // (At least) one of the next two loops will perform zero iterations:
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Y.Length; i++) Z[i] = digit_sub(Y[i], borrow, out borrow);
        Debug.Assert(borrow == 0);
        for (; i < Z.Length; i++) Z[i] = 0;
        Add(Z, 1);
    }

    // ---- shifts -------------------------------------------------------------------------------

    /// <summary>Z := X &lt;&lt; shift (arbitrary shift amount).</summary>
    public static void LeftShift(Span<ulong> Z, ReadOnlySpan<ulong> X, ulong shift)
    {
        int digitShift = (int)(shift / kDigitBits);
        int bitsShift = (int)(shift % kDigitBits);

        int i = 0;
        for (; i < digitShift; ++i) Z[i] = 0;
        if (bitsShift == 0)
        {
            for (; i < X.Length + digitShift; ++i) Z[i] = X[i - digitShift];
            for (; i < Z.Length; ++i) Z[i] = 0;
        }
        else
        {
            ulong carry = 0;
            for (; i < X.Length + digitShift; ++i)
            {
                ulong d = X[i - digitShift];
                Z[i] = (d << bitsShift) | carry;
                carry = d >> (kDigitBits - bitsShift);
            }
            if (carry != 0) Z[i++] = carry;
            for (; i < Z.Length; ++i) Z[i] = 0;
        }
    }

    /// <summary>Z := X &gt;&gt; shift, rounding towards -infinity for negative
    /// values as prepared by RightShift_ResultLength.</summary>
    public static void RightShift(Span<ulong> Z, ReadOnlySpan<ulong> X, ulong shift, in RightShiftState state)
    {
        int digitShift = (int)(shift / kDigitBits);
        int bitsShift = (int)(shift % kDigitBits);

        int i = 0;
        if (bitsShift == 0)
        {
            for (; i < X.Length - digitShift; ++i) Z[i] = X[i + digitShift];
        }
        else
        {
            ulong carry = X[digitShift] >> bitsShift;
            for (; i < X.Length - digitShift - 1; ++i)
            {
                ulong d = X[i + digitShift + 1];
                Z[i] = (d << (kDigitBits - bitsShift)) | carry;
                carry = d >> bitsShift;
            }
            Z[i++] = carry;
        }
        for (; i < Z.Length; ++i) Z[i] = 0;

        if (state.must_round_down)
        {
            // Rounding down (a negative value) means adding one to
            // its absolute value. This cannot overflow.
            Add(Z, 1);
        }
    }

    public static int RightShift_ResultLength(ReadOnlySpan<ulong> X, bool xSign, ulong shift, out RightShiftState state)
    {
        state = default;
        ulong digitShiftL = shift / kDigitBits;
        int bitsShift = (int)(shift % kDigitBits);
        if ((ulong)X.Length <= digitShiftL) return 0;
        int digitShift = (int)digitShiftL;
        int resultLength = X.Length - digitShift;

        // For negative numbers, round down if any bit was shifted out (so that e.g.
        // -5n >> 1n == -3n and not -2n). Check now whether this will happen and
        // whether it can cause overflow into a new digit.
        bool mustRoundDown = false;
        if (xSign)
        {
            ulong mask = (1UL << bitsShift) - 1;
            if ((X[digitShift] & mask) != 0)
            {
                mustRoundDown = true;
            }
            else
            {
                for (int i = 0; i < digitShift; i++)
                {
                    if (X[i] != 0)
                    {
                        mustRoundDown = true;
                        break;
                    }
                }
            }
        }
        // If bits_shift is non-zero, it frees up bits, preventing overflow.
        if (mustRoundDown && bitsShift == 0)
        {
            // Overflow cannot happen if the most significant digit has unset bits.
            bool roundingCanOverflow = digit_ismax(X[^1]);
            if (roundingCanOverflow) ++resultLength;
        }

        state.must_round_down = mustRoundDown;
        return resultLength;
    }

    // ---- AsIntN / AsUintN ---------------------------------------------------------------------

    /// <summary>Z := (least significant n bits of X).</summary>
    static void TruncateToNBits(Span<ulong> Z, ReadOnlySpan<ulong> X, int n)
    {
        int digits = DIV_CEIL(n, kDigitBits);
        int bits = n % kDigitBits;
        // Copy all digits except the MSD.
        int last = digits - 1;
        for (int i = 0; i < last; i++) Z[i] = X[i];
        // The MSD might contain extra bits that we don't want.
        ulong msd = X[last];
        if (bits != 0)
        {
            int drop = kDigitBits - bits;
            msd = (msd << drop) >> drop;
        }
        Z[last] = msd;
    }

    /// <summary>Z := 2**n - (least significant n bits of X).</summary>
    static void TruncateAndSubFromPowerOfTwo(Span<ulong> Z, ReadOnlySpan<ulong> X, int n)
    {
        int digits = DIV_CEIL(n, kDigitBits);
        int bits = n % kDigitBits;
        // Process all digits except the MSD. Take X's digits, then simulate leading
        // zeroes.
        int last = digits - 1;
        int haveX = Math.Min(last, X.Length);
        ulong borrow = 0;
        int i = 0;
        for (; i < haveX; i++) Z[i] = digit_sub2(0, X[i], borrow, out borrow);
        for (; i < last; i++) Z[i] = digit_sub(0, borrow, out borrow);

        // The MSD might contain extra bits that we don't want.
        ulong msd = last < X.Length ? X[last] : 0;
        if (bits == 0)
        {
            Z[last] = digit_sub2(0, msd, borrow, out borrow);
        }
        else
        {
            int drop = kDigitBits - bits;
            msd = (msd << drop) >> drop;
            ulong minuendMsd = 1UL << bits;
            ulong resultMsd = digit_sub2(minuendMsd, msd, borrow, out borrow);
            Debug.Assert(borrow == 0);  // result < 2^n.
            // If all subtracted bits were zero, we have to get rid of the
            // materialized minuend_msd again.
            Z[last] = resultMsd & (minuendMsd - 1);
        }
    }

    /// <summary>Z := (least significant n bits of X, interpreted as a signed
    /// n-bit integer). Returns true if the result is negative; Z will hold the
    /// absolute value.</summary>
    public static bool AsIntN(Span<ulong> Z, ReadOnlySpan<ulong> X, bool xNegative, int n)
    {
        Debug.Assert(X.Length > 0);
        Debug.Assert(n > 0);
        Debug.Assert(AsIntNResultLength(X, xNegative, n) > 0);
        int neededDigits = DIV_CEIL(n, kDigitBits);
        ulong topDigit = X[neededDigits - 1];
        ulong compareDigit = 1UL << ((n - 1) % kDigitBits);
        // The canonical algorithm would be: convert negative numbers to two's
        // complement representation, truncate, convert back to sign+magnitude. To
        // avoid the conversions, we predict what the result would be:
        // When the (n-1)th bit is not set:
        //  - truncate the absolute value
        //  - preserve the sign.
        // When the (n-1)th bit is set:
        //  - subtract the truncated absolute value from 2**n to simulate two's
        //    complement representation
        //  - flip the sign, unless it's the special case where the input is negative
        //    and the result is the minimum n-bit integer. E.g. asIntN(3, -12) => -4.
        bool hasBit = (topDigit & compareDigit) == compareDigit;
        if (!hasBit)
        {
            TruncateToNBits(Z, X, n);
            return xNegative;
        }
        TruncateAndSubFromPowerOfTwo(Z, X, n);
        if (!xNegative) return true;  // Result is negative.
        // Scan for the special case (see above): if all bits below the (n-1)th
        // digit are zero, the result is negative.
        if ((topDigit & (compareDigit - 1)) != 0) return false;
        for (int i = neededDigits - 2; i >= 0; i--)
        {
            if (X[i] != 0) return false;
        }
        return true;
    }

    /// <summary>Returns -1 when the operation would return X unchanged.</summary>
    public static int AsIntNResultLength(ReadOnlySpan<ulong> X, bool xNegative, int n)
    {
        int neededDigits = DIV_CEIL(n, kDigitBits);
        // Generally: decide based on number of digits, and bits in the top digit.
        if (X.Length < neededDigits) return -1;
        if (X.Length > neededDigits) return neededDigits;
        ulong topDigit = X[neededDigits - 1];
        ulong compareDigit = 1UL << ((n - 1) % kDigitBits);
        if (topDigit < compareDigit) return -1;
        if (topDigit > compareDigit) return neededDigits;
        // Special case: if X == -2**(n-1), truncation is a no-op.
        if (!xNegative) return neededDigits;
        for (int i = neededDigits - 2; i >= 0; i--)
        {
            if (X[i] != 0) return neededDigits;
        }
        return -1;
    }

    /// <summary>Returns -1 when the operation would return X unchanged.</summary>
    public static int AsUintN_Pos_ResultLength(ReadOnlySpan<ulong> X, int n)
    {
        int neededDigits = DIV_CEIL(n, kDigitBits);
        if (X.Length < neededDigits) return -1;
        if (X.Length > neededDigits) return neededDigits;
        int bitsInTopDigit = n % kDigitBits;
        if (bitsInTopDigit == 0) return -1;
        ulong topDigit = X[neededDigits - 1];
        if ((topDigit >> bitsInTopDigit) == 0) return -1;
        return neededDigits;
    }

    /// <summary>Z := (least significant n bits of X).</summary>
    public static void AsUintN_Pos(Span<ulong> Z, ReadOnlySpan<ulong> X, int n)
    {
        Debug.Assert(AsUintN_Pos_ResultLength(X, n) > 0);
        TruncateToNBits(Z, X, n);
    }

    /// <summary>Same, but X is the absolute value of a negative BigInt.</summary>
    public static void AsUintN_Neg(Span<ulong> Z, ReadOnlySpan<ulong> X, int n) => TruncateAndSubFromPowerOfTwo(Z, X, n);

    public static int AsUintN_Neg_ResultLength(int n) => ((n - 1) / kDigitBits) + 1;
}
