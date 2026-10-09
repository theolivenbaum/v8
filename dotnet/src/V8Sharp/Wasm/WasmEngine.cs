// The per-isolate WebAssembly engine: V8's WasmEngine (src/wasm/wasm-engine.cc,
// module compilation), the store of the isolate's wasm objects, and the value
// conversions of src/wasm/wasm-objects.cc (JSToWasmObject, WasmToJSObject)
// and of the JS-API's ToWebAssemblyValue / ToJSValue.
//
// V8 has one process-wide WasmEngine and compiles modules to machine code;
// V8Sharp gives each isolate a WACS runtime (V8Sharp.Wasm), whose store holds
// every function, memory, table, global and tag the isolate creates, and runs
// the code in WACS's interpreter (deviations.md, "WebAssembly").
using System.Runtime.CompilerServices;
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Validation;
using WasmModule = Wacs.Core.Module;

namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>The isolate's wasm engine, created on first use.</summary>
        internal Wasm.WasmEngine? WasmEngineField;
    }
}

namespace V8Sharp.Wasm
{
    /// <summary>A wasm reference to a JavaScript value (externref, or anyref holding a host value).</summary>
    public sealed class JSExternRef : IGcRef
    {
        public JSExternRef(JSValue value, long id)
        {
            Value = value;
            Index = new PtrIdx(id);
        }

        public JSValue Value { get; }
        public PtrIdx Index { get; }
        public RefIdx StoreIndex => Index;
    }

    /// <summary>A module failed to decode or validate (WebAssembly.CompileError).</summary>
    public sealed class WasmCompileException(string message) : Exception(message);

    public sealed class WasmEngine
    {
        // Wasm externrefs of distinct JS values must have distinct addresses;
        // they start far above any store index.
        const long kFirstExternRefId = 1L << 40;

        readonly Dictionary<int, JSFunction> _exportedFunctions = new();
        readonly Dictionary<int, WasmMemoryObject> _memoryObjects = new();
        readonly Dictionary<int, WasmTableObject> _tableObjects = new();
        readonly Dictionary<int, WasmGlobalObject> _globalObjects = new();
        readonly Dictionary<int, WasmTagObject> _tagObjects = new();
        readonly ConditionalWeakTable<HeapObject, JSExternRef> _externRefs = new();
        readonly ConditionalWeakTable<IGcRef, WasmGCObjectWrapper> _gcWrappers = new();
        readonly ConditionalWeakTable<ExnInstance, WasmExceptionPackage> _exceptionPackages = new();
        long _nextExternRefId = kFirstExternRefId;
        TagAddr? _jsTag;

        WasmEngine(Isolate isolate)
        {
            Isolate = isolate;
            var attributes = new RuntimeAttributes
            {
                // V8 limits recursion by the machine stack (about 1 MB); WACS
                // by frame and operand counts.
                MaxCallStack = 32768,
                MaxOpStack = 1 << 17,
                // memory.atomic.wait/notify share V8's futex wait list with
                // Atomics.wait/notify.
                ConcurrencyPolicy = WasmFutexPolicy.Instance,
            };
            Runtime = new WasmRuntime(attributes)
            {
                InterruptPoll = () =>
                {
                    if (isolate.StackGuard.HasPendingInterrupts) isolate.StackGuard.HandleInterrupts();
                },
            };
        }

        public Isolate Isolate { get; }

        /// <summary>
        /// The embedder disallows wasm code generation (%DisallowWasmCodegen,
        /// v8::Isolate::SetAllowWasmCodeGenerationCallback).
        /// </summary>
        public bool CodegenDisallowed { get; set; }

        public WasmRuntime Runtime { get; }

        ExecContext? _execContext;

        /// <summary>The runtime's execution context on the isolate's thread.</summary>
        public ExecContext ExecContext => _execContext ??= Runtime.GetExecContext();

        // ---- Compiled code (V8: the code tables of the wasm code manager) -------------

        WasmCode?[] _code = new WasmCode?[64];

        internal void RegisterCode(WasmCode code)
        {
            int address = code.Address.Value;
            if (address >= _code.Length) Array.Resize(ref _code, Math.Max(address + 1, _code.Length * 2));
            _code[address] = code;
        }

        internal WasmCode? FindCode(FuncAddr address) =>
            (uint)address.Value < (uint)_code.Length ? _code[address.Value] : null;

