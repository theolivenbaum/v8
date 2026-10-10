// Port of test/unittests/interpreter/bytecode-array-iterator-unittest.cc.
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeArrayIteratorUnitTest
{
    const int kFeedbackIsEmbedded = InterpreterConstants.kFeedbackIsEmbedded;

    static double NumberValue(object o) => o switch
    {
        Smi s => s.Value,
        double d => d,
        _ => throw new InvalidOperationException(),
    };

    [Fact]
    public void BytecodeArrayIteratorTest_IteratesBytecodeArray()
    {
        // Use a builder to create an array with containing multiple bytecodes
        // with 0, 1 and 2 operands.
        var builder = new BytecodeArrayBuilder(3, 17);
        double heap_num_0 = 2.718;
        double heap_num_1 = 2.0 * Smi.kMaxValue;
        Smi zero = Smi.Zero;
        Smi smi_0 = Smi.FromInt(64);
        Smi smi_1 = Smi.FromInt(-65536);
        var reg_0 = new Register(0);
        var reg_16 = new Register(16); // Something not eligible for short Star.
        RegisterList pair = BytecodeUtils.NewRegisterList(0, 2);
        RegisterList triple = BytecodeUtils.NewRegisterList(0, 3);
        Register param = Register.FromParameterIndex(2);
        const string name = "abc";
        uint name_index = 2;
        // FeedbackVectorSpec: a LoadIC slot takes two entries, ForIn one.
        uint load_feedback_slot = 0;
        uint forin_feedback_slot = 2;
        uint load_global_feedback_slot = 3;

        builder.LoadLiteral(heap_num_0)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(heap_num_1)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(zero)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(smi_0)
            .StoreAccumulatorInRegister(reg_0)
            .LoadLiteral(smi_1)
            .StoreAccumulatorInRegister(reg_16)
            .LoadAccumulatorWithRegister(reg_0)
            .BinaryOperation(Token.Add, reg_0, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(reg_16)
            .LoadNamedProperty(reg_16, name, (int)load_feedback_slot)
            .BinaryOperation(Token.Add, reg_0, kFeedbackIsEmbedded)
            .StoreAccumulatorInRegister(param)
            .CallRuntimeForPair(FunctionId.LoadLookupSlotForCall, param, pair)
            .ForInPrepare(triple, (int)forin_feedback_slot)
            .CallRuntime(FunctionId.LoadIC_Miss, reg_0)
            .Debugger()
            .LoadGlobal(name, (int)load_global_feedback_slot, TypeofMode.NotInside)
            .Return();

        // Test iterator sees the expected output from the builder.
        var iterator = new BytecodeArrayIterator(builder.ToBytecodeArray());
        const int kPrefixByteSize = 1;
        int offset = 0;

        Assert.Equal(Bytecode.LdaConstant, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(heap_num_0, NumberValue(iterator.GetConstantForOperand(0)));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.LdaConstant, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star0, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star0, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.LdaConstant, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(heap_num_1, NumberValue(iterator.GetConstantForOperand(0)));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.LdaConstant, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star0, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star0, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.LdaZero, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.LdaZero, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star0, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star0, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.LdaSmi, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(smi_0, Smi.FromInt(iterator.GetImmediateOperand(0)));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.LdaSmi, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star0, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star0, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.LdaSmi, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Quadruple, iterator.CurrentOperandScale());
        Assert.Equal(smi_1, Smi.FromInt(iterator.GetImmediateOperand(0)));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.LdaSmi, OperandScale.Quadruple) + kPrefixByteSize;
        iterator.Advance();

        Assert.Equal(Bytecode.Star, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_16.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Ldar, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Ldar, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Add, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Add, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_16.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.GetNamedProperty, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_16.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(name_index, iterator.GetConstantPoolIndexOperand(1));
        Assert.Equal(load_feedback_slot, iterator.GetFeedbackSlotOperand(2));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.GetNamedProperty, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Add, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Add, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Star, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(param.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(0));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Star, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.CallRuntimeForPair, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(FunctionId.LoadLookupSlotForCall, iterator.GetRuntimeIdOperand(0));
        Assert.Equal(param.Index, iterator.GetRegisterOperand(1).Index);
        Assert.Equal(1u, iterator.GetRegisterOperandRange(1));
        Assert.Equal(1u, iterator.GetRegisterCountOperand(2));
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(3).Index);
        Assert.Equal(2u, iterator.GetRegisterOperandRange(3));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.CallRuntimeForPair, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.ForInPrepare, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(0).Index);
        Assert.Equal(3u, iterator.GetRegisterOperandRange(0));
        Assert.Equal(forin_feedback_slot, iterator.GetFeedbackSlotOperand(1));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.ForInPrepare, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.CallRuntime, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(FunctionId.LoadIC_Miss, iterator.GetRuntimeIdOperand(0));
        Assert.Equal(reg_0.Index, iterator.GetRegisterOperand(1).Index);
        Assert.Equal(1u, iterator.GetRegisterCountOperand(2));
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.CallRuntime, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Debugger, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        offset += Bytecodes.Size(Bytecode.Debugger, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.LdaGlobal, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.Equal(3, iterator.CurrentBytecodeSize());
        Assert.Equal(load_global_feedback_slot, iterator.GetFeedbackSlotOperand(1));
        offset += Bytecodes.Size(Bytecode.LdaGlobal, OperandScale.Single);
        iterator.Advance();

        Assert.Equal(Bytecode.Return, iterator.CurrentBytecode());
        Assert.Equal(offset, iterator.CurrentOffset());
        Assert.Equal(OperandScale.Single, iterator.CurrentOperandScale());
        Assert.False(iterator.Done());
        iterator.Advance();
        Assert.True(iterator.Done());
    }
}
