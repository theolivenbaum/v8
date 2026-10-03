// Port of src/objects/property-descriptor.{h,cc}: the spec's Property
// Descriptor record, and its conversion from and to objects.
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's PropertyDescriptor. A mutable struct; pass it by ref.</summary>
public struct PropertyDescriptor
{
    bool _enumerable;
    bool _hasEnumerable;
    bool _configurable;
    bool _hasConfigurable;
    bool _writable;
    bool _hasWritable;
    JSValue _value;
    bool _hasValue;
    JSValue _get;
    bool _hasGet;
    JSValue _set;
    bool _hasSet;
    JSValue _name;

    public static bool IsAccessorDescriptor(in PropertyDescriptor desc) => desc._hasGet || desc._hasSet;
    public static bool IsDataDescriptor(in PropertyDescriptor desc) => desc._hasValue || desc._hasWritable;
    public static bool IsGenericDescriptor(in PropertyDescriptor desc) => !IsAccessorDescriptor(desc) && !IsDataDescriptor(desc);

    public readonly bool IsEmpty =>
        !_hasEnumerable && !_hasConfigurable && !_hasWritable && !_hasValue && !_hasGet && !_hasSet;

    public readonly bool IsRegularAccessorProperty() =>
        _hasConfigurable && _hasEnumerable && !_hasValue && !_hasWritable && _hasGet && _hasSet;

    public readonly bool IsRegularDataProperty() =>
        _hasConfigurable && _hasEnumerable && _hasValue && _hasWritable && !_hasGet && !_hasSet;

    public readonly bool Enumerable => _enumerable;
    public void SetEnumerable(bool enumerable)
    {
        _enumerable = enumerable;
        _hasEnumerable = true;
    }
    public readonly bool HasEnumerable => _hasEnumerable;

    public readonly bool Configurable => _configurable;
    public void SetConfigurable(bool configurable)
    {
        _configurable = configurable;
        _hasConfigurable = true;
    }
    public readonly bool HasConfigurable => _hasConfigurable;

    public readonly JSValue Value => _value;
    public void SetValue(JSValue value)
    {
        _value = value;
        _hasValue = true;
    }
    public readonly bool HasValue => _hasValue;

    public readonly bool Writable => _writable;
    public void SetWritable(bool writable)
    {
        _writable = writable;
        _hasWritable = true;
    }
    public readonly bool HasWritable => _hasWritable;

    public readonly JSValue Get => _get;
    public void SetGet(JSValue get)
    {
        _get = get;
        _hasGet = true;
    }
    public readonly bool HasGet => _hasGet;

    public readonly JSValue Set => _set;
    public void SetSet(JSValue set)
    {
        _set = set;
        _hasSet = true;
    }
    public readonly bool HasSet => _hasSet;

    public readonly JSValue Name => _name;
    public void SetName(JSValue name) => _name = name;

    public readonly PropertyAttributes ToAttributes() =>
        (_hasEnumerable && !_enumerable ? PropertyAttributes.DONT_ENUM : PropertyAttributes.NONE) |
        (_hasConfigurable && !_configurable ? PropertyAttributes.DONT_DELETE : PropertyAttributes.NONE) |
        (_hasWritable && !_writable ? PropertyAttributes.READ_ONLY : PropertyAttributes.NONE);

    // Helper function for ToPropertyDescriptor. Returns whether the property
    // exists; throws if the lookup or the get throws.
    static bool GetPropertyIfPresent(Isolate isolate, JSReceiver receiver, JSString name, out JSValue value)
    {
        var it = new LookupIterator(isolate, receiver, name, receiver);
        // 4. Let hasEnumerable be HasProperty(Obj, "enumerable").
        // 6. If hasEnumerable is true, then
        if (JSReceiver.HasProperty(ref it))
        {
            // 6a. Let enum be ToBoolean(Get(Obj, "enumerable")).
            value = ObjectOps.GetProperty(ref it);
            return true;
        }
        value = JSValue.Undefined;
        return false;
    }