        /// <summary>The code of the function at <paramref name="address"/>, created on first use.</summary>
        internal WasmCode CodeAt(FuncAddr address)
        {
            if ((uint)address.Value < (uint)_code.Length && _code[address.Value] is { } code) return code;
            return GetOrCreateCode(address);
        }

        internal WasmCode GetOrCreateCode(FuncAddr address)
        {
            if (FindCode(address) is { } existing) return existing;
            IFunctionInstance function = Store[address];
            if (function is FunctionInstance { Module.Compiler: WasmInstanceData data } && FindCode(address) is { } owned)
            {
                return owned;
            }
            var code = new WasmCode(this, null, function, address, -1);
            RegisterCode(code);
            return code;
        }

        // ---- Activations (stack traces) -----------------------------------------------

        /// <summary>A JS-to-wasm call in progress: the wasm frames above its base height are its own.</summary>
        internal sealed class Activation
        {
            public int BaseHeight;
            /// <summary>The instruction the enclosing activation was executing when this one began.</summary>
            public int OuterPc;
            /// <summary>The frames a trap unwound, while its error is created.</summary>
            public WasmStackFrame[]? TrapFrames;
        }

        readonly List<Activation> _activations = [];
        readonly ConditionalWeakTable<ModuleInstance, WasmInstanceObject> _instanceObjects = new();

        internal Activation EnterActivation()
        {
            var activation = new Activation
            {
                BaseHeight = ExecContext.UnifiedHeight,
                OuterPc = ExecContext.InstructionPointer,
            };
            _activations.Add(activation);
            return activation;
        }

        internal void LeaveActivation(Activation activation)
        {
            int index = _activations.LastIndexOf(activation);
            if (index >= 0) _activations.RemoveAt(index);
        }

        internal Activation? CurrentActivation => _activations.Count > 0 ? _activations[^1] : null;

        /// <summary>The wasm frames of an activation, top first (0 is the innermost).</summary>
        internal WasmStackFrame[] ActivationFrames(int activationFromTop)
        {
            int index = _activations.Count - 1 - activationFromTop;
            if (index < 0) return [];
            Activation activation = _activations[index];
            if (activation.TrapFrames is { } trapFrames) return trapFrames;
            bool innermost = index == _activations.Count - 1;
            int topHeight = innermost ? ExecContext.UnifiedHeight : _activations[index + 1].BaseHeight;
            int topPc = innermost ? ExecContext.InstructionPointer : _activations[index + 1].OuterPc;
            return ExecContext.SnapshotFrames(activation.BaseHeight, topHeight, topPc);
        }

        internal void RegisterInstanceObject(WasmInstanceObject instance) =>
            _instanceObjects.AddOrUpdate(instance.Instance, instance);

        internal WasmInstanceObject? InstanceObjectFor(ModuleInstance module) =>
            _instanceObjects.TryGetValue(module, out WasmInstanceObject? instance) ? instance : null;

        public Store Store => Runtime.RuntimeStore;

        /// <summary>The isolate's engine.</summary>
        public static WasmEngine Get(Isolate isolate) => isolate.WasmEngineField ??= new WasmEngine(isolate);

        // ---- Compilation (WasmEngine::SyncCompile, SyncValidate) ----------------------

        /// <summary>
        /// Decodes and validates <paramref name="bytes"/>. Throws
        /// <see cref="WasmCompileException"/> with the reason on failure.
        /// </summary>
        /// <summary>wasm-limits.h kV8MaxWasmFunctionLocals.</summary>
        const int kV8MaxWasmFunctionLocals = 50_000;

        static WasmEngine() => BinaryModuleParser.MaximumFunctionLocals = kV8MaxWasmFunctionLocals;

