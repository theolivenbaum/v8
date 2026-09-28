// Port of the JSReceiver operations of src/objects/js-objects.cc (and the
// JSReceiver parts of objects.cc): [[HasProperty]], [[GetOwnProperty]],
// [[DefineOwnProperty]], [[Delete]], integrity levels, extensibility,
// prototypes, ToPrimitive, constructor names and realms.
//
// Errors are thrown as JavaScriptException (V8 returns Nothing/empty handles);
// operations that V8 declares Maybe<bool> return bool.
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

public abstract partial class JSReceiver
{
    /// <summary>V8's IntegrityLevel.</summary>
    public enum IntegrityLevel { SEALED, FROZEN }

    /// <summary>V8's PropertiesEnumerationMode.</summary>
    public enum PropertiesEnumerationMode { EnumerationOrder, PropertyAdditionOrder }

    /// <summary>JSReceiver::HasProperty(LookupIterator*).</summary>
    public static bool HasProperty(ref LookupIterator it)
    {
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.JSPROXY:
                    return JSProxy.HasProperty(it.Isolate, it.GetHolder<JSProxy>(), it.GetName());
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    // JSObject::GetPropertyAttributesWithFailedAccessCheck.
                    it.Isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                    return false;
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    // TypedArray out-of-bounds access.
                    return false;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                case LookupIterator.StateKind.DATA:
                    return true;
                case LookupIterator.StateKind.NOT_FOUND:
                    return false;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    /// <summary>JSReceiver::HasProperty(isolate, object, name).</summary>
    public static bool HasProperty(Isolate isolate, JSReceiver obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, obj);
        return HasProperty(ref it);
    }

    public static bool HasElement(Isolate isolate, JSReceiver obj, uint index)
    {
        var it = new LookupIterator(isolate, obj, index, obj);
        return HasProperty(ref it);
    }

    /// <summary>JSReceiver::HasOwnProperty.</summary>
    public static bool HasOwnProperty(Isolate isolate, JSReceiver obj, Name name)
    {
        if (obj.InstanceType == InstanceType.JSModuleNamespaceType)
        {
            var desc = new PropertyDescriptor();
            return GetOwnPropertyDescriptor(isolate, obj, name, ref desc);
        }

        if (obj is JSObject)
        {
            // Shortcut.
            var key = new PropertyKey(isolate, name);
            var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN);
            return HasProperty(ref it);
        }

