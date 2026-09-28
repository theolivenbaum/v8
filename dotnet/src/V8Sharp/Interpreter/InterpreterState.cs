// The register state of an executing interpreter frame and the operand
// decoding of src/interpreter/interpreter-assembler.cc (BytecodeOperand*,
// LoadRegisterAtOperandIndex ...).
//
// V8 generates one handler per bytecode and operand scale. V8Sharp has one
// dispatch loop written once over an operand-scale type parameter: the JIT
// specializes it for Single (the loop proper, constant operand offsets) and for
// Double/Quadruple, which the Single loop calls for one bytecode when it meets
// a Wide/ExtraWide prefix.
using System.Runtime.CompilerServices;

namespace V8Sharp.Interpreter;

/// <summary>An operand scale as a type, so the dispatch loop can be specialized per scale.</summary>
public interface IOperandScale
{
    /// <summary>The operand scale (1, 2 or 4).</summary>
    static abstract int Scale { get; }

    /// <summary>True for the prefixed scales: the loop executes one bytecode and returns.</summary>
    static abstract bool SingleStep { get; }
}

public struct SingleScale : IOperandScale
{
    public static int Scale => 1;
    public static bool SingleStep => false;
}

public struct DoubleScale : IOperandScale
{
    public static int Scale => 2;
    public static bool SingleStep => true;
}

public struct QuadrupleScale : IOperandScale
{
    public static int Scale => 4;
    public static bool SingleStep => true;
}

/// <summary>The live state of an interpreter frame, spilled while a prefixed bytecode runs and after an exception.</summary>
public struct InterpreterState
{
    public JSFunction Function;
    public BytecodeArray Bytecode;
    public JSValue[] Constants;
    public FeedbackVector? FeedbackVector;
    public Context Context;
    public JSValue Accumulator;
    /// <summary>The offset of the current bytecode (after any prefix).</summary>
    public int Pc;
    /// <summary>The frame pointer (index into the register stack).</summary>
    public int Fp;
    /// <summary>The index of this frame's record in Isolate.InterpreterFrames.</summary>
    public int FrameIndex;
    /// <summary>The actual argument count.</summary>
    public int Argc;
    /// <summary>Set when the frame returned (Return / SuspendGenerator) during a single step.</summary>
    public bool Done;
}

/// <summary>Operand decoding (little-endian, unaligned, as V8's BytecodeOperandReadUnaligned).</summary>
internal static class Operands
{
    /// <summary>A signed scalable operand (Reg, Imm) at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Signed<TS>(ref byte code, int offset) where TS : struct, IOperandScale
    {
        if (TS.Scale == 1) return (sbyte)Unsafe.Add(ref code, offset);
        if (TS.Scale == 2) return Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref code, offset));
        return Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref code, offset));
    }

    /// <summary>An unsigned scalable operand (ConstantPoolIndex, FeedbackSlot, UImm, RegCount ...).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Unsigned<TS>(ref byte code, int offset) where TS : struct, IOperandScale
    {
        if (TS.Scale == 1) return Unsafe.Add(ref code, offset);
        if (TS.Scale == 2) return Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref code, offset));
        return (int)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, offset));
    }

    /// <summary>A fixed one-byte operand (Flag8, IntrinsicId, NativeContextIndex, AbortReason, EmbeddedFeedback).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Byte(ref byte code, int offset) => Unsafe.Add(ref code, offset);

    /// <summary>A fixed two-byte operand (Flag16, RuntimeId).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Short(ref byte code, int offset) => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref code, offset));

    /// <summary>
    /// The register slot for a register operand. <paramref name="regBase"/> is
    /// the slot of operand 0 (fp - 7): a register operand o addresses fp - 7 - o
    /// (Register::FromOperand and the frame layout of InterpreterFrames.cs).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref JSValue Reg(ref JSValue regBase, int operand) => ref Unsafe.Subtract(ref regBase, operand);
}
