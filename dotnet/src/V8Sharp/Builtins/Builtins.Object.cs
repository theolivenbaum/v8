// Port of the Object builtins: src/builtins/builtins-object.cc,
// builtins-object-gen.cc, object.tq, object-fromentries.tq,
// object-groupby.tq and ObjectConstructor (constructor.tq), together with the
// runtime functions they fall back to (src/runtime/runtime-object.cc:
// ObjectKeys, ObjectGetOwnPropertyNames[TryFast], ObjectHasOwnProperty,
// ObjectCreate, ObjectValues/Entries[SkipFastPath], ObjectIsExtensible,
// JSReceiverPreventExtensions*, JSReceiverGet/SetPrototypeOf*,
// SetDataProperties).
//
// Also collections.tq LoadKeyValuePair, which Object.fromEntries needs. The
// iteration helpers are IteratorBuiltins (Builtins.Iterator.cs).
using System.Runtime.CompilerServices;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterObject()
    {
        Register(Builtin.ObjectConstructor, BuiltinsObject.ObjectConstructor);
        Register(Builtin.ObjectAssign, BuiltinsObject.ObjectAssign);
        Register(Builtin.ObjectCreate, BuiltinsObject.ObjectCreate);
        Register(Builtin.ObjectDefineProperties, BuiltinsObject.ObjectDefineProperties);
        Register(Builtin.ObjectDefineProperty, BuiltinsObject.ObjectDefineProperty);
        Register(Builtin.ObjectEntries, BuiltinsObject.ObjectEntries);
        Register(Builtin.ObjectFreeze, BuiltinsObject.ObjectFreeze);
        Register(Builtin.ObjectFromEntries, BuiltinsObject.ObjectFromEntries);
        Register(Builtin.ObjectGetOwnPropertyDescriptor, BuiltinsObject.ObjectGetOwnPropertyDescriptor);
        Register(Builtin.ObjectGetOwnPropertyDescriptors, BuiltinsObject.ObjectGetOwnPropertyDescriptors);
        Register(Builtin.ObjectGetOwnPropertyNames, BuiltinsObject.ObjectGetOwnPropertyNames);
        Register(Builtin.ObjectGetOwnPropertySymbols, BuiltinsObject.ObjectGetOwnPropertySymbols);
        Register(Builtin.ObjectGetPrototypeOf, BuiltinsObject.ObjectGetPrototypeOf);
        Register(Builtin.ObjectGroupBy, BuiltinsObject.ObjectGroupBy);
        Register(Builtin.ObjectHasOwn, BuiltinsObject.ObjectHasOwn);
        Register(Builtin.ObjectIs, BuiltinsObject.ObjectIs);
        Register(Builtin.ObjectIsExtensible, BuiltinsObject.ObjectIsExtensible);
        Register(Builtin.ObjectIsFrozen, BuiltinsObject.ObjectIsFrozen);
        Register(Builtin.ObjectIsSealed, BuiltinsObject.ObjectIsSealed);
        Register(Builtin.ObjectKeys, BuiltinsObject.ObjectKeys);
        Register(Builtin.ObjectPreventExtensions, BuiltinsObject.ObjectPreventExtensions);
        Register(Builtin.ObjectSeal, BuiltinsObject.ObjectSeal);
        Register(Builtin.ObjectSetPrototypeOf, BuiltinsObject.ObjectSetPrototypeOf);
        Register(Builtin.ObjectValues, BuiltinsObject.ObjectValues);
        Register(Builtin.ObjectDefineGetter, BuiltinsObject.ObjectDefineGetter);
        Register(Builtin.ObjectDefineSetter, BuiltinsObject.ObjectDefineSetter);
        Register(Builtin.ObjectLookupGetter, BuiltinsObject.ObjectLookupGetter);
        Register(Builtin.ObjectLookupSetter, BuiltinsObject.ObjectLookupSetter);
        Register(Builtin.ObjectPrototypeHasOwnProperty, BuiltinsObject.ObjectPrototypeHasOwnProperty);
        Register(Builtin.ObjectPrototypeIsPrototypeOf, BuiltinsObject.ObjectPrototypeIsPrototypeOf);
        Register(Builtin.ObjectPrototypePropertyIsEnumerable, BuiltinsObject.ObjectPrototypePropertyIsEnumerable);
        Register(Builtin.ObjectPrototypeToString, BuiltinsObject.ObjectPrototypeToString);
        Register(Builtin.ObjectPrototypeValueOf, BuiltinsObject.ObjectPrototypeValueOf);
        Register(Builtin.ObjectPrototypeToLocaleString, BuiltinsObject.ObjectPrototypeToLocaleString);
        Register(Builtin.ObjectPrototypeGetProto, BuiltinsObject.ObjectPrototypeGetProto);
        Register(Builtin.ObjectPrototypeSetProto, BuiltinsObject.ObjectPrototypeSetProto);
    }
}

/// <summary>The Object builtins.</summary>
public static class BuiltinsObject
{
    // ---------------------------------------------------------------------
    // constructor.tq

