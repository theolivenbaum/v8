// Port of test/unittests/base/utils/random-number-generator-unittest.cc. The
// death tests become checks for the exception that replaces V8's CHECK.

using V8Sharp.Base.Utils;

namespace V8Sharp.Base.Tests.Base.Utils;

public class RandomNumberGeneratorUnitTest
{
    const int kMaxRuns = 12345;

    public static TheoryData<int> RandomSeeds => [int.MinValue, -1, 0, 1, 42, 100, 1234567890, 987654321, int.MaxValue];

    static void CheckSample(List<ulong> sample, ulong max, int size)
    {
        Assert.Equal(size, sample.Count);

        // Check if values are unique.
        Assert.Equal(sample.Count, new HashSet<ulong>(sample).Count);

        foreach (ulong x in sample)
        {
            Assert.True(x < max);
        }
    }

    static void CheckSlowSample(List<ulong> sample, ulong max, int size, HashSet<ulong> excluded)
    {
        CheckSample(sample, max, size);

        foreach (ulong i in sample)
        {
            Assert.DoesNotContain(i, excluded);
        }
    }

    static void TestNextSample(RandomNumberGenerator rng, ulong max, int size, bool slow = false)
    {
        List<ulong> sample = slow ? rng.NextSampleSlow(max, size) : rng.NextSample(max, size);

        CheckSample(sample, max, size);
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextIntWithMaxValue(int seed)
    {
        RandomNumberGenerator rng = new(seed);
        for (int max = 1; max <= kMaxRuns; ++max)
        {
            int n = rng.NextInt(max);
            Assert.True(0 <= n);
            Assert.True(n < max);
        }
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextBooleanReturnsFalseOrTrue(int seed)
    {
        RandomNumberGenerator rng = new(seed);
        int trues = 0;
        for (int k = 0; k < kMaxRuns; ++k)
        {
            if (rng.NextBool()) trues++;
        }
        Assert.InRange(trues, kMaxRuns / 3, 2 * kMaxRuns / 3);
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextDoubleReturnsValueBetween0And1(int seed)
    {
        RandomNumberGenerator rng = new(seed);
        for (int k = 0; k < kMaxRuns; ++k)
        {
            double d = rng.NextDouble();
            Assert.True(0.0 <= d);
            Assert.True(d < 1.0);
        }
    }

    [Fact]
    public void NextSampleInvalidParam()
    {
        RandomNumberGenerator rng = new(123);
        Assert.Contains("n <= max", Assert.Throws<InvalidOperationException>(() => rng.NextSample(10, 11)).Message);
    }

    [Fact]
    public void NextSampleSlowInvalidParam1()
    {
        RandomNumberGenerator rng = new(123);
        Assert.Contains("max - excluded.size", Assert.Throws<InvalidOperationException>(() => rng.NextSampleSlow(10, 11)).Message);
    }

    [Fact]
    public void NextSampleSlowInvalidParam2()
    {
        RandomNumberGenerator rng = new(123);
        Assert.Contains("max - excluded.size",
                        Assert.Throws<InvalidOperationException>(() => rng.NextSampleSlow(5, 3, [0, 2, 3])).Message);
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSample0(int seed) => TestNextSample(new RandomNumberGenerator(seed), 1, 0);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlow0(int seed) => TestNextSample(new RandomNumberGenerator(seed), 1, 0, true);

    static void Repeat(int seed, ulong m, int n, bool slow)
    {
        RandomNumberGenerator rng = new(seed);
        for (int k = 0; k < kMaxRuns; ++k)
        {
            TestNextSample(rng, m, n, slow);
        }
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSample1(int seed) => Repeat(seed, 10, 1, false);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlow1(int seed) => Repeat(seed, 10, 1, true);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleMax(int seed) => Repeat(seed, 10, 10, false);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowMax(int seed) => Repeat(seed, 10, 10, true);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleHalf(int seed) => Repeat(seed, 10, 5, false);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowHalf(int seed) => Repeat(seed, 10, 5, true);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleMoreThanHalf(int seed) => Repeat(seed, 100, 90, false);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowMoreThanHalf(int seed) => Repeat(seed, 100, 90, true);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleLessThanHalf(int seed) => Repeat(seed, 100, 10, false);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowLessThanHalf(int seed) => Repeat(seed, 100, 10, true);

    static void RepeatExcluded(int seed, ulong m, int n, HashSet<ulong> excluded)
    {
        RandomNumberGenerator rng = new(seed);
        for (int k = 0; k < kMaxRuns; ++k)
        {
            CheckSlowSample(rng.NextSampleSlow(m, n, excluded), m, n, excluded);
        }
    }

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowExcluded(int seed) => RepeatExcluded(seed, 10, 2, [2, 4, 5, 9]);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowExcludedMax1(int seed) => RepeatExcluded(seed, 5, 1, [0, 2, 3, 4]);

    [Theory, MemberData(nameof(RandomSeeds))]
    public void NextSampleSlowExcludedMax2(int seed) => RepeatExcluded(seed, 10, 7, [0, 4, 8]);

    // Not in the V8 test: the generator is xorshift128+ seeded through
    // MurmurHash3's finalizer, so the first outputs are fixed by the seed.
    [Fact]
    public void SeededSequenceIsXorShift128Plus()
    {
        RandomNumberGenerator rng = new(42);
        ulong s0 = RandomNumberGenerator.MurmurHash3(42);
        ulong s1 = RandomNumberGenerator.MurmurHash3(~s0);
        for (int i = 0; i < 100; i++)
        {
            ulong t = s0;
            ulong u = s1;
            s0 = u;
            t ^= t << 23;
            t ^= t >> 17;
            t ^= u;
            t ^= u >> 26;
            s1 = t;
            Assert.Equal((long)(u + t), rng.NextInt64());
        }
        Assert.Equal(42, rng.initial_seed);
        Assert.Equal(0.5, RandomNumberGenerator.ToDouble(1UL << 63));
        Assert.Equal(0.0, RandomNumberGenerator.ToDouble(2047));
    }

    [Fact]
    public void EntropySource()
    {
        try
        {
            RandomNumberGenerator.SetEntropySource(buffer =>
            {
                BitConverter.TryWriteBytes(buffer, 1234L);
                return true;
            });
            Assert.Equal(1234, new RandomNumberGenerator().initial_seed);
        }
        finally
        {
            RandomNumberGenerator.SetEntropySource(null);
        }
    }
}
