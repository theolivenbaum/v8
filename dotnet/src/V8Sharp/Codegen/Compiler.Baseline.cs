// Port of the baseline parts of src/codegen/compiler.cc:
// Compiler::CompileSharedWithBaseline, Compiler::CompileBaseline and
// CompileAllWithBaseline (--always-sparkplug), with the CompilerTracer output
// of --trace-baseline.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using V8Sharp.Baseline;

namespace V8Sharp.Codegen;

public static partial class Compiler
{
    /// <summary>Compiler::CompileSharedWithBaseline.</summary>
    public static bool CompileSharedWithBaseline(Isolate isolate, SharedFunctionInfo shared)
    {
        // Early return for already baseline-compiled functions.
        if (shared.HasBaselineCode) return true;

        // Check if we actually can compile with baseline.
        if (!BaselineSupport.CanCompileWithBaseline(isolate, shared)) return false;

        // StackLimitCheck: CLEAR_EXCEPTION, so a compile near the stack limit fails quietly.
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return false;

        bool trace = isolate.Flags.trace_baseline;
        if (trace) Console.WriteLine("[compiling method " + DebugName(shared) + " (target BASELINE)]");
        long start = trace ? Stopwatch.GetTimestamp() : 0;

        BaselineCode code = BaselineSupport.GenerateBaselineCode(isolate, shared);
        shared.BaselineCode = code;
        isolate.MayHaveBaselineCode = true;

        if (trace)
        {
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine("[completed compiling " + DebugName(shared) + " (target BASELINE) - took " +
                              ms.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms]");
        }
        return true;
    }

    /// <summary>Compiler::CompileBaseline.</summary>
    public static bool CompileBaseline(Isolate isolate, JSFunction function)
    {
        if (!CompileSharedWithBaseline(isolate, function.Shared)) return false;

        // Baseline code needs a feedback vector.
        JSFunctionFeedback.EnsureFeedbackVector(isolate, function);

        // (function->UpdateCodeKeepTieringRequests(baseline_code): V8Sharp's
        // calls find the code on the SharedFunctionInfo.)
        return true;
    }

    /// <summary>CompileAllWithBaseline: --always-sparkplug compiles every function compiled to bytecode.</summary>
    internal static void CompileAllWithBaseline(Isolate isolate, List<SharedFunctionInfo> compiled)
    {
        foreach (SharedFunctionInfo shared in compiled)
        {
            if (!shared.IsCompiled) continue;
            // (TiersUpToBaseline: a V8Sharp size limit.)
            if (!BaselineSupport.TiersUpToBaseline(isolate, shared)) continue;
            CompileSharedWithBaseline(isolate, shared);
        }
    }

    static string DebugName(SharedFunctionInfo shared)
    {
        string name = shared.Name().ToString();
        if (name.Length == 0) name = shared.InferredName().ToString();
        return "0x" + RuntimeHelpers.GetHashCode(shared).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) +
               " <SharedFunctionInfo" + (name.Length == 0 ? "" : " " + name) + ">";
    }
}