    // Helper function for ToPropertyDescriptor. Handles the case of "simple"
    // objects: nothing on the prototype chain, just own fast data properties.
    // Must not have observable side effects, because the slow path will restart
    // the entire conversion!
    static bool ToPropertyDescriptorFastPath(Isolate isolate, JSReceiver obj, ref PropertyDescriptor desc)
    {
        if (obj is not JSObject jsObject) return false;
        Map map = jsObject.Map;
        if (map.InstanceType != InstanceType.JSObjectType) return false;
        if (map.IsAccessCheckNeeded) return false;
        NativeContext nc = isolate.NativeContext;
        if (!ReferenceEquals(map.Prototype, nc.InitialObjectPrototype)) return false;
        // During bootstrapping, the object_function_prototype_map hasn't been
        // set up yet.
        if (isolate.BootstrapperActive) return false;
        if (!ReferenceEquals(map.Prototype!.Map, nc.ObjectFunctionPrototypeMap)) return false;
        // TODO(jkummerow): support dictionary properties?
        if (map.IsDictionaryMap) return false;

        DescriptorArray descs = map.InstanceDescriptors;
        int n = map.NumberOfOwnDescriptors;
        for (int i = 0; i < n; i++)
        {
            var index = new InternalIndex(i);
            PropertyDetails details = descs.GetDetails(index);
            if (details.Kind != PropertyKind.Data) return false;  // Bail out to slow path.
            JSValue value = details.Location == PropertyLocation.Field
                ? JSObject.FastPropertyAt(isolate, jsObject, details.Representation, FieldIndex.ForDetails(map, details))
                : descs.GetStrongValue(index);
            Name key = descs.GetKey(index);
            if (ReferenceEquals(key, ReadOnlyRoots.enumerable_string)) desc.SetEnumerable(ObjectOps.BooleanValue(value));
            else if (ReferenceEquals(key, ReadOnlyRoots.configurable_string)) desc.SetConfigurable(ObjectOps.BooleanValue(value));
            else if (ReferenceEquals(key, ReadOnlyRoots.value_string)) desc.SetValue(value);
            else if (ReferenceEquals(key, ReadOnlyRoots.writable_string)) desc.SetWritable(ObjectOps.BooleanValue(value));
            else if (ReferenceEquals(key, ReadOnlyRoots.get_string))
            {
                // Bail out to slow path to throw an exception if necessary.
                if (!ObjectOps.IsCallable(value)) return false;
                desc.SetGet(value);
            }
            else if (ReferenceEquals(key, ReadOnlyRoots.set_string))
            {
                // Bail out to slow path to throw an exception if necessary.
                if (!ObjectOps.IsCallable(value)) return false;
                desc.SetSet(value);
            }
        }
        if ((desc.HasGet || desc.HasSet) && (desc.HasValue || desc.HasWritable))
        {
            // Bail out to slow path to throw an exception.
            return false;
        }
        return true;
    }

    static void CreateDataProperty(Isolate isolate, JSObject obj, JSString name, JSValue value) =>
        JSObject.CreateDataProperty(isolate, obj, new PropertyKey(isolate, name), value);

    /// <summary>ES6 6.2.4.4 "FromPropertyDescriptor".</summary>
    public readonly JSObject ToObject(Isolate isolate)
    {
        Factory factory = isolate.Factory;
        NativeContext nc = isolate.NativeContext;
        if (IsRegularAccessorProperty())
        {
            // Fast case for regular accessor properties.
            JSObject result = factory.NewJSObjectFromMap(nc.AccessorPropertyDescriptorMap);
            result.InObjectPropertyRef(0) = Get;
            result.InObjectPropertyRef(1) = Set;
            result.InObjectPropertyRef(2) = JSValue.FromBoolean(Enumerable);
            result.InObjectPropertyRef(3) = JSValue.FromBoolean(Configurable);
            return result;
        }
        if (IsRegularDataProperty())
        {
            // Fast case for regular data properties.
            JSObject result = factory.NewJSObjectFromMap(nc.DataPropertyDescriptorMap);
            result.InObjectPropertyRef(0) = Value;
            result.InObjectPropertyRef(1) = JSValue.FromBoolean(Writable);
            result.InObjectPropertyRef(2) = JSValue.FromBoolean(Enumerable);
            result.InObjectPropertyRef(3) = JSValue.FromBoolean(Configurable);
            return result;
        }
        JSObject obj = factory.NewJSObject(nc.ObjectFunction);
        if (HasValue) CreateDataProperty(isolate, obj, ReadOnlyRoots.value_string, Value);
        if (HasWritable) CreateDataProperty(isolate, obj, ReadOnlyRoots.writable_string, JSValue.FromBoolean(Writable));
        if (HasGet) CreateDataProperty(isolate, obj, ReadOnlyRoots.get_string, Get);
        if (HasSet) CreateDataProperty(isolate, obj, ReadOnlyRoots.set_string, Set);
        if (HasEnumerable) CreateDataProperty(isolate, obj, ReadOnlyRoots.enumerable_string, JSValue.FromBoolean(Enumerable));
        if (HasConfigurable) CreateDataProperty(isolate, obj, ReadOnlyRoots.configurable_string, JSValue.FromBoolean(Configurable));
        return obj;
    }

