// Port of src/objects/keys.{h,cc}: KeyAccumulator (collects own and inherited
// property keys in spec order: integer indices ascending, then strings in
// creation order, then symbols in creation order) and FastKeyAccumulator (the
// enum-cache fast paths for for-in and Object.keys).
//
// V8 accumulates into an OrderedHashSet; V8Sharp uses an insertion-ordered
// list with a hash set for the duplicate check (same order and semantics).
// Interceptor and access-check keys are not ported (no embedder API).
using System.Runtime.CompilerServices;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's KeyCollectionMode.</summary>
public enum KeyCollectionMode { OwnOnly, IncludePrototypes }

/// <summary>V8's GetKeysConversion.</summary>
public enum GetKeysConversion { KeepNumbers, ConvertToString, NoNumbers }

/// <summary>Key equality for the accumulator: numbers by value, strings by content, everything else by identity.</summary>
sealed class KeyComparer : IEqualityComparer<JSValue>
{
    public static readonly KeyComparer Instance = new();

    public bool Equals(JSValue x, JSValue y)
    {
        if (x.IsNumber) return y.IsNumber && x.Number == y.Number;
        if (x.HeapObjectOrNull is JSString xs) return y.HeapObjectOrNull is JSString ys && JSString.Equals(xs, ys);
        return x.IsIdenticalTo(y);
    }

    public int GetHashCode(JSValue v)
    {
        if (v.IsNumber) return v.Number.GetHashCode();
        if (v.HeapObjectOrNull is Name n) return (int)n.EnsureHash();
        return v.HeapObjectOrNull is null ? 0 : RuntimeHelpers.GetHashCode(v.Object);
    }
}

/// <summary>V8's KeyAccumulator.</summary>
public sealed class KeyAccumulator(Isolate isolate, KeyCollectionMode mode, PropertyFilter filter)
{
    readonly Isolate _isolate = isolate;
    readonly KeyCollectionMode _mode = mode;
    readonly PropertyFilter _filter = filter;
    List<JSValue>? _keys;
    HashSet<JSValue>? _keySet;
    HashSet<JSValue>? _shadowingKeys;
    JSReceiver? _lastNonEmptyPrototype;
    bool _isForIn;
    bool _skipIndices;
    // For all the keys on the first receiver adding a shadowing key we can skip
    // the shadow check.
    bool _skipShadowCheck = true;
    bool _mayHaveElements = true;

    public Isolate Isolate => _isolate;
    public PropertyFilter Filter => _filter;
    public KeyCollectionMode Mode => _mode;

    public void SetIsForIn(bool value) => _isForIn = value;
    public void SetSkipIndices(bool value) => _skipIndices = value;
    public void SetLastNonEmptyPrototype(JSReceiver? obj) => _lastNonEmptyPrototype = obj;
    public void SetMayHaveElements(bool value) => _mayHaveElements = value;

    /// <summary>KeyAccumulator::GetKeys(isolate, object, mode, filter, conversion, is_for_in, skip_indices).</summary>
    public static FixedArray GetKeys(Isolate isolate, JSReceiver obj, KeyCollectionMode mode, PropertyFilter filter,
        GetKeysConversion keysConversion = GetKeysConversion.KeepNumbers, bool isForIn = false, bool skipIndices = false)
    {
        var accumulator = new FastKeyAccumulator(isolate, obj, mode, filter, isForIn, skipIndices);
        return accumulator.GetKeys(keysConversion);
    }

    /// <summary>KeyAccumulator::GetKeys(convert): the collected keys as a FixedArray.</summary>
    public FixedArray GetKeys(GetKeysConversion convert = GetKeysConversion.KeepNumbers)
    {
        if (_keys is null) return FixedArray.Empty;
        int length = _keys.Count;
        var result = new FixedArray(length);
        for (int i = 0; i < length; i++)
        {
            JSValue key = _keys[i];
            if (convert == GetKeysConversion.ConvertToString)
            {
                if (ObjectOps.ToArrayIndex(key, out uint indexValue))
                {
                    key = _isolate.Factory.SizeToString(indexValue);
                }
            }
            result.Set(i, key);
        }
        return result;
    }

    /// <summary>KeyAccumulator::AddKey.</summary>
    public void AddKey(JSValue key, AddKeyConversion convert = AddKeyConversion.DO_NOT_CONVERT)
    {
        if (_filter == PropertyFilter.PRIVATE_NAMES_ONLY)
        {
            if (key.HeapObjectOrNull is not Symbol s) return;
            if (!s.IsAnyPrivateName) return;
        }
        else if (key.HeapObjectOrNull is Symbol symbol)
        {
            if ((_filter & PropertyFilter.SKIP_SYMBOLS) != 0) return;
            if (symbol.IsAnyPrivate) return;
        }
        else if ((_filter & PropertyFilter.SKIP_STRINGS) != 0)
        {
            return;
        }

        if (IsShadowed(key)) return;
        if (_keys is null)
        {
            _keys = new List<JSValue>(16);
            _keySet = new HashSet<JSValue>(16, KeyComparer.Instance);
        }
        if (convert == AddKeyConversion.CONVERT_TO_ARRAY_INDEX && key.HeapObjectOrNull is JSString str &&
            str.AsArrayIndex(out uint index))
        {
            key = JSValue.FromNumber(index);
        }
        if (_keySet!.Add(key))
        {
            if (_keys.Count >= FixedArrayBase.kMaxLength)
            {
                _isolate.Throw(_isolate.Factory.NewRangeError(MessageTemplate.TooManyProperties));
            }
            _keys.Add(key);
        }
    }

