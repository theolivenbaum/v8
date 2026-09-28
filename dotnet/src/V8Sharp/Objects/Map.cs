// Port of src/objects/map.{h,cc} and map-inl.h: the hidden class.
using System.Runtime.CompilerServices;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>
/// V8's Map (hidden class) of a JSReceiver: instance type, prototype, bit
/// fields, elements kind, (shared) descriptor array and transitions.
/// </summary>
/// <remarks>
/// <see cref="InstanceType"/> hides <see cref="HeapObject.InstanceType"/>: on a
/// <c>Map</c> it is the type of the map's instances, as V8's map->instance_type();
/// a Map seen as a HeapObject still reports MapType.
/// </remarks>
public sealed class Map : HeapObject
{
    public const int kDescriptorIndexBitCount = 10;
    public const int kFirstInobjectPropertyOffsetBitCount = 8;
    public const int kMaxNumberOfDescriptors = (1 << kDescriptorIndexBitCount) - 4;
    public const int kInvalidEnumCacheSentinel = (1 << kDescriptorIndexBitCount) - 1;

    public const int kSlackTrackingCounterStart = 7;
    public const int kSlackTrackingCounterEnd = 1;
    public const int kNoSlackTracking = 0;
    public const int kGenerousAllocationCount = kSlackTrackingCounterStart - kSlackTrackingCounterEnd + 1;
    public const int kNoConstructorFunctionIndex = 0;

    // V8 counts sizes in tagged words; kTaggedSize is 4 with pointer compression.
    public const int kTaggedSize = 4;

    // ---- Fields ---------------------------------------------------------------

    /// <summary>The instance type of this map's instances (map->instance_type()).</summary>
    public new InstanceType InstanceType;

    int _instanceSizeInWords;
    int _inobjectPropertiesStartOrConstructorFunctionIndex;
    int _usedOrUnusedInstanceSizeInWords;

    public byte BitField;
    public byte BitField2;
    public uint BitField3;

    /// <summary>The prototype: a JSReceiver, or null for JavaScript null.</summary>
    public JSReceiver? Prototype;

    /// <summary>Back pointer (a Map) for transitioned maps, else the constructor (V8's constructor_or_back_pointer).</summary>
    internal HeapObject? ConstructorOrBackPointer;

    DescriptorArray _instanceDescriptors = DescriptorArray.Empty;

    /// <summary>V8's raw_transitions / prototype_info: null, a Map, a TransitionArray, a PrototypeInfo or a MigrationTarget.</summary>
    internal HeapObject? RawTransitions;

    /// <summary>V8's prototype_validity_cell: null (Smi zero) or a Cell.</summary>
    public Cell? PrototypeValidityCell;

    /// <summary>The native context the map belongs to (V8 reads it from the meta map).</summary>
    public NativeContext? NativeContext;

    public Map(InstanceType instanceType, int instanceSize, ElementsKind elementsKind, int inobjectProperties)
        : base(Objects.InstanceType.MapType)
    {
        InstanceType = instanceType;
        // Factory::InitializeMap.
        SetInstanceSize(instanceSize);
        if (InstanceTypeChecks.IsJSObject(instanceType))
        {
            SetInObjectPropertiesStartInWords(instanceSize / kTaggedSize - inobjectProperties);
            Debug.Assert(GetInObjectProperties() == inobjectProperties);
        }
        else
        {
            _inobjectPropertiesStartOrConstructorFunctionIndex = 0;
        }
        BitField3 = Bits3.EnumLength.Encode(kInvalidEnumCacheSentinel) | Bits3.OwnsDescriptorsBit | Bits3.IsExtensibleBit;
        SetElementsKind(elementsKind);
        SetInObjectUnusedPropertyFields(inobjectProperties);
    }

    // ---- Bit fields -------------------------------------------------------------

    static class Bits1
    {
        public const byte IsCallableBit = 1 << 0;
        public const byte HasNamedInterceptorBit = 1 << 1;
        public const byte HasIndexedInterceptorBit = 1 << 2;
        public const byte IsUndetectableBit = 1 << 3;
        public const byte IsAccessCheckNeededBit = 1 << 4;
        public const byte IsConstructorBit = 1 << 5;
        public const byte IsExtendedMapBit = 1 << 6;
    }

    static class Bits2
    {
        public const byte NewTargetIsBaseBit = 1 << 0;
        public const byte IsImmutablePrototypeBit = 1 << 1;
        public const int ElementsKindShift = 2;
        public const byte ElementsKindMask = 0x3F << ElementsKindShift;
    }

    internal static class Bits3
    {
        public static class EnumLength
        {
            public const int Shift = 0;
            public const uint Mask = 0x3FFu;
            public static uint Encode(int v) => (uint)v & Mask;
            public static int Decode(uint f) => (int)(f & Mask);
        }

        public static class NumberOfOwnDescriptors
        {
            public const int Shift = 10;
            public const uint Mask = 0x3FFu << Shift;
            public static uint Encode(int v) => ((uint)v << Shift) & Mask;
            public static int Decode(uint f) => (int)((f & Mask) >> Shift);
        }

        public const uint IsPrototypeMapBit = 1u << 20;
        public const uint IsDictionaryMapBit = 1u << 21;
        public const uint OwnsDescriptorsBit = 1u << 22;
        public const uint IsInRetainedMapListBit = 1u << 23;
        public const uint IsDeprecatedBit = 1u << 24;
        public const uint IsUnstableBit = 1u << 25;
        public const uint IsMigrationTargetBit = 1u << 26;
        public const uint IsExtensibleBit = 1u << 27;
        public const uint MayHaveInterestingPropertiesBit = 1u << 28;
        public const int ConstructionCounterShift = 29;
        public const uint ConstructionCounterMask = 7u << ConstructionCounterShift;
    }

    bool GetBit1(byte bit) => (BitField & bit) != 0;
    void SetBit1(byte bit, bool value) => BitField = value ? (byte)(BitField | bit) : (byte)(BitField & ~bit);
    bool GetBit2(byte bit) => (BitField2 & bit) != 0;
    void SetBit2(byte bit, bool value) => BitField2 = value ? (byte)(BitField2 | bit) : (byte)(BitField2 & ~bit);
    bool GetBit3(uint bit) => (BitField3 & bit) != 0;
    void SetBit3(uint bit, bool value) => BitField3 = value ? BitField3 | bit : BitField3 & ~bit;

    public bool IsCallable { get => GetBit1(Bits1.IsCallableBit); set => SetBit1(Bits1.IsCallableBit, value); }
    public bool HasNamedInterceptor { get => GetBit1(Bits1.HasNamedInterceptorBit); set => SetBit1(Bits1.HasNamedInterceptorBit, value); }
    public bool HasIndexedInterceptor { get => GetBit1(Bits1.HasIndexedInterceptorBit); set => SetBit1(Bits1.HasIndexedInterceptorBit, value); }
    public bool IsUndetectable { get => GetBit1(Bits1.IsUndetectableBit); set => SetBit1(Bits1.IsUndetectableBit, value); }
    public bool IsAccessCheckNeeded { get => GetBit1(Bits1.IsAccessCheckNeededBit); set => SetBit1(Bits1.IsAccessCheckNeededBit, value); }
    public bool IsConstructor { get => GetBit1(Bits1.IsConstructorBit); set => SetBit1(Bits1.IsConstructorBit, value); }
    public bool IsExtendedMap { get => GetBit1(Bits1.IsExtendedMapBit); set => SetBit1(Bits1.IsExtendedMapBit, value); }

    public bool NewTargetIsBase { get => GetBit2(Bits2.NewTargetIsBaseBit); set => SetBit2(Bits2.NewTargetIsBaseBit, value); }
    public bool IsImmutableProto { get => GetBit2(Bits2.IsImmutablePrototypeBit); set => SetBit2(Bits2.IsImmutablePrototypeBit, value); }

    public bool OwnsDescriptors { get => GetBit3(Bits3.OwnsDescriptorsBit); set => SetBit3(Bits3.OwnsDescriptorsBit, value); }
    public bool IsDeprecated { get => GetBit3(Bits3.IsDeprecatedBit); set => SetBit3(Bits3.IsDeprecatedBit, value); }
    public bool IsInRetainedMapList { get => GetBit3(Bits3.IsInRetainedMapListBit); set => SetBit3(Bits3.IsInRetainedMapListBit, value); }
    public bool IsPrototypeMap { get => GetBit3(Bits3.IsPrototypeMapBit); set => SetBit3(Bits3.IsPrototypeMapBit, value); }
    public bool IsMigrationTarget { get => GetBit3(Bits3.IsMigrationTargetBit); set => SetBit3(Bits3.IsMigrationTargetBit, value); }
    public bool IsExtensible { get => GetBit3(Bits3.IsExtensibleBit); set => SetBit3(Bits3.IsExtensibleBit, value); }
    public bool MayHaveInterestingProperties { get => GetBit3(Bits3.MayHaveInterestingPropertiesBit); set => SetBit3(Bits3.MayHaveInterestingPropertiesBit, value); }

