// Port of the optimization test natives of src/runtime/runtime-test.cc for
// Maglev: %PrepareFunctionForOptimization (ManualOptimizationTable),
// %OptimizeFunctionOnNextCall / %OptimizeMaglevOnNextCall
// (OptimizeFunctionOnNextCall with CanOptimizeFunction), %OptimizeOsr,
// %NeverOptimizeFunction, %DeoptimizeFunction, %DeoptimizeNow and
// %ActiveTierIsMaglev.
//
// V8Sharp has no Turbofan: %OptimizeFunctionOnNextCall optimizes to Maglev
// (as V8 with --optimize-on-next-call-optimizes-to-maglev). Deviation:
// compilation is synchronous and happens at the request rather than at the
// next call (the feedback it sees is the same).
using V8Sharp.Deoptimizer;
using V8Sharp.Maglev;

namespace V8Sharp.Runtime;

public static partial class RuntimeTest
{
    /// <summary>
    /// %PrepareFunctionForOptimization: compiles the function, allocates its
    /// feedback vector, and keeps heuristic tiering away from it.
    /// </summary>
    public static JSValue PrepareFunctionForOptimization(Isolate isolate, JSValue functionObject)
    {
        JSValue result = EnsureFeedbackVector(isolate, functionObject);
        if (functionObject.HeapObjectOrNull is JSFunction function) MaglevCompiler.MarkForManualOptimization(function);
        return result;
    }

    /// <summary>%OptimizeMaglevOnNextCall / %OptimizeFunctionOnNextCall.</summary>
    public static JSValue OptimizeMaglevOnNextCall(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (args.Length < 1 || args[0].HeapObjectOrNull is not JSFunction function) return JSValue.Undefined;
        if (!isolate.UseOptimizer) return JSValue.Undefined;
        // CanOptimizeFunction.
        if (!function.Shared.IsCompiled && !Codegen.Compiler.CompileLazy(isolate, function)) return JSValue.Undefined;
        if (MaglevCompiler.CompilationDisabled(function.Shared)) return JSValue.Undefined;
        if (function.Shared.FunctionData is not Interpreter.BytecodeArray) return JSValue.Undefined;
        JSFunctionFeedback.EnsureFeedbackVector(isolate, function);
        Codegen.Compiler.CompileMaglev(isolate, function);
        return JSValue.Undefined;
    }

    /// <summary>
    /// %OptimizeOsr: the topmost frame of the function OSRs at its next loop
    /// back edge (urgency at the maximum, and an immediate budget interrupt).
    /// </summary>
    public static JSValue OptimizeOsr(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (!isolate.UseOptimizer || !isolate.Flags.use_osr) return JSValue.Undefined;
        JSFunction? function = null;
        if (args.Length > 0 && args[0].HeapObjectOrNull is JSFunction f)
        {
            function = f;
        }
        else
        {
            InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
            for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
            {
                if (frames[i].Kind != InterpreterFrameKind.Interpreted) continue;
                function = frames[i].Function;
                break;
            }
        }
        if (function is null || MaglevCompiler.CompilationDisabled(function.Shared)) return JSValue.Undefined;
        FeedbackVector vector = JSFunctionFeedback.EnsureFeedbackVector(isolate, function);
        vector.RequestOsrAtNextOpportunity();
        function.RawFeedbackCell.InterruptBudget = 0;
        return JSValue.Undefined;
    }

    /// <summary>%NeverOptimizeFunction.</summary>
    public static JSValue NeverOptimizeFunction(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (args.Length > 0 && args[0].HeapObjectOrNull is JSFunction function)
        {
            MaglevCompiler.DisableOptimization(function.Shared, "%NeverOptimizeFunction");
            if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code })
            {
                MaglevCompiler.InvalidateCode(isolate, code, LazyDeoptimizeReason.kTesting);
            }
        }
        return JSValue.Undefined;
    }

    /// <summary>%DeoptimizeFunction: invalidates the function's optimized code (activations deoptimize lazily).</summary>
    public static JSValue DeoptimizeFunction(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (args.Length > 0 && args[0].HeapObjectOrNull is JSFunction function) DeoptimizeAll(isolate, function);
        return JSValue.Undefined;
    }

    /// <summary>%DeoptimizeNow: deoptimizes the topmost JavaScript frame's function.</summary>
    public static JSValue DeoptimizeNow(Isolate isolate)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
        {
            if (frames[i].Kind != InterpreterFrameKind.Interpreted) continue;
            // The optimized frame (the outermost of an inlined chain) owns the code.
            int j = i;
            while (j > 0 && frames[j].IsMaglev && frames[j - 1].IsMaglev && frames[j - 1].Kind == InterpreterFrameKind.Interpreted) j--;
            DeoptimizeAll(isolate, frames[j].Function);
            break;
        }
        return JSValue.Undefined;
    }

    static void DeoptimizeAll(Isolate isolate, JSFunction function)
    {
        if (function.RawFeedbackCell.Value is not FeedbackVector vector) return;
        if (vector.MaglevCode is { } code) MaglevCompiler.InvalidateCode(isolate, code, LazyDeoptimizeReason.kTesting);
        if (vector.MaglevOsrCode is { } osr)
        {
            foreach (MaglevCode c in osr.Values.ToArray()) MaglevCompiler.InvalidateCode(isolate, c, LazyDeoptimizeReason.kTesting);
        }
    }

    /// <summary>%ActiveTierIsMaglev.</summary>
    public static JSValue ActiveTierIsMaglev(JSValue functionObject) =>
        JSValue.FromBoolean(functionObject.HeapObjectOrNull is JSFunction function && TieringManager.ActiveTierIsMaglev(function));
}
