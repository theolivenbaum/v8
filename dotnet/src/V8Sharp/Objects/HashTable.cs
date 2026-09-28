// Port of src/objects/hash-table{.h,-inl.h}, dictionary{.h,-inl.h} and the
// HashTable/Dictionary parts of src/objects/objects.cc: open-addressing hash
// tables with quadratic probing (NameDictionary, GlobalDictionary,
// NumberDictionary, ObjectHashTable).
using System.Runtime.CompilerServices;
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>
/// Integer hash mixers (V8's base::hash32/hash64 and SmiHash32/SmiHash64).
/// TODO(merge): use V8Sharp.Base's hashing (rapid_mix on 64-bit hosts); these
/// are V8's 32-bit-host Thomas Wang mixers. Hash values are not observable.
/// </summary>
public static class Hashing
{
    public const uint kSmiHashMask = (1u << 30) - 1;

    public static uint Hash32(uint key)
    {
        uint hash = key;
        hash = ~hash + (hash << 15);
        hash ^= hash >> 12;
        hash += hash << 2;
        hash ^= hash >> 4;
        hash *= 2057;
        hash ^= hash >> 16;
        return hash;
    }

    public static ulong Hash64(ulong key)
    {
        ulong hash = key;
        hash = ~hash + (hash << 18);
        hash ^= hash >> 31;
        hash *= 21;
        hash ^= hash >> 11;
        hash += hash << 6;
        hash ^= hash >> 22;
        return hash;
    }

    public static uint SmiHash32(uint key) => Hash32(key) & kSmiHashMask;
    public static uint SmiHash64(ulong key) => (uint)Hash64(key) & kSmiHashMask;

    /// <summary>ComputeSeededHash (src/utils/utils.h).</summary>
    public static uint ComputeSeededHash(uint key, ulong seed) => SmiHash64(key ^ seed);

    /// <summary>ComputeSeededHash with V8Sharp's fixed seed (0).</summary>
    public static uint ComputeSeededHash(uint key) => SmiHash64(key);

    /// <summary>Object::GetSimpleHash for numbers.</summary>
    public static uint NumberHash(double num)
    {
        if (double.IsNaN(num)) return JSValue.SmiMaxValue;
        if (num >= int.MinValue && num <= int.MaxValue && (int)num == num)
        {
            return SmiHash32((uint)(int)num);
        }
        return SmiHash64((ulong)BitConverter.DoubleToInt64Bits(num));
    }
}

/// <summary>
/// V8's HashTable: capacity is a power of two, entries are probed
/// quadratically (FirstProbe/NextProbe), empty keys are undefined and deleted
/// keys are the hole.
/// </summary>
public abstract class HashTableBase : FixedArrayBase
{
    public const int kMinCapacity = 4;
    public const int kMinShrinkCapacity = 16;
    public const int kMaxCapacity = 1 << 25;

    protected JSValue[] _keys;
    int _numberOfElements;
    int _numberOfDeletedElements;

    protected HashTableBase(InstanceType type, int capacity) : base(type)
    {
        _keys = new JSValue[capacity];
    }

    public int Capacity => _keys.Length;
    public override int Length => _keys.Length;
    public int NumberOfElements => _numberOfElements;
    public int NumberOfDeletedElements => _numberOfDeletedElements;

    public void ElementAdded() => _numberOfElements++;
    public void ElementRemoved() => ElementsRemoved(1);
    public void ElementsRemoved(int n)
    {
        _numberOfElements -= n;
        _numberOfDeletedElements += n;
    }

    protected void SetNumberOfElements(int n) => _numberOfElements = n;
    protected void SetNumberOfDeletedElements(int n) => _numberOfDeletedElements = n;

