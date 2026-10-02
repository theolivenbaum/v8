// Interpreter registers in IL locals.
//
// Deviation: V8's Sparkplug code keeps the interpreter registers in the
// frame (the interpreter frame layout is the point of the tier). V8Sharp's
// frame is an array on the managed heap: every register store costs a GC
// write barrier and every access a memory operation. For a function whose
// frame nothing else reads while it runs, the registers r0..rN live in IL
// locals of the baseline method instead, which RyuJIT keeps in machine
// registers. The frame copy is written where something reads it:
//
// - a register list passed to a call, a runtime function or an intrinsic is
//   stored to the frame first (the callee's arguments are copied from there);
// - registers a builtin writes through the frame (RegOut pairs, triples and
//   lists, ToObject, GetSuperConstructor, PushContext) are reloaded after it;
// - on entry at a loop header (OSR from the interpreter) the locals are loaded
//   from the frame, and before the interrupt budget's runtime call on a back
//   edge (tiering decisions, OSR) the frame gets all of them.
//
// Functions whose frame can be read at any point keep their registers in the
// frame: those with exception handlers (a handler is entered by re-entering
// the method, after the .NET exception left it with its locals), generators
// and async functions (suspension copies the register file), and compact code.
// Parameters (a0..an, the receiver) and the fixed slots (context, closure,
// feedback vector) always stay in the frame: arguments objects and the stack
// walker read them there.
using System.Reflection.Emit;
using V8Sharp.Interpreter;
using OperandType = V8Sharp.Interpreter.OperandType;

namespace V8Sharp.Baseline;

public sealed partial class BaselineCompiler
{
    /// <summary>The most registers a function may have to keep them in IL locals (RyuJIT tracks about a thousand locals).</summary>
    const int kMaxCachedRegisters = 64;

    static readonly bool s_noRegisterCache = Environment.GetEnvironmentVariable("V8SHARP_BASELINE_NO_REGISTER_CACHE") == "1";

    /// <summary>The IL locals of the cached registers r0..rN, or null when the registers stay in the frame.</summary>
    LocalBuilder[]? _registerLocals;

    bool IsCached(Register r) => _registerLocals is not null && r.Index >= 0 && r.Index < _registerLocals.Length;

    /// <summary>Decides whether the registers live in IL locals and declares them (before the prologue).</summary>
    void SetUpRegisterCache()
    {
        int count = _bytecode.RegisterCount;
        if (s_noRegisterCache || _compact || count == 0 || count > kMaxCachedRegisters) return;
        if (_bytecode.HandlerTable.Length != 0) return;
        if (Globals.IsResumableFunction(_shared.Kind)) return;
        _registerLocals = new LocalBuilder[count];
        for (int i = 0; i < count; i++) _registerLocals[i] = _il.DeclareLocal(typeof(JSValue));
    }

    /// <summary>Stores the cached registers [first, first + count) to their frame slots.</summary>
    void SpillRegisters(int first, int count)
    {
        for (int i = first; i < first + count; i++)
        {
            if (i < 0 || i >= _registerLocals!.Length) continue;
            _masm.LoadFrameSlotAddress(i);
            Emit(OpCodes.Ldloc, _registerLocals[i]);
            Emit(OpCodes.Stobj, typeof(JSValue));
        }
    }

    /// <summary>Loads the cached registers [first, first + count) from their frame slots.</summary>
    void ReloadRegisters(int first, int count)
    {
        for (int i = first; i < first + count; i++)
        {
            if (i < 0 || i >= _registerLocals!.Length) continue;
            _masm.LoadFrameSlotAddress(i);
            Emit(OpCodes.Ldobj, typeof(JSValue));
            Emit(OpCodes.Stloc, _registerLocals[i]);
        }
    }

    /// <summary>Before a bytecode: the register lists it passes by frame index go to the frame.</summary>
    void SpillRegisterListOperands(Bytecode bytecode)
    {
        ReadOnlySpan<OperandType> types = Bytecodes.GetOperandTypes(bytecode);
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] == OperandType.RegList) SpillRegisters(RegisterOperand(i).Index, RegisterCount(i + 1));
        }
    }

    /// <summary>
    /// After a bytecode: the registers its builtin wrote through the frame are
    /// reloaded (Star and Mov write the locals themselves).
    /// </summary>
    void ReloadRegisterOutputOperands(Bytecode bytecode)
    {
        if (Bytecodes.IsAnyStar(bytecode) || bytecode == Bytecode.Mov) return;
        ReadOnlySpan<OperandType> types = Bytecodes.GetOperandTypes(bytecode);
        for (int i = 0; i < types.Length; i++)
        {
            switch (types[i])
            {
                case OperandType.RegOut:
                    ReloadRegisters(RegisterOperand(i).Index, 1);
                    break;
                case OperandType.RegOutPair:
                    ReloadRegisters(RegisterOperand(i).Index, 2);
                    break;
                case OperandType.RegOutTriple:
                    ReloadRegisters(RegisterOperand(i).Index, 3);
                    break;
                case OperandType.RegOutList:
                    ReloadRegisters(RegisterOperand(i).Index, RegisterCount(i + 1));
                    break;
            }
        }
    }
}
