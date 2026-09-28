// Port of the remaining operations of src/objects/js-objects.cc (from
// JSReceiver::SetIntegrityLevel on): integrity levels, extensibility,
// ToPrimitive, Object.values/entries, [[SetPrototypeOf]], object creation,
// map migration (fast<->fast, fast->slow, slow->fast), property addition,
// DefineOwnPropertyIgnoreAttributes, element normalization, prototype
// registration and invalidation, AddDataElement and TransitionElementsKind.
// Also the inline helpers of js-objects-inl.h and object-predicates-inl.h
// they use.
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

public abstract partial class JSReceiver
{
    /// <summary>JSReceiver::SetIntegrityLevel.</summary>
    public static bool SetIntegrityLevel(Isolate isolate, JSReceiver receiver, IntegrityLevel level, ShouldThrow shouldThrow)
    {
        if (receiver is JSObject obj)
        {
            if (!obj.HasSloppyArgumentsElements && obj is not JSModuleNamespace)
            {
                // Fast path.
                // Prevent memory leaks by not adding unnecessary transitions.
                if (JSObject.TestIntegrityLevel(isolate, obj, level)) return true;
                return JSObject.PreventExtensionsWithTransition(isolate, obj,
                    level == IntegrityLevel.SEALED ? PropertyAttributes.SEALED : PropertyAttributes.FROZEN, shouldThrow);
            }
        }

        PreventExtensions(isolate, receiver, shouldThrow);

        FixedArray keys = OwnPropertyKeys(isolate, receiver);

        var noConf = new PropertyDescriptor();
        noConf.SetConfigurable(false);

        var noConfNoWrite = new PropertyDescriptor();
        noConfNoWrite.SetConfigurable(false);
        noConfNoWrite.SetWritable(false);

        int keysLength = keys.Length;
        if (level == IntegrityLevel.SEALED)
        {
            for (int i = 0; i < keysLength; ++i)
            {
                JSValue key = keys.Get(i);
                var desc = noConf;
                DefineOwnProperty(isolate, receiver, key, ref desc, ShouldThrow.ThrowOnError);
            }
            return true;
        }

        for (int i = 0; i < keysLength; ++i)
        {
            JSValue key = keys.Get(i);
            var currentDesc = new PropertyDescriptor();
            bool owned = GetOwnPropertyDescriptor(isolate, receiver, key, ref currentDesc);
            if (owned)
            {
                PropertyDescriptor desc = PropertyDescriptor.IsAccessorDescriptor(in currentDesc) ? noConf : noConfNoWrite;
                DefineOwnProperty(isolate, receiver, key, ref desc, ShouldThrow.ThrowOnError);
            }
        }
        return true;
    }

    internal static bool GenericTestIntegrityLevel(Isolate isolate, JSReceiver receiver, IntegrityLevel level)
    {
        bool extensible = IsExtensible(isolate, receiver);
        if (extensible) return false;

        FixedArray keys = OwnPropertyKeys(isolate, receiver);

        int keysLength = keys.Length;
        for (int i = 0; i < keysLength; ++i)
        {
            JSValue key = keys.Get(i);
            var currentDesc = new PropertyDescriptor();
            bool owned = GetOwnPropertyDescriptor(isolate, receiver, key, ref currentDesc);
            if (owned)
            {
                if (currentDesc.Configurable) return false;
                if (level == IntegrityLevel.FROZEN && PropertyDescriptor.IsDataDescriptor(in currentDesc) && currentDesc.Writable)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>JSReceiver::TestIntegrityLevel.</summary>
    public static bool TestIntegrityLevel(Isolate isolate, JSReceiver receiver, IntegrityLevel level)
    {
        if (!Map.IsCustomElementsReceiverMap(receiver.Map))
        {
            return JSObject.TestIntegrityLevel(isolate, (JSObject)receiver, level);
        }
        return GenericTestIntegrityLevel(isolate, receiver, level);
    }

    /// <summary>JSReceiver::PreventExtensions.</summary>
    public static bool PreventExtensions(Isolate isolate, JSReceiver obj, ShouldThrow shouldThrow)
    {
        if (obj is JSProxy proxy) return JSProxy.PreventExtensions(isolate, proxy, shouldThrow);
        return JSObject.PreventExtensions(isolate, (JSObject)obj, shouldThrow);
    }

    /// <summary>JSReceiver::IsExtensible.</summary>
    public static bool IsExtensible(Isolate isolate, JSReceiver obj)
    {
        if (obj is JSProxy proxy) return JSProxy.IsExtensible(isolate, proxy);
        return JSObject.IsExtensible(isolate, (JSObject)obj);
    }

    /// <summary>JSReceiver::OwnPropertyKeys: KeyAccumulator::GetKeys(kOwnOnly, ALL_PROPERTIES, kConvertToString).</summary>
    public static FixedArray OwnPropertyKeys(Isolate isolate, JSReceiver obj) =>
        KeyAccumulator.GetKeys(isolate, obj, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.ConvertToString);

    /// <summary>JSReceiver::ToPrimitive (ES #sec-toprimitive, steps for an object input).</summary>
    public static JSValue ToPrimitive(Isolate isolate, JSReceiver receiver, ToPrimitiveHint hint = ToPrimitiveHint.Default)
    {
        JSValue exoticToPrim = ObjectOps.GetMethod(isolate, receiver, ReadOnlyRoots.to_primitive_symbol);
        if (!exoticToPrim.IsUndefined)
        {
            JSValue hintString = ToPrimitiveHintString(hint);
            JSValue result = Execution.Call(isolate, exoticToPrim, receiver, [hintString]);
            if (ObjectOps.IsPrimitive(result)) return result;
            return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CannotConvertToPrimitive));
        }
        return OrdinaryToPrimitive(isolate, receiver,
            hint == ToPrimitiveHint.String ? OrdinaryToPrimitiveHint.String : OrdinaryToPrimitiveHint.Number);
    }

    /// <summary>Factory::ToPrimitiveHintString.</summary>
    public static JSString ToPrimitiveHintString(ToPrimitiveHint hint) => hint switch
    {
        ToPrimitiveHint.Default => ReadOnlyRoots.default_string,
        ToPrimitiveHint.Number => ReadOnlyRoots.number_string,
        _ => ReadOnlyRoots.string_string,
    };

    /// <summary>JSReceiver::OrdinaryToPrimitive.</summary>
    public static JSValue OrdinaryToPrimitive(Isolate isolate, JSReceiver receiver, OrdinaryToPrimitiveHint hint)
    {
        JSString first, second;
        switch (hint)
        {
            case OrdinaryToPrimitiveHint.Number:
                first = ReadOnlyRoots.valueOf_string;
                second = ReadOnlyRoots.toString_string;
                break;
            default:
                first = ReadOnlyRoots.toString_string;
                second = ReadOnlyRoots.valueOf_string;
                break;
        }
        for (int i = 0; i < 2; i++)
        {
            JSString name = i == 0 ? first : second;
            JSValue method = GetProperty(isolate, receiver, name);
            if (ObjectOps.IsCallable(method))
            {
                JSValue result = Execution.Call(isolate, method, receiver, []);
                if (ObjectOps.IsPrimitive(result)) return result;
            }
        }
        return isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CannotConvertToPrimitive));
    }

    static bool FastGetOwnValuesOrEntries(Isolate isolate, JSReceiver receiver, bool getEntries, out FixedArray? result)
    {
        result = null;
        Map map = receiver.Map;

        if (!Map.IsJSObjectMap(map)) return false;
        if (!map.OnlyHasSimpleProperties()) return false;

        var obj = (JSObject)receiver;
        DescriptorArray descriptors = map.InstanceDescriptors;

        int numberOfOwnDescriptors = map.NumberOfOwnDescriptors;
        long numberOfOwnElements = obj.GetElementsAccessor().GetCapacity(obj, obj.Elements);

        if (numberOfOwnElements + numberOfOwnDescriptors > FixedArrayBase.kMaxLength)
        {
            isolate.Throw(isolate.Factory.NewRangeError(MessageTemplate.InvalidArrayLength));
        }
        FixedArray valuesOrEntries = isolate.Factory.NewFixedArray((int)(numberOfOwnDescriptors + numberOfOwnElements));
        int count = 0;

        if (!ReferenceEquals(obj.Elements, FixedArray.Empty))
        {
            obj.GetElementsAccessor().CollectValuesOrEntries(isolate, obj, valuesOrEntries, getEntries, ref count,
                PropertyFilter.ENUMERABLE_STRINGS);
        }

        // We may have already lost stability, if CollectValuesOrEntries had
        // side-effects.
        bool stable = ReferenceEquals(map, obj.Map);
        if (stable) descriptors = map.InstanceDescriptors;

        for (int i = 0; i < numberOfOwnDescriptors; i++)
        {
            var index = new InternalIndex(i);
            Name nextKey = descriptors.GetKey(index);
            if (nextKey is not JSString) continue;
            JSValue propValue;

            // Directly decode from the descriptor array if |from| did not change shape.
            if (stable)
            {
                PropertyDetails details = descriptors.GetDetails(index);
                if (!details.IsEnumerable) continue;
                if (details.Kind == PropertyKind.Data)
                {
                    if (details.Location == PropertyLocation.Descriptor)
                    {
                        propValue = descriptors.GetStrongValue(index);
                    }
                    else
                    {
                        FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                        propValue = JSObject.FastPropertyAt(isolate, obj, details.Representation, fieldIndex);
                    }
                }
                else
                {
                    var it = new LookupIterator(isolate, obj, nextKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    propValue = ObjectOps.GetProperty(ref it);
                    stable = ReferenceEquals(obj.Map, map);
                    descriptors = map.InstanceDescriptors;
                }
            }
            else
            {
                // If the map did change, do a slower lookup. We are still guaranteed that
                // the object has a simple shape, and that the key is a name.
                var it = new LookupIterator(isolate, obj, nextKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                if (!it.IsFound) continue;
                if (!it.IsEnumerable) continue;
                propValue = ObjectOps.GetProperty(ref it);
            }

            if (getEntries) propValue = MakeEntryPair(isolate, nextKey, propValue);

            valuesOrEntries.Set(count, propValue);
            count++;
        }

        result = RightTrimOrEmpty(valuesOrEntries, count);
        return true;
    }

    static JSValue MakeEntryPair(Isolate isolate, JSValue key, JSValue value)
    {
        FixedArray entryStorage = isolate.Factory.NewFixedArray(2);
        entryStorage.Set(0, key);
        entryStorage.Set(1, value);
        return isolate.Factory.NewJSArrayWithElements(entryStorage, ElementsKind.PACKED_ELEMENTS, 2);
    }

    /// <summary>FixedArray::RightTrimOrEmpty.</summary>
    internal static FixedArray RightTrimOrEmpty(FixedArray array, int newLength)
    {
        if (newLength == 0) return FixedArray.Empty;
        if (newLength < array.Length) array.RightTrim(newLength);
        return array;
    }

    static FixedArray GetOwnValuesOrEntries(Isolate isolate, JSReceiver obj, PropertyFilter filter, bool tryFastPath,
        bool getEntries)
    {
        if (tryFastPath && filter == PropertyFilter.ENUMERABLE_STRINGS)
        {
            if (FastGetOwnValuesOrEntries(isolate, obj, getEntries, out FixedArray? fast)) return fast!;
        }

        PropertyFilter keyFilter = filter & ~PropertyFilter.ONLY_ENUMERABLE;

        FixedArray keys = KeyAccumulator.GetKeys(isolate, obj, KeyCollectionMode.OwnOnly, keyFilter,
            GetKeysConversion.ConvertToString);

        int keysLength = keys.Length;
        FixedArray valuesOrEntries = isolate.Factory.NewFixedArray(keysLength);
        int length = 0;

        for (int i = 0; i < keysLength; ++i)
        {
            var key = (Name)keys.Get(i).Object;

            if ((filter & PropertyFilter.ONLY_ENUMERABLE) != 0)
            {
                var descriptor = new PropertyDescriptor();
                bool didGetDescriptor = GetOwnPropertyDescriptor(isolate, obj, key, ref descriptor);
                if (!didGetDescriptor || !descriptor.Enumerable) continue;
            }

            JSValue value = ObjectOps.GetPropertyOrElement(isolate, obj, key);

            if (getEntries) value = MakeEntryPair(isolate, key, value);

            valuesOrEntries.Set(length, value);
            length++;
        }
        return RightTrimOrEmpty(valuesOrEntries, length);
    }

    /// <summary>JSReceiver::GetOwnValues.</summary>
    public static FixedArray GetOwnValues(Isolate isolate, JSReceiver obj, PropertyFilter filter, bool tryFastPath = true) =>
        GetOwnValuesOrEntries(isolate, obj, filter, tryFastPath, false);

    /// <summary>JSReceiver::GetOwnEntries.</summary>
    public static FixedArray GetOwnEntries(Isolate isolate, JSReceiver obj, PropertyFilter filter, bool tryFastPath = true) =>
        GetOwnValuesOrEntries(isolate, obj, filter, tryFastPath, true);

    /// <summary>JSReceiver::SetPrototype ([[SetPrototypeOf]]).</summary>
    public static bool SetPrototype(Isolate isolate, JSReceiver obj, JSValue value, bool fromJavaScript, ShouldThrow shouldThrow)
    {
        if (obj is JSProxy proxy)
        {
            if (!value.IsJSReceiver && !value.IsNull)
            {
                return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.ProtoObjectOrNull, value);
            }
            return JSProxy.SetPrototype(isolate, proxy, value.AsOrNull<JSReceiver>(), fromJavaScript, shouldThrow);
        }
        return JSObject.SetPrototype(isolate, (JSObject)obj, value, fromJavaScript, shouldThrow);
    }

    /// <summary>JSReceiver::GetPrototype (js-objects-inl.h): [[GetPrototypeOf]], following proxies.</summary>
    public static JSReceiver? GetPrototype(Isolate isolate, JSReceiver receiver)
    {
        // We don't expect access checks to be needed on JSProxy objects.
        var iter = new PrototypeIterator(isolate, receiver, WhereToStart.StartAtReceiver,
            PrototypeIterator.WhereToEnd.END_AT_NON_HIDDEN);
        do
        {
            iter.AdvanceFollowingProxies();
        } while (!iter.IsAtEnd);
        return iter.GetCurrent();
    }

    /// <summary>JSReceiver::HasProxyInPrototype.</summary>
    public bool HasProxyInPrototype(Isolate isolate)
    {
        for (var iter = new PrototypeIterator(isolate, this, WhereToStart.StartAtReceiver); !iter.IsAtEnd;
             iter.AdvanceIgnoringProxies())
        {
            if (iter.GetCurrent() is JSProxy) return true;
        }
        return false;
    }
}