        PropertyAttributes? attributes = GetOwnPropertyAttributes(isolate, obj, name);
        return attributes != PropertyAttributes.ABSENT;
    }

    public static bool HasOwnProperty(Isolate isolate, JSReceiver obj, uint index)
    {
        if (obj is JSObject)
        {
            var it = new LookupIterator(isolate, obj, index, obj, LookupIterator.Configuration.OWN);
            return HasProperty(ref it);
        }
        var it2 = new LookupIterator(isolate, obj, index, obj, LookupIterator.Configuration.OWN);
        return GetPropertyAttributes(ref it2) != PropertyAttributes.ABSENT;
    }

    /// <summary>
    /// JSReceiver::GetDataProperty: the value of a data property without
    /// running JavaScript (accessors and proxies read as undefined; the
    /// engine's own AccessorInfo "special data properties" are evaluated).
    /// </summary>
    public static JSValue GetDataProperty(ref LookupIterator it, bool allowAllocation = true)
    {
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.ACCESS_CHECK:
                    // Support calling this method without an active context, but refuse
                    // access to access-checked objects in that case.
                    if (it.Isolate.Context is not null && it.HasAccess()) continue;
                    it.NotFound();
                    return JSValue.Undefined;
                case LookupIterator.StateKind.JSPROXY:
                    it.NotFound();
                    return JSValue.Undefined;
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.
                case LookupIterator.StateKind.ACCESSOR:
                {
                    // Special handling for AccessorInfo, which behaves like a data
                    // property.
                    if (allowAllocation && it.GetAccessors() is AccessorInfo info && info.HasNoSideEffect)
                    {
                        try
                        {
                            return ObjectOps.GetPropertyWithAccessor(ref it);
                        }
                        catch (JavaScriptException)
                        {
                        }
                    }
                    it.NotFound();
                    return JSValue.Undefined;
                }
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return JSValue.Undefined;
                case LookupIterator.StateKind.DATA:
                    return it.GetDataValue();
                case LookupIterator.StateKind.NOT_FOUND:
                    return JSValue.Undefined;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    /// <summary>JSReceiver::GetDataProperty(isolate, object, name).</summary>
    public static JSValue GetDataProperty(Isolate isolate, JSReceiver obj, Name name, bool allowAllocation = true)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, obj, LookupIterator.Configuration.PROTOTYPE_CHAIN_SKIP_INTERCEPTOR);
        if (!it.IsFound) return JSValue.Undefined;
        return GetDataProperty(ref it, allowAllocation);
    }

    /// <summary>JSReceiver::GetProperty(isolate, receiver, name).</summary>
    public static JSValue GetProperty(Isolate isolate, JSReceiver receiver, Name name) =>
        ObjectOps.GetProperty(isolate, receiver, name);

    public static JSValue GetProperty(Isolate isolate, JSReceiver receiver, string name) =>
        ObjectOps.GetProperty(isolate, receiver, isolate.Factory.InternalizeString(name));

    /// <summary>JSReceiver::GetElement.</summary>
    public static JSValue GetElement(Isolate isolate, JSReceiver receiver, uint index)
    {
        var it = new LookupIterator(isolate, receiver, index, receiver);
        return ObjectOps.GetProperty(ref it);
    }

    /// <summary>JSReceiver::HasInPrototypeChain.</summary>
    public static bool HasInPrototypeChain(Isolate isolate, JSReceiver obj, JSValue proto)
    {
        var iter = new PrototypeIterator(isolate, obj, WhereToStart.StartAtReceiver);
        while (true)
        {
            if (!iter.AdvanceFollowingProxies()) return false;
            if (iter.IsAtEnd) return false;
            if (ReferenceEquals(iter.GetCurrent(), proto.HeapObjectOrNull)) return true;
        }
    }

    /// <summary>JSReceiver::CheckPrivateNameStore: brand/field checks for #private writes and definitions.</summary>
    public static bool CheckPrivateNameStore(ref LookupIterator it, bool isDefine)
    {
        Isolate isolate = it.Isolate;
        var nameString = ((Symbol)it.GetName()).Description.As<JSString>();
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (!it.HasAccess())
                    {
                        isolate.ReportFailedAccessCheck(it.GetReceiver().As<JSObject>());
                        return false;
                    }
                    continue;
                case LookupIterator.StateKind.DATA:
                    if (isDefine)
                    {
                        MessageTemplate message = it.GetName().IsPrivateBrand
                            ? MessageTemplate.InvalidPrivateBrandReinitialization
                            : MessageTemplate.InvalidPrivateFieldReinitialization;
                        return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, null),
                            message, nameString, it.GetReceiver());
                    }
                    return true;
                case LookupIterator.StateKind.WASM_OBJECT:
                    return ObjectOps.ReturnFailure(isolate, ShouldThrow.ThrowOnError, MessageTemplate.WasmObjectsAreOpaque);
                case LookupIterator.StateKind.NOT_FOUND:
                    if (!isDefine)
                    {
                        return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, null),
                            MessageTemplate.InvalidPrivateMemberWrite, nameString, it.GetReceiver());
                    }
                    if (it.ExtendingNonExtensible(it.GetStoreTarget<JSReceiver>()))
                    {
                        return ObjectOps.ReturnFailure(isolate, ShouldThrow.ThrowOnError, MessageTemplate.DefineDisallowed, nameString);
                    }
                    return true;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    static bool HasExcludedProperty(ReadOnlySpan<JSValue> excludedProperties, JSValue searchElement)
    {
        foreach (JSValue obj in excludedProperties)
        {
            if (ObjectOps.SameValue(searchElement, obj)) return true;
        }
        return false;
    }

    static bool? FastAssign(Isolate isolate, JSReceiver target, JSValue source, PropertiesEnumerationMode mode,
        ReadOnlySpan<JSValue> excludedProperties, bool useSet)
    {
        // Non-empty strings are the only non-JSReceivers that need to be handled
        // explicitly by Object.assign.
        if (source.HeapObjectOrNull is not JSReceiver sourceReceiver)
        {
            return source.HeapObjectOrNull is not JSString s || s.Length == 0;
        }

        // If the target is deprecated, the object will be updated on first store. If
        // the source for that store equals the target, this will invalidate the
        // cached representation of the source. Preventively upgrade the target.
        // Do this on each iteration since any property load could cause deprecation.
        if (target.Map.IsDeprecated) JSObject.MigrateInstance(isolate, (JSObject)target);

        Map map = sourceReceiver.Map;
        if (!Map.IsJSObjectMap(map)) return false;
        if (!map.OnlyHasSimpleProperties()) return false;

        var from = (JSObject)sourceReceiver;
        if (from.Elements != FixedArray.Empty && from.Elements.Length != 0) return false;
        // V8's typed arrays have ByteArray elements, never empty_fixed_array.
        if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(map.ElementsKind)) return false;

        bool stable = true;

        // Process symbols last and only do that if we found symbols.
        bool hasSymbol = false;
        bool processSymbolOnly = false;
        while (true)
        {
            int n = map.NumberOfOwnDescriptors;
            for (int i = 0; i < n; i++)
            {
                var index = new InternalIndex(i);
                // The descriptor array is not cached on purpose since it has to stay in
                // sync with map->instance_descriptors to avoid it from being pruned.
                Name nextKey = map.InstanceDescriptors.GetKey(index);
                if (mode == PropertiesEnumerationMode.EnumerationOrder)
                {
                    if (nextKey is Symbol)
                    {
                        hasSymbol = true;
                        if (!processSymbolOnly) continue;
                    }
                    else if (processSymbolOnly)
                    {
                        continue;
                    }
                }
                // CopyDataProperties step 4.c: an excluded key is skipped before its
                // value is read, so an excluded getter is not called
                // (mjsunit/regress/regress-41488094). No element indexes get here, so
                // the exclusion check cannot yield false negatives for type mismatch.
                if (!useSet && excludedProperties.Length != 0 && HasExcludedProperty(excludedProperties, nextKey)) continue;

                JSValue propValue;
                // Directly decode from the descriptor array if |from| did not change
                // shape.
                if (stable)
                {
                    PropertyDetails details = map.InstanceDescriptors.GetDetails(index);
                    if (!details.IsEnumerable) continue;
                    if (details.Kind == PropertyKind.Data)
                    {
                        FieldIndex fieldIndex = FieldIndex.ForDetails(map, details);
                        propValue = JSObject.FastPropertyAt(isolate, from, details.Representation, fieldIndex);
                    }
                    else
                    {
                        var it = new LookupIterator(isolate, from, nextKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                        propValue = ObjectOps.GetProperty(ref it);
                        stable = ReferenceEquals(from.Map, map);
                    }
                }
                else
                {
                    // If the map did change, do a slower lookup. We are still guaranteed
                    // that the object has a simple shape, and that the key is a name.
                    var it = new LookupIterator(isolate, from, nextKey, from, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    if (!it.IsFound) continue;
                    if (!it.IsEnumerable) continue;
                    propValue = ObjectOps.GetProperty(ref it);
                }

                if (useSet)
                {
                    // The lookup will walk the prototype chain, so we have to be careful
                    // to treat any key correctly for any receiver/holder.
                    var key = new PropertyKey(isolate, nextKey);
                    var it = new LookupIterator(isolate, target, key);
                    ObjectOps.SetProperty(ref it, propValue, StoreOrigin.Named, ShouldThrow.ThrowOnError);
                    if (stable) stable = ReferenceEquals(from.Map, map);
                }
                else
                {
                    // 4a ii 2. Perform ? CreateDataProperty(target, nextKey, propValue).
                    CreateDataProperty(isolate, target, nextKey, propValue, ShouldThrow.ThrowOnError);
                }
            }
            if (mode == PropertiesEnumerationMode.EnumerationOrder)
            {
                if (processSymbolOnly || !hasSymbol) return true;
                processSymbolOnly = true;
            }
            else
            {
                return true;
            }
        }
    }

    /// <summary>JSReceiver::SetOrCopyDataProperties (Object.assign, object spread, CopyDataProperties).</summary>
    public static bool SetOrCopyDataProperties(Isolate isolate, JSReceiver target, JSValue source,
        PropertiesEnumerationMode mode, ReadOnlySpan<JSValue> excludedProperties = default, bool useSet = true)
    {
        bool? fastAssign = FastAssign(isolate, target, source, mode, excludedProperties, useSet);
        if (fastAssign == true) return true;

        JSReceiver from = ObjectOps.ToObject(isolate, source);

        // 3b. Let keys be ? from.[[OwnPropertyKeys]]().
        FixedArray keys = KeyAccumulator.GetKeys(isolate, from, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.KeepNumbers);

        if (!from.HasFastProperties && target.HasFastProperties)
        {
            InstanceType targetInstanceType = target.Map.InstanceType;
            if (InstanceTypeChecks.IsJSObject(targetInstanceType) && targetInstanceType != InstanceType.JSGlobalProxyType)
            {
                // Convert to slow properties if we're guaranteed to overflow the number
                // of descriptors.
                int sourceLength = from is JSGlobalObject g
                    ? g.GlobalDictionary.NumberOfEnumerableProperties()
                    : from.PropertyDictionary.NumberOfEnumerableProperties();
                if (sourceLength > Map.kMaxNumberOfDescriptors)
                {
                    JSObject.NormalizeProperties(isolate, (JSObject)target, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES,
                        sourceLength, "Copying data properties");
                }
            }
        }

        // 4. Repeat for each element nextKey of keys in List order,
        int keysLen = keys.Length;
        for (int i = 0; i < keysLen; ++i)
        {
            JSValue nextKey = keys[i];
            if (excludedProperties.Length != 0 && HasExcludedProperty(excludedProperties, nextKey)) continue;

            // 4a i. Let desc be ? from.[[GetOwnProperty]](nextKey).
            var desc = new PropertyDescriptor();
            bool found = GetOwnPropertyDescriptor(isolate, from, nextKey, ref desc);
            // 4a ii. If desc is not undefined and desc.[[Enumerable]] is true, then
            if (found && desc.Enumerable)
            {
                // 4a ii 1. Let propValue be ? Get(from, nextKey).
                JSValue propValue = ObjectOps.GetPropertyOrElement(isolate, from, PropertyKey.FromKey(isolate, nextKey));

                if (useSet)
                {
                    // 4c ii 2. Let status be ? Set(to, nextKey, propValue, true).
                    var key = PropertyKey.FromKey(isolate, nextKey);
                    var it = new LookupIterator(isolate, target, key);
                    ObjectOps.SetProperty(ref it, propValue, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
                }
                else
                {
                    // 4a ii 2. Perform ! CreateDataProperty(target, nextKey, propValue).
                    var key = new PropertyKey(isolate, nextKey);
                    CreateDataProperty(isolate, target, key, propValue, ShouldThrow.ThrowOnError);
                }
            }
        }
        return true;
    }

    /// <summary>JSReceiver::class_name: the builtin tag for Object.prototype.toString fallbacks.</summary>
    public JSString ClassName()
    {
        switch (this)
        {
            case JSFunctionOrBoundFunctionOrWrappedFunction:
                return ReadOnlyRoots.Function_string;
            case JSArgumentsObject:
                return ReadOnlyRoots.Arguments_string;
            case JSArray:
                return ReadOnlyRoots.Array_string;
            case JSArrayBuffer ab:
                return ab.IsShared ? ReadOnlyRoots.SharedArrayBuffer_string : ReadOnlyRoots.ArrayBuffer_string;
            case JSDate:
                return ReadOnlyRoots.Date_string;
            case JSProxy:
                return Map.IsCallable ? ReadOnlyRoots.Function_string : ReadOnlyRoots.Object_string;
            case JSRegExp:
                return ReadOnlyRoots.RegExp_string;
            case JSPrimitiveWrapper wrapper:
            {
                JSValue value = wrapper.Value;
                if (value.IsBoolean) return ReadOnlyRoots.Boolean_string;
                if (value.IsString) return ReadOnlyRoots.String_string;
                if (value.IsNumber) return ReadOnlyRoots.Number_string;
                if (value.IsBigInt) return ReadOnlyRoots.BigInt_string;
                if (value.IsSymbol) return ReadOnlyRoots.Symbol_string;
                return ReadOnlyRoots.Script_string;
            }
        }
        switch (InstanceType)
        {
            case InstanceType.JSArrayIteratorType: return ReadOnlyRoots.ArrayIterator_string;
            case InstanceType.JSErrorType: return ReadOnlyRoots.Error_string;
            case InstanceType.JSGeneratorObjectType:
            case InstanceType.JSAsyncGeneratorObjectType:
            case InstanceType.JSAsyncFunctionObjectType:
                return ReadOnlyRoots.Generator_string;
            case InstanceType.JSMapType: return ReadOnlyRoots.Map_string;
            case InstanceType.JSMapKeyIteratorType:
            case InstanceType.JSMapKeyValueIteratorType:
            case InstanceType.JSMapValueIteratorType:
                return ReadOnlyRoots.MapIterator_string;
            case InstanceType.JSSetType: return ReadOnlyRoots.Set_string;
            case InstanceType.JSSetKeyValueIteratorType:
            case InstanceType.JSSetValueIteratorType:
                return ReadOnlyRoots.SetIterator_string;
            case InstanceType.JSTypedArrayType:
                return JSTypedArray.TypedArrayClassName(Map.ElementsKind);
            case InstanceType.JSWeakMapType: return ReadOnlyRoots.WeakMap_string;
            case InstanceType.JSWeakSetType: return ReadOnlyRoots.WeakSet_string;
        }
        return ReadOnlyRoots.Object_string;
    }

    static (JSFunction? Constructor, JSString Name) GetConstructorHelper(Isolate isolate, JSReceiver receiver)
    {
        // If the object was instantiated simply with base == new.target, the
        // constructor on the map provides the most accurate name.
        // Don't provide the info for prototypes, since their constructors are
        // reclaimed and replaced by Object in OptimizeAsPrototype.
        if (receiver is not JSProxy && receiver.Map.NewTargetIsBase && !receiver.Map.IsPrototypeMap)
        {
            if (receiver.Map.GetConstructor() is JSFunction constructor)
            {
                JSString name = JSFunction.GetDebugName(isolate, constructor, allowAllocation: false);
                if (name.Length != 0 && !JSString.Equals(name, ReadOnlyRoots.Object_string))
                {
                    return (constructor, name);
                }
            }
        }

        for (var it = new PrototypeIterator(isolate, receiver, WhereToStart.StartAtReceiver); !it.IsAtEnd; it.AdvanceIgnoringProxies())
        {
            JSReceiver current = it.GetCurrent()!;

            var itToStringTag = new LookupIterator(isolate, receiver, ReadOnlyRoots.to_string_tag_symbol, current,
                LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
            JSValue maybeToStringTag = GetDataProperty(ref itToStringTag, allowAllocation: false);
            if (maybeToStringTag.HeapObjectOrNull is JSString tag) return (null, tag);

            // Consider the following example:
            //
            //   function A() {}
            //   function B() {}
            //   B.prototype = new A();
            //   B.prototype.constructor = B;
            //
            // The constructor name for `B.prototype` must yield "A", so we don't take
            // "constructor" into account for the receiver itself, but only starting
            // on the prototype chain.
            if (!ReferenceEquals(receiver, current))
            {
                var itConstructor = new LookupIterator(isolate, receiver, ReadOnlyRoots.constructor_string, current,
                    LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                JSValue maybeConstructor = GetDataProperty(ref itConstructor, allowAllocation: false);
                if (maybeConstructor.HeapObjectOrNull is JSFunction constructor)
                {
                    JSString name = SharedFunctionInfo.DebugName(isolate, constructor.Shared);
                    if (name.Length != 0 && !JSString.Equals(name, ReadOnlyRoots.Object_string))
                    {
                        return (constructor, name);
                    }
                }
            }
        }

        return (null, receiver.ClassName());
    }

    /// <summary>JSReceiver::GetConstructor.</summary>
    public static JSFunction? GetConstructor(Isolate isolate, JSReceiver receiver) => GetConstructorHelper(isolate, receiver).Constructor;

    /// <summary>JSReceiver::GetConstructorName.</summary>
    public static JSString GetConstructorName(Isolate isolate, JSReceiver receiver) => GetConstructorHelper(isolate, receiver).Name;

    /// <summary>JSReceiver::GetFunctionRealm.</summary>
    public static NativeContext GetFunctionRealm(Isolate isolate, JSReceiver receiver)
    {
        // This is implemented as a loop because it's possible to construct very
        // long chains of bound functions or proxies where a recursive implementation
        // would run out of stack space.
        JSReceiver current = receiver;
        while (true)
        {
            switch (current)
            {
                case JSProxy proxy:
                    if (proxy.IsRevoked) isolate.ThrowTypeError(MessageTemplate.ProxyRevoked);
                    current = (JSReceiver)proxy.Target.Object;
                    continue;
                case JSFunction function:
                    return function.NativeContext;
                case JSBoundFunction bound:
                    current = bound.BoundTargetFunction;
                    continue;
                case JSWrappedFunction wrapped:
                    current = wrapped.WrappedTargetFunction;
                    continue;
                default:
                    return current.GetCreationContext() ?? isolate.NativeContext;
            }
        }
    }

    /// <summary>JSReceiver::GetContextForMicrotask.</summary>
    public static NativeContext? GetContextForMicrotask(Isolate isolate, JSReceiver receiver)
    {
        while (receiver is JSBoundFunction or JSProxy)
        {
            if (receiver is JSBoundFunction bound)
            {
                receiver = bound.BoundTargetFunction;
            }
            else
            {
                JSReceiver? target = ((JSProxy)receiver).Target.HeapObjectOrNull as JSReceiver;
                if (target is null) return null;
                receiver = target;
            }
        }
        return receiver is JSFunction f ? f.NativeContext : null;
    }

    /// <summary>JSReceiver::GetPropertyAttributes; ABSENT when the property does not exist.</summary>
    public static PropertyAttributes GetPropertyAttributes(ref LookupIterator it)
    {
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.JSPROXY:
                    return JSProxy.GetPropertyAttributes(ref it);
                case LookupIterator.StateKind.WASM_OBJECT:
                    return PropertyAttributes.ABSENT;
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    // JSObject::GetPropertyAttributesWithFailedAccessCheck.
                    it.Isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                    return PropertyAttributes.ABSENT;
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return PropertyAttributes.ABSENT;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                    if (it.GetHolder<JSReceiver>() is JSModuleNamespace)
                    {
                        return JSModuleNamespace.GetPropertyAttributes(ref it);
                    }
                    return it.PropertyAttributes();
                case LookupIterator.StateKind.DATA:
                    return it.PropertyAttributes();
                case LookupIterator.StateKind.NOT_FOUND:
                    return PropertyAttributes.ABSENT;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    public static PropertyAttributes GetPropertyAttributes(Isolate isolate, JSReceiver obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, obj);
        return GetPropertyAttributes(ref it);
    }

    /// <summary>JSReceiver::GetOwnPropertyAttributes.</summary>
    public static PropertyAttributes GetOwnPropertyAttributes(Isolate isolate, JSReceiver obj, Name name)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, obj, key, obj, LookupIterator.Configuration.OWN);
        return GetPropertyAttributes(ref it);
    }

    /// <summary>JSReceiver::DeleteNormalizedProperty.</summary>
    public static void DeleteNormalizedProperty(Isolate isolate, JSReceiver obj, InternalIndex entry)
    {
        if (obj is JSGlobalObject global)
        {
            // If we have a global object, invalidate the cell and remove it from the
            // global object's dictionary.
            GlobalDictionary dictionary = global.GlobalDictionary;
            PropertyCell cell = dictionary.CellAt(entry);
            global.GlobalDictionary = GlobalDictionary.DeleteEntry(isolate, dictionary, entry);
            cell.ClearAndInvalidate(isolate);
        }
        else
        {
            NameDictionary dictionary = obj.PropertyDictionary;
            obj.SetProperties(NameDictionary.DeleteEntry(isolate, dictionary, entry));
        }
        if (obj.Map.IsPrototypeMap)
        {
            // Invalidate prototype validity cell as this may invalidate transitioning
            // store IC handlers.
            JSObject.InvalidatePrototypeChains(obj.Map);
        }
    }

    /// <summary>JSReceiver::DeleteProperty(LookupIterator*, language_mode).</summary>
    public static bool DeleteProperty(ref LookupIterator it, LanguageMode languageMode)
    {
        it.UpdateProtector();

        Isolate isolate = it.Isolate;

        if (it.State == LookupIterator.StateKind.JSPROXY)
        {
            return JSProxy.DeletePropertyOrElement(isolate, it.GetHolder<JSProxy>(), it.GetName(), languageMode);
        }

        if (it.GetReceiver().HeapObjectOrNull is JSProxy)
        {
            if (it.State != LookupIterator.StateKind.NOT_FOUND) it.Delete();
            return true;
        }

        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.WASM_OBJECT:
                    return ObjectOps.ReturnFailure(isolate, ShouldThrow.ThrowOnError, MessageTemplate.WasmObjectsAreOpaque);
                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                    return false;
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    return true;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.DATA:
                case LookupIterator.StateKind.ACCESSOR:
                {
                    JSObject holder = it.GetHolder<JSObject>();
                    if (!it.IsConfigurable || (holder is JSTypedArray && it.IsElement(holder)))
                    {
                        // Fail if the property is not configurable or if the property is a
                        // TypedArray element.
                        ShouldThrow shouldThrow = languageMode == LanguageMode.Sloppy ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError;
                        return ObjectOps.ReturnFailure(isolate, shouldThrow, MessageTemplate.StrictCannotDeleteProperty,
                            it.GetName(), it.GetReceiver());
                    }
                    it.Delete();
                    return true;
                }
                case LookupIterator.StateKind.NOT_FOUND:
                    return true;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    public static bool DeleteElement(Isolate isolate, JSReceiver obj, uint index, LanguageMode languageMode = LanguageMode.Sloppy)
    {
        var it = new LookupIterator(isolate, obj, index, obj, LookupIterator.Configuration.OWN);
        return DeleteProperty(ref it, languageMode);
    }

    public static bool DeleteProperty(Isolate isolate, JSReceiver obj, Name name, LanguageMode languageMode = LanguageMode.Sloppy)
    {
        var it = new LookupIterator(isolate, obj, name, obj, LookupIterator.Configuration.OWN);
        return DeleteProperty(ref it, languageMode);
    }

    public static bool DeletePropertyOrElement(Isolate isolate, JSReceiver obj, Name name, LanguageMode languageMode = LanguageMode.Sloppy) =>
        DeletePropertyOrElement(isolate, obj, new PropertyKey(isolate, name), languageMode);

    public static bool DeletePropertyOrElement(Isolate isolate, JSReceiver obj, in PropertyKey key, LanguageMode languageMode = LanguageMode.Sloppy)
    {
        var it = new LookupIterator(isolate, obj, key, obj, LookupIterator.Configuration.OWN);
        return DeleteProperty(ref it, languageMode);
    }

    /// <summary>ES6 19.1.2.4 Object.defineProperty (JSReceiver::DefineProperty).</summary>
    public static JSValue DefineProperty(Isolate isolate, JSValue obj, JSValue key, JSValue attributes)
    {
        // 1. If Type(O) is not Object, throw a TypeError exception.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject,
                isolate.Factory.InternalizeString("Object.defineProperty"));
        }
        // 2. Let key be ToPropertyKey(P).
        key = ObjectOps.ToPropertyKey(isolate, key);
        // 4. Let desc be ToPropertyDescriptor(Attributes).
        var desc = new PropertyDescriptor();
        PropertyDescriptor.ToPropertyDescriptor(isolate, attributes, ref desc);
        // 6. Let success be DefinePropertyOrThrow(O,key, desc).
        DefineOwnProperty(isolate, receiver, key, ref desc, ShouldThrow.ThrowOnError);
        // 8. Return O.
        return obj;
    }

    /// <summary>ES6 19.1.2.3.1 ObjectDefineProperties.</summary>
    public static JSValue DefineProperties(Isolate isolate, JSValue obj, JSValue properties)
    {
        // 1. If Type(O) is not Object, throw a TypeError exception.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject,
                isolate.Factory.InternalizeString("Object.defineProperties"));
        }
        // 2. Let props be ToObject(Properties).
        JSReceiver props = ObjectOps.ToObject(isolate, properties);

        // 4. Let keys be props.[[OwnPropertyKeys]]().
        FixedArray keys = KeyAccumulator.GetKeys(isolate, props, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES);
        // 6. Let descriptors be an empty List.
        int capacity = keys.Length;
        var descriptors = new PropertyDescriptor[capacity];
        int descriptorsIndex = 0;
        // 7. Repeat for each element nextKey of keys in List order,
        for (int i = 0; i < capacity; ++i)
        {
            JSValue nextKey = keys[i];
            // 7a. Let propDesc be props.[[GetOwnProperty]](nextKey).
            var key = new PropertyKey(isolate, nextKey);
            var it = new LookupIterator(isolate, props, key, LookupIterator.Configuration.OWN);
            PropertyAttributes attrs = GetPropertyAttributes(ref it);
            // 7c. If propDesc is not undefined and propDesc.[[Enumerable]] is true:
            if (attrs == PropertyAttributes.ABSENT) continue;
            if ((attrs & PropertyAttributes.DONT_ENUM) != 0) continue;
            // 7c i. Let descObj be Get(props, nextKey).
            JSValue descObj = ObjectOps.GetProperty(ref it);
            // 7c iii. Let desc be ToPropertyDescriptor(descObj).
            PropertyDescriptor.ToPropertyDescriptor(isolate, descObj, ref descriptors[descriptorsIndex]);
            // 7c v. Append the pair (a two element List) consisting of nextKey and
            //       desc to the end of descriptors.
            descriptors[descriptorsIndex].SetName(nextKey);
            descriptorsIndex++;
        }
        // 8. For each pair from descriptors in list order,
        for (int i = 0; i < descriptorsIndex; ++i)
        {
            // 8c. Let status be DefinePropertyOrThrow(O, P, desc).
            DefineOwnProperty(isolate, receiver, descriptors[i].Name, ref descriptors[i], ShouldThrow.ThrowOnError);
        }
        // 9. Return o.
        return obj;
    }

    /// <summary>JSReceiver::DefineOwnProperty: dispatches to the exotic [[DefineOwnProperty]] of the object.</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSReceiver obj, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        switch (obj)
        {
            case JSArray array:
                return JSArray.DefineOwnProperty(isolate, array, key, ref desc, shouldThrow);
            case JSProxy proxy:
                return JSProxy.DefineOwnProperty(isolate, proxy, key, ref desc, shouldThrow);
            case JSTypedArray typedArray:
                return JSTypedArray.DefineOwnProperty(isolate, typedArray, key, ref desc, shouldThrow);
            case JSModuleNamespace ns:
                return JSModuleNamespace.DefineOwnProperty(isolate, ns, key, ref desc, shouldThrow);
        }
        // OrdinaryDefineOwnProperty, by virtue of calling
        // DefineOwnPropertyIgnoreAttributes, can handle arguments
        // (https://tc39.es/ecma262/#sec-arguments-exotic-objects-defineownproperty-p-desc).
        return OrdinaryDefineOwnProperty(isolate, (JSObject)obj, key, ref desc, shouldThrow);
    }

    public static bool DefineOwnProperty(Isolate isolate, JSReceiver obj, Name key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow) =>
        DefineOwnProperty(isolate, obj, (JSValue)key, ref desc, shouldThrow);

    /// <summary>JSReceiver::OrdinaryDefineOwnProperty(key).</summary>
    public static bool OrdinaryDefineOwnProperty(Isolate isolate, JSObject obj, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        var lookupKey = new PropertyKey(isolate, key);
        return OrdinaryDefineOwnProperty(isolate, obj, lookupKey, ref desc, shouldThrow);
    }

    /// <summary>ES6 9.1.6.1 OrdinaryDefineOwnProperty.</summary>
    public static bool OrdinaryDefineOwnProperty(Isolate isolate, JSObject obj, in PropertyKey key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        var it = new LookupIterator(isolate, obj, key, LookupIterator.Configuration.OWN);

        // Deal with access checks first.
        if (it.State == LookupIterator.StateKind.ACCESS_CHECK)
        {
            if (!it.HasAccess()) isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
            it.Next();
        }

        // 1. Let current be O.[[GetOwnProperty]](P).
        var current = new PropertyDescriptor();
        GetOwnPropertyDescriptor(ref it, ref current);

        // TODO(jkummerow/verwaest): It would be nice if we didn't have to reset
        // the iterator every time. Currently, the reasons why we need it are because
        // GetOwnPropertyDescriptor can have side effects, namely:
        // - Interceptors
        // - Accessors (which might change the holder's map)
        it.Restart();

        // Skip over the access check after restarting -- we've already checked it.
        while (it.State == LookupIterator.StateKind.ACCESS_CHECK) it.Next();

        // 3. Let extensible be the value of the [[Extensible]] internal slot of O.
        bool extensible = JSObject.IsExtensible(isolate, obj);

        return ValidateAndApplyPropertyDescriptor(isolate, ref it, true, extensible, ref desc, ref current, shouldThrow, null);
    }

    /// <summary>ES6 9.1.6.2 IsCompatiblePropertyDescriptor.</summary>
    public static bool IsCompatiblePropertyDescriptor(Isolate isolate, bool extensible, ref PropertyDescriptor desc,
        ref PropertyDescriptor current, Name propertyName, ShouldThrow? shouldThrow)
    {
        // 1. Return ValidateAndApplyPropertyDescriptor(undefined, undefined,
        //    Extensible, Desc, Current).
        LookupIterator none = default;
        return ValidateAndApplyPropertyDescriptor(isolate, ref none, false, extensible, ref desc, ref current, shouldThrow, propertyName);
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-validateandapplypropertydescriptor.
    /// <paramref name="hasIt"/> is V8's it != nullptr.
    /// </summary>
    public static bool ValidateAndApplyPropertyDescriptor(Isolate isolate, ref LookupIterator it, bool hasIt, bool extensible,
        ref PropertyDescriptor desc, ref PropertyDescriptor current, ShouldThrow? shouldThrow, Name? propertyName)
    {
        bool descIsDataDescriptor = PropertyDescriptor.IsDataDescriptor(desc);
        bool descIsAccessorDescriptor = PropertyDescriptor.IsAccessorDescriptor(desc);
        bool descIsGenericDescriptor = PropertyDescriptor.IsGenericDescriptor(desc);
        // 2. If current is undefined, then
        if (current.IsEmpty)
        {
            // 2a. If extensible is false, return false.
            if (!extensible)
            {
                return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.DefineDisallowed,
                    hasIt ? it.GetName() : propertyName!);
            }
            // 2c. If IsGenericDescriptor(Desc) or IsDataDescriptor(Desc) is true, then:
            // (This is equivalent to !IsAccessorDescriptor(desc).)
            if (!descIsAccessorDescriptor)
            {
                // 2c i. If O is not undefined, create an own data property named P of
                // object O whose [[Value]], [[Writable]], [[Enumerable]] and
                // [[Configurable]] attribute values are described by Desc. If the value
                // of an attribute field of Desc is absent, the attribute of the newly
                // created property is set to its default value.
                if (hasIt)
                {
                    if (!desc.HasWritable) desc.SetWritable(false);
                    if (!desc.HasEnumerable) desc.SetEnumerable(false);
                    if (!desc.HasConfigurable) desc.SetConfigurable(false);
                    JSValue value = desc.HasValue ? desc.Value : JSValue.Undefined;
                    JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, desc.ToAttributes());
                }
            }
            else
            {
                // 2d. Else Desc must be an accessor Property Descriptor,
                // 2d i. If O is not undefined, create an own accessor property named P
                // of object O whose [[Get]], [[Set]], [[Enumerable]] and
                // [[Configurable]] attribute values are described by Desc. If the value
                // of an attribute field of Desc is absent, the attribute of the newly
                // created property is set to its default value.
                if (hasIt)
                {
                    if (!desc.HasEnumerable) desc.SetEnumerable(false);
                    if (!desc.HasConfigurable) desc.SetConfigurable(false);
                    JSValue getter = desc.HasGet ? desc.Get : JSValue.Null;
                    JSValue setter = desc.HasSet ? desc.Set : JSValue.Null;
                    JSObject.DefineOwnAccessorIgnoreAttributes(ref it, getter, setter, desc.ToAttributes());
                }
            }
            // 2e. Return true.
            return true;
        }
        // 3. If every field in Desc is absent, return true. (This also has a shortcut
        // not in the spec: if every field value matches the current value, return.)
        if ((!desc.HasEnumerable || desc.Enumerable == current.Enumerable) &&
            (!desc.HasConfigurable || desc.Configurable == current.Configurable) &&
            !desc.HasValue &&
            (!desc.HasWritable || (current.HasWritable && current.Writable == desc.Writable)) &&
            (!desc.HasGet || (current.HasGet && ObjectOps.SameValue(current.Get, desc.Get))) &&
            (!desc.HasSet || (current.HasSet && ObjectOps.SameValue(current.Set, desc.Set))))
        {
            return true;
        }
        // 4. If current.[[Configurable]] is false, then
        if (!current.Configurable)
        {
            // 4a. If Desc.[[Configurable]] is present and its value is true, return
            // false.
            if (desc.HasConfigurable && desc.Configurable)
            {
                return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                    hasIt ? it.GetName() : propertyName!);
            }
            // 4b. If Desc.[[Enumerable]] is present and
            // ! SameValue(Desc.[[Enumerable]], current.[[Enumerable]]) is false, return
            // false.
            if (desc.HasEnumerable && desc.Enumerable != current.Enumerable)
            {
                return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                    hasIt ? it.GetName() : propertyName!);
            }
        }

        bool currentIsDataDescriptor = PropertyDescriptor.IsDataDescriptor(current);
        // 5. If ! IsGenericDescriptor(Desc) is true, no further validation is
        // required.
        if (descIsGenericDescriptor)
        {
            // Nothing to see here.
        }
        else if (currentIsDataDescriptor != descIsDataDescriptor)
        {
            // 6. Else if ! SameValue(!IsDataDescriptor(current),
            // !IsDataDescriptor(Desc)) is false, the
            // 6a. If current.[[Configurable]] is false, return false.
            if (!current.Configurable)
            {
                return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                    hasIt ? it.GetName() : propertyName!);
            }
            // 6b/6c: the conversion is folded into step 9.
        }
        else if (currentIsDataDescriptor && descIsDataDescriptor)
        {
            // 7. Else if IsDataDescriptor(current) and IsDataDescriptor(Desc) are both
            // true, then:
            // 7a. If current.[[Configurable]] is false and current.[[Writable]] is
            // false, then
            if (!current.Configurable && !current.Writable)
            {
                // 7a i. If Desc.[[Writable]] is present and Desc.[[Writable]] is true,
                // return false.
                if (desc.HasWritable && desc.Writable)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                        hasIt ? it.GetName() : propertyName!);
                }
                // 7a ii. If Desc.[[Value]] is present and SameValue(Desc.[[Value]],
                // current.[[Value]]) is false, return false.
                if (desc.HasValue)
                {
                    // We'll succeed applying the property, but the value is already the
                    // same and the property is read-only, so skip actually writing the
                    // property. Otherwise we may try to e.g., write to frozen elements.
                    if (ObjectOps.SameValue(desc.Value, current.Value)) return true;
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                        hasIt ? it.GetName() : propertyName!);
                }
            }
        }
        else
        {
            // 8. Else,
            // 8b. If current.[[Configurable]] is false, then:
            if (!current.Configurable)
            {
                // 8a i. If Desc.[[Set]] is present and SameValue(Desc.[[Set]],
                // current.[[Set]]) is false, return false.
                if (desc.HasSet && !ObjectOps.SameValue(desc.Set, current.Set))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                        hasIt ? it.GetName() : propertyName!);
                }
                // 8a ii. If Desc.[[Get]] is present and SameValue(Desc.[[Get]],
                // current.[[Get]]) is false, return false.
                if (desc.HasGet && !ObjectOps.SameValue(desc.Get, current.Get))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow), MessageTemplate.RedefineDisallowed,
                        hasIt ? it.GetName() : propertyName!);
                }
            }
        }

        // 9. If O is not undefined, then:
        if (hasIt)
        {
            // 9a. For each field of Desc that is present, set the corresponding
            // attribute of the property named P of object O to the value of the field.
            PropertyAttributes attrs = PropertyAttributes.NONE;
            if (desc.HasEnumerable) attrs |= desc.Enumerable ? PropertyAttributes.NONE : PropertyAttributes.DONT_ENUM;
            else attrs |= current.Enumerable ? PropertyAttributes.NONE : PropertyAttributes.DONT_ENUM;
            if (desc.HasConfigurable) attrs |= desc.Configurable ? PropertyAttributes.NONE : PropertyAttributes.DONT_DELETE;
            else attrs |= current.Configurable ? PropertyAttributes.NONE : PropertyAttributes.DONT_DELETE;

            if (descIsDataDescriptor || (descIsGenericDescriptor && currentIsDataDescriptor))
            {
                if (desc.HasWritable) attrs |= desc.Writable ? PropertyAttributes.NONE : PropertyAttributes.READ_ONLY;
                else attrs |= current.Writable ? PropertyAttributes.NONE : PropertyAttributes.READ_ONLY;
                JSValue value = desc.HasValue ? desc.Value : current.HasValue ? current.Value : JSValue.Undefined;
                return JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, attrs, shouldThrow,
                    JSObject.AccessorInfoHandling.DONT_FORCE_FIELD, EnforceDefineSemantics.Set, StoreOrigin.Named,
                    current.HasValue ? current.Value : (JSValue?)null);
            }
            else
            {
                JSValue getter = desc.HasGet ? desc.Get : current.HasGet ? current.Get : JSValue.Null;
                JSValue setter = desc.HasSet ? desc.Set : current.HasSet ? current.Set : JSValue.Null;
                JSObject.DefineOwnAccessorIgnoreAttributes(ref it, getter, setter, attrs);
            }
        }

        // 10. Return true.
        return true;
    }

    /// <summary>JSReceiver::CreateDataProperty(name).</summary>
    public static bool CreateDataProperty(Isolate isolate, JSReceiver obj, Name key, JSValue value, ShouldThrow? shouldThrow) =>
        CreateDataProperty(isolate, obj, new PropertyKey(isolate, key), value, shouldThrow);

    /// <summary>JSReceiver::CreateDataProperty(JSAny, key): fails for primitives.</summary>
    public static bool CreateDataProperty(Isolate isolate, JSValue obj, in PropertyKey key, JSValue value, ShouldThrow? shouldThrow)
    {
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            return ObjectOps.CannotCreateProperty(isolate, obj, key.GetName(isolate), shouldThrow);
        }
        return CreateDataProperty(isolate, receiver, key, value, shouldThrow);
    }

    /// <summary>JSReceiver::CreateDataProperty(key).</summary>
    public static bool CreateDataProperty(Isolate isolate, JSReceiver obj, in PropertyKey key, JSValue value, ShouldThrow? shouldThrow)
    {
        if (obj is JSObject jsObject)
        {
            return JSObject.CreateDataProperty(isolate, jsObject, key, value, shouldThrow);  // Shortcut.
        }

        var newDesc = new PropertyDescriptor();
        newDesc.SetValue(value);
        newDesc.SetWritable(true);
        newDesc.SetEnumerable(true);
        newDesc.SetConfigurable(true);
        return DefineOwnProperty(isolate, obj, key.GetName(isolate), ref newDesc, shouldThrow);
    }

    /// <summary>JSReceiver::AddPrivateField.</summary>
    public static bool AddPrivateField(ref LookupIterator it, JSValue value, ShouldThrow? shouldThrow)
    {
        var receiver = it.GetReceiver().As<JSReceiver>();
        Isolate isolate = it.Isolate;
        var symbol = (Symbol)it.GetName();

        switch (it.State)
        {
            case LookupIterator.StateKind.JSPROXY:
            {
                var newDesc = new PropertyDescriptor();
                newDesc.SetValue(value);
                newDesc.SetWritable(true);
                newDesc.SetEnumerable(true);
                newDesc.SetConfigurable(true);
                return JSProxy.SetPrivateSymbol(isolate, (JSProxy)receiver, symbol, ref newDesc, shouldThrow);
            }
            case LookupIterator.StateKind.WASM_OBJECT:
                return ObjectOps.ReturnFailure(isolate, ShouldThrow.ThrowOnError, MessageTemplate.WasmObjectsAreOpaque);
            case LookupIterator.StateKind.ACCESS_CHECK:
            case LookupIterator.StateKind.TRANSITION:
            case LookupIterator.StateKind.NOT_FOUND:
                break;
            default:
                throw new InvalidOperationException("unreachable");
        }
        return ObjectOps.TransitionAndWriteDataProperty(ref it, value, PropertyAttributes.NONE, shouldThrow, StoreOrigin.MaybeKeyed);
    }

    /// <summary>JSReceiver::GetOwnPropertyDescriptor(object, key).</summary>
    public static bool GetOwnPropertyDescriptor(Isolate isolate, JSReceiver obj, JSValue key, ref PropertyDescriptor desc)
    {
        var lookupKey = new PropertyKey(isolate, key);
        var it = new LookupIterator(isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
        return GetOwnPropertyDescriptor(ref it, ref desc);
    }

    public static bool GetOwnPropertyDescriptor(Isolate isolate, JSReceiver obj, Name key, ref PropertyDescriptor desc) =>
        GetOwnPropertyDescriptor(isolate, obj, (JSValue)key, ref desc);

    /// <summary>
    /// ES6 9.1.5.1 [[GetOwnProperty]]: true on success, false if the property
    /// didn't exist; exceptions propagate.
    /// </summary>
    public static bool GetOwnPropertyDescriptor(ref LookupIterator it, ref PropertyDescriptor desc)
    {
        Isolate isolate = it.Isolate;
        // "Virtual" dispatch.
        if (it.IsFound && it.GetHolder<JSReceiver>() is JSProxy proxy)
        {
            return JSProxy.GetOwnPropertyDescriptor(isolate, proxy, it.GetName(), ref desc);
        }

        // Request to deal with access checks through GetPropertyAttributes if needed.
        if (it.State == LookupIterator.StateKind.ACCESS_CHECK)
        {
            if (!it.HasAccess()) it.Isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
            it.Next();
        }

        // 2. If O does not have an own property with key P, return undefined.
        PropertyAttributes attrs = JSObject.GetPropertyAttributes(ref it);
        if (attrs == PropertyAttributes.ABSENT) return false;

        // 5. If X is a data property, then
        bool isAccessorPair = it.State == LookupIterator.StateKind.ACCESSOR && it.GetAccessors() is AccessorPair;
        if (!isAccessorPair)
        {
            // 5a. Set D.[[Value]] to the value of X's [[Value]] attribute.
            JSValue value = ObjectOps.GetProperty(ref it);
            desc.SetValue(value);
            // 5b. Set D.[[Writable]] to the value of X's [[Writable]] attribute
            desc.SetWritable((attrs & PropertyAttributes.READ_ONLY) == 0);
        }
        else
        {
            // 6. Else X is an accessor property, so
            var accessors = (AccessorPair)it.GetAccessors();
            // 6a. Set D.[[Get]] to the value of X's [[Get]] attribute.
            desc.SetGet(AccessorPair.GetComponent(accessors, AccessorComponent.ACCESSOR_GETTER));
            // 6b. Set D.[[Set]] to the value of X's [[Set]] attribute.
            desc.SetSet(AccessorPair.GetComponent(accessors, AccessorComponent.ACCESSOR_SETTER));
        }

        // 7. Set D.[[Enumerable]] to the value of X's [[Enumerable]] attribute.
        desc.SetEnumerable((attrs & PropertyAttributes.DONT_ENUM) == 0);
        // 8. Set D.[[Configurable]] to the value of X's [[Configurable]] attribute.
        desc.SetConfigurable((attrs & PropertyAttributes.DONT_DELETE) == 0);
        // 9. Return D.
        return true;
    }
}
