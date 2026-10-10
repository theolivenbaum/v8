// Port of test/unittests/base/bits-unittest.cc. The death tests
// (RoundUpToPowerOfTwo beyond 2^31 / 2^63) are DCHECKs in V8 and Debug.Assert
// here, so they are not ported.

using static V8Sharp.Base.Bits;

namespace V8Sharp.Base.Tests.Base;

public class BitsUnitTest
{
    [Fact]
    public void CountPopulation8()
    {
        Assert.Equal(0u, CountPopulation((byte)0));
        Assert.Equal(1u, CountPopulation((byte)1));
        Assert.Equal(2u, CountPopulation((byte)0x11));
        Assert.Equal(4u, CountPopulation((byte)0x0F));
        Assert.Equal(6u, CountPopulation((byte)0x3F));
        Assert.Equal(8u, CountPopulation((byte)0xFF));
    }

    [Fact]
    public void CountPopulation16()
    {
        Assert.Equal(0u, CountPopulation((ushort)0));
        Assert.Equal(1u, CountPopulation((ushort)1));
        Assert.Equal(4u, CountPopulation((ushort)0x1111));
        Assert.Equal(8u, CountPopulation((ushort)0xF0F0));
        Assert.Equal(12u, CountPopulation((ushort)0xF0FF));
        Assert.Equal(16u, CountPopulation((ushort)0xFFFF));
    }

    [Fact]
    public void CountPopulation32()
    {
        Assert.Equal(0u, CountPopulation(0u));
        Assert.Equal(1u, CountPopulation(1u));
        Assert.Equal(8u, CountPopulation(0x11111111u));
        Assert.Equal(16u, CountPopulation(0xF0F0F0F0u));
        Assert.Equal(24u, CountPopulation(0xFFF0F0FFu));
        Assert.Equal(32u, CountPopulation(0xFFFFFFFFu));
    }

    [Fact]
    public void CountPopulation64()
    {
        Assert.Equal(0u, CountPopulation(0UL));
        Assert.Equal(1u, CountPopulation(1UL));
        Assert.Equal(2u, CountPopulation(0x8000000000000001UL));
        Assert.Equal(8u, CountPopulation(0x11111111UL));
        Assert.Equal(16u, CountPopulation(0xF0F0F0F0UL));
        Assert.Equal(24u, CountPopulation(0xFFF0F0FFUL));
        Assert.Equal(32u, CountPopulation(0xFFFFFFFFUL));
        Assert.Equal(16u, CountPopulation(0x1111111111111111UL));
        Assert.Equal(32u, CountPopulation(0xF0F0F0F0F0F0F0F0UL));
        Assert.Equal(48u, CountPopulation(0xFFF0F0FFFFF0F0FFUL));
        Assert.Equal(64u, CountPopulation(0xFFFFFFFFFFFFFFFFUL));
    }

    [Fact]
    public void CountLeadingZeros16()
    {
        Assert.Equal(16u, CountLeadingZeros((ushort)0));
        Assert.Equal(15u, CountLeadingZeros((ushort)1));
        for (int shift = 0; shift <= 15; shift++)
            Assert.Equal(15u - (uint)shift, CountLeadingZeros((ushort)(1 << shift)));
        Assert.Equal(4u, CountLeadingZeros((ushort)0x0F0F));
    }

    [Fact]
    public void CountLeadingZeros32()
    {
        Assert.Equal(32u, CountLeadingZeros(0u));
        Assert.Equal(31u, CountLeadingZeros(1u));
        for (int shift = 0; shift <= 31; shift++)
            Assert.Equal(31u - (uint)shift, CountLeadingZeros(1u << shift));
        Assert.Equal(4u, CountLeadingZeros(0x0F0F0F0Fu));
    }

    [Fact]
    public void CountLeadingZeros64()
    {
        Assert.Equal(64u, CountLeadingZeros(0UL));
        Assert.Equal(63u, CountLeadingZeros(1UL));
        for (int shift = 0; shift <= 63; shift++)
            Assert.Equal(63u - (uint)shift, CountLeadingZeros(1UL << shift));
        Assert.Equal(36u, CountLeadingZeros(0x0F0F0F0FUL));
        Assert.Equal(4u, CountLeadingZeros(0x0F0F0F0F00000000UL));
    }

    [Fact]
    public void CountTrailingZeros16()
    {
        Assert.Equal(16u, CountTrailingZeros((ushort)0));
        Assert.Equal(15u, CountTrailingZeros((ushort)0x8000));
        for (int shift = 0; shift <= 15; shift++)
            Assert.Equal((uint)shift, CountTrailingZeros((ushort)(1 << shift)));
        Assert.Equal(4u, CountTrailingZeros((ushort)0xF0F0u));
    }

