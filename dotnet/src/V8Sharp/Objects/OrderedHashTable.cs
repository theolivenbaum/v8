// Port of src/objects/ordered-hash-table.{h,cc} (OrderedHashTable,
// OrderedHashSet, OrderedHashMap and OrderedHashTableIterator::Transition):
// the insertion-ordered tables behind Map and Set.
//
// V8 packs the table into one FixedArray (prefix, bucket heads, then entries
// of [key, value..., chain]). V8Sharp keeps the same fields in separate arrays;
// capacities, the load factor, rehash/shrink points and the obsolete-table
// chaining that live iterators follow are V8's.
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's OrderedHashTable&lt;Derived, entrysize&gt;.</summary>
public abstract class OrderedHashTable : HeapObject
{
    public const int kNotFound = -1;
    public const int kInitialCapacity = 4;
    public const int kLoadFactor = 2;

    /// <summary>NumberOfDeletedElements is set to kClearedTableSentinel when the table is cleared.</summary>
    public const int kClearedTableSentinel = -1;

    readonly int _entrySize;
    int[] _buckets;
    JSValue[] _entries;
    int[] _chain;
    int _numberOfElements;
    int _numberOfDeletedElements;
    OrderedHashTable? _nextTable;
    int[]? _removedHoles;

    protected OrderedHashTable(InstanceType type, int entrySize, int capacity) : base(type)
    {
        _entrySize = entrySize;
        int numBuckets = capacity / kLoadFactor;
        _buckets = new int[numBuckets];
        _buckets.AsSpan().Fill(kNotFound);
        _entries = new JSValue[capacity * entrySize];
        _chain = new int[capacity];
    }

    /// <summary>MaxCapacity: bounded by FixedArray::kMaxLength as in V8's single-array layout.</summary>
    public static int MaxCapacity(int entrySize) => (FixedArrayBase.kMaxLength - 3) / (1 + ((entrySize + 1) * kLoadFactor));

    public int NumberOfElements => _numberOfElements;
    public int NumberOfDeletedElements => _numberOfDeletedElements;
    public int NumberOfBuckets => _buckets.Length;
    public int Capacity => NumberOfBuckets * kLoadFactor;
    public int UsedCapacity => _numberOfElements + _numberOfDeletedElements;

    /// <summary>IsObsolete: the table was rehashed or cleared and forwards to NextTable.</summary>
    public bool IsObsolete => _nextTable is not null;

    public OrderedHashTable? NextTable => _nextTable;

    /// <summary>RemovedIndexAt: the entry indices removed by the rehash that made this table obsolete.</summary>
    public int RemovedIndexAt(int index) => _removedHoles![index];

    public JSValue KeyAt(InternalIndex entry) => _entries[entry.AsInt * _entrySize];

    protected JSValue ValueAtRaw(int entry, int offset) => _entries[entry * _entrySize + offset];

    protected void SetValueAtRaw(int entry, int offset, JSValue value) => _entries[entry * _entrySize + offset] = value;

    public int HashToBucket(int hash) => hash & (NumberOfBuckets - 1);

    public int HashToEntryRaw(int hash)
    {
        int bucket = HashToBucket(hash);
        return _buckets[bucket];
    }

    public int NextChainEntryRaw(int entry) => _chain[entry];

    /// <summary>OrderedHashTable::FindEntry.</summary>
    public InternalIndex FindEntry(Isolate isolate, JSValue key)
    {
        if (NumberOfElements == 0)
        {
            // This is not just an optimization but also ensures that we do the right
            // thing if Capacity() == 0
            return InternalIndex.NotFound;
        }

        JSValue hash = ObjectOps.GetHash(key);
        // If the object does not have an identity hash, it was never used as a key
        if (hash.IsUndefined) return InternalIndex.NotFound;

        // Walk the chain in the bucket to find the key.
        for (int rawEntry = HashToEntryRaw((int)hash.Number); rawEntry != kNotFound; rawEntry = NextChainEntryRaw(rawEntry))
        {
            JSValue candidateKey = KeyAt(new InternalIndex(rawEntry));
            if (ObjectOps.SameValueZero(candidateKey, key)) return new InternalIndex(rawEntry);
        }

        return InternalIndex.NotFound;
    }

