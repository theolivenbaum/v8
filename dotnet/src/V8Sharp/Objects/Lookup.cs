// Port of src/objects/lookup.{h,cc,-inl.h}: PropertyKey and LookupIterator,
// the single path for property access semantics (own and prototype-chain
// lookup, accessors, proxies, elements, typed arrays, global cells).
//
// LookupIterator is a mutable struct; pass it by ref (V8 passes a pointer).
// Interceptors are not ported (there is no embedder API that installs them);
// the INTERCEPTOR state and the interceptor bookkeeping are kept so the
// structure matches V8, but no map reports an interceptor.
using System.Runtime.CompilerServices;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>
/// V8's PropertyKey: a property name, or an integer index (elements).
/// Integer-index keys are elements, never named properties.
/// </summary>
public readonly struct PropertyKey
{
    public const ulong kInvalidIndex = ulong.MaxValue;

    readonly Name? _name;
    readonly ulong _index;

    /// <summary>PropertyKey(isolate, double index): an integer index.</summary>
    public PropertyKey(Isolate isolate, double index)
    {
        _index = (ulong)index;
        _name = null;
    }

    /// <summary>PropertyKey(isolate, name): an element key if the name is an integer index, else the internalized name.</summary>
    public PropertyKey(Isolate isolate, Name name)
    {
        if (name.AsIntegerIndex(out ulong index))
        {
            _index = index;
            _name = name;
        }
        else
        {
            _index = kInvalidIndex;
            _name = isolate.Factory.InternalizeName(name);
        }
    }

    /// <summary>PropertyKey(isolate, valid_key): a Name or a Number key.</summary>
    public PropertyKey(Isolate isolate, JSValue validKey)
    {
        if (ObjectOps.ToIntegerIndex(validKey, out ulong index))
        {
            _index = index;
            _name = null;
            return;
        }
        Name name = validKey.IsNumber ? isolate.Factory.NumberToString(validKey) : validKey.As<Name>();
        if (name.AsIntegerIndex(out index))
        {
            _index = index;
            _name = name;
        }
        else
        {
            _index = kInvalidIndex;
            _name = isolate.Factory.InternalizeName(name);
        }
    }

    internal PropertyKey(Name? name, ulong index)
    {
        _name = name;
        _index = index;
    }

    /// <summary>
    /// PropertyKey(isolate, key, &amp;success): converts an arbitrary key with
    /// ToName (which can throw). V8 reports failure through success; here the
    /// exception propagates.
    /// </summary>
    public static PropertyKey FromKey(Isolate isolate, JSValue key)
    {
        if (ObjectOps.ToIntegerIndex(key, out ulong index)) return new PropertyKey(null, index);
        Name name = ObjectOps.ToName(isolate, key);
        if (name.AsIntegerIndex(out index)) return new PropertyKey(name, index);
        return new PropertyKey(isolate.Factory.InternalizeName(name), kInvalidIndex);
    }

    public bool IsElement => _index != kInvalidIndex;

    /// <summary>The name; null for element keys created from numbers.</summary>
    public Name? Name => _name;

    public ulong Index => _index;

    /// <summary>PropertyKey::GetName: the name, materializing it for element keys.</summary>
    public Name GetName(Isolate isolate) => _name ?? isolate.Factory.SizeToString(_index);
}

/// <summary>V8's LookupIterator.</summary>
public struct LookupIterator
{
    [Flags]
    public enum Configuration
    {
        kInterceptor = 1 << 0,
        kPrototypeChain = 1 << 1,

        // Convenience combinations of bits.
        OWN_SKIP_INTERCEPTOR = 0,
        OWN = kInterceptor,
        PROTOTYPE_CHAIN_SKIP_INTERCEPTOR = kPrototypeChain,
        PROTOTYPE_CHAIN = kPrototypeChain | kInterceptor,
        DEFAULT = PROTOTYPE_CHAIN,
    }

    /// <summary>LookupIterator::State.</summary>
    public enum StateKind
    {
        NOT_FOUND,
        STRING_LOOKUP_START_OBJECT,
        TYPED_ARRAY_INDEX_NOT_FOUND,
        ACCESS_CHECK,
        INTERCEPTOR,
        JSPROXY,
        ACCESSOR,
        DATA,
        WASM_OBJECT,
        MODULE_NAMESPACE,
        TRANSITION,
        BEFORE_PROPERTY = INTERCEPTOR,
    }

    enum InterceptorState
    {
        kUninitialized,
        kSkipNonMasking,
        kSkipNonMaskingOwnProperty,
        kProcessNonMasking,
    }

    public const ulong kInvalidIndex = PropertyKey.kInvalidIndex;

    readonly Configuration _configuration;
    StateKind _state;
    bool _hasProperty;

    /// <summary>has_property_: whether the current state found a property (used by DCHECKs in V8).</summary>
    internal readonly bool HasProperty => _hasProperty;
    InterceptorState _interceptorState;
    PropertyDetails _propertyDetails;
    readonly Isolate _isolate;
    Name? _name;
    HeapObject? _transition;  // Map or PropertyCell
    readonly JSValue _receiver;
    JSReceiver? _holder;
    readonly JSValue _lookupStartObject;
    readonly ulong _index;
    InternalIndex _number;

    public LookupIterator(Isolate isolate, JSValue receiver, Name name, Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, name, kInvalidIndex, receiver, configuration) { }

    public LookupIterator(Isolate isolate, JSValue receiver, Name name, JSValue lookupStartObject,
        Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, name, kInvalidIndex, lookupStartObject, configuration) { }

    public LookupIterator(Isolate isolate, JSValue receiver, ulong index, Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, null, index, receiver, configuration) { }

    public LookupIterator(Isolate isolate, JSValue receiver, ulong index, JSValue lookupStartObject,
        Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, null, index, lookupStartObject, configuration) { }

    public LookupIterator(Isolate isolate, JSValue receiver, in PropertyKey key, Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, key.Name, key.Index, receiver, configuration) { }

    public LookupIterator(Isolate isolate, JSValue receiver, in PropertyKey key, JSValue lookupStartObject,
        Configuration configuration = Configuration.DEFAULT)
        : this(isolate, receiver, key.Name, key.Index, lookupStartObject, configuration) { }

    LookupIterator(Isolate isolate, JSValue receiver, Name? name, ulong index, JSValue lookupStartObject,
        Configuration configuration)
    {
        _configuration = ComputeConfiguration(configuration, index, name);
        _state = StateKind.NOT_FOUND;
        _hasProperty = false;
        _interceptorState = InterceptorState.kUninitialized;
        _propertyDetails = PropertyDetails.Empty();
        _isolate = isolate;
        _name = name;
        _transition = null;
        _receiver = receiver;
        _holder = null;
        _lookupStartObject = lookupStartObject;
        _index = index;
        _number = InternalIndex.NotFound;

        if (IsElement())
        {
            if (_index > JSObject.kMaxElementIndex && lookupStartObject.HeapObjectOrNull is not JSTypedArray)
            {
                _name ??= isolate.Factory.SizeToString(_index);
                _name = isolate.Factory.InternalizeName(_name);
            }
            else if (_name is not null && !(_name is JSString s && s.IsInternalized))
            {
                _name = null;
            }
            Start(true);
        }
        else
        {
            _name = isolate.Factory.InternalizeName(_name!);
            Start(false);
        }
    }

