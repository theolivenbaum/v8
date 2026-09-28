// Port of src/objects/elements.{h,cc}: the ElementsAccessor hierarchy, one
// accessor per ElementsKind (fast smi/object/double, nonextensible, sealed,
// frozen, dictionary, sloppy arguments, string wrapper, typed arrays).
//
// V8 uses CRTP templates (ElementsAccessorBase<Subclass, KindTraits>) so the
// "Subclass::FooImpl" calls are static. V8Sharp uses one abstract class with
// virtual *Impl methods and one sealed subclass per kind; the composition
// accessors (sloppy arguments, string wrappers) hold the accessor of their
// backing store, as V8's template parameters do.
//
// Deliberately not ported: the Atomics entry points (GetAtomic/SetAtomic/...,
// shared arrays are not supported), ElementsAccessor::Concat and the
// typed-array bulk copy helpers (CopyElementsHandle, CopyTypedArrayElementsSlice),
// which belong with the Array/TypedArray builtins.
using System.Buffers.Binary;
using V8Sharp.Base.Numbers;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's AddKeyConversion.</summary>
public enum AddKeyConversion { DO_NOT_CONVERT, CONVERT_TO_ARRAY_INDEX }

/// <summary>V8's ElementsAccessor: element operations for one ElementsKind.</summary>
public abstract class ElementsAccessor
{
    /// <summary>kCopyToEndAndInitializeToHole: copy everything and pad the destination with holes.</summary>
    public const uint kCopyToEndAndInitializeToHole = uint.MaxValue;

    /// <summary>kPackedSizeNotKnown.</summary>
    public const uint kPackedSizeNotKnown = uint.MaxValue;

    static readonly ElementsAccessor[] s_accessors = CreateAccessors();

    protected ElementsAccessor(ElementsKind kind) => Kind = kind;

    /// <summary>The kind this accessor handles (ElementsTraits::Kind).</summary>
    public ElementsKind Kind { get; }

    /// <summary>ElementsAccessor::ForKind.</summary>
    public static ElementsAccessor ForKind(ElementsKind elementsKind) => s_accessors[(int)elementsKind];

    static ElementsAccessor[] CreateAccessors()
    {
        var accessors = new ElementsAccessor[ElementsKinds.kElementsKindCount];
        var holeyObject = new FastSmiOrObjectElementsAccessor(ElementsKind.HOLEY_ELEMENTS);
        var dictionary = new DictionaryElementsAccessor();
        accessors[(int)ElementsKind.PACKED_SMI_ELEMENTS] = new FastSmiOrObjectElementsAccessor(ElementsKind.PACKED_SMI_ELEMENTS);
        accessors[(int)ElementsKind.HOLEY_SMI_ELEMENTS] = new FastSmiOrObjectElementsAccessor(ElementsKind.HOLEY_SMI_ELEMENTS);
        accessors[(int)ElementsKind.PACKED_ELEMENTS] = new FastSmiOrObjectElementsAccessor(ElementsKind.PACKED_ELEMENTS);
        accessors[(int)ElementsKind.HOLEY_ELEMENTS] = holeyObject;
        accessors[(int)ElementsKind.PACKED_DOUBLE_ELEMENTS] = new FastDoubleElementsAccessor(ElementsKind.PACKED_DOUBLE_ELEMENTS);
        accessors[(int)ElementsKind.HOLEY_DOUBLE_ELEMENTS] = new FastDoubleElementsAccessor(ElementsKind.HOLEY_DOUBLE_ELEMENTS);
        accessors[(int)ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS] =
            new FastNonextensibleObjectElementsAccessor(ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS, dictionary);
        accessors[(int)ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS] =
            new FastNonextensibleObjectElementsAccessor(ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS, dictionary);
        accessors[(int)ElementsKind.PACKED_SEALED_ELEMENTS] =
            new FastSealedObjectElementsAccessor(ElementsKind.PACKED_SEALED_ELEMENTS, dictionary);
        accessors[(int)ElementsKind.HOLEY_SEALED_ELEMENTS] =
            new FastSealedObjectElementsAccessor(ElementsKind.HOLEY_SEALED_ELEMENTS, dictionary);
        accessors[(int)ElementsKind.PACKED_FROZEN_ELEMENTS] = new FastFrozenObjectElementsAccessor(ElementsKind.PACKED_FROZEN_ELEMENTS);
        accessors[(int)ElementsKind.HOLEY_FROZEN_ELEMENTS] = new FastFrozenObjectElementsAccessor(ElementsKind.HOLEY_FROZEN_ELEMENTS);
        accessors[(int)ElementsKind.SHARED_ARRAY_ELEMENTS] =
            new FastSealedObjectElementsAccessor(ElementsKind.SHARED_ARRAY_ELEMENTS, dictionary);
        accessors[(int)ElementsKind.DICTIONARY_ELEMENTS] = dictionary;
        var slowArguments = new SlowSloppyArgumentsElementsAccessor(dictionary);
        accessors[(int)ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS] = new FastSloppyArgumentsElementsAccessor(holeyObject, slowArguments);
        accessors[(int)ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS] = slowArguments;
        accessors[(int)ElementsKind.FAST_STRING_WRAPPER_ELEMENTS] =
            new StringWrapperElementsAccessor(ElementsKind.FAST_STRING_WRAPPER_ELEMENTS, holeyObject);
        accessors[(int)ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS] =
            new StringWrapperElementsAccessor(ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS, dictionary);
        for (ElementsKind k = ElementsKind.UINT8_ELEMENTS; k <= ElementsKind.RAB_GSAB_FLOAT16_ELEMENTS; k++)
        {
            accessors[(int)k] = new TypedElementsAccessor(k);
        }
        return accessors;
    }

    // ---- Public interface (elements.h) -----------------------------------------------

    /// <summary>ElementsAccessor::Validate (slow DCHECKs only in V8).</summary>
    public virtual void Validate(Isolate isolate, JSObject obj) { }

    /// <summary>ElementsAccessor::HasElement(holder, index, backing_store, filter).</summary>
    public bool HasElement(Isolate isolate, JSObject holder, uint index, FixedArrayBase backingStore,
        PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        HasElementImpl(isolate, holder, index, backingStore, filter);

    /// <summary>ElementsAccessor::HasElement(holder, index, filter).</summary>
    public bool HasElement(Isolate isolate, JSObject holder, uint index, PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        HasElementImpl(isolate, holder, index, holder.Elements, filter);

    public bool HasEntry(Isolate isolate, JSObject holder, InternalIndex entry) => HasEntryImpl(isolate, holder.Elements, entry);

    public JSValue Get(Isolate isolate, JSObject holder, InternalIndex entry) => GetInternalImpl(isolate, holder, entry);

    public bool HasAccessors(JSObject holder) => HasAccessorsImpl(holder, holder.Elements);

    public long NumberOfElements(Isolate isolate, JSObject receiver) => NumberOfElementsImpl(isolate, receiver, receiver.Elements);

    /// <summary>
    /// ElementsAccessor::SetLength: modifies the length data property as
    /// specified for JSArrays and resizes the backing store; arrays with
    /// non-deletable elements can only be shrunk to above the highest one.
    /// </summary>
    public bool SetLength(Isolate isolate, JSArray array, uint length) => SetLengthImpl(isolate, array, length, array.Elements);

    /// <summary>ElementsAccessor::CollectElementIndices.</summary>
    public void CollectElementIndices(JSObject obj, FixedArrayBase backingStore, KeyAccumulator keys) =>
        CollectElementIndicesImpl(obj, backingStore, keys);

    public void CollectElementIndices(JSObject obj, KeyAccumulator keys) => CollectElementIndicesImpl(obj, obj.Elements, keys);

    /// <summary>ElementsAccessor::CollectValuesOrEntries (for Object.values/entries); <paramref name="nofItems"/> counts the items added.</summary>
    public bool CollectValuesOrEntries(Isolate isolate, JSObject obj, FixedArray valuesOrEntries, bool getEntries, ref int nofItems,
        PropertyFilter filter = PropertyFilter.ALL_PROPERTIES)
    {
        int count = 0;
        bool result = CollectValuesOrEntriesImpl(isolate, obj, valuesOrEntries, valuesOrEntries.Length, getEntries, ref count, filter);
        nofItems = count;
        return result;
    }

    /// <summary>ElementsAccessor::PrependElementIndices.</summary>
    public FixedArray PrependElementIndices(Isolate isolate, JSObject obj, FixedArrayBase backingStore, FixedArray keys,
        GetKeysConversion convert, PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        PrependElementIndicesImpl(isolate, obj, backingStore, keys, convert, filter);

    public FixedArray PrependElementIndices(Isolate isolate, JSObject obj, FixedArray keys, GetKeysConversion convert,
        PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        PrependElementIndicesImpl(isolate, obj, obj.Elements, keys, convert, filter);

    /// <summary>ElementsAccessor::AddElementsToKeyAccumulator: adds the element values (for Array.from on sets and friends).</summary>
    public void AddElementsToKeyAccumulator(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert) =>
        AddElementsToKeyAccumulatorImpl(receiver, accumulator, convert);

    public void TransitionElementsKind(Isolate isolate, JSObject obj, Map map) => TransitionElementsKindImpl(isolate, obj, map);

    public bool GrowCapacityAndConvert(Isolate isolate, JSObject obj, uint capacity) =>
        GrowCapacityAndConvertImpl(isolate, obj, capacity);

    /// <summary>
    /// ElementsAccessor::GrowCapacity: unlike GrowCapacityAndConvert, do not
    /// attempt to convert the backing store and simply return false in this case.
    /// </summary>
    public bool GrowCapacity(Isolate isolate, JSObject obj, uint index)
    {
        // This function is intended to be called from optimized code. We don't
        // want to trigger lazy deopts there, so refuse to handle cases that would.
        if (obj.Map.IsPrototypeMap || obj.WouldConvertToSlowElements(index)) return false;
        FixedArrayBase oldElements = obj.Elements;
        uint newCapacity = JSObject.NewElementsCapacity(index + 1);
        if (newCapacity > FixedArrayBase.kMaxLength) return false;
        FixedArrayBase elements = ConvertElementsWithCapacity(isolate, obj, oldElements, Kind, newCapacity);
        // Transition through the allocation site as well if present.
        if (JSObject.UpdateAllocationSite(isolate, obj, Kind)) return false;
        obj.Elements = elements;
        return true;
    }

    public void Set(JSObject holder, InternalIndex entry, JSValue value) => SetImpl(holder, entry, value);

    public bool Add(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes, uint newCapacity) =>
        AddImpl(isolate, obj, index, value, attributes, newCapacity);

    /// <summary>ElementsAccessor::Push (Array.prototype.push fast path): returns the new length.</summary>
    public uint Push(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) => PushImpl(isolate, receiver, args);

    /// <summary>ElementsAccessor::Unshift (Array.prototype.unshift fast path): returns the new length.</summary>
    public uint Unshift(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) => UnshiftImpl(isolate, receiver, args);

    /// <summary>ElementsAccessor::Pop.</summary>
    public JSValue Pop(Isolate isolate, JSArray receiver) => PopImpl(isolate, receiver);

    /// <summary>ElementsAccessor::Shift.</summary>
    public JSValue Shift(Isolate isolate, JSArray receiver) => ShiftImpl(isolate, receiver);

    public NumberDictionary Normalize(Isolate isolate, JSObject obj) => NormalizeImpl(isolate, obj, obj.Elements);

    public long GetCapacity(JSObject holder, FixedArrayBase backingStore) => GetCapacityImpl(holder, backingStore);

    /// <summary>ElementsAccessor::Fill (Array.prototype.fill / TypedArray.prototype.fill fast path).</summary>
    public JSValue Fill(Isolate isolate, JSObject receiver, JSValue value, ulong start, ulong end) =>
        FillImpl(isolate, receiver, value, start, end);

    /// <summary>ElementsAccessor::IncludesValue: SameValueZero search of the own elements.</summary>
    public bool IncludesValue(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length) =>
        IncludesValueImpl(isolate, receiver, value, startFrom, length);

    /// <summary>ElementsAccessor::IndexOfValue: strict-equality search of the own elements.</summary>
    public long IndexOfValue(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length) =>
        IndexOfValueImpl(isolate, receiver, value, startFrom, length);

    public long LastIndexOfValue(JSObject receiver, JSValue value, ulong startFrom) => LastIndexOfValueImpl(receiver, value, startFrom);

    public void Reverse(JSObject receiver) => ReverseImpl(receiver);

    /// <summary>ElementsAccessor::CopyElements(source, source_kind, destination, size).</summary>
    public void CopyElements(Isolate isolate, FixedArrayBase source, ElementsKind sourceKind, FixedArrayBase destination, uint size) =>
        CopyElementsImpl(isolate, source, 0, destination, sourceKind, 0, kPackedSizeNotKnown, size);

    /// <summary>ElementsAccessor::CopyElements(from_holder, from_start, from_kind, to, to_start, copy_size).</summary>
    public void CopyElements(Isolate isolate, JSObject fromHolder, uint fromStart, ElementsKind fromKind, FixedArrayBase to,
        uint toStart, uint copySize)
    {
        uint packedSize = kPackedSizeNotKnown;
        bool isPacked = ElementsKinds.IsFastPackedElementsKind(fromKind) && fromHolder is JSArray;
        if (isPacked)
        {
            packedSize = (uint)((JSArray)fromHolder).Length.Number;
            if (packedSize > copySize) packedSize = copySize;
        }
        CopyElementsImpl(isolate, fromHolder.Elements, fromStart, to, fromKind, toStart, packedSize, copySize);
    }

    public FixedArray CreateListFromArrayLike(Isolate isolate, JSObject obj, uint length) =>
        CreateListFromArrayLikeImpl(isolate, obj, length);

    /// <summary>ElementsAccessor::GetEntryForIndex (entries are backing-store slots; indices are JS indices).</summary>
    public InternalIndex GetEntryForIndex(Isolate isolate, JSObject holder, FixedArrayBase backingStore, ulong index) =>
        GetEntryForIndexImpl(isolate, holder, backingStore, index, PropertyFilter.ALL_PROPERTIES);

    public PropertyDetails GetDetails(JSObject holder, InternalIndex entry) => GetDetailsImpl(holder, entry);

    public void Reconfigure(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes) =>
        ReconfigureImpl(isolate, obj, store, entry, value, attributes);

    /// <summary>ElementsAccessor::Delete: deletes an element in an object.</summary>
    public void Delete(Isolate isolate, JSObject holder, InternalIndex entry) => DeleteImpl(isolate, holder, entry);

    // ---- ElementsAccessorBase defaults ----------------------------------------------

    internal virtual bool HasElementImpl(Isolate isolate, JSObject holder, ulong index, FixedArrayBase backingStore,
        PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        GetEntryForIndexImpl(isolate, holder, backingStore, index, filter).IsFound;

    internal virtual bool HasEntryImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        throw new NotImplementedException("HasEntryImpl for " + Kind);

    internal virtual bool HasAccessorsImpl(JSObject holder, FixedArrayBase backingStore) => false;

    internal virtual JSValue GetInternalImpl(Isolate isolate, JSObject holder, InternalIndex entry) =>
        GetImpl(isolate, holder.Elements, entry);

    internal virtual JSValue GetImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        ((FixedArray)backingStore).Get(entry.AsInt);

    internal virtual void SetImpl(JSObject holder, InternalIndex entry, JSValue value) => SetImpl(holder.Elements, entry, value);

    internal virtual void SetImpl(FixedArrayBase backingStore, InternalIndex entry, JSValue value) =>
        throw new InvalidOperationException("unreachable: SetImpl for " + Kind);

    internal virtual void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes) =>
        throw new InvalidOperationException("unreachable: ReconfigureImpl for " + Kind);

    internal virtual bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity) =>
        throw new InvalidOperationException("unreachable: AddImpl for " + Kind);

    internal virtual uint PushImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        throw new InvalidOperationException("unreachable: PushImpl for " + Kind);

    internal virtual uint UnshiftImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        throw new InvalidOperationException("unreachable: UnshiftImpl for " + Kind);

    internal virtual JSValue PopImpl(Isolate isolate, JSArray receiver) =>
        throw new InvalidOperationException("unreachable: PopImpl for " + Kind);

    internal virtual JSValue ShiftImpl(Isolate isolate, JSArray receiver) =>
        throw new InvalidOperationException("unreachable: ShiftImpl for " + Kind);

    /// <summary>
    /// ElementsAccessorBase::DecreaseLength: if more than half the elements
    /// won't be used, trim the array (not for short arrays, to prevent frequent
    /// trimming on repeated pop operations).
    /// </summary>
    internal static void DecreaseLength(Isolate isolate, FixedArrayBase backingStore, uint oldLength, uint length)
    {
        uint capacity = (uint)backingStore.Length;
        // It's possible we got here through left-trimming, which would have reduced
        // the capacity.
        if (2 * length + JSObject.kMinAddedElementsCapacity <= capacity)
        {
            // Leave some space to allow for subsequent push operations.
            uint newCapacity = length + 1 == oldLength ? (capacity + length) / 2 : length;
            RightTrim(backingStore, (int)newCapacity);
        }
    }

    internal static void RightTrim(FixedArrayBase store, int newCapacity)
    {
        switch (store)
        {
            case FixedArray fa:
                fa.RightTrim(newCapacity);
                break;
            case FixedDoubleArray fda:
                fda.RightTrim(newCapacity);
                break;
        }
    }

    internal static void FillWithHoles(FixedArrayBase store, int from, int to)
    {
        if (from >= to) return;
        switch (store)
        {
            case FixedArray fa:
                fa.FillWithHoles(from, to);
                break;
            case FixedDoubleArray fda:
                fda.FillWithHoles(from, to);
                break;
        }
    }

    internal static bool IsTheHole(FixedArrayBase store, int index) => store switch
    {
        FixedArray fa => fa.IsTheHole(index),
        FixedDoubleArray fda => fda.IsTheHole(index),
        _ => false,
    };

    /// <summary>ElementsAccessorBase::SetLengthImpl (fast kinds).</summary>
    internal virtual bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore)
    {
        Debug.Assert(!array.SetLengthWouldNormalize(length));
        Debug.Assert(ElementsKinds.IsFastElementsKind(array.GetElementsKind()));
        if (!ObjectOps.ToArrayIndex(array.Length, out uint oldLength)) throw new InvalidOperationException("invalid length");

        if (oldLength < length)
        {
            ElementsKind kind = array.GetElementsKind();
            if (!ElementsKinds.IsHoleyElementsKind(kind))
            {
                kind = ElementsKinds.GetHoleyElementsKind(kind);
                JSObject.TransitionElementsKind(isolate, array, kind);
            }
        }

        // Check whether the backing store should be shrunk or grown.
        uint capacity = (uint)backingStore.Length;
        oldLength = Math.Min(oldLength, capacity);
        if (length == 0)
        {
            array.Elements = array.Map.GetInitialElements();
        }
        else if (length <= capacity)
        {
            if (ElementsKinds.IsSmiOrObjectElementsKind(Kind))
            {
                JSObject.EnsureWritableFastElements(isolate, array);
                if (!ReferenceEquals(array.Elements, backingStore)) backingStore = array.Elements;
            }
            DecreaseLength(isolate, backingStore, oldLength, length);
            // Fill the non-trimmed elements with holes.
            // Also use min if we don't RightTrim. It's possible we got here through
            // left-trimming.
            capacity = (uint)backingStore.Length;
            FillWithHoles(backingStore, (int)length, (int)Math.Min(oldLength, capacity));
        }
        else
        {
            // Calculate a new capacity for the array.
            uint newCapacity;
            if (capacity == 0)
            {
                // If the existing capacity is zero, assume we are setting the length to
                // presize to the exact size we want.
                newCapacity = length;
            }
            else
            {
                // Otherwise, assume we want exponential growing semantics, and grow as
                // if we were pushing. We might not grow enough for the length, so take
                // the max of hte two values.
                newCapacity = Math.Max(length, JSObject.NewElementsCapacity(capacity));
            }
            // Grow the array to the new capacity. Note that this code will allow
            // create backing stores that consist almost entirely of holes, for which
            // `JSObject::ShouldConvertToSlowElements` would return "true". This is
            // intentional, because we are assuming the user is setting a length to
            // pre-size an array to then write to it within bounds. A subsequent
            // resizing operation, like Array.p.push, might still trigger a transition
            // to dictionary elements because of sparseness.
            GrowCapacityAndConvertImpl(isolate, array, newCapacity);
        }

        array.Length = JSValue.FromNumber(length);
        return true;
    }

    internal virtual long NumberOfElementsImpl(Isolate isolate, JSObject receiver, FixedArrayBase backingStore) =>
        throw new InvalidOperationException("unreachable: NumberOfElementsImpl for " + Kind);

    /// <summary>ElementsAccessorBase::GetMaxIndex.</summary>
    internal virtual long GetMaxIndex(JSObject receiver, FixedArrayBase elements)
    {
        if (receiver is JSArray array) return (long)array.Length.Number;
        return GetCapacityImpl(receiver, elements);
    }

    internal virtual long GetMaxNumberOfEntries(Isolate isolate, JSObject receiver, FixedArrayBase elements) =>
        GetMaxIndex(receiver, elements);

    /// <summary>ElementsAccessorBase::ConvertElementsWithCapacity.</summary>
    internal FixedArrayBase ConvertElementsWithCapacity(Isolate isolate, JSObject obj, FixedArrayBase oldElements,
        ElementsKind fromKind, uint capacity, uint srcIndex = 0, uint dstIndex = 0)
    {
        FixedArrayBase newElements;
        if (capacity > FixedArrayBase.kMaxLength)
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.InvalidArrayLength));
        }
        if (ElementsKinds.IsDoubleElementsKind(Kind))
        {
            newElements = isolate.Factory.NewFixedDoubleArray((int)capacity);
        }
        else
        {
            newElements = isolate.Factory.NewFixedArray((int)capacity);
        }

