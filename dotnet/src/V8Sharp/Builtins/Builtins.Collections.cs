// Port of src/builtins/builtins-collections-gen.{h,cc}, builtins-collections.cc,
// collections.tq, map-groupby.tq (with GroupByImpl of object-groupby.tq) and
// the collection runtime functions of src/runtime/runtime-collections.cc
// (MapGrow, MapShrink, SetGrow, SetShrink, OrderedHashSetGrow,
// WeakCollectionSet/Delete): the Map, Set, WeakMap and WeakSet constructors
// and prototype methods, the Map and Set iterators, and the helpers other
// builtins use (MapIteratorToList, SetOrSetIteratorToList, the
// original-iteration predicates).
using System.Runtime.CompilerServices;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterCollections()
    {
        Register(Builtin.MapConstructor, CollectionsBuiltins.MapConstructor);
        Register(Builtin.MapGroupBy, CollectionsBuiltins.MapGroupBy);
        Register(Builtin.MapPrototypeGet, CollectionsBuiltins.MapPrototypeGet);
        Register(Builtin.MapPrototypeSet, CollectionsBuiltins.MapPrototypeSet);
        Register(Builtin.MapPrototypeHas, CollectionsBuiltins.MapPrototypeHas);
        Register(Builtin.MapPrototypeDelete, CollectionsBuiltins.MapPrototypeDelete);
        Register(Builtin.MapPrototypeClear, CollectionsBuiltins.MapPrototypeClear);
        Register(Builtin.MapPrototypeEntries, CollectionsBuiltins.MapPrototypeEntries);
        Register(Builtin.MapPrototypeForEach, CollectionsBuiltins.MapPrototypeForEach);
        Register(Builtin.MapPrototypeKeys, CollectionsBuiltins.MapPrototypeKeys);
        Register(Builtin.MapPrototypeGetSize, CollectionsBuiltins.MapPrototypeGetSize);
        Register(Builtin.MapPrototypeValues, CollectionsBuiltins.MapPrototypeValues);
        Register(Builtin.MapPrototypeGetOrInsert, CollectionsBuiltins.MapPrototypeGetOrInsert);
        Register(Builtin.MapPrototypeGetOrInsertComputed, CollectionsBuiltins.MapPrototypeGetOrInsertComputed);
        Register(Builtin.MapIteratorPrototypeNext, CollectionsBuiltins.MapIteratorPrototypeNext);

        Register(Builtin.SetConstructor, CollectionsBuiltins.SetConstructor);
        Register(Builtin.SetPrototypeHas, CollectionsBuiltins.SetPrototypeHas);
        Register(Builtin.SetPrototypeAdd, CollectionsBuiltins.SetPrototypeAdd);
        Register(Builtin.SetPrototypeDelete, CollectionsBuiltins.SetPrototypeDelete);
        Register(Builtin.SetPrototypeClear, CollectionsBuiltins.SetPrototypeClear);
        Register(Builtin.SetPrototypeEntries, CollectionsBuiltins.SetPrototypeEntries);
        Register(Builtin.SetPrototypeForEach, CollectionsBuiltins.SetPrototypeForEach);
        Register(Builtin.SetPrototypeGetSize, CollectionsBuiltins.SetPrototypeGetSize);
        Register(Builtin.SetPrototypeValues, CollectionsBuiltins.SetPrototypeValues);
        Register(Builtin.SetIteratorPrototypeNext, CollectionsBuiltins.SetIteratorPrototypeNext);
        Register(Builtin.SetPrototypeUnion, CollectionsBuiltins.SetPrototypeUnion);
        Register(Builtin.SetPrototypeIntersection, CollectionsBuiltins.SetPrototypeIntersection);
        Register(Builtin.SetPrototypeDifference, CollectionsBuiltins.SetPrototypeDifference);
        Register(Builtin.SetPrototypeSymmetricDifference, CollectionsBuiltins.SetPrototypeSymmetricDifference);
        Register(Builtin.SetPrototypeIsSubsetOf, CollectionsBuiltins.SetPrototypeIsSubsetOf);
        Register(Builtin.SetPrototypeIsSupersetOf, CollectionsBuiltins.SetPrototypeIsSupersetOf);
        Register(Builtin.SetPrototypeIsDisjointFrom, CollectionsBuiltins.SetPrototypeIsDisjointFrom);

        Register(Builtin.WeakMapConstructor, CollectionsBuiltins.WeakMapConstructor);
        Register(Builtin.WeakMapPrototypeGet, CollectionsBuiltins.WeakMapPrototypeGet);
        Register(Builtin.WeakMapPrototypeSet, CollectionsBuiltins.WeakMapPrototypeSet);
        Register(Builtin.WeakMapPrototypeHas, CollectionsBuiltins.WeakMapPrototypeHas);
        Register(Builtin.WeakMapPrototypeDelete, CollectionsBuiltins.WeakMapPrototypeDelete);
        Register(Builtin.WeakMapPrototypeGetOrInsert, CollectionsBuiltins.WeakMapPrototypeGetOrInsert);
        Register(Builtin.WeakMapPrototypeGetOrInsertComputed, CollectionsBuiltins.WeakMapPrototypeGetOrInsertComputed);
        Register(Builtin.WeakSetConstructor, CollectionsBuiltins.WeakSetConstructor);
        Register(Builtin.WeakSetPrototypeAdd, CollectionsBuiltins.WeakSetPrototypeAdd);
        Register(Builtin.WeakSetPrototypeHas, CollectionsBuiltins.WeakSetPrototypeHas);
        Register(Builtin.WeakSetPrototypeDelete, CollectionsBuiltins.WeakSetPrototypeDelete);
    }
}

/// <summary>V8's BaseCollectionsAssembler / CollectionsBuiltinsAssembler / WeakCollectionsBuiltinsAssembler.</summary>
public static partial class CollectionsBuiltins
{
    /// <summary>BaseCollectionsAssembler::Variant.</summary>
    public enum Variant { kMap, kSet, kWeakMap, kWeakSet }