    /// <summary>ES6 6.2.4.5 ToPropertyDescriptor; throws on invalid input.</summary>
    public static void ToPropertyDescriptor(Isolate isolate, JSValue obj, ref PropertyDescriptor desc)
    {
        // 2. If Type(Obj) is not Object, throw a TypeError exception.
        if (obj.HeapObjectOrNull is not JSReceiver receiver)
        {
            isolate.ThrowTypeError(MessageTemplate.PropertyDescObject, obj);
            return;
        }
        // 3. Let desc be a new Property Descriptor that initially has no fields.
        if (ToPropertyDescriptorFastPath(isolate, receiver, ref desc)) return;
        desc = default;

        // enumerable? 4 through 6b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.enumerable_string, out JSValue enumerable))
        {
            // 6c. Set the [[Enumerable]] field of desc to enum.
            desc.SetEnumerable(ObjectOps.BooleanValue(enumerable));
        }
        // configurable? 7 through 9b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.configurable_string, out JSValue configurable))
        {
            // 9c. Set the [[Configurable]] field of desc to conf.
            desc.SetConfigurable(ObjectOps.BooleanValue(configurable));
        }
        // value? 10 through 12b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.value_string, out JSValue value))
        {
            // 12c. Set the [[Value]] field of desc to value.
            desc.SetValue(value);
        }
        // writable? 13 through 15b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.writable_string, out JSValue writable))
        {
            // 15c. Set the [[Writable]] field of desc to writable.
            desc.SetWritable(ObjectOps.BooleanValue(writable));
        }
        // getter? 16 through 18b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.get_string, out JSValue getter))
        {
            // 18c. If IsCallable(getter) is false and getter is not undefined,
            // throw a TypeError exception.
            if (!ObjectOps.IsCallable(getter) && !getter.IsUndefined)
            {
                isolate.ThrowTypeError(MessageTemplate.ObjectGetterCallable, getter);
            }
            // 18d. Set the [[Get]] field of desc to getter.
            desc.SetGet(getter);
        }
        // setter? 19 through 21b.
        if (GetPropertyIfPresent(isolate, receiver, ReadOnlyRoots.set_string, out JSValue setter))
        {
            // 21c. If IsCallable(setter) is false and setter is not undefined,
            // throw a TypeError exception.
            if (!ObjectOps.IsCallable(setter) && !setter.IsUndefined)
            {
                isolate.ThrowTypeError(MessageTemplate.ObjectSetterCallable, setter);
            }
            // 21d. Set the [[Set]] field of desc to setter.
            desc.SetSet(setter);
        }

        // 22. If either desc.[[Get]] or desc.[[Set]] is present, then
        // 22a. If either desc.[[Value]] or desc.[[Writable]] is present,
        // throw a TypeError exception.
        if ((desc.HasGet || desc.HasSet) && (desc.HasValue || desc.HasWritable))
        {
            isolate.ThrowTypeError(MessageTemplate.ValueAndAccessor, obj);
        }
        // 23. Return desc.
    }

    /// <summary>ES6 6.2.4.6 CompletePropertyDescriptor.</summary>
    public static void CompletePropertyDescriptor(Isolate isolate, ref PropertyDescriptor desc)
    {
        // 4. If either IsGenericDescriptor(Desc) or IsDataDescriptor(Desc) is true,
        // then:
        if (!IsAccessorDescriptor(desc))
        {
            // 4a. If Desc does not have a [[Value]] field, set Desc.[[Value]] to
            //     like.[[Value]].
            if (!desc.HasValue) desc.SetValue(JSValue.Undefined);
            // 4b. If Desc does not have a [[Writable]] field, set Desc.[[Writable]]
            //     to like.[[Writable]].
            if (!desc.HasWritable) desc.SetWritable(false);
        }
        else
        {
            // 5a. If Desc does not have a [[Get]] field, set Desc.[[Get]] to
            //     like.[[Get]].
            if (!desc.HasGet) desc.SetGet(JSValue.Undefined);
            // 5b. If Desc does not have a [[Set]] field, set Desc.[[Set]] to
            //     like.[[Set]].
            if (!desc.HasSet) desc.SetSet(JSValue.Undefined);
        }
        // 6. If Desc does not have an [[Enumerable]] field, set
        //    Desc.[[Enumerable]] to like.[[Enumerable]].
        if (!desc.HasEnumerable) desc.SetEnumerable(false);
        // 7. If Desc does not have a [[Configurable]] field, set
        //    Desc.[[Configurable]] to like.[[Configurable]].
        if (!desc.HasConfigurable) desc.SetConfigurable(false);
    }
}