    /// <summary>KeyAccumulator::AddKeys(FixedArray).</summary>
    public void AddKeys(FixedArray array, AddKeyConversion convert)
    {
        int addLength = array.Length;
        for (int i = 0; i < addLength; i++) AddKey(array.Get(i), convert);
    }

    /// <summary>KeyAccumulator::AddKeys(JSObject array_like).</summary>
    public void AddKeys(JSObject arrayLike, AddKeyConversion convert)
    {
        Debug.Assert(arrayLike is JSArray || arrayLike.HasSloppyArgumentsElements);
        ElementsAccessor accessor = arrayLike.GetElementsAccessor();
        accessor.AddElementsToKeyAccumulator(arrayLike, this, convert);
    }

    static FixedArray FilterProxyKeys(KeyAccumulator accumulator, JSProxy owner, FixedArray keys, PropertyFilter filter,
        bool skipIndices)
    {
        if (filter == PropertyFilter.ALL_PROPERTIES)
        {
            // Nothing to do.
            return keys;
        }
        Isolate isolate = accumulator.Isolate;
        int storePosition = 0;
        int keysLength = keys.Length;
        for (int i = 0; i < keysLength; ++i)
        {
            var key = (Name)keys.Get(i).Object;
            if (ObjectOps.FilterKey(key, filter)) continue;  // Skip this key.
            if (skipIndices)
            {
                if (key.AsArrayIndex(out _)) continue;  // Skip this key.
            }
            if ((filter & PropertyFilter.ONLY_ENUMERABLE) != 0)
            {
                var desc = new PropertyDescriptor();
                bool found = JSProxy.GetOwnPropertyDescriptor(isolate, owner, key, ref desc);
                if (!found) continue;
                if (!desc.Enumerable)
                {
                    accumulator.AddShadowingKey(key);
                    continue;
                }
            }
            // Keep this key.
            if (storePosition != i) keys.Set(storePosition, key);
            storePosition++;
        }
        return JSReceiver.RightTrimOrEmpty(keys, storePosition);
    }

    /// <summary>KeyAccumulator::AddKeysFromJSProxy.</summary>
    void AddKeysFromJSProxy(JSProxy proxy, FixedArray keys)
    {
        // Postpone the enumerable check for for-in to the ForInFilter step.
        if (!_isForIn) keys = FilterProxyKeys(this, proxy, keys, _filter, _skipIndices);
        // https://tc39.es/ecma262/#sec-proxy-object-internal-methods-and-internal-slots-ownpropertykeys
        // As of 10.5.11.9 says, the keys collected from Proxy should not contain
        // any duplicates. And the order of the keys is preserved by the
        // OrderedHashTable.
        AddKeys(keys, AddKeyConversion.CONVERT_TO_ARRAY_INDEX);
    }

    /// <summary>KeyAccumulator::CollectKeys.</summary>
    public void CollectKeys(JSReceiver receiver, JSReceiver obj)
    {
        // Proxies have no hidden prototype and we should not trigger the
        // [[GetPrototypeOf]] trap on the last iteration when using
        // AdvanceFollowingProxies.
        if (_mode == KeyCollectionMode.OwnOnly && obj is JSProxy ownProxy)
        {
            CollectOwnJSProxyKeys(receiver, ownProxy);
            return;
        }

        PrototypeIterator.WhereToEnd end = _mode == KeyCollectionMode.OwnOnly
            ? PrototypeIterator.WhereToEnd.END_AT_NON_HIDDEN
            : PrototypeIterator.WhereToEnd.END_AT_NULL;
        for (var iter = new PrototypeIterator(_isolate, obj, WhereToStart.StartAtReceiver, end); !iter.IsAtEnd;)
        {
            // Start the shadow checks only after the first prototype has added
            // shadowing keys.
            if (HasShadowingKeys()) _skipShadowCheck = false;
            JSReceiver current = iter.GetCurrent()!;
            bool result;
            if (current is JSProxy proxy)
            {
                result = CollectOwnJSProxyKeys(receiver, proxy);
            }
            else
            {
                result = CollectOwnKeys((JSObject)current);
            }
            if (!result) break;  // |false| means "stop iterating".
            // Iterate through proxies but ignore access checks case on API objects for
            // OWN_ONLY keys handled in CollectOwnKeys.
            iter.AdvanceFollowingProxiesIgnoringAccessChecks();
            if (_lastNonEmptyPrototype is not null && ReferenceEquals(_lastNonEmptyPrototype, current)) break;
        }
    }

    bool HasShadowingKeys() => _shadowingKeys is not null;

    bool IsShadowed(JSValue key)
    {
        if (!HasShadowingKeys() || _skipShadowCheck) return false;
        return _shadowingKeys!.Contains(key);
    }