public partial class JSObject
{
    /// <summary>JSObject::AccessorInfoHandling.</summary>
    public enum AccessorInfoHandling { FORCE_FIELD, DONT_FORCE_FIELD }

    /// <summary>
    /// Creates the CLR object for a JSObject map: the class is chosen by the
    /// map's instance type (V8 allocates InstanceSize bytes and the map says
    /// what they are). Function maps are not accepted: functions are created
    /// by Factory.NewFunction, which needs a SharedFunctionInfo.
    /// </summary>
    public static JSObject AllocateForMap(Map map)
    {
        switch (map.InstanceType)
        {
            case InstanceType.JSArrayType:
                return new JSArray(map);
            case InstanceType.JSPrimitiveWrapperType:
                return new JSPrimitiveWrapper(map);
            case InstanceType.JSGlobalObjectType:
                return new JSGlobalObject(map);
            case InstanceType.JSGlobalProxyType:
                return new JSGlobalProxy(map);
            case InstanceType.JSArgumentsObjectType:
                return new JSArgumentsObject(map);
            case InstanceType.JSDateType:
                return new JSDate(map);
            case InstanceType.JSRegExpType:
                return new JSRegExp(map);
            case InstanceType.JSPromiseType:
                return new JSPromise(map);
            case InstanceType.JSMapType:
                return new JSMap(map);
            case InstanceType.JSSetType:
                return new JSSet(map);
            case InstanceType.JSWeakMapType:
                return new JSWeakMap(map);
            case InstanceType.JSWeakSetType:
                return new JSWeakSet(map);
            case InstanceType.JSWeakRefType:
                return new JSWeakRef(map);
            case InstanceType.JSFinalizationRegistryType:
                return new JSFinalizationRegistry(map);
            case InstanceType.JSArrayBufferType:
                return new JSArrayBuffer(map);
            case InstanceType.JSTypedArrayType:
                return new JSTypedArray(map);
            case InstanceType.JSDataViewType:
            case InstanceType.JSRabGsabDataViewType:
                return new JSDataView(map);
            case InstanceType.JSGeneratorObjectType:
                return new JSGeneratorObject(map);
            case InstanceType.JSAsyncGeneratorObjectType:
                return new JSAsyncGeneratorObject(map);
            case InstanceType.JSAsyncFunctionObjectType:
                return new JSAsyncFunctionObject(map);
            case InstanceType.JSArrayIteratorType:
                return new JSArrayIterator(map);
            case InstanceType.JSStringIteratorType:
                return new JSStringIterator(map);
            case InstanceType.JSMapKeyIteratorType:
            case InstanceType.JSMapKeyValueIteratorType:
            case InstanceType.JSMapValueIteratorType:
                return new JSMapIterator(map);
            case InstanceType.JSSetKeyValueIteratorType:
            case InstanceType.JSSetValueIteratorType:
                return new JSSetIterator(map);
            case InstanceType.JSRegExpStringIteratorType:
                return new JSRegExpStringIterator(map);
            case InstanceType.JSAsyncFromSyncIteratorType:
                return new JSAsyncFromSyncIterator(map);
            case InstanceType.JSIteratorMapHelperType:
                return new JSIteratorMapHelper(map);
            case InstanceType.JSIteratorFilterHelperType:
                return new JSIteratorFilterHelper(map);
            case InstanceType.JSIteratorTakeHelperType:
                return new JSIteratorTakeHelper(map);
            case InstanceType.JSIteratorDropHelperType:
                return new JSIteratorDropHelper(map);
            case InstanceType.JSIteratorFlatMapHelperType:
                return new JSIteratorFlatMapHelper(map);
            case InstanceType.JSIteratorConcatHelperType:
                return new JSIteratorConcatHelper(map);
            case InstanceType.JSIteratorZipHelperType:
                return new JSIteratorZipHelper(map);
            case InstanceType.JSIteratorZipKeyedHelperType:
                return new JSIteratorZipKeyedHelper(map);
            case InstanceType.JSValidIteratorWrapperType:
                return new JSValidIteratorWrapper(map);
            case InstanceType.JSDisposableStackBaseType:
                return new JSDisposableStackBase(map);
            case InstanceType.JSDisposableStackType:
                return new JSSyncDisposableStack(map);
            case InstanceType.JSAsyncDisposableStackType:
                return new JSAsyncDisposableStack(map);
            case InstanceType.JSModuleNamespaceType:
                return new JSModuleNamespace(map);
            case InstanceType.JSRawJsonType:
                return new JSRawJson(map);
            case InstanceType.JSShadowRealmType:
                return new JSShadowRealm(map);
            case InstanceType.JSExternalObjectType:
                return new JSExternalObject(map);
            case InstanceType.JSProxyType:
            case InstanceType.JSBoundFunctionType:
            case InstanceType.JSWrappedFunctionType:
                throw new InvalidOperationException("AllocateForMap: " + map.InstanceType + " is not allocated from a map alone");
            default:
                if (InstanceTypeChecks.IsJSFunction(map.InstanceType))
                {
                    throw new InvalidOperationException("AllocateForMap: functions are created by Factory.NewFunction");
                }
                return new JSObject(map);
        }
    }

    /// <summary>JSObject::New: [[Construct]] for ordinary objects, with new.target.</summary>
    public static JSObject New(Isolate isolate, JSFunction constructor, JSReceiver newTarget, AllocationSite? site = null)
    {
        // If called through new, new.target can be:
        // - a subclass of constructor,
        // - a proxy wrapper around constructor, or
        // - the constructor itself.
        // If called through Reflect.construct, it's guaranteed to be a constructor.
        Map initialMap = JSFunction.GetDerivedMap(isolate, constructor, newTarget);
        return NewFastOrSlowJSObjectFromMap(isolate, initialMap, NameDictionary.kInitialCapacity, site);
    }

    /// <summary>JSObject::NewWithMap.</summary>
    public static JSObject NewWithMap(Isolate isolate, Map initialMap, AllocationSite? site = null) =>
        NewFastOrSlowJSObjectFromMap(isolate, initialMap, NameDictionary.kInitialCapacity, site);

    /// <summary>Factory::NewFastOrSlowJSObjectFromMap.</summary>
    public static JSObject NewFastOrSlowJSObjectFromMap(Isolate isolate, Map map, int capacity = NameDictionary.kInitialCapacity,
        AllocationSite? site = null) =>
        map.IsDictionaryMap
            ? isolate.Factory.NewSlowJSObjectFromMap(map, capacity)
            : isolate.Factory.NewJSObjectFromMap(map, site);

    /// <summary>JSObject::ObjectCreate (ES #sec-objectcreate, not Object.create).</summary>
    public static JSObject ObjectCreate(Isolate isolate, JSReceiver? prototype)
    {
        // Generate the map with the specified {prototype} based on the Object
        // function's initial map from the current native context.
        Map map = Map.GetObjectCreateMap(isolate, prototype);
        // Actually allocate the object.
        return NewFastOrSlowJSObjectFromMap(isolate, map);
    }

    /// <summary>
    /// JSObject::GetHeaderSize. V8Sharp objects have no raw layout; the sizes
    /// are V8's (pointer-compressed, kTaggedSize = 4) so that instance sizes,
    /// in-object property counts and slack tracking come out as in V8.
    /// </summary>
    public static int GetHeaderSize(InstanceType type)
    {
        const int kTagged = Map.kTaggedSize;
        switch (type)
        {
            case InstanceType.JSSpecialApiObjectType:
            case InstanceType.JSApiObjectType:
            case InstanceType.JSObjectType:
            case InstanceType.JSIteratorPrototypeType:
            case InstanceType.JSObjectPrototypeType:
            case InstanceType.JSArrayIteratorPrototypeType:
            case InstanceType.JSPromisePrototypeType:
            case InstanceType.JSRegExpPrototypeType:
            case InstanceType.JSStringIteratorPrototypeType:
            case InstanceType.JSMapIteratorPrototypeType:
            case InstanceType.JSSetIteratorPrototypeType:
            case InstanceType.JSSetPrototypeType:
            case InstanceType.JSTypedArrayPrototypeType:
            case InstanceType.JSContextExtensionObjectType:
            case InstanceType.JSArgumentsObjectType:
            case InstanceType.JSErrorType:
                return kHeaderSize;
            case InstanceType.JSGeneratorObjectType:
                return kHeaderSize + 7 * kTagged;
            case InstanceType.JSAsyncFunctionObjectType:
                return kHeaderSize + 8 * kTagged;
            case InstanceType.JSAsyncGeneratorObjectType:
                return kHeaderSize + 9 * kTagged;
            case InstanceType.JSAsyncFromSyncIteratorType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSGlobalProxyType:
                return kHeaderSize;
            case InstanceType.JSGlobalObjectType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSBoundFunctionType:
                return kHeaderSize + 3 * kTagged;
            case InstanceType.JSFunctionWithoutPrototypeType:
                return kHeaderSize + 4 * kTagged;
            case InstanceType.JSFunctionType:
            case InstanceType.JSClassConstructorType:
            case InstanceType.JSPromiseConstructorType:
            case InstanceType.JSRegExpConstructorType:
            case InstanceType.JSArrayConstructorType:
            case InstanceType.JSObjectConstructorType:
                return kHeaderSize + 5 * kTagged;
            case InstanceType.JSPrimitiveWrapperType:
                return kHeaderSize + kTagged;
            case InstanceType.JSDateType:
                return kHeaderSize + 10 * kTagged;
            case InstanceType.JSDisposableStackBaseType:
            case InstanceType.JSDisposableStackType:
            case InstanceType.JSAsyncDisposableStackType:
                return kHeaderSize + 4 * kTagged;
            case InstanceType.JSArrayType:
                return kHeaderSize + kTagged;
            case InstanceType.JSArrayBufferType:
                return kHeaderSize + 12 * kTagged;
            case InstanceType.JSArrayIteratorType:
                return kHeaderSize + 3 * kTagged;
            case InstanceType.JSTypedArrayType:
                return kHeaderSize + 14 * kTagged;
            case InstanceType.JSDataViewType:
            case InstanceType.JSRabGsabDataViewType:
                return kHeaderSize + 10 * kTagged;
            case InstanceType.JSSetType:
            case InstanceType.JSMapType:
            case InstanceType.JSWeakMapType:
            case InstanceType.JSWeakSetType:
                return kHeaderSize + kTagged;
            case InstanceType.JSSetKeyValueIteratorType:
            case InstanceType.JSSetValueIteratorType:
            case InstanceType.JSMapKeyIteratorType:
            case InstanceType.JSMapKeyValueIteratorType:
            case InstanceType.JSMapValueIteratorType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSWeakRefType:
                return kHeaderSize + kTagged;
            case InstanceType.JSFinalizationRegistryType:
                return kHeaderSize + 7 * kTagged;
            case InstanceType.JSPromiseType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSRegExpType:
                return kHeaderSize + 3 * kTagged;
            case InstanceType.JSRegExpStringIteratorType:
                return kHeaderSize + 3 * kTagged;
            case InstanceType.JSExternalObjectType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSShadowRealmType:
                return kHeaderSize + kTagged;
            case InstanceType.JSStringIteratorType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSIteratorHelperType:
            case InstanceType.JSIteratorMapHelperType:
            case InstanceType.JSIteratorFilterHelperType:
            case InstanceType.JSIteratorTakeHelperType:
            case InstanceType.JSIteratorDropHelperType:
            case InstanceType.JSIteratorFlatMapHelperType:
            case InstanceType.JSIteratorConcatHelperType:
            case InstanceType.JSIteratorZipHelperType:
            case InstanceType.JSIteratorZipKeyedHelperType:
                return kHeaderSize + 5 * kTagged;
            case InstanceType.JSModuleNamespaceType:
                return kHeaderSize + kTagged;
            case InstanceType.JSSharedArrayType:
            case InstanceType.JSSharedStructType:
            case InstanceType.JSAtomicsMutexType:
            case InstanceType.JSAtomicsConditionType:
                return kHeaderSize + kTagged;
            case InstanceType.JSValidIteratorWrapperType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSWrappedFunctionType:
                return kHeaderSize + 2 * kTagged;
            case InstanceType.JSRawJsonType:
                return kHeaderSize;
            case InstanceType.JSArgumentsExoticObjectType:
                return kHeaderSize;
            default:
                throw new InvalidOperationException("unexpected instance type: " + type);
        }
    }