    /// <summary>OrderedHashTable::HasKey.</summary>
    public static bool HasKey(Isolate isolate, OrderedHashTable table, JSValue key) => table.FindEntry(isolate, key).IsFound;

    /// <summary>OrderedHashTable::Delete: replaces the entry with hash-table holes.</summary>
    public static bool Delete(Isolate isolate, OrderedHashTable table, JSValue key)
    {
        InternalIndex entry = table.FindEntry(isolate, key);
        if (entry.IsNotFound) return false;

        int nof = table.NumberOfElements;
        int nod = table.NumberOfDeletedElements;
        int index = entry.AsInt * table._entrySize;

        JSValue hashTableHole = JSValue.FromObject(Oddball.HashTableHole);
        for (int i = 0; i < table._entrySize; ++i) table._entries[index + i] = hashTableHole;

        table._numberOfElements = nof - 1;
        table._numberOfDeletedElements = nod + 1;
        return true;
    }

    protected abstract OrderedHashTable AllocateLike(int capacity);

    protected static int CheckCapacity(Isolate? isolate, int capacity, int entrySize)
    {
        // Capacity must be a power of two, since we depend on being able
        // to divide and multiple by 2 (kLoadFactor) to derive capacity
        // from number of buckets.
        capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(kInitialCapacity, capacity));
        if (capacity > MaxCapacity(entrySize))
        {
            isolate ??= Isolate.Current!;
            // Throw RangeError with a generic message.
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.CollectionGrowFailed, ReadOnlyRoots.empty_string));
        }
        return capacity;
    }

    /// <summary>OrderedHashTable::EnsureCapacityForAdding.</summary>
    protected static OrderedHashTable EnsureCapacityForAddingBase(Isolate isolate, OrderedHashTable table)
    {
        Debug.Assert(!table.IsObsolete);

        int nof = table.NumberOfElements;
        int nod = table.NumberOfDeletedElements;
        int capacity = table.Capacity;
        if (nof + nod < capacity) return table;

        int newCapacity;
        if (capacity == 0)
        {
            // step from empty to minimum proper size
            newCapacity = kInitialCapacity;
        }
        else if (nod >= (capacity >> 1))
        {
            // Don't need to grow if we can simply clear out deleted entries instead.
            // Note that we can't compact in place, though, so we always allocate
            // a new table.
            newCapacity = capacity;
        }
        else
        {
            newCapacity = capacity << 1;
        }

        return RehashBase(isolate, table, newCapacity);
    }

    /// <summary>OrderedHashTable::Shrink.</summary>
    protected static OrderedHashTable ShrinkBase(Isolate isolate, OrderedHashTable table)
    {
        Debug.Assert(!table.IsObsolete);

        int nof = table.NumberOfElements;
        int capacity = table.Capacity;
        if (nof >= (capacity >> 2)) return table;
        return RehashBase(isolate, table, capacity / 2);
    }

    /// <summary>OrderedHashTable::Clear: a fresh table; the old one forwards to it for live iterators.</summary>
    protected static OrderedHashTable ClearBase(Isolate isolate, OrderedHashTable table)
    {
        Debug.Assert(!table.IsObsolete);

        OrderedHashTable newTable = table.AllocateLike(kInitialCapacity);

        if (table.NumberOfBuckets > 0)
        {
            table._nextTable = newTable;
            table._numberOfDeletedElements = kClearedTableSentinel;
        }

        return newTable;
    }

    /// <summary>OrderedHashTable::Rehash(table, new_capacity).</summary>
    protected static OrderedHashTable RehashBase(Isolate isolate, OrderedHashTable table, int newCapacity)
    {
        Debug.Assert(!table.IsObsolete);

        OrderedHashTable newTable = table.AllocateLike(newCapacity);
        int newEntry = 0;
        int removedHolesIndex = 0;
        var removedHoles = table.NumberOfDeletedElements > 0 ? new int[table.NumberOfDeletedElements] : [];

        int entrySize = table._entrySize;
        int used = table.UsedCapacity;
        for (int oldEntry = 0; oldEntry < used; oldEntry++)
        {
            JSValue key = table.KeyAt(new InternalIndex(oldEntry));
            if (ReferenceEquals(key.HeapObjectOrNull, Oddball.HashTableHole))
            {
                removedHoles[removedHolesIndex++] = oldEntry;
                continue;
            }

            int bucket = newTable.HashToBucket((int)ObjectOps.GetHash(key).Number);
            int chainEntry = newTable._buckets[bucket];
            newTable._buckets[bucket] = newEntry;
            Array.Copy(table._entries, oldEntry * entrySize, newTable._entries, newEntry * entrySize, entrySize);
            newTable._chain[newEntry] = chainEntry;
            ++newEntry;
        }

        Debug.Assert(table.NumberOfDeletedElements == removedHolesIndex);

        newTable._numberOfElements = table.NumberOfElements;
        if (table.NumberOfBuckets > 0)
        {
            table._removedHoles = removedHoles;
            table._nextTable = newTable;
        }

        return newTable;
    }

    /// <summary>Appends a new entry for <paramref name="hash"/> (the insertion half of Add).</summary>
    protected int AppendEntry(int hash, JSValue key)
    {
        // Read the existing bucket values.
        int bucket = HashToBucket(hash);
        int previousEntry = HashToEntryRaw(hash);
        int nof = NumberOfElements;
        // Insert a new entry at the end,
        int newEntry = nof + NumberOfDeletedElements;
        _entries[newEntry * _entrySize] = key;
        _chain[newEntry] = previousEntry;
        // and point the bucket to the new entry.
        _buckets[bucket] = newEntry;
        _numberOfElements = nof + 1;
        return newEntry;
    }

    /// <summary>Whether the table already holds <paramref name="key"/> with <paramref name="hash"/> (Add's pre-check).</summary>
    protected bool ContainsKeyWithHash(int hash, JSValue key)
    {
        if (NumberOfElements == 0) return false;
        // Walk the chain of the bucket and try finding the key.
        for (int rawEntry = HashToEntryRaw(hash); rawEntry != kNotFound; rawEntry = NextChainEntryRaw(rawEntry))
        {
            JSValue candidateKey = KeyAt(new InternalIndex(rawEntry));
            // Do not add if we have the key already
            if (ObjectOps.SameValueZero(candidateKey, key)) return true;
        }
        return false;
    }

    /// <summary>
    /// OrderedHashTableIterator::Transition: moves an iterator position from an
    /// obsolete table to the live one, accounting for removed entries.
    /// </summary>
    public static OrderedHashTable TransitionIterator(OrderedHashTable table, ref int index)
    {
        if (!table.IsObsolete) return table;

        while (table.IsObsolete)
        {
            OrderedHashTable nextTable = table.NextTable!;

            if (index > 0)
            {
                int nod = table.NumberOfDeletedElements;

                if (nod == kClearedTableSentinel)
                {
                    index = 0;
                }
                else
                {
                    int oldIndex = index;
                    for (int i = 0; i < nod; ++i)
                    {
                        int removedIndex = table.RemovedIndexAt(i);
                        if (removedIndex >= oldIndex) break;
                        --index;
                    }
                }
            }

            table = nextTable;
        }
        return table;
    }
}