    /// <summary>ComputeCapacity: 50% slack, rounded up to a power of two, at least kMinCapacity.</summary>
    public static int ComputeCapacity(int atLeastSpaceFor)
    {
        uint rawCap = (uint)(atLeastSpaceFor + (atLeastSpaceFor >> 1));
        uint capacity = System.Numerics.BitOperations.RoundUpToPowerOf2(rawCap == 0 ? 1 : rawCap);
        return Math.Max((int)capacity, kMinCapacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FirstProbe(uint hash, int size) => (int)(hash & (uint)(size - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextProbe(int last, int number, int size) => (int)((uint)(last + number) & (uint)(size - 1));

    public JSValue KeyAt(InternalIndex entry) => _keys[entry.AsInt];

    /// <summary>Whether the slot holds a live key (IsKey: not undefined, not the hole).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsKey(int entry)
    {
        HeapObject? k = _keys[entry]._obj;
        return k is not null && !ReferenceEquals(k, Oddball.TheHole);
    }

    public bool ToKey(InternalIndex entry, out JSValue key)
    {
        key = _keys[entry.AsInt];
        return IsKey(entry.AsInt);
    }

    public bool HasSufficientCapacityToAdd(int numberOfAdditionalElements) =>
        HasSufficientCapacityToAdd(Capacity, NumberOfElements, NumberOfDeletedElements, numberOfAdditionalElements);

    public static bool HasSufficientCapacityToAdd(int capacity, int numberOfElements, int numberOfDeletedElements, int numberOfAdditionalElements)
    {
        int nof = numberOfElements + numberOfAdditionalElements;
        // Return true if:
        //   50% is still free after adding number_of_additional_elements elements and
        //   at most 50% of the free elements are deleted elements.
        if (nof < capacity && numberOfDeletedElements <= (capacity - nof) / 2)
        {
            int neededFree = nof / 2;
            if (nof + neededFree <= capacity) return true;
        }
        return false;
    }

    public static int ComputeCapacityWithShrink(int currentCapacity, int atLeastRoomFor)
    {
        // Shrink to fit the number of elements if only a quarter of the
        // capacity is filled with elements.
        if (atLeastRoomFor > currentCapacity / 4) return currentCapacity;
        // Recalculate the smaller capacity actually needed.
        int newCapacity = ComputeCapacity(atLeastRoomFor);
        // Don't go lower than room for {kMinShrinkCapacity} elements.
        if (newCapacity < kMinShrinkCapacity) return currentCapacity;
        return newCapacity;
    }

    protected abstract uint HashForKey(in JSValue key);

    /// <summary>FindInsertionEntry: the first free (empty or deleted) slot on the probe sequence.</summary>
    public InternalIndex FindInsertionEntry(uint hash)
    {
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            if (!IsKey(entry)) return new InternalIndex(entry);
        }
    }

    /// <summary>Swaps the per-entry payload (values, details) of two entries; keys are swapped by the caller.</summary>
    protected virtual void SwapPayload(int i, int j) { }

    void Swap(int i, int j)
    {
        (_keys[i], _keys[j]) = (_keys[j], _keys[i]);
        SwapPayload(i, j);
    }

    int EntryForProbe(in JSValue key, int probe, int expected)
    {
        uint hash = HashForKey(key);
        int capacity = Capacity;
        int entry = FirstProbe(hash, capacity);
        for (int i = 1; i < probe; i++)
        {
            if (entry == expected) return expected;
            entry = NextProbe(entry, i, capacity);
        }
        return entry;
    }

    /// <summary>
    /// HashTable::Rehash (in place): moves every key to the first free position
    /// of its probe sequence and wipes deleted entries.
    /// </summary>
    public void Rehash()
    {
        int capacity = Capacity;
        bool done = false;
        for (int probe = 1; !done; probe++)
        {
            // All elements at entries given by one of the first _probe_ probes
            // are placed correctly. Other elements might need to be moved.
            done = true;
            for (int current = 0; current < capacity; /* see below */)
            {
                if (!IsKey(current))
                {
                    ++current;
                    continue;
                }
                int target = EntryForProbe(_keys[current], probe, current);
                if (current == target)
                {
                    ++current;
                    continue;
                }
                if (!IsKey(target) || EntryForProbe(_keys[target], probe, target) != target)
                {
                    // Put the current element into the correct position.
                    Swap(current, target);
                    // The other element will be processed on the next iteration,
                    // so don't advance |current|.
                }
                else
                {
                    // The place for the current element is occupied. Leave the element
                    // for the next probe.
                    done = false;
                    ++current;
                }
            }
        }
        // Wipe deleted entries.
        for (int current = 0; current < capacity; current++)
        {
            if (_keys[current].IsTheHole) _keys[current] = JSValue.Undefined;
        }
        SetNumberOfDeletedElements(0);
    }

    protected static int CheckedNewCapacity(int atLeastSpaceFor)
    {
        const int kMaxAtLeastSpaceFor = kMaxCapacity * 2 / 3;
        if (atLeastSpaceFor > kMaxAtLeastSpaceFor) return -1;
        return ComputeCapacity(atLeastSpaceFor);
    }
}

/// <summary>V8's NameDictionary: the property dictionary of dictionary-mode objects.</summary>
public sealed class NameDictionary : HashTableBase
{
    protected override void SwapPayload(int i, int j)
    {
        (_values[i], _values[j]) = (_values[j], _values[i]);
        (_details[i], _details[j]) = (_details[j], _details[i]);
    }

    public const int kInitialCapacity = 2;

    readonly JSValue[] _values;
    readonly PropertyDetails[] _details;
    int _nextEnumerationIndex = PropertyDetails.kInitialIndex;

    public bool MayHaveInterestingProperties { get; set; }

    NameDictionary(int capacity) : base(InstanceType.NameDictionaryType, capacity)
    {
        _values = new JSValue[capacity];
        _details = new PropertyDetails[capacity];
    }

    /// <summary>NameDictionary::New.</summary>
    public static NameDictionary New(int atLeastSpaceFor, bool useCustomMinimumCapacity = false)
    {
        int capacity = useCustomMinimumCapacity ? atLeastSpaceFor : CheckedNewCapacity(atLeastSpaceFor);
        if (capacity < 0) throw new OutOfMemoryException("invalid table size");
        return new NameDictionary(capacity);
    }

    static NameDictionary? TryNew(int atLeastSpaceFor)
    {
        int capacity = CheckedNewCapacity(atLeastSpaceFor);
        return capacity < 0 ? null : new NameDictionary(capacity);
    }

    protected override uint HashForKey(in JSValue key) => ((Name)key.Object).EnsureHash();

    public int NextEnumerationIndexRaw
    {
        get => _nextEnumerationIndex;
        set => _nextEnumerationIndex = value;
    }

    public Name NameAt(InternalIndex entry) => (Name)_keys[entry.AsInt].Object;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue ValueAt(InternalIndex entry) => _values[entry.AsInt];

    public void ValueAtPut(InternalIndex entry, JSValue value) => _values[entry.AsInt] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PropertyDetails DetailsAt(InternalIndex entry) => _details[entry.AsInt];

    public void DetailsAtPut(InternalIndex entry, PropertyDetails value) => _details[entry.AsInt] = value;

    public void SetEntry(InternalIndex entry, Name key, JSValue value, PropertyDetails details)
    {
        _keys[entry.AsInt] = key;
        _values[entry.AsInt] = value;
        _details[entry.AsInt] = details;
    }

    /// <summary>ClearEntry: marks the slot deleted (the hole).</summary>
    public void ClearEntry(InternalIndex entry)
    {
        _keys[entry.AsInt] = JSValue.TheHole;
        _values[entry.AsInt] = JSValue.TheHole;
        _details[entry.AsInt] = PropertyDetails.Empty();
    }

    /// <summary>FindEntry: names are unique, so matching is by identity.</summary>
    public InternalIndex FindEntry(Name key)
    {
        Debug.Assert(key.IsUniqueName);
        uint hash = key.EnsureHash();
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            HeapObject? element = _keys[entry]._obj;
            // Empty entry.
            if (element is null) return InternalIndex.NotFound;
            if (ReferenceEquals(element, key)) return new InternalIndex(entry);
        }
    }

    /// <summary>BaseNameDictionary::NextEnumerationIndex (renumbers when the index overflows).</summary>
    public static int NextEnumerationIndex(Isolate isolate, NameDictionary dictionary)
    {
        int index = dictionary._nextEnumerationIndex;
        // Check whether the next enumeration index is valid.
        if (!PropertyDetails.IsValidIndex(index))
        {
            // If not, we generate new indices for the properties.
            int[] iterationOrder = IterationIndices(dictionary);
            for (int i = 0; i < iterationOrder.Length; i++)
            {
                var internalIndex = new InternalIndex(iterationOrder[i]);
                int enumIndex = PropertyDetails.kInitialIndex + i;
                PropertyDetails details = dictionary.DetailsAt(internalIndex);
                dictionary.DetailsAtPut(internalIndex, details.SetIndex(enumIndex));
            }
            index = PropertyDetails.kInitialIndex + iterationOrder.Length;
        }
        return index;
    }

    /// <summary>IterationIndices: live entries in enumeration (insertion) order.</summary>
    public static int[] IterationIndices(NameDictionary dictionary)
    {
        var array = new int[dictionary.NumberOfElements];
        int arraySize = 0;
        int capacity = dictionary.Capacity;
        for (int i = 0; i < capacity; i++)
        {
            if (!dictionary.IsKey(i)) continue;
            array[arraySize++] = i;
        }
        PropertyDetails[] details = dictionary._details;
        Array.Sort(array, 0, arraySize, Comparer<int>.Create((a, b) =>
            details[a].DictionaryIndex.CompareTo(details[b].DictionaryIndex)));
        if (arraySize != array.Length) Array.Resize(ref array, arraySize);
        return array;
    }

    /// <summary>BaseNameDictionary::Add: assigns the next enumeration index.</summary>
    public static NameDictionary Add(Isolate isolate, NameDictionary dictionary, Name key, JSValue value,
        PropertyDetails details, out InternalIndex entryOut)
    {
        Debug.Assert(details.DictionaryIndex == 0);
        // Assign an enumeration index to the property and update
        // SetNextEnumerationIndex.
        int index = NextEnumerationIndex(isolate, dictionary);
        if (!PropertyDetails.CanSetIndex(index))
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.TooManyProperties));
        }
        if (!dictionary.HasSufficientCapacityToAdd(1))
        {
            NameDictionary? grown = TryEnsureCapacity(dictionary, 1);
            if (grown is null) isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.TooManyProperties));
            dictionary = grown!;
        }
        details = details.SetIndex(index);
        dictionary = AddNoUpdateNextEnumerationIndex(dictionary, key, value, details, out entryOut);
        // Update enumeration index here in order to avoid potential modification of
        // the canonical empty dictionary which lives in read only space.
        dictionary._nextEnumerationIndex = index + 1;
        return dictionary;
    }

    public static NameDictionary Add(Isolate isolate, NameDictionary dictionary, Name key, JSValue value, PropertyDetails details) =>
        Add(isolate, dictionary, key, value, details, out _);

    /// <summary>Dictionary::Add without touching the enumeration index.</summary>
    public static NameDictionary AddNoUpdateNextEnumerationIndex(NameDictionary dictionary, Name key, JSValue value,
        PropertyDetails details, out InternalIndex entryOut)
    {
        uint hash = key.EnsureHash();
        Debug.Assert(dictionary.FindEntry(key).IsNotFound);
        // Check whether the dictionary should be extended.
        dictionary = EnsureCapacity(dictionary, 1);
        InternalIndex entry = dictionary.FindInsertionEntry(hash);
        dictionary.SetEntry(entry, key, value, details);
        dictionary.ElementAdded();
        entryOut = entry;
        return dictionary;
    }

    /// <summary>Dictionary::UncheckedAdd: the caller has ensured capacity.</summary>
    public void UncheckedAdd(Name key, JSValue value, PropertyDetails details)
    {
        Debug.Assert(HasSufficientCapacityToAdd(1));
        InternalIndex entry = FindInsertionEntry(key.EnsureHash());
        SetEntry(entry, key, value, details);
        ElementAdded();
    }

    /// <summary>Dictionary::AtPut: set the value if present, else add.</summary>
    public static NameDictionary AtPut(Isolate isolate, NameDictionary dictionary, Name key, JSValue value, PropertyDetails details)
    {
        InternalIndex entry = dictionary.FindEntry(key);
        // If the entry is present set the value;
        if (entry.IsNotFound) return Add(isolate, dictionary, key, value, details);
        // We don't need to copy over the enumeration index.
        dictionary.ValueAtPut(entry, value);
        dictionary.DetailsAtPut(entry, details);
        return dictionary;
    }

    /// <summary>Dictionary::DeleteEntry: removes and possibly shrinks.</summary>
    public static NameDictionary DeleteEntry(Isolate isolate, NameDictionary dictionary, InternalIndex entry)
    {
        Debug.Assert(dictionary.DetailsAt(entry).IsConfigurable);
        dictionary.ClearEntry(entry);
        dictionary.ElementRemoved();
        return Shrink(dictionary);
    }

    public static NameDictionary EnsureCapacity(NameDictionary table, int n)
    {
        if (table.HasSufficientCapacityToAdd(n)) return table;
        int newNof = table.NumberOfElements + n;
        NameDictionary newTable = New(newNof);
        table.Rehash(newTable);
        return newTable;
    }

    static NameDictionary? TryEnsureCapacity(NameDictionary table, int n)
    {
        if (table.HasSufficientCapacityToAdd(n)) return table;
        NameDictionary? newTable = TryNew(table.NumberOfElements + n);
        if (newTable is null) return null;
        table.Rehash(newTable);
        return newTable;
    }

    public static NameDictionary Shrink(NameDictionary table, int additionalCapacity = 0)
    {
        int newCapacity = ComputeCapacityWithShrink(table.Capacity, table.NumberOfElements + additionalCapacity);
        if (newCapacity == table.Capacity) return table;
        NameDictionary newTable = New(newCapacity, useCustomMinimumCapacity: true);
        table.Rehash(newTable);
        return newTable;
    }

    void Rehash(NameDictionary newTable)
    {
        newTable._nextEnumerationIndex = _nextEnumerationIndex;
        newTable.MayHaveInterestingProperties = MayHaveInterestingProperties;
        int capacity = Capacity;
        for (int i = 0; i < capacity; i++)
        {
            if (!IsKey(i)) continue;
            Name k = (Name)_keys[i].Object;
            InternalIndex insertion = newTable.FindInsertionEntry(k.EnsureHash());
            newTable.SetEntry(insertion, k, _values[i], _details[i]);
        }
        newTable.SetNumberOfElements(NumberOfElements);
        newTable.SetNumberOfDeletedElements(0);
    }

    public NameDictionary ShallowCopy()
    {
        var copy = new NameDictionary(Capacity)
        {
            _nextEnumerationIndex = _nextEnumerationIndex,
            MayHaveInterestingProperties = MayHaveInterestingProperties,
        };
        Array.Copy(_keys, copy._keys, Capacity);
        Array.Copy(_values, copy._values, Capacity);
        Array.Copy(_details, copy._details, Capacity);
        copy.SetNumberOfElements(NumberOfElements);
        copy.SetNumberOfDeletedElements(NumberOfDeletedElements);
        return copy;
    }

    public int NumberOfEnumerableProperties()
    {
        int result = 0;
        int capacity = Capacity;
        for (int i = 0; i < capacity; i++)
        {
            if (!IsKey(i)) continue;
            Name k = (Name)_keys[i].Object;
            if (ObjectOps.FilterKey(k, PropertyFilter.ENUMERABLE_STRINGS)) continue;
            if ((_details[i].Attributes & PropertyAttributes.DONT_ENUM) == 0) result++;
        }
        return result;
    }

    public JSValue SlowReverseLookup(JSValue value)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsKey(i)) continue;
            if (_values[i].IsIdenticalTo(value)) return _keys[i];
        }
        return JSValue.Undefined;
    }
}

