// Port of Genesis::InitializeGlobal (src/init/bootstrapper.cc): installs the
// ECMAScript intrinsics on the global object. The builtins are referenced by
// id (V8's Builtin enum); their C# implementations are registered in
// BuiltinRegistry by the builtins ports. Sections not ported yet are listed
// in Genesis.cs.
namespace V8Sharp.Init;

sealed partial class Genesis
{
    void InitializeGlobal(JSGlobalObject globalObject, JSFunction emptyFunction)
    {
        Isolate isolate = _isolate;
        // --- N a t i v e   C o n t e x t ---
        // Set extension and global object.
        _nativeContext.GlobalObject = globalObject;
        // Security setup: Set the security token of the native context to the global
        // object. This makes the security check between two different contexts fail
        // by default even in case of global object reinitialization.
        _nativeContext.SecurityToken = globalObject;

        {  // -- C o n t e x t
            _nativeContext.ScriptContextTable = new ScriptContextTable();
            InstallGlobalThisBinding();
        }

        // ---- bootstrapper.cc 2204-2307
        {  // --- O b j e c t ---
            JSString objectName = ReadOnlyRoots.Object_string;
            JSFunction objectFunction = nativeContext.ObjectFunction;
            JSObject.AddProperty(isolate, globalObject, objectName, objectFunction, PropertyAttributes.DONT_ENUM);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "assign", Builtin.ObjectAssign, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "getOwnPropertyDescriptor", Builtin.ObjectGetOwnPropertyDescriptor, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "getOwnPropertyDescriptors", Builtin.ObjectGetOwnPropertyDescriptors, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "getOwnPropertyNames", Builtin.ObjectGetOwnPropertyNames, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "getOwnPropertySymbols", Builtin.ObjectGetOwnPropertySymbols, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "hasOwn", Builtin.ObjectHasOwn, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "is", Builtin.ObjectIs, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "preventExtensions", Builtin.ObjectPreventExtensions, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "seal", Builtin.ObjectSeal, 1, false);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "create", Builtin.ObjectCreate, 2, false);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "defineProperties", Builtin.ObjectDefineProperties, 2, true);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "defineProperty", Builtin.ObjectDefineProperty, 3, true);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "freeze", Builtin.ObjectFreeze, 1, false);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "getPrototypeOf", Builtin.ObjectGetPrototypeOf, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "setPrototypeOf", Builtin.ObjectSetPrototypeOf, 2, true);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "isExtensible", Builtin.ObjectIsExtensible, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "isFrozen", Builtin.ObjectIsFrozen, 1, false);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "isSealed", Builtin.ObjectIsSealed, 1, false);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "keys", Builtin.ObjectKeys, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "entries", Builtin.ObjectEntries, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "fromEntries", Builtin.ObjectFromEntries, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "values", Builtin.ObjectValues, 1, true);

            Bootstrapper.SimpleInstallFunction(isolate, objectFunction, "groupBy", Builtin.ObjectGroupBy, 2, true);

            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "__defineGetter__", Builtin.ObjectDefineGetter, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "__defineSetter__", Builtin.ObjectDefineSetter, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "hasOwnProperty", Builtin.ObjectPrototypeHasOwnProperty, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "__lookupGetter__", Builtin.ObjectLookupGetter, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "__lookupSetter__", Builtin.ObjectLookupSetter, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "isPrototypeOf", Builtin.ObjectPrototypeIsPrototypeOf, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "propertyIsEnumerable", Builtin.ObjectPrototypePropertyIsEnumerable, 1, false);
            JSFunction objectToString = Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "toString", Builtin.ObjectPrototypeToString, 0, true);
            nativeContext.ObjectToString = objectToString;
            JSFunction objectValueOf = Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "valueOf", Builtin.ObjectPrototypeValueOf, 0, true);
            nativeContext.ObjectValueOfFunction = objectValueOf;

            Bootstrapper.SimpleInstallGetterSetter(isolate, nativeContext.InitialObjectPrototype, ReadOnlyRoots.proto_string, Builtin.ObjectPrototypeGetProto, Builtin.ObjectPrototypeSetProto);

            Bootstrapper.SimpleInstallFunction(isolate, nativeContext.InitialObjectPrototype, "toLocaleString", Builtin.ObjectPrototypeToLocaleString, 0, true);
        }

        JSObject global = nativeContext.GlobalObject;
        // ---- bootstrapper.cc 2309-2375
        {  // --- F u n c t i o n ---
            JSFunction prototype = emptyFunction;
            JSFunction functionFun = Bootstrapper.InstallFunction(isolate, global, "Function", InstanceType.JSFunctionType, JSObject.GetHeaderSize(InstanceType.JSFunctionType), 0, prototype, Builtin.FunctionConstructor, 1, false);
            // Function instances are sloppy by default.
            functionFun.PrototypeOrInitialMap = nativeContext.SloppyFunctionMap;
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, functionFun, Context.Field.FUNCTION_FUNCTION_INDEX);
            nativeContext.FunctionPrototype = prototype;

            // Setup the methods on the %FunctionPrototype%.
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.constructor_string, functionFun, PropertyAttributes.DONT_ENUM);
            JSFunction functionPrototypeApply = Bootstrapper.SimpleInstallFunction(isolate, prototype, "apply", Builtin.FunctionPrototypeApply, 2, false);
            nativeContext.FunctionPrototypeApply = functionPrototypeApply;
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "bind", Builtin.FastFunctionPrototypeBind, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "call", Builtin.FunctionPrototypeCall, 1, false);
            JSFunction functionToString = Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.FunctionPrototypeToString, 0, false);
            nativeContext.FunctionToString = functionToString;

            // Install the @@hasInstance function.
            JSFunction hasInstance = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.has_instance_symbol, "[Symbol.hasInstance]", Builtin.FunctionPrototypeHasInstance, 1, true, PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY);
            nativeContext.FunctionHasInstance = hasInstance;

            // The .arguments and .caller getters are non-standard.
            Bootstrapper.SimpleInstallGetterSetter(isolate, prototype, ReadOnlyRoots.arguments_string, Builtin.FunctionPrototypeLegacyArgumentsGetter, Builtin.FunctionPrototypeLegacyArgumentsSetter);
            Bootstrapper.SimpleInstallGetterSetter(isolate, prototype, ReadOnlyRoots.caller_string, Builtin.FunctionPrototypeLegacyCallerGetter, Builtin.FunctionPrototypeLegacyCallerSetter);

            // Complete setting up function maps.
            {
                nativeContext.SloppyFunctionMap.SetConstructor(functionFun);
                nativeContext.SloppyFunctionWithNameMap.SetConstructor(functionFun);
                nativeContext.SloppyFunctionWithReadonlyPrototypeMap.SetConstructor(functionFun);
                nativeContext.SloppyFunctionWithoutPrototypeMap.SetConstructor(functionFun);

                nativeContext.StrictFunctionMap.SetConstructor(functionFun);
                nativeContext.StrictFunctionWithNameMap.SetConstructor(functionFun);
                nativeContext.StrictFunctionWithReadonlyPrototypeMap.SetConstructor(functionFun);
                nativeContext.StrictFunctionWithoutPrototypeMap.SetConstructor(functionFun);

                nativeContext.ClassFunctionMap.SetConstructor(functionFun);
            }
        }

        // ---- bootstrapper.cc 2377-2561
        {  // --- A r r a y ---
            // This seems a bit hackish, but we need to make sure Array.length is 1.
            int length = 1;
            JSFunction arrayFunction = Bootstrapper.InstallFunction(isolate, global, "Array", InstanceType.JSArrayType, JSObject.GetHeaderSize(InstanceType.JSArrayType), 0, nativeContext.InitialObjectPrototype, Builtin.ArrayConstructor, length, false);

            Map initialMap = arrayFunction.InitialMap;

            // This assert protects an optimization in
            // HGraphBuilder.JSArrayBuilder.EmitMapCode()
            Debug.Assert(initialMap.ElementsKind == ElementsKinds.GetInitialFastElementsKind());
            Map.EnsureDescriptorSlack(isolate, initialMap, 1);

            PropertyAttributes attribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE;


            {  // Add length.
                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.ArrayLengthAccessor, attribs);
                initialMap.AppendDescriptor(isolate, d);
            }

            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, arrayFunction, Context.Field.ARRAY_FUNCTION_INDEX);
            Bootstrapper.InstallSpeciesGetter(isolate, arrayFunction);

            // Create the initial array map for Array.prototype which is required by
            // the used ArrayConstructorStub.
            // This is repeated after properly instantiating the Array.prototype.
            InitializeJSArrayMaps(isolate, nativeContext, initialMap);

            // Set up %ArrayPrototype%.
            // The %ArrayPrototype% has ElementsKind.TERMINAL_FAST_ELEMENTS_KIND in order to ensure
            // that constant functions stay constant after turning prototype to setup
            // mode and back.
            JSArray proto = _factory.NewJSArray(ElementsKind.TERMINAL_FAST_ELEMENTS_KIND);
            JSFunction.SetPrototype(isolate, arrayFunction, proto);
            nativeContext.InitialArrayPrototype = proto;

            InitializeJSArrayMaps(isolate, nativeContext, arrayFunction.InitialMap);
            Bootstrapper.SimpleInstallFunction(isolate, arrayFunction, "isArray", Builtin.ArrayIsArray, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, arrayFunction, "from", Builtin.ArrayFrom, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, arrayFunction, "fromAsync", Builtin.ArrayFromAsync, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, arrayFunction, "of", Builtin.ArrayOf, 0, false);
            Bootstrapper.SetConstructorInstanceType(isolate, arrayFunction, InstanceType.JSArrayConstructorType);

            JSObject.AddProperty(isolate, proto, ReadOnlyRoots.constructor_string, arrayFunction, PropertyAttributes.DONT_ENUM);

            Bootstrapper.SimpleInstallFunction(isolate, proto, "at", Builtin.ArrayPrototypeAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "concat", Builtin.ArrayPrototypeConcat, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "copyWithin", Builtin.ArrayPrototypeCopyWithin, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "fill", Builtin.ArrayPrototypeFill, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "find", Builtin.ArrayPrototypeFind, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "findIndex", Builtin.ArrayPrototypeFindIndex, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "findLast", Builtin.ArrayPrototypeFindLast, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "findLastIndex", Builtin.ArrayPrototypeFindLastIndex, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "lastIndexOf", Builtin.ArrayPrototypeLastIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "pop", Builtin.ArrayPrototypePop, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "push", Builtin.ArrayPrototypePush, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "reverse", Builtin.ArrayPrototypeReverse, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "shift", Builtin.ArrayPrototypeShift, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "unshift", Builtin.ArrayPrototypeUnshift, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "slice", Builtin.ArrayPrototypeSlice, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "sort", Builtin.ArrayPrototypeSort, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "splice", Builtin.ArrayPrototypeSplice, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "includes", Builtin.ArrayIncludes, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "indexOf", Builtin.ArrayIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "join", Builtin.ArrayPrototypeJoin, 1, false);

            {  // Set up iterator-related properties.
                JSFunction keys = Bootstrapper.InstallFunctionWithBuiltinId(isolate, proto, "keys", Builtin.ArrayPrototypeKeys, 0, true);
                nativeContext.ArrayKeysIterator = keys;

                JSFunction entries = Bootstrapper.InstallFunctionWithBuiltinId(isolate, proto, "entries", Builtin.ArrayPrototypeEntries, 0, true);
                nativeContext.ArrayEntriesIterator = entries;

                JSFunction values = Bootstrapper.InstallFunctionWithBuiltinId(isolate, proto, "values", Builtin.ArrayPrototypeValues, 0, true);
                JSObject.AddProperty(isolate, proto, ReadOnlyRoots.iterator_symbol, values, PropertyAttributes.DONT_ENUM);
                nativeContext.ArrayValuesIterator = values;
            }

            JSFunction forEachFun = Bootstrapper.SimpleInstallFunction(isolate, proto, "forEach", Builtin.ArrayForEach, 1, false);
            nativeContext.ArrayForEachIterator = forEachFun;
            Bootstrapper.SimpleInstallFunction(isolate, proto, "filter", Builtin.ArrayFilter, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "flat", Builtin.ArrayPrototypeFlat, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "flatMap", Builtin.ArrayPrototypeFlatMap, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "map", Builtin.ArrayMap, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "every", Builtin.ArrayEvery, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "some", Builtin.ArraySome, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "reduce", Builtin.ArrayReduce, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "reduceRight", Builtin.ArrayReduceRight, 1, false);

            Bootstrapper.SimpleInstallFunction(isolate, proto, "toReversed", Builtin.ArrayPrototypeToReversed, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "toSorted", Builtin.ArrayPrototypeToSorted, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "toSpliced", Builtin.ArrayPrototypeToSpliced, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, proto, "with", Builtin.ArrayPrototypeWith, 2, true);

            Bootstrapper.SimpleInstallFunction(isolate, proto, "toLocaleString", Builtin.ArrayPrototypeToLocaleString, 0, false);
            JSFunction arrayPrototypeToStringFun = Bootstrapper.SimpleInstallFunction(isolate, proto, "toString", Builtin.ArrayPrototypeToString, 0, false);

            JSObject unscopables = _factory.NewJSObjectWithNullProto();
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "at");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "copyWithin");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "entries");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "fill");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "find");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "findIndex");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "findLast");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "findLastIndex");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "flat");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "flatMap");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "includes");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "keys");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "toReversed");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "toSorted");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "toSpliced");
            Bootstrapper.InstallTrueValuedProperty(isolate, unscopables, "values");

            JSObject.MigrateSlowToFast(isolate, unscopables, 0, "Bootstrapping");
            JSObject.AddProperty(isolate, proto, ReadOnlyRoots.unscopables_symbol, unscopables, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);

            Map map = proto.Map;
            Map.SetShouldBeFastPrototypeMap(map, true, isolate);

            Cell validityCell = Map.GetOrCreatePrototypeChainValidityCell(map, isolate)!;

            nativeContext.InitialArrayPrototypeValidityCell = validityCell;
        }
        // ---- bootstrapper.cc 2563-2593
        {  // --- A r r a y I t e r a t o r ---
            JSObject iteratorPrototype = nativeContext.InitialIteratorPrototype;

            JSObject arrayIteratorPrototype = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, arrayIteratorPrototype, iteratorPrototype);

            arrayIteratorPrototype.Map.InstanceType = InstanceType.JSArrayIteratorPrototypeType;

            Bootstrapper.InstallToStringTag(isolate, arrayIteratorPrototype, ReadOnlyRoots.ArrayIterator_string);

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, arrayIteratorPrototype, "next", Builtin.ArrayIteratorPrototypeNext, 0, true);

            JSFunction arrayIteratorFunction = Bootstrapper.CreateFunction(isolate, ReadOnlyRoots.ArrayIterator_string, InstanceType.JSArrayIteratorType, 0, 0, arrayIteratorPrototype, Builtin.Illegal, 0, false);
            arrayIteratorFunction.Shared.Native = false;

            nativeContext.InitialArrayIteratorMap = arrayIteratorFunction.InitialMap;
            nativeContext.InitialArrayIteratorPrototype = arrayIteratorPrototype;
        }
        // ---- bootstrapper.cc 2595-2678
        {  // --- N u m b e r ---
            JSFunction numberFun = Bootstrapper.InstallFunction(isolate, global, "Number", InstanceType.JSPrimitiveWrapperType, 0, 0, nativeContext.InitialObjectPrototype, Builtin.NumberConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, numberFun, Context.Field.NUMBER_FUNCTION_INDEX);

            // Create the %NumberPrototype%
            JSPrimitiveWrapper prototype = (JSPrimitiveWrapper)(_factory.NewJSObject(numberFun));
            prototype.Value = JSValue.Zero;
            JSFunction.SetPrototype(isolate, numberFun, prototype);

            // Install the "constructor" property on the {prototype}.
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.constructor_string, numberFun, PropertyAttributes.DONT_ENUM);

            // Install the Number.prototype methods.
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toExponential", Builtin.NumberPrototypeToExponential, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toFixed", Builtin.NumberPrototypeToFixed, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toPrecision", Builtin.NumberPrototypeToPrecision, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.NumberPrototypeToString, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.NumberPrototypeValueOf, 0, true);

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.NumberPrototypeToLocaleString, 0, false);

            // Install the Number functions.
            Bootstrapper.SimpleInstallFunction(isolate, numberFun, "isFinite", Builtin.NumberIsFinite, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, numberFun, "isInteger", Builtin.NumberIsInteger, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, numberFun, "isNaN", Builtin.NumberIsNaN, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, numberFun, "isSafeInteger", Builtin.NumberIsSafeInteger, 1, true);

            // Install Number.parseFloat and Global.parseFloat.
            JSFunction parseFloatFun = Bootstrapper.SimpleInstallFunction(isolate, numberFun, "parseFloat", Builtin.NumberParseFloat, 1, true);
            JSObject.AddProperty(isolate, globalObject, "parseFloat", parseFloatFun, PropertyAttributes.DONT_ENUM);
            nativeContext.GlobalParseFloatFun = parseFloatFun;

            // Install Number.parseInt and Global.parseInt.
            JSFunction parseIntFun = Bootstrapper.SimpleInstallFunction(isolate, numberFun, "parseInt", Builtin.NumberParseInt, 2, true);
            JSObject.AddProperty(isolate, globalObject, "parseInt", parseIntFun, PropertyAttributes.DONT_ENUM);
            nativeContext.GlobalParseIntFun = parseIntFun;

            // Install Number constants
            const double kMaxValue = 1.7976931348623157e+308;
            const double kMinValue = 5e-324;
            const double kEPS = 2.220446049250313e-16;

            Bootstrapper.InstallConstant(isolate, numberFun, "MAX_VALUE", JSValue.FromNumber(kMaxValue));
            Bootstrapper.InstallConstant(isolate, numberFun, "MIN_VALUE", JSValue.FromNumber(kMinValue));
            Bootstrapper.InstallConstant(isolate, numberFun, "NaN", JSValue.NaN);
            Bootstrapper.InstallConstant(isolate, numberFun, "NEGATIVE_INFINITY", JSValue.FromNumber(-double.PositiveInfinity));
            Bootstrapper.InstallConstant(isolate, numberFun, "POSITIVE_INFINITY", JSValue.FromNumber(double.PositiveInfinity));
            Bootstrapper.InstallConstant(isolate, numberFun, "MAX_SAFE_INTEGER", JSValue.FromNumber(EngineGlobals.kMaxSafeInteger));
            Bootstrapper.InstallConstant(isolate, numberFun, "MIN_SAFE_INTEGER", JSValue.FromNumber(EngineGlobals.kMinSafeInteger));
            Bootstrapper.InstallConstant(isolate, numberFun, "EPSILON", JSValue.FromNumber(kEPS));

            Bootstrapper.InstallConstant(isolate, global, "Infinity", JSValue.FromNumber(double.PositiveInfinity));
            Bootstrapper.InstallConstant(isolate, global, "NaN", JSValue.NaN);
            Bootstrapper.InstallConstant(isolate, global, "undefined", JSValue.Undefined);
        }
        // ---- bootstrapper.cc 2680-2703
        {  // --- B o o l e a n ---
            JSFunction booleanFun = Bootstrapper.InstallFunction(isolate, global, "Boolean", InstanceType.JSPrimitiveWrapperType, 0, 0, nativeContext.InitialObjectPrototype, Builtin.BooleanConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, booleanFun, Context.Field.BOOLEAN_FUNCTION_INDEX);

            // Create the %BooleanPrototype%
            JSPrimitiveWrapper prototype = (JSPrimitiveWrapper)(_factory.NewJSObject(booleanFun));
            prototype.Value = JSValue.False;
            JSFunction.SetPrototype(isolate, booleanFun, prototype);

            // Install the "constructor" property on the {prototype}.
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.constructor_string, booleanFun, PropertyAttributes.DONT_ENUM);

            // Install the Boolean.prototype methods.
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.BooleanPrototypeToString, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.BooleanPrototypeValueOf, 0, true);
        }
        // ---- bootstrapper.cc 2705-2884
        {  // --- S t r i n g ---
            JSFunction stringFun = Bootstrapper.InstallFunction(isolate, global, "String", InstanceType.JSPrimitiveWrapperType, 0, 0, nativeContext.InitialObjectPrototype, Builtin.StringConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, stringFun, Context.Field.STRING_FUNCTION_INDEX);

            Map stringMap = nativeContext.StringFunction.InitialMap;
            stringMap.SetElementsKind(ElementsKind.FAST_STRING_WRAPPER_ELEMENTS);
            Map.EnsureDescriptorSlack(isolate, stringMap, 1);

            PropertyAttributes attribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;

            {  // Add length.
                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.StringLengthAccessor, attribs);
                stringMap.AppendDescriptor(isolate, d);
            }

            // Install the String.fromCharCode function.
            Bootstrapper.SimpleInstallFunction(isolate, stringFun, "fromCharCode", Builtin.StringFromCharCode, 1, false);

            // Install the String.fromCodePoint function.
            Bootstrapper.SimpleInstallFunction(isolate, stringFun, "fromCodePoint", Builtin.StringFromCodePoint, 1, false);

            // Install the String.raw function.
            Bootstrapper.SimpleInstallFunction(isolate, stringFun, "raw", Builtin.StringRaw, 1, false);

            // Create the %StringPrototype%
            JSPrimitiveWrapper prototype = (JSPrimitiveWrapper)(_factory.NewJSObject(stringFun));
            prototype.Value = ReadOnlyRoots.empty_string;
            JSFunction.SetPrototype(isolate, stringFun, prototype);
            nativeContext.InitialStringPrototype = prototype;

            // Install the "constructor" property on the {prototype}.
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.constructor_string, stringFun, PropertyAttributes.DONT_ENUM);

            // Install the String.prototype methods.
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "anchor", Builtin.StringPrototypeAnchor, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "at", Builtin.StringPrototypeAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "big", Builtin.StringPrototypeBig, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "blink", Builtin.StringPrototypeBlink, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "bold", Builtin.StringPrototypeBold, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "charAt", Builtin.StringPrototypeCharAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "charCodeAt", Builtin.StringPrototypeCharCodeAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "codePointAt", Builtin.StringPrototypeCodePointAt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "concat", Builtin.StringPrototypeConcat, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "endsWith", Builtin.StringPrototypeEndsWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "fontcolor", Builtin.StringPrototypeFontcolor, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "fontsize", Builtin.StringPrototypeFontsize, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "fixed", Builtin.StringPrototypeFixed, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "includes", Builtin.StringPrototypeIncludes, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "indexOf", Builtin.StringPrototypeIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "isWellFormed", Builtin.StringPrototypeIsWellFormed, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "italics", Builtin.StringPrototypeItalics, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "lastIndexOf", Builtin.StringPrototypeLastIndexOf, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "link", Builtin.StringPrototypeLink, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "localeCompare", Builtin.StringPrototypeLocaleCompare, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "match", Builtin.StringPrototypeMatch, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "matchAll", Builtin.StringPrototypeMatchAll, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "normalize", Builtin.StringPrototypeNormalize, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "padEnd", Builtin.StringPrototypePadEnd, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "padStart", Builtin.StringPrototypePadStart, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "repeat", Builtin.StringPrototypeRepeat, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "replace", Builtin.StringPrototypeReplace, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "replaceAll", Builtin.StringPrototypeReplaceAll, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "search", Builtin.StringPrototypeSearch, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "slice", Builtin.StringPrototypeSlice, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "small", Builtin.StringPrototypeSmall, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "split", Builtin.StringPrototypeSplit, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "strike", Builtin.StringPrototypeStrike, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "sub", Builtin.StringPrototypeSub, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "substr", Builtin.StringPrototypeSubstr, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "substring", Builtin.StringPrototypeSubstring, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "sup", Builtin.StringPrototypeSup, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "startsWith", Builtin.StringPrototypeStartsWith, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.StringPrototypeToString, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toWellFormed", Builtin.StringPrototypeToWellFormed, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "trim", Builtin.StringPrototypeTrim, 0, false);

            // Install `String.prototype.trimStart` with `trimLeft` alias.
            JSFunction trimStartFun = Bootstrapper.SimpleInstallFunction(isolate, prototype, "trimStart", Builtin.StringPrototypeTrimStart, 0, false);
            JSObject.AddProperty(isolate, prototype, "trimLeft", trimStartFun, PropertyAttributes.DONT_ENUM);

            // Install `String.prototype.trimEnd` with `trimRight` alias.
            JSFunction trimEndFun = Bootstrapper.SimpleInstallFunction(isolate, prototype, "trimEnd", Builtin.StringPrototypeTrimEnd, 0, false);
            JSObject.AddProperty(isolate, prototype, "trimRight", trimEndFun, PropertyAttributes.DONT_ENUM);

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleLowerCase", Builtin.StringPrototypeToLocaleLowerCase, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleUpperCase", Builtin.StringPrototypeToLocaleUpperCase, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLowerCase", Builtin.StringPrototypeToLowerCase, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toUpperCase", Builtin.StringPrototypeToUpperCase, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.StringPrototypeValueOf, 0, true);

            Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.iterator_symbol, "[Symbol.iterator]", Builtin.StringPrototypeIterator, 0, true, PropertyAttributes.DONT_ENUM);
        }
        // ---- bootstrapper.cc 2886-2913
        {  // --- S t r i n g I t e r a t o r ---
            JSObject iteratorPrototype = nativeContext.InitialIteratorPrototype;

            JSObject stringIteratorPrototype = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, stringIteratorPrototype, iteratorPrototype);

            stringIteratorPrototype.Map.InstanceType = InstanceType.JSStringIteratorPrototypeType;
            Bootstrapper.InstallToStringTag(isolate, stringIteratorPrototype, "String Iterator");

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, stringIteratorPrototype, "next", Builtin.StringIteratorPrototypeNext, 0, true);

            JSFunction stringIteratorFunction = Bootstrapper.CreateFunction(isolate, _factory.InternalizeString("StringIterator"), InstanceType.JSStringIteratorType, 0, 0, stringIteratorPrototype, Builtin.Illegal, 0, false);
            stringIteratorFunction.Shared.Native = false;
            nativeContext.InitialStringIteratorMap = stringIteratorFunction.InitialMap;
            nativeContext.InitialStringIteratorPrototype = stringIteratorPrototype;
        }
        // ---- bootstrapper.cc 2915-2976
        {  // --- S y m b o l ---
            JSFunction symbolFun = Bootstrapper.InstallFunction(isolate, global, "Symbol", InstanceType.JSPrimitiveWrapperType, 0, 0, JSValue.TheHole, Builtin.SymbolConstructor, 0, false);
            nativeContext.SymbolFunction = symbolFun;

            // Install the Symbol.for and Symbol.keyFor functions.
            Bootstrapper.SimpleInstallFunction(isolate, symbolFun, "for", Builtin.SymbolFor, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, symbolFun, "keyFor", Builtin.SymbolKeyFor, 1, false);

            // Install well-known symbols.
            Bootstrapper.InstallConstant(isolate, symbolFun, "asyncIterator", ReadOnlyRoots.async_iterator_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "hasInstance", ReadOnlyRoots.has_instance_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "isConcatSpreadable", ReadOnlyRoots.is_concat_spreadable_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "iterator", ReadOnlyRoots.iterator_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "match", ReadOnlyRoots.match_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "matchAll", ReadOnlyRoots.match_all_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "replace", ReadOnlyRoots.replace_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "search", ReadOnlyRoots.search_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "species", ReadOnlyRoots.species_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "split", ReadOnlyRoots.split_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "toPrimitive", ReadOnlyRoots.to_primitive_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "toStringTag", ReadOnlyRoots.to_string_tag_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "unscopables", ReadOnlyRoots.unscopables_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "dispose", ReadOnlyRoots.dispose_symbol);
            Bootstrapper.InstallConstant(isolate, symbolFun, "asyncDispose", ReadOnlyRoots.async_dispose_symbol);

            // Setup %SymbolPrototype%.
            JSObject prototype = (JSObject)symbolFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, prototype, "Symbol");

            // Install the Symbol.prototype methods.
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "toString", Builtin.SymbolPrototypeToString, 0, true);
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "valueOf", Builtin.SymbolPrototypeValueOf, 0, true);

            // Install the Symbol.prototype.description getter.
            Bootstrapper.SimpleInstallGetter(isolate, prototype, _factory.InternalizeString("description"), Builtin.SymbolPrototypeDescriptionGetter, true);

            // Install the @@toPrimitive function.
            Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.to_primitive_symbol, "[Symbol.toPrimitive]", Builtin.SymbolPrototypeToPrimitive, 1, true, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        }
        // ---- bootstrapper.cc 2978-3112
        {  // --- D a t e ---
            JSFunction dateFun = Bootstrapper.InstallFunction(isolate, global, "Date", InstanceType.JSDateType, 0, 0, JSValue.TheHole, Builtin.DateConstructor, 7, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, dateFun, Context.Field.DATE_FUNCTION_INDEX);

            // Install the Date.now, Date.parse and Date.UTC functions.
            Bootstrapper.SimpleInstallFunction(isolate, dateFun, "now", Builtin.DateNow, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, dateFun, "parse", Builtin.DateParse, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, dateFun, "UTC", Builtin.DateUTC, 7, false);

            // Setup %DatePrototype%.
            JSObject prototype = (JSObject)dateFun.InstancePrototype;

            // Install the Date.prototype methods.
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.DatePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toDateString", Builtin.DatePrototypeToDateString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toTimeString", Builtin.DatePrototypeToTimeString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toISOString", Builtin.DatePrototypeToISOString, 0, false);
            JSFunction toUtcString = Bootstrapper.SimpleInstallFunction(isolate, prototype, "toUTCString", Builtin.DatePrototypeToUTCString, 0, false);
            JSObject.AddProperty(isolate, prototype, "toGMTString", toUtcString, PropertyAttributes.DONT_ENUM);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getDate", Builtin.DatePrototypeGetDate, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setDate", Builtin.DatePrototypeSetDate, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getDay", Builtin.DatePrototypeGetDay, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getFullYear", Builtin.DatePrototypeGetFullYear, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setFullYear", Builtin.DatePrototypeSetFullYear, 3, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getHours", Builtin.DatePrototypeGetHours, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setHours", Builtin.DatePrototypeSetHours, 4, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getMilliseconds", Builtin.DatePrototypeGetMilliseconds, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setMilliseconds", Builtin.DatePrototypeSetMilliseconds, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getMinutes", Builtin.DatePrototypeGetMinutes, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setMinutes", Builtin.DatePrototypeSetMinutes, 3, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getMonth", Builtin.DatePrototypeGetMonth, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setMonth", Builtin.DatePrototypeSetMonth, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getSeconds", Builtin.DatePrototypeGetSeconds, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setSeconds", Builtin.DatePrototypeSetSeconds, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getTime", Builtin.DatePrototypeGetTime, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setTime", Builtin.DatePrototypeSetTime, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getTimezoneOffset", Builtin.DatePrototypeGetTimezoneOffset, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCDate", Builtin.DatePrototypeGetUTCDate, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCDate", Builtin.DatePrototypeSetUTCDate, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCDay", Builtin.DatePrototypeGetUTCDay, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCFullYear", Builtin.DatePrototypeGetUTCFullYear, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCFullYear", Builtin.DatePrototypeSetUTCFullYear, 3, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCHours", Builtin.DatePrototypeGetUTCHours, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCHours", Builtin.DatePrototypeSetUTCHours, 4, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCMilliseconds", Builtin.DatePrototypeGetUTCMilliseconds, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCMilliseconds", Builtin.DatePrototypeSetUTCMilliseconds, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCMinutes", Builtin.DatePrototypeGetUTCMinutes, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCMinutes", Builtin.DatePrototypeSetUTCMinutes, 3, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCMonth", Builtin.DatePrototypeGetUTCMonth, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCMonth", Builtin.DatePrototypeSetUTCMonth, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getUTCSeconds", Builtin.DatePrototypeGetUTCSeconds, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setUTCSeconds", Builtin.DatePrototypeSetUTCSeconds, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.DatePrototypeValueOf, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getYear", Builtin.DatePrototypeGetYear, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "setYear", Builtin.DatePrototypeSetYear, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toJSON", Builtin.DatePrototypeToJson, 1, false);

            // Install Intl fallback functions.
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.DatePrototypeToString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleDateString", Builtin.DatePrototypeToDateString, 0, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleTimeString", Builtin.DatePrototypeToTimeString, 0, false);

            // Install the @@toPrimitive function.
            Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.to_primitive_symbol, "[Symbol.toPrimitive]", Builtin.DatePrototypeToPrimitive, 1, true, PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        }
        // ---- bootstrapper.cc 3114-3194
        {  // -- P r o m i s e
            JSFunction promiseFun = Bootstrapper.InstallFunction(isolate, global, "Promise", InstanceType.JSPromiseType, 0, 0, JSValue.TheHole, Builtin.PromiseConstructor, 1, true);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, promiseFun, Context.Field.PROMISE_FUNCTION_INDEX);

            Bootstrapper.InstallSpeciesGetter(isolate, promiseFun);

            JSFunction promiseAll = Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "all", Builtin.PromiseAll, 1, true);
            nativeContext.PromiseAll = promiseAll;

            JSFunction promiseAllSettled = Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "allSettled", Builtin.PromiseAllSettled, 1, true);
            nativeContext.PromiseAllSettled = promiseAllSettled;

            JSFunction promiseAny = Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "any", Builtin.PromiseAny, 1, true);
            nativeContext.PromiseAny = promiseAny;

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "race", Builtin.PromiseRace, 1, true);

            JSFunction promiseResolve = Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "resolve", Builtin.PromiseResolveTrampoline, 1, true);
            nativeContext.PromiseResolve = promiseResolve;

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "reject", Builtin.PromiseReject, 1, true);

            Map resultMap = Bootstrapper.CreateLiteralObjectMapFromCache(isolate, [ReadOnlyRoots.promise_string, ReadOnlyRoots.resolve_string, ReadOnlyRoots.reject_string]);
            nativeContext.PromiseWithresolversResultMap = resultMap;
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "withResolvers", Builtin.PromiseWithResolvers, 0, true);
            Bootstrapper.InstallFunctionWithBuiltinId(isolate, promiseFun, "try", Builtin.PromiseTry, 1, false);

            Bootstrapper.SetConstructorInstanceType(isolate, promiseFun, InstanceType.JSPromiseConstructorType);

            // Setup %PromisePrototype%.
            JSObject prototype = (JSObject)promiseFun.InstancePrototype;
            nativeContext.PromisePrototype = prototype;

            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.Promise_string);

            JSFunction promiseThen = Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "then", Builtin.PromisePrototypeThen, 2, true);
            nativeContext.PromiseThen = promiseThen;

            JSFunction performPromiseThen = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.empty_string, Builtin.PerformPromiseThenFunction, 3, true);
            nativeContext.PerformPromiseThen = performPromiseThen;

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "catch", Builtin.PromisePrototypeCatch, 1, true);

            Bootstrapper.InstallFunctionWithBuiltinId(isolate, prototype, "finally", Builtin.PromisePrototypeFinally, 1, true);

            Debug.Assert(promiseFun.HasFastProperties);

            Map prototypeMap = prototype.Map;
            Map.SetShouldBeFastPrototypeMap(prototypeMap, true, isolate);

            prototype.Map.InstanceType = InstanceType.JSPromisePrototypeType;

            Debug.Assert(promiseFun.HasFastProperties);
        }
        // ---- bootstrapper.cc 3196-3460
        {  // -- R e g E x p
            // Builtin functions for RegExp.prototype.
            JSFunction regexpFun = Bootstrapper.InstallFunction(isolate, global, "RegExp", InstanceType.JSRegExpType, 0, JSRegExp.kInObjectFieldCount, JSValue.TheHole, Builtin.RegExpConstructor, 2, true);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, regexpFun, Context.Field.REGEXP_FUNCTION_INDEX);

            {
                // Setup %RegExpPrototype%.
                JSObject prototype = (JSObject)regexpFun.InstancePrototype;
                nativeContext.RegExpPrototype = prototype;

                {
                    JSFunction fun = Bootstrapper.SimpleInstallFunction(isolate, prototype, "exec", Builtin.RegExpPrototypeExec, 1, true);
                    nativeContext.RegExpExecFunction = fun;

                }

                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.dotAll_string, Builtin.RegExpPrototypeDotAllGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.flags_string, Builtin.RegExpPrototypeFlagsGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.global_string, Builtin.RegExpPrototypeGlobalGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.hasIndices_string, Builtin.RegExpPrototypeHasIndicesGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.ignoreCase_string, Builtin.RegExpPrototypeIgnoreCaseGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.multiline_string, Builtin.RegExpPrototypeMultilineGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.source_string, Builtin.RegExpPrototypeSourceGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.sticky_string, Builtin.RegExpPrototypeStickyGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.unicode_string, Builtin.RegExpPrototypeUnicodeGetter, true);
                Bootstrapper.SimpleInstallGetter(isolate, prototype, ReadOnlyRoots.unicodeSets_string, Builtin.RegExpPrototypeUnicodeSetsGetter, true);

                Bootstrapper.SimpleInstallFunction(isolate, prototype, "compile", Builtin.RegExpPrototypeCompile, 2, true);
                Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.RegExpPrototypeToString, 0, false);
                Bootstrapper.SimpleInstallFunction(isolate, prototype, "test", Builtin.RegExpPrototypeTest, 1, true);

                {
                    JSFunction fun = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.match_symbol, "[Symbol.match]", Builtin.RegExpPrototypeMatch, 1, true);
                    nativeContext.RegExpMatchFunction = fun;

                }

                {
                    JSFunction fun = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.match_all_symbol, "[Symbol.matchAll]", Builtin.RegExpPrototypeMatchAll, 1, true);
                    nativeContext.RegExpMatchAllFunction = fun;

                }

                {
                    JSFunction fun = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.replace_symbol, "[Symbol.replace]", Builtin.RegExpPrototypeReplace, 2, false);
                    nativeContext.RegExpReplaceFunction = fun;

                }

                {
                    JSFunction fun = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.search_symbol, "[Symbol.search]", Builtin.RegExpPrototypeSearch, 1, true);
                    nativeContext.RegExpSearchFunction = fun;

                }

                {
                    JSFunction fun = Bootstrapper.InstallFunctionAtSymbol(isolate, prototype, ReadOnlyRoots.split_symbol, "[Symbol.split]", Builtin.RegExpPrototypeSplit, 2, false);
                    nativeContext.RegExpSplitFunction = fun;

                }

                Map prototypeMap = prototype.Map;
                Map.SetShouldBeFastPrototypeMap(prototypeMap, true, isolate);

                prototypeMap.InstanceType = InstanceType.JSRegExpPrototypeType;

                // Store the initial RegExp.prototype map. This is used in fast-path
                // checks. Do not alter the prototype after this point.
                nativeContext.RegExpPrototypeMap = prototypeMap;
            }

            {
                // RegExp getters and setters.

                Bootstrapper.InstallSpeciesGetter(isolate, regexpFun);

                // Static properties set by a successful match.

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, ReadOnlyRoots.input_string, Builtin.RegExpInputGetter, Builtin.RegExpInputSetter);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$_", Builtin.RegExpInputGetter, Builtin.RegExpInputSetter);

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "lastMatch", Builtin.RegExpLastMatchGetter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$&", Builtin.RegExpLastMatchGetter, Builtin.EmptyFunction1);

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "lastParen", Builtin.RegExpLastParenGetter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$+", Builtin.RegExpLastParenGetter, Builtin.EmptyFunction1);

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "leftContext", Builtin.RegExpLeftContextGetter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$`", Builtin.RegExpLeftContextGetter, Builtin.EmptyFunction1);

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "rightContext", Builtin.RegExpRightContextGetter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$'", Builtin.RegExpRightContextGetter, Builtin.EmptyFunction1);

                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$1", Builtin.RegExpCapture1Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$2", Builtin.RegExpCapture2Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$3", Builtin.RegExpCapture3Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$4", Builtin.RegExpCapture4Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$5", Builtin.RegExpCapture5Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$6", Builtin.RegExpCapture6Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$7", Builtin.RegExpCapture7Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$8", Builtin.RegExpCapture8Getter, Builtin.EmptyFunction1);
                Bootstrapper.SimpleInstallGetterSetter(isolate, regexpFun, "$9", Builtin.RegExpCapture9Getter, Builtin.EmptyFunction1);

                Bootstrapper.SimpleInstallFunction(isolate, regexpFun, "escape", Builtin.RegExpEscape, 1, true);
            }
            Bootstrapper.SetConstructorInstanceType(isolate, regexpFun, InstanceType.JSRegExpConstructorType);

            Debug.Assert(regexpFun.HasInitialMap);
            Map initialMap = regexpFun.InitialMap;



            Map.EnsureDescriptorSlack(isolate, initialMap, 1);

            // ECMA-262, section 15.10.7.5.
            PropertyAttributes writable = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE;
            Descriptor d = Descriptor.DataField(ReadOnlyRoots.lastIndex_string, 0, writable, Representation.Tagged);
            initialMap.AppendDescriptor(isolate, d);

            // Create the last match info.
            // TODO(v8sharp): regexp_last_match_info (RegExpMatchInfo::New) is set up
            // by the RegExp builtins port, which owns RegExpMatchInfo.

            Debug.Assert(regexpFun.HasFastProperties);
        }

        {  // --- R e g E x p S t r i n g  I t e r a t o r ---
            JSObject iteratorPrototype = nativeContext.InitialIteratorPrototype;

            JSObject regexpStringIteratorPrototype = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, regexpStringIteratorPrototype, iteratorPrototype);

            Bootstrapper.InstallToStringTag(isolate, regexpStringIteratorPrototype, "RegExp String Iterator");

            Bootstrapper.SimpleInstallFunction(isolate, regexpStringIteratorPrototype, "next", Builtin.RegExpStringIteratorPrototypeNext, 0, true);

            JSFunction regexpStringIteratorFunction = Bootstrapper.CreateFunction(isolate, "RegExpStringIterator", InstanceType.JSRegExpStringIteratorType, 0, 0, regexpStringIteratorPrototype, Builtin.Illegal, 0, false);
            regexpStringIteratorFunction.Shared.Native = false;
            nativeContext.InitialRegExpStringIteratorPrototypeMap = regexpStringIteratorFunction.InitialMap;
        }

        // -- E r r o r
        InstallError(global, ReadOnlyRoots.Error_string, Context.Field.ERROR_FUNCTION_INDEX);

        // -- A g g r e g a t e E r r o r
        InstallError(global, ReadOnlyRoots.AggregateError_string, Context.Field.AGGREGATE_ERROR_FUNCTION_INDEX, Builtin.AggregateErrorConstructor, 2);

        // -- E v a l E r r o r
        InstallError(global, ReadOnlyRoots.EvalError_string, Context.Field.EVAL_ERROR_FUNCTION_INDEX);

        // -- R a n g e E r r o r
        InstallError(global, ReadOnlyRoots.RangeError_string, Context.Field.RANGE_ERROR_FUNCTION_INDEX);

        // -- R e f e r e n c e E r r o r
        InstallError(global, ReadOnlyRoots.ReferenceError_string, Context.Field.REFERENCE_ERROR_FUNCTION_INDEX);

        // -- S y n t a x E r r o r
        InstallError(global, ReadOnlyRoots.SyntaxError_string, Context.Field.SYNTAX_ERROR_FUNCTION_INDEX);

        // -- T y p e E r r o r
        InstallError(global, ReadOnlyRoots.TypeError_string, Context.Field.TYPE_ERROR_FUNCTION_INDEX);

        // -- U R I E r r o r
        InstallError(global, ReadOnlyRoots.URIError_string, Context.Field.URI_ERROR_FUNCTION_INDEX);

        // -- S u p p r e s s e d E r r o r
        InstallError(global, ReadOnlyRoots.SuppressedError_string, Context.Field.SUPPRESSED_ERROR_FUNCTION_INDEX, Builtin.SuppressedErrorConstructor, 3);

        // ---- bootstrapper.cc 3462-3591
        {  // -- g l o b a l T h i s
            JSGlobalProxy globalProxy = nativeContext.GlobalProxyObject;
            JSObject.AddProperty(isolate, global, ReadOnlyRoots.globalThis_string, globalProxy, PropertyAttributes.DONT_ENUM);
        }

        {  // -- J S O N
            Map rawJsonMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSRawJsonType, JSObject.kHeaderSize + Map.kTaggedSize, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 1);
            Map.EnsureDescriptorSlack(isolate, rawJsonMap, 1);
            {
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.raw_json_string, 0, PropertyAttributes.NONE, Representation.Tagged);
                rawJsonMap.AppendDescriptor(isolate, d);
            }
            Map.SetPrototype(isolate, rawJsonMap, null);
            rawJsonMap.SetConstructor(nativeContext.ObjectFunction);
            nativeContext.JSRawJsonMap = rawJsonMap;

            JSObject jsonObject = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, global, "JSON", jsonObject, PropertyAttributes.DONT_ENUM);
            Bootstrapper.SimpleInstallFunction(isolate, jsonObject, "parse", Builtin.JsonParse, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, jsonObject, "stringify", Builtin.JsonStringify, 3, true);
            Bootstrapper.SimpleInstallFunction(isolate, jsonObject, "rawJSON", Builtin.JsonRawJson, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, jsonObject, "isRawJSON", Builtin.JsonIsRawJson, 1, true);
            Bootstrapper.InstallToStringTag(isolate, jsonObject, "JSON");
            nativeContext.JsonObject = jsonObject;
        }

        {  // -- M a t h
            JSObject math = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, global, "Math", math, PropertyAttributes.DONT_ENUM);
            Bootstrapper.SimpleInstallFunction(isolate, math, "abs", Builtin.MathAbs, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "acos", Builtin.MathAcos, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "acosh", Builtin.MathAcosh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "asin", Builtin.MathAsin, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "asinh", Builtin.MathAsinh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "atan", Builtin.MathAtan, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "atanh", Builtin.MathAtanh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "atan2", Builtin.MathAtan2, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "ceil", Builtin.MathCeil, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "cbrt", Builtin.MathCbrt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "expm1", Builtin.MathExpm1, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "clz32", Builtin.MathClz32, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "cos", Builtin.MathCos, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "cosh", Builtin.MathCosh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "exp", Builtin.MathExp, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "floor", Builtin.MathFloor, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "f16round", Builtin.MathF16round, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "fround", Builtin.MathFround, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "hypot", Builtin.MathHypot, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, math, "imul", Builtin.MathImul, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "log", Builtin.MathLog, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "log1p", Builtin.MathLog1p, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "log2", Builtin.MathLog2, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "log10", Builtin.MathLog10, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "max", Builtin.MathMax, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, math, "min", Builtin.MathMin, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, math, "pow", Builtin.MathPow, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "random", Builtin.MathRandom, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "round", Builtin.MathRound, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "sign", Builtin.MathSign, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "sin", Builtin.MathSin, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "sinh", Builtin.MathSinh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "sqrt", Builtin.MathSqrt, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "sumPrecise", Builtin.MathSumPrecise, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "tan", Builtin.MathTan, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "tanh", Builtin.MathTanh, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, math, "trunc", Builtin.MathTrunc, 1, true);

            // Install math constants.
            const double kE = 2.718281828459045;
            const double kPI = 3.1415926535897932;
            Bootstrapper.InstallConstant(isolate, math, "E", JSValue.FromNumber(kE));
            Bootstrapper.InstallConstant(isolate, math, "LN10", JSValue.FromNumber(2.302585092994046));
            Bootstrapper.InstallConstant(isolate, math, "LN2", JSValue.FromNumber(0.6931471805599453));
            Bootstrapper.InstallConstant(isolate, math, "LOG10E", JSValue.FromNumber(0.4342944819032518));
            Bootstrapper.InstallConstant(isolate, math, "LOG2E", JSValue.FromNumber(1.4426950408889634));
            Bootstrapper.InstallConstant(isolate, math, "PI", JSValue.FromNumber(kPI));
            Bootstrapper.InstallConstant(isolate, math, "SQRT1_2", JSValue.FromNumber(Math.Sqrt(0.5)));
            Bootstrapper.InstallConstant(isolate, math, "SQRT2", JSValue.FromNumber(Math.Sqrt(2.0)));
            Bootstrapper.InstallToStringTag(isolate, math, "Math");
        }

        // ---- bootstrapper.cc 4453-4626
        {  // -- M a p
            JSFunction jsMapFun = Bootstrapper.InstallFunction(isolate, global, "Map", InstanceType.JSMapType, 0, 0, JSValue.TheHole, Builtin.MapConstructor, 0, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, jsMapFun, Context.Field.JS_MAP_FUN_INDEX);

            Bootstrapper.SimpleInstallFunction(isolate, jsMapFun, "groupBy", Builtin.MapGroupBy, 2, true);

            // Setup %MapPrototype%.
            JSObject prototype = (JSObject)jsMapFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.Map_string);

            JSFunction mapGet = Bootstrapper.SimpleInstallFunction(isolate, prototype, "get", Builtin.MapPrototypeGet, 1, true);
            nativeContext.MapGet = mapGet;

            JSFunction mapSet = Bootstrapper.SimpleInstallFunction(isolate, prototype, "set", Builtin.MapPrototypeSet, 2, true);
            // Check that index of "set" function in JSCollection is correct.

            nativeContext.MapSet = mapSet;

            JSFunction mapHas = Bootstrapper.SimpleInstallFunction(isolate, prototype, "has", Builtin.MapPrototypeHas, 1, true);
            nativeContext.MapHas = mapHas;

            JSFunction mapDelete = Bootstrapper.SimpleInstallFunction(isolate, prototype, "delete", Builtin.MapPrototypeDelete, 1, true);
            nativeContext.MapDelete = mapDelete;

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "clear", Builtin.MapPrototypeClear, 0, true);
            JSFunction entries = Bootstrapper.SimpleInstallFunction(isolate, prototype, "entries", Builtin.MapPrototypeEntries, 0, true);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.iterator_symbol, entries, PropertyAttributes.DONT_ENUM);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "forEach", Builtin.MapPrototypeForEach, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "keys", Builtin.MapPrototypeKeys, 0, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, _factory.InternalizeString("size"), Builtin.MapPrototypeGetSize, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "values", Builtin.MapPrototypeValues, 0, true);

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getOrInsert", Builtin.MapPrototypeGetOrInsert, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getOrInsertComputed", Builtin.MapPrototypeGetOrInsertComputed, 2, true);
            nativeContext.InitialMapPrototypeMap = prototype.Map;

            Bootstrapper.InstallSpeciesGetter(isolate, jsMapFun);

            Debug.Assert(jsMapFun.HasFastProperties);

            nativeContext.JSMapMap = jsMapFun.InitialMap;
        }

        {  // -- B i g I n t
            JSFunction bigintFun = Bootstrapper.InstallFunction(isolate, global, "BigInt", InstanceType.JSPrimitiveWrapperType, 0, 0, JSValue.TheHole, Builtin.BigIntConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, bigintFun, Context.Field.BIGINT_FUNCTION_INDEX);

            // Install the properties of the BigInt constructor.
            // asUintN(bits, bigint)
            Bootstrapper.SimpleInstallFunction(isolate, bigintFun, "asUintN", Builtin.BigIntAsUintN, 2, false);
            // asIntN(bits, bigint)
            Bootstrapper.SimpleInstallFunction(isolate, bigintFun, "asIntN", Builtin.BigIntAsIntN, 2, false);

            // Set up the %BigIntPrototype%.
            JSObject prototype = (JSObject)bigintFun.InstancePrototype;
            JSFunction.SetPrototype(isolate, bigintFun, prototype);

            // Install the properties of the BigInt.prototype.
            // "constructor" is created implicitly by Bootstrapper.InstallFunction() above.
            // toLocaleString([reserved1 [, reserved2]])
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toLocaleString", Builtin.BigIntPrototypeToLocaleString, 0, false);
            // toString([radix])
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "toString", Builtin.BigIntPrototypeToString, 0, false);
            // valueOf()
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "valueOf", Builtin.BigIntPrototypeValueOf, 0, false);
            // @@toStringTag
            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.BigInt_string);
        }

        {  // -- S e t
            JSFunction jsSetFun = Bootstrapper.InstallFunction(isolate, global, "Set", InstanceType.JSSetType, 0, 0, JSValue.TheHole, Builtin.SetConstructor, 0, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, jsSetFun, Context.Field.JS_SET_FUN_INDEX);

            // Setup %SetPrototype%.
            JSObject prototype = (JSObject)jsSetFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.Set_string);

            JSFunction setHas = Bootstrapper.SimpleInstallFunction(isolate, prototype, "has", Builtin.SetPrototypeHas, 1, true);
            nativeContext.SetHas = setHas;

            JSFunction setAdd = Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.SetPrototypeAdd, 1, true);
            // Check that index of "add" function in JSCollection is correct.

            nativeContext.SetAdd = setAdd;

            JSFunction setDelete = Bootstrapper.SimpleInstallFunction(isolate, prototype, "delete", Builtin.SetPrototypeDelete, 1, true);
            nativeContext.SetDelete = setDelete;

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "difference", Builtin.SetPrototypeDifference, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "clear", Builtin.SetPrototypeClear, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "entries", Builtin.SetPrototypeEntries, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "forEach", Builtin.SetPrototypeForEach, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "intersection", Builtin.SetPrototypeIntersection, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "isSubsetOf", Builtin.SetPrototypeIsSubsetOf, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "isSupersetOf", Builtin.SetPrototypeIsSupersetOf, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "isDisjointFrom", Builtin.SetPrototypeIsDisjointFrom, 1, true);
            Bootstrapper.SimpleInstallGetter(isolate, prototype, _factory.InternalizeString("size"), Builtin.SetPrototypeGetSize, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "symmetricDifference", Builtin.SetPrototypeSymmetricDifference, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "union", Builtin.SetPrototypeUnion, 1, true);

            JSFunction values = Bootstrapper.SimpleInstallFunction(isolate, prototype, "values", Builtin.SetPrototypeValues, 0, true);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.keys_string, values, PropertyAttributes.DONT_ENUM);
            JSObject.AddProperty(isolate, prototype, ReadOnlyRoots.iterator_symbol, values, PropertyAttributes.DONT_ENUM);

            nativeContext.InitialSetPrototypeMap = prototype.Map;
            nativeContext.InitialSetPrototype = prototype;

            Bootstrapper.InstallSpeciesGetter(isolate, jsSetFun);

            Debug.Assert(jsSetFun.HasFastProperties);

            nativeContext.JSSetMap = jsSetFun.InitialMap;

            prototype.Map.InstanceType = InstanceType.JSSetPrototypeType;
        }

        // ---- bootstrapper.cc 4668-4867
        {  // -- I t e r a t o r and helpers
            JSObject iteratorPrototype = nativeContext.InitialIteratorPrototype;
            JSFunction iteratorFunction = Bootstrapper.InstallFunction(isolate, global, "Iterator", InstanceType.JSObjectType, JSObject.kHeaderSize, 0, iteratorPrototype, Builtin.IteratorConstructor, 0, true);

            Bootstrapper.SimpleInstallFunction(isolate, iteratorFunction, "from", Builtin.IteratorFrom, 1, true);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, iteratorFunction, Context.Field.ITERATOR_FUNCTION_INDEX);

            // --- %WrapForValidIteratorPrototype%
            JSObject wrapForValidIteratorPrototype = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, wrapForValidIteratorPrototype, iteratorPrototype);
            JSObject.AddProperty(isolate, iteratorPrototype, ReadOnlyRoots.constructor_string, iteratorFunction, PropertyAttributes.DONT_ENUM);
            Bootstrapper.SimpleInstallFunction(isolate, wrapForValidIteratorPrototype, "next", Builtin.WrapForValidIteratorPrototypeNext, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, wrapForValidIteratorPrototype, "return", Builtin.WrapForValidIteratorPrototypeReturn, 0, true);
            Map validIteratorWrapperMap = _factory.NewContextfulMapForCurrentContext(InstanceType.JSValidIteratorWrapperType, 0, ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
            Map.SetPrototype(isolate, validIteratorWrapperMap, wrapForValidIteratorPrototype);
            validIteratorWrapperMap.SetConstructor(iteratorFunction);
            nativeContext.ValidIteratorWrapperMap = validIteratorWrapperMap;

            // --- %IteratorHelperPrototype%
            JSObject iteratorHelperPrototype = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.ForceSetPrototype(isolate, iteratorHelperPrototype, iteratorPrototype);
            Bootstrapper.InstallToStringTag(isolate, iteratorHelperPrototype, "Iterator Helper");
            Bootstrapper.SimpleInstallFunction(isolate, iteratorHelperPrototype, "next", Builtin.IteratorHelperPrototypeNext, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorHelperPrototype, "return", Builtin.IteratorHelperPrototypeReturn, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "reduce", Builtin.IteratorPrototypeReduce, 1, false);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "toArray", Builtin.IteratorPrototypeToArray, 0, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "forEach", Builtin.IteratorPrototypeForEach, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "some", Builtin.IteratorPrototypeSome, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "every", Builtin.IteratorPrototypeEvery, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, iteratorPrototype, "find", Builtin.IteratorPrototypeFind, 1, true);
            Bootstrapper.SimpleInstallGetterSetter(isolate, iteratorPrototype, ReadOnlyRoots.to_string_tag_symbol, Builtin.IteratorPrototypeGetToStringTag, Builtin.IteratorPrototypeSetToStringTag);

            Bootstrapper.SimpleInstallGetterSetter(isolate, iteratorPrototype, ReadOnlyRoots.constructor_string, Builtin.IteratorPrototypeGetConstructor, Builtin.IteratorPrototypeSetConstructor);

            // --- Helper maps
            InstallIteratorHelper(iteratorHelperPrototype, iteratorFunction, iteratorPrototype, "map",
                InstanceType.JSIteratorMapHelperType, Builtin.IteratorPrototypeMap, 1, true, Context.Field.ITERATOR_MAP_HELPER_MAP_INDEX);
            InstallIteratorHelper(iteratorHelperPrototype, iteratorFunction, iteratorPrototype, "filter",
                InstanceType.JSIteratorFilterHelperType, Builtin.IteratorPrototypeFilter, 1, true, Context.Field.ITERATOR_FILTER_HELPER_MAP_INDEX);
            InstallIteratorHelper(iteratorHelperPrototype, iteratorFunction, iteratorPrototype, "take",
                InstanceType.JSIteratorTakeHelperType, Builtin.IteratorPrototypeTake, 1, true, Context.Field.ITERATOR_TAKE_HELPER_MAP_INDEX);
            InstallIteratorHelper(iteratorHelperPrototype, iteratorFunction, iteratorPrototype, "drop",
                InstanceType.JSIteratorDropHelperType, Builtin.IteratorPrototypeDrop, 1, true, Context.Field.ITERATOR_DROP_HELPER_MAP_INDEX);
            InstallIteratorHelper(iteratorHelperPrototype, iteratorFunction, iteratorPrototype, "flatMap",
                InstanceType.JSIteratorFlatMapHelperType, Builtin.IteratorPrototypeFlatMap, 1, true, Context.Field.ITERATOR_FLAT_MAP_HELPER_MAP_INDEX);

            nativeContext.InitialIteratorHelperPrototype = iteratorHelperPrototype;
            nativeContext.InitialIteratorFunction = iteratorFunction;
        }

        {  // -- I t e r a t o r R e s u l t
            Map map = Bootstrapper.CreateLiteralObjectMapFromCache(isolate, [ReadOnlyRoots.value_string, ReadOnlyRoots.done_string]);
            nativeContext.IteratorResultMap = map;
        }

        {  // -- W e a k M a p
            JSFunction cons = Bootstrapper.InstallFunction(isolate, global, "WeakMap", InstanceType.JSWeakMapType, 0, 0, JSValue.TheHole, Builtin.WeakMapConstructor, 0, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, cons, Context.Field.JS_WEAK_MAP_FUN_INDEX);

            // Setup %WeakMapPrototype%.
            var prototype = (JSObject)cons.InstancePrototype;

            JSFunction weakmapDelete = Bootstrapper.SimpleInstallFunction(isolate, prototype, "delete", Builtin.WeakMapPrototypeDelete, 1, true);
            nativeContext.WeakMapDelete = weakmapDelete;

            JSFunction weakmapGet = Bootstrapper.SimpleInstallFunction(isolate, prototype, "get", Builtin.WeakMapPrototypeGet, 1, true);
            nativeContext.WeakMapGet = weakmapGet;

            JSFunction weakmapSet = Bootstrapper.SimpleInstallFunction(isolate, prototype, "set", Builtin.WeakMapPrototypeSet, 2, true);
            nativeContext.WeakMapSet = weakmapSet;

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "has", Builtin.WeakMapPrototypeHas, 1, true);

            Bootstrapper.InstallToStringTag(isolate, prototype, "WeakMap");

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getOrInsert", Builtin.WeakMapPrototypeGetOrInsert, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "getOrInsertComputed", Builtin.WeakMapPrototypeGetOrInsertComputed, 2, true);
            nativeContext.InitialWeakMapPrototypeMap = prototype.Map;
        }

        {  // -- W e a k S e t
            JSFunction cons = Bootstrapper.InstallFunction(isolate, global, "WeakSet", InstanceType.JSWeakSetType, 0, 0, JSValue.TheHole, Builtin.WeakSetConstructor, 0, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, cons, Context.Field.JS_WEAK_SET_FUN_INDEX);

            // Setup %WeakSetPrototype%.
            var prototype = (JSObject)cons.InstancePrototype;

            Bootstrapper.SimpleInstallFunction(isolate, prototype, "delete", Builtin.WeakSetPrototypeDelete, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, prototype, "has", Builtin.WeakSetPrototypeHas, 1, true);

            JSFunction weaksetAdd = Bootstrapper.SimpleInstallFunction(isolate, prototype, "add", Builtin.WeakSetPrototypeAdd, 1, true);
            nativeContext.WeakSetAdd = weaksetAdd;

            Bootstrapper.InstallToStringTag(isolate, prototype, "WeakSet");

            nativeContext.InitialWeakSetPrototypeMap = prototype.Map;
        }
        // ---- bootstrapper.cc 4868-5133
        {  // -- P r o x y
            CreateJSProxyMaps();
            // Proxy function map has prototype slot for storing initial map but does
            // not have a prototype property.
            Map proxyFunctionMap = Map.Copy(isolate, nativeContext.StrictFunctionWithoutPrototypeMap, "Proxy");
            proxyFunctionMap.IsConstructor = true;

            JSString name = ReadOnlyRoots.Proxy_string;
            JSFunction proxyFunction = Bootstrapper.CreateFunctionForBuiltin(isolate, name, proxyFunctionMap, Builtin.ProxyConstructor, 2, true);

            nativeContext.ProxyMap.SetConstructor(proxyFunction);

            nativeContext.ProxyFunction = proxyFunction;
            JSObject.AddProperty(isolate, global, name, proxyFunction, PropertyAttributes.DONT_ENUM);

            Debug.Assert(!proxyFunction.HasPrototypeProperty);

            Bootstrapper.SimpleInstallFunction(isolate, proxyFunction, "revocable", Builtin.ProxyRevocable, 2, true);
        }

        {  // -- R e f l e c t
            JSString reflectString = _factory.InternalizeString("Reflect");
            JSObject reflect = _factory.NewJSObject(nativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, global, reflectString, reflect, PropertyAttributes.DONT_ENUM);
            Bootstrapper.InstallToStringTag(isolate, reflect, reflectString);

            Bootstrapper.SimpleInstallFunction(isolate, reflect, "defineProperty", Builtin.ReflectDefineProperty, 3, true);

            Bootstrapper.SimpleInstallFunction(isolate, reflect, "deleteProperty", Builtin.ReflectDeleteProperty, 2, true);

            JSFunction apply = Bootstrapper.SimpleInstallFunction(isolate, reflect, "apply", Builtin.ReflectApply, 3, false);
            nativeContext.ReflectApply = apply;

            JSFunction construct = Bootstrapper.SimpleInstallFunction(isolate, reflect, "construct", Builtin.ReflectConstruct, 2, false);
            nativeContext.ReflectConstruct = construct;

            Bootstrapper.SimpleInstallFunction(isolate, reflect, "get", Builtin.ReflectGet, 2, false);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "getOwnPropertyDescriptor", Builtin.ReflectGetOwnPropertyDescriptor, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "getPrototypeOf", Builtin.ReflectGetPrototypeOf, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "has", Builtin.ReflectHas, 2, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "isExtensible", Builtin.ReflectIsExtensible, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "ownKeys", Builtin.ReflectOwnKeys, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "preventExtensions", Builtin.ReflectPreventExtensions, 1, true);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "set", Builtin.ReflectSet, 3, false);
            Bootstrapper.SimpleInstallFunction(isolate, reflect, "setPrototypeOf", Builtin.ReflectSetPrototypeOf, 2, true);
        }

        {  // --- B o u n d F u n c t i o n
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSBoundFunctionType, JSObject.GetHeaderSize(InstanceType.JSBoundFunctionType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
            map.SetConstructor(nativeContext.ObjectFunction);
            map.IsCallable = true;
            Map.SetPrototype(isolate, map, emptyFunction);

            PropertyAttributes rocAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;
            Map.EnsureDescriptorSlack(isolate, map, 2);

            {  // length

                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.BoundFunctionLengthAccessor, rocAttribs);
                map.AppendDescriptor(isolate, d);
            }

            {  // name

                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.name_string, Accessors.BoundFunctionNameAccessor, rocAttribs);
                map.AppendDescriptor(isolate, d);
            }
            nativeContext.BoundFunctionWithoutConstructorMap = map;

            map = Map.Copy(isolate, map, "IsConstructor");
            map.IsConstructor = true;
            nativeContext.BoundFunctionWithConstructorMap = map;
        }

        {  // -- F i n a l i z a t i o n R e g i s t r y
            JSFunction finalizationRegistryFun = Bootstrapper.InstallFunction(isolate, global, ReadOnlyRoots.FinalizationRegistry_string, InstanceType.JSFinalizationRegistryType, 0, 0, JSValue.TheHole, Builtin.FinalizationRegistryConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, finalizationRegistryFun, Context.Field.JS_FINALIZATION_REGISTRY_FUNCTION_INDEX);

            JSObject finalizationRegistryPrototype = (JSObject)finalizationRegistryFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, finalizationRegistryPrototype, ReadOnlyRoots.FinalizationRegistry_string);

            Bootstrapper.SimpleInstallFunction(isolate, finalizationRegistryPrototype, "register", Builtin.FinalizationRegistryRegister, 2, false);

            Bootstrapper.SimpleInstallFunction(isolate, finalizationRegistryPrototype, "unregister", Builtin.FinalizationRegistryUnregister, 1, false);
        }

        {  // -- W e a k R e f
            JSFunction weakRefFun = Bootstrapper.InstallFunction(isolate, global, "WeakRef", InstanceType.JSWeakRefType, 0, 0, JSValue.TheHole, Builtin.WeakRefConstructor, 1, false);
            Bootstrapper.InstallWithIntrinsicDefaultProto(isolate, weakRefFun, Context.Field.JS_WEAK_REF_FUNCTION_INDEX);

            JSObject weakRefPrototype = (JSObject)weakRefFun.InstancePrototype;

            Bootstrapper.InstallToStringTag(isolate, weakRefPrototype, ReadOnlyRoots.WeakRef_string);

            Bootstrapper.SimpleInstallFunction(isolate, weakRefPrototype, "deref", Builtin.WeakRefDeref, 0, true);
        }

        {  // --- sloppy arguments map
            JSString argumentsString = ReadOnlyRoots.Arguments_string;
            JSFunction function = Bootstrapper.CreateFunctionForBuiltinWithPrototype(isolate, argumentsString, Builtin.Illegal, nativeContext.InitialObjectPrototype, InstanceType.JSArgumentsObjectType, 0, 2, MutableMode.MUTABLE, 0, false);
            Map map = function.InitialMap;

            // Create the descriptor array for the arguments object.
            Map.EnsureDescriptorSlack(isolate, map, 2);

            {  // length
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.length_string, 0, PropertyAttributes.DONT_ENUM, Representation.Tagged);
                map.AppendDescriptor(isolate, d);
            }
            {  // callee
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.callee_string, 1, PropertyAttributes.DONT_ENUM, Representation.Tagged);
                map.AppendDescriptor(isolate, d);
            }
            // @@iterator method is added later.

            nativeContext.SloppyArgumentsMap = map;

            Debug.Assert(!map.IsDictionaryMap);
            Debug.Assert(ElementsKinds.IsObjectElementsKind(map.ElementsKind));
        }

        {  // --- fast and slow aliased arguments map
            Map map = nativeContext.SloppyArgumentsMap;
            map = Map.Copy(isolate, map, "FastAliasedArguments");
            map.SetElementsKind(ElementsKind.FAST_SLOPPY_ARGUMENTS_ELEMENTS);

            nativeContext.FastAliasedArgumentsMap = map;

            map = Map.Copy(isolate, map, "SlowAliasedArguments");
            map.SetElementsKind(ElementsKind.SLOW_SLOPPY_ARGUMENTS_ELEMENTS);

            nativeContext.SlowAliasedArgumentsMap = map;
        }

        {  // --- strict mode arguments map
            const PropertyAttributes attributes = PropertyAttributes.DONT_ENUM | PropertyAttributes.DONT_DELETE | PropertyAttributes.READ_ONLY;

            // Create the ThrowTypeError function.
            AccessorPair callee = _factory.NewAccessorPair();

            JSFunction poison = GetThrowTypeErrorIntrinsic();

            // Install the ThrowTypeError function.
            callee.Getter = poison;
            callee.Setter = poison;

            // Create the map. Allocate one in-object field for length.
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSArgumentsObjectType, JSObject.kHeaderSize + Map.kTaggedSize, ElementsKind.PACKED_ELEMENTS, 1);
            // Create the descriptor array for the arguments object.
            Map.EnsureDescriptorSlack(isolate, map, 2);

            {  // length
                Descriptor d = Descriptor.DataField(ReadOnlyRoots.length_string, 0, PropertyAttributes.DONT_ENUM, Representation.Tagged);
                map.AppendDescriptor(isolate, d);
            }
            {  // callee
                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.callee_string, callee, attributes);
                map.AppendDescriptor(isolate, d);
            }
            // @@iterator method is added later.


            Map.SetPrototype(isolate, map, nativeContext.InitialObjectPrototype);

            // Copy constructor from the sloppy arguments boilerplate.
            map.SetConstructor(nativeContext.SloppyArgumentsMap.GetConstructor());

            nativeContext.StrictArgumentsMap = map;

            Debug.Assert(!map.IsDictionaryMap);
            Debug.Assert(ElementsKinds.IsObjectElementsKind(map.ElementsKind));
        }

        {  // --- context extension
            // Create a function for the context extension objects.
            JSFunction contextExtensionFun = Bootstrapper.CreateFunction(isolate, ReadOnlyRoots.empty_string, InstanceType.JSContextExtensionObjectType, JSObject.kHeaderSize, 0, JSValue.TheHole, Builtin.Illegal, 0, false);
            nativeContext.ContextExtensionFunction = contextExtensionFun;
        }

        {
            // Set up the call-as-function delegate.
            JSFunction @delegate = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.empty_string, Builtin.HandleApiCallAsFunctionDelegate, 0, false);
            nativeContext.CallAsFunctionDelegate = @delegate;
        }

        {
            // Set up the call-as-constructor delegate.
            JSFunction @delegate = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.empty_string, Builtin.HandleApiCallAsConstructorDelegate, 0, false);
            nativeContext.CallAsConstructorDelegate = @delegate;
        }

        // -- D i s p o s a b l e S t a c k (Genesis.DisposableStack.cs)
        InstallDisposableStack(global);

        // ---- bootstrapper.cc 5824-5871
        {  // --- W r a p p e d F u n c t i o n
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSWrappedFunctionType, JSObject.GetHeaderSize(InstanceType.JSWrappedFunctionType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
            map.SetConstructor(nativeContext.ObjectFunction);
            map.IsCallable = true;
            JSObject functionPrototype = nativeContext.FunctionPrototype;
            Map.SetPrototype(isolate, map, functionPrototype);

            PropertyAttributes rocAttribs = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;
            Map.EnsureDescriptorSlack(isolate, map, 2);
            {  // length

                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.length_string, Accessors.WrappedFunctionLengthAccessor, rocAttribs);
                map.AppendDescriptor(isolate, d);
            }

            {  // name

                Descriptor d = Descriptor.AccessorConstant(ReadOnlyRoots.name_string, Accessors.WrappedFunctionNameAccessor, rocAttribs);
                map.AppendDescriptor(isolate, d);
            }

            nativeContext.WrappedFunctionMap = map;
        }

        // Internal steps of ShadowRealmImportValue
        {
            JSFunction shadowRealmImportValueRejected = Bootstrapper.SimpleCreateFunction(isolate, ReadOnlyRoots.empty_string, Builtin.ShadowRealmImportValueRejected, 1, true);
            shadowRealmImportValueRejected.Shared.Native = false;
            nativeContext.ShadowRealmImportValueRejected = shadowRealmImportValueRejected;
        }
    }

    void InstallGlobalThisBinding()
    {
        ScopeInfo scopeInfo = ScopeInfo.CreateGlobalThisBinding();
        Context context = _factory.NewScriptContext(_nativeContext, scopeInfo);

        // Go ahead and hook it up while we're at it.
        int slot = scopeInfo.ReceiverContextSlotIndex();
        context.Slots[slot] = _nativeContext.GlobalProxyObject;

        ScriptContextTable scriptContexts = _nativeContext.ScriptContextTable;
        ScriptContextTable newScriptContexts = ScriptContextTable.Add(_isolate, scriptContexts, context, false);
        _nativeContext.ScriptContextTable = newScriptContexts;
    }
}
