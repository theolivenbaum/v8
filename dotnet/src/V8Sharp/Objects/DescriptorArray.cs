// Port of src/objects/descriptor-array{.h,-inl.h}, the DescriptorArray parts of
// src/objects/objects.cc, and src/objects/property.{h,cc} (Descriptor).
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's EnumCache: the enumerable own keys of a map and their field indices.</summary>
public sealed class EnumCache(FixedArray keys, FixedArray indices) : HeapObject(InstanceType.EnumCacheType)
{
    public static readonly EnumCache Empty = new(FixedArray.Empty, FixedArray.Empty);

    public FixedArray Keys = keys;
    public FixedArray Indices = indices;
}

/// <summary>
/// A property descriptor under construction (V8's Descriptor, src/objects/property.h):
/// key, value (field type, constant or accessor pair) and details.
/// </summary>
public struct Descriptor
{
    public Name Key;
    public JSValue Value;
    public PropertyDetails Details;

    public Descriptor(Name key, JSValue value, PropertyDetails details)
    {
        Debug.Assert(key.IsUniqueName);
        Debug.Assert(!key.IsAnyPrivate || !details.IsEnumerable);
        Key = key;
        Value = value;
        Details = details;
    }

    public readonly Name GetKey() => Key;
    public readonly JSValue GetValue() => Value;
    public readonly PropertyDetails GetDetails() => Details;
    public readonly int GetSortedKeyIndex() => Details.Pointer;
    public void SetSortedKeyIndex(int index) => Details = Details.SetPointer(index);

    /// <summary>Descriptor::DataField with FieldType::Any.</summary>
    public static Descriptor DataField(Name key, int fieldIndex, PropertyAttributes attributes, Representation representation) =>
        DataField(key, fieldIndex, attributes, PropertyConstness.Mutable, representation, FieldType.Any);

    public static Descriptor DataField(Name key, int fieldIndex, PropertyAttributes attributes,
        PropertyConstness constness, Representation representation, HeapObject fieldType)
    {
        var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyLocation.Field, constness, representation, fieldIndex);
        return new Descriptor(key, fieldType, details);
    }

    public static Descriptor DataConstant(Name key, JSValue value, PropertyAttributes attributes)
    {
        (Representation representation, PropertyConstness constness) = ObjectOps.OptimalRepresentation(value, PropertyConstness.Const);
        return new Descriptor(key, value,
            new PropertyDetails(PropertyKind.Data, attributes, PropertyLocation.Descriptor, constness, representation));
    }

    public static Descriptor AccessorConstant(Name key, HeapObject foreign, PropertyAttributes attributes) =>
        new(key, foreign, new PropertyDetails(PropertyKind.Accessor, attributes, PropertyLocation.Descriptor,
            PropertyConstness.Const, Representation.Tagged));
}

/// <summary>
/// V8's DescriptorArray: the keys, details and values of a map's fast-mode
/// properties in insertion order, with a hash-sorted index for binary search.
/// Shared along a transition tree; each map owns a prefix
/// (Map.NumberOfOwnDescriptors).
/// </summary>
public sealed class DescriptorArray : HeapObject
{
    /// <summary>V8's empty_descriptor_array root.</summary>
    public static readonly DescriptorArray Empty = new(0, 0);

    public const int kMaxElementsForLinearSearch = 32;
    public const int kNotFound = -1;

    public enum FastIterableState : byte
    {
        JsonFast = 0b00,
        JsonSlow = 0b01,
        Unknown = 0b11,
    }

    readonly Name?[] _keys;
    readonly PropertyDetails[] _details;
    readonly JSValue[] _values;
    int _numberOfDescriptors;

    public EnumCache EnumCache = EnumCache.Empty;
    public FastIterableState FastIterable = FastIterableState.Unknown;

    DescriptorArray(int nofDescriptors, int slack) : base(InstanceType.DescriptorArrayType)
    {
        int all = nofDescriptors + slack;
        _keys = new Name?[all];
        _details = new PropertyDetails[all];
        _values = new JSValue[all];
        _numberOfDescriptors = nofDescriptors;
    }