    /// <summary>KeyAccumulator::AddShadowingKey: a non-enumerable key that hides the same key further up the chain.</summary>
    public void AddShadowingKey(JSValue key)
    {
        if (_mode == KeyCollectionMode.OwnOnly) return;
        (_shadowingKeys ??= new HashSet<JSValue>(16, KeyComparer.Instance)).Add(key);
    }

    /// <summary>KeyAccumulator::CollectOwnElementIndices.</summary>
    void CollectOwnElementIndices(JSObject obj)
    {
        if ((_filter & PropertyFilter.SKIP_STRINGS) != 0 || _skipIndices) return;

        ElementsAccessor accessor = obj.GetElementsAccessor();
        accessor.CollectElementIndices(obj, this);
    }

    /// <summary>CollectOwnPropertyNamesInternal&lt;skip_symbols&gt;: returns the first skipped index, or -1.</summary>
    int CollectOwnPropertyNamesInternal(bool skipSymbols, DescriptorArray descs, int startIndex, int limit)
    {
        int firstSkipped = -1;
        PropertyFilter filter = _filter;
        KeyCollectionMode mode = _mode;
        for (int i = startIndex; i < limit; i++)
        {
            var idx = new InternalIndex(i);
            bool isShadowingKey = false;
            PropertyDetails details = descs.GetDetails(idx);

            if (((int)details.Attributes & (int)filter) != 0)
            {
                if (mode == KeyCollectionMode.IncludePrototypes)
                {
                    isShadowingKey = true;
                }
                else
                {
                    continue;
                }
            }

            Name key = descs.GetKey(idx);
            if (skipSymbols == key is Symbol)
            {
                if (firstSkipped == -1) firstSkipped = i;
                continue;
            }
            if (ObjectOps.FilterKey(key, filter)) continue;

            if (isShadowingKey)
            {
                AddShadowingKey(key);
                continue;
            }
            AddKey(key, AddKeyConversion.DO_NOT_CONVERT);
        }
        return firstSkipped;
    }

    // Collect the keys from |dictionary| into |keys|, in ascending chronological
    // order of property creation.
    void CollectKeysFromDictionary(NameDictionary dictionary)
    {
        PropertyFilter filter = _filter;
        // Handle enumerable strings in CopyEnumKeysTo.
        Debug.Assert(filter != PropertyFilter.ENUMERABLE_STRINGS);
        int[] order = NameDictionary.IterationIndices(dictionary);
        var array = new int[order.Length];
        int arraySize = 0;
        foreach (int i in order)
        {
            var entry = new InternalIndex(i);
            JSValue key = dictionary.KeyAt(entry);
            if (ObjectOps.FilterKey(key, filter)) continue;
            PropertyDetails details = dictionary.DetailsAt(entry);
            if (((int)details.Attributes & (int)filter) != 0)
            {
                AddShadowingKey(key);
                continue;
            }
            array[arraySize++] = i;
        }

        bool hasSeenSymbol = false;
        for (int i = 0; i < arraySize; i++)
        {
            Name key = dictionary.NameAt(new InternalIndex(array[i]));
            if (key is Symbol)
            {
                hasSeenSymbol = true;
                continue;
            }
            AddKey(key, AddKeyConversion.DO_NOT_CONVERT);
        }
        if (hasSeenSymbol)
        {
            for (int i = 0; i < arraySize; i++)
            {
                Name key = dictionary.NameAt(new InternalIndex(array[i]));
                if (key is not Symbol) continue;
                AddKey(key, AddKeyConversion.DO_NOT_CONVERT);
            }
        }
    }

    void CollectKeysFromDictionary(GlobalDictionary dictionary)
    {
        PropertyFilter filter = _filter;
        int[] order = GlobalDictionary.IterationIndices(dictionary);
        var array = new int[order.Length];
        int arraySize = 0;
        foreach (int i in order)
        {
            var entry = new InternalIndex(i);
            Name key = dictionary.NameAt(entry);
            if (ObjectOps.FilterKey(key, filter)) continue;
            PropertyDetails details = dictionary.DetailsAt(entry);
            if (((int)details.Attributes & (int)filter) != 0)
            {
                AddShadowingKey(key);
                continue;
            }
            array[arraySize++] = i;
        }

        bool hasSeenSymbol = false;
        for (int i = 0; i < arraySize; i++)
        {
            Name key = dictionary.NameAt(new InternalIndex(array[i]));
            if (key is Symbol)
            {
                hasSeenSymbol = true;
                continue;
            }
            AddKey(key, AddKeyConversion.DO_NOT_CONVERT);
        }
        if (hasSeenSymbol)
        {
            for (int i = 0; i < arraySize; i++)
            {
                Name key = dictionary.NameAt(new InternalIndex(array[i]));
                if (key is not Symbol) continue;
                AddKey(key, AddKeyConversion.DO_NOT_CONVERT);
            }
        }
    }

