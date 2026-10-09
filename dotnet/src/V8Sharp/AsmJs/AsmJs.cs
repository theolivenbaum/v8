// Port of src/asmjs/asm-js.{h,cc} of V8 14.7: the asm.js compilation job
// (validation and translation to a wasm module, then compiling it) and the
// instantiation of a translated module (checking the standard library and the
// heap, then instantiating the wasm module with the foreign object and the
// heap as its memory), with V8's messages.
//
// This tree's revision of V8 removed src/asmjs ("use asm" runs as ordinary
// JavaScript); V8Sharp keeps 14.7's pipeline because the oracle runs it
// (deviations.md, "asm.js").
using System.Diagnostics;
using System.Globalization;
using V8Sharp.Wasm;
using StandardMember = V8Sharp.AsmJs.AsmJsParser.StandardMember;
using StdlibSet = V8Sharp.AsmJs.AsmJsParser.StdlibSet;

namespace V8Sharp.AsmJs;

/// <summary>V8's AsmJs: compile and instantiate asm.js modules.</summary>
public static class AsmJs
{
    /// <summary>
    /// Special export name used to indicate that the module exports a single
    /// function instead of a JavaScript object holding multiple functions.
    /// </summary>
    public const string kSingleFunctionName = "__single_function__";

    // wasm-limits.h.
    public const int kV8MaxWasmFunctionParams = 1_000;
    public const int kV8MaxWasmFunctionLocals = 50_000;
    public const int kV8MaxWasmFunctionSize = 7_654_321;
    /// <summary>--wasm-max-module-size (kV8MaxWasmModuleSize, 1 GiB).</summary>
    const long kV8MaxWasmModuleSize = 1024L * 1024 * 1024;

    // v8::Isolate::MessageErrorLevel.
    public const int kMessageLog = 1 << 0;
    public const int kMessageDebug = 1 << 1;
    public const int kMessageInfo = 1 << 2;
    public const int kMessageError = 1 << 3;
    public const int kMessageWarning = 1 << 4;

    static JSValue StdlibMathMember(Isolate isolate, JSReceiver stdlib, Name name)
    {
        Name mathName = isolate.Factory.InternalizeString("Math");
        JSValue math = JSReceiver.GetDataProperty(isolate, stdlib, mathName);
        if (math.HeapObjectOrNull is not JSReceiver mathReceiver) return JSValue.Undefined;
        return JSReceiver.GetDataProperty(isolate, mathReceiver, name);
    }

    // STDLIB_MATH_FUNCTION_LIST with the builtin each name must be.
    static readonly (StandardMember Member, string Name, Builtins.Builtin Builtin)[] s_mathFunctions =
    [
        (StandardMember.kMathMin, "min", Builtins.Builtin.MathMin),
        (StandardMember.kMathMax, "max", Builtins.Builtin.MathMax),
        (StandardMember.kMathAbs, "abs", Builtins.Builtin.MathAbs),
        (StandardMember.kMathFround, "fround", Builtins.Builtin.MathFround),
        (StandardMember.kMathAcos, "acos", Builtins.Builtin.MathAcos),
        (StandardMember.kMathAsin, "asin", Builtins.Builtin.MathAsin),
        (StandardMember.kMathAtan, "atan", Builtins.Builtin.MathAtan),
        (StandardMember.kMathCos, "cos", Builtins.Builtin.MathCos),
        (StandardMember.kMathSin, "sin", Builtins.Builtin.MathSin),
        (StandardMember.kMathTan, "tan", Builtins.Builtin.MathTan),
        (StandardMember.kMathExp, "exp", Builtins.Builtin.MathExp),
        (StandardMember.kMathLog, "log", Builtins.Builtin.MathLog),
        (StandardMember.kMathAtan2, "atan2", Builtins.Builtin.MathAtan2),
        (StandardMember.kMathPow, "pow", Builtins.Builtin.MathPow),
        (StandardMember.kMathImul, "imul", Builtins.Builtin.MathImul),
        (StandardMember.kMathClz32, "clz32", Builtins.Builtin.MathClz32),
        (StandardMember.kMathCeil, "ceil", Builtins.Builtin.MathCeil),
        (StandardMember.kMathFloor, "floor", Builtins.Builtin.MathFloor),
        (StandardMember.kMathSqrt, "sqrt", Builtins.Builtin.MathSqrt),
    ];