    /// <summary>JSObject::GetHeaderSize(map).</summary>
    public static int GetHeaderSize(Map map) => GetHeaderSize(map.InstanceType);

    /// <summary>JSObject::GetEmbedderFieldCount: V8Sharp has no embedder fields.</summary>
    public static int GetEmbedderFieldCount(Map map) => 0;

    // ---- Dictionary-mode properties ---------------------------------------------------

    /// <summary>JSObject::SetNormalizedProperty.</summary>
    public static void SetNormalizedProperty(Isolate isolate, JSObject obj, Name name, JSValue value, PropertyDetails details)
    {
        Debug.Assert(!obj.HasFastProperties);

        if (obj is JSGlobalObject globalObj)
        {
            GlobalDictionary dictionary = globalObj.GlobalDictionary;
            InternalIndex entry = dictionary.FindEntry(name);

            if (entry.IsNotFound)
            {
                PropertyCellType cellType = value.IsUndefined ? PropertyCellType.Undefined : PropertyCellType.Constant;
                details = details.SetCellType(cellType);
                PropertyCell cell = isolate.Factory.NewPropertyCell(name, details, value);
                dictionary = GlobalDictionary.Add(isolate, dictionary, name, cell, details, out _);
                globalObj.GlobalDictionary = dictionary;
            }
            else
            {
                PropertyCell.PrepareForAndSetValue(isolate, dictionary, entry, value, details);
            }
        }
        else
        {
            NameDictionary dictionary = obj.PropertyDictionary;
            InternalIndex entry = dictionary.FindEntry(name);
            if (entry.IsNotFound)
            {
                dictionary = NameDictionary.Add(isolate, dictionary, name, value, details);
                obj.SetProperties(dictionary);
            }
            else
            {
                PropertyDetails originalDetails = dictionary.DetailsAt(entry);
                int enumerationIndex = originalDetails.DictionaryIndex;
                Debug.Assert(enumerationIndex > 0);
                details = details.SetIndex(enumerationIndex);
                dictionary.SetEntry(entry, name, value, details);
            }
            if (name.IsInteresting()) dictionary.MayHaveInterestingProperties = true;
        }
    }

    /// <summary>JSObject::SetNormalizedElement.</summary>
    public static void SetNormalizedElement(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyDetails details)
    {
        Debug.Assert(obj.GetElementsKind() == ElementsKind.DICTIONARY_ELEMENTS);
        NumberDictionary dictionary = (NumberDictionary)obj.Elements;
        dictionary = NumberDictionary.Set(isolate, dictionary, index, value, obj, details);
        obj.Elements = dictionary;
    }

    // ---- Map changes ---------------------------------------------------------------

    /// <summary>JSObject::UpdatePrototypeUserRegistration.</summary>
    public static void UpdatePrototypeUserRegistration(Map oldMap, Map newMap, Isolate isolate)
    {
        Debug.Assert(oldMap.IsPrototypeMap);
        Debug.Assert(newMap.IsPrototypeMap);
        bool wasRegistered = UnregisterPrototypeUser(oldMap, isolate);
        newMap.RawTransitions = oldMap.PrototypeInfo;
        oldMap.RawTransitions = null;
        if (wasRegistered)
        {
            if (newMap.HasPrototypeInfo)
            {
                // The new map isn't registered with its prototype yet; reflect this fact
                // in the PrototypeInfo it just inherited from the old map.
                newMap.PrototypeInfo!.RegistrySlot = PrototypeInfo.UNREGISTERED;
            }
            LazyRegisterPrototypeUser(newMap, isolate);
        }
    }

    /// <summary>JSObject::NotifyMapChange.</summary>
    public static void NotifyMapChange(Map oldMap, Map newMap, Isolate isolate)
    {
        if (!oldMap.IsPrototypeMap) return;

        InvalidatePrototypeChains(oldMap);

        // If the map was registered with its prototype before, ensure that it
        // registers with its new prototype now. This preserves the invariant that
        // when a map on a prototype chain is registered with its prototype, then
        // all prototypes further up the chain are also registered with their
        // respective prototypes.
        UpdatePrototypeUserRegistration(oldMap, newMap, isolate);
    }

    // To migrate a fast instance to a fast map:
    // - First check whether the instance needs to be rewritten. If not, simply
    //   change the map.
    // - Otherwise, build the new field storage and copy every field into the
    //   slot its new descriptor names. V8Sharp keeps in-object and out-of-object
    //   fields in one array indexed by field index (architecture.md section 5),
    //   and numbers are unboxed, so no HeapNumber boxes are allocated.
    static void MigrateFastToFast(Isolate isolate, JSObject obj, Map newMap)
    {
        Map oldMap = obj.Map;
        // In case of a regular transition.
        if (ReferenceEquals(newMap.GetBackPointer(), oldMap))
        {
            // If the map does not add named properties, simply set the map.
            if (oldMap.NumberOfOwnDescriptors == newMap.NumberOfOwnDescriptors)
            {
                obj.Map = newMap;
                return;
            }

            // If the map adds a new kDescriptor property, simply set the map.
            PropertyDetails details = newMap.GetLastDescriptorDetails();
            if (details.Location == PropertyLocation.Descriptor)
            {
                obj.Map = newMap;
                return;
            }

            // Make room for the new field (V8 grows the PropertyArray by
            // UnusedPropertyFields() + 1 when it has run out of space).
            FieldIndex index = FieldIndex.ForDetails(newMap, details);
            if (index.PropertyIndex >= obj._fields.Length)
            {
                obj.EnsureFieldCapacity(index.PropertyIndex + newMap.UnusedPropertyFields() + 1);
            }
            // Properly initialize newly added property.
            obj._fields[index.PropertyIndex] = details.Representation.IsDouble
                ? JSValue.FromNumber(FixedDoubleArray.HoleNaN)
                : JSValue.FromObject(Oddball.Uninitialized);
            obj.Map = newMap;
            return;
        }

        int numberOfFields = newMap.NumberOfFields();
        int inobject = newMap.GetInObjectProperties();
        int unused = newMap.UnusedPropertyFields();

        // Nothing to do if no functions were converted to fields and no smis were
        // converted to doubles.
        if (!oldMap.InstancesNeedRewriting(newMap, numberOfFields, inobject, unused, out _))
        {
            if (obj._fields.Length < numberOfFields) obj.EnsureFieldCapacity(numberOfFields + unused);
            obj.Map = newMap;
            return;
        }

        int totalSize = Math.Max(numberOfFields + unused, inobject);
        var newFields = totalSize == 0 ? EmptyFields : new JSValue[totalSize];

        DescriptorArray oldDescriptors = oldMap.InstanceDescriptors;
        DescriptorArray newDescriptors = newMap.InstanceDescriptors;
        int oldNof = oldMap.NumberOfOwnDescriptors;
        int newNof = newMap.NumberOfOwnDescriptors;

        // This method only supports generalizing instances to at least the same
        // number of properties.
        Debug.Assert(oldNof <= newNof);

        for (int i = 0; i < oldNof; i++)
        {
            var idx = new InternalIndex(i);
            PropertyDetails details = newDescriptors.GetDetails(idx);
            if (details.Location != PropertyLocation.Field) continue;
            PropertyDetails oldDetails = oldDescriptors.GetDetails(idx);
            JSValue value;
            if (oldDetails.Location == PropertyLocation.Descriptor)
            {
                if (oldDetails.Kind == PropertyKind.Accessor)
                {
                    // In case of kAccessor -> kData property reconfiguration, the property
                    // must already be prepared for data of certain type.
                    value = details.Representation.IsDouble
                        ? JSValue.FromNumber(FixedDoubleArray.HoleNaN)
                        : JSValue.FromObject(Oddball.Uninitialized);
                }
                else
                {
                    value = oldDescriptors.GetStrongValue(idx);
                }
            }
            else
            {
                FieldIndex index = FieldIndex.ForDetails(oldMap, oldDetails);
                value = obj.RawFastPropertyAt(index);
                if (!oldDetails.Representation.IsDouble && details.Representation.IsDouble &&
                    ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized))
                {
                    value = JSValue.FromNumber(FixedDoubleArray.HoleNaN);
                }
            }
            newFields[details.FieldIndex] = value;
        }

        for (int i = oldNof; i < newNof; i++)
        {
            var idx = new InternalIndex(i);
            PropertyDetails details = newDescriptors.GetDetails(idx);
            if (details.Location != PropertyLocation.Field) continue;
            newFields[details.FieldIndex] = details.Representation.IsDouble
                ? JSValue.FromNumber(FixedDoubleArray.HoleNaN)
                : JSValue.FromObject(Oddball.Uninitialized);
        }

