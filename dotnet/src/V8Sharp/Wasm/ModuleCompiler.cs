// Port of the compilation policy of src/wasm/module-compiler.cc and
// src/wasm/function-compiler.cc: when functions are compiled (lazily on the
// first call, V8's --wasm-lazy-compilation, or all at instantiation) and with
// what (WasmCompilationUnit::ExecuteCompilation).
//
// V8 compiles with Liftoff first and tiers hot functions up to TurboFan.
// V8Sharp has one compiler, the Liftoff port in Baseline/, whose IL RyuJIT
// compiles with full optimization (RyuJIT is V8Sharp's optimizing tier for
// wasm; deviations.md). --liftoff, --no-liftoff and --wasm-tier-up all select
// it; --wasm-jitless (implied by --jitless) keeps every function in the
// interpreter, V8's DrumBrake analogue.
using V8Sharp.Wasm.Baseline;
using Wacs.Core.Runtime.Types;

namespace V8Sharp.Wasm;

internal sealed class WasmModuleCompiler
{
    static readonly bool s_trace = Environment.GetEnvironmentVariable("V8SHARP_TRACE_WASM_COMPILE") == "1";
    static readonly bool s_disabled = Environment.GetEnvironmentVariable("V8SHARP_WASM_INTERPRETER") == "1";

    /// <summary>
    /// V8SHARP_WASM_INTERPRET_FUNCTIONS=1,3: function indices the compiler
    /// leaves to the interpreter (to test mixed compiled and interpreted code).
    /// </summary>
    static readonly HashSet<int> s_interpreted = ParseIndices(Environment.GetEnvironmentVariable("V8SHARP_WASM_INTERPRET_FUNCTIONS"));

    static HashSet<int> ParseIndices(string? list)
    {
        var set = new HashSet<int>();
        if (list is null) return set;
        foreach (string s in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i)) set.Add(i);
        }
        return set;
    }

    readonly WasmEngine _engine;

    WasmModuleCompiler(WasmEngine engine) => _engine = engine;

    /// <summary>Whether the isolate compiles wasm (V8: not --wasm-jitless).</summary>
    public static bool IsEnabled(Isolate isolate) =>
        !s_disabled && !isolate.Flags.wasm_jitless && !isolate.Flags.jitless;

    /// <summary>
    /// Sets up compiled code for a new instance (V8: the NativeModule's code
    /// table and the instance's dispatch tables), compiling every function
    /// now unless compilation is lazy.
    /// </summary>
    public static void Attach(WasmEngine engine, WasmInstanceObject instanceObject)
    {
        Isolate isolate = engine.Isolate;
        if (!IsEnabled(isolate)) return;
        ModuleInstance module = instanceObject.Instance;
        var data = new WasmInstanceData(engine, module, instanceObject.ModuleObject.WireBytes, new WasmModuleCompiler(engine));
        module.Compiler = data;
        if (!isolate.Flags.wasm_lazy_compilation)
        {
            // CompileNativeModule: every function, eagerly.
            for (int i = data.ImportedFunctionCount; i < data.Code.Length; i++)
            {
                WasmCode code = data.Code[i];
                if (code.Instance == data && code.State == WasmCodeState.Lazy) code.Compile();
            }
        }
    }

    /// <summary>
    /// WasmCompilationUnit::ExecuteFunctionCompilation: compiles one function,
    /// or returns null (the function stays in the interpreter).
    /// </summary>
    public Delegate? CompileFunction(WasmCode code, out string? bailout)
    {
        Delegate? result;
        if (s_interpreted.Contains(code.FunctionIndex))
        {
            bailout = "V8SHARP_WASM_INTERPRET_FUNCTIONS";
            return null;
        }
        try
        {
            result = LiftoffCompiler.Compile(code, out bailout);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A compiler failure must not change what the program does: the
            // function runs in the interpreter.
            bailout = "compiler error: " + e.GetType().Name + ": " + e.Message;
            result = null;
        }
        if (s_trace)
        {
            Console.Error.WriteLine(result is null
                ? $"[wasm-compile] function #{code.FunctionIndex} bailed out: {bailout}"
                : $"[wasm-compile] function #{code.FunctionIndex} compiled");
        }
        return result;
    }
}