/// <summary>V8's OrderedHashSet (entry: key).</summary>
public sealed class OrderedHashSet : OrderedHashTable
{
    public const int kEntrySize = 1;

    OrderedHashSet(int capacity) : base(InstanceType.OrderedHashSetType, kEntrySize, capacity) { }

    /// <summary>OrderedHashSet::Allocate.</summary>
    public static OrderedHashSet Allocate(int capacity, Isolate? isolate = null) =>
        new(CheckCapacity(isolate, capacity, kEntrySize));

    protected override OrderedHashTable AllocateLike(int capacity) => Allocate(capacity);

    /// <summary>OrderedHashSet::Add.</summary>
    public static OrderedHashSet Add(Isolate isolate, OrderedHashSet table, JSValue key)
    {
        int hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.ContainsKeyWithHash(hash, key)) return table;

        table = (OrderedHashSet)EnsureCapacityForAddingBase(isolate, table);
        table.AppendEntry(hash, key);
        return table;
    }

    public static OrderedHashSet EnsureCapacityForAdding(Isolate isolate, OrderedHashSet table) =>
        (OrderedHashSet)EnsureCapacityForAddingBase(isolate, table);

    public static OrderedHashSet Shrink(Isolate isolate, OrderedHashSet table) => (OrderedHashSet)ShrinkBase(isolate, table);

    public static OrderedHashSet Clear(Isolate isolate, OrderedHashSet table) => (OrderedHashSet)ClearBase(isolate, table);

    public static OrderedHashSet Rehash(Isolate isolate, OrderedHashSet table, int newCapacity) =>
        (OrderedHashSet)RehashBase(isolate, table, newCapacity);

    /// <summary>OrderedHashSet::ConvertToKeysArray.</summary>
    public static FixedArray ConvertToKeysArray(Isolate isolate, OrderedHashSet table, GetKeysConversion convert)
    {
        int length = table.NumberOfElements;
        var result = new FixedArray(length);
        int j = 0;
        for (int i = 0; i < table.UsedCapacity && j < length; i++)
        {
            JSValue key = table.KeyAt(new InternalIndex(i));
            if (ReferenceEquals(key.HeapObjectOrNull, Oddball.HashTableHole)) continue;
            if (convert == GetKeysConversion.ConvertToString && ObjectOps.ToArrayIndex(key, out uint indexValue))
            {
                key = isolate.Factory.SizeToString(indexValue);
            }
            result.Set(j++, key);
        }
        return result;
    }
}

