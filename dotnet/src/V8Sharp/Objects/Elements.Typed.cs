// Port of the typed-array bulk operations of src/objects/elements.cc
// (TypedElementsAccessor<Kind>: FromScalar/ToHandle, FillImpl,
// CopyElementsHandleImpl, CopyElementsFromTypedArray,
// TryCopyElementsFastNumber, CopyElementsHandleSlow,
// CopyTypedArrayElementsSliceImpl and CopyBetweenBackingStoresImpl), of
// LoadFixedTypedArrayElementAsTagged / StoreJSTypedArrayElementFromTagged
// (builtins-typed-array-gen.cc, code-stub-assembler.cc) and of the float16
// conversions (DoubleToFloat16 in src/numbers/conversions-inl.h, fp16's
// fp16_ieee_from_fp32_value / fp16_ieee_to_fp32_value).
//
// V8 specializes these per ElementsKind with templates; V8Sharp does the same
// with generic methods over struct "traits" types (static abstract interface
// members), which RyuJIT specializes per struct instantiation.
//
// Shared buffers: V8 uses relaxed atomics for element accesses of
// SharedArrayBuffers to avoid C++ undefined behaviour; plain managed accesses
// already have the JavaScript memory model's unordered semantics (aligned
// accesses up to 8 bytes do not tear on the supported 64-bit platforms), so
// only the Atomics builtins use Interlocked/Volatile.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Objects;

/// <summary>Scalar conversions of the typed array element types.</summary>
public static class TypedArrayScalars
{
    const ulong kFP64SignMask = 1UL << 63;
    const ulong kFP64Infinity = 0x7FF0_0000_0000_0000UL;
    const int kFP64MantissaBits = 52;
    const int kFP16MantissaBits = 10;
    const ushort kFP16qNaN = 0x7E00;
    const ushort kFP16Infinity = 0x7C00;
    // (ExponentBias(fp64) + 16) << mantissa bits: 2^16 as a double.
    const ulong kFP16InfinityAndNaNInfimum = (1023UL + 16) << 52;
    const ulong kFP16MinExponent = 1023UL - 14;
    const ulong kFP16DenormalThreshold = kFP16MinExponent << 52;
    // A magic value that aligns 10 mantissa bits at the bottom of the double when
    // added to a double using floating point addition. Depends on floating point
    // addition being round-to-nearest-even.
    const ulong kFP64To16DenormalMagic = (kFP16MinExponent + (kFP64MantissaBits - kFP16MantissaBits)) << kFP64MantissaBits;
    // A value that, when added, has the effect that if any of the lower 41 bits of
    // the mantissa are set, the 11th mantissa bit from the front becomes set.
    const ulong kFP64To16RoundingAddend = (1UL << ((kFP64MantissaBits - kFP16MantissaBits) - 1)) - 1;
    // Rebiases the exponent of a double to the range of the half precision and
    // rounds as described above. 15 - kFP64ExponentBias overflows into the sign
    // bit, which the 16-bit output cuts off.
    const ulong kFP64To16RebiasExponentAndRound = unchecked(((15UL - 1023UL) << kFP64MantissaBits) + kFP64To16RoundingAddend);

    /// <summary>DoubleToFloat16 (conversions-inl.h): round to nearest even; NaN becomes the quiet NaN, sign kept.</summary>
    public static ushort DoubleToFloat16(double value)
    {
        ulong input = BitConverter.DoubleToUInt64Bits(value);
        ushort output;

        // Take the absolute value of the input.
        ulong sign = input & kFP64SignMask;
        input ^= sign;

        if (input >= kFP16InfinityAndNaNInfimum)
        {
            // Result is infinity or NaN.
            output = input > kFP64Infinity ? kFP16qNaN  // NaN->qNaN
                : kFP16Infinity;  // Inf->Inf
        }
        else
        {
            // Result is a (de)normalized number or zero.
            if (input < kFP16DenormalThreshold)
            {
                // Result is a denormal or zero. Use the magic value and FP addition to
                // align 10 mantissa bits at the bottom of the float. Depends on FP
                // addition being round-to-nearest-even.
                double temp = BitConverter.UInt64BitsToDouble(input) + BitConverter.UInt64BitsToDouble(kFP64To16DenormalMagic);
                output = (ushort)(BitConverter.DoubleToUInt64Bits(temp) - kFP64To16DenormalMagic);
            }
            else
            {
                // Result is not a denormal.

                // Remember if the result mantissa will be odd before rounding.
                ulong mantOdd = (input >> (kFP64MantissaBits - kFP16MantissaBits)) & 1;

                // Update the exponent and round to nearest even.
                input += kFP64To16RebiasExponentAndRound;
                input += mantOdd;

                output = (ushort)(input >> (kFP64MantissaBits - kFP16MantissaBits));
            }
        }

        output |= (ushort)(sign >> 48);
        return output;
    }

    /// <summary>fp16_ieee_to_fp32_value, widened to double (both conversions are exact).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Float16ToDouble(ushort bits) => (double)BitConverter.UInt16BitsToHalf(bits);

    /// <summary>fp16_ieee_from_fp32_value of an integer (exact in float for the values that do not overflow fp16).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort IntToFloat16(double value) => DoubleToFloat16((float)value);

    /// <summary>DoubleToFloat32 (conversions-inl.h): round to nearest, overflow to infinity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DoubleToFloat32(double x) => (float)x;