    static bool AreStdlibMembersValid(Isolate isolate, JSReceiver stdlib, StdlibSet members, out bool isTypedArray)
    {
        isTypedArray = false;
        Factory factory = isolate.Factory;
        if (members.Contains(StandardMember.kInfinity))
        {
            members.Remove(StandardMember.kInfinity);
            JSValue value = JSReceiver.GetDataProperty(isolate, stdlib, factory.InternalizeString("Infinity"));
            if (!value.IsNumber || !double.IsInfinity(value.Number)) return false;
        }
        if (members.Contains(StandardMember.kNaN))
        {
            members.Remove(StandardMember.kNaN);
            JSValue value = JSReceiver.GetDataProperty(isolate, stdlib, factory.InternalizeString("NaN"));
            if (!value.IsNumber || !double.IsNaN(value.Number)) return false;
        }
        foreach ((StandardMember member, string name, Builtins.Builtin builtin) in s_mathFunctions)
        {
            if (!members.Contains(member)) continue;
            members.Remove(member);
            JSValue value = StdlibMathMember(isolate, stdlib, factory.InternalizeString(name));
            if (value.HeapObjectOrNull is not JSFunction function) return false;
            SharedFunctionInfo shared = function.Shared;
            if (!shared.HasBuiltinId || shared.BuiltinId != builtin) return false;
        }
        for (int i = 0; i < AsmNames.StdlibMathValues.Length; i++)
        {
            StandardMember member = StandardMember.kMathE + i;
            if (!members.Contains(member)) continue;
            members.Remove(member);
            (string name, _, double constValue) = AsmNames.StdlibMathValues[i];
            JSValue value = StdlibMathMember(isolate, stdlib, factory.InternalizeString(name));
            if (!value.IsNumber || value.Number != constValue) return false;
        }
        NativeContext nc = isolate.NativeContext;
        (StandardMember Member, string Name, JSFunction Function)[] arrays =
        [
            (StandardMember.kInt8Array, "Int8Array", nc.Int8ArrayFun),
            (StandardMember.kUint8Array, "Uint8Array", nc.Uint8ArrayFun),
            (StandardMember.kInt16Array, "Int16Array", nc.Int16ArrayFun),
            (StandardMember.kUint16Array, "Uint16Array", nc.Uint16ArrayFun),
            (StandardMember.kInt32Array, "Int32Array", nc.Int32ArrayFun),
            (StandardMember.kUint32Array, "Uint32Array", nc.Uint32ArrayFun),
            (StandardMember.kFloat32Array, "Float32Array", nc.Float32ArrayFun),
            (StandardMember.kFloat64Array, "Float64Array", nc.Float64ArrayFun),
        ];
        foreach ((StandardMember member, string name, JSFunction expected) in arrays)
        {
            if (!members.Contains(member)) continue;
            members.Remove(member);
            isTypedArray = true;
            JSValue value = JSReceiver.GetDataProperty(isolate, stdlib, factory.InternalizeString(name));
            if (value.HeapObjectOrNull is not JSFunction function) return false;
            if (!ReferenceEquals(function, expected)) return false;
        }
        // All members accounted for.
        Debug.Assert(members.Empty);
        return true;
    }

    static void Report(Isolate isolate, Script script, int position, string text, MessageTemplate messageTemplate,
        int level)
    {
        var location = new MessageLocation(script, position, position);
        JSString textObject = isolate.Factory.InternalizeString(text);
        JSMessageObject message = MessageHandler.MakeMessageObject(isolate, messageTemplate, location, textObject);
        message.ErrorLevel = level;
        MessageHandler.ReportMessage(isolate, location, message);
    }

