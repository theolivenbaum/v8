// Port of test/unittests/interpreter/bytecode-array-random-iterator-unittest.cc.
//
// V8 repeats the per-bytecode expectations for every direction of travel;
// here they are one table (Expected) checked at each visited index.
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeArrayRandomIteratorUnitTest
{
    const int kFeedbackIsEmbedded = InterpreterConstants.kFeedbackIsEmbedded;
    const int kPrefixByteSize = 1;

    static readonly double heap_num_0 = 2.718;
    static readonly double heap_num_1 = 2.0 * Smi.kMaxValue;
    static readonly Smi smi_0 = Smi.FromInt(64);
    static readonly Smi smi_1 = Smi.FromInt(-65536);
    static readonly Register reg_0 = new(0);
    static readonly Register param = Register.FromParameterIndex(2);
    const uint name_index = 2;
    const uint feedback_slot = 0;

    // Use a builder to create an array with containing multiple bytecodes
    // with 0, 1 and 2 operands. |wideReg| is reg_16 (17 locals, not eligible
    // for a short Star) or reg_1 (3 locals).
    static BytecodeArray BuildArray(bool wideReg, out Register reg)
    {
        var builder = new BytecodeArrayBuilder(3, wideReg ? 17 : 3);
        reg = new Register(wideReg ? 16 : 1);
        RegisterList pair = BytecodeUtils.NewRegisterList(0, 2);
        RegisterList triple = BytecodeUtils.NewRegisterList(0, 3);
        const string name = "abc";

        builder.LoadLiteral(heap_num_0)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(heap_num_1)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(Smi.Zero)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(smi_0)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(smi_1)
            .StoreAccumulatorInRegister(reg)
            .LoadAccumulatorWithRegister(reg_0)
            .BinaryOperation(Token.Add, reg_0, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(reg)
            .LoadNamedProperty(reg, name, (int)feedback_slot)
            .BinaryOperation(Token.Add, reg_0, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(param)
            .CallRuntimeForPair(RuntimeFunctionId.LoadLookupSlotForCall, param, pair)
            .ForInPrepare(triple, (int)feedback_slot)
            .CallRuntime(RuntimeFunctionId.LoadIC_Miss, reg_0)
            .Debugger()
            .Return();
        return builder.ToBytecodeArray();
    }

    static double NumberValue(object o) => o switch
    {
        Smi s => s.Value,
        double d => d,
        _ => throw new InvalidOperationException(),
    };

    // The bytecodes of BuildArray(true, ...), with their operand scale.
    static readonly (Bytecode Bytecode, OperandScale Scale)[] Sequence =
    [
        (Bytecode.LdaConstant, OperandScale.Single),         // 0
        (Bytecode.Star0, OperandScale.Single),               // 1
        (Bytecode.LdaConstant, OperandScale.Single),         // 2
        (Bytecode.Star0, OperandScale.Single),               // 3
        (Bytecode.LdaZero, OperandScale.Single),             // 4
        (Bytecode.Star0, OperandScale.Single),               // 5
        (Bytecode.LdaSmi, OperandScale.Single),              // 6
        (Bytecode.Star0, OperandScale.Single),               // 7
        (Bytecode.LdaSmi, OperandScale.Quadruple),           // 8
        (Bytecode.Star, OperandScale.Single),                // 9
        (Bytecode.Ldar, OperandScale.Single),                // 10
        (Bytecode.Add, OperandScale.Single),                 // 11
        (Bytecode.Star, OperandScale.Single),                // 12
        (Bytecode.GetNamedProperty, OperandScale.Single),    // 13
        (Bytecode.Add, OperandScale.Single),                 // 14
        (Bytecode.Star, OperandScale.Single),                // 15
        (Bytecode.CallRuntimeForPair, OperandScale.Single),  // 16
        (Bytecode.ForInPrepare, OperandScale.Single),        // 17
        (Bytecode.CallRuntime, OperandScale.Single),         // 18
        (Bytecode.Debugger, OperandScale.Single),            // 19
        (Bytecode.Return, OperandScale.Single),              // 20
    ];

    static int OffsetOf(int index)
    {
        int offset = 0;
        for (int i = 0; i < index; i++)
        {
            var (bytecode, scale) = Sequence[i];
            offset += Bytecodes.Size(bytecode, scale) + (scale == OperandScale.Single ? 0 : kPrefixByteSize);
        }
        return offset;
    }

    // The per-index EXPECT_EQs of V8's test.
    static void Expected(BytecodeArrayRandomIterator iterator, int index, Register reg_16)
    {
        var (bytecode, scale) = Sequence[index];
        Assert.Equal(bytecode, iterator.CurrentBytecode());
        Assert.Equal(index, iterator.CurrentIndex);
        Assert.Equal(OffsetOf(index), iterator.CurrentOffset());
        Assert.Equal(scale, iterator.CurrentOperandScale());
        switch (index)
        {
            case 0:
                Assert.Equal(heap_num_0, NumberValue(iterator.GetConstantForOperand(0)));
                break;
            case 2:
                Assert.Equal(heap_num_1, NumberValue(iterator.GetConstantForOperand(0)));
                break;
            case 6:
                Assert.Equal(smi_0, Smi.FromInt(iterator.GetImmediateOperand(0)));
                break;
            case 8:
                Assert.Equal(smi_1, Smi.FromInt(iterator.GetImmediateOperand(0)));
                break;
            case 9:
            case 12:
                Assert.Equal(reg_16.Index, iterator.GetRegisterOperand(0).Index);
                Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
                break;
            case 10:
                Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
                break;
            case 11:
            case 14:
                Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
                Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
                break;
            case 13:
                Assert.Equal(reg_16.Index, iterator.GetRegisterOperand(0).Index);
                Assert.Equal(name_index, iterator.GetConstantPoolIndexOperand(1));
                Assert.Equal(feedback_slot, iterator.GetFeedbackSlotOperand(2));
                break;
            case 15:
                Assert.Equal(param.Index, iterator.GetRegisterOperand(0).Index);
                Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
                break;
            case 16:
                Assert.Equal(RuntimeFunctionId.LoadLookupSlotForCall, iterator.GetRuntimeIdOperand(0));
                Assert.Equal(param.Index, iterator.GetRegisterOperand(1).Index);
                Assert.Equal(1u, iterator.GetRegisterOperandRange(1));
                Assert.Equal(1u, iterator.GetRegisterCountOperand(2));
                Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(3).Index);
                Assert.Equal(2u, iterator.GetRegisterOperandRange(3));
                break;
            case 17:
                Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
                Assert.Equal(3u, iterator.GetRegisterOperandRange(0));
                Assert.Equal(feedback_slot, iterator.GetFeedbackSlotOperand(1));
                break;
            case 18:
                Assert.Equal(RuntimeFunctionId.LoadIC_Miss, iterator.GetRuntimeIdOperand(0));
                Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(1).Index);
                Assert.Equal(1u, iterator.GetRegisterCountOperand(2));
                break;
        }
        Assert.True(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_InvalidBeforeStart()
    {
        var iterator = new BytecodeArrayRandomIterator(BuildArray(false, out _));
        iterator.GoToStart();
        Assert.True(iterator.IsValid());
        --iterator;
        Assert.False(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_InvalidAfterEnd()
    {
        var iterator = new BytecodeArrayRandomIterator(BuildArray(false, out _));
        iterator.GoToEnd();
        Assert.True(iterator.IsValid());
        ++iterator;
        Assert.False(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_AccessesFirst()
    {
        var iterator = new BytecodeArrayRandomIterator(BuildArray(false, out _));
        iterator.GoToStart();

        Assert.Equal(Bytecode.LdaConstant, iterator.CurrentBytecode());
        Assert.Equal(0, iterator.CurrentIndex);
        Assert.Equal(0, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(heap_num_0, NumberValue(iterator.GetConstantForOperand(0)));
        Assert.True(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_AccessesLast()
    {
        BytecodeArray bytecodeArray = BuildArray(false, out _);
        var iterator = new BytecodeArrayRandomIterator(bytecodeArray);
        iterator.GoToEnd();

        int offset = bytecodeArray.Length - Bytecodes.Size(Bytecode.Return, OperandScale.Single);
        Assert.Equal(Bytecode.Return, iterator.CurrentBytecode());
        Assert.Equal(20, iterator.CurrentIndex);
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.True(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_RandomAccessValid()
    {
        var iterator = new BytecodeArrayRandomIterator(BuildArray(true, out Register reg_16));

        iterator.GoToIndex(11);
        Expected(iterator, 11, reg_16);

        iterator.GoToIndex(2);
        Expected(iterator, 2, reg_16);

        iterator.GoToIndex(16);
        Expected(iterator, 16, reg_16);

        iterator.RetreatBy(3);
        Expected(iterator, 13, reg_16);

        iterator.AdvanceBy(2);
        Expected(iterator, 15, reg_16);

        iterator.GoToIndex(20);
        Expected(iterator, 20, reg_16);

        iterator.GoToIndex(22);
        Assert.False(iterator.IsValid());

        iterator.GoToIndex(-5);
        Assert.False(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_IteratesBytecodeArray()
    {
        var iterator = new BytecodeArrayRandomIterator(BuildArray(true, out Register reg_16));
        for (int index = 0; index < Sequence.Length; index++)
        {
            Expected(iterator, index, reg_16);
            ++iterator;
        }
        Assert.False(iterator.IsValid());
    }

    [Fact]
    public void BytecodeArrayRandomIteratorTest_IteratesBytecodeArrayBackwards()
    {
        BytecodeArray bytecodeArray = BuildArray(true, out Register reg_16);
        var iterator = new BytecodeArrayRandomIterator(bytecodeArray);
        iterator.GoToEnd();
        Assert.Equal(bytecodeArray.Length - Bytecodes.Size(Bytecode.Return, OperandScale.Single),
                     iterator.CurrentOffset());
        for (int index = Sequence.Length - 1; index >= 0; index--)
        {
            Expected(iterator, index, reg_16);
            --iterator;
        }
        Assert.False(iterator.IsValid());
    }
}