    [Fact]
    public void CountTrailingZerosu32()
    {
        Assert.Equal(32u, CountTrailingZeros(0u));
        Assert.Equal(31u, CountTrailingZeros(0x80000000u));
        for (int shift = 0; shift <= 31; shift++)
            Assert.Equal((uint)shift, CountTrailingZeros(1u << shift));
        Assert.Equal(4u, CountTrailingZeros(0xF0F0F0F0u));
    }

    [Fact]
    public void CountTrailingZerosi32()
    {
        Assert.Equal(32u, CountTrailingZeros(0));
        for (int shift = 0; shift <= 31; shift++)
            Assert.Equal((uint)shift, CountTrailingZeros(1 << shift));
        Assert.Equal(4u, CountTrailingZeros(0x70F0F0F0));
        Assert.Equal(2u, CountTrailingZeros(-4));
        Assert.Equal(0u, CountTrailingZeros(-1));
    }

    [Fact]
    public void CountTrailingZeros64()
    {
        Assert.Equal(64u, CountTrailingZeros(0UL));
        Assert.Equal(63u, CountTrailingZeros(0x8000000000000000UL));
        for (int shift = 0; shift <= 63; shift++)
            Assert.Equal((uint)shift, CountTrailingZeros(1UL << shift));
        Assert.Equal(4u, CountTrailingZeros(0xF0F0F0F0UL));
        Assert.Equal(36u, CountTrailingZeros(0xF0F0F0F000000000UL));
    }

    [Fact]
    public void IsPowerOfTwo32()
    {
        Assert.False(IsPowerOfTwo(0U));
        for (int shift = 0; shift <= 31; shift++)
        {
            Assert.True(IsPowerOfTwo(1U << shift));
            Assert.False(IsPowerOfTwo((1U << shift) + 5U));
            Assert.False(IsPowerOfTwo(~(1U << shift)));
        }
        for (int shift = 2; shift <= 31; shift++)
            Assert.False(IsPowerOfTwo((1U << shift) - 1U));
        Assert.False(IsPowerOfTwo(0xFFFFFFFFu));
    }

    [Fact]
    public void IsPowerOfTwo64()
    {
        Assert.False(IsPowerOfTwo(0UL));
        for (int shift = 0; shift <= 63; shift++)
        {
            Assert.True(IsPowerOfTwo(1UL << shift));
            Assert.False(IsPowerOfTwo((1UL << shift) + 5U));
            Assert.False(IsPowerOfTwo(~(1UL << shift)));
        }
        for (int shift = 2; shift <= 63; shift++)
            Assert.False(IsPowerOfTwo((1UL << shift) - 1U));
        Assert.False(IsPowerOfTwo(0xFFFFFFFFFFFFFFFFUL));
    }

    [Fact]
    public void WhichPowerOfTwo32()
    {
        for (int shift = 0; shift <= 30; shift++) Assert.Equal(shift, WhichPowerOfTwo(1 << shift));
        for (int shift = 0; shift <= 31; shift++) Assert.Equal(shift, WhichPowerOfTwo(1u << shift));
    }

    [Fact]
    public void WhichPowerOfTwo64()
    {
        for (int shift = 0; shift <= 62; shift++) Assert.Equal(shift, WhichPowerOfTwo(1L << shift));
        for (int shift = 0; shift <= 63; shift++) Assert.Equal(shift, WhichPowerOfTwo(1UL << shift));
    }

    [Fact]
    public void RoundUpToPowerOfTwo32_()
    {
        for (int shift = 0; shift <= 31; shift++)
            Assert.Equal(1u << shift, RoundUpToPowerOfTwo32(1u << shift));
        Assert.Equal(1u, RoundUpToPowerOfTwo32(0));
        Assert.Equal(1u, RoundUpToPowerOfTwo32(1));
        Assert.Equal(4u, RoundUpToPowerOfTwo32(3));
        Assert.Equal(0x80000000u, RoundUpToPowerOfTwo32(0x7FFFFFFFu));
    }

    [Fact]
    public void RoundUpToPowerOfTwo64_()
    {
        for (int shift = 0; shift <= 63; shift++)
        {
            ulong value = 1UL << shift;
            Assert.Equal(value, RoundUpToPowerOfTwo64(value));
        }
        Assert.Equal(1UL, RoundUpToPowerOfTwo64(0));
        Assert.Equal(1UL, RoundUpToPowerOfTwo64(1));
        Assert.Equal(4UL, RoundUpToPowerOfTwo64(3));
        Assert.Equal(1UL << 63, RoundUpToPowerOfTwo64((1UL << 63) - 1));
        Assert.Equal(1UL << 63, RoundUpToPowerOfTwo64(1UL << 63));
    }

