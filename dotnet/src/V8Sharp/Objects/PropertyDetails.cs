// Port of src/objects/property-details.h (+ -inl.h): property attributes,
// kinds, locations, constness, representations and the packed PropertyDetails.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's PropertyAttributes (v8::PropertyAttribute values).</summary>
[Flags]
public enum PropertyAttributes
{
    NONE = 0,
    READ_ONLY = 1,
    DONT_ENUM = 2,
    DONT_DELETE = 4,
    ALL_ATTRIBUTES_MASK = READ_ONLY | DONT_ENUM | DONT_DELETE,
    SEALED = DONT_DELETE,
    FROZEN = SEALED | READ_ONLY,
    /// <summary>Only a return value: the property does not exist.</summary>
    ABSENT = 64,
}

/// <summary>V8's PropertyFilter.</summary>
[Flags]
public enum PropertyFilter
{
    ALL_PROPERTIES = 0,
    ONLY_WRITABLE = 1,
    ONLY_ENUMERABLE = 2,
    ONLY_CONFIGURABLE = 4,
    SKIP_STRINGS = 8,
    SKIP_SYMBOLS = 16,
    PRIVATE_NAMES_ONLY = 32,
    ENUMERABLE_STRINGS = ONLY_ENUMERABLE | SKIP_SYMBOLS,
}

public enum PropertyKind : byte { Data = 0, Accessor = 1 }

public enum PropertyLocation : byte { Field = 0, Descriptor = 1 }

public enum PropertyConstness : byte { Mutable = 0, Const = 1 }

public enum PropertyCellType : byte
{
    Mutable,
    Undefined,
    Constant,
    ConstantType,
    InTransition,
    NoCell = Mutable,
}

/// <summary>V8's Representation of a field's values.</summary>
public readonly struct Representation : IEquatable<Representation>
{
    public enum Kind : byte
    {
        None,
        Smi,
        Double,
        HeapObject,
        Tagged,
        WasmValue,
    }

    readonly Kind _kind;

    Representation(Kind kind) => _kind = kind;

    public static Representation None => new(Kind.None);
    public static Representation Tagged => new(Kind.Tagged);
    public static Representation Smi => new(Kind.Smi);
    public static Representation Double => new(Kind.Double);
    public static Representation HeapObject => new(Kind.HeapObject);
    public static Representation WasmValue => new(Kind.WasmValue);
    public static Representation FromKind(Kind kind) => new(kind);

    public Kind kind => _kind;
    public bool IsNone => _kind == Kind.None;
    public bool IsWasmValue => _kind == Kind.WasmValue;
    public bool IsTagged => _kind == Kind.Tagged;
    public bool IsSmi => _kind == Kind.Smi;
    public bool IsSmiOrTagged => IsSmi || IsTagged;
    public bool IsDouble => _kind == Kind.Double;
    public bool IsHeapObject => _kind == Kind.HeapObject;

    public bool Equals(Representation other) => _kind == other._kind;
    public override bool Equals(object? obj) => obj is Representation r && Equals(r);
    public override int GetHashCode() => (int)_kind;
    public static bool operator ==(Representation a, Representation b) => a._kind == b._kind;
    public static bool operator !=(Representation a, Representation b) => a._kind != b._kind;

    public bool IsCompatibleForLoad(Representation other) => IsDouble == other.IsDouble;
    public bool IsCompatibleForStore(Representation other) => Equals(other);

    /// <summary>Whether generalizing from this representation might deprecate the map.</summary>
    public bool MightCauseMapDeprecation()
    {
        if (IsTagged || IsHeapObject || IsDouble || IsWasmValue) return false;
        return true;
    }

    public bool CanBeInPlaceChangedTo(Representation other)
    {
        if (Equals(other)) return true;
        if (IsWasmValue || other.IsWasmValue) return false;
        if (IsNone) return !other.IsDouble;
        if (!other.IsTagged) return false;
        return true;
    }

    public Representation MostGenericInPlaceChange() => IsWasmValue ? WasmValue : Tagged;

    public bool IsMoreGeneralThan(Representation other)
    {
        if (IsWasmValue) return false;
        if (IsHeapObject) return other.IsNone;
        return _kind > other._kind;
    }

    public bool FitsInto(Representation other) => other.IsMoreGeneralThan(this) || other.Equals(this);

    public Representation Generalize(Representation other)
    {
        if (other.FitsInto(this)) return this;
        if (other.IsMoreGeneralThan(this)) return other;
        return Tagged;
    }

    public string Mnemonic() => _kind switch
    {
        Kind.None => "v",
        Kind.Tagged => "t",
        Kind.Smi => "s",
        Kind.Double => "d",
        Kind.HeapObject => "h",
        Kind.WasmValue => "w",
        _ => throw new InvalidOperationException(),
    };

    public override string ToString() => _kind switch
    {
        Kind.None => "none",
        Kind.Smi => "smi",
        Kind.Double => "double",
        Kind.HeapObject => "heap-object",
        Kind.Tagged => "tagged",
        Kind.WasmValue => "wasm-value",
        _ => "?",
    };
}

