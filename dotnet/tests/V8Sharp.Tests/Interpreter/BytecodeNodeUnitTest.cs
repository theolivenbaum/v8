// Port of test/unittests/interpreter/bytecode-node-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeNodeUnitTest
{
    [Fact]
    public void BytecodeNodeTest_Constructor1()
    {
        var node = new BytecodeNode(Bytecode.LdaZero);
        Assert.Equal(Bytecode.LdaZero, node.Bytecode);
        Assert.Equal(0, node.OperandCount);
        Assert.False(node.SourceInfo.IsValid());
    }

    [Fact]
    public void BytecodeNodeTest_Constructor2()
    {
        uint[] operands = [0x11];
        var node = new BytecodeNode(Bytecode.JumpIfTrue, operands[0]);
        Assert.Equal(Bytecode.JumpIfTrue, node.Bytecode);
        Assert.Equal(1, node.OperandCount);
        Assert.Equal(operands[0], node.Operand(0));
        Assert.False(node.SourceInfo.IsValid());
    }

    [Fact]
    public void BytecodeNodeTest_Constructor3()
    {
        uint[] operands = [0x11, 0x22];
        var node = new BytecodeNode(Bytecode.LdaGlobal, operands[0], operands[1]);
        Assert.Equal(Bytecode.LdaGlobal, node.Bytecode);
        Assert.Equal(2, node.OperandCount);
        Assert.Equal(operands[0], node.Operand(0));
        Assert.Equal(operands[1], node.Operand(1));
        Assert.False(node.SourceInfo.IsValid());
    }

    [Fact]
    public void BytecodeNodeTest_Constructor4()
    {
        uint[] operands = [0x11, 0x22, 0x33];
        var node = new BytecodeNode(Bytecode.GetNamedProperty, operands[0], operands[1], operands[2]);
        Assert.Equal(3, node.OperandCount);
        Assert.Equal(Bytecode.GetNamedProperty, node.Bytecode);
        Assert.Equal(operands[0], node.Operand(0));
        Assert.Equal(operands[1], node.Operand(1));
        Assert.Equal(operands[2], node.Operand(2));
        Assert.False(node.SourceInfo.IsValid());
    }

    [Fact]
    public void BytecodeNodeTest_Constructor5()
    {
        uint[] operands = [0x71, 0xA5, 0x5A, 0xFC];
        var node = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3]);
        Assert.Equal(4, node.OperandCount);
        Assert.Equal(Bytecode.ForInNext, node.Bytecode);
        Assert.Equal(operands[0], node.Operand(0));
        Assert.Equal(operands[1], node.Operand(1));
        Assert.Equal(operands[2], node.Operand(2));
        Assert.Equal(operands[3], node.Operand(3));
        Assert.False(node.SourceInfo.IsValid());
    }

    [Fact]
    public void BytecodeNodeTest_Equality()
    {
        uint[] operands = [0x71, 0xA5, 0x5A, 0xFC];
        var node = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3]);
        Assert.Equal(node, node);
        var other = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3]);
        Assert.Equal(node, other);
    }

    [Fact]
    public void BytecodeNodeTest_EqualityWithSourceInfo()
    {
        uint[] operands = [0x71, 0xA5, 0x5A, 0xFC];
        var first_source_info = new BytecodeSourceInfo(3, true);
        var node = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3],
                                    first_source_info);
        Assert.Equal(node, node);
        var second_source_info = new BytecodeSourceInfo(3, true);
        var other = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3],
                                     second_source_info);
        Assert.Equal(node, other);
    }

    [Fact]
    public void BytecodeNodeTest_NoEqualityWithDifferentSourceInfo()
    {
        uint[] operands = [0x71, 0xA5, 0x5A, 0xFC];
        var source_info = new BytecodeSourceInfo(77, true);
        var node = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3],
                                    source_info);
        var other = new BytecodeNode(Bytecode.ForInNext, operands[0], operands[1], operands[2], operands[3]);
        Assert.NotEqual(node, other);
    }
}