    /// <summary>Setting the dictionary bit also marks the map unstable (V8's set_is_dictionary_map).</summary>
    public bool IsDictionaryMap
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetBit3(Bits3.IsDictionaryMapBit);
        set
        {
            SetBit3(Bits3.IsDictionaryMapBit, value);
            SetBit3(Bits3.IsUnstableBit, value);
        }
    }

    public int ConstructionCounter
    {
        get => (int)((BitField3 & Bits3.ConstructionCounterMask) >> Bits3.ConstructionCounterShift);
        set => BitField3 = (BitField3 & ~Bits3.ConstructionCounterMask) | ((uint)value << Bits3.ConstructionCounterShift);
    }

    public bool IsStable => !GetBit3(Bits3.IsUnstableBit);
    public void MarkUnstable() => SetBit3(Bits3.IsUnstableBit, true);

    public bool IsAbandonedPrototypeMap => IsPrototypeMap && !OwnsDescriptors;

    // ---- Elements kind ------------------------------------------------------------

    public ElementsKind ElementsKind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ElementsKind)((BitField2 & Bits2.ElementsKindMask) >> Bits2.ElementsKindShift);
    }

    public void SetElementsKind(ElementsKind kind)
    {
        if ((int)kind >= ElementsKinds.kElementsKindCount) throw new ArgumentOutOfRangeException(nameof(kind));
        BitField2 = (byte)((BitField2 & ~Bits2.ElementsKindMask) | ((int)kind << Bits2.ElementsKindShift));
    }

    public bool HasFastSmiElements => ElementsKinds.IsSmiElementsKind(ElementsKind);
    public bool HasFastObjectElements => ElementsKinds.IsObjectElementsKind(ElementsKind);
    public bool HasFastSmiOrObjectElements => ElementsKinds.IsSmiOrObjectElementsKind(ElementsKind);
    public bool HasFastDoubleElements => ElementsKinds.IsDoubleElementsKind(ElementsKind);
    public bool HasFastElements => ElementsKinds.IsFastElementsKind(ElementsKind);
    public bool HasFastPackedElements => ElementsKinds.IsFastPackedElementsKind(ElementsKind);
    public bool HasSloppyArgumentsElements => ElementsKinds.IsSloppyArgumentsElementsKind(ElementsKind);
    public bool HasFastSloppyArgumentsElements => ElementsKind == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS;
    public bool HasFastStringWrapperElements => ElementsKind == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS;
    public bool HasTypedArrayOrRabGsabTypedArrayElements => ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(ElementsKind);
    public bool HasAnyTypedArrayOrWasmArrayElements => ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(ElementsKind);
    public bool HasDictionaryElements => ElementsKinds.IsDictionaryElementsKind(ElementsKind);
    public bool HasAnyNonextensibleElements => ElementsKinds.IsAnyNonextensibleElementsKind(ElementsKind);
    public bool HasNonextensibleElements => ElementsKinds.IsNonextensibleElementsKind(ElementsKind);
    public bool HasSealedElements => ElementsKinds.IsSealedElementsKind(ElementsKind);
    public bool HasFrozenElements => ElementsKinds.IsFrozenElementsKind(ElementsKind);
    public bool HasSharedArrayElements => ElementsKinds.IsSharedArrayElementsKind(ElementsKind);

    public static bool CanHaveFastTransitionableElementsKind(InstanceType instanceType) =>
        instanceType is InstanceType.JSArrayType or InstanceType.JSPrimitiveWrapperType or InstanceType.JSArgumentsObjectType;

    public bool CanHaveFastTransitionableElementsKind() => CanHaveFastTransitionableElementsKind(InstanceType);

    /// <summary>Map::GetInitialElements.</summary>
    public FixedArrayBase GetInitialElements()
    {
        if (HasFastElements || HasFastStringWrapperElements || HasAnyNonextensibleElements) return FixedArray.Empty;
        if (HasTypedArrayOrRabGsabTypedArrayElements) return ByteArray.Empty;
        if (HasDictionaryElements) return NumberDictionary.NewEmpty();
        throw new InvalidOperationException("unreachable");
    }

    // ---- Instance size and in-object properties ------------------------------------

    public int InstanceSizeInWords => _instanceSizeInWords;
    public int InstanceSize => _instanceSizeInWords * kTaggedSize;

    public void SetInstanceSize(int sizeInBytes)
    {
        int words = sizeInBytes / kTaggedSize;
        if (words > 255) throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        _instanceSizeInWords = words;
    }

    public int GetInObjectPropertiesStartInWords() => _inobjectPropertiesStartOrConstructorFunctionIndex;

    public void SetInObjectPropertiesStartInWords(int value) => _inobjectPropertiesStartOrConstructorFunctionIndex = value;

    /// <summary>Number of in-object property slots of instances.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetInObjectProperties() =>
        InstanceTypeChecks.IsJSObject(InstanceType) ? _instanceSizeInWords - _inobjectPropertiesStartOrConstructorFunctionIndex : 0;

    public bool IsFieldInObject(int fieldIndex) => fieldIndex < GetInObjectProperties();

    public int GetConstructorFunctionIndex() => _inobjectPropertiesStartOrConstructorFunctionIndex;

    public void SetConstructorFunctionIndex(int value) => _inobjectPropertiesStartOrConstructorFunctionIndex = value;

    public int GetInObjectPropertyOffset(int index) => (GetInObjectPropertiesStartInWords() + index) * kTaggedSize;

    public bool HasOutOfObjectProperties() => _usedOrUnusedInstanceSizeInWords < JSObject.kFieldsAdded;

    public int UnusedPropertyFields()
    {
        int value = _usedOrUnusedInstanceSizeInWords;
        return value >= JSObject.kFieldsAdded ? _instanceSizeInWords - value : value;
    }

    public int UnusedInObjectProperties()
    {
        int value = _usedOrUnusedInstanceSizeInWords;
        return value >= JSObject.kFieldsAdded ? _instanceSizeInWords - value : 0;
    }

    public int UsedInstanceSize()
    {
        int words = _usedOrUnusedInstanceSizeInWords;
        return words < JSObject.kFieldsAdded ? InstanceSize : words * kTaggedSize;
    }

    public void SetInObjectUnusedPropertyFields(int value)
    {
        if (!InstanceTypeChecks.IsJSObject(InstanceType))
        {
            if (value != 0) throw new InvalidOperationException();
            _usedOrUnusedInstanceSizeInWords = 0;
            return;
        }
        int usedInobjectProperties = GetInObjectProperties() - value;
        _usedOrUnusedInstanceSizeInWords = GetInObjectPropertyOffset(usedInobjectProperties) / kTaggedSize;
    }

    public void SetOutOfObjectUnusedPropertyFields(int value)
    {
        if ((uint)value >= JSObject.kFieldsAdded) throw new ArgumentOutOfRangeException(nameof(value));
        _usedOrUnusedInstanceSizeInWords = value;
    }

    public void CopyUnusedPropertyFields(Map map) => _usedOrUnusedInstanceSizeInWords = map._usedOrUnusedInstanceSizeInWords;

    public void CopyUnusedPropertyFieldsAdjustedForInstanceSize(Map map)
    {
        int value = map._usedOrUnusedInstanceSizeInWords;
        if (value >= JSObject.kFieldsAdded)
        {
            // Unused in-object fields. Adjust the offset from the object's start
            // so it matches the distance to the object's end.
            value += _instanceSizeInWords - map._instanceSizeInWords;
        }
        _usedOrUnusedInstanceSizeInWords = value;
    }

    public void AccountAddedPropertyField()
    {
        int value = _usedOrUnusedInstanceSizeInWords;
        if (value >= JSObject.kFieldsAdded)
        {
            if (value == _instanceSizeInWords)
            {
                AccountAddedOutOfObjectPropertyField(0);
            }
            else
            {
                // The property is added in-object, so simply increment the counter.
                _usedOrUnusedInstanceSizeInWords = value + 1;
            }
        }
        else
        {
            AccountAddedOutOfObjectPropertyField(value);
        }
    }

    public void AccountAddedOutOfObjectPropertyField(int unusedInPropertyArray)
    {
        unusedInPropertyArray--;
        if (unusedInPropertyArray < 0) unusedInPropertyArray += JSObject.kFieldsAdded;
        _usedOrUnusedInstanceSizeInWords = unusedInPropertyArray;
    }

    public int InstanceSizeFromSlack(int slack) => InstanceSize - slack * kTaggedSize;

    // ---- Slack tracking -----------------------------------------------------------

    public bool IsInobjectSlackTrackingInProgress() => ConstructionCounter != kNoSlackTracking;

    public void StartInobjectSlackTracking()
    {
        Debug.Assert(!IsInobjectSlackTrackingInProgress());
        if (UnusedPropertyFields() == 0) return;
        ConstructionCounter = kSlackTrackingCounterStart;
    }

    /// <summary>Map::InobjectSlackTrackingStep.</summary>
    public void InobjectSlackTrackingStep(Isolate isolate)
    {
        if (!IsInobjectSlackTrackingInProgress()) return;
        int counter = ConstructionCounter;
        ConstructionCounter = counter - 1;
        if (counter == kSlackTrackingCounterEnd)
        {
            MapUpdater.CompleteInobjectSlackTracking(isolate, this);
        }
    }

    /// <summary>Map::ComputeMinObjectSlack: the least number of unused fields across the transition tree.</summary>
    public int ComputeMinObjectSlack(Isolate isolate)
    {
        int slack = UnusedPropertyFields();
        new TransitionsAccessor(isolate, this).TraverseTransitionTree(map =>
        {
            slack = Math.Min(slack, map.UnusedPropertyFields());
        });
        return slack;
    }

    // ---- Descriptors ---------------------------------------------------------------

    public DescriptorArray InstanceDescriptors
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _instanceDescriptors;
    }

    public int NumberOfOwnDescriptors
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Bits3.NumberOfOwnDescriptors.Decode(BitField3);
    }

    public void SetNumberOfOwnDescriptors(int number)
    {
        if ((uint)number > kMaxNumberOfDescriptors) throw new ArgumentOutOfRangeException(nameof(number));
        Debug.Assert(number <= _instanceDescriptors.NumberOfDescriptors);
        BitField3 = (BitField3 & ~Bits3.NumberOfOwnDescriptors.Mask) | Bits3.NumberOfOwnDescriptors.Encode(number);
    }

    public int EnumLength => Bits3.EnumLength.Decode(BitField3);

    public void SetEnumLength(int length)
    {
        if (length != kInvalidEnumCacheSentinel)
        {
            Debug.Assert(length <= NumberOfOwnDescriptors);
        }
        BitField3 = (BitField3 & ~Bits3.EnumLength.Mask) | Bits3.EnumLength.Encode(length);
    }

    public void SetInstanceDescriptors(DescriptorArray descriptors, int numberOfOwnDescriptors)
    {
        _instanceDescriptors = descriptors;
        SetNumberOfOwnDescriptors(numberOfOwnDescriptors);
    }

    public void UpdateDescriptors(DescriptorArray descriptors, int numberOfOwnDescriptors) =>
        SetInstanceDescriptors(descriptors, numberOfOwnDescriptors);

    public void InitializeDescriptors(DescriptorArray descriptors) =>
        SetInstanceDescriptors(descriptors, descriptors.NumberOfDescriptors);

    public InternalIndex LastAdded()
    {
        int n = NumberOfOwnDescriptors;
        Debug.Assert(n > 0);
        return new InternalIndex(n - 1);
    }

    public Name GetLastDescriptorName() => _instanceDescriptors.GetKey(LastAdded());
    public PropertyDetails GetLastDescriptorDetails() => _instanceDescriptors.GetDetails(LastAdded());

    /// <summary>Map::AppendDescriptor: add to a map that owns all of its descriptors (bootstrapping).</summary>
    public void AppendDescriptor(Isolate isolate, Descriptor desc)
    {
        DescriptorArray descriptors = _instanceDescriptors;
        int numberOfOwnDescriptors = NumberOfOwnDescriptors;
        Debug.Assert(descriptors.NumberOfDescriptors == numberOfOwnDescriptors);
        descriptors.Append(desc);
        SetNumberOfOwnDescriptors(numberOfOwnDescriptors + 1);
        if (desc.Key.IsInteresting()) MayHaveInterestingProperties = true;
        if (desc.Details.Location == PropertyLocation.Field)
        {
            Debug.Assert(UnusedPropertyFields() > 0);
            AccountAddedPropertyField();
        }
    }

    public static int SlackForArraySize(int oldSize, int sizeLimit)
    {
        int max_slack = sizeLimit - oldSize;
        if (oldSize < 4) return Math.Min(max_slack, 1);
        return Math.Min(max_slack, oldSize / 4);
    }

    /// <summary>Map::EnsureDescriptorSlack.</summary>
    public static void EnsureDescriptorSlack(Isolate isolate, Map map, int slack)
    {
        // Only supports adding slack to owned descriptors.
        if (!map.OwnsDescriptors) throw new InvalidOperationException();
        DescriptorArray descriptors = map._instanceDescriptors;
        int oldSize = map.NumberOfOwnDescriptors;
        if (slack <= descriptors.NumberOfSlackDescriptors) return;
        DescriptorArray newDescriptors = DescriptorArray.CopyUpTo(descriptors, oldSize, slack);
        if (oldSize == 0)
        {
            map.UpdateDescriptors(newDescriptors, map.NumberOfOwnDescriptors);
            return;
        }
        newDescriptors.CopyEnumCacheFrom(descriptors);
        map.UpdateDescriptors(newDescriptors, map.NumberOfOwnDescriptors);
        HeapObject? next = map.GetBackPointer();
        if (next is null) return;
        Map current = (Map)next;
        while (ReferenceEquals(current._instanceDescriptors, descriptors))
        {
            next = current.GetBackPointer();
            if (next is null) break;
            current.UpdateDescriptors(newDescriptors, current.NumberOfOwnDescriptors);
            current = (Map)next;
        }
    }

    /// <summary>Map::ReplaceDescriptors: installs new_descriptors over all maps sharing the current ones.</summary>
    internal void ReplaceDescriptors(Isolate isolate, DescriptorArray newDescriptors)
    {
        // Don't overwrite the empty descriptor array or initial map's descriptors.
        if (NumberOfOwnDescriptors == 0 || GetBackPointer() is null) return;
        DescriptorArray toReplace = _instanceDescriptors;
        Map current = this;
        while (ReferenceEquals(current._instanceDescriptors, toReplace))
        {
            if (!current.TryGetBackPointer(out Map? next)) break;
            current.SetEnumLength(kInvalidEnumCacheSentinel);
            current.UpdateDescriptors(newDescriptors, current.NumberOfOwnDescriptors);
            current = next;
        }
        OwnsDescriptors = false;
    }

    public bool OnlyHasSimpleProperties() =>
        !ElementsKinds.IsStringWrapperElementsKind(ElementsKind) && !IsSpecialReceiverMap(this) && !IsDictionaryMap;

    public int NumberOfFields()
    {
        DescriptorArray descriptors = _instanceDescriptors;
        int result = 0;
        int n = NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            if (descriptors.GetDetails(i).Location == PropertyLocation.Field) result++;
        }
        return result;
    }

    public readonly record struct FieldCounts(int MutableCount, int ConstCount)
    {
        public int GetTotal() => MutableCount + ConstCount;
    }

    public FieldCounts GetFieldCounts()
    {
        DescriptorArray descriptors = _instanceDescriptors;
        int mutableCount = 0, constCount = 0;
        int n = NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            PropertyDetails details = descriptors.GetDetails(i);
            if (details.Location == PropertyLocation.Field)
            {
                if (details.Constness == PropertyConstness.Mutable) mutableCount++;
                else constCount++;
            }
        }
        return new FieldCounts(mutableCount, constCount);
    }

    public int NumberOfEnumerableProperties()
    {
        int result = 0;
        DescriptorArray descs = _instanceDescriptors;
        int n = NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            if ((descs.GetDetails(i).Attributes & PropertyAttributes.DONT_ENUM) == 0 &&
                !ObjectOps.FilterKey(descs.GetKey(i), PropertyFilter.ENUMERABLE_STRINGS))
            {
                result++;
            }
        }
        return result;
    }

    /// <summary>The next free field index (V8's NextFreeFieldStorageLocation).</summary>
    public int NextFreePropertyIndex()
    {
        int n = NumberOfOwnDescriptors;
        DescriptorArray descs = _instanceDescriptors;
        for (int i = n - 1; i >= 0; --i)
        {
            PropertyDetails details = descs.GetDetails(i);
            if (details.Location == PropertyLocation.Field) return details.FieldIndex + 1;
        }
        return 0;
    }

    public bool InstancesNeedRewriting(Map target) =>
        InstancesNeedRewriting(target, target.NumberOfFields(), target.GetInObjectProperties(),
            target.UnusedPropertyFields(), out _);

    public bool InstancesNeedRewriting(Map target, int targetNumberOfFields, int targetInobject, int targetUnused, out int oldNumberOfFields)
    {
        // If fields were added (or removed), rewrite the instance.
        oldNumberOfFields = NumberOfFields();
        if (targetNumberOfFields != oldNumberOfFields) return true;

        // If smi descriptors were replaced by double descriptors, rewrite.
        DescriptorArray oldDesc = _instanceDescriptors;
        DescriptorArray newDesc = target._instanceDescriptors;
        int n = NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            if (newDesc.GetDetails(i).Representation.IsDouble != oldDesc.GetDetails(i).Representation.IsDouble) return true;
        }

        // If no fields were added, and no inobject properties were removed, setting
        // the map is sufficient.
        if (targetInobject == GetInObjectProperties()) return false;
        // In-object slack tracking may have reduced the object size of the new map.
        // In that case, succeed if all existing fields were inobject, and they still
        // fit within the new inobject size.
        if (targetNumberOfFields <= targetInobject) return false;
        // Otherwise, properties will need to be moved to the backing store.
        return true;
    }

    public static bool IsMostGeneralFieldType(Representation representation, HeapObject fieldType) =>
        !representation.IsHeapObject || FieldType.IsAny(fieldType);

    public static void GeneralizeIfCanHaveTransitionableFastElementsKind(InstanceType instanceType,
        ref Representation representation, ref HeapObject fieldType)
    {
        if (CanHaveFastTransitionableElementsKind(instanceType))
        {
            // We don't support propagation of field generalization through elements
            // kind transitions because they are inserted into the transition tree
            // before field transitions. In order to avoid complexity of handling
            // such a case we ensure that all maps with transitionable elements kinds
            // have the most general field representation and type.
            fieldType = FieldType.Any;
            representation = Representation.Tagged;
        }
    }

    public bool TooManyFastProperties(StoreOrigin storeOrigin)
    {
        if (UnusedPropertyFields() != 0) return false;
        if (storeOrigin != StoreOrigin.MaybeKeyed) return false;
        if (IsPrototypeMap) return false;
        int limit = Math.Max(Isolate.CurrentFlags.fast_properties_soft_limit, GetInObjectProperties());
        int external = NumberOfFields() - GetInObjectProperties();
        return external > limit;
    }

    public bool CanBeDeprecated()
    {
        int n = NumberOfOwnDescriptors;
        DescriptorArray d = _instanceDescriptors;
        for (int i = 0; i < n; i++)
        {
            PropertyDetails details = d.GetDetails(i);
            if (details.Representation.MightCauseMapDeprecation()) return true;
            if (details.Kind == PropertyKind.Data && details.Location == PropertyLocation.Descriptor) return true;
        }
        return false;
    }

    public void NotifyLeafMapLayoutChange(Isolate isolate)
    {
        if (IsStable)
        {
            MarkUnstable();
            DependentCode.DeoptimizeDependencyGroups(isolate, this, DependentCode.DependencyGroups.PrototypeCheck);
        }
    }

    public bool CanTransition() => InstanceTypeChecks.IsJSObject(InstanceType);

    // ---- Constructor and back pointer --------------------------------------------------

    /// <summary>The back pointer, or null (V8: undefined) for root maps.</summary>
    public HeapObject? GetBackPointer() => ConstructorOrBackPointer as Map;

    public bool TryGetBackPointer(out Map backPointer)
    {
        if (ConstructorOrBackPointer is Map m)
        {
            backPointer = m;
            return true;
        }
        backPointer = null!;
        return false;
    }

    public void SetBackPointer(Map value)
    {
        Debug.Assert(ConstructorOrBackPointer is not Map || ReferenceEquals(ConstructorOrBackPointer, value) ||
                     ReferenceEquals(value.GetConstructorRaw(), GetConstructorRaw()));
        ConstructorOrBackPointer = value;
    }

    public HeapObject? GetConstructorRaw()
    {
        HeapObject? maybeConstructor = ConstructorOrBackPointer;
        // Follow any back pointers.
        while (maybeConstructor is Map m) maybeConstructor = m.ConstructorOrBackPointer;
        return maybeConstructor;
    }

    /// <summary>The constructor function, or null (V8: null/undefined).</summary>
    public HeapObject? GetConstructor() => GetConstructorRaw();

    public void SetConstructor(HeapObject? constructor)
    {
        Debug.Assert(ConstructorOrBackPointer is not Map);
        ConstructorOrBackPointer = constructor;
    }

    public HeapObject? TryGetConstructor(int maxSteps)
    {
        HeapObject? maybeConstructor = ConstructorOrBackPointer;
        while (maybeConstructor is Map m)
        {
            if (maxSteps-- == 0) return null;
            maybeConstructor = m.ConstructorOrBackPointer;
        }
        return maybeConstructor;
    }

    public Map FindRootMap()
    {
        Map result = this;
        while (result.TryGetBackPointer(out Map parent)) result = parent;
        return result;
    }

    public Map FindFieldOwner(InternalIndex descriptor)
    {
        Map result = this;
        while (true)
        {
            if (!result.TryGetBackPointer(out Map parent)) break;
            if (parent.NumberOfOwnDescriptors <= descriptor.AsInt) break;
            result = parent;
        }
        return result;
    }

    /// <summary>
    /// Map::IsDetached: prototype maps and JS_OBJECT_TYPE maps that have
    /// descriptors but no back pointer are not part of a transition tree.
    /// </summary>
    public bool IsDetached(Isolate isolate)
    {
        if (IsPrototypeMap) return true;
        return InstanceType == InstanceType.JSObjectType && NumberOfOwnDescriptors > 0 && GetBackPointer() is null;
    }

    // ---- Prototype info --------------------------------------------------------------

    public bool HasPrototypeInfo
    {
        get
        {
            Debug.Assert(IsPrototypeMap);
            return RawTransitions is PrototypeInfo;
        }
    }

    public PrototypeInfo? PrototypeInfo => RawTransitions as PrototypeInfo;

    public bool TryGetPrototypeInfo(out PrototypeInfo info)
    {
        if (RawTransitions is PrototypeInfo p)
        {
            info = p;
            return true;
        }
        info = null!;
        return false;
    }

    public bool ShouldBeFastPrototypeMap => HasPrototypeInfo && ((PrototypeInfo)RawTransitions!).ShouldBeFastMap;

    public static PrototypeInfo GetOrCreatePrototypeInfo(JSReceiver prototype, Isolate isolate)
    {
        if (prototype.Map.TryGetPrototypeInfo(out PrototypeInfo info)) return info;
        var protoInfo = new PrototypeInfo();
        prototype.Map.RawTransitions = protoInfo;
        return protoInfo;
    }

    public static PrototypeInfo GetOrCreatePrototypeInfo(Map prototypeMap, Isolate isolate)
    {
        if (prototypeMap.RawTransitions is PrototypeInfo info) return info;
        var protoInfo = new PrototypeInfo();
        prototypeMap.RawTransitions = protoInfo;
        return protoInfo;
    }

    public static void SetShouldBeFastPrototypeMap(Map map, bool value, Isolate isolate)
    {
        Debug.Assert(map.IsPrototypeMap);
        if (!value && !map.HasPrototypeInfo) return;
        GetOrCreatePrototypeInfo(map, isolate).ShouldBeFastMap = value;
    }

    public bool IsPrototypeValidityCellValid()
    {
        Cell? cell = PrototypeValidityCell;
        return cell is not null && !cell.Value.IsIdenticalTo(Cell.kPrototypeChainInvalid);
    }

    /// <summary>Map::GetPrototypeChainRootMap: primitives use their wrapper's initial map.</summary>
    public Map GetPrototypeChainRootMap(Isolate isolate) => this;

    public static bool TryGetValidityCellHolderMap(Map map, Isolate isolate, out Map holder)
    {
        if (map.IsPrototypeMap)
        {
            holder = map;
            return true;
        }
        JSReceiver? maybePrototype = map.GetPrototypeChainRootMap(isolate).Prototype;
        if (maybePrototype is null || !JSObject.IsAnyObjectThatCanBeTrackedAsPrototype(maybePrototype))
        {
            holder = null!;
            return false;
        }
        holder = maybePrototype.Map;
        return true;
    }

    /// <summary>Map::GetOrCreatePrototypeChainValidityCell. Returns null for "no validity cell".</summary>
    public static Cell? GetOrCreatePrototypeChainValidityCell(Map map, Isolate isolate)
    {
        if (!TryGetValidityCellHolderMap(map, isolate, out Map holder)) return null;
        // Ensure the prototype is registered with its own prototypes so its cell
        // will be invalidated when necessary.
        JSObject.LazyRegisterPrototypeUser(holder, isolate);
        Cell? maybeCell = holder.PrototypeValidityCell;
        if (maybeCell is not null && !maybeCell.Value.IsIdenticalTo(Cell.kPrototypeChainInvalid)) return maybeCell;
        // Otherwise create a new cell.
        var cell = new Cell(Cell.kPrototypeChainValid);
        holder.PrototypeValidityCell = cell;
        return cell;
    }

    /// <summary>Map::SetPrototype.</summary>
    public static void SetPrototype(Isolate isolate, Map map, JSReceiver? prototype, bool enablePrototypeSetupMode = true)
    {
        if (prototype is JSObject protoObj && JSObject.IsJSObjectThatCanBeTrackedAsPrototype(protoObj))
        {
            JSObject.OptimizeAsPrototype(isolate, protoObj, enablePrototypeSetupMode);
        }
        map.Prototype = prototype;
    }

    // ---- Special receiver predicates (map-inl.h) -------------------------------------

    /// <summary>
    /// IsSpecialReceiverMap: receivers whose property lookup is not the ordinary
    /// one (proxies, globals, module namespaces, access-checked/interceptor maps).
    /// </summary>
    public static bool IsSpecialReceiverMap(Map map)
    {
        bool result = map.InstanceType <= InstanceType.JSSpecialApiObjectType ||
                      map.InstanceType == InstanceType.JSModuleNamespaceType;
        return result || map.HasNamedInterceptor || map.IsAccessCheckNeeded;
    }

    /// <summary>IsCustomElementsReceiverMap: receivers with non-ordinary element access.</summary>
    public static bool IsCustomElementsReceiverMap(Map map) =>
        map.InstanceType <= InstanceType.JSPrimitiveWrapperType || IsSpecialReceiverMap(map) || map.HasIndexedInterceptor;

    public static bool IsJSGlobalObjectMap(Map map) => map.InstanceType == InstanceType.JSGlobalObjectType;
    public static bool IsJSGlobalProxyMap(Map map) => map.InstanceType == InstanceType.JSGlobalProxyType;
    public static bool IsJSProxyMap(Map map) => map.InstanceType == InstanceType.JSProxyType;
    public static bool IsJSObjectMap(Map map) => InstanceTypeChecks.IsJSObject(map.InstanceType);
    public static bool IsJSReceiverMap(Map map) => InstanceTypeChecks.IsJSReceiver(map.InstanceType);
    public static bool IsJSArrayMap(Map map) => map.InstanceType == InstanceType.JSArrayType;
    public static bool IsJSFunctionMap(Map map) => InstanceTypeChecks.IsJSFunction(map.InstanceType);
    public static bool IsJSModuleNamespaceMap(Map map) => map.InstanceType == InstanceType.JSModuleNamespaceType;
    public static bool IsJSTypedArrayMap(Map map) => map.InstanceType == InstanceType.JSTypedArrayType;

    // ---- Static operations (map.cc) --------------------------------------------------

    static Map RawCopy(Isolate isolate, Map src, int instanceSize, int inobjectProperties)
    {
        var result = new Map(src.InstanceType, instanceSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, inobjectProperties)
        {
            ConstructorOrBackPointer = src.GetConstructorRaw(),
            BitField = src.BitField,
            BitField2 = src.BitField2,
            NativeContext = src.NativeContext,
        };
        uint newBitField3 = src.BitField3;
        newBitField3 |= Bits3.OwnsDescriptorsBit;
        newBitField3 = (newBitField3 & ~Bits3.NumberOfOwnDescriptors.Mask) | Bits3.NumberOfOwnDescriptors.Encode(0);
        newBitField3 = (newBitField3 & ~Bits3.EnumLength.Mask) | Bits3.EnumLength.Encode(kInvalidEnumCacheSentinel);
        newBitField3 &= ~Bits3.IsDeprecatedBit;
        newBitField3 &= ~Bits3.IsInRetainedMapListBit;
        if (!src.IsDictionaryMap) newBitField3 &= ~Bits3.IsUnstableBit;
        result.BitField3 = newBitField3;
        SetPrototype(isolate, result, src.Prototype);
        return result;
    }

    /// <summary>Map::Normalize: a dictionary-mode copy of <paramref name="fastMap"/>.</summary>
    public static Map Normalize(Isolate isolate, Map fastMap, InstanceType newInstanceType, ElementsKind newElementsKind,
        JSReceiver? newPrototype, bool hasNewPrototype, PropertyNormalizationMode mode, bool useCache, string reason)
    {
        Debug.Assert(!fastMap.IsDictionaryMap);
        if (fastMap.IsPrototypeMap) useCache = false;
        if (fastMap.InstanceType != newInstanceType) useCache = false;
        NormalizedMapCache? cache = null;
        if (useCache)
        {
            cache = fastMap.NativeContext?.NormalizedMapCache;
            useCache = cache is not null;
        }

        Map? newMap;
        JSReceiver? prototype = hasNewPrototype ? newPrototype : fastMap.Prototype;
        if (useCache && (newMap = cache!.Get(isolate, fastMap, newElementsKind, prototype, mode)) is not null)
        {
            // Cached.
        }
        else
        {
            newMap = CopyNormalized(isolate, fastMap, mode);
            newMap.InstanceType = newInstanceType;
            newMap.SetElementsKind(newElementsKind);
            if (hasNewPrototype)
            {
                SetPrototype(isolate, newMap, newPrototype);
            }
            if (useCache) cache!.Set(isolate, fastMap, newMap);
        }
        fastMap.NotifyLeafMapLayoutChange(isolate);
        return newMap;
    }

    public static Map Normalize(Isolate isolate, Map fastMap, ElementsKind newElementsKind, JSReceiver? newPrototype,
        bool hasNewPrototype, PropertyNormalizationMode mode, string reason) =>
        Normalize(isolate, fastMap, fastMap.InstanceType, newElementsKind, newPrototype, hasNewPrototype, mode, true, reason);

    public static Map Normalize(Isolate isolate, Map fastMap, PropertyNormalizationMode mode, string reason) =>
        Normalize(isolate, fastMap, fastMap.InstanceType, fastMap.ElementsKind, null, false, mode, true, reason);

    public static Map Normalize(Isolate isolate, Map fastMap, PropertyNormalizationMode mode, bool useCache, string reason) =>
        Normalize(isolate, fastMap, fastMap.InstanceType, fastMap.ElementsKind, null, false, mode, useCache, reason);

    static Map CopyNormalized(Isolate isolate, Map map, PropertyNormalizationMode mode)
    {
        int newInstanceSize = map.InstanceSize;
        if (mode == PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES)
        {
            newInstanceSize -= map.GetInObjectProperties() * kTaggedSize;
        }
        Map result = RawCopy(isolate, map, newInstanceSize,
            mode == PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES ? 0 : map.GetInObjectProperties());
        // Clear the unused_property_fields explicitly as this field should not
        // be accessed for normalized maps.
        result.SetInObjectUnusedPropertyFields(0);
        result.IsDictionaryMap = true;
        result.IsMigrationTarget = false;
        result.MayHaveInterestingProperties = true;
        result.ConstructionCounter = kNoSlackTracking;
        return result;
    }

    /// <summary>An immutable-prototype exotic version of the map (global objects).</summary>
    public static Map TransitionToImmutableProto(Isolate isolate, Map map)
    {
        Map newMap = Copy(isolate, map, "ImmutablePrototype");
        newMap.IsImmutableProto = true;
        return newMap;
    }

    public static Map CopyInitialMapNormalized(Isolate isolate, Map map,
        PropertyNormalizationMode mode = PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES) =>
        CopyNormalized(isolate, map, mode);

    public static Map CopyInitialMap(Isolate isolate, Map map) =>
        CopyInitialMap(isolate, map, map.InstanceSize, map.GetInObjectProperties(), map.UnusedPropertyFields());

    public static Map CopyInitialMap(Isolate isolate, Map map, int instanceSize, int inobjectProperties, int unusedPropertyFields)
    {
        Map result = RawCopy(isolate, map, instanceSize, inobjectProperties);
        // Please note instance_type and InstanceSize are set when allocated.
        result.SetInObjectUnusedPropertyFields(unusedPropertyFields);
        int numberOfOwnDescriptors = map.NumberOfOwnDescriptors;
        if (numberOfOwnDescriptors > 0)
        {
            // The copy will use the same descriptors array without ownership.
            result.OwnsDescriptors = false;
            result.UpdateDescriptors(map._instanceDescriptors, numberOfOwnDescriptors);
        }
        return result;
    }

    public static Map CopyDropDescriptors(Isolate isolate, Map map)
    {
        Map result = RawCopy(isolate, map, map.InstanceSize, IsJSObjectMap(map) ? map.GetInObjectProperties() : 0);
        // Please note instance_type and InstanceSize are set when allocated.
        if (IsJSObjectMap(map)) result.CopyUnusedPropertyFields(map);
        map.NotifyLeafMapLayoutChange(isolate);
        return result;
    }

    static Map ShareDescriptor(Isolate isolate, Map map, DescriptorArray descriptors, Descriptor descriptor)
    {
        // Sanity check. This path is only to be taken if the map owns its descriptor
        // array, implying that its NumberOfOwnDescriptors equals the number of
        // descriptors in the descriptor array.
        Debug.Assert(map.NumberOfOwnDescriptors == map._instanceDescriptors.NumberOfDescriptors);
        Map result = CopyDropDescriptors(isolate, map);
        Name name = descriptor.Key;
        if (name.IsInteresting()) result.MayHaveInterestingProperties = true;

        // Ensure there's space for the new descriptor in the shared descriptor array.
        if (descriptors.NumberOfSlackDescriptors == 0)
        {
            int oldSize = descriptors.NumberOfDescriptors;
            if (oldSize == 0)
            {
                descriptors = DescriptorArray.Allocate(0, 1);
            }
            else
            {
                int slack = SlackForArraySize(oldSize, kMaxNumberOfDescriptors);
                EnsureDescriptorSlack(isolate, map, slack);
                descriptors = map._instanceDescriptors;
            }
        }
        descriptors.Append(descriptor);
        result.InitializeDescriptors(descriptors);
        Debug.Assert(result.NumberOfOwnDescriptors == map.NumberOfOwnDescriptors + 1);
        ConnectTransition(isolate, map, result, name, TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION);
        return result;
    }

    static void ConnectTransition(Isolate isolate, Map parent, Map child, Name name, TransitionKindFlag transitionKind,
        bool forceConnect = false)
    {
        if (parent.GetBackPointer() is not null)
        {
            parent.OwnsDescriptors = false;
        }
        if (parent.IsDetached(isolate) && !forceConnect)
        {
            // The parent is not part of a transition tree: nothing to record.
        }
        else
        {
            TransitionsAccessor.Insert(isolate, parent, name, child, transitionKind);
        }
    }

    static Map CopyReplaceDescriptors(Isolate isolate, Map map, DescriptorArray descriptors, TransitionFlag flag,
        Name? maybeName, string reason, TransitionKindFlag transitionKind) =>
        CopyReplaceDescriptors(isolate, map, descriptors, flag, null, maybeName, reason, transitionKind);

    static Map CopyReplaceDescriptors(Isolate isolate, Map map, DescriptorArray descriptors, TransitionFlag flag,
        Action<Map>? initMap, Name? maybeName, string reason, TransitionKindFlag transitionKind)
    {
        Map result = CopyDropDescriptors(isolate, map);
        if (maybeName is not null && maybeName.IsInteresting()) result.MayHaveInterestingProperties = true;

        bool insertTransition = false;
        if (map.IsPrototypeMap)
        {
            result.InitializeDescriptors(descriptors);
        }
        else
        {
            if (flag == TransitionFlag.INSERT_TRANSITION && TransitionsAccessor.CanHaveMoreTransitions(isolate, map))
            {
                insertTransition = true;
                result.InitializeDescriptors(descriptors);
            }
            else if (transitionKind == TransitionKindFlag.PROTOTYPE_TRANSITION || isolate.BootstrapperActive)
            {
                // Prototype transitions are always between root maps. UpdatePrototype
                // uses the MapUpdater and instance migration. Thus, field generalization
                // is allowed to happen lazily.
                result.InitializeDescriptors(descriptors);
            }
            else
            {
                descriptors.GeneralizeAllFields();
                result.InitializeDescriptors(descriptors);
            }
        }
        initMap?.Invoke(result);
        if (insertTransition)
        {
            ConnectTransition(isolate, map, result, maybeName!, transitionKind);
        }
        return result;
    }

    /// <summary>
    /// Map::AddMissingTransitions: builds the transition tree from
    /// <paramref name="splitMap"/> adding descriptors [split_nof, nof).
    /// </summary>
    internal static Map AddMissingTransitions(Isolate isolate, Map splitMap, DescriptorArray descriptors)
    {
        int splitNof = splitMap.NumberOfOwnDescriptors;
        int nofDescriptors = descriptors.NumberOfDescriptors;
        if (splitNof >= nofDescriptors) throw new InvalidOperationException();

        // Start with creating last map which will own full descriptors array.
        Map lastMap = CopyDropDescriptors(isolate, splitMap);
        lastMap.SetInObjectUnusedPropertyFields(0);
        lastMap.InitializeDescriptors(descriptors);
        lastMap.MayHaveInterestingProperties = true;

        Map map = splitMap;
        for (int i = splitNof; i < nofDescriptors - 1; i++)
        {
            Map newMap = CopyDropDescriptors(isolate, map);
            InstallDescriptors(isolate, map, newMap, new InternalIndex(i), descriptors, forceConnect: true);
            map = newMap;
        }
        map.NotifyLeafMapLayoutChange(isolate);
        lastMap.MayHaveInterestingProperties = false;
        InstallDescriptors(isolate, map, lastMap, new InternalIndex(nofDescriptors - 1), descriptors);
        return lastMap;
    }

    public static Map AddMissingTransitionsForTesting(Isolate isolate, Map splitMap, DescriptorArray descriptors) =>
        AddMissingTransitions(isolate, splitMap, descriptors);

    static void InstallDescriptors(Isolate isolate, Map parent, Map child, InternalIndex newDescriptor,
        DescriptorArray descriptors, bool forceConnect = false)
    {
        child.SetInstanceDescriptors(descriptors, newDescriptor.AsInt + 1);
        child.CopyUnusedPropertyFields(parent);
        PropertyDetails details = descriptors.GetDetails(newDescriptor);
        if (details.Location == PropertyLocation.Field) child.AccountAddedPropertyField();
        Name name = descriptors.GetKey(newDescriptor);
        if (parent.MayHaveInterestingProperties || name.IsInteresting()) child.MayHaveInterestingProperties = true;
        ConnectTransition(isolate, parent, child, name, TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION, forceConnect);
    }

    public static Map AsDetachedTypedArray(Isolate isolate, Map map)
    {
        Map? newMap = TransitionsAccessor.SearchSpecial(isolate, map, ReadOnlyRoots.detached_symbol);
        if (newMap is null)
        {
            newMap = Copy(isolate, map, "detached TypedArray");
            if (TransitionsAccessor.CanHaveMoreTransitions(isolate, map))
            {
                ConnectTransition(isolate, map, newMap, ReadOnlyRoots.detached_symbol, TransitionKindFlag.SPECIAL_TRANSITION);
            }
            map.NotifyLeafMapLayoutChange(isolate);
        }
        return newMap;
    }

    public static Map CopyAsElementsKind(Isolate isolate, Map map, ElementsKind kind, TransitionFlag flag)
    {
        Map? maybeElementsTransitionMap = null;
        if (flag == TransitionFlag.INSERT_TRANSITION)
        {
            maybeElementsTransitionMap = map.ElementsTransitionMap(isolate);
        }
        bool insertTransition = flag == TransitionFlag.INSERT_TRANSITION &&
                                TransitionsAccessor.CanHaveMoreTransitions(isolate, map) &&
                                maybeElementsTransitionMap is null;
        if (insertTransition)
        {
            Map newMap = CopyForElementsTransition(isolate, map);
            newMap.SetElementsKind(kind);
            ConnectTransition(isolate, map, newMap, ReadOnlyRoots.elements_transition_symbol, TransitionKindFlag.SPECIAL_TRANSITION);
            return newMap;
        }
        // Create a new free-floating map only if we are not allowed to store it.
        Map copy = Copy(isolate, map, "CopyAsElementsKind");
        copy.SetElementsKind(kind);
        return copy;
    }

    /// <summary>The map reached by this map's elements-kind transition, if any.</summary>
    public Map? ElementsTransitionMap(Isolate isolate) =>
        new TransitionsAccessor(isolate, this).SearchSpecial(ReadOnlyRoots.elements_transition_symbol);

    public static Map CopyForElementsTransition(Isolate isolate, Map map)
    {
        Map newMap = CopyDropDescriptors(isolate, map);
        if (map.OwnsDescriptors)
        {
            // In case the map owned its own descriptors, share the descriptors and
            // transfer ownership to the new map.
            map.OwnsDescriptors = false;
            newMap.InitializeDescriptors(map._instanceDescriptors);
        }
        else
        {
            // In case the map did not own its own descriptors, a split is forced by
            // copying the map; creating a new descriptor array cell.
            DescriptorArray newDescriptors = DescriptorArray.CopyUpTo(map._instanceDescriptors, map.NumberOfOwnDescriptors);
            newMap.InitializeDescriptors(newDescriptors);
        }
        return newMap;
    }

    public static Map CopyForPrototypeTransition(Isolate isolate, Map map, JSReceiver? prototype)
    {
        Map newMap = Copy(isolate, map, "TransitionToPrototype", TransitionKindFlag.PROTOTYPE_TRANSITION);
        SetPrototype(isolate, newMap, prototype);
        return newMap;
    }

    public static Map Copy(Isolate isolate, Map map, string reason, TransitionKindFlag kind = TransitionKindFlag.SPECIAL_TRANSITION)
    {
        DescriptorArray newDescriptors = DescriptorArray.CopyUpTo(map._instanceDescriptors, map.NumberOfOwnDescriptors);
        return CopyReplaceDescriptors(isolate, map, newDescriptors, TransitionFlag.OMIT_TRANSITION, null, reason, kind);
    }

    /// <summary>Map::Create: a copy of Object's initial map with <paramref name="inobjectProperties"/> slots.</summary>
    public static Map Create(Isolate isolate, int inobjectProperties)
    {
        Map copy = Copy(isolate, isolate.NativeContext.ObjectFunction.InitialMap, "MapCreate");
        if (inobjectProperties > JSObject.kMaxInObjectProperties) inobjectProperties = JSObject.kMaxInObjectProperties;
        int newInstanceSize = JSObject.kHeaderSize + kTaggedSize * inobjectProperties;
        copy.SetInstanceSize(newInstanceSize);
        copy.SetInObjectPropertiesStartInWords(JSObject.kHeaderSize / kTaggedSize);
        copy.SetInObjectUnusedPropertyFields(inobjectProperties);
        return copy;
    }

    /// <summary>Map::CopyForPreventExtensions.</summary>
    public static Map CopyForPreventExtensions(Isolate isolate, Map map, PropertyAttributes attrsToAdd, Symbol transitionMarker,
        string reason, bool oldMapIsDictionaryElementsKind = false)
    {
        int numDescriptors = map.NumberOfOwnDescriptors;
        DescriptorArray newDesc = DescriptorArray.CopyUpToAddAttributes(map._instanceDescriptors, numDescriptors, attrsToAdd);

        void InitMap(Map newMap)
        {
            newMap.IsExtensible = false;
            if (!ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(map.ElementsKind))
            {
                ElementsKind newKind = ElementsKinds.IsStringWrapperElementsKind(map.ElementsKind)
                    ? ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS
                    : ElementsKind.DICTIONARY_ELEMENTS;
                if (!oldMapIsDictionaryElementsKind)
                {
                    switch (map.ElementsKind)
                    {
                        case ElementsKind.PACKED_ELEMENTS:
                            newKind = attrsToAdd == PropertyAttributes.SEALED ? ElementsKind.PACKED_SEALED_ELEMENTS
                                : attrsToAdd == PropertyAttributes.FROZEN ? ElementsKind.PACKED_FROZEN_ELEMENTS
                                : ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS;
                            break;
                        case ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS:
                            if (attrsToAdd == PropertyAttributes.SEALED) newKind = ElementsKind.PACKED_SEALED_ELEMENTS;
                            else if (attrsToAdd == PropertyAttributes.FROZEN) newKind = ElementsKind.PACKED_FROZEN_ELEMENTS;
                            break;
                        case ElementsKind.PACKED_SEALED_ELEMENTS:
                            if (attrsToAdd == PropertyAttributes.FROZEN) newKind = ElementsKind.PACKED_FROZEN_ELEMENTS;
                            break;
                        case ElementsKind.HOLEY_ELEMENTS:
                            newKind = attrsToAdd == PropertyAttributes.SEALED ? ElementsKind.HOLEY_SEALED_ELEMENTS
                                : attrsToAdd == PropertyAttributes.FROZEN ? ElementsKind.HOLEY_FROZEN_ELEMENTS
                                : ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS;
                            break;
                        case ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS:
                            if (attrsToAdd == PropertyAttributes.SEALED) newKind = ElementsKind.HOLEY_SEALED_ELEMENTS;
                            else if (attrsToAdd == PropertyAttributes.FROZEN) newKind = ElementsKind.HOLEY_FROZEN_ELEMENTS;
                            break;
                        case ElementsKind.HOLEY_SEALED_ELEMENTS:
                            if (attrsToAdd == PropertyAttributes.FROZEN) newKind = ElementsKind.HOLEY_FROZEN_ELEMENTS;
                            break;
                    }
                }
                newMap.SetElementsKind(newKind);
            }
        }

        // Do not track transitions during bootstrapping.
        TransitionFlag flag = isolate.BootstrapperActive ? TransitionFlag.OMIT_TRANSITION : TransitionFlag.INSERT_TRANSITION;
        return CopyReplaceDescriptors(isolate, map, newDesc, flag, InitMap, transitionMarker, reason, TransitionKindFlag.SPECIAL_TRANSITION);
    }

    static bool CanHoldValue(DescriptorArray descriptors, InternalIndex descriptor, PropertyConstness constness, in JSValue value)
    {
        PropertyDetails details = descriptors.GetDetails(descriptor);
        if (details.Location == PropertyLocation.Field)
        {
            if (details.Kind == PropertyKind.Data)
            {
                if (!PropertyDetailsHelpers.IsGeneralizableTo(constness, details.Constness)) return false;
                if (ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized)) return true;
                return ObjectOps.FitsRepresentation(value, details.Representation) &&
                       FieldType.NowContains(descriptors.GetFieldType(descriptor), value);
            }
            return false;
        }
        return false;
    }

    static Map UpdateDescriptorForValue(Isolate isolate, Map map, InternalIndex descriptor, PropertyConstness constness, JSValue value)
    {
        if (CanHoldValue(map._instanceDescriptors, descriptor, constness, value)) return map;
        PropertyAttributes attributes = map._instanceDescriptors.GetDetails(descriptor).Attributes;
        (Representation representation, constness) = ObjectOps.OptimalRepresentation(value, constness);
        HeapObject type = ObjectOps.OptimalType(value, isolate, representation);
        var mu = new MapUpdater(isolate, map);
        return mu.ReconfigureToDataField(descriptor, attributes, constness, representation, type);
    }

    /// <summary>Map::PrepareForDataProperty: a map whose field can hold <paramref name="value"/>.</summary>
    public static Map PrepareForDataProperty(Isolate isolate, Map map, InternalIndex descriptor, PropertyConstness constness, JSValue value)
    {
        Debug.Assert(!map.IsDeprecated);
        Debug.Assert(!map.IsDictionaryMap);
        return UpdateDescriptorForValue(isolate, map, descriptor, constness, value);
    }

    public static Map? CopyWithField(Isolate isolate, Map map, Name name, HeapObject type, PropertyAttributes attributes,
        PropertyConstness constness, Representation representation, TransitionFlag flag)
    {
        Debug.Assert(map._instanceDescriptors.Search(name, map.NumberOfOwnDescriptors).IsNotFound);
        // Ensure the descriptor array does not get too big.
        if (map.NumberOfOwnDescriptors >= kMaxNumberOfDescriptors) return null;

        // Compute the new index for new field.
        int index = map.NextFreePropertyIndex();

        if (map.InstanceType == InstanceType.JSContextExtensionObjectType)
        {
            constness = PropertyConstness.Mutable;
            representation = Representation.Tagged;
            type = FieldType.Any;
        }
        else
        {
            GeneralizeIfCanHaveTransitionableFastElementsKind(map.InstanceType, ref representation, ref type);
        }
        Descriptor d = Descriptor.DataField(name, index, attributes, constness, representation, type);
        Map newMap = CopyAddDescriptor(isolate, map, d, flag);
        newMap.AccountAddedPropertyField();
        return newMap;
    }

    public static Map? CopyWithConstant(Isolate isolate, Map map, Name name, JSValue constant, PropertyAttributes attributes, TransitionFlag flag)
    {
        // Ensure the descriptor array does not get too big.
        if (map.NumberOfOwnDescriptors >= kMaxNumberOfDescriptors) return null;
        (Representation representation, PropertyConstness constness) = ObjectOps.OptimalRepresentation(constant, PropertyConstness.Const);
        HeapObject type = ObjectOps.OptimalType(constant, isolate, representation);
        return CopyWithField(isolate, map, name, type, attributes, constness, representation, flag);
    }

    /// <summary>Map::TransitionToDataProperty.</summary>
    public static Map TransitionToDataProperty(Isolate isolate, Map map, Name name, JSValue value, PropertyAttributes attributes,
        PropertyConstness constness, StoreOrigin storeOrigin)
    {
        Debug.Assert(name.IsUniqueName);
        Debug.Assert(!map.IsDictionaryMap);
        if (map.IsDeprecated) throw new InvalidOperationException("deprecated map");

        Map? transition = TransitionsAccessor.SearchTransition(isolate, map, name, PropertyKind.Data, attributes);
        if (transition is not null)
        {
            InternalIndex descriptor = transition.LastAdded();
            return UpdateDescriptorForValue(isolate, transition, descriptor, constness, value);
        }

        // Do not track transitions during bootstrapping.
        TransitionFlag flag = isolate.BootstrapperActive ? TransitionFlag.OMIT_TRANSITION : TransitionFlag.INSERT_TRANSITION;
        Map? maybeMap = null;
        if (!map.TooManyFastProperties(storeOrigin))
        {
            Representation representation;
            (representation, constness) = ObjectOps.OptimalRepresentation(value, constness);
            HeapObject type = ObjectOps.OptimalType(value, isolate, representation);
            maybeMap = CopyWithField(isolate, map, name, type, attributes, constness, representation, flag);
        }

        Map result;
        if (maybeMap is null)
        {
            const string reason = "TooManyFastProperties";
            HeapObject? maybeConstructor = map.GetConstructor();
            if (isolate.Flags.feedback_normalization && map.NewTargetIsBase && maybeConstructor is JSFunction constructor &&
                !constructor.Shared.Native)
            {
                Map initialMap = constructor.InitialMap;
                result = Normalize(isolate, initialMap, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, reason);
                initialMap.DeprecateTransitionTree(isolate);
                JSFunction.SetInitialMap(isolate, constructor, result, result.Prototype);

                // Deoptimize all code that embeds the previous initial map.
                DependentCode.DeoptimizeDependencyGroups(isolate, initialMap, DependentCode.DependencyGroups.InitialMapChanged);
                if (!result.EquivalentToForNormalization(map, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES))
                {
                    result = Normalize(isolate, map, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, reason);
                }
            }
            else
            {
                result = Normalize(isolate, map, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, reason);
            }
        }
        else
        {
            result = maybeMap;
        }
        return result;
    }

    /// <summary>Map::TransitionToAccessorProperty.</summary>
    public static Map TransitionToAccessorProperty(Isolate isolate, Map map, Name name, InternalIndex descriptor,
        JSValue getter, JSValue setter, PropertyAttributes attributes)
    {
        // At least one of the accessors needs to be a new value.
        Debug.Assert(!getter.IsNull || !setter.IsNull);
        Debug.Assert(name.IsUniqueName);

        // Migrate to the newest map before transitioning to the new property.
        map = Update(isolate, map);

        // Dictionary maps can always have additional data properties.
        if (map.IsDictionaryMap) return map;

        PropertyNormalizationMode mode = map.IsPrototypeMap
            ? PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES
            : PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES;

        Map? transition = TransitionsAccessor.SearchTransition(isolate, map, name, PropertyKind.Accessor, attributes);
        if (transition is not null)
        {
            DescriptorArray descriptors = transition._instanceDescriptors;
            InternalIndex lastDescriptor = transition.LastAdded();
            JSValue maybePair = descriptors.GetStrongValue(lastDescriptor);
            if (maybePair.HeapObjectOrNull is not AccessorPair pair0)
            {
                return Normalize(isolate, map, mode, "TransitionToAccessorFromNonPair");
            }
            if (!pair0.Equals(getter, setter))
            {
                return Normalize(isolate, map, mode, "TransitionToDifferentAccessor");
            }
            return transition;
        }

        AccessorPair pair;
        DescriptorArray oldDescriptors = map._instanceDescriptors;
        if (descriptor.IsFound)
        {
            if (descriptor != map.LastAdded())
            {
                return Normalize(isolate, map, mode, "AccessorsOverwritingNonLast");
            }
            PropertyDetails oldDetails = oldDescriptors.GetDetails(descriptor);
            if (oldDetails.Kind != PropertyKind.Accessor)
            {
                return Normalize(isolate, map, mode, "AccessorsOverwritingNonAccessors");
            }
            if (oldDetails.Attributes != attributes)
            {
                return Normalize(isolate, map, mode, "AccessorsWithAttributes");
            }
            JSValue maybePair = oldDescriptors.GetStrongValue(descriptor);
            if (maybePair.HeapObjectOrNull is not AccessorPair currentPair)
            {
                return Normalize(isolate, map, mode, "AccessorsOverwritingNonPair");
            }
            if (currentPair.Equals(getter, setter)) return map;

            bool overwritingAccessor = false;
            if (!getter.IsNull && !currentPair.Getter.IsNull && !currentPair.Getter.IsIdenticalTo(getter))
            {
                overwritingAccessor = true;
            }
            if (!setter.IsNull && !currentPair.Setter.IsNull && !currentPair.Setter.IsIdenticalTo(setter))
            {
                overwritingAccessor = true;
            }
            if (overwritingAccessor)
            {
                return Normalize(isolate, map, mode, "AccessorsOverwritingAccessors");
            }
            pair = AccessorPair.Copy(currentPair);
        }
        else if (map.NumberOfOwnDescriptors >= kMaxNumberOfDescriptors || map.TooManyFastProperties(StoreOrigin.Named))
        {
            return Normalize(isolate, map, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, "TooManyAccessors");
        }
        else
        {
            pair = new AccessorPair();
        }

        pair.SetComponents(getter, setter);

        // Do not track transitions during bootstrapping.
        TransitionFlag flag = isolate.BootstrapperActive ? TransitionFlag.OMIT_TRANSITION : TransitionFlag.INSERT_TRANSITION;
        Descriptor d = Descriptor.AccessorConstant(name, pair, attributes);
        return CopyInsertDescriptor(isolate, map, d, flag);
    }

    static Map CopyAddDescriptor(Isolate isolate, Map map, Descriptor descriptor, TransitionFlag flag)
    {
        DescriptorArray descriptors = map._instanceDescriptors;
        // Share descriptors only if map owns descriptors and is not an initial map.
        if (flag == TransitionFlag.INSERT_TRANSITION && map.OwnsDescriptors && map.GetBackPointer() is not null &&
            TransitionsAccessor.CanHaveMoreTransitions(isolate, map))
        {
            return ShareDescriptor(isolate, map, descriptors, descriptor);
        }
        int nof = map.NumberOfOwnDescriptors;
        DescriptorArray newDescriptors = DescriptorArray.CopyUpTo(descriptors, nof, 1);
        newDescriptors.Append(descriptor);
        return CopyReplaceDescriptors(isolate, map, newDescriptors, flag, descriptor.Key, "CopyAddDescriptor",
            TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION);
    }

    /// <summary>Map::CopyInsertDescriptor: add, or replace if the key is present.</summary>
    public static Map CopyInsertDescriptor(Isolate isolate, Map map, Descriptor descriptor, TransitionFlag flag)
    {
        DescriptorArray oldDescriptors = map._instanceDescriptors;
        // We replace the key if it is already present.
        InternalIndex index = oldDescriptors.SearchWithCache(descriptor.Key, map);
        if (index.IsFound) return CopyReplaceDescriptor(isolate, map, oldDescriptors, descriptor, index, flag);
        return CopyAddDescriptor(isolate, map, descriptor, flag);
    }

    static Map CopyReplaceDescriptor(Isolate isolate, Map map, DescriptorArray descriptors, Descriptor descriptor,
        InternalIndex insertionIndex, TransitionFlag flag)
    {
        Name key = descriptor.Key;
        Debug.Assert(ReferenceEquals(key, descriptors.GetKey(insertionIndex)));
        // This function does not support replacing property fields as
        // that would break property field counters.
        Debug.Assert(descriptor.Details.Location != PropertyLocation.Field);
        Debug.Assert(descriptors.GetDetails(insertionIndex).Location != PropertyLocation.Field);
        DescriptorArray newDescriptors = DescriptorArray.CopyUpTo(descriptors, map.NumberOfOwnDescriptors);
        newDescriptors.Replace(insertionIndex, descriptor);
        TransitionKindFlag simpleFlag = insertionIndex.AsInt == descriptors.NumberOfDescriptors - 1
            ? TransitionKindFlag.SIMPLE_PROPERTY_TRANSITION
            : TransitionKindFlag.PROPERTY_TRANSITION;
        return CopyReplaceDescriptors(isolate, map, newDescriptors, flag, key, "CopyReplaceDescriptor", simpleFlag);
    }

    // ---- Elements kind transitions --------------------------------------------------------

    static Map FindClosestElementsTransition(Isolate isolate, Map map, ElementsKind toKind)
    {
        Map currentMap = map;
        ElementsKind kind = map.ElementsKind;
        while (kind != toKind)
        {
            Map? nextMap = currentMap.ElementsTransitionMap(isolate);
            if (nextMap is null) return currentMap;
            kind = nextMap.ElementsKind;
            currentMap = nextMap;
        }
        return currentMap;
    }

    internal Map? LookupElementsTransitionMap(Isolate isolate, ElementsKind toKind)
    {
        Map toMap = FindClosestElementsTransition(isolate, this, toKind);
        return toMap.ElementsKind == toKind ? toMap : null;
    }

    public bool IsMapInArrayPrototypeChain(Isolate isolate)
    {
        NativeContext nc = isolate.NativeContext;
        return ReferenceEquals(nc.InitialArrayPrototype.Map, this) || ReferenceEquals(nc.InitialObjectPrototype.Map, this);
    }

    /// <summary>Map::TransitionElementsTo.</summary>
    public static Map TransitionElementsTo(Isolate isolate, Map map, ElementsKind toKind)
    {
        ElementsKind fromKind = map.ElementsKind;
        if (fromKind == toKind) return map;

        NativeContext nativeContext = isolate.NativeContext;
        if (fromKind == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS)
        {
            if (ReferenceEquals(map, nativeContext.FastAliasedArgumentsMap)) return nativeContext.SlowAliasedArgumentsMap;
        }
        else if (fromKind == ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS)
        {
            if (ReferenceEquals(map, nativeContext.SlowAliasedArgumentsMap)) return nativeContext.FastAliasedArgumentsMap;
        }
        else if (ElementsKinds.IsFastElementsKind(fromKind) && ElementsKinds.IsFastElementsKind(toKind))
        {
            // Reuse map transitions for JSArrays.
            if (ReferenceEquals(nativeContext.GetInitialJSArrayMap(fromKind), map))
            {
                Map? maybeTransitionedMap = nativeContext.GetInitialJSArrayMap(toKind);
                if (maybeTransitionedMap is not null) return maybeTransitionedMap;
            }
        }

        bool allowStoreTransition = ElementsKinds.IsTransitionElementsKind(fromKind);
        // Only store fast element maps in ascending generality.
        if (ElementsKinds.IsFastElementsKind(toKind))
        {
            allowStoreTransition = allowStoreTransition && ElementsKinds.IsTransitionableFastElementsKind(fromKind) &&
                                   ElementsKinds.IsMoreGeneralElementsKindTransition(fromKind, toKind);
        }
        if (!allowStoreTransition) return CopyAsElementsKind(isolate, map, toKind, TransitionFlag.OMIT_TRANSITION);
        return new MapUpdater(isolate, map).ReconfigureElementsKind(toKind);
    }

    static Map AddMissingElementsTransitions(Isolate isolate, Map map, ElementsKind toKind)
    {
        Map currentMap = map;
        ElementsKind kind = map.ElementsKind;
        TransitionFlag flag;
        if (map.IsDetached(isolate))
        {
            flag = TransitionFlag.OMIT_TRANSITION;
        }
        else
        {
            flag = TransitionFlag.INSERT_TRANSITION;
            if (ElementsKinds.IsFastElementsKind(kind))
            {
                while (kind != toKind && !ElementsKinds.IsTerminalElementsKind(kind))
                {
                    kind = ElementsKinds.GetNextTransitionElementsKind(kind);
                    currentMap = CopyAsElementsKind(isolate, currentMap, kind, flag);
                }
            }
        }
        // In case we are exiting the fast elements kind system, just add the map in
        // the end.
        if (kind != toKind) currentMap = CopyAsElementsKind(isolate, currentMap, toKind, flag);
        return currentMap;
    }

    public static Map? TryAsElementsKind(Isolate isolate, Map map, ElementsKind kind)
    {
        Map closest = FindClosestElementsTransition(isolate, map, kind);
        return closest.ElementsKind == kind ? closest : null;
    }

    /// <summary>Map::AsElementsKind.</summary>
    public static Map AsElementsKind(Isolate isolate, Map map, ElementsKind kind)
    {
        Map closestMap = FindClosestElementsTransition(isolate, map, kind);
        if (closestMap.ElementsKind == kind) return closestMap;
        return AddMissingElementsTransitions(isolate, closestMap, kind);
    }

    /// <summary>Map::FindElementsKindTransitionedMap.</summary>
    public Map? FindElementsKindTransitionedMap(Isolate isolate, ReadOnlySpan<Map> candidates)
    {
        if (IsDetached(isolate)) return null;
        ElementsKind kind = ElementsKind;
        bool isPacked = ElementsKinds.IsFastPackedElementsKind(kind);
        Map? transition = null;
        if (ElementsKinds.IsTransitionableFastElementsKind(kind))
        {
            // Check the state of the root map.
            Map? rootMap = FindRootMap();
            if (!EquivalentToForElementsKindTransition(rootMap)) return null;
            rootMap = rootMap.LookupElementsTransitionMap(isolate, kind);
            for (rootMap = rootMap!.ElementsTransitionMap(isolate);
                 rootMap is not null && rootMap.HasFastElements;
                 rootMap = rootMap.ElementsTransitionMap(isolate))
            {
                if (!HasElementsKind(candidates, rootMap.ElementsKind)) continue;
                Map? current = rootMap.TryReplayPropertyTransitions(isolate, this);
                if (current is null) continue;
                if (InstancesNeedRewriting(current)) continue;
                bool currentIsPacked = ElementsKinds.IsFastPackedElementsKind(current.ElementsKind);
                if (ContainsMap(candidates, current) && (isPacked || !currentIsPacked))
                {
                    transition = current;
                    isPacked = isPacked && currentIsPacked;
                }
            }
        }
        return transition;
    }

    static bool ContainsMap(ReadOnlySpan<Map> maps, Map map)
    {
        foreach (Map m in maps)
        {
            if (ReferenceEquals(m, map)) return true;
        }
        return false;
    }

    static bool HasElementsKind(ReadOnlySpan<Map> maps, ElementsKind kind)
    {
        foreach (Map m in maps)
        {
            if (m is not null && m.ElementsKind == kind) return true;
        }
        return false;
    }

    // ---- Deprecation and update -----------------------------------------------------------

    public void DeprecateTransitionTree(Isolate isolate)
    {
        if (IsDeprecated) return;
        new TransitionsAccessor(isolate, this).ForEachTransition(m => m.DeprecateTransitionTree(isolate));
        IsDeprecated = true;
        DependentCode.DeoptimizeDependencyGroups(isolate, this, DependentCode.DependencyGroups.Transition);
        NotifyLeafMapLayoutChange(isolate);
    }

    static Map? SearchMigrationTarget(Isolate isolate, Map oldMap)
    {
        Map? target = oldMap;
        do
        {
            target = new TransitionsAccessor(isolate, target).GetMigrationTarget();
        } while (target is not null && target.IsDeprecated);
        return target;
    }

    /// <summary>Map::TryUpdate: the up-to-date map for a deprecated one, or null if none exists yet.</summary>
    public static Map? TryUpdate(Isolate isolate, Map oldMap)
    {
        if (!oldMap.IsDeprecated) return oldMap;
        if (isolate.Flags.fast_map_update)
        {
            Map? targetMap = SearchMigrationTarget(isolate, oldMap);
            if (targetMap is not null) return targetMap;
        }
        Map? newMap = MapUpdater.TryUpdateNoLock(isolate, oldMap);
        if (newMap is null) return null;
        if (isolate.Flags.fast_map_update) TransitionsAccessor.SetMigrationTarget(isolate, oldMap, newMap);
        return newMap;
    }

    internal Map? TryReplayPropertyTransitions(Isolate isolate, Map oldMap)
    {
        int rootNof = NumberOfOwnDescriptors;
        int oldNof = oldMap.NumberOfOwnDescriptors;
        DescriptorArray oldDescriptors = oldMap._instanceDescriptors;
        Map newMap = this;
        for (int i = rootNof; i < oldNof; i++)
        {
            var idx = new InternalIndex(i);
            PropertyDetails oldDetails = oldDescriptors.GetDetails(idx);
            Map? transition = new TransitionsAccessor(isolate, newMap)
                .SearchTransition(oldDescriptors.GetKey(idx), oldDetails.Kind, oldDetails.Attributes);
            if (transition is null) return null;
            newMap = transition;
            DescriptorArray newDescriptors = newMap._instanceDescriptors;
            PropertyDetails newDetails = newDescriptors.GetDetails(idx);
            if (!PropertyDetailsHelpers.IsGeneralizableTo(oldDetails.Constness, newDetails.Constness)) return null;
            if (!oldDetails.Representation.FitsInto(newDetails.Representation)) return null;
            if (newDetails.Location == PropertyLocation.Field)
            {
                if (newDetails.Kind == PropertyKind.Data)
                {
                    HeapObject newType = newDescriptors.GetFieldType(idx);
                    HeapObject oldType = oldDescriptors.GetFieldType(idx);
                    if (!FieldType.NowIs(oldType, newType)) return null;
                }
                else
                {
                    throw new InvalidOperationException("unreachable");
                }
            }
            else
            {
                if (oldDetails.Location == PropertyLocation.Field ||
                    !oldDescriptors.GetStrongValue(idx).IsIdenticalTo(newDescriptors.GetStrongValue(idx)))
                {
                    return null;
                }
            }
        }
        if (newMap.NumberOfOwnDescriptors != oldNof) return null;
        return newMap;
    }

    /// <summary>Map::Update: the up-to-date map for a possibly deprecated map.</summary>
    public static Map Update(Isolate isolate, Map map)
    {
        if (!map.IsDeprecated) return map;
        if (isolate.Flags.fast_map_update)
        {
            Map? targetMap = SearchMigrationTarget(isolate, map);
            if (targetMap is not null) return targetMap;
        }
        return new MapUpdater(isolate, map).Update();
    }

    // ---- Equivalence ------------------------------------------------------------------

    static bool CheckEquivalentModuloProto(Map first, Map second) =>
        ReferenceEquals(first.GetConstructorRaw(), second.GetConstructorRaw()) &&
        first.InstanceType == second.InstanceType &&
        first.BitField == second.BitField &&
        first.IsExtensible == second.IsExtensible &&
        first.NewTargetIsBase == second.NewTargetIsBase;

    internal bool EquivalentToForTransition(Map other, bool hasNewPrototype = false, JSReceiver? newPrototype = null,
        InstanceType? newInstanceType = null)
    {
        if (!ReferenceEquals(GetConstructor(), other.GetConstructor())) throw new InvalidOperationException("constructor mismatch");
        if (newInstanceType is { } t)
        {
            if (t != other.InstanceType) return false;
        }
        else if (InstanceType != other.InstanceType)
        {
            return false;
        }
        if (BitField != other.BitField) return false;
        if (!hasNewPrototype)
        {
            if (!ReferenceEquals(Prototype, other.Prototype)) return false;
        }
        else if (!ReferenceEquals(newPrototype, other.Prototype))
        {
            return false;
        }
        if (NewTargetIsBase != other.NewTargetIsBase) return false;
        if (InstanceTypeChecks.IsJSFunction(InstanceType))
        {
            // JSFunctions require more checks to ensure that sloppy function is
            // not equivalent to strict function.
            int nof = Math.Min(NumberOfOwnDescriptors, other.NumberOfOwnDescriptors);
            return _instanceDescriptors.IsEqualUpTo(other._instanceDescriptors, nof);
        }
        return true;
    }

    bool EquivalentToForElementsKindTransition(Map other) => EquivalentToForTransition(other);

    public bool EquivalentToForNormalization(Map other, ElementsKind elementsKind, JSReceiver? otherPrototype,
        PropertyNormalizationMode mode)
    {
        int properties = mode == PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES ? 0 : other.GetInObjectProperties();
        int adjustedOtherBitField2 = (other.BitField2 & ~Bits2.ElementsKindMask) | ((int)elementsKind << Bits2.ElementsKindShift);
        return CheckEquivalentModuloProto(this, other) &&
               ReferenceEquals(Prototype, otherPrototype) &&
               BitField2 == adjustedOtherBitField2 &&
               GetInObjectProperties() == properties;
    }

    public bool EquivalentToForNormalization(Map other, PropertyNormalizationMode mode) =>
        EquivalentToForNormalization(other, ElementsKind, Prototype, mode);

    /// <summary>Map::Hash: prototype identity hash mixed with bit_field2 and instance type.</summary>
    public int Hash(Isolate isolate, JSReceiver? prototype)
    {
        int prototypeHash = prototype is null ? 1 : prototype.GetOrCreateIdentityHash(isolate);
        uint h = (uint)prototypeHash;
        h = (h ^ (h >> 16)) * 0x45d9f3b;
        h ^= BitField2 * 0x9E3779B1u;
        h ^= (uint)InstanceType * 0x85EBCA77u;
        return (int)(h & 0x7FFFFFFF);
    }

    // ---- Prototype-related transitions ----------------------------------------------------

    /// <summary>Map::GetObjectCreateMap: the map for Object.create(prototype).</summary>
    public static Map GetObjectCreateMap(Isolate isolate, JSReceiver? prototype)
    {
        Map map = isolate.NativeContext.ObjectFunction.InitialMap;
        if (ReferenceEquals(map.Prototype, prototype)) return map;
        if (prototype is null) return isolate.NativeContext.SlowObjectWithNullPrototypeMap;
        if (prototype is JSObject jsPrototype && JSObject.IsJSObjectThatCanBeTrackedAsPrototype(jsPrototype))
        {
            if (!jsPrototype.Map.IsPrototypeMap) JSObject.OptimizeAsPrototype(isolate, jsPrototype);
            PrototypeInfo info = GetOrCreatePrototypeInfo(jsPrototype, isolate);
            if (info.ObjectCreateMap is { } cached)
            {
                map = cached;
            }
            else
            {
                map = CopyInitialMap(isolate, map);
                SetPrototype(isolate, map, prototype);
                info.ObjectCreateMap = map;
            }
            return map;
        }
        return TransitionRootMapToPrototypeForNewObject(isolate, map, prototype);
    }

    /// <summary>Map::GetDerivedMap: the map for new.target-derived instances with <paramref name="prototype"/>.</summary>
    public static Map GetDerivedMap(Isolate isolate, Map from, JSReceiver prototype)
    {
        Debug.Assert(from.GetBackPointer() is null);
        if (prototype is JSObject jsPrototype && JSObject.IsJSObjectThatCanBeTrackedAsPrototype(jsPrototype))
        {
            if (!jsPrototype.Map.IsPrototypeMap) JSObject.OptimizeAsPrototype(isolate, jsPrototype);
            PrototypeInfo info = GetOrCreatePrototypeInfo(jsPrototype, isolate);
            Map? map = info.GetDerivedMap(from);
            if (map is null)
            {
                map = CopyInitialMap(isolate, from);
                map.NewTargetIsBase = false;
                if (!ReferenceEquals(map.Prototype, prototype)) SetPrototype(isolate, map, prototype);
                info.AddDerivedMap(map);
            }
            return map;
        }
        if (ReferenceEquals(from.Prototype, prototype)) return from;
        // The TransitionToPrototype map will not have new_target_is_base reset. But
        // we don't need it to for proxies.
        return TransitionRootMapToPrototypeForNewObject(isolate, from, prototype);
    }

    public static Map TransitionRootMapToPrototypeForNewObject(Isolate isolate, Map map, JSReceiver? prototype)
    {
        Map newMap = TransitionToUpdatePrototype(isolate, map, prototype);
        if (!ReferenceEquals(newMap.GetBackPointer(), map) && map.IsInobjectSlackTrackingInProgress())
        {
            // Advance the construction count on the base map to keep it in sync with
            // the transitioned map.
            map.InobjectSlackTrackingStep(isolate);
        }
        return newMap;
    }

    public static Map TransitionToUpdatePrototype(Isolate isolate, Map map, JSReceiver? prototype)
    {
        Debug.Assert(map.GetBackPointer() is null);
        Map? newMap = TransitionsAccessor.GetPrototypeTransition(isolate, map, prototype);
        if (newMap is null)
        {
            newMap = CopyForPrototypeTransition(isolate, map, prototype);
            if (!map.IsDetached(isolate))
            {
                TransitionsAccessor.PutPrototypeTransition(isolate, map, prototype, newMap);
            }
        }
        return newMap;
    }

    public bool ShouldCheckForReadOnlyElementsInPrototypeChain(Isolate isolate)
    {
        // If this map has TypedArray elements kind, we won't look at the prototype
        // chain, so we can return early.
        if (ElementsKinds.IsTypedArrayElementsKind(ElementsKind)) return false;
        for (JSReceiver? current = Prototype; current is not null; current = current.Map.Prototype)
        {
            // Be conservative, don't look into any JSReceivers that may have custom
            // elements.
            if (IsCustomElementsReceiverMap(current.Map)) return true;
            JSObject obj = (JSObject)current;
            ElementsKind elementsKind = obj.GetElementsKind();
            if (ElementsKinds.IsTypedArrayElementsKind(elementsKind)) return false;
            if (ElementsKinds.IsFrozenElementsKind(elementsKind)) return true;
            if (ElementsKinds.IsDictionaryElementsKind(elementsKind) && obj.ElementDictionary.RequiresSlowElements) return true;
            if (ElementsKinds.IsSlowArgumentsElementsKind(elementsKind))
            {
                var elements = (SloppyArgumentsElements)obj.Elements;
                if (((NumberDictionary)elements.Arguments).RequiresSlowElements) return true;
            }
        }
        return false;
    }

    public override string ToString() => $"<Map {InstanceType} {ElementsKind}{(IsDictionaryMap ? " dictionary" : "")}>";
}

