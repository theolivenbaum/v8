// Port of V8 14.7's entry points of asm.js: the InstantiateAsmJs builtin
// (src/builtins/builtins-internal-gen.cc), Runtime_InstantiateAsmJs
// (src/runtime/runtime-compiler.cc), Runtime_IsAsmWasmCode
// (src/runtime/runtime-test-wasm.cc) and SharedFunctionInfo::DiscardCompiled
// as asm.js uses it (src/objects/shared-function-info.cc).
//
// V8 points the JSFunction's code at InstantiateAsmJs while its
// SharedFunctionInfo holds AsmWasmData; V8Sharp dispatches by the
// SharedFunctionInfo, whose builtin id is InstantiateAsmJs while it holds the
// AsmWasmData.
using V8Sharp.Builtins;
using V8Sharp.Runtime;

namespace V8Sharp.AsmJs
{
    public static class AsmJsRuntime
    {
        /// <summary>
        /// The InstantiateAsmJs builtin: instantiates the module with (stdlib,
        /// foreign, heap) and returns the exports; on failure the function was
        /// reset to CompileLazy and is called again as JavaScript (V8 tail-calls
        /// it), as a call or as a construct.
        /// </summary>
        public static JSValue InstantiateAsmJsBuiltin(Isolate isolate, in BuiltinArguments args)
        {
            JSFunction function = args.Target;
            JSValue stdlib = args.AtOrUndefined(1);
            JSValue foreign = args.AtOrUndefined(2);
            JSValue heap = args.AtOrUndefined(3);

            // Call runtime, on success just pass the result to the caller and pop all
            // arguments. A smi 0 is returned on failure, an object on success.
            JSValue maybeResultOrSmiZero = InstantiateAsmJs(isolate, function, stdlib, foreign, heap);
            if (!maybeResultOrSmiZero.IsSmi) return maybeResultOrSmiZero;

            // On failure, tail call back to regular JavaScript by re-calling the given
            // function which has been reset to the compile lazy builtin.
            if (args.IsConstructCall)
            {
                return Execution.ConstructFunction(isolate, function, args.NewTarget, args.Arguments);
            }
            return Execution.Call(isolate, function, args.Receiver, args.Arguments);
        }

        /// <summary>Runtime_InstantiateAsmJs.</summary>
        public static JSValue InstantiateAsmJs(Isolate isolate, JSFunction function, JSValue stdlibValue,
            JSValue foreignValue, JSValue heapValue)
        {
            JSReceiver? stdlib = stdlibValue.HeapObjectOrNull as JSReceiver;
            JSReceiver? foreign = foreignValue.HeapObjectOrNull as JSReceiver;
            JSArrayBuffer? memory = heapValue.HeapObjectOrNull as JSArrayBuffer;
            SharedFunctionInfo shared = function.Shared;
            if (shared.FunctionData is AsmWasmData data)
            {
                JSValue? result = AsmJs.InstantiateAsmWasm(isolate, shared, data, stdlib, foreign, memory);
                if (result is { } exports) return exports;

                // Remove wasm data, mark as broken for asm->wasm, replace AsmWasmData on
                // the SFI with UncompiledData and set entrypoint to CompileLazy builtin,
                // and return a smi 0 to indicate failure.
                DiscardCompiled(shared);
            }
            shared.IsAsmWasmBroken = true;
            return JSValue.Zero;
        }

        /// <summary>
        /// SharedFunctionInfo::DiscardCompiled: the function becomes lazily
        /// compiled again (its scope info stays, as V8's does).
        /// </summary>
        public static void DiscardCompiled(SharedFunctionInfo shared)
        {
            int startPosition = shared.StartPosition();
            int endPosition = shared.EndPosition();
            JSString inferredName = shared.InferredName();
            shared.FeedbackMetadata = null;
            shared.FunctionData = new UncompiledData(inferredName, startPosition, endPosition);
            shared.BuiltinId = Builtin.CompileLazy;
        }

        /// <summary>Runtime_IsAsmWasmCode.</summary>
        public static JSValue IsAsmWasmCode(JSValue value)
        {
            if (value.HeapObjectOrNull is not JSFunction function) return JSValue.False;
            return JSValue.FromBoolean(function.Shared.HasAsmWasmData);
        }
    }
}

namespace V8Sharp.Runtime
{
    public static partial class RuntimeTable
    {
        static void RegisterAsmJs()
        {
            Register(FunctionId.InstantiateAsmJs, static (i, a) =>
                AsmJs.AsmJsRuntime.InstantiateAsmJs(i, a[0].As<JSFunction>(), a[1], a[2], a[3]));
            Register(FunctionId.IsAsmWasmCode, static (i, a) =>
                AsmJs.AsmJsRuntime.IsAsmWasmCode(a.Length > 0 ? a[0] : JSValue.Undefined));
        }
    }
}