    static Configuration ComputeConfiguration(Configuration configuration, ulong index, Name? name)
    {
        if (index != kInvalidIndex) return configuration;
        return name!.IsAnyPrivate ? Configuration.OWN_SKIP_INTERCEPTOR : configuration;
    }

    // --- accessors ----------------------------------------------------------------

    public readonly Isolate Isolate => _isolate;
    public readonly StateKind State => _state;
    public readonly bool IsFound => _state != StateKind.NOT_FOUND;
    public readonly ulong Index => _index;
    public readonly uint ArrayIndex => (uint)_index;
    public readonly bool IsElement() => _index != kInvalidIndex;

    /// <summary>LookupIterator::IsElement(object): typed arrays treat all integer indices as elements.</summary>
    public readonly bool IsElement(JSReceiver obj) =>
        _index <= JSObject.kMaxElementIndex ||
        (_index != kInvalidIndex && obj.Map.HasTypedArrayOrRabGsabTypedArrayElements);

    public readonly bool IsAnyPrivateName() => !IsElement() && Name.IsAnyPrivateName;

    public readonly Name Name => _name!;

    public readonly Name NameForTransition => _name!;

    /// <summary>LookupIterator::GetName: the name, materializing it for elements.</summary>
    public Name GetName() => _name ??= _isolate.Factory.SizeToString(_index);

    public readonly PropertyKey GetKey() => new(_name, _index);

    public readonly JSValue GetReceiver() => _receiver;
    public readonly JSValue LookupStartObject => _lookupStartObject;

    public readonly bool CheckPrototypeChain => (_configuration & Configuration.kPrototypeChain) != 0;
    readonly bool CheckInterceptor => (_configuration & Configuration.kInterceptor) != 0;

    public readonly T GetHolder<T>() where T : JSReceiver => (T)_holder!;
    public readonly JSReceiver? Holder => _holder;

    public readonly bool IsDictionaryHolder => !_holder!.HasFastProperties;

    public readonly Map TransitionMap => (Map)_transition!;
    public readonly PropertyCell TransitionCell => (PropertyCell)_transition!;

    public readonly PropertyDetails PropertyDetails => _propertyDetails;
    public readonly PropertyAttributes PropertyAttributes() => _propertyDetails.Attributes;
    public readonly bool IsConfigurable => _propertyDetails.IsConfigurable;
    public readonly bool IsReadOnly => _propertyDetails.IsReadOnly;
    public readonly bool IsEnumerable => _propertyDetails.IsEnumerable;
    public readonly Representation Representation => _propertyDetails.Representation;
    public readonly PropertyLocation Location => _propertyDetails.Location;
    public readonly PropertyConstness Constness => _propertyDetails.Constness;

    public readonly InternalIndex DescriptorNumber => _number;
    public readonly InternalIndex DictionaryEntry => _number;

    /// <summary>LookupIterator::GetStoreTarget: the global object behind a global proxy, else the receiver.</summary>
    public readonly T GetStoreTarget<T>() where T : JSReceiver
    {
        if (_receiver.HeapObjectOrNull is JSGlobalProxy proxy && proxy.Map.Prototype is JSGlobalObject global)
        {
            return (T)(JSReceiver)global;
        }
        return (T)_receiver.Object;
    }

    public void NotFound()
    {
        _hasProperty = false;
        _state = StateKind.NOT_FOUND;
    }

    // --- iteration --------------------------------------------------------------

    void Start(bool isElement)
    {
        if (_lookupStartObject.HeapObjectOrNull is JSReceiver receiver)
        {
            _holder = receiver;
        }
        else
        {
            // Strings are the only non-JSReceiver objects with properties (only
            // elements and 'length') directly on the wrapper. Inline the lookup
            // implementation here.
            if (_lookupStartObject.HeapObjectOrNull is JSString str &&
                ((!isElement && ReferenceEquals(_name, ReadOnlyRoots.length_string)) ||
                 (isElement && _index < (ulong)str.Length)))
            {
                _hasProperty = true;
                _state = StateKind.STRING_LOOKUP_START_OBJECT;
                return;
            }
            if (!CheckPrototypeChain)
            {
                // This is an attempt to perform an own property lookup on a
                // non-JSReceiver that doesn't have any properties.
                _hasProperty = false;
                _state = StateKind.NOT_FOUND;
                return;
            }
            _holder = GetRootForNonJSReceiver();
        }

        // This is a regular JSReceiver lookup.
        _hasProperty = false;
        _state = StateKind.NOT_FOUND;

        JSReceiver holder = _holder;
        Map map = holder.Map;

        _state = LookupInHolder(isElement, map, holder);
        if (IsFound) return;

        NextInternal(isElement, map, holder);
    }

    /// <summary>LookupIterator::Next: continues the lookup past the current holder.</summary>
    public void Next()
    {
        _hasProperty = false;
        JSReceiver holder = _holder!;
        Map map = holder.Map;

        if (Map.IsSpecialReceiverMap(map))
        {
            _state = LookupInSpecialHolder(IsElement(), map, holder);
            if (IsFound) return;
        }
        NextInternal(IsElement(), map, holder);
    }

    void NextInternal(bool isElement, Map origMap, JSReceiver holder)
    {
        Map map = origMap;
        do
        {
            JSReceiver? maybeHolder = NextHolder(map);
            if (maybeHolder is null)
            {
                switch (_interceptorState)
                {
                    case InterceptorState.kSkipNonMasking:
                        // If we've found a non-masking interceptor, but we're not checking
                        // the prototype chain, we need to do this now.
                        if (!CheckPrototypeChain)
                        {
                            _interceptorState = InterceptorState.kSkipNonMaskingOwnProperty;
                            continue;
                        }
                        RestartInternal(isElement, InterceptorState.kProcessNonMasking);
                        return;
                    case InterceptorState.kSkipNonMaskingOwnProperty:
                        // We're at the end of the chain, and haven't found anything, so
                        // non-masking interceptors can be applied.
                        RestartInternal(isElement, InterceptorState.kProcessNonMasking);
                        return;
                }
                _state = StateKind.NOT_FOUND;
                _holder = holder;
                return;
            }
            holder = maybeHolder;
            map = holder.Map;
            _state = LookupInHolder(isElement, map, holder);
            if (_interceptorState == InterceptorState.kSkipNonMaskingOwnProperty && IsFound)
            {
                switch (_state)
                {
                    case StateKind.INTERCEPTOR:
                        if (!isElement) continue;
                        break;
                    case StateKind.ACCESS_CHECK:
                        // If an access check fails, we assume the lookup succeeds.
                        if (HasAccess()) continue;
                        break;
                }
                // We need fully reset state, since this is not a true hit.
                _number = InternalIndex.NotFound;
                _propertyDetails = PropertyDetails.Empty();
                _state = StateKind.NOT_FOUND;
                _holder = holder;
                return;
            }
        } while (!IsFound);

        _holder = holder;
    }

