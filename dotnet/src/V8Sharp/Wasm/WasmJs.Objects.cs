// Port of src/wasm/wasm-js.cc, continued: WebAssembly.Table, Memory, Global,
// Tag, Exception and JSPI's Suspending/promising; and the memory parts of
// src/wasm/wasm-objects.cc (WasmMemoryObject::New, GetArrayBuffer, Grow,
// RefreshBuffer).
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmConstants = Wacs.Core.Utilities.Constants;

namespace V8Sharp.Wasm;

public static partial class WasmJs
{
    // ---- WebAssembly.Table ----------------------------------------------------------

    /// <summary>new WebAssembly.Table(descriptor, value) -> WebAssembly.Table.</summary>
    static JSValue WebAssemblyTable(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Table()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Table must be invoked with 'new'");
        }
        if (!args.AtOrUndefined(1).IsJSReceiver)
        {
            thrower.TypeError("Argument 0 must be a table descriptor");
        }
        var descriptor = (JSReceiver)args.AtOrUndefined(1).Object;
        ValType type;
        {
            JSValue value = ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString("element"));
            string s = ObjectOps.ToString(isolate, value).ToString();
            // The JS api uses 'anyfunc' instead of 'funcref'.
            switch (s)
            {
                case "anyfunc": type = ValType.FuncRef; break;
                case "externref": type = ValType.ExternRef; break;
                case "anyref": type = ValType.Any; break;
                case "eqref": type = ValType.Eq; break;
                case "structref": type = ValType.Struct; break;
                case "arrayref": type = ValType.Array; break;
                case "i31ref": type = ValType.I31; break;
                default:
                    thrower.TypeError("Descriptor property 'element' must be a WebAssembly reference type");
                    return default;
            }
        }
        bool isI64 = GetAddressType(isolate, descriptor, thrower);
        ulong initial = GetInitialOrMinimumProperty(isolate, thrower, descriptor, isI64, WasmLimits.kV8MaxWasmTableInitEntries);
        ulong? maximum = GetOptionalAddressValue(isolate, thrower, descriptor, "maximum", isI64, initial, ulong.MaxValue);

        WasmEngine engine = WasmEngine.Get(isolate);
        Value initialValue;
        if (initial > 0 && args.ArgcWithoutReceiver >= 2 && !args.AtOrUndefined(2).IsUndefined)
        {
            if (!engine.TryJSToWasmRef(args.AtOrUndefined(2), type, null, out initialValue, out string? error))
            {
                thrower.TypeError("Argument 2 must be undefined or a value of type compatible with the type of the new table: " +
                                  error + ".");
            }
        }
        else
        {
            engine.TryJSToWasmRef(DefaultReferenceValue(type), type, null, out initialValue, out _);
        }

        var limits = new Limits(isI64 ? AddrType.I64 : AddrType.I32, (long)initial, maximum is { } m ? (long)m : null);
        var tableType = new TableType(type, limits);
        TableAddr address = engine.Runtime.AllocateTable(tableType, initialValue);
        var tableObj = (WasmTableObject)JSObject.NewWithMap(isolate, isolate.NativeContext.WasmTableConstructor.InitialMap);
        tableObj.Address = address;
        tableObj.Table = engine.Store[address];
        engine.RegisterTableObject(tableObj);
        TransferPrototype(isolate, tableObj, args.Receiver);
        return tableObj;
    }

    static JSValue WebAssemblyTableGetLength(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Table.length()");
        WasmTableObject receiver = ExtractThis<WasmTableObject>(args, thrower, "WebAssembly.Table");
        return AddressValueFromUnsigned(isolate, receiver.IsTable64, (ulong)receiver.CurrentLength);
    }

    /// <summary>WebAssembly.Table.grow(num, init_value = null) -> num.</summary>
    static JSValue WebAssemblyTableGrow(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Table.grow()");
        WasmTableObject receiver = ExtractThis<WasmTableObject>(args, thrower, "WebAssembly.Table");
        ulong growBy = AddressValueToU64(isolate, thrower, args.AtOrUndefined(1), "Argument 0", receiver.IsTable64);
        WasmEngine engine = WasmEngine.Get(isolate);
        Value initValue;
        if (args.ArgcWithoutReceiver >= 2)
        {
            if (!engine.TryJSToWasmRef(args.AtOrUndefined(2), receiver.ElementType, receiver.Module, out initValue,
                    out string? error))
            {
                thrower.TypeError("Argument 1 is invalid: " + error);
            }
        }
        else if (!receiver.ElementType.IsNullable())
        {
            thrower.TypeError("Argument 1 must be specified for non-nullable element type");
            return default;
        }
        else
        {
            engine.TryJSToWasmRef(DefaultReferenceValue(receiver.ElementType), receiver.ElementType, receiver.Module,
                out initValue, out _);
        }
        int oldSize = receiver.CurrentLength;
        bool grown = growBy <= WasmLimits.kV8MaxWasmTableSize &&
                     (ulong)oldSize + growBy <= WasmLimits.kV8MaxWasmTableSize &&
                     receiver.Table.Grow((long)growBy, initValue);
        if (!grown)
        {
            thrower.RangeError($"failed to grow table by {growBy}");
        }
        return AddressValueFromUnsigned(isolate, receiver.IsTable64, (ulong)oldSize);
    }

    /// <summary>WebAssembly.Table.get(num) -> any.</summary>
    static JSValue WebAssemblyTableGet(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Table.get()");
        WasmTableObject receiver = ExtractThis<WasmTableObject>(args, thrower, "WebAssembly.Table");
        ulong address = AddressValueToU64(isolate, thrower, args.AtOrUndefined(1), "Argument 0", receiver.IsTable64);
        if (address >= (ulong)receiver.CurrentLength)
        {
            thrower.RangeError($"invalid address {address} in {TypeName(receiver.ElementType)} table of size {receiver.CurrentLength}");
        }
        Value result = receiver.Table.Elements[(int)address];
        return WasmObjectToJSReturnValue(isolate, result, receiver.ElementType, receiver.Module, thrower);
    }

    /// <summary>WebAssembly.Table.set(num, any).</summary>
    static JSValue WebAssemblyTableSet(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Table.set()");
        WasmTableObject tableObject = ExtractThis<WasmTableObject>(args, thrower, "WebAssembly.Table");
        ulong address = AddressValueToU64(isolate, thrower, args.AtOrUndefined(1), "Argument 0", tableObject.IsTable64);
        if (address >= (ulong)tableObject.CurrentLength)
        {
            thrower.RangeError($"invalid address {address} in {TypeName(tableObject.ElementType)} table of size {tableObject.CurrentLength}");
        }
        WasmEngine engine = WasmEngine.Get(isolate);
        Value element;
        if (args.ArgcWithoutReceiver >= 2)
        {
            if (!engine.TryJSToWasmRef(args.AtOrUndefined(2), tableObject.ElementType, tableObject.Module, out element,
                    out string? error))
            {
                thrower.TypeError("Argument 1 is invalid for table: " + error);
            }
        }
        else if (tableObject.ElementType.IsDefaultable())
        {
            engine.TryJSToWasmRef(DefaultReferenceValue(tableObject.ElementType), tableObject.ElementType,
                tableObject.Module, out element, out _);
        }
        else
        {
            thrower.TypeError($"Table of non-defaultable type {TypeName(tableObject.ElementType)} needs explicit element");
            return default;
        }
        tableObject.Table.Elements[(int)address] = element;
        return JSValue.Undefined;
    }

    /// <summary>WasmObjectToJSReturnValue: types without a JS representation are a TypeError.</summary>
    static JSValue WasmObjectToJSReturnValue(Isolate isolate, Value value, ValType type, ModuleInstance? module,
        ErrorThrower thrower)
    {
        HeapType heap = type.IsDefType() ? HeapType.Func : type.GetHeapType();
        if (!type.IsDefType() && heap is HeapType.Exn or HeapType.NoExn or HeapType.Cont or HeapType.NoCont)
        {
            thrower.TypeError("invalid type " + TypeName(type));
        }
        if (type.IsDefType() && module?.Types[type.Index()].Expansion is ContType)
        {
            thrower.TypeError("invalid type " + TypeName(type));
        }
        return WasmEngine.Get(isolate).RefToJS(value);
    }

    // ---- WebAssembly.Memory ---------------------------------------------------------

    /// <summary>new WebAssembly.Memory(descriptor) -> WebAssembly.Memory.</summary>
    static JSValue WebAssemblyMemory(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Memory()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Memory must be invoked with 'new'");
        }
        if (!args.AtOrUndefined(1).IsJSReceiver)
        {
            thrower.TypeError("Argument 0 must be a memory descriptor");
        }
        var descriptor = (JSReceiver)args.AtOrUndefined(1).Object;
        bool isI64 = GetAddressType(isolate, descriptor, thrower);
        ulong maxSupportedPages = isI64 ? WasmLimits.kSpecMaxMemory64Pages : WasmLimits.kSpecMaxMemory32Pages;
        ulong initial = GetInitialOrMinimumProperty(isolate, thrower, descriptor, isI64, maxSupportedPages);
        ulong? maximum = GetOptionalAddressValue(isolate, thrower, descriptor, "maximum", isI64, initial, maxSupportedPages);
        JSValue sharedValue = ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString("shared"));
        bool shared = ObjectOps.BooleanValue(sharedValue);
        if (shared && maximum is null)
        {
            thrower.TypeError("If shared is true, maximum property should be defined.");
        }
        WasmMemoryObject? memoryObj = WasmMemoryObjectOps.New(isolate, initial, maximum, shared, isI64);
        if (memoryObj is null)
        {
            thrower.RangeError("could not allocate memory");
        }
        TransferPrototype(isolate, memoryObj!, args.Receiver);
        return memoryObj!;
    }

    /// <summary>WebAssembly.Memory.grow(num) -> num.</summary>
    static JSValue WebAssemblyMemoryGrow(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Memory.grow()");
        WasmMemoryObject receiver = ExtractThis<WasmMemoryObject>(args, thrower, "WebAssembly.Memory");
        ulong deltaPages = AddressValueToU64(isolate, thrower, args.AtOrUndefined(1), "Argument 0", receiver.IsMemory64);
        ulong oldPages = (ulong)receiver.Memory.Size;
        ulong maxPages = receiver.MaximumPages;
        if (deltaPages > maxPages - oldPages)
        {
            thrower.RangeError("Maximum memory size exceeded");
        }
        long ret = WasmMemoryObjectOps.Grow(isolate, receiver, deltaPages);
        if (ret == -1)
        {
            thrower.RangeError("Unable to grow instance memory");
        }
        return AddressValueFromUnsigned(isolate, receiver.IsMemory64, (ulong)ret);
    }

    /// <summary>WebAssembly.Memory.buffer -> ArrayBuffer.</summary>
    static JSValue WebAssemblyMemoryGetBuffer(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Memory.buffer");
        WasmMemoryObject receiver = ExtractThis<WasmMemoryObject>(args, thrower, "WebAssembly.Memory");
        return WasmMemoryObjectOps.GetArrayBuffer(isolate, receiver);
    }

    /// <summary>WebAssembly.Memory.toFixedLengthBuffer() -> ArrayBuffer.</summary>
    static JSValue WebAssemblyMemoryToFixedLengthBuffer(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Memory.toFixedLengthBuffer()");
        WasmMemoryObject receiver = ExtractThis<WasmMemoryObject>(args, thrower, "WebAssembly.Memory");
        return WasmMemoryObjectOps.ChangeArrayBufferResizability(isolate, receiver, resizable: false);
    }

    /// <summary>WebAssembly.Memory.toResizableBuffer() -> ArrayBuffer.</summary>
    static JSValue WebAssemblyMemoryToResizableBuffer(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Memory.toResizableBuffer()");
        WasmMemoryObject receiver = ExtractThis<WasmMemoryObject>(args, thrower, "WebAssembly.Memory");
        if (!receiver.HasMaximumPages)
        {
            thrower.TypeError("Memory must have a maximum");
        }
        return WasmMemoryObjectOps.ChangeArrayBufferResizability(isolate, receiver, resizable: true);
    }

    // ---- WebAssembly.Global ---------------------------------------------------------

    /// <summary>new WebAssembly.Global(descriptor[, value]).</summary>
    static JSValue WebAssemblyGlobal(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Global()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Global must be invoked with 'new'");
        }
        if (!args.AtOrUndefined(1).IsJSReceiver)
        {
            thrower.TypeError("Argument 0 must be a global descriptor");
        }
        var descriptor = (JSReceiver)args.AtOrUndefined(1).Object;
        bool isMutable = ObjectOps.BooleanValue(
            ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString("mutable")));
        ValType? maybeType = GetValueType(isolate,
            ObjectOps.GetProperty(isolate, descriptor, isolate.Factory.InternalizeString("value")));
        if (maybeType is null)
        {
            thrower.TypeError("Descriptor property 'value' must be a WebAssembly type");
        }
        ValType type = maybeType!.Value;

        // Convert value to a WebAssembly value, the default value is 0.
        JSValue value = args.AtOrUndefined(2);
        WasmEngine engine = WasmEngine.Get(isolate);
        Value wasmValue;
        switch (type)
        {
            case ValType.I32:
                wasmValue = new Value(value.IsUndefined ? 0 : (int)ObjectOps.ToInt32(isolate, value).Number);
                break;
            case ValType.I64:
                wasmValue = new Value(value.IsUndefined ? 0L : BigInt.AsInt64(BigInt.FromObject(isolate, value), out _));
                break;
            case ValType.F32:
                wasmValue = new Value(value.IsUndefined ? 0f : (float)ObjectOps.ToNumber(isolate, value).Number);
                break;
            case ValType.F64:
                wasmValue = new Value(value.IsUndefined ? 0d : ObjectOps.ToNumber(isolate, value).Number);
                break;
            case ValType.V128:
                thrower.TypeError("A global of type 'v128' cannot be created in JavaScript");
                return default;
            default:
            {
                if (!type.IsNullable() && args.ArgcWithoutReceiver < 2)
                {
                    thrower.TypeError("Non-defaultable global needs initial value");
                }
                JSValue v = args.ArgcWithoutReceiver < 2 ? DefaultReferenceValue(type) : value;
                if (!engine.TryJSToWasmRef(v, type, null, out wasmValue, out string? error))
                {
                    thrower.TypeError(error!);
                }
                break;
            }
        }
        var globalType = new GlobalType(type, isMutable ? Mutability.Mutable : Mutability.Immutable);
        GlobalAddr address = engine.Runtime.AllocateGlobal(globalType, wasmValue);
        var globalObj = (WasmGlobalObject)JSObject.NewWithMap(isolate, isolate.NativeContext.WasmGlobalConstructor.InitialMap);
        globalObj.Address = address;
        globalObj.Global = engine.Store[address];
        engine.RegisterGlobalObject(globalObj);
        TransferPrototype(isolate, globalObj, args.Receiver);
        return globalObj;
    }

    static JSValue WebAssemblyGlobalGetValueCommon(Isolate isolate, in BuiltinArguments args, ErrorThrower thrower)
    {
        WasmGlobalObject receiver = ExtractThis<WasmGlobalObject>(args, thrower, "WebAssembly.Global");
        ValType type = receiver.Type;
        Value value = receiver.Global.Value;
        switch (type)
        {
            case ValType.I32:
                return JSValue.FromInt(value.Data.Int32);
            case ValType.I64:
                return BigInt.FromInt64(isolate, value.Data.Int64);
            case ValType.F32:
                return JSValue.FromNumber(value.Data.Float32);
            case ValType.F64:
                return JSValue.FromNumber(value.Data.Float64);
            case ValType.V128:
                thrower.TypeError("Can't get the value of s128 WebAssembly.Global");
                return default;
            default:
                return WasmObjectToJSReturnValue(isolate, value, type, receiver.Module, thrower);
        }
    }

    /// <summary>WebAssembly.Global.valueOf() -> num.</summary>
    static JSValue WebAssemblyGlobalValueOf(Isolate isolate, in BuiltinArguments args) =>
        WebAssemblyGlobalGetValueCommon(isolate, args, new ErrorThrower(isolate, "WebAssembly.Global.valueOf()"));

    /// <summary>get WebAssembly.Global.value -> num.</summary>
    static JSValue WebAssemblyGlobalGetValue(Isolate isolate, in BuiltinArguments args) =>
        WebAssemblyGlobalGetValueCommon(isolate, args, new ErrorThrower(isolate, "get WebAssembly.Global.value)"));

    /// <summary>set WebAssembly.Global.value(num).</summary>
    static JSValue WebAssemblyGlobalSetValue(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "set WebAssembly.Global.value)");
        WasmGlobalObject receiver = ExtractThis<WasmGlobalObject>(args, thrower, "WebAssembly.Global");
        if (!receiver.IsMutable)
        {
            thrower.TypeError("Can't set the value of an immutable global.");
        }
        JSValue value = args.AtOrUndefined(1);
        ValType type = receiver.Type;
        switch (type)
        {
            case ValType.I32:
                receiver.Global.Value = new Value((int)ObjectOps.ToInt32(isolate, value).Number);
                break;
            case ValType.I64:
                receiver.Global.Value = new Value(BigInt.AsInt64(BigInt.FromObject(isolate, value), out _));
                break;
            case ValType.F32:
                receiver.Global.Value = new Value((float)ObjectOps.ToNumber(isolate, value).Number);
                break;
            case ValType.F64:
                receiver.Global.Value = new Value(ObjectOps.ToNumber(isolate, value).Number);
                break;
            case ValType.V128:
                thrower.TypeError("Can't set the value of s128 WebAssembly.Global");
                break;
            default:
            {
                if (!WasmEngine.Get(isolate).TryJSToWasmRef(value, type, receiver.Module, out Value wasmValue,
                        out string? error))
                {
                    thrower.TypeError(error!);
                }
                receiver.Global.Value = wasmValue;
                break;
            }
        }
        return JSValue.Undefined;
    }

    // ---- WebAssembly.Tag / Exception --------------------------------------------------

    /// <summary>GetIterableLength: ToArrayIndex of the "length" property, or uint.MaxValue.</summary>
    static uint GetIterableLength(Isolate isolate, JSReceiver iterable)
    {
        JSValue property = ObjectOps.GetProperty(isolate, iterable, ReadOnlyRoots.length_string);
        JSValue number = ObjectOps.ToNumber(isolate, property);
        double d = number.Number;
        if (d < 0 || d >= uint.MaxValue || d != Math.Floor(d)) return uint.MaxValue;
        return (uint)d;
    }

    /// <summary>new WebAssembly.Tag(type).</summary>
    static JSValue WebAssemblyTag(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Tag()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Tag must be invoked with 'new'");
        }
        if (!args.AtOrUndefined(1).IsJSReceiver)
        {
            thrower.TypeError("Argument 0 must be a tag type");
        }
        var eventType = (JSReceiver)args.AtOrUndefined(1).Object;
        JSValue parametersValue = ObjectOps.GetProperty(isolate, eventType, isolate.Factory.InternalizeString("parameters"));
        if (!parametersValue.IsJSReceiver)
        {
            thrower.TypeError("Argument 0 must be a tag type with 'parameters'");
        }
        var parameters = (JSReceiver)parametersValue.Object;
        uint parametersLen = GetIterableLength(isolate, parameters);
        if (parametersLen == uint.MaxValue)
        {
            thrower.TypeError("Argument 0 contains parameters without 'length'");
        }
        if (parametersLen > WasmLimits.kV8MaxWasmFunctionParams)
        {
            thrower.TypeError("Argument 0 contains too many parameters");
        }
        var paramTypes = new ValType[parametersLen];
        for (uint i = 0; i < parametersLen; ++i)
        {
            JSValue element = ObjectOps.GetPropertyOrElement(isolate, parameters, new PropertyKey(isolate, (double)i));
            ValType? type = GetValueType(isolate, element);
            if (type is null)
            {
                thrower.TypeError($"Argument 0 parameter type at index #{i} must be a value type");
            }
            paramTypes[i] = type!.Value;
        }
        var signature = new FunctionType(new ResultType(paramTypes), ResultType.Empty);
        WasmEngine engine = WasmEngine.Get(isolate);
        TagAddr address = engine.Runtime.AllocateTag(WasmEngine.NewFunctionDefType(signature));
        var tagObject = (WasmTagObject)JSObject.NewWithMap(isolate, isolate.NativeContext.WasmTagConstructor.InitialMap);
        tagObject.Address = address;
        tagObject.Tag = engine.Store[address];
        tagObject.Signature = signature;
        engine.RegisterTagObject(tagObject);
        return tagObject;
    }

    /// <summary>ToI32/ToI64/ToF32/ToF64 of wasm-js.cc: undefined is 0.</summary>
    static Value EncodeNumeric(Isolate isolate, JSValue value, ValType type) => type switch
    {
        ValType.I32 => new Value(value.IsUndefined ? 0 : (int)ObjectOps.ToInt32(isolate, value).Number),
        ValType.I64 => new Value(value.IsUndefined ? 0L : BigInt.AsInt64(BigInt.FromObject(isolate, value), out _)),
        ValType.F32 => new Value(value.IsUndefined ? 0f : (float)ObjectOps.ToNumber(isolate, value).Number),
        _ => new Value(value.IsUndefined ? 0d : ObjectOps.ToNumber(isolate, value).Number),
    };

    /// <summary>new WebAssembly.Exception(tag, values, options).</summary>
    static JSValue WebAssemblyException(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Exception()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Exception must be invoked with 'new'");
        }
        if (args.AtOrUndefined(1).HeapObjectOrNull is not WasmTagObject tagObject)
        {
            thrower.TypeError("Argument 0 must be a WebAssembly tag");
            return default;
        }
        WasmEngine engine = WasmEngine.Get(isolate);
        if (tagObject.Address.Equals(engine.JSTag))
        {
            thrower.TypeError("Argument 0 cannot be WebAssembly.JSTag");
        }
        FunctionType signature = tagObject.Signature;
        if (signature.ResultType.Arity != 0)
        {
            thrower.TypeError("Invalid WebAssembly tag (return values not permitted in Exception tag)");
        }

        // EncodeExceptionValues.
        JSValue arg = args.AtOrUndefined(2);
        if (!arg.IsJSReceiver)
        {
            thrower.TypeError("Exception values must be an iterable object");
        }
        var values = (JSReceiver)arg.Object;
        uint length = GetIterableLength(isolate, values);
        if (length == uint.MaxValue)
        {
            thrower.TypeError("Exception values argument has no length");
        }
        ValType[] paramTypes = signature.ParameterTypes.Types;
        if (length != paramTypes.Length)
        {
            thrower.TypeError("Number of exception values does not match signature length");
        }
        var encoded = new Value[paramTypes.Length];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            JSValue value = ObjectOps.GetPropertyOrElement(isolate, values, new PropertyKey(isolate, (double)i));
            ValType type = paramTypes[i];
            switch (type)
            {
                case ValType.I32:
                case ValType.I64:
                case ValType.F32:
                case ValType.F64:
                    encoded[i] = EncodeNumeric(isolate, value, type);
                    break;
                case ValType.V128:
                    thrower.TypeError("Invalid type v128");
                    break;
                default:
                    if (!engine.TryJSToWasmRef(value, type, tagObject.Module, out encoded[i], out string? error))
                    {
                        thrower.TypeError(error!);
                    }
                    break;
            }
        }

        Value exnRef = engine.NewException(tagObject.Address, encoded);
        var runtimeException = (WasmExceptionPackage)JSObject.NewWithMap(isolate,
            isolate.NativeContext.WasmExceptionConstructor.InitialMap);
        runtimeException.ExnRef = exnRef;
        runtimeException.TagObject = tagObject;
        engine.RegisterExceptionPackage(runtimeException);

        // Third argument: optional ExceptionOption ({traceStack: <bool>}).
        JSValue options = args.AtOrUndefined(3);
        if (!options.IsNullOrUndefined && !options.IsJSReceiver)
        {
            thrower.TypeError("Argument 2 is not an object");
        }
        if (options.IsJSReceiver)
        {
            JSValue traceStack = ObjectOps.GetProperty(isolate, options, isolate.Factory.InternalizeString("traceStack"));
            if (ObjectOps.BooleanValue(traceStack))
            {
                ErrorUtils.CaptureStackTrace(isolate, runtimeException, FrameSkipMode.SKIP_NONE, args.NewTarget);
            }
        }
        return runtimeException;
    }

    /// <summary>WebAssembly.Exception.prototype.getArg(tag, index).</summary>
    static JSValue WebAssemblyExceptionGetArg(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Exception.getArg()");
        WasmExceptionPackage exception = ExtractThis<WasmExceptionPackage>(args, thrower, "WebAssembly.Exception");
        WasmTagObject tagObject = GetFirstArgumentAsTag(args, thrower);
        uint index = EnforceUint32(isolate, "Index", args.AtOrUndefined(2), thrower);
        if (!tagObject.Address.Equals(exception.Exn.Tag))
        {
            thrower.TypeError("First argument does not match the exception tag");
        }
        ValType[] paramTypes = tagObject.Signature.ParameterTypes.Types;
        if (index >= paramTypes.Length)
        {
            thrower.RangeError("Index out of range");
        }
        Value[] values = WasmEngine.ExceptionValues(exception.Exn);
        ValType type = paramTypes[index];
        Value value = values[index];
        switch (type)
        {
            case ValType.I32:
                return JSValue.FromInt(value.Data.Int32);
            case ValType.I64:
                return BigInt.FromInt64(isolate, value.Data.Int64);
            case ValType.F32:
                return JSValue.FromNumber(value.Data.Float32);
            case ValType.F64:
                return JSValue.FromNumber(value.Data.Float64);
            case ValType.V128:
                thrower.TypeError("Invalid type v128");
                return default;
            default:
                return WasmObjectToJSReturnValue(isolate, value, type, tagObject.Module, thrower);
        }
    }

    /// <summary>WebAssembly.Exception.prototype.is(tag).</summary>
    static JSValue WebAssemblyExceptionIs(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Exception.is()");
        WasmExceptionPackage exception = ExtractThis<WasmExceptionPackage>(args, thrower, "WebAssembly.Exception");
        WasmTagObject tagObject = GetFirstArgumentAsTag(args, thrower);
        return JSValue.FromBoolean(tagObject.Address.Equals(exception.Exn.Tag));
    }

    /// <summary>WebAssembly.Exception.prototype.stack getter.</summary>
    static JSValue WebAssemblyExceptionGetStack(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Exception.stack()");
        ExtractThis<WasmExceptionPackage>(args, thrower, "WebAssembly.Exception");
        return JSValue.Undefined;
    }

    // ---- JSPI: WebAssembly.Suspending / promising -----------------------------------------

    /// <summary>new WebAssembly.Suspending(callable).</summary>
    static JSValue WebAssemblySuspending(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.Suspending()");
        if (!args.IsConstructCall)
        {
            thrower.TypeError("WebAssembly.Suspending must be invoked with 'new'");
        }
        JSValue callable = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(callable))
        {
            thrower.TypeError("Argument 0 must be a function");
        }
        if (WasmObjects.IsWasmExportedFunction(callable))
        {
            thrower.TypeError("Argument 0 must not be a WebAssembly function");
        }
        var result = (WasmSuspendingObject)JSObject.NewWithMap(isolate,
            isolate.NativeContext.WasmSuspendingConstructor.InitialMap);
        result.Callable = (JSReceiver)callable.Object;
        return result;
    }

    /// <summary>
    /// WebAssembly.promising(wasm_func): a function that calls wasm_func and
    /// returns a promise of its result. V8Sharp cannot suspend the wasm stack
    /// (deviations.md): a Suspending import that returns a promise rejects
    /// the call with SuspendError.
    /// </summary>
    static JSValue WebAssemblyPromising(Isolate isolate, in BuiltinArguments args)
    {
        var thrower = new ErrorThrower(isolate, "WebAssembly.promising()");
        JSValue arg = args.AtOrUndefined(1);
        if (!ObjectOps.IsCallable(arg))
        {
            thrower.TypeError("Argument 0 must be a function");
        }
        if (WasmObjects.GetExportedFunctionData(arg) is not { } data)
        {
            thrower.TypeError("Argument 0 must be a WebAssembly exported function");
            return default;
        }
        var promisingData = new WasmExportedFunctionData(CallPromisingFunction)
        {
            Length = data.Length,
            Engine = data.Engine,
            Address = data.Address,
            Signature = data.Signature,
            FunctionIndex = data.FunctionIndex,
            Instance = data.Instance,
            IsPromising = true,
        };
        JSString name = ((JSFunction)arg.Object).Shared.Name();
        SharedFunctionInfo info = isolate.Factory.NewSharedFunctionInfo(name, promisingData,
            Builtin.HandleApiCallOrConstruct, data.Length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        return isolate.Factory.NewFunction(info, isolate.NativeContext,
            isolate.NativeContext.SloppyFunctionWithoutPrototypeMap);
    }
}

