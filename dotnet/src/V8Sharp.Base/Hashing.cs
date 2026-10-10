// Port of src/base/hashing.h.
//
// size_t is ulong: V8Sharp models a 64-bit host and target (V8_TARGET_ARCH_64_BIT,
// !V8_HOST_ARCH_32_BIT), so only the 64-bit branches are ported. The C++
// base::hash<T> functor and the hash_value overload set become overloads of
// Hashing.hash_value; a user type takes part by implementing IHashValue (the
// C++ "has a hash_value() member" concept).

using System.Runtime.CompilerServices;
using V8Sharp.Base.Strings;

namespace V8Sharp.Base;

/// <summary>A type with a hash_value() member (the C++ Hashable concept).</summary>
public interface IHashValue
{
    ulong hash_value();
}

/// <summary>base::Hasher: combines hashes of several values.</summary>
public struct Hasher(ulong seed)
{
    ulong _hash = seed;

    public Hasher() : this(0) { }

    // Retrieve the current hash.
    public readonly ulong hash() => _hash;

    // Combine an existing hash value into this hasher's hash.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hasher AddHash(ulong otherHash)
    {
        _hash = Hashing.hash_combine(_hash, otherHash);
        return this;
    }

    // Hash a value {t} and combine its hash into this hasher's hash.
    public Hasher Add(bool v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(byte v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(sbyte v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(ushort v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(short v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(uint v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(int v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(ulong v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(long v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(float v) => AddHash(Hashing.hash_value(v));
    public Hasher Add(double v) => AddHash(Hashing.hash_value(v));
    public Hasher Add<T>(in T v) where T : IHashValue => AddHash(v.hash_value());

    // Hash a range of values and combine the hashes into this hasher's hash.
    public Hasher AddRange(ReadOnlySpan<int> values)
    {
        foreach (int v in values) Add(v);
        return this;
    }
    public Hasher AddRange(ReadOnlySpan<uint> values)
    {
        foreach (uint v in values) Add(v);
        return this;
    }
    public Hasher AddRange(ReadOnlySpan<long> values)
    {
        foreach (long v in values) Add(v);
        return this;
    }
    public Hasher AddRange(ReadOnlySpan<ulong> values)
    {
        foreach (ulong v in values) Add(v);
        return this;
    }
    public Hasher AddRange(ReadOnlySpan<double> values)
    {
        foreach (double v in values) Add(v);
        return this;
    }
    public Hasher AddRange(ReadOnlySpan<byte> values)
    {
        foreach (byte v in values) Add(v);
        return this;
    }
}

public static class Hashing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong hash_combine(ulong seed, ulong hash)
    {
        const ulong m = 0xC6A4A7935BD1E995UL;
        const int r = 47;

        hash *= m;
        hash ^= hash >> r;
        hash *= m;

        seed ^= hash;
        seed *= m;
        return seed;
    }

    public const ulong kRapidhashSecret1 = 0x2d358dccaa6c78a5UL;
    public const ulong kRapidhashSecret2 = 0x8bb84b93962eacc9UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong hash64(ulong key) => RapidHash.Mix(key ^ kRapidhashSecret1, key ^ kRapidhashSecret2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint hash32(uint key) => (uint)hash64(key);

    public static ulong hash_value(bool v) => v ? 1UL : 0UL;
    public static ulong hash_value(byte v) => v;
    public static ulong hash_value(ushort v) => v;
    public static ulong hash_value(uint v) => hash32(v);
    public static ulong hash_value(ulong v) => hash64(v);
    public static ulong hash_value(sbyte v) => hash_value((byte)v);
    public static ulong hash_value(short v) => hash_value((ushort)v);
    public static ulong hash_value(int v) => hash_value((uint)v);
    public static ulong hash_value(long v) => hash_value((ulong)v);

    // 0 and -0 both hash to zero.
    public static ulong hash_value(float v) => v != 0.0f ? hash_value(BitConverter.SingleToUInt32Bits(v)) : 0;
    public static ulong hash_value(double v) => v != 0.0 ? hash_value(BitConverter.DoubleToUInt64Bits(v)) : 0;

    public static ulong hash_value<T>(in T v) where T : IHashValue => v.hash_value();

    // Arrays hash as their range.
    public static ulong hash_value(ReadOnlySpan<int> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_value(ReadOnlySpan<uint> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_value(ReadOnlySpan<long> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_value(ReadOnlySpan<ulong> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_value(ReadOnlySpan<double> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_value(ReadOnlySpan<byte> v) => new Hasher().AddRange(v).hash();

    public static ulong hash_range(ReadOnlySpan<int> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_range(ReadOnlySpan<uint> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_range(ReadOnlySpan<long> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_range(ReadOnlySpan<ulong> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_range(ReadOnlySpan<double> v) => new Hasher().AddRange(v).hash();
    public static ulong hash_range(ReadOnlySpan<byte> v) => new Hasher().AddRange(v).hash();

    // The variadic hash_combine(vs...) (Hasher::Combine) is spelled
    // new Hasher().Add(v1).Add(v2)...hash() in C#.

    // bit_equal_to / bit_hash: like equal_to and hash, except for floating
    // point, where they compare and hash the bit pattern (so 0 and -0 differ
    // and a NaN equals itself).
    public static bool bit_equal_to(float lhs, float rhs) =>
        BitConverter.SingleToUInt32Bits(lhs) == BitConverter.SingleToUInt32Bits(rhs);
    public static bool bit_equal_to(double lhs, double rhs) =>
        BitConverter.DoubleToUInt64Bits(lhs) == BitConverter.DoubleToUInt64Bits(rhs);
    public static ulong bit_hash(float v) => hash_value(BitConverter.SingleToUInt32Bits(v));
    public static ulong bit_hash(double v) => hash_value(BitConverter.DoubleToUInt64Bits(v));
}
