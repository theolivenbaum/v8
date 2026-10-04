// Port of src/wasm/wasm-js.cc: the WebAssembly namespace object and the
// JS API callbacks (WebAssembly.compile/validate/instantiate, Module,
// Instance, Memory, Table, Global, Tag, Exception, the error constructors,
// and JSPI's Suspending/promising).
//
// As in V8, the functions are API functions (FunctionTemplateInfo callbacks,
// run through Builtin.HandleApiCallOrConstruct), and each callback reports
// errors through an ErrorThrower named after the API, so messages read
// "WebAssembly.Module(): ...". Compilation is synchronous in V8Sharp; the
// asynchronous APIs resolve their promises from a foreground task, as V8's
// asynchronous compilation does (deviations.md, "WebAssembly").
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmModule = Wacs.Core.Module;

namespace V8Sharp.Wasm;

public static partial class WasmJs
{
    // ---- Installation (WasmJs::PrepareForSnapshot, InstallModule, Install) ------------

    /// <summary>
    /// WasmJs::PrepareForSnapshot: creates the WebAssembly object and its
    /// constructors in the native context (not yet on the global object).
    /// <paramref name="installError"/> is Genesis's InstallError.
    /// </summary>
    public static void PrepareForSnapshot(Isolate isolate, NativeContext nativeContext,
        Action<JSObject, JSString, Context.Field> installError)
    {
        Factory f = isolate.Factory;
        const PropertyAttributes roAttributes = PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY;

        // Create the WebAssembly object.
        JSObject webassembly = f.NewJSObject(nativeContext.ObjectFunction);
        nativeContext.WasmWebassemblyObject = webassembly;
        JSObject.AddProperty(isolate, webassembly, ReadOnlyRoots.to_string_tag_symbol,
            f.InternalizeString("WebAssembly"), roAttributes);
        InstallFunc(isolate, webassembly, "compile", WebAssemblyCompile, 1);
        InstallFunc(isolate, webassembly, "validate", WebAssemblyValidate, 1);
        InstallFunc(isolate, webassembly, "instantiate", WebAssemblyInstantiate, 1);

        // Create the Module object.
        InstallModule(isolate, nativeContext, webassembly);

        // Create the Instance object.
        {
            JSFunction instanceConstructor = InstallConstructorFunc(isolate, webassembly, "Instance", WebAssemblyInstance);
            JSObject instanceProto = SetupConstructor(isolate, instanceConstructor, InstanceType.WasmInstanceObjectType,
                "WebAssembly.Instance");
            nativeContext.WasmInstanceConstructor = instanceConstructor;
            InstallGetter(isolate, instanceProto, "exports", WebAssemblyInstanceGetExports);
        }

        // Create the Table object.
        {
            JSFunction tableConstructor = InstallConstructorFunc(isolate, webassembly, "Table", WebAssemblyTable);
            JSObject tableProto = SetupConstructor(isolate, tableConstructor, InstanceType.WasmTableObjectType,
                "WebAssembly.Table");
            nativeContext.WasmTableConstructor = tableConstructor;
            InstallGetter(isolate, tableProto, "length", WebAssemblyTableGetLength);
            InstallFunc(isolate, tableProto, "grow", WebAssemblyTableGrow, 1);
            InstallFunc(isolate, tableProto, "set", WebAssemblyTableSet, 1);
            InstallFunc(isolate, tableProto, "get", WebAssemblyTableGet, 1);
        }

        // Create the Memory object.
        {
            JSFunction memoryConstructor = InstallConstructorFunc(isolate, webassembly, "Memory", WebAssemblyMemory);
            JSObject memoryProto = SetupConstructor(isolate, memoryConstructor, InstanceType.WasmMemoryObjectType,
                "WebAssembly.Memory");
            nativeContext.WasmMemoryConstructor = memoryConstructor;
            InstallFunc(isolate, memoryProto, "grow", WebAssemblyMemoryGrow, 1);
            InstallGetter(isolate, memoryProto, "buffer", WebAssemblyMemoryGetBuffer);
            InstallFunc(isolate, memoryProto, "toFixedLengthBuffer", WebAssemblyMemoryToFixedLengthBuffer, 0);
            InstallFunc(isolate, memoryProto, "toResizableBuffer", WebAssemblyMemoryToResizableBuffer, 0);
        }

        // Create the Global object.
        {
            JSFunction globalConstructor = InstallConstructorFunc(isolate, webassembly, "Global", WebAssemblyGlobal);
            JSObject globalProto = SetupConstructor(isolate, globalConstructor, InstanceType.WasmGlobalObjectType,
                "WebAssembly.Global");
            nativeContext.WasmGlobalConstructor = globalConstructor;
            InstallFunc(isolate, globalProto, "valueOf", WebAssemblyGlobalValueOf, 0);
            InstallGetterSetter(isolate, globalProto, "value", WebAssemblyGlobalGetValue, WebAssemblyGlobalSetValue);
        }

        // Create the Exception object.
        {
            JSFunction tagConstructor = InstallConstructorFunc(isolate, webassembly, "Tag", WebAssemblyTag);
            SetupConstructor(isolate, tagConstructor, InstanceType.WasmTagObjectType, "WebAssembly.Tag");
            nativeContext.WasmTagConstructor = tagConstructor;
            // The JSTag's tag is the engine's (one per isolate, created on
            // first use of wasm); see WasmEngine.JSTag.
            var jsTagObject = (WasmTagObject)JSObject.NewWithMap(isolate, tagConstructor.InitialMap);
            jsTagObject.IsJSTag = true;
            nativeContext.WasmJSTag = jsTagObject;
            JSObject.AddProperty(isolate, webassembly, f.InternalizeString("JSTag"), jsTagObject, roAttributes);
        }

        // Set up the runtime exception constructor.
        {
            JSFunction exceptionConstructor = InstallConstructorFunc(isolate, webassembly, "Exception", WebAssemblyException);
            exceptionConstructor.Shared.Length = 2;
            JSObject exceptionProto = SetupConstructor(isolate, exceptionConstructor, InstanceType.WasmExceptionPackageType,
                "WebAssembly.Exception");
            InstallFunc(isolate, exceptionProto, "getArg", WebAssemblyExceptionGetArg, 2);
            InstallFunc(isolate, exceptionProto, "is", WebAssemblyExceptionIs, 1);
            InstallGetter(isolate, exceptionProto, "stack", WebAssemblyExceptionGetStack);
            nativeContext.WasmExceptionConstructor = exceptionConstructor;
        }

        // By default, make all exported functions an instance of {Function}.
        nativeContext.WasmExportedFunctionMap = nativeContext.SloppyFunctionWithoutPrototypeMap;

        // Setup errors.
        installError(webassembly, ReadOnlyRoots.CompileError_string, Context.Field.WASM_COMPILE_ERROR_FUNCTION_INDEX);
        installError(webassembly, ReadOnlyRoots.LinkError_string, Context.Field.WASM_LINK_ERROR_FUNCTION_INDEX);
        installError(webassembly, ReadOnlyRoots.RuntimeError_string, Context.Field.WASM_RUNTIME_ERROR_FUNCTION_INDEX);

        // JSPI (InstallJSPromiseIntegration): WebAssembly.Suspending,
        // WebAssembly.promising and WebAssembly.SuspendError.
        if (!isolate.Flags.wasm_jitless)
        {
            JSFunction suspendingConstructor = InstallConstructorFunc(isolate, webassembly, "Suspending", WebAssemblySuspending);
            SetupConstructor(isolate, suspendingConstructor, InstanceType.WasmSuspendingObjectType, "WebAssembly.Suspending");
            nativeContext.WasmSuspendingConstructor = suspendingConstructor;
            InstallFunc(isolate, webassembly, "promising", WebAssemblyPromising, 1);
            installError(webassembly, isolate.Factory.InternalizeString("SuspendError"),
                Context.Field.WASM_SUSPEND_ERROR_FUNCTION_INDEX);
        }
    }

