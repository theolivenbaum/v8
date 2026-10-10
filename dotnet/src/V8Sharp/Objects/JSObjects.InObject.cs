// Port of the in-object property layout of src/objects/js-objects.h (+ -inl.h):
// JSObject::GetInObjectPropertyOffset, RawFastPropertyAt/FastPropertyAtPut for
// in-object fields, and the instance-size classes Factory::NewJSObjectFromMap
// allocates.
//
// V8 lays out an ordinary object as header (map, properties_or_hash, elements)
// followed by map->GetInObjectProperties() tagged slots; fields beyond those
// live in the PropertyArray. A CLR object cannot be sized per allocation, so
// V8Sharp allocates ordinary objects from a small chain of classes whose last
// fields are [InlineArray] segments: JSObjectInObject1 holds slot 0,
// JSObjectInObject2 derives from it and adds slot 1, and so on up to 4 (small
// objects, V8's usual instance sizes after slack tracking, are common: pairs,
// vectors). JSObjectInObject5, 6, 7 and 8 each derive from JSObjectInObject4
// and add 1, 2, 3 or 4 slots (objects of five to seven fields are common too:
// Richards' packets and tasks, DeltaBlue's variables, RayTrace's
// intersections; siblings rather than a chain keep the class depth, which
// type checks walk, at most five below JSObject), then JSObjectInObject12
// derives from JSObjectInObject8 and adds 8..11, and so on. An object gets
// the smallest class covering its map's in-object property count. Every
// class's slots are one run from slot 0 (InObjectLayout), so in-object slot i
// of any object whose map has more than i in-object properties is at the
// same offset. In-object slack tracking shrinks the map's instance size
// after kGenerousAllocationCount constructions; objects allocated before keep
// their (larger) class, which is what V8's left-trimmed filler amounts to.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

[InlineArray(1)]
internal struct InObjectSlots1 { JSValue _e0; }

[InlineArray(2)]
internal struct InObjectSlots2 { JSValue _e0; }

