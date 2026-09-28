// The code object of a baseline-compiled function: V8's Code of kind BASELINE
// (src/objects/code.h), which V8 installs as SharedFunctionInfo::baseline_code.
//
// V8's baseline code is machine code with the interpreter's frame layout and a
// bytecode offset table (src/baseline/bytecode-offset-iterator.h) mapping
// machine pcs back to bytecode offsets. V8Sharp's is a DynamicMethod over the
// same register-stack frame as the interpreter; it keeps the current bytecode
// offset in the frame record instead of a pc table, and can be entered at
// every offset in EntryOffsets (the function start, exception handlers and
// loop headers for OSR from the interpreter).
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

/// <summary>
/// The entry point of baseline code: runs the frame described by
/// <paramref name="state"/> from state.Pc (one of the code's entry offsets)
/// until it returns or suspends, and returns the accumulator.
/// </summary>
public delegate JSValue BaselineCodeEntry(Isolate isolate, ref InterpreterState state);

/// <summary>Code of kind BASELINE.</summary>
public sealed class BaselineCode(SharedFunctionInfo shared, BytecodeArray bytecode, BaselineCodeEntry entry, int[] entryOffsets,
    int ilSize)
{
    public SharedFunctionInfo SharedFunctionInfo { get; } = shared;

    /// <summary>The bytecode the code was compiled from (V8: the Code's bytecode_or_interpreter_data).</summary>
    public BytecodeArray Bytecode { get; } = bytecode;

    public BaselineCodeEntry Entry { get; } = entry;

    /// <summary>The bytecode offsets at which the code can be entered, sorted.</summary>
    public int[] EntryOffsets { get; } = entryOffsets;

    /// <summary>The size of the generated IL in bytes (V8: instruction_size).</summary>
    public int ILSize { get; } = ilSize;

    /// <summary>Whether the bytecode has exception handlers (the entry then needs the handler dispatch loop).</summary>
    public bool HasHandlers { get; } = bytecode.HandlerTable.Length != 0;

    public static CodeKind Kind => CodeKind.BASELINE;
}