    /// <summary>WasmJs::InstallModule.</summary>
    static void InstallModule(Isolate isolate, NativeContext nativeContext, JSObject webassembly)
    {
        JSFunction moduleConstructor = InstallConstructorFunc(isolate, webassembly, "Module", WebAssemblyModule);
        SetupConstructor(isolate, moduleConstructor, InstanceType.WasmModuleObjectType, "WebAssembly.Module");
        nativeContext.WasmModuleConstructor = moduleConstructor;
        InstallFunc(isolate, moduleConstructor, "imports", WebAssemblyModuleImports, 1);
        InstallFunc(isolate, moduleConstructor, "exports", WebAssemblyModuleExports, 1);
        InstallFunc(isolate, moduleConstructor, "customSections", WebAssemblyModuleCustomSections, 2);
    }

    /// <summary>
    /// WasmJs::Install: exposes WebAssembly on the global object (unless
    /// jitless without the wasm interpreter, as in V8).
    /// </summary>
    public static void Install(Isolate isolate, NativeContext nativeContext)
    {
        if (nativeContext.IsWasmJSInstalled.IsSmi && nativeContext.IsWasmJSInstalled.Number != 0) return;
        nativeContext.IsWasmJSInstalled = JSValue.FromInt(1);
        if (nativeContext.Slots[(int)Context.Field.WASM_WEBASSEMBLY_OBJECT_INDEX].IsUndefined) return;
        JSObject webassembly = nativeContext.WasmWebassemblyObject;
        bool exposeWasm = !isolate.Flags.jitless || isolate.Flags.correctness_fuzzer_suppressions ||
                          isolate.Flags.wasm_jitless;
        if (exposeWasm)
        {
            JSObject.AddProperty(isolate, nativeContext.GlobalObject, isolate.Factory.InternalizeString("WebAssembly"),
                webassembly, PropertyAttributes.DONT_ENUM);
        }
    }

