// Port of the Reflect builtins: src/builtins/reflect.tq,
// src/builtins/builtins-reflect.cc, and the semantics of the ASM builtins
// Reflect.apply / Reflect.construct (builtins-x64.cc Generate_ReflectApply,
// Generate_ReflectConstruct), which tail call CallWithArrayLike /
// ConstructWithArrayLike.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterReflect()
    {
        Register(Builtin.ReflectApply, BuiltinsReflect.ReflectApply);
        Register(Builtin.ReflectConstruct, BuiltinsReflect.ReflectConstruct);
        Register(Builtin.ReflectDefineProperty, BuiltinsReflect.ReflectDefineProperty);
        Register(Builtin.ReflectDeleteProperty, BuiltinsReflect.ReflectDeleteProperty);
        Register(Builtin.ReflectGet, BuiltinsReflect.ReflectGet);
        Register(Builtin.ReflectGetOwnPropertyDescriptor, BuiltinsReflect.ReflectGetOwnPropertyDescriptor);
        Register(Builtin.ReflectGetPrototypeOf, BuiltinsReflect.ReflectGetPrototypeOf);
        Register(Builtin.ReflectHas, BuiltinsReflect.ReflectHas);
        Register(Builtin.ReflectIsExtensible, BuiltinsReflect.ReflectIsExtensible);
        Register(Builtin.ReflectOwnKeys, BuiltinsReflect.ReflectOwnKeys);
        Register(Builtin.ReflectPreventExtensions, BuiltinsReflect.ReflectPreventExtensions);
        Register(Builtin.ReflectSet, BuiltinsReflect.ReflectSet);
        Register(Builtin.ReflectSetPrototypeOf, BuiltinsReflect.ReflectSetPrototypeOf);
    }
}

/// <summary>The Reflect builtins.</summary>
public static class BuiltinsReflect
{
    static JSReceiver CastReceiverOrThrow(Isolate isolate, JSValue obj, string methodName)
    {
        if (obj.HeapObjectOrNull is JSReceiver receiver) return receiver;
        isolate.ThrowTypeError(MessageTemplate.CalledOnNonObject, isolate.Factory.NewStringFromAsciiChecked(methodName));
        return null!;
    }

