// Port of src/init/bootstrapper.{h,cc}: Bootstrapper::CreateEnvironment and
// the function-creation helpers of the anonymous namespace in bootstrapper.cc
// (CreateFunction, InstallFunction, SimpleInstallFunction, ...), plus the
// function-map factories of src/heap/factory.cc (CreateSloppyFunctionMap,
// CreateStrictFunctionMap, CreateClassFunctionMap).
//
// The helpers are public so that the builtins ports can install further
// intrinsics with the same code V8 uses.
namespace V8Sharp.Init;

/// <summary>V8's FunctionMode (src/common/globals.h).</summary>
[Flags]
public enum FunctionMode
{
    // Bit 0: has name, bit 1: has writable prototype, bit 2: has readonly prototype.
    FUNCTION_WITHOUT_PROTOTYPE = 0,
    FUNCTION_WITH_NAME_BIT = 1 << 0,
    FUNCTION_WITH_WRITEABLE_PROTOTYPE_BIT = 1 << 1,
    FUNCTION_WITH_READONLY_PROTOTYPE_BIT = 1 << 2,

    METHOD_WITH_NAME = FUNCTION_WITH_NAME_BIT,
    FUNCTION_WITH_WRITEABLE_PROTOTYPE = FUNCTION_WITH_WRITEABLE_PROTOTYPE_BIT,
    FUNCTION_WITH_NAME_AND_WRITEABLE_PROTOTYPE = FUNCTION_WITH_WRITEABLE_PROTOTYPE_BIT | FUNCTION_WITH_NAME_BIT,
    FUNCTION_WITH_READONLY_PROTOTYPE = FUNCTION_WITH_READONLY_PROTOTYPE_BIT,
    FUNCTION_WITH_NAME_AND_READONLY_PROTOTYPE = FUNCTION_WITH_READONLY_PROTOTYPE_BIT | FUNCTION_WITH_NAME_BIT,
}

/// <summary>V8's MutableMode (prototype mutability of builtin constructors).</summary>
public enum MutableMode
{
    MUTABLE,
    IMMUTABLE,
}

public static class Bootstrapper
{
    /// <summary>
    /// Bootstrapper::CreateEnvironment: builds a fresh native context from
    /// scratch (V8Sharp has no snapshot) and returns it.
    /// </summary>
    public static NativeContext CreateEnvironment(Isolate isolate, MicrotaskQueue? microtaskQueue = null)
    {
        BuiltinRegistry.RegisterAll();
        bool wasActive = isolate.BootstrapperActive;
        isolate.BootstrapperActive = true;
        try
        {
            var genesis = new Genesis(isolate, microtaskQueue);
            // Bootstrapper::InstallExtensions (of the extensions gc and externalize-string are ported).
            using (isolate.EnterContext(genesis.Result))
            {
                Genesis.InstallSpecialObjects(isolate, genesis.Result);
                GCExtension.InstallIfExposed(isolate, genesis.Result);
                ExternalizeStringExtension.InstallIfExposed(isolate, genesis.Result);
            }
            return genesis.Result;
        }
        finally
        {
            isolate.BootstrapperActive = wasActive;
        }
    }

    // -----------------------------------------------------------------------
    // Function maps (factory.cc).

    static bool IsFunctionModeWithPrototype(FunctionMode mode) =>
        (mode & (FunctionMode.FUNCTION_WITH_WRITEABLE_PROTOTYPE_BIT | FunctionMode.FUNCTION_WITH_READONLY_PROTOTYPE_BIT)) != 0;

    static bool IsFunctionModeWithWritablePrototype(FunctionMode mode) =>
        (mode & FunctionMode.FUNCTION_WITH_WRITEABLE_PROTOTYPE_BIT) != 0;

    static bool IsFunctionModeWithName(FunctionMode mode) => (mode & FunctionMode.FUNCTION_WITH_NAME_BIT) != 0;