    // ---- API function creation (CreateFunc, InstallFunc, InstallGetter, SetupConstructor) ----

    static JSFunction CreateFunc(Isolate isolate, JSString name, BuiltinFunction func, bool hasPrototype)
    {
        Factory factory = isolate.Factory;
        NativeContext context = isolate.NativeContext;
        var data = new FunctionTemplateInfo(func);
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(name, data, Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        if (!hasPrototype)
        {
            return factory.NewFunction(info, context, context.StrictFunctionWithoutPrototypeMap);
        }
        // ApiNatives::CreateApiFunction with a read-only prototype.
        JSFunction result = factory.NewFunction(info, context, context.StrictFunctionWithReadonlyPrototypeMap);
        JSObject prototype = factory.NewFunctionPrototype(result);
        Map initialMap = factory.NewContextfulMapForCurrentContext(InstanceType.JSApiObjectType,
            JSObject.GetHeaderSize(InstanceType.JSApiObjectType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
        initialMap.SetConstructor(result);
        JSFunction.SetInitialMap(isolate, result, initialMap, prototype);
        return result;
    }

    static JSFunction InstallFunc(Isolate isolate, JSObject obj, string name, BuiltinFunction func, int length,
        bool hasPrototype = false, PropertyAttributes attributes = PropertyAttributes.NONE)
    {
        JSString internalized = isolate.Factory.InternalizeString(name);
        JSFunction function = CreateFunc(isolate, internalized, func, hasPrototype);
        function.Shared.Length = (ushort)length;
        function.Shared.GetApiFunctionData().Length = length;
        JSObject.AddProperty(isolate, obj, internalized, function, attributes);
        return function;
    }

    static JSFunction InstallConstructorFunc(Isolate isolate, JSObject obj, string name, BuiltinFunction func) =>
        InstallFunc(isolate, obj, name, func, 1, true, PropertyAttributes.DONT_ENUM);

    static void InstallGetter(Isolate isolate, JSObject obj, string name, BuiltinFunction func)
    {
        JSString internalized = isolate.Factory.InternalizeString(name);
        JSFunction getter = CreateFunc(isolate, isolate.Factory.InternalizeString("get " + name), func, false);
        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, obj, internalized, getter, JSValue.Null, PropertyAttributes.NONE);
    }

    static void InstallGetterSetter(Isolate isolate, JSObject obj, string name, BuiltinFunction getterFunc,
        BuiltinFunction setterFunc)
    {
        JSString internalized = isolate.Factory.InternalizeString(name);
        JSFunction getter = CreateFunc(isolate, isolate.Factory.InternalizeString("get " + name), getterFunc, false);
        JSFunction setter = CreateFunc(isolate, isolate.Factory.InternalizeString("set " + name), setterFunc, false);
        setter.Shared.Length = 1;
        setter.Shared.GetApiFunctionData().Length = 1;
        JSObject.DefineOwnAccessorIgnoreAttributes(isolate, obj, internalized, getter, setter, PropertyAttributes.NONE);
    }

    /// <summary>SetupConstructor: the instance map of the wasm type, and @@toStringTag on the prototype.</summary>
    static JSObject SetupConstructor(Isolate isolate, JSFunction constructor, InstanceType instanceType, string? name)
    {
        var proto = (JSObject)constructor.InstancePrototype;
        Map map = isolate.Factory.NewContextfulMapForCurrentContext(instanceType, JSObject.GetHeaderSize(instanceType),
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
        map.SetConstructor(constructor);
        JSFunction.SetInitialMap(isolate, constructor, map, proto);
        if (name is not null)
        {
            JSObject.AddProperty(isolate, proto, ReadOnlyRoots.to_string_tag_symbol,
                isolate.Factory.NewStringFromAsciiChecked(name),
                PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        }
        return proto;
    }

    /// <summary>The map of the JS objects of wasm GC structs and arrays: null prototype, not extensible.</summary>
    internal static Map GCObjectMap(Isolate isolate, InstanceType type)
    {
        Map map = isolate.Factory.NewContextfulMapForCurrentContext(type, JSObject.GetHeaderSize(type),
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
        Map.SetPrototype(isolate, map, null);
        map.IsExtensible = false;
        return map;
    }

    // ---- Shared helpers ------------------------------------------------------------

    /// <summary>
    /// GetAndCopyFirstArgumentAsBytes: the bytes of an ArrayBuffer or typed
    /// array argument; a TypeError for anything else, a CompileError when empty.
    /// </summary>
    static byte[] GetAndCopyFirstArgumentAsBytes(Isolate isolate, in BuiltinArguments args, ErrorThrower thrower)
    {
        JSValue source = args.AtOrUndefined(1);
        ReadOnlySpan<byte> bytes;
        switch (source.HeapObjectOrNull)
        {
            case JSArrayBuffer buffer:
                bytes = buffer.BackingStoreBuffer.AsSpan(0, (int)Math.Min(buffer.GetByteLength(), (ulong)buffer.BackingStoreBuffer.Length));
                break;
            case JSTypedArray array:
            {
                ulong length = array.WasDetached ? 0 : array.GetByteLength();
                bytes = length == 0 ? [] : array.Buffer.BackingStoreBuffer.AsSpan((int)array.ByteOffset, (int)length);
                break;
            }
            default:
                thrower.TypeError("Argument 0 must be a buffer source");
                return null!;
        }
        if (bytes.Length == 0)
        {
            thrower.CompileError("BufferSource argument is empty");
        }
        if (bytes.Length > WasmLimits.kV8MaxWasmModuleSize)
        {
            thrower.CompileError($"buffer source exceeds maximum size of {WasmLimits.kV8MaxWasmModuleSize} (is {bytes.Length})");
        }
        return bytes.ToArray();
    }

    /// <summary>GET_FIRST_ARGUMENT_AS(Module).</summary>
    static WasmModuleObject GetFirstArgumentAsModule(in BuiltinArguments args, ErrorThrower thrower)
    {
        if (args.AtOrUndefined(1).HeapObjectOrNull is WasmModuleObject module) return module;
        thrower.TypeError("Argument 0 must be a WebAssembly.Module");
        return null!;
    }

    /// <summary>GET_FIRST_ARGUMENT_AS(Tag).</summary>
    static WasmTagObject GetFirstArgumentAsTag(in BuiltinArguments args, ErrorThrower thrower)
    {
        if (args.AtOrUndefined(1).HeapObjectOrNull is WasmTagObject tag) return tag;
        thrower.TypeError("Argument 0 must be a WebAssembly.Tag");
        return null!;
    }

    /// <summary>EXTRACT_THIS.</summary>
    static T ExtractThis<T>(in BuiltinArguments args, ErrorThrower thrower, string typeName) where T : JSObject
    {
        if (args.Receiver.HeapObjectOrNull is T receiver) return receiver;
        thrower.TypeError("Receiver is not a " + typeName);
        return null!;
    }

    /// <summary>
    /// TransferPrototype: the new object takes the prototype of the receiver
    /// the construct call allocated (subclassing).
    /// </summary>
    static void TransferPrototype(Isolate isolate, JSObject destination, JSValue source)
    {
        if (source.HeapObjectOrNull is not JSReceiver receiver) return;
        JSReceiver? prototype = JSObject.GetPrototype(isolate, receiver);
        if (ReferenceEquals(prototype, destination.Map.Prototype)) return;
        JSObject.SetPrototype(isolate, destination, prototype is null ? JSValue.Null : JSValue.FromObject(prototype),
            false, ShouldThrow.ThrowOnError);
    }

    /// <summary>Web IDL '[EnforceRange] unsigned long' (EnforceUint32).</summary>
    static uint EnforceUint32(Isolate isolate, string argumentName, JSValue v, ErrorThrower thrower)
    {
        // A failing ToNumber leaves its own exception (V8: the thrower defers to it).
        double d = ObjectOps.ToNumber(isolate, v).Number;
        if (!double.IsFinite(d))
        {
            thrower.TypeError(argumentName + " must be convertible to a valid number");
        }
        if (d < 0)
        {
            thrower.TypeError(argumentName + " must be non-negative");
        }
        if (d > uint.MaxValue)
        {
            thrower.TypeError(argumentName + " must be in the unsigned long range");
        }
        return (uint)d;
    }

    /// <summary>EnforceBigIntUint64: the first step of AddressValueToU64 for i64.</summary>
    static ulong EnforceBigIntUint64(Isolate isolate, string argumentName, JSValue v, ErrorThrower thrower)
    {
        BigInt bigint = BigInt.FromObject(isolate, v);
        ulong result = BigInt.AsUint64(bigint, out bool lossless);
        if (!lossless)
        {
            thrower.TypeError(argumentName + " must be in u64 range");
        }
        return result;
    }

    /// <summary>AddressValueToU64 (memory64 JS API).</summary>
    static ulong AddressValueToU64(Isolate isolate, ErrorThrower thrower, JSValue value, string propertyName, bool isI64) =>
        isI64 ? EnforceBigIntUint64(isolate, propertyName, value, thrower) : EnforceUint32(isolate, propertyName, value, thrower);

    /// <summary>AddressValueToBoundedU64.</summary>
    static ulong AddressValueToBoundedU64(Isolate isolate, ErrorThrower thrower, JSValue value, string propertyName,
        bool isI64, ulong lowerBound, ulong upperBound)
    {
        ulong addressValue = AddressValueToU64(isolate, thrower, value, "Property '" + propertyName + "'", isI64);
        if (addressValue < lowerBound)
        {
            thrower.RangeError($"Property '{propertyName}': value {addressValue} is below the lower bound {lowerBound:x}");
        }
        if (addressValue > upperBound)
        {
            thrower.RangeError($"Property '{propertyName}': value {addressValue} is above the upper bound {upperBound}");
        }
        return addressValue;
    }

    /// <summary>GetOptionalAddressValue: null when the property is undefined.</summary>
    static ulong? GetOptionalAddressValue(Isolate isolate, ErrorThrower thrower, JSReceiver descriptor, string property,
        bool isI64, ulong lowerBound, ulong upperBound)
    {
        JSValue value = ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString(property));
        if (value.IsUndefined) return null;
        return AddressValueToBoundedU64(isolate, thrower, value, property, isI64, lowerBound, upperBound);
    }

    /// <summary>GetInitialOrMinimumProperty.</summary>
    static ulong GetInitialOrMinimumProperty(Isolate isolate, ErrorThrower thrower, JSReceiver descriptor, bool isI64,
        ulong upperBound)
    {
        ulong? initial = GetOptionalAddressValue(isolate, thrower, descriptor, "initial", isI64, 0, upperBound);
        if (initial is null)
        {
            thrower.TypeError("Property 'initial' is required");
        }
        return initial!.Value;
    }

    /// <summary>AddressValueFromUnsigned.</summary>
    static JSValue AddressValueFromUnsigned(Isolate isolate, bool isI64, ulong value) =>
        isI64 ? BigInt.FromUint64(isolate, value) : JSValue.FromNumber(value);

    /// <summary>GetAddressType: true for "i64", false for "i32" or undefined.</summary>
    static bool GetAddressType(Isolate isolate, JSReceiver descriptor, ErrorThrower thrower)
    {
        JSValue addressValue = ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString("address"));
        if (addressValue.IsUndefined) return false;
        string address = ObjectOps.ToString(isolate, addressValue).ToString();
        if (address == "i64") return true;
        if (address == "i32") return false;
        thrower.TypeError($"Unknown address type '{address}'; pass 'i32' or 'i64'");
        return false;
    }

    /// <summary>DefaultReferenceValue: undefined for externref, null for the other references.</summary>
    static JSValue DefaultReferenceValue(ValType type) =>
        type.GetHeapType() == HeapType.Extern ? JSValue.Undefined : JSValue.Null;

    /// <summary>
    /// GetValueType: the value type named by a type-reflection string, null
    /// (V8: kWasmVoid) when it is not one.
    /// </summary>
    static ValType? GetValueType(Isolate isolate, JSValue value)
    {
        string s = ObjectOps.ToString(isolate, value).ToString();
        return s switch
        {
            "i32" => ValType.I32,
            "f32" => ValType.F32,
            "i64" => ValType.I64,
            "f64" => ValType.F64,
            "v128" => ValType.V128,
            "externref" => ValType.ExternRef,
            "anyfunc" => ValType.FuncRef,
            "eqref" => ValType.Eq,
            "anyref" => ValType.Any,
            "structref" => ValType.Struct,
            "arrayref" => ValType.Array,
            "i31ref" => ValType.I31,
            "exnref" => ValType.Exn,
            _ => null,
        };
    }

    /// <summary>V8's ValueType::name() for the types the JS API reports.</summary>
    internal static string TypeName(ValType type)
    {
        switch (type)
        {
            case ValType.I32: return "i32";
            case ValType.I64: return "i64";
            case ValType.F32: return "f32";
            case ValType.F64: return "f64";
            case ValType.V128: return "v128";
            case ValType.FuncRef: return "funcref";
            case ValType.ExternRef: return "externref";
            case ValType.Any: return "anyref";
            case ValType.Eq: return "eqref";
            case ValType.I31: return "i31ref";
            case ValType.Struct: return "structref";
            case ValType.Array: return "arrayref";
            case ValType.Exn: return "exnref";
            case ValType.None: return "nullref";
            case ValType.NoFunc: return "nullfuncref";
            case ValType.NoExtern: return "nullexternref";
            case ValType.NoExn: return "nullexnref";
        }
        if (type.IsDefType())
        {
            return (type.IsNullable() ? "(ref null " : "(ref ") + type.Index().Value + ")";
        }
        string heap = type.GetHeapType() switch
        {
            HeapType.Func => "func",
            HeapType.Extern => "extern",
            HeapType.Any => "any",
            HeapType.Eq => "eq",
            HeapType.I31 => "i31",
            HeapType.Struct => "struct",
            HeapType.Array => "array",
            HeapType.Exn => "exn",
            HeapType.None => "none",
            HeapType.NoFunc => "nofunc",
            HeapType.NoExtern => "noextern",
            HeapType.NoExn => "noexn",
            _ => type.ToString(),
        };
        return "(ref " + heap + ")";
    }

    // ---- WebAssembly.compile / validate / Module ------------------------------------

    /// <summary>WebAssembly.compile(bytes, options) -> Promise.</summary>
    static JSValue WebAssemblyCompile(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.compile()");
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        byte[] bytes;
        try
        {
            bytes = GetAndCopyFirstArgumentAsBytes(isolate, args, thrower);
        }
        catch (JavaScriptException e)
        {
            RejectLater(isolate, promise, e.Value);
            return promise;
        }
        AsyncCompile(isolate, thrower, bytes, (module, error) =>
        {
            if (module is { } m) JSPromise.Resolve(isolate, promise, m);
            else JSPromise.Reject(isolate, promise, error);
        });
        return promise;
    }

    /// <summary>WebAssembly.validate(bytes, options) -> bool.</summary>
    static JSValue WebAssemblyValidate(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.validate()");
        byte[] bytes;
        try
        {
            bytes = GetAndCopyFirstArgumentAsBytes(isolate, args, thrower);
        }
        catch (JavaScriptException e) when (e.Value.HeapObjectOrNull is JSObject error &&
                                            ReferenceEquals(error.Map.GetConstructor(), isolate.NativeContext.WasmCompileErrorFunction))
        {
            // Clear wasm exceptions; return false instead.
            return JSValue.False;
        }
        return JSValue.FromBoolean(WasmEngine.Validate(bytes));
    }

    /// <summary>new WebAssembly.Module(bytes, options) -> WebAssembly.Module.</summary>
    static JSValue WebAssemblyModule(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Module()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Module must be invoked with 'new'");
        }
        byte[] bytes = GetAndCopyFirstArgumentAsBytes(isolate, args, thrower);
        WasmModuleObject moduleObj = SyncCompile(isolate, thrower, bytes);
        TransferPrototype(isolate, moduleObj, args.Receiver);
        return moduleObj;
    }

    /// <summary>WasmEngine::SyncCompile: decodes and validates, or throws a CompileError.</summary>
    internal static WasmModuleObject SyncCompile(Isolate isolate, ErrorThrower thrower, byte[] bytes)
    {
        WasmModule module;
        try
        {
            module = WasmEngine.Compile(bytes);
        }
        catch (WasmCompileException e)
        {
            thrower.CompileError(e.Message);
            return null!;
        }
        return NewModuleObject(isolate, module, bytes);
    }

    internal static WasmModuleObject NewModuleObject(Isolate isolate, WasmModule module, byte[] bytes)
    {
        var moduleObj = (WasmModuleObject)JSObject.NewWithMap(isolate, isolate.NativeContext.WasmModuleConstructor.InitialMap);
        moduleObj.Module = module;
        moduleObj.WireBytes = bytes;
        return moduleObj;
    }

    /// <summary>
    /// WasmEngine::AsyncCompile: compiles now and reports the result from a
    /// foreground task, as V8's background compilation does.
    /// </summary>
    static void AsyncCompile(Isolate isolate, ErrorThrower thrower, byte[] bytes, Action<JSValue?, JSValue> onDone)
    {
        NativeContext context = isolate.NativeContext;
        WasmModule? module = null;
        string? error = null;
        try
        {
            module = WasmEngine.Compile(bytes);
        }
        catch (WasmCompileException e)
        {
            error = e.Message;
        }
        isolate.PostNonNestableTask(i =>
        {
            using (i.EnterContext(context))
            {
                if (module is not null) onDone(NewModuleObject(i, module, bytes), JSValue.Undefined);
                else onDone(null, thrower.Reify(ErrorThrower.ErrorType.CompileError, error!));
            }
        });
    }

    static void RejectLater(Isolate isolate, JSPromise promise, JSValue reason) =>
        JSPromise.Reject(isolate, promise, reason);

    /// <summary>WebAssembly.Module.imports(module) -> Array&lt;Import&gt;.</summary>
    static JSValue WebAssemblyModuleImports(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Module.imports()");
        WasmModuleObject module = GetFirstArgumentAsModule(args, thrower);
        return WasmModuleObjectOps.GetImports(isolate, module);
    }

    /// <summary>WebAssembly.Module.exports(module) -> Array&lt;Export&gt;.</summary>
    static JSValue WebAssemblyModuleExports(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Module.exports()");
        WasmModuleObject module = GetFirstArgumentAsModule(args, thrower);
        return WasmModuleObjectOps.GetExports(isolate, module);
    }

    /// <summary>WebAssembly.Module.customSections(module, name) -> Array&lt;Section&gt;.</summary>
    static JSValue WebAssemblyModuleCustomSections(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Module.customSections()");
        WasmModuleObject module = GetFirstArgumentAsModule(args, thrower);
        if (args.AtOrUndefined(2).IsUndefined)
        {
            thrower.TypeError("Argument 1 is required");
        }
        JSString name = ObjectOps.ToString(isolate, args.AtOrUndefined(2));
        return WasmModuleObjectOps.GetCustomSections(isolate, module, name.ToString());
    }

    // ---- WebAssembly.Instance / instantiate ---------------------------------------------

    /// <summary>new WebAssembly.Instance(module, imports) -> WebAssembly.Instance.</summary>
    static JSValue WebAssemblyInstance(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Instance()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Instance must be invoked with 'new'");
        }
        WasmModuleObject moduleObject = GetFirstArgumentAsModule(args, thrower);
        JSValue ffi = args.AtOrUndefined(2);
        if (!ffi.IsUndefined && !ffi.IsJSReceiver)
        {
            thrower.TypeError("Argument 1 must be an object");
        }
        WasmInstanceObject instance = InstanceBuilder.Build(isolate, thrower, moduleObject,
            ffi.IsUndefined ? null : (JSReceiver)ffi.Object);
        TransferPrototype(isolate, instance, args.Receiver);
        return instance;
    }

    /// <summary>
    /// WebAssembly.instantiate(module, imports) -> Promise&lt;Instance&gt;;
    /// WebAssembly.instantiate(bytes, imports, options) -> Promise&lt;{module, instance}&gt;.
    /// </summary>
    static JSValue WebAssemblyInstantiate(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.instantiate()");
        JSPromise promise = PromiseBuiltins.NewJSPromise(isolate);
        JSValue firstArg = args.AtOrUndefined(1);
        if (!firstArg.IsJSObject)
        {
            JSPromise.Reject(isolate, promise, thrower.Reify(ErrorThrower.ErrorType.TypeError,
                "Argument 0 must be a buffer source or a WebAssembly.Module object"));
            return promise;
        }
        JSValue ffi = args.AtOrUndefined(2);
        if (!ffi.IsUndefined && !ffi.IsJSReceiver)
        {
            JSPromise.Reject(isolate, promise, thrower.Reify(ErrorThrower.ErrorType.TypeError,
                "Argument 1 must be an object"));
            return promise;
        }
        JSReceiver? imports = ffi.IsUndefined ? null : (JSReceiver)ffi.Object;

        if (firstArg.Object is WasmModuleObject moduleObj)
        {
            AsyncInstantiate(isolate, thrower, promise, moduleObj, imports, returnModule: false);
            return promise;
        }

        byte[] bytes;
        try
        {
            bytes = GetAndCopyFirstArgumentAsBytes(isolate, args, thrower);
        }
        catch (JavaScriptException e)
        {
            JSPromise.Reject(isolate, promise, e.Value);
            return promise;
        }
        AsyncCompile(isolate, thrower, bytes, (module, error) =>
        {
            if (module is null)
            {
                JSPromise.Reject(isolate, promise, error);
                return;
            }
            AsyncInstantiate(isolate, thrower, promise, (WasmModuleObject)module!.Value.Object, imports, returnModule: true);
        });
        return promise;
    }

    /// <summary>
    /// WasmEngine::AsyncInstantiate: instantiates now (V8 does too; only
    /// compilation is asynchronous) and settles <paramref name="promise"/>
    /// with the instance or the {module, instance} result object.
    /// </summary>
    static void AsyncInstantiate(Isolate isolate, ErrorThrower thrower, JSPromise promise, WasmModuleObject moduleObj,
        JSReceiver? imports, bool returnModule)
    {
        WasmInstanceObject instance;
        try
        {
            instance = InstanceBuilder.Build(isolate, thrower, moduleObj, imports);
        }
        catch (JavaScriptException e)
        {
            JSPromise.Reject(isolate, promise, e.Value);
            return;
        }
        if (returnModule)
        {
            JSObject result = isolate.Factory.NewJSObject(isolate.NativeContext.ObjectFunction);
            JSReceiver.CreateDataProperty(isolate, result, isolate.Factory.InternalizeString("module"), moduleObj,
                ShouldThrow.ThrowOnError);
            JSReceiver.CreateDataProperty(isolate, result, isolate.Factory.InternalizeString("instance"), instance,
                ShouldThrow.ThrowOnError);
            JSPromise.Resolve(isolate, promise, result);
        }
        else
        {
            JSPromise.Resolve(isolate, promise, instance);
        }
    }

    /// <summary>WebAssembly.Instance.prototype.exports getter.</summary>
    static JSValue WebAssemblyInstanceGetExports(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Instance.exports()");
        WasmInstanceObject receiver = ExtractThis<WasmInstanceObject>(args, thrower, "WebAssembly.Instance");
        return receiver.ExportsObject;
    }
}
