// Port of src/objects/fixed-array{.h,-inl.h,.cc} and fixed-array-base.h: the
// backing stores (FixedArray of values, FixedDoubleArray of unboxed doubles).
using System.Runtime.CompilerServices;
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>Common base of elements backing stores and hash tables (V8's FixedArrayBase).</summary>
public abstract class FixedArrayBase(InstanceType instanceType) : HeapObject(instanceType)
{
    /// <summary>kMaxFixedArrayCapacity (128M elements, V8_LOWER_LIMITS_MODE off).</summary>
    public const int kMaxLength = 128 * 1024 * 1024;

    public abstract int Length { get; }

    /// <summary>
    /// True for copy-on-write arrays (V8's fixed_cow_array_map): shared
    /// boilerplate elements that must be copied before the first write.
    /// Kept in the header word's hash field, which backing stores do not
    /// otherwise use (a bool field of its own would add 8 bytes to every
    /// FixedArray: the CLR does not place a derived class's fields in the
    /// base class's padding).
    /// </summary>
    public bool IsCowArray
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _hashField != 0;
        internal set => _hashField = value ? 1u : 0u;
    }
}

/// <summary>A fixed-length array of values (V8's FixedArray).</summary>
public sealed class FixedArray : FixedArrayBase
{
    /// <summary>The canonical empty array (V8's empty_fixed_array root). Never written.</summary>
    public static readonly FixedArray Empty = new(0);

    public const int kMaxRegularLength = kMaxLength;

    internal JSValue[] _data;

    public FixedArray(int length) : base(InstanceType.FixedArrayType)
    {
        _data = length == 0 ? [] : new JSValue[length];
    }

    public FixedArray(JSValue[] data) : base(InstanceType.FixedArrayType) => _data = data;

    public override int Length => _data.Length;

    /// <summary>The raw storage. Writers must respect <see cref="FixedArrayBase.IsCowArray"/>.</summary>
    public JSValue[] Data => _data;

    public JSValue this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data[index];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _data[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue Get(int index) => _data[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, JSValue value) => _data[index] = value;

    public bool IsTheHole(int index) => _data[index].IsTheHole;
    public void SetTheHole(int index) => _data[index] = JSValue.TheHole;

    public void FillWithHoles(int from, int to) => _data.AsSpan(from, to - from).Fill(JSValue.TheHole);

    /// <summary>A new array of <paramref name="length"/> holes.</summary>
    public static FixedArray NewWithHoles(int length)
    {
        if (length == 0) return Empty;
        var a = new FixedArray(length);
        a._data.AsSpan().Fill(JSValue.TheHole);
        return a;
    }

    /// <summary>Copy of the first min(length, new_length) entries, padded with holes (CopyFixedArrayAndGrow/CopyFixedArrayUpTo).</summary>
    public FixedArray CopyAndResize(int newLength, bool fillWithHoles = true)
    {
        if (newLength == 0) return Empty;
        var result = new FixedArray(newLength);
        int n = Math.Min(newLength, _data.Length);
        _data.AsSpan(0, n).CopyTo(result._data);
        if (fillWithHoles && newLength > n) result._data.AsSpan(n).Fill(JSValue.TheHole);
        return result;
    }

    /// <summary>FixedArray::SetAndGrow.</summary>
    public static FixedArray SetAndGrow(FixedArray array, int index, JSValue value)
    {
        int len = array.Length;
        if (index < len)
        {
            array.Set(index, value);
            return array;
        }
        int capacity = len;
        do
        {
            capacity = JSObject.NewElementsCapacity(capacity);
        } while (capacity <= index);
        var newArray = new FixedArray(capacity);
        array._data.CopyTo(newArray._data, 0);
        newArray.Set(index, value);
        return newArray;
    }

    public void CopyElements(int dstIndex, FixedArray src, int srcIndex, int len) =>
        Array.Copy(src._data, srcIndex, _data, dstIndex, len);

    public void MoveElements(int dstIndex, int srcIndex, int len) =>
        Array.Copy(_data, srcIndex, _data, dstIndex, len);

    /// <summary>
    /// V8's RightTrim: shrink to <paramref name="newCapacity"/>. The CLR cannot
    /// trim in place, so this reallocates the storage array.
    /// </summary>
    public void RightTrim(int newCapacity)
    {
        if (newCapacity < _data.Length) Array.Resize(ref _data, newCapacity);
    }
}

/// <summary>
/// A fixed-length array of unboxed doubles (V8's FixedDoubleArray). Holes are
/// V8's hole NaN bit pattern; every other NaN is canonicalized on store.
/// </summary>
public sealed class FixedDoubleArray : FixedArrayBase
{
    public static readonly double HoleNaN = BitConverter.Int64BitsToDouble(EngineGlobals.kHoleNanInt64);
    static readonly double CanonicalNaN = double.NaN;

    internal double[] _data;

    public FixedDoubleArray(int length) : base(InstanceType.FixedDoubleArrayType)
    {
        _data = new double[length];
    }

    public override int Length => _data.Length;

    public double[] Data => _data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHoleBits(double d) => BitConverter.DoubleToInt64Bits(d) == EngineGlobals.kHoleNanInt64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTheHole(int index) => BitConverter.DoubleToInt64Bits(_data[index]) == EngineGlobals.kHoleNanInt64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetScalar(int index)
    {
        Debug.Assert(!IsTheHole(index));
        return _data[index];
    }

    /// <summary>The element as a value, or the hole (FixedDoubleArray::get).</summary>
    public JSValue Get(int index) => IsTheHole(index) ? JSValue.TheHole : JSValue.FromNumber(_data[index]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index, double value)
    {
        if (double.IsNaN(value)) value = CanonicalNaN;
        _data[index] = value;
    }

    public void SetTheHole(int index) => _data[index] = HoleNaN;

    public void FillWithHoles(int from, int to) => _data.AsSpan(from, to - from).Fill(HoleNaN);

    public static FixedDoubleArray NewWithHoles(int length)
    {
        var a = new FixedDoubleArray(length);
        a._data.AsSpan().Fill(HoleNaN);
        return a;
    }

    public void MoveElements(int dstIndex, int srcIndex, int len) => Array.Copy(_data, srcIndex, _data, dstIndex, len);

    public void RightTrim(int newCapacity)
    {
        if (newCapacity < _data.Length) Array.Resize(ref _data, newCapacity);
    }
}

/// <summary>An array of raw bytes (V8's ByteArray).</summary>
public sealed class ByteArray(int length) : FixedArrayBase(InstanceType.ByteArrayType)
{
    public static readonly ByteArray Empty = new(0);
    public readonly byte[] Data = length == 0 ? [] : new byte[length];
    public override int Length => Data.Length;
}

/// <summary>A growable list of values (V8's ArrayList).</summary>
public sealed class ArrayList() : HeapObject(InstanceType.ArrayListType)
{
    readonly List<JSValue> _items = [];
    public int Length => _items.Count;
    public JSValue Get(int index) => _items[index];
    public void Set(int index, JSValue value) => _items[index] = value;
    public void Add(JSValue value) => _items.Add(value);
    public void Clear() => _items.Clear();
}
