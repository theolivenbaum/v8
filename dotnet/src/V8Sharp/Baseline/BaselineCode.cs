// The code object of a baseline-compiled function: V8's Code of kind BASELINE
// (src/objects/code.h), which V8 installs as SharedFunctionInfo::baseline_code.
//
// V8's baseline code is machine code with the interpreter's frame layout and a
// bytecode offset table (src/baseline/bytecode-offset-iterator.h) mapping
// machine pcs back to bytecode offsets. V8Sharp's is an IL method over the
// same register-stack frame as the interpreter; it keeps the current bytecode
// offset in the frame record instead of a pc table, and can be entered at
// every entry offset (the function start, exception handlers and loop
// headers for OSR from the interpreter).
//
// Deviation: the IL is generated when the code first runs, not when the code
// object is created. The function counts as baseline-compiled from the
// moment the code is installed (HasBaselineCode, %ActiveTierIsSparkplug), as
// in V8; only the work is deferred. Generating and jitting a method costs
// about a millisecond, and --always-sparkplug installs code for every
// function a script contains, most of which never run.
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

/// <summary>
/// The entry point of baseline code: runs the frame described by
/// <paramref name="state"/> from state.Pc (one of the code's entry offsets)
/// until it returns or suspends, and returns the accumulator.
/// </summary>
public delegate JSValue BaselineCodeEntry(Isolate isolate, ref InterpreterState state);

/// <summary>Code of kind BASELINE.</summary>
public sealed class BaselineCode(Isolate isolate, SharedFunctionInfo shared, BytecodeArray bytecode)
{
    BaselineCodeEntry? _entry;
    int _ilSize = -1;

    public SharedFunctionInfo SharedFunctionInfo { get; } = shared;

    /// <summary>The bytecode the code was compiled from (V8: the Code's bytecode_or_interpreter_data).</summary>
    public BytecodeArray Bytecode { get; } = bytecode;

    /// <summary>The compiled method (generated on first use).</summary>
    public BaselineCodeEntry Entry => _entry ?? Generate();

    /// <summary>The size of the generated IL in bytes (V8: instruction_size); -1 before generation.</summary>
    public int ILSize => _ilSize;

    /// <summary>Whether the bytecode has exception handlers (the entry then needs the handler dispatch loop).</summary>
    public bool HasHandlers { get; } = bytecode.HandlerTable.Length != 0;

    public static CodeKind Kind => CodeKind.BASELINE;

    BaselineCodeEntry Generate()
    {
        var compiler = new BaselineCompiler(isolate, SharedFunctionInfo, Bytecode);
        compiler.GenerateCode();
        (BaselineCodeEntry entry, int ilSize) = compiler.Build();
        _ilSize = ilSize;
        return _entry = entry;
    }
}