    /// <summary>KeyAccumulator::CollectOwnPropertyNames.</summary>
    void CollectOwnPropertyNames(JSObject obj)
    {
        if (_filter == PropertyFilter.ENUMERABLE_STRINGS)
        {
            FixedArray enumKeys;
            if (obj.HasFastProperties)
            {
                enumKeys = GetOwnEnumPropertyKeys(_isolate, obj);
                // If the number of properties equals the length of enumerable properties
                // we do not have to filter out non-enumerable ones
                Map map = obj.Map;
                int nofDescriptors = map.NumberOfOwnDescriptors;
                if (enumKeys.Length != nofDescriptors)
                {
                    if (map.Prototype is not null)
                    {
                        DescriptorArray descs = map.InstanceDescriptors;
                        for (int i = 0; i < nofDescriptors; i++)
                        {
                            PropertyDetails details = descs.GetDetails(new InternalIndex(i));
                            if (!details.IsDontEnum) continue;
                            AddShadowingKey(descs.GetKey(new InternalIndex(i)));
                        }
                    }
                }
            }
            else if (obj is JSGlobalObject global)
            {
                enumKeys = GetOwnEnumPropertyDictionaryKeys(_isolate, _mode, this, global.GlobalDictionary);
            }
            else
            {
                enumKeys = GetOwnEnumPropertyDictionaryKeys(_isolate, _mode, this, obj.PropertyDictionary);
            }
            if (obj is JSModuleNamespace ns)
            {
                // Simulate [[GetOwnProperty]] for establishing enumerability, which
                // throws for uninitialized exports.
                for (int i = 0; i < enumKeys.Length; ++i)
                {
                    ns.GetExport(_isolate, (JSString)enumKeys.Get(i).Object);
                }
            }
            AddKeys(enumKeys, AddKeyConversion.DO_NOT_CONVERT);
        }
        else
        {
            if (obj.HasFastProperties)
            {
                int limit = obj.Map.NumberOfOwnDescriptors;
                DescriptorArray descs = obj.Map.InstanceDescriptors;
                // First collect the strings,
                int firstSymbol = CollectOwnPropertyNamesInternal(true, descs, 0, limit);
                // then the symbols.
                if (firstSymbol != -1) CollectOwnPropertyNamesInternal(false, descs, firstSymbol, limit);
            }
            else if (obj is JSGlobalObject global)
            {
                CollectKeysFromDictionary(global.GlobalDictionary);
            }
            else
            {
                CollectKeysFromDictionary(obj.PropertyDictionary);
            }
        }
    }

    /// <summary>KeyAccumulator::CollectPrivateNames.</summary>
    void CollectPrivateNames(JSObject obj)
    {
        Debug.Assert(_mode == KeyCollectionMode.OwnOnly);
        if (obj.HasFastProperties)
        {
            int limit = obj.Map.NumberOfOwnDescriptors;
            CollectOwnPropertyNamesInternal(false, obj.Map.InstanceDescriptors, 0, limit);
        }
        else if (obj is JSGlobalObject global)
        {
            CollectKeysFromDictionary(global.GlobalDictionary);
        }
        else
        {
            CollectKeysFromDictionary(obj.PropertyDictionary);
        }
    }

    /// <summary>KeyAccumulator::CollectOwnKeys: true to continue with the prototype, false to stop.</summary>
    bool CollectOwnKeys(JSObject obj)
    {
        // Check access rights if required.
        if (obj.Map.IsAccessCheckNeeded && _isolate.Context is not null && !_isolate.MayAccess(_isolate.NativeContext, obj))
        {
            // The cross-origin spec says that [[Enumerate]] shall return an empty
            // iterator when it doesn't have access, whereas [[OwnPropertyKeys]]
            // shall return allowlisted properties (access-check interceptors,
            // which V8Sharp has none of).
            return false;
        }
        if ((_filter & PropertyFilter.PRIVATE_NAMES_ONLY) != 0)
        {
            CollectPrivateNames(obj);
            return true;
        }

        if (obj is JSDeferredModuleNamespace ns && ns.Module.ModuleStatus != Module.Status.kEvaluated)
        {
            JSDeferredModuleNamespace.EvaluateModuleSync(_isolate, ns);
        }

        if (_mayHaveElements) CollectOwnElementIndices(obj);
        CollectOwnPropertyNames(obj);
        return true;
    }

    /// <summary>KeyAccumulator::GetOwnEnumPropertyKeys.</summary>
    public static FixedArray GetOwnEnumPropertyKeys(Isolate isolate, JSObject obj)
    {
        if (obj.HasFastProperties) return FastKeyAccumulator.GetFastEnumPropertyKeys(isolate, obj);
        if (obj is JSGlobalObject global)
        {
            return GetOwnEnumPropertyDictionaryKeys(isolate, KeyCollectionMode.OwnOnly, null, global.GlobalDictionary);
        }
        return GetOwnEnumPropertyDictionaryKeys(isolate, KeyCollectionMode.OwnOnly, null, obj.PropertyDictionary);
    }

