// Port of src/objects/field-index{.h,-inl.h} and src/objects/field-type.{h,cc}.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>
/// V8's FieldIndex: where a fast-mode field lives, in-object (an object slot)
/// or in the PropertyArray (architecture.md section 5).
/// </summary>
public readonly struct FieldIndex : IEquatable<FieldIndex>
{
    public enum Encoding : byte { Tagged, Double, Word32 }

    readonly int _propertyIndex;
    readonly int _inObjectPropertyCount;
    readonly int _storageIndex;
    readonly Encoding _encoding;

    FieldIndex(int propertyIndex, Map map, Encoding encoding)
    {
        _propertyIndex = propertyIndex;
        _inObjectPropertyCount = map.GetInObjectProperties();
        _encoding = encoding;
        // Ordinary objects hold in-object fields in object slots; the other
        // JSObject subclasses hold them at the start of the PropertyArray
        // (JSObjects.InObject.cs, deviations.md).
        _storageIndex = map.HasInObjectSlots
            ? propertyIndex < _inObjectPropertyCount ? propertyIndex : JSObject.kPropertyArrayStorageBase + propertyIndex - _inObjectPropertyCount
            : JSObject.kPropertyArrayStorageBase + propertyIndex;
    }

    /// <summary>
    /// Where the field is in a V8Sharp object: an in-object slot index below
    /// JSObject.kPropertyArrayStorageBase, else that base plus the index in the
    /// PropertyArray (JSObject.FieldAt). The IC handlers cache it.
    /// </summary>
    public int StorageIndex => _storageIndex;

    /// <summary>Zero-based from the first in-object property; overflows to out-of-object properties.</summary>
    public int PropertyIndex => _propertyIndex;

    public bool IsInObject => _propertyIndex < _inObjectPropertyCount;
    public bool IsDouble => _encoding == Encoding.Double;
    public Encoding FieldEncoding => _encoding;

    /// <summary>The index in the out-of-object part (V8's outobject_array_index).</summary>
    public int OutobjectArrayIndex
    {
        get
        {
            Debug.Assert(!IsInObject);
            return _propertyIndex - _inObjectPropertyCount;
        }
    }

    static Encoding FieldEncodingFor(Representation representation) => representation.kind switch
    {
        Representation.Kind.None or Representation.Kind.Smi or Representation.Kind.HeapObject or Representation.Kind.Tagged => Encoding.Tagged,
        Representation.Kind.Double => Encoding.Double,
        _ => throw new InvalidOperationException("unexpected representation " + representation.Mnemonic()),
    };

    public static FieldIndex ForPropertyIndex(Map map, int propertyIndex, Representation representation) =>
        new(propertyIndex, map, FieldEncodingFor(representation));

    public static FieldIndex ForPropertyIndex(Map map, int propertyIndex) =>
        new(propertyIndex, map, Encoding.Tagged);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldIndex ForDetails(Map map, PropertyDetails details) =>
        new(details.FieldIndex, map, FieldEncodingFor(details.Representation));

    public static FieldIndex ForDescriptor(Map map, InternalIndex descriptor) =>
        ForDetails(map, map.InstanceDescriptors.GetDetails(descriptor));

    /// <summary>
    /// V8's GetLoadByFieldIndex encoding: in-object indices are positive,
    /// out-of-object ones are -index-1, shifted left by one with the low bit
    /// marking a double field.
    /// </summary>
    public int GetLoadByFieldIndex()
    {
        int result = IsInObject ? _propertyIndex : -OutobjectArrayIndex - 1;
        result = (int)((uint)result << 1);
        return IsDouble ? (result | 1) : result;
    }

    public bool Equals(FieldIndex other) =>
        _propertyIndex == other._propertyIndex && _inObjectPropertyCount == other._inObjectPropertyCount &&
        _encoding == other._encoding && _storageIndex == other._storageIndex;
    public override bool Equals(object? obj) => obj is FieldIndex f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(_propertyIndex, _inObjectPropertyCount, _encoding);
    public static bool operator ==(FieldIndex a, FieldIndex b) => a.Equals(b);
    public static bool operator !=(FieldIndex a, FieldIndex b) => !a.Equals(b);
}

/// <summary>
/// V8's FieldType: None, Any, or Class(map). Stored in a descriptor's value
/// slot as a HeapObject: the two sentinels or the Map itself.
/// </summary>
/// <remarks>
/// Deviation: V8 holds Class field types weakly (they can be cleared on GC);
/// V8Sharp holds them strongly.
/// </remarks>
public static class FieldType
{
    sealed class Sentinel(string name) : HeapObject(InstanceType.CellType)
    {
        public override string ToString() => name;
    }

    public static readonly HeapObject None = new Sentinel("None");
    public static readonly HeapObject Any = new Sentinel("Any");

    public static HeapObject Class(Map map) => map;

    public static bool IsNone(HeapObject type) => ReferenceEquals(type, None);
    public static bool IsAny(HeapObject type) => ReferenceEquals(type, Any);
    public static bool IsClass(HeapObject type) => type is Map;

    public static Map AsClass(HeapObject type) => (Map)type;

    public static bool NowStable(HeapObject type) => type is not Map m || m.IsStable;

    public static bool NowIs(HeapObject type, HeapObject other)
    {
        if (IsAny(other)) return true;
        if (IsNone(type)) return true;
        if (IsNone(other)) return false;
        if (IsAny(type)) return false;
        return ReferenceEquals(type, other);
    }

    public static bool Equals(HeapObject type, HeapObject other)
    {
        if (IsAny(type) && IsAny(other)) return true;
        if (IsNone(type) && IsNone(other)) return true;
        if (IsClass(type) && IsClass(other)) return ReferenceEquals(type, other);
        return false;
    }

    public static bool NowContains(HeapObject type, in JSValue value)
    {
        if (IsAny(type)) return true;
        if (IsNone(type)) return false;
        // Only JSReceivers carry a map in V8Sharp; primitives never match a class type.
        return value.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, type);
    }

    public static string PrintTo(HeapObject type) => IsAny(type) ? "Any" : IsNone(type) ? "None" : "Class";
}
