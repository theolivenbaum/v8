// Port of Genesis (src/init/bootstrapper.cc): creation of a native context
// from scratch. V8 normally deserializes contexts from the snapshot; V8Sharp
// has no snapshot (architecture.md section 2) and always runs Genesis.
//
// Ported: CreateRoots, CreateEmptyFunction, the sloppy/strict/class function
// maps, CreateObjectFunction, the iterator/async iterator/async function maps,
// CreateJSProxyMaps, CreateNewGlobals, InitializeMapCaches, InitializeGlobal
// (Genesis.InitializeGlobal.cs), InitializeIteratorFunctions,
// InitializeCallSiteBuiltins and InstallABunchOfRandomThings.
// ArrayBuffer, SharedArrayBuffer, Atomics, TypedArrays and DataView are in
// Genesis.TypedArrays.cs, DisposableStack in Genesis.DisposableStack.cs.
// Temporal is in Genesis.Temporal.cs.
// Not ported yet (see todo.md): Intl, shared structs, extras
// bindings, extensions and API global templates.
namespace V8Sharp.Init;

sealed partial class Genesis
{
    readonly Isolate _isolate;
    readonly Factory _factory;
    NativeContext _nativeContext = null!;
    JSFunction? _restrictedPropertiesThrower;

    public NativeContext Result => _nativeContext;

    /// <summary>
    /// Genesis::InstallSpecialObjects, which Bootstrapper::InstallExtensions
    /// runs on every new context: Error.stackTraceLimit. (Wasm and the
    /// memory corruption API are not ported.)
    /// </summary>
    public static void InstallSpecialObjects(Isolate isolate, NativeContext nativeContext)
    {
        // Error.stackTraceLimit.
        JSFunction error = nativeContext.ErrorFunction;
        JSObject.AddProperty(isolate, error, ReadOnlyRoots.stackTraceLimit_string,
            JSValue.FromInt(isolate.Flags.stack_trace_limit), PropertyAttributes.NONE);
    }

    public Genesis(Isolate isolate, MicrotaskQueue? microtaskQueue)
    {
        _isolate = isolate;
        _factory = isolate.Factory;

        // Create an uninitialized global proxy now and initialize it later in
        // CreateNewGlobals.
        JSGlobalProxy globalProxy = _factory.NewUninitializedJSGlobalProxy(JSObject.GetHeaderSize(InstanceType.JSGlobalProxyType));

        // We get here if there was no context snapshot.
        CreateRoots();
        using Isolate.SaveContext saved = isolate.EnterContext(_nativeContext);

        JSFunction emptyFunction = CreateEmptyFunction();
        CreateSloppyModeFunctionMaps(emptyFunction);
        CreateStrictModeFunctionMaps(emptyFunction);
        CreateObjectFunction(emptyFunction);
        CreateIteratorMaps(emptyFunction);
        CreateAsyncIteratorMaps(emptyFunction);
        CreateAsyncFunctionMaps(emptyFunction);
        JSGlobalObject globalObject = CreateNewGlobals(globalProxy);
        InitializeMapCaches();
        InitializeGlobal(globalObject, emptyFunction);
        InitializeIteratorFunctions();
        InitializeCallSiteBuiltins();
        InstallABunchOfRandomThings();
        InstallErrorStackAccessorFunctions();
        BuiltinsConsole.InstallExtrasBindings(isolate, _nativeContext);
        ConfigureGlobalObject();

        _nativeContext.MicrotaskQueue = microtaskQueue ?? isolate.DefaultMicrotaskQueue;

        // Install experimental natives.
        InitializeExperimentalGlobal();

        // Store String.prototype's map again in case it has been changed by
        // experimental natives.
        JSFunction stringFunction = _nativeContext.StringFunction;
        var stringFunctionPrototype = (JSObject)stringFunction.InitialMap.Prototype!;
        Debug.Assert(stringFunctionPrototype.HasFastProperties);
        _nativeContext.StringFunctionPrototypeMap = stringFunctionPrototype.Map;

        if (isolate.Flags.disallow_code_generation_from_strings)
        {
            _nativeContext.AllowCodeGenFromStrings = JSValue.False;
        }

        _nativeContext.ErrorsThrown = JSValue.Zero;
    }

    NativeContext nativeContext => _nativeContext;

    /// <summary>
    /// Genesis::InitializeExperimentalGlobal: the feature installers from
    /// more mature to less mature (shipped, staged, ...), then
    /// regexp_linear_flag, sharedarraybuffer and queueMicrotask. Features
    /// whose installer is not ported are omitted from the list.
    /// </summary>
    void InitializeExperimentalGlobal()
    {
        // FOREACH_SHIPPED_FEATURE_FLAG
        InitializeGlobal_harmony_temporal();
        InitializeGlobal_js_iterator_join();
        InitializeGlobal_js_iterator_sequencing();
        InitializeGlobal_js_joint_iteration();
        InitializeGlobal_js_iterator_includes();

        // FOREACH_STAGED_FEATURE_FLAG (js_immutable_arraybuffer), then
        // InitializeGlobal_sharedarraybuffer (Genesis.TypedArrays.cs).
        InitializeExperimentalGlobalTypedArrays();

        // FOREACH_HARMONY_FLAG
        InitializeGlobal_js_source_phase_imports();

        InitializeGlobal_regexp_linear_flag();
        InitializeGlobal_queueMicrotask();
    }

    /// <summary>Genesis::InitializeGlobal_regexp_linear_flag.</summary>
    void InitializeGlobal_regexp_linear_flag()
    {
        if (!_isolate.Flags.enable_experimental_regexp_engine) return;

        var regexpPrototype = (JSObject)_nativeContext.RegExpFunction.InstancePrototype;
        Bootstrapper.SimpleInstallGetter(_isolate, regexpPrototype, ReadOnlyRoots.linear_string,
            Builtin.RegExpPrototypeLinearGetter, true);

        // Store regexp prototype map again after change.
        _nativeContext.RegExpPrototypeMap = regexpPrototype.Map;
    }

    /// <summary>Genesis::ConfigureGlobalObject (no global proxy template): hooks the
    /// global object up as the global proxy's hidden prototype.</summary>
    void ConfigureGlobalObject()
    {
        JSObject.ForceSetPrototype(_isolate, _nativeContext.GlobalProxyObject, _nativeContext.GlobalObject);
    }

    // -----------------------------------------------------------------------

    void CreateRoots()
    {
        // Allocate the native context FixedArray first and then patch the
        // closure and extension object later (we need the empty function
        // and the global object, but in order to create those, we need the
        // native context).
        _nativeContext = _factory.NewNativeContext();
        _nativeContext.ScopeInfo = ScopeInfo.CreateForNativeContext();
        _nativeContext.AllowCodeGenFromStrings = JSValue.True;
    }