/// <summary>V8's OrderedHashMap (entry: key, value).</summary>
public sealed class OrderedHashMap : OrderedHashTable
{
    public const int kEntrySize = 2;
    public const int kValueOffset = 1;

    OrderedHashMap(int capacity) : base(InstanceType.OrderedHashMapType, kEntrySize, capacity) { }

    /// <summary>OrderedHashMap::Allocate.</summary>
    public static OrderedHashMap Allocate(int capacity, Isolate? isolate = null) =>
        new(CheckCapacity(isolate, capacity, kEntrySize));

    protected override OrderedHashTable AllocateLike(int capacity) => Allocate(capacity);

    public JSValue ValueAt(InternalIndex entry) => ValueAtRaw(entry.AsInt, kValueOffset);

    public void SetValueAt(InternalIndex entry, JSValue value) => SetValueAtRaw(entry.AsInt, kValueOffset, value);

    /// <summary>OrderedHashMap::Add: adds key/value unless the key is present.</summary>
    public static OrderedHashMap Add(Isolate isolate, OrderedHashMap table, JSValue key, JSValue value)
    {
        int hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.ContainsKeyWithHash(hash, key)) return table;

        table = (OrderedHashMap)EnsureCapacityForAddingBase(isolate, table);
        int newEntry = table.AppendEntry(hash, key);
        table.SetValueAtRaw(newEntry, kValueOffset, value);
        return table;
    }

    /// <summary>Map.prototype.set semantics: update the value if present, else add.</summary>
    public static OrderedHashMap Set(Isolate isolate, OrderedHashMap table, JSValue key, JSValue value)
    {
        InternalIndex entry = table.FindEntry(isolate, key);
        if (entry.IsFound)
        {
            table.SetValueAt(entry, value);
            return table;
        }
        return Add(isolate, table, key, value);
    }

    public static OrderedHashMap EnsureCapacityForAdding(Isolate isolate, OrderedHashMap table) =>
        (OrderedHashMap)EnsureCapacityForAddingBase(isolate, table);

    public static OrderedHashMap Shrink(Isolate isolate, OrderedHashMap table) => (OrderedHashMap)ShrinkBase(isolate, table);

    public static OrderedHashMap Clear(Isolate isolate, OrderedHashMap table) => (OrderedHashMap)ClearBase(isolate, table);

    public static OrderedHashMap Rehash(Isolate isolate, OrderedHashMap table, int newCapacity) =>
        (OrderedHashMap)RehashBase(isolate, table, newCapacity);

    /// <summary>OrderedHashMap::GetHash: the key's hash, or -1 if it has none (never used as a key).</summary>
    public static int GetHash(Isolate isolate, JSValue key)
    {
        JSValue hash = ObjectOps.GetHash(key);
        return hash.IsUndefined ? -1 : (int)hash.Number;
    }
}