        uint packedSize = kPackedSizeNotKnown;
        if (ElementsKinds.IsFastPackedElementsKind(fromKind) && obj is JSArray array)
        {
            packedSize = (uint)array.Length.Number;
        }

        CopyElementsImpl(isolate, oldElements, srcIndex, newElements, fromKind, dstIndex, packedSize,
            kCopyToEndAndInitializeToHole);

        return newElements;
    }

    /// <summary>ElementsAccessorBase::TransitionElementsKindImpl.</summary>
    internal virtual void TransitionElementsKindImpl(Isolate isolate, JSObject obj, Map toMap)
    {
        Map fromMap = obj.Map;
        ElementsKind fromKind = fromMap.ElementsKind;
        ElementsKind toKind = toMap.ElementsKind;
        if (ElementsKinds.IsHoleyElementsKind(fromKind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);
        if (fromKind != toKind)
        {
            // This method should never be called for any other case.
            Debug.Assert(ElementsKinds.IsFastElementsKind(fromKind));
            Debug.Assert(ElementsKinds.IsFastElementsKind(toKind));

            FixedArrayBase fromElements = obj.Elements;
            if (ReferenceEquals(obj.Elements, FixedArray.Empty) ||
                ElementsKinds.IsDoubleElementsKind(fromKind) == ElementsKinds.IsDoubleElementsKind(toKind))
            {
                // No change is needed to the elements() buffer, the transition
                // only requires a map change.
                JSObject.MigrateToMap(isolate, obj, toMap);
            }
            else
            {
                uint capacity = (uint)obj.Elements.Length;
                // Since the max length of FixedArray and FixedDoubleArray is the same,
                // we can safely assume that element conversion with the same capacity
                // will succeed.
                FixedArrayBase elements = ConvertElementsWithCapacity(isolate, obj, fromElements, fromKind, capacity);
                JSObject.SetMapAndElements(isolate, obj, toMap, elements);
            }
        }
    }

    /// <summary>ElementsAccessorBase::GrowCapacityAndConvertImpl.</summary>
    internal virtual bool GrowCapacityAndConvertImpl(Isolate isolate, JSObject obj, uint capacity)
    {
        ElementsKind fromKind = obj.GetElementsKind();
        if (ElementsKinds.IsSmiOrObjectElementsKind(fromKind))
        {
            // Array optimizations rely on the prototype lookups of Array objects
            // always returning undefined. If there is a store to the initial
            // prototype object, make sure all of these optimizations are invalidated.
            isolate.UpdateNoElementsProtectorOnSetLength(obj);
        }
        FixedArrayBase oldElements = obj.Elements;
        return BasicGrowCapacityAndConvertImpl(isolate, obj, oldElements, fromKind, Kind, capacity);
    }

    /// <summary>ElementsAccessorBase::BasicGrowCapacityAndConvertImpl.</summary>
    internal bool BasicGrowCapacityAndConvertImpl(Isolate isolate, JSObject obj, FixedArrayBase oldElements,
        ElementsKind fromKind, ElementsKind toKind, uint capacity)
    {
        FixedArrayBase elements = ConvertElementsWithCapacity(isolate, obj, oldElements, fromKind, capacity);

        if (ElementsKinds.IsHoleyElementsKind(fromKind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);
        Map newMap = JSObject.GetElementsTransitionMap(isolate, obj, toKind);
        JSObject.SetMapAndElements(isolate, obj, newMap, elements);

        // Transition through the allocation site as well if present.
        JSObject.UpdateAllocationSite(isolate, obj, toKind);
        return true;
    }

    internal virtual void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry) =>
        throw new InvalidOperationException("unreachable: DeleteImpl for " + Kind);

    internal virtual void CopyElementsImpl(Isolate isolate, FixedArrayBase from, uint fromStart, FixedArrayBase to,
        ElementsKind fromKind, uint toStart, uint packedSize, uint copySize) =>
        throw new InvalidOperationException("unreachable: CopyElementsImpl for " + Kind);

    internal virtual NumberDictionary NormalizeImpl(Isolate isolate, JSObject obj, FixedArrayBase elements) =>
        throw new InvalidOperationException("unreachable: NormalizeImpl for " + Kind);

    /// <summary>ElementsAccessorBase::CollectValuesOrEntriesImpl.</summary>
    internal virtual bool CollectValuesOrEntriesImpl(Isolate isolate, JSObject obj, FixedArray valuesOrEntries, int maxNofItems,
        bool getEntries, ref int nofItems, PropertyFilter filter)
    {
        Debug.Assert(nofItems == 0);
        var accumulator = new KeyAccumulator(isolate, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES);
        CollectElementIndicesImpl(obj, obj.Elements, accumulator);
        FixedArray keys = accumulator.GetKeys();

        int count = 0;
        int i = 0;
        ElementsKind originalElementsKind = obj.GetElementsKind();

        for (; i < keys.Length; ++i)
        {
            JSValue key = keys.Get(i);
            if (!ObjectOps.ToUint32(key, out uint index)) continue;

            InternalIndex entry = GetEntryForIndexImpl(isolate, obj, obj.Elements, index, filter);
            if (entry.IsNotFound) continue;
            PropertyDetails details = GetDetailsImpl(obj, entry);

            JSValue value;
            if (details.Kind == PropertyKind.Data)
            {
                value = GetInternalImpl(isolate, obj, entry);
            }
            else
            {
                // This might modify the elements and/or change the elements kind.
                var it = new LookupIterator(isolate, obj, index, LookupIterator.Configuration.OWN);
                value = ObjectOps.GetProperty(ref it);
            }
            if (getEntries) value = MakeEntryPair(isolate, index, value);
            valuesOrEntries.Set(count++, value);
            if (obj.GetElementsKind() != originalElementsKind) break;
        }

        // Slow path caused by changes in elements kind during iteration.
        for (; i < keys.Length; i++)
        {
            JSValue key = keys.Get(i);
            if (!ObjectOps.ToUint32(key, out uint index)) continue;

            if ((filter & PropertyFilter.ONLY_ENUMERABLE) != 0)
            {
                ElementsAccessor accessor = obj.GetElementsAccessor();
                InternalIndex entry = accessor.GetEntryForIndex(isolate, obj, obj.Elements, index);
                if (entry.IsNotFound) continue;
                PropertyDetails details = accessor.GetDetails(obj, entry);
                if (!details.IsEnumerable) continue;
            }

            var it = new LookupIterator(isolate, obj, index, LookupIterator.Configuration.OWN);
            JSValue value = ObjectOps.GetProperty(ref it);

            if (getEntries) value = MakeEntryPair(isolate, index, value);
            valuesOrEntries.Set(count++, value);
        }

        nofItems = count;
        return true;
    }

    internal static JSValue MakeEntryPair(Isolate isolate, ulong index, JSValue value)
    {
        JSString key = isolate.Factory.SizeToString(index);
        FixedArray entryStorage = isolate.Factory.NewFixedArray(2);
        entryStorage.Set(0, key);
        entryStorage.Set(1, value);
        return isolate.Factory.NewJSArrayWithElements(entryStorage, ElementsKind.PACKED_ELEMENTS, 2);
    }

    /// <summary>ElementsAccessorBase::CollectElementIndicesImpl.</summary>
    internal virtual void CollectElementIndicesImpl(JSObject obj, FixedArrayBase backingStore, KeyAccumulator keys)
    {
        Debug.Assert(Kind != ElementsKind.DICTIONARY_ELEMENTS);
        // Non-dictionary elements can't have all-can-read accessors.
        long length = GetMaxIndex(obj, backingStore);
        PropertyFilter filter = keys.Filter;
        Isolate isolate = keys.Isolate;
        for (long i = 0; i < length; i++)
        {
            if (HasElementImpl(isolate, obj, (ulong)i, backingStore, filter)) keys.AddKey(JSValue.FromNumber(i));
        }
    }

    /// <summary>ElementsAccessorBase::DirectCollectElementIndicesImpl.</summary>
    internal virtual FixedArray DirectCollectElementIndicesImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore,
        GetKeysConversion convert, PropertyFilter filter, FixedArray list, int maxNofIndices, ref int nofIndices,
        int insertionIndex = 0)
    {
        long length = GetMaxIndex(obj, backingStore);
        for (long i = 0; i < length; i++)
        {
            if (HasElementImpl(isolate, obj, (ulong)i, backingStore, filter))
            {
                if (insertionIndex >= maxNofIndices)
                {
                    // This might happen when the object is a TypedArray which was grown
                    // by a background thread.
                    break;
                }
                if (convert == GetKeysConversion.ConvertToString)
                {
                    list.Set(insertionIndex, isolate.Factory.SizeToString((ulong)i));
                }
                else
                {
                    list.Set(insertionIndex, JSValue.FromNumber(i));
                }
                insertionIndex++;
            }
        }
        nofIndices = insertionIndex;
        return list;
    }

    /// <summary>SortIndices: sorts collected numeric keys (undefined last).</summary>
    internal static void SortIndices(FixedArray indices, int sortSize)
    {
        if (sortSize == 0) return;
        indices.Data.AsSpan(0, sortSize).Sort(static (a, b) =>
        {
            if (a.IsUndefined) return b.IsUndefined ? 0 : 1;
            if (b.IsUndefined) return -1;
            return a.Number.CompareTo(b.Number);
        });
    }

    /// <summary>ElementsAccessorBase::PrependElementIndicesImpl.</summary>
    internal virtual FixedArray PrependElementIndicesImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore,
        FixedArray keys, GetKeysConversion convert, PropertyFilter filter)
    {
        int nofPropertyKeys = keys.Length;
        long nofElementsLong = GetMaxNumberOfEntries(isolate, obj, backingStore);

        if (nofElementsLong > FixedArrayBase.kMaxLength - nofPropertyKeys)
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.InvalidArrayLength));
        }
        int nofElements = (int)nofElementsLong;
        int initialListLength = nofElements + nofPropertyKeys;

        // Collect the element indices into a new list.
        FixedArray combinedKeys = isolate.Factory.NewFixedArray(initialListLength);

        int nofIndices = 0;
        bool needsSorting = ElementsKinds.IsDictionaryElementsKind(Kind) || ElementsKinds.IsSloppyArgumentsElementsKind(Kind);
        combinedKeys = DirectCollectElementIndicesImpl(isolate, obj, backingStore,
            needsSorting ? GetKeysConversion.KeepNumbers : convert, filter, combinedKeys, nofElements, ref nofIndices);

        if (needsSorting)
        {
            SortIndices(combinedKeys, nofIndices);
            // Indices from dictionary elements should only be converted after
            // sorting.
            if (convert == GetKeysConversion.ConvertToString)
            {
                for (int i = 0; i < nofIndices; i++)
                {
                    combinedKeys.Set(i, isolate.Factory.SizeToString((ulong)combinedKeys.Get(i).Number));
                }
            }
        }

        // Copy over the passed-in property keys.
        keys.Data.AsSpan(0, nofPropertyKeys).CopyTo(combinedKeys.Data.AsSpan(nofIndices));

        // For holey elements and arguments we might have to shrink the collected
        // keys since the estimates might be off.
        if (ElementsKinds.IsHoleyOrDictionaryElementsKind(Kind) || ElementsKinds.IsSloppyArgumentsElementsKind(Kind))
        {
            // Shrink combined_keys to the final size.
            int finalSize = nofIndices + nofPropertyKeys;
            return JSReceiver.RightTrimOrEmpty(combinedKeys, finalSize);
        }

        return combinedKeys;
    }

    internal virtual void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert) =>
        throw new InvalidOperationException("unreachable: AddElementsToKeyAccumulatorImpl for " + Kind);

    internal virtual long GetCapacityImpl(JSObject holder, FixedArrayBase backingStore) => backingStore.Length;

    internal virtual JSValue FillImpl(Isolate isolate, JSObject receiver, JSValue value, ulong start, ulong end) =>
        throw new InvalidOperationException("unreachable: FillImpl for " + Kind);

    internal virtual bool IncludesValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length) =>
        IncludesValueSlowPath(isolate, receiver, value, startFrom, length);

    internal virtual long IndexOfValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length) =>
        IndexOfValueSlowPath(isolate, receiver, value, startFrom, length);

    internal virtual long LastIndexOfValueImpl(JSObject receiver, JSValue value, ulong startFrom) =>
        throw new InvalidOperationException("unreachable: LastIndexOfValueImpl for " + Kind);

    internal virtual void ReverseImpl(JSObject receiver) =>
        throw new InvalidOperationException("unreachable: ReverseImpl for " + Kind);

    /// <summary>ElementsAccessorBase::GetEntryForIndexImpl (fast kinds).</summary>
    internal virtual InternalIndex GetEntryForIndexImpl(Isolate isolate, JSObject holder, FixedArrayBase backingStore,
        ulong index, PropertyFilter filter)
    {
        long length = GetMaxIndex(holder, backingStore);
        if (ElementsKinds.IsHoleyElementsKindForRead(Kind))
        {
            return index < (ulong)length && !IsTheHole(backingStore, (int)index)
                ? new InternalIndex((int)index)
                : InternalIndex.NotFound;
        }
        return index < (ulong)length ? new InternalIndex((int)index) : InternalIndex.NotFound;
    }

    internal virtual PropertyDetails GetDetailsImpl(FixedArrayBase backingStore, InternalIndex entry) =>
        new(PropertyKind.Data, PropertyAttributes.NONE, PropertyCellType.NoCell);

    internal virtual PropertyDetails GetDetailsImpl(JSObject holder, InternalIndex entry) =>
        new(PropertyKind.Data, PropertyAttributes.NONE, PropertyCellType.NoCell);

    internal virtual FixedArray CreateListFromArrayLikeImpl(Isolate isolate, JSObject obj, uint length) =>
        throw new InvalidOperationException("unreachable: CreateListFromArrayLikeImpl for " + Kind);

    // ---- Slow paths shared by all kinds ----------------------------------------------

    internal static bool IncludesValueSlowPath(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        bool searchForHole = value.IsUndefined;
        for (ulong k = startFrom; k < length; ++k)
        {
            var it = new LookupIterator(isolate, receiver, k);
            if (!it.IsFound)
            {
                if (searchForHole) return true;
                continue;
            }
            JSValue elementK = ObjectOps.GetProperty(ref it);
            if (ObjectOps.SameValueZero(value, elementK)) return true;
        }
        return false;
    }

    internal static long IndexOfValueSlowPath(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        for (ulong k = startFrom; k < length; ++k)
        {
            var it = new LookupIterator(isolate, receiver, k);
            if (!it.IsFound) continue;
            JSValue elementK = ObjectOps.GetProperty(ref it);
            if (ObjectOps.StrictEquals(value, elementK)) return (long)k;
        }
        return -1;
    }

    // ---- Copy helpers (file-local functions of elements.cc) ---------------------------

    internal static void CopyObjectToObjectElements(Isolate isolate, FixedArrayBase fromBase, ElementsKind fromKind, uint fromStart,
        FixedArrayBase toBase, ElementsKind toKind, uint toStart, uint rawCopySize)
    {
        uint fromBaseLen = (uint)fromBase.Length;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = Math.Min(fromBaseLen - fromStart, toBaseLen - toStart);
            uint start = toStart + copySize;
            uint length = toBaseLen;
            if (start < length) ((FixedArray)toBase).FillWithHoles((int)start, (int)length);
        }
        if (copySize == 0) return;
        var from = (FixedArray)fromBase;
        var to = (FixedArray)toBase;
        Array.Copy(from.Data, (int)fromStart, to.Data, (int)toStart, (int)copySize);
    }

    internal static void CopyDictionaryToObjectElements(Isolate isolate, FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase,
        ElementsKind toKind, uint toStart, uint rawCopySize)
    {
        var from = (NumberDictionary)fromBase;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = from.MaxNumberKey + 1 - fromStart;
            uint start = toStart + copySize;
            uint length = toBaseLen;
            if (start < length) ((FixedArray)toBase).FillWithHoles((int)start, (int)length);
        }
        if (copySize == 0) return;
        var to = (FixedArray)toBase;
        uint toLength = (uint)to.Length;
        if (toStart + copySize > toLength) copySize = toLength - toStart;
        for (uint i = 0; i < copySize; i++)
        {
            InternalIndex entry = from.FindEntry(i + fromStart);
            if (entry.IsFound)
            {
                to.Set((int)(i + toStart), from.ValueAt(entry));
            }
            else
            {
                to.SetTheHole((int)(i + toStart));
            }
        }
    }

    internal static void CopyDoubleToObjectElements(Isolate isolate, FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase,
        uint toStart, uint rawCopySize)
    {
        uint fromBaseLen = (uint)fromBase.Length;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = Math.Min(fromBaseLen - fromStart, toBaseLen - toStart);
            // Also initialize the area that will be copied over since HeapNumber
            // allocation below can cause an incremental marking step, requiring all
            // existing heap objects to be properly initialized.
            if (toStart < toBaseLen) ((FixedArray)toBase).FillWithHoles((int)toStart, (int)toBaseLen);
        }
        if (copySize == 0) return;
        var from = (FixedDoubleArray)fromBase;
        var to = (FixedArray)toBase;
        for (uint i = 0; i < copySize; ++i)
        {
            to.Set((int)(i + toStart), from.Get((int)(i + fromStart)));
        }
    }

    internal static void CopyDoubleToDoubleElements(FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase, uint toStart,
        uint rawCopySize)
    {
        uint fromBaseLen = (uint)fromBase.Length;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = Math.Min(fromBaseLen - fromStart, toBaseLen - toStart);
            ((FixedDoubleArray)toBase).FillWithHoles((int)(toStart + copySize), (int)toBaseLen);
        }
        if (copySize == 0) return;
        var from = (FixedDoubleArray)fromBase;
        var to = (FixedDoubleArray)toBase;
        Array.Copy(from.Data, (int)fromStart, to.Data, (int)toStart, (int)copySize);
    }

    internal static void CopySmiToDoubleElements(FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase, uint toStart,
        uint rawCopySize)
    {
        uint fromBaseLen = (uint)fromBase.Length;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = fromBaseLen - fromStart;
            ((FixedDoubleArray)toBase).FillWithHoles((int)(toStart + copySize), (int)toBaseLen);
        }
        if (copySize == 0) return;
        var from = (FixedArray)fromBase;
        var to = (FixedDoubleArray)toBase;
        for (uint fromEnd = fromStart + copySize; fromStart < fromEnd; fromStart++, toStart++)
        {
            JSValue holeOrSmi = from.Get((int)fromStart);
            if (holeOrSmi.IsTheHole)
            {
                to.SetTheHole((int)toStart);
            }
            else
            {
                to.Set((int)toStart, holeOrSmi.Number);
            }
        }
    }

    internal static void CopyPackedSmiToDoubleElements(FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase, uint toStart,
        uint packedSize, uint rawCopySize)
    {
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = packedSize - fromStart;
            ((FixedDoubleArray)toBase).FillWithHoles((int)(toStart + copySize), (int)toBaseLen);
        }
        if (copySize == 0) return;
        var from = (FixedArray)fromBase;
        var to = (FixedDoubleArray)toBase;
        for (uint fromEnd = fromStart + packedSize; fromStart < fromEnd; fromStart++, toStart++)
        {
            JSValue smi = from.Get((int)fromStart);
            to.Set((int)toStart, smi.Number);
        }
    }

    internal static void CopyObjectToDoubleElements(FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase, uint toStart,
        uint rawCopySize)
    {
        uint fromBaseLen = (uint)fromBase.Length;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = fromBaseLen - fromStart;
            ((FixedDoubleArray)toBase).FillWithHoles((int)(toStart + copySize), (int)toBaseLen);
        }
        if (copySize == 0) return;
        var from = (FixedArray)fromBase;
        var to = (FixedDoubleArray)toBase;
        for (uint fromEnd = fromStart + copySize; fromStart < fromEnd; fromStart++, toStart++)
        {
            JSValue holeOrObject = from.Get((int)fromStart);
            if (holeOrObject.IsTheHole)
            {
                to.SetTheHole((int)toStart);
            }
            else
            {
                to.Set((int)toStart, holeOrObject.Number);
            }
        }
    }

    internal static void CopyDictionaryToDoubleElements(Isolate isolate, FixedArrayBase fromBase, uint fromStart, FixedArrayBase toBase,
        uint toStart, uint rawCopySize)
    {
        var from = (NumberDictionary)fromBase;
        uint toBaseLen = (uint)toBase.Length;
        uint copySize = rawCopySize;
        if (rawCopySize == kCopyToEndAndInitializeToHole)
        {
            copySize = from.MaxNumberKey + 1 - fromStart;
            ((FixedDoubleArray)toBase).FillWithHoles((int)Math.Min(toStart + copySize, toBaseLen), (int)toBaseLen);
        }
        if (copySize == 0) return;
        var to = (FixedDoubleArray)toBase;
        uint toLength = (uint)to.Length;
        if (toStart + copySize > toLength) copySize = toLength - toStart;
        for (uint i = 0; i < copySize; i++)
        {
            InternalIndex entry = from.FindEntry(i + fromStart);
            if (entry.IsFound)
            {
                to.Set((int)(i + toStart), from.ValueAt(entry).Number);
            }
            else
            {
                to.SetTheHole((int)(i + toStart));
            }
        }
    }
}

