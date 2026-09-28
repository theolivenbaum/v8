// Port of test/unittests/interpreter/bytecode-array-writer-unittest.cc.
using V8Sharp.Codegen;
using V8Sharp.Interpreter;
using static V8Sharp.Tests.Interpreter.B;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeArrayWriterUnitTest
{
    readonly ConstantArrayBuilder _constantArrayBuilder = new();
    readonly BytecodeArrayWriter _bytecodeArrayWriter;

    public BytecodeArrayWriterUnitTest()
    {
        _bytecodeArrayWriter = new BytecodeArrayWriter(_constantArrayBuilder,
                                                       SourcePositionTableBuilder.RecordingMode.RECORD_SOURCE_POSITIONS);
    }

    BytecodeArrayWriter writer() => _bytecodeArrayWriter;
    ReadOnlySpan<byte> bytecodes() => writer().BytecodesSpan;
    ConstantArrayBuilder constant_array_builder() => _constantArrayBuilder;

    static BytecodeSourceInfo Info(int position, bool isStatement) => new(position, isStatement);

    void Write(Bytecode bytecode, BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, info);
        writer().Write(ref node);
    }

    void Write(Bytecode bytecode, uint operand0, BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, operand0, info);
        writer().Write(ref node);
    }

    void Write(Bytecode bytecode, uint operand0, uint operand1, BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, operand0, operand1, info);
        writer().Write(ref node);
    }

    void Write(Bytecode bytecode, uint operand0, uint operand1, uint operand2, BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, operand0, operand1, operand2, info);
        writer().Write(ref node);
    }

    void Write(Bytecode bytecode, uint operand0, uint operand1, uint operand2, uint operand3,
               BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, operand0, operand1, operand2, operand3, info);
        writer().Write(ref node);
    }

    void WriteJump(Bytecode bytecode, BytecodeLabel label, BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, 0, info);
        writer().WriteJump(ref node, label);
    }

    void WriteJump(Bytecode bytecode, BytecodeLabel label, uint operand1, uint operand2,
                   BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, 0, operand1, operand2, info);
        writer().WriteJump(ref node, label);
    }

    void WriteJumpLoop(Bytecode bytecode, BytecodeLoopHeader loopHeader, int depth, int feedbackIndex,
                       BytecodeSourceInfo info = default)
    {
        var node = new BytecodeNode(bytecode, 0, (uint)depth, (uint)feedbackIndex, info);
        writer().WriteJumpLoop(ref node, loopHeader);
    }

    void CheckBytes(ReadOnlySpan<byte> expected_bytes)
    {
        Assert.Equal(expected_bytes.Length, bytecodes().Length);
        for (int i = 0; i < expected_bytes.Length; ++i) Assert.Equal(expected_bytes[i], bytecodes()[i]);
    }

    void CheckPositions(int registerCount, (int CodeOffset, int SourcePosition, bool IsStatement)[] expected_positions)
    {
        BytecodeArray bytecode_array = writer().ToBytecodeArray(DefaultConstantPoolMaterializer.Instance,
                                                                registerCount, InterpreterConstants.kJSArgcReceiverSlots,
                                                                0, []);
        bytecode_array.SourcePositionTableOrNull = writer().ToSourcePositionTable();
        var source_iterator = new SourcePositionTableIterator(bytecode_array.SourcePositionTable);
        foreach (var expected in expected_positions)
        {
            Assert.Equal(expected.CodeOffset, source_iterator.CodeOffset());
            Assert.Equal(expected.SourcePosition, source_iterator.SourcePosition().ScriptOffset());
            Assert.Equal(expected.IsStatement, source_iterator.IsStatement());
            source_iterator.Advance();
        }
        Assert.True(source_iterator.Done());
    }

    [Fact]
    public void BytecodeArrayWriterUnittest_SimpleExample()
    {
        Assert.Equal(0, bytecodes().Length);

        Write(Bytecode.LdaSmi, 127, Info(55, true));
        Assert.Equal(2, bytecodes().Length);

        Write(Bytecode.Star, (uint)new Register(20).ToOperand());
        Assert.Equal(4, bytecodes().Length);

        Write(Bytecode.Ldar, (uint)new Register(200).ToOperand());
        Assert.Equal(8, bytecodes().Length);

        Write(Bytecode.Return, Info(70, true));
        Assert.Equal(9, bytecodes().Length);

        byte[] expected_bytes =
        [
            /*  0 55 S> */ Op(Bytecode.LdaSmi), U8(127),
            /*  2       */ Op(Bytecode.Star), R8(20),
            /*  4       */ Op(Bytecode.Wide), Op(Bytecode.Ldar), .. R16(200),
            /*  8 70 S> */ Op(Bytecode.Return),
        ];
        CheckBytes(expected_bytes);
        CheckPositions(201, [(0, 55, true), (8, 70, true)]);
        Assert.Equal(expected_bytes.Length, bytecodes().Length);
    }

    [Fact]
    public void BytecodeArrayWriterUnittest_ComplexExample()
    {
        byte[] expected_bytes =
        [
            /*  0 42 S> */ Op(Bytecode.LdaConstant), U8(0),
            /*  2 42 E> */ Op(Bytecode.Add), R8(1), U8(1),
            /*  4 68 S> */ Op(Bytecode.JumpIfUndefined), U8(36),
            /*  6       */ Op(Bytecode.JumpIfNull), U8(34),
            /*  8       */ Op(Bytecode.ToObject), R8(3),
            /* 10       */ Op(Bytecode.ForInPrepare), R8(3), U8(4),
            /* 13       */ Op(Bytecode.LdaZero),
            /* 14       */ Op(Bytecode.Star), R8(7),
            /* 16 63 S> */ Op(Bytecode.JumpIfForInDone), U8(24), R8(7), R8(6),
            /* 21       */ Op(Bytecode.ForInNext), R8(3), R8(7), R8(4), U8(1),
            /* 26       */ Op(Bytecode.JumpIfUndefined), U8(9),
            /* 28       */ Op(Bytecode.Star), R8(0),
            /* 30       */ Op(Bytecode.Ldar), R8(0),
            /* 32       */ Op(Bytecode.Star), R8(2),
            /* 34 85 S> */ Op(Bytecode.Return),
            /* 35       */ Op(Bytecode.ForInStep), R8(7),
            /* 39       */ Op(Bytecode.JumpLoop), U8(20), U8(0), U8(0),
            /* 43       */ Op(Bytecode.LdaUndefined),
            /* 44 85 S> */ Op(Bytecode.Return),
        ];

        var loop_header = new BytecodeLoopHeader();
        var jump_for_in = new BytecodeLabel();
        var jump_end_1 = new BytecodeLabel();
        var jump_end_2 = new BytecodeLabel();
        var jump_end_3 = new BytecodeLabel();

        constant_array_builder().Insert(Smi.Zero);

        Write(Bytecode.LdaConstant, U8(0), Info(42, true));
        Write(Bytecode.Add, R(1), U8(1), Info(42, false));
        WriteJump(Bytecode.JumpIfUndefined, jump_end_1, Info(68, true));
        WriteJump(Bytecode.JumpIfNull, jump_end_2);
        Write(Bytecode.ToObject, R(3));
        Write(Bytecode.ForInPrepare, R(3), U8(4));
        Write(Bytecode.LdaZero);
        Write(Bytecode.Star, R(7));
        writer().BindLoopHeader(loop_header);
        WriteJump(Bytecode.JumpIfForInDone, jump_end_3, R(7), R(6), Info(63, true));
        Write(Bytecode.ForInNext, R(3), R(7), R(4), U8(1));
        WriteJump(Bytecode.JumpIfUndefined, jump_for_in);
        Write(Bytecode.Star, R(0));
        Write(Bytecode.Ldar, R(0));
        Write(Bytecode.Star, R(2));
        Write(Bytecode.Return, Info(85, true));
        writer().BindLabel(jump_for_in);
        Write(Bytecode.ForInStep, R(7));
        WriteJumpLoop(Bytecode.JumpLoop, loop_header, 0, 0);
        writer().BindLabel(jump_end_1);
        writer().BindLabel(jump_end_2);
        writer().BindLabel(jump_end_3);
        Write(Bytecode.LdaUndefined);
        Write(Bytecode.Return, Info(85, true));

        CheckBytes(expected_bytes);
        CheckPositions(8, [(0, 42, true), (2, 42, false), (5, 68, true), (17, 63, true), (34, 85, true), (42, 85, true)]);
    }

    [Fact]
    public void BytecodeArrayWriterUnittest_ElideNoneffectfulBytecodes()
    {
        if (!InterpreterFlags.ignition_elide_noneffectful_bytecodes) return;

        byte[] expected_bytes =
        [
            /*  0  55 S> */ Op(Bytecode.Ldar), R8(20),
            /*  2        */ Op(Bytecode.Star), R8(20),
            /*  4        */ Op(Bytecode.CreateMappedArguments),
            /*  5  60 S> */ Op(Bytecode.LdaSmi), U8(127),
            /*  7  70 S> */ Op(Bytecode.Ldar), R8(20),
            /*  9 75 S> */ Op(Bytecode.Return),
        ];

        Write(Bytecode.LdaSmi, 127, Info(55, true)); // Should be elided.
        Write(Bytecode.Ldar, (uint)new Register(20).ToOperand());
        Write(Bytecode.Star, (uint)new Register(20).ToOperand());
        Write(Bytecode.Ldar, (uint)new Register(20).ToOperand()); // Should be elided.
        Write(Bytecode.CreateMappedArguments);
        Write(Bytecode.LdaSmi, 127, Info(60, false)); // Not elided due to source info.
        Write(Bytecode.Ldar, (uint)new Register(20).ToOperand(), Info(70, true));
        Write(Bytecode.Return, Info(75, true));

        CheckBytes(expected_bytes);
        CheckPositions(21, [(0, 55, true), (5, 60, false), (7, 70, true), (9, 75, true)]);
    }

    [Fact]
    public void BytecodeArrayWriterUnittest_DeadcodeElimination()
    {
        byte[] expected_bytes =
        [
            /*  0  55 S> */ Op(Bytecode.LdaSmi), U8(127),
            /*  2        */ Op(Bytecode.Jump), U8(2),
            /*  4  65 S> */ Op(Bytecode.LdaSmi), U8(127),
            /*  6        */ Op(Bytecode.JumpIfFalse), U8(3),
            /*  8  75 S> */ Op(Bytecode.Return),
            /*  9       */ Op(Bytecode.JumpIfFalse), U8(3),
            /*  11       */ Op(Bytecode.Throw),
            /*  12       */ Op(Bytecode.JumpIfFalse), U8(3),
            /*  14       */ Op(Bytecode.ReThrow),
            /*  15       */ Op(Bytecode.Return),
        ];

        var after_jump = new BytecodeLabel();
        var after_conditional_jump = new BytecodeLabel();
        var after_return = new BytecodeLabel();
        var after_throw = new BytecodeLabel();
        var after_rethrow = new BytecodeLabel();

        Write(Bytecode.LdaSmi, 127, Info(55, true));
        WriteJump(Bytecode.Jump, after_jump);
        Write(Bytecode.LdaSmi, 127);                             // Dead code.
        WriteJump(Bytecode.JumpIfFalse, after_conditional_jump); // Dead code.
        writer().BindLabel(after_jump);
        // We would bind the after_conditional_jump label here, but the jump to it is
        // dead.
        Assert.False(after_conditional_jump.HasReferrerJump);
        Write(Bytecode.LdaSmi, 127, Info(65, true));
        WriteJump(Bytecode.JumpIfFalse, after_return);
        Write(Bytecode.Return, Info(75, true));
        Write(Bytecode.LdaSmi, 127, Info(100, true)); // Dead code.
        writer().BindLabel(after_return);
        WriteJump(Bytecode.JumpIfFalse, after_throw);
        Write(Bytecode.Throw);
        Write(Bytecode.LdaSmi, 127); // Dead code.
        writer().BindLabel(after_throw);
        WriteJump(Bytecode.JumpIfFalse, after_rethrow);
        Write(Bytecode.ReThrow);
        Write(Bytecode.LdaSmi, 127); // Dead code.
        writer().BindLabel(after_rethrow);
        Write(Bytecode.Return);

        CheckBytes(expected_bytes);
        CheckPositions(0, [(0, 55, true), (4, 65, true), (8, 75, true)]);
    }

    [Fact]
    public void BytecodeArrayWriterUnittest_HandlerTableBuilderOverflowCheck()
    {
        var builder = new HandlerTableBuilder();
        int handler_id = builder.NewHandlerEntry();
        builder.SetTryRegionStart(handler_id, 0);
        builder.SetTryRegionEnd(handler_id, 10);
        builder.SetHandlerTarget(handler_id, HandlerTable.kLazyDeopt);
        byte[] table = builder.ToHandlerTable();
        Assert.True(table.Length > 0);

        var invalid_builder = new HandlerTableBuilder();
        int invalid_handler_id = invalid_builder.NewHandlerEntry();
        invalid_builder.SetTryRegionStart(invalid_handler_id, 0);
        invalid_builder.SetTryRegionEnd(invalid_handler_id, 10);
        invalid_builder.SetHandlerTarget(invalid_handler_id, HandlerTable.kLazyDeopt + 1);
        // V8: ASSERT_DEATH_IF_SUPPORTED (a CHECK failure).
        Assert.Throws<InvalidOperationException>(() => invalid_builder.ToHandlerTable());
    }
}