        public static WasmModule Compile(byte[] bytes, CompileTimeImports? imports = null)
        {
            WasmModule module;
            try
            {
                module = BinaryModuleParser.ParseWasm(new MemoryStream(bytes, writable: false));
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or InvalidDataException
                                          or EndOfStreamException or OverflowException or ArgumentException
                                          or InvalidOperationException or IndexOutOfRangeException
                                          or InvalidCastException or ValidationException or KeyNotFoundException)
            {
                throw new WasmCompileException(WasmErrorMessages.DecodeError(bytes, e));
            }
            try
            {
                module.ValidateAndThrow(new RuntimeAttributes
                {
                    AllowMixedExceptionHandling = Isolate.Current?.Flags.wasm_allow_mixed_eh_for_testing == true,
                    // V8 allows atomics on unshared memories and up to
                    // kV8MaxWasmFunctionLocals locals.
                    RelaxAtomicSharedCheck = true,
                    MaxFunctionLocals = kV8MaxWasmFunctionLocals,
                });
            }
            catch (Exception e) when (e is ValidationException or InvalidDataException or FormatException
                                          or NotSupportedException or InvalidOperationException
                                          or IndexOutOfRangeException or ArgumentException or InvalidCastException
                                          or KeyNotFoundException or NullReferenceException)
            {
                throw new WasmCompileException(WasmErrorMessages.ValidationError(e, bytes));
            }
            if (WasmStringBuiltins.ValidateImports(module, bytes, imports) is { } importError)
            {
                throw new WasmCompileException(importError);
            }
            return module;
        }

        /// <summary>WasmEngine::SyncValidate.</summary>
        public static bool Validate(byte[] bytes, CompileTimeImports? imports = null)
        {
            try
            {
                Compile(bytes, imports);
                return true;
            }
            catch (WasmCompileException)
            {
                return false;
            }
        }

        // ---- Types -----------------------------------------------------------------

        /// <summary>A defined function type outside any module (WebAssembly.Tag, JSTag).</summary>
        public static DefType NewFunctionDefType(FunctionType signature)
        {
            var rec = new RecursiveType(new SubType(signature, final: true)) { DefIndex = (TypeIdx)0 };
            var def = new DefType(rec, 0, (TypeIdx)0);
            var defs = new List<DefType> { def };
            rec.ComputeHash(defs);
            def.ComputeHash();
            def.SuperTypes = new List<DefType>();
            return def;
        }

        /// <summary>The tag of WebAssembly.JSTag (a JS exception caught in wasm), one per isolate.</summary>
        public TagAddr JSTag
        {
            get
            {
                if (_jsTag is { } tag) return tag;
                var signature = new FunctionType(new ResultType(ValType.ExternRef), ResultType.Empty);
                tag = Runtime.AllocateTag(NewFunctionDefType(signature));
                _jsTag = tag;
                return tag;
            }
        }

        // ---- Exported functions (WasmInternalFunction::GetOrCreateExternal) ----------------