    void RestartInternal(bool isElement, InterceptorState interceptorState)
    {
        _interceptorState = interceptorState;
        _propertyDetails = PropertyDetails.Empty();
        _number = InternalIndex.NotFound;
        Start(isElement);
    }

    /// <summary>LookupIterator::Restart.</summary>
    public void Restart() => RestartInternal(IsElement(), InterceptorState.kUninitialized);

    /// <summary>LookupIterator::RecheckTypedArrayBounds.</summary>
    public void RecheckTypedArrayBounds()
    {
        if (!IsElement(_holder!)) return;
        var jsObject = (JSObject)_holder!;
        ElementsAccessor accessor = jsObject.GetElementsAccessor();
        _number = accessor.GetEntryForIndex(_isolate, jsObject, jsObject.Elements, _index);
        if (_number.IsNotFound) return;
        _propertyDetails = accessor.GetDetails(jsObject, _number);
        _hasProperty = true;
        _state = StateKind.DATA;
    }

    readonly JSReceiver GetRootForNonJSReceiver() =>
        ObjectOps.GetPrototypeChainRootMap(_lookupStartObject, _isolate).Prototype
        ?? throw new InvalidOperationException("null prototype chain root");

    /// <summary>LookupIterator::HasAccess: the embedder's security check (always true: one security token).</summary>
    public readonly bool HasAccess() => true;

    readonly JSReceiver? NextHolder(Map map)
    {
        if (map.Prototype is null) return null;
        bool checkPrototypeChain = CheckPrototypeChain || _interceptorState == InterceptorState.kSkipNonMaskingOwnProperty;
        if (!checkPrototypeChain && !Map.IsJSGlobalProxyMap(map)) return null;
        return map.Prototype;
    }