    /// <summary>GetOwnEnumPropertyDictionaryKeys + CopyEnumKeysTo (NameDictionary).</summary>
    static FixedArray GetOwnEnumPropertyDictionaryKeys(Isolate isolate, KeyCollectionMode mode, KeyAccumulator? accumulator,
        NameDictionary dictionary)
    {
        if (dictionary.NumberOfElements == 0) return FixedArray.Empty;
        int length = dictionary.NumberOfEnumerableProperties();
        var storage = new FixedArray(length);
        int properties = 0;
        // Iterate in enumeration order (V8 collects entry indices and sorts them
        // with EnumIndexComparator).
        foreach (int i in NameDictionary.IterationIndices(dictionary))
        {
            var entry = new InternalIndex(i);
            JSValue key = dictionary.KeyAt(entry);
            bool isShadowingKey = false;
            if (key.IsSymbol) continue;
            PropertyDetails details = dictionary.DetailsAt(entry);
            if (details.IsDontEnum)
            {
                if (mode == KeyCollectionMode.IncludePrototypes)
                {
                    isShadowingKey = true;
                }
                else
                {
                    continue;
                }
            }
            if (isShadowingKey)
            {
                accumulator!.AddShadowingKey(key);
                continue;
            }
            storage.Set(properties, key);
            properties++;
            if (mode == KeyCollectionMode.OwnOnly && properties == length) break;
        }
        Debug.Assert(properties == length);
        return storage;
    }

    /// <summary>GetOwnEnumPropertyDictionaryKeys + CopyEnumKeysTo (GlobalDictionary).</summary>
    static FixedArray GetOwnEnumPropertyDictionaryKeys(Isolate isolate, KeyCollectionMode mode, KeyAccumulator? accumulator,
        GlobalDictionary dictionary)
    {
        if (dictionary.NumberOfElements == 0) return FixedArray.Empty;
        int length = dictionary.NumberOfEnumerableProperties();
        var storage = new FixedArray(length);
        int properties = 0;
        foreach (int i in GlobalDictionary.IterationIndices(dictionary))
        {
            var entry = new InternalIndex(i);
            Name key = dictionary.NameAt(entry);
            bool isShadowingKey = false;
            if (key is Symbol) continue;
            PropertyDetails details = dictionary.DetailsAt(entry);
            if (details.IsDontEnum)
            {
                if (mode == KeyCollectionMode.IncludePrototypes)
                {
                    isShadowingKey = true;
                }
                else
                {
                    continue;
                }
            }
            if (isShadowingKey)
            {
                accumulator!.AddShadowingKey(key);
                continue;
            }
            storage.Set(properties, key);
            properties++;
            if (mode == KeyCollectionMode.OwnOnly && properties == length) break;
        }
        return storage;
    }