        /// <summary>
        /// The JS function of the wasm function at <paramref name="address"/>,
        /// created once per function (V8: the external of its WasmInternalFunction).
        /// Its name is the function index, its length the parameter count.
        /// </summary>
        public JSFunction GetOrCreateExportedFunction(FuncAddr address, int functionIndex, WasmInstanceObject? instance)
        {
            if (_exportedFunctions.TryGetValue(address.Value, out JSFunction? existing)) return existing;
            Isolate isolate = Isolate;
            IFunctionInstance func = Store[address];
            FunctionType signature = func.Type;
            int length = signature.ParameterTypes.Arity;
            var data = new WasmExportedFunctionData(WasmJs.CallExportedFunction)
            {
                Length = length,
                Engine = this,
                Address = address,
                Signature = signature,
                FunctionIndex = functionIndex,
                Instance = instance,
            };
            JSString name = isolate.Factory.InternalizeString(functionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SharedFunctionInfo info = isolate.Factory.NewSharedFunctionInfo(name, data, Builtin.HandleApiCallOrConstruct, length, false);
            info.BuiltinId = Builtin.HandleApiCallOrConstruct;
            info.LanguageMode = LanguageMode.Strict;
            info.Native = true;
            info.UpdateFunctionMapIndex();
            // WasmJs::PrepareForSnapshot: "make all exported functions an
            // instance of {Function}" with the sloppy map without prototype.
            JSFunction function = isolate.Factory.NewFunction(info, isolate.NativeContext,
                isolate.NativeContext.SloppyFunctionWithoutPrototypeMap);
            _exportedFunctions[address.Value] = function;
            return function;
        }

        /// <summary>The JS function of a funcref value (WasmToJSObject of a WasmFuncRef).</summary>
        public JSFunction FuncRefToJS(Value value)
        {
            var address = new FuncAddr((int)value.Data.Ptr);
            if (_exportedFunctions.TryGetValue(address.Value, out JSFunction? existing)) return existing;
            int index = Store[address] is FunctionInstance f ? (int)f.Index.Value : address.Value;
            return GetOrCreateExportedFunction(address, index, null);
        }

        // ---- JS objects of store addresses ------------------------------------------

        public WasmMemoryObject GetOrCreateMemoryObject(MemAddr address)
        {
            if (_memoryObjects.TryGetValue(address.Value, out WasmMemoryObject? existing)) return existing;
            var obj = (WasmMemoryObject)JSObject.NewWithMap(Isolate, Isolate.NativeContext.WasmMemoryConstructor.InitialMap);
            obj.Address = address;
            obj.Memory = Store[address];
            // A shared memory may grow on another isolate's thread; its buffer
            // is refreshed when read (GetArrayBuffer compares the length).
            if (!obj.IsShared) obj.Memory.OnGrow = (_, _) => WasmMemoryObjectOps.OnMemoryGrown(Isolate, obj);
            _memoryObjects[address.Value] = obj;
            return obj;
        }

        public WasmTableObject GetOrCreateTableObject(TableAddr address, ModuleInstance? module)
        {
            if (_tableObjects.TryGetValue(address.Value, out WasmTableObject? existing)) return existing;
            var obj = (WasmTableObject)JSObject.NewWithMap(Isolate, Isolate.NativeContext.WasmTableConstructor.InitialMap);
            obj.Address = address;
            obj.Table = Store[address];
            obj.Module = module;
            _tableObjects[address.Value] = obj;
            return obj;
        }

        public WasmGlobalObject GetOrCreateGlobalObject(GlobalAddr address, ModuleInstance? module)
        {
            if (_globalObjects.TryGetValue(address.Value, out WasmGlobalObject? existing)) return existing;
            var obj = (WasmGlobalObject)JSObject.NewWithMap(Isolate, Isolate.NativeContext.WasmGlobalConstructor.InitialMap);
            obj.Address = address;
            obj.Global = Store[address];
            obj.Module = module;
            _globalObjects[address.Value] = obj;
            return obj;
        }

        public WasmTagObject GetOrCreateTagObject(TagAddr address, ModuleInstance? module)
        {
            if (_tagObjects.TryGetValue(address.Value, out WasmTagObject? existing)) return existing;
            var obj = (WasmTagObject)JSObject.NewWithMap(Isolate, Isolate.NativeContext.WasmTagConstructor.InitialMap);
            obj.Address = address;
            obj.Tag = Store[address];
            obj.Signature = (FunctionType)obj.Tag.Type.Expansion;
            obj.Module = module;
            _tagObjects[address.Value] = obj;
            return obj;
        }

        internal void RegisterTagObject(WasmTagObject tag) => _tagObjects[tag.Address.Value] = tag;

        internal void RegisterMemoryObject(WasmMemoryObject memory) => _memoryObjects[memory.Address.Value] = memory;

        internal void RegisterTableObject(WasmTableObject table) => _tableObjects[table.Address.Value] = table;

        internal void RegisterGlobalObject(WasmGlobalObject global) => _globalObjects[global.Address.Value] = global;

        // ---- Value conversions ---------------------------------------------------------

        /// <summary>A wasm reference to a JS value (externref).</summary>
        public Value ExternRefFor(JSValue value, ValType type)
        {
            if (value.IsNull) return Value.Null(ValType.ExternRef);
            // V8 keeps the JS value itself: a wasm GC object stays that object
            // and a Smi-range number becomes an i31ref under any.convert_extern
            // (CanonicalizeSmi), so both keep their identity as anyref.
            if (value.HeapObjectOrNull is WasmGCObjectWrapper gcObject)
            {
                Value gcRef = gcObject.Ref;
                gcRef.Type = type;
                return gcRef;
            }
            if (TryI31(value, out Value i31))
            {
                i31.Type = type;
                return i31;
            }
            // Strings are the JS String Builtins' operands: a .NET string the
            // builtins read directly (a JS string's identity is its value).
            if (value.IsString)
            {
                return new Value(type, 0L, new Wacs.Core.Runtime.Builtins.JsStringRef(ObjectOps.ToString(Isolate, value).ToString()));
            }
            JSExternRef reference;
            if (value.HeapObjectOrNull is { } heapObject && !value.IsNumber)
            {
                reference = _externRefs.GetValue(heapObject, o => new JSExternRef(JSValue.FromObject(o), _nextExternRefId++));
            }
            else
            {
                reference = new JSExternRef(value, _nextExternRefId++);
            }
            return new Value(type, reference.Index.Value, reference);
        }

        /// <summary>
        /// JSToWasmObject (wasm-objects.cc): converts a JS value to a wasm
        /// reference of type <paramref name="expected"/>, or fails with V8's
        /// error message.
        /// </summary>
        public bool TryJSToWasmRef(JSValue value, ValType expected, ModuleInstance? module, out Value result,
            out string? errorMessage)
        {
            errorMessage = null;
            result = default;
            bool nullable = expected.IsNullable();
            if (nullable && value.IsNull)
            {
                switch (TopKind(expected, module))
                {
                    case HeapKind.Exn:
                        errorMessage = expected.IsDefType() ? "invalid type" : "invalid type (ref null exn)";
                        return false;
                    case HeapKind.Cont:
                        errorMessage = "invalid type (ref null cont)";
                        return false;
                }
                result = Value.Null(NullTypeFor(expected, module));
                return true;
            }

            if (expected.IsDefType())
            {
                DefType def = module!.Types[expected.Index()];
                if (WasmObjects.GetExportedFunctionData(value) is { } data)
                {
                    if (def.Expansion is not FunctionType expectedSig || !FunctionMatches(data.Address, def, module))
                    {
                        errorMessage = "assigned exported function has to be a subtype of the expected type";
                        return false;
                    }
                    result = new Value(ValType.FuncRef, data.Address.Value);
                    return true;
                }
                if (value.HeapObjectOrNull is WasmGCObjectWrapper gc)
                {
                    if (!expected.Matches(gc.Ref, module.Types))
                    {
                        errorMessage = "object is not a subtype of expected type";
                        return false;
                    }
                    result = gc.Ref;
                    return true;
                }
                errorMessage = "JS object does not match expected wasm type";
                return false;
            }

            switch (expected.GetHeapType())
            {
                case HeapType.Func:
                case HeapType.NoFunc when false:
                    if (WasmObjects.GetExportedFunctionData(value) is { } fdata)
                    {
                        result = new Value(ValType.FuncRef, fdata.Address.Value);
                        return true;
                    }
                    errorMessage = "function-typed object must be null (if nullable) or a Wasm function object";
                    return false;
                case HeapType.Extern:
                    if (!value.IsNull)
                    {
                        result = ExternRefFor(value, nullable ? ValType.ExternRef : ValType.Extern);
                        return true;
                    }
                    errorMessage = "null is not allowed for (ref extern)";
                    return false;
                case HeapType.Any:
                    if (TryI31(value, out result)) return true;
                    if (value.HeapObjectOrNull is WasmGCObjectWrapper anyGc)
                    {
                        result = anyGc.Ref;
                        return true;
                    }
                    if (!value.IsNull)
                    {
                        result = ExternRefFor(value, nullable ? ValType.Any : ValType.AnyNN);
                        return true;
                    }
                    errorMessage = "null is not allowed for (ref any)";
                    return false;
                case HeapType.Exn:
                    errorMessage = "invalid type (ref exn)";
                    return false;
                case HeapType.Cont:
                    errorMessage = "invalid type (ref cont)";
                    return false;
                case HeapType.Struct:
                    if (value.HeapObjectOrNull is WasmGCObjectWrapper { Ref.GcRef: Wacs.Core.Runtime.GC.StoreStruct } s)
                    {
                        result = s.Ref;
                        return true;
                    }
                    errorMessage = "structref object must be null (if nullable) or a wasm struct";
                    return false;
                case HeapType.Array:
                    if (value.HeapObjectOrNull is WasmGCObjectWrapper { Ref.GcRef: Wacs.Core.Runtime.StoreArray } a)
                    {
                        result = a.Ref;
                        return true;
                    }
                    errorMessage = "arrayref object must be null (if nullable) or a wasm array";
                    return false;
                case HeapType.Eq:
                    if (TryI31(value, out result)) return true;
                    if (value.HeapObjectOrNull is WasmGCObjectWrapper eq)
                    {
                        result = eq.Ref;
                        return true;
                    }
                    errorMessage = "eqref object must be null (if nullable), or a wasm struct/array, or a Number that fits in i31ref range";
                    return false;
                case HeapType.I31:
                    if (TryI31(value, out result)) return true;
                    errorMessage = "i31ref object must be null (if nullable) or a Number that fits in i31ref range";
                    return false;
                case HeapType.None:
                case HeapType.NoFunc:
                case HeapType.NoExtern:
                case HeapType.NoExn:
                    errorMessage = "only null allowed for null types";
                    return false;
                default:
                    errorMessage = "invalid type";
                    return false;
            }
        }

        enum HeapKind { Other, Exn, Cont }

        static HeapKind TopKind(ValType type, ModuleInstance? module)
        {
            if (type.IsDefType())
            {
                return module?.Types[type.Index()].Expansion is ContType ? HeapKind.Cont : HeapKind.Other;
            }
            return type.GetHeapType() switch
            {
                HeapType.Exn or HeapType.NoExn => HeapKind.Exn,
                HeapType.Cont or HeapType.NoCont => HeapKind.Cont,
                _ => HeapKind.Other,
            };
        }

        static ValType NullTypeFor(ValType type, ModuleInstance? module)
        {
            if (!type.IsDefType()) return type;
            return module!.Types[type.Index()].Expansion switch
            {
                FunctionType => ValType.FuncRef,
                StructType => ValType.Struct,
                ArrayType => ValType.Array,
                _ => ValType.NullableRef,
            };
        }

        /// <summary>CanonicalizeSmi / CanonicalizeHeapNumber: a Number in i31 range is an i31ref.</summary>
        static bool TryI31(JSValue value, out Value result)
        {
            result = default;
            if (!value.IsNumber) return false;
            double d = value.Number;
            if (d < -(1 << 30) || d > (1 << 30) - 1 || d != Math.Floor(d) || (d == 0 && double.IsNegative(d)))
            {
                return false;
            }
            long i = (long)d;
            result = new Value(ValType.I31NN, i, new Wacs.Core.Runtime.GC.I31Ref(i));
            return true;
        }

        bool FunctionMatches(FuncAddr address, DefType expected, ModuleInstance module)
        {
            IFunctionInstance f = Store[address];
            if (f is FunctionInstance wasm) return wasm.DefType.Matches(expected, module.Types);
            if (f is HostFunction { DefType: { } hostType }) return hostType.Matches(expected, module.Types);
            return expected.Expansion is FunctionType sig && f.Type.Matches(sig, module.Types);
        }

        /// <summary>
        /// ToWebAssemblyValue (JS-API): converts a JS value to a wasm value of
        /// type <paramref name="type"/>; throws a TypeError for values that
        /// have no conversion (V8's wrappers throw kWasmTrapJSTypeError).
        /// </summary>
        public Value ToWasmValue(JSValue value, ValType type, ModuleInstance? module)
        {
            Isolate isolate = Isolate;
            switch (type)
            {
                case ValType.I32:
                    return new Value((int)ObjectOps.ToInt32(isolate, value).Number);
                case ValType.I64:
                    return new Value(BigInt.AsInt64(BigInt.FromObject(isolate, value), out _));
                case ValType.F32:
                    return new Value((float)ObjectOps.ToNumber(isolate, value).Number);
                case ValType.F64:
                    return new Value(ObjectOps.ToNumber(isolate, value).Number);
                case ValType.V128:
                    isolate.ThrowTypeError(MessageTemplate.WasmTrapJSTypeError);
                    return default;
            }
            if (!TryJSToWasmRef(value, type, module, out Value result, out _))
            {
                isolate.ThrowTypeError(MessageTemplate.WasmTrapJSTypeError);
            }
            return result;
        }

        /// <summary>
        /// ToJSValue (JS-API) / WasmToJSObject: the JS value of a wasm value of
        /// type <paramref name="type"/>.
        /// </summary>
        public JSValue ToJSValue(Value value, ValType type)
        {
            Isolate isolate = Isolate;
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
                    isolate.ThrowTypeError(MessageTemplate.WasmTrapJSTypeError);
                    return default;
            }
            return RefToJS(value);
        }