/// <summary>DictionaryElementsAccessor: DICTIONARY_ELEMENTS, backed by a NumberDictionary.</summary>
internal sealed class DictionaryElementsAccessor() : ElementsAccessor(ElementsKind.DICTIONARY_ELEMENTS)
{
    // We cannot properly estimate this for dictionaries.
    internal override long GetMaxIndex(JSObject receiver, FixedArrayBase elements) =>
        throw new InvalidOperationException("unreachable");

    internal override long GetMaxNumberOfEntries(Isolate isolate, JSObject receiver, FixedArrayBase backingStore) =>
        NumberOfElementsImpl(isolate, receiver, backingStore);

    internal override long NumberOfElementsImpl(Isolate isolate, JSObject receiver, FixedArrayBase backingStore) =>
        ((NumberDictionary)backingStore).NumberOfElements;

    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore)
    {
        var dict = (NumberDictionary)backingStore;
        if (!ObjectOps.ToArrayLength(array.Length, out uint oldLength)) throw new InvalidOperationException("invalid length");
        if (length < oldLength)
        {
            if (dict.RequiresSlowElements)
            {
                // Find last non-deletable element in range of elements to be
                // deleted and adjust range accordingly.
                for (int i = 0; i < dict.Capacity; i++)
                {
                    var entry = new InternalIndex(i);
                    if (!dict.ToKey(entry, out JSValue index)) continue;
                    uint number = (uint)index.Number;
                    if (length <= number && number < oldLength)
                    {
                        PropertyDetails details = dict.DetailsAt(entry);
                        if (!details.IsConfigurable) length = number + 1;
                    }
                }
            }

            if (length == 0)
            {
                // Flush the backing store.
                array.Elements = array.Map.GetInitialElements();
            }
            else
            {
                // Remove elements that should be deleted.
                int removedEntries = 0;
                for (int i = 0; i < dict.Capacity; i++)
                {
                    var entry = new InternalIndex(i);
                    if (!dict.ToKey(entry, out JSValue index)) continue;
                    uint number = (uint)index.Number;
                    if (length <= number && number < oldLength)
                    {
                        dict.ClearEntry(entry);
                        removedEntries++;
                    }
                }

                // Update the number of elements.
                if (removedEntries > 0) dict.ElementsRemoved(removedEntries);
            }
        }

        array.Length = JSValue.FromNumber(length);
        return true;
    }

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry)
    {
        var dict = (NumberDictionary)obj.Elements;
        dict = NumberDictionary.DeleteEntry(isolate, dict, entry);
        obj.Elements = dict;
    }

    internal override bool HasAccessorsImpl(JSObject holder, FixedArrayBase backingStore)
    {
        var dict = (NumberDictionary)backingStore;
        if (!dict.RequiresSlowElements) return false;
        for (int i = 0; i < dict.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dict.ToKey(entry, out _)) continue;
            if (dict.DetailsAt(entry).Kind == PropertyKind.Accessor) return true;
        }
        return false;
    }

    internal override JSValue GetImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        ((NumberDictionary)backingStore).ValueAt(entry);

    internal override void SetImpl(FixedArrayBase backingStore, InternalIndex entry, JSValue value) =>
        ((NumberDictionary)backingStore).ValueAtPut(entry, value);

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes)
    {
        var dictionary = (NumberDictionary)store;
        if (attributes != PropertyAttributes.NONE) obj.RequireSlowElements(dictionary);
        dictionary.ValueAtPut(entry, value);
        PropertyDetails details = dictionary.DetailsAt(entry);
        details = new PropertyDetails(PropertyKind.Data, attributes, PropertyCellType.NoCell, details.DictionaryIndex);
        dictionary.DetailsAtPut(entry, details);
    }

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity)
    {
        var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyCellType.NoCell);
        NumberDictionary dictionary = obj.HasFastElements || obj.HasFastStringWrapperElements
            ? JSObject.NormalizeElements(isolate, obj)
            : (NumberDictionary)obj.Elements;
        NumberDictionary newDictionary = NumberDictionary.Add(dictionary, index, value, details, out _);
        newDictionary.UpdateMaxNumberKey(index, obj);
        if (attributes != PropertyAttributes.NONE) obj.RequireSlowElements(newDictionary);
        if (!ReferenceEquals(dictionary, newDictionary))
        {
            if (obj.HasSloppyArgumentsElements)
            {
                ((SloppyArgumentsElements)obj.Elements).Arguments = newDictionary;
            }
            else
            {
                obj.Elements = newDictionary;
            }
        }
        return true;
    }

    internal override bool HasEntryImpl(Isolate isolate, FixedArrayBase store, InternalIndex entry)
    {
        var dict = (NumberDictionary)store;
        JSValue index = dict.KeyAt(entry);
        return !index.IsTheHole;
    }

    internal override InternalIndex GetEntryForIndexImpl(Isolate isolate, JSObject holder, FixedArrayBase store, ulong index,
        PropertyFilter filter)
    {
        var dictionary = (NumberDictionary)store;
        InternalIndex entry = dictionary.FindEntry((uint)index);
        if (entry.IsNotFound) return entry;

        if (filter != PropertyFilter.ALL_PROPERTIES)
        {
            PropertyDetails details = dictionary.DetailsAt(entry);
            PropertyAttributes attr = details.Attributes;
            if (((int)attr & (int)filter) != 0) return InternalIndex.NotFound;
        }
        return entry;
    }

    internal override PropertyDetails GetDetailsImpl(JSObject holder, InternalIndex entry) => GetDetailsImpl(holder.Elements, entry);

    internal override PropertyDetails GetDetailsImpl(FixedArrayBase backingStore, InternalIndex entry) =>
        ((NumberDictionary)backingStore).DetailsAt(entry);

    static uint FilterKey(NumberDictionary dictionary, InternalIndex entry, JSValue rawKey, PropertyFilter filter)
    {
        PropertyDetails details = dictionary.DetailsAt(entry);
        PropertyAttributes attr = details.Attributes;
        if (((int)attr & (int)filter) != 0) return uint.MaxValue;
        return (uint)rawKey.Number;
    }

    static uint GetKeyForEntryImpl(NumberDictionary dictionary, InternalIndex entry, PropertyFilter filter)
    {
        if (!dictionary.ToKey(entry, out JSValue rawKey)) return uint.MaxValue;
        return FilterKey(dictionary, entry, rawKey, filter);
    }

    internal override void CollectElementIndicesImpl(JSObject obj, FixedArrayBase backingStore, KeyAccumulator keys)
    {
        if ((keys.Filter & PropertyFilter.SKIP_STRINGS) != 0) return;
        Isolate isolate = keys.Isolate;
        var dictionary = (NumberDictionary)backingStore;
        FixedArray elements = isolate.Factory.NewFixedArray((int)GetMaxNumberOfEntries(isolate, obj, backingStore));
        int insertionIndex = 0;
        PropertyFilter filter = keys.Filter;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue rawKey)) continue;
            uint key = FilterKey(dictionary, entry, rawKey, filter);
            if (key == uint.MaxValue)
            {
                keys.AddShadowingKey(rawKey);
                continue;
            }
            elements.Set(insertionIndex, rawKey);
            insertionIndex++;
        }
        SortIndices(elements, insertionIndex);
        for (int i = 0; i < insertionIndex; i++) keys.AddKey(elements.Get(i));
    }

    internal override FixedArray DirectCollectElementIndicesImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore,
        GetKeysConversion convert, PropertyFilter filter, FixedArray list, int maxNofIndices, ref int nofIndices,
        int insertionIndex = 0)
    {
        if ((filter & PropertyFilter.SKIP_STRINGS) != 0) return list;

        var dictionary = (NumberDictionary)backingStore;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            uint key = GetKeyForEntryImpl(dictionary, new InternalIndex(i), filter);
            if (key == uint.MaxValue) continue;
            list.Set(insertionIndex, JSValue.FromNumber(key));
            insertionIndex++;
        }
        nofIndices = insertionIndex;
        return list;
    }

    internal override void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert)
    {
        var dictionary = (NumberDictionary)receiver.Elements;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out _)) continue;
            JSValue value = dictionary.ValueAt(entry);
            accumulator.AddKey(value, convert);
        }
    }

    static bool IncludesValueFastPath(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length,
        out bool result)
    {
        var dictionary = (NumberDictionary)receiver.Elements;

        // Scan for accessor properties. If accessors are present, then elements
        // must be accessed in order via the slow path.
        bool found = false;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue k)) continue;

            if (!ObjectOps.ToArrayIndex(k, out uint index) || index < startFrom || index >= length) continue;

            if (dictionary.DetailsAt(entry).Kind == PropertyKind.Accessor)
            {
                // Restart from beginning in slow path, otherwise we may observably
                // access getters out of order
                result = false;
                return false;
            }
            if (!found)
            {
                JSValue elementK = dictionary.ValueAt(entry);
                if (ObjectOps.SameValueZero(value, elementK)) found = true;
            }
        }

        result = found;
        return true;
    }

    internal override bool IncludesValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        bool searchForHole = value.IsUndefined;

        if (!searchForHole)
        {
            if (IncludesValueFastPath(isolate, receiver, value, startFrom, length, out bool result)) return result;
        }
        var dictionary = (NumberDictionary)receiver.Elements;
        // Iterate through the entire range, as accessing elements out of order is
        // observable.
        for (ulong k = startFrom; k < length; ++k)
        {
            InternalIndex entry = dictionary.FindEntry((uint)k);
            if (entry.IsNotFound)
            {
                if (searchForHole) return true;
                continue;
            }

            PropertyDetails details = GetDetailsImpl(dictionary, entry);
            switch (details.Kind)
            {
                case PropertyKind.Data:
                {
                    JSValue elementK = dictionary.ValueAt(entry);
                    if (ObjectOps.SameValueZero(value, elementK)) return true;
                    break;
                }
                case PropertyKind.Accessor:
                {
                    var it = new LookupIterator(isolate, receiver, k, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSValue elementK = ObjectOps.GetPropertyWithAccessor(ref it);

                    if (ObjectOps.SameValueZero(value, elementK)) return true;

                    // Bailout to slow path if elements on prototype changed
                    if (!JSObject.PrototypeHasNoElements(isolate, receiver))
                    {
                        return IncludesValueSlowPath(isolate, receiver, value, k + 1, length);
                    }

                    // Continue if elements unchanged
                    if (ReferenceEquals(dictionary, receiver.Elements)) continue;

                    // Otherwise, bailout or update elements

                    // If the array became empty, return true if searching for
                    // undefined (and if we'll continue searching beyond this index), and
                    // false otherwise.
                    if (ReferenceEquals(receiver.Map.GetInitialElements(), receiver.Elements))
                    {
                        return searchForHole && k + 1 < length;
                    }

                    // If switched to fast elements, continue with the correct accessor.
                    if (receiver.GetElementsKind() != ElementsKind.DICTIONARY_ELEMENTS)
                    {
                        ElementsAccessor accessor = receiver.GetElementsAccessor();
                        return accessor.IncludesValue(isolate, receiver, value, k + 1, length);
                    }
                    dictionary = (NumberDictionary)receiver.Elements;
                    break;
                }
            }
        }
        return false;
    }

    static bool IndexOfValueFastPath(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length,
        out long result)
    {
        var dictionary = (NumberDictionary)receiver.Elements;

        // Iterate the dictionary's entries directly (O(entries)) instead of
        // probing every index in [start_from, length) (O(length)). This is
        // unobservable as long as no entry is an accessor: reading data values
        // and comparing them with StrictEquals has no side effects, and indexOf
        // skips holes (HasProperty gates the Get in the spec), so the smallest
        // matching entry index is exactly the spec's answer. If accessors are
        // present, elements must be accessed in order via the slow path.
        bool found = false;
        uint foundIndex = 0;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue k)) continue;

            if (!ObjectOps.ToArrayIndex(k, out uint index) || index < startFrom || index >= length) continue;

            if (dictionary.DetailsAt(entry).Kind == PropertyKind.Accessor)
            {
                // Restart from the beginning in the slow path, otherwise we may
                // observably access getters out of order.
                result = -1;
                return false;
            }
            if (!found || index < foundIndex)
            {
                JSValue elementK = dictionary.ValueAt(entry);
                if (ObjectOps.StrictEquals(value, elementK))
                {
                    found = true;
                    foundIndex = index;
                }
            }
        }

        result = found ? foundIndex : -1;
        return true;
    }

    internal override long IndexOfValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        if (IndexOfValueFastPath(isolate, receiver, value, startFrom, length, out long fastResult)) return fastResult;

        var dictionary = (NumberDictionary)receiver.Elements;
        // Iterate through entire range, as accessing elements out of order is
        // observable.
        for (ulong k = startFrom; k < length; ++k)
        {
            InternalIndex entry = dictionary.FindEntry((uint)k);
            if (entry.IsNotFound) continue;

            PropertyDetails details = GetDetailsImpl(dictionary, entry);
            switch (details.Kind)
            {
                case PropertyKind.Data:
                {
                    JSValue elementK = dictionary.ValueAt(entry);
                    if (ObjectOps.StrictEquals(value, elementK)) return (long)k;
                    break;
                }
                case PropertyKind.Accessor:
                {
                    var it = new LookupIterator(isolate, receiver, k, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSValue elementK = ObjectOps.GetPropertyWithAccessor(ref it);

                    if (ObjectOps.StrictEquals(value, elementK)) return (long)k;

                    // Bailout to slow path if elements on prototype changed.
                    if (!JSObject.PrototypeHasNoElements(isolate, receiver))
                    {
                        return IndexOfValueSlowPath(isolate, receiver, value, k + 1, length);
                    }

                    // Continue if elements unchanged.
                    if (ReferenceEquals(dictionary, receiver.Elements)) continue;

                    // Otherwise, bailout or update elements.
                    if (receiver.GetElementsKind() != ElementsKind.DICTIONARY_ELEMENTS)
                    {
                        // Otherwise, switch to slow path.
                        return IndexOfValueSlowPath(isolate, receiver, value, k + 1, length);
                    }
                    dictionary = (NumberDictionary)receiver.Elements;
                    break;
                }
            }
        }
        return -1;
    }
}