    /// <summary>DescriptorArray::Allocate.</summary>
    public static DescriptorArray Allocate(int nofDescriptors, int slack)
    {
        Debug.Assert(nofDescriptors + slack <= Map.kMaxNumberOfDescriptors);
        return nofDescriptors + slack == 0 ? Empty : new DescriptorArray(nofDescriptors, slack);
    }

    public int NumberOfAllDescriptors => _keys.Length;
    public int NumberOfDescriptors => _numberOfDescriptors;
    public int NumberOfSlackDescriptors => _keys.Length - _numberOfDescriptors;
    public int NumberOfEntries => _numberOfDescriptors;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Name GetKey(InternalIndex descriptor) => _keys[descriptor.AsInt]!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Name GetKey(int descriptor) => _keys[descriptor]!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PropertyDetails GetDetails(InternalIndex descriptor) => _details[descriptor.AsInt];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PropertyDetails GetDetails(int descriptor) => _details[descriptor];

    /// <summary>The raw value slot: a field type, a constant, or an accessor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue GetValue(InternalIndex descriptor) => _values[descriptor.AsInt];

    /// <summary>GetStrongValue: the constant or accessor of a kDescriptor property.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue GetStrongValue(InternalIndex descriptor) => _values[descriptor.AsInt];

    public HeapObject GetFieldType(InternalIndex descriptor)
    {
        Debug.Assert(GetDetails(descriptor).Location == PropertyLocation.Field);
        return _values[descriptor.AsInt].Object;
    }

    public int GetFieldIndex(InternalIndex descriptor) => GetDetails(descriptor).FieldIndex;

    public bool IsInitializedDescriptor(InternalIndex descriptor) => _keys[descriptor.AsInt] is not null;

    public int GetSortedKeyIndex(int descriptorNumber) => _details[descriptorNumber].Pointer;

    public Name GetSortedKey(int descriptorNumber) => _keys[GetSortedKeyIndex(descriptorNumber)]!;

    void SetSortedKey(int descriptorNumber, int pointer) =>
        _details[descriptorNumber] = _details[descriptorNumber].SetPointer(pointer);

    void SwapSortedKeys(int first, int second)
    {
        int firstKey = GetSortedKeyIndex(first);
        SetSortedKey(first, GetSortedKeyIndex(second));
        SetSortedKey(second, firstKey);
    }

    void SetKey(InternalIndex descriptor, Name key)
    {
        _keys[descriptor.AsInt] = key;
        if (FastIterable == FastIterableState.JsonFast) FastIterable = FastIterableState.Unknown;
    }

    internal void SetValue(InternalIndex descriptor, JSValue value) => _values[descriptor.AsInt] = value;

    internal void SetDetails(InternalIndex descriptor, PropertyDetails details) => _details[descriptor.AsInt] = details;

    public void Set(InternalIndex descriptor, Name key, JSValue value, PropertyDetails details)
    {
        if ((uint)descriptor.AsInt >= (uint)_numberOfDescriptors) throw new InvalidOperationException("descriptor out of range");
        SetKey(descriptor, key);
        SetDetails(descriptor, details);
        SetValue(descriptor, value);
    }

    public void Set(InternalIndex descriptor, in Descriptor desc) => Set(descriptor, desc.Key, desc.Value, desc.Details);

    /// <summary>DescriptorArray::Append: add at the end, keeping the hash-sorted index.</summary>
    public void Append(Descriptor desc)
    {
        int descriptorNumber = _numberOfDescriptors;
        int newNumberOfDescriptors = descriptorNumber + 1;
        Debug.Assert(newNumberOfDescriptors <= NumberOfAllDescriptors);
        _numberOfDescriptors = newNumberOfDescriptors;
        Set(new InternalIndex(descriptorNumber), desc);

        // We do not need to maintain the sorted keys while the array is small
        // enough for a linear search.
        if (newNumberOfDescriptors <= kMaxElementsForLinearSearch)
        {
            if (LinearSearch(desc.Key, descriptorNumber).IsFound) throw new InvalidOperationException("duplicate descriptor");
            return;
        }
        if (newNumberOfDescriptors == kMaxElementsForLinearSearch + 1)
        {
            SortImpl(newNumberOfDescriptors);
            CheckNameCollisionDuringInsertion(desc.Key, desc.Key.EnsureHash(), GetDetails(descriptorNumber).Pointer);
            return;
        }

        uint descHash = desc.Key.EnsureHash();
        uint collisionHash = 0;
        int insertion;
        for (insertion = descriptorNumber; insertion > 0; --insertion)
        {
            Name key = GetSortedKey(insertion - 1);
            collisionHash = key.EnsureHash();
            if (collisionHash <= descHash) break;
            SetSortedKey(insertion, GetSortedKeyIndex(insertion - 1));
        }
        SetSortedKey(insertion, descriptorNumber);
        if (collisionHash != descHash) return;
        CheckNameCollisionDuringInsertion(desc.Key, descHash, insertion);
    }

