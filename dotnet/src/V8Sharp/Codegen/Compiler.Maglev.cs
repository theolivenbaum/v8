// Port of the Maglev parts of src/codegen/compiler.cc: Compiler::CompileOptimized
// for CodeKind::MAGLEV (GetOrCompileOptimized in synchronous mode).
using V8Sharp.Maglev;

namespace V8Sharp.Codegen;

public static partial class Compiler
{
    /// <summary>
    /// Compiler::CompileOptimized(function, kSynchronous, MAGLEV): compiles
    /// and installs Maglev code. False when the function cannot be optimized.
    /// </summary>
    public static bool CompileMaglev(Isolate isolate, JSFunction function)
    {
        if (!isolate.UseOptimizer) return false;
        if (!function.Shared.IsCompiled && !CompileLazy(isolate, function)) return false;
        if (function.Shared.FunctionData is not Interpreter.BytecodeArray) return false;
        JSFunctionFeedback.EnsureFeedbackVector(isolate, function);
        if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { MarkedForDeoptimization: false } }) return true;
        MaglevCode? code = MaglevCompiler.Compile(isolate, function);
        if (code is null) return false;
        MaglevCompiler.InstallCode(isolate, code);
        return true;
    }
}