[InlineArray(3)]
internal struct InObjectSlots3 { JSValue _e0; }

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
    /// AllocateForMap creates as plain JSObjects, and arguments objects. Other JSObject subclasses
    /// (arrays, functions, regexps ...) keep them at the start of the
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
        InstanceType.JSSetPrototypeType or InstanceType.JSTypedArrayPrototypeType or
        InstanceType.JSArgumentsObjectType => true,
        _ => false,
    };

    /// <summary>The number of in-object slots of this object's class (its physical instance size).</summary>
    internal virtual int InObjectSlotCapacity => 0;

    /// <summary>An ordinary object of the smallest class with room for <paramref name="map"/>'s in-object properties.</summary>
    internal static JSObject NewWithInObjectSlots(Map map)
    {
        if (map.InstanceType == InstanceType.JSArgumentsObjectType) return new JSArgumentsObject(map);
        int count = map.GetInObjectProperties();
        return count switch
        {
            0 => new JSObject(map),
            1 => new JSObjectInObject1(map),
            2 => new JSObjectInObject2(map),
            3 => new JSObjectInObject3(map),
            4 => new JSObjectInObject4(map),
            5 => new JSObjectInObject5(map),
            6 => new JSObjectInObject6(map),
            7 => new JSObjectInObject7(map),
            8 => new JSObjectInObject8(map),
            <= 12 => new JSObjectInObject12(map),
            <= 16 => new JSObjectInObject16(map),
            <= 32 => new JSObjectInObject32(map),
            <= 64 => new JSObjectInObject64(map),
            <= 128 => new JSObjectInObject128(map),
            _ => new JSObjectInObject256(map),
        };
    }

    /// <summary>
    /// FastNewObject's allocation (builtins-constructor-gen.cc): an ordinary
    /// object for a constructor's initial map, which is in fast mode with
    /// fast elements, so the header is the map and the empty elements and
    /// the in-object slots start as undefined (the CLR's zeroed memory).
    /// </summary>
    internal static JSObject FastNewWithInObjectSlots(Map map)
    {
        Debug.Assert(map.InstanceType == InstanceType.JSObjectType && !map.IsDictionaryMap && map.HasFastElements);
        FixedArray empty = FixedArray.Empty;
        return map.GetInObjectProperties() switch
        {
            1 => new JSObjectInObject1(map, empty),
            2 => new JSObjectInObject2(map, empty),
            3 => new JSObjectInObject3(map, empty),
            4 => new JSObjectInObject4(map, empty),
            5 => new JSObjectInObject5(map, empty),
            6 => new JSObjectInObject6(map, empty),
            7 => new JSObjectInObject7(map, empty),
            8 => new JSObjectInObject8(map, empty),
            <= 12 => new JSObjectInObject12(map, empty),
            <= 16 => new JSObjectInObject16(map, empty),
            <= 32 => new JSObjectInObject32(map, empty),
            <= 64 => new JSObjectInObject64(map, empty),
            <= 128 => new JSObjectInObject128(map, empty),
            _ => new JSObjectInObject256(map, empty),
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
            return ref Unsafe.Add(ref InObjectLayout.First(Unsafe.As<JSObjectInObject1>(this)), index);
        }
        return ref InObjectSlotBySegment(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    ref JSValue InObjectSlotBySegment(int index)
    {
        if (index == 0) return ref Unsafe.As<JSObjectInObject1>(this)._slots0[0];
        if (index == 1) return ref Unsafe.As<JSObjectInObject2>(this)._slot1[0];
        if (index == 2) return ref Unsafe.As<JSObjectInObject3>(this)._slot2[0];
        if (index == 3) return ref Unsafe.As<JSObjectInObject4>(this)._slot3[0];
        if (index < 8)
        {
            if (this is JSObjectInObject5 o5) return ref o5._slot4[0];
            if (this is JSObjectInObject6 o6) return ref o6._tail6[index - 4];
            if (this is JSObjectInObject7 o7) return ref o7._tail7[index - 4];
            return ref Unsafe.As<JSObjectInObject8>(this)._slots1[index - 4];
        }
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
    public static ref JSValue First(JSObjectInObject1 obj) => ref obj._slots0[0];

    static bool Check()
    {
        var probe = (JSObjectInObject256)RuntimeHelpers.GetUninitializedObject(typeof(JSObjectInObject256));
        ref JSValue first = ref probe._slots0[0];
        return Offset(ref first, ref probe._slot1[0]) == 1 &&
               Offset(ref first, ref probe._slot2[0]) == 2 &&
               Offset(ref first, ref probe._slot3[0]) == 3 &&
               Offset(ref first, ref probe._slots1[0]) == 4 &&
               SiblingOffset<JSObjectInObject5>(static o => ref o._slot4[0]) == 4 &&
               SiblingOffset<JSObjectInObject6>(static o => ref o._tail6[0]) == 4 &&
               SiblingOffset<JSObjectInObject7>(static o => ref o._tail7[0]) == 4 &&
               Offset(ref first, ref probe._slots2[0]) == 8 &&
               Offset(ref first, ref probe._slots3[0]) == 12 &&
               Offset(ref first, ref probe._slots4[0]) == 16 &&
               Offset(ref first, ref probe._slots5[0]) == 32 &&
               Offset(ref first, ref probe._slots6[0]) == 64 &&
               Offset(ref first, ref probe._slots7[0]) == 128;
    }

    delegate ref JSValue SlotOf<T>(T obj);

    static long SiblingOffset<T>(SlotOf<T> slot) where T : JSObjectInObject1
    {
        var probe = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        return Offset(ref probe._slots0[0], ref slot(probe));
    }

    static long Offset(ref JSValue first, ref JSValue other) =>
        (long)Unsafe.ByteOffset(ref first, ref other) / Unsafe.SizeOf<JSValue>();
}

/// <summary>An ordinary object with in-object slot 0.</summary>
internal class JSObjectInObject1 : JSObject
{
    internal InObjectSlots1 _slots0;
    public JSObjectInObject1(Map map) : base(map, inObjectSlots: true) { }
    /// <summary>FastNewObject (see <see cref="JSObject.FastNewWithInObjectSlots"/>).</summary>
    internal JSObjectInObject1(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject1(JSObjectInObject1 source) : base(source) => _slots0 = source._slots0;
    internal override int InObjectSlotCapacity => 1;
    internal override JSObject CloneShallowCore() => new JSObjectInObject1(this);
}

/// <summary>An ordinary object with in-object slots 0..1.</summary>
internal class JSObjectInObject2 : JSObjectInObject1
{
    internal InObjectSlots1 _slot1;
    public JSObjectInObject2(Map map) : base(map) { }
    internal JSObjectInObject2(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject2(JSObjectInObject2 source) : base(source) => _slot1 = source._slot1;
    internal override int InObjectSlotCapacity => 2;
    internal override JSObject CloneShallowCore() => new JSObjectInObject2(this);
}

/// <summary>An ordinary object with in-object slots 0..2.</summary>
internal class JSObjectInObject3 : JSObjectInObject2
{
    internal InObjectSlots1 _slot2;
    public JSObjectInObject3(Map map) : base(map) { }
    internal JSObjectInObject3(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject3(JSObjectInObject3 source) : base(source) => _slot2 = source._slot2;
    internal override int InObjectSlotCapacity => 3;
    internal override JSObject CloneShallowCore() => new JSObjectInObject3(this);
}

/// <summary>An ordinary object with in-object slots 0..3.</summary>
internal class JSObjectInObject4 : JSObjectInObject3
{
    internal InObjectSlots1 _slot3;
    public JSObjectInObject4(Map map) : base(map) { }
    internal JSObjectInObject4(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject4(JSObjectInObject4 source) : base(source) => _slot3 = source._slot3;
    internal override int InObjectSlotCapacity => 4;
    internal override JSObject CloneShallowCore() => new JSObjectInObject4(this);
}

/// <summary>An ordinary object with in-object slots 0..4.</summary>
internal sealed class JSObjectInObject5 : JSObjectInObject4
{
    internal InObjectSlots1 _slot4;
    public JSObjectInObject5(Map map) : base(map) { }
    internal JSObjectInObject5(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    JSObjectInObject5(JSObjectInObject5 source) : base(source) => _slot4 = source._slot4;
    internal override int InObjectSlotCapacity => 5;
    internal override JSObject CloneShallowCore() => new JSObjectInObject5(this);
}

/// <summary>An ordinary object with in-object slots 0..5.</summary>
internal sealed class JSObjectInObject6 : JSObjectInObject4
{
    internal InObjectSlots2 _tail6;
    public JSObjectInObject6(Map map) : base(map) { }
    internal JSObjectInObject6(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    JSObjectInObject6(JSObjectInObject6 source) : base(source) => _tail6 = source._tail6;
    internal override int InObjectSlotCapacity => 6;
    internal override JSObject CloneShallowCore() => new JSObjectInObject6(this);
}

/// <summary>An ordinary object with in-object slots 0..6.</summary>
internal sealed class JSObjectInObject7 : JSObjectInObject4
{
    internal InObjectSlots3 _tail7;
    public JSObjectInObject7(Map map) : base(map) { }
    internal JSObjectInObject7(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    JSObjectInObject7(JSObjectInObject7 source) : base(source) => _tail7 = source._tail7;
    internal override int InObjectSlotCapacity => 7;
    internal override JSObject CloneShallowCore() => new JSObjectInObject7(this);
}

/// <summary>An ordinary object with in-object slots 0..7.</summary>
internal class JSObjectInObject8 : JSObjectInObject4
{
    internal InObjectSlots4 _slots1;
    public JSObjectInObject8(Map map) : base(map) { }
    internal JSObjectInObject8(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject8(JSObjectInObject8 source) : base(source) => _slots1 = source._slots1;
    internal override int InObjectSlotCapacity => 8;
    internal override JSObject CloneShallowCore() => new JSObjectInObject8(this);
}

/// <summary>An ordinary object with in-object slots 0..11.</summary>
internal class JSObjectInObject12 : JSObjectInObject8
{
    internal InObjectSlots4 _slots2;
    public JSObjectInObject12(Map map) : base(map) { }
    internal JSObjectInObject12(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject12(JSObjectInObject12 source) : base(source) => _slots2 = source._slots2;
    internal override int InObjectSlotCapacity => 12;
    internal override JSObject CloneShallowCore() => new JSObjectInObject12(this);
}

/// <summary>An ordinary object with in-object slots 0..15.</summary>
internal class JSObjectInObject16 : JSObjectInObject12
{
    internal InObjectSlots4 _slots3;
    public JSObjectInObject16(Map map) : base(map) { }
    internal JSObjectInObject16(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject16(JSObjectInObject16 source) : base(source) => _slots3 = source._slots3;
    internal override int InObjectSlotCapacity => 16;
    internal override JSObject CloneShallowCore() => new JSObjectInObject16(this);
}

/// <summary>An ordinary object with in-object slots 0..31.</summary>
internal class JSObjectInObject32 : JSObjectInObject16
{
    internal InObjectSlots16 _slots4;
    public JSObjectInObject32(Map map) : base(map) { }
    internal JSObjectInObject32(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject32(JSObjectInObject32 source) : base(source) => _slots4 = source._slots4;
    internal override int InObjectSlotCapacity => 32;
    internal override JSObject CloneShallowCore() => new JSObjectInObject32(this);
}

/// <summary>An ordinary object with in-object slots 0..63.</summary>
internal class JSObjectInObject64 : JSObjectInObject32
{
    internal InObjectSlots32 _slots5;
    public JSObjectInObject64(Map map) : base(map) { }
    internal JSObjectInObject64(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject64(JSObjectInObject64 source) : base(source) => _slots5 = source._slots5;
    internal override int InObjectSlotCapacity => 64;
    internal override JSObject CloneShallowCore() => new JSObjectInObject64(this);
}

/// <summary>An ordinary object with in-object slots 0..127.</summary>
internal class JSObjectInObject128 : JSObjectInObject64
{
    internal InObjectSlots64 _slots6;
    public JSObjectInObject128(Map map) : base(map) { }
    internal JSObjectInObject128(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    protected JSObjectInObject128(JSObjectInObject128 source) : base(source) => _slots6 = source._slots6;
    internal override int InObjectSlotCapacity => 128;
    internal override JSObject CloneShallowCore() => new JSObjectInObject128(this);
}

/// <summary>An ordinary object with in-object slots 0..255 (kMaxInObjectProperties is 252).</summary>
internal sealed class JSObjectInObject256 : JSObjectInObject128
{
    internal InObjectSlots128 _slots7;
    public JSObjectInObject256(Map map) : base(map) { }
    internal JSObjectInObject256(Map map, FixedArray emptyElements) : base(map, emptyElements) { }
    JSObjectInObject256(JSObjectInObject256 source) : base(source) => _slots7 = source._slots7;
    internal override int InObjectSlotCapacity => 256;
    internal override JSObject CloneShallowCore() => new JSObjectInObject256(this);
}
