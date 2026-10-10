// Port of src/objects/js-collection.h, js-collection-inl.h and the
// JSMap/JSSet/JSWeakCollection parts of src/objects/objects.cc
// (JSMap::Initialize, JSMap::Clear, JSSet::Initialize, JSSet::Clear,
// JSWeakCollection::Initialize/Set/Delete/GetEntries).
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>V8's JSCollection (JSMap / JSSet).</summary>
public abstract class JSCollection(Map map) : JSObject(map)
{
    /// <summary>
    /// JSCollection::kAddFunctionDescriptorIndex: the index of "set"/"add" in
    /// the initial prototype's descriptors.
    /// </summary>
    public const int kAddFunctionDescriptorIndex = 3;

    /// <summary>The OrderedHashMap / OrderedHashSet backing store (V8's table).</summary>
    public abstract OrderedHashTable TableBase { get; }
}

/// <summary>V8's JSMap.</summary>
public sealed class JSMap(Map map) : JSCollection(map)
{
    public OrderedHashMap Table = ReadOnlyRoots.empty_ordered_hash_map;

    public override OrderedHashTable TableBase => Table;

    /// <summary>JSMap::Initialize.</summary>
    public static void Initialize(JSMap map, Isolate isolate) => map.Table = isolate.Factory.NewOrderedHashMap();

    /// <summary>JSMap::Clear.</summary>
    public static void Clear(Isolate isolate, JSMap map) => map.Table = OrderedHashMap.Clear(isolate, map.Table);

    /// <summary>JSMap::Rehash.</summary>
    public static void Rehash(Isolate isolate, JSMap map) => map.Table = OrderedHashMap.Rehash(isolate, map.Table);
}

/// <summary>V8's JSSet.</summary>
public sealed class JSSet(Map map) : JSCollection(map)
{
    public OrderedHashSet Table = ReadOnlyRoots.empty_ordered_hash_set;

    public override OrderedHashTable TableBase => Table;

    /// <summary>JSSet::Initialize.</summary>
    public static void Initialize(JSSet set, Isolate isolate) => set.Table = isolate.Factory.NewOrderedHashSet();

    /// <summary>JSSet::Clear.</summary>
    public static void Clear(Isolate isolate, JSSet set) => set.Table = OrderedHashSet.Clear(isolate, set.Table);

    /// <summary>JSSet::Rehash.</summary>
    public static void Rehash(Isolate isolate, JSSet set) => set.Table = OrderedHashSet.Rehash(isolate, set.Table);
}

/// <summary>
/// V8's JSMapIterator (JS_MAP_KEY_ITERATOR_TYPE, JS_MAP_VALUE_ITERATOR_TYPE,
/// JS_MAP_KEY_VALUE_ITERATOR_TYPE): the table it walks (which may become
/// obsolete and forward to a newer one) and the entry index.
/// </summary>
public sealed class JSMapIterator(Map map) : JSObject(map)
{
    public OrderedHashMap Table = ReadOnlyRoots.empty_ordered_hash_map;
    public int Index;
}

/// <summary>V8's JSSetIterator (JS_SET_VALUE_ITERATOR_TYPE, JS_SET_KEY_VALUE_ITERATOR_TYPE).</summary>
public sealed class JSSetIterator(Map map) : JSObject(map)
{
    public OrderedHashSet Table = ReadOnlyRoots.empty_ordered_hash_set;
    public int Index;
}

/// <summary>
/// V8's JSWeakCollection (JSWeakMap / JSWeakSet). Deviation: V8 keeps an
/// EphemeronHashTable whose entries die with their keys; V8Sharp uses a
/// ConditionalWeakTable keyed by the key object (a JSReceiver or a symbol
/// that is not registered), which has the same ephemeron semantics under the
/// CLR GC: an entry keeps its value alive only while its key is alive, and a
/// value that references its own key does not keep the key alive.
/// </summary>
public abstract class JSWeakCollection(Map map) : JSObject(map)
{
    /// <summary>JSWeakCollection::kAddFunctionDescriptorIndex (see JSCollection).</summary>
    public const int kAddFunctionDescriptorIndex = 3;

    public readonly ConditionalWeakTable<HeapObject, StrongBox> Table = new();

    /// <summary>A mutable box for the value (ConditionalWeakTable values are reference types).</summary>
    public sealed class StrongBox(JSValue value)
    {
        public JSValue Value = value;
    }

    /// <summary>JSWeakCollection::Set.</summary>
    public static void Set(JSWeakCollection weakCollection, HeapObject key, JSValue value)
    {
        if (weakCollection.Table.TryGetValue(key, out StrongBox? box))
        {
            box.Value = value;
        }
        else
        {
            weakCollection.Table.Add(key, new StrongBox(value));
        }
    }

    /// <summary>JSWeakCollection::Set for a JSValue key known to be a heap object.</summary>
    public static void Set(JSWeakCollection weakCollection, JSValue key, JSValue value) =>
        Set(weakCollection, key.Object, value);

    /// <summary>JSWeakCollection::Delete.</summary>
    public static bool Delete(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.Remove(k);

    /// <summary>Whether <paramref name="key"/> has an entry.</summary>
    public static bool Has(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.TryGetValue(k, out _);

    /// <summary>The entry's box, or null.</summary>
    public static StrongBox? Lookup(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.TryGetValue(k, out StrongBox? box) ? box : null;

    /// <summary>The value for <paramref name="key"/>, or undefined.</summary>
    public static JSValue Get(JSWeakCollection weakCollection, JSValue key) =>
        Lookup(weakCollection, key)?.Value ?? JSValue.Undefined;

    /// <summary>
    /// JSWeakCollection::GetEntries: up to <paramref name="maxEntries"/>
    /// entries (0 = all) as [key, value, key, value ...] for a WeakMap and
    /// [key, key ...] for a WeakSet, in no particular order.
    /// </summary>
    public static FixedArray GetEntries(JSWeakCollection holder, int maxEntries)
    {
        bool isMap = holder is JSWeakMap;
        var list = new List<JSValue>();
        int count = 0;
        foreach (KeyValuePair<HeapObject, StrongBox> entry in holder.Table)
        {
            if (maxEntries != 0 && count == maxEntries) break;
            list.Add(entry.Key);
            if (isMap) list.Add(entry.Value.Value);
            count++;
        }
        return new FixedArray(list.ToArray());
    }
}

/// <summary>V8's JSWeakMap.</summary>
public sealed class JSWeakMap(Map map) : JSWeakCollection(map);

/// <summary>V8's JSWeakSet.</summary>
public sealed class JSWeakSet(Map map) : JSWeakCollection(map);