/// <summary>V8's Where (AT_START / AT_END) for RemoveElement and AddArguments.</summary>
internal enum Where { AT_START, AT_END }

/// <summary>FastElementsAccessor: the super class of all fast element arrays.</summary>
internal abstract class FastElementsAccessor(ElementsKind kind) : ElementsAccessor(kind)
{
    internal override NumberDictionary NormalizeImpl(Isolate isolate, JSObject obj, FixedArrayBase store)
    {
        ElementsKind kind = Kind;

        // Ensure that notifications fire if the array or object prototypes are
        // normalizing.
        if (ElementsKinds.IsSmiOrObjectElementsKind(kind) || kind == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS)
        {
            isolate.UpdateNoElementsProtectorOnNormalizeElements(obj);
        }

        uint capacity = obj.GetFastElementsUsage();
        NumberDictionary dictionary = NumberDictionary.New((int)capacity);

        PropertyDetails details = PropertyDetails.Empty();
        uint j = 0;
        uint maxNumberKey = 0;
        for (uint i = 0; j < capacity; i++)
        {
            if (ElementsKinds.IsHoleyElementsKindForRead(kind))
            {
                if (IsTheHole(store, (int)i)) continue;
            }
            maxNumberKey = i;
            JSValue value = GetImpl(isolate, store, new InternalIndex((int)i));
            dictionary = NumberDictionary.Add(dictionary, i, value, details, out _);
            j++;
        }

        if (maxNumberKey > 0) dictionary.UpdateMaxNumberKey(maxNumberKey, obj);
        return dictionary;
    }

    internal virtual void DeleteAtEnd(Isolate isolate, JSObject obj, FixedArrayBase backingStore, uint entry)
    {
        uint length = (uint)backingStore.Length;
        for (; entry > 0; entry--)
        {
            if (!IsTheHole(backingStore, (int)entry - 1)) break;
        }
        if (entry == 0)
        {
            FixedArray empty = FixedArray.Empty;
            // Dynamically ask for the elements kind here since we manually redirect
            // the operations for argument backing stores.
            if (obj.GetElementsKind() == ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS)
            {
                ((SloppyArgumentsElements)obj.Elements).Arguments = empty;
            }
            else
            {
                obj.Elements = empty;
            }
            return;
        }

        RightTrim(backingStore, (int)entry);
    }

