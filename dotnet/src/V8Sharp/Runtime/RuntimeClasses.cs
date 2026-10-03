// Port of src/runtime/runtime-classes.cc: DefineClass (with
// InitClassConstructor / InitClassPrototype), the super property loads and
// stores, and the class error throwers.
namespace V8Sharp.Runtime;

public static class RuntimeClasses
{
    // ---- Errors -----------------------------------------------------------------------

    /// <summary>Runtime_ThrowUnsupportedSuperError.</summary>
    public static JSValue ThrowUnsupportedSuperError(Isolate isolate) =>
        isolate.ThrowReferenceError(MessageTemplate.UnsupportedSuper);

    /// <summary>
    /// Runtime_ThrowConstructorNonCallableError: "Class constructor X cannot
    /// be invoked without 'new'", a TypeError of the constructor's realm.
    /// </summary>
    public static JSValue ThrowConstructorNonCallableError(Isolate isolate, JSFunction constructor)
    {
        JSString name = constructor.Shared.Name();
        NativeContext context = constructor.NativeContext;
        JSFunction realmTypeErrorFunction = context.TypeErrorFunction;
        if (name.Length == 0)
        {
            return isolate.Throw(ErrorUtils.MakeGenericError(isolate, realmTypeErrorFunction,
                MessageTemplate.AnonymousConstructorNonCallable, [], FrameSkipMode.SKIP_NONE));
        }
        return isolate.Throw(ErrorUtils.MakeGenericError(isolate, realmTypeErrorFunction,
            MessageTemplate.ConstructorNonCallable, [name], FrameSkipMode.SKIP_NONE));
    }

    /// <summary>Runtime_ThrowStaticPrototypeError.</summary>
    public static JSValue ThrowStaticPrototypeError(Isolate isolate) => isolate.ThrowTypeError(MessageTemplate.StaticPrototype);

    /// <summary>Runtime_ThrowSuperAlreadyCalledError.</summary>
    public static JSValue ThrowSuperAlreadyCalledError(Isolate isolate) =>
        isolate.ThrowReferenceError(MessageTemplate.SuperAlreadyCalled);

    /// <summary>Runtime_ThrowSuperNotCalled.</summary>
    public static JSValue ThrowSuperNotCalled(Isolate isolate) => isolate.ThrowReferenceError(MessageTemplate.SuperNotCalled);

    /// <summary>ThrowNotSuperConstructor (runtime-classes.cc).</summary>
    public static JSValue ThrowNotSuperConstructor(Isolate isolate, JSValue constructor, JSFunction function)
    {
        JSString superName;
        if (constructor.HeapObjectOrNull is JSFunction constructorFunction)
        {
            superName = constructorFunction.Shared.Name();
        }
        else if (constructor.IsNull)
        {
            superName = ReadOnlyRoots.null_string;
        }
        else
        {
            superName = ObjectOps.NoSideEffectsToString(isolate, constructor);
        }
        // null constructor
        if (superName.Length == 0) superName = ReadOnlyRoots.null_string;
        JSString functionName = function.Shared.Name();
        // anonymous class
        if (functionName.Length == 0)
        {
            return isolate.ThrowTypeError(MessageTemplate.NotSuperConstructorAnonymousClass, superName);
        }
        return isolate.ThrowTypeError(MessageTemplate.NotSuperConstructor, superName, functionName);
    }

    // ---- DefineClass ----------------------------------------------------------------------------

    /// <summary>Runtime_DefineClass: args = [boilerplate, constructor, super class, dynamic arguments...].</summary>
    public static JSValue DefineClass(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        var classBoilerplate = args[0].As<ClassBoilerplate>();
        var constructor = args[ClassBoilerplate.kConstructorArgumentIndex].As<JSFunction>();
        JSValue superClass = args[ClassBoilerplate.kPrototypeArgumentIndex];

        JSReceiver? prototypeParent;
        JSReceiver? constructorParent = null;
        if (superClass.IsTheHole)
        {
            prototypeParent = isolate.NativeContext.InitialObjectPrototype;
        }
        else if (superClass.IsNull)
        {
            prototypeParent = null;
        }
        else if (ObjectOps.IsConstructor(superClass))
        {
            JSValue maybePrototypeParent =
                RuntimeObject.GetObjectProperty(isolate, superClass, ReadOnlyRoots.prototype_string, superClass, out _);
            if (maybePrototypeParent.IsNull)
            {
                prototypeParent = null;
            }
            else if (maybePrototypeParent.HeapObjectOrNull is JSReceiver receiverParent)
            {
                prototypeParent = receiverParent;
            }
            else
            {
                return isolate.ThrowTypeError(MessageTemplate.PrototypeParentNotAnObject, maybePrototypeParent);
            }
            constructorParent = superClass.As<JSReceiver>();
        }
        else
        {
            return isolate.ThrowTypeError(MessageTemplate.ExtendsValueNotConstructor, superClass);
        }

        JSObject prototype = CreateClassPrototype(isolate);
        InitClassConstructor(isolate, classBoilerplate, constructorParent, constructor, args);
        InitClassPrototype(isolate, classBoilerplate, prototype, prototypeParent, constructor, args);
        return prototype;
    }