    [Fact]
    public void RoundDownToPowerOfTwo32_()
    {
        for (int shift = 0; shift <= 31; shift++)
            Assert.Equal(1u << shift, RoundDownToPowerOfTwo32(1u << shift));
        Assert.Equal(0u, RoundDownToPowerOfTwo32(0));
        Assert.Equal(4u, RoundDownToPowerOfTwo32(5));
        Assert.Equal(0x80000000u, RoundDownToPowerOfTwo32(0x80000001u));
    }

    [Fact]
    public void RotateRight32_()
    {
        for (uint shift = 0; shift <= 31; shift++) Assert.Equal(0u, RotateRight32(0u, shift));
        Assert.Equal(1u, RotateRight32(1, 0));
        Assert.Equal(1u, RotateRight32(2, 1));
        Assert.Equal(0x80000000u, RotateRight32(1, 1));
    }

    [Fact]
    public void RotateRight64_()
    {
        for (ulong shift = 0; shift <= 63; shift++) Assert.Equal(0UL, RotateRight64(0u, shift));
        Assert.Equal(1UL, RotateRight64(1, 0));
        Assert.Equal(1UL, RotateRight64(2, 1));
        Assert.Equal(0x8000000000000000UL, RotateRight64(1, 1));
    }

    [Fact]
    public void SignedAddOverflow32_()
    {
        Assert.False(SignedAddOverflow32(0, 0, out int val));
        Assert.Equal(0, val);
        Assert.True(SignedAddOverflow32(int.MaxValue, 1, out val));
        Assert.Equal(int.MinValue, val);
        Assert.True(SignedAddOverflow32(int.MinValue, -1, out val));
        Assert.Equal(int.MaxValue, val);
        Assert.True(SignedAddOverflow32(int.MaxValue, int.MaxValue, out val));
        Assert.Equal(-2, val);
        for (int i = 1; i <= 50; i++)
        {
            for (int j = 1; j <= i; j++)
            {
                Assert.False(SignedAddOverflow32(i, j, out val));
                Assert.Equal(i + j, val);
            }
        }
    }

    [Fact]
    public void SignedSubOverflow32_()
    {
        Assert.False(SignedSubOverflow32(0, 0, out int val));
        Assert.Equal(0, val);
        Assert.True(SignedSubOverflow32(int.MinValue, 1, out val));
        Assert.Equal(int.MaxValue, val);
        Assert.True(SignedSubOverflow32(int.MaxValue, -1, out val));
        Assert.Equal(int.MinValue, val);
        for (int i = 1; i <= 50; i++)
        {
            for (int j = 1; j <= i; j++)
            {
                Assert.False(SignedSubOverflow32(i, j, out val));
                Assert.Equal(i - j, val);
            }
        }
    }

    [Fact]
    public void SignedMulHigh32_()
    {
        Assert.Equal(0, SignedMulHigh32(0, 0));
        for (int i = 1; i <= 50; i++)
            for (int j = 1; j <= i; j++) Assert.Equal(0, SignedMulHigh32(i, j));
        Assert.Equal(-1073741824, SignedMulHigh32(int.MaxValue, int.MinValue));
        Assert.Equal(-1073741824, SignedMulHigh32(int.MinValue, int.MaxValue));
        Assert.Equal(1, SignedMulHigh32(1024 * 1024 * 1024, 4));
        Assert.Equal(2, SignedMulHigh32(8 * 1024, 1024 * 1024));
    }

    [Fact]
    public void SignedMulHighAndAdd32_()
    {
        for (int i = 1; i <= 50; i++)
        {
            Assert.Equal(i, SignedMulHighAndAdd32(0, 0, i));
            for (int j = 1; j <= i; j++) Assert.Equal(i, SignedMulHighAndAdd32(j, j, i));
            Assert.Equal(i + 1, SignedMulHighAndAdd32(1024 * 1024 * 1024, 4, i));
        }
    }

    [Fact]
    public void SignedDiv32_()
    {
        Assert.Equal(int.MinValue, SignedDiv32(int.MinValue, -1));
        Assert.Equal(int.MaxValue, SignedDiv32(int.MaxValue, 1));
        for (int i = 0; i <= 50; i++)
        {
            Assert.Equal(0, SignedDiv32(i, 0));
            for (int j = 1; j <= i; j++)
            {
                Assert.Equal(1, SignedDiv32(j, j));
                Assert.Equal(i / j, SignedDiv32(i, j));
                Assert.Equal(-i / j, SignedDiv32(i, -j));
            }
        }
    }