/// <summary>
/// V8's PropertyDetails: kind, constness, attributes, and either (dictionary
/// mode) the property-cell type and enumeration index, or (fast mode) the
/// location, representation, sorted-key pointer and field index.
/// </summary>
/// <remarks>
/// Deviation: V8 encodes a fast field's position as (offset in words, in-object
/// bit). V8Sharp keeps one property backing array per object, so the fast-mode
/// bits hold the field's property index instead; FieldIndex.ForDetails derives
/// the in-object bit from the map (architecture.md section 5).
/// </remarks>
public readonly struct PropertyDetails : IEquatable<PropertyDetails>
{
    // KindField = BitField<PropertyKind, 0, 1>, ConstnessField = Next<1>,
    // AttributesField = Next<3>.
    const int KindShift = 0;
    const int ConstnessShift = 1;
    const int AttributesShift = 2;
    const uint KindMask = 1u << KindShift;
    const uint ConstnessMask = 1u << ConstnessShift;
    const uint AttributesMask = 7u << AttributesShift;

    // Dictionary mode: PropertyCellTypeField = Next<3>, DictionaryStorageField = Next<23>.
    const int CellTypeShift = 5;
    const uint CellTypeMask = 7u << CellTypeShift;
    const int DictionaryStorageShift = 8;
    const uint DictionaryStorageMax = (1u << 23) - 1;
    const uint DictionaryStorageMask = DictionaryStorageMax << DictionaryStorageShift;

    // Fast mode: LocationField = Next<1>, RepresentationField = Next<3>,
    // DescriptorPointer = Next<10>, field index = Next<11>.
    const int LocationShift = 5;
    const uint LocationMask = 1u << LocationShift;
    const int RepresentationShift = 6;
    const uint RepresentationMask = 7u << RepresentationShift;
    const int PointerShift = 9;
    const uint PointerMask = ((1u << Map.kDescriptorIndexBitCount) - 1) << PointerShift;
    const int FieldIndexShift = PointerShift + Map.kDescriptorIndexBitCount;
    const uint FieldIndexMask = ((1u << (Map.kDescriptorIndexBitCount + 1)) - 1) << FieldIndexShift;

    public const int kInitialIndex = 1;

    /// <summary>
    /// V8_DICT_PROPERTY_CONST_TRACKING is off in the default build, so dictionary
    /// properties start mutable.
    /// </summary>
    public const PropertyConstness kConstIfDictConstnessTracking = PropertyConstness.Mutable;

    readonly uint _value;

    PropertyDetails(uint value) => _value = value;

    /// <summary>Property details for global dictionary properties.</summary>
    public PropertyDetails(PropertyKind kind, PropertyAttributes attributes, PropertyCellType cellType, int dictionaryIndex = 0)
    {
        _value = ((uint)kind << KindShift) |
                 ((uint)attributes << AttributesShift) |
                 ((uint)PropertyConstness.Mutable << ConstnessShift) |
                 ((uint)dictionaryIndex << DictionaryStorageShift) |
                 ((uint)cellType << CellTypeShift);
    }

    /// <summary>Property details for dictionary mode properties/elements.</summary>
    public PropertyDetails(PropertyKind kind, PropertyAttributes attributes, PropertyConstness constness, int dictionaryIndex = 0)
    {
        _value = ((uint)kind << KindShift) |
                 ((uint)attributes << AttributesShift) |
                 ((uint)constness << ConstnessShift) |
                 ((uint)dictionaryIndex << DictionaryStorageShift);
    }

    /// <summary>Property details for fast mode properties.</summary>
    public PropertyDetails(PropertyKind kind, PropertyAttributes attributes, PropertyLocation location,
        PropertyConstness constness, Representation representation, int fieldIndex = 0)
    {
        _value = ((uint)kind << KindShift) |
                 ((uint)attributes << AttributesShift) |
                 ((uint)location << LocationShift) |
                 ((uint)constness << ConstnessShift) |
                 ((uint)representation.kind << RepresentationShift) |
                 ((uint)fieldIndex << FieldIndexShift);
    }

    public static PropertyDetails Empty(PropertyCellType cellType = PropertyCellType.NoCell) =>
        new(PropertyKind.Data, PropertyAttributes.NONE, cellType);

    public uint RawValue => _value;
    public static PropertyDetails FromRaw(uint value) => new(value);

    public PropertyKind Kind => (PropertyKind)((_value & KindMask) >> KindShift);
    public PropertyLocation Location => (PropertyLocation)((_value & LocationMask) >> LocationShift);
    public PropertyConstness Constness => (PropertyConstness)((_value & ConstnessMask) >> ConstnessShift);
    public PropertyAttributes Attributes => (PropertyAttributes)((_value & AttributesMask) >> AttributesShift);
    public int DictionaryIndex => (int)((_value & DictionaryStorageMask) >> DictionaryStorageShift);
    public Representation Representation => Representation.FromKind((Representation.Kind)((_value & RepresentationMask) >> RepresentationShift));
    public PropertyCellType CellType => (PropertyCellType)((_value & CellTypeMask) >> CellTypeShift);

    /// <summary>The field's property index (V8: field_offset/is_in_object; see remarks).</summary>
    public int FieldIndex => (int)((_value & FieldIndexMask) >> FieldIndexShift);

    /// <summary>The descriptor's position in the hash-sorted order (DescriptorPointer).</summary>
    public int Pointer => (int)((_value & PointerMask) >> PointerShift);

    public bool IsReadOnly => (Attributes & PropertyAttributes.READ_ONLY) != 0;
    public bool IsConfigurable => (Attributes & PropertyAttributes.DONT_DELETE) == 0;
    public bool IsDontEnum => (Attributes & PropertyAttributes.DONT_ENUM) != 0;
    public bool IsEnumerable => !IsDontEnum;

    public bool HasKindAndAttributes(PropertyKind kind, PropertyAttributes attributes) =>
        (_value & (KindMask | AttributesMask)) == (((uint)kind << KindShift) | ((uint)attributes << AttributesShift));

    public PropertyDetails SetPointer(int i) => new((_value & ~PointerMask) | ((uint)i << PointerShift));
    public PropertyDetails SetCellType(PropertyCellType type) => new((_value & ~CellTypeMask) | ((uint)type << CellTypeShift));
    public PropertyDetails SetIndex(int index) => new((_value & ~DictionaryStorageMask) | ((uint)index << DictionaryStorageShift));
    public PropertyDetails SetFieldIndex(int index) => new((_value & ~FieldIndexMask) | ((uint)index << FieldIndexShift));
    public static bool CanSetIndex(int index) => (uint)index <= DictionaryStorageMax;
    public static bool IsValidIndex(int index) => (uint)index <= DictionaryStorageMax;

    public PropertyDetails CopyWithRepresentation(Representation representation) =>
        new((_value & ~RepresentationMask) | ((uint)representation.kind << RepresentationShift));

    public PropertyDetails CopyWithConstness(PropertyConstness constness) =>
        new((_value & ~ConstnessMask) | ((uint)constness << ConstnessShift));

    public PropertyDetails CopyAddAttributes(PropertyAttributes newAttributes)
    {
        var attrs = Attributes | newAttributes;
        return new((_value & ~AttributesMask) | ((uint)attrs << AttributesShift));
    }

    public PropertyDetails CopyWithAttributes(PropertyAttributes attributes) =>
        new((_value & ~AttributesMask) | ((uint)attributes << AttributesShift));

    public bool Equals(PropertyDetails other) => _value == other._value;
    public override bool Equals(object? obj) => obj is PropertyDetails d && Equals(d);
    public override int GetHashCode() => (int)_value;
    public static bool operator ==(PropertyDetails a, PropertyDetails b) => a._value == b._value;
    public static bool operator !=(PropertyDetails a, PropertyDetails b) => a._value != b._value;

    public override string ToString()
    {
        string attrs = "[" + ((Attributes & PropertyAttributes.READ_ONLY) == 0 ? "W" : "_") +
                       ((Attributes & PropertyAttributes.DONT_ENUM) == 0 ? "E" : "_") +
                       ((Attributes & PropertyAttributes.DONT_DELETE) == 0 ? "C" : "_") + "]";
        return $"({(Constness == PropertyConstness.Const ? "const " : "")}{(Kind == PropertyKind.Data ? "data" : "accessor")}, attrs: {attrs})";
    }
}