/// <summary>
/// V8's GlobalDictionary: the properties of a JSGlobalObject, one PropertyCell
/// per property (the cell carries name, value and details).
/// </summary>
public sealed class GlobalDictionary : HashTableBase
{
    int _nextEnumerationIndex = PropertyDetails.kInitialIndex;

    GlobalDictionary(int capacity) : base(InstanceType.GlobalDictionaryType, capacity) { }

    public static GlobalDictionary New(int atLeastSpaceFor, bool useCustomMinimumCapacity = false)
    {
        int capacity = useCustomMinimumCapacity ? atLeastSpaceFor : CheckedNewCapacity(atLeastSpaceFor);
        if (capacity < 0) throw new OutOfMemoryException("invalid table size");
        return new GlobalDictionary(capacity);
    }

    protected override uint HashForKey(in JSValue key) => ((PropertyCell)key.Object).Name.EnsureHash();

    public PropertyCell CellAt(InternalIndex entry) => (PropertyCell)_keys[entry.AsInt].Object;
    public Name NameAt(InternalIndex entry) => CellAt(entry).Name;
    public JSValue ValueAt(InternalIndex entry) => CellAt(entry).Value;
    public void ValueAtPut(InternalIndex entry, JSValue value) => CellAt(entry).Value = value;
    public PropertyDetails DetailsAt(InternalIndex entry) => CellAt(entry).PropertyDetails;
    public void DetailsAtPut(InternalIndex entry, PropertyDetails value) => CellAt(entry).PropertyDetails = value;