    internal virtual void DeleteCommon(Isolate isolate, JSObject obj, uint entry, FixedArrayBase store)
    {
        uint storeLen = (uint)store.Length;
        if (obj is not JSArray && entry == storeLen - 1)
        {
            DeleteAtEnd(isolate, obj, store, entry);
            return;
        }

        switch (store)
        {
            case FixedArray fa:
                fa.SetTheHole((int)entry);
                break;
            case FixedDoubleArray fda:
                fda.SetTheHole((int)entry);
                break;
        }

        // TODO(verwaest): Move this out of elements.cc.
        // If the backing store is larger than a certain size and
        // has too few used values, normalize it.
        const uint kMinLengthForSparsenessCheck = 64;
        uint backingStoreLen = (uint)store.Length;
        if (backingStoreLen < kMinLengthForSparsenessCheck) return;
        uint length;
        if (obj is JSArray array)
        {
            ObjectOps.ToArrayLength(array.Length, out length);
        }
        else
        {
            length = storeLen;
        }

        // To avoid doing the check on every delete, use a counter-based heuristic.
        const int kLengthFraction = 16;
        // The above constant must be large enough to ensure that we check for
        // normalization frequently enough. At a minimum, it should be large
        // enough to reliably hit the "window" of remaining elements count where
        // normalization would be beneficial.
        ulong currentCounter = isolate.ElementsDeletionCounter;
        if (currentCounter < length / kLengthFraction)
        {
            isolate.ElementsDeletionCounter = currentCounter + 1;
            return;
        }
        // Reset the counter whenever the full check is performed.
        isolate.ElementsDeletionCounter = 0;

        if (obj is not JSArray)
        {
            uint i;
            for (i = entry + 1; i < length; i++)
            {
                if (!IsTheHole(store, (int)i)) break;
            }
            if (i == length)
            {
                DeleteAtEnd(isolate, obj, store, entry);
                return;
            }
        }
        uint numUsed = 0;
        for (uint i = 0; i < backingStoreLen; ++i)
        {
            if (!IsTheHole(store, (int)i))
            {
                ++numUsed;
                // Bail out if a number dictionary wouldn't be able to save much space.
                if (NumberDictionary.kPreferFastElementsSizeFactor * (uint)NumberDictionary.ComputeCapacity((int)numUsed) *
                    NumberDictionary.kEntrySize > backingStoreLen)
                {
                    return;
                }
            }
        }
        JSObject.NormalizeElements(isolate, obj);
    }

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes)
    {
        NumberDictionary dictionary = JSObject.NormalizeElements(isolate, obj);
        entry = dictionary.FindEntry(entry.AsUInt32);
        ForKind(ElementsKind.DICTIONARY_ELEMENTS).ReconfigureImpl(isolate, obj, dictionary, entry, value, attributes);
    }

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity)
    {
        Debug.Assert(attributes == PropertyAttributes.NONE);
        ElementsKind fromKind = obj.GetElementsKind();
        ElementsKind toKind = Kind;
        if (ElementsKinds.IsDictionaryElementsKind(fromKind) ||
            ElementsKinds.IsDoubleElementsKind(fromKind) != ElementsKinds.IsDoubleElementsKind(toKind) ||
            GetCapacityImpl(obj, obj.Elements) != newCapacity)
        {
            GrowCapacityAndConvertImpl(isolate, obj, newCapacity);
        }
        else
        {
            if (ElementsKinds.IsFastElementsKind(fromKind) && fromKind != toKind)
            {
                JSObject.TransitionElementsKind(isolate, obj, toKind);
            }
            if (ElementsKinds.IsSmiOrObjectElementsKind(fromKind))
            {
                JSObject.EnsureWritableFastElements(isolate, obj);
            }
        }
        SetImpl(obj, new InternalIndex((int)index), value);
        return true;
    }

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry)
    {
        ElementsKind kind = Kind;
        if (ElementsKinds.IsFastPackedElementsKind(kind) || kind == ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS)
        {
            JSObject.TransitionElementsKind(isolate, obj, ElementsKinds.GetHoleyElementsKind(kind));
        }
        if (ElementsKinds.IsSmiOrObjectElementsKind(kind) || ElementsKinds.IsNonextensibleElementsKind(kind))
        {
            JSObject.EnsureWritableFastElements(isolate, obj);
        }
        DeleteCommon(isolate, obj, entry.AsUInt32, obj.Elements);
    }

    internal override bool HasEntryImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        !IsTheHole(backingStore, entry.AsInt);

    internal override long NumberOfElementsImpl(Isolate isolate, JSObject receiver, FixedArrayBase backingStore)
    {
        long maxIndex = GetMaxIndex(receiver, backingStore);
        if (ElementsKinds.IsFastPackedElementsKind(Kind)) return maxIndex;
        uint count = 0;
        for (long i = 0; i < maxIndex; i++)
        {
            if (HasEntryImpl(isolate, backingStore, new InternalIndex((int)i))) count++;
        }
        return count;
    }

    internal override void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert)
    {
        Isolate isolate = accumulator.Isolate;
        FixedArrayBase elements = receiver.Elements;
        long length = GetMaxNumberOfEntries(isolate, receiver, elements);
        for (int i = 0; i < length; i++)
        {
            if (ElementsKinds.IsFastPackedElementsKind(Kind) || HasEntryImpl(isolate, elements, new InternalIndex(i)))
            {
                accumulator.AddKey(GetImpl(isolate, elements, new InternalIndex(i)), convert);
            }
        }
    }

    internal override JSValue PopImpl(Isolate isolate, JSArray receiver) => RemoveElement(isolate, receiver, Where.AT_END);

    internal override JSValue ShiftImpl(Isolate isolate, JSArray receiver) => RemoveElement(isolate, receiver, Where.AT_START);

    internal override uint PushImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        AddArguments(isolate, receiver, receiver.Elements, args, Where.AT_END);

    internal override uint UnshiftImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        AddArguments(isolate, receiver, receiver.Elements, args, Where.AT_START);

    internal void EnsureFillRangeCapacity(Isolate isolate, JSObject receiver, ulong start, ulong end)
    {
        // Make sure we have enough space.
        if ((long)end > GetCapacityImpl(receiver, receiver.Elements))
        {
            GrowCapacityAndConvertImpl(isolate, receiver, (uint)end);
        }
    }

    internal override FixedArray CreateListFromArrayLikeImpl(Isolate isolate, JSObject obj, uint length)
    {
        FixedArray result = isolate.Factory.NewFixedArray((int)length);
        FixedArrayBase elements = obj.Elements;
        for (int i = 0; i < length; i++)
        {
            var entry = new InternalIndex(i);
            if (!HasEntryImpl(isolate, elements, entry)) continue;
            JSValue value = GetImpl(isolate, elements, entry);
            if (value.HeapObjectOrNull is Name name) value = isolate.Factory.InternalizeName(name);
            result.Set(i, value);
        }
        return result;
    }

    internal virtual JSValue RemoveElement(Isolate isolate, JSArray receiver, Where removePosition)
    {
        ElementsKind kind = Kind;
        uint length = (uint)receiver.Length.Number;
        if (length == 0) return JSValue.Undefined;

        if (ElementsKinds.IsSmiOrObjectElementsKind(kind)) JSObject.EnsureWritableFastElements(isolate, receiver);

        uint newLength = length - 1;
        uint removeIndex = removePosition == Where.AT_START ? 0 : newLength;
        JSValue result = GetImpl(isolate, receiver.Elements, new InternalIndex((int)removeIndex));
        if (newLength == 0)
        {
            receiver.Elements = receiver.Map.GetInitialElements();
        }
        else
        {
            FixedArrayBase dstElms = receiver.Elements;
            if (removePosition == Where.AT_START)
            {
                switch (dstElms)
                {
                    case FixedArray fa:
                        fa.MoveElements(0, 1, (int)newLength);
                        break;
                    case FixedDoubleArray fda:
                        fda.MoveElements(0, 1, (int)newLength);
                        break;
                }
            }
            FillWithHoles(dstElms, (int)newLength, (int)newLength + 1);
            DecreaseLength(isolate, dstElms, length, newLength);
        }
        receiver.Length = JSValue.FromNumber(newLength);

        if (ElementsKinds.IsHoleyElementsKind(kind) && result.IsTheHole) return JSValue.Undefined;
        return result;
    }

    internal uint AddArguments(Isolate isolate, JSArray receiver, FixedArrayBase backingStore, ReadOnlySpan<JSValue> args,
        Where addPosition)
    {
        uint addSize = (uint)args.Length;
        uint length = (uint)receiver.Length.Number;
        uint elmsLen = (uint)backingStore.Length;
        // Check we do not overflow the new_length.
        uint newLength = length + addSize;

        if (newLength > elmsLen)
        {
            // New backing storage is needed.
            uint capacity = JSObject.NewElementsCapacity(newLength);
            // If we add arguments to the start we have to shift the existing objects.
            uint copyDstIndex = addPosition == Where.AT_START ? addSize : 0;
            // Copy over all objects to a new backing_store.
            backingStore = ConvertElementsWithCapacity(isolate, receiver, backingStore, Kind, capacity, 0, copyDstIndex);
            receiver.Elements = backingStore;
        }
        else if (addPosition == Where.AT_START)
        {
            // If the backing store has enough capacity and we add elements to the
            // start we have to shift the existing objects.
            switch (backingStore)
            {
                case FixedArray fa:
                    fa.MoveElements((int)addSize, 0, (int)length);
                    break;
                case FixedDoubleArray fda:
                    fda.MoveElements((int)addSize, 0, (int)length);
                    break;
            }
        }

        uint insertionIndex = addPosition == Where.AT_START ? 0 : length;
        // Copy the arguments to the start.
        for (int i = 0; i < args.Length; i++)
        {
            SetImpl(backingStore, new InternalIndex((int)insertionIndex + i), args[i]);
        }
        // Set the length.
        receiver.Length = JSValue.FromNumber(newLength);
        return newLength;
    }

    internal override bool IncludesValueImpl(Isolate isolate, JSObject receiver, JSValue searchValue, ulong startFrom, ulong length)
    {
        FixedArrayBase elementsBase = receiver.Elements;
        JSValue value = searchValue;

        if (startFrom >= length) return false;

        // Elements beyond the capacity of the backing store treated as undefined.
        uint elementsLength = (uint)elementsBase.Length;
        if (value.IsUndefined && elementsLength < length) return true;
        if (elementsLength == 0) return false;

        length = Math.Min(elementsLength, length);

        if (!value.IsNumber)
        {
            if (value.IsUndefined)
            {
                // Search for `undefined` or The Hole. Even in the case of
                // PACKED_DOUBLE_ELEMENTS or PACKED_SMI_ELEMENTS, we might encounter The
                // Hole here, since the {length} used here can be larger than
                // JSArray::length.
                if (ElementsKinds.IsSmiOrObjectElementsKind(Kind) || ElementsKinds.IsAnyNonextensibleElementsKind(Kind))
                {
                    var elements = (FixedArray)elementsBase;
                    for (ulong k = startFrom; k < length; ++k)
                    {
                        JSValue elementK = elements.Get((int)k);
                        if (elementK.IsTheHole || elementK.IsUndefined) return true;
                    }
                    return false;
                }
                else
                {
                    // Search for The Hole in HOLEY_DOUBLE_ELEMENTS or
                    // PACKED_DOUBLE_ELEMENTS.
                    var elements = (FixedDoubleArray)elementsBase;
                    for (ulong k = startFrom; k < length; ++k)
                    {
                        if (elements.IsTheHole((int)k)) return true;
                    }
                    return false;
                }
            }
            else if (!ElementsKinds.IsObjectElementsKind(Kind) && !ElementsKinds.IsAnyNonextensibleElementsKind(Kind))
            {
                // Search for non-number, non-Undefined value, with either
                // PACKED_SMI_ELEMENTS, PACKED_DOUBLE_ELEMENTS, HOLEY_SMI_ELEMENTS or
                // HOLEY_DOUBLE_ELEMENTS. Guaranteed to return false, since these
                // elements kinds can only contain Number values or undefined.
                return false;
            }
            else
            {
                // Search for non-number, non-Undefined value with either
                // PACKED_ELEMENTS or HOLEY_ELEMENTS.
                var elements = (FixedArray)elementsBase;
                for (ulong k = startFrom; k < length; ++k)
                {
                    JSValue elementK = elements.Get((int)k);
                    if (elementK.IsTheHole) continue;
                    if (ObjectOps.SameValueZero(value, elementK)) return true;
                }
                return false;
            }
        }
        if (!double.IsNaN(value.Number))
        {
            double searchNumber = value.Number;
            if (ElementsKinds.IsDoubleElementsKind(Kind))
            {
                // Search for non-NaN Number in PACKED_DOUBLE_ELEMENTS or
                // HOLEY_DOUBLE_ELEMENTS --- Skip TheHole, and trust UCOMISD or
                // similar operation for result.
                var elements = (FixedDoubleArray)elementsBase;
                for (ulong k = startFrom; k < length; ++k)
                {
                    if (elements.IsTheHole((int)k)) continue;
                    if (elements.GetScalar((int)k) == searchNumber) return true;
                }
                return false;
            }
            else
            {
                // Search for non-NaN Number in PACKED_ELEMENTS, HOLEY_ELEMENTS,
                // PACKED_SMI_ELEMENTS or HOLEY_SMI_ELEMENTS --- Skip non-Numbers,
                // and trust UCOMISD or similar operation for result
                var elements = (FixedArray)elementsBase;
                for (ulong k = startFrom; k < length; ++k)
                {
                    JSValue elementK = elements.Get((int)k);
                    if (elementK.IsNumber && elementK.Number == searchNumber) return true;
                }
                return false;
            }
        }
        // Search for NaN --- NaN cannot be represented with Smi elements, so
        // abort if ElementsKind is PACKED_SMI_ELEMENTS or HOLEY_SMI_ELEMENTS
        if (ElementsKinds.IsSmiElementsKind(Kind)) return false;

        if (ElementsKinds.IsDoubleElementsKind(Kind))
        {
            // Search for NaN in PACKED_DOUBLE_ELEMENTS or
            // HOLEY_DOUBLE_ELEMENTS --- Skip The Hole and trust
            // std::isnan(elementK) for result
            var elements = (FixedDoubleArray)elementsBase;
            for (ulong k = startFrom; k < length; ++k)
            {
                if (elements.IsTheHole((int)k)) continue;
                if (double.IsNaN(elements.GetScalar((int)k))) return true;
            }
            return false;
        }
        else
        {
            // Search for NaN in PACKED_ELEMENTS or HOLEY_ELEMENTS. Return true
            // if elementK->IsHeapNumber() && std::isnan(elementK->Number())
            var elements = (FixedArray)elementsBase;
            for (ulong k = startFrom; k < length; ++k)
            {
                JSValue elementK = elements.Get((int)k);
                if (elementK.IsNumber && double.IsNaN(elementK.Number)) return true;
            }
            return false;
        }
    }
}

/// <summary>FastSmiOrObjectElementsAccessor: PACKED/HOLEY SMI and OBJECT elements (FixedArray).</summary>
internal class FastSmiOrObjectElementsAccessor(ElementsKind kind) : FastElementsAccessor(kind)
{
    internal override void SetImpl(FixedArrayBase backingStore, InternalIndex entry, JSValue value) =>
        ((FixedArray)backingStore).Set(entry.AsInt, value);

    internal override JSValue FillImpl(Isolate isolate, JSObject receiver, JSValue value, ulong start, ulong end)
    {
        // Make sure COW arrays are copied.
        JSObject.EnsureWritableFastElements(isolate, receiver);

        EnsureFillRangeCapacity(isolate, receiver, start, end);

        var elements = (FixedArray)receiver.Elements;
        elements.Data.AsSpan((int)start, (int)(end - start)).Fill(value);
        return receiver;
    }

    // NOTE: this method violates the handlified function signature convention:
    // raw pointer parameters in the function that allocates.
    // See ElementsAccessor::CopyElements() for details.
    // This method could actually allocate if copying from double elements to
    // object elements.
    internal override void CopyElementsImpl(Isolate isolate, FixedArrayBase from, uint fromStart, FixedArrayBase to,
        ElementsKind fromKind, uint toStart, uint packedSize, uint copySize)
    {
        ElementsKind toKind = Kind;
        switch (fromKind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            case ElementsKind.PACKED_ELEMENTS:
            case ElementsKind.PACKED_FROZEN_ELEMENTS:
            case ElementsKind.PACKED_SEALED_ELEMENTS:
            case ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.HOLEY_ELEMENTS:
            case ElementsKind.HOLEY_FROZEN_ELEMENTS:
            case ElementsKind.HOLEY_SEALED_ELEMENTS:
            case ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.SHARED_ARRAY_ELEMENTS:
                CopyObjectToObjectElements(isolate, from, fromKind, fromStart, to, toKind, toStart, copySize);
                break;
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                CopyDoubleToObjectElements(isolate, from, fromStart, to, toStart, copySize);
                break;
            case ElementsKind.DICTIONARY_ELEMENTS:
                CopyDictionaryToObjectElements(isolate, from, fromStart, to, toKind, toStart, copySize);
                break;
            case ElementsKind.NO_ELEMENTS:
                break;  // Nothing to do.
            default:
                // This function is currently only used for JSArrays with non-zero
                // length.
                throw new InvalidOperationException("unreachable");
        }
    }

    internal override bool CollectValuesOrEntriesImpl(Isolate isolate, JSObject obj, FixedArray valuesOrEntries, int maxNofItems,
        bool getEntries, ref int nofItems, PropertyFilter filter)
    {
        int count = 0;
        var elements = (FixedArray)obj.Elements;
        int length = elements.Length;
        for (int index = 0; index < length; ++index)
        {
            var entry = new InternalIndex(index);
            if (!HasEntryImpl(isolate, elements, entry)) continue;
            JSValue value = elements.Get(index);
            if (getEntries) value = MakeEntryPair(isolate, (ulong)index, value);
            valuesOrEntries.Set(count++, value);
        }
        nofItems = count;
        return true;
    }

    internal override long IndexOfValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        FixedArrayBase elementsBase = receiver.Elements;

        if (startFrom >= length) return -1;

        length = Math.Min((ulong)elementsBase.Length, length);

        // Only FAST_{,HOLEY_}ELEMENTS can store non-numbers.
        if (!value.IsNumber && !ElementsKinds.IsObjectElementsKind(Kind) && !ElementsKinds.IsAnyNonextensibleElementsKind(Kind))
        {
            return -1;
        }
        // NaN can never be found by strict equality.
        if (value.IsNumber && double.IsNaN(value.Number)) return -1;

        // k can be greater than receiver->length() below, but it is bounded by
        // elements_base->length() so we never read out of bounds. This means that
        // elements->get(k) can return the hole, for which the StrictEquals will
        // always fail.
        var elements = (FixedArray)elementsBase;
        for (ulong k = startFrom; k < length; ++k)
        {
            if (ObjectOps.StrictEquals(value, elements.Get((int)k))) return (long)k;
        }
        return -1;
    }
}

/// <summary>FastNonextensibleObjectElementsAccessor: PACKED/HOLEY_NONEXTENSIBLE_ELEMENTS.</summary>
internal sealed class FastNonextensibleObjectElementsAccessor(ElementsKind kind, DictionaryElementsAccessor dictionary)
    : FastSmiOrObjectElementsAccessor(kind)
{
    internal override uint PushImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        throw new InvalidOperationException("unreachable");

    internal override JSValue PopImpl(Isolate isolate, JSArray receiver) => throw new InvalidOperationException("unreachable");

    internal override JSValue ShiftImpl(Isolate isolate, JSArray receiver) => throw new InvalidOperationException("unreachable");

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity) =>
        throw new InvalidOperationException("unreachable");

    // TODO(duongn): refactor this due to code duplication of sealed version.
    // Consider using JSObject::NormalizeElements(). Also consider follow the fast
    // element logic instead of changing to dictionary mode.
    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore) =>
        SetLengthToDictionary(isolate, array, length, dictionary, PropertyAttributes.NONE);

    /// <summary>The shared body of the nonextensible and sealed SetLengthImpl: transition to DICTIONARY_ELEMENTS.</summary>
    internal static bool SetLengthToDictionary(Isolate isolate, JSArray array, uint length, DictionaryElementsAccessor dictionary,
        PropertyAttributes attributes)
    {
        if (!ObjectOps.ToArrayIndex(array.Length, out uint oldLength)) throw new InvalidOperationException("invalid length");
        if (length == oldLength)
        {
            // Do nothing.
            return true;
        }

        // Transition to DICTIONARY_ELEMENTS.
        // Convert to dictionary mode.
        NumberDictionary newElementDictionary = oldLength == 0
            ? ReadOnlyRoots.empty_slow_element_dictionary
            : array.GetElementsAccessor().Normalize(isolate, array);

        // Migrate map.
        Map newMap = Map.Copy(isolate, array.Map, "SlowCopyForSetLengthImpl");
        newMap.IsExtensible = false;
        newMap.SetElementsKind(ElementsKind.DICTIONARY_ELEMENTS);
        JSObject.MigrateToMap(isolate, array, newMap);

        array.Elements = newElementDictionary;

        if (!ReferenceEquals(array.Elements, ReadOnlyRoots.empty_slow_element_dictionary))
        {
            NumberDictionary dict = array.ElementDictionary;
            // Make sure we never go back to the fast case
            array.RequireSlowElements(dict);
            JSObject.ApplyAttributesToDictionary(isolate, dict, attributes);
        }

        // Set length.
        return dictionary.SetLengthImpl(isolate, array, length, array.Elements);
    }
}

/// <summary>FastSealedObjectElementsAccessor: PACKED/HOLEY_SEALED_ELEMENTS (and SHARED_ARRAY_ELEMENTS).</summary>
internal sealed class FastSealedObjectElementsAccessor(ElementsKind kind, DictionaryElementsAccessor dictionary)
    : FastSmiOrObjectElementsAccessor(kind)
{
    internal override JSValue RemoveElement(Isolate isolate, JSArray receiver, Where removePosition) =>
        throw new InvalidOperationException("unreachable");

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry) =>
        throw new InvalidOperationException("unreachable");

    internal override void DeleteAtEnd(Isolate isolate, JSObject obj, FixedArrayBase backingStore, uint entry) =>
        throw new InvalidOperationException("unreachable");

    internal override void DeleteCommon(Isolate isolate, JSObject obj, uint entry, FixedArrayBase store) =>
        throw new InvalidOperationException("unreachable");

    internal override JSValue PopImpl(Isolate isolate, JSArray receiver) => throw new InvalidOperationException("unreachable");

    internal override uint PushImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        throw new InvalidOperationException("unreachable");

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity) =>
        throw new InvalidOperationException("unreachable");

    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore) =>
        FastNonextensibleObjectElementsAccessor.SetLengthToDictionary(isolate, array, length, dictionary, PropertyAttributes.SEALED);
}