    JSFunction CreateEmptyFunction()
    {
        Isolate isolate = _isolate;
        // Allocate the function map first and then patch the prototype later.
        Map emptyFunctionMap = Bootstrapper.CreateSloppyFunctionMap(isolate, FunctionMode.FUNCTION_WITHOUT_PROTOTYPE, null);
        emptyFunctionMap.IsPrototypeMap = true;
        Debug.Assert(!emptyFunctionMap.IsDictionaryMap);

        // Allocate the empty function as the prototype for function according to
        // https://tc39.es/ecma262/#sec-properties-of-the-function-prototype-object
        JSFunction emptyFunction = Bootstrapper.CreateFunctionForBuiltin(isolate, ReadOnlyRoots.empty_string,
            emptyFunctionMap, Builtin.EmptyFunction, 0, false);
        emptyFunctionMap.SetConstructor(emptyFunction);
        _nativeContext.EmptyFunction = emptyFunction;

        // --- E m p t y ---
        JSString source = _factory.InternalizeString("() {}");
        Script script = _factory.NewScript(source);
        script.ScriptType = Script.Type.Native;
        SharedFunctionInfo sfi = emptyFunction.Shared;
        sfi.SetScopeInfo(ScopeInfo.CreateForEmptyFunction(isolate));
        sfi.SetScript(script, 1);
        sfi.UpdateFunctionMapIndex();

        return emptyFunction;
    }

    void CreateSloppyModeFunctionMaps(JSFunction empty)
    {
        Isolate isolate = _isolate;

        // Allocate maps for sloppy functions without prototype.
        _nativeContext.SloppyFunctionWithoutPrototypeMap =
            Bootstrapper.CreateSloppyFunctionMap(isolate, FunctionMode.FUNCTION_WITHOUT_PROTOTYPE, empty);

        // Allocate maps for sloppy functions with readonly prototype.
        _nativeContext.SloppyFunctionWithReadonlyPrototypeMap =
            Bootstrapper.CreateSloppyFunctionMap(isolate, FunctionMode.FUNCTION_WITH_READONLY_PROTOTYPE, empty);

        // Allocate maps for sloppy functions with writable prototype.
        _nativeContext.SloppyFunctionMap =
            Bootstrapper.CreateSloppyFunctionMap(isolate, FunctionMode.FUNCTION_WITH_WRITEABLE_PROTOTYPE, empty);

        _nativeContext.SloppyFunctionWithNameMap =
            Bootstrapper.CreateSloppyFunctionMap(isolate, FunctionMode.FUNCTION_WITH_NAME_AND_WRITEABLE_PROTOTYPE, empty);
    }

    JSFunction GetThrowTypeErrorIntrinsic()
    {
        if (_restrictedPropertiesThrower is not null) return _restrictedPropertiesThrower;
        Isolate isolate = _isolate;
        JSFunction function = Bootstrapper.CreateFunctionForBuiltinWithoutPrototype(isolate, ReadOnlyRoots.empty_string,
            Builtin.StrictPoisonPillThrower, 0, true);

        // %ThrowTypeError% must have a name property with an empty string value. Per
        // spec, ThrowTypeError's name is non-configurable, unlike ordinary functions'
        // name property. To redefine it to be non-configurable, use
        // SetOwnPropertyIgnoreAttributes.
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, function, ReadOnlyRoots.name_string, ReadOnlyRoots.empty_string,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY);