    /// <summary>ES6 section 26.1.1 Reflect.apply ( target, thisArgument, argumentsList ).</summary>
    public static JSValue ReflectApply(Isolate isolate, in BuiltinArguments args) =>
        // We don't need to check explicitly for callable target here,
        // since that's the first thing the Call/CallWithArrayLike builtins
        // will do.
        BuiltinsFunction.CallWithArrayLike(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2), args.AtOrUndefined(3));

    /// <summary>ES6 section 26.1.2 Reflect.construct ( target, argumentsList [ , newTarget ] ).</summary>
    public static JSValue ReflectConstruct(Isolate isolate, in BuiltinArguments args)
    {
        JSValue target = args.AtOrUndefined(1);
        JSValue argumentsList = args.AtOrUndefined(2);
        // new.target (if present, otherwise use target).
        JSValue newTarget = args.ArgcWithoutReceiver >= 3 ? args.AtOrUndefined(3) : target;
        return BuiltinsFunction.ConstructWithArrayLike(isolate, target, newTarget, argumentsList);
    }

    /// <summary>ES6 section 26.1.3 Reflect.defineProperty.</summary>
    public static JSValue ReflectDefineProperty(Isolate isolate, in BuiltinArguments args)
    {
        JSValue target = args.AtOrUndefined(1);
        JSValue key = args.AtOrUndefined(2);
        JSValue attributes = args.AtOrUndefined(3);

        JSReceiver receiver = CastReceiverOrThrow(isolate, target, "Reflect.defineProperty");
        Name name = ObjectOps.ToName(isolate, key);
        var desc = new PropertyDescriptor();
        PropertyDescriptor.ToPropertyDescriptor(isolate, attributes, ref desc);
        bool result = JSReceiver.DefineOwnProperty(isolate, receiver, name, ref desc, ShouldThrow.DontThrow);
        return JSValue.FromBoolean(result);
    }

    /// <summary>ES6 section 26.1.4 Reflect.deleteProperty.</summary>
    public static JSValue ReflectDeleteProperty(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver receiver = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.deleteProperty");
        // The DeleteProperty builtin (Runtime::DeleteObjectProperty).
        PropertyKey key = PropertyKey.FromKey(isolate, args.AtOrUndefined(2));
        return JSValue.FromBoolean(JSReceiver.DeletePropertyOrElement(isolate, receiver, key, LanguageMode.Sloppy));
    }

    /// <summary>ES6 section 26.1.6 Reflect.get.</summary>
    public static JSValue ReflectGet(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.get");
        Name name = ObjectOps.ToName(isolate, args.AtOrUndefined(2));
        JSValue receiver = args.ArgcWithoutReceiver > 2 ? args.AtOrUndefined(3) : obj;
        // GetPropertyWithReceiver(object, name, receiver, kReturnUndefined).
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, receiver, key, obj);
        return ObjectOps.GetProperty(ref it);
    }

    /// <summary>ES6 section 26.1.7 Reflect.getOwnPropertyDescriptor.</summary>
    public static JSValue ReflectGetOwnPropertyDescriptor(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver target = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.getOwnPropertyDescriptor");
        Name name = ObjectOps.ToName(isolate, args.AtOrUndefined(2));
        return BuiltinsObject.GetOwnPropertyDescriptorObject(isolate, target, name);
    }

    /// <summary>ES6 section 26.1.8 Reflect.getPrototypeOf.</summary>
    public static JSValue ReflectGetPrototypeOf(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.getPrototypeOf");
        return BuiltinsObject.PrototypeOrNull(JSReceiver.GetPrototype(isolate, obj));
    }

    /// <summary>ES6 section 26.1.9 Reflect.has.</summary>
    public static JSValue ReflectHas(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.has");
        // The HasProperty builtin (Runtime_HasProperty): ToPropertyKey, then
        // JSReceiver::HasProperty.
        PropertyKey key = PropertyKey.FromKey(isolate, args.AtOrUndefined(2));
        var it = new LookupIterator(isolate, obj, key, obj);
        return JSValue.FromBoolean(JSReceiver.HasProperty(ref it));
    }

    /// <summary>ES6 section 26.1.10 Reflect.isExtensible.</summary>
    public static JSValue ReflectIsExtensible(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.isExtensible");
        return JSValue.FromBoolean(JSReceiver.IsExtensible(isolate, obj));
    }

    /// <summary>ES6 section 26.1.11 Reflect.ownKeys.</summary>
    public static JSValue ReflectOwnKeys(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver target = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.ownKeys");
        FixedArray keys = KeyAccumulator.GetKeys(isolate, target, KeyCollectionMode.OwnOnly, PropertyFilter.ALL_PROPERTIES,
            GetKeysConversion.ConvertToString);
        return BuiltinsObject.NewJSArray(isolate, keys);
    }

    /// <summary>ES6 section 26.1.12 Reflect.preventExtensions.</summary>
    public static JSValue ReflectPreventExtensions(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.preventExtensions");
        return JSValue.FromBoolean(JSReceiver.PreventExtensions(isolate, obj, ShouldThrow.DontThrow));
    }

    /// <summary>ES6 section 26.1.13 Reflect.set.</summary>
    public static JSValue ReflectSet(Isolate isolate, in BuiltinArguments args)
    {
        JSValue target = args.AtOrUndefined(1);
        JSValue key = args.AtOrUndefined(2);
        JSValue value = args.AtOrUndefined(3);

        JSReceiver targetRecv = CastReceiverOrThrow(isolate, target, "Reflect.set");
        JSValue receiver = args.Length > 4 ? args.AtOrUndefined(4) : targetRecv;
        Name name = ObjectOps.ToName(isolate, key);
        var lookupKey = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, receiver, lookupKey, targetRecv);
        bool result = ObjectOps.SetSuperProperty(ref it, value, StoreOrigin.MaybeKeyed, ShouldThrow.DontThrow);
        return JSValue.FromBoolean(result);
    }

    /// <summary>ES6 section 26.1.14 Reflect.setPrototypeOf.</summary>
    public static JSValue ReflectSetPrototypeOf(Isolate isolate, in BuiltinArguments args)
    {
        JSReceiver obj = CastReceiverOrThrow(isolate, args.AtOrUndefined(1), "Reflect.setPrototypeOf");
        JSValue proto = args.AtOrUndefined(2);
        if (!proto.IsNull && !proto.IsJSReceiver) return isolate.ThrowTypeError(MessageTemplate.ProtoObjectOrNull, proto);
        return JSValue.FromBoolean(JSReceiver.SetPrototype(isolate, obj, proto, true, ShouldThrow.DontThrow));
    }
}