    // Hook to report successful execution of {AsmJs::CompileAsmViaWasm} phase.
    static void ReportCompilationSuccess(Isolate isolate, Script script, int position, double compileTime, int moduleSize)
    {
        if (isolate.Flags.suppress_asm_messages || !isolate.Flags.trace_asm_time) return;
        string text = string.Format(CultureInfo.InvariantCulture, "success, compile time {0:F3} ms, {1} bytes",
            compileTime, moduleSize);
        Report(isolate, script, position, text, MessageTemplate.AsmJsCompiled, kMessageInfo);
    }

    // Hook to report failed execution of {AsmJs::CompileAsmViaWasm} phase.
    static void ReportCompilationFailure(Isolate isolate, Parsing.ParseInfo parseInfo, int position, string reason)
    {
        if (isolate.Flags.suppress_asm_messages) return;
        parseInfo.pending_error_handler().ReportWarningAt(position, position, MessageTemplate.AsmJsInvalid, reason);
    }

    // Hook to report successful execution of {AsmJs::InstantiateAsmWasm} phase.
    static void ReportInstantiationSuccess(Isolate isolate, Script script, int position, double instantiateTime)
    {
        if (isolate.Flags.suppress_asm_messages || !isolate.Flags.trace_asm_time) return;
        string text = string.Format(CultureInfo.InvariantCulture, "success, {0:F3} ms", instantiateTime);
        Report(isolate, script, position, text, MessageTemplate.AsmJsInstantiated, kMessageInfo);
    }

    // Hook to report failed execution of {AsmJs::InstantiateAsmWasm} phase.
    static void ReportInstantiationFailure(Isolate isolate, Script script, int position, string reason)
    {
        if (isolate.Flags.suppress_asm_messages) return;
        Report(isolate, script, position, reason, MessageTemplate.AsmJsLinkingFailed, kMessageWarning);
    }

    // ---- The compilation job ------------------------------------------------------------

    /// <summary>
    /// V8's AsmJsCompilationJob. The compilation of asm.js modules is split into
    /// two distinct steps:
    ///  [1] ExecuteJobImpl: The asm.js module source is parsed, validated, and
    ///      translated to a valid WebAssembly module. The result are two vectors
    ///      representing the encoded module as well as encoded source position
    ///      information and a StdlibSet bit set.
    ///  [2] FinalizeJobImpl: The module is handed to WebAssembly which decodes it
    ///      into an internal representation and eventually compiles it to machine
    ///      code.
    /// </summary>
    public sealed class AsmJsCompilationJob(Isolate isolate, Parsing.ParseInfo parseInfo, Ast.FunctionLiteral literal,
        string source)
    {
        byte[]? _module;
        byte[]? _asmOffsets;
        StdlibSet _stdlibUses;
        double _compileTime;

        /// <summary>ExecuteJobImpl: true on success; on failure the warning is pending on the parse info.</summary>
        public bool ExecuteJob()
        {
            // Step 1: Translate asm.js module to WebAssembly module.
            // V8 scans the parse's character stream, which ends with the function
            // when the function is compiled lazily (ScannerStream::For with the
            // function's range), else with the script.
            int end = parseInfo.flags().is_lazy_compile() && ReferenceEquals(literal, parseInfo.literal())
                ? Math.Min(literal.end_position(), source.Length)
                : source.Length;
            var parser = new AsmJsParser(source, literal.start_position(), end, isolate.Flags.trace_asm_parser);
            if (!parser.Run())
            {
                if (!isolate.Flags.suppress_asm_messages)
                {
                    ReportCompilationFailure(isolate, parseInfo, parser.FailureLocation, parser.FailureMessage!);
                }
                return false;
            }
            var module = new ZoneBuffer();
            parser.ModuleBuilder.WriteTo(module);
            if (module.Size > kV8MaxWasmModuleSize)
            {
                if (!isolate.Flags.suppress_asm_messages)
                {
                    ReportCompilationFailure(isolate, parseInfo, parser.FailureLocation,
                        "Module size exceeds engine's supported maximum");
                }
                return false;
            }
            _module = module.ToArray();
            var asmOffsets = new ZoneBuffer();
            parser.ModuleBuilder.WriteAsmJsOffsetTable(asmOffsets);
            _asmOffsets = asmOffsets.ToArray();
            _stdlibUses = parser.StdlibUses;
            return true;
        }

        /// <summary>
        /// FinalizeJobImpl: compiles the wasm module and returns the AsmWasmData,
        /// or null if V8Sharp's wasm decoder rejects the translated module
        /// (V8: DCHECK(!thrower.error()); V8Sharp falls back to JavaScript).
        /// </summary>
        public AsmWasmData? FinalizeJob(SharedFunctionInfo shared)
        {
            // Step 2: Compile and decode the WebAssembly module.
            long start = Stopwatch.GetTimestamp();
            Script script = shared.Script!;
            Wacs.Core.Module compiled;
            try
            {
                compiled = WasmEngine.Compile(_module!, asmJs: true);
            }
            catch (WasmCompileException)
            {
                return null;
            }
            var result = new AsmWasmData(_module!, _asmOffsets!, _stdlibUses.ToIntegral(), shared.LanguageMode, compiled);
            _compileTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ReportCompilationSuccess(isolate, script, shared.StartPosition(), _compileTime, _module!.Length);
            return result;
        }
    }