    /// <summary>KeyAccumulator::CollectOwnJSProxyKeys (ES #sec-proxy-object-internal-methods-and-internal-slots-ownpropertykeys).</summary>
    bool CollectOwnJSProxyKeys(JSReceiver receiver, JSProxy proxy)
    {
        _isolate.StackGuard.StackCheck(_isolate);
        if (_filter == PropertyFilter.PRIVATE_NAMES_ONLY)
        {
            CollectKeysFromDictionary(proxy.PropertyDictionary);
            return true;
        }

        // 1. Let handler be the value of the [[ProxyHandler]] internal slot of O.
        // 2. If handler is null, throw a TypeError exception.
        // 3. Assert: Type(handler) is Object.
        if (proxy.IsRevoked)
        {
            _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.ProxyRevoked, ReadOnlyRoots.ownKeys_string));
        }
        // 4. Let target be the value of the [[ProxyTarget]] internal slot of O.
        var target = proxy.Target.As<JSReceiver>();
        // 5. Let trap be ? GetMethod(handler, "ownKeys").
        JSValue trap = ObjectOps.GetMethod(_isolate, proxy.Handler.As<JSReceiver>(), ReadOnlyRoots.ownKeys_string);
        // 6. If trap is undefined, then
        if (trap.IsUndefined)
        {
            // 6a. Return target.[[OwnPropertyKeys]]().
            return CollectOwnJSProxyTargetKeys(proxy, target);
        }
        // 7. Let trapResultArray be Call(trap, handler, «target»).
        JSValue trapResultArray = Execution.Call(_isolate, trap, proxy.Handler, [target]);
        // 8. Let trapResult be ? CreateListFromArrayLike(trapResultArray,
        //    «String, Symbol»).
        FixedArray trapResult = ObjectOps.CreateListFromArrayLike(_isolate, trapResultArray, ElementTypes.StringAndSymbol);
        // 9. If trapResult contains any duplicate entries, throw a TypeError
        // exception. Combine with step 18
        // 18. Let uncheckedResultKeys be a new List which is a copy of trapResult.
        var uncheckedResultKeys = new Dictionary<JSValue, bool>(KeyComparer.Instance);
        int uncheckedResultKeysSize = 0;
        int trapResultLength = trapResult.Length;
        for (int i = 0; i < trapResultLength; ++i)
        {
            JSValue key = trapResult.Get(i);
            if (!uncheckedResultKeys.TryAdd(key, true))
            {
                // found dupes, throw exception
                _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.ProxyOwnKeysDuplicateEntries));
            }
            uncheckedResultKeysSize++;
        }
        // 10. Let extensibleTarget be ? IsExtensible(target).
        bool extensibleTarget = JSReceiver.IsExtensible(_isolate, target);
        // 11. Let targetKeys be ? target.[[OwnPropertyKeys]]().
        FixedArray targetKeys = JSReceiver.OwnPropertyKeys(_isolate, target);
        // 12, 13. (Assert)
        // 14. Let targetConfigurableKeys be an empty List.
        // To save memory, we're reusing target_keys and will modify it in-place.
        FixedArray targetConfigurableKeys = targetKeys;
        // 15. Let targetNonconfigurableKeys be an empty List.
        int targetKeysLength = targetKeys.Length;
        var targetNonconfigurableKeys = new FixedArray(targetKeysLength);
        int nonconfigurableKeysLength = 0;
        // 16. Repeat, for each element key of targetKeys:
        for (int i = 0; i < targetKeysLength; ++i)
        {
            // 16a. Let desc be ? target.[[GetOwnProperty]](key).
            var desc = new PropertyDescriptor();
            bool found = JSReceiver.GetOwnPropertyDescriptor(_isolate, target, targetKeys.Get(i), ref desc);
            // 16b. If desc is not undefined and desc.[[Configurable]] is false, then
            if (found && !desc.Configurable)
            {
                // 16b i. Append key as an element of targetNonconfigurableKeys.
                targetNonconfigurableKeys.Set(nonconfigurableKeysLength, targetKeys.Get(i));
                nonconfigurableKeysLength++;
                // The key was moved, null it out in the original list.
                targetKeys.Set(i, JSValue.Zero);
            }
            // 16c. Else,
            // 16c i. Append key as an element of targetConfigurableKeys.
            // (No-op, just keep it in |target_keys|.)
        }
        // 17. If extensibleTarget is true and targetNonconfigurableKeys is empty,
        //     then:
        if (extensibleTarget && nonconfigurableKeysLength == 0)
        {
            // 17a. Return trapResult.
            AddKeysFromJSProxy(proxy, trapResult);
            return true;
        }
        // 18. (Done in step 9)
        // 19. Repeat, for each key that is an element of targetNonconfigurableKeys:
        for (int i = 0; i < nonconfigurableKeysLength; ++i)
        {
            JSValue key = targetNonconfigurableKeys.Get(i);
            // 19a. If key is not an element of uncheckedResultKeys, throw a
            //      TypeError exception.
            if (!uncheckedResultKeys.TryGetValue(key, out bool present) || !present)
            {
                _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.ProxyOwnKeysMissing, key));
            }
            // 19b. Remove key from uncheckedResultKeys.
            uncheckedResultKeys[key] = false;
            uncheckedResultKeysSize--;
        }
        // 20. If extensibleTarget is true, return trapResult.
        if (extensibleTarget)
        {
            AddKeysFromJSProxy(proxy, trapResult);
            return true;
        }
        // 21. Repeat, for each key that is an element of targetConfigurableKeys:
        for (int i = 0; i < targetConfigurableKeys.Length; ++i)
        {
            JSValue key = targetConfigurableKeys.Get(i);
            if (key.IsNumber) continue;  // Zapped entry, was nonconfigurable.
            // 21a. If key is not an element of uncheckedResultKeys, throw a
            //      TypeError exception.
            if (!uncheckedResultKeys.TryGetValue(key, out bool present) || !present)
            {
                _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.ProxyOwnKeysMissing, key));
            }
            // 21b. Remove key from uncheckedResultKeys.
            uncheckedResultKeys[key] = false;
            uncheckedResultKeysSize--;
        }
        // 22. If uncheckedResultKeys is not empty, throw a TypeError exception.
        if (uncheckedResultKeysSize != 0)
        {
            _isolate.Throw(_isolate.Factory.NewTypeError(MessageTemplate.ProxyOwnKeysNonExtensible));
        }
        // 23. Return trapResult.
        AddKeysFromJSProxy(proxy, trapResult);
        return true;
    }

    /// <summary>KeyAccumulator::CollectOwnJSProxyTargetKeys.</summary>
    bool CollectOwnJSProxyTargetKeys(JSProxy proxy, JSReceiver target)
    {
        // TODO(cbruni): avoid creating another KeyAccumulator
        FixedArray keys = GetKeys(_isolate, target, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.ConvertToString, _isForIn, _skipIndices);
        AddKeysFromJSProxy(proxy, keys);
        return true;
    }
}

/// <summary>
/// V8's FastKeyAccumulator: tries the enum-cache fast paths before falling
/// back to the KeyAccumulator slow path.
/// </summary>
public sealed class FastKeyAccumulator
{
    readonly Isolate _isolate;
    readonly JSReceiver _receiver;
    readonly KeyCollectionMode _mode;
    readonly PropertyFilter _filter;
    readonly bool _isForIn;
    readonly bool _skipIndices;
    JSReceiver? _lastNonEmptyPrototype;
    bool _isReceiverSimpleEnum;
    bool _hasEmptyPrototype;
    bool _mayHaveElements = true;
    bool _onlyOwnHasSimpleElements;

    public FastKeyAccumulator(Isolate isolate, JSReceiver receiver, KeyCollectionMode mode, PropertyFilter filter,
        bool isForIn = false, bool skipIndices = false)
    {
        _isolate = isolate;
        _receiver = receiver;
        _mode = mode;
        _filter = filter;
        _isForIn = isForIn;
        _skipIndices = skipIndices;
        Prepare();
    }

    public bool IsReceiverSimpleEnum => _isReceiverSimpleEnum;
    public bool HasEmptyPrototype => _hasEmptyPrototype;
    public bool MayHaveElementsFlag => _mayHaveElements;

    static void TrySettingEmptyEnumCache(JSReceiver obj)
    {
        Map map = obj.Map;
        if (!map.OnlyHasSimpleProperties()) return;
        if (map.NumberOfEnumerableProperties() > 0) return;
        map.SetEnumLength(0);
    }