    public void SetEntry(InternalIndex entry, PropertyCell cell, PropertyDetails details)
    {
        _keys[entry.AsInt] = cell;
        cell.PropertyDetails = details;
    }

    public void ClearEntry(InternalIndex entry) => _keys[entry.AsInt] = JSValue.TheHole;

    public int NextEnumerationIndexRaw
    {
        get => _nextEnumerationIndex;
        set => _nextEnumerationIndex = value;
    }

    public InternalIndex FindEntry(Name key)
    {
        uint hash = key.EnsureHash();
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            HeapObject? element = _keys[entry]._obj;
            if (element is null) return InternalIndex.NotFound;
            if (ReferenceEquals(element, Oddball.TheHole)) continue;
            if (ReferenceEquals(((PropertyCell)element).Name, key)) return new InternalIndex(entry);
        }
    }

    public static int[] IterationIndices(GlobalDictionary dictionary)
    {
        var list = new List<int>(dictionary.NumberOfElements);
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            if (dictionary.IsKey(i)) list.Add(i);
        }
        list.Sort((a, b) => dictionary.DetailsAt(new InternalIndex(a)).DictionaryIndex
            .CompareTo(dictionary.DetailsAt(new InternalIndex(b)).DictionaryIndex));
        return list.ToArray();
    }

    public static int NextEnumerationIndex(Isolate isolate, GlobalDictionary dictionary)
    {
        int index = dictionary._nextEnumerationIndex;
        if (!PropertyDetails.IsValidIndex(index))
        {
            int[] iterationOrder = IterationIndices(dictionary);
            for (int i = 0; i < iterationOrder.Length; i++)
            {
                var internalIndex = new InternalIndex(iterationOrder[i]);
                PropertyDetails details = dictionary.DetailsAt(internalIndex);
                dictionary.DetailsAtPut(internalIndex, details.SetIndex(PropertyDetails.kInitialIndex + i));
            }
            index = PropertyDetails.kInitialIndex + iterationOrder.Length;
        }
        return index;
    }

    /// <summary>GlobalDictionary::Add (BaseNameDictionary::Add with the value being the cell).</summary>
    public static GlobalDictionary Add(Isolate isolate, GlobalDictionary dictionary, Name name, PropertyCell cell,
        PropertyDetails details, out InternalIndex entryOut)
    {
        int index = NextEnumerationIndex(isolate, dictionary);
        if (!PropertyDetails.CanSetIndex(index))
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.TooManyProperties));
        }
        details = details.SetIndex(index);
        dictionary = EnsureCapacity(dictionary, 1);
        InternalIndex entry = dictionary.FindInsertionEntry(name.EnsureHash());
        dictionary.SetEntry(entry, cell, details);
        dictionary.ElementAdded();
        dictionary._nextEnumerationIndex = index + 1;
        entryOut = entry;
        return dictionary;
    }

    public static GlobalDictionary DeleteEntry(Isolate isolate, GlobalDictionary dictionary, InternalIndex entry)
    {
        dictionary.ClearEntry(entry);
        dictionary.ElementRemoved();
        return Shrink(dictionary);
    }

    public static GlobalDictionary EnsureCapacity(GlobalDictionary table, int n)
    {
        if (table.HasSufficientCapacityToAdd(n)) return table;
        GlobalDictionary newTable = New(table.NumberOfElements + n);
        table.Rehash(newTable);
        return newTable;
    }

    public static GlobalDictionary Shrink(GlobalDictionary table)
    {
        int newCapacity = ComputeCapacityWithShrink(table.Capacity, table.NumberOfElements);
        if (newCapacity == table.Capacity) return table;
        GlobalDictionary newTable = New(newCapacity, useCustomMinimumCapacity: true);
        table.Rehash(newTable);
        return newTable;
    }

    void Rehash(GlobalDictionary newTable)
    {
        newTable._nextEnumerationIndex = _nextEnumerationIndex;
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsKey(i)) continue;
            var cell = (PropertyCell)_keys[i].Object;
            InternalIndex insertion = newTable.FindInsertionEntry(cell.Name.EnsureHash());
            newTable._keys[insertion.AsInt] = cell;
        }
        newTable.SetNumberOfElements(NumberOfElements);
        newTable.SetNumberOfDeletedElements(0);
    }

    public int NumberOfEnumerableProperties()
    {
        int result = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsKey(i)) continue;
            PropertyCell cell = (PropertyCell)_keys[i].Object;
            if (cell.Value.IsTheHole || ReferenceEquals(cell.Value._obj, Oddball.PropertyCellHole)) continue;
            if (ObjectOps.FilterKey(cell.Name, PropertyFilter.ENUMERABLE_STRINGS)) continue;
            if ((cell.PropertyDetails.Attributes & PropertyAttributes.DONT_ENUM) == 0) result++;
        }
        return result;
    }
}

