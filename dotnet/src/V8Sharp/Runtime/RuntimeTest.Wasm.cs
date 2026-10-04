// Port of src/runtime/runtime-test-wasm.cc (the %-natives of the wasm tests)
// and the wasm entries of runtime-test.cc, as they answer for V8Sharp's
// interpreter-only WebAssembly (deviations.md, "WebAssembly"): there are no
// Liftoff or TurboFan tiers, so the tier queries are false, tier-up requests
// are accepted and ignored, and %IsWasmTieringPredictable() is false so that
// tests skip their tier assertions.
using V8Sharp.Wasm;
using Wacs.Core.Runtime;
using Wacs.Core.Types.Defs;

namespace V8Sharp.Runtime;

public static partial class RuntimeTable
{
    static void RegisterWasmTest()
    {
        static JSValue Undefined(Isolate i, ReadOnlySpan<JSValue> a) => JSValue.Undefined;
        static JSValue False(Isolate i, ReadOnlySpan<JSValue> a) => JSValue.False;
        static JSValue Zero(Isolate i, ReadOnlySpan<JSValue> a) => JSValue.Zero;

        // runtime-test.cc.
        Register(FunctionId.IsWasmTieringPredictable, False);
        Register(FunctionId.ArrayBufferDetachForceWasm, static (i, a) =>
        {
            var buffer = a[0].As<JSArrayBuffer>();
            JSArrayBuffer.Detach(i, buffer, forceForWasmMemory: true, hasKey: a.Length > 1,
                a.Length > 1 ? a[1] : JSValue.Undefined);
            return JSValue.Undefined;
        });

        // Runtime_ScheduleGCInStackCheck: the wasm tests use it to collect
        // garbage while references are live on the stack; the .NET collector
        // runs on its own schedule and is precise, so there is nothing to
        // provoke.
        Register(FunctionId.ScheduleGCInStackCheck, Undefined);
        // Runtime_IsAtomicsWaitAllowed: d8 allows Atomics.wait on the main
        // thread, and so does the V8Sharp shell.
        Register(FunctionId.IsAtomicsWaitAllowed, static (i, a) => JSValue.True);

        // runtime-test-wasm.cc: tiers and code.
        Register(FunctionId.IsWasmCode, static (i, a) =>
            JSValue.FromBoolean(WasmObjects.IsWasmExportedFunction(a.Length > 0 ? a[0] : JSValue.Undefined)));
        Register(FunctionId.IsLiftoffFunction, False);
        Register(FunctionId.IsTurboFanFunction, False);
        Register(FunctionId.IsUncompiledWasmFunction, False);
        Register(FunctionId.IsWasmDebugFunction, False);
        Register(FunctionId.IsWasmTrapHandlerEnabled, False);
        // Bounds checks precede every store: a partially out-of-bounds write
        // writes nothing.
        Register(FunctionId.IsWasmPartialOOBWriteNoop, static (i, a) => JSValue.True);
        Register(FunctionId.WasmTierUpFunction, Undefined);
        Register(FunctionId.WasmTriggerTierUpForTesting, Undefined);
        Register(FunctionId.FreezeWasmLazyCompilation, Undefined);
        Register(FunctionId.SetWasmCompileControls, Undefined);
        Register(FunctionId.SetWasmInstantiateControls, Undefined);
        Register(FunctionId.FlushLiftoffCode, Undefined);
        Register(FunctionId.WasmTriggerCodeGC, Undefined);
        Register(FunctionId.WasmEnterDebugging, Undefined);
        Register(FunctionId.WasmLeaveDebugging, Undefined);
        Register(FunctionId.GenerateWasmCompilationHints, Undefined);
        Register(FunctionId.CheckIsOnCentralStack, Undefined);
        Register(FunctionId.WasmNumCodeSpaces, static (i, a) => JSValue.FromInt(1));
        Register(FunctionId.GetWasmRecoveredTrapCount, Zero);
        Register(FunctionId.WasmCompiledExportWrappersCount, Zero);
        Register(FunctionId.WasmDeoptsExecutedCount, Zero);
        Register(FunctionId.WasmDeoptsExecutedForFunction, Zero);
        Register(FunctionId.WasmSwitchToTheCentralStackCount, Zero);
        Register(FunctionId.EstimateCurrentMemoryConsumption, Zero);
        Register(FunctionId.HasUnoptimizedWasmToJSWrapper, False);
        Register(FunctionId.CountUnoptimizedWasmToJSWrapper, Zero);

        // Runtime_DisallowWasmCodegen: installs (or removes) the embedder
        // callback that disallows wasm code generation.
        Register(FunctionId.DisallowWasmCodegen, static (i, a) =>
        {
            WasmEngine.Get(i).CodegenDisallowed = a.Length > 0 && a[0].IsTrue;
            return JSValue.Undefined;
        });

        // Runtime_WasmGetNumberOfInstances: the instances of a module (V8:
        // the live ones; V8Sharp counts every instance created).
        Register(FunctionId.WasmGetNumberOfInstances, static (i, a) =>
            JSValue.FromInt(a[0].As<WasmModuleObject>().InstanceCount));

        // Runtime_GetWasmExceptionTagId: the index of the exception's tag in
        // the instance's tags.
        Register(FunctionId.GetWasmExceptionTagId, static (i, a) =>
        {
            var exception = a[0].As<WasmExceptionPackage>();
            var instance = a[1].As<WasmInstanceObject>();
            Wacs.Core.Runtime.TagAddr tag = exception.Exn.Tag;
            int index = 0;
            foreach (Wacs.Core.Runtime.TagAddr candidate in instance.Instance.TagAddrs)
            {
                if (candidate.Equals(tag)) return JSValue.FromInt(index);
                index++;
            }
            return JSValue.Undefined;
        });

        // Runtime_GetWasmExceptionValues: the values in V8's encoding
        // (WasmExceptionPackage::GetExceptionValues): an i32 is two 16-bit
        // halves, an i64 four, a float its bits, a reference the JS value.
        Register(FunctionId.GetWasmExceptionValues, static (i, a) =>
        {
            var exception = a[0].As<WasmExceptionPackage>();
            WasmEngine engine = WasmEngine.Get(i);
            var values = new List<JSValue>();
            Value[] fields = WasmEngine.ExceptionValues(exception.Exn);
            foreach (Value field in fields)
            {
                switch (field.Type)
                {
                    case ValType.I32:
                        EncodeI32(values, field.Data.UInt32);
                        break;
                    case ValType.F32:
                        EncodeI32(values, BitConverter.SingleToUInt32Bits(field.Data.Float32));
                        break;
                    case ValType.I64:
                        EncodeI64(values, field.Data.UInt64);
                        break;
                    case ValType.F64:
                        EncodeI64(values, BitConverter.DoubleToUInt64Bits(field.Data.Float64));
                        break;
                    case ValType.V128:
                    {
                        V128 v = field;
                        EncodeI32(values, v.U32x4_0);
                        EncodeI32(values, v.U32x4_1);
                        EncodeI32(values, v.U32x4_2);
                        EncodeI32(values, v.U32x4_3);
                        break;
                    }
                    default:
                        values.Add(engine.RefToJS(field));
                        break;
                }
            }
            return i.Factory.NewJSArrayWithElements(i.Factory.NewFixedArrayFrom(values.ToArray()));
        });
    }

    static void EncodeI32(List<JSValue> values, uint value)
    {
        values.Add(JSValue.FromInt((int)(value >> 16)));
        values.Add(JSValue.FromInt((int)(value & 0xFFFF)));
    }

    static void EncodeI64(List<JSValue> values, ulong value)
    {
        EncodeI32(values, (uint)(value >> 32));
        EncodeI32(values, (uint)value);
    }
}