    static bool CheckAndInitializeEmptyEnumCache(JSReceiver obj)
    {
        if (obj.Map.EnumLength == Map.kInvalidEnumCacheSentinel) TrySettingEmptyEnumCache(obj);
        if (obj.Map.EnumLength != 0) return false;
        return !((JSObject)obj).HasEnumerableElements();
    }

    /// <summary>FastKeyAccumulator::Prepare.</summary>
    void Prepare()
    {
        // Directly go for the fast path for OWN_ONLY keys.
        if (_mode == KeyCollectionMode.OwnOnly) return;
        // Fully walk the prototype chain and find the last prototype with keys.
        _isReceiverSimpleEnum = false;
        _hasEmptyPrototype = true;
        _onlyOwnHasSimpleElements = !Map.IsCustomElementsReceiverMap(_receiver.Map);
        JSReceiver? lastPrototype = null;
        _mayHaveElements = MayHaveElements(_receiver);
        for (var iter = new PrototypeIterator(_isolate, _receiver); !iter.IsAtEnd; iter.Advance())
        {
            JSReceiver current = iter.GetCurrent()!;
            if (!_mayHaveElements || _onlyOwnHasSimpleElements)
            {
                if (MayHaveElements(current))
                {
                    _mayHaveElements = true;
                    _onlyOwnHasSimpleElements = false;
                }
            }
            bool hasNoProperties = current is JSObject && CheckAndInitializeEmptyEnumCache(current);
            if (hasNoProperties) continue;
            lastPrototype = current;
            _hasEmptyPrototype = false;
        }
        if (_hasEmptyPrototype)
        {
            _isReceiverSimpleEnum = _receiver is JSObject receiverObj &&
                                    _receiver.Map.EnumLength != Map.kInvalidEnumCacheSentinel &&
                                    !receiverObj.HasEnumerableElements();
        }
        else if (lastPrototype is not null)
        {
            _lastNonEmptyPrototype = lastPrototype;
        }
    }

    static FixedArray ReduceFixedArrayTo(FixedArray array, int length)
    {
        int arrayLength = array.Length;
        if (arrayLength == length) return array;
        return array.CopyAndResize(length);
    }

    /// <summary>
    /// GetFastEnumPropertyKeys: initializes and directly returns the enum cache.
    /// Users of this function have to make sure to never directly leak the enum cache.
    /// </summary>
    internal static FixedArray GetFastEnumPropertyKeys(Isolate isolate, JSObject obj)
    {
        Map map = obj.Map;
        FixedArray keys = map.InstanceDescriptors.EnumCache.Keys;

        // Check if the {map} has a valid enum length, which implies that it
        // must have a valid enum cache as well.
        int enumLength = map.EnumLength;
        if (enumLength != Map.kInvalidEnumCacheSentinel)
        {
            return ReduceFixedArrayTo(keys, enumLength);
        }

        // Determine the actual number of enumerable properties of the {map}.
        enumLength = map.NumberOfEnumerableProperties();

        // Check if there's already a shared enum cache on the {map}s
        // DescriptorArray with sufficient number of entries.
        if (enumLength <= keys.Length)
        {
            if (map.OnlyHasSimpleProperties()) map.SetEnumLength(enumLength);
            return ReduceFixedArrayTo(keys, enumLength);
        }

        return InitializeFastPropertyEnumCache(isolate, map, enumLength);
    }

    /// <summary>FastKeyAccumulator::InitializeFastPropertyEnumCache.</summary>
    public static FixedArray InitializeFastPropertyEnumCache(Isolate isolate, Map map, int enumLength)
    {
        DescriptorArray descriptors = map.InstanceDescriptors;

        // Create the keys array.
        int index = 0;
        bool fieldsOnly = true;
        var keys = new FixedArray(enumLength);
        int nof = map.NumberOfOwnDescriptors;
        for (int i = 0; i < nof; i++)
        {
            var idx = new InternalIndex(i);
            PropertyDetails details = descriptors.GetDetails(idx);
            if (details.IsDontEnum) continue;
            Name key = descriptors.GetKey(idx);
            if (key is Symbol) continue;
            keys.Set(index, key);
            if (details.Location != PropertyLocation.Field) fieldsOnly = false;
            index++;
        }

        // Optionally also create the indices array.
        FixedArray indices = FixedArray.Empty;
        if (fieldsOnly)
        {
            indices = new FixedArray(enumLength);
            index = 0;
            for (int i = 0; i < nof; i++)
            {
                var idx = new InternalIndex(i);
                PropertyDetails details = descriptors.GetDetails(idx);
                if (details.IsDontEnum) continue;
                Name key = descriptors.GetKey(idx);
                if (key is Symbol) continue;
                FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                indices.Set(index, JSValue.FromInt(fieldIndex.GetLoadByFieldIndex()));
                index++;
            }
        }

        DescriptorArray.InitializeOrChangeEnumCache(descriptors, keys, indices);
        if (map.OnlyHasSimpleProperties()) map.SetEnumLength(enumLength);
        return keys;
    }