public static class PropertyDetailsHelpers
{
    public static bool IsGeneralizableTo(PropertyLocation a, PropertyLocation b) =>
        b == PropertyLocation.Field || a == PropertyLocation.Descriptor;

    public static bool IsGeneralizableTo(PropertyConstness a, PropertyConstness b) =>
        b == PropertyConstness.Mutable || a == PropertyConstness.Const;

    public static PropertyConstness GeneralizeConstness(PropertyConstness a, PropertyConstness b) =>
        a == PropertyConstness.Mutable ? PropertyConstness.Mutable : b;
}

/// <summary>V8's InternalIndex: a hash-table entry or descriptor number, or NotFound.</summary>
public readonly struct InternalIndex : IEquatable<InternalIndex>
{
    readonly int _value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InternalIndex(int value) => _value = value;

    public static InternalIndex NotFound => new(-1);

    public bool IsFound => _value >= 0;
    public bool IsNotFound => _value < 0;
    public int AsInt => _value;
    public uint AsUInt32 => (uint)_value;

    public bool Equals(InternalIndex other) => _value == other._value;
    public override bool Equals(object? obj) => obj is InternalIndex i && Equals(i);
    public override int GetHashCode() => _value;
    public static bool operator ==(InternalIndex a, InternalIndex b) => a._value == b._value;
    public static bool operator !=(InternalIndex a, InternalIndex b) => a._value != b._value;
    public override string ToString() => _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