        /// <summary>WasmToJSObject of a reference value.</summary>
        public JSValue RefToJS(Value value)
        {
            if (value.IsNullRef) return JSValue.Null;
            switch (value.GcRef)
            {
                case JSExternRef extern_:
                    return extern_.Value;
                case Wacs.Core.Runtime.GC.I31Ref i31:
                    return JSValue.FromInt(i31.Value);
                case Wacs.Core.Runtime.GC.StoreStruct:
                case Wacs.Core.Runtime.StoreArray:
                    return WrapGCObject(value);
                case ExnInstance exn:
                    return ExceptionPackageFor(value, exn);
                case Wacs.Core.Runtime.Builtins.JsStringRef jsString:
                    return Isolate.Factory.NewStringFromUtf16(jsString.Value);
            }
            // A function reference is the only non-null reference without a
            // heap object (its value is the function's address).
            if (value.GcRef is null)
            {
                return FuncRefToJS(value);
            }
            return JSValue.Null;
        }

        WasmGCObjectWrapper WrapGCObject(Value value)
        {
            return _gcWrappers.GetValue(value.GcRef!, _ =>
            {
                Isolate isolate = Isolate;
                InstanceType type = value.GcRef is Wacs.Core.Runtime.StoreArray
                    ? InstanceType.WasmArrayType
                    : InstanceType.WasmStructType;
                var wrapper = (WasmGCObjectWrapper)JSObject.NewWithMap(isolate, WasmJs.GCObjectMap(isolate, type));
                wrapper.Ref = value;
                return wrapper;
            });
        }