/// <summary>The memory parts of src/wasm/wasm-objects.cc (WasmMemoryObject).</summary>
public static class WasmMemoryObjectOps
{
    /// <summary>WasmMemoryObject::New: null when the memory cannot be allocated.</summary>
    public static WasmMemoryObject? New(Isolate isolate, ulong initial, ulong? maximum, bool shared, bool isI64)
    {
        WasmEngine engine = WasmEngine.Get(isolate);
        ulong engineMax = WasmLimits.MaxMemoryPages(isI64);
        if (initial > engineMax) return null;
        var limits = new Limits(isI64 ? AddrType.I64 : AddrType.I32, (long)initial,
            maximum is { } m ? (long)m : null)
        {
            Shared = shared,
        };
        MemAddr address;
        try
        {
            address = engine.Runtime.AllocateMemory(new MemoryType(limits));
        }
        catch (Exception e) when (e is OutOfMemoryException or Wacs.Core.Runtime.Exceptions.InstantiationException or OverflowException)
        {
            return null;
        }
        return engine.GetOrCreateMemoryObject(address);
    }

    /// <summary>WasmMemoryObject::GetArrayBuffer: the current buffer, created lazily.</summary>
    public static JSArrayBuffer GetArrayBuffer(Isolate isolate, WasmMemoryObject memory)
    {
        if (memory.ArrayBuffer is { } buffer && !buffer.WasDetached &&
            (memory.IsShared ? buffer.GetByteLength() == (ulong)memory.Memory.ByteLength : true))
        {
            return buffer;
        }
        return memory.ArrayBuffer = NewBuffer(isolate, memory, resizable: false);
    }

