// Port of test/unittests/interpreter/bytecodes-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodesUnitTest
{
    [Fact]
    public void OperandConversion_Registers()
    {
        int register_count = 128;
        int step = register_count / 7;
        for (int i = 0; i < register_count; i += step)
        {
            if (i <= sbyte.MaxValue)
            {
                uint operand0 = (uint)new Register(i).ToOperand();
                Register reg0 = Register.FromOperand((int)operand0);
                Assert.Equal(i, reg0.Index);
            }

            uint operand1 = (uint)new Register(i).ToOperand();
            Register reg1 = Register.FromOperand((int)operand1);
            Assert.Equal(i, reg1.Index);

            uint operand2 = (uint)new Register(i).ToOperand();
            Register reg2 = Register.FromOperand((int)operand2);
            Assert.Equal(i, reg2.Index);
        }
    }

    [Fact]
    public void OperandConversion_Parameters()
    {
        int[] parameter_counts = [7, 13, 99];
        foreach (int parameter_count in parameter_counts)
        {
            for (int i = 0; i < parameter_count; i++)
            {
                Register r = Register.FromParameterIndex(i);
                uint operand_value = (uint)r.ToOperand();
                Register s = Register.FromOperand((int)operand_value);
                Assert.Equal(i, s.ToParameterIndex());
            }
        }
    }

    [Fact]
    public void OperandConversion_RegistersParametersNoOverlap()
    {
        int register_count = 128;
        int parameter_count = 100;
        int register_space_size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(register_count + parameter_count));
        var operand_count = new byte[register_space_size];

        for (int i = 0; i < register_count; i += 1)
        {
            var r = new Register(i);
            int operand = r.ToOperand();
            byte index = (byte)operand;
            Assert.True(index < operand_count.Length);
            operand_count[index] += 1;
            Assert.Equal(1, operand_count[index]);
        }

        for (int i = 0; i < parameter_count; i += 1)
        {
            Register r = Register.FromParameterIndex(i);
            uint operand = (uint)r.ToOperand();
            byte index = (byte)operand;
            Assert.True(index < operand_count.Length);
            operand_count[index] += 1;
            Assert.Equal(1, operand_count[index]);
        }
    }

    [Fact]
    public void OperandScaling_ScalableAndNonScalable()
    {
        OperandScale[] kOperandScales = [OperandScale.Single, OperandScale.Double, OperandScale.Quadruple];
        foreach (OperandScale operand_scale in kOperandScales)
        {
            int scale = (int)operand_scale;
            Assert.Equal(1 + 2 + 2 * scale, Bytecodes.Size(Bytecode.CallRuntime, operand_scale));
            Assert.Equal(1 + 2 * scale + 1, Bytecodes.Size(Bytecode.CreateObjectLiteral, operand_scale));
            Assert.Equal(1 + 2 * scale, Bytecodes.Size(Bytecode.TestIn, operand_scale));
        }
    }

    [Fact]
    public void Bytecodes_RegisterOperands()
    {
        Assert.True(Bytecodes.IsRegisterOperandType(OperandType.Reg));
        Assert.True(Bytecodes.IsRegisterOperandType(OperandType.RegPair));
        Assert.True(Bytecodes.IsRegisterInputOperandType(OperandType.Reg));
        Assert.True(Bytecodes.IsRegisterInputOperandType(OperandType.RegPair));
        Assert.True(Bytecodes.IsRegisterInputOperandType(OperandType.RegList));
        Assert.False(Bytecodes.IsRegisterOutputOperandType(OperandType.Reg));
        Assert.False(Bytecodes.IsRegisterInputOperandType(OperandType.RegOut));
        Assert.True(Bytecodes.IsRegisterOutputOperandType(OperandType.RegOut));
        Assert.True(Bytecodes.IsRegisterOutputOperandType(OperandType.RegOutPair));
    }

    static IEnumerable<Bytecode> AllBytecodes()
    {
        for (int i = 0; i < Bytecodes.kBytecodeCount; i++) yield return (Bytecode)i;
    }

    [Fact]
    public void Bytecodes_DebugBreakExistForEachBytecode()
    {
        const OperandScale kOperandScale = OperandScale.Single;
        foreach (Bytecode bytecode in AllBytecodes())
        {
            if (!Bytecodes.IsDebugBreak(bytecode) && !Bytecodes.IsPrefixScalingBytecode(bytecode))
            {
                Bytecode debug_bytecode = Bytecodes.GetDebugBreak(bytecode);
                Assert.Equal(Bytecodes.Size(bytecode, kOperandScale), Bytecodes.Size(debug_bytecode, kOperandScale));
            }
        }
    }

    [Fact]
    public void Bytecodes_DebugBreakForPrefixBytecodes()
    {
        Assert.Equal(Bytecode.DebugBreakWide, Bytecodes.GetDebugBreak(Bytecode.Wide));
        Assert.Equal(Bytecode.DebugBreakExtraWide, Bytecodes.GetDebugBreak(Bytecode.ExtraWide));
    }

    [Fact]
    public void Bytecodes_PrefixMappings()
    {
        Bytecode[] prefixes = [Bytecode.Wide, Bytecode.ExtraWide];
        foreach (Bytecode prefix in prefixes)
        {
            Assert.Equal(prefix, Bytecodes.OperandScaleToPrefixBytecode(Bytecodes.PrefixBytecodeToOperandScale(prefix)));
        }
    }

    [Fact]
    public void Bytecodes_ScaleForSignedOperand()
    {
        Assert.Equal(OperandScale.Single, Bytecodes.ScaleForSignedOperand(0));
        Assert.Equal(OperandScale.Single, Bytecodes.ScaleForSignedOperand(sbyte.MaxValue));
        Assert.Equal(OperandScale.Single, Bytecodes.ScaleForSignedOperand(sbyte.MinValue));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForSignedOperand(sbyte.MaxValue + 1));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForSignedOperand(sbyte.MinValue - 1));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForSignedOperand(short.MaxValue));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForSignedOperand(short.MinValue));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForSignedOperand(short.MaxValue + 1));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForSignedOperand(short.MinValue - 1));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForSignedOperand(int.MaxValue));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForSignedOperand(int.MinValue));
    }

    [Fact]
    public void Bytecodes_ScaleForUnsignedOperands()
    {
        Assert.Equal(OperandScale.Single, Bytecodes.ScaleForUnsignedOperand(0));
        Assert.Equal(OperandScale.Single, Bytecodes.ScaleForUnsignedOperand(byte.MaxValue));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForUnsignedOperand(byte.MaxValue + 1));
        Assert.Equal(OperandScale.Double, Bytecodes.ScaleForUnsignedOperand(ushort.MaxValue));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForUnsignedOperand(ushort.MaxValue + 1));
        Assert.Equal(OperandScale.Quadruple, Bytecodes.ScaleForUnsignedOperand(uint.MaxValue));
    }

    [Fact]
    public void Bytecodes_SizesForUnsignedOperands()
    {
        Assert.Equal(OperandSize.Byte, Bytecodes.SizeForUnsignedOperand(0));
        Assert.Equal(OperandSize.Byte, Bytecodes.SizeForUnsignedOperand(byte.MaxValue));
        Assert.Equal(OperandSize.Short, Bytecodes.SizeForUnsignedOperand(byte.MaxValue + 1));
        Assert.Equal(OperandSize.Short, Bytecodes.SizeForUnsignedOperand(ushort.MaxValue));
        Assert.Equal(OperandSize.Quad, Bytecodes.SizeForUnsignedOperand(ushort.MaxValue + 1));
        Assert.Equal(OperandSize.Quad, Bytecodes.SizeForUnsignedOperand(uint.MaxValue));
    }

    static bool InList(Bytecode bytecode, ReadOnlySpan<Bytecode> list)
    {
        foreach (Bytecode b in list)
        {
            if (b == bytecode) return true;
        }
        return false;
    }

    [Fact]
    public void Bytecodes_IsJump()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpBytecodeList), Bytecodes.IsJump(b));
    }

    [Fact]
    public void Bytecodes_IsForwardJump()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpForwardBytecodeList), Bytecodes.IsForwardJump(b));
    }

    [Fact]
    public void Bytecodes_IsConditionalJump()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpConditionalBytecodeList), Bytecodes.IsConditionalJump(b));
    }

    [Fact]
    public void Bytecodes_IsUnconditionalJump()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpUnconditionalBytecodeList), Bytecodes.IsUnconditionalJump(b));
    }

    [Fact]
    public void Bytecodes_IsJumpImmediate()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpImmediateBytecodeList), Bytecodes.IsJumpImmediate(b));
    }

    [Fact]
    public void Bytecodes_IsJumpConstant()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpConstantBytecodeList), Bytecodes.IsJumpConstant(b));
    }

    [Fact]
    public void Bytecodes_IsConditionalJumpImmediate()
    {
        foreach (Bytecode b in AllBytecodes())
        {
            bool expected = InList(b, Bytecodes.JumpConditionalBytecodeList) &&
                            InList(b, Bytecodes.JumpImmediateBytecodeList);
            Assert.Equal(expected, Bytecodes.IsConditionalJumpImmediate(b));
        }
    }

    [Fact]
    public void Bytecodes_IsConditionalJumpConstant()
    {
        foreach (Bytecode b in AllBytecodes())
        {
            bool expected = InList(b, Bytecodes.JumpConditionalBytecodeList) &&
                            InList(b, Bytecodes.JumpConstantBytecodeList);
            Assert.Equal(expected, Bytecodes.IsConditionalJumpConstant(b));
        }
    }

    [Fact]
    public void Bytecodes_IsJumpIfToBoolean()
    {
        foreach (Bytecode b in AllBytecodes())
            Assert.Equal(InList(b, Bytecodes.JumpToBooleanBytecodeList), Bytecodes.IsJumpIfToBoolean(b));
    }

    [Fact]
    public void OperandScale_PrefixesRequired()
    {
        Assert.False(Bytecodes.OperandScaleRequiresPrefixBytecode(OperandScale.Single));
        Assert.True(Bytecodes.OperandScaleRequiresPrefixBytecode(OperandScale.Double));
        Assert.True(Bytecodes.OperandScaleRequiresPrefixBytecode(OperandScale.Quadruple));
        Assert.Equal(Bytecode.Wide, Bytecodes.OperandScaleToPrefixBytecode(OperandScale.Double));
        Assert.Equal(Bytecode.ExtraWide, Bytecodes.OperandScaleToPrefixBytecode(OperandScale.Quadruple));
    }

    [Fact]
    public void ImplicitRegisterUse_LogicalOperators()
    {
        Assert.Equal(ImplicitRegisterUse.ReadAccumulator,
                     ImplicitRegisterUse.None | ImplicitRegisterUse.ReadAccumulator);
        Assert.Equal(ImplicitRegisterUse.ReadWriteAccumulator,
                     ImplicitRegisterUse.ReadAccumulator | ImplicitRegisterUse.WriteAccumulator);
        Assert.Equal(ImplicitRegisterUse.ReadAccumulator,
                     ImplicitRegisterUse.ReadAccumulator & ImplicitRegisterUse.ReadWriteAccumulator);
        Assert.Equal(ImplicitRegisterUse.None,
                     ImplicitRegisterUse.ReadAccumulator & ImplicitRegisterUse.WriteAccumulator);
    }

    [Fact]
    public void ImplicitRegisterUse_SampleBytecodes()
    {
        Assert.True(Bytecodes.ReadsAccumulator(Bytecode.Star));
        Assert.False(Bytecodes.WritesAccumulator(Bytecode.Star));
        Assert.Equal(ImplicitRegisterUse.ReadAccumulator, Bytecodes.GetImplicitRegisterUse(Bytecode.Star));
        Assert.False(Bytecodes.ReadsAccumulator(Bytecode.Ldar));
        Assert.True(Bytecodes.WritesAccumulator(Bytecode.Ldar));
        Assert.Equal(ImplicitRegisterUse.WriteAccumulator, Bytecodes.GetImplicitRegisterUse(Bytecode.Ldar));
        Assert.True(Bytecodes.ReadsAccumulator(Bytecode.Add));
        Assert.True(Bytecodes.WritesAccumulator(Bytecode.Add));
        Assert.Equal(ImplicitRegisterUse.ReadWriteAccumulator, Bytecodes.GetImplicitRegisterUse(Bytecode.Add));
    }

    [Fact]
    public void TypeOfLiteral_OnlyUndefinedGreaterThanU()
    {
        for (var flag = TestTypeOfFlags.LiteralFlag.Number; flag <= TestTypeOfFlags.LiteralFlag.Other; flag++)
        {
            string name = TestTypeOfFlags.ToString(flag);
            if (flag == TestTypeOfFlags.LiteralFlag.Undefined)
                Assert.True(string.CompareOrdinal(name, "u") > 0);
            else if (flag != TestTypeOfFlags.LiteralFlag.Other)
                Assert.True(string.CompareOrdinal(name, "u") < 0);
        }
    }

    // Not in V8's unittest: pins the numbering to V8's (Bytecode::kLast and a
    // few landmarks), which the golden files and the oracle depend on.
    [Fact]
    public void Bytecodes_NumberingMatchesV8()
    {
        Assert.Equal(0, (int)Bytecode.Wide);
        Assert.Equal(1, (int)Bytecode.ExtraWide);
        Assert.Equal(Bytecode.Star15, Bytecodes.kFirstShortStar);
        Assert.Equal((int)Bytecode.Star0 + 1, (int)Bytecode.Illegal);
        Assert.Equal(Bytecodes.kLast, Bytecode.Illegal);
        Assert.Equal(16, Bytecodes.kShortStarCount);
    }
}