/// <summary>
/// V8's NumberDictionary: dictionary-mode elements, keyed by uint32 index,
/// tracking the maximum key and whether slow elements are required.
/// </summary>
public sealed class NumberDictionary : HashTableBase
{
    protected override void SwapPayload(int i, int j)
    {
        (_values[i], _values[j]) = (_values[j], _values[i]);
        (_details[i], _details[j]) = (_details[j], _details[i]);
    }

    public const int kRequiresSlowElementsMask = 1;
    public const int kRequiresSlowElementsTagSize = 1;
    public const uint kRequiresSlowElementsLimit = (1u << 29) - 1;
    public const uint kPreferFastElementsSizeFactor = 3;

    /// <summary>NumberDictionaryShape::kEntrySize: key, value, details.</summary>
    public const int kEntrySize = 3;

    readonly JSValue[] _values;
    readonly PropertyDetails[] _details;

    /// <summary>V8's kMaxNumberKeyIndex slot: (max_key &lt;&lt; 1) | requires_slow_elements, or undefined.</summary>
    JSValue _maxNumberKeySlot;

    NumberDictionary(int capacity) : base(InstanceType.NumberDictionaryType, capacity)
    {
        _values = new JSValue[capacity];
        _details = new PropertyDetails[capacity];
    }

    public static NumberDictionary New(int atLeastSpaceFor, bool useCustomMinimumCapacity = false)
    {
        int capacity = useCustomMinimumCapacity ? atLeastSpaceFor : CheckedNewCapacity(atLeastSpaceFor);
        if (capacity < 0) throw new OutOfMemoryException("invalid table size");
        return new NumberDictionary(capacity);
    }