    /// <summary>CreateClassPrototype: an object whose map has no in-object properties.</summary>
    static JSObject CreateClassPrototype(Isolate isolate)
    {
        Map map = Map.Create(isolate, 0);
        return isolate.Factory.NewJSObjectFromMap(map);
    }

    static void InitClassConstructor(Isolate isolate, ClassBoilerplate classBoilerplate, JSReceiver? constructorParent,
        JSFunction constructor, ReadOnlySpan<JSValue> args)
    {
        Map map = Map.CopyDropDescriptors(isolate, constructor.Map);
        if (constructorParent is not null)
        {
            // Set map's prototype without enabling prototype setup mode for superclass
            // because it does not make sense.
            Map.SetPrototype(isolate, map, constructorParent, false);
            // Ensure that setup mode will never be enabled for superclass.
            JSObject.MakePrototypesFast(constructorParent, WhereToStart.StartAtReceiver, isolate);
        }
        constructor.Map = map;

        const PropertyAttributes roc = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;
        const PropertyAttributes ro = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;
        // The static properties template starts with the length, name and
        // prototype accessors (ClassBoilerplate::New); AddDescriptorsByTemplate
        // installs them as AccessorConstant descriptors, so the constructor
        // keeps a fast map whose name descriptor is the FunctionNameGetter
        // (UseFastFunctionNameLookup: stack frames of anonymous classes show
        // the inferred name). JSObject::SetAccessor would normalize the map.
        Map.EnsureDescriptorSlack(isolate, map, 3);
        map.AppendDescriptor(isolate, Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.FunctionLengthAccessor, roc));
        // All classes, even anonymous ones, have a name accessor.
        map.AppendDescriptor(isolate, Descriptor.AccessorConstant(ReadOnlyRoots.name_string, Accessors.FunctionNameAccessor, roc));
        map.AppendDescriptor(isolate, Descriptor.AccessorConstant(ReadOnlyRoots.prototype_string, Accessors.FunctionPrototypeAccessor, ro));
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, constructor, ReadOnlyRoots.class_positions_symbol,
            new ClassPositions(classBoilerplate.StartPosition, classBoilerplate.EndPosition), PropertyAttributes.DONT_ENUM);

        if (classBoilerplate.StaticIsDictionary)
        {
            JSObject.NormalizeProperties(isolate, constructor, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0, false,
                "ClassBoilerplate");
        }
        AddMembers(isolate, classBoilerplate, constructor, isStatic: true, args);
    }

    static void InitClassPrototype(Isolate isolate, ClassBoilerplate classBoilerplate, JSObject prototype,
        JSReceiver? prototypeParent, JSFunction constructor, ReadOnlySpan<JSValue> args)
    {
        Map map = Map.CopyDropDescriptors(isolate, prototype.Map);
        map.IsPrototypeMap = true;
        Map.SetPrototype(isolate, map, prototypeParent);
        isolate.UpdateProtectorsOnSetPrototype(prototype, prototypeParent is null ? JSValue.Null : prototypeParent);
        constructor.PrototypeOrInitialMap = prototype;
        map.SetConstructor(constructor);
        prototype.Map = map;

        JSObject.SetOwnPropertyIgnoreAttributes(isolate, prototype, ReadOnlyRoots.constructor_string, constructor,
            PropertyAttributes.DONT_ENUM);

        if (classBoilerplate.InstanceIsDictionary)
        {
            JSObject.NormalizeProperties(isolate, prototype, PropertyNormalizationMode.CLEAR_INOBJECT_PROPERTIES, 0, false,
                "ClassBoilerplate");
        }
        AddMembers(isolate, classBoilerplate, prototype, isStatic: false, args);
    }

    /// <summary>
    /// Installs the methods and accessors of one side of the class in source
    /// order (V8: AddDescriptorsByTemplate + SubstituteValues).
    /// </summary>
    static void AddMembers(Isolate isolate, ClassBoilerplate classBoilerplate, JSObject receiver, bool isStatic,
        ReadOnlySpan<JSValue> args)
    {
        foreach (ClassBoilerplate.Member member in classBoilerplate.Members)
        {
            if (member.IsStatic != isStatic) continue;

            JSValue key;
            if (member.IsComputed)
            {
                key = args[member.KeyIndex];
                if (key.HeapObjectOrNull is JSString s) key = isolate.Factory.InternalizeString(s);
            }
            else
            {
                key = member.Key;
            }
            PropertyKey lookupKey = new PropertyKey(isolate, key);

            switch (member.Kind)
            {
                case ClassBoilerplate.ValueKind.kData:
                {
                    JSValue method = GetMethodAndSetName(isolate, args, member.ValueIndex, ReadOnlyRoots.empty_string, key);
                    var it = new LookupIterator(isolate, receiver, lookupKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSObject.DefineOwnPropertyIgnoreAttributes(ref it, method, PropertyAttributes.DONT_ENUM);
                    break;
                }
                case ClassBoilerplate.ValueKind.kGetter:
                {
                    JSValue getter = GetMethodAndSetName(isolate, args, member.ValueIndex, ReadOnlyRoots.get_string, key);
                    var it = new LookupIterator(isolate, receiver, lookupKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSObject.DefineOwnAccessorIgnoreAttributes(ref it, getter, JSValue.Null, PropertyAttributes.DONT_ENUM);
                    break;
                }
                case ClassBoilerplate.ValueKind.kSetter:
                {
                    JSValue setter = GetMethodAndSetName(isolate, args, member.ValueIndex, ReadOnlyRoots.set_string, key);
                    var it = new LookupIterator(isolate, receiver, lookupKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSObject.DefineOwnAccessorIgnoreAttributes(ref it, JSValue.Null, setter, PropertyAttributes.DONT_ENUM);
                    break;
                }
                case ClassBoilerplate.ValueKind.kAutoAccessor:
                {
                    JSValue getter = GetMethodAndSetName(isolate, args, member.ValueIndex, ReadOnlyRoots.get_string, key);
                    JSValue setter = GetMethodAndSetName(isolate, args, member.ValueIndex + 1, ReadOnlyRoots.set_string, key);
                    var it = new LookupIterator(isolate, receiver, lookupKey, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
                    JSObject.DefineOwnAccessorIgnoreAttributes(ref it, getter, setter, PropertyAttributes.DONT_ENUM);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// GetMethodAndSetName: the method argument, named |name_prefix| + key when
    /// its SharedFunctionInfo has no shared name (computed keys).
    /// </summary>
    static JSValue GetMethodAndSetName(Isolate isolate, ReadOnlySpan<JSValue> args, int index, JSString namePrefix, JSValue key)
    {
        var method = args[index].As<JSFunction>();
        if (!method.Shared.HasSharedName)
        {
            Name name = key.IsNumber ? isolate.Factory.NumberToString(key) : key.As<Name>();
            JSFunction.SetName(isolate, method, name, namePrefix);
        }
        return method;
    }

    // ---- super property access ---------------------------------------------------------------

    /// <summary>GetHomeObjectPrototype (runtime-classes.cc).</summary>
    static JSValue GetHomeObjectPrototype(Isolate isolate, JSValue homeObject)
    {
        JSReceiver? prototype = homeObject.As<JSReceiver>().Map.Prototype;
        return prototype is null ? JSValue.Null : prototype;
    }

    static JSReceiver? GetSuperHolder(Isolate isolate, JSValue homeObjectProto, bool isLoad, in PropertyKey key)
    {
        if (homeObjectProto.HeapObjectOrNull is JSReceiver holder) return holder;
        MessageTemplate message = isLoad
            ? MessageTemplate.NonObjectPropertyLoadWithProperty
            : MessageTemplate.NonObjectPropertyStoreWithProperty;
        isolate.ThrowTypeError(message, homeObjectProto, key.GetName(isolate));
        return null;
    }

    /// <summary>Runtime_LoadFromSuper / Runtime_LoadKeyedFromSuper.</summary>
    public static JSValue LoadFromSuper(Isolate isolate, JSValue receiver, JSValue homeObject, JSValue key)
    {
        JSValue homeObjectProto = GetHomeObjectPrototype(isolate, homeObject);
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        JSReceiver holder = GetSuperHolder(isolate, homeObjectProto, isLoad: true, lookupKey)!;
        var it = new LookupIterator(isolate, receiver, lookupKey, holder);
        return ObjectOps.GetProperty(ref it);
    }

    /// <summary>Runtime_StoreToSuper / Runtime_StoreKeyedToSuper.</summary>
    public static JSValue StoreToSuper(Isolate isolate, JSValue receiver, JSValue homeObject, JSValue key, JSValue value,
        StoreOrigin storeOrigin)
    {
        JSValue homeObjectProto = GetHomeObjectPrototype(isolate, homeObject);
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        JSReceiver holder = GetSuperHolder(isolate, homeObjectProto, isLoad: false, lookupKey)!;
        var it = new LookupIterator(isolate, receiver, lookupKey, holder);
        ObjectOps.SetSuperProperty(ref it, value, storeOrigin);
        return value;
    }
}