    // ---- Instantiation -------------------------------------------------------------------

    static bool IsValidAsmjsMemorySize(ulong size)
    {
        // Enforce asm.js spec minimum size.
        if (size < (1u << 12)) return false;
        // Enforce engine-limited and flag-limited maximum allocation size.
        if (size > 65536UL * 65536UL) return false;
        // Enforce power-of-2 sizes for 2^12 - 2^24.
        if (size < (1u << 24)) return System.Numerics.BitOperations.IsPow2((uint)size);
        // Enforce multiple of 2^24 for sizes >= 2^24
        if ((size % (1u << 24)) != 0) return false;
        // Limitation of our implementation: for performance reasons, we use unsigned
        // uint32-to-uintptr extensions for memory addresses, which would give
        // incorrect behavior for memories larger than 2 GiB.
        // Note that this does not affect Chrome, which does not allow allocating
        // larger ArrayBuffers anyway.
        if (size > 0x8000_0000u) return false;
        // All checks passed!
        return true;
    }

    /// <summary>
    /// AsmJs::InstantiateAsmWasm: the exports (an object, or the single
    /// exported function), or null if instantiation failed (reported as a
    /// "Linking failure in asm.js" message; the caller falls back to JavaScript).
    /// </summary>
    public static JSValue? InstantiateAsmWasm(Isolate isolate, SharedFunctionInfo shared, AsmWasmData wasmData,
        JSReceiver? stdlib, JSReceiver? foreign, JSArrayBuffer? memory)
    {
        long start = Stopwatch.GetTimestamp();
        Script script = shared.Script!;

        // Allocate the WasmModuleObject.
        WasmModuleObject module = WasmEngine.FinalizeTranslatedAsmJs(isolate, wasmData, script);

        // TODO(asmjs): The position currently points to the module definition
        // but should instead point to the instantiation site (more intuitive).
        int position = shared.StartPosition();

        // Check that the module is not instantiated as a generator or async function.
        if (Common.Globals.IsResumableFunction(shared.Kind))
        {
            ReportInstantiationFailure(isolate, script, position, "Cannot be instantiated as resumable function");
            return null;
        }

        // Check that all used stdlib members are valid.
        bool stdlibUseOfTypedArrayPresent = false;
        StdlibSet stdlibUses = StdlibSet.FromIntegral(wasmData.UsesBitset);
        if (!stdlibUses.Empty)
        {
            // No checking needed if no uses.
            if (stdlib is null)
            {
                ReportInstantiationFailure(isolate, script, position, "Requires standard library");
                return null;
            }
            if (!AreStdlibMembersValid(isolate, stdlib, stdlibUses, out stdlibUseOfTypedArrayPresent))
            {
                ReportInstantiationFailure(isolate, script, position, "Unexpected stdlib member");
                return null;
            }
        }

        // Check that a valid heap buffer is provided if required.
        if (stdlibUseOfTypedArrayPresent)
        {
            if (memory is null)
            {
                ReportInstantiationFailure(isolate, script, position, "Requires heap buffer");
                return null;
            }
            // AsmJs memory must be an ArrayBuffer.
            if (memory.IsShared)
            {
                ReportInstantiationFailure(isolate, script, position, "Invalid heap type: SharedArrayBuffer");
                return null;
            }
            // We don't allow resizable ArrayBuffers because resizable ArrayBuffers may
            // shrink, and then asm.js does out of bounds memory accesses.
            if (memory.IsResizableByJs)
            {
                ReportInstantiationFailure(isolate, script, position, "Invalid heap type: resizable ArrayBuffer");
                return null;
            }
            // We don't allow WebAssembly.Memory, because WebAssembly.Memory.grow()
            // detaches the ArrayBuffer, and that would invalidate the asm.js module.
            if (memory.GetBackingStore() is { IsWasmMemory: true })
            {
                ReportInstantiationFailure(isolate, script, position, "Invalid heap type: WebAssembly.Memory");
                return null;
            }
            ulong size = memory.ByteLength;
            // Check the asm.js heap size against the valid limits.
            if (!IsValidAsmjsMemorySize(size))
            {
                ReportInstantiationFailure(isolate, script, position, "Invalid heap size");
                return null;
            }
            // Mark the buffer as undetachable. This implies that the buffer cannot be
            // postMessage()'d, as that detaches the buffer.
            memory.IsDetachable = false;
        }
        else
        {
            memory = null;
        }

        var thrower = new ErrorThrower(isolate, "AsmJs::Instantiate");
        WasmInstanceObject instance;
        try
        {
            instance = InstanceBuilder.Build(isolate, thrower, module, foreign, memory);
        }
        catch (JavaScriptException)
        {
            // Clear a possible stack overflow from function entry that would have
            // bypassed the {ErrorThrower}. (A termination exception is not a
            // JavaScriptException and propagates.)
            if (thrower.ErrorMessage is { } errorMessage)
            {
                // SNPrintF into a buffer of 100 characters.
                string errorReason = "Internal wasm failure: " + errorMessage;
                if (errorReason.Length > 99) errorReason = errorReason[..99];
                ReportInstantiationFailure(isolate, script, position, errorReason);
            }
            else
            {
                ReportInstantiationFailure(isolate, script, position, "Internal wasm failure");
            }
            return null;
        }

        ReportInstantiationSuccess(isolate, script, position, Stopwatch.GetElapsedTime(start).TotalMilliseconds);

        if (instance.AsmSingleFunction is { } singleFunction) return singleFunction;
        return instance.ExportsObject;
    }
}