    /// <summary>The empty slow element dictionary (V8 shares one read-only instance; ours are fresh).</summary>
    public static NumberDictionary NewEmpty() => New(0);

    protected override uint HashForKey(in JSValue key) => Hashing.ComputeSeededHash((uint)key.Number);

    public uint KeyAtUInt(InternalIndex entry) => (uint)_keys[entry.AsInt].Number;
    public JSValue ValueAt(InternalIndex entry) => _values[entry.AsInt];
    public void ValueAtPut(InternalIndex entry, JSValue value) => _values[entry.AsInt] = value;
    public PropertyDetails DetailsAt(InternalIndex entry) => _details[entry.AsInt];
    public void DetailsAtPut(InternalIndex entry, PropertyDetails value) => _details[entry.AsInt] = value;

    public void SetEntry(InternalIndex entry, uint key, JSValue value, PropertyDetails details)
    {
        _keys[entry.AsInt] = JSValue.FromNumber(key);
        _values[entry.AsInt] = value;
        _details[entry.AsInt] = details;
    }

    public void ClearEntry(InternalIndex entry)
    {
        _keys[entry.AsInt] = JSValue.TheHole;
        _values[entry.AsInt] = JSValue.TheHole;
        _details[entry.AsInt] = PropertyDetails.Empty();
    }