    public void CheckNameCollisionDuringInsertion(Name key, uint descHash, int insertionIndex)
    {
        if (insertionIndex <= 0) return;
        for (int i = insertionIndex; i > 0; --i)
        {
            Name currentKey = GetSortedKey(i - 1);
            if (currentKey.EnsureHash() != descHash) return;
            if (ReferenceEquals(currentKey, key)) throw new InvalidOperationException("duplicate descriptor");
        }
    }

    /// <summary>DescriptorArray::Replace: overwrite keeping the sorted position.</summary>
    public void Replace(InternalIndex index, Descriptor descriptor)
    {
        descriptor.SetSortedKeyIndex(GetSortedKeyIndex(index.AsInt));
        Set(index, descriptor);
    }

    /// <summary>Makes every field Tagged/Any (used when transitions cannot be recorded).</summary>
    public void GeneralizeAllFields()
    {
        int length = _numberOfDescriptors;
        for (int i = 0; i < length; i++)
        {
            PropertyDetails details = _details[i];
            details = details.CopyWithRepresentation(Representation.Tagged);
            if (details.Location == PropertyLocation.Field)
            {
                _values[i] = FieldType.Any;
            }
            _details[i] = details;
        }
    }

    public void Sort()
    {
        int len = _numberOfDescriptors;
        if (len <= kMaxElementsForLinearSearch) return;
        SortImpl(len);
    }

    /// <summary>In-place heap sort of the sorted-key pointers by name hash.</summary>
    void SortImpl(int len)
    {
        for (int i = 0; i < len; ++i) SetSortedKey(i, i);
        int maxParentIndex = (len / 2) - 1;
        for (int i = maxParentIndex; i >= 0; --i)
        {
            int parentIndex = i;
            uint parentHash = GetSortedKey(i).EnsureHash();
            while (parentIndex <= maxParentIndex)
            {
                int childIndex = 2 * parentIndex + 1;
                uint childHash = GetSortedKey(childIndex).EnsureHash();
                if (childIndex + 1 < len)
                {
                    uint rightChildHash = GetSortedKey(childIndex + 1).EnsureHash();
                    if (rightChildHash > childHash)
                    {
                        childIndex++;
                        childHash = rightChildHash;
                    }
                }
                if (childHash <= parentHash) break;
                SwapSortedKeys(parentIndex, childIndex);
                parentIndex = childIndex;
            }
        }
        for (int i = len - 1; i > 0; --i)
        {
            SwapSortedKeys(0, i);
            int parentIndex = 0;
            uint parentHash = GetSortedKey(parentIndex).EnsureHash();
            maxParentIndex = (i / 2) - 1;
            while (parentIndex <= maxParentIndex)
            {
                int childIndex = parentIndex * 2 + 1;
                uint childHash = GetSortedKey(childIndex).EnsureHash();
                if (childIndex + 1 < i)
                {
                    uint rightChildHash = GetSortedKey(childIndex + 1).EnsureHash();
                    if (rightChildHash > childHash)
                    {
                        childIndex++;
                        childHash = rightChildHash;
                    }
                }
                if (childHash <= parentHash) break;
                SwapSortedKeys(parentIndex, childIndex);
                parentIndex = childIndex;
            }
        }
    }

    /// <summary>Search among the first <paramref name="validDescriptors"/> descriptors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InternalIndex Search(Name name, int validDescriptors)
    {
        Debug.Assert(name.IsUniqueName);
        if (validDescriptors == 0) return InternalIndex.NotFound;
        if (validDescriptors <= kMaxElementsForLinearSearch) return LinearSearch(name, validDescriptors);
        return BinarySearch(name, validDescriptors);
    }

