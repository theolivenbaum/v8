// Port of the in-object property layout of src/objects/js-objects.h (+ -inl.h):
// JSObject::GetInObjectPropertyOffset, RawFastPropertyAt/FastPropertyAtPut for
// in-object fields, and the instance-size classes Factory::NewJSObjectFromMap
// allocates.
//
// V8 lays out an ordinary object as header (map, properties_or_hash, elements)
// followed by map->GetInObjectProperties() tagged slots; fields beyond those
// live in the PropertyArray. A CLR object cannot be sized per allocation, so
// V8Sharp allocates ordinary objects from a small chain of classes whose last
// fields are [InlineArray] segments: JSObjectInObject4 holds slots 0..3,
// JSObjectInObject8 derives from it and adds 4..7, and so on. An object gets
// the smallest class covering its map's in-object property count; a class of
// the chain is also every larger class, so in-object slot i of any object whose
// map has more than i in-object properties is the same field of the class that
// introduces slot i. In-object slack tracking shrinks the map's instance size
// after kGenerousAllocationCount constructions; objects allocated before keep
// their (larger) class, which is what V8's left-trimmed filler amounts to.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

[InlineArray(4)]
internal struct InObjectSlots4 { JSValue _e0; }

[InlineArray(16)]
internal struct InObjectSlots16 { JSValue _e0; }

[InlineArray(32)]
internal struct InObjectSlots32 { JSValue _e0; }

[InlineArray(64)]
internal struct InObjectSlots64 { JSValue _e0; }

[InlineArray(128)]
internal struct InObjectSlots128 { JSValue _e0; }

public partial class JSObject
{
    /// <summary>
    /// The storage index of a field that lives in the PropertyArray is
    /// <c>kPropertyArrayStorageBase + array index</c>; smaller storage indices
    /// are in-object slots (see <see cref="FieldIndex.StorageIndex"/>).
    /// </summary>
    internal const int kPropertyArrayStorageBase = 256;

    /// <summary>
    /// Whether objects of <paramref name="type"/> keep their in-object fields in
    /// object slots (the classes above). These are the ordinary objects that
    /// AllocateForMap creates as plain JSObjects. Other JSObject subclasses
    /// (arrays, functions, regexps, arguments ...) keep them at the start of the
    /// PropertyArray instead (deviations.md, "Heap and object model").
    /// </summary>
    internal static bool UsesInObjectSlots(InstanceType type) => type switch
    {
        InstanceType.JSObjectType or InstanceType.JSApiObjectType or InstanceType.JSSpecialApiObjectType or
        InstanceType.JSErrorType or InstanceType.JSContextExtensionObjectType or
        InstanceType.JSObjectPrototypeType or InstanceType.JSIteratorPrototypeType or
        InstanceType.JSArrayIteratorPrototypeType or InstanceType.JSPromisePrototypeType or
        InstanceType.JSRegExpPrototypeType or InstanceType.JSStringIteratorPrototypeType or
        InstanceType.JSMapIteratorPrototypeType or InstanceType.JSSetIteratorPrototypeType or
        InstanceType.JSSetPrototypeType or InstanceType.JSTypedArrayPrototypeType => true,
        _ => false,
    };

    /// <summary>The number of in-object slots of this object's class (its physical instance size).</summary>
    internal virtual int InObjectSlotCapacity => 0;

    /// <summary>An ordinary object of the smallest class with room for <paramref name="map"/>'s in-object properties.</summary>
    internal static JSObject NewWithInObjectSlots(Map map)
    {
        int count = map.GetInObjectProperties();
        return count switch
        {
            0 => new JSObject(map),
            <= 4 => new JSObjectInObject4(map),
            <= 8 => new JSObjectInObject8(map),
            <= 12 => new JSObjectInObject12(map),
            <= 16 => new JSObjectInObject16(map),
            <= 32 => new JSObjectInObject32(map),
            <= 64 => new JSObjectInObject64(map),
            <= 128 => new JSObjectInObject128(map),
            _ => new JSObjectInObject256(map),
        };
    }

    /// <summary>
    /// The field at <paramref name="storageIndex"/> (a <see cref="FieldIndex.StorageIndex"/>).
    /// The caller has checked the map, which guarantees the slot exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref JSValue FieldAt(int storageIndex)
    {
        if (storageIndex >= kPropertyArrayStorageBase) return ref _fields[storageIndex - kPropertyArrayStorageBase];
        return ref InObjectSlot(storageIndex);
    }