/// <summary>FastFrozenObjectElementsAccessor: PACKED/HOLEY_FROZEN_ELEMENTS; every mutation is unreachable.</summary>
internal sealed class FastFrozenObjectElementsAccessor(ElementsKind kind) : FastSmiOrObjectElementsAccessor(kind)
{
    internal override void SetImpl(FixedArrayBase backingStore, InternalIndex entry, JSValue value) =>
        throw new InvalidOperationException("unreachable");

    internal override void SetImpl(JSObject holder, InternalIndex entry, JSValue value) =>
        throw new InvalidOperationException("unreachable");

    internal override JSValue RemoveElement(Isolate isolate, JSArray receiver, Where removePosition) =>
        throw new InvalidOperationException("unreachable");

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry) =>
        throw new InvalidOperationException("unreachable");

    internal override JSValue PopImpl(Isolate isolate, JSArray receiver) => throw new InvalidOperationException("unreachable");

    internal override uint PushImpl(Isolate isolate, JSArray receiver, ReadOnlySpan<JSValue> args) =>
        throw new InvalidOperationException("unreachable");

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity) =>
        throw new InvalidOperationException("unreachable");

    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore) =>
        throw new InvalidOperationException("unreachable");

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes) =>
        throw new InvalidOperationException("unreachable");
}

/// <summary>FastDoubleElementsAccessor: PACKED/HOLEY_DOUBLE_ELEMENTS (FixedDoubleArray).</summary>
internal sealed class FastDoubleElementsAccessor(ElementsKind kind) : FastElementsAccessor(kind)
{
    internal override JSValue GetImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        ((FixedDoubleArray)backingStore).Get(entry.AsInt);

    internal override void SetImpl(FixedArrayBase backingStore, InternalIndex entry, JSValue value) =>
        ((FixedDoubleArray)backingStore).Set(entry.AsInt, value.Number);

    internal override JSValue FillImpl(Isolate isolate, JSObject receiver, JSValue objValue, ulong start, ulong end)
    {
        EnsureFillRangeCapacity(isolate, receiver, start, end);

        var elements = (FixedDoubleArray)receiver.Elements;
        double value = objValue.Number;
        if (double.IsNaN(value)) value = double.NaN;
        elements.Data.AsSpan((int)start, (int)(end - start)).Fill(value);
        return receiver;
    }

    internal override void CopyElementsImpl(Isolate isolate, FixedArrayBase from, uint fromStart, FixedArrayBase to,
        ElementsKind fromKind, uint toStart, uint packedSize, uint copySize)
    {
        switch (fromKind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
                CopyPackedSmiToDoubleElements(from, fromStart, to, toStart, packedSize, copySize);
                break;
            case ElementsKind.HOLEY_SMI_ELEMENTS:
                CopySmiToDoubleElements(from, fromStart, to, toStart, copySize);
                break;
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                CopyDoubleToDoubleElements(from, fromStart, to, toStart, copySize);
                break;
            case ElementsKind.PACKED_ELEMENTS:
            case ElementsKind.PACKED_FROZEN_ELEMENTS:
            case ElementsKind.PACKED_SEALED_ELEMENTS:
            case ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.HOLEY_ELEMENTS:
            case ElementsKind.HOLEY_FROZEN_ELEMENTS:
            case ElementsKind.HOLEY_SEALED_ELEMENTS:
            case ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.SHARED_ARRAY_ELEMENTS:
                CopyObjectToDoubleElements(from, fromStart, to, toStart, copySize);
                break;
            case ElementsKind.DICTIONARY_ELEMENTS:
                CopyDictionaryToDoubleElements(isolate, from, fromStart, to, toStart, copySize);
                break;
            default:
                // This function is currently only used for JSArrays with non-zero
                // length.
                throw new InvalidOperationException("unreachable");
        }
    }

    internal override bool CollectValuesOrEntriesImpl(Isolate isolate, JSObject obj, FixedArray valuesOrEntries, int maxNofItems,
        bool getEntries, ref int nofItems, PropertyFilter filter)
    {
        var elements = (FixedDoubleArray)obj.Elements;
        int count = 0;
        int length = elements.Length;
        for (int index = 0; index < length; ++index)
        {
            var entry = new InternalIndex(index);
            if (!HasEntryImpl(isolate, elements, entry)) continue;
            JSValue value = GetImpl(isolate, elements, entry);
            if (getEntries) value = MakeEntryPair(isolate, (ulong)index, value);
            valuesOrEntries.Set(count++, value);
        }
        nofItems = count;
        return true;
    }

    internal override long IndexOfValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        FixedArrayBase elementsBase = receiver.Elements;

        length = Math.Min((ulong)elementsBase.Length, length);

        if (startFrom >= length) return -1;

        if (!value.IsNumber) return -1;
        if (double.IsNaN(value.Number)) return -1;
        double numericSearchValue = value.Number;
        var elements = (FixedDoubleArray)receiver.Elements;

        for (ulong k = startFrom; k < length; ++k)
        {
            if (elements.IsTheHole((int)k)) continue;
            if (elements.GetScalar((int)k) == numericSearchValue) return (long)k;
        }
        return -1;
    }
}

/// <summary>
/// TypedElementsAccessor&lt;Kind&gt;: typed arrays (fixed-length and RAB/GSAB).
/// The element type is chosen by a switch on the kind (V8 instantiates one
/// template per kind).
/// </summary>
internal sealed class TypedElementsAccessor(ElementsKind kind) : ElementsAccessor(kind)
{
    readonly int _elementSize = ElementsKinds.ElementsKindToByteSize(kind);
    readonly ElementsKind _baseKind = ElementsKinds.IsRabGsabTypedArrayElementsKind(kind)
        ? ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind)
        : kind;

    /// <summary>TypedElementsAccessor::FromObject: the value (a Number, BigInt or undefined) converted to the raw element.</summary>
    ulong FromObject(JSValue value, out bool lossless)
    {
        lossless = true;
        switch (_baseKind)
        {
            case ElementsKind.BIGINT64_ELEMENTS:
                return (ulong)BigInt.AsInt64((BigInt)value.Object, out lossless);
            case ElementsKind.BIGUINT64_ELEMENTS:
                return BigInt.AsUint64((BigInt)value.Object, out lossless);
        }
        double d = value.IsUndefined ? double.NaN : value.Number;
        return FromScalar(d);
    }

    /// <summary>TypedElementsAccessor::FromScalar(double) for the non-BigInt kinds.</summary>
    ulong FromScalar(double value)
    {
        switch (_baseKind)
        {
            case ElementsKind.UINT8_CLAMPED_ELEMENTS:
                // Handle NaNs and less than zero values which clamp to zero.
                if (!(value > 0)) return 0;
                if (value > 0xFF) return 0xFF;
                return (byte)Math.Round(value, MidpointRounding.ToEven);  // lrint: round half to even.
            case ElementsKind.FLOAT32_ELEMENTS:
                return BitConverter.SingleToUInt32Bits(DoubleToFloat32(value));
            case ElementsKind.FLOAT64_ELEMENTS:
                return BitConverter.DoubleToUInt64Bits(value);
            case ElementsKind.FLOAT16_ELEMENTS:
                return BitConverter.HalfToUInt16Bits((Half)value);
            default:
                return unchecked((ulong)(long)Conversions.DoubleToInt32(value));
        }
    }

    /// <summary>DoubleToFloat32 (conversions-inl.h): round to nearest float, overflow to infinity.</summary>
    static float DoubleToFloat32(double x) => (float)x;

    /// <summary>The raw bits of element <paramref name="index"/>, zero-extended.</summary>
    ulong ReadRaw(JSTypedArray array, ulong index)
    {
        ReadOnlySpan<byte> data = array.DataSpan()[(int)(index * (ulong)_elementSize)..];
        return _elementSize switch
        {
            1 => data[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(data),
        };
    }

    void WriteRaw(JSTypedArray array, ulong index, ulong raw)
    {
        Span<byte> data = array.DataSpan()[(int)(index * (ulong)_elementSize)..];
        switch (_elementSize)
        {
            case 1:
                data[0] = (byte)raw;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)raw);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)raw);
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(data, raw);
                break;
        }
    }

    /// <summary>TypedElementsAccessor::ToHandle: the JS value of a raw element.</summary>
    JSValue ToValue(Isolate isolate, ulong raw) => _baseKind switch
    {
        ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS => JSValue.FromInt((byte)raw),
        ElementsKind.INT8_ELEMENTS => JSValue.FromInt((sbyte)raw),
        ElementsKind.UINT16_ELEMENTS => JSValue.FromInt((ushort)raw),
        ElementsKind.INT16_ELEMENTS => JSValue.FromInt((short)raw),
        ElementsKind.UINT32_ELEMENTS => JSValue.FromNumber((uint)raw),
        ElementsKind.INT32_ELEMENTS => JSValue.FromInt((int)raw),
        ElementsKind.FLOAT32_ELEMENTS => JSValue.FromNumber(BitConverter.UInt32BitsToSingle((uint)raw)),
        ElementsKind.FLOAT64_ELEMENTS => JSValue.FromNumber(BitConverter.UInt64BitsToDouble(raw)),
        ElementsKind.FLOAT16_ELEMENTS => JSValue.FromNumber((double)BitConverter.UInt16BitsToHalf((ushort)raw)),
        ElementsKind.BIGINT64_ELEMENTS => BigInt.FromInt64(isolate, (long)raw),
        ElementsKind.BIGUINT64_ELEMENTS => BigInt.FromUint64(isolate, raw),
        _ => throw new InvalidOperationException("unreachable"),
    };

    /// <summary>The element as a double (for the search helpers).</summary>
    double ToDouble(ulong raw) => _baseKind switch
    {
        ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS => (byte)raw,
        ElementsKind.INT8_ELEMENTS => (sbyte)raw,
        ElementsKind.UINT16_ELEMENTS => (ushort)raw,
        ElementsKind.INT16_ELEMENTS => (short)raw,
        ElementsKind.UINT32_ELEMENTS => (uint)raw,
        ElementsKind.INT32_ELEMENTS => (int)raw,
        ElementsKind.FLOAT32_ELEMENTS => BitConverter.UInt32BitsToSingle((uint)raw),
        ElementsKind.FLOAT64_ELEMENTS => BitConverter.UInt64BitsToDouble(raw),
        ElementsKind.FLOAT16_ELEMENTS => (double)BitConverter.UInt16BitsToHalf((ushort)raw),
        _ => throw new InvalidOperationException("unreachable"),
    };

    bool IsBigIntKind => _baseKind is ElementsKind.BIGINT64_ELEMENTS or ElementsKind.BIGUINT64_ELEMENTS;

    bool IsFloatKind => _baseKind is ElementsKind.FLOAT32_ELEMENTS or ElementsKind.FLOAT64_ELEMENTS or ElementsKind.FLOAT16_ELEMENTS;

    internal override void SetImpl(JSObject holder, InternalIndex entry, JSValue value)
    {
        var typedArray = (JSTypedArray)holder;
        WriteRaw(typedArray, entry.AsUInt32, FromObject(value, out _));
    }

    internal override JSValue GetInternalImpl(Isolate isolate, JSObject holder, InternalIndex entry)
    {
        var typedArray = (JSTypedArray)holder;
        return ToValue(isolate, ReadRaw(typedArray, entry.AsUInt32));
    }

    internal override JSValue GetImpl(Isolate isolate, FixedArrayBase backingStore, InternalIndex entry) =>
        throw new InvalidOperationException("unreachable");

    internal override PropertyDetails GetDetailsImpl(JSObject holder, InternalIndex entry)
    {
        var typedArray = (JSTypedArray)holder;
        if (typedArray.Buffer.IsImmutable)
        {
            return new PropertyDetails(PropertyKind.Data, PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY,
                PropertyCellType.NoCell);
        }
        return new PropertyDetails(PropertyKind.Data, PropertyAttributes.NONE, PropertyCellType.NoCell);
    }

    internal override bool HasElementImpl(Isolate isolate, JSObject holder, ulong index, FixedArrayBase backingStore,
        PropertyFilter filter = PropertyFilter.ALL_PROPERTIES) =>
        (long)index < GetCapacityImpl(holder, backingStore);

    internal override bool HasAccessorsImpl(JSObject holder, FixedArrayBase backingStore) => false;

    // External arrays do not support changing their length.
    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase backingStore) =>
        throw new InvalidOperationException("unreachable");

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry)
    {
        // Do nothing.
        //
        // TypedArray elements are configurable to explain detaching, but cannot be
        // deleted otherwise.
    }

    internal override InternalIndex GetEntryForIndexImpl(Isolate isolate, JSObject holder, FixedArrayBase backingStore, ulong index,
        PropertyFilter filter) =>
        (long)index < GetCapacityImpl(holder, backingStore) ? new InternalIndex((int)index) : InternalIndex.NotFound;

    internal override long GetCapacityImpl(JSObject holder, FixedArrayBase backingStore) => (long)((JSTypedArray)holder).GetLength();

    internal override long GetMaxIndex(JSObject receiver, FixedArrayBase elements) => GetCapacityImpl(receiver, elements);

    internal override long NumberOfElementsImpl(Isolate isolate, JSObject receiver, FixedArrayBase backingStore) =>
        GetCapacityImpl(receiver, backingStore);

    internal override void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert)
    {
        Isolate isolate = accumulator.Isolate;
        long length = GetCapacityImpl(receiver, receiver.Elements);
        for (long i = 0; i < length; i++)
        {
            accumulator.AddKey(GetInternalImpl(isolate, receiver, new InternalIndex((int)i)), convert);
        }
    }

    internal override bool CollectValuesOrEntriesImpl(Isolate isolate, JSObject obj, FixedArray valuesOrEntries, int maxNofItems,
        bool getEntries, ref int nofItems, PropertyFilter filter)
    {
        int count = 0;
        if ((filter & PropertyFilter.ONLY_CONFIGURABLE) == 0)
        {
            long length = GetCapacityImpl(obj, obj.Elements);
            // The TypedArray might have been grown by a background thread. Handle it
            // gracefully.
            if (length > maxNofItems) length = maxNofItems;
            for (long index = 0; index < length; ++index)
            {
                JSValue value = GetInternalImpl(isolate, obj, new InternalIndex((int)index));
                if (getEntries) value = MakeEntryPair(isolate, (ulong)index, value);
                valuesOrEntries.Set(count++, value);
            }
        }
        nofItems = count;
        return true;
    }

    /// <summary>ToTypedSearchValue: true if the search value can't be represented in this type (loss of precision).</summary>
    bool ToTypedSearchValue(double searchValue, out ulong typedSearchValue)
    {
        if (_baseKind == ElementsKind.FLOAT16_ELEMENTS)
        {
            Half h = (Half)(float)searchValue;
            typedSearchValue = BitConverter.HalfToUInt16Bits(h);
            return (double)(float)h != searchValue;  // Loss of precision.
        }
        typedSearchValue = FromScalar(searchValue);
        if (_baseKind == ElementsKind.FLOAT32_ELEMENTS)
        {
            return (double)BitConverter.UInt32BitsToSingle((uint)typedSearchValue) != searchValue;
        }
        if (_baseKind == ElementsKind.FLOAT64_ELEMENTS) return false;
        // Integral types: the value must be in range and integral.
        (double min, double max) = _baseKind switch
        {
            ElementsKind.UINT8_ELEMENTS or ElementsKind.UINT8_CLAMPED_ELEMENTS => (0.0, 255.0),
            ElementsKind.INT8_ELEMENTS => (-128.0, 127.0),
            ElementsKind.UINT16_ELEMENTS => (0.0, 65535.0),
            ElementsKind.INT16_ELEMENTS => (-32768.0, 32767.0),
            ElementsKind.UINT32_ELEMENTS => (0.0, 4294967295.0),
            _ => (-2147483648.0, 2147483647.0),
        };
        if (!(searchValue >= min && searchValue <= max) && double.IsFinite(searchValue)) return true;
        return ToDouble(typedSearchValue) != searchValue;
    }

    static bool IsFloat16RawBitsZero(ulong x) => (x & ~0x8000UL) == 0;

    internal override JSValue FillImpl(Isolate isolate, JSObject receiver, JSValue value, ulong start, ulong end)
    {
        var typedArray = (JSTypedArray)receiver;
        ulong scalar = FromObject(value, out _);
        for (ulong i = start; i < end; i++) WriteRaw(typedArray, i, scalar);
        return typedArray;
    }

    internal override bool IncludesValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        var typedArray = (JSTypedArray)receiver;

        ulong newLength = typedArray.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds) return value.IsUndefined && length > startFrom;

        // Prototype has no elements, and not searching for the hole --- limit
        // search to backing store length.
        if (newLength < length)
        {
            if (value.IsUndefined && length > startFrom) return true;
            length = newLength;
        }

        ulong typedSearchValue;
        if (IsBigIntKind)
        {
            if (!value.IsBigInt) return false;
            typedSearchValue = FromObject(value, out bool lossless);
            if (!lossless) return false;
        }
        else
        {
            if (!value.IsNumber) return false;
            double searchValue = value.Number;
            if (!double.IsFinite(searchValue))
            {
                // Integral types cannot represent +Inf or NaN.
                if (!IsFloatKind) return false;
                if (double.IsNaN(searchValue))
                {
                    for (ulong k = startFrom; k < length; ++k)
                    {
                        if (double.IsNaN(ToDouble(ReadRaw(typedArray, k)))) return true;
                    }
                    return false;
                }
            }
            else if (searchValue == 0 && _baseKind == ElementsKind.FLOAT16_ELEMENTS)
            {
                for (ulong k = startFrom; k < length; ++k)
                {
                    if (IsFloat16RawBitsZero(ReadRaw(typedArray, k))) return true;
                }
                return false;
            }

            if (ToTypedSearchValue(searchValue, out typedSearchValue)) return false;
        }

        for (ulong k = startFrom; k < length; ++k)
        {
            ulong elemK = ReadRaw(typedArray, k);
            if (IsFloatKind ? ToDouble(elemK) == ToDouble(typedSearchValue) : elemK == typedSearchValue) return true;
        }
        return false;
    }

    internal override long IndexOfValueImpl(Isolate isolate, JSObject receiver, JSValue value, ulong startFrom, ulong length)
    {
        var typedArray = (JSTypedArray)receiver;

        // If this is called via Array.prototype.indexOf (not
        // TypedArray.prototype.indexOf), it's possible that the TypedArray is
        // detached / out of bounds here.
        if (typedArray.WasDetached) return -1;
        ulong typedArrayLength = typedArray.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds) return -1;

        // Prototype has no elements, and not searching for the hole --- limit
        // search to backing store length.
        if (typedArrayLength < length) length = typedArrayLength;

        ulong typedSearchValue;
        if (IsBigIntKind)
        {
            if (!value.IsBigInt) return -1;
            typedSearchValue = FromObject(value, out bool lossless);
            if (!lossless) return -1;
        }
        else
        {
            if (!value.IsNumber) return -1;
            double searchValue = value.Number;
            if (!double.IsFinite(searchValue))
            {
                // Integral types cannot represent +Inf or NaN.
                if (!IsFloatKind) return -1;
                if (double.IsNaN(searchValue)) return -1;
            }
            else if (searchValue == 0 && _baseKind == ElementsKind.FLOAT16_ELEMENTS)
            {
                for (ulong k = startFrom; k < length; ++k)
                {
                    if (IsFloat16RawBitsZero(ReadRaw(typedArray, k))) return (long)k;
                }
                return -1;
            }
            if (ToTypedSearchValue(searchValue, out typedSearchValue)) return -1;
        }

        for (ulong k = startFrom; k < length; ++k)
        {
            ulong elemK = ReadRaw(typedArray, k);
            if (IsFloatKind ? ToDouble(elemK) == ToDouble(typedSearchValue) : elemK == typedSearchValue) return (long)k;
        }
        return -1;
    }

    internal override long LastIndexOfValueImpl(JSObject receiver, JSValue value, ulong startFrom)
    {
        var typedArray = (JSTypedArray)receiver;

        ulong typedSearchValue;
        if (IsBigIntKind)
        {
            if (!value.IsBigInt) return -1;
            typedSearchValue = FromObject(value, out bool lossless);
            if (!lossless) return -1;
        }
        else
        {
            if (!value.IsNumber) return -1;
            double searchValue = value.Number;
            if (!double.IsFinite(searchValue))
            {
                if (!IsFloatKind) return -1;  // Integral types cannot represent +Inf or NaN.
                if (double.IsNaN(searchValue)) return -1;  // Strict Equality Comparison of NaN is always false.
            }
            if (ToTypedSearchValue(searchValue, out typedSearchValue)) return -1;
        }

        ulong typedArrayLength = typedArray.GetLength();
        if (startFrom >= typedArrayLength)
        {
            // This can happen if the TypedArray got resized when we did ToInteger
            // on the last parameter of lastIndexOf.
            if (typedArrayLength == 0) return -1;
            startFrom = typedArrayLength - 1;
        }

        ulong k = startFrom;
        do
        {
            ulong elemK = ReadRaw(typedArray, k);
            if (_baseKind == ElementsKind.FLOAT16_ELEMENTS && IsFloat16RawBitsZero(typedSearchValue) && IsFloat16RawBitsZero(elemK))
            {
                return (long)k;
            }
            if (IsFloatKind ? ToDouble(elemK) == ToDouble(typedSearchValue) : elemK == typedSearchValue) return (long)k;
        } while (k-- != 0);
        return -1;
    }

    internal override void ReverseImpl(JSObject receiver)
    {
        var typedArray = (JSTypedArray)receiver;
        ulong len = typedArray.GetLength();
        if (len == 0) return;
        for (ulong first = 0, last = len - 1; first < last; ++first, --last)
        {
            ulong firstValue = ReadRaw(typedArray, first);
            ulong lastValue = ReadRaw(typedArray, last);
            WriteRaw(typedArray, first, lastValue);
            WriteRaw(typedArray, last, firstValue);
        }
    }

    internal override FixedArray CreateListFromArrayLikeImpl(Isolate isolate, JSObject obj, uint length)
    {
        FixedArray result = isolate.Factory.NewFixedArray((int)length);
        for (int i = 0; i < length; i++)
        {
            result.Set(i, GetInternalImpl(isolate, obj, new InternalIndex(i)));
        }
        return result;
    }
}