    public InternalIndex Search(Name name, Map map)
    {
        int numberOfOwnDescriptors = map.NumberOfOwnDescriptors;
        if (numberOfOwnDescriptors == 0) return InternalIndex.NotFound;
        return Search(name, numberOfOwnDescriptors);
    }

    /// <summary>
    /// SearchWithCache. V8 consults the isolate's DescriptorLookupCache first;
    /// V8Sharp searches directly (the IC layer caches (map, name) pairs).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InternalIndex SearchWithCache(Name name, Map map) => Search(name, map);

    InternalIndex BinarySearch(Name name, int validDescriptors)
    {
        int end = _numberOfDescriptors;
        uint hash = name.EnsureHash();
        int low = 0;
        int high = end;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (GetSortedKey(mid).EnsureHash() < hash) low = mid + 1;
            else high = mid;
        }
        for (int number = low; number < end; ++number)
        {
            int index = GetSortedKeyIndex(number);
            Name entry = _keys[index]!;
            if (ReferenceEquals(entry, name))
            {
                return index >= validDescriptors ? InternalIndex.NotFound : new InternalIndex(index);
            }
            if (entry.EnsureHash() != hash) return InternalIndex.NotFound;
        }
        return InternalIndex.NotFound;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    InternalIndex LinearSearch(Name name, int validDescriptors)
    {
        Name?[] keys = _keys;
        for (int i = 0; i < validDescriptors; ++i)
        {
            if (ReferenceEquals(keys[i], name)) return new InternalIndex(i);
        }
        return InternalIndex.NotFound;
    }

    /// <summary>DescriptorArray::CopyUpTo.</summary>
    public static DescriptorArray CopyUpTo(DescriptorArray desc, int enumerationIndex, int slack = 0) =>
        CopyUpToAddAttributes(desc, enumerationIndex, PropertyAttributes.NONE, slack);

    /// <summary>DescriptorArray::CopyUpToAddAttributes.</summary>
    public static DescriptorArray CopyUpToAddAttributes(DescriptorArray source, int enumerationIndex,
        PropertyAttributes attributes, int slack = 0)
    {
        if (enumerationIndex + slack == 0) return Empty;
        int size = enumerationIndex;
        DescriptorArray copy = Allocate(size, slack);
        if (attributes != PropertyAttributes.NONE)
        {
            for (int i = 0; i < size; i++)
            {
                JSValue valueOrFieldType = source._values[i];
                Name key = source._keys[i]!;
                PropertyDetails details = source._details[i];
                // Bulk attribute changes never affect private properties.
                if (!key.IsAnyPrivate)
                {
                    var mask = PropertyAttributes.DONT_DELETE | PropertyAttributes.DONT_ENUM;
                    // READ_ONLY is an invalid attribute for JS setters/getters.
                    if (details.Kind != PropertyKind.Accessor || valueOrFieldType.HeapObjectOrNull is not AccessorPair)
                    {
                        mask |= PropertyAttributes.READ_ONLY;
                    }
                    details = details.CopyAddAttributes(attributes & mask);
                }
                copy.Set(new InternalIndex(i), key, valueOrFieldType, details);
            }
        }
        else
        {
            for (int i = 0; i < size; i++) copy.CopyFrom(new InternalIndex(i), source);
        }
        if (source._numberOfDescriptors != enumerationIndex) copy.Sort();
        return copy;
    }

    void CopyFrom(InternalIndex index, DescriptorArray src) =>
        Set(index, src.GetKey(index), src.GetValue(index), src.GetDetails(index));

    public bool IsEqualUpTo(DescriptorArray desc, int nofDescriptors)
    {
        for (int i = 0; i < nofDescriptors; i++)
        {
            if (!ReferenceEquals(_keys[i], desc._keys[i]) || !_values[i].IsIdenticalTo(desc._values[i])) return false;
            PropertyDetails details = _details[i];
            PropertyDetails otherDetails = desc._details[i];
            if (details.Kind != otherDetails.Kind || details.Location != otherDetails.Location ||
                !details.Representation.Equals(otherDetails.Representation))
            {
                return false;
            }
        }
        return true;
    }