    /// <summary>
    /// In-object slot <paramref name="index"/> of an object with in-object slots.
    /// The caller guarantees <c>index &lt; InObjectSlotCapacity</c>: the map
    /// check of an IC, or the map's in-object property count, selects it, as
    /// V8's in-object field loads rely on the map's instance size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref JSValue InObjectSlot(int index)
    {
        Debug.Assert((uint)index < (uint)InObjectSlotCapacity, "in-object slot out of range");
        if (InObjectLayout.IsContiguous)
        {
            // The segments of the class chain are laid out back to back (checked
            // once, InObjectLayout), so the slots are one run from _slots0.
            return ref Unsafe.Add(ref InObjectLayout.First(Unsafe.As<JSObjectInObject4>(this)), index);
        }
        return ref InObjectSlotBySegment(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    ref JSValue InObjectSlotBySegment(int index)
    {
        if (index < 4) return ref Unsafe.As<JSObjectInObject4>(this)._slots0[index];
        if (index < 8) return ref Unsafe.As<JSObjectInObject8>(this)._slots1[index - 4];
        if (index < 12) return ref Unsafe.As<JSObjectInObject12>(this)._slots2[index - 8];
        if (index < 16) return ref Unsafe.As<JSObjectInObject16>(this)._slots3[index - 12];
        if (index < 32) return ref Unsafe.As<JSObjectInObject32>(this)._slots4[index - 16];
        if (index < 64) return ref Unsafe.As<JSObjectInObject64>(this)._slots5[index - 32];
        if (index < 128) return ref Unsafe.As<JSObjectInObject128>(this)._slots6[index - 64];
        return ref Unsafe.As<JSObjectInObject256>(this)._slots7[index - 128];
    }

    /// <summary>
    /// In-object field <paramref name="index"/> by the current map's layout:
    /// an object slot for ordinary objects, else the start of the PropertyArray.
    /// </summary>
    internal ref JSValue InObjectPropertyRef(int index)
    {
        Debug.Assert(index < Map.GetInObjectProperties());
        if (Map.HasInObjectSlots) return ref InObjectSlot(index);
        return ref _fields[index];
    }

    /// <summary>
    /// Copies the fast-mode fields of <paramref name="source"/> (in-object
    /// properties, then the PropertyArray, in property-index order) into this
    /// freshly allocated object, laid out for this object's map.
    /// </summary>
    internal void CopyFastFieldsFrom(JSObject source)
    {
        Map sourceMap = source.Map;
        Map targetMap = Map;
        int sourceInObject = sourceMap.GetInObjectProperties();
        int targetInObject = targetMap.GetInObjectProperties();
        JSValue[] sourceArray = source._fields;
        int sourceArrayStart = sourceMap.HasInObjectSlots ? 0 : sourceInObject;
        int total = sourceInObject + sourceArray.Length - sourceArrayStart;
        int targetArrayStart = targetMap.HasInObjectSlots ? 0 : targetInObject;
        int targetArrayLength = Math.Max(targetArrayStart + total - targetInObject, targetArrayStart);
        if (_fields.Length < targetArrayLength)
        {
            var array = new JSValue[targetArrayLength];
            _fields.AsSpan().CopyTo(array);
            _fields = array;
        }
        for (int p = 0; p < total; p++)
        {
            JSValue value = p < sourceInObject ? source.InObjectPropertyRef(p) : sourceArray[sourceArrayStart + p - sourceInObject];
            if (p < targetInObject) InObjectPropertyRef(p) = value;
            else _fields[targetArrayStart + p - targetInObject] = value;
        }
    }

    /// <summary>Clears in-object slots [0, count) (V8 fills freed in-object space with Smi zero or fillers).</summary>
    internal void ClearInObjectSlots(int count, JSValue value)
    {
        int capacity = InObjectSlotCapacity;
        if (count > capacity) count = capacity;
        for (int i = 0; i < count; i++) InObjectSlot(i) = value;
    }
}

/// <summary>
/// Whether the in-object slot segments of the class chain are contiguous, so
/// that slot i is at offset i from slot 0. The CLR lays out a derived class's
/// fields after its base class's, and every segment is a multiple of the
/// 8-byte field alignment, so they are; the check makes the flat access
/// depend on the observed layout rather than on that reasoning.
/// </summary>
internal static class InObjectLayout
{
    public static readonly bool IsContiguous = Check();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref JSValue First(JSObjectInObject4 obj) => ref obj._slots0[0];

    static bool Check()
    {
        var probe = (JSObjectInObject256)RuntimeHelpers.GetUninitializedObject(typeof(JSObjectInObject256));
        ref JSValue first = ref probe._slots0[0];
        return Offset(ref first, ref probe._slots1[0]) == 4 &&
               Offset(ref first, ref probe._slots2[0]) == 8 &&
               Offset(ref first, ref probe._slots3[0]) == 12 &&
               Offset(ref first, ref probe._slots4[0]) == 16 &&
               Offset(ref first, ref probe._slots5[0]) == 32 &&
               Offset(ref first, ref probe._slots6[0]) == 64 &&
               Offset(ref first, ref probe._slots7[0]) == 128;
    }

    static long Offset(ref JSValue first, ref JSValue other) =>
        (long)Unsafe.ByteOffset(ref first, ref other) / Unsafe.SizeOf<JSValue>();
}

/// <summary>An ordinary object with in-object slots 0..3.</summary>
internal class JSObjectInObject4 : JSObject
{
    internal InObjectSlots4 _slots0;
    public JSObjectInObject4(Map map) : base(map, inObjectSlots: true) { }
    protected JSObjectInObject4(JSObjectInObject4 source) : base(source) => _slots0 = source._slots0;
    internal override int InObjectSlotCapacity => 4;
    internal override JSObject CloneShallowCore() => new JSObjectInObject4(this);
}

/// <summary>An ordinary object with in-object slots 0..7.</summary>
internal class JSObjectInObject8 : JSObjectInObject4
{
    internal InObjectSlots4 _slots1;
    public JSObjectInObject8(Map map) : base(map) { }
    protected JSObjectInObject8(JSObjectInObject8 source) : base(source) => _slots1 = source._slots1;
    internal override int InObjectSlotCapacity => 8;
    internal override JSObject CloneShallowCore() => new JSObjectInObject8(this);
}

/// <summary>An ordinary object with in-object slots 0..11.</summary>
internal class JSObjectInObject12 : JSObjectInObject8
{
    internal InObjectSlots4 _slots2;
    public JSObjectInObject12(Map map) : base(map) { }
    protected JSObjectInObject12(JSObjectInObject12 source) : base(source) => _slots2 = source._slots2;
    internal override int InObjectSlotCapacity => 12;
    internal override JSObject CloneShallowCore() => new JSObjectInObject12(this);
}

/// <summary>An ordinary object with in-object slots 0..15.</summary>
internal class JSObjectInObject16 : JSObjectInObject12
{
    internal InObjectSlots4 _slots3;
    public JSObjectInObject16(Map map) : base(map) { }
    protected JSObjectInObject16(JSObjectInObject16 source) : base(source) => _slots3 = source._slots3;
    internal override int InObjectSlotCapacity => 16;
    internal override JSObject CloneShallowCore() => new JSObjectInObject16(this);
}

/// <summary>An ordinary object with in-object slots 0..31.</summary>
internal class JSObjectInObject32 : JSObjectInObject16
{
    internal InObjectSlots16 _slots4;
    public JSObjectInObject32(Map map) : base(map) { }
    protected JSObjectInObject32(JSObjectInObject32 source) : base(source) => _slots4 = source._slots4;
    internal override int InObjectSlotCapacity => 32;
    internal override JSObject CloneShallowCore() => new JSObjectInObject32(this);
}

/// <summary>An ordinary object with in-object slots 0..63.</summary>
internal class JSObjectInObject64 : JSObjectInObject32
{
    internal InObjectSlots32 _slots5;
    public JSObjectInObject64(Map map) : base(map) { }
    protected JSObjectInObject64(JSObjectInObject64 source) : base(source) => _slots5 = source._slots5;
    internal override int InObjectSlotCapacity => 64;
    internal override JSObject CloneShallowCore() => new JSObjectInObject64(this);
}

/// <summary>An ordinary object with in-object slots 0..127.</summary>
internal class JSObjectInObject128 : JSObjectInObject64
{
    internal InObjectSlots64 _slots6;
    public JSObjectInObject128(Map map) : base(map) { }
    protected JSObjectInObject128(JSObjectInObject128 source) : base(source) => _slots6 = source._slots6;
    internal override int InObjectSlotCapacity => 128;
    internal override JSObject CloneShallowCore() => new JSObjectInObject128(this);
}

/// <summary>An ordinary object with in-object slots 0..255 (kMaxInObjectProperties is 252).</summary>
internal sealed class JSObjectInObject256 : JSObjectInObject128
{
    internal InObjectSlots128 _slots7;
    public JSObjectInObject256(Map map) : base(map) { }
    JSObjectInObject256(JSObjectInObject256 source) : base(source) => _slots7 = source._slots7;
    internal override int InObjectSlotCapacity => 256;
    internal override JSObject CloneShallowCore() => new JSObjectInObject256(this);
}