    /// <summary>TypedElementsAccessor&lt;UINT8_CLAMPED_ELEMENTS&gt;::FromScalar(double): lrint, clamped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClampDouble(double value)
    {
        // Handle NaNs and less than zero values which clamp to zero.
        if (!(value > 0)) return 0;
        if (value > 0xFF) return 0xFF;
        return (byte)Math.Round(value, MidpointRounding.ToEven);  // lrint: round half to even.
    }
}

/// <summary>
/// The element-type traits of one typed array kind (TypedElementsAccessor&lt;Kind&gt;
/// and TypedArrayCType&lt;Kind&gt;). The From* members are V8's FromScalar
/// overloads; ConvertTo writes a source element to a destination kind the way
/// CopyBetweenBackingStoresImpl does (GetImpl of the source, FromScalar of the
/// destination, chosen by the C++ type of the source element).
/// </summary>
internal interface ITypedElement
{
    static abstract int Size { get; }
    static abstract bool IsBigInt { get; }
    static abstract void StoreInt(Span<byte> d, int value);
    static abstract void StoreUint(Span<byte> d, uint value);
    static abstract void StoreDouble(Span<byte> d, double value);
    static abstract void StoreInt64(Span<byte> d, long value);
    static abstract void StoreUint64(Span<byte> d, ulong value);
    static abstract void StoreFloat16Bits(Span<byte> d, ushort bits);
    static abstract void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement;
    static abstract JSValue Load(Isolate isolate, ReadOnlySpan<byte> s);
}

internal struct Int8Element : ITypedElement
{
    public static int Size => 1;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => d[0] = (byte)value;
    public static void StoreUint(Span<byte> d, uint value) => d[0] = (byte)value;
    public static void StoreDouble(Span<byte> d, double value) => d[0] = (byte)Conversions.DoubleToInt32(value);
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement => TD.StoreInt(d, (sbyte)s[0]);
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt((sbyte)s[0]);
}

internal struct Uint8Element : ITypedElement
{
    public static int Size => 1;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => d[0] = (byte)value;
    public static void StoreUint(Span<byte> d, uint value) => d[0] = (byte)value;
    public static void StoreDouble(Span<byte> d, double value) => d[0] = (byte)Conversions.DoubleToInt32(value);
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement => TD.StoreInt(d, s[0]);
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt(s[0]);
}

internal struct Uint8ClampedElement : ITypedElement
{
    public static int Size => 1;
    public static bool IsBigInt => false;

    public static void StoreInt(Span<byte> d, int value) => d[0] = value < 0 ? (byte)0 : value > 0xFF ? (byte)0xFF : (byte)value;

    // We need this special case for Uint32 -> Uint8Clamped, because the highest
    // Uint32 values will be negative as an int, clamping to 0, rather than 255.
    public static void StoreUint(Span<byte> d, uint value) => d[0] = value > 0xFF ? (byte)0xFF : (byte)value;

    public static void StoreDouble(Span<byte> d, double value) => d[0] = TypedArrayScalars.ClampDouble(value);
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement => TD.StoreInt(d, s[0]);
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt(s[0]);
}