        // length needs to be non configurable.
        JSValue value = JSValue.FromNumber(function.Length);
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, function, ReadOnlyRoots.length_string, value,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY);

        JSObject.PreventExtensions(isolate, function, ShouldThrow.ThrowOnError);

        JSObject.MigrateSlowToFast(isolate, function, 0, "Bootstrapping");

        _restrictedPropertiesThrower = function;
        return function;
    }

    void CreateStrictModeFunctionMaps(JSFunction empty)
    {
        Isolate isolate = _isolate;

        // Allocate maps for strict functions without prototype.
        _nativeContext.StrictFunctionWithoutPrototypeMap =
            Bootstrapper.CreateStrictFunctionMap(isolate, FunctionMode.FUNCTION_WITHOUT_PROTOTYPE, empty);

        _nativeContext.MethodWithNameMap =
            Bootstrapper.CreateStrictFunctionMap(isolate, FunctionMode.METHOD_WITH_NAME, empty);

        // Allocate maps for strict functions with writable prototype.
        _nativeContext.StrictFunctionMap =
            Bootstrapper.CreateStrictFunctionMap(isolate, FunctionMode.FUNCTION_WITH_WRITEABLE_PROTOTYPE, empty);

        _nativeContext.StrictFunctionWithNameMap =
            Bootstrapper.CreateStrictFunctionMap(isolate, FunctionMode.FUNCTION_WITH_NAME_AND_WRITEABLE_PROTOTYPE, empty);

        // Allocate maps for strict functions with readonly prototype.
        _nativeContext.StrictFunctionWithReadonlyPrototypeMap =
            Bootstrapper.CreateStrictFunctionMap(isolate, FunctionMode.FUNCTION_WITH_READONLY_PROTOTYPE, empty);

        // Allocate map for class functions.
        _nativeContext.ClassFunctionMap = Bootstrapper.CreateClassFunctionMap(isolate, empty);
    }

    void CreateObjectFunction(JSFunction emptyFunction)
    {
        Isolate isolate = _isolate;

        // --- O b j e c t ---
        const int inobjectProperties = JSObject.kInitialGlobalObjectUnusedPropertiesCount;
        const int instanceSize = JSObject.kHeaderSize + Map.kTaggedSize * inobjectProperties;

        JSFunction objectFun = Bootstrapper.CreateFunction(isolate, ReadOnlyRoots.Object_string, InstanceType.JSObjectType,
            instanceSize, inobjectProperties, JSValue.Null, Builtin.ObjectConstructor, 1, false);
        _nativeContext.ObjectFunction = objectFun;

        {
            // Finish setting up Object function's initial map.
            Map initialMap = objectFun.InitialMap;
            initialMap.SetElementsKind(ElementsKind.HOLEY_ELEMENTS);
        }

        // Allocate a new prototype for the object function.
        JSObject objectFunctionPrototype = _factory.NewFunctionPrototype(objectFun);

        {
            Map map = Map.Copy(isolate, objectFunctionPrototype.Map, "EmptyObjectPrototype");
            map.IsPrototypeMap = true;
            // Ban re-setting Object.prototype.__proto__ to prevent Proxy security bug
            map.IsImmutableProto = true;
            objectFunctionPrototype.Map = map;
        }

        // Complete setting up empty function.
        {
            Map emptyFunctionMap = emptyFunction.Map;
            Map.SetPrototype(isolate, emptyFunctionMap, objectFunctionPrototype);
        }

        _nativeContext.InitialObjectPrototype = objectFunctionPrototype;
        JSFunction.SetPrototype(isolate, objectFun, objectFunctionPrototype);
        objectFunctionPrototype.Map.InstanceType = InstanceType.JSObjectPrototypeType;
        {
            // Set up slow map for Object.create(null) instances without in-object
            // properties.
            Map map = objectFun.InitialMap;
            map = Map.CopyInitialMapNormalized(isolate, map);
            Map.SetPrototype(isolate, map, null);
            _nativeContext.SlowObjectWithNullPrototypeMap = map;

            // Set up slow map for literals with too many properties.
            map = Map.Copy(isolate, map, "slow_object_with_object_prototype_map");
            Map.SetPrototype(isolate, map, objectFunctionPrototype);
            _nativeContext.SlowObjectWithObjectPrototypeMap = map;
        }
    }

    /// <summary>CreateNonConstructorMap.</summary>
    static Map CreateNonConstructorMap(Isolate isolate, Map sourceMap, JSObject prototype, string reason)
    {
        Map map = Map.Copy(isolate, sourceMap, reason);
        // Ensure the resulting map has prototype slot (it is necessary for storing
        // initial map even when the prototype property is not required).
        Debug.Assert(map.InstanceType is InstanceType.JSFunctionWithoutPrototypeType or InstanceType.JSFunctionType);
        if (map.InstanceType == InstanceType.JSFunctionWithoutPrototypeType)
        {
            // Re-set the unused property fields after changing the instance size.
            int unusedPropertyFields = map.UnusedPropertyFields();
            map.InstanceType = InstanceType.JSFunctionType;
            map.SetInstanceSize(map.InstanceSize + Map.kTaggedSize);
            // The prototype slot shifts the in-object properties area by one slot.
            map.SetInObjectPropertiesStartInWords(map.GetInObjectPropertiesStartInWords() + 1);
            map.SetInObjectUnusedPropertyFields(unusedPropertyFields);
        }
        map.IsConstructor = false;
        Map.SetPrototype(isolate, map, prototype);
        return map;
    }

    void CreateIteratorMaps(JSFunction empty)
    {
        Isolate isolate = _isolate;
        // Create iterator-related meta-objects.
        JSObject iteratorPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);

        Bootstrapper.InstallFunctionAtSymbol(isolate, iteratorPrototype, ReadOnlyRoots.iterator_symbol, "[Symbol.iterator]",
            Builtin.ReturnReceiver, 0, true);
        _nativeContext.InitialIteratorPrototype = iteratorPrototype;
        Debug.Assert(!ReferenceEquals(iteratorPrototype.Map, _nativeContext.InitialObjectPrototype.Map));
        iteratorPrototype.Map.InstanceType = InstanceType.JSIteratorPrototypeType;

        JSObject generatorObjectPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        _nativeContext.InitialGeneratorPrototype = generatorObjectPrototype;
        JSObject.ForceSetPrototype(isolate, generatorObjectPrototype, iteratorPrototype);
        JSObject generatorFunctionPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        JSObject.ForceSetPrototype(isolate, generatorFunctionPrototype, empty);

        Bootstrapper.InstallToStringTag(isolate, generatorFunctionPrototype, "GeneratorFunction");
        JSObject.AddProperty(isolate, generatorFunctionPrototype, ReadOnlyRoots.prototype_string, generatorObjectPrototype,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

        JSObject.AddProperty(isolate, generatorObjectPrototype, ReadOnlyRoots.constructor_string, generatorFunctionPrototype,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        Bootstrapper.InstallToStringTag(isolate, generatorObjectPrototype, "Generator");
        Bootstrapper.SimpleInstallFunction(isolate, generatorObjectPrototype, "next", Builtin.GeneratorPrototypeNext, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, generatorObjectPrototype, "return", Builtin.GeneratorPrototypeReturn, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, generatorObjectPrototype, "throw", Builtin.GeneratorPrototypeThrow, 1, false);

        // Internal version of generator_prototype_next, flagged as non-native such
        // that it doesn't show up in Error traces.
        JSFunction generatorNextInternal = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.next_string,
            Builtin.GeneratorPrototypeNext, 1, false);
        generatorNextInternal.Shared.Native = false;
        _nativeContext.GeneratorNextInternal = generatorNextInternal;

        // Internal version of async module functions, flagged as non-native such
        // that they don't show up in Error traces.
        {
            JSFunction asyncModuleEvaluateInternal = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.next_string,
                Builtin.AsyncModuleEvaluate, 1, false);
            asyncModuleEvaluateInternal.Shared.Native = false;
            _nativeContext.AsyncModuleEvaluateInternal = asyncModuleEvaluateInternal;
        }

        // Create maps for generator functions and their prototypes.  Store those
        // maps in the native context. The "prototype" property descriptor is
        // writable, non-enumerable, and non-configurable (as per ES6 draft
        // 04-14-15, section 25.2.4.3).
        // Generator functions do not have "caller" or "arguments" accessors.
        _nativeContext.GeneratorFunctionMap = CreateNonConstructorMap(isolate, _nativeContext.StrictFunctionMap,
            generatorFunctionPrototype, "GeneratorFunction");
        _nativeContext.GeneratorFunctionWithNameMap = CreateNonConstructorMap(isolate,
            _nativeContext.StrictFunctionWithNameMap, generatorFunctionPrototype, "GeneratorFunction with name");

        Map generatorObjectPrototypeMap = Map.Create(isolate, 0);
        Map.SetPrototype(isolate, generatorObjectPrototypeMap, generatorObjectPrototype);
        _nativeContext.GeneratorObjectPrototypeMap = generatorObjectPrototypeMap;
    }

    void CreateAsyncIteratorMaps(JSFunction empty)
    {
        Isolate isolate = _isolate;
        // %AsyncIteratorPrototype%
        // https://tc39.es/proposal-async-iteration/#sec-asynciteratorprototype
        JSObject asyncIteratorPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);

        Bootstrapper.InstallFunctionAtSymbol(isolate, asyncIteratorPrototype, ReadOnlyRoots.async_iterator_symbol,
            "[Symbol.asyncIterator]", Builtin.ReturnReceiver, 0, true);
        _nativeContext.InitialAsyncIteratorPrototype = asyncIteratorPrototype;

        // %AsyncFromSyncIteratorPrototype%
        // https://tc39.es/proposal-async-iteration/#sec-%asyncfromsynciteratorprototype%-object
        JSObject asyncFromSyncIteratorPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        Bootstrapper.SimpleInstallFunction(isolate, asyncFromSyncIteratorPrototype, "next",
            Builtin.AsyncFromSyncIteratorPrototypeNext, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, asyncFromSyncIteratorPrototype, "return",
            Builtin.AsyncFromSyncIteratorPrototypeReturn, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, asyncFromSyncIteratorPrototype, "throw",
            Builtin.AsyncFromSyncIteratorPrototypeThrow, 1, false);

        Bootstrapper.InstallToStringTag(isolate, asyncFromSyncIteratorPrototype, "Async-from-Sync Iterator");

        JSObject.ForceSetPrototype(isolate, asyncFromSyncIteratorPrototype, asyncIteratorPrototype);

        Map asyncFromSyncIteratorMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSAsyncFromSyncIteratorType,
            JSObject.GetHeaderSize(InstanceType.JSAsyncFromSyncIteratorType));
        Map.SetPrototype(isolate, asyncFromSyncIteratorMap, asyncFromSyncIteratorPrototype);
        _nativeContext.AsyncFromSyncIteratorMap = asyncFromSyncIteratorMap;

        // Async Generators
        JSObject asyncGeneratorObjectPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        JSObject asyncGeneratorFunctionPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);

        // %AsyncGenerator% / %AsyncGeneratorFunction%.prototype
        JSObject.ForceSetPrototype(isolate, asyncGeneratorFunctionPrototype, empty);

        // The value of AsyncGeneratorFunction.prototype.prototype is the
        //     %AsyncGeneratorPrototype% intrinsic object.
        // This property has the attributes
        //     { [[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: true }.
        JSObject.AddProperty(isolate, asyncGeneratorFunctionPrototype, ReadOnlyRoots.prototype_string,
            asyncGeneratorObjectPrototype, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        JSObject.AddProperty(isolate, asyncGeneratorObjectPrototype, ReadOnlyRoots.constructor_string,
            asyncGeneratorFunctionPrototype, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        Bootstrapper.InstallToStringTag(isolate, asyncGeneratorFunctionPrototype, "AsyncGeneratorFunction");

        // %AsyncGeneratorPrototype%
        JSObject.ForceSetPrototype(isolate, asyncGeneratorObjectPrototype, asyncIteratorPrototype);
        _nativeContext.InitialAsyncGeneratorPrototype = asyncGeneratorObjectPrototype;

        Bootstrapper.InstallToStringTag(isolate, asyncGeneratorObjectPrototype, "AsyncGenerator");
        Bootstrapper.SimpleInstallFunction(isolate, asyncGeneratorObjectPrototype, "next",
            Builtin.AsyncGeneratorPrototypeNext, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, asyncGeneratorObjectPrototype, "return",
            Builtin.AsyncGeneratorPrototypeReturn, 1, false);
        Bootstrapper.SimpleInstallFunction(isolate, asyncGeneratorObjectPrototype, "throw",
            Builtin.AsyncGeneratorPrototypeThrow, 1, false);

        // Create maps for generator functions and their prototypes.  Store those
        // maps in the native context. The "prototype" property descriptor is
        // writable, non-enumerable, and non-configurable (as per ES6 draft
        // 04-14-15, section 25.2.4.3).
        // Async Generator functions do not have "caller" or "arguments" accessors.
        _nativeContext.AsyncGeneratorFunctionMap = CreateNonConstructorMap(isolate, _nativeContext.StrictFunctionMap,
            asyncGeneratorFunctionPrototype, "AsyncGeneratorFunction");
        _nativeContext.AsyncGeneratorFunctionWithNameMap = CreateNonConstructorMap(isolate,
            _nativeContext.StrictFunctionWithNameMap, asyncGeneratorFunctionPrototype, "AsyncGeneratorFunction with name");

        Map asyncGeneratorObjectPrototypeMap = Map.Create(isolate, 0);
        Map.SetPrototype(isolate, asyncGeneratorObjectPrototypeMap, asyncGeneratorObjectPrototype);
        _nativeContext.AsyncGeneratorObjectPrototypeMap = asyncGeneratorObjectPrototypeMap;
    }

    void CreateAsyncFunctionMaps(JSFunction empty)
    {
        Isolate isolate = _isolate;
        // %AsyncFunctionPrototype% intrinsic
        JSObject asyncFunctionPrototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
        JSObject.ForceSetPrototype(isolate, asyncFunctionPrototype, empty);

        Bootstrapper.InstallToStringTag(isolate, asyncFunctionPrototype, "AsyncFunction");

        Map map = Map.Copy(isolate, _nativeContext.StrictFunctionWithoutPrototypeMap, "AsyncFunction");
        Map.SetPrototype(isolate, map, asyncFunctionPrototype);
        _nativeContext.AsyncFunctionMap = map;

        map = Map.Copy(isolate, _nativeContext.MethodWithNameMap, "AsyncFunction with name");
        Map.SetPrototype(isolate, map, asyncFunctionPrototype);
        _nativeContext.AsyncFunctionWithNameMap = map;
    }

    void CreateJSProxyMaps()
    {
        Isolate isolate = _isolate;
        // Allocate maps for all Proxy types.
        // Next to the default proxy, we need maps indicating callable and
        // constructable proxies.
        Map proxyMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSProxyType, JSObject.kHeaderSize,
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND);
        proxyMap.IsDictionaryMap = true;
        proxyMap.MayHaveInterestingProperties = true;
        _nativeContext.ProxyMap = proxyMap;
        proxyMap.SetConstructor(_nativeContext.ObjectFunction);

        Map proxyCallableMap = Map.Copy(isolate, proxyMap, "callable Proxy");
        proxyCallableMap.IsCallable = true;
        _nativeContext.ProxyCallableMap = proxyCallableMap;
        proxyCallableMap.SetConstructor(_nativeContext.FunctionFunction);

        Map proxyConstructorMap = Map.Copy(isolate, proxyCallableMap, "constructor Proxy");
        proxyConstructorMap.IsConstructor = true;
        _nativeContext.ProxyConstructorMap = proxyConstructorMap;

        {
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSObjectType,
                JSObject.kHeaderSize + 2 * Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 2);
            Map.EnsureDescriptorSlack(isolate, map, 2);

            {  // proxy
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.proxy_string, 0, PropertyAttributes.NONE, Representation.Tagged);
                map.AppendDescriptor(isolate, d);
            }
            {  // revoke
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.revoke_string, 1, PropertyAttributes.NONE, Representation.Tagged);
                map.AppendDescriptor(isolate, d);
            }

            Map.SetPrototype(isolate, map, _nativeContext.InitialObjectPrototype);
            map.SetConstructor(_nativeContext.ObjectFunction);

            _nativeContext.ProxyRevocableResultMap = map;
        }
    }

    /// <summary>InitializeJSArrayMaps.</summary>
    static void InitializeJSArrayMaps(Isolate isolate, NativeContext nativeContext, Map initialMap)
    {
        // Replace all of the cached initial array maps in the native context with
        // the appropriate transitioned elements kind maps.
        Map currentMap = initialMap;
        ElementsKind kind = currentMap.ElementsKind;
        Debug.Assert(kind == ElementsKind.PACKED_SMI_ELEMENTS);
        nativeContext.Slots[Context.ArrayMapIndex(kind)] = currentMap;
        for (int i = ElementsKinds.GetSequenceIndexFromFastElementsKind(kind) + 1; i < ElementsKinds.kFastElementsKindCount; ++i)
        {
            ElementsKind nextKind = ElementsKinds.GetFastElementsKindFromSequenceIndex(i);
            Map? maybeElementsTransition = currentMap.ElementsTransitionMap(isolate);
            Map newMap = maybeElementsTransition ??
                Map.CopyAsElementsKind(isolate, currentMap, nextKind, TransitionFlag.INSERT_TRANSITION);
            Debug.Assert(newMap.ElementsKind == nextKind);
            nativeContext.Slots[Context.ArrayMapIndex(nextKind)] = newMap;
            currentMap = newMap;
        }
    }

    JSGlobalObject CreateNewGlobals(JSGlobalProxy globalProxy)
    {
        Isolate isolate = _isolate;
        // --- G l o b a l ---
        // Step 1: Create a fresh JSGlobalObject.
        JSObject prototype = _factory.NewFunctionPrototype(_nativeContext.ObjectFunction);
        JSFunction jsGlobalObjectFunction = Bootstrapper.CreateFunctionForBuiltinWithPrototype(isolate,
            ReadOnlyRoots.empty_string, Builtin.Illegal, prototype, InstanceType.JSGlobalObjectType,
            JSObject.GetHeaderSize(InstanceType.JSGlobalObjectType), 0, MutableMode.MUTABLE, 0, false);

        jsGlobalObjectFunction.InitialMap.IsPrototypeMap = true;
        jsGlobalObjectFunction.InitialMap.IsDictionaryMap = true;
        jsGlobalObjectFunction.InitialMap.MayHaveInterestingProperties = true;
        JSGlobalObject globalObject = _factory.NewJSGlobalObject(jsGlobalObjectFunction);

        // Step 2: (re)initialize the global proxy object.
        JSFunction globalProxyFunction = Bootstrapper.CreateFunctionForBuiltinWithPrototype(isolate,
            ReadOnlyRoots.empty_string, Builtin.Illegal, JSValue.TheHole, InstanceType.JSGlobalProxyType,
            JSObject.GetHeaderSize(InstanceType.JSGlobalProxyType), 0, MutableMode.MUTABLE, 0, false);
        globalProxyFunction.InitialMap.IsAccessCheckNeeded = true;
        globalProxyFunction.InitialMap.MayHaveInterestingProperties = true;
        _nativeContext.GlobalProxyFunction = globalProxyFunction;

        // Set the global object as the (hidden) __proto__ of the global proxy after
        // ConfigureGlobalObject
        _factory.ReinitializeJSGlobalProxy(globalProxy, globalProxyFunction);

        // Set up the pointer back from the global object to the global proxy.
        globalObject.GlobalProxy = globalProxy;
        // Set the native context of the global proxy.
        globalProxy.Map.NativeContext = _nativeContext;
        _nativeContext.GlobalProxyObject = globalProxy;

        return globalObject;
    }

    void InitializeMapCaches()
    {
        {
            _nativeContext.NormalizedMapCache = new NormalizedMapCache();
        }

        {
            FixedArray cache = _factory.NewFixedArray(JSObject.kMapCacheSize);
            for (int i = 0; i < JSObject.kMapCacheSize; i++) cache[i] = JSValue.Undefined;
            _nativeContext.MapCache = cache;
            Map initial = _nativeContext.ObjectFunction.InitialMap;
            cache[0] = initial;
            cache[initial.GetInObjectProperties()] = initial;
        }
    }

    /// <summary>InstallError.</summary>
    void InstallError(JSObject global, JSString name, Context.Field contextIndex,
        Builtin errorConstructor = Builtin.ErrorConstructor, int errorFunctionLength = 1)
    {
        Isolate isolate = _isolate;
        // Most Error objects consist of a message, a stack trace, and possibly a
        // cause. Reserve three in-object properties for these.
        const int inObjectProperties = 3;
        const int kErrorObjectSize = JSObject.kHeaderSize + inObjectProperties * Map.kTaggedSize;
        JSFunction errorFun = Bootstrapper.InstallFunction(isolate, global, name, InstanceType.JSErrorType, kErrorObjectSize,
            inObjectProperties, JSValue.TheHole, errorConstructor, errorFunctionLength, false);

        if (contextIndex == Context.Field.ERROR_FUNCTION_INDEX)
        {
            Bootstrapper.SimpleInstallFunction(isolate, errorFun, "captureStackTrace", Builtin.ErrorCaptureStackTrace, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, errorFun, "isError", Builtin.ErrorIsError, 1, true);
        }

        Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, errorFun, contextIndex);

        {
            // Setup %XXXErrorPrototype%.
            var prototype = (JSObject)errorFun.InstancePrototype;

            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.name_string, name, PropertyAttributes.DONT_ENUM);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.message_string, ReadOnlyRoots.empty_string,
                PropertyAttributes.DONT_ENUM);

            if (contextIndex == Context.Field.ERROR_FUNCTION_INDEX)
            {
                JSFunction toStringFun = Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString",
                    Builtin.ErrorPrototypeToString, 0, true);
                _nativeContext.ErrorToString = toStringFun;
                _nativeContext.InitialErrorPrototype = prototype;
            }
            else
            {
                JSFunction globalError = _nativeContext.ErrorFunction;
                JSReceiver.SetPrototype(isolate, errorFun, globalError, false, ShouldThrow.ThrowOnError);
                JSReceiver.SetPrototype(isolate, prototype, globalError.Prototype, false, ShouldThrow.ThrowOnError);
            }
        }

        Map initialMap = errorFun.InitialMap;
        Map.EnsureDescriptorSlack(isolate, initialMap, 3);

        {  // error_stack_symbol
            Descriptor d = Descriptor.DataField(ReadOnlyRoots.error_stack_symbol, 0, PropertyAttributes.DONT_ENUM, Representation.Tagged);
            initialMap.AppendDescriptor(isolate, d);
        }
        {
            // error_message_symbol
            Descriptor d = Descriptor.DataField(ReadOnlyRoots.error_message_symbol, 1, PropertyAttributes.DONT_ENUM, Representation.Tagged);
            initialMap.AppendDescriptor(isolate, d);
        }
        {  // stack
            AccessorPair newPair = _factory.NewAccessorPair();
            newPair.Getter = ErrorStackGetterFun();
            newPair.Setter = ErrorStackSetterFun();

            Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.stack_string, newPair, PropertyAttributes.DONT_ENUM);
            initialMap.AppendDescriptor(isolate, d);
        }
    }

    // V8 keeps error_stack_getter_fun_template/error_stack_setter_fun_template as
    // FunctionTemplateInfo roots instantiated lazily per context; V8Sharp creates
    // the two functions eagerly, once per native context.
    JSFunction ErrorStackGetterFun()
    {
        if (_nativeContext.ErrorStackGetterFun is { } fun) return fun;
        fun = CreateApiFunction(ReadOnlyRoots.empty_string, Accessors.ErrorStackGetter, 0);
        _nativeContext.ErrorStackGetterFun = fun;
        return fun;
    }

    JSFunction ErrorStackSetterFun()
    {
        if (_nativeContext.ErrorStackSetterFun is { } fun) return fun;
        fun = CreateApiFunction(ReadOnlyRoots.empty_string, Accessors.ErrorStackSetter, 1);
        _nativeContext.ErrorStackSetterFun = fun;
        return fun;
    }

    void InstallErrorStackAccessorFunctions()
    {
        ErrorStackGetterFun();
        ErrorStackSetterFun();
    }

    JSFunction CreateApiFunction(JSString name, BuiltinFunction callback, int length)
    {
        var data = new FunctionTemplateInfo(callback) { Length = length };
        SharedFunctionInfo info = _factory.NewSharedFunctionInfo(name, data, Builtin.HandleApiCallOrConstruct, length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        return _factory.NewFunction(info, _nativeContext, _nativeContext.StrictFunctionWithoutPrototypeMap);
    }

    /// <summary>Genesis::CreateInitialMapForArraySubclass.</summary>
    Map CreateInitialMapForArraySubclass(int size, int inobjectProperties)
    {
        Isolate isolate = _isolate;
        // Find global.Array.prototype to inherit from.
        JSFunction arrayConstructor = _nativeContext.ArrayFunction;
        JSObject arrayPrototype = _nativeContext.InitialArrayPrototype;

        // Add initial map.
        Map initialMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSArrayType,
            JSObject.GetHeaderSize(InstanceType.JSArrayType) + inobjectProperties * Map.kTaggedSize,
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, inobjectProperties);
        initialMap.SetConstructor(arrayConstructor);

        // Set prototype on map.
        Map.SetPrototype(isolate, initialMap, arrayPrototype);

        // Update map with length accessor from Array.
        const int kTheLengthAccessor = 1;
        Map.EnsureDescriptorSlack(isolate, initialMap, inobjectProperties + kTheLengthAccessor);

        // length descriptor.
        {
            JSFunction arrayFunction = _nativeContext.ArrayFunction;
            DescriptorArray arrayDescriptors = arrayFunction.InitialMap.InstanceDescriptors;
            JSString length = ReadOnlyRoots.length_string;
            InternalIndex old = arrayDescriptors.Search(length, arrayFunction.InitialMap.NumberOfOwnDescriptors);
            Debug.Assert(old.IsFound);
            Descriptor d = Descriptor.AccessorConstant(length, arrayDescriptors.GetStrongValue(old).Object,
                arrayDescriptors.GetDetails(old).Attributes);
            initialMap.AppendDescriptor(isolate, d);
        }
        return initialMap;
    }

    void InitializeIteratorFunctions()
    {
        Isolate isolate = _isolate;
        JSObject iteratorPrototype = _nativeContext.InitialIteratorPrototype;

        {  // -- G e n e r a t o r
            var generatorFunctionPrototype = (JSObject)_nativeContext.GeneratorFunctionMap.Prototype!;
            JSFunction generatorFunctionFunction = Bootstrapper.CreateFunction(isolate, "GeneratorFunction",
                InstanceType.JSFunctionType, 0, 0, generatorFunctionPrototype, Builtin.GeneratorFunctionConstructor, 1,
                false);
            generatorFunctionFunction.PrototypeOrInitialMap = _nativeContext.GeneratorFunctionMap;
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, generatorFunctionFunction,
                Context.Field.GENERATOR_FUNCTION_FUNCTION_INDEX);

            JSObject.ForceSetPrototype(isolate, generatorFunctionFunction, _nativeContext.FunctionFunction);
            JSObject.AddProperty(isolate, generatorFunctionPrototype, ReadOnlyRoots.constructor_string,
                generatorFunctionFunction, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

            _nativeContext.GeneratorFunctionMap.SetConstructor(generatorFunctionFunction);
            _nativeContext.GeneratorFunctionWithNameMap.SetConstructor(generatorFunctionFunction);
        }

        {  // -- A s y n c G e n e r a t o r
            var asyncGeneratorFunctionPrototype = (JSObject)_nativeContext.AsyncGeneratorFunctionMap.Prototype!;
            JSFunction asyncGeneratorFunctionFunction = Bootstrapper.CreateFunction(isolate, "AsyncGeneratorFunction",
                InstanceType.JSFunctionType, 0, 0, asyncGeneratorFunctionPrototype,
                Builtin.AsyncGeneratorFunctionConstructor, 1, false);
            asyncGeneratorFunctionFunction.PrototypeOrInitialMap = _nativeContext.AsyncGeneratorFunctionMap;
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, asyncGeneratorFunctionFunction,
                Context.Field.ASYNC_GENERATOR_FUNCTION_FUNCTION_INDEX);

            JSObject.ForceSetPrototype(isolate, asyncGeneratorFunctionFunction, _nativeContext.FunctionFunction);

            JSObject.AddProperty(isolate, asyncGeneratorFunctionPrototype, ReadOnlyRoots.constructor_string,
                asyncGeneratorFunctionFunction, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

            _nativeContext.AsyncGeneratorFunctionMap.SetConstructor(asyncGeneratorFunctionFunction);
            _nativeContext.AsyncGeneratorFunctionWithNameMap.SetConstructor(asyncGeneratorFunctionFunction);
        }

        {  // -- S e t I t e r a t o r
            // Setup %SetIteratorPrototype%.
            JSObject prototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, prototype, iteratorPrototype);

            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.SetIterator_string);

            // Install the next function on the {prototype}.
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "next", Builtin.SetIteratorPrototypeNext, 0, true);
            _nativeContext.InitialSetIteratorPrototype = prototype;
            prototype.Map.InstanceType = InstanceType.JSSetIteratorPrototypeType;

            // Setup SetIterator constructor.
            JSFunction setIteratorFunction = Bootstrapper.CreateFunction(isolate, "SetIterator",
                InstanceType.JSSetValueIteratorType, 0, 0, prototype, Builtin.Illegal, 0, false);
            setIteratorFunction.Shared.Native = false;

            Map setValueIteratorMap = setIteratorFunction.InitialMap;
            _nativeContext.SetValueIteratorMap = setValueIteratorMap;

            Map setKeyValueIteratorMap = Map.Copy(isolate, setValueIteratorMap, "JS_SET_KEY_VALUE_ITERATOR_TYPE");
            setKeyValueIteratorMap.InstanceType = InstanceType.JSSetKeyValueIteratorType;
            _nativeContext.SetKeyValueIteratorMap = setKeyValueIteratorMap;
        }

        {  // -- M a p I t e r a t o r
            // Setup %MapIteratorPrototype%.
            JSObject prototype = _factory.NewJSObject(_nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, prototype, iteratorPrototype);

            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.MapIterator_string);

            // Install the next function on the {prototype}.
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "next", Builtin.MapIteratorPrototypeNext, 0, true);
            _nativeContext.InitialMapIteratorPrototype = prototype;
            prototype.Map.InstanceType = InstanceType.JSMapIteratorPrototypeType;

            // Setup MapIterator constructor.
            JSFunction mapIteratorFunction = Bootstrapper.CreateFunction(isolate, "MapIterator",
                InstanceType.JSMapKeyIteratorType, 0, 0, prototype, Builtin.Illegal, 0, false);
            mapIteratorFunction.Shared.Native = false;

            Map mapKeyIteratorMap = mapIteratorFunction.InitialMap;
            _nativeContext.MapKeyIteratorMap = mapKeyIteratorMap;

            Map mapKeyValueIteratorMap = Map.Copy(isolate, mapKeyIteratorMap, "JS_MAP_KEY_VALUE_ITERATOR_TYPE");
            mapKeyValueIteratorMap.InstanceType = InstanceType.JSMapKeyValueIteratorType;
            _nativeContext.MapKeyValueIteratorMap = mapKeyValueIteratorMap;

            Map mapValueIteratorMap = Map.Copy(isolate, mapKeyIteratorMap, "JS_MAP_VALUE_ITERATOR_TYPE");
            mapValueIteratorMap.InstanceType = InstanceType.JSMapValueIteratorType;
            _nativeContext.MapValueIteratorMap = mapValueIteratorMap;
        }

        {  // -- A s y n c F u n c t i o n
            // Builtin functions for AsyncFunction.
            var asyncFunctionPrototype = (JSObject)_nativeContext.AsyncFunctionMap.Prototype!;

            JSFunction asyncFunctionConstructor = Bootstrapper.CreateFunction(isolate, "AsyncFunction",
                InstanceType.JSFunctionType, 0, 0, asyncFunctionPrototype, Builtin.AsyncFunctionConstructor, 1, false);
            asyncFunctionConstructor.PrototypeOrInitialMap = _nativeContext.AsyncFunctionMap;
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, asyncFunctionConstructor,
                Context.Field.ASYNC_FUNCTION_FUNCTION_INDEX);

            _nativeContext.AsyncFunctionConstructor = asyncFunctionConstructor;
            JSObject.ForceSetPrototype(isolate, asyncFunctionConstructor, _nativeContext.FunctionFunction);

            JSObject.AddProperty(isolate, asyncFunctionPrototype, ReadOnlyRoots.constructor_string,
                asyncFunctionConstructor, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

            // Async functions don't have a prototype, but they use generator objects
            // under the hood to model the suspend/resume (in await). Instead of using
            // the "prototype" / initial_map machinery (like for (async) generators),
            // there's one global (per native context) map here that is used for the
            // async function generator objects. These objects never escape to user
            // JavaScript anyways.
            Map asyncFunctionObjectMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSAsyncFunctionObjectType,
                JSObject.GetHeaderSize(InstanceType.JSAsyncFunctionObjectType));
            _nativeContext.AsyncFunctionObjectMap = asyncFunctionObjectMap;

            _nativeContext.AsyncFunctionMap.SetConstructor(asyncFunctionConstructor);
            _nativeContext.AsyncFunctionWithNameMap.SetConstructor(asyncFunctionConstructor);
        }
    }

    void InitializeCallSiteBuiltins()
    {
        Isolate isolate = _isolate;
        // -- C a l l S i t e
        // Builtin functions for CallSite.

        // CallSites are a special case; the constructor is for our private use
        // only, therefore we set it up as a builtin that throws. Internally, we use
        // CallSiteUtils::Construct to create CallSite objects.

        JSFunction callsiteFun = Bootstrapper.CreateFunction(isolate, "CallSite", InstanceType.JSObjectType,
            JSObject.kHeaderSize, 0, JSValue.TheHole, Builtin.UnsupportedThrower, 0, false);
        _nativeContext.CallSiteFunction = callsiteFun;

        // Setup CallSite.prototype.
        var prototype = (JSObject)callsiteFun.InstancePrototype;

        ReadOnlySpan<(string Name, Builtin Id)> infos =
        [
            ("getColumnNumber", Builtin.CallSitePrototypeGetColumnNumber),
            ("getEnclosingColumnNumber", Builtin.CallSitePrototypeGetEnclosingColumnNumber),
            ("getEnclosingLineNumber", Builtin.CallSitePrototypeGetEnclosingLineNumber),
            ("getEvalOrigin", Builtin.CallSitePrototypeGetEvalOrigin),
            ("getFileName", Builtin.CallSitePrototypeGetFileName),
            ("getFunction", Builtin.CallSitePrototypeGetFunction),
            ("getFunctionName", Builtin.CallSitePrototypeGetFunctionName),
            ("getLineNumber", Builtin.CallSitePrototypeGetLineNumber),
            ("getMethodName", Builtin.CallSitePrototypeGetMethodName),
            ("getPosition", Builtin.CallSitePrototypeGetPosition),
            ("getPromiseIndex", Builtin.CallSitePrototypeGetPromiseIndex),
            ("getScriptNameOrSourceURL", Builtin.CallSitePrototypeGetScriptNameOrSourceURL),
            ("getScriptHash", Builtin.CallSitePrototypeGetScriptHash),
            ("getThis", Builtin.CallSitePrototypeGetThis),
            ("getTypeName", Builtin.CallSitePrototypeGetTypeName),
            ("isAsync", Builtin.CallSitePrototypeIsAsync),
            ("isConstructor", Builtin.CallSitePrototypeIsConstructor),
            ("isEval", Builtin.CallSitePrototypeIsEval),
            ("isNative", Builtin.CallSitePrototypeIsNative),
            ("isPromiseAll", Builtin.CallSitePrototypeIsPromiseAll),
            ("isToplevel", Builtin.CallSitePrototypeIsToplevel),
            ("toString", Builtin.CallSitePrototypeToString),
        ];

        const PropertyAttributes attrs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;

        foreach ((string name, Builtin id) in infos)
        {
            Bootstrapper.SimpleInstallFunction(isolate, prototype, name, id, 0, true, attrs);
        }
    }

    void InstallABunchOfRandomThings()
    {
        Isolate isolate = _isolate;

        // Store the map for the %ObjectPrototype% after the natives has been compiled
        // and the Object function has been set up.
        {
            JSFunction objectFunction = _nativeContext.ObjectFunction;
            var objectFunctionPrototype = (JSObject)objectFunction.InitialMap.Prototype!;
            Debug.Assert(objectFunctionPrototype.HasFastProperties);
            _nativeContext.ObjectFunctionPrototype = objectFunctionPrototype;
            _nativeContext.ObjectFunctionPrototypeMap = objectFunctionPrototype.Map;
        }

        // Store the map for the %StringPrototype% after the natives has been compiled
        // and the String function has been set up.
        JSFunction stringFunction = _nativeContext.StringFunction;
        var stringFunctionPrototype = (JSObject)stringFunction.InitialMap.Prototype!;
        Debug.Assert(stringFunctionPrototype.HasFastProperties);
        _nativeContext.StringFunctionPrototypeMap = stringFunctionPrototype.Map;

        JSGlobalObject globalObject = _nativeContext.GlobalObject;

        // Install Global.decodeURI.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "decodeURI", Builtin.GlobalDecodeURI, 1, false);

        // Install Global.decodeURIComponent.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "decodeURIComponent",
            Builtin.GlobalDecodeURIComponent, 1, false);

        // Install Global.encodeURI.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "encodeURI", Builtin.GlobalEncodeURI, 1, false);

        // Install Global.encodeURIComponent.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "encodeURIComponent",
            Builtin.GlobalEncodeURIComponent, 1, false);

        // Install Global.escape.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "escape", Builtin.GlobalEscape, 1, false);

        // Install Global.unescape.
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "unescape", Builtin.GlobalUnescape, 1, false);

        // Install Global.eval.
        {
            JSFunction eval = Bootstrapper.SimpleInstallFunction(isolate, globalObject, "eval", Builtin.GlobalEval, 1, false);
            _nativeContext.GlobalEvalFun = eval;
        }

        // Install Global.isFinite
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "isFinite", Builtin.GlobalIsFinite, 1, true);

        // Install Global.isNaN
        Bootstrapper.InstallFunctionWithBuiltinId(isolate, globalObject, "isNaN", Builtin.GlobalIsNaN, 1, true);

        // Install Array builtin functions.
        {
            JSFunction arrayConstructor = _nativeContext.ArrayFunction;
            var proto = (JSArray)arrayConstructor.Prototype.Object;

            // Verification of important array prototype properties.
            Debug.Assert(proto.Length.IsSmi && proto.Length.Number == 0);
            Debug.Assert(proto.HasSmiOrObjectElements);
            // This is necessary to enable fast checks for absence of elements
            // on Array.prototype and below.
            proto.Elements = ReadOnlyRoots.empty_fixed_array;
        }

        // Create a map for accessor property descriptors (a variant of JSObject
        // that predefines four properties get, set, configurable and enumerable).
        {
            // AccessorPropertyDescriptor initial map.
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSObjectType,
                JSObject.kHeaderSize + 4 * Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 4);
            // Create the descriptor array for the property descriptor object.
            Map.EnsureDescriptorSlack(isolate, map, 4);
            AppendField(map, ReadOnlyRoots.get_string, 0, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.set_string, 1, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.enumerable_string, 2, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.configurable_string, 3, PropertyAttributes.NONE);

            Map.SetPrototype(isolate, map, _nativeContext.InitialObjectPrototype);
            map.SetConstructor(_nativeContext.ObjectFunction);

            _nativeContext.AccessorPropertyDescriptorMap = map;
        }

        // Create a map for data property descriptors (a variant of JSObject
        // that predefines four properties value, writable, configurable and
        // enumerable).
        {
            // DataPropertyDescriptor initial map.
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSObjectType,
                JSObject.kHeaderSize + 4 * Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 4);
            // Create the descriptor array for the property descriptor object.
            Map.EnsureDescriptorSlack(isolate, map, 4);
            AppendField(map, ReadOnlyRoots.value_string, 0, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.writable_string, 1, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.enumerable_string, 2, PropertyAttributes.NONE);
            AppendField(map, ReadOnlyRoots.configurable_string, 3, PropertyAttributes.NONE);

            Map.SetPrototype(isolate, map, _nativeContext.InitialObjectPrototype);
            map.SetConstructor(_nativeContext.ObjectFunction);

            _nativeContext.DataPropertyDescriptorMap = map;
        }

        // TODO(v8sharp): the TemplateLiteral JSArray map (needs
        // TemplateLiteralObject) is created by the interpreter port.

        // Create a constructor for RegExp results (a variant of Array that
        // predefines the properties index, input, and groups).
        {
            // JSRegExpResult initial map.
            const int kRegExpResultInObjectPropertyCount = 5;
            Map initialMap = CreateInitialMapForArraySubclass(0, kRegExpResultInObjectPropertyCount);

            // index descriptor.
            AppendField(initialMap, ReadOnlyRoots.index_string, 0, PropertyAttributes.NONE);
            // input descriptor.
            AppendField(initialMap, ReadOnlyRoots.input_string, 1, PropertyAttributes.NONE);
            // groups descriptor.
            AppendField(initialMap, ReadOnlyRoots.groups_string, 2, PropertyAttributes.NONE);

            // Private internal only fields. All of the remaining fields have special
            // symbols to prevent their use in Javascript.
            {
                const PropertyAttributes attribs = PropertyAttributes.DONT_ENUM;
                // regexp_input_index descriptor.
                AppendField(initialMap, ReadOnlyRoots.regexp_result_regexp_input_symbol, 3, attribs);
                // regexp_last_index descriptor.
                AppendField(initialMap, ReadOnlyRoots.regexp_result_regexp_last_index_symbol, 4, attribs);
            }

            // Set up the map for RegExp results objects for regexps with the /d flag.
            Map initialWithIndicesMap = Map.Copy(isolate, initialMap, "JSRegExpResult with indices");
            initialWithIndicesMap.SetInstanceSize(initialWithIndicesMap.InstanceSize + Map.kTaggedSize);

            // indices descriptor
            {
                Map.EnsureDescriptorSlack(isolate, initialWithIndicesMap, 1);
                AppendField(initialWithIndicesMap, ReadOnlyRoots.indices_string, 5, PropertyAttributes.NONE);
            }

            _nativeContext.RegExpResultMap = initialMap;
            _nativeContext.RegExpResultWithIndicesMap = initialWithIndicesMap;
        }

        // Create a constructor for JSRegExpResultIndices (a variant of Array that
        // predefines the groups property).
        {
            // JSRegExpResultIndices initial map.
            Map initialMap = CreateInitialMapForArraySubclass(0, 1);

            // groups descriptor.
            AppendField(initialMap, ReadOnlyRoots.groups_string, 0, PropertyAttributes.NONE);

            _nativeContext.RegExpResultIndicesMap = initialMap;
        }

        // Add @@iterator method to the arguments object maps.
        {
            const PropertyAttributes attribs = PropertyAttributes.DONT_ENUM;
            AccessorInfo argumentsIterator = Accessors.ArgumentsIteratorAccessor;
            foreach (Map map in (ReadOnlySpan<Map>)[_nativeContext.SloppyArgumentsMap, _nativeContext.FastAliasedArgumentsMap,
                         _nativeContext.SlowAliasedArgumentsMap, _nativeContext.StrictArgumentsMap])
            {
                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.iterator_symbol, argumentsIterator, attribs);
                Map.EnsureDescriptorSlack(isolate, map, 1);
                map.AppendDescriptor(isolate, d);
            }
        }
    }

    /// <summary>INSTALL_ITERATOR_HELPER: a helper map plus the prototype method creating it.</summary>
    void InstallIteratorHelper(JSObject iteratorHelperPrototype, JSFunction iteratorFunction, JSObject baseObject,
        string lowercaseName, InstanceType type, Builtin builtin, int argc, bool adapt, Context.Field mapIndex)
    {
        Map map = _factory.NewContextfulMapForCurrentContext(type, JSObject.GetHeaderSize(type),
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
        Map.SetPrototype(_isolate, map, iteratorHelperPrototype);
        map.SetConstructor(iteratorFunction);
        _nativeContext.Slots[(int)mapIndex] = map;
        Bootstrapper.SimpleInstallFunction(_isolate, baseObject, lowercaseName, builtin, argc, adapt);
    }

    void AppendField(Map map, Name name, int fieldIndex, PropertyAttributes attributes)
    {
        Descriptor d = Descriptor.DataField(name, fieldIndex, attributes, Representation.Tagged);
        map.AppendDescriptor(_isolate, d);
    }
}