    static FixedArray GetOwnKeysWithElements(Isolate isolate, JSObject obj, GetKeysConversion convert, bool skipIndices,
        bool fastProperties)
    {
        FixedArray keys = fastProperties
            ? GetFastEnumPropertyKeys(isolate, obj)
            : KeyAccumulator.GetOwnEnumPropertyKeys(isolate, obj);

        if (skipIndices) return keys;
        ElementsAccessor accessor = obj.GetElementsAccessor();
        return accessor.PrependElementIndices(isolate, obj, keys, convert, PropertyFilter.ONLY_ENUMERABLE);
    }

    /// <summary>FastKeyAccumulator::GetKeys.</summary>
    public FixedArray GetKeys(GetKeysConversion keysConversion = GetKeysConversion.KeepNumbers)
    {
        // TODO(v8:9401): We should extend the fast path of KeyAccumulator::GetKeys to
        // also use fast path even when filter = SKIP_SYMBOLS. We used to pass wrong
        // filter to use fast path in cases where we tried to verify all properties
        // are enumerable. However these checks weren't correct and passing the wrong
        // filter led to wrong behaviour.
        if (_filter == PropertyFilter.ENUMERABLE_STRINGS)
        {
            FixedArray? keys = GetKeysFast(keysConversion);
            if (keys is not null) return keys;
        }
        else if (_filter == PropertyFilter.SKIP_STRINGS && !MayHaveSymbols())
        {
            return FixedArray.Empty;
        }

        return GetKeysSlow(keysConversion);
    }

    bool MayHaveSymbols()
    {
        bool ownOnly = _hasEmptyPrototype || _mode == KeyCollectionMode.OwnOnly;
        Map map = _receiver.Map;
        if (!ownOnly || Map.IsCustomElementsReceiverMap(map)) return true;

        // From this point on we are certain to only collect own keys.
        if (map.IsDictionaryMap)
        {
            // TODO(olivf): Keep a bit in the dictionary to remember if we have any
            // symbols.
            return true;
        }
        int num = map.NumberOfOwnDescriptors;
        if (num == 0) return false;
        int enumLength = _receiver.Map.EnumLength;
        if (enumLength != Map.kInvalidEnumCacheSentinel) return enumLength != num;
        // TODO(olivf): Keep a bit in the descriptor to remember if we have any
        // symbols.
        return true;
    }

    FixedArray? GetKeysFast(GetKeysConversion keysConversion)
    {
        bool ownOnly = _hasEmptyPrototype || _mode == KeyCollectionMode.OwnOnly;
        Map map = _receiver.Map;
        if (!ownOnly || Map.IsCustomElementsReceiverMap(map)) return null;

        // From this point on we are certain to only collect own keys.
        var obj = (JSObject)_receiver;

        // Do not try to use the enum-cache for dict-mode objects.
        if (map.IsDictionaryMap) return GetOwnKeysWithElements(_isolate, obj, keysConversion, _skipIndices, false);
        int enumLength = _receiver.Map.EnumLength;
        if (enumLength == Map.kInvalidEnumCacheSentinel)
        {
            // Try initializing the enum cache and return own properties.
            FixedArray? keys = GetOwnKeysWithUninitializedEnumLength();
            if (keys is not null)
            {
                _isReceiverSimpleEnum = obj.Map.EnumLength != Map.kInvalidEnumCacheSentinel;
                return keys;
            }
        }
        // The properties-only case failed because there were probably elements on the
        // receiver.
        return GetOwnKeysWithElements(_isolate, obj, keysConversion, _skipIndices, true);
    }

    FixedArray? GetOwnKeysWithUninitializedEnumLength()
    {
        var obj = (JSObject)_receiver;
        // Uninitialized enum length
        Map map = obj.Map;
        if (!ReferenceEquals(obj.Elements, FixedArray.Empty) &&
            !ReferenceEquals(obj.Elements, ReadOnlyRoots.empty_slow_element_dictionary))
        {
            // Assume that there are elements.
            return null;
        }
        int numberOfOwnDescriptors = map.NumberOfOwnDescriptors;
        if (numberOfOwnDescriptors == 0)
        {
            map.SetEnumLength(0);
            return FixedArray.Empty;
        }
        // We have no elements but possibly enumerable property keys, hence we can
        // directly initialize the enum cache.
        FixedArray keys = GetFastEnumPropertyKeys(_isolate, obj);
        if (_isForIn) return keys;
        // Do not leak the enum cache as it might end up as an elements backing store.
        return keys.CopyAndResize(keys.Length);
    }

    FixedArray GetKeysSlow(GetKeysConversion keysConversion)
    {
        var accumulator = new KeyAccumulator(_isolate, _mode, _filter);
        accumulator.SetIsForIn(_isForIn);
        accumulator.SetSkipIndices(_skipIndices);
        accumulator.SetLastNonEmptyPrototype(_lastNonEmptyPrototype);
        accumulator.SetMayHaveElements(_mayHaveElements);

        accumulator.CollectKeys(_receiver, _receiver);
        return accumulator.GetKeys(keysConversion);
    }

    static bool MayHaveElements(JSReceiver receiver)
    {
        if (receiver is not JSObject obj) return true;
        if (obj.HasEnumerableElements()) return true;
        if (obj.Map.HasIndexedInterceptor) return true;
        return false;
    }
}
