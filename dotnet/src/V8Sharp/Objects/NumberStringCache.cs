// Port of src/objects/number-string-cache.{h,cc} and number-string-cache-inl.h:
// the caches Factory::NumberToString consults before converting a number
// (SmiStringCache for Smis, DoubleStringCache for other doubles). Both start
// at kInitialSize entries and switch to their full size (the
// --smi-string-cache-size / --double-string-cache-size flags) on the first
// collision, unless memory saver mode is on.
//
// Deviation: V8 clears both caches in a full GC (Heap::FlushNumberStringCache);
// V8Sharp has no GC hook, so the cached strings live until they are
// overwritten. The caches are bounded, and string identity is not observable.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's NumberCacheMode (factory-base.h).</summary>
public enum NumberCacheMode
{
    kIgnore,
    kSetOnly,
    kBoth,
}

/// <summary>V8's SmiStringCache: maps non-zero Smis to their strings.</summary>
public sealed class SmiStringCache
{
    public const uint kInitialSize = 128;

    // Empty entries hold the key 0 (V8's kEmptySentinel, Smi::zero()) and a
    // null value.
    readonly int[] _keys;
    readonly JSString?[] _values;

    SmiStringCache(uint capacity)
    {
        _keys = new int[capacity];
        _values = new JSString?[capacity];
    }

    public static SmiStringCache New(uint capacity) => new(capacity);

    public uint Capacity => (uint)_keys.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetEntryFor(int number) => (uint)number & (Capacity - 1);

    /// <summary>SmiStringCache::Get: the cached string, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSString? Get(uint entry, int number) => _keys[entry] == number ? _values[entry] : null;

    /// <summary>
    /// SmiStringCache::Set: puts the entry, first switching to the full-size
    /// cache when the entry is occupied. Returns the cache now in use.
    /// </summary>
    public SmiStringCache Set(Isolate isolate, uint entry, int number, JSString value)
    {
        Debug.Assert(number != 0);
        SmiStringCache cache = this;
        if (Capacity == kInitialSize && _keys[entry] != 0 && !isolate.MemorySaverModeEnabled())
        {
            uint fullSize = BitOperations.RoundUpToPowerOf2(isolate.Flags.smi_string_cache_size);
            cache = New(fullSize);
            entry = cache.GetEntryFor(number);
        }
        cache._keys[entry] = number;
        cache._values[entry] = value;
        return cache;
    }

    /// <summary>SmiStringCache::Clear.</summary>
    public void Clear()
    {
        Array.Clear(_keys);
        Array.Clear(_values);
    }
}

/// <summary>V8's DoubleStringCache: maps the bits of doubles to their strings.</summary>
public sealed class DoubleStringCache
{
    public const int kInitialSize = 128;
    public const int kMaxCapacity = 0x1000;

    readonly ulong[] _keys;
    readonly JSString?[] _values;

    DoubleStringCache(int capacity)
    {
        _keys = new ulong[capacity];
        _values = new JSString?[capacity];
    }

    public static DoubleStringCache New(int capacity)
    {
        Debug.Assert(capacity >= kInitialSize && capacity <= kMaxCapacity);
        return new DoubleStringCache(capacity);
    }

    public int Capacity => _keys.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetEntryFor(ulong numberBits)
    {
        uint hash = (uint)numberBits ^ (uint)(numberBits >> 32);
        return hash & (uint)(Capacity - 1);
    }

    /// <summary>DoubleStringCache::Get: the cached string, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSString? Get(uint entry, ulong numberBits) => _keys[entry] == numberBits ? _values[entry] : null;

    /// <summary>DoubleStringCache::Set; returns the cache now in use.</summary>
    public DoubleStringCache Set(Isolate isolate, uint entry, ulong numberBits, JSString value)
    {
        DoubleStringCache cache = this;
        if (Capacity == kInitialSize && _values[entry] is not null && !isolate.MemorySaverModeEnabled())
        {
            int fullSize = (int)BitOperations.RoundUpToPowerOf2(isolate.Flags.double_string_cache_size);
            cache = New(fullSize);
            entry = cache.GetEntryFor(numberBits);
        }
        cache._keys[entry] = numberBits;
        cache._values[entry] = value;
        return cache;
    }

    /// <summary>DoubleStringCache::Clear.</summary>
    public void Clear()
    {
        Array.Clear(_keys);
        Array.Clear(_values);
    }
}
