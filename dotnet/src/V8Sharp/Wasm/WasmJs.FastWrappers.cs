// The compiled JS-to-wasm wrappers: V8's specialized JS-to-wasm wrappers
// (src/wasm/wrappers.cc, compiled per signature) for functions with numeric
// signatures. The wrapper converts the JS arguments (ToWebAssemblyValue, with
// the number fast paths of the generic wrapper), calls the compiled code
// directly and converts the result, without the interpreter's operand stack
// or Value arrays. Other signatures (references, v128, multi-value) take the
// generic path (WasmJs.Wrappers.cs).
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;

namespace V8Sharp.Wasm;

/// <summary>A compiled JS-to-wasm wrapper of a signature.</summary>
public delegate JSValue JSToWasmWrapper(WasmCode code, Isolate isolate, ReadOnlySpan<JSValue> args);

public static class WasmJsFast
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue Arg(ReadOnlySpan<JSValue> args, int index) => index < args.Length ? args[index] : JSValue.Undefined;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToI32(Isolate isolate, JSValue value)
    {
        if (value.IsNumber)
        {
            double d = value.Number;
            int i = (int)d;
            return i == d ? i : Conversions.DoubleToInt32(d);
        }
        return (int)ObjectOps.ToInt32(isolate, value).Number;
    }

    public static long ToI64(Isolate isolate, JSValue value) =>
        BigInt.AsInt64(BigInt.FromObject(isolate, value), out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToF64(Isolate isolate, JSValue value) =>
        value.IsNumber ? value.Number : ObjectOps.ToNumber(isolate, value).Number;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToF32(Isolate isolate, JSValue value) => (float)ToF64(isolate, value);

    public static JSValue FromI32(int value) => JSValue.FromInt(value);
    public static JSValue FromI64(long value, Isolate isolate) => BigInt.FromInt64(isolate, value);
    public static JSValue FromF32(float value) => JSValue.FromNumber(value);
    public static JSValue FromF64(double value) => JSValue.FromNumber(value);
    public static JSValue Undefined() => JSValue.Undefined;

    static MethodInfo M(string name) => typeof(WasmJsFast).GetMethod(name)!;

    /// <summary>Whether the signature has a compiled wrapper (numbers only, at most one result).</summary>
    public static bool HasFastWrapper(WasmSignature sig)
    {
        foreach (WasmKind k in sig.Params)
        {
            if (k > WasmKind.F64) return false;
        }
        return sig.Results.Length == 0 || (sig.Results.Length == 1 && sig.Results[0] <= WasmKind.F64);
    }

    /// <summary>Builds the wrapper of <paramref name="sig"/> (see <see cref="HasFastWrapper"/>).</summary>
    internal static JSToWasmWrapper Build(WasmSignature sig)
    {
        var m = new DynamicMethod("js-to-wasm:" + sig, typeof(JSValue),
            [typeof(WasmCode), typeof(Isolate), typeof(ReadOnlySpan<JSValue>)], typeof(WasmJsFast).Module, skipVisibility: true);
        ILGenerator il = m.GetILGenerator();
        var args = new LocalBuilder[sig.Params.Length];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = il.DeclareLocal(sig.ParamTypes[i]);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Call, M(nameof(Arg)));
            il.Emit(OpCodes.Call, M(sig.Params[i] switch
            {
                WasmKind.I32 => nameof(ToI32),
                WasmKind.I64 => nameof(ToI64),
                WasmKind.F32 => nameof(ToF32),
                _ => nameof(ToF64),
            }));
            il.Emit(OpCodes.Stloc, args[i]);
        }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(WasmCode).GetField(nameof(WasmCode.Entry))!);
        il.Emit(OpCodes.Castclass, sig.DelegateType);
        foreach (LocalBuilder a in args) il.Emit(OpCodes.Ldloc, a);
        il.Emit(OpCodes.Callvirt, sig.Invoke);
        if (sig.Results.Length == 0)
        {
            il.Emit(OpCodes.Call, M(nameof(Undefined)));
        }
        else
        {
            switch (sig.Results[0])
            {
                case WasmKind.I32:
                    il.Emit(OpCodes.Call, M(nameof(FromI32)));
                    break;
                case WasmKind.I64:
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, M(nameof(FromI64)));
                    break;
                case WasmKind.F32:
                    il.Emit(OpCodes.Call, M(nameof(FromF32)));
                    break;
                default:
                    il.Emit(OpCodes.Call, M(nameof(FromF64)));
                    break;
            }
        }
        il.Emit(OpCodes.Ret);
        return m.CreateDelegate<JSToWasmWrapper>();
    }
}

public static partial class WasmJs
{
    /// <summary>
    /// The compiled wrapper path of an exported function: if the function is
    /// compiled code (or compiles now) with a numeric signature, calls it and
    /// returns true.
    /// </summary>
    static bool TryCallCompiled(Isolate isolate, WasmExportedFunctionData data, ReadOnlySpan<JSValue> arguments,
        out JSValue result)
    {
        result = default;
        WasmEngine engine = data.Engine;
        if (engine.FindCode(data.Address) is not { } code || code.Instance is null) return false;
        if (code.State == WasmCodeState.Lazy) code.Compile();
        if (code.State != WasmCodeState.Compiled) return false;
        JSToWasmWrapper? wrapper = code.Signature.JSWrapper;
        if (wrapper is null) return false;
        ExecContext context = engine.ExecContext;
        CompiledFrames frames = context.CompiledFrames;
        int sp = frames.Sp;
        int segments = frames.SegmentCount;
        frames.PushSegment(context.StackHeight, ExecContext.AbortSequence);
        ((FunctionInstance)code.Function).CallCount++;
        try
        {
            result = wrapper(code, isolate, arguments);
            return true;
        }
        catch (TrapException trap)
        {
            throw TrapToJS(isolate, trap);
        }
        catch (WasmHostException e)
        {
            throw ExceptionLeavingWasm(engine, e.ExnRef);
        }
        catch (Exception e) when (WasmErrorMessages.IsStackExhaustion(e))
        {
            RecordUnwoundFrames(isolate, (e as WasmRuntimeException)?.WasmFrames,
                (e as WasmRuntimeException)?.CalleeFuncAddr ?? -1);
            isolate.StackOverflow();
            throw;
        }
        catch (WasmRuntimeException e)
        {
            JSObject error = isolate.Factory.NewError(isolate.NativeContext.WasmRuntimeErrorFunction,
                isolate.Factory.NewStringFromAsciiChecked(e.Message));
            isolate.Throw(error);
            throw;
        }
        finally
        {
            frames.Sp = sp;
            frames.PopSegments(segments);
        }
    }
}