    /// <summary>Factory::CreateSloppyFunctionMap.</summary>
    public static Map CreateSloppyFunctionMap(Isolate isolate, FunctionMode functionMode, JSFunction? maybeEmptyFunction)
    {
        bool hasPrototype = IsFunctionModeWithPrototype(functionMode);
        InstanceType instanceType = hasPrototype ? InstanceType.JSFunctionType : InstanceType.JSFunctionWithoutPrototypeType;
        int headerSize = JSObject.GetHeaderSize(instanceType);
        int descriptorsCount = hasPrototype ? 3 : 2;
        int inobjectPropertiesCount = 0;
        if (IsFunctionModeWithName(functionMode)) ++inobjectPropertiesCount;

        Map map = isolate.Factory.NewContextfulMapForCurrentContext(instanceType,
            headerSize + inobjectPropertiesCount * Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND,
            inobjectPropertiesCount);
        map.IsConstructor = hasPrototype;
        map.IsCallable = true;
        if (maybeEmptyFunction is not null)
        {
            // Temporarily set constructor to empty function to calm down map verifier.
            map.SetConstructor(maybeEmptyFunction);
            Map.SetPrototype(isolate, map, maybeEmptyFunction);
        }

        // Setup descriptors array.
        Map.EnsureDescriptorSlack(isolate, map, descriptorsCount);
        AppendFunctionDescriptors(isolate, map, functionMode);
        return map;
    }

    /// <summary>Factory::CreateStrictFunctionMap.</summary>
    public static Map CreateStrictFunctionMap(Isolate isolate, FunctionMode functionMode, JSFunction emptyFunction)
    {
        bool hasPrototype = IsFunctionModeWithPrototype(functionMode);
        InstanceType instanceType = hasPrototype ? InstanceType.JSFunctionType : InstanceType.JSFunctionWithoutPrototypeType;
        int headerSize = JSObject.GetHeaderSize(instanceType);
        int inobjectPropertiesCount = 0;
        // length and prototype accessors or just length accessor.
        int descriptorsCount = hasPrototype ? 2 : 1;
        if (IsFunctionModeWithName(functionMode))
        {
            ++inobjectPropertiesCount;  // name property.
        }
        else
        {
            ++descriptorsCount;  // name accessor.
        }
        descriptorsCount += inobjectPropertiesCount;

        Map map = isolate.Factory.NewContextfulMapForCurrentContext(instanceType,
            headerSize + inobjectPropertiesCount * Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND,
            inobjectPropertiesCount);
        map.IsConstructor = hasPrototype;
        map.IsCallable = true;
        // Temporarily set constructor to empty function to calm down map verifier.
        map.SetConstructor(emptyFunction);
        Map.SetPrototype(isolate, map, emptyFunction);

        // Setup descriptors array.
        Map.EnsureDescriptorSlack(isolate, map, descriptorsCount);
        AppendFunctionDescriptors(isolate, map, functionMode);
        return map;
    }

    // The descriptor part shared by CreateSloppyFunctionMap and
    // CreateStrictFunctionMap (V8_FUNCTION_ARGUMENTS_CALLER_ARE_OWN_PROPS is
    // off by default, so sloppy function maps have no arguments/caller).
    static void AppendFunctionDescriptors(Isolate isolate, Map map, FunctionMode functionMode)
    {
        const PropertyAttributes roAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;
        const PropertyAttributes rwAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE;
        const PropertyAttributes rocAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;

        int fieldIndex = 0;
        Debug.Assert(JSFunctionOrBoundFunctionOrWrappedFunction.kLengthDescriptorIndex == 0);
        {  // Add length accessor.
            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.FunctionLengthAccessor, rocAttribs);
            map.AppendDescriptor(isolate, d);
        }