/// <summary>V8's NormalizedMapCache: per-native-context cache of normalized maps.</summary>
public sealed class NormalizedMapCache() : HeapObject(InstanceType.WeakFixedArrayType)
{
    const int kEntries = 64;
    readonly Map?[] _entries = new Map?[kEntries];

    static int GetIndex(Isolate isolate, Map map, JSReceiver? prototype) => map.Hash(isolate, prototype) % kEntries;

    public Map? Get(Isolate isolate, Map fastMap, ElementsKind elementsKind, JSReceiver? prototype, PropertyNormalizationMode mode)
    {
        Map? normalizedMap = _entries[GetIndex(isolate, fastMap, prototype)];
        if (normalizedMap is null) return null;
        if (!normalizedMap.EquivalentToForNormalization(fastMap, elementsKind, prototype, mode)) return null;
        return normalizedMap;
    }

    public void Set(Isolate isolate, Map fastMap, Map normalizedMap)
    {
        Debug.Assert(normalizedMap.IsDictionaryMap);
        _entries[GetIndex(isolate, fastMap, normalizedMap.Prototype)] = normalizedMap;
    }
}

/// <summary>
/// V8's PrototypeInfo: bookkeeping of a prototype map (users, derived maps,
/// Object.create map, enum cache, fast-mode hint).
/// </summary>
public sealed class PrototypeInfo() : HeapObject(InstanceType.PrototypeInfoType)
{
    public const int UNREGISTERED = -1;

