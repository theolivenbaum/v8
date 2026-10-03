// Port of test/unittests/base/hashing-unittest.cc. The typed tests run over
// the C# integer types and float/double.

using static V8Sharp.Base.Hashing;

namespace V8Sharp.Base.Tests.Base;

public class HashingUnitTest
{
    static readonly Random Rng = new(1234);

    [Fact]
    public void HashBool()
    {
        Assert.Equal(hash_value(true), hash_value(true));
        Assert.Equal(hash_value(false), hash_value(false));
        Assert.NotEqual(hash_value(true), hash_value(false));
    }

    [Fact]
    public void HashFloatZero() => Assert.Equal(hash_value(0.0f), hash_value(-0.0f));

    [Fact]
    public void HashDoubleZero() => Assert.Equal(hash_value(0.0), hash_value(-0.0));

    // One instance of the typed tests per type.
    static void TypedTests<T>(Func<byte[], T> make, Func<T, ulong> hash, Func<T, ulong> bitHash,
                              Func<T, T, bool> bitEqual, int size) where T : notnull
    {
        byte[] buf = new byte[size];
        T Next()
        {
            Rng.NextBytes(buf);
            return make(buf);
        }

        // EqualToImpliesSameHashCode / BitEqualToImpliesSameBitHash
        T[] values = new T[32];
        for (int i = 0; i < values.Length; i++) values[i] = Next();
        foreach (T v1 in values)
        {
            foreach (T v2 in values)
            {
                if (EqualityComparer<T>.Default.Equals(v1, v2) || (v1 is double d1 && v2 is double d2 && d1 == d2) ||
                    (v1 is float f1 && v2 is float f2 && f1 == f2))
                    Assert.Equal(hash(v1), hash(v2));
                if (bitEqual(v1, v2)) Assert.Equal(bitHash(v1), bitHash(v2));
            }
        }

        // HashIsStateless
        for (int i = 0; i < 128; i++)
        {
            T v = Next();
            Assert.Equal(hash(v), hash(v));
        }

        // HashIsOkish
        HashSet<T> vs = [];
        for (int i = 0; i < 128; i++) vs.Add(Next());
        HashSet<ulong> hs = [];
        foreach (T v in vs) hs.Add(hash(v));
        Assert.True(vs.Count / 4 <= hs.Count);

        // BitEqualTo
        byte[] b1 = new byte[size], b2 = new byte[size];
        for (int i = 0; i < 128; i++)
        {
            Rng.NextBytes(b1);
            Rng.NextBytes(b2);
            T v1 = make(b1), v2 = make(b2);
            Assert.True(bitEqual(v1, v1));
            Assert.True(bitEqual(v2, v2));
            Assert.Equal(b1.AsSpan().SequenceEqual(b2), bitEqual(v1, v2));
        }
    }

    [Fact]
    public void TypedHashingTests()
    {
        TypedTests(b => (sbyte)b[0], v => hash_value(v), v => hash_value(v), (a, b) => a == b, 1);
        TypedTests(b => b[0], v => hash_value(v), v => hash_value(v), (a, b) => a == b, 1);
        TypedTests(b => BitConverter.ToInt16(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 2);
        TypedTests(b => BitConverter.ToUInt16(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 2);
        TypedTests(b => BitConverter.ToInt32(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 4);
        TypedTests(b => BitConverter.ToUInt32(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 4);
        TypedTests(b => BitConverter.ToInt64(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 8);
        TypedTests(b => BitConverter.ToUInt64(b), v => hash_value(v), v => hash_value(v), (a, b) => a == b, 8);
        TypedTests(b => BitConverter.ToSingle(b), v => hash_value(v), v => bit_hash(v), (a, b) => bit_equal_to(a, b), 4);
        TypedTests(b => BitConverter.ToDouble(b), v => hash_value(v), v => bit_hash(v), (a, b) => bit_equal_to(a, b), 8);
    }

    [Fact]
    public void HashValueArrayUsesHashRange()
    {
        int[] ints = new int[128];
        for (int i = 0; i < ints.Length; i++) ints[i] = Rng.Next(int.MinValue, int.MaxValue);
        Assert.Equal(hash_range(ints), hash_value((ReadOnlySpan<int>)ints));
        ulong[] longs = new ulong[128];
        for (int i = 0; i < longs.Length; i++) longs[i] = (ulong)Rng.NextInt64();
        Assert.Equal(hash_range(longs), hash_value((ReadOnlySpan<ulong>)longs));
    }

    readonly struct Foo(int x, double y) : IHashValue
    {
        public ulong hash_value() => new Hasher().Add(x).Add(y).hash();
    }

    [Fact]
    public void HashUsesArgumentDependentLookup()
    {
        int[] kIntValues = [int.MinValue, -1, 0, 1, 42, int.MaxValue];
        double[] kDoubleValues = [double.Epsilon * (1L << 52), -1, -0, 0, 1, double.MaxValue];
        foreach (int x in kIntValues)
        {
            foreach (double y in kDoubleValues)
            {
                Foo foo = new(x, y);
                Assert.Equal(new Hasher().Add(x).Add(y).hash(), hash_value(foo));
            }
        }
    }

    [Fact]
    public void BitEqualToFloat()
    {
        Assert.False(bit_equal_to(0.0f, -0.0f));
        Assert.False(bit_equal_to(-0.0f, 0.0f));
        float qNaN = float.NaN;
        float sNaN = BitConverter.UInt32BitsToSingle(0x7FA00000);
        Assert.True(bit_equal_to(qNaN, qNaN));
        Assert.True(bit_equal_to(sNaN, sNaN));
    }

    [Fact]
    public void BitHashFloatDifferentForZeroAndMinusZero() => Assert.NotEqual(bit_hash(0.0f), bit_hash(-0.0f));

    [Fact]
    public void BitEqualToDouble()
    {
        Assert.False(bit_equal_to(0.0, -0.0));
        Assert.False(bit_equal_to(-0.0, 0.0));
        double qNaN = double.NaN;
        double sNaN = BitConverter.UInt64BitsToDouble(0x7FF4000000000000);
        Assert.True(bit_equal_to(qNaN, qNaN));
        Assert.True(bit_equal_to(sNaN, sNaN));
    }

    [Fact]
    public void BitHashDoubleDifferentForZeroAndMinusZero() => Assert.NotEqual(bit_hash(0.0), bit_hash(-0.0));

    // Not in hashing-unittest.cc: the 64-bit hash_combine and hash64 values,
    // computed by hand from the definitions (murmur2 mix; rapid_mix with the
    // default rapidhash secrets).
    [Fact]
    public void KnownValues()
    {
        const ulong m = 0xC6A4A7935BD1E995UL;
        ulong five = 5;
        ulong h = five * m;
        h ^= h >> 47;
        h *= m;
        Assert.Equal((7 ^ h) * m, hash_combine(7, 5));
        ulong hi = Math.BigMul(3 ^ kRapidhashSecret1, 3 ^ kRapidhashSecret2, out ulong lo);
        Assert.Equal(hi ^ lo, hash64(3));
        Assert.Equal((uint)(hi ^ lo), hash32(3));
        Assert.Equal(hash32(3), hash_value(3));
    }
}
