// Port of test/unittests/interpreter/bytecode-operands-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeOperandsUnitTest
{
    static readonly OperandType[] RegisterOperandTypeList =
    [
        OperandType.Reg, OperandType.RegList, OperandType.RegPair, OperandType.RegOut, OperandType.RegOutList,
        OperandType.RegOutPair, OperandType.RegOutTriple, OperandType.RegInOut,
    ];

    static readonly OperandType[] SignedScalableScalarOperandTypeList = [OperandType.Imm];
    static readonly OperandType[] InvalidOperandTypeList = [OperandType.None];

    static readonly OperandType[] UnsignedFixedScalarOperandTypeList =
    [
        OperandType.Flag8, OperandType.Flag16, OperandType.IntrinsicId, OperandType.RuntimeId,
        OperandType.NativeContextIndex, OperandType.AbortReason, OperandType.EmbeddedFeedback,
    ];

    static readonly OperandType[] UnsignedScalableScalarOperandTypeList =
    [
        OperandType.ConstantPoolIndex, OperandType.FeedbackSlot, OperandType.ContextSlot, OperandType.CoverageSlot,
        OperandType.UImm, OperandType.RegCount,
    ];

    [Fact]
    public void BytecodeOperandsTest_IsScalableSignedByte()
    {
        foreach (var t in RegisterOperandTypeList) Assert.True(BytecodeOperands.IsScalableSignedByte(t));
        foreach (var t in SignedScalableScalarOperandTypeList) Assert.True(BytecodeOperands.IsScalableSignedByte(t));
        foreach (var t in InvalidOperandTypeList) Assert.False(BytecodeOperands.IsScalableSignedByte(t));
        foreach (var t in UnsignedFixedScalarOperandTypeList) Assert.False(BytecodeOperands.IsScalableSignedByte(t));
        foreach (var t in UnsignedScalableScalarOperandTypeList) Assert.False(BytecodeOperands.IsScalableSignedByte(t));
    }

    [Fact]
    public void BytecodeOperandsTest_IsScalableUnsignedByte()
    {
        foreach (var t in UnsignedScalableScalarOperandTypeList) Assert.True(BytecodeOperands.IsScalableUnsignedByte(t));
        foreach (var t in InvalidOperandTypeList) Assert.False(BytecodeOperands.IsScalableUnsignedByte(t));
        foreach (var t in RegisterOperandTypeList) Assert.False(BytecodeOperands.IsScalableUnsignedByte(t));
        foreach (var t in SignedScalableScalarOperandTypeList) Assert.False(BytecodeOperands.IsScalableUnsignedByte(t));
        foreach (var t in UnsignedFixedScalarOperandTypeList) Assert.False(BytecodeOperands.IsScalableUnsignedByte(t));
    }
}