    public JSValue ModuleNamespace;

    /// <summary>The maps that use this prototype (V8's prototype_users WeakArrayList).</summary>
    internal List<Map?>? PrototypeUsers;

    public FixedArray? PrototypeChainEnumCache;
    public int RegistrySlot = UNREGISTERED;
    public bool ShouldBeFastMap;
    public Map? ObjectCreateMap;
    List<Map>? _derivedMaps;

    /// <summary>Cached IC handlers (V8's cached_handler slots).</summary>
    public readonly HeapObject?[] CachedHandlers = new HeapObject?[2];

    public Map? GetDerivedMap(Map from)
    {
        if (_derivedMaps is null) return null;
        foreach (Map m in _derivedMaps)
        {
            if (ReferenceEquals(m.GetConstructor(), from.GetConstructor()) && m.InstanceType == from.InstanceType) return m;
        }
        return null;
    }

    public void AddDerivedMap(Map to) => (_derivedMaps ??= []).Add(to);
}

/// <summary>V8's Cell: a single boxed value (used for prototype validity cells).</summary>
public sealed class Cell(JSValue value) : HeapObject(InstanceType.CellType)
{
    /// <summary>Value of a valid prototype chain validity cell (V8: the native context, weak).</summary>
    public static readonly JSValue kPrototypeChainValid = JSValue.FromInt(0);