    public InternalIndex FindEntry(uint key)
    {
        uint hash = Hashing.ComputeSeededHash(key);
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            JSValue element = _keys[entry];
            if (element.IsUndefined) return InternalIndex.NotFound;
            if (element.IsTheHole) continue;
            if (element.Number == key) return new InternalIndex(entry);
        }
    }

    public bool RequiresSlowElements
    {
        get
        {
            JSValue max = _maxNumberKeySlot;
            if (!max.IsNumber) return false;
            return ((int)max.Number & kRequiresSlowElementsMask) != 0;
        }
    }

    public void SetRequiresSlowElements() =>
        _maxNumberKeySlot = JSValue.FromInt(kRequiresSlowElementsMask);

    public uint MaxNumberKey
    {
        get
        {
            Debug.Assert(!RequiresSlowElements);
            JSValue max = _maxNumberKeySlot;
            if (!max.IsNumber) return 0;
            return (uint)((long)max.Number >> kRequiresSlowElementsTagSize);
        }
    }

    public void UpdateMaxNumberKey(uint key, JSObject? dictionaryHolder)
    {
        // If the dictionary requires slow elements an element has already
        // been added at a high index.
        if (RequiresSlowElements) return;
        // Check if this index is high enough that we should require slow
        // elements.
        if (key > kRequiresSlowElementsLimit)
        {
            dictionaryHolder?.RequireSlowElements(this);
            SetRequiresSlowElements();
            return;
        }
        // Update max key value.
        JSValue maxIndexObject = _maxNumberKeySlot;
        if (!maxIndexObject.IsNumber || MaxNumberKey < key)
        {
            _maxNumberKeySlot = JSValue.FromNumber((double)((ulong)key << kRequiresSlowElementsTagSize));
        }
    }

    /// <summary>NumberDictionary::Set: AtPut and update the max key.</summary>
    public static NumberDictionary Set(Isolate isolate, NumberDictionary dictionary, uint key, JSValue value,
        JSObject? dictionaryHolder = null, PropertyDetails details = default)
    {
        NumberDictionary newDictionary = AtPut(dictionary, key, value, details);
        newDictionary.UpdateMaxNumberKey(key, dictionaryHolder);
        return newDictionary;
    }

    public static void UncheckedSet(NumberDictionary dictionary, uint key, JSValue value)
    {
        InternalIndex entry = dictionary.FindEntry(key);
        if (entry.IsNotFound)
        {
            dictionary.UncheckedAdd(key, value, PropertyDetails.Empty());
        }
        else
        {
            dictionary.ValueAtPut(entry, value);
            dictionary.DetailsAtPut(entry, PropertyDetails.Empty());
        }
    }

    public static NumberDictionary AtPut(NumberDictionary dictionary, uint key, JSValue value, PropertyDetails details)
    {
        InternalIndex entry = dictionary.FindEntry(key);
        if (entry.IsNotFound) return Add(dictionary, key, value, details, out _);
        dictionary.ValueAtPut(entry, value);
        dictionary.DetailsAtPut(entry, details);
        return dictionary;
    }

    public static NumberDictionary Add(NumberDictionary dictionary, uint key, JSValue value, PropertyDetails details, out InternalIndex entryOut)
    {
        uint hash = Hashing.ComputeSeededHash(key);
        dictionary = EnsureCapacity(dictionary, 1);
        InternalIndex entry = dictionary.FindInsertionEntry(hash);
        dictionary.SetEntry(entry, key, value, details);
        dictionary.ElementAdded();
        entryOut = entry;
        return dictionary;
    }

    public void UncheckedAdd(uint key, JSValue value, PropertyDetails details)
    {
        InternalIndex entry = FindInsertionEntry(Hashing.ComputeSeededHash(key));
        SetEntry(entry, key, value, details);
        ElementAdded();
    }

    public static NumberDictionary DeleteEntry(Isolate isolate, NumberDictionary dictionary, InternalIndex entry)
    {
        dictionary.ClearEntry(entry);
        dictionary.ElementRemoved();
        return Shrink(dictionary);
    }

    public static NumberDictionary EnsureCapacity(NumberDictionary table, int n)
    {
        if (table.HasSufficientCapacityToAdd(n)) return table;
        NumberDictionary newTable = New(table.NumberOfElements + n);
        table.Rehash(newTable);
        return newTable;
    }

    public static NumberDictionary Shrink(NumberDictionary table)
    {
        int newCapacity = ComputeCapacityWithShrink(table.Capacity, table.NumberOfElements);
        if (newCapacity == table.Capacity) return table;
        NumberDictionary newTable = New(newCapacity, useCustomMinimumCapacity: true);
        table.Rehash(newTable);
        return newTable;
    }

    void Rehash(NumberDictionary newTable)
    {
        newTable._maxNumberKeySlot = _maxNumberKeySlot;
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsKey(i)) continue;
            uint k = (uint)_keys[i].Number;
            InternalIndex insertion = newTable.FindInsertionEntry(Hashing.ComputeSeededHash(k));
            newTable.SetEntry(insertion, k, _values[i], _details[i]);
        }
        newTable.SetNumberOfElements(NumberOfElements);
        newTable.SetNumberOfDeletedElements(0);
    }

    /// <summary>Copies the values in slot order into <paramref name="elements"/>.</summary>
    public void CopyValuesTo(FixedArray elements)
    {
        int pos = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (IsKey(i)) elements.Set(pos++, _values[i]);
        }
    }

    public int NumberOfEnumerableProperties()
    {
        int result = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsKey(i)) continue;
            if ((_details[i].Attributes & PropertyAttributes.DONT_ENUM) == 0) result++;
        }
        return result;
    }

    public NumberDictionary ShallowCopy()
    {
        var copy = new NumberDictionary(Capacity) { _maxNumberKeySlot = _maxNumberKeySlot };
        Array.Copy(_keys, copy._keys, Capacity);
        Array.Copy(_values, copy._values, Capacity);
        Array.Copy(_details, copy._details, Capacity);
        copy.SetNumberOfElements(NumberOfElements);
        copy.SetNumberOfDeletedElements(NumberOfDeletedElements);
        return copy;
    }
}

/// <summary>
/// V8's ObjectHashTable: keys compared with SameValue and hashed with
/// Object::GetHash. Used for engine-internal maps keyed by arbitrary values.
/// </summary>
public sealed class ObjectHashTable : HashTableBase
{
    protected override void SwapPayload(int i, int j) => (_values[i], _values[j]) = (_values[j], _values[i]);

    /// <summary>ObjectHashTable::FindEntry(isolate, key): not found if the key has no hash yet.</summary>
    public InternalIndex FindEntry(Isolate isolate, JSValue key)
    {
        JSValue hash = ObjectOps.GetHash(key);
        // If the object does not have an identity hash, it was never used as a key.
        if (hash.IsUndefined) return InternalIndex.NotFound;
        return FindEntry(isolate, key, (uint)hash.Number);
    }

    /// <summary>Stores a key and value directly into an entry (tests fill tables by hand).</summary>
    internal void SetEntry(InternalIndex entry, JSValue key, JSValue value)
    {
        _keys[entry.AsInt] = key;
        _values[entry.AsInt] = value;
    }

    readonly JSValue[] _values;

    ObjectHashTable(int capacity) : base(InstanceType.FixedArrayType, capacity) => _values = new JSValue[capacity];