    public void ClearEnumCache() => EnumCache = EnumCache.Empty;

    public void CopyEnumCacheFrom(DescriptorArray array) => EnumCache = array.EnumCache;

    public static void InitializeOrChangeEnumCache(DescriptorArray descriptors, FixedArray keys, FixedArray indices)
    {
        if (ReferenceEquals(descriptors.EnumCache, EnumCache.Empty))
        {
            descriptors.EnumCache = new EnumCache(keys, indices);
        }
        else
        {
            descriptors.EnumCache.Keys = keys;
            descriptors.EnumCache.Indices = indices;
        }
    }

    /// <summary>Debug check that the sorted index has no duplicate keys.</summary>
    public bool IsSortedNoDuplicates()
    {
        uint currentHash = 0;
        Name? currentKey = null;
        for (int i = 0; i < _numberOfDescriptors; i++)
        {
            Name key = GetSortedKey(i);
            uint hash = key.EnsureHash();
            if (currentKey is not null && hash < currentHash) return false;
            if (ReferenceEquals(key, currentKey)) return false;
            currentKey = key;
            currentHash = hash;
        }
        return true;
    }
}

/// <summary>A getter/setter pair (V8's AccessorPair). Missing components are null.</summary>
public sealed class AccessorPair : HeapObject
{
    public JSValue Getter = JSValue.Null;
    public JSValue Setter = JSValue.Null;

    public AccessorPair() : base(InstanceType.AccessorPairType) { }

    public JSValue Get(Common.AccessorComponent component) =>
        component == Common.AccessorComponent.ACCESSOR_GETTER ? Getter : Setter;

    public void Set(Common.AccessorComponent component, JSValue value)
    {
        if (component == Common.AccessorComponent.ACCESSOR_GETTER) Getter = value;
        else Setter = value;
    }

    /// <summary>Set both components, ignoring nulls (AccessorPair::SetComponents).</summary>
    public void SetComponents(JSValue getter, JSValue setter)
    {
        if (!getter.IsNull) Getter = getter;
        if (!setter.IsNull) Setter = setter;
    }

    public bool Equals(JSValue getterValue, JSValue setterValue) =>
        Getter.IsIdenticalTo(getterValue) && Setter.IsIdenticalTo(setterValue);

    public static AccessorPair Copy(AccessorPair pair) => new() { Getter = pair.Getter, Setter = pair.Setter };

    /// <summary>AccessorPair::GetComponent: null reads as undefined.</summary>
    public static JSValue GetComponent(AccessorPair pair, Common.AccessorComponent component)
    {
        JSValue accessor = pair.Get(component);
        return accessor.IsNull ? JSValue.Undefined : accessor;
    }
}

/// <summary>The getter of a native accessor (V8's AccessorNameGetterCallback).</summary>
public delegate JSValue AccessorNameGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name);

/// <summary>The setter of a native accessor; returns false if the store failed.</summary>
public delegate bool AccessorNameSetter(Isolate isolate, JSValue receiver, JSObject holder, Name name, JSValue value, Common.ShouldThrow? shouldThrow);

/// <summary>
/// A native accessor (V8's AccessorInfo), used for the engine's "special data
/// properties" such as Array length, function name/length/prototype, String
/// length and arguments.callee (src/builtins/accessors.cc).
/// </summary>
public sealed class AccessorInfo(Name name, AccessorNameGetter? getter, AccessorNameSetter? setter) : HeapObject(InstanceType.AccessorInfoType)
{
    public readonly Name Name = name;
    public readonly AccessorNameGetter? Getter = getter;
    public readonly AccessorNameSetter? Setter = setter;

    /// <summary>Replace the accessor with a data property on first access.</summary>
    public bool ReplaceOnAccess { get; init; }

    /// <summary>The property looks like a data property to JavaScript.</summary>
    public bool IsSpecialDataProperty { get; init; } = true;

    /// <summary>getter_side_effect_type() == SideEffectType::kHasNoSideEffect.</summary>
    public bool HasNoSideEffect { get; init; }

    public bool HasGetter => Getter is not null;
    public bool HasSetter => Setter is not null;
}