internal struct Int16Element : ITypedElement
{
    public static int Size => 2;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, (short)value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, (short)value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, (short)Conversions.DoubleToInt32(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreInt(d, MemoryMarshal.Read<short>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt(MemoryMarshal.Read<short>(s));
}

internal struct Uint16Element : ITypedElement
{
    public static int Size => 2;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, (ushort)value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, (ushort)value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, (ushort)Conversions.DoubleToInt32(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreInt(d, MemoryMarshal.Read<ushort>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt(MemoryMarshal.Read<ushort>(s));
}

internal struct Int32Element : ITypedElement
{
    public static int Size => 4;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, (int)value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, Conversions.DoubleToInt32(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreInt(d, MemoryMarshal.Read<int>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromInt(MemoryMarshal.Read<int>(s));
}

internal struct Uint32Element : ITypedElement
{
    public static int Size => 4;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, (uint)value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, (uint)Conversions.DoubleToInt32(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreUint(d, MemoryMarshal.Read<uint>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromNumber(MemoryMarshal.Read<uint>(s));
}

internal struct Float16Element : ITypedElement
{
    public static int Size => 2;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, TypedArrayScalars.IntToFloat16(value));
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, TypedArrayScalars.IntToFloat16(value));
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, TypedArrayScalars.DoubleToFloat16(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");

    // There is the reasonable expectations that copying to the same kind of
    // TypedArray does not change the bit pattern of the data.
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => MemoryMarshal.Write(d, bits);

    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreFloat16Bits(d, MemoryMarshal.Read<ushort>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) =>
        JSValue.FromNumber(TypedArrayScalars.Float16ToDouble(MemoryMarshal.Read<ushort>(s)));
}

internal struct Float32Element : ITypedElement
{
    public static int Size => 4;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, (float)value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, (float)value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, TypedArrayScalars.DoubleToFloat32(value));
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreDouble(d, MemoryMarshal.Read<float>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromNumber(MemoryMarshal.Read<float>(s));
}

internal struct Float64Element : ITypedElement
{
    public static int Size => 8;
    public static bool IsBigInt => false;
    public static void StoreInt(Span<byte> d, int value) => MemoryMarshal.Write(d, (double)value);
    public static void StoreUint(Span<byte> d, uint value) => MemoryMarshal.Write(d, (double)value);
    public static void StoreDouble(Span<byte> d, double value) => MemoryMarshal.Write(d, value);
    public static void StoreInt64(Span<byte> d, long value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint64(Span<byte> d, ulong value) => throw new InvalidOperationException("unreachable");
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => StoreDouble(d, TypedArrayScalars.Float16ToDouble(bits));
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreDouble(d, MemoryMarshal.Read<double>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => JSValue.FromNumber(MemoryMarshal.Read<double>(s));
}

internal struct BigInt64Element : ITypedElement
{
    public static int Size => 8;
    public static bool IsBigInt => true;
    public static void StoreInt(Span<byte> d, int value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint(Span<byte> d, uint value) => throw new InvalidOperationException("unreachable");
    public static void StoreDouble(Span<byte> d, double value) => throw new InvalidOperationException("unreachable");
    public static void StoreInt64(Span<byte> d, long value) => MemoryMarshal.Write(d, value);
    public static void StoreUint64(Span<byte> d, ulong value) => MemoryMarshal.Write(d, (long)value);
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => throw new InvalidOperationException("unreachable");
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreInt64(d, MemoryMarshal.Read<long>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => BigInt.FromInt64(isolate, MemoryMarshal.Read<long>(s));
}

internal struct BigUint64Element : ITypedElement
{
    public static int Size => 8;
    public static bool IsBigInt => true;
    public static void StoreInt(Span<byte> d, int value) => throw new InvalidOperationException("unreachable");
    public static void StoreUint(Span<byte> d, uint value) => throw new InvalidOperationException("unreachable");
    public static void StoreDouble(Span<byte> d, double value) => throw new InvalidOperationException("unreachable");
    public static void StoreInt64(Span<byte> d, long value) => MemoryMarshal.Write(d, (ulong)value);
    public static void StoreUint64(Span<byte> d, ulong value) => MemoryMarshal.Write(d, value);
    public static void StoreFloat16Bits(Span<byte> d, ushort bits) => throw new InvalidOperationException("unreachable");
    public static void ConvertTo<TD>(ReadOnlySpan<byte> s, Span<byte> d) where TD : struct, ITypedElement =>
        TD.StoreUint64(d, MemoryMarshal.Read<ulong>(s));
    public static JSValue Load(Isolate isolate, ReadOnlySpan<byte> s) => BigInt.FromUint64(isolate, MemoryMarshal.Read<ulong>(s));
}

/// <summary>
/// Typed array element operations shared by the TypedArray, Array, DataView
/// and Atomics builtins and the runtime (V8's TypedElementsAccessor bulk
/// operations and the typed-array CSA helpers).
/// </summary>
public static class TypedArrayElementsOps
{
    /// <summary>The non-RAB/GSAB kind of <paramref name="kind"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ElementsKind BaseKind(ElementsKind kind) =>
        ElementsKinds.IsRabGsabTypedArrayElementsKind(kind) ? ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind) : kind;

    /// <summary>LoadFixedTypedArrayElementAsTagged over the element's bytes.</summary>
    public static JSValue LoadFromBytes(Isolate isolate, ElementsKind kind, ReadOnlySpan<byte> s) => BaseKind(kind) switch
    {
        ElementsKind.INT8_ELEMENTS => Int8Element.Load(isolate, s),
        ElementsKind.UINT8_ELEMENTS => Uint8Element.Load(isolate, s),
        ElementsKind.UINT8_CLAMPED_ELEMENTS => Uint8ClampedElement.Load(isolate, s),
        ElementsKind.INT16_ELEMENTS => Int16Element.Load(isolate, s),
        ElementsKind.UINT16_ELEMENTS => Uint16Element.Load(isolate, s),
        ElementsKind.INT32_ELEMENTS => Int32Element.Load(isolate, s),
        ElementsKind.UINT32_ELEMENTS => Uint32Element.Load(isolate, s),
        ElementsKind.FLOAT16_ELEMENTS => Float16Element.Load(isolate, s),
        ElementsKind.FLOAT32_ELEMENTS => Float32Element.Load(isolate, s),
        ElementsKind.FLOAT64_ELEMENTS => Float64Element.Load(isolate, s),
        ElementsKind.BIGINT64_ELEMENTS => BigInt64Element.Load(isolate, s),
        ElementsKind.BIGUINT64_ELEMENTS => BigUint64Element.Load(isolate, s),
        _ => throw new InvalidOperationException("unreachable"),
    };

    /// <summary>
    /// LoadFixedTypedArrayElementAsTagged(data_ptr, index, kind): element
    /// <paramref name="index"/> of an attached, in-bounds typed array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue Load(Isolate isolate, JSTypedArray array, ulong index)
    {
        int size = array.ElementSize;
        return LoadFromBytes(isolate, array.Kind,
            array.Buffer.BackingStoreBuffer.AsSpan((int)(array.ByteOffset + index * (ulong)size), size));
    }

    /// <summary>
    /// The length of a typed array whose length is fixed (JSTypedArray::length:
    /// not length-tracking, not backed by a resizable buffer) and that is
    /// attached; false when the variable-length and detach checks of
    /// GetLengthOrOutOfBounds are needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetFixedLength(JSTypedArray array, out ulong length)
    {
        length = array.RawLength;
        return !array.IsLengthTracking && !array.IsBackedByRab && !array.Buffer.WasDetached;
    }

    /// <summary>
    /// LoadFixedTypedArrayElementAsTagged for an in-bounds index of an attached
    /// array, specialized per kind (the element size is the case's constant).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue LoadElement(Isolate isolate, JSTypedArray array, ElementsKind kind, int index) =>
        LoadElement(isolate, array.Buffer.BackingStoreBuffer, (int)array.ByteOffset, kind, index);

    /// <summary>
    /// <see cref="LoadElement(Isolate, JSTypedArray, ElementsKind, int)"/> over the
    /// view's bytes given as its buffer's array and byte offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue LoadElement(Isolate isolate, byte[] data, int byteOffset, ElementsKind kind, int index)
    {
        ReadOnlySpan<byte> s = data.AsSpan(byteOffset);
        switch (kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.UINT8_CLAMPED_ELEMENTS:
                return JSValue.FromInt(s[index]);
            case ElementsKind.INT8_ELEMENTS:
                return JSValue.FromInt((sbyte)s[index]);
            case ElementsKind.UINT16_ELEMENTS:
                return JSValue.FromInt(MemoryMarshal.Read<ushort>(s.Slice(index * 2)));
            case ElementsKind.INT16_ELEMENTS:
                return JSValue.FromInt(MemoryMarshal.Read<short>(s.Slice(index * 2)));
            case ElementsKind.INT32_ELEMENTS:
                return JSValue.FromInt(MemoryMarshal.Read<int>(s.Slice(index * 4)));
            case ElementsKind.UINT32_ELEMENTS:
                return Uint32Element.Load(isolate, s.Slice(index * 4));
            case ElementsKind.FLOAT32_ELEMENTS:
                return Float32Element.Load(isolate, s.Slice(index * 4));
            case ElementsKind.FLOAT64_ELEMENTS:
                return Float64Element.Load(isolate, s.Slice(index * 8));
            default:
                int size = ElementsKinds.ElementsKindToByteSize(kind);
                return LoadFromBytes(isolate, kind, s.Slice(index * size, size));
        }
    }

    /// <summary>
    /// StoreJSTypedArrayElementFromNumeric for a Number into an in-bounds index
    /// of an attached array of a Number kind, specialized per kind.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreElement(JSTypedArray array, ElementsKind kind, int index, double value) =>
        StoreElement(array.Buffer.BackingStoreBuffer, (int)array.ByteOffset, kind, index, value);

    /// <summary>
    /// <see cref="StoreElement(JSTypedArray, ElementsKind, int, double)"/> over the
    /// view's bytes given as its buffer's array and byte offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreElement(byte[] data, int byteOffset, ElementsKind kind, int index, double value)
    {
        Span<byte> d = data.AsSpan(byteOffset);
        switch (kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.INT8_ELEMENTS:
                d[index] = (byte)Conversions.DoubleToInt32(value);
                return;
            case ElementsKind.UINT8_CLAMPED_ELEMENTS:
                d[index] = TypedArrayScalars.ClampDouble(value);
                return;
            case ElementsKind.UINT16_ELEMENTS:
            case ElementsKind.INT16_ELEMENTS:
                MemoryMarshal.Write(d.Slice(index * 2), (ushort)Conversions.DoubleToInt32(value));
                return;
            case ElementsKind.UINT32_ELEMENTS:
            case ElementsKind.INT32_ELEMENTS:
                MemoryMarshal.Write(d.Slice(index * 4), Conversions.DoubleToInt32(value));
                return;
            case ElementsKind.FLOAT32_ELEMENTS:
                Float32Element.StoreDouble(d.Slice(index * 4), value);
                return;
            case ElementsKind.FLOAT64_ELEMENTS:
                MemoryMarshal.Write(d.Slice(index * 8), value);
                return;
            default:
                int size = ElementsKinds.ElementsKindToByteSize(kind);
                StoreDoubleToBytes(kind, d.Slice(index * size, size), value);
                return;
        }
    }

    /// <summary>
    /// StoreJSTypedArrayElementFromNumeric: stores a Number (or BigInt for
    /// BigInt arrays) already converted by the caller into an in-bounds index.
    /// </summary>
    public static void StoreNumeric(JSTypedArray array, ulong index, JSValue value)
    {
        Debug.Assert(!array.Buffer.IsImmutable);
        int size = array.ElementSize;
        Span<byte> d = array.Buffer.BackingStoreBuffer.AsSpan((int)(array.ByteOffset + index * (ulong)size), size);
        StoreNumericToBytes(array.Kind, d, value);
    }

    /// <summary>TypedElementsAccessor::SetImpl(entry_ptr, FromObject(value)) over raw bytes.</summary>
    public static void StoreNumericToBytes(ElementsKind kind, Span<byte> d, JSValue value)
    {
        switch (BaseKind(kind))
        {
            case ElementsKind.BIGINT64_ELEMENTS:
                MemoryMarshal.Write(d, BigInt.AsInt64(value.As<BigInt>(), out _));
                return;
            case ElementsKind.BIGUINT64_ELEMENTS:
                MemoryMarshal.Write(d, BigInt.AsUint64(value.As<BigInt>(), out _));
                return;
        }
        // Clamp undefined here as well. All other types have been
        // converted to a number type further up in the call chain.
        StoreDoubleToBytes(kind, d, value.IsUndefined ? double.NaN : value.Number);
    }

    /// <summary>FromScalar(double) + SetImpl for the Number kinds.</summary>
    public static void StoreDoubleToBytes(ElementsKind kind, Span<byte> d, double value)
    {
        switch (BaseKind(kind))
        {
            case ElementsKind.INT8_ELEMENTS: Int8Element.StoreDouble(d, value); break;
            case ElementsKind.UINT8_ELEMENTS: Uint8Element.StoreDouble(d, value); break;
            case ElementsKind.UINT8_CLAMPED_ELEMENTS: Uint8ClampedElement.StoreDouble(d, value); break;
            case ElementsKind.INT16_ELEMENTS: Int16Element.StoreDouble(d, value); break;
            case ElementsKind.UINT16_ELEMENTS: Uint16Element.StoreDouble(d, value); break;
            case ElementsKind.INT32_ELEMENTS: Int32Element.StoreDouble(d, value); break;
            case ElementsKind.UINT32_ELEMENTS: Uint32Element.StoreDouble(d, value); break;
            case ElementsKind.FLOAT16_ELEMENTS: Float16Element.StoreDouble(d, value); break;
            case ElementsKind.FLOAT32_ELEMENTS: Float32Element.StoreDouble(d, value); break;
            case ElementsKind.FLOAT64_ELEMENTS: Float64Element.StoreDouble(d, value); break;
            default: throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// ToNumber / ToBigInt of <paramref name="value"/> for storing into an
    /// array of <paramref name="kind"/> (PrepareValueForWriteToTypedArray);
    /// may run JavaScript.
    /// </summary>
    public static JSValue PrepareValue(Isolate isolate, ElementsKind kind, JSValue value) =>
        ElementsKinds.IsBigIntTypedArrayElementsKind(kind)
            ? BigInt.FromObject(isolate, value)
            : ObjectOps.ToNumber(isolate, value);

    /// <summary>
    /// StoreJSTypedArrayElementFromTagged: converts <paramref name="value"/>,
    /// then (because the conversion may run JavaScript) re-validates the array
    /// and stores it. Returns false when the array became detached, immutable
    /// or out of bounds, or the index is no longer in bounds.
    /// </summary>
    public static bool StoreJSAny(Isolate isolate, JSTypedArray array, ulong index, JSValue value)
    {
        JSValue prepared = PrepareValue(isolate, array.Kind, value);
        // ToNumber/ToBigInt (or other functions called by the upper level) may
        // execute JavaScript code, which could detach the TypedArray's buffer or make
        // the TypedArray out of bounds.
        if (!TryGetLengthAndValidate(array, TypedArrayAccessMode.kWrite, out ulong length)) return false;
        if (index >= length) return false;
        StoreNumeric(array, index, prepared);
        return true;
    }

    /// <summary>
    /// LoadJSTypedArrayLengthAndValidate: false (the Fail label) for detached
    /// or out-of-bounds arrays, and for immutable buffers in write mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLengthAndValidate(JSTypedArray array, TypedArrayAccessMode mode, out ulong length)
    {
        JSArrayBuffer buffer = array.Buffer;
        if (buffer.WasDetached || (mode == TypedArrayAccessMode.kWrite && buffer.IsImmutable))
        {
            length = 0;
            return false;
        }
        length = array.GetLengthOrOutOfBounds(out bool outOfBounds);
        return !outOfBounds;
    }

    // ---- Fill ---------------------------------------------------------------------------

    /// <summary>
    /// TypedElementsAccessor::FillImpl: stores the converted
    /// <paramref name="value"/> into [start, end). Vectorized through Span.Fill.
    /// </summary>
    public static void Fill(JSTypedArray array, JSValue value, ulong start, ulong end)
    {
        Debug.Assert(!array.IsDetachedOrOutOfBounds);
        Debug.Assert(start <= end && end <= array.GetLength());
        if (start == end) return;
        Span<byte> data = array.DataSpan(start, end - start);
        ElementsKind kind = BaseKind(array.Kind);
        Span<byte> one = stackalloc byte[8];
        StoreNumericToBytes(kind, one, value);
        switch (array.ElementSize)
        {
            case 1:
                data.Fill(one[0]);
                break;
            case 2:
                MemoryMarshal.Cast<byte, ushort>(data).Fill(MemoryMarshal.Read<ushort>(one));
                break;
            case 4:
                MemoryMarshal.Cast<byte, uint>(data).Fill(MemoryMarshal.Read<uint>(one));
                break;
            default:
                MemoryMarshal.Cast<byte, ulong>(data).Fill(MemoryMarshal.Read<ulong>(one));
                break;
        }
    }

    // ---- Copies between backing stores ------------------------------------------------------

    /// <summary>CopyBetweenBackingStoresImpl&lt;Kind, SourceKind&gt;::Copy.</summary>
    static void CopyBetween<TD, TS>(ReadOnlySpan<byte> source, Span<byte> dest, int length)
        where TD : struct, ITypedElement
        where TS : struct, ITypedElement
    {
        int ss = TS.Size, ds = TD.Size;
        for (int i = 0, si = 0, di = 0; i < length; i++, si += ss, di += ds)
        {
            TS.ConvertTo<TD>(source.Slice(si, ss), dest.Slice(di, ds));
        }
    }

    static void CopyFromSource<TD>(ElementsKind sourceKind, ReadOnlySpan<byte> source, Span<byte> dest, int length)
        where TD : struct, ITypedElement
    {
        switch (BaseKind(sourceKind))
        {
            case ElementsKind.INT8_ELEMENTS: CopyBetween<TD, Int8Element>(source, dest, length); break;
            case ElementsKind.UINT8_ELEMENTS: CopyBetween<TD, Uint8Element>(source, dest, length); break;
            case ElementsKind.UINT8_CLAMPED_ELEMENTS: CopyBetween<TD, Uint8ClampedElement>(source, dest, length); break;
            case ElementsKind.INT16_ELEMENTS: CopyBetween<TD, Int16Element>(source, dest, length); break;
            case ElementsKind.UINT16_ELEMENTS: CopyBetween<TD, Uint16Element>(source, dest, length); break;
            case ElementsKind.INT32_ELEMENTS: CopyBetween<TD, Int32Element>(source, dest, length); break;
            case ElementsKind.UINT32_ELEMENTS: CopyBetween<TD, Uint32Element>(source, dest, length); break;
            case ElementsKind.FLOAT16_ELEMENTS: CopyBetween<TD, Float16Element>(source, dest, length); break;
            case ElementsKind.FLOAT32_ELEMENTS: CopyBetween<TD, Float32Element>(source, dest, length); break;
            case ElementsKind.FLOAT64_ELEMENTS: CopyBetween<TD, Float64Element>(source, dest, length); break;
            case ElementsKind.BIGINT64_ELEMENTS: CopyBetween<TD, BigInt64Element>(source, dest, length); break;
            case ElementsKind.BIGUINT64_ELEMENTS: CopyBetween<TD, BigUint64Element>(source, dest, length); break;
            default: throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// Converts <paramref name="length"/> elements of <paramref name="sourceKind"/>
    /// to <paramref name="destKind"/> (CopyBetweenBackingStores dispatched on both kinds).
    /// </summary>
    public static void CopyBetweenBackingStores(ElementsKind destKind, ElementsKind sourceKind, ReadOnlySpan<byte> source,
        Span<byte> dest, int length)
    {
        switch (BaseKind(destKind))
        {
            case ElementsKind.INT8_ELEMENTS: CopyFromSource<Int8Element>(sourceKind, source, dest, length); break;
            case ElementsKind.UINT8_ELEMENTS: CopyFromSource<Uint8Element>(sourceKind, source, dest, length); break;
            case ElementsKind.UINT8_CLAMPED_ELEMENTS: CopyFromSource<Uint8ClampedElement>(sourceKind, source, dest, length); break;
            case ElementsKind.INT16_ELEMENTS: CopyFromSource<Int16Element>(sourceKind, source, dest, length); break;
            case ElementsKind.UINT16_ELEMENTS: CopyFromSource<Uint16Element>(sourceKind, source, dest, length); break;
            case ElementsKind.INT32_ELEMENTS: CopyFromSource<Int32Element>(sourceKind, source, dest, length); break;
            case ElementsKind.UINT32_ELEMENTS: CopyFromSource<Uint32Element>(sourceKind, source, dest, length); break;
            case ElementsKind.FLOAT16_ELEMENTS: CopyFromSource<Float16Element>(sourceKind, source, dest, length); break;
            case ElementsKind.FLOAT32_ELEMENTS: CopyFromSource<Float32Element>(sourceKind, source, dest, length); break;
            case ElementsKind.FLOAT64_ELEMENTS: CopyFromSource<Float64Element>(sourceKind, source, dest, length); break;
            case ElementsKind.BIGINT64_ELEMENTS: CopyFromSource<BigInt64Element>(sourceKind, source, dest, length); break;
            case ElementsKind.BIGUINT64_ELEMENTS: CopyFromSource<BigUint64Element>(sourceKind, source, dest, length); break;
            default: throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>TypedElementsAccessor::HasSimpleRepresentation.</summary>
    static bool HasSimpleRepresentation(ElementsKind kind) =>
        kind is not (ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS
            or ElementsKind.FLOAT16_ELEMENTS);

    /// <summary>
    /// TypedElementsAccessor::CopyElementsFromTypedArray: copies
    /// <paramref name="length"/> elements of <paramref name="source"/> to
    /// <paramref name="destination"/> starting at <paramref name="offset"/>,
    /// converting when the kinds differ. Handles overlapping buffers.
    /// </summary>
    public static void CopyElementsFromTypedArray(JSTypedArray source, JSTypedArray destination, ulong length, ulong offset)
    {
        // The source is a typed array, so we know we don't need to do ToNumber
        // side-effects, as the source elements will always be a number.
        if (source.IsDetachedOrOutOfBounds || destination.IsDetachedOrOutOfBounds)
        {
            throw new InvalidOperationException("CopyElementsFromTypedArray: detached or out of bounds");
        }
        Debug.Assert(offset <= destination.GetLength());
        Debug.Assert(length <= destination.GetLength() - offset);
        Debug.Assert(length <= source.GetLength());
        if (length == 0) return;

        ElementsKind sourceKind = BaseKind(source.Kind);
        ElementsKind destinationKind = BaseKind(destination.Kind);
        int sourceSize = source.ElementSize;
        int destinationSize = destination.ElementSize;

        byte[] sourceBuffer = source.Buffer.BackingStoreBuffer;
        byte[] destBuffer = destination.Buffer.BackingStoreBuffer;
        int sourceStart = (int)source.ByteOffset;
        int destStart = (int)(destination.ByteOffset + offset * (ulong)destinationSize);

        bool sameType = sourceKind == destinationKind;
        bool sameSize = sourceSize == destinationSize;
        bool bothAreSimple = HasSimpleRepresentation(sourceKind) && HasSimpleRepresentation(destinationKind);

        int sourceByteLength = (int)(length * (ulong)sourceSize);

        // We can simply copy the backing store if the types are the same, or if
        // we are converting e.g. Uint8 <-> Int8, as the binary representation
        // will be the same. This is not the case for floats or clamped Uint8,
        // which have special conversion operations.
        if (sameType || (sameSize && bothAreSimple))
        {
            // std::memmove / base::Relaxed_Memcpy: Span.CopyTo handles overlap.
            sourceBuffer.AsSpan(sourceStart, sourceByteLength).CopyTo(destBuffer.AsSpan(destStart, sourceByteLength));
            return;
        }

        int destByteLength = (int)(length * (ulong)destinationSize);
        ReadOnlySpan<byte> sourceData = sourceBuffer.AsSpan(sourceStart, sourceByteLength);
        // If the typedarrays are overlapped, clone the source.
        if (ReferenceEquals(sourceBuffer, destBuffer) && destStart + destByteLength > sourceStart &&
            sourceStart + sourceByteLength > destStart)
        {
            sourceData = sourceData.ToArray();
        }
        CopyBetweenBackingStores(destinationKind, sourceKind, sourceData, destBuffer.AsSpan(destStart, destByteLength), (int)length);
    }

    /// <summary>
    /// TypedElementsAccessor::CopyTypedArrayElementsSliceImpl (the
    /// copy_typed_array_elements_slice C function): elements [start, end) of
    /// <paramref name="source"/> to the start of <paramref name="destination"/>.
    /// </summary>
    public static void CopyTypedArrayElementsSlice(JSTypedArray source, JSTypedArray destination, ulong start, ulong end)
    {
        if (source.IsDetachedOrOutOfBounds || destination.IsDetachedOrOutOfBounds)
        {
            throw new InvalidOperationException("CopyTypedArrayElementsSlice: detached or out of bounds");
        }
        Debug.Assert(start <= end && end <= source.GetLength());
        ulong count = end - start;
        Debug.Assert(count <= destination.GetLength());
        if (count == 0) return;
        ReadOnlySpan<byte> sourceData = source.DataSpan(start, count);
        Span<byte> destData = destination.DataSpan(0, count);
        // CopyBetweenBackingStoresImpl copies element by element, front to
        // back, also when a species constructor made the views overlap.
        CopyBetweenBackingStores(destination.Kind, source.Kind, sourceData, destData, (int)count);
    }

    /// <summary>TypedElementsAccessor::HoleyPrototypeLookupRequired.</summary>
    static bool HoleyPrototypeLookupRequired(Isolate isolate, JSArray source)
    {
        JSReceiver? sourceProto = source.Map.Prototype;
        // Null prototypes are OK - we don't need to do prototype chain lookups on
        // them.
        if (sourceProto is null) return false;
        if (sourceProto is JSProxy) return true;
        if (sourceProto is JSObject && !ReferenceEquals(sourceProto, isolate.NativeContext.InitialArrayPrototype)) return true;
        return !Protectors.IsNoElementsIntact(isolate);
    }

    /// <summary>TypedElementsAccessor::TryCopyElementsFastNumber.</summary>
    static bool TryCopyElementsFastNumber(Isolate isolate, JSArray source, JSTypedArray destination, ulong length, ulong offset)
    {
        ElementsKind destKind = BaseKind(destination.Kind);
        if (ElementsKinds.IsBigIntTypedArrayElementsKind(destKind)) return false;

        Debug.Assert(!destination.WasDetached);
        Debug.Assert(destination.GetLength() >= length + offset);

        ElementsKind kind = source.GetElementsKind();

        // When we find the hole, we normally have to look up the element on the
        // prototype chain, which is not handled here and we return false instead.
        // When the array has the original array prototype, and that prototype has
        // not been changed in a way that would affect lookups, we can just convert
        // the hole into undefined.
        if (HoleyPrototypeLookupRequired(isolate, source)) return false;

        int size = destination.ElementSize;
        Span<byte> dest = destination.DataSpan(offset, length);
        int n = (int)length;

        switch (kind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            {
                var sourceStore = (FixedArray)source.Elements;
                for (int i = 0; i < n; i++)
                {
                    JSValue elem = sourceStore[i];
                    Span<byte> d = dest.Slice(i * size, size);
                    if (elem.IsTheHole)
                    {
                        StoreDoubleToBytes(destKind, d, double.NaN);  // FromObject(undefined)
                    }
                    else
                    {
                        StoreIntToBytes(destKind, d, (int)elem.Number);
                    }
                }
                return true;
            }
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
            {
                if (source.Elements is not FixedDoubleArray sourceStore)
                {
                    // An empty double array has an empty FixedArray backing store.
                    return n == 0;
                }
                double[] data = sourceStore.Data;
                if (destKind == ElementsKind.FLOAT64_ELEMENTS && kind == ElementsKind.PACKED_DOUBLE_ELEMENTS)
                {
                    MemoryMarshal.AsBytes(data.AsSpan(0, n)).CopyTo(dest);
                    return true;
                }
                for (int i = 0; i < n; i++)
                {
                    // Use the from_double conversion for this specific TypedArray type,
                    // rather than relying on C++ to convert elem.
                    double elem = data[i];
                    if (FixedDoubleArray.IsHoleBits(elem)) elem = double.NaN;  // FromObject(undefined)
                    StoreDoubleToBytes(destKind, dest.Slice(i * size, size), elem);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>FromScalar(int) + SetImpl for the Number kinds (Smi sources).</summary>
    static void StoreIntToBytes(ElementsKind kind, Span<byte> d, int value)
    {
        switch (kind)
        {
            case ElementsKind.INT8_ELEMENTS: Int8Element.StoreInt(d, value); break;
            case ElementsKind.UINT8_ELEMENTS: Uint8Element.StoreInt(d, value); break;
            case ElementsKind.UINT8_CLAMPED_ELEMENTS: Uint8ClampedElement.StoreInt(d, value); break;
            case ElementsKind.INT16_ELEMENTS: Int16Element.StoreInt(d, value); break;
            case ElementsKind.UINT16_ELEMENTS: Uint16Element.StoreInt(d, value); break;
            case ElementsKind.INT32_ELEMENTS: Int32Element.StoreInt(d, value); break;
            case ElementsKind.UINT32_ELEMENTS: Uint32Element.StoreInt(d, value); break;
            case ElementsKind.FLOAT16_ELEMENTS: Float16Element.StoreInt(d, value); break;
            case ElementsKind.FLOAT32_ELEMENTS: Float32Element.StoreInt(d, value); break;
            case ElementsKind.FLOAT64_ELEMENTS: Float64Element.StoreInt(d, value); break;
            default: throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// TypedElementsAccessor::CopyElementsHandleSlow (ES #sec-settypedarrayfromarraylike):
    /// Get + ToNumber/ToBigInt per element; stores are skipped once the target
    /// became detached or too short, but the getters still run.
    /// </summary>
    static void CopyElementsHandleSlow(Isolate isolate, JSValue source, JSTypedArray destination, ulong length, ulong offset)
    {
        ElementsKind kind = destination.Kind;
        bool isBigInt = ElementsKinds.IsBigIntTypedArrayElementsKind(kind);
        // 8. Let k be 0.
        // 9. Repeat, while k < srcLength,
        for (ulong i = 0; i < length; i++)
        {
            // a. Let Pk be ! ToString(𝔽(k)).
            // b. Let value be ? Get(src, Pk).
            JSValue elem = ObjectOps.GetPropertyOrElement(isolate, source, new PropertyKey(isolate, (double)i));
            // c. Let targetIndex be 𝔽(targetOffset + k).
            // d. Perform ? IntegerIndexedElementSet(target, targetIndex, value).
            //
            // Rest of loop body inlines ES#IntegerIndexedElementSet
            elem = isBigInt ? BigInt.FromObject(isolate, elem) : ObjectOps.ToNumber(isolate, elem);
            // 3. If IsValidIntegerIndex(O, index) is true, then ...
            ulong newLength = destination.GetLengthOrOutOfBounds(out bool outOfBounds);
            if (outOfBounds || destination.WasDetached || newLength <= offset + i)
            {
                // Proceed with the loop so that we call get getters for the source even
                // though we don't set the values in the target.
                continue;
            }
            StoreNumeric(destination, offset + i, elem);
            // e. Set k to k + 1.
        }
        // 10. Return unused.
    }

    /// <summary>
    /// TypedElementsAccessor::CopyElementsHandleImpl (Runtime_TypedArrayCopyElements
    /// and ElementsAccessor::CopyElements for typed arrays). This doesn't
    /// guarantee that the destination array will be completely filled.
    /// </summary>
    public static void CopyElementsHandle(Isolate isolate, JSValue source, JSTypedArray destination, ulong length, ulong offset)
    {
        if (length == 0) return;

        // All conversions from TypedArrays can be done without allocation.
        if (source.HeapObjectOrNull is JSTypedArray sourceTa)
        {
            Debug.Assert(!destination.WasDetached);
            bool sourceIsBigInt = ElementsKinds.IsBigIntTypedArrayElementsKind(sourceTa.Kind);
            bool targetIsBigInt = ElementsKinds.IsBigIntTypedArrayElementsKind(destination.Kind);
            // If we have to copy more elements than we have in the source, we need to
            // do special handling and conversion; that happens in the slow case.
            if (sourceIsBigInt == targetIsBigInt && !sourceTa.WasDetached && length + offset <= sourceTa.GetLength())
            {
                CopyElementsFromTypedArray(sourceTa, destination, length, offset);
                return;
            }
        }
        else if (source.HeapObjectOrNull is JSArray sourceArray)
        {
            Debug.Assert(!destination.WasDetached);
            // Fast cases for packed numbers kinds where we don't need to allocate.
            double currentLength = sourceArray.Length.Number;
            if (length <= currentLength && TryCopyElementsFastNumber(isolate, sourceArray, destination, length, offset)) return;
        }
        // Final generic case that handles prototype chain lookups, getters, proxies
        // and observable side effects via valueOf, etc. In this case, it's possible
        // that the length getter detached / resized the underlying buffer.
        CopyElementsHandleSlow(isolate, source, destination, length, offset);
    }
}