    public static ObjectHashTable New(int atLeastSpaceFor) => new(ComputeCapacity(atLeastSpaceFor));

    protected override uint HashForKey(in JSValue key) => ObjectOps.GetOrCreateHashRaw(key);

    public InternalIndex FindEntry(Isolate isolate, JSValue key, uint hash)
    {
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            JSValue element = _keys[entry];
            if (element.IsUndefined) return InternalIndex.NotFound;
            if (element.IsTheHole) continue;
            if (ObjectOps.SameValue(key, element)) return new InternalIndex(entry);
        }
    }

    /// <summary>ObjectHashTable::Lookup: the value, or the hole if absent.</summary>
    public JSValue Lookup(Isolate isolate, JSValue key)
    {
        // If the object does not have an identity hash, it was never used as a key.
        JSValue hash = ObjectOps.GetHash(key);
        if (hash.IsUndefined) return JSValue.TheHole;
        InternalIndex entry = FindEntry(isolate, key, (uint)hash.Number);
        return entry.IsNotFound ? JSValue.TheHole : _values[entry.AsInt];
    }

    public static ObjectHashTable Put(Isolate isolate, ObjectHashTable table, JSValue key, JSValue value)
    {
        uint hash = ObjectOps.GetOrCreateHashRaw(key);
        InternalIndex entry = table.FindEntry(isolate, key, hash);
        if (entry.IsFound)
        {
            table._values[entry.AsInt] = value;
            return table;
        }
        if (!table.HasSufficientCapacityToAdd(1))
        {
            var grown = New(table.NumberOfElements + 1);
            for (int i = 0; i < table.Capacity; i++)
            {
                if (!table.IsKey(i)) continue;
                InternalIndex ins = grown.FindInsertionEntry(ObjectOps.GetOrCreateHashRaw(table._keys[i]));
                grown._keys[ins.AsInt] = table._keys[i];
                grown._values[ins.AsInt] = table._values[i];
            }
            grown.SetNumberOfElements(table.NumberOfElements);
            table = grown;
        }
        InternalIndex e = table.FindInsertionEntry(hash);
        table._keys[e.AsInt] = key;
        table._values[e.AsInt] = value;
        table.ElementAdded();
        return table;
    }

    public static ObjectHashTable Remove(Isolate isolate, ObjectHashTable table, JSValue key, out bool wasPresent)
    {
        JSValue hash = ObjectOps.GetHash(key);
        if (hash.IsUndefined)
        {
            wasPresent = false;
            return table;
        }
        InternalIndex entry = table.FindEntry(isolate, key, (uint)hash.Number);
        wasPresent = entry.IsFound;
        if (!wasPresent) return table;
        table._keys[entry.AsInt] = JSValue.TheHole;
        table._values[entry.AsInt] = JSValue.TheHole;
        table.ElementRemoved();
        return table;
    }

    public JSValue ValueAt(InternalIndex entry) => _values[entry.AsInt];
}

/// <summary>V8's ObjectHashSet: an ObjectHashTable without values.</summary>
public sealed class ObjectHashSet : HashTableBase
{
    ObjectHashSet(int capacity) : base(InstanceType.FixedArrayType, capacity) { }

    public static ObjectHashSet New(int atLeastSpaceFor) => new(ComputeCapacity(atLeastSpaceFor));

    protected override uint HashForKey(in JSValue key) => ObjectOps.GetOrCreateHashRaw(key);

    InternalIndex FindEntry(JSValue key, uint hash)
    {
        int capacity = Capacity;
        int count = 1;
        for (int entry = FirstProbe(hash, capacity); ; entry = NextProbe(entry, count++, capacity))
        {
            JSValue element = _keys[entry];
            if (element.IsUndefined) return InternalIndex.NotFound;
            if (element.IsTheHole) continue;
            if (ObjectOps.SameValue(key, element)) return new InternalIndex(entry);
        }
    }

    /// <summary>ObjectHashSet::Has.</summary>
    public bool Has(Isolate isolate, JSValue key)
    {
        JSValue hash = ObjectOps.GetHash(key);
        // If the object does not have an identity hash, it was never used as a key.
        if (hash.IsUndefined) return false;
        return FindEntry(key, (uint)hash.Number).IsFound;
    }

    /// <summary>ObjectHashSet::Add.</summary>
    public static ObjectHashSet Add(Isolate isolate, ObjectHashSet set, JSValue key)
    {
        uint hash = ObjectOps.GetOrCreateHashRaw(key);
        if (set.FindEntry(key, hash).IsFound) return set;
        if (!set.HasSufficientCapacityToAdd(1))
        {
            var grown = New(set.NumberOfElements + 1);
            for (int i = 0; i < set.Capacity; i++)
            {
                if (!set.IsKey(i)) continue;
                InternalIndex ins = grown.FindInsertionEntry(ObjectOps.GetOrCreateHashRaw(set._keys[i]));
                grown._keys[ins.AsInt] = set._keys[i];
            }
            grown.SetNumberOfElements(set.NumberOfElements);
            set = grown;
        }
        InternalIndex e = set.FindInsertionEntry(hash);
        set._keys[e.AsInt] = key;
        set.ElementAdded();
        return set;
    }
}
