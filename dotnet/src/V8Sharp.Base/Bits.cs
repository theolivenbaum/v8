// Port of src/base/bits.h and src/base/bits.cc.
//
// The C++ templates become overloads per width. The builtins (popcount, clz,
// ctz, bswap, the overflow checks) map onto System.Numerics.BitOperations,
// BinaryPrimitives and Math.BigMul, which the JIT lowers to the same
// instructions.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base;

public static class Bits
{
    // CountPopulation(value) returns the number of bits set in value.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountPopulation(byte value) => (uint)BitOperations.PopCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountPopulation(ushort value) => (uint)BitOperations.PopCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountPopulation(uint value) => (uint)BitOperations.PopCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountPopulation(ulong value) => (uint)BitOperations.PopCount(value);

    // ReverseBits(value) returns |value| in reverse bit order.
    public static byte ReverseBits(byte value) => (byte)(ReverseBits((uint)value) >> 24);
    public static ushort ReverseBits(ushort value) => (ushort)(ReverseBits((uint)value) >> 16);
    public static uint ReverseBits(uint value)
    {
        uint result = 0;
        for (int i = 0; i < 32; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
    public static ulong ReverseBits(ulong value)
    {
        ulong result = 0;
        for (int i = 0; i < 64; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
    public static int ReverseBits(int value) => (int)ReverseBits((uint)value);
    public static long ReverseBits(long value) => (long)ReverseBits((ulong)value);

    // ReverseBytes(value) returns |value| in reverse byte order.
    public static byte ReverseBytes(byte value) => value;
    public static ushort ReverseBytes(ushort value) => BinaryPrimitives.ReverseEndianness(value);
    public static uint ReverseBytes(uint value) => BinaryPrimitives.ReverseEndianness(value);
    public static ulong ReverseBytes(ulong value) => BinaryPrimitives.ReverseEndianness(value);
    public static int ReverseBytes(int value) => BinaryPrimitives.ReverseEndianness(value);
    public static long ReverseBytes(long value) => BinaryPrimitives.ReverseEndianness(value);

    public static uint Unsigned(int value) => (uint)value;
    public static ulong Unsigned(long value) => (ulong)value;
    public static int Signed(uint value) => (int)value;
    public static long Signed(ulong value) => (long)value;

    // CountLeadingZeros(value) returns the number of zero bits following the
    // most significant 1 bit in |value| if |value| is non-zero, otherwise it
    // returns {sizeof(T) * 8}.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountLeadingZeros(byte value) => (uint)BitOperations.LeadingZeroCount((uint)value) - 24;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountLeadingZeros(ushort value) => (uint)BitOperations.LeadingZeroCount((uint)value) - 16;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountLeadingZeros(uint value) => (uint)BitOperations.LeadingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountLeadingZeros(ulong value) => (uint)BitOperations.LeadingZeroCount(value);
    public static uint CountLeadingZeros32(uint value) => CountLeadingZeros(value);
    public static uint CountLeadingZeros64(ulong value) => CountLeadingZeros(value);

    // The number of leading zeros for a positive number,
    // the number of leading ones for a negative number.
    public static uint CountLeadingSignBits(int value) =>
        value < 0 ? CountLeadingZeros(~Unsigned(value)) : CountLeadingZeros(Unsigned(value));
    public static uint CountLeadingSignBits(long value) =>
        value < 0 ? CountLeadingZeros(~Unsigned(value)) : CountLeadingZeros(Unsigned(value));

    // CountTrailingZeros(value) returns the number of zero bits preceding the
    // least significant 1 bit in |value| if |value| is non-zero, otherwise it
    // returns {sizeof(T) * 8}.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(byte value) => value == 0 ? 8u : (uint)BitOperations.TrailingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(ushort value) => value == 0 ? 16u : (uint)BitOperations.TrailingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(uint value) => (uint)BitOperations.TrailingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(ulong value) => (uint)BitOperations.TrailingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(int value) => (uint)BitOperations.TrailingZeroCount(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CountTrailingZeros(long value) => (uint)BitOperations.TrailingZeroCount(value);
    public static uint CountTrailingZeros32(uint value) => CountTrailingZeros(value);
    public static uint CountTrailingZeros64(ulong value) => CountTrailingZeros(value);

    public static uint CountTrailingZerosNonZero(uint value)
    {
        Debug.Assert(value != 0);
        return (uint)BitOperations.TrailingZeroCount(value);
    }
    public static uint CountTrailingZerosNonZero(ulong value)
    {
        Debug.Assert(value != 0);
        return (uint)BitOperations.TrailingZeroCount(value);
    }

    // Returns true iff |value| is a power of 2.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(uint value) => value > 0 && (value & (value - 1)) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(ulong value) => value > 0 && (value & (value - 1)) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;

    // Identical to {CountTrailingZeros}, but only works for powers of 2.
    public static int WhichPowerOfTwo(uint value)
    {
        Debug.Assert(IsPowerOfTwo(value));
        return BitOperations.TrailingZeroCount(value);
    }
    public static int WhichPowerOfTwo(ulong value)
    {
        Debug.Assert(IsPowerOfTwo(value));
        return BitOperations.TrailingZeroCount(value);
    }
    public static int WhichPowerOfTwo(int value)
    {
        Debug.Assert(IsPowerOfTwo(value));
        return BitOperations.TrailingZeroCount(value);
    }
    public static int WhichPowerOfTwo(long value)
    {
        Debug.Assert(IsPowerOfTwo(value));
        return BitOperations.TrailingZeroCount(value);
    }

    // RoundUpToPowerOfTwo32(value) returns the smallest power of two which is
    // greater than or equal to |value|. If you pass in a |value| that is already
    // a power of two, it is returned as is. |value| must be less than or equal
    // to 0x80000000u. Uses computation based on leading zeros if we have
    // compiler support for that. Falls back to the implementation from
    // "Hacker's Delight" by Henry S. Warren, Jr., figure 3-3, page 48, where
    // the posted implementation for 64-bit is incorrect.
    public static uint RoundUpToPowerOfTwo32(uint value)
    {
        Debug.Assert(value <= 1u << 31);
        if (value != 0) --value;
        // CountLeadingZeros(0) is 32, so 0 and 1 both round up to 1.
        return 1u << (32 - BitOperations.LeadingZeroCount(value));
    }

    // Same for 64 bit integers. |value| must be <= 2^63
    public static ulong RoundUpToPowerOfTwo64(ulong value)
    {
        Debug.Assert(value <= 1UL << 63);
        if (value != 0) --value;
        return 1UL << (64 - BitOperations.LeadingZeroCount(value));
    }

    // Same for size_t integers.
    public static nuint RoundUpToPowerOfTwo(nuint value) => (nuint)RoundUpToPowerOfTwo64(value);

    // RoundDownToPowerOfTwo32(value) returns the greatest power of two which is
    // less than or equal to |value|. If you pass in a |value| that is already a
    // power of two, it is returned as is.
    public static uint RoundDownToPowerOfTwo32(uint value)
    {
        if (value > 0x80000000u) return 0x80000000u;
        uint result = RoundUpToPowerOfTwo32(value);
        if (result > value) result >>= 1;
        return result;
    }

    // Precondition: 0 <= shift < 32
    public static uint RotateRight32(uint value, uint shift) => BitOperations.RotateRight(value, (int)shift);
    // Precondition: 0 <= shift < 32
    public static uint RotateLeft32(uint value, uint shift) => BitOperations.RotateLeft(value, (int)shift);
    // Precondition: 0 <= shift < 64
    public static ulong RotateRight64(ulong value, ulong shift) => BitOperations.RotateRight(value, (int)shift);
    // Precondition: 0 <= shift < 64
    public static ulong RotateLeft64(ulong value, ulong shift) => BitOperations.RotateLeft(value, (int)shift);

    // As in C++, the result is an int.
    public static int ClearLsb(int value) => value & (value - 1);
    public static int ClearLsb(long value) => (int)(value & (value - 1));
    public static int ClearLsb(uint value) => (int)(value & (value - 1));
    public static int ClearLsb(ulong value) => (int)(value & (value - 1));

    // SignedAddOverflow32(lhs,rhs,val) performs a signed summation of |lhs| and
    // |rhs| and stores the result into the variable pointed to by |val| and
    // returns true if the signed summation resulted in an overflow.
    public static bool SignedAddOverflow32(int lhs, int rhs, out int val)
    {
        uint res = (uint)lhs + (uint)rhs;
        val = (int)res;
        return ((res ^ (uint)lhs) & (res ^ (uint)rhs) & (1U << 31)) != 0;
    }

    // SignedSubOverflow32(lhs,rhs,val) performs a signed subtraction of |lhs|
    // and |rhs| and stores the result into the variable pointed to by |val| and
    // returns true if the signed subtraction resulted in an overflow.
    public static bool SignedSubOverflow32(int lhs, int rhs, out int val)
    {
        uint res = (uint)lhs - (uint)rhs;
        val = (int)res;
        return ((res ^ (uint)lhs) & (res ^ ~(uint)rhs) & (1U << 31)) != 0;
    }

    // SignedMulOverflow32(lhs,rhs,val) performs a signed multiplication of
    // |lhs| and |rhs| and stores the result into the variable pointed to by
    // |val| and returns true if the signed multiplication resulted in an
    // overflow.
    public static bool SignedMulOverflow32(int lhs, int rhs, out int val)
    {
        // Compute the result as {int64_t}, then check for overflow.
        long result = (long)lhs * rhs;
        val = (int)result;
        return result < int.MinValue || result > int.MaxValue;
    }

    // SignedAddOverflow64(lhs,rhs,val) performs a signed summation of |lhs| and
    // |rhs| and stores the result into the variable pointed to by |val| and
    // returns true if the signed summation resulted in an overflow.
    public static bool SignedAddOverflow64(long lhs, long rhs, out long val)
    {
        ulong res = (ulong)lhs + (ulong)rhs;
        val = (long)res;
        return ((res ^ (ulong)lhs) & (res ^ (ulong)rhs) & (1UL << 63)) != 0;
    }

    // SignedSubOverflow64(lhs,rhs,val) performs a signed subtraction of |lhs|
    // and |rhs| and stores the result into the variable pointed to by |val| and
    // returns true if the signed subtraction resulted in an overflow.
    public static bool SignedSubOverflow64(long lhs, long rhs, out long val)
    {
        ulong res = (ulong)lhs - (ulong)rhs;
        val = (long)res;
        return ((res ^ (ulong)lhs) & (res ^ ~(ulong)rhs) & (1UL << 63)) != 0;
    }

    // SignedMulOverflow64(lhs,rhs,val) performs a signed multiplication of
    // |lhs| and |rhs| and stores the result into the variable pointed to by
    // |val| and returns true if the signed multiplication resulted in an
    // overflow.
    public static bool SignedMulOverflow64(long lhs, long rhs, out long val)
    {
        long high = Math.BigMul(lhs, rhs, out long low);
        val = low;
        // Overflow unless the high word is the sign extension of the low word.
        return high != (low >> 63);
    }

    // SignedMulHigh32(lhs, rhs) multiplies two signed 32-bit values |lhs| and
    // |rhs|, extracts the most significant 32 bits of the result, and returns
    // those.
    public static int SignedMulHigh32(int lhs, int rhs) => (int)((ulong)((long)lhs * rhs) >> 32);

    // UnsignedMulHigh32(lhs, rhs) multiplies two unsigned 32-bit values |lhs|
    // and |rhs|, extracts the most significant 32 bits of the result, and
    // returns those.
    public static uint UnsignedMulHigh32(uint lhs, uint rhs) => (uint)(((ulong)lhs * rhs) >> 32);

    // SignedMulHigh64(lhs, rhs) multiplies two signed 64-bit values |lhs| and
    // |rhs|, extracts the most significant 64 bits of the result, and returns
    // those.
    public static long SignedMulHigh64(long lhs, long rhs) => Math.BigMul(lhs, rhs, out _);

    // UnsignedMulHigh64(lhs, rhs) multiplies two unsigned 64-bit values |lhs|
    // and |rhs|, extracts the most significant 64 bits of the result, and
    // returns those.
    public static ulong UnsignedMulHigh64(ulong lhs, ulong rhs) => Math.BigMul(lhs, rhs, out _);

    // SignedMulHighAndAdd32(lhs, rhs, acc) multiplies two signed 32-bit values
    // |lhs| and |rhs|, extracts the most significant 32 bits of the result, and
    // adds the accumulate value |acc|.
    public static int SignedMulHighAndAdd32(int lhs, int rhs, int acc) =>
        (int)((uint)acc + (uint)SignedMulHigh32(lhs, rhs));

    // SignedDiv32(lhs, rhs) divides |lhs| by |rhs| and returns the quotient
    // truncated to int32. If |rhs| is zero, then zero is returned. If |lhs|
    // is minint and |rhs| is -1, it returns minint.
    public static int SignedDiv32(int lhs, int rhs)
    {
        if (rhs == 0) return 0;
        if (rhs == -1) return lhs == int.MinValue ? lhs : -lhs;
        return lhs / rhs;
    }

    // SignedDiv64(lhs, rhs) divides |lhs| by |rhs| and returns the quotient
    // truncated to int64. If |rhs| is zero, then zero is returned. If |lhs|
    // is minint and |rhs| is -1, it returns minint.
    public static long SignedDiv64(long lhs, long rhs)
    {
        if (rhs == 0) return 0;
        if (rhs == -1) return lhs == long.MinValue ? lhs : -lhs;
        return lhs / rhs;
    }

    // SignedMod32(lhs, rhs) divides |lhs| by |rhs| and returns the remainder
    // truncated to int32. If either |rhs| is zero or |lhs| is minint and |rhs|
    // is -1, it returns zero.
    public static int SignedMod32(int lhs, int rhs)
    {
        if (rhs == 0 || rhs == -1) return 0;
        return lhs % rhs;
    }

    // SignedMod64(lhs, rhs) divides |lhs| by |rhs| and returns the remainder
    // truncated to int64. If either |rhs| is zero or |lhs| is minint and |rhs|
    // is -1, it returns zero.
    public static long SignedMod64(long lhs, long rhs)
    {
        if (rhs == 0 || rhs == -1) return 0;
        return lhs % rhs;
    }

    // UnsignedAddOverflow32(lhs,rhs,val) performs an unsigned summation of
    // |lhs| and |rhs| and stores the result into the variable pointed to by
    // |val| and returns true if the unsigned summation resulted in an overflow.
    public static bool UnsignedAddOverflow32(uint lhs, uint rhs, out uint val)
    {
        val = lhs + rhs;
        return val < (lhs | rhs);
    }

    // UnsignedDiv32(lhs, rhs) divides |lhs| by |rhs| and returns the quotient
    // truncated to uint32. If |rhs| is zero, then zero is returned.
    public static uint UnsignedDiv32(uint lhs, uint rhs) => rhs != 0 ? lhs / rhs : 0u;
    public static ulong UnsignedDiv64(ulong lhs, ulong rhs) => rhs != 0 ? lhs / rhs : 0u;

    // UnsignedMod32(lhs, rhs) divides |lhs| by |rhs| and returns the remainder
    // truncated to uint32. If |rhs| is zero, then zero is returned.
    public static uint UnsignedMod32(uint lhs, uint rhs) => rhs != 0 ? lhs % rhs : 0u;
    public static ulong UnsignedMod64(ulong lhs, ulong rhs) => rhs != 0 ? lhs % rhs : 0u;

    // Wraparound integer arithmetic without undefined behavior.
    public static int WraparoundAdd32(int lhs, int rhs) => (int)((uint)lhs + (uint)rhs);
    public static int WraparoundNeg32(int x) => (int)(0u - (uint)x);

    // SignedSaturatedAdd64(lhs, rhs) adds |lhs| and |rhs|,
    // checks and returns the result.
    public static long SignedSaturatedAdd64(long lhs, long rhs)
    {
        // Underflow if {lhs + rhs < min}. In that case, return {min}.
        if (rhs < 0 && lhs < long.MinValue - rhs) return long.MinValue;
        // Overflow if {lhs + rhs > max}. In that case, return {max}.
        if (rhs >= 0 && lhs > long.MaxValue - rhs) return long.MaxValue;
        return lhs + rhs;
    }

    // SignedSaturatedSub64(lhs, rhs) subtracts |lhs| by |rhs|,
    // checks and returns the result.
    public static long SignedSaturatedSub64(long lhs, long rhs)
    {
        // Underflow if {lhs - rhs < min}. In that case, return {min}.
        if (rhs > 0 && lhs < long.MinValue + rhs) return long.MinValue;
        // Overflow if {lhs - rhs > max}. In that case, return {max}.
        if (rhs <= 0 && lhs > long.MaxValue + rhs) return long.MaxValue;
        return lhs - rhs;
    }

    public static ushort ByteReverse16(ushort value) => BinaryPrimitives.ReverseEndianness(value);
    public static uint ByteReverse32(uint value) => BinaryPrimitives.ReverseEndianness(value);
    public static ulong ByteReverse64(ulong value) => BinaryPrimitives.ReverseEndianness(value);

    // BitWidth(x) returns the number of bits needed to represent x
    // (std::numeric_limits<T>::digits - CountLeadingZeros(x)).
    public static int BitWidth(uint x) => 32 - BitOperations.LeadingZeroCount(x);
    public static int BitWidth(ulong x) => 64 - BitOperations.LeadingZeroCount(x);
}
