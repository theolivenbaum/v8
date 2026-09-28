// Port of src/base/utils/random-number-generator.{h,cc}.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Utils;

/// <summary>
/// A random number generator using the xorshift128+ algorithm. This is the
/// generator V8 uses for internal randomness and for seeding Math.random.
/// Instances are not thread safe.
/// </summary>
public sealed class RandomNumberGenerator
{
    /// <summary>EntropySource is used as a callback function when V8 needs a
    /// source of entropy.</summary>
    public delegate bool EntropySource(Span<byte> buffer);

    static readonly Lock s_entropyMutex = new();
    static EntropySource? s_entropySource;

    long _initialSeed;
    ulong _state0;
    ulong _state1;

    public static void SetEntropySource(EntropySource? source)
    {
        lock (s_entropyMutex) s_entropySource = source;
    }

    public RandomNumberGenerator()
    {
        // Check if embedder supplied an entropy source.
        lock (s_entropyMutex)
        {
            if (s_entropySource is not null)
            {
                Span<byte> bytes = stackalloc byte[sizeof(long)];
                if (s_entropySource(bytes))
                {
                    SetSeed(BitConverter.ToInt64(bytes));
                    return;
                }
            }
        }
        // V8 reads /dev/urandom (rand_s on Windows, arc4random on the BSDs);
        // the operating system's generator behind this call is the same.
        Span<byte> seed = stackalloc byte[sizeof(long)];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seed);
        SetSeed(BitConverter.ToInt64(seed));
    }

    public RandomNumberGenerator(long seed) => SetSeed(seed);

    // Returns the next pseudorandom, uniformly distributed int value from this
    // random number generator's sequence. All 2^32 possible integer values are
    // produced with (approximately) equal probability.
    public int NextInt() => Next(32);

    // Returns a pseudorandom, uniformly distributed int value between 0
    // (inclusive) and the specified max value (exclusive).
    public int NextInt(int max)
    {
        Debug.Assert(0 < max);

        // Fast path if max is a power of 2.
        if (Bits.IsPowerOfTwo(max))
        {
            return (int)((max * (long)Next(31)) >> 31);
        }

        while (true)
        {
            int rnd = Next(31);
            int val = rnd % max;
            if (int.MaxValue - (rnd - val) >= (max - 1))
            {
                return val;
            }
        }
    }

    // Returns the next pseudorandom, uniformly distributed boolean value.
    public bool NextBool() => Next(1) != 0;

    // Returns the next pseudorandom double, uniformly distributed in [0, 1).
    public double NextDouble() => ToDouble(XorShift128(ref _state0, ref _state1));

    // Returns the next pseudorandom, uniformly distributed int64 value.
    public long NextInt64() => (long)XorShift128(ref _state0, ref _state1);

    // Fills the elements of a specified array of bytes with random numbers.
    public void NextBytes(Span<byte> buffer)
    {
        for (int n = 0; n < buffer.Length; ++n)
        {
            buffer[n] = (byte)Next(8);
        }
    }

    static List<ulong> ComplementSample(HashSet<ulong> set, ulong max)
    {
        List<ulong> result = new((int)(max - (ulong)set.Count));
        for (ulong i = 0; i < max; i++)
        {
            if (!set.Contains(i))
            {
                result.Add(i);
            }
        }
        return result;
    }

    // Returns the next pseudorandom set of n unique uint64 values smaller than
    // max. n must be less or equal to max.
    public List<ulong> NextSample(ulong max, int n)
    {
        Check((ulong)n <= max, "n <= max");

        if (n == 0)
        {
            return [];
        }

        // Choose to select or exclude, whatever needs fewer generator calls.
        int smallerPart = (int)Math.Min(max - (ulong)n, (ulong)n);
        HashSet<ulong> selected = [];

        int counter = 0;
        while (selected.Count != smallerPart && counter / 3 < smallerPart)
        {
            ulong x = (ulong)(NextDouble() * max);
            Check(x < max, "x < max");

            selected.Add(x);
            counter++;
        }

        if (selected.Count == smallerPart)
        {
            if (smallerPart != n)
            {
                return ComplementSample(selected, max);
            }
            return [.. selected];
        }

        // Failed to select numbers in smaller_part * 3 steps, try different approach.
        return NextSampleSlow(max, n, selected);
    }

    // Returns the next pseudorandom set of n unique uint64 values smaller than
    // max. n must be less or equal to max. max - |excluded| must be less or
    // equal to n.
    //
    // Generates list of all possible values and removes random values from it
    // until size reaches n.
    public List<ulong> NextSampleSlow(ulong max, int n, HashSet<ulong>? excluded = null)
    {
        excluded ??= [];
        Check(max - (ulong)excluded.Count >= (ulong)n, "max - excluded.size() >= n");

        List<ulong> result = new((int)(max - (ulong)excluded.Count));

        for (ulong i = 0; i < max; i++)
        {
            if (!excluded.Contains(i))
            {
                result.Add(i);
            }
        }

        // Decrease result vector until it contains values to select or exclude,
        // whatever needs fewer generator calls.
        int largerPart = (int)Math.Max(max - (ulong)n, (ulong)n);

        // Excluded set may cause that initial result is already smaller than
        // larget_part.
        while (result.Count != largerPart && result.Count > n)
        {
            int x = (int)(NextDouble() * result.Count);
            Check(x < result.Count, "x < result.size()");

            (result[x], result[^1]) = (result[^1], result[x]);
            result.RemoveAt(result.Count - 1);
        }

        if (result.Count != n)
        {
            return ComplementSample([.. result], max);
        }
        return result;
    }

    // Override the current seed.
    public void SetSeed(long seed)
    {
        _initialSeed = seed;
        _state0 = MurmurHash3((ulong)seed);
        _state1 = MurmurHash3(~_state0);
        Check(_state0 != 0 || _state1 != 0, "state0_ != 0 || state1_ != 0");
    }

    public long initial_seed => _initialSeed;

    // Static and exposed for external use.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble(ulong random)
    {
        // Get a random [0,2**53) integer value (up to MAX_SAFE_INTEGER) by dropping
        // 11 bits of the state.
        double random_0_to_2_53 = random >> 11;
        // Map this to [0,1) by division with 2**53.
        const double k2_53 = 1UL << 53;
        return random_0_to_2_53 / k2_53;
    }

    // Static and exposed for external use.
    // Generate random numbers using xorshift128+.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong XorShift128(ref ulong state0, ref ulong state1)
    {
        ulong s1 = state0;
        ulong s0 = state1;
        state0 = s0;
        s1 ^= s1 << 23;
        s1 ^= s1 >> 17;
        s1 ^= s0;
        s1 ^= s0 >> 26;
        state1 = s1;
        return s0 + s1;
    }

    public static ulong MurmurHash3(ulong h)
    {
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    int Next(int bits)
    {
        Debug.Assert(0 < bits);
        Debug.Assert(32 >= bits);
        ulong random = XorShift128(ref _state0, ref _state1);
        return (int)(random >> (64 - bits));
    }

    // V8's CHECK is fatal; here it throws.
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + message);
    }
}