/// <summary>SloppyArgumentsElementsAccessor: the mapped (context-aliased) part of a sloppy arguments object.</summary>
internal abstract class SloppyArgumentsElementsAccessor(ElementsKind kind, ElementsAccessor argumentsAccessor) : ElementsAccessor(kind)
{
    protected readonly ElementsAccessor ArgumentsAccessor = argumentsAccessor;

    internal abstract JSValue ConvertArgumentsStoreResult(Isolate isolate, SloppyArgumentsElements elements, JSValue result);

    internal override JSValue GetImpl(Isolate isolate, FixedArrayBase parameters, InternalIndex entry)
    {
        var elements = (SloppyArgumentsElements)parameters;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 < length)
        {
            // Read context mapped entry.
            JSValue probe = elements.MappedEntries(entry.AsInt);
            Debug.Assert(!probe.IsTheHole);
            Context context = elements.Context;
            int contextEntry = (int)probe.Number;
            return context.Get(contextEntry);
        }
        // Entry is not context mapped, defer to the arguments.
        JSValue result = ArgumentsAccessor.GetImpl(isolate, elements.Arguments, new InternalIndex(entry.AsInt - (int)length));
        return ConvertArgumentsStoreResult(isolate, elements, result);
    }

    internal override void TransitionElementsKindImpl(Isolate isolate, JSObject obj, Map map) =>
        throw new InvalidOperationException("unreachable");

    internal override bool GrowCapacityAndConvertImpl(Isolate isolate, JSObject obj, uint capacity) =>
        throw new InvalidOperationException("unreachable");

    internal override void SetImpl(FixedArrayBase store, InternalIndex entry, JSValue value)
    {
        var elements = (SloppyArgumentsElements)store;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 < length)
        {
            // Store context mapped entry.
            JSValue probe = elements.MappedEntries(entry.AsInt);
            Debug.Assert(!probe.IsTheHole);
            Context context = elements.Context;
            int contextEntry = (int)probe.Number;
            context.Set(contextEntry, value);
        }
        else
        {
            //  Entry is not context mapped defer to arguments.
            FixedArrayBase arguments = elements.Arguments;
            var argEntry = new InternalIndex(entry.AsInt - (int)length);
            JSValue current = ArgumentsAccessor.GetImpl(Isolate.Current!, arguments, argEntry);
            if (current.HeapObjectOrNull is AliasedArgumentsEntry alias)
            {
                Context context = elements.Context;
                int contextEntry = alias.AliasedContextSlot;
                context.Set(contextEntry, value);
            }
            else
            {
                ArgumentsAccessor.SetImpl(arguments, argEntry, value);
            }
        }
    }

    // Sloppy arguments objects are not arrays.
    internal override bool SetLengthImpl(Isolate isolate, JSArray array, uint length, FixedArrayBase parameterMap) =>
        throw new InvalidOperationException("unreachable");

    internal override long GetCapacityImpl(JSObject holder, FixedArrayBase store)
    {
        var elements = (SloppyArgumentsElements)store;
        return elements.Length + ArgumentsAccessor.GetCapacityImpl(holder, elements.Arguments);
    }

    internal override long GetMaxIndex(JSObject receiver, FixedArrayBase elements) => GetCapacityImpl(receiver, elements);

    internal override long GetMaxNumberOfEntries(Isolate isolate, JSObject holder, FixedArrayBase backingStore)
    {
        var elements = (SloppyArgumentsElements)backingStore;
        long length = elements.Length;
        long maxEntries = ArgumentsAccessor.GetMaxNumberOfEntries(isolate, holder, elements.Arguments);
        return length + maxEntries;
    }

    internal override long NumberOfElementsImpl(Isolate isolate, JSObject receiver, FixedArrayBase backingStore)
    {
        var elements = (SloppyArgumentsElements)backingStore;
        FixedArrayBase arguments = elements.Arguments;
        long nofElements = 0;
        int length = elements.Length;
        for (int index = 0; index < length; index++)
        {
            if (HasParameterMapArg(elements, (ulong)index)) nofElements++;
        }
        return nofElements + ArgumentsAccessor.NumberOfElementsImpl(isolate, receiver, arguments);
    }

    internal override void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert)
    {
        Isolate isolate = accumulator.Isolate;
        FixedArrayBase elements = receiver.Elements;
        long length = GetCapacityImpl(receiver, elements);
        for (int index = 0; index < length; index++)
        {
            var entry = new InternalIndex(index);
            if (!HasEntryImpl(isolate, elements, entry)) continue;
            JSValue value = GetImpl(isolate, elements, entry);
            accumulator.AddKey(value, convert);
        }
    }

    internal override bool HasEntryImpl(Isolate isolate, FixedArrayBase parameters, InternalIndex entry)
    {
        var elements = (SloppyArgumentsElements)parameters;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 < length) return HasParameterMapArg(elements, entry.AsUInt32);
        FixedArrayBase arguments = elements.Arguments;
        return ArgumentsAccessor.HasEntryImpl(isolate, arguments, new InternalIndex(entry.AsInt - (int)length));
    }

    internal override bool HasAccessorsImpl(JSObject holder, FixedArrayBase backingStore)
    {
        var elements = (SloppyArgumentsElements)backingStore;
        return ArgumentsAccessor.HasAccessorsImpl(holder, elements.Arguments);
    }

    internal override InternalIndex GetEntryForIndexImpl(Isolate isolate, JSObject holder, FixedArrayBase parameters, ulong index,
        PropertyFilter filter)
    {
        var elements = (SloppyArgumentsElements)parameters;
        if (HasParameterMapArg(elements, index)) return new InternalIndex((int)index);
        FixedArrayBase arguments = elements.Arguments;
        InternalIndex entry = ArgumentsAccessor.GetEntryForIndexImpl(isolate, holder, arguments, index, filter);
        if (entry.IsNotFound) return entry;
        // Arguments entries could overlap with the dictionary entries, hence offset
        // them by the number of context mapped entries.
        return new InternalIndex(entry.AsInt + elements.Length);
    }

    internal override PropertyDetails GetDetailsImpl(JSObject holder, InternalIndex entry)
    {
        var elements = (SloppyArgumentsElements)holder.Elements;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 < length) return new PropertyDetails(PropertyKind.Data, PropertyAttributes.NONE, PropertyCellType.NoCell);
        FixedArrayBase arguments = elements.Arguments;
        return ArgumentsAccessor.GetDetailsImpl(arguments, new InternalIndex(entry.AsInt - (int)length));
    }

    internal static bool HasParameterMapArg(SloppyArgumentsElements elements, ulong index)
    {
        uint length = (uint)elements.Length;
        if (index >= length) return false;
        return !elements.MappedEntries((int)index).IsTheHole;
    }

    internal override void DeleteImpl(Isolate isolate, JSObject obj, InternalIndex entry)
    {
        var elements = (SloppyArgumentsElements)obj.Elements;
        uint length = (uint)elements.Length;
        InternalIndex deleteOrEntry = entry;
        if (entry.AsUInt32 < length) deleteOrEntry = InternalIndex.NotFound;
        SloppyDeleteImpl(isolate, obj, elements, deleteOrEntry);
        // SloppyDeleteImpl allocates a new dictionary elements store. For making
        // heap verification happy we postpone clearing out the mapped entry.
        if (entry.AsUInt32 < length) elements.SetMappedEntries(entry.AsInt, JSValue.TheHole);
    }

    internal abstract void SloppyDeleteImpl(Isolate isolate, JSObject obj, SloppyArgumentsElements elements, InternalIndex entry);

    internal override void CollectElementIndicesImpl(JSObject obj, FixedArrayBase backingStore, KeyAccumulator keys)
    {
        Isolate isolate = keys.Isolate;
        int maxNofIndices = (int)GetCapacityImpl(obj, backingStore);
        int nofIndices = 0;
        FixedArray indices = isolate.Factory.NewFixedArray(maxNofIndices);
        DirectCollectElementIndicesImpl(isolate, obj, backingStore, GetKeysConversion.KeepNumbers, PropertyFilter.ENUMERABLE_STRINGS,
            indices, maxNofIndices, ref nofIndices);
        SortIndices(indices, nofIndices);
        for (int i = 0; i < nofIndices; i++) keys.AddKey(indices.Get(i));
    }

    internal override FixedArray DirectCollectElementIndicesImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore,
        GetKeysConversion convert, PropertyFilter filter, FixedArray list, int maxNofIndices, ref int nofIndices,
        int insertionIndex = 0)
    {
        var elements = (SloppyArgumentsElements)backingStore;
        int length = elements.Length;

        for (int i = 0; i < length; ++i)
        {
            if (elements.MappedEntries(i).IsTheHole) continue;
            if (convert == GetKeysConversion.ConvertToString)
            {
                list.Set(insertionIndex, isolate.Factory.SizeToString((ulong)i));
            }
            else
            {
                list.Set(insertionIndex, JSValue.FromInt(i));
            }
            insertionIndex++;
        }

        FixedArrayBase store = elements.Arguments;
        return ArgumentsAccessor.DirectCollectElementIndicesImpl(isolate, obj, store, convert, filter, list, maxNofIndices,
            ref nofIndices, insertionIndex);
    }

    internal override bool IncludesValueImpl(Isolate isolate, JSObject obj, JSValue value, ulong startFrom, ulong length)
    {
        Map originalMap = obj.Map;
        var elements = (SloppyArgumentsElements)obj.Elements;
        bool searchForHole = value.IsUndefined;

        for (ulong k = startFrom; k < length; ++k)
        {
            InternalIndex entry = GetEntryForIndexImpl(isolate, obj, elements, k, PropertyFilter.ALL_PROPERTIES);
            if (entry.IsNotFound)
            {
                if (searchForHole) return true;
                continue;
            }

            JSValue elementK = GetImpl(isolate, elements, entry);

            if (elementK.HeapObjectOrNull is AccessorPair)
            {
                var it = new LookupIterator(isolate, obj, k, LookupIterator.Configuration.OWN);
                elementK = ObjectOps.GetPropertyWithAccessor(ref it);

                if (ObjectOps.SameValueZero(value, elementK)) return true;

                if (!ReferenceEquals(obj.Map, originalMap))
                {
                    // Some mutation occurred in accessor. Abort "fast" path
                    return IncludesValueSlowPath(isolate, obj, value, k + 1, length);
                }
            }
            else if (ObjectOps.SameValueZero(value, elementK))
            {
                return true;
            }
        }
        return false;
    }

    internal override long IndexOfValueImpl(Isolate isolate, JSObject obj, JSValue value, ulong startFrom, ulong length)
    {
        Map originalMap = obj.Map;
        var elements = (SloppyArgumentsElements)obj.Elements;

        for (ulong k = startFrom; k < length; ++k)
        {
            InternalIndex entry = GetEntryForIndexImpl(isolate, obj, elements, k, PropertyFilter.ALL_PROPERTIES);
            if (entry.IsNotFound) continue;

            JSValue elementK = GetImpl(isolate, elements, entry);

            if (elementK.HeapObjectOrNull is AccessorPair)
            {
                var it = new LookupIterator(isolate, obj, k, LookupIterator.Configuration.OWN);
                elementK = ObjectOps.GetPropertyWithAccessor(ref it);

                if (ObjectOps.StrictEquals(value, elementK)) return (long)k;

                if (!ReferenceEquals(obj.Map, originalMap))
                {
                    // Some mutation occurred in accessor. Abort "fast" path.
                    return IndexOfValueSlowPath(isolate, obj, value, k + 1, length);
                }
            }
            else if (ObjectOps.StrictEquals(value, elementK))
            {
                return (long)k;
            }
        }
        return -1;
    }
}