    // ---- Receiver checks --------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ThrowIncompatibleMethodReceiver(Isolate isolate, string methodName, JSValue receiver) =>
        isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
            isolate.Factory.NewStringFromAsciiChecked(methodName), receiver);

    /// <summary>ThrowIfNotInstanceType(JS_MAP_TYPE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSMap CheckMap(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSMap map) return map;
        ThrowIncompatibleMethodReceiver(isolate, methodName, receiver);
        return null!;
    }

    /// <summary>ThrowIfNotInstanceType(JS_SET_TYPE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSSet CheckSet(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSSet set) return set;
        ThrowIncompatibleMethodReceiver(isolate, methodName, receiver);
        return null!;
    }

    /// <summary>ThrowIfNotCallable.</summary>
    static void ThrowIfNotCallable(Isolate isolate, JSValue callback, string methodName)
    {
        if (!ObjectOps.IsCallable(callback))
        {
            isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }
    }

    // ---- Keys ----------------------------------------------------------------------------------

    /// <summary>NormalizeNumberKey: -0 becomes +0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue NormalizeNumberKey(JSValue key) =>
        key.IsNumber && key.Number == 0.0 ? JSValue.Zero : key;

    /// <summary>Object::CanBeHeldWeakly: a JSReceiver or a symbol that is not registered (Symbol.for).</summary>
    public static bool CanBeHeldWeakly(JSValue obj) => obj.HeapObjectOrNull switch
    {
        JSReceiver => true,
        Symbol symbol => !symbol.IsInPublicSymbolTable,
        _ => false,
    };

    // ---- Table operations ----------------------------------------------------------------------

    /// <summary>TableHasKey for a map table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TableHasKey(OrderedHashTable table, JSValue key) => table.FindEntryAndHash(key, out _) >= 0;

    /// <summary>
    /// TableSet (MapPrototypeSet's AddToOrderedHashTable): updates the value of
    /// an existing key or appends a new entry, growing through Runtime_MapGrow.
    /// </summary>
    public static void MapTableSet(Isolate isolate, JSMap map, JSValue key, JSValue value)
    {
        key = NormalizeNumberKey(key);
        OrderedHashMap table = map.Table;
        int entry = table.FindEntryAndHash(key, out int hash);
        if (entry >= 0)
        {
            // If we found the entry, we just store the value there.
            table.SetValueAtRaw(entry, value);
            return;
        }
        // Otherwise, go to runtime to compute the hash code.
        if (hash < 0) hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.NeedsGrowForAdding) table = MapGrow(isolate, map);
        int newEntry = table.AddNewEntry(hash, key);
        table.SetValueAtRaw(newEntry, value);
    }

    /// <summary>Runtime_MapGrow.</summary>
    static OrderedHashMap MapGrow(Isolate isolate, JSMap holder)
    {
        OrderedHashMap table;
        try
        {
            table = OrderedHashMap.EnsureCapacityForAdding(isolate, holder.Table);
        }
        catch (JavaScriptException)
        {
            // Replace generic RangeError exception with a more descriptive one.
            isolate.ThrowRangeError(MessageTemplate.CollectionGrowFailed, isolate.Factory.NewStringFromAsciiChecked("Map"));
            throw;
        }
        holder.Table = table;
        return table;
    }

    /// <summary>SetPrototypeAdd's AddToOrderedHashTable, growing through Runtime_SetGrow.</summary>
    public static void SetTableAdd(Isolate isolate, JSSet set, JSValue key)
    {
        key = NormalizeNumberKey(key);
        OrderedHashSet table = set.Table;
        int entry = table.FindEntryAndHash(key, out int hash);
        // If the entry was found, there is nothing to do.
        if (entry >= 0) return;
        if (hash < 0) hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.NeedsGrowForAdding)
        {
            // Runtime_SetGrow.
            try
            {
                table = OrderedHashSet.EnsureCapacityForAdding(isolate, table);
            }
            catch (JavaScriptException)
            {
                isolate.ThrowRangeError(MessageTemplate.CollectionGrowFailed, isolate.Factory.NewStringFromAsciiChecked("Set"));
                throw;
            }
            set.Table = table;
        }
        table.AddNewEntry(hash, key);
    }

    /// <summary>
    /// AddToSetTable: adds to a standalone set table (the result of a Set
    /// method), growing through Runtime_OrderedHashSetGrow; returns the table.
    /// </summary>
    public static OrderedHashSet AddToSetTable(Isolate isolate, OrderedHashSet table, JSValue key, string methodName)
    {
        key = NormalizeNumberKey(key);
        int entry = table.FindEntryAndHash(key, out int hash);
        if (entry >= 0) return table;
        if (hash < 0) hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.NeedsGrowForAdding)
        {
            try
            {
                table = OrderedHashSet.EnsureCapacityForAdding(isolate, table);
            }
            catch (JavaScriptException)
            {
                // Replace generic RangeError exception with a more descriptive one.
                isolate.ThrowRangeError(MessageTemplate.OutOfMemory, isolate.Factory.NewStringFromAsciiChecked(methodName));
                throw;
            }
        }
        table.AddNewEntry(hash, key);
        return table;
    }

    /// <summary>
    /// DeleteFromSetTable: returns the new number of elements, or -1 (V8's
    /// NotFound label) if <paramref name="key"/> is not present.
    /// </summary>
    public static int DeleteFromSetTable(OrderedHashSet table, JSValue key)
    {
        int entry = table.FindEntryAndHash(NormalizeNumberKey(key), out _);
        if (entry < 0) return -1;
        return table.DeleteEntry(entry);
    }

    /// <summary>
    /// NextSkipHashTableHoles: the next live entry at or after
    /// <paramref name="index"/>; returns the entry and advances
    /// <paramref name="index"/> past it, or -1 at the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextSkipHashTableHoles(OrderedHashTable table, ref int index)
    {
        int usedCapacity = table.UsedCapacity;
        while (index < usedCapacity)
        {
            int entry = index++;
            if (!table.IsDeletedEntry(entry)) return entry;
        }
        return -1;
    }

    // ---- Constructors (BaseCollectionsAssembler::GenerateConstructor) ----------------------------

    public static JSValue MapConstructor(Isolate isolate, in BuiltinArguments args) =>
        GenerateConstructor(isolate, args, Variant.kMap, "Map");

    public static JSValue SetConstructor(Isolate isolate, in BuiltinArguments args) =>
        GenerateConstructor(isolate, args, Variant.kSet, "Set");

    public static JSValue WeakMapConstructor(Isolate isolate, in BuiltinArguments args) =>
        GenerateConstructor(isolate, args, Variant.kWeakMap, "WeakMap");

    public static JSValue WeakSetConstructor(Isolate isolate, in BuiltinArguments args) =>
        GenerateConstructor(isolate, args, Variant.kWeakSet, "WeakSet");

    static JSFunction GetConstructor(Variant variant, NativeContext nativeContext) => variant switch
    {
        Variant.kMap => nativeContext.JSMapFun,
        Variant.kSet => nativeContext.JSSetFun,
        Variant.kWeakMap => nativeContext.JSWeakMapFun,
        _ => nativeContext.JSWeakSetFun,
    };

    static JSFunction GetInitialAddFunction(Variant variant, NativeContext nativeContext) => variant switch
    {
        Variant.kMap => nativeContext.MapSet,
        Variant.kSet => nativeContext.SetAdd,
        Variant.kWeakMap => nativeContext.WeakMapSet,
        _ => nativeContext.WeakSetAdd,
    };

    static Map GetInitialCollectionPrototype(Variant variant, NativeContext nativeContext) => variant switch
    {
        Variant.kMap => nativeContext.InitialMapPrototypeMap,
        Variant.kSet => nativeContext.InitialSetPrototypeMap,
        Variant.kWeakMap => nativeContext.InitialWeakMapPrototypeMap,
        _ => nativeContext.InitialWeakSetPrototypeMap,
    };

    static JSString GetAddFunctionName(Variant variant) =>
        variant is Variant.kMap or Variant.kWeakMap ? ReadOnlyRoots.set_string : ReadOnlyRoots.add_string;

    /// <summary>BaseCollectionsAssembler::GenerateConstructor.</summary>
    static JSValue GenerateConstructor(Isolate isolate, in BuiltinArguments args, Variant variant, string constructorName)
    {
        JSValue iterable = args.AtOrUndefined(1);
        if (args.NewTarget.IsUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.ConstructorNotFunction,
                isolate.Factory.NewStringFromAsciiChecked(constructorName));
        }

        NativeContext nativeContext = isolate.NativeContext;
        JSObject collection = AllocateJSCollection(isolate, GetConstructor(variant, nativeContext), args.NewTarget.As<JSReceiver>());

        // The empty case.
        //
        // This is handled specially to simplify AddConstructorEntries, which is
        // complex and contains multiple fast paths.
        if (iterable.IsNullOrUndefined)
        {
            AllocateTable(isolate, variant, collection, 0);
            return collection;
        }

        AddConstructorEntries(isolate, variant, nativeContext, collection, iterable);
        return collection;
    }

    /// <summary>AllocateJSCollection: fast when new.target is the constructor.</summary>
    static JSObject AllocateJSCollection(Isolate isolate, JSFunction constructor, JSReceiver newTarget)
    {
        if (ReferenceEquals(constructor, newTarget)) return isolate.Factory.NewJSObjectFromMap(constructor.InitialMap);
        return JSObject.New(isolate, constructor, newTarget);
    }

    /// <summary>AllocateTable: a fresh backing store for the collection.</summary>
    static void AllocateTable(Isolate isolate, Variant variant, JSObject collection, int atLeastSpaceFor)
    {
        switch (variant)
        {
            case Variant.kMap:
                ((JSMap)collection).Table = isolate.Factory.NewOrderedHashMap();
                break;
            case Variant.kSet:
                ((JSSet)collection).Table = OrderedHashSet.Allocate(HashTableComputeCapacity(atLeastSpaceFor), isolate);
                break;
            default:
                // The ConditionalWeakTable of a weak collection needs no allocation.
                break;
        }
    }

    /// <summary>HashTableComputeCapacity: at_least_space_for * 1.5, rounded up to a power of two (at least 4).</summary>
    static int HashTableComputeCapacity(int atLeastSpaceFor)
    {
        int capacity = atLeastSpaceFor + (atLeastSpaceFor >> 1);
        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 4));
    }

    /// <summary>
    /// GotoIfInitialAddFunctionModified, inverted: whether the collection's
    /// prototype is the initial one and its "set"/"add" is still the initial
    /// builtin. V8 checks the prototype's map and the constness of the add
    /// function's descriptor; V8Sharp checks the map and the property value.
    /// </summary>
    static bool IsInitialAddFunctionUnmodified(Isolate isolate, Variant variant, NativeContext nativeContext, JSObject collection)
    {
        if (collection.Map.Prototype is not JSObject prototype) return false;
        if (!ReferenceEquals(prototype.Map, GetInitialCollectionPrototype(variant, nativeContext))) return false;
        var it = new LookupIterator(isolate, prototype, GetAddFunctionName(variant), LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        return it.State == LookupIterator.StateKind.DATA &&
               ReferenceEquals(it.GetDataValue().HeapObjectOrNull, GetInitialAddFunction(variant, nativeContext));
    }

    /// <summary>BaseCollectionsAssembler::AddConstructorEntries.</summary>
    static void AddConstructorEntries(Isolate isolate, Variant variant, NativeContext nativeContext, JSObject collection,
        JSValue initialEntries)
    {
        // The slow path is taken if the initial add function is modified. This check
        // must precede the kSet fast path below, which has the side effect of
        // exhausting {initial_entries} if it is a JSSetIterator.
        if (IsInitialAddFunctionUnmodified(isolate, variant, nativeContext, collection))
        {
            if (initialEntries.HeapObjectOrNull is JSArray array && IteratorBuiltins.IsFastJSArrayWithNoCustomIteration(isolate, array))
            {
                int atLeastSpaceFor = variant is Variant.kWeakMap or Variant.kWeakSet
                    ? (int)array.Length.Number
                    : OrderedHashSet.kInitialCapacity;
                AllocateTable(isolate, variant, collection, atLeastSpaceFor);
                if (AddConstructorEntriesFromFastJSArray(isolate, variant, collection, array)) return;
                // if_may_have_side_effects: an entry was not a fast [key, value]
                // array. Start over on the slow path with a fresh table.
            }
            else if (variant == Variant.kSet && initialEntries.HeapObjectOrNull is JSObject source &&
                     IsIterableWithOriginalValueSetIterator(isolate, source))
            {
                OrderedHashSet sourceTable = SetOrSetIteratorToSet(source);
                AllocateTable(isolate, variant, collection, sourceTable.NumberOfElements);
                AddConstructorEntriesFromSet((JSSet)collection, sourceTable);
                return;
            }
        }

        AllocateTable(isolate, variant, collection, 0);
        AddConstructorEntriesFromIterable(isolate, variant, nativeContext, collection, initialEntries);
    }

    /// <summary>
    /// AddConstructorEntriesFromFastJSArray: adds the elements of a fast array
    /// with the initial add function. Returns false (if_may_have_side_effects)
    /// when a Map entry is not a fast array. On an exception the array iterator
    /// the spec would have used is closed (IteratorCloseOnException).
    /// </summary>
    static bool AddConstructorEntriesFromFastJSArray(Isolate isolate, Variant variant, JSObject collection, JSArray fastJSArray)
    {
        int length = (int)fastJSArray.Length.Number;
        int index = 0;
        try
        {
            switch (fastJSArray.Elements)
            {
                case FixedArray elements:
                    for (; index < length; index++)
                    {
                        JSValue element = index < elements.Length ? elements[index] : JSValue.TheHole;
                        if (element.IsTheHole) element = JSValue.Undefined;
                        if (!AddConstructorEntry(isolate, variant, collection, element, mayHaveSideEffects: true)) return false;
                    }
                    break;
                case FixedDoubleArray doubles:
                    if (length == 0) break;
                    // A Map constructor requires entries to be arrays (ex. [key, value]),
                    // so a FixedDoubleArray can never succeed.
                    if (variant is Variant.kMap or Variant.kWeakMap)
                    {
                        JSValue element = doubles.IsTheHole(0) ? JSValue.Undefined : JSValue.FromNumber(doubles.GetScalar(0));
                        isolate.ThrowTypeError(MessageTemplate.IteratorValueNotAnObject, element);
                    }
                    for (; index < length; index++)
                    {
                        JSValue entry = index >= doubles.Length || doubles.IsTheHole(index)
                            ? JSValue.Undefined
                            : JSValue.FromNumber(doubles.GetScalar(index));
                        AddConstructorEntry(isolate, variant, collection, entry, mayHaveSideEffects: false);
                    }
                    break;
            }
        }
        catch (JavaScriptException)
        {
            // In case exception is thrown during collection population, materialize
            // the iteator and execute iterator closing protocol. It might be
            // non-trivial in case "return" callback is added somewhere in the
            // iterator's prototype chain.
            JSReceiver iterator = CreateArrayIterator(isolate, fastJSArray, index + 1);
            IteratorBuiltins.IteratorCloseOnException(isolate, iterator);
            throw;
        }
        return true;
    }

    /// <summary>CreateArrayIterator(array, kValues, nextIndex).</summary>
    static JSArrayIterator CreateArrayIterator(Isolate isolate, JSArray array, int nextIndex)
    {
        var iterator = (JSArrayIterator)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.InitialArrayIteratorMap);
        iterator.IteratedObject = array;
        iterator.NextIndex = JSValue.FromInt(nextIndex);
        iterator.Kind = IterationKind.Values;
        return iterator;
    }

    /// <summary>
    /// AddConstructorEntry with the initial add function (the fast paths call
    /// the builtin's semantics directly; it runs no user code). Returns false
    /// (if_may_have_side_effects) when a Map entry is not a fast pair.
    /// </summary>
    static bool AddConstructorEntry(Isolate isolate, Variant variant, JSObject collection, JSValue keyValue, bool mayHaveSideEffects)
    {
        switch (variant)
        {
            case Variant.kMap:
            case Variant.kWeakMap:
            {
                if (!LoadKeyValuePairNoSideEffects(isolate, keyValue, out JSValue key, out JSValue value))
                {
                    if (mayHaveSideEffects) return false;
                    LoadKeyValuePair(isolate, keyValue, out key, out value);
                }
                if (variant == Variant.kMap) MapTableSet(isolate, (JSMap)collection, key, value);
                else WeakCollectionSet(isolate, (JSWeakCollection)collection, key, value, MessageTemplate.InvalidWeakMapKey);
                return true;
            }
            case Variant.kSet:
                SetTableAdd(isolate, (JSSet)collection, keyValue);
                return true;
            default:
                WeakCollectionSet(isolate, (JSWeakCollection)collection, keyValue, JSValue.True, MessageTemplate.InvalidWeakSetValue);
                return true;
        }
    }

    /// <summary>BaseCollectionsAssembler::AddConstructorEntriesFromIterable.</summary>
    static void AddConstructorEntriesFromIterable(Isolate isolate, Variant variant, NativeContext nativeContext, JSObject collection,
        JSValue iterable)
    {
        // GetAddFunction.
        JSString addFuncName = GetAddFunctionName(variant);
        JSValue addFunc = JSReceiver.GetProperty(isolate, collection, addFuncName);
        if (!ObjectOps.IsCallable(addFunc))
        {
            isolate.ThrowTypeError(MessageTemplate.PropertyNotFunction, addFunc, addFuncName, collection);
        }

        IteratorRecord iterator = IteratorBuiltins.GetIterator(isolate, iterable);
        Map fastIteratorResultMap = nativeContext.IteratorResultMap;
        while (IteratorBuiltins.IteratorStep(isolate, iterator, out JSReceiver next, fastIteratorResultMap))
        {
            JSValue nextValue = IteratorBuiltins.IteratorValue(isolate, next, fastIteratorResultMap);
            try
            {
                if (variant is Variant.kMap or Variant.kWeakMap)
                {
                    LoadKeyValuePair(isolate, nextValue, out JSValue key, out JSValue value);
                    Execution.Call(isolate, addFunc, collection, [key, value]);
                }
                else
                {
                    Execution.Call(isolate, addFunc, collection, [nextValue]);
                }
            }
            catch (JavaScriptException)
            {
                IteratorBuiltins.IteratorCloseOnException(isolate, iterator.Object);
                throw;
            }
        }
    }

    /// <summary>CollectionsBuiltinsAssembler::AddConstructorEntriesFromSet: copies the live keys of <paramref name="table"/>.</summary>
    static void AddConstructorEntriesFromSet(JSSet collection, OrderedHashSet table)
    {
        OrderedHashSet entryTable = collection.Table;
        int usedCapacity = table.UsedCapacity;
        for (int i = 0; i < usedCapacity; i++)
        {
            if (table.IsDeletedEntry(i)) continue;
            JSValue key = table.KeyAtRaw(i);
            entryTable.AddNewEntry((int)ObjectOps.GetHash(key).Number, key);
        }
    }

    /// <summary>collections::LoadKeyValuePairNoSideEffects: a fast JSArray [k, v] (holes read as undefined).</summary>
    public static bool LoadKeyValuePairNoSideEffects(Isolate isolate, JSValue o, out JSValue key, out JSValue value)
    {
        key = JSValue.Undefined;
        value = JSValue.Undefined;
        if (o.HeapObjectOrNull is not JSArray array || !IteratorBuiltins.IsFastJSArrayForRead(isolate, array)) return false;
        int length = (int)array.Length.Number;
        switch (array.Elements)
        {
            case FixedArray fixedArray:
                if (length > 0 && fixedArray.Length > 0 && !fixedArray[0].IsTheHole) key = fixedArray[0];
                if (length > 1 && fixedArray.Length > 1 && !fixedArray[1].IsTheHole) value = fixedArray[1];
                return true;
            case FixedDoubleArray doubleArray:
                if (length > 0 && doubleArray.Length > 0 && !doubleArray.IsTheHole(0)) key = JSValue.FromNumber(doubleArray.GetScalar(0));
                if (length > 1 && doubleArray.Length > 1 && !doubleArray.IsTheHole(1)) value = JSValue.FromNumber(doubleArray.GetScalar(1));
                return true;
        }
        return false;
    }

    /// <summary>collections::LoadKeyValuePair.</summary>
    public static void LoadKeyValuePair(Isolate isolate, JSValue o, out JSValue key, out JSValue value)
    {
        if (LoadKeyValuePairNoSideEffects(isolate, o, out key, out value)) return;
        if (o.HeapObjectOrNull is not JSReceiver receiver)
        {
            isolate.ThrowTypeError(MessageTemplate.IteratorValueNotAnObject, o);
            return;
        }
        key = JSReceiver.GetElement(isolate, receiver, 0);
        value = JSReceiver.GetElement(isolate, receiver, 1);
    }

    // ---- Map.prototype --------------------------------------------------------------------------

    /// <summary>Map.prototype.get ( key ).</summary>
    public static JSValue MapPrototypeGet(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.get");
        OrderedHashMap table = receiver.Table;
        int entry = table.FindEntryAndHash(args.AtOrUndefined(1), out _);
        return entry >= 0 ? table.ValueAtRaw(entry) : JSValue.Undefined;
    }

    /// <summary>Map.prototype.has ( key ).</summary>
    public static JSValue MapPrototypeHas(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.has");
        return JSValue.FromBoolean(TableHasKey(receiver.Table, args.AtOrUndefined(1)));
    }

    /// <summary>Map.prototype.set ( key, value ).</summary>
    public static JSValue MapPrototypeSet(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.set");
        MapTableSet(isolate, receiver, args.AtOrUndefined(1), args.AtOrUndefined(2));
        return receiver;
    }

    /// <summary>Map.prototype.getOrInsert ( key, value ).</summary>
    public static JSValue MapPrototypeGetOrInsert(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.getOrInsert");
        JSValue key = NormalizeNumberKey(args.AtOrUndefined(1));
        JSValue value = args.AtOrUndefined(2);
        OrderedHashMap table = receiver.Table;
        int entry = table.FindEntryAndHash(key, out int hash);
        if (entry >= 0) return table.ValueAtRaw(entry);
        if (hash < 0) hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (table.NeedsGrowForAdding) table = MapGrow(isolate, receiver);
        int newEntry = table.AddNewEntry(hash, key);
        table.SetValueAtRaw(newEntry, value);
        return value;
    }

    /// <summary>Map.prototype.getOrInsertComputed ( key, callbackfn ).</summary>
    public static JSValue MapPrototypeGetOrInsertComputed(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.getOrInsertComputed");
        JSValue key = args.AtOrUndefined(1);
        JSValue callback = args.AtOrUndefined(2);
        ThrowIfNotCallable(isolate, callback, "Map.prototype.getOrInsertComputed");

        OrderedHashMap table = receiver.Table;
        int entry = table.FindEntryAndHash(key, out _);
        if (entry >= 0) return table.ValueAtRaw(entry);

        key = NormalizeNumberKey(key);
        JSValue value = Execution.Call(isolate, callback, JSValue.Undefined, [key]);
        // NOTE: The Map may have been modified during execution of _callback_.
        MapTableSet(isolate, receiver, key, value);
        return value;
    }

    /// <summary>Map.prototype.delete ( key ).</summary>
    public static JSValue MapPrototypeDelete(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.delete");
        OrderedHashMap table = receiver.Table;
        int entry = table.FindEntryAndHash(args.AtOrUndefined(1), out _);
        if (entry < 0) return JSValue.False;

        // If we found the entry, mark the entry as deleted.
        int numberOfElements = table.DeleteEntry(entry);

        // If there fewer elements than #buckets / 2, shrink the table.
        if (numberOfElements + numberOfElements < table.NumberOfBuckets)
        {
            // Runtime_MapShrink.
            receiver.Table = OrderedHashMap.Shrink(isolate, receiver.Table);
        }
        return JSValue.True;
    }

    /// <summary>Map.prototype.clear ( ) (builtins-collections.cc).</summary>
    public static JSValue MapPrototypeClear(Isolate isolate, in BuiltinArguments args)
    {
        JSMap map = CheckMap(isolate, args.Receiver, "Map.prototype.clear");
        JSMap.Clear(isolate, map);
        return JSValue.Undefined;
    }

    /// <summary>AllocateJSCollectionIterator&lt;JSMapIterator&gt;.</summary>
    static JSMapIterator AllocateMapIterator(Isolate isolate, Map iteratorMap, JSMap collection)
    {
        var iterator = (JSMapIterator)isolate.Factory.NewJSObjectFromMap(iteratorMap);
        iterator.Table = collection.Table;
        iterator.Index = 0;
        return iterator;
    }

    /// <summary>Map.prototype.entries ( ).</summary>
    public static JSValue MapPrototypeEntries(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.entries");
        return AllocateMapIterator(isolate, isolate.NativeContext.MapKeyValueIteratorMap, receiver);
    }

    /// <summary>Map.prototype.keys ( ).</summary>
    public static JSValue MapPrototypeKeys(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.keys");
        return AllocateMapIterator(isolate, isolate.NativeContext.MapKeyIteratorMap, receiver);
    }

    /// <summary>Map.prototype.values ( ).</summary>
    public static JSValue MapPrototypeValues(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.values");
        return AllocateMapIterator(isolate, isolate.NativeContext.MapValueIteratorMap, receiver);
    }

    /// <summary>get Map.prototype.size.</summary>
    public static JSValue MapPrototypeGetSize(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "get Map.prototype.size");
        return JSValue.FromInt(receiver.Table.NumberOfElements);
    }

    /// <summary>Map.prototype.forEach ( callbackfn [ , thisArg ] ).</summary>
    public static JSValue MapPrototypeForEach(Isolate isolate, in BuiltinArguments args)
    {
        JSMap receiver = CheckMap(isolate, args.Receiver, "Map.prototype.forEach");
        JSValue callback = args.AtOrUndefined(1);
        JSValue thisArg = args.AtOrUndefined(2);

        // Ensure that {callback} is actually callable.
        if (!ObjectOps.IsCallable(callback))
        {
            return isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, callback));
        }

        int index = 0;
        OrderedHashTable table = receiver.Table;
        while (true)
        {
            // Transition {table} and {index} if there was any modification to
            // the {receiver} while we're iterating.
            table = OrderedHashTable.TransitionIterator(table, ref index);

            // Read the next entry from the {table}, skipping holes.
            int entry = NextSkipHashTableHoles(table, ref index);
            if (entry < 0) break;

            // Load the entry value as well.
            JSValue entryKey = table.KeyAtRaw(entry);
            JSValue entryValue = ((OrderedHashMap)table).ValueAtRaw(entry);

            // Invoke the {callback} passing the {entry_key}, {entry_value} and the
            // {receiver}.
            Execution.Call(isolate, callback, thisArg, [entryValue, entryKey, receiver]);
        }
        return JSValue.Undefined;
    }

    /// <summary>%MapIteratorPrototype%.next ( ).</summary>
    public static JSValue MapIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        // Ensure that {maybe_receiver} is actually a JSMapIterator.
        if (args.Receiver.HeapObjectOrNull is not JSMapIterator receiver)
        {
            return ThrowIncompatibleMethodReceiver(isolate, "Map Iterator.prototype.next", args.Receiver);
        }

        // Transition the {receiver} table if necessary.
        int index = receiver.Index;
        var table = (OrderedHashMap)OrderedHashTable.TransitionIterator(receiver.Table, ref index);
        receiver.Table = table;
        receiver.Index = index;

        // Read the next entry from the {table}, skipping holes.
        int entry = NextSkipHashTableHoles(table, ref index);
        if (entry < 0)
        {
            receiver.Table = ReadOnlyRoots.empty_ordered_hash_map;
            return IteratorBuiltins.CreateIterResultObject(isolate, JSValue.Undefined, true);
        }
        receiver.Index = index;
        JSValue entryKey = table.KeyAtRaw(entry);

        // Check how to return the {key} (depending on {receiver} type).
        switch (receiver.Map.InstanceType)
        {
            case InstanceType.JSMapKeyIteratorType:
                return IteratorBuiltins.CreateIterResultObject(isolate, entryKey, false);
            case InstanceType.JSMapValueIteratorType:
                return IteratorBuiltins.CreateIterResultObject(isolate, table.ValueAtRaw(entry), false);
            default:
                return AllocateJSIteratorResultForEntry(isolate, entryKey, table.ValueAtRaw(entry));
        }
    }

    /// <summary>AllocateJSIteratorResultForEntry: { value: [key, value], done: false }.</summary>
    static JSObject AllocateJSIteratorResultForEntry(Isolate isolate, JSValue key, JSValue value)
    {
        JSArray entry = isolate.Factory.NewJSArrayWithElements(new FixedArray([key, value]));
        return IteratorBuiltins.CreateIterResultObject(isolate, entry, false);
    }

    // ---- Set.prototype -------------------------------------------------------------------------

    /// <summary>Set.prototype.has ( value ).</summary>
    public static JSValue SetPrototypeHas(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.has");
        return JSValue.FromBoolean(TableHasKey(receiver.Table, args.AtOrUndefined(1)));
    }

    /// <summary>Set.prototype.add ( value ).</summary>
    public static JSValue SetPrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.add");
        SetTableAdd(isolate, receiver, args.AtOrUndefined(1));
        return receiver;
    }

    /// <summary>Set.prototype.delete ( value ).</summary>
    public static JSValue SetPrototypeDelete(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.delete");
        OrderedHashSet table = receiver.Table;
        int numberOfElements = DeleteFromSetTable(table, args.AtOrUndefined(1));
        if (numberOfElements < 0) return JSValue.False;

        // If there fewer elements than #buckets / 2, shrink the table.
        if (numberOfElements + numberOfElements < table.NumberOfBuckets)
        {
            // Runtime_SetShrink.
            receiver.Table = OrderedHashSet.Shrink(isolate, receiver.Table);
        }
        return JSValue.True;
    }

    /// <summary>Set.prototype.clear ( ) (builtins-collections.cc).</summary>
    public static JSValue SetPrototypeClear(Isolate isolate, in BuiltinArguments args)
    {
        JSSet set = CheckSet(isolate, args.Receiver, "Set.prototype.clear");
        JSSet.Clear(isolate, set);
        return JSValue.Undefined;
    }

    /// <summary>AllocateJSCollectionIterator&lt;JSSetIterator&gt;.</summary>
    static JSSetIterator AllocateSetIterator(Isolate isolate, Map iteratorMap, JSSet collection)
    {
        var iterator = (JSSetIterator)isolate.Factory.NewJSObjectFromMap(iteratorMap);
        iterator.Table = collection.Table;
        iterator.Index = 0;
        return iterator;
    }

    /// <summary>Set.prototype.entries ( ).</summary>
    public static JSValue SetPrototypeEntries(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.entries");
        return AllocateSetIterator(isolate, isolate.NativeContext.SetKeyValueIteratorMap, receiver);
    }

    /// <summary>Set.prototype.values ( ) (also keys and [Symbol.iterator]).</summary>
    public static JSValue SetPrototypeValues(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.values");
        return AllocateSetIterator(isolate, isolate.NativeContext.SetValueIteratorMap, receiver);
    }

    /// <summary>get Set.prototype.size.</summary>
    public static JSValue SetPrototypeGetSize(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "get Set.prototype.size");
        return JSValue.FromInt(receiver.Table.NumberOfElements);
    }

    /// <summary>Set.prototype.forEach ( callbackfn [ , thisArg ] ).</summary>
    public static JSValue SetPrototypeForEach(Isolate isolate, in BuiltinArguments args)
    {
        JSSet receiver = CheckSet(isolate, args.Receiver, "Set.prototype.forEach");
        JSValue callback = args.AtOrUndefined(1);
        JSValue thisArg = args.AtOrUndefined(2);

        // Ensure that {callback} is actually callable.
        if (!ObjectOps.IsCallable(callback))
        {
            return isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, callback));
        }

        int index = 0;
        OrderedHashTable table = receiver.Table;
        while (true)
        {
            // Transition {table} and {index} if there was any modification to
            // the {receiver} while we're iterating.
            table = OrderedHashTable.TransitionIterator(table, ref index);

            // Read the next entry from the {table}, skipping holes.
            int entry = NextSkipHashTableHoles(table, ref index);
            if (entry < 0) break;
            JSValue entryKey = table.KeyAtRaw(entry);

            // Invoke the {callback} passing the {entry_key} (twice) and the {receiver}.
            Execution.Call(isolate, callback, thisArg, [entryKey, entryKey, receiver]);
        }
        return JSValue.Undefined;
    }

    /// <summary>%SetIteratorPrototype%.next ( ).</summary>
    public static JSValue SetIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        // Ensure that {maybe_receiver} is actually a JSSetIterator.
        if (args.Receiver.HeapObjectOrNull is not JSSetIterator receiver)
        {
            return ThrowIncompatibleMethodReceiver(isolate, "Set Iterator.prototype.next", args.Receiver);
        }

        // Transition the {receiver} table if necessary.
        int index = receiver.Index;
        var table = (OrderedHashSet)OrderedHashTable.TransitionIterator(receiver.Table, ref index);
        receiver.Table = table;
        receiver.Index = index;

        // Read the next entry from the {table}, skipping holes.
        int entry = NextSkipHashTableHoles(table, ref index);
        if (entry < 0)
        {
            receiver.Table = ReadOnlyRoots.empty_ordered_hash_set;
            return IteratorBuiltins.CreateIterResultObject(isolate, JSValue.Undefined, true);
        }
        receiver.Index = index;
        JSValue entryKey = table.KeyAtRaw(entry);

        // Check how to return the {key} (depending on {receiver} type).
        if (receiver.Map.InstanceType == InstanceType.JSSetValueIteratorType)
        {
            return IteratorBuiltins.CreateIterResultObject(isolate, entryKey, false);
        }
        return AllocateJSIteratorResultForEntry(isolate, entryKey, entryKey);
    }

    // ---- Fast iteration helpers used by spread / IterableToList --------------------------------

    /// <summary>
    /// BranchIfIterableWithOriginalKeyOrValueMapIterator: a keys or values
    /// JSMapIterator that was not advanced, with the original prototypes and
    /// the map iterator protector intact.
    /// </summary>
    public static bool IsIterableWithOriginalKeyOrValueMapIterator(Isolate isolate, JSObject iterator)
    {
        if (iterator is not JSMapIterator mapIterator) return false;
        InstanceType type = mapIterator.Map.InstanceType;
        if (type is not (InstanceType.JSMapKeyIteratorType or InstanceType.JSMapValueIteratorType)) return false;
        // Check that the iterator is not partially consumed.
        if (mapIterator.Index != 0) return false;
        if (!Protectors.IsMapIteratorLookupChainIntact(isolate)) return false;
        // Check if the iterator object has the original %MapIteratorPrototype%.
        NativeContext nativeContext = isolate.NativeContext;
        JSReceiver? mapIterProto = mapIterator.Map.Prototype;
        if (!ReferenceEquals(mapIterProto, nativeContext.InitialMapIteratorPrototype)) return false;
        // Check if the original MapIterator prototype has the original
        // %IteratorPrototype%.
        return ReferenceEquals(mapIterProto!.Map.Prototype, nativeContext.InitialIteratorPrototype);
    }

    /// <summary>
    /// BranchIfIterableWithOriginalValueSetIterator: a JSSet with the initial
    /// prototype, or a values JSSetIterator that was not advanced, with the set
    /// iterator protector intact.
    /// </summary>
    public static bool IsIterableWithOriginalValueSetIterator(Isolate isolate, JSObject iterable)
    {
        NativeContext nativeContext = isolate.NativeContext;
        switch (iterable)
        {
            case JSSet set:
                // Check if the set object has the original Set prototype.
                if (!ReferenceEquals(set.Map.Prototype, nativeContext.InitialSetPrototype)) return false;
                break;
            case JSSetIterator setIterator when setIterator.Map.InstanceType == InstanceType.JSSetValueIteratorType:
            {
                // Check that the iterator is not partially consumed.
                if (setIterator.Index != 0) return false;
                // Check if the iterator object has the original SetIterator prototype.
                JSReceiver? setIterProto = setIterator.Map.Prototype;
                if (!ReferenceEquals(setIterProto, nativeContext.InitialSetIteratorPrototype)) return false;
                // Check if the original SetIterator prototype has the original
                // %IteratorPrototype%.
                if (!ReferenceEquals(setIterProto!.Map.Prototype, nativeContext.InitialIteratorPrototype)) return false;
                break;
            }
            default:
                return false;
        }
        return Protectors.IsSetIteratorLookupChainIntact(isolate);
    }

    /// <summary>
    /// SetOrSetIteratorToSet: the table of a Set or SetIterator; an iterator
    /// is marked exhausted.
    /// </summary>
    public static OrderedHashSet SetOrSetIteratorToSet(JSObject iterable)
    {
        if (iterable is JSSet set) return set.Table;

        var iterator = (JSSetIterator)iterable;
        // Transition the {iterable} table if necessary.
        int index = iterator.Index;
        var table = (OrderedHashSet)OrderedHashTable.TransitionIterator(iterator.Table, ref index);
        Debug.Assert(index == 0);
        // Set the {iterable} to exhausted if it's an iterator.
        iterator.Table = ReadOnlyRoots.empty_ordered_hash_set;
        iterator.Index = table.NumberOfElements;
        return table;
    }

    /// <summary>MapIteratorToList: the keys or values of an unconsumed map iterator; exhausts it.</summary>
    public static JSArray MapIteratorToList(Isolate isolate, JSMapIterator iterator)
    {
        // Transition the {iterator} table if necessary.
        int index = iterator.Index;
        var table = (OrderedHashMap)OrderedHashTable.TransitionIterator(iterator.Table, ref index);
        Debug.Assert(index == 0);

        int size = table.NumberOfElements;
        var elements = new FixedArray(size);
        bool keys = iterator.Map.InstanceType == InstanceType.JSMapKeyIteratorType;
        int k = 0;
        while (true)
        {
            // Read the next entry from the {table}, skipping holes.
            int entry = NextSkipHashTableHoles(table, ref index);
            if (entry < 0) break;
            elements[k++] = keys ? table.KeyAtRaw(entry) : table.ValueAtRaw(entry);
        }

        // Set the {iterator} to exhausted.
        iterator.Table = ReadOnlyRoots.empty_ordered_hash_map;
        iterator.Index = index;
        return isolate.Factory.NewJSArrayWithElements(elements);
    }

    /// <summary>SetOrSetIteratorToList: the values of a Set (or an unconsumed set iterator, which is exhausted).</summary>
    public static JSArray SetOrSetIteratorToList(Isolate isolate, JSObject iterable)
    {
        OrderedHashSet table = SetOrSetIteratorToSet(iterable);
        int size = table.NumberOfElements;
        var elements = new FixedArray(size);
        int index = 0;
        int k = 0;
        while (true)
        {
            int entry = NextSkipHashTableHoles(table, ref index);
            if (entry < 0) break;
            elements[k++] = table.KeyAtRaw(entry);
        }
        return isolate.Factory.NewJSArrayWithElements(elements);
    }

    // ---- Map.groupBy (map-groupby.tq, object-groupby.tq GroupByImpl) -----------------------------

    /// <summary>Map.groupBy ( items, callbackfn ).</summary>
    public static JSValue MapGroupBy(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let groups be ? GroupBy(items, callbackfn, zero).
        OrderedHashMap groups = GroupByImpl(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), "Map.groupBy");

        // 2. Let map be ! Construct(%Map%).
        // 3. For each Record { [[Key]], [[Elements]] } g of groups, do
        //   a. Let elements be CreateArrayFromList(g.[[Elements]]).
        //   b. Let entry be the Record { [[Key]]: g.[[Key]], [[Value]]: elements }.
        //   c. Append entry to map.[[MapData]].
        int usedCapacity = groups.UsedCapacity;
        for (int entry = 0; entry < usedCapacity; entry++)
        {
            if (groups.IsDeletedEntry(entry)) continue;
            var list = (ArrayList)groups.ValueAtRaw(entry).Object;
            var elements = new FixedArray(list.Length);
            for (int i = 0; i < list.Length; i++) elements[i] = list.Get(i);
            groups.SetValueAtRaw(entry, isolate.Factory.NewJSArrayWithElements(elements));
        }

        // 4. Return map.
        var map = (JSMap)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.JSMapMap);
        map.Table = groups;
        return map;
    }

    /// <summary>GroupByImpl with coercion zero (the Map.groupBy variant).</summary>
    static OrderedHashMap GroupByImpl(Isolate isolate, JSValue items, JSValue callback, string methodName)
    {
        // 1. Perform ? RequireObjectCoercible(items).
        if (items.IsNullOrUndefined)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }

        // 2. If IsCallable(callbackfn) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(callback)) isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, callback);

        // 3. Let groups be a new empty List.
        OrderedHashMap groups = isolate.Factory.NewOrderedHashMap();

        if (items.HeapObjectOrNull is JSArray array && IteratorBuiltins.IsFastJSArrayWithNoCustomIteration(isolate, array))
        {
            // Per spec, the iterator and its next method are cached up front. This
            // means that we only need to check for no custom iteration once up
            // front. The grouping callback can mutate the array; the array
            // iterator reads each element with [[Get]] against the current length.
            for (uint k = 0; k < (uint)array.Length.Number; k++)
            {
                JSValue value = JSReceiver.GetElement(isolate, array, k);
                JSValue key = Execution.Call(isolate, callback, JSValue.Undefined, [value, JSValue.FromNumber(k)]);
                key = NormalizeNumberKey(key);
                groups = AddValueToKeyedGroup(isolate, groups, key, value, methodName);
            }
            return groups;
        }

        // GroupByGeneric.
        // 4. Let iteratorRecord be ? GetIterator(items, sync).
        Map fastIteratorResultMap = isolate.NativeContext.IteratorResultMap;
        IteratorRecord iteratorRecord = IteratorBuiltins.GetIterator(isolate, items);

        // 5. Let k be 0.
        double k2 = 0;

        // 6. Repeat,
        while (true)
        {
            // b. Let next be ? IteratorStep(iteratorRecord).
            // c. If next is false, then
            //   i. Return groups.
            if (!IteratorBuiltins.IteratorStep(isolate, iteratorRecord, out JSReceiver next, fastIteratorResultMap)) return groups;

            // d. Let value be ? IteratorValue(next).
            JSValue value = IteratorBuiltins.IteratorValue(isolate, next, fastIteratorResultMap);

            // e. Let key be Completion(Call(callbackfn, undefined, « value, 𝔽(k) »)).
            JSValue key;
            try
            {
                key = Execution.Call(isolate, callback, JSValue.Undefined, [value, JSValue.FromNumber(k2)]);
                key = NormalizeNumberKey(key);
            }
            catch (JavaScriptException)
            {
                // f. and g.ii.
                // IfAbruptCloseIterator(key, iteratorRecord).
                IteratorBuiltins.IteratorCloseOnException(isolate, iteratorRecord.Object);
                throw;
            }

            // i. Perform AddValueToKeyedGroup(groups, key, value).
            groups = AddValueToKeyedGroup(isolate, groups, key, value, methodName);

            // j. Set k to k + 1.
            k2 += 1;
        }
    }

    /// <summary>AddValueToKeyedGroup: appends to the group's ArrayList, adding the group if needed.</summary>
    static OrderedHashMap AddValueToKeyedGroup(Isolate isolate, OrderedHashMap groups, JSValue key, JSValue value, string methodName)
    {
        int entry = groups.FindEntryAndHash(key, out int hash);
        if (entry >= 0)
        {
            ((ArrayList)groups.ValueAtRaw(entry).Object).Add(value);
            return groups;
        }
        if (hash < 0) hash = (int)ObjectOps.GetOrCreateHashRaw(key);
        if (groups.NeedsGrowForAdding)
        {
            // Runtime_OrderedHashMapGrow. The groups OrderedHashMap is not escaped to
            // user script while grouping items, so there can't be live iterators.
            try
            {
                groups = OrderedHashMap.EnsureCapacityForAdding(isolate, groups);
            }
            catch (JavaScriptException)
            {
                isolate.ThrowRangeError(MessageTemplate.OutOfMemory, isolate.Factory.NewStringFromAsciiChecked(methodName));
                throw;
            }
        }
        var list = new ArrayList();
        list.Add(value);
        int newEntry = groups.AddNewEntry(hash, key);
        groups.SetValueAtRaw(newEntry, list);
        return groups;
    }

    // ---- WeakMap / WeakSet ------------------------------------------------------------------------

    /// <summary>WeakCollectionSet (+ the CanBeHeldWeakly check of its callers).</summary>
    static void WeakCollectionSet(Isolate isolate, JSWeakCollection collection, JSValue key, JSValue value, MessageTemplate invalidKey)
    {
        if (!CanBeHeldWeakly(key)) isolate.ThrowTypeError(invalidKey, key);
        JSWeakCollection.Set(collection, key.Object, value);
    }

    static JSWeakMap CheckWeakMap(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSWeakMap map) return map;
        ThrowIncompatibleMethodReceiver(isolate, methodName, receiver);
        return null!;
    }

    static JSWeakSet CheckWeakSet(Isolate isolate, JSValue receiver, string methodName)
    {
        if (receiver.HeapObjectOrNull is JSWeakSet set) return set;
        ThrowIncompatibleMethodReceiver(isolate, methodName, receiver);
        return null!;
    }

    /// <summary>WeakMap.prototype.get ( key ).</summary>
    public static JSValue WeakMapPrototypeGet(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.get");
        JSValue key = args.AtOrUndefined(1);
        if (!CanBeHeldWeakly(key)) return JSValue.Undefined;
        return JSWeakCollection.Get(receiver, key);
    }

    /// <summary>WeakMap.prototype.has ( key ).</summary>
    public static JSValue WeakMapPrototypeHas(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.has");
        JSValue key = args.AtOrUndefined(1);
        return JSValue.FromBoolean(CanBeHeldWeakly(key) && JSWeakCollection.Has(receiver, key));
    }

    /// <summary>WeakMap.prototype.set ( key, value ).</summary>
    public static JSValue WeakMapPrototypeSet(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.set");
        WeakCollectionSet(isolate, receiver, args.AtOrUndefined(1), args.AtOrUndefined(2), MessageTemplate.InvalidWeakMapKey);
        return receiver;
    }

    /// <summary>WeakMap.prototype.delete ( key ).</summary>
    public static JSValue WeakMapPrototypeDelete(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.delete");
        JSValue key = args.AtOrUndefined(1);
        return JSValue.FromBoolean(CanBeHeldWeakly(key) && JSWeakCollection.Delete(receiver, key));
    }

    /// <summary>WeakMap.prototype.getOrInsert ( key, value ).</summary>
    public static JSValue WeakMapPrototypeGetOrInsert(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.getOrInsert");
        JSValue key = args.AtOrUndefined(1);
        JSValue value = args.AtOrUndefined(2);
        if (!CanBeHeldWeakly(key)) return isolate.ThrowTypeError(MessageTemplate.InvalidWeakMapKey, key);
        if (JSWeakCollection.Lookup(receiver, key) is { } box) return box.Value;
        JSWeakCollection.Set(receiver, key.Object, value);
        return value;
    }

    /// <summary>WeakMap.prototype.getOrInsertComputed ( key, callbackfn ).</summary>
    public static JSValue WeakMapPrototypeGetOrInsertComputed(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakMap receiver = CheckWeakMap(isolate, args.Receiver, "WeakMap.prototype.getOrInsertComputed");
        JSValue key = args.AtOrUndefined(1);
        JSValue callback = args.AtOrUndefined(2);
        ThrowIfNotCallable(isolate, callback, "WeakMap.prototype.getOrInsertComputed");
        if (!CanBeHeldWeakly(key)) return isolate.ThrowTypeError(MessageTemplate.InvalidWeakMapKey, key);
        if (JSWeakCollection.Lookup(receiver, key) is { } box) return box.Value;
        JSValue value = Execution.Call(isolate, callback, JSValue.Undefined, [key]);
        // NOTE: The WeakMap may have been modified during execution of _callback_.
        JSWeakCollection.Set(receiver, key.Object, value);
        return value;
    }

    /// <summary>WeakSet.prototype.add ( value ).</summary>
    public static JSValue WeakSetPrototypeAdd(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakSet receiver = CheckWeakSet(isolate, args.Receiver, "WeakSet.prototype.add");
        WeakCollectionSet(isolate, receiver, args.AtOrUndefined(1), JSValue.True, MessageTemplate.InvalidWeakSetValue);
        return receiver;
    }

    /// <summary>WeakSet.prototype.has ( value ).</summary>
    public static JSValue WeakSetPrototypeHas(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakSet receiver = CheckWeakSet(isolate, args.Receiver, "WeakSet.prototype.has");
        JSValue key = args.AtOrUndefined(1);
        return JSValue.FromBoolean(CanBeHeldWeakly(key) && JSWeakCollection.Has(receiver, key));
    }

    /// <summary>WeakSet.prototype.delete ( value ).</summary>
    public static JSValue WeakSetPrototypeDelete(Isolate isolate, in BuiltinArguments args)
    {
        JSWeakSet receiver = CheckWeakSet(isolate, args.Receiver, "WeakSet.prototype.delete");
        JSValue key = args.AtOrUndefined(1);
        return JSValue.FromBoolean(CanBeHeldWeakly(key) && JSWeakCollection.Delete(receiver, key));
    }
}