    readonly StateKind NotFound(JSReceiver holder)
    {
        if (holder is not JSTypedArray) return StateKind.NOT_FOUND;
        if (IsElement()) return StateKind.TYPED_ARRAY_INDEX_NOT_FOUND;
        if (_name is not JSString s) return StateKind.NOT_FOUND;
        return ObjectOps.IsSpecialIndex(s) ? StateKind.TYPED_ARRAY_INDEX_NOT_FOUND : StateKind.NOT_FOUND;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    StateKind LookupInHolder(bool isElement, Map map, JSReceiver holder) =>
        Map.IsSpecialReceiverMap(map)
            ? LookupInSpecialHolder(isElement, map, holder)
            : LookupInRegularHolder(isElement, map, holder);

    StateKind LookupInSpecialHolder(bool isElement, Map map, JSReceiver holder)
    {
        switch (_state)
        {
            case StateKind.NOT_FOUND:
                if (Map.IsJSProxyMap(map))
                {
                    if (isElement || !_name!.IsAnyPrivate) return StateKind.JSPROXY;
                }
                if (Map.IsJSModuleNamespaceMap(map))
                {
                    if (isElement || !_name!.IsAnyPrivate) return StateKind.MODULE_NAMESPACE;
                }
                if (map.IsAccessCheckNeeded)
                {
                    if (isElement || !_name!.IsPrivateInternal) return StateKind.ACCESS_CHECK;
                }
                goto case StateKind.ACCESS_CHECK;
            case StateKind.ACCESS_CHECK:
                // Interceptors are not ported (see the file comment).
                goto case StateKind.INTERCEPTOR;
            case StateKind.INTERCEPTOR:
                if (Map.IsJSGlobalObjectMap(map) && !IsJsArrayElement(isElement))
                {
                    GlobalDictionary dict = ((JSGlobalObject)holder).GlobalDictionary;
                    _number = dict.FindEntry(_name!);
                    if (_number.IsNotFound) return StateKind.NOT_FOUND;
                    PropertyCell cell = dict.CellAt(_number);
                    if (ReferenceEquals(cell.Value.HeapObjectOrNull, Oddball.PropertyCellHole)) return StateKind.NOT_FOUND;
                    _propertyDetails = cell.PropertyDetails;
                    _hasProperty = true;
                    return _propertyDetails.Kind == PropertyKind.Data ? StateKind.DATA : StateKind.ACCESSOR;
                }
                goto case StateKind.MODULE_NAMESPACE;
            case StateKind.MODULE_NAMESPACE:
                return LookupInRegularHolder(isElement, map, holder);
            case StateKind.ACCESSOR:
            case StateKind.DATA:
            case StateKind.WASM_OBJECT:
                return StateKind.NOT_FOUND;
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    readonly bool IsJsArrayElement(bool isElement) => isElement && _index <= JSArray.kMaxArrayIndex;

    StateKind LookupInRegularHolder(bool isElement, Map map, JSReceiver holder)
    {
        if (_interceptorState == InterceptorState.kProcessNonMasking) return StateKind.NOT_FOUND;
        if (isElement && IsElement(holder))
        {
            var jsObject = (JSObject)holder;
            ElementsAccessor accessor = jsObject.GetElementsAccessor();
            _number = accessor.GetEntryForIndex(_isolate, jsObject, jsObject.Elements, _index);
            if (_number.IsNotFound)
            {
                return holder is JSTypedArray ? StateKind.TYPED_ARRAY_INDEX_NOT_FOUND : StateKind.NOT_FOUND;
            }
            _propertyDetails = accessor.GetDetails(jsObject, _number);
            if (map.HasFrozenElements)
            {
                _propertyDetails = _propertyDetails.CopyAddAttributes(V8Sharp.Objects.PropertyAttributes.FROZEN);
            }
            else if (map.HasSealedElements)
            {
                _propertyDetails = _propertyDetails.CopyAddAttributes(V8Sharp.Objects.PropertyAttributes.SEALED);
            }
        }
        else if (!map.IsDictionaryMap)
        {
            DescriptorArray descriptors = map.InstanceDescriptors;
            _number = descriptors.SearchWithCache(_name!, map);
            if (_number.IsNotFound) return NotFound(holder);
            _propertyDetails = descriptors.GetDetails(_number);
        }
        else
        {
            NameDictionary dict = holder.PropertyDictionary;
            _number = dict.FindEntry(_name!);
            if (_number.IsNotFound) return NotFound(holder);
            _propertyDetails = dict.DetailsAt(_number);
        }
        _hasProperty = true;
        return _propertyDetails.Kind == PropertyKind.Data ? StateKind.DATA : StateKind.ACCESSOR;
    }

    void ReloadPropertyInformation(bool isElement)
    {
        _state = StateKind.BEFORE_PROPERTY;
        _interceptorState = InterceptorState.kUninitialized;
        _state = LookupInHolder(isElement, _holder!.Map, _holder);
    }

    /// <summary>LookupIterator::HasInternalMarkerProperty.</summary>
    public static bool HasInternalMarkerProperty(Isolate isolate, JSReceiver holder, Symbol marker)
    {
        Map map = holder.Map;
        if (map.IsDictionaryMap) return holder.PropertyDictionary.FindEntry(marker).IsFound;
        return map.InstanceDescriptors.SearchWithCache(marker, map).IsFound;
    }

    // --- holder checks -----------------------------------------------------------

    public readonly bool HolderIsReceiver()
    {
        // Optimization that only works if configuration_ is not mutable.
        if (!CheckPrototypeChain) return true;
        return ReferenceEquals(_receiver.HeapObjectOrNull, _holder);
    }

    public readonly bool HolderIsReceiverOrHiddenPrototype()
    {
        // Optimization that only works if configuration_ is not mutable.
        if (!CheckPrototypeChain) return true;
        if (ReferenceEquals(_receiver.HeapObjectOrNull, _holder)) return true;
        if (_receiver.HeapObjectOrNull is not JSGlobalProxy proxy) return false;
        return ReferenceEquals(proxy.Map.Prototype, _holder);
    }

    // --- values --------------------------------------------------------------------

    readonly JSValue FetchValue()
    {
        if (IsElement(_holder!))
        {
            var holder = (JSObject)_holder!;
            ElementsAccessor accessor = holder.GetElementsAccessor();
            return accessor.Get(_isolate, holder, _number);
        }
        if (_holder is JSGlobalObject global)
        {
            return global.GlobalDictionary.ValueAt(_number);
        }
        if (!_holder!.HasFastProperties)
        {
            return _holder.PropertyDictionary.ValueAt(_number);
        }
        if (_propertyDetails.Location == PropertyLocation.Field)
        {
            var holder = (JSObject)_holder;
            FieldIndex fieldIndex = FieldIndex.ForDetails(holder.Map, _propertyDetails);
            return JSObject.FastPropertyAt(_isolate, holder, _propertyDetails.Representation, fieldIndex);
        }
        return _holder.Map.InstanceDescriptors.GetStrongValue(_number);
    }

    readonly bool CanStayConst(JSValue value)
    {
        if (ReferenceEquals(value.HeapObjectOrNull, Oddball.Uninitialized))
        {
            // Storing uninitialized value means that we are preparing for a computed
            // property value in an object literal. The initializing store will follow
            // and it will properly update constness based on the actual value.
            return true;
        }
        var holder = (JSObject)_holder!;
        FieldIndex fieldIndex = FieldIndex.ForDetails(holder.Map, _propertyDetails);
        if (_propertyDetails.Representation.IsDouble)
        {
            if (!value.IsNumber) return false;
            JSValue current = holder.RawFastPropertyAt(fieldIndex);
            // Only allow initializing stores to double to stay constant.
            return current.IsNumber && FixedDoubleArray.IsHoleBits(current.Number);
        }
        JSValue currentValue = holder.RawFastPropertyAt(fieldIndex);
        return ReferenceEquals(currentValue.HeapObjectOrNull, Oddball.Uninitialized);
    }

    public readonly InternalIndex GetFieldDescriptorIndex() => _number;
    public readonly InternalIndex GetAccessorIndex() => _number;

    public readonly FieldIndex GetFieldIndex() => FieldIndex.ForDetails(_holder!.Map, _propertyDetails);

    public readonly PropertyCell GetPropertyCell() => ((JSGlobalObject)_holder!).GlobalDictionary.CellAt(_number);

    /// <summary>LookupIterator::GetAccessors: an AccessorPair or an AccessorInfo.</summary>
    public readonly HeapObject GetAccessors() => FetchValue().Object;

    /// <summary>LookupIterator::GetStringPropertyValue: a character or "length" of a string start object.</summary>
    public readonly JSValue GetStringPropertyValue()
    {
        var str = _lookupStartObject.As<JSString>();
        if (IsElement()) return _isolate.Factory.LookupSingleCharacterStringFromCode(str.Get((int)_index));
        // The state guarantees that it's a lookup of "length" property.
        return JSValue.FromInt(str.Length);
    }

    public readonly JSValue GetDataValue() => FetchValue();

    /// <summary>LookupIterator::WriteDataValue.</summary>
    public readonly void WriteDataValue(JSValue value, bool initializingStore)
    {
        JSReceiver holder = _holder!;
        if (IsElement(holder))
        {
            var obj = (JSObject)holder;
            obj.GetElementsAccessor().Set(obj, _number, value);
        }
        else if (holder.HasFastProperties)
        {
            if (_propertyDetails.Location == PropertyLocation.Field)
            {
                if (_propertyDetails.Representation.IsDouble && value.IsNumber && double.IsNaN(value.Number))
                {
                    value = JSValue.NaN;
                }
                ((JSObject)holder).WriteToField(_number, _propertyDetails, value);
            }
        }
        else if (holder is JSGlobalObject)
        {
            // PropertyCell::PrepareForAndSetValue already wrote the value into the
            // cell.
        }
        else
        {
            holder.PropertyDictionary.ValueAtPut(_number, value);
        }
    }

    // --- mutation -------------------------------------------------------------------

    /// <summary>LookupIterator::PrepareForDataProperty: generalizes the field or elements kind for <paramref name="value"/>.</summary>
    public void PrepareForDataProperty(JSValue value)
    {
        JSReceiver holder = _holder!;
        // We are not interested in tracking constness of a JSProxy's direct
        // properties.
        if (holder is JSProxy) return;

        if (IsElement(holder))
        {
            var holderObj = (JSObject)holder;
            ElementsKind kind = holderObj.GetElementsKind();
            ElementsKind to = ObjectOps.OptimalElementsKind(value);
            if (ElementsKinds.IsHoleyElementsKind(kind)) to = ElementsKinds.GetHoleyElementsKind(to);
            to = ElementsKinds.GetMoreGeneralElementsKind(kind, to);

            if (kind != to) JSObject.TransitionElementsKind(_isolate, holderObj, to);

            // Copy the backing store if it is copy-on-write.
            if (ElementsKinds.IsSmiOrObjectElementsKind(to) || ElementsKinds.IsSealedElementsKind(to) ||
                ElementsKinds.IsNonextensibleElementsKind(to))
            {
                JSObject.EnsureWritableFastElements(_isolate, holderObj);
            }
            return;
        }

        if (holder is JSGlobalObject global)
        {
            GlobalDictionary dictionary = global.GlobalDictionary;
            PropertyCell cell = dictionary.CellAt(_number);
            _propertyDetails = cell.PropertyDetails;
            PropertyCell.PrepareForAndSetValue(_isolate, dictionary, _number, value, _propertyDetails);
            return;
        }

        PropertyConstness newConstness = PropertyConstness.Const;
        if (Constness == PropertyConstness.Const)
        {
            // Check that current value matches new value otherwise we should make
            // the property mutable.
            if (holder.HasFastProperties)
            {
                if (_propertyDetails.Representation.IsDouble && value.IsNumber && double.IsNaN(value.Number))
                {
                    value = JSValue.NaN;
                }
                if (!CanStayConst(value)) newConstness = PropertyConstness.Mutable;
            }
        }

        if (!holder.HasFastProperties) return;

        var holderObject = (JSObject)holder;
        Map oldMap = holder.Map;

        Map newMap = Map.Update(_isolate, oldMap);
        if (!newMap.IsDictionaryMap)
        {
            // fast -> fast
            newMap = Map.PrepareForDataProperty(_isolate, newMap, _number, newConstness, value);
            if (ReferenceEquals(oldMap, newMap))
            {
                // Update the property details if the representation was None.
                if (Constness != newConstness || Representation.IsNone)
                {
                    _propertyDetails = newMap.InstanceDescriptors.GetDetails(_number);
                }
                return;
            }
        }

        JSObject.MigrateToMap(_isolate, holderObject, newMap);
        ReloadPropertyInformation(false);
    }

    /// <summary>LookupIterator::ReconfigureDataProperty.</summary>
    public void ReconfigureDataProperty(JSValue value, PropertyAttributes attributes)
    {
        JSReceiver holder = _holder!;

        // Property details can never change for private properties.
        if (holder is JSProxy) return;

        var holderObj = (JSObject)holder;
        if (IsElement(holder))
        {
            holderObj.GetElementsAccessor().Reconfigure(_isolate, holderObj, holderObj.Elements, _number, value, attributes);
            ReloadPropertyInformation(true);
        }
        else if (holderObj.HasFastProperties)
        {
            Map oldMap = holderObj.Map;
            // Force mutable to avoid changing constant value by reconfiguring
            // kData -> kAccessor -> kData.
            Map newMap = MapUpdater.ReconfigureExistingProperty(_isolate, oldMap, _number, PropertyKind.Data, attributes,
                PropertyConstness.Mutable);
            if (!newMap.IsDictionaryMap)
            {
                // Make sure that the data property has a compatible representation.
                newMap = Map.PrepareForDataProperty(_isolate, newMap, _number, PropertyConstness.Mutable, value);
            }
            JSObject.MigrateToMap(_isolate, holderObj, newMap);
            ReloadPropertyInformation(false);
        }

        if (!IsElement(holder) && !holderObj.HasFastProperties)
        {
            if (holderObj.Map.IsPrototypeMap &&
                ((((_propertyDetails.Attributes & Objects.PropertyAttributes.READ_ONLY) == 0) &&
                  (attributes & Objects.PropertyAttributes.READ_ONLY) != 0) ||
                 (_propertyDetails.Attributes & Objects.PropertyAttributes.DONT_ENUM) != (attributes & Objects.PropertyAttributes.DONT_ENUM)))
            {
                // Invalidate prototype validity cell when a property is reconfigured
                // from writable to read-only as this may invalidate transitioning store
                // IC handlers.
                // Invalidate prototype validity cell when a property changes
                // enumerability to clear the prototype chain enum cache.
                JSObject.InvalidatePrototypeChains(holder.Map);
            }
            if (holderObj is JSGlobalObject global)
            {
                var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyCellType.Mutable);
                GlobalDictionary dictionary = global.GlobalDictionary;
                PropertyCell cell = PropertyCell.PrepareForAndSetValue(_isolate, dictionary, _number, value, details);
                _propertyDetails = cell.PropertyDetails;
            }
            else
            {
                var details = new PropertyDetails(PropertyKind.Data, attributes, PropertyConstness.Mutable);
                NameDictionary dictionary = holderObj.PropertyDictionary;
                PropertyDetails originalDetails = dictionary.DetailsAt(_number);
                int enumerationIndex = originalDetails.DictionaryIndex;
                details = details.SetIndex(enumerationIndex);
                dictionary.SetEntry(_number, Name, value, details);
                _propertyDetails = details;
            }
            _state = StateKind.DATA;
        }

        WriteDataValue(value, true);
    }

    /// <summary>
    /// LookupIterator::PrepareTransitionToDataProperty: computes the transition
    /// (map or property cell) that adds the property to <paramref name="receiver"/>.
    /// </summary>
    public void PrepareTransitionToDataProperty(JSReceiver receiver, JSValue value, PropertyAttributes attributes,
        StoreOrigin storeOrigin)
    {
        if (_state == StateKind.TRANSITION) return;

        if (!IsElement() && NameForTransition.IsAnyPrivate)
        {
            attributes |= Objects.PropertyAttributes.DONT_ENUM;
        }

        Map map = receiver.Map;

        // Migrate to the newest map before storing the property.
        map = Map.Update(_isolate, map);

        // Dictionary maps can always have additional data properties.
        if (map.IsDictionaryMap)
        {
            _state = StateKind.TRANSITION;
            if (Map.IsJSGlobalObjectMap(map))
            {
                // Don't set enumeration index (it will be set during value store).
                _propertyDetails = new PropertyDetails(PropertyKind.Data, attributes, PropertyCell.InitialType(_isolate, value));
                _transition = _isolate.Factory.NewPropertyCell(NameForTransition, _propertyDetails, value);
                _hasProperty = true;
            }
            else
            {
                // Don't set enumeration index (it will be set during value store).
                _propertyDetails = new PropertyDetails(PropertyKind.Data, attributes, PropertyDetails.kConstIfDictConstnessTracking);
                _transition = map;
            }
            return;
        }

        Map transition = Map.TransitionToDataProperty(_isolate, map, NameForTransition, value, attributes,
            PropertyConstness.Const, storeOrigin);
        _state = StateKind.TRANSITION;
        _transition = transition;

        if (transition.IsDictionaryMap)
        {
            // Don't set enumeration index (it will be set during value store).
            _propertyDetails = new PropertyDetails(PropertyKind.Data, attributes, PropertyDetails.kConstIfDictConstnessTracking);
        }
        else
        {
            _propertyDetails = transition.GetLastDescriptorDetails();
            _hasProperty = true;
        }
    }

    /// <summary>LookupIterator::ApplyTransitionToDataProperty.</summary>
    public bool ApplyTransitionToDataProperty(JSReceiver receiver)
    {
        _holder = receiver;
        if (receiver is JSGlobalObject global)
        {
            JSObject.InvalidatePrototypeChains(receiver.Map);

            // Install a property cell.
            GlobalDictionary dictionary = GlobalDictionary.Add(_isolate, global.GlobalDictionary, Name, TransitionCell,
                _propertyDetails, out _number);
            global.GlobalDictionary = dictionary;

            // Reload details containing proper enumeration index value.
            _propertyDetails = TransitionCell.PropertyDetails;
            _hasProperty = true;
            _state = StateKind.DATA;
            return true;
        }
        Map transition = TransitionMap;
        bool simpleTransition = ReferenceEquals(transition.GetBackPointer(), receiver.Map);

        if (_configuration == Configuration.DEFAULT && !transition.IsDictionaryMap && !transition.IsPrototypeMap &&
            !transition.IsPrototypeValidityCellValid())
        {
            // Only LookupIterator instances with DEFAULT (full prototype chain)
            // configuration can produce valid transition handler maps.
            // Note that it doesn't make sense to prepare a fast property transition
            // handler for prototype maps because they are not going to be reused
            // anyway.
            transition.PrototypeValidityCell = Map.GetOrCreatePrototypeChainValidityCell(transition, _isolate);
        }

        if (receiver is JSObject receiverObject)
        {
            JSObject.MigrateToMap(_isolate, receiverObject, transition);
        }
        else
        {
            // JSProxy with a private name: only the map changes.
            receiver.Map = transition;
        }

        if (simpleTransition)
        {
            _number = transition.LastAdded();
            _propertyDetails = transition.GetLastDescriptorDetails();
            _state = StateKind.DATA;
        }
        else if (receiver.Map.IsDictionaryMap)
        {
            if (receiver.Map.IsPrototypeMap && receiver is JSObject)
            {
                JSObject.InvalidatePrototypeChains(receiver.Map);
            }
            NameDictionary dictionary = NameDictionary.Add(_isolate, receiver.PropertyDictionary, Name,
                JSValue.FromObject(Oddball.Uninitialized), _propertyDetails, out _number);
            receiver.SetProperties(dictionary);
            if (Name.IsInteresting()) dictionary.MayHaveInterestingProperties = true;
            // Reload details containing proper enumeration index value.
            _propertyDetails = dictionary.DetailsAt(_number);
            _hasProperty = true;
            _state = StateKind.DATA;
        }
        else
        {
            ReloadPropertyInformation(false);
        }
        return true;
    }

    /// <summary>LookupIterator::Delete.</summary>
    public void Delete()
    {
        JSReceiver holder = _holder!;
        if (IsElement(holder))
        {
            var obj = (JSObject)holder;
            obj.GetElementsAccessor().Delete(_isolate, obj, _number);
        }
        else
        {
            bool isPrototypeMap = holder.Map.IsPrototypeMap;
            PropertyNormalizationMode mode = isPrototypeMap
                ? PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES
                : PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES;

            if (holder.HasFastProperties)
            {
                JSObject.NormalizeProperties(_isolate, (JSObject)holder, mode, 0, "DeletingProperty");
                ReloadPropertyInformation(false);
            }
            JSReceiver.DeleteNormalizedProperty(_isolate, holder, _number);
            if (holder is JSObject obj) JSObject.ReoptimizeIfPrototype(_isolate, obj);
        }
        _state = StateKind.NOT_FOUND;
    }

    /// <summary>LookupIterator::TransitionToAccessorProperty.</summary>
    public bool TransitionToAccessorProperty(JSValue getter, JSValue setter, PropertyAttributes attributes)
    {
        // Can only be called when the receiver is a JSObject. JSProxy has to be
        // handled via a trap. Adding properties to primitive values is not
        // observable.
        JSObject receiver = GetStoreTarget<JSObject>();
        if (!IsElement() && Name.IsAnyPrivate)
        {
            attributes |= Objects.PropertyAttributes.DONT_ENUM;
        }

        if (!IsElement(receiver) && !receiver.Map.IsDictionaryMap)
        {
            Map oldMap = receiver.Map;

            if (!ReferenceEquals(_holder, receiver))
            {
                _holder = receiver;
                _state = StateKind.NOT_FOUND;
            }
            else if (_state == StateKind.INTERCEPTOR)
            {
                LookupInRegularHolder(false, oldMap, _holder);
            }

            Map newMap = Map.TransitionToAccessorProperty(_isolate, oldMap, _name!, _number, getter, setter, attributes);
            bool simpleTransition = ReferenceEquals(newMap.GetBackPointer(), receiver.Map);
            JSObject.MigrateToMap(_isolate, receiver, newMap);

            if (simpleTransition)
            {
                _number = newMap.LastAdded();
                _propertyDetails = newMap.GetLastDescriptorDetails();
                _state = StateKind.ACCESSOR;
                return true;
            }

            ReloadPropertyInformation(false);
            if (!newMap.IsDictionaryMap) return true;
        }

        AccessorPair pair;
        if (State == StateKind.ACCESSOR && GetAccessors() is AccessorPair existing)
        {
            pair = existing;
            // If the component and attributes are identical, nothing has to be done.
            if (pair.Equals(getter, setter))
            {
                if (PropertyDetails.Attributes == attributes)
                {
                    if (!IsElement(receiver)) JSObject.ReoptimizeIfPrototype(_isolate, receiver);
                    return true;
                }
            }
            else
            {
                pair = AccessorPair.Copy(pair);
                pair.SetComponents(getter, setter);
            }
        }
        else
        {
            pair = _isolate.Factory.NewAccessorPair();
            pair.SetComponents(getter, setter);
        }

        return TransitionToAccessorPair(pair, attributes);
    }

    /// <summary>LookupIterator::TransitionToAccessorPair.</summary>
    public bool TransitionToAccessorPair(HeapObject pair, PropertyAttributes attributes)
    {
        JSObject receiver = GetStoreTarget<JSObject>();
        _holder = receiver;

        var details = new PropertyDetails(PropertyKind.Accessor, attributes, PropertyCellType.Mutable);

        if (IsElement(receiver))
        {
            NumberDictionary dictionary = JSObject.NormalizeElements(_isolate, receiver);
            dictionary = NumberDictionary.Set(_isolate, dictionary, ArrayIndex, pair, receiver, details);
            receiver.RequireSlowElements(dictionary);

            if (receiver.HasSlowArgumentsElements)
            {
                var parameterMap = (SloppyArgumentsElements)receiver.Elements;
                int length = parameterMap.Length;
                if (_number.IsFound && _number.AsInt < length)
                {
                    parameterMap.SetMappedEntry(_number.AsInt, JSValue.TheHole);
                }
                parameterMap.Arguments = dictionary;
            }
            else
            {
                receiver.Elements = dictionary;
            }
            ReloadPropertyInformation(true);
        }
        else
        {
            PropertyNormalizationMode mode = PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES;
            if (receiver.Map.IsPrototypeMap)
            {
                JSObject.InvalidatePrototypeChains(receiver.Map);
                mode = PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES;
            }

            // Normalize object to make this operation simple.
            JSObject.NormalizeProperties(_isolate, receiver, mode, 0, "TransitionToAccessorPair");

            JSObject.SetNormalizedProperty(_isolate, receiver, _name!, pair, details);
            JSObject.ReoptimizeIfPrototype(_isolate, receiver);

            ReloadPropertyInformation(false);
        }
        return true;
    }

    /// <summary>LookupIterator::ExtendingNonExtensible.</summary>
    public readonly bool ExtendingNonExtensible(JSReceiver receiver)
    {
        Map receiverMap = receiver.Map;
        if (receiverMap.IsExtensible) return false;
        if (IsElement() || !_name!.IsAnyPrivate) return true;
        if (_name.IsPrivateInternal) return false;
        return _isolate.Flags.js_nonextensible_applies_to_private;
    }

    /// <summary>LookupIterator::IsCacheableTransition.</summary>
    public readonly bool IsCacheableTransition()
    {
        if (_transition is PropertyCell || (TransitionMap.IsDictionaryMap && !GetStoreTarget<JSReceiver>().HasFastProperties))
        {
            return true;
        }
        return TransitionMap.GetBackPointer() is Map;
    }

    // --- protectors ------------------------------------------------------------------

    /// <summary>LookupIterator::UpdateProtector.</summary>
    public readonly void UpdateProtector(JSValue? value = null, JSValue? oldValue = null)
    {
        if (IsElement()) return;
        UpdateProtector(_isolate, _receiver, _name!, value, oldValue);
    }

    public static void UpdateProtector(Isolate isolate, JSValue receiver, Name name, JSValue? value = null, JSValue? oldValue = null)
    {
        if (IsNameForProtector(name)) InternalUpdateProtector(isolate, receiver, name, value, oldValue);
    }

    /// <summary>ReadOnlyRoots::IsNameForProtector.</summary>
    static bool IsNameForProtector(Name name) =>
        ReferenceEquals(name, ReadOnlyRoots.constructor_string) || ReferenceEquals(name, ReadOnlyRoots.next_string) ||
        ReferenceEquals(name, ReadOnlyRoots.resolve_string) || ReferenceEquals(name, ReadOnlyRoots.then_string) ||
        ReferenceEquals(name, ReadOnlyRoots.is_concat_spreadable_symbol) ||
        ReferenceEquals(name, ReadOnlyRoots.iterator_symbol) || ReferenceEquals(name, ReadOnlyRoots.species_symbol) ||
        ReferenceEquals(name, ReadOnlyRoots.match_all_symbol) || ReferenceEquals(name, ReadOnlyRoots.replace_symbol) ||
        ReferenceEquals(name, ReadOnlyRoots.split_symbol) || ReferenceEquals(name, ReadOnlyRoots.to_primitive_symbol) ||
        ReferenceEquals(name, ReadOnlyRoots.valueOf_string);

    static bool IsInCreationContext(Isolate isolate, JSObject obj, JSValue slotValue) =>
        ReferenceEquals(obj, slotValue.HeapObjectOrNull);

    static void InternalUpdateProtector(Isolate isolate, JSValue receiverGeneric, Name name, JSValue? value, JSValue? oldValue)
    {
        if (isolate.BootstrapperActive) return;
        if (receiverGeneric.HeapObjectOrNull is not JSObject receiver) return;
        NativeContext? nc = receiver.GetCreationContext();

        if (ReferenceEquals(name, ReadOnlyRoots.constructor_string))
        {
            // Setting the constructor property could change an instance's @@species
            if (receiver is JSArray)
            {
                if (Protectors.IsArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateArraySpeciesLookupChain(isolate);
                return;
            }
            if (receiver is JSPromise)
            {
                if (Protectors.IsPromiseSpeciesLookupChainIntact(isolate)) Protectors.InvalidatePromiseSpeciesLookupChain(isolate);
                return;
            }
            if (receiver is JSRegExp)
            {
                if (Protectors.IsRegExpSpeciesLookupChainIntact(isolate)) Protectors.InvalidateRegExpSpeciesLookupChain(isolate);
                return;
            }
            if (receiver is JSTypedArray)
            {
                if (Protectors.IsTypedArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateTypedArraySpeciesLookupChain(isolate);
                return;
            }
            if (receiver.Map.IsPrototypeMap && nc is not null)
            {
                // Setting the constructor of any prototype with the @@species protector
                // (of any realm) also needs to invalidate the protector.
                if (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_ARRAY_PROTOTYPE_INDEX]))
                {
                    if (Protectors.IsArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateArraySpeciesLookupChain(isolate);
                }
                else if (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.PROMISE_PROTOTYPE_INDEX]))
                {
                    if (Protectors.IsPromiseSpeciesLookupChainIntact(isolate)) Protectors.InvalidatePromiseSpeciesLookupChain(isolate);
                }
                else if (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.REGEXP_PROTOTYPE_INDEX]))
                {
                    if (Protectors.IsRegExpSpeciesLookupChainIntact(isolate)) Protectors.InvalidateRegExpSpeciesLookupChain(isolate);
                }
                else if (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.TYPED_ARRAY_PROTOTYPE_INDEX]))
                {
                    if (Protectors.IsTypedArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateTypedArraySpeciesLookupChain(isolate);
                }
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.next_string))
        {
            if (receiver.InstanceType == InstanceType.JSArrayIteratorType ||
                (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_ARRAY_ITERATOR_PROTOTYPE_INDEX])))
            {
                // Setting the next property of %ArrayIteratorPrototype% also needs to
                // invalidate the array iterator protector.
                if (Protectors.IsArrayIteratorLookupChainIntact(isolate)) Protectors.InvalidateArrayIteratorLookupChain(isolate);
            }
            else if (receiver.InstanceType is InstanceType.JSMapKeyIteratorType or InstanceType.JSMapKeyValueIteratorType
                         or InstanceType.JSMapValueIteratorType ||
                     (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_MAP_ITERATOR_PROTOTYPE_INDEX])))
            {
                if (Protectors.IsMapIteratorLookupChainIntact(isolate)) Protectors.InvalidateMapIteratorLookupChain(isolate);
            }
            else if (receiver.InstanceType is InstanceType.JSSetKeyValueIteratorType or InstanceType.JSSetValueIteratorType ||
                     (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_SET_ITERATOR_PROTOTYPE_INDEX])))
            {
                if (Protectors.IsSetIteratorLookupChainIntact(isolate)) Protectors.InvalidateSetIteratorLookupChain(isolate);
            }
            else if (receiver.InstanceType == InstanceType.JSStringIteratorType ||
                     (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_STRING_ITERATOR_PROTOTYPE_INDEX])))
            {
                // Setting the next property of %StringIteratorPrototype% invalidates the
                // string iterator protector.
                if (Protectors.IsStringIteratorLookupChainIntact(isolate)) Protectors.InvalidateStringIteratorLookupChain(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.species_symbol))
        {
            // Setting the Symbol.species property of any Array, Promise or TypedArray
            // constructor invalidates the @@species protector
            InstanceType t = receiver.InstanceType;
            if (t == InstanceType.JSArrayConstructorType)
            {
                if (Protectors.IsArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateArraySpeciesLookupChain(isolate);
            }
            else if (t == InstanceType.JSPromiseConstructorType)
            {
                if (Protectors.IsPromiseSpeciesLookupChainIntact(isolate)) Protectors.InvalidatePromiseSpeciesLookupChain(isolate);
            }
            else if (t == InstanceType.JSRegExpConstructorType)
            {
                if (Protectors.IsRegExpSpeciesLookupChainIntact(isolate)) Protectors.InvalidateRegExpSpeciesLookupChain(isolate);
            }
            else if (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.TYPED_ARRAY_FUN_INDEX]))
            {
                if (Protectors.IsTypedArraySpeciesLookupChainIntact(isolate)) Protectors.InvalidateTypedArraySpeciesLookupChain(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.is_concat_spreadable_symbol))
        {
            if (Protectors.IsIsConcatSpreadableLookupChainIntact(isolate)) Protectors.InvalidateIsConcatSpreadableLookupChain(isolate);
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.iterator_symbol))
        {
            if (receiver is JSArray)
            {
                if (!Protectors.IsArrayIteratorLookupChainIntact(isolate)) return;
                // When the value of ArrayIterator is canonical, its behavior remains
                // unchanged.
                if (oldValue is JSValue o && value is JSValue v && o.IsIdenticalTo(v)) return;
                Protectors.InvalidateArrayIteratorLookupChain(isolate);
            }
            else if (receiver.InstanceType is InstanceType.JSSetType or InstanceType.JSSetKeyValueIteratorType
                         or InstanceType.JSSetValueIteratorType ||
                     (nc is not null && (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_SET_ITERATOR_PROTOTYPE_INDEX]) ||
                                         IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_SET_PROTOTYPE_INDEX]))))
            {
                if (Protectors.IsSetIteratorLookupChainIntact(isolate)) Protectors.InvalidateSetIteratorLookupChain(isolate);
            }
            else if (receiver.InstanceType is InstanceType.JSMapKeyIteratorType or InstanceType.JSMapKeyValueIteratorType
                         or InstanceType.JSMapValueIteratorType ||
                     (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_MAP_ITERATOR_PROTOTYPE_INDEX])))
            {
                if (Protectors.IsMapIteratorLookupChainIntact(isolate)) Protectors.InvalidateMapIteratorLookupChain(isolate);
            }
            else if (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_ITERATOR_PROTOTYPE_INDEX]))
            {
                if (Protectors.IsMapIteratorLookupChainIntact(isolate)) Protectors.InvalidateMapIteratorLookupChain(isolate);
                if (Protectors.IsSetIteratorLookupChainIntact(isolate)) Protectors.InvalidateSetIteratorLookupChain(isolate);
            }
            else if (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_STRING_PROTOTYPE_INDEX]))
            {
                // Setting the Symbol.iterator property of String.prototype invalidates
                // the string iterator protector. Symbol.iterator can also be set on a
                // String wrapper, but not on a primitive string. We only support
                // protector for primitive strings.
                if (Protectors.IsStringIteratorLookupChainIntact(isolate)) Protectors.InvalidateStringIteratorLookupChain(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.resolve_string))
        {
            if (!Protectors.IsPromiseResolveLookupChainIntact(isolate)) return;
            // Setting the "resolve" property on any %Promise% intrinsic object
            // invalidates the Promise.resolve protector.
            if (receiver.InstanceType == InstanceType.JSPromiseConstructorType)
            {
                Protectors.InvalidatePromiseResolveLookupChain(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.then_string))
        {
            if (!Protectors.IsPromiseThenLookupChainIntact(isolate)) return;
            // Setting the "then" property on any JSPromise instance or on the
            // initial %PromisePrototype% invalidates the Promise#then protector.
            // Also setting the "then" property on the initial %ObjectPrototype%
            // invalidates the Promise#then protector, since we use this protector
            // to guard the fast-path in AsyncGeneratorResolve, where we can skip
            // the ResolvePromise step and go directly to FulfillPromise if we
            // know that the Object.prototype doesn't contain a "then" method.
            if (receiver is JSPromise ||
                (nc is not null && (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_OBJECT_PROTOTYPE_INDEX]) ||
                                    IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.PROMISE_PROTOTYPE_INDEX]))))
            {
                Protectors.InvalidatePromiseThenLookupChain(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.match_all_symbol) || ReferenceEquals(name, ReadOnlyRoots.replace_symbol) ||
                 ReferenceEquals(name, ReadOnlyRoots.split_symbol))
        {
            if (!Protectors.IsNumberStringNotRegexpLikeIntact(isolate)) return;
            // We need to protect the prototype chains of `Number.prototype` and
            // `String.prototype`: that `Symbol.{matchAll|replace|split}` is not added
            // as a property on any object on these prototype chains. We detect
            // `Number.prototype` and `String.prototype` by checking for a prototype
            // that is a JSPrimitiveWrapper. This is a safe approximation. Using
            // JSPrimitiveWrapper as prototype should be sufficiently rare.
            if (receiver.Map.IsPrototypeMap &&
                (receiver is JSPrimitiveWrapper ||
                 (nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_OBJECT_PROTOTYPE_INDEX]))))
            {
                Protectors.InvalidateNumberStringNotRegexpLike(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.to_primitive_symbol))
        {
            if (!Protectors.IsStringWrapperToPrimitiveIntact(isolate)) return;
            if ((nc is not null && (IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_STRING_PROTOTYPE_INDEX]) ||
                                    IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_OBJECT_PROTOTYPE_INDEX]))) ||
                ObjectOps.IsStringWrapper(receiver))
            {
                Protectors.InvalidateStringWrapperToPrimitive(isolate);
            }
        }
        else if (ReferenceEquals(name, ReadOnlyRoots.valueOf_string))
        {
            if (!Protectors.IsStringWrapperToPrimitiveIntact(isolate)) return;
            if ((nc is not null && IsInCreationContext(isolate, receiver, nc.Slots[(int)Context.Field.INITIAL_STRING_PROTOTYPE_INDEX])) ||
                ObjectOps.IsStringWrapper(receiver))
            {
                Protectors.InvalidateStringWrapperToPrimitive(isolate);
            }
        }
    }
}