        obj._fields = newFields;
        obj.Map = newMap;
    }

    static void MigrateFastToSlow(Isolate isolate, JSObject obj, Map newMap, int expectedAdditionalProperties)
    {
        // The global object is always normalized.
        Debug.Assert(obj is not JSGlobalObject);
        // JSGlobalProxy must never be normalized
        Debug.Assert(obj is not JSGlobalProxy);

        Map map = obj.Map;

        // Allocate new content.
        int realSize = map.NumberOfOwnDescriptors;
        int propertyCount = realSize;
        if (expectedAdditionalProperties > 0)
        {
            propertyCount += expectedAdditionalProperties;
        }
        else
        {
            // Make space for two more properties.
            propertyCount += NameDictionary.kInitialCapacity;
        }

        NameDictionary dictionary = NameDictionary.New(propertyCount);

        DescriptorArray descs = map.InstanceDescriptors;
        for (int i = 0; i < realSize; i++)
        {
            var idx = new InternalIndex(i);
            PropertyDetails details = descs.GetDetails(idx);
            Name key = descs.GetKey(idx);
            JSValue value;
            if (details.Location == PropertyLocation.Field)
            {
                FieldIndex index = FieldIndex.ForDetails(map, details);
                value = obj.RawFastPropertyAt(index);
            }
            else
            {
                value = descs.GetStrongValue(idx);
            }
            var d = new PropertyDetails(details.Kind, details.Attributes, PropertyConstness.Mutable);
            dictionary = NameDictionary.Add(isolate, dictionary, key, value, d);
        }

        // Copy the next enumeration index from instance descriptor.
        dictionary.NextEnumerationIndexRaw = realSize + 1;
        dictionary.MayHaveInterestingProperties = map.MayHaveInterestingProperties;

        obj.Map = newMap;
        obj.SetProperties(dictionary);

        // Ensure that in-object space of slow-mode object does not contain random
        // garbage.
        int inobjectProperties = newMap.GetInObjectProperties();
        obj._fields = inobjectProperties == 0 ? EmptyFields : new JSValue[inobjectProperties];
        for (int i = 0; i < inobjectProperties; i++) obj._fields[i] = JSValue.Zero;
    }

    /// <summary>JSObject::SetMapAndElements.</summary>
    public static void SetMapAndElements(Isolate isolate, JSObject obj, Map newMap, FixedArrayBase value)
    {
        MigrateToMap(isolate, obj, newMap);
        obj.Elements = value;
    }

    /// <summary>JSObject::PrototypeHasNoElements (js-objects-inl.h).</summary>
    public static bool PrototypeHasNoElements(Isolate isolate, JSObject obj)
    {
        JSReceiver? prototype = obj.Map.Prototype;
        while (prototype is not null)
        {
            Map map = prototype.Map;
            if (Map.IsCustomElementsReceiverMap(map)) return false;
            FixedArrayBase elements = ((JSObject)prototype).Elements;
            if (!ReferenceEquals(elements, ReadOnlyRoots.empty_fixed_array) &&
                !ReferenceEquals(elements, ReadOnlyRoots.empty_slow_element_dictionary))
            {
                return false;
            }
            prototype = map.Prototype;
        }
        return true;
    }

    /// <summary>JSObject::MigrateToMap.</summary>
    public static void MigrateToMap(Isolate isolate, JSObject obj, Map newMap, int expectedAdditionalProperties = 0)
    {
        if (ReferenceEquals(obj.Map, newMap)) return;
        Map oldMap = obj.Map;
        NotifyMapChange(oldMap, newMap, isolate);

        if (oldMap.IsDictionaryMap)
        {
            // For slow-to-fast migrations JSObject::MigrateSlowToFast(isolate, )
            // must be used instead.
            if (!newMap.IsDictionaryMap) throw new InvalidOperationException("MigrateToMap: slow to fast needs MigrateSlowToFast");

            // Slow-to-slow migration is trivial.
            obj.Map = newMap;
        }
        else if (!newMap.IsDictionaryMap)
        {
            MigrateFastToFast(isolate, obj, newMap);
            if (oldMap.IsPrototypeMap)
            {
                // Transfer ownership to the new map. Keep the descriptor pointer of the
                // old map intact because the concurrent marker might be iterating the
                // object with the old map.
                oldMap.OwnsDescriptors = false;
            }
        }
        else
        {
            MigrateFastToSlow(isolate, obj, newMap, expectedAdditionalProperties);
        }
    }

    /// <summary>JSObject::ForceSetPrototype.</summary>
    public static void ForceSetPrototype(Isolate isolate, JSObject obj, JSReceiver? proto)
    {
        // object.__proto__ = proto;
        Map newMap = Map.Copy(isolate, obj.Map, "ForceSetPrototype");
        Map.SetPrototype(isolate, newMap, proto);
        MigrateToMap(isolate, obj, newMap);
    }

    /// <summary>JSObject::GetElementsTransitionMap.</summary>
    public static Map GetElementsTransitionMap(Isolate isolate, JSObject obj, ElementsKind toKind) =>
        Map.TransitionElementsTo(isolate, obj.Map, toKind);

    /// <summary>JSObject::AllocateStorageForMap.</summary>
    public static void AllocateStorageForMap(Isolate isolate, JSObject obj, Map map)
    {
        Debug.Assert(obj.Map.GetInObjectProperties() == map.GetInObjectProperties());
        ElementsKind objKind = obj.Map.ElementsKind;
        ElementsKind mapKind = map.ElementsKind;
        if (mapKind != objKind)
        {
            ElementsKind toKind = ElementsKinds.GetMoreGeneralElementsKind(mapKind, objKind);
            if (ElementsKinds.IsDictionaryElementsKind(objKind)) toKind = objKind;
            if (ElementsKinds.IsDictionaryElementsKind(toKind))
            {
                NormalizeElements(isolate, obj);
            }
            else
            {
                TransitionElementsKind(isolate, obj, toKind);
            }
            map = new MapUpdater(isolate, map).ReconfigureElementsKind(toKind);
        }
        int numberOfFields = map.NumberOfFields();
        int inobject = map.GetInObjectProperties();
        int unused = map.UnusedPropertyFields();
        int totalSize = Math.Max(numberOfFields + unused, inobject);

        var storage = totalSize == 0 ? EmptyFields : new JSValue[totalSize];
        DescriptorArray descriptors = map.InstanceDescriptors;
        int nof = map.NumberOfOwnDescriptors;
        for (int i = 0; i < nof; i++)
        {
            PropertyDetails details = descriptors.GetDetails(new InternalIndex(i));
            if (!details.Representation.IsDouble) continue;
            storage[details.FieldIndex] = JSValue.FromNumber(FixedDoubleArray.HoleNaN);
        }
        obj._fields = storage;
        obj.Map = map;
    }

    /// <summary>JSObject::MigrateInstance.</summary>
    public static void MigrateInstance(Isolate isolate, JSObject obj)
    {
        Map originalMap = obj.Map;
        Map map = Map.Update(isolate, originalMap);
        map.IsMigrationTarget = true;
        MigrateToMap(isolate, obj, map);
    }

    /// <summary>JSObject::TryMigrateInstance.</summary>
    public static bool TryMigrateInstance(Isolate isolate, JSObject obj)
    {
        Map? newMap = Map.TryUpdate(isolate, obj.Map);
        if (newMap is null) return false;
        MigrateToMap(isolate, obj, newMap);
        return true;
    }

    static bool TryFastAddDataProperty(Isolate isolate, JSObject obj, Name name, JSValue value, PropertyAttributes attributes)
    {
        Map? map = TransitionsAccessor.SearchTransition(isolate, obj.Map, name, PropertyKind.Data, attributes);
        if (map is null) return false;
        Debug.Assert(!map.IsDictionaryMap);

        Map newMap = map;
        if (map.IsDeprecated)
        {
            newMap = Map.Update(isolate, newMap);
            if (newMap.IsDictionaryMap) return false;
        }

        InternalIndex descriptor = newMap.LastAdded();
        newMap = Map.PrepareForDataProperty(isolate, newMap, descriptor, PropertyConstness.Const, value);
        if (newMap.IsDictionaryMap) return false;
        MigrateToMap(isolate, obj, newMap);
        PropertyDetails details = newMap.InstanceDescriptors.GetDetails(descriptor);
        if (details.Representation.IsDouble && value.IsNumber && double.IsNaN(value.Number))
        {
            value = JSValue.NaN;
        }
        obj.WriteToField(descriptor, details, value);
        return true;
    }

    /// <summary>JSObject::AddProperty: adds a property the object is known not to have.</summary>
    public static void AddProperty(Isolate isolate, JSObject obj, Name name, JSValue value, PropertyAttributes attributes)
    {
        name = isolate.Factory.InternalizeName(name);
        if (TryFastAddDataProperty(isolate, obj, name, value, attributes)) return;

        var it = new LookupIterator(isolate, obj, name, obj, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        Debug.Assert(it.State != LookupIterator.StateKind.ACCESS_CHECK);
        ObjectOps.AddDataProperty(ref it, value, attributes, ShouldThrow.ThrowOnError, StoreOrigin.Named);
    }

    /// <summary>JSObject::AddProperty(const char* name).</summary>
    public static void AddProperty(Isolate isolate, JSObject obj, string name, JSValue value, PropertyAttributes attributes) =>
        AddProperty(isolate, obj, isolate.Factory.InternalizeString(name), value, attributes);

    /// <summary>
    /// JSObject::DefineOwnPropertyIgnoreAttributes: reconfigures a property to a
    /// data property with attributes, even if it is not reconfigurable. The
    /// iterator must not look at the prototype chain beyond hidden prototypes.
    /// </summary>
    public static bool DefineOwnPropertyIgnoreAttributes(ref LookupIterator it, JSValue value, PropertyAttributes attributes,
        ShouldThrow? shouldThrow = null, AccessorInfoHandling handling = AccessorInfoHandling.DONT_FORCE_FIELD,
        EnforceDefineSemantics semantics = EnforceDefineSemantics.Set, StoreOrigin storeOrigin = StoreOrigin.Named,
        JSValue? oldValue = null)
    {
        it.UpdateProtector(value, oldValue);

        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.JSPROXY:
                case LookupIterator.StateKind.TRANSITION:
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    throw new InvalidOperationException("unreachable");
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // {AddDataProperty} will throw if no other case is hit.

                case LookupIterator.StateKind.ACCESS_CHECK:
                    continue;

                // Interceptors are not ported (no embedder API); the lookup never
                // stops at INTERCEPTOR.
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;

                case LookupIterator.StateKind.ACCESSOR:
                {
                    HeapObject accessors = it.GetAccessors();

                    // Special handling for AccessorInfo, which behaves like a data
                    // property.
                    if (accessors is AccessorInfo && handling == AccessorInfoHandling.DONT_FORCE_FIELD)
                    {
                        PropertyAttributes currentAttributes = it.PropertyAttributes();
                        // Update the attributes before calling the setter. The setter may
                        // later change the shape of the property.
                        if (currentAttributes != attributes)
                        {
                            it.TransitionToAccessorPair(accessors, attributes);
                        }
                        return ObjectOps.SetPropertyWithAccessor(ref it, value, shouldThrow);
                    }

                    it.ReconfigureDataProperty(value, attributes);
                    return true;
                }
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return ObjectOps.RedefineIncompatibleProperty(it.Isolate, it.GetName(), value, shouldThrow);

                case LookupIterator.StateKind.DATA:
                {
                    // Regular property update if the attributes match.
                    if (it.PropertyAttributes() == attributes)
                    {
                        return ObjectOps.SetDataProperty(ref it, value);
                    }

                    // The non-matching attribute case for JSTypedArrays has already been
                    // handled by JSTypedArray::DefineOwnProperty.
                    // Reconfigure the data property if the attributes mismatch.
                    it.ReconfigureDataProperty(value, attributes);
                    return true;
                }

                case LookupIterator.StateKind.NOT_FOUND:
                    return ObjectOps.AddDataProperty(ref it, value, attributes, shouldThrow, storeOrigin, semantics);
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>JSObject::SetOwnPropertyIgnoreAttributes.</summary>
    public static JSValue SetOwnPropertyIgnoreAttributes(Isolate isolate, JSObject obj, Name name, JSValue value, PropertyAttributes attributes)
    {
        Debug.Assert(!value.IsTheHole);
        var it = new LookupIterator(isolate, obj, name, obj, LookupIterator.Configuration.OWN);
        DefineOwnPropertyIgnoreAttributes(ref it, value, attributes);
        return value;
    }

    /// <summary>JSObject::SetOwnElementIgnoreAttributes.</summary>
    public static JSValue SetOwnElementIgnoreAttributes(Isolate isolate, JSObject obj, ulong index, JSValue value, PropertyAttributes attributes)
    {
        Debug.Assert(obj is not JSTypedArray);
        var it = new LookupIterator(isolate, obj, index, obj, LookupIterator.Configuration.OWN);
        DefineOwnPropertyIgnoreAttributes(ref it, value, attributes);
        return value;
    }

    /// <summary>JSObject::DefinePropertyOrElementIgnoreAttributes.</summary>
    public static JSValue DefinePropertyOrElementIgnoreAttributes(Isolate isolate, JSObject obj, Name name, JSValue value,
        PropertyAttributes attributes = PropertyAttributes.NONE)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, obj, LookupIterator.Configuration.OWN);
        DefineOwnPropertyIgnoreAttributes(ref it, value, attributes);
        return value;
    }

    /// <summary>JSObject::NormalizeProperties.</summary>
    public static void NormalizeProperties(Isolate isolate, JSObject obj, PropertyNormalizationMode mode,
        int expectedAdditionalProperties, bool useCache, string reason)
    {
        if (!obj.HasFastProperties) return;

        Map map = obj.Map;
        Map newMap = Map.Normalize(isolate, map, map.InstanceType, map.ElementsKind, null, false, mode, useCache, reason);

        MigrateToMap(isolate, obj, newMap, expectedAdditionalProperties);
    }

    /// <summary>JSObject::NormalizeProperties (use_cache = true).</summary>
    public static void NormalizeProperties(Isolate isolate, JSObject obj, PropertyNormalizationMode mode,
        int expectedAdditionalProperties, string reason) =>
        NormalizeProperties(isolate, obj, mode, expectedAdditionalProperties, true, reason);

    /// <summary>JSObject::MigrateSlowToFast.</summary>
    public static void MigrateSlowToFast(Isolate isolate, JSObject obj, int unusedPropertyFields, string reason)
    {
        if (obj.HasFastProperties) return;
        Debug.Assert(obj is not JSGlobalObject);

        NameDictionary dictionary = obj.PropertyDictionary;
        int numberOfElements = dictionary.NumberOfElements;

        // Make sure we preserve dictionary representation if there are too many
        // descriptors.
        if (numberOfElements > Map.kMaxNumberOfDescriptors) return;

        int[] iterationOrder = NameDictionary.IterationIndices(dictionary);
        int iterationLength = dictionary.NumberOfElements;

        int numberOfFields = 0;

        // Compute the length of the instance descriptor.
        for (int i = 0; i < iterationLength; i++)
        {
            var index = new InternalIndex(iterationOrder[i]);
            PropertyKind kind = dictionary.DetailsAt(index).Kind;
            if (kind == PropertyKind.Data) numberOfFields += 1;
        }

        Map oldMap = obj.Map;

        int inobjectProps = oldMap.GetInObjectProperties();

        // Allocate new map.
        Map newMap = Map.CopyDropDescriptors(isolate, oldMap);
        // We should not only set this bit if we need to. We should not retain the
        // old bit because turning a map into dictionary always sets this bit.
        newMap.MayHaveInterestingProperties = newMap.HasNamedInterceptor || newMap.IsAccessCheckNeeded;
        newMap.IsDictionaryMap = false;

        NotifyMapChange(oldMap, newMap, isolate);

        if (numberOfElements == 0)
        {
            Debug.Assert(unusedPropertyFields <= inobjectProps);
            // Transform the object.
            newMap.SetInObjectUnusedPropertyFields(inobjectProps);
            obj.Map = newMap;
            obj._dictionary = null;
            obj._fields = inobjectProps == 0 ? EmptyFields : new JSValue[inobjectProps];
            return;
        }

        // Allocate the instance descriptor.
        DescriptorArray descriptors = DescriptorArray.Allocate(numberOfElements, 0);

        int numberOfAllocatedFields = numberOfFields + unusedPropertyFields - inobjectProps;
        if (numberOfAllocatedFields < 0)
        {
            // There is enough inobject space for all fields (including unused).
            numberOfAllocatedFields = 0;
            unusedPropertyFields = inobjectProps - numberOfFields;
        }

        // Allocate the storage for the fields.
        var fields = new JSValue[inobjectProps + numberOfAllocatedFields];

        bool isTransitionableElementsKind = ElementsKinds.IsTransitionableFastElementsKind(oldMap.ElementsKind);

        // Fill in the instance descriptor and the fields.
        int currentField = 0;
        int descriptorIndex = 0;
        for (int i = 0; i < iterationLength; i++)
        {
            var index = new InternalIndex(iterationOrder[i]);
            Name key = dictionary.NameAt(index);
            JSValue value = dictionary.ValueAt(index);
            PropertyDetails details = dictionary.DetailsAt(index);

            // Properly mark the {new_map} if the {key} is an "interesting symbol".
            if (key.IsInteresting()) newMap.MayHaveInterestingProperties = true;

            Descriptor d;
            if (details.Kind == PropertyKind.Data)
            {
                // Ensure that we make constant field only when elements kind is not
                // transitionable.
                PropertyConstness constness = isTransitionableElementsKind ? PropertyConstness.Mutable : PropertyConstness.Const;
                d = Descriptor.DataField(key, currentField, details.Attributes, constness, Representation.Tagged,
                    FieldType.Any);
            }
            else
            {
                d = Descriptor.AccessorConstant(key, value.Object, details.Attributes);
            }
            details = d.GetDetails();
            if (details.Location == PropertyLocation.Field)
            {
                fields[currentField] = value;
                currentField++;
            }
            descriptors.Set(new InternalIndex(descriptorIndex++), in d);
        }
        Debug.Assert(descriptorIndex == numberOfElements);

        descriptors.Sort();

        newMap.InitializeDescriptors(descriptors);
        if (numberOfAllocatedFields == 0)
        {
            newMap.SetInObjectUnusedPropertyFields(unusedPropertyFields);
        }
        else
        {
            newMap.SetOutOfObjectUnusedPropertyFields(unusedPropertyFields);
        }

        // Transform the object.
        obj.Map = newMap;
        obj._dictionary = null;
        obj._fields = fields;
    }

    /// <summary>JSObject::RequireSlowElements.</summary>
    public void RequireSlowElements(NumberDictionary dictionary)
    {
        if (dictionary.RequiresSlowElements) return;
        dictionary.SetRequiresSlowElements();
        if (Map.IsPrototypeMap)
        {
            // If this object is a prototype (the callee will check), invalidate any
            // prototype chains involving it.
            InvalidatePrototypeChains(Map);
        }
    }

    /// <summary>JSObject::NormalizeElements.</summary>
    public static NumberDictionary NormalizeElements(Isolate isolate, JSObject obj)
    {
        Debug.Assert(!obj.HasTypedArrayOrRabGsabTypedArrayElements);
        bool isSloppyArguments = obj.HasSloppyArgumentsElements;
        {
            FixedArrayBase elements = obj.Elements;
            if (isSloppyArguments) elements = ((SloppyArgumentsElements)elements).Arguments;
            if (elements is NumberDictionary existing) return existing;
        }

        NumberDictionary dictionary = obj.GetElementsAccessor().Normalize(isolate, obj);

        // Switch to using the dictionary as the backing storage for elements.
        ElementsKind targetKind = isSloppyArguments ? ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS
            : obj.HasFastStringWrapperElements ? ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS
            : ElementsKind.DICTIONARY_ELEMENTS;
        Map newMap = GetElementsTransitionMap(isolate, obj, targetKind);
        // Set the new map first to satisfy the elements type assert in
        // set_elements().
        MigrateToMap(isolate, obj, newMap);

        if (isSloppyArguments)
        {
            ((SloppyArgumentsElements)obj.Elements).Arguments = dictionary;
        }
        else
        {
            obj.Elements = dictionary;
        }
        return dictionary;
    }

    /// <summary>JSObject::CreateDataProperty.</summary>
    public static bool CreateDataProperty(Isolate isolate, JSObject obj, in PropertyKey key, JSValue value,
        ShouldThrow? shouldThrow = null)
    {
        if (!key.IsElement)
        {
            if (TryFastAddDataProperty(isolate, obj, key.Name!, value, PropertyAttributes.NONE)) return true;
        }

        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN);
        if (!CheckIfCanDefineAsConfigurable(isolate, ref it, value, shouldThrow)) return false;

        DefineOwnPropertyIgnoreAttributes(ref it, value, PropertyAttributes.NONE);
        return true;
    }

    // ---- Integrity levels -------------------------------------------------------------

    static bool TestDictionaryPropertiesIntegrityLevel(NameDictionary dict, IntegrityLevel level)
    {
        for (int i = 0; i < dict.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dict.ToKey(entry, out JSValue key)) continue;
            if (ObjectOps.FilterKey(key, PropertyFilter.ALL_PROPERTIES)) continue;
            PropertyDetails details = dict.DetailsAt(entry);
            if (details.IsConfigurable) return false;
            if (level == IntegrityLevel.FROZEN &&
                (details.Kind == PropertyKind.Data || dict.ValueAt(entry).HeapObjectOrNull is AccessorInfo) &&
                !details.IsReadOnly)
            {
                return false;
            }
        }
        return true;
    }

    static bool TestDictionaryPropertiesIntegrityLevel(GlobalDictionary dict, IntegrityLevel level)
    {
        for (int i = 0; i < dict.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dict.ToKey(entry, out _)) continue;
            JSValue key = dict.NameAt(entry);
            if (ObjectOps.FilterKey(key, PropertyFilter.ALL_PROPERTIES)) continue;
            PropertyDetails details = dict.DetailsAt(entry);
            if (details.IsConfigurable) return false;
            if (level == IntegrityLevel.FROZEN &&
                (details.Kind == PropertyKind.Data || dict.ValueAt(entry).HeapObjectOrNull is AccessorInfo) &&
                !details.IsReadOnly)
            {
                return false;
            }
        }
        return true;
    }

    static bool TestDictionaryPropertiesIntegrityLevel(NumberDictionary dict, IntegrityLevel level)
    {
        for (int i = 0; i < dict.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dict.ToKey(entry, out _)) continue;
            PropertyDetails details = dict.DetailsAt(entry);
            if (details.IsConfigurable) return false;
            if (level == IntegrityLevel.FROZEN &&
                (details.Kind == PropertyKind.Data || dict.ValueAt(entry).HeapObjectOrNull is AccessorInfo) &&
                !details.IsReadOnly)
            {
                return false;
            }
        }
        return true;
    }

    static bool TestFastPropertiesIntegrityLevel(Map map, IntegrityLevel level)
    {
        Debug.Assert(!map.IsDictionaryMap);

        DescriptorArray descriptors = map.InstanceDescriptors;
        int nof = map.NumberOfOwnDescriptors;
        for (int i = 0; i < nof; i++)
        {
            var idx = new InternalIndex(i);
            if (descriptors.GetKey(idx).IsAnyPrivate) continue;
            PropertyDetails details = descriptors.GetDetails(idx);
            if (details.IsConfigurable) return false;
            if (level == IntegrityLevel.FROZEN &&
                (details.Kind == PropertyKind.Data || descriptors.GetStrongValue(idx).HeapObjectOrNull is AccessorInfo) &&
                !details.IsReadOnly)
            {
                return false;
            }
        }
        return true;
    }

    static bool TestPropertiesIntegrityLevel(JSObject obj, IntegrityLevel level)
    {
        if (obj.HasFastProperties) return TestFastPropertiesIntegrityLevel(obj.Map, level);
        if (obj is JSGlobalObject global) return TestDictionaryPropertiesIntegrityLevel(global.GlobalDictionary, level);
        return TestDictionaryPropertiesIntegrityLevel(obj.PropertyDictionary, level);
    }

    static bool TestElementsIntegrityLevel(Isolate isolate, JSObject obj, IntegrityLevel level)
    {
        Debug.Assert(!obj.HasSloppyArgumentsElements);

        ElementsKind kind = obj.GetElementsKind();

        if (ElementsKinds.IsDictionaryElementsKind(kind))
        {
            return TestDictionaryPropertiesIntegrityLevel((NumberDictionary)obj.Elements, level);
        }
        if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(kind))
        {
            if (level == IntegrityLevel.FROZEN && ((JSTypedArray)obj).ByteLength > 0)
            {
                return false;  // TypedArrays with elements can't be frozen.
            }
            return TestPropertiesIntegrityLevel(obj, level);
        }
        if (ElementsKinds.IsFrozenElementsKind(kind)) return true;
        if (ElementsKinds.IsSealedElementsKind(kind) && level != IntegrityLevel.FROZEN) return true;

        ElementsAccessor accessor = ElementsAccessor.ForKind(kind);
        // Only DICTIONARY_ELEMENTS and SLOW_SLOPPY_ARGUMENTS_ELEMENTS have
        // PropertyAttributes so just test if empty
        return accessor.NumberOfElements(isolate, obj) == 0;
    }

    static bool FastTestIntegrityLevel(Isolate isolate, JSObject obj, IntegrityLevel level) =>
        !obj.Map.IsExtensible &&
        TestElementsIntegrityLevel(isolate, obj, level) &&
        TestPropertiesIntegrityLevel(obj, level);

    /// <summary>JSObject::TestIntegrityLevel.</summary>
    public static bool TestIntegrityLevel(Isolate isolate, JSObject obj, IntegrityLevel level)
    {
        if (!Map.IsCustomElementsReceiverMap(obj.Map) && !obj.HasSloppyArgumentsElements)
        {
            return FastTestIntegrityLevel(isolate, obj, level);
        }
        return GenericTestIntegrityLevel(isolate, obj, level);
    }

    /// <summary>JSObject::PreventExtensions.</summary>
    public static bool PreventExtensions(Isolate isolate, JSObject obj, ShouldThrow shouldThrow)
    {
        if (!obj.HasSloppyArgumentsElements)
        {
            return PreventExtensionsWithTransition(isolate, obj, PropertyAttributes.NONE, shouldThrow);
        }

        if (!obj.Map.IsExtensible) return true;

        if (obj is JSGlobalProxy)
        {
            var iter = new PrototypeIterator(isolate, obj);
            if (iter.IsAtEnd) return true;
            return PreventExtensions(isolate, iter.GetCurrent<JSObject>(), shouldThrow);
        }

        if (obj.Map.HasNamedInterceptor || obj.Map.HasIndexedInterceptor)
        {
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.CannotPreventExt);
        }

        // Normalize fast elements.
        NumberDictionary dictionary = NormalizeElements(isolate, obj);

        // Make sure that we never go back to fast case.
        if (!ReferenceEquals(dictionary, ReadOnlyRoots.empty_slow_element_dictionary))
        {
            obj.RequireSlowElements(dictionary);
        }

        // Do a map transition, other objects with this map may still
        // be extensible.
        Map newMap = Map.Copy(isolate, obj.Map, "PreventExtensions");

        newMap.IsExtensible = false;
        MigrateToMap(isolate, obj, newMap);
        return true;
    }

    /// <summary>JSObject::IsExtensible.</summary>
    public static bool IsExtensible(Isolate isolate, JSObject obj)
    {
        if (obj is JSGlobalProxy)
        {
            var iter = new PrototypeIterator(isolate, obj);
            if (iter.IsAtEnd) return false;
            return iter.GetCurrent<JSObject>().Map.IsExtensible;
        }
        return obj.Map.IsExtensible;
    }

    /// <summary>JSObject::ReadFromOptionsBag.</summary>
    public static JSValue ReadFromOptionsBag(JSValue options, JSString optionName, Isolate isolate)
    {
        if (options.HeapObjectOrNull is JSReceiver jsOptions) return GetProperty(isolate, jsOptions, optionName);
        return JSValue.Undefined;
    }

    static PropertyDetails ApplyAttributes(PropertyDetails details, JSValue value, PropertyAttributes attributes)
    {
        PropertyAttributes attrs = attributes;
        // READ_ONLY is an invalid attribute for JS setters/getters.
        if ((attributes & PropertyAttributes.READ_ONLY) != 0 && details.Kind == PropertyKind.Accessor)
        {
            if (value.HeapObjectOrNull is AccessorPair) attrs &= ~PropertyAttributes.READ_ONLY;
        }
        return details.CopyAddAttributes(attrs);
    }

    /// <summary>JSObject::ApplyAttributesToDictionary (NameDictionary).</summary>
    public static void ApplyAttributesToDictionary(Isolate isolate, NameDictionary dictionary, PropertyAttributes attributes)
    {
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue k)) continue;
            if (ObjectOps.FilterKey(k, PropertyFilter.ALL_PROPERTIES)) continue;
            dictionary.DetailsAtPut(entry, ApplyAttributes(dictionary.DetailsAt(entry), dictionary.ValueAt(entry), attributes));
        }
    }

    /// <summary>JSObject::ApplyAttributesToDictionary (GlobalDictionary).</summary>
    public static void ApplyAttributesToDictionary(Isolate isolate, GlobalDictionary dictionary, PropertyAttributes attributes)
    {
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out _)) continue;
            if (ObjectOps.FilterKey(dictionary.NameAt(entry), PropertyFilter.ALL_PROPERTIES)) continue;
            dictionary.DetailsAtPut(entry, ApplyAttributes(dictionary.DetailsAt(entry), dictionary.ValueAt(entry), attributes));
        }
    }

    /// <summary>JSObject::ApplyAttributesToDictionary (NumberDictionary).</summary>
    public static void ApplyAttributesToDictionary(Isolate isolate, NumberDictionary dictionary, PropertyAttributes attributes)
    {
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out _)) continue;
            dictionary.DetailsAtPut(entry, ApplyAttributes(dictionary.DetailsAt(entry), dictionary.ValueAt(entry), attributes));
        }
    }

    static NumberDictionary? CreateElementDictionary(Isolate isolate, JSObject obj)
    {
        NumberDictionary? newElementDictionary = null;
        if (!obj.HasTypedArrayOrRabGsabTypedArrayElements && !obj.HasDictionaryElements && !obj.HasSlowStringWrapperElements)
        {
            uint length = obj is JSArray array ? (uint)array.Length.Number : (uint)obj.Elements.Length;
            newElementDictionary = length == 0
                ? ReadOnlyRoots.empty_slow_element_dictionary
                : obj.GetElementsAccessor().Normalize(isolate, obj);
        }
        return newElementDictionary;
    }

    /// <summary>JSObject::PreventExtensionsWithTransition&lt;attrs&gt; (attrs is NONE, SEALED or FROZEN).</summary>
    public static bool PreventExtensionsWithTransition(Isolate isolate, JSObject obj, PropertyAttributes attrs,
        ShouldThrow shouldThrow)
    {
        Debug.Assert(attrs is PropertyAttributes.NONE or PropertyAttributes.SEALED or PropertyAttributes.FROZEN);

        // Sealing/freezing sloppy arguments or namespace objects should be handled
        // elsewhere.
        Debug.Assert(!obj.HasSloppyArgumentsElements);

        if (attrs == PropertyAttributes.NONE && !obj.Map.IsExtensible) return true;

        {
            ElementsKind oldElementsKind = obj.Map.ElementsKind;
            if (ElementsKinds.IsFrozenElementsKind(oldElementsKind)) return true;
            if (attrs != PropertyAttributes.FROZEN && ElementsKinds.IsSealedElementsKind(oldElementsKind)) return true;
        }

        if (obj is JSGlobalProxy)
        {
            var iter = new PrototypeIterator(isolate, obj);
            if (iter.IsAtEnd) return true;
            return PreventExtensionsWithTransition(isolate, iter.GetCurrent<JSObject>(), attrs, shouldThrow);
        }

        if (obj.Map.HasNamedInterceptor || obj.Map.HasIndexedInterceptor ||
            (obj.HasTypedArrayOrRabGsabTypedArrayElements && ((JSTypedArray)obj).IsVariableLength))
        {
            MessageTemplate message = attrs switch
            {
                PropertyAttributes.NONE => MessageTemplate.CannotPreventExt,
                PropertyAttributes.SEALED => MessageTemplate.CannotSeal,
                _ => MessageTemplate.CannotFreeze,
            };
            return ObjectOps.ReturnFailure(isolate, shouldThrow, message);
        }

        Symbol transitionMarker = attrs == PropertyAttributes.NONE ? ReadOnlyRoots.nonextensible_symbol
            : attrs == PropertyAttributes.SEALED ? ReadOnlyRoots.sealed_symbol
            : ReadOnlyRoots.frozen_symbol;

        // Currently, there are only have sealed/frozen Object element kinds and
        // Map::MigrateToMap doesn't handle properties' attributes reconfiguring and
        // elements kind change in one go. If seal or freeze with Smi or Double
        // elements kind, we will transition to Object elements kind first to make
        // sure of valid element access.
        switch (obj.Map.ElementsKind)
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
                TransitionElementsKind(isolate, obj, ElementsKind.PACKED_ELEMENTS);
                break;
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                TransitionElementsKind(isolate, obj, ElementsKind.HOLEY_ELEMENTS);
                break;
        }

        // Make sure we only use this element dictionary in case we can't transition
        // to sealed, frozen elements kind.
        NumberDictionary? newElementDictionary = null;

        Map oldMap = Map.Update(isolate, obj.Map);
        Map? transitionMap = TransitionsAccessor.SearchSpecial(isolate, oldMap, transitionMarker);
        if (transitionMap is not null)
        {
            Debug.Assert(!transitionMap.IsExtensible);
            if (!transitionMap.HasAnyNonextensibleElements)
            {
                newElementDictionary = CreateElementDictionary(isolate, obj);
            }
            MigrateToMap(isolate, obj, transitionMap);
        }
        else if (TransitionsAccessor.CanHaveMoreTransitions(isolate, oldMap))
        {
            // Create a new descriptor array with the appropriate property attributes
            Map newMap = Map.CopyForPreventExtensions(isolate, oldMap, attrs, transitionMarker, "CopyForPreventExtensions");
            if (!newMap.HasAnyNonextensibleElements)
            {
                newElementDictionary = CreateElementDictionary(isolate, obj);
            }
            MigrateToMap(isolate, obj, newMap);
        }
        else
        {
            // Slow path: need to normalize properties for safety
            NormalizeProperties(isolate, obj, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0, "SlowPreventExtensions");

            // Create a new map, since other objects with this map may be extensible.
            Map newMap = Map.Copy(isolate, obj.Map, "SlowCopyForPreventExtensions");
            newMap.IsExtensible = false;
            newElementDictionary = CreateElementDictionary(isolate, obj);
            if (newElementDictionary is not null)
            {
                ElementsKind newKind = ElementsKinds.IsStringWrapperElementsKind(oldMap.ElementsKind)
                    ? ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS
                    : ElementsKind.DICTIONARY_ELEMENTS;
                newMap.SetElementsKind(newKind);
            }
            MigrateToMap(isolate, obj, newMap);

            if (attrs != PropertyAttributes.NONE)
            {
                if (obj is JSGlobalObject global)
                {
                    ApplyAttributesToDictionary(isolate, global.GlobalDictionary, attrs);
                }
                else
                {
                    ApplyAttributesToDictionary(isolate, obj.PropertyDictionary, attrs);
                }
            }
        }

        if (obj.Map.HasAnyNonextensibleElements)
        {
            Debug.Assert(newElementDictionary is null);
            return true;
        }

        // PreventExtensions works without modifications to typed array elements if
        // the typed array is fixed length; see
        // https://tc39.es/ecma262/#sec-typedarray-preventextensions. Seal and freeze
        // work only if there are no actual elements, because TypedArray elements
        // cannot be reconfigured; see
        // https://tc39.es/ecma262/#sec-typedarray-defineownproperty.
        if (obj.HasTypedArrayOrRabGsabTypedArrayElements)
        {
            if (attrs != PropertyAttributes.NONE && ((JSTypedArray)obj).GetLength() > 0)
            {
                isolate.Throw(isolate.Factory.NewTypeError(attrs == PropertyAttributes.SEALED
                    ? MessageTemplate.CannotSealArrayBufferView
                    : MessageTemplate.CannotFreezeArrayBufferView));
            }
            return true;
        }

        if (newElementDictionary is not null) obj.Elements = newElementDictionary;

        if (!ReferenceEquals(obj.Elements, ReadOnlyRoots.empty_slow_element_dictionary))
        {
            NumberDictionary dictionary = obj.ElementDictionary;
            // Make sure we never go back to the fast case
            obj.RequireSlowElements(dictionary);
            if (attrs != PropertyAttributes.NONE) ApplyAttributesToDictionary(isolate, dictionary, attrs);
        }

        return true;
    }

    /// <summary>JSObject::HasEnumerableElements.</summary>
    public bool HasEnumerableElements()
    {
        switch (GetElementsKind())
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            case ElementsKind.PACKED_ELEMENTS:
            case ElementsKind.PACKED_FROZEN_ELEMENTS:
            case ElementsKind.PACKED_SEALED_ELEMENTS:
            case ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.SHARED_ARRAY_ELEMENTS:
            {
                uint length = this is JSArray array ? (uint)array.Length.Number : (uint)Elements.Length;
                return length > 0;
            }
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            case ElementsKind.HOLEY_FROZEN_ELEMENTS:
            case ElementsKind.HOLEY_SEALED_ELEMENTS:
            case ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.HOLEY_ELEMENTS:
            {
                var elements = (FixedArray)Elements;
                uint length = this is JSArray array ? (uint)array.Length.Number : (uint)elements.Length;
                for (int i = 0; i < length; i++)
                {
                    if (!elements.IsTheHole(i)) return true;
                }
                return false;
            }
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
            {
                uint length = this is JSArray array ? (uint)array.Length.Number : (uint)Elements.Length;
                // Zero-length arrays would use the empty FixedArray...
                if (length == 0) return false;
                // ...so only cast to FixedDoubleArray otherwise.
                var elements = (FixedDoubleArray)Elements;
                for (int i = 0; i < length; i++)
                {
                    if (!elements.IsTheHole(i)) return true;
                }
                return false;
            }
            case ElementsKind.DICTIONARY_ELEMENTS:
                return ((NumberDictionary)Elements).NumberOfEnumerableProperties() > 0;
            case ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS:
            case ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS:
                // We're approximating non-empty arguments objects here.
                return true;
            case ElementsKind.FAST_STRING_WRAPPER_ELEMENTS:
            case ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS:
                if (((JSString)((JSPrimitiveWrapper)this).Value.Object).Length > 0) return true;
                return Elements.Length > 0;
            case ElementsKind.NO_ELEMENTS:
                return false;
            default:
                if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(GetElementsKind()))
                {
                    return ((JSTypedArray)this).GetLength() > 0;
                }
                throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>JSObject::DefineOwnAccessorIgnoreAttributes(isolate, object, name, getter, setter, attributes).</summary>
    public static JSValue DefineOwnAccessorIgnoreAttributes(Isolate isolate, JSObject obj, Name name, JSValue getter, JSValue setter,
        PropertyAttributes attributes)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        return DefineOwnAccessorIgnoreAttributes(ref it, getter, setter, attributes);
    }

    /// <summary>JSObject::DefineOwnAccessorIgnoreAttributes(isolate, LookupIterator*, ...).</summary>
    public static JSValue DefineOwnAccessorIgnoreAttributes(ref LookupIterator it, JSValue getter, JSValue setter,
        PropertyAttributes attributes)
    {
        it.UpdateProtector();

        while (it.State == LookupIterator.StateKind.ACCESS_CHECK) it.Next();

        var obj = (JSObject)it.GetReceiver().Object;
        // Ignore accessors on typed arrays.
        if (it.IsElement() && obj.HasTypedArrayOrRabGsabTypedArrayElements) return JSValue.Undefined;

        it.TransitionToAccessorProperty(getter, setter, attributes);
        return JSValue.Undefined;
    }

    /// <summary>JSObject::SetAccessor: installs an AccessorInfo (a native data-like accessor).</summary>
    public static JSValue SetAccessor(Isolate isolate, JSObject obj, Name name, AccessorInfo info, PropertyAttributes attributes)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);

        while (it.State == LookupIterator.StateKind.ACCESS_CHECK) it.Next();

        // Ignore accessors on typed arrays.
        if (it.IsElement() && obj.HasTypedArrayOrRabGsabTypedArrayElements) return JSValue.Undefined;

        if (!CheckIfCanDefineAsConfigurable(isolate, ref it, info, null)) return JSValue.Undefined;

        it.TransitionToAccessorPair(info, attributes);
        return obj;
    }

    /// <summary>JSObject::CheckIfCanDefineAsConfigurable.</summary>
    public static bool CheckIfCanDefineAsConfigurable(Isolate isolate, ref LookupIterator it, JSValue value, ShouldThrow? shouldThrow)
    {
        if (it.IsFound)
        {
            PropertyAttributes attributes = GetPropertyAttributes(ref it);
            if (attributes != PropertyAttributes.ABSENT)
            {
                if ((attributes & PropertyAttributes.DONT_DELETE) != 0)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.RedefineDisallowed, it.GetName());
                }
                return true;
            }
            // Property does not exist, check object extensibility.
        }
        if (!IsExtensible(isolate, (JSObject)it.GetReceiver().Object))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.DefineDisallowed, it.GetName());
        }
        return true;
    }

    /// <summary>JSObject::SlowReverseLookup: the name of a property holding <paramref name="value"/>, or undefined.</summary>
    public JSValue SlowReverseLookup(JSValue value)
    {
        if (HasFastProperties)
        {
            DescriptorArray descs = Map.InstanceDescriptors;
            bool valueIsNumber = value.IsNumber;
            int nof = Map.NumberOfOwnDescriptors;
            for (int i = 0; i < nof; i++)
            {
                var idx = new InternalIndex(i);
                PropertyDetails details = descs.GetDetails(idx);
                if (details.Location == PropertyLocation.Field)
                {
                    FieldIndex fieldIndex = FieldIndex.ForDetails(Map, details);
                    JSValue property = RawFastPropertyAt(fieldIndex);
                    if (fieldIndex.IsDouble)
                    {
                        if (valueIsNumber && property.IsNumber && property.Number == value.Number) return descs.GetKey(idx);
                    }
                    else if (property.IsIdenticalTo(value))
                    {
                        return descs.GetKey(idx);
                    }
                }
                else if (details.Kind == PropertyKind.Data)
                {
                    if (descs.GetStrongValue(idx).IsIdenticalTo(value)) return descs.GetKey(idx);
                }
            }
            return JSValue.Undefined;
        }
        if (this is JSGlobalObject global)
        {
            GlobalDictionary dict = global.GlobalDictionary;
            for (int i = 0; i < dict.Capacity; i++)
            {
                var entry = new InternalIndex(i);
                if (!dict.ToKey(entry, out _)) continue;
                if (dict.ValueAt(entry).IsIdenticalTo(value)) return dict.NameAt(entry);
            }
            return JSValue.Undefined;
        }
        return PropertyDictionary.SlowReverseLookup(value);
    }

    // ---- Prototypes -----------------------------------------------------------------

    /// <summary>IsJSObjectThatCanBeTrackedAsPrototype (object-predicates-inl.h): no shared-space objects in V8Sharp.</summary>
    public static bool IsJSObjectThatCanBeTrackedAsPrototype(JSValue obj) => obj.HeapObjectOrNull is JSObject;

    public static bool IsJSObjectThatCanBeTrackedAsPrototype(HeapObject? obj) => obj is JSObject;

    /// <summary>IsAnyObjectThatCanBeTrackedAsPrototype (no Wasm objects in V8Sharp).</summary>
    public static bool IsAnyObjectThatCanBeTrackedAsPrototype(HeapObject? obj) => obj is JSObject;

    /// <summary>JSObject::MakePrototypesFast.</summary>
    public static void MakePrototypesFast(JSValue receiver, WhereToStart whereToStart, Isolate isolate)
    {
        if (receiver.HeapObjectOrNull is not JSReceiver r) return;
        for (var iter = new PrototypeIterator(isolate, r, whereToStart); !iter.IsAtEnd; iter.Advance())
        {
            JSReceiver? current = iter.GetCurrent();
            if (!IsJSObjectThatCanBeTrackedAsPrototype(current)) return;
            var currentObj = (JSObject)current!;
            Map currentMap = currentObj.Map;
            if (currentMap.IsPrototypeMap)
            {
                // If the map is already marked as should be fast, we're done. Its
                // prototypes will have been marked already as well.
                if (currentMap.ShouldBeFastPrototypeMap) return;
                Map.SetShouldBeFastPrototypeMap(currentMap, true, isolate);
                OptimizeAsPrototype(isolate, currentObj);
            }
        }
    }

    static bool PrototypeBenefitsFromNormalization(Isolate isolate, JSObject obj)
    {
        if (!obj.HasFastProperties) return false;
        if (obj is JSGlobalProxy) return false;
        // TODO(v8:11248) make bootstrapper create dict mode prototypes, too?
        if (isolate.BootstrapperActive) return false;
        return !obj.Map.IsPrototypeMap || !obj.Map.ShouldBeFastPrototypeMap;
    }

    /// <summary>JSObject::OptimizeAsPrototype.</summary>
    public static void OptimizeAsPrototype(Isolate isolate, JSObject obj, bool enableSetupMode = true)
    {
        if (obj is JSGlobalObject) return;

        if (obj.Map.IsPrototypeMap)
        {
            if (enableSetupMode && PrototypeBenefitsFromNormalization(isolate, obj))
            {
                // First normalize to ensure all JSFunctions are DATA_CONSTANT.
                NormalizeProperties(isolate, obj, PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES, 0, true,
                    "NormalizeAsPrototype");
            }
            if (obj.Map.ShouldBeFastPrototypeMap && !obj.HasFastProperties)
            {
                MigrateSlowToFast(isolate, obj, 0, "OptimizeAsPrototype");
            }
        }
        else
        {
            Map newMap;
            if (enableSetupMode && PrototypeBenefitsFromNormalization(isolate, obj))
            {
                // First normalize to ensure all JSFunctions are DATA_CONSTANT. Don't use
                // the cache, since we're going to use the normalized version directly,
                // without making a copy.
                NormalizeProperties(isolate, obj, PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES, 0, false,
                    "NormalizeAndCopyAsPrototype");
                // A new map was created.
                newMap = obj.Map;
            }
            else
            {
                newMap = Map.Copy(isolate, obj.Map, "CopyAsPrototype");
            }
            newMap.IsPrototypeMap = true;

            // Replace the pointer to the exact constructor with the Object function
            // from the same context if undetectable from JS. This is to avoid keeping
            // memory alive unnecessarily.
            HeapObject? maybeConstructor = newMap.GetConstructorRaw();
            if (maybeConstructor is JSFunction constructor)
            {
                NativeContext context = constructor.NativeContext;
                newMap.SetConstructor(context.ObjectFunction);
            }
            MigrateToMap(isolate, obj, newMap);
        }
    }

    /// <summary>JSObject::ReoptimizeIfPrototype.</summary>
    public static void ReoptimizeIfPrototype(Isolate isolate, JSObject obj)
    {
        Map map = obj.Map;
        if (!map.IsPrototypeMap) return;
        if (!map.ShouldBeFastPrototypeMap) return;
        OptimizeAsPrototype(isolate, obj);
    }

    /// <summary>
    /// JSObject::LazyRegisterPrototypeUser. Deviation: the registry holds the
    /// user maps strongly (V8's WeakArrayList holds them weakly).
    /// </summary>
    public static void LazyRegisterPrototypeUser(Map user, Isolate isolate)
    {
        // Contract: In line with InvalidatePrototypeChains()'s requirements,
        // leaf maps don't need to register as users, only prototypes do.
        Debug.Assert(user.IsPrototypeMap);

        Map currentUser = user;
        PrototypeInfo currentUserInfo = Map.GetOrCreatePrototypeInfo(user, isolate);
        for (var iter = new PrototypeIterator(isolate, user); !iter.IsAtEnd; iter.Advance())
        {
            // Walk up the prototype chain as far as links haven't been registered yet.
            if (currentUserInfo.RegistrySlot != PrototypeInfo.UNREGISTERED) break;
            JSReceiver? maybeProto = iter.GetCurrent();
            // Proxies on the prototype chain are not supported. They make it
            // impossible to make any assumptions about the prototype chain anyway.
            if (!IsAnyObjectThatCanBeTrackedAsPrototype(maybeProto)) continue;
            JSReceiver proto = maybeProto!;
            PrototypeInfo protoInfo = Map.GetOrCreatePrototypeInfo(proto, isolate);
            List<Map?> registry = protoInfo.PrototypeUsers ??= [];
            int slot = registry.IndexOf(null);
            if (slot < 0)
            {
                slot = registry.Count;
                registry.Add(currentUser);
            }
            else
            {
                registry[slot] = currentUser;
            }
            currentUserInfo.RegistrySlot = slot;

            currentUser = proto.Map;
            currentUserInfo = protoInfo;
        }
    }

    /// <summary>
    /// JSObject::UnregisterPrototypeUser: can be called regardless of whether
    /// |user| was actually registered with |prototype|. Returns true when there
    /// was a registration.
    /// </summary>
    public static bool UnregisterPrototypeUser(Map user, Isolate isolate)
    {
        Debug.Assert(user.IsPrototypeMap);
        // If it doesn't have a PrototypeInfo, it was never registered.
        if (!user.HasPrototypeInfo) return false;
        // If it had no prototype before, see if it had users that might expect
        // registration.
        if (!IsAnyObjectThatCanBeTrackedAsPrototype(user.Prototype))
        {
            return user.PrototypeInfo!.PrototypeUsers is not null;
        }
        JSReceiver prototype = user.Prototype!;
        PrototypeInfo userInfo = Map.GetOrCreatePrototypeInfo(user, isolate);
        int slot = userInfo.RegistrySlot;
        if (slot == PrototypeInfo.UNREGISTERED) return false;
        // User knows its registry slot, prototype info and user registry must exist.
        PrototypeInfo protoInfo = prototype.Map.PrototypeInfo!;
        List<Map?> prototypeUsers = protoInfo.PrototypeUsers!;
        prototypeUsers[slot] = null;  // PrototypeUsers::MarkSlotEmpty
        return true;
    }

    // This function must be kept in sync with
    // AccessorAssembler::InvalidateValidityCellIfPrototype() which does pre-checks
    // before jumping here.
    static void InvalidateOnePrototypeValidityCellInternal(Map map)
    {
        Cell? cell = map.PrototypeValidityCell;
        if (cell is not null)
        {
            // Just set the value; the cell will be replaced lazily.
            if (!cell.Value.IsIdenticalTo(Cell.kPrototypeChainInvalid)) cell.Value = Cell.kPrototypeChainInvalid;
        }
        if (map.TryGetPrototypeInfo(out PrototypeInfo prototypeInfo))
        {
            prototypeInfo.PrototypeChainEnumCache = null;
            // Previously created non-existent data handlers might no longer be valid,
            // ensure they are re-created if necessary.
            for (int i = 0; i < prototypeInfo.CachedHandlers.Length; i++) prototypeInfo.CachedHandlers[i] = null;
        }
    }

    static void InvalidatePrototypeChainsInternal(Map? map)
    {
        // We handle linear prototype chains by looping, and multiple children
        // by recursion, in order to reduce the likelihood of running into stack
        // overflows. So, conceptually, the outer loop iterates the depth of the
        // prototype tree, and the inner loop iterates the breadth of a node.
        Map? nextMap;
        for (; map is not null; map = nextMap)
        {
            nextMap = null;
            InvalidateOnePrototypeValidityCellInternal(map);

            if (!map.TryGetPrototypeInfo(out PrototypeInfo protoInfo)) return;
            List<Map?>? prototypeUsers = protoInfo.PrototypeUsers;
            if (prototypeUsers is null) return;
            // For now, only maps register themselves as users.
            for (int i = 0; i < prototypeUsers.Count; ++i)
            {
                Map? user = prototypeUsers[i];
                if (user is null) continue;
                // Walk the prototype chain (backwards, towards leaf objects) if
                // necessary.
                if (nextMap is null)
                {
                    nextMap = user;
                }
                else
                {
                    InvalidatePrototypeChainsInternal(user);
                }
            }
        }
    }

    /// <summary>JSObject::InvalidatePrototypeChains.</summary>
    public static Map InvalidatePrototypeChains(Map map)
    {
        InvalidatePrototypeChainsInternal(map);
        return map;
    }

    // We also invalidate global objects validity cell when a new lexical
    // environment variable is added. This is necessary to ensure that
    // Load/StoreGlobalIC handlers that load/store from global object's prototype
    // get properly invalidated.
    // Note, that the normal Load/StoreICs that load/store through the global object
    // in the prototype chain are not affected by appearance of a new lexical
    // variable and therefore we don't propagate invalidation down.
    /// <summary>JSObject::InvalidatePrototypeValidityCell.</summary>
    public static void InvalidatePrototypeValidityCell(JSGlobalObject global) =>
        InvalidateOnePrototypeValidityCellInternal(global.Map);

    /// <summary>JSObject::SetPrototype.</summary>
    public static bool SetPrototype(Isolate isolate, JSObject obj, JSValue valueObj, bool fromJavaScript, ShouldThrow shouldThrow)
    {
        // Silently ignore the change if value is not a JSReceiver or null.
        // SpiderMonkey behaves this way.
        if (!valueObj.IsJSReceiver && !valueObj.IsNull) return true;
        JSReceiver? value = valueObj.AsOrNull<JSReceiver>();

        bool allExtensible = obj.Map.IsExtensible;
        JSObject realReceiver = obj;
        if (fromJavaScript)
        {
            // Find the first object in the chain whose prototype object is not
            // hidden.
            var iter = new PrototypeIterator(isolate, realReceiver, WhereToStart.StartAtPrototype,
                PrototypeIterator.WhereToEnd.END_AT_NON_HIDDEN);
            while (!iter.IsAtEnd)
            {
                // Casting to JSObject is fine because hidden prototypes are never
                // JSProxies.
                realReceiver = iter.GetCurrent<JSObject>();
                iter.Advance();
                allExtensible = allExtensible && realReceiver.Map.IsExtensible;
            }
        }
        Map map = realReceiver.Map;

        // Nothing to do if prototype is already set.
        if (ReferenceEquals(map.Prototype, value)) return true;

        bool immutableProto = map.IsImmutableProto;
        if (immutableProto)
        {
            JSValue msg = ReferenceEquals(obj, isolate.NativeContext.InitialObjectPrototype)
                ? ReadOnlyRoots.Object_prototype_string
                : obj;
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.ImmutablePrototypeSet, msg);
        }

        // From 6.1.7.3 Invariants of the Essential Internal Methods
        //
        // [[SetPrototypeOf]] ( V )
        // * ...
        // * If target is non-extensible, [[SetPrototypeOf]] must return false,
        //   unless V is the SameValue as the target's observed [[GetPrototypeOf]]
        //   value.
        if (!allExtensible)
        {
            return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.NonExtensibleProto, obj);
        }

        // Before we can set the prototype we need to be sure prototype cycles are
        // prevented.  It is sufficient to validate that the receiver is not in the
        // new prototype chain.
        if (value is not null)
        {
            for (var iter = new PrototypeIterator(isolate, value, WhereToStart.StartAtReceiver); !iter.IsAtEnd; iter.Advance())
            {
                if (ReferenceEquals(iter.GetCurrent(), obj))
                {
                    // Cycle detected.
                    return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.CyclicProto);
                }
            }
        }

        // Set the new prototype of the object.
        isolate.UpdateProtectorsOnSetPrototype(realReceiver, JSValue.FromObject(value));

        Map newMap = new MapUpdater(isolate, map).ApplyPrototypeTransition(value);

        Debug.Assert(ReferenceEquals(newMap.Prototype, value));
        MigrateToMap(isolate, realReceiver, newMap);
        return true;
    }

    /// <summary>JSObject::SetImmutableProto.</summary>
    public static void SetImmutableProto(Isolate isolate, JSObject obj)
    {
        Map map = obj.Map;

        // Nothing to do if prototype is already set.
        if (map.IsImmutableProto) return;

        obj.Map = Map.TransitionToImmutableProto(isolate, map);
    }

    // ---- Elements ---------------------------------------------------------------------

    static bool ShouldConvertToSlowElements(uint usedElements, uint newCapacity)
    {
        uint sizeThreshold = NumberDictionary.kPreferFastElementsSizeFactor *
                             (uint)NumberDictionary.ComputeCapacity((int)usedElements) *
                             NumberDictionary.kEntrySize;
        return sizeThreshold <= newCapacity;
    }

    /// <summary>
    /// ShouldConvertToSlowElements (js-objects-inl.h). V8 allows up to
    /// kMaxUncheckedFastElementsLength for young-generation objects; V8Sharp has
    /// no generations and treats every object as young.
    /// </summary>
    internal static bool ShouldConvertToSlowElements(JSObject obj, uint capacity, uint index, out uint newCapacity)
    {
        if (index < capacity)
        {
            newCapacity = capacity;
            return false;
        }
        if (index - capacity >= kMaxGap)
        {
            newCapacity = 0;
            return true;
        }
        newCapacity = NewElementsCapacity(index + 1);
        Debug.Assert(index < newCapacity);
        if (newCapacity <= kMaxUncheckedOldFastElementsLength || newCapacity <= kMaxUncheckedFastElementsLength)
        {
            return false;
        }
        return ShouldConvertToSlowElements(obj.GetFastElementsUsage(), newCapacity);
    }

    /// <summary>JSObject::WouldConvertToSlowElements.</summary>
    public bool WouldConvertToSlowElements(uint index)
    {
        if (!HasFastElements) return false;
        uint capacity = (uint)Elements.Length;
        return ShouldConvertToSlowElements(this, capacity, index, out _);
    }

    static bool ShouldConvertToFastElements(JSObject obj, NumberDictionary dictionary, uint index, out uint newCapacity)
    {
        newCapacity = 0;
        // If properties with non-standard attributes or accessors were added, we
        // cannot go back to fast elements.
        if (dictionary.RequiresSlowElements) return false;

        // Adding a property with this index will require slow elements.
        if (index >= (uint)JSValue.SmiMaxValue) return false;

        if (obj is JSArray array)
        {
            JSValue length = array.Length;
            if (!length.IsSmi) return false;
            newCapacity = (uint)length.Number;
        }
        else if (obj is JSArgumentsObject)
        {
            return false;
        }
        else
        {
            newCapacity = dictionary.MaxNumberKey + 1;
        }
        newCapacity = Math.Max(index + 1, newCapacity);

        uint dictionarySize = (uint)dictionary.Capacity * NumberDictionary.kEntrySize;

        // Turn fast if the dictionary only saves 50% space.
        return 2 * dictionarySize >= newCapacity;
    }

    static ElementsKind BestFittingFastElementsKind(JSObject obj)
    {
        if (!obj.Map.CanHaveFastTransitionableElementsKind()) return ElementsKind.HOLEY_ELEMENTS;
        if (obj.HasSloppyArgumentsElements) return ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS;
        if (obj.HasStringWrapperElements) return ElementsKind.FAST_STRING_WRAPPER_ELEMENTS;
        Debug.Assert(obj.HasDictionaryElements);
        NumberDictionary dictionary = obj.ElementDictionary;
        ElementsKind kind = ElementsKind.HOLEY_SMI_ELEMENTS;
        for (int i = 0; i < dictionary.Capacity; i++)
        {
            var entry = new InternalIndex(i);
            if (!dictionary.ToKey(entry, out JSValue key)) continue;
            if (key.IsNumber)
            {
                JSValue value = dictionary.ValueAt(entry);
                if (!value.IsNumber) return ElementsKind.HOLEY_ELEMENTS;
                if (!value.IsSmi)
                {
                    if (!Isolate.CurrentFlags.unbox_double_arrays) return ElementsKind.HOLEY_ELEMENTS;
                    kind = ElementsKind.HOLEY_DOUBLE_ELEMENTS;
                }
            }
        }
        return kind;
    }

    /// <summary>JSObject::AddDataElement.</summary>
    public static bool AddDataElement(Isolate isolate, JSObject obj, uint index, JSValue value, PropertyAttributes attributes)
    {
        Debug.Assert(obj.Map.IsExtensible);

        uint oldLength = 0;
        uint newCapacity = 0;

        if (obj is JSArray array)
        {
            if (!ObjectOps.ToArrayLength(array.Length, out oldLength)) throw new InvalidOperationException("invalid array length");
        }

        ElementsKind kind = obj.GetElementsKind();
        FixedArrayBase elements = obj.Elements;
        ElementsKind dictionaryKind = ElementsKind.DICTIONARY_ELEMENTS;
        if (ElementsKinds.IsSloppyArgumentsElementsKind(kind))
        {
            elements = ((SloppyArgumentsElements)elements).Arguments;
            dictionaryKind = ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS;
        }
        else if (ElementsKinds.IsStringWrapperElementsKind(kind))
        {
            dictionaryKind = ElementsKind.SLOW_STRING_WRAPPER_ELEMENTS;
        }

        if (attributes != PropertyAttributes.NONE)
        {
            kind = dictionaryKind;
        }
        else if (elements is NumberDictionary dict)
        {
            kind = ShouldConvertToFastElements(obj, dict, index, out newCapacity)
                ? BestFittingFastElementsKind(obj)
                : dictionaryKind;
        }
        else if (ShouldConvertToSlowElements(obj, (uint)elements.Length, index, out newCapacity))
        {
            kind = dictionaryKind;
        }

        ElementsKind to = ObjectOps.OptimalElementsKind(value);
        if (ElementsKinds.IsHoleyElementsKind(kind) || obj is not JSArray || index > oldLength)
        {
            to = ElementsKinds.GetHoleyElementsKind(to);
            kind = ElementsKinds.GetHoleyElementsKind(kind);
        }
        to = ElementsKinds.GetMoreGeneralElementsKind(kind, to);
        ElementsAccessor accessor = ElementsAccessor.ForKind(to);
        accessor.Add(isolate, obj, index, value, attributes, newCapacity);

        if (obj is JSArray arr && index >= oldLength)
        {
            arr.Length = isolate.Factory.NewNumberFromUint(index + 1);
        }
        return true;
    }

    /// <summary>
    /// JSObject::UpdateAllocationSite. V8 finds the site through the
    /// AllocationMemento behind a young array; V8Sharp has no mementos, so
    /// there is no site to update.
    /// </summary>
    public static bool UpdateAllocationSite(Isolate isolate, JSObject obj, ElementsKind toKind) => false;

    /// <summary>JSObject::TransitionElementsKind.</summary>
    public static void TransitionElementsKind(Isolate isolate, JSObject obj, ElementsKind toKind)
    {
        ElementsKind fromKind = obj.GetElementsKind();

        if (ElementsKinds.IsHoleyElementsKind(fromKind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);

        if (fromKind == toKind) return;

        // This method should never be called for any other case.
        Debug.Assert(ElementsKinds.IsFastElementsKind(fromKind) || ElementsKinds.IsNonextensibleElementsKind(fromKind));
        Debug.Assert(ElementsKinds.IsFastElementsKind(toKind) || ElementsKinds.IsNonextensibleElementsKind(toKind));
        Debug.Assert(fromKind != ElementsKind.TERMINAL_FAST_ELEMENTS_KIND);

        UpdateAllocationSite(isolate, obj, toKind);
        if (ReferenceEquals(obj.Elements, FixedArray.Empty) ||
            ElementsKinds.IsDoubleElementsKind(fromKind) == ElementsKinds.IsDoubleElementsKind(toKind))
        {
            // No change is needed to the elements() buffer, the transition
            // only requires a map change.
            Map newMap = GetElementsTransitionMap(isolate, obj, toKind);
            MigrateToMap(isolate, obj, newMap);
        }
        else
        {
            Debug.Assert((ElementsKinds.IsSmiElementsKind(fromKind) && ElementsKinds.IsDoubleElementsKind(toKind)) ||
                         (ElementsKinds.IsDoubleElementsKind(fromKind) && ElementsKinds.IsObjectElementsKind(toKind)));
            uint c = (uint)obj.Elements.Length;
            ElementsAccessor.ForKind(toKind).GrowCapacityAndConvert(isolate, obj, c);
        }
    }

    static uint HoleyElementsUsage(JSObject obj, FixedArray store)
    {
        uint limit = obj is JSArray array ? (uint)array.Length.Number : (uint)store.Length;
        uint used = 0;
        for (int i = 0; i < limit; ++i)
        {
            if (!store.IsTheHole(i)) ++used;
        }
        return used;
    }

    static uint HoleyElementsUsage(JSObject obj, FixedDoubleArray store)
    {
        uint limit = obj is JSArray array ? (uint)array.Length.Number : (uint)store.Length;
        uint used = 0;
        for (int i = 0; i < limit; ++i)
        {
            if (!store.IsTheHole(i)) ++used;
        }
        return used;
    }

    /// <summary>JSObject::GetFastElementsUsage.</summary>
    public uint GetFastElementsUsage()
    {
        FixedArrayBase store = Elements;
        switch (GetElementsKind())
        {
            case ElementsKind.PACKED_SMI_ELEMENTS:
            case ElementsKind.PACKED_DOUBLE_ELEMENTS:
            case ElementsKind.PACKED_ELEMENTS:
            case ElementsKind.PACKED_FROZEN_ELEMENTS:
            case ElementsKind.PACKED_SEALED_ELEMENTS:
            case ElementsKind.PACKED_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.SHARED_ARRAY_ELEMENTS:
                return this is JSArray array ? (uint)array.Length.Number : (uint)store.Length;
            case ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS:
                return HoleyElementsUsage(this, (FixedArray)((SloppyArgumentsElements)store).Arguments);
            case ElementsKind.HOLEY_SMI_ELEMENTS:
            case ElementsKind.HOLEY_ELEMENTS:
            case ElementsKind.HOLEY_FROZEN_ELEMENTS:
            case ElementsKind.HOLEY_SEALED_ELEMENTS:
            case ElementsKind.HOLEY_NONEXTENSIBLE_ELEMENTS:
            case ElementsKind.FAST_STRING_WRAPPER_ELEMENTS:
                return HoleyElementsUsage(this, (FixedArray)store);
            case ElementsKind.HOLEY_DOUBLE_ELEMENTS:
                if (Elements.Length == 0) return 0;
                return HoleyElementsUsage(this, (FixedDoubleArray)store);
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>JSObject::EnsureWritableFastElements: copies copy-on-write elements.</summary>
    public static void EnsureWritableFastElements(Isolate isolate, JSObject obj)
    {
        Debug.Assert(obj.HasSmiOrObjectElements || obj.HasFastStringWrapperElements || obj.HasAnyNonextensibleElements);
        if (obj.Elements is not FixedArray raw || !raw.IsCowArray) return;
        obj.Elements = raw.CopyAndResize(raw.Length, false);
    }

    /// <summary>JSObject::HasRealNamedProperty.</summary>
    public static bool HasRealNamedProperty(Isolate isolate, JSObject obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        return HasProperty(ref it);
    }

    /// <summary>JSObject::HasRealElementProperty.</summary>
    public static bool HasRealElementProperty(Isolate isolate, JSObject obj, uint index)
    {
        var it = new LookupIterator(isolate, obj, index, obj, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        return HasProperty(ref it);
    }

    /// <summary>JSObject::HasRealNamedCallbackProperty.</summary>
    public static bool HasRealNamedCallbackProperty(Isolate isolate, JSObject obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        GetPropertyAttributes(ref it);
        return it.State == LookupIterator.StateKind.ACCESSOR;
    }

    /// <summary>JSGlobalObject::HasRestrictedGlobalProperty.</summary>
    public static bool HasRestrictedGlobalProperty(Isolate isolate, JSGlobalObject global, Name name)
    {
        var it = new LookupIterator(isolate, global, name, global, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        PropertyAttributes attributes = GetPropertyAttributes(ref it);
        // Global var and function bindings (except those that are introduced by
        // non-strict direct eval) are non-configurable and are therefore restricted
        // global properties.
        return attributes != PropertyAttributes.ABSENT && (attributes & PropertyAttributes.DONT_DELETE) != 0;
    }
}
