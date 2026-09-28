// Port of src/ic/stub-cache.{h,cc}: the megamorphic stub cache, a primary
// and a secondary table of (name, map) -> handler entries. Entries retired
// from the primary table move to the secondary one.
//
// V8 hashes the map's address; a managed object has no stable address, so the
// map's identity hash (RuntimeHelpers.GetHashCode) takes its place.
using System.Runtime.CompilerServices;

namespace V8Sharp.IC;

public sealed class StubCache
{
    public const int kPrimaryTableBits = 12;
    public const int kPrimaryTableSize = 1 << kPrimaryTableBits;
    public const int kSecondaryTableBits = 10;
    public const int kSecondaryTableSize = 1 << kSecondaryTableBits;

    struct Entry
    {
        public Name? Key;
        public Map? Map;
        public HeapObject? Value;
    }

    readonly Entry[] _primary = new Entry[kPrimaryTableSize];
    readonly Entry[] _secondary = new Entry[kSecondaryTableSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int PrimaryOffset(Name name, Map map)
    {
        // Compute the hash of the name (use entire hash field).
        uint field = name.RawHashField;
        uint mapBits = (uint)RuntimeHelpers.GetHashCode(map);
        uint mapLow = mapBits ^ (mapBits >> kPrimaryTableBits);
        // Base the offset on a simple combination of name and map.
        uint key = mapLow + field;
        return (int)(key & (kPrimaryTableSize - 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int SecondaryOffset(Name name, Map oldMap)
    {
        uint nameBits = (uint)RuntimeHelpers.GetHashCode(name);
        uint mapBits = (uint)RuntimeHelpers.GetHashCode(oldMap);
        uint key = mapBits + nameBits;
        key += key >> kSecondaryTableBits;
        return (int)(key & (kSecondaryTableSize - 1));
    }

    /// <summary>StubCache::Set.</summary>
    public void Set(Name name, Map map, HeapObject handler)
    {
        // Compute the primary entry.
        ref Entry primary = ref _primary[PrimaryOffset(name, map)];
        // If the primary entry has useful data in it, we retire it to the
        // secondary cache before overwriting it.
        if (primary.Map is not null && primary.Key is not null)
        {
            _secondary[SecondaryOffset(primary.Key, primary.Map)] = primary;
        }
        // Update primary cache.
        primary.Key = name;
        primary.Value = handler;
        primary.Map = map;
    }

    /// <summary>StubCache::Get: the handler, or null when missing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeapObject? Get(Name name, Map map)
    {
        ref Entry primary = ref _primary[PrimaryOffset(name, map)];
        if (ReferenceEquals(primary.Key, name) && ReferenceEquals(primary.Map, map)) return primary.Value;
        ref Entry secondary = ref _secondary[SecondaryOffset(name, map)];
        if (ReferenceEquals(secondary.Key, name) && ReferenceEquals(secondary.Map, map)) return secondary.Value;
        return null;
    }

    /// <summary>StubCache::Clear.</summary>
    public void Clear()
    {
        Array.Clear(_primary);
        Array.Clear(_secondary);
    }
}
