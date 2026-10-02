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
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

/// <summary>
/// The entry point of baseline code: runs the frame described by
/// <paramref name="state"/> from state.Pc (one of the code's entry offsets)
/// until it returns or suspends, and returns the accumulator.
/// </summary>
public delegate JSValue BaselineCodeEntry(Isolate isolate, ref InterpreterState state);

/// <summary>Code of kind BASELINE.</summary>
public sealed class BaselineCode
{
    readonly Isolate _isolate;
    BaselineCodeEntry? _entry;
    int _ilSize = -1;

    public BaselineCode(Isolate isolate, SharedFunctionInfo shared, BytecodeArray bytecode)
    {
        _isolate = isolate;
        SharedFunctionInfo = shared;
        Bytecode = bytecode;
        HasHandlers = bytecode.HandlerTable.Length != 0;
        FormalParameterCount = bytecode.ParameterCount - 1;
        RegisterCount = bytecode.RegisterCount;
        Register incoming = bytecode.IncomingNewTargetOrGeneratorRegister;
        IncomingNewTargetRegister = incoming.IsValid ? incoming.Index : int.MinValue;
        // Builtins::Call's checks on the callee that do not change after
        // compilation (CallFunction: class constructors throw, sloppy non-native
        // functions convert the receiver).
        CallableDirectly = !shared.IsClassConstructor;
        ConvertsReceiver = !shared.Native && shared.LanguageMode == LanguageMode.Sloppy;
        CheckStackOnEveryCall = bytecode.Length > kLargeFrameBytecodeLength;
    }

    public SharedFunctionInfo SharedFunctionInfo { get; }

    /// <summary>The bytecode the code was compiled from (V8: the Code's bytecode_or_interpreter_data).</summary>
    public BytecodeArray Bytecode { get; }

    /// <summary>The compiled method (generated on first use).</summary>
    public BaselineCodeEntry Entry
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entry ?? Generate();
    }

    /// <summary>The size of the generated IL in bytes (V8: instruction_size); -1 before generation.</summary>
    public int ILSize => _ilSize;

    /// <summary>Whether the bytecode has exception handlers (the entry then needs the handler dispatch loop).</summary>
    public bool HasHandlers { get; }

    // What a call needs to know about the callee (BaselineCalls), read from
    // the code object instead of the SharedFunctionInfo and the BytecodeArray.

    /// <summary>The bytecode's formal parameter count (without the receiver).</summary>
    public readonly int FormalParameterCount;

    /// <summary>The register file size.</summary>
    public readonly int RegisterCount;

    /// <summary>The register index of the incoming new.target or generator, or int.MinValue.</summary>
    public readonly int IncomingNewTargetRegister;

    /// <summary>Not a class constructor: [[Call]] runs the code.</summary>
    public readonly bool CallableDirectly;

    /// <summary>A sloppy, non-native function: a primitive receiver is converted (CallFunction).</summary>
    public readonly bool ConvertsReceiver;

    /// <summary>
    /// A large function, whose .NET frame may be large too: a call into it checks
    /// the .NET stack every time (BaselineCalls checks every fourth level otherwise).
    /// </summary>
    public readonly bool CheckStackOnEveryCall;

    const int kLargeFrameBytecodeLength = 1024;

    public static CodeKind Kind => CodeKind.BASELINE;

    [MethodImpl(MethodImplOptions.NoInlining)]
    BaselineCodeEntry Generate()
    {
        // The code reads the materialized constant pool.
        if (Bytecode.ConstantPoolValues is null) InterpreterRuntime.MaterializeConstantPool(_isolate, Bytecode);
        var compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode);
        compiler.GenerateCode();
        if (compiler.ExceedsOptimizationLimits)
        {
            // RyuJIT would not optimize the method (BaselineILEmitter): emit the
            // compact form, whose bytecodes call out of line.
            compiler = new BaselineCompiler(_isolate, SharedFunctionInfo, Bytecode, compact: true);
            compiler.GenerateCode();
        }
        if (_isolate.Flags.trace_baseline)
        {
            Console.WriteLine("[baseline code for " + SharedFunctionInfo.Name() + ": bytecode=" + Bytecode.Length + " " + compiler.Statistics + "]");
        }
        (BaselineCodeEntry entry, int ilSize) = compiler.Build(this);
        _ilSize = ilSize;
        return _entry = entry;
    }
}