        Debug.Assert(JSFunctionOrBoundFunctionOrWrappedFunction.kNameDescriptorIndex == 1);
        if (IsFunctionModeWithName(functionMode))
        {
            // Add name field.
            Descriptor d = Descriptor.DataField(ReadOnlyRoots.name_string, fieldIndex++, rocAttribs, Representation.Tagged);
            map.AppendDescriptor(isolate, d);
        }
        else
        {
            // Add name accessor.
            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.name_string, Accessors.FunctionNameAccessor, rocAttribs);
            map.AppendDescriptor(isolate, d);
        }

        if (IsFunctionModeWithPrototype(functionMode))
        {
            // Add prototype accessor.
            PropertyAttributes attribs = IsFunctionModeWithWritablePrototype(functionMode) ? rwAttribs : roAttribs;
            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.prototype_string, Accessors.FunctionPrototypeAccessor, attribs);
            map.AppendDescriptor(isolate, d);
        }
    }

    /// <summary>Factory::CreateClassFunctionMap.</summary>
    public static Map CreateClassFunctionMap(Isolate isolate, JSFunction emptyFunction)
    {
        Map map = isolate.Factory.NewContextfulMapForCurrentContext(InstanceType.JSClassConstructorType,
            JSObject.GetHeaderSize(InstanceType.JSClassConstructorType));
        map.IsConstructor = true;
        map.IsPrototypeMap = true;
        map.IsCallable = true;
        // Temporarily set constructor to empty function to calm down map verifier.
        map.SetConstructor(emptyFunction);
        Map.SetPrototype(isolate, map, emptyFunction);

        // Setup descriptors array.
        Map.EnsureDescriptorSlack(isolate, map, 2);

        const PropertyAttributes roAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;
        const PropertyAttributes rocAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;

        {  // Add length accessor.
            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.FunctionLengthAccessor, rocAttribs);
            map.AppendDescriptor(isolate, d);
        }
        {  // Add prototype accessor.
            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.prototype_string, Accessors.FunctionPrototypeAccessor, roAttribs);
            map.AppendDescriptor(isolate, d);
        }
        return map;
    }

    // -----------------------------------------------------------------------
    // Function creation helpers (bootstrapper.cc, anonymous namespace).

    /// <summary>CreateSharedFunctionInfoForBuiltin.</summary>
    public static SharedFunctionInfo CreateSharedFunctionInfoForBuiltin(Isolate isolate, JSString name, Builtin builtin,
        int len, bool adapt)
    {
        SharedFunctionInfo info = isolate.Factory.NewSharedFunctionInfoForBuiltin(name, builtin, len, adapt);
        info.LanguageMode = LanguageMode.Strict;
        info.UpdateFunctionMapIndex();
        return info;
    }

    /// <summary>CreateFunctionForBuiltin.</summary>
    public static JSFunction CreateFunctionForBuiltin(Isolate isolate, JSString name, Map map, Builtin builtin, int len,
        bool adapt)
    {
        NativeContext context = isolate.NativeContext;
        SharedFunctionInfo info = CreateSharedFunctionInfoForBuiltin(isolate, name, builtin, len, adapt);
        return isolate.Factory.NewFunction(info, context, map);
    }

    /// <summary>CreateFunctionForBuiltinWithPrototype.</summary>
    public static JSFunction CreateFunctionForBuiltinWithPrototype(Isolate isolate, JSString name, Builtin builtin,
        JSValue prototype, InstanceType type, int instanceSize, int inobjectProperties, MutableMode prototypeMutability,
        int len, bool adapt)
    {
        Factory factory = isolate.Factory;
        NativeContext context = isolate.NativeContext;
        Map map = prototypeMutability == MutableMode.MUTABLE
            ? context.StrictFunctionMap
            : context.StrictFunctionWithReadonlyPrototypeMap;

        SharedFunctionInfo info = CreateSharedFunctionInfoForBuiltin(isolate, name, builtin, len, adapt);
        info.ExpectedNofProperties = (byte)inobjectProperties;

        JSFunction result = factory.NewFunction(info, context, map);

        ElementsKind elementsKind = type switch
        {
            InstanceType.JSArrayType => ElementsKind.PACKED_SMI_ELEMENTS,
            InstanceType.JSArgumentsObjectType => ElementsKind.PACKED_ELEMENTS,
            _ => ElementsKind.TERMINAL_FAST_ELEMENTS_KIND,
        };
        // V8Sharp objects have no raw layout: instance sizes are derived from
        // the instance type (JSObject::GetHeaderSize) plus the in-object
        // properties, whatever sizeof() V8's caller passed.
        instanceSize = JSObject.GetHeaderSize(type) + inobjectProperties * Map.kTaggedSize;
        Map initialMap = factory.NewContextfulMapForCurrentContext(type, instanceSize, elementsKind, inobjectProperties);
        initialMap.SetConstructor(result);
        // TODO(littledan): Why do we have this is_generator test when
        // NewFunctionPrototype already handles finding an appropriately
        // shared prototype?
        if (!Globals.IsResumableFunction(info.Kind) && prototype.IsTheHole)
        {
            prototype = factory.NewFunctionPrototype(result);
        }
        JSFunction.SetInitialMap(isolate, result, initialMap, prototype.HeapObjectOrNull as JSReceiver);
        return result;
    }

    /// <summary>CreateFunctionForBuiltinWithoutPrototype.</summary>
    public static JSFunction CreateFunctionForBuiltinWithoutPrototype(Isolate isolate, JSString name, Builtin builtin,
        int len, bool adapt)
    {
        NativeContext context = isolate.NativeContext;
        Map map = context.StrictFunctionWithoutPrototypeMap;
        SharedFunctionInfo info = CreateSharedFunctionInfoForBuiltin(isolate, name, builtin, len, adapt);
        return isolate.Factory.NewFunction(info, context, map);
    }

    /// <summary>CreateFunction.</summary>
    public static JSFunction CreateFunction(Isolate isolate, JSString name, InstanceType type, int instanceSize,
        int inobjectProperties, JSValue prototype, Builtin builtin, int len, bool adapt)
    {
        JSFunction result = CreateFunctionForBuiltinWithPrototype(isolate, name, builtin, prototype, type, instanceSize,
            inobjectProperties, MutableMode.IMMUTABLE, len, adapt);

        // Make the JSFunction's prototype object fast.
        JSObject.MakePrototypesFast(result.Prototype, WhereToStart.StartAtReceiver, isolate);

        // Make the resulting JSFunction object fast.
        JSObject.MakePrototypesFast(result, WhereToStart.StartAtReceiver, isolate);
        result.Shared.Native = true;
        return result;
    }

    public static JSFunction CreateFunction(Isolate isolate, string name, InstanceType type, int instanceSize,
        int inobjectProperties, JSValue prototype, Builtin builtin, int len, bool adapt) =>
        CreateFunction(isolate, isolate.Factory.InternalizeString(name), type, instanceSize, inobjectProperties,
            prototype, builtin, len, adapt);

    /// <summary>InstallFunction.</summary>
    public static JSFunction InstallFunction(Isolate isolate, JSObject target, JSString name, InstanceType type,
        int instanceSize, int inobjectProperties, JSValue prototype, Builtin call, int len, bool adapt)
    {
        JSFunction function = CreateFunction(isolate, name, type, instanceSize, inobjectProperties, prototype, call, len,
            adapt);
        JSObject.AddProperty(isolate, target, name, function, PropertyAttributes.DONT_ENUM);
        return function;
    }

    public static JSFunction InstallFunction(Isolate isolate, JSObject target, string name, InstanceType type,
        int instanceSize, int inobjectProperties, JSValue prototype, Builtin call, int len, bool adapt) =>
        InstallFunction(isolate, target, isolate.Factory.InternalizeString(name), type, instanceSize,
            inobjectProperties, prototype, call, len, adapt);

    /// <summary>
    /// SetConstructorInstanceType: sets a constructor instance type on the
    /// constructor map, used by the IsXxxConstructor() predicates of the
    /// protector checks.
    /// </summary>
    public static void SetConstructorInstanceType(Isolate isolate, JSFunction constructor, InstanceType constructorType)
    {
        Map map = constructor.Map;
        Debug.Assert(!ReferenceEquals(map, isolate.NativeContext.StrictFunctionMap));
        Debug.Assert(map.InstanceType == InstanceType.JSFunctionType);
        map.InstanceType = constructorType;
    }

    /// <summary>SimpleCreateFunction.</summary>
    public static JSFunction SimpleCreateFunction(Isolate isolate, JSString name, Builtin call, int len, bool adapt)
    {
        JSFunction fun = CreateFunctionForBuiltinWithoutPrototype(isolate, name, call, len, adapt);
        // Make the resulting JSFunction object fast.
        JSObject.MakePrototypesFast(fun, WhereToStart.StartAtReceiver, isolate);
        fun.Shared.Native = true;
        return fun;
    }

    /// <summary>InstallFunctionWithBuiltinId.</summary>
    public static JSFunction InstallFunctionWithBuiltinId(Isolate isolate, JSObject baseObject, string name, Builtin call,
        int len, bool adapt)
    {
        JSString internalizedName = isolate.Factory.InternalizeString(name);
        JSFunction fun = SimpleCreateFunction(isolate, internalizedName, call, len, adapt);
        JSObject.AddProperty(isolate, baseObject, internalizedName, fun, PropertyAttributes.DONT_ENUM);
        return fun;
    }

    /// <summary>InstallFunctionAtSymbol.</summary>
    public static JSFunction InstallFunctionAtSymbol(Isolate isolate, JSObject baseObject, Symbol symbol,
        string symbolString, Builtin call, int len, bool adapt, PropertyAttributes attrs = PropertyAttributes.DONT_ENUM)
    {
        JSString internalizedSymbol = isolate.Factory.InternalizeString(symbolString);
        JSFunction fun = SimpleCreateFunction(isolate, internalizedSymbol, call, len, adapt);
        JSObject.AddProperty(isolate, baseObject, symbol, fun, attrs);
        return fun;
    }

    /// <summary>SimpleInstallFunction.</summary>
    public static JSFunction SimpleInstallFunction(Isolate isolate, JSObject baseObject, string name, Builtin call, int len,
        bool adapt, PropertyAttributes attrs = PropertyAttributes.DONT_ENUM)
    {
        // Although function name does not have to be internalized the property name
        // will be internalized during property addition anyway, so do it here now.
        JSString internalizedName = isolate.Factory.InternalizeString(name);
        JSFunction fun = SimpleCreateFunction(isolate, internalizedName, call, len, adapt);
        JSObject.AddProperty(isolate, baseObject, internalizedName, fun, attrs);
        return fun;
    }

    /// <summary>SimpleInstallGetterSetter.</summary>
    public static void SimpleInstallGetterSetter(Isolate isolate, JSObject baseObject, Name name, Builtin callGetter,
        Builtin callSetter)
    {
        JSString getterName = Name.ToFunctionName(isolate, name, ReadOnlyRoots.get_string);
        JSFunction getter = SimpleCreateFunction(isolate, getterName, callGetter, 0, true);

        JSString setterName = Name.ToFunctionName(isolate, name, ReadOnlyRoots.set_string);
        JSFunction setter = SimpleCreateFunction(isolate, setterName, callSetter, 1, true);

        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, baseObject, name, getter, setter, PropertyAttributes.DONT_ENUM);
    }

    public static void SimpleInstallGetterSetter(Isolate isolate, JSObject baseObject, string name, Builtin callGetter,
        Builtin callSetter) =>
        SimpleInstallGetterSetter(isolate, baseObject, isolate.Factory.InternalizeString(name), callGetter, callSetter);

    /// <summary>SimpleInstallGetter(isolate, base, name, property_name, call, adapt).</summary>
    public static JSFunction SimpleInstallGetter(Isolate isolate, JSObject baseObject, Name name, Name propertyName,
        Builtin call, bool adapt)
    {
        JSString getterName = Name.ToFunctionName(isolate, name, ReadOnlyRoots.get_string);
        JSFunction getter = SimpleCreateFunction(isolate, getterName, call, 0, adapt);
        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, baseObject, propertyName, getter, JSValue.Undefined,
            PropertyAttributes.DONT_ENUM);
        return getter;
    }

    /// <summary>SimpleInstallGetter(isolate, base, name, call, adapt).</summary>
    public static JSFunction SimpleInstallGetter(Isolate isolate, JSObject baseObject, Name name, Builtin call, bool adapt) =>
        SimpleInstallGetter(isolate, baseObject, name, name, call, adapt);

    /// <summary>InstallConstant.</summary>
    public static void InstallConstant(Isolate isolate, JSObject holder, string name, JSValue value) =>
        JSObject.AddProperty(isolate, holder, isolate.Factory.InternalizeString(name), value,
            PropertyAttributes.DONT_DELETE | PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

    /// <summary>InstallTrueValuedProperty.</summary>
    public static void InstallTrueValuedProperty(Isolate isolate, JSObject holder, string name) =>
        JSObject.AddProperty(isolate, holder, isolate.Factory.InternalizeString(name), JSValue.True, PropertyAttributes.NONE);

    /// <summary>InstallSpeciesGetter.</summary>
    public static void InstallSpeciesGetter(Isolate isolate, JSFunction constructor)
    {
        // TODO(adamk): We should be able to share a SharedFunctionInfo
        // between all these JSFunctions.
        SimpleInstallGetter(isolate, constructor, ReadOnlyRoots.symbol_species_string, ReadOnlyRoots.species_symbol,
            Builtin.ReturnReceiver, true);
    }

    /// <summary>InstallToStringTag.</summary>
    public static void InstallToStringTag(Isolate isolate, JSObject holder, JSString value) =>
        JSObject.AddProperty(isolate, holder, ReadOnlyRoots.to_string_tag_symbol, value,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

    public static void InstallToStringTag(Isolate isolate, JSObject holder, string value) =>
        InstallToStringTag(isolate, holder, isolate.Factory.InternalizeString(value));

    /// <summary>
    /// InstallWithIntrinsicDefaultProto: see
    /// https://tc39.es/ecma262/#sec-ordinarycreatefromconstructor for the
    /// intrinsicDefaultProto concept.
    /// </summary>
    public static void InstallWithIntrinsicDefaultProto(Isolate isolate, JSFunction function, Context.Field contextIndex)
    {
        JSObject.AddProperty(isolate, function, ReadOnlyRoots.native_context_index_symbol,
            JSValue.FromInt((int)contextIndex), PropertyAttributes.NONE);
        isolate.NativeContext.Slots[(int)contextIndex] = function;
    }

    /// <summary>
    /// CreateLiteralObjectMapFromCache: a map for result objects returned from
    /// builtins that is exactly the map object literals of the same shape get.
    /// </summary>
    public static Map CreateLiteralObjectMapFromCache(Isolate isolate, ReadOnlySpan<Name> properties)
    {
        NativeContext nativeContext = isolate.NativeContext;
        Map map = isolate.Factory.ObjectLiteralMapFromCache(nativeContext, properties.Length);
        foreach (Name name in properties)
        {
            map = Map.CopyWithField(isolate, map, name, FieldType.Any, PropertyAttributes.NONE, PropertyConstness.Const,
                Representation.Tagged, TransitionFlag.INSERT_TRANSITION)!;
        }
        return map;
    }
}