/// <summary>SlowSloppyArgumentsElementsAccessor: SLOW_SLOPPY_ARGUMENTS_ELEMENTS (arguments in a NumberDictionary).</summary>
internal sealed class SlowSloppyArgumentsElementsAccessor(DictionaryElementsAccessor dictionary)
    : SloppyArgumentsElementsAccessor(ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS, dictionary)
{
    internal override JSValue ConvertArgumentsStoreResult(Isolate isolate, SloppyArgumentsElements elements, JSValue result)
    {
        // Elements of the arguments object in slow mode might be slow aliases.
        if (result.HeapObjectOrNull is AliasedArgumentsEntry alias)
        {
            Context context = elements.Context;
            int contextEntry = alias.AliasedContextSlot;
            return context.Get(contextEntry);
        }
        return result;
    }

    internal override void SloppyDeleteImpl(Isolate isolate, JSObject obj, SloppyArgumentsElements elements, InternalIndex entry)
    {
        // No need to delete a context mapped entry from the arguments elements.
        if (entry.IsNotFound) return;
        var dict = (NumberDictionary)elements.Arguments;
        int length = elements.Length;
        dict = NumberDictionary.DeleteEntry(isolate, dict, new InternalIndex(entry.AsInt - length));
        elements.Arguments = dict;
    }

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity)
    {
        var elements = (SloppyArgumentsElements)obj.Elements;
        FixedArrayBase oldArguments = elements.Arguments;
        NumberDictionary dictionary = oldArguments is NumberDictionary nd ? nd : JSObject.NormalizeElements(isolate, obj);
        var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyCellType.NoCell);
        NumberDictionary newDictionary = NumberDictionary.Add(dictionary, index, value, details, out _);
        if (attributes != PropertyAttributes.NONE) obj.RequireSlowElements(newDictionary);
        if (!ReferenceEquals(dictionary, newDictionary)) elements.Arguments = newDictionary;
        return true;
    }

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes)
    {
        var elements = (SloppyArgumentsElements)store;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 < length)
        {
            JSValue probe = elements.MappedEntries(entry.AsInt);
            Debug.Assert(!probe.IsTheHole);
            Context context = elements.Context;
            int contextEntry = (int)probe.Number;
            context.Set(contextEntry, value);

            // Redefining attributes of an aliased element destroys fast aliasing.
            elements.SetMappedEntries(entry.AsInt, JSValue.TheHole);
            // For elements that are still writable we re-establish slow aliasing.
            if ((attributes & PropertyAttributes.READ_ONLY) == 0)
            {
                value = new AliasedArgumentsEntry(contextEntry);
            }

            var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyCellType.NoCell);
            var arguments = (NumberDictionary)elements.Arguments;
            arguments = NumberDictionary.Add(arguments, entry.AsUInt32, value, details, out _);
            // If the attributes were NONE, we would have called set rather than
            // reconfigure.
            Debug.Assert(attributes != PropertyAttributes.NONE);
            obj.RequireSlowElements(arguments);
            elements.Arguments = arguments;
        }
        else
        {
            FixedArrayBase arguments = elements.Arguments;
            ArgumentsAccessor.ReconfigureImpl(isolate, obj, arguments, new InternalIndex(entry.AsInt - (int)length), value, attributes);
        }
    }
}

/// <summary>FastSloppyArgumentsElementsAccessor: FAST_SLOPPY_ARGUMENTS_ELEMENTS (arguments in a holey FixedArray).</summary>
internal sealed class FastSloppyArgumentsElementsAccessor(FastSmiOrObjectElementsAccessor holeyObject,
    SlowSloppyArgumentsElementsAccessor slowArguments)
    : SloppyArgumentsElementsAccessor(ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS, holeyObject)
{
    internal override JSValue ConvertArgumentsStoreResult(Isolate isolate, SloppyArgumentsElements parameterMap, JSValue result)
    {
        Debug.Assert(result.HeapObjectOrNull is not AliasedArgumentsEntry);
        return result;
    }

    internal override NumberDictionary NormalizeImpl(Isolate isolate, JSObject obj, FixedArrayBase elements)
    {
        FixedArrayBase arguments = ((SloppyArgumentsElements)elements).Arguments;
        return ArgumentsAccessor.NormalizeImpl(isolate, obj, arguments);
    }

    static NumberDictionary NormalizeArgumentsElements(Isolate isolate, JSObject obj, SloppyArgumentsElements elements,
        ref InternalIndex entry)
    {
        NumberDictionary dictionary = JSObject.NormalizeElements(isolate, obj);
        elements.Arguments = dictionary;
        // kMaxUInt32 indicates that a context mapped element got deleted. In this
        // case we only normalize the elements (aka. migrate to SLOW_SLOPPY).
        if (entry.IsNotFound) return dictionary;
        uint length = (uint)elements.Length;
        if (entry.AsUInt32 >= length)
        {
            InternalIndex found = dictionary.FindEntry(entry.AsUInt32 - length);
            entry = found.IsFound ? new InternalIndex(found.AsInt + (int)length) : found;
        }
        return dictionary;
    }

    internal override void SloppyDeleteImpl(Isolate isolate, JSObject obj, SloppyArgumentsElements elements, InternalIndex entry)
    {
        // Always normalize element on deleting an entry.
        NormalizeArgumentsElements(isolate, obj, elements, ref entry);
        slowArguments.SloppyDeleteImpl(isolate, obj, elements, entry);
    }

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity)
    {
        Debug.Assert(attributes == PropertyAttributes.NONE);
        var elements = (SloppyArgumentsElements)obj.Elements;
        FixedArrayBase oldArguments = elements.Arguments;
        if (oldArguments is NumberDictionary || oldArguments.Length < newCapacity)
        {
            GrowCapacityAndConvertImpl(isolate, obj, newCapacity);
        }
        FixedArrayBase arguments = elements.Arguments;
        // For fast holey objects, the entry equals the index. The code above made
        // sure that there's enough space to store the value. We cannot convert
        // index to entry explicitly since the slot still contains the hole, so the
        // current EntryForIndex would indicate that it is "absent" by returning
        // kMaxUInt32.
        ArgumentsAccessor.SetImpl(arguments, new InternalIndex((int)index), value);
        return true;
    }

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes)
    {
        var elements = (SloppyArgumentsElements)store;
        NormalizeArgumentsElements(isolate, obj, elements, ref entry);
        slowArguments.ReconfigureImpl(isolate, obj, store, entry, value, attributes);
    }

    internal override void CopyElementsImpl(Isolate isolate, FixedArrayBase from, uint fromStart, FixedArrayBase to,
        ElementsKind fromKind, uint toStart, uint packedSize, uint copySize)
    {
        if (fromKind == ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS)
        {
            CopyDictionaryToObjectElements(isolate, from, fromStart, to, ElementsKind.HOLEY_ELEMENTS, toStart, copySize);
        }
        else
        {
            CopyObjectToObjectElements(isolate, from, ElementsKind.HOLEY_ELEMENTS, fromStart, to, ElementsKind.HOLEY_ELEMENTS,
                toStart, copySize);
        }
    }

    internal override bool GrowCapacityAndConvertImpl(Isolate isolate, JSObject obj, uint capacity)
    {
        var elements = (SloppyArgumentsElements)obj.Elements;
        FixedArrayBase oldArguments = elements.Arguments;
        ElementsKind fromKind = obj.GetElementsKind();
        FixedArrayBase arguments = ConvertElementsWithCapacity(isolate, obj, oldArguments, fromKind, capacity);
        Map newMap = JSObject.GetElementsTransitionMap(isolate, obj, ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS);
        JSObject.MigrateToMap(isolate, obj, newMap);
        elements.Arguments = arguments;
        return true;
    }
}

/// <summary>StringWrapperElementsAccessor: the characters of a String wrapper, then its own elements.</summary>
internal sealed class StringWrapperElementsAccessor(ElementsKind kind, ElementsAccessor backingStoreAccessor) : ElementsAccessor(kind)
{
    static JSString GetString(JSObject holder) => (JSString)((JSPrimitiveWrapper)holder).Value.Object;

    internal override JSValue GetInternalImpl(Isolate isolate, JSObject holder, InternalIndex entry)
    {
        JSString str = GetString(holder);
        uint length = (uint)str.Length;
        if (entry.AsUInt32 < length) return isolate.Factory.LookupSingleCharacterStringFromCode(str.Get(entry.AsInt));
        return backingStoreAccessor.GetImpl(isolate, holder.Elements, new InternalIndex(entry.AsInt - (int)length));
    }

    internal override JSValue GetImpl(Isolate isolate, FixedArrayBase elements, InternalIndex entry) =>
        throw new InvalidOperationException("unreachable");

    internal override PropertyDetails GetDetailsImpl(JSObject holder, InternalIndex entry)
    {
        uint length = (uint)GetString(holder).Length;
        if (entry.AsUInt32 < length)
        {
            return new PropertyDetails(PropertyKind.Data, PropertyAttributes.READ_ONLY | PropertyAttributes.DONT_DELETE,
                PropertyCellType.NoCell);
        }
        return backingStoreAccessor.GetDetailsImpl(holder.Elements, new InternalIndex(entry.AsInt - (int)length));
    }

    internal override InternalIndex GetEntryForIndexImpl(Isolate isolate, JSObject holder, FixedArrayBase backingStore, ulong index,
        PropertyFilter filter)
    {
        uint length = (uint)GetString(holder).Length;
        if (index < length) return new InternalIndex((int)index);
        InternalIndex backingStoreEntry = backingStoreAccessor.GetEntryForIndexImpl(isolate, holder, backingStore, index, filter);
        if (backingStoreEntry.IsNotFound) return backingStoreEntry;
        return new InternalIndex(backingStoreEntry.AsInt + (int)length);
    }

    internal override void DeleteImpl(Isolate isolate, JSObject holder, InternalIndex entry)
    {
        uint length = (uint)GetString(holder).Length;
        if (entry.AsUInt32 < length) return;  // String contents can't be deleted.
        backingStoreAccessor.DeleteImpl(isolate, holder, new InternalIndex(entry.AsInt - (int)length));
    }

    internal override void SetImpl(JSObject holder, InternalIndex entry, JSValue value)
    {
        uint length = (uint)GetString(holder).Length;
        if (entry.AsUInt32 < length) return;  // String contents are read-only.
        backingStoreAccessor.SetImpl(holder.Elements, new InternalIndex(entry.AsInt - (int)length), value);
    }

    internal override bool AddImpl(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes,
        uint newCapacity)
    {
        // Explicitly grow fast backing stores if needed. Dictionaries know how to
        // extend their capacity themselves.
        if (Kind == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS &&
            (obj.GetElementsKind() == ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS ||
             backingStoreAccessor.GetCapacityImpl(obj, obj.Elements) != newCapacity))
        {
            GrowCapacityAndConvertImpl(isolate, obj, newCapacity);
        }
        backingStoreAccessor.AddImpl(isolate, obj, index, value, attributes, newCapacity);
        return true;
    }

    internal override void ReconfigureImpl(Isolate isolate, JSObject obj, FixedArrayBase store, InternalIndex entry, JSValue value,
        PropertyAttributes attributes)
    {
        uint length = (uint)GetString(obj).Length;
        if (entry.AsUInt32 < length) return;  // String contents can't be reconfigured.
        backingStoreAccessor.ReconfigureImpl(isolate, obj, store, new InternalIndex(entry.AsInt - (int)length), value, attributes);
    }

    internal override void AddElementsToKeyAccumulatorImpl(JSObject receiver, KeyAccumulator accumulator, AddKeyConversion convert)
    {
        Isolate isolate = accumulator.Isolate;
        JSString str = GetString(receiver);
        int length = str.Length;
        for (int i = 0; i < length; i++)
        {
            accumulator.AddKey(isolate.Factory.LookupSingleCharacterStringFromCode(str.Get(i)), convert);
        }
        backingStoreAccessor.AddElementsToKeyAccumulatorImpl(receiver, accumulator, convert);
    }

    internal override void CollectElementIndicesImpl(JSObject obj, FixedArrayBase backingStore, KeyAccumulator keys)
    {
        uint length = (uint)GetString(obj).Length;
        for (uint i = 0; i < length; i++) keys.AddKey(JSValue.FromNumber(i));
        backingStoreAccessor.CollectElementIndicesImpl(obj, backingStore, keys);
    }

    internal override FixedArray DirectCollectElementIndicesImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore,
        GetKeysConversion convert, PropertyFilter filter, FixedArray list, int maxNofIndices, ref int nofIndices,
        int insertionIndex = 0)
    {
        // ElementsAccessorBase::DirectCollectElementIndicesImpl over this
        // accessor's HasElementImpl, as V8's CRTP instantiation does.
        long length = GetMaxIndex(obj, backingStore);
        for (long i = 0; i < length; i++)
        {
            if (!HasElementImpl(isolate, obj, (ulong)i, backingStore, filter)) continue;
            if (insertionIndex >= maxNofIndices) break;
            list.Set(insertionIndex, convert == GetKeysConversion.ConvertToString
                ? isolate.Factory.SizeToString((ulong)i)
                : JSValue.FromNumber(i));
            insertionIndex++;
        }
        nofIndices = insertionIndex;
        return list;
    }

    internal override long GetMaxIndex(JSObject receiver, FixedArrayBase elements) =>
        GetString(receiver).Length + backingStoreAccessor.GetCapacityImpl(receiver, elements);

    internal override long GetCapacityImpl(JSObject holder, FixedArrayBase backingStore) =>
        backingStoreAccessor.GetCapacityImpl(holder, backingStore);

    internal override long GetMaxNumberOfEntries(Isolate isolate, JSObject receiver, FixedArrayBase elements) =>
        GetString(receiver).Length + (ElementsKinds.IsDictionaryElementsKind(backingStoreAccessor.Kind)
            ? backingStoreAccessor.NumberOfElementsImpl(isolate, receiver, elements)
            : backingStoreAccessor.GetCapacityImpl(receiver, elements));

    internal override bool GrowCapacityAndConvertImpl(Isolate isolate, JSObject obj, uint capacity)
    {
        FixedArrayBase oldElements = obj.Elements;
        ElementsKind fromKind = obj.GetElementsKind();
        if (fromKind == ElementsKind.FAST_STRING_WRAPPER_ELEMENTS)
        {
            // The optimizing compiler relies on the prototype lookups of String
            // objects always returning undefined. If there's a store to the
            // initial String.prototype object, make sure all the optimizations
            // are invalidated.
            isolate.UpdateNoElementsProtectorOnSetLength(obj);
        }
        return BasicGrowCapacityAndConvertImpl(isolate, obj, oldElements, fromKind, ElementsKind.FAST_STRING_WRAPPER_ELEMENTS,
            capacity);
    }

    internal override void CopyElementsImpl(Isolate isolate, FixedArrayBase from, uint fromStart, FixedArrayBase to,
        ElementsKind fromKind, uint toStart, uint packedSize, uint copySize)
    {
        if (fromKind == ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS)
        {
            CopyDictionaryToObjectElements(isolate, from, fromStart, to, ElementsKind.HOLEY_ELEMENTS, toStart, copySize);
        }
        else
        {
            CopyObjectToObjectElements(isolate, from, ElementsKind.HOLEY_ELEMENTS, fromStart, to, ElementsKind.HOLEY_ELEMENTS,
                toStart, copySize);
        }
    }

    internal override long NumberOfElementsImpl(Isolate isolate, JSObject obj, FixedArrayBase backingStore)
    {
        uint length = (uint)GetString(obj).Length;
        return length + backingStoreAccessor.NumberOfElementsImpl(isolate, obj, backingStore);
    }

    internal override NumberDictionary NormalizeImpl(Isolate isolate, JSObject obj, FixedArrayBase elements) =>
        backingStoreAccessor.NormalizeImpl(isolate, obj, elements);

    internal override bool HasAccessorsImpl(JSObject holder, FixedArrayBase backingStore) =>
        backingStoreAccessor.HasAccessorsImpl(holder, backingStore);
}