/// <summary>
/// V8's AsmWasmData (wasm-objects.h): the result of translating an asm.js
/// module, held as the SharedFunctionInfo's function data until the module
/// is instantiated or found broken.
/// </summary>
public sealed class AsmWasmData(byte[] wireBytes, byte[] asmJsOffsets, ulong usesBitset, LanguageMode languageMode,
    Wacs.Core.Module module) : HeapObject(InstanceType.ForeignType)
{
    public byte[] WireBytes { get; } = wireBytes;
    /// <summary>The encoded asm.js offset table (WasmModuleBuilder::WriteAsmJsOffsetTable).</summary>
    public byte[] AsmJsOffsets { get; } = asmJsOffsets;
    /// <summary>The StdlibSet of the standard library members the module uses.</summary>
    public ulong UsesBitset { get; } = usesBitset;
    /// <summary>kAsmJsSloppyOrigin or kAsmJsStrictOrigin.</summary>
    public LanguageMode LanguageMode { get; } = languageMode;
    /// <summary>The decoded module, for the first instantiation (later ones decode the wire bytes again).</summary>
    public Wacs.Core.Module? Module { get; set; } = module;
    /// <summary>The offset table, decoded lazily, shared by the module's instances.</summary>
    public AsmJsOffsetInformation OffsetInformation { get; } = new(asmJsOffsets);
    /// <summary>The compiled code, shared by the module's instances (V8: the NativeModule's).</summary>
    internal Wasm.WasmSharedCode SharedCode { get; } = new();
}