        // ---- Exceptions -----------------------------------------------------------------

        /// <summary>
        /// The JS value of a wasm exception that leaves wasm: the thrown JS
        /// value for an exception of WebAssembly.JSTag, else its
        /// WebAssembly.Exception object.
        /// </summary>
        public JSValue ExceptionToJS(Value exnRef)
        {
            var exn = (ExnInstance)exnRef.GcRef!;
            if (_jsTag is { } jsTag && exn.Tag.Equals(jsTag))
            {
                Value payload = exn.Fields.Count > 0 ? exn.Fields.Peek() : Value.Null(ValType.ExternRef);
                return RefToJS(payload);
            }
            return ExceptionPackageFor(exnRef, exn);
        }

        WasmExceptionPackage ExceptionPackageFor(Value exnRef, ExnInstance exn)
        {
            return _exceptionPackages.GetValue(exn, _ =>
            {
                Isolate isolate = Isolate;
                var package = (WasmExceptionPackage)JSObject.NewWithMap(isolate,
                    isolate.NativeContext.WasmExceptionConstructor.InitialMap);
                package.ExnRef = exnRef;
                _tagObjects.TryGetValue(exn.Tag.Value, out package.TagObject);
                return package;
            });
        }

        internal void RegisterExceptionPackage(WasmExceptionPackage package) =>
            _exceptionPackages.AddOrUpdate(package.Exn, package);