    static JSArrayBuffer NewBuffer(Isolate isolate, WasmMemoryObject memory, bool resizable)
    {
        MemoryInstance mem = memory.Memory;
        bool shared = memory.IsShared;
        BackingStore store = BackingStore.WrapWasmMemory(mem.Data, (ulong)mem.ByteLength, shared);
        JSArrayBuffer buffer = shared ? isolate.Factory.NewJSSharedArrayBuffer(store) : isolate.Factory.NewJSArrayBuffer(store);
        // Wasm memory buffers are not detachable from JavaScript.
        buffer.IsDetachable = false;
        memory.BufferIsResizable = resizable;
        return buffer;
    }

    /// <summary>
    /// The memory grew (memory.grow in wasm, or Memory.prototype.grow):
    /// WasmMemoryObject::RefreshBuffer detaches the old buffer of a
    /// non-shared memory; a shared memory's old SharedArrayBuffer keeps its length.
    /// </summary>
    public static void OnMemoryGrown(Isolate isolate, WasmMemoryObject memory)
    {
        if (memory.ArrayBuffer is not { } old) return;
        if (!memory.IsShared)
        {
            JSArrayBuffer.Detach(isolate, old, forceForWasmMemory: true);
        }
        memory.ArrayBuffer = null;
    }

    /// <summary>WasmMemoryObject::Grow: the old size in pages, or -1.</summary>
    public static long Grow(Isolate isolate, WasmMemoryObject memory, ulong deltaPages)
    {
        long oldPages = memory.Memory.Size;
        if (deltaPages > WasmLimits.MaxMemoryPages(memory.IsMemory64) - (ulong)oldPages) return -1;
        bool grown;
        try
        {
            grown = memory.Memory.Grow((long)deltaPages);
        }
        catch (OutOfMemoryException)
        {
            grown = false;
        }
        if (!grown) return -1;
        // MemoryInstance.OnGrow refreshed the buffer; V8 also refreshes it
        // when the size did not change (grow(0) of a non-shared memory).
        if (deltaPages == 0 && !memory.IsShared && memory.ArrayBuffer is { } old)
        {
            JSArrayBuffer.Detach(isolate, old, forceForWasmMemory: true);
            memory.ArrayBuffer = null;
        }
        return oldPages;
    }

    /// <summary>
    /// WasmMemoryObject::ChangeArrayBufferResizability. A resizable buffer
    /// over a managed memory cannot alias it across growth, so V8Sharp hands
    /// out a fixed-length buffer whose resizability is recorded only
    /// (deviations.md).
    /// </summary>
    public static JSArrayBuffer ChangeArrayBufferResizability(Isolate isolate, WasmMemoryObject memory, bool resizable)
    {
        JSArrayBuffer current = GetArrayBuffer(isolate, memory);
        if (memory.BufferIsResizable == resizable) return current;
        if (!memory.IsShared) JSArrayBuffer.Detach(isolate, current, forceForWasmMemory: true);
        return memory.ArrayBuffer = NewBuffer(isolate, memory, resizable);
    }
}