    /// <summary>ObjectConstructor (https://tc39.es/ecma262/#sec-object-constructor).</summary>
    public static JSValue ObjectConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSFunction target = args.Target;
        JSValue newTarget = args.NewTarget;
        if (newTarget.IsUndefined || ReferenceEquals(newTarget.HeapObjectOrNull, target))
        {
            // Not Subclass.
            JSValue value = args.AtOrUndefined(1);
            if (args.ArgcWithoutReceiver <= 0 || value.IsNullOrUndefined)
            {
                // New object.
                return isolate.Factory.NewJSObject(target.NativeContext.ObjectFunction);
            }
            return ObjectOps.ToObject(isolate, value);
        }
        // Subclass.
        return JSObject.New(isolate, target, newTarget.As<JSReceiver>(), null);
    }

    // ---------------------------------------------------------------------
    // builtins-object-gen.cc

    /// <summary>https://tc39.es/ecma262/#sec-object.assign</summary>
    public static JSValue ObjectAssign(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let to be ? ToObject(target).
        JSReceiver to = ObjectOps.ToObject(isolate, args.AtOrUndefined(1));

        // 2. If only one argument was passed, return to.
        // 3. Let sources be the List of argument values starting with the
        //    second argument.
        // 4. For each element nextSource of sources, in ascending index order,
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        for (int i = 1; i < arguments.Length; i++)
        {
            SetDataProperties(isolate, to, arguments[i]);
        }
        // 5. Return to.
        return to;
    }

    /// <summary>
    /// Runtime_SetDataProperties / the SetDataProperties builtin. The
    /// JSReceiver::SetOrCopyDataProperties fast path (FastAssign over the
    /// source's descriptors) covers V8's CSA fast paths.
    /// </summary>
    internal static void SetDataProperties(Isolate isolate, JSReceiver target, JSValue source)
    {
        // 2. If source is undefined or null, let keys be an empty List.
        if (source.IsNullOrUndefined) return;
        JSReceiver.SetOrCopyDataProperties(isolate, target, source, JSReceiver.PropertiesEnumerationMode.EnumerationOrder);
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.keys</summary>
    public static JSValue ObjectKeys(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);

        // Check if the {object} has a usable enum cache.
        if (obj.HeapObjectOrNull is JSObject jsObject && !Map.IsCustomElementsReceiverMap(jsObject.Map))
        {
            Map map = jsObject.Map;
            int enumLength = map.EnumLength;
            if (enumLength != Map.kInvalidEnumCacheSentinel && HasNoElements(jsObject))
            {
                if (enumLength == 0) return NewJSArray(isolate, FixedArray.Empty);
                // The {object} has a usable enum cache, use that.
                return NewJSArray(isolate, CopyEnumCacheKeys(map, enumLength));
            }
        }

        // Let the runtime compute the elements (Runtime_ObjectKeys).
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        FixedArray keys = KeyAccumulator.GetKeys(isolate, receiver, KeyCollectionMode.OwnOnly, PropertyFilter.ENUMERABLE_STRINGS,
            GetKeysConversion.ConvertToString);
        return NewJSArray(isolate, keys);
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.getownpropertynames</summary>
    public static JSValue ObjectGetOwnPropertyNames(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);

        // Take the slow path if the {object} IsCustomElementsReceiverInstanceType or
        // has any elements.
        if (obj.HeapObjectOrNull is JSObject jsObject && !Map.IsCustomElementsReceiverMap(jsObject.Map) && HasNoElements(jsObject))
        {
            Map map = jsObject.Map;
            int enumLength = map.EnumLength;
            if (enumLength == Map.kInvalidEnumCacheSentinel)
            {
                // Let the runtime compute the elements and try initializing enum
                // cache (Runtime_ObjectGetOwnPropertyNamesTryFast).
                int nod = map.NumberOfOwnDescriptors;
                PropertyFilter filter = nod != 0 && map.NumberOfEnumerableProperties() == nod
                    ? PropertyFilter.ENUMERABLE_STRINGS
                    : PropertyFilter.SKIP_SYMBOLS;
                return NewJSArray(isolate,
                    KeyAccumulator.GetKeys(isolate, jsObject, KeyCollectionMode.OwnOnly, filter, GetKeysConversion.ConvertToString));
            }
            // Check whether all own properties are enumerable.
            if (enumLength == map.NumberOfOwnDescriptors)
            {
                if (enumLength == 0) return NewJSArray(isolate, FixedArray.Empty);
                // The {object} has a usable enum cache and all own properties are
                // enumerable, use that.
                return NewJSArray(isolate, CopyEnumCacheKeys(map, enumLength));
            }
        }

        // Let the runtime compute the elements (Runtime_ObjectGetOwnPropertyNames).
        // TODO(v8:9401): We should extend the fast path of KeyAccumulator::GetKeys to
        // also use fast path even when filter = SKIP_SYMBOLS.
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        return NewJSArray(isolate,
            KeyAccumulator.GetKeys(isolate, receiver, KeyCollectionMode.OwnOnly, PropertyFilter.SKIP_SYMBOLS,
                GetKeysConversion.ConvertToString));
    }

    /// <summary>ES6 section 19.1.2.8 Object.getOwnPropertySymbols ( O ).</summary>
    public static JSValue ObjectGetOwnPropertySymbols(Isolate isolate, in BuiltinArguments args) =>
        GetOwnPropertyKeys(isolate, args.AtOrUndefined(1), PropertyFilter.SKIP_STRINGS);

    static JSValue GetOwnPropertyKeys(Isolate isolate, JSValue obj, PropertyFilter filter)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        FixedArray keys = KeyAccumulator.GetKeys(isolate, receiver, KeyCollectionMode.OwnOnly, filter, GetKeysConversion.ConvertToString);
        return NewJSArray(isolate, keys);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool HasNoElements(JSObject obj)
    {
        FixedArrayBase elements = obj.Elements;
        // IsEmptyFixedArray || IsEmptySlowElementDictionary: a typed array's
        // (empty byte array) elements say nothing about its length.
        return ReferenceEquals(elements, FixedArray.Empty) || ReferenceEquals(elements, ReadOnlyRoots.empty_slow_element_dictionary);
    }

    static FixedArray CopyEnumCacheKeys(Map map, int enumLength)
    {
        FixedArray enumKeys = map.InstanceDescriptors.EnumCache.Keys;
        var result = new JSValue[enumLength];
        enumKeys.Data.AsSpan(0, enumLength).CopyTo(result);
        return new FixedArray(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSArray NewJSArray(Isolate isolate, FixedArray elements) => isolate.Factory.NewJSArrayWithElements(elements);

    /// <summary>ObjectValues (ObjectEntriesValuesBuiltinsAssembler::GetOwnValuesOrEntries).</summary>
    public static JSValue ObjectValues(Isolate isolate, in BuiltinArguments args) =>
        GetOwnValuesOrEntries(isolate, args.AtOrUndefined(1), entries: false);

    /// <summary>ObjectEntries (ObjectEntriesValuesBuiltinsAssembler::GetOwnValuesOrEntries).</summary>
    public static JSValue ObjectEntries(Isolate isolate, in BuiltinArguments args) =>
        GetOwnValuesOrEntries(isolate, args.AtOrUndefined(1), entries: true);

    static JSValue GetOwnValuesOrEntries(Isolate isolate, JSValue maybeObject, bool entries)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, maybeObject);
        // The CSA fast path (fast-mode JSObject without elements) and the
        // runtime fast path are one: JSReceiver::GetOwnValuesOrEntries with
        // try_fast_path, which walks the descriptors directly. Objects that are
        // not fast-mode JSObjects take the runtime's *SkipFastPath variants.
        bool tryFastPath = receiver is JSObject && !receiver.Map.IsDictionaryMap;
        FixedArray result = entries
            ? JSReceiver.GetOwnEntries(isolate, receiver, PropertyFilter.ENUMERABLE_STRINGS, tryFastPath)
            : JSReceiver.GetOwnValues(isolate, receiver, PropertyFilter.ENUMERABLE_STRINGS, tryFastPath);
        return NewJSArray(isolate, result);
    }

    /// <summary>
    /// Object.prototype.hasOwnProperty (TF_BUILTIN ObjectPrototypeHasOwnProperty
    /// and Runtime_ObjectHasOwnProperty).
    /// </summary>
    public static JSValue ObjectPrototypeHasOwnProperty(Isolate isolate, in BuiltinArguments args) =>
        HasOwnPropertyImpl(isolate, args.Receiver, args.AtOrUndefined(1));

    static JSValue HasOwnPropertyImpl(Isolate isolate, JSValue obj, JSValue property)
    {
        // Smi receivers do not have own properties, just perform ToPrimitive on the
        // key.
        // TODO(ishell): To improve performance, consider performing the to-string
        // conversion of {property} before calling into the runtime.
        PropertyKey key = PropertyKey.FromKey(isolate, property);

        switch (obj.HeapObjectOrNull)
        {
            case JSReceiver receiver:
                if (key.IsElement)
                {
                    var it = new LookupIterator(isolate, receiver, key, receiver, LookupIterator.Configuration.OWN);
                    if (receiver is JSObject) return JSValue.FromBoolean(JSReceiver.HasProperty(ref it));
                    return JSValue.FromBoolean(JSReceiver.GetPropertyAttributes(ref it) != PropertyAttributes.ABSENT);
                }
                return JSValue.FromBoolean(JSReceiver.HasOwnProperty(isolate, receiver, key.Name!));
            case JSString s:
                return JSValue.FromBoolean(key.IsElement
                    ? key.Index < (ulong)s.Length
                    : ReferenceEquals(key.Name, ReadOnlyRoots.length_string));
        }
        if (obj.IsNullOrUndefined) return isolate.ThrowTypeError(MessageTemplate.UndefinedOrNullToObject);
        return JSValue.False;
    }

    /// <summary>Object.hasOwn (https://github.com/tc39/proposal-accessible-object-hasownproperty).</summary>
    public static JSValue ObjectHasOwn(Isolate isolate, in BuiltinArguments args)
    {
        // Object.prototype.hasOwnProperty()
        // 1. Let obj be ? ToObject(O).
        // 2. Let key be ? ToPropertyKey(P).
        // 3. Return ? HasOwnProperty(obj, key).
        //
        // ObjectPrototypeHasOwnProperty has similar semantics with steps 1 and 2
        // swapped. We check if ToObject can fail and delegate the rest of the
        // execution to ObjectPrototypeHasOwnProperty.
        JSValue obj = args.AtOrUndefined(1);
        if (obj.IsNullOrUndefined) return isolate.ThrowTypeError(MessageTemplate.UndefinedOrNullToObject);
        return HasOwnPropertyImpl(isolate, obj, args.AtOrUndefined(2));
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.isprototypeof</summary>
    public static JSValue ObjectPrototypeIsPrototypeOf(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        JSValue value = args.AtOrUndefined(1);

        // We only check whether {value} is a Smi here, so that the
        // prototype chain walk below can safely access the {value}s
        // map. We don't rule out Primitive {value}s, since all of
        // them have null as their prototype, so the chain walk below
        // immediately aborts and returns false anyways.
        if (!value.IsHeapObject) return JSValue.False;

        // Check if {receiver} is either null or undefined and in that case,
        // invoke the ToObject builtin, which raises the appropriate error.
        if (receiver.IsNullOrUndefined)
        {
            // If {value} is a primitive HeapObject, we need to return
            // false instead of throwing an exception per order of the
            // steps in the specification, so check that first here.
            if (!value.IsJSReceiver) return JSValue.False;
            // Simulate the ToObject invocation on {receiver}.
            ObjectOps.ToObject(isolate, receiver);
        }
        if (value.HeapObjectOrNull is not JSReceiver valueReceiver) return JSValue.False;

        // Loop through the prototype chain looking for the {receiver}.
        return JSValue.FromBoolean(JSReceiver.HasInPrototypeChain(isolate, valueReceiver, receiver));
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.tostring</summary>
    public static JSValue ObjectPrototypeToString(Isolate isolate, in BuiltinArguments args) =>
        ObjectToString(isolate, args.Receiver);

    /// <summary>The ObjectToString builtin (Object.prototype.toString and %ObjectProtoToString).</summary>
    public static JSString ObjectToString(Isolate isolate, JSValue receiver)
    {
        HeapObject? heapObject = receiver.HeapObjectOrNull;
        JSString defaultTag;
        JSReceiver holder;
        NativeContext nativeContext = isolate.NativeContext;

        // This is arranged to check the likely cases first.
        if (receiver.IsNumber)
        {
            defaultTag = ReadOnlyRoots.number_to_string;
            holder = PrimitivePrototype(nativeContext.NumberFunction);
        }
        else switch (heapObject)
        {
            case null:
                return ReadOnlyRoots.undefined_to_string;
            case JSString:
                defaultTag = ReadOnlyRoots.string_to_string;
                holder = PrimitivePrototype(nativeContext.StringFunction);
                break;
            case Symbol:
                defaultTag = ReadOnlyRoots.object_to_string;
                holder = PrimitivePrototype(nativeContext.SymbolFunction);
                break;
            case BigInt:
                defaultTag = ReadOnlyRoots.object_to_string;
                holder = PrimitivePrototype(nativeContext.BigIntFunction);
                break;
            case Oddball:
                if (receiver.IsBoolean)
                {
                    defaultTag = ReadOnlyRoots.boolean_to_string;
                    holder = PrimitivePrototype(nativeContext.BooleanFunction);
                    break;
                }
                if (receiver.IsNull) return ReadOnlyRoots.null_to_string;
                return ReadOnlyRoots.undefined_to_string;
            case JSProxy proxy:
                return ProxyToString(isolate, proxy);
            case JSReceiver jsReceiver:
                holder = jsReceiver;
                defaultTag = DefaultReceiverTag(jsReceiver);
                break;
            default:
                // Internal objects never reach JavaScript.
                return ReadOnlyRoots.object_to_string;
        }

        // checkstringtag: GetInterestingProperty(receiver, holder, @@toStringTag).
        JSValue tag = GetInterestingProperty(isolate, receiver, holder, ReadOnlyRoots.to_string_tag_symbol);
        if (tag.HeapObjectOrNull is JSString tagString) return ToStringFormat(isolate, tagString);
        return defaultTag;
    }

    static JSReceiver PrimitivePrototype(JSFunction constructor) => constructor.InitialMap.Prototype!;

    static JSString DefaultReceiverTag(JSReceiver receiver)
    {
        if (receiver.Map.IsCallable && receiver is JSFunctionOrBoundFunctionOrWrappedFunction)
        {
            return ReadOnlyRoots.function_to_string;
        }
        switch (receiver.InstanceType)
        {
            case InstanceType.JSArrayType: return ReadOnlyRoots.array_to_string;
            case InstanceType.JSRegExpType: return ReadOnlyRoots.regexp_to_string;
            case InstanceType.JSArgumentsObjectType: return ReadOnlyRoots.arguments_to_string;
            case InstanceType.JSDateType: return ReadOnlyRoots.date_to_string;
            case InstanceType.JSErrorType: return ReadOnlyRoots.error_to_string;
            case InstanceType.JSPrimitiveWrapperType:
            {
                // We need to start with the object to see if the value was a subclass
                // which might have interesting properties.
                JSValue value = ((JSPrimitiveWrapper)receiver).Value;
                if (value.IsNumber) return ReadOnlyRoots.number_to_string;
                if (value.IsBoolean) return ReadOnlyRoots.boolean_to_string;
                if (value.IsString) return ReadOnlyRoots.string_to_string;
                // Symbol and BigInt wrappers.
                return ReadOnlyRoots.object_to_string;
            }
            default:
                return ReadOnlyRoots.object_to_string;
        }
    }

    /// <summary>
    /// CodeStubAssembler::GetInterestingProperty: [[Get]] of an interesting
    /// symbol with the lookup starting at <paramref name="holder"/>; returns
    /// undefined without a lookup when no map on the chain may have
    /// interesting properties.
    /// </summary>
    static JSValue GetInterestingProperty(Isolate isolate, JSValue receiver, JSReceiver holder, Symbol name)
    {
        JSReceiver? current = holder;
        while (current is not null)
        {
            Map map = current.Map;
            if (map.MayHaveInterestingProperties || map.IsDictionaryMap || Map.IsSpecialReceiverMap(map))
            {
                var it = new LookupIterator(isolate, receiver, name, holder);
                return ObjectOps.GetProperty(ref it);
            }
            current = map.Prototype;
        }
        return JSValue.Undefined;
    }

    static JSString ProxyToString(Isolate isolate, JSProxy proxy)
    {
        // Check if the proxy has been revoked.
        if (!proxy.Handler.IsJSReceiver)
        {
            isolate.ThrowTypeError(MessageTemplate.ProxyRevoked, isolate.Factory.NewStringFromAsciiChecked("Object.prototype.toString"));
        }

        // If {receiver_heap_object} is a proxy for a JSArray, we default to
        // "[object Array]", otherwise we default to "[object Object]" or "[object
        // Function]" here, depending on whether the {receiver_heap_object} is
        // callable. The order matters here, i.e. we need to execute the
        // %ArrayIsArray check before the [[Get]] below, as the exception is
        // observable.
        JSString builtinTag = ObjectOps.IsArray(isolate, proxy)
            ? ReadOnlyRoots.Array_string
            : proxy.Map.IsCallable ? ReadOnlyRoots.Function_string : ReadOnlyRoots.Object_string;

        // Lookup the @@toStringTag property on the {receiver_heap_object}.
        JSValue tag = JSReceiver.GetProperty(isolate, proxy, ReadOnlyRoots.to_string_tag_symbol);
        return ToStringFormat(isolate, tag.HeapObjectOrNull as JSString ?? builtinTag);
    }

    /// <summary>ObjectBuiltinsAssembler::ReturnToStringFormat: "[object " + tag + "]".</summary>
    static JSString ToStringFormat(Isolate isolate, JSString tag)
    {
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("[object ");
        builder.AppendString(tag);
        builder.AppendCharacter(']');
        return builder.Finish();
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.create</summary>
    public static JSValue ObjectCreate(Isolate isolate, in BuiltinArguments args)
    {
        JSValue maybePrototype = args.AtOrUndefined(1);
        JSValue properties = args.AtOrUndefined(2);

        // 1. If Type(O) is neither Object nor Null, throw a TypeError exception.
        if (!maybePrototype.IsNull && !maybePrototype.IsJSReceiver)
        {
            return isolate.ThrowTypeError(MessageTemplate.ProtoObjectOrNull, maybePrototype);
        }

        // 2. Let obj be ObjectCreate(O).
        JSObject obj = JSObject.ObjectCreate(isolate, maybePrototype.AsOrNull<JSReceiver>());

        // 3. If Properties is not undefined, then
        if (!properties.IsUndefined)
        {
            // a. Return ? ObjectDefineProperties(obj, Properties).
            // Define the properties if properties was specified and is not undefined.
            return JSReceiver.DefineProperties(isolate, obj, properties);
        }
        // 4. Return obj.
        return obj;
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.is</summary>
    public static JSValue ObjectIs(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(ObjectOps.SameValue(args.AtOrUndefined(1), args.AtOrUndefined(2)));

    /// <summary>ES6 section 19.1.2.7 Object.getOwnPropertyDescriptor ( O, P ).</summary>
    public static JSValue ObjectGetOwnPropertyDescriptor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let obj be ? ToObject(O).
        JSReceiver obj = ObjectOps.ToObject(isolate, args.AtOrUndefined(1));
        // 2. Let key be ? ToPropertyKey(P).
        Name key = ObjectOps.ToName(isolate, args.AtOrUndefined(2));
        // 3. Let desc be ? obj.[[GetOwnProperty]](key).
        // 4. Return FromPropertyDescriptor(desc).
        return GetOwnPropertyDescriptorObject(isolate, obj, key);
    }

    /// <summary>
    /// The GetOwnPropertyDescriptor builtin followed by FromPropertyDescriptor:
    /// the descriptor object, or undefined.
    /// </summary>
    internal static JSValue GetOwnPropertyDescriptorObject(Isolate isolate, JSReceiver obj, Name key)
    {
        var desc = new PropertyDescriptor();
        if (!JSReceiver.GetOwnPropertyDescriptor(isolate, obj, key, ref desc)) return JSValue.Undefined;
        return desc.ToObject(isolate);
    }

    // ---------------------------------------------------------------------
    // builtins-object.cc

    /// <summary>ES6 section 19.1.3.4 Object.prototype.propertyIsEnumerable ( V ).</summary>
    public static JSValue ObjectPrototypePropertyIsEnumerable(Isolate isolate, in BuiltinArguments args)
    {
        Name name = ObjectOps.ToName(isolate, args.AtOrUndefined(1));
        JSReceiver obj = ObjectOps.ToObject(isolate, args.Receiver);
        PropertyAttributes attributes = JSReceiver.GetOwnPropertyAttributes(isolate, obj, name);
        if (attributes == PropertyAttributes.ABSENT) return JSValue.False;
        return JSValue.FromBoolean((attributes & PropertyAttributes.DONT_ENUM) == 0);
    }

    /// <summary>ES6 section 19.1.2.3 Object.defineProperties.</summary>
    public static JSValue ObjectDefineProperties(Isolate isolate, in BuiltinArguments args) =>
        JSReceiver.DefineProperties(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2));

    /// <summary>ES6 section 19.1.2.4 Object.defineProperty.</summary>
    public static JSValue ObjectDefineProperty(Isolate isolate, in BuiltinArguments args) =>
        JSReceiver.DefineProperty(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3));

    static JSValue ObjectDefineAccessor(Isolate isolate, JSValue obj, JSValue name, JSValue accessor, AccessorComponent whichAccessor)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        // 2. If IsCallable(getter) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(accessor))
        {
            return isolate.ThrowTypeError(whichAccessor == AccessorComponent.ACCESSOR_GETTER
                ? MessageTemplate.ObjectGetterExpectingFunction
                : MessageTemplate.ObjectSetterExpectingFunction);
        }
        // 3. Let desc be PropertyDescriptor{[[Get]]: getter, [[Enumerable]]: true,
        //                                   [[Configurable]]: true}.
        var desc = new PropertyDescriptor();
        if (whichAccessor == AccessorComponent.ACCESSOR_GETTER) desc.SetGet(accessor);
        else desc.SetSet(accessor);
        desc.SetEnumerable(true);
        desc.SetConfigurable(true);
        // 4. Let key be ? ToPropertyKey(P).
        JSValue key = ObjectOps.ToPropertyKey(isolate, name);
        // 5. Perform ? DefinePropertyOrThrow(O, key, desc).
        // To preserve legacy behavior, we ignore errors silently rather than
        // throwing an exception.
        bool success = JSReceiver.DefineOwnProperty(isolate, receiver, key, ref desc, ShouldThrow.ThrowOnError);
        if (!success) isolate.CountUsage("kDefineGetterOrSetterWouldThrow");
        // 6. Return undefined.
        return JSValue.Undefined;
    }

    static JSValue ObjectLookupAccessor(Isolate isolate, JSValue obj, JSValue key, AccessorComponent component)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, obj);
        // TODO(jkummerow/verwaest): PropertyKey(..., bool*) performs a
        // functionally equivalent conversion, but handles element indices slightly
        // differently. Does one of the approaches have a performance advantage?
        key = ObjectOps.ToPropertyKey(isolate, key);
        var lookupKey = new PropertyKey(isolate, key);
        var it = new LookupIterator(isolate, receiver, lookupKey, receiver, LookupIterator.Configuration.PROTOTYPE_CHAIN_SKIP_INTERCEPTOR);

        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.INTERCEPTOR:
                case LookupIterator.StateKind.TRANSITION:
                    throw new InvalidOperationException("unreachable");

                case LookupIterator.StateKind.ACCESS_CHECK:
                    if (it.HasAccess()) continue;
                    isolate.ReportFailedAccessCheck(it.GetHolder<JSObject>());
                    return JSValue.Undefined;

                case LookupIterator.StateKind.JSPROXY:
                {
                    var desc = new PropertyDescriptor();
                    bool found = JSProxy.GetOwnPropertyDescriptor(isolate, it.GetHolder<JSProxy>(), it.GetName(), ref desc);
                    if (found)
                    {
                        if (component == AccessorComponent.ACCESSOR_GETTER && desc.HasGet) return desc.Get;
                        if (component == AccessorComponent.ACCESSOR_SETTER && desc.HasSet) return desc.Set;
                        return JSValue.Undefined;
                    }
                    JSReceiver? prototype = JSProxy.GetPrototype(isolate, it.GetHolder<JSProxy>());
                    if (prototype is null) return JSValue.Undefined;
                    return ObjectLookupAccessor(isolate, prototype, key, component);
                }
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                case LookupIterator.StateKind.DATA:
                case LookupIterator.StateKind.NOT_FOUND:
                    return JSValue.Undefined;
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    // Deferred module namespaces (import defer) are not ported.
                    continue;
                case LookupIterator.StateKind.ACCESSOR:
                {
                    HeapObject maybePair = it.GetAccessors();
                    if (maybePair is AccessorPair pair) return AccessorPair.GetComponent(pair, component);
                    continue;
                }
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.__defineGetter__</summary>
    public static JSValue ObjectDefineGetter(Isolate isolate, in BuiltinArguments args) =>
        ObjectDefineAccessor(isolate, args.Receiver, args.AtOrUndefined(1), args.AtOrUndefined(2), AccessorComponent.ACCESSOR_GETTER);

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.__defineSetter__</summary>
    public static JSValue ObjectDefineSetter(Isolate isolate, in BuiltinArguments args) =>
        ObjectDefineAccessor(isolate, args.Receiver, args.AtOrUndefined(1), args.AtOrUndefined(2), AccessorComponent.ACCESSOR_SETTER);

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.__lookupGetter__</summary>
    public static JSValue ObjectLookupGetter(Isolate isolate, in BuiltinArguments args) =>
        ObjectLookupAccessor(isolate, args.Receiver, args.AtOrUndefined(1), AccessorComponent.ACCESSOR_GETTER);

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.__lookupSetter__</summary>
    public static JSValue ObjectLookupSetter(Isolate isolate, in BuiltinArguments args) =>
        ObjectLookupAccessor(isolate, args.Receiver, args.AtOrUndefined(1), AccessorComponent.ACCESSOR_SETTER);

    /// <summary>ES6 section 19.1.2.5 Object.freeze ( O ).</summary>
    public static JSValue ObjectFreeze(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        if (obj.HeapObjectOrNull is JSReceiver receiver)
        {
            JSReceiver.SetIntegrityLevel(isolate, receiver, JSReceiver.IntegrityLevel.FROZEN, ShouldThrow.ThrowOnError);
        }
        return obj;
    }

    /// <summary>ES6 section 19.1.2.17 Object.seal ( O ).</summary>
    public static JSValue ObjectSeal(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        if (obj.HeapObjectOrNull is JSReceiver receiver)
        {
            JSReceiver.SetIntegrityLevel(isolate, receiver, JSReceiver.IntegrityLevel.SEALED, ShouldThrow.ThrowOnError);
        }
        return obj;
    }

    /// <summary>ES6 section 19.1.2.12 Object.isFrozen ( O ).</summary>
    public static JSValue ObjectIsFrozen(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        bool result = obj.HeapObjectOrNull is not JSReceiver receiver ||
                      JSReceiver.TestIntegrityLevel(isolate, receiver, JSReceiver.IntegrityLevel.FROZEN);
        return JSValue.FromBoolean(result);
    }

    /// <summary>ES6 section 19.1.2.13 Object.isSealed ( O ).</summary>
    public static JSValue ObjectIsSealed(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        bool result = obj.HeapObjectOrNull is not JSReceiver receiver ||
                      JSReceiver.TestIntegrityLevel(isolate, receiver, JSReceiver.IntegrityLevel.SEALED);
        return JSValue.FromBoolean(result);
    }

    /// <summary>ES6 section B.2.2.1.1 get Object.prototype.__proto__.</summary>
    public static JSValue ObjectPrototypeGetProto(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? ToObject(this value).
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.Receiver);
        // 2. Return ? O.[[GetPrototypeOf]]().
        return JSValue.FromObject(JSReceiver.GetPrototype(isolate, receiver) ?? (HeapObject)Oddball.Null);
    }

    /// <summary>ES6 section B.2.2.1.2 set Object.prototype.__proto__.</summary>
    public static JSValue ObjectPrototypeSetProto(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be ? RequireObjectCoercible(this value).
        JSValue obj = args.Receiver;
        if (obj.IsNullOrUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined,
                isolate.Factory.NewStringFromAsciiChecked("set Object.prototype.__proto__"));
        }

        // 2. If Type(proto) is neither Object nor Null, return undefined.
        JSValue proto = args.AtOrUndefined(1);
        if (!proto.IsNull && !proto.IsJSReceiver) return JSValue.Undefined;

        // 3. If Type(O) is not Object, return undefined.
        if (obj.HeapObjectOrNull is not JSReceiver receiver) return JSValue.Undefined;

        // 4. Let status be ? O.[[SetPrototypeOf]](proto).
        // 5. If status is false, throw a TypeError exception.
        JSReceiver.SetPrototype(isolate, receiver, proto, true, ShouldThrow.ThrowOnError);

        // Return undefined.
        return JSValue.Undefined;
    }

    /// <summary>Object.getOwnPropertyDescriptors ( O ).</summary>
    public static JSValue ObjectGetOwnPropertyDescriptors(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.AtOrUndefined(1));

        FixedArray keys = KeyAccumulator.GetKeys(isolate, receiver, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.ConvertToString);

        JSObject descriptors = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);

        int keysLength = keys.Length;
        for (int i = 0; i < keysLength; ++i)
        {
            Name key = keys[i].As<Name>();
            var descriptor = new PropertyDescriptor();
            bool didGetDescriptor = JSReceiver.GetOwnPropertyDescriptor(isolate, receiver, key, ref descriptor);
            if (!didGetDescriptor) continue;
            JSObject fromDescriptor = descriptor.ToObject(isolate);
            bool success = JSReceiver.CreateDataProperty(isolate, descriptors, key, fromDescriptor, ShouldThrow.DontThrow);
            Debug.Assert(success);
        }
        return descriptors;
    }

    // ---------------------------------------------------------------------
    // object.tq

    /// <summary>ES6 section 19.1.2.11 Object.isExtensible ( O ).</summary>
    public static JSValue ObjectIsExtensible(Isolate isolate, in BuiltinArguments args)
    {
        if (args.AtOrUndefined(1).HeapObjectOrNull is not JSReceiver receiver) return JSValue.False;
        return JSValue.FromBoolean(JSReceiver.IsExtensible(isolate, receiver));
    }

    /// <summary>ES6 section 19.1.2.18 Object.preventExtensions ( O ).</summary>
    public static JSValue ObjectPreventExtensions(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        if (obj.HeapObjectOrNull is JSReceiver receiver)
        {
            JSReceiver.PreventExtensions(isolate, receiver, ShouldThrow.ThrowOnError);
        }
        return obj;
    }

    /// <summary>ES6 section 19.1.2.9 Object.getPrototypeOf ( O ).</summary>
    public static JSValue ObjectGetPrototypeOf(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver receiver = ObjectOps.ToObject(isolate, args.AtOrUndefined(1));
        return PrototypeOrNull(JSReceiver.GetPrototype(isolate, receiver));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JSValue PrototypeOrNull(JSReceiver? prototype) => prototype is null ? JSValue.Null : prototype;

    /// <summary>ES6 section 19.1.2.21 Object.setPrototypeOf ( O, proto ).</summary>
    public static JSValue ObjectSetPrototypeOf(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);
        JSValue proto = args.AtOrUndefined(2);

        // 1. Set O to ? RequireObjectCoercible(O).
        RequireObjectCoercible(isolate, obj, "Object.setPrototypeOf");

        // 2. If Type(proto) is neither Object nor Null, throw a TypeError
        // exception.
        if (!proto.IsNull && !proto.IsJSReceiver) return isolate.ThrowTypeError(MessageTemplate.ProtoObjectOrNull, proto);

        // 3. If Type(O) is not Object, return O.
        // 4. Let status be ? O.[[SetPrototypeOf]](proto).
        // 5. If status is false, throw a TypeError exception.
        // 6. Return O.
        if (obj.HeapObjectOrNull is JSReceiver receiver)
        {
            JSReceiver.SetPrototype(isolate, receiver, proto, true, ShouldThrow.ThrowOnError);
        }
        return obj;
    }

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.valueof</summary>
    public static JSValue ObjectPrototypeValueOf(Isolate isolate, in BuiltinArguments args) =>
        // 1. Return ? ToObject(this value).
        ObjectOps.ToObject(isolate, args.Receiver);

    /// <summary>https://tc39.es/ecma262/#sec-object.prototype.tolocalestring</summary>
    public static JSValue ObjectPrototypeToLocaleString(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        // 1. Let O be the this value.
        // 2. Return ? Invoke(O, "toString").
        if (receiver.IsNullOrUndefined)
        {
            return isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined,
                isolate.Factory.NewStringFromAsciiChecked("Object.prototype.toLocaleString"));
        }
        JSValue method = ObjectOps.GetProperty(isolate, receiver, ReadOnlyRoots.toString_string);
        return Execution.Call(isolate, method, receiver, []);
    }

    /// <summary>RequireObjectCoercible(value, name): "% called on null or undefined".</summary>
    internal static void RequireObjectCoercible(Isolate isolate, JSValue value, string methodName)
    {
        if (value.IsNullOrUndefined)
        {
            isolate.ThrowTypeError(MessageTemplate.CalledOnNullOrUndefined, isolate.Factory.NewStringFromAsciiChecked(methodName));
        }
    }

    // ---------------------------------------------------------------------
    // object-fromentries.tq

    /// <summary>Object.fromEntries ( iterable ).</summary>
    public static JSValue ObjectFromEntries(Isolate isolate, in BuiltinArguments args)
    {
        JSValue iterable = args.AtOrUndefined(1);
        if (iterable.IsNullOrUndefined) return isolate.ThrowTypeError(MessageTemplate.NotIterable);

        JSObject? fast = ObjectFromEntriesFastCase(isolate, iterable);
        if (fast is not null) return fast;

        JSObject result = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        IteratorRecord i = IteratorBuiltins.GetIterator(isolate, iterable);
        try
        {
            while (true)
            {
                if (!IteratorBuiltins.IteratorStep(isolate, i, out JSReceiver step)) return result;
                JSValue iteratorValue = IteratorBuiltins.IteratorValue(isolate, step);
                LoadKeyValuePair(isolate, iteratorValue, out JSValue key, out JSValue value);
                CreateDataProperty(isolate, result, key, value);
            }
        }
        catch (JavaScriptException)
        {
            IteratorBuiltins.IteratorCloseOnException(isolate, i.Object);
            throw;
        }
    }

    static JSObject? ObjectFromEntriesFastCase(Isolate isolate, JSValue iterable)
    {
        if (iterable.HeapObjectOrNull is not JSArray array || !IteratorBuiltins.IsFastJSArrayWithNoCustomIteration(isolate, array))
        {
            return null;
        }
        if (array.Elements is not FixedArray elements) return null;
        int length = (int)array.Length.Number;
        // Validate every pair before creating properties: V8 creates properties
        // as it goes and bails out to the slow path on the first pair it
        // cannot handle, restarting from scratch; creating properties has no
        // side effects on the pairs, so checking first gives the same result.
        for (int k = 0; k < length; ++k)
        {
            JSValue value = k < elements.Length ? elements[k] : JSValue.TheHole;
            if (!LoadKeyValuePairNoSideEffects(isolate, value, out JSValue key, out _)) return null;
            if (!(key.IsName || key.IsNumber || key.IsOddball)) return null;
        }
        JSObject result = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        for (int k = 0; k < length; ++k)
        {
            JSValue value = k < elements.Length ? elements[k] : JSValue.TheHole;
            LoadKeyValuePairNoSideEffects(isolate, value, out JSValue key, out JSValue pairValue);
            if (key.HeapObjectOrNull is Oddball oddball && !key.IsTheHole) key = isolate.Factory.InternalizeString(oddball.ToStringValue);
            CreateDataProperty(isolate, result, key, pairValue);
        }
        return result;
    }

    /// <summary>
    /// collections::LoadKeyValuePairNoSideEffects: a fast JSArray pair [k, v]
    /// (missing entries read as undefined).
    /// </summary>
    static bool LoadKeyValuePairNoSideEffects(Isolate isolate, JSValue o, out JSValue key, out JSValue value)
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
    internal static void LoadKeyValuePair(Isolate isolate, JSValue o, out JSValue key, out JSValue value)
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

    /// <summary>CreateDataProperty(o, key, value) with a Name or Number key; other keys go through ToPropertyKey.</summary>
    internal static void CreateDataProperty(Isolate isolate, JSObject obj, JSValue key, JSValue value)
    {
        PropertyKey lookupKey = key.IsName || key.IsNumber ? new PropertyKey(isolate, key) : PropertyKey.FromKey(isolate, key);
        JSReceiver.CreateDataProperty(isolate, obj, lookupKey, value, ShouldThrow.ThrowOnError);
    }

    // ---------------------------------------------------------------------
    // object-groupby.tq

    /// <summary>Object.groupBy ( items, callbackfn ).</summary>
    public static JSValue ObjectGroupBy(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let groups be ? GroupBy(items, callbackfn, property).
        var groups = new PropertyGroups();
        GroupByImpl(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), ref groups, "Object.groupBy");

        // 2. Let obj be OrdinaryObjectCreate(null).
        // 3. For each Record { [[Key]], [[Elements]] } g of groups, do
        //   a. Let elements be CreateArrayFromList(g.[[Elements]]).
        //   b. Perform ! CreateDataPropertyOrThrow(obj, g.[[Key]], elements).
        Map nullProtoMap = isolate.NativeContext.SlowObjectWithNullPrototypeMap;
        JSObject obj = isolate.Factory.NewSlowJSObjectFromMap(nullProtoMap, Math.Max(groups.Count, NameDictionary.kInitialCapacity));
        for (int i = 0; i < groups.Count; i++)
        {
            ref PropertyGroup group = ref groups.At(i);
            var elements = new FixedArray(group.Count);
            group.Elements.AsSpan(0, group.Count).CopyTo(elements.Data);
            JSArray array = isolate.Factory.NewJSArrayWithElements(elements);
            JSReceiver.CreateDataProperty(isolate, obj, new PropertyKey(isolate, group.Key), array, ShouldThrow.ThrowOnError);
        }
        // 4. Return obj.
        return obj;
    }

    /// <summary>
    /// GroupBy with coercion "property": ToPropertyKey of each callback result
    /// (object-groupby.tq GroupByImpl / GroupByGeneric).
    /// </summary>
    static void GroupByImpl(Isolate isolate, JSValue items, JSValue callback, ref PropertyGroups groups, string methodName)
    {
        // 1. Perform ? RequireObjectCoercible(items).
        RequireObjectCoercible(isolate, items, methodName);

        // 2. If IsCallable(callbackfn) is false, throw a TypeError exception.
        if (!ObjectOps.IsCallable(callback)) isolate.ThrowTypeError(MessageTemplate.CalledNonCallable, callback);

        if (items.HeapObjectOrNull is JSArray array && IteratorBuiltins.IsFastJSArrayWithNoCustomIteration(isolate, array))
        {
            // Per spec, the iterator and its next method are cached up front. This
            // means that we only need to check for no custom iteration once up
            // front. Even though the grouping callback has arbitrary side effects,
            // mutations to %ArrayIteratorPrototype% will not be reflected during
            // the iteration itself. The array iterator logic is encoded here
            // directly (V8's fast witness path and SlowArrayContinuation agree on
            // the values read).
            JSValue[] callArgs = new JSValue[2];
            for (uint index = 0; index < (uint)array.Length.Number; ++index)
            {
                JSValue value = JSReceiver.GetElement(isolate, array, index);
                callArgs[0] = value;
                callArgs[1] = JSValue.FromNumber(index);
                JSValue key = Execution.Call(isolate, callback, JSValue.Undefined, callArgs);
                groups.Add(isolate, ObjectOps.ToName(isolate, key), value);
            }
            return;
        }

        // GroupByGeneric.
        // 4. Let iteratorRecord be ? GetIterator(items, sync).
        IteratorRecord iteratorRecord = IteratorBuiltins.GetIterator(isolate, items);
        // 5. Let k be 0.
        double k = 0;
        JSValue[] genericArgs = new JSValue[2];
        // 6. Repeat,
        while (true)
        {
            // b. Let next be ? IteratorStep(iteratorRecord).
            // c. If next is false, then return groups.
            if (!IteratorBuiltins.IteratorStep(isolate, iteratorRecord, out JSReceiver next)) return;
            // d. Let value be ? IteratorValue(next).
            JSValue value = IteratorBuiltins.IteratorValue(isolate, next);
            // e. Let key be Completion(Call(callbackfn, undefined, « value, 𝔽(k) »)).
            Name key;
            try
            {
                genericArgs[0] = value;
                genericArgs[1] = JSValue.FromNumber(k);
                JSValue result = Execution.Call(isolate, callback, JSValue.Undefined, genericArgs);
                key = ObjectOps.ToName(isolate, result);
            }
            catch (JavaScriptException)
            {
                // f. and g.ii.
                // IfAbruptCloseIterator(key, iteratorRecord).
                IteratorBuiltins.IteratorCloseOnException(isolate, iteratorRecord.Object);
                throw;
            }
            // i. Perform AddValueToKeyedGroup(groups, key, value).
            groups.Add(isolate, key, value);
            // j. Set k to k + 1.
            k += 1;
        }
    }

    struct PropertyGroup
    {
        public Name Key;
        public JSValue[] Elements;
        public int Count;
    }

    /// <summary>
    /// The groups of Object.groupBy in first-insertion order. V8 keeps them in an
    /// OrderedHashMap of ArrayLists; property keys are unique names here, so a
    /// Dictionary over the internalized key gives the same grouping.
    /// </summary>
    struct PropertyGroups
    {
        Dictionary<Name, int>? _index;
        PropertyGroup[]? _groups;

        public int Count { get; private set; }

        public ref PropertyGroup At(int i) => ref _groups![i];

        public void Add(Isolate isolate, Name key, JSValue value)
        {
            key = isolate.Factory.InternalizeName(key);
            _index ??= new Dictionary<Name, int>(ReferenceEqualityComparer.Instance);
            _groups ??= new PropertyGroup[4];
            if (!_index.TryGetValue(key, out int i))
            {
                i = Count++;
                if (i == _groups.Length) Array.Resize(ref _groups, i * 2);
                _groups[i] = new PropertyGroup { Key = key, Elements = new JSValue[1], Count = 0 };
                _index.Add(key, i);
            }
            ref PropertyGroup group = ref _groups[i];
            if (group.Count == group.Elements.Length) Array.Resize(ref group.Elements, group.Count * 2);
            group.Elements[group.Count++] = value;
        }
    }
}