    [Fact]
    public void SignedMod32_()
    {
        Assert.Equal(0, SignedMod32(int.MinValue, -1));
        Assert.Equal(0, SignedMod32(int.MaxValue, 1));
        for (int i = 0; i <= 50; i++)
        {
            Assert.Equal(0, SignedMod32(i, 0));
            for (int j = 1; j <= i; j++)
            {
                Assert.Equal(0, SignedMod32(j, j));
                Assert.Equal(i % j, SignedMod32(i, j));
                Assert.Equal(i % j, SignedMod32(i, -j));
            }
        }
    }

    [Fact]
    public void UnsignedAddOverflow32_()
    {
        Assert.False(UnsignedAddOverflow32(0, 0, out uint val));
        Assert.Equal(0u, val);
        Assert.True(UnsignedAddOverflow32(uint.MaxValue, 1u, out val));
        Assert.Equal(uint.MinValue, val);
        Assert.True(UnsignedAddOverflow32(uint.MaxValue, uint.MaxValue, out val));
        for (uint i = 1; i <= 50; i++)
        {
            for (uint j = 1; j <= i; j++)
            {
                Assert.False(UnsignedAddOverflow32(i, j, out val));
                Assert.Equal(i + j, val);
            }
        }
    }

    [Fact]
    public void UnsignedDiv32_()
    {
        for (uint i = 0; i <= 50; i++)
        {
            Assert.Equal(0u, UnsignedDiv32(i, 0));
            for (uint j = i + 1; j <= 100; j++)
            {
                Assert.Equal(1u, UnsignedDiv32(j, j));
                Assert.Equal(i / j, UnsignedDiv32(i, j));
            }
        }
    }

    [Fact]
    public void UnsignedMod32_()
    {
        for (uint i = 0; i <= 50; i++)
        {
            Assert.Equal(0u, UnsignedMod32(i, 0));
            for (uint j = i + 1; j <= 100; j++)
            {
                Assert.Equal(0u, UnsignedMod32(j, j));
                Assert.Equal(i % j, UnsignedMod32(i, j));
            }
        }
    }

    // Not in bits-unittest.cc: the 64-bit helpers against System.Int128.
    [Fact]
    public void Wide64AgainstInt128()
    {
        Random rng = new(42);
        long[] edges = [0, 1, -1, long.MaxValue, long.MinValue, int.MaxValue, int.MinValue, 1L << 32, -(1L << 32)];
        for (int n = 0; n < 5000; n++)
        {
            long a = n < 81 ? edges[n % 9] : rng.NextInt64() >> rng.Next(64);
            long b = n < 81 ? edges[n / 9] : rng.NextInt64() >> rng.Next(64);
            Int128 p = (Int128)a * b;
            Assert.Equal((long)(p >> 64), SignedMulHigh64(a, b));
            Assert.Equal((ulong)(((UInt128)(ulong)a * (ulong)b) >> 64), UnsignedMulHigh64((ulong)a, (ulong)b));
            Assert.Equal(p < long.MinValue || p > long.MaxValue, SignedMulOverflow64(a, b, out long m));
            Assert.Equal(unchecked(a * b), m);
            Int128 s = (Int128)a + b;
            Assert.Equal(s < long.MinValue || s > long.MaxValue, SignedAddOverflow64(a, b, out long sv));
            Assert.Equal(unchecked(a + b), sv);
            Assert.Equal((long)Int128.Clamp(s, long.MinValue, long.MaxValue), SignedSaturatedAdd64(a, b));
            Int128 d = (Int128)a - b;
            Assert.Equal(d < long.MinValue || d > long.MaxValue, SignedSubOverflow64(a, b, out long dv));
            Assert.Equal(unchecked(a - b), dv);
            Assert.Equal((long)Int128.Clamp(d, long.MinValue, long.MaxValue), SignedSaturatedSub64(a, b));
        }
    }

    [Fact]
    public void ReverseAndBitWidth()
    {
        Assert.Equal(0x80000000u, ReverseBits(1u));
        Assert.Equal((byte)0x80, ReverseBits((byte)1));
        Assert.Equal(0x0123456789ABCDEFUL, ReverseBits(ReverseBits(0x0123456789ABCDEFUL)));
        Assert.Equal(0x78563412u, ReverseBytes(0x12345678u));
        Assert.Equal((ushort)0x3412, ByteReverse16(0x1234));
        Assert.Equal(0, BitWidth(0u));
        Assert.Equal(1, BitWidth(1u));
        Assert.Equal(64, BitWidth(ulong.MaxValue));
        Assert.Equal(32u, CountLeadingSignBits(-1));
        Assert.Equal(31u, CountLeadingSignBits(-2));
        Assert.Equal(31u, CountLeadingSignBits(1));
        Assert.Equal(0b10000, ClearLsb(0b10010));
        Assert.Equal(0, ClearLsb(0));
    }
}
