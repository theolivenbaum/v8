// Port of src/base/bit-field.h.
//
// C++ BitField<T, shift, size, U> is a type; here a field is a readonly
// struct value (usually a static readonly field or a const-folded local),
// and the value type is the base integer. Callers cast to/from enums.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base;

/// <summary>A bit field of a uint (V8's BitField with U = uint32_t).</summary>
public readonly struct BitField
{
    public readonly int kShift;
    public readonly int kSize;
    public readonly uint kMask;
    public readonly uint kMax;

    public BitField(int shift, int size)
    {
        Debug.Assert(shift < 32 && size < 32 && shift + size <= 32 && size > 0);
        kShift = shift;
        kSize = size;
        kMask = (uint)(((1UL << shift) << size) - (1UL << shift));
        kMax = (uint)((1UL << size) - 1);
    }

    public int kLastUsedBit => kShift + kSize - 1;
    public uint kNumValues => kMax + 1;

    /// <summary>The field after this one (BitField::Next).</summary>
    public BitField Next(int size) => new(kShift + kSize, size);

    /// <summary>Tells whether the provided value fits into the bit field.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool is_valid(uint value) => (value & ~kMax) == 0;

    /// <summary>Returns a uint with the bit field value encoded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint encode(uint value)
    {
        Debug.Assert(is_valid(value));
        return value << kShift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint encode(bool value) => encode(value ? 1u : 0u);

    /// <summary>Returns a uint with the bit field value updated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint update(uint previous, uint value) => (previous & ~kMask) | encode(value);

    /// <summary>Extracts the bit field from the value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint decode(uint value) => (value & kMask) >> kShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool decode_bool(uint value) => (value & kMask) != 0;
}

/// <summary>A bit field of a ulong (V8's BitField64).</summary>
public readonly struct BitField64
{
    public readonly int kShift;
    public readonly int kSize;
    public readonly ulong kMask;
    public readonly ulong kMax;

    public BitField64(int shift, int size)
    {
        Debug.Assert(shift < 64 && size < 64 && shift + size <= 64 && size > 0);
        kShift = shift;
        kSize = size;
        kMask = unchecked(((1UL << shift) << size) - (1UL << shift));
        kMax = (1UL << size) - 1;
    }

    public BitField64 Next(int size) => new(kShift + kSize, size);

    public bool is_valid(ulong value) => (value & ~kMax) == 0;

    public ulong encode(ulong value)
    {
        Debug.Assert(is_valid(value));
        return value << kShift;
    }

    public ulong update(ulong previous, ulong value) => (previous & ~kMask) | encode(value);

    public ulong decode(ulong value) => (value & kMask) >> kShift;
}