        /// <summary>
        /// The wasm exception of a JS exception that enters wasm: the same
        /// exception for a WebAssembly.Exception, else an exception of
        /// WebAssembly.JSTag carrying the value.
        /// </summary>
        public Value JSExceptionToWasm(JSValue thrown)
        {
            if (thrown.HeapObjectOrNull is WasmExceptionPackage package) return package.ExnRef;
            var fields = new Stack<Value>();
            fields.Push(ExternRefFor(thrown, ValType.ExternRef));
            ExnAddr address = Store.AllocateExn(JSTag, fields);
            return new Value(ValType.Exn, Store[address]);
        }

        /// <summary>
        /// Allocates a wasm exception of <paramref name="tag"/> with field values
        /// in parameter order (WebAssembly.Exception).
        /// </summary>
        public Value NewException(TagAddr tag, Value[] values)
        {
            // ExnInstance.Fields is popped first-parameter-first (PushResults).
            var fields = new Stack<Value>();
            for (int i = values.Length - 1; i >= 0; i--) fields.Push(values[i]);
            ExnAddr address = Store.AllocateExn(tag, fields);
            return new Value(ValType.Exn, Store[address]);
        }

        /// <summary>The field values of a wasm exception, in parameter order.</summary>
        public static Value[] ExceptionValues(ExnInstance exn) => exn.Fields.ToArray();

        /// <summary>Whether a JS exception must not be caught by wasm (stack overflow, termination).</summary>
        public static bool IsUncatchable(JSValue thrown) =>
            thrown.HeapObjectOrNull is JSObject obj &&
            JSReceiver.HasOwnProperty(Isolate.Current!, obj, ReadOnlyRoots.wasm_uncatchable_symbol);
    }
}