    /// <summary>Value of an invalidated validity cell (V8: the cleared weak value).</summary>
    public static readonly JSValue kPrototypeChainInvalid = JSValue.FromInt(1);

    public JSValue Value = value;
}

/// <summary>
/// V8's DependentCode: optimized code registered on maps and cells. The
/// optimizing tiers are not ported yet, so there is nothing to deoptimize;
/// the hook stays so the IL tiers can register dependencies later.
/// </summary>
public static class DependentCode
{
    [Flags]
    public enum DependencyGroups
    {
        None = 0,
        Transition = 1 << 0,
        PrototypeCheck = 1 << 1,
        PropertyCellChanged = 1 << 2,
        FieldConst = 1 << 3,
        FieldType = 1 << 4,
        FieldRepresentation = 1 << 5,
        InitialMapChanged = 1 << 6,
        AllocationSiteTenuringChanged = 1 << 7,
        AllocationSiteTransitionChanged = 1 << 8,
        ScriptContextSlotPropertyChanged = 1 << 9,
        EmptyContextExtension = 1 << 10,
    }

    /// <summary>Called when dependencies of <paramref name="obj"/> are invalidated.</summary>
    public static event Action<Isolate, HeapObject, DependencyGroups>? OnDeoptimize;

    public static void DeoptimizeDependencyGroups(Isolate isolate, HeapObject obj, DependencyGroups groups)
    {
        if (groups != DependencyGroups.None) OnDeoptimize?.Invoke(isolate, obj, groups);
    }
}
