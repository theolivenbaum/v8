// Port of src/interpreter/bytecode-array-builder.h/.cc.
//
// AST-typed parameters: V8 passes `const AstRawString*`, `const AstConsString*`,
// `AstBigInt` and `const Scope*`. V8Sharp.Parsing cannot reference the engine,
// so these are `object` here, compared by identity in the constant pool:
//   - `object name` / `object rawString` / `object pattern`: an AstRawString
//   - LoadLiteralConsString(object), LoadLiteralBigInt(object), LoadLiteralScope(object)
//   - `object scope`: a Scope; where V8 reads scope->has_context_cells() the
//     caller passes it as a bool.
//   - `Variable*` for LoadContextSlot/StoreContextSlot is a ContextSlotVariable.
// TODO(merge): the BytecodeGenerator port passes its AST objects straight through.
using V8Sharp.Ast;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using System.Runtime.CompilerServices;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

/// <summary>What LoadContextSlot/StoreContextSlot read from a V8 Variable:
/// variable->index(), variable->maybe_assigned(), variable->scope()->has_context_cells().</summary>
public readonly record struct ContextSlotVariable(int Index, MaybeAssignedFlag MaybeAssigned, bool ScopeHasContextCells);

public sealed class BytecodeArrayBuilder
{
    public enum ToBooleanMode
    {
        ConvertToBoolean, // Perform ToBoolean conversion on accumulator.
        AlreadyBoolean,   // Accumulator is already a Boolean.
    }

    /// <summary>JavaScript defines two kinds of 'nil'.</summary>
    public enum NilValue { NullValue, UndefinedValue }

    sealed class RegisterTransferWriter(BytecodeArrayBuilder builder) : BytecodeRegisterOptimizer.IBytecodeWriter
    {
        public void EmitLdar(Register input) => builder.OutputLdarRaw(input);
        public void EmitStar(Register output) => builder.OutputStarRaw(output);
        public void EmitMov(Register input, Register output) => builder.OutputMovRaw(input, output);
    }

    bool _bytecodeGenerated;
    readonly ConstantArrayBuilder _constantArrayBuilder = new();
    readonly HandlerTableBuilder _handlerTableBuilder = new();
    readonly ushort _parameterCount;
    ushort _maxArguments;
    readonly int _localRegisterCount;
    readonly BytecodeRegisterAllocator _registerAllocator;
    readonly BytecodeArrayWriter _bytecodeArrayWriter;
    readonly BytecodeRegisterOptimizer? _registerOptimizer;
    BytecodeSourceInfo _latestSourceInfo;
    BytecodeSourceInfo _deferredSourceInfo;
    int _potentiallyThrowingBytecodeCount;

    // TODO(merge): V8 also takes a FeedbackVectorSpec*, used only in DCHECKs that
    // the typeof / language mode agrees with the IC slot kind.
    public BytecodeArrayBuilder(
        int parameterCount, int localsCount,
        SourcePositionTableBuilder.RecordingMode sourcePositionMode =
            SourcePositionTableBuilder.RecordingMode.RECORD_SOURCE_POSITIONS)
    {
        _parameterCount = checked((ushort)parameterCount);
        _localRegisterCount = localsCount;
        _registerAllocator = new BytecodeRegisterAllocator(FixedRegisterCount());
        _bytecodeArrayWriter = new BytecodeArrayWriter(_constantArrayBuilder, sourcePositionMode);
        Debug.Assert(_parameterCount >= InterpreterConstants.kJSArgcReceiverSlots);
        Debug.Assert(_localRegisterCount >= 0);

        if (InterpreterFlags.ignition_reo)
        {
            _registerOptimizer = new BytecodeRegisterOptimizer(_registerAllocator, FixedRegisterCount(),
                                                               parameterCount, new RegisterTransferWriter(this));
        }
    }

    /// <summary>The number of parameters expected by the function (including the receiver).</summary>
    public ushort ParameterCount() => _parameterCount;
    public ushort MaxArguments() => _maxArguments;

    public void UpdateMaxArguments(int maxArguments)
    {
        if (maxArguments > InterpreterConstants.kMaxArguments)
            throw new InvalidOperationException("Check failed: max_arguments <= Code::kMaxArguments");
        _maxArguments = Math.Max(_maxArguments, (ushort)maxArguments);
    }

    /// <summary>The number of locals required for the bytecode array.</summary>
    public int LocalsCount()
    {
        Debug.Assert(_localRegisterCount >= 0);
        return _localRegisterCount;
    }

    public int PotentiallyThrowingBytecodeCount() => _potentiallyThrowingBytecodeCount;

    /// <summary>The number of fixed (non-temporary) registers.</summary>
    public int FixedRegisterCount() => LocalsCount();

    /// <summary>The number of fixed and temporary registers.</summary>
    public int TotalRegisterCount()
    {
        Debug.Assert(FixedRegisterCount() <= _registerAllocator.MaximumRegisterCount());
        return _registerAllocator.MaximumRegisterCount();
    }

    public Register Local(int index)
    {
        Debug.Assert(index < LocalsCount());
        return new Register(index);
    }

    public Register Parameter(int parameterIndex)
    {
        Debug.Assert(parameterIndex >= 0);
        // The parameter indices are shifted by 1 (receiver is the first entry).
        return Register.FromParameterIndex(parameterIndex + 1);
    }

    public Register Receiver() => Register.FromParameterIndex(0);

    public BytecodeArray ToBytecodeArray(IConstantPoolMaterializer? materializer = null)
    {
        Debug.Assert(RemainderOfBlockIsDead());
        Debug.Assert(!_bytecodeGenerated);
        _bytecodeGenerated = true;

        int register_count = TotalRegisterCount();

        if (_registerOptimizer is not null)
        {
            _registerOptimizer.Flush();
            register_count = _registerOptimizer.MaximumRegisterIndex() + 1;
        }

        byte[] handler_table = _handlerTableBuilder.ToHandlerTable();
        return _bytecodeArrayWriter.ToBytecodeArray(materializer ?? DefaultConstantPoolMaterializer.Instance,
                                                    register_count, ParameterCount(), MaxArguments(), handler_table);
    }

    public byte[] ToSourcePositionTable()
    {
        Debug.Assert(RemainderOfBlockIsDead());
        return _bytecodeArrayWriter.ToSourcePositionTable();
    }

    public int CheckBytecodeMatches(BytecodeArray bytecode) => _bytecodeArrayWriter.CheckBytecodeMatches(bytecode);

    // ---- Source positions -------------------------------------------------

    /// <summary>Returns the current source position for the given |bytecode|.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    BytecodeSourceInfo CurrentSourcePosition(Bytecode bytecode)
    {
        BytecodeSourceInfo source_position = default;
        if (_latestSourceInfo.IsValid())
        {
            // Statement positions need to be emitted immediately.  Expression
            // positions can be pushed back until a bytecode is found that can
            // throw (if expression position filtering is turned on). We only
            // invalidate the existing source position information if it is used.
            if (_latestSourceInfo.IsStatement() || !InterpreterFlags.ignition_filter_expression_positions ||
                !Bytecodes.IsWithoutExternalSideEffects(bytecode) || Bytecodes.IsJumpIfToBoolean(bytecode))
            {
                source_position = _latestSourceInfo;
                _latestSourceInfo.SetInvalid();
            }
        }
        return source_position;
    }

    void SetDeferredSourceInfo(BytecodeSourceInfo sourceInfo)
    {
        if (!sourceInfo.IsValid()) return;
        _deferredSourceInfo = sourceInfo;
    }

    void AttachOrEmitDeferredSourceInfo(ref BytecodeNode node)
    {
        if (!_deferredSourceInfo.IsValid()) return;
        if (!node.SourceInfo.IsValid())
        {
            node.SetSourceInfo(_deferredSourceInfo);
        }
        else if (_deferredSourceInfo.IsStatement() && node.SourceInfo.IsExpression())
        {
            BytecodeSourceInfo source_position = node.SourceInfo;
            source_position.MakeStatementPosition(source_position.SourcePosition());
            node.SetSourceInfo(source_position);
        }
        _deferredSourceInfo.SetInvalid();
    }

    public BytecodeSourceInfo? MaybePopSourcePosition(int scopeStart)
    {
        if (!_latestSourceInfo.IsValid() || _latestSourceInfo.SourcePosition() < scopeStart) return null;
        BytecodeSourceInfo source_info = _latestSourceInfo;
        _latestSourceInfo.SetInvalid();
        return source_info;
    }

    public void PushSourcePosition(BytecodeSourceInfo sourceInfo)
    {
        Debug.Assert(!_latestSourceInfo.IsValid());
        _latestSourceInfo = sourceInfo;
    }

    public void SetStatementPosition(int position, bool isBreakable = true)
    {
        if (position == InterpreterConstants.kNoSourcePosition) return;
        _latestSourceInfo.MakeStatementPosition(position, isBreakable);
    }

    public void SetExpressionPosition(int position)
    {
        if (position == InterpreterConstants.kNoSourcePosition) return;
        if (!_latestSourceInfo.IsStatement())
        {
            // Ensure the current expression position is overwritten with the
            // latest value.
            _latestSourceInfo.MakeExpressionPosition(position);
        }
    }

    /// <summary>SetExpressionAsStatementPosition(expr): a statement position at the expression's position.</summary>
    public void SetExpressionAsStatementPosition(int position, bool isBreakable = true) =>
        SetStatementPosition(position, isBreakable);

    public bool RemainderOfBlockIsDead() => _bytecodeArrayWriter.RemainderOfBlockIsDead();

    public void EmitFunctionStartSourcePosition(int position)
    {
        _bytecodeArrayWriter.SetFunctionEntrySourcePosition(position);
        // Force an expression position to make sure we have one. If the next bytecode
        // overwrites it, it's fine since it would mean we have a source position
        // anyway.
        _latestSourceInfo.ForceExpressionPosition(position);
    }

    public int CurrentBytecodeSize() => _bytecodeArrayWriter.CurrentBytecodeSize();

    // ---- Writing ------------------------------------------------------------

    void Write(ref BytecodeNode node)
    {
        AttachOrEmitDeferredSourceInfo(ref node);
        if (!Bytecodes.IsWithoutExternalSideEffects(node.Bytecode)) _potentiallyThrowingBytecodeCount++;
        _bytecodeArrayWriter.Write(ref node);
    }

    void WriteJump(ref BytecodeNode node, BytecodeLabel label)
    {
        AttachOrEmitDeferredSourceInfo(ref node);
        Debug.Assert(Bytecodes.IsWithoutExternalSideEffects(node.Bytecode) || Bytecodes.IsJumpIfToBoolean(node.Bytecode));
        _bytecodeArrayWriter.WriteJump(ref node, label);
    }

    void WriteJumpLoop(ref BytecodeNode node, BytecodeLoopHeader loopHeader)
    {
        AttachOrEmitDeferredSourceInfo(ref node);
        // JumpLoop can throw due to interrupt checks (stack overflow, termination).
        _potentiallyThrowingBytecodeCount++;
        _bytecodeArrayWriter.WriteJumpLoop(ref node, loopHeader);
    }

    void WriteSwitch(ref BytecodeNode node, BytecodeJumpTable jumpTable)
    {
        AttachOrEmitDeferredSourceInfo(ref node);
        Debug.Assert(Bytecodes.IsWithoutExternalSideEffects(node.Bytecode));
        _bytecodeArrayWriter.WriteSwitch(ref node, jumpTable);
    }

    /// <summary>Outputs a raw register transfer bytecode without going through the register optimizer.</summary>
    public void OutputLdarRaw(Register reg)
    {
        uint operand = (uint)reg.ToOperand();
        var node = new BytecodeNode(Bytecode.Ldar, operand);
        Write(ref node);
    }

    public void OutputStarRaw(Register reg)
    {
        uint operand = (uint)reg.ToOperand();
        Bytecode? short_code = reg.TryToShortStar();
        BytecodeNode node = short_code is { } code ? new BytecodeNode(code) : new BytecodeNode(Bytecode.Star, operand);
        Write(ref node);
    }

    public void OutputMovRaw(Register src, Register dest)
    {
        uint operand0 = (uint)src.ToOperand();
        uint operand1 = (uint)dest.ToOperand();
        var node = new BytecodeNode(Bytecode.Mov, operand0, operand1);
        Write(ref node);
    }

    /// <summary>
    /// An operand value handed to the generic output path; how it is converted
    /// is decided by the bytecode's operand type (V8's OperandHelper&lt;type&gt;).
    /// </summary>
    readonly struct Op
    {
        public readonly int A;
        public readonly int B;
        Op(int a, int b)
        {
            A = a;
            B = b;
        }

        public static implicit operator Op(Register reg) => new(reg.Index, 1);
        public static implicit operator Op(RegisterList list) => new(list.FirstIndexRaw, list.RegisterCount);
        public static implicit operator Op(int value) => new(value, 0);
        public static implicit operator Op(uint value) => new((int)value, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint Convert(Bytecode bytecode, int index, Op op)
    {
        OperandType type = Bytecodes.GetOperandType(bytecode, index);
        switch (type)
        {
            case OperandType.Reg:
                return GetInputRegisterOperand(new Register(op.A));
            case OperandType.RegList:
                return GetInputRegisterListOperand(new RegisterList(op.A, op.B));
            case OperandType.RegPair:
                Debug.Assert(op.B == 2);
                return GetInputRegisterListOperand(new RegisterList(op.A, op.B));
            case OperandType.RegOut:
                return GetOutputRegisterOperand(new Register(op.A));
            case OperandType.RegOutList:
                return GetOutputRegisterListOperand(new RegisterList(op.A, op.B));
            case OperandType.RegOutPair:
                Debug.Assert(op.B == 2);
                return GetOutputRegisterListOperand(new RegisterList(op.A, op.B));
            case OperandType.RegOutTriple:
                Debug.Assert(op.B == 3);
                return GetOutputRegisterListOperand(new RegisterList(op.A, op.B));
            case OperandType.RegInOut:
                return GetInputOutputRegisterOperand(new Register(op.A));
            case OperandType.Imm:
                return (uint)op.A;
            default:
                // Unsigned operands (UnsignedOperandHelper).
                Debug.Assert(IsValidUnsigned(type, (uint)op.A));
                return (uint)op.A;
        }
    }

    static bool IsValidUnsigned(OperandType type, uint value) => BytecodeOperands.GetOperandTypeInfo(type) switch
    {
        OperandTypeInfo.FixedUnsignedByte => value <= byte.MaxValue,
        OperandTypeInfo.FixedUnsignedShort => value <= ushort.MaxValue,
        OperandTypeInfo.ScalableUnsignedByte => true,
        _ => false,
    };

    // The BytecodeNodeBuilder<...>::Make and Create##Name##Node machinery:
    // prepare the register optimizer, convert the operands (left to right, as
    // clang evaluates V8's argument pack), attach the current source position.
    BytecodeNode CreateNode(Bytecode bytecode)
    {
        PrepareToOutputBytecode(bytecode);
        return new BytecodeNode(bytecode, CurrentSourcePosition(bytecode));
    }

    BytecodeNode CreateNode(Bytecode bytecode, Op o0)
    {
        PrepareToOutputBytecode(bytecode);
        BytecodeSourceInfo source = CurrentSourcePosition(bytecode);
        uint c0 = Convert(bytecode, 0, o0);
        return new BytecodeNode(bytecode, c0, source);
    }

    BytecodeNode CreateNode(Bytecode bytecode, Op o0, Op o1)
    {
        PrepareToOutputBytecode(bytecode);
        BytecodeSourceInfo source = CurrentSourcePosition(bytecode);
        uint c0 = Convert(bytecode, 0, o0);
        uint c1 = Convert(bytecode, 1, o1);
        return new BytecodeNode(bytecode, c0, c1, source);
    }

    BytecodeNode CreateNode(Bytecode bytecode, Op o0, Op o1, Op o2)
    {
        PrepareToOutputBytecode(bytecode);
        BytecodeSourceInfo source = CurrentSourcePosition(bytecode);
        uint c0 = Convert(bytecode, 0, o0);
        uint c1 = Convert(bytecode, 1, o1);
        uint c2 = Convert(bytecode, 2, o2);
        return new BytecodeNode(bytecode, c0, c1, c2, source);
    }

    BytecodeNode CreateNode(Bytecode bytecode, Op o0, Op o1, Op o2, Op o3)
    {
        PrepareToOutputBytecode(bytecode);
        BytecodeSourceInfo source = CurrentSourcePosition(bytecode);
        uint c0 = Convert(bytecode, 0, o0);
        uint c1 = Convert(bytecode, 1, o1);
        uint c2 = Convert(bytecode, 2, o2);
        uint c3 = Convert(bytecode, 3, o3);
        return new BytecodeNode(bytecode, c0, c1, c2, c3, source);
    }

    BytecodeNode CreateNode(Bytecode bytecode, Op o0, Op o1, Op o2, Op o3, Op o4)
    {
        PrepareToOutputBytecode(bytecode);
        BytecodeSourceInfo source = CurrentSourcePosition(bytecode);
        uint c0 = Convert(bytecode, 0, o0);
        uint c1 = Convert(bytecode, 1, o1);
        uint c2 = Convert(bytecode, 2, o2);
        uint c3 = Convert(bytecode, 3, o3);
        uint c4 = Convert(bytecode, 4, o4);
        return new BytecodeNode(bytecode, c0, c1, c2, c3, c4, source);
    }

    void Output(Bytecode bytecode)
    {
        BytecodeNode node = CreateNode(bytecode);
        Write(ref node);
    }

    void Output(Bytecode bytecode, Op o0)
    {
        BytecodeNode node = CreateNode(bytecode, o0);
        Write(ref node);
    }

    void Output(Bytecode bytecode, Op o0, Op o1)
    {
        BytecodeNode node = CreateNode(bytecode, o0, o1);
        Write(ref node);
    }

    void Output(Bytecode bytecode, Op o0, Op o1, Op o2)
    {
        BytecodeNode node = CreateNode(bytecode, o0, o1, o2);
        Write(ref node);
    }

    void Output(Bytecode bytecode, Op o0, Op o1, Op o2, Op o3)
    {
        BytecodeNode node = CreateNode(bytecode, o0, o1, o2, o3);
        Write(ref node);
    }

    void Output(Bytecode bytecode, Op o0, Op o1, Op o2, Op o3, Op o4)
    {
        BytecodeNode node = CreateNode(bytecode, o0, o1, o2, o3, o4);
        Write(ref node);
    }

    void OutputJump(Bytecode bytecode, BytecodeLabel label, Op o0)
    {
        Debug.Assert(Bytecodes.IsForwardJump(bytecode));
        BytecodeNode node = CreateNode(bytecode, o0);
        WriteJump(ref node, label);
    }

    void OutputJump(Bytecode bytecode, BytecodeLabel label, Op o0, Op o1, Op o2)
    {
        Debug.Assert(Bytecodes.IsForwardJump(bytecode));
        BytecodeNode node = CreateNode(bytecode, o0, o1, o2);
        WriteJump(ref node, label);
    }

    void OutputJumpLoop(BytecodeLoopHeader loopHeader, int loopDepth, int feedbackSlot)
    {
        BytecodeNode node = CreateNode(Bytecode.JumpLoop, 0, loopDepth, feedbackSlot);
        WriteJumpLoop(ref node, loopHeader);
    }

    void OutputSwitchOnSmiNoFeedback(BytecodeJumpTable jumpTable)
    {
        BytecodeNode node = CreateNode(Bytecode.SwitchOnSmiNoFeedback, jumpTable.ConstantPoolIndex, jumpTable.Size,
                                       jumpTable.CaseValueBase);
        WriteSwitch(ref node, jumpTable);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PrepareToOutputBytecode(Bytecode bytecode) =>
        _registerOptimizer?.PrepareForBytecode(bytecode, Bytecodes.GetImplicitRegisterUse(bytecode));

    // ---- Operators ----------------------------------------------------------

    /// <summary>Binary operators (register holds the lhs value, accumulator holds the rhs value).</summary>
    public BytecodeArrayBuilder BinaryOperation(Token op, Register reg, int feedbackSlot)
    {
        // feedback is embedded into bytecode array for binary operations
        Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
        const int fb = InterpreterConstants.kUninitializedEmbeddedFeedback;
        switch (op)
        {
            case Token.Add: Output(Bytecode.Add, reg, fb); break;
            case Token.Sub: Output(Bytecode.Sub, reg, fb); break;
            case Token.Mul: Output(Bytecode.Mul, reg, fb); break;
            case Token.Div: Output(Bytecode.Div, reg, fb); break;
            case Token.Mod: Output(Bytecode.Mod, reg, fb); break;
            case Token.Exp: Output(Bytecode.Exp, reg, fb); break;
            case Token.BitOr: Output(Bytecode.BitwiseOr, reg, fb); break;
            case Token.BitXor: Output(Bytecode.BitwiseXor, reg, fb); break;
            case Token.BitAnd: Output(Bytecode.BitwiseAnd, reg, fb); break;
            case Token.Shl: Output(Bytecode.ShiftLeft, reg, fb); break;
            case Token.Sar: Output(Bytecode.ShiftRight, reg, fb); break;
            case Token.Shr: Output(Bytecode.ShiftRightLogical, reg, fb); break;
            default: throw new UnreachableException();
        }
        return this;
    }

    public BytecodeArrayBuilder Add_StringConstant_Internalize(Token op, Register reg, int feedbackSlot,
                                                               AddStringConstantAndInternalizeVariant asVariant)
    {
        Debug.Assert(op == Token.Add);
        Output(Bytecode.Add_StringConstant_Internalize, reg, feedbackSlot, (int)asVariant);
        return this;
    }

    /// <summary>Same as BinaryOperation, but lhs in the accumulator and rhs in |literal|.</summary>
    public BytecodeArrayBuilder BinaryOperationSmiLiteral(Token op, Smi literal, int feedbackSlot)
    {
        Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
        const int fb = InterpreterConstants.kUninitializedEmbeddedFeedback;
        int v = literal.Value;
        switch (op)
        {
            case Token.Add: Output(Bytecode.AddSmi, v, fb); break;
            case Token.Sub: Output(Bytecode.SubSmi, v, fb); break;
            case Token.Mul: Output(Bytecode.MulSmi, v, fb); break;
            case Token.Div: Output(Bytecode.DivSmi, v, fb); break;
            case Token.Mod: Output(Bytecode.ModSmi, v, fb); break;
            case Token.Exp: Output(Bytecode.ExpSmi, v, fb); break;
            case Token.BitOr: Output(Bytecode.BitwiseOrSmi, v, fb); break;
            case Token.BitXor: Output(Bytecode.BitwiseXorSmi, v, fb); break;
            case Token.BitAnd: Output(Bytecode.BitwiseAndSmi, v, fb); break;
            case Token.Shl: Output(Bytecode.ShiftLeftSmi, v, fb); break;
            case Token.Sar: Output(Bytecode.ShiftRightSmi, v, fb); break;
            case Token.Shr: Output(Bytecode.ShiftRightLogicalSmi, v, fb); break;
            default: throw new UnreachableException();
        }
        return this;
    }

    /// <summary>Unary and Count Operators (value stored in accumulator).</summary>
    public BytecodeArrayBuilder UnaryOperation(Token op, int feedbackSlot)
    {
        const int fb = InterpreterConstants.kUninitializedEmbeddedFeedback;
        switch (op)
        {
            case Token.Inc:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.Inc, fb);
                break;
            case Token.Dec:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.Dec, fb);
                break;
            case Token.Add:
                Output(Bytecode.ToNumber, feedbackSlot);
                break;
            case Token.Sub:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.Negate, fb);
                break;
            case Token.BitNot:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.BitwiseNot, fb);
                break;
            default:
                throw new UnreachableException();
        }
        return this;
    }

    public BytecodeArrayBuilder LogicalNot(ToBooleanMode mode)
    {
        if (mode == ToBooleanMode.AlreadyBoolean)
        {
            Output(Bytecode.LogicalNot);
        }
        else
        {
            Debug.Assert(mode == ToBooleanMode.ConvertToBoolean);
            Output(Bytecode.ToBooleanLogicalNot);
        }
        return this;
    }

    public BytecodeArrayBuilder TypeOf(int feedbackSlot)
    {
        Output(Bytecode.TypeOf, feedbackSlot);
        return this;
    }

    /// <summary>Expects a heap object in the accumulator. Returns its super
    /// constructor in the register |out| if it passes the IsConstructor test.
    /// Otherwise, it throws a TypeError exception.</summary>
    public BytecodeArrayBuilder GetSuperConstructor(Register @out)
    {
        Output(Bytecode.GetSuperConstructor, @out);
        return this;
    }

    public BytecodeArrayBuilder FindNonDefaultConstructorOrConstruct(Register thisFunction, Register newTarget,
                                                                     RegisterList output)
    {
        Output(Bytecode.FindNonDefaultConstructorOrConstruct, thisFunction, newTarget, output);
        return this;
    }

    public BytecodeArrayBuilder CompareOperation(Token op, Register reg, int feedbackSlot)
    {
        const int fb = InterpreterConstants.kUninitializedEmbeddedFeedback;
        switch (op)
        {
            case Token.Eq:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestEqual, reg, fb);
                break;
            case Token.EqStrict:
                // feedback is embedded into bytecode array for strict equal
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestEqualStrict, reg, fb);
                break;
            case Token.LessThan:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestLessThan, reg, fb);
                break;
            case Token.GreaterThan:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestGreaterThan, reg, fb);
                break;
            case Token.LessThanEq:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestLessThanOrEqual, reg, fb);
                break;
            case Token.GreaterThanEq:
                Debug.Assert(feedbackSlot == InterpreterConstants.kFeedbackIsEmbedded);
                Output(Bytecode.TestGreaterThanOrEqual, reg, fb);
                break;
            case Token.InstanceOf:
                Output(Bytecode.TestInstanceOf, reg, feedbackSlot);
                break;
            case Token.In:
                Output(Bytecode.TestIn, reg, feedbackSlot);
                break;
            default:
                throw new UnreachableException();
        }
        return this;
    }

    public BytecodeArrayBuilder CompareReference(Register reg)
    {
        Output(Bytecode.TestReferenceEqual, reg);
        return this;
    }

    public BytecodeArrayBuilder CompareUndetectable()
    {
        Output(Bytecode.TestUndetectable);
        return this;
    }

    public BytecodeArrayBuilder CompareUndefined()
    {
        Output(Bytecode.TestUndefined);
        return this;
    }

    public BytecodeArrayBuilder CompareNull()
    {
        Output(Bytecode.TestNull);
        return this;
    }

    public BytecodeArrayBuilder CompareNil(Token op, NilValue nil)
    {
        if (op == Token.Eq) return CompareUndetectable();
        Debug.Assert(op == Token.EqStrict);
        if (nil == NilValue.UndefinedValue) return CompareUndefined();
        Debug.Assert(nil == NilValue.NullValue);
        return CompareNull();
    }

    public BytecodeArrayBuilder CompareTypeOf(TestTypeOfFlags.LiteralFlag literalFlag)
    {
        Debug.Assert(literalFlag != TestTypeOfFlags.LiteralFlag.Other);
        Output(Bytecode.TestTypeOf, (int)TestTypeOfFlags.Encode(literalFlag));
        return this;
    }

    // ---- Constant loads -----------------------------------------------------

    public BytecodeArrayBuilder LoadConstantPoolEntry(int entry)
    {
        Output(Bytecode.LdaConstant, entry);
        return this;
    }

    public BytecodeArrayBuilder LoadLiteral(Smi smi)
    {
        int raw_smi = smi.Value;
        if (raw_smi == 0) Output(Bytecode.LdaZero);
        else Output(Bytecode.LdaSmi, raw_smi);
        return this;
    }

    public BytecodeArrayBuilder LoadLiteral(double value)
    {
        // If we can encode the value as a Smi, we should.
        if (DoubleToSmiInteger(value, out int smi))
        {
            LoadLiteral(Smi.FromInt(smi));
        }
        else
        {
            int entry = GetConstantPoolEntry(value);
            Output(Bytecode.LdaConstant, entry);
        }
        return this;
    }

    /// <summary>DoubleToSmiInteger (src/numbers/conversions-inl.h): an integral,
    /// non-minus-zero value in Smi range.</summary>
    static bool DoubleToSmiInteger(double value, out int smi)
    {
        smi = 0;
        if (double.IsNegative(value) && value == 0) return false;
        if (!(value >= Smi.kMinValue && value <= Smi.kMaxValue)) return false;
        int i = (int)value;
        if (i != value) return false;
        smi = i;
        return true;
    }

    /// <summary>LoadLiteral(const AstRawString*).</summary>
    public BytecodeArrayBuilder LoadLiteralRawString(object rawString)
    {
        int entry = GetConstantPoolEntry(rawString);
        Output(Bytecode.LdaConstant, entry);
        return this;
    }

    /// <summary>LoadLiteral(const AstConsString*).</summary>
    public BytecodeArrayBuilder LoadLiteralConsString(object consString)
    {
        int entry = GetConstantPoolEntryForConsString(consString);
        Output(Bytecode.LdaConstant, entry);
        return this;
    }

    /// <summary>LoadLiteral(const Scope*).</summary>
    public BytecodeArrayBuilder LoadLiteralScope(object scope)
    {
        int entry = GetConstantPoolEntryForScope(scope);
        Output(Bytecode.LdaConstant, entry);
        return this;
    }

    /// <summary>LoadLiteral(AstBigInt).</summary>
    public BytecodeArrayBuilder LoadLiteralBigInt(object bigint)
    {
        int entry = GetConstantPoolEntryForBigInt(bigint);
        Output(Bytecode.LdaConstant, entry);
        return this;
    }

    public BytecodeArrayBuilder LoadUndefined()
    {
        Output(Bytecode.LdaUndefined);
        return this;
    }

    public BytecodeArrayBuilder LoadNull()
    {
        Output(Bytecode.LdaNull);
        return this;
    }

    public BytecodeArrayBuilder LoadTheHole()
    {
        Output(Bytecode.LdaTheHole);
        return this;
    }

    public BytecodeArrayBuilder LoadTdzHole()
    {
        Output(Bytecode.LdaTdzHole);
        return this;
    }

    public BytecodeArrayBuilder LoadTrue()
    {
        Output(Bytecode.LdaTrue);
        return this;
    }

    public BytecodeArrayBuilder LoadFalse()
    {
        Output(Bytecode.LdaFalse);
        return this;
    }

    public BytecodeArrayBuilder LoadBoolean(bool value) => value ? LoadTrue() : LoadFalse();

    // ---- Register transfers ---------------------------------------------------

    public BytecodeArrayBuilder LoadAccumulatorWithRegister(Register reg)
    {
        if (_registerOptimizer is not null)
        {
            // Defer source info so that if we elide the bytecode transfer, we attach
            // the source info to a subsequent bytecode if it exists.
            SetDeferredSourceInfo(CurrentSourcePosition(Bytecode.Ldar));
            _registerOptimizer.DoLdar(reg);
        }
        else
        {
            Output(Bytecode.Ldar, reg);
        }
        return this;
    }

    public BytecodeArrayBuilder StoreAccumulatorInRegister(Register reg)
    {
        if (_registerOptimizer is not null)
        {
            // Defer source info so that if we elide the bytecode transfer, we attach
            // the source info to a subsequent bytecode if it exists.
            SetDeferredSourceInfo(CurrentSourcePosition(Bytecode.Star));
            _registerOptimizer.DoStar(reg);
        }
        else
        {
            OutputStarRaw(reg);
        }
        return this;
    }

    public BytecodeArrayBuilder MoveRegister(Register from, Register to)
    {
        Debug.Assert(from != to);
        if (_registerOptimizer is not null)
        {
            // Defer source info so that if we elide the bytecode transfer, we attach
            // the source info to a subsequent bytecode if it exists.
            SetDeferredSourceInfo(CurrentSourcePosition(Bytecode.Mov));
            _registerOptimizer.DoMov(from, to);
        }
        else
        {
            Output(Bytecode.Mov, from, to);
        }
        return this;
    }

    // ---- Globals, contexts, properties -----------------------------------------

    /// <summary>Merges the boilerplate definition at index_obj into the prototype
    /// of the function object in the accumulator.</summary>
    public BytecodeArrayBuilder SetPrototypeProperties(int indexObj, int slot)
    {
        Output(Bytecode.SetPrototypeProperties, indexObj, slot);
        return this;
    }

    public BytecodeArrayBuilder LoadGlobal(object name, int feedbackSlot, TypeofMode typeofMode)
    {
        int name_index = GetConstantPoolEntry(name);
        switch (typeofMode)
        {
            case TypeofMode.Inside:
                Output(Bytecode.LdaGlobalInsideTypeof, name_index, feedbackSlot);
                break;
            case TypeofMode.NotInside:
                Output(Bytecode.LdaGlobal, name_index, feedbackSlot);
                break;
        }
        return this;
    }

    public BytecodeArrayBuilder StoreGlobal(object name, int feedbackSlot)
    {
        int name_index = GetConstantPoolEntry(name);
        Output(Bytecode.StaGlobal, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Load the object at |variable| at |depth| in the context chain
    /// starting with |context| into the accumulator.</summary>
    public BytecodeArrayBuilder LoadContextSlot(Register context, ContextSlotVariable variable, int depth)
    {
        int slot_index = variable.Index;
        if (variable.MaybeAssigned == MaybeAssignedFlag.kNotAssigned)
        {
            if (context.IsCurrentContext && depth == 0)
                Output(Bytecode.LdaImmutableCurrentContextSlot, slot_index);
            else
                Output(Bytecode.LdaImmutableContextSlot, context, slot_index, depth);
        }
        else if (variable.ScopeHasContextCells)
        {
            if (context.IsCurrentContext && depth == 0)
                Output(Bytecode.LdaCurrentContextSlot, slot_index);
            else
                Output(Bytecode.LdaContextSlot, context, slot_index, depth);
        }
        else
        {
            if (context.IsCurrentContext && depth == 0)
                Output(Bytecode.LdaCurrentContextSlotNoCell, slot_index);
            else
                Output(Bytecode.LdaContextSlotNoCell, context, slot_index, depth);
        }
        return this;
    }

    /// <summary>Stores the object in the accumulator into |variable| at |depth|
    /// in the context chain starting with |context|.</summary>
    public BytecodeArrayBuilder StoreContextSlot(Register context, ContextSlotVariable variable, int depth)
    {
        int slot_index = variable.Index;
        if (variable.MaybeAssigned != MaybeAssignedFlag.kNotAssigned && variable.ScopeHasContextCells)
        {
            if (context.IsCurrentContext && depth == 0)
                Output(Bytecode.StaCurrentContextSlot, slot_index);
            else
                Output(Bytecode.StaContextSlot, context, slot_index, depth);
        }
        else
        {
            if (context.IsCurrentContext && depth == 0)
                Output(Bytecode.StaCurrentContextSlotNoCell, slot_index);
            else
                Output(Bytecode.StaContextSlotNoCell, context, slot_index, depth);
        }
        return this;
    }

    public BytecodeArrayBuilder LoadLookupSlot(object name, TypeofMode typeofMode)
    {
        int name_index = GetConstantPoolEntry(name);
        if (typeofMode == TypeofMode.Inside) Output(Bytecode.LdaLookupSlotInsideTypeof, name_index);
        else Output(Bytecode.LdaLookupSlot, name_index);
        return this;
    }

    /// <summary>Lookup the variable with |name|, which is known to be at
    /// |slotIndex| at |depth| in the context chain if not shadowed by a context
    /// extension somewhere in that context chain.</summary>
    public BytecodeArrayBuilder LoadLookupContextSlot(object name, TypeofMode typeofMode, ContextMode contextMode,
                                                      int slotIndex, int depth)
    {
        int name_index = GetConstantPoolEntry(name);
        switch (typeofMode)
        {
            case TypeofMode.Inside:
                if (contextMode == ContextMode.HasContextCells)
                    Output(Bytecode.LdaLookupContextSlotInsideTypeof, name_index, slotIndex, depth);
                else
                    Output(Bytecode.LdaLookupContextSlotNoCellInsideTypeof, name_index, slotIndex, depth);
                break;
            case TypeofMode.NotInside:
                if (contextMode == ContextMode.HasContextCells)
                    Output(Bytecode.LdaLookupContextSlot, name_index, slotIndex, depth);
                else
                    Output(Bytecode.LdaLookupContextSlotNoCell, name_index, slotIndex, depth);
                break;
        }
        return this;
    }

    /// <summary>Lookup the variable with |name|, which has its feedback in
    /// |feedbackSlot| and is known to be global if not shadowed by a context
    /// extension somewhere up to |depth| in that context chain.</summary>
    public BytecodeArrayBuilder LoadLookupGlobalSlot(object name, TypeofMode typeofMode, int feedbackSlot, int depth)
    {
        int name_index = GetConstantPoolEntry(name);
        if (typeofMode == TypeofMode.Inside)
            Output(Bytecode.LdaLookupGlobalSlotInsideTypeof, name_index, feedbackSlot, depth);
        else
            Output(Bytecode.LdaLookupGlobalSlot, name_index, feedbackSlot, depth);
        return this;
    }

    /// <summary>Store value in the accumulator into the variable with |name|.</summary>
    public BytecodeArrayBuilder StoreLookupSlot(object name, LanguageMode languageMode,
                                                LookupHoistingMode lookupHoistingMode)
    {
        int name_index = GetConstantPoolEntry(name);
        byte flags = StoreLookupSlotFlags.Encode(languageMode, lookupHoistingMode);
        Output(Bytecode.StaLookupSlot, name_index, (int)flags);
        return this;
    }

    public BytecodeArrayBuilder LoadNamedProperty(Register @object, object name, int feedbackSlot)
    {
        int name_index = GetConstantPoolEntry(name);
        Output(Bytecode.GetNamedProperty, @object, name_index, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder LoadNamedPropertyFromSuper(Register @object, object name, int feedbackSlot)
    {
        int name_index = GetConstantPoolEntry(name);
        Output(Bytecode.GetNamedPropertyFromSuper, @object, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Keyed load property. The key should be in the accumulator.</summary>
    public BytecodeArrayBuilder LoadKeyedProperty(Register @object, int feedbackSlot)
    {
        Output(Bytecode.GetKeyedProperty, @object, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder LoadEnumeratedKeyedProperty(Register @object, Register enumIndex, Register cacheType,
                                                            int feedbackSlot)
    {
        Output(Bytecode.GetEnumeratedKeyedProperty, @object, enumIndex, cacheType, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder GetPrivateField(Register context, int slotIndex, int depth, Register @object,
                                                int feedbackSlot)
    {
        Output(Bytecode.GetPrivateField, context, slotIndex, depth, @object, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder SetPrivateField(Register context, int slotIndex, int depth, Register @object,
                                                int feedbackSlot)
    {
        Output(Bytecode.SetPrivateField, context, slotIndex, depth, @object, feedbackSlot);
        return this;
    }

    /// <summary>Named load property of the @@iterator symbol.</summary>
    public BytecodeArrayBuilder LoadIteratorProperty(Register @object, int feedbackSlot)
    {
        int name_index = IteratorSymbolConstantPoolEntry();
        Output(Bytecode.GetNamedProperty, @object, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Load and call property of the @@iterator symbol.</summary>
    public BytecodeArrayBuilder GetIterator(Register @object, int loadFeedbackSlot, int callFeedbackSlot)
    {
        Output(Bytecode.GetIterator, @object, loadFeedbackSlot, callFeedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder ArrayDestructure(RegisterList outputs, int count)
    {
        Output(Bytecode.ArrayDestructure, outputs, count);
        return this;
    }

    public BytecodeArrayBuilder ForOfNext(Register @object, Register next, int callSlot)
    {
        Output(Bytecode.ForOfNext, @object, next, callSlot);
        return this;
    }

    /// <summary>Named load property of the @@asyncIterator symbol.</summary>
    public BytecodeArrayBuilder LoadAsyncIteratorProperty(Register @object, int feedbackSlot)
    {
        int name_index = AsyncIteratorSymbolConstantPoolEntry();
        Output(Bytecode.GetNamedProperty, @object, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Store properties. Flag for NeedsSetFunctionName() should be in the accumulator.</summary>
    public BytecodeArrayBuilder DefineKeyedOwnPropertyInLiteral(Register @object, Register name,
                                                                DefineKeyedOwnPropertyInLiteralFlags flags,
                                                                int feedbackSlot)
    {
        Output(Bytecode.DefineKeyedOwnPropertyInLiteral, @object, name, (int)flags, feedbackSlot);
        return this;
    }

    /// <summary>Set a property named by a constant from the constant pool,
    /// trigger the setters and set traps if necessary. The value to be set
    /// should be in the accumulator.</summary>
    public BytecodeArrayBuilder SetNamedProperty(Register @object, int constantPoolEntry, int feedbackSlot,
                                                 LanguageMode languageMode)
    {
        // TODO(merge): V8 DCHECKs that language mode is in sync with the IC slot kind.
        Output(Bytecode.SetNamedProperty, @object, constantPoolEntry, feedbackSlot);
        return this;
    }

    /// <summary>Set a property named by a property name.</summary>
    public BytecodeArrayBuilder SetNamedProperty(Register @object, object name, int feedbackSlot,
                                                 LanguageMode languageMode)
    {
        int name_index = GetConstantPoolEntry(name);
        return SetNamedProperty(@object, name_index, feedbackSlot, languageMode);
    }

    /// <summary>Define an own property named by a constant from the constant pool.</summary>
    public BytecodeArrayBuilder DefineNamedOwnProperty(Register @object, object name, int feedbackSlot)
    {
        int name_index = GetConstantPoolEntry(name);
        Output(Bytecode.DefineNamedOwnProperty, @object, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Set a property keyed by a value in a register.</summary>
    public BytecodeArrayBuilder SetKeyedProperty(Register @object, Register key, int feedbackSlot,
                                                 LanguageMode languageMode)
    {
        Output(Bytecode.SetKeyedProperty, @object, key, feedbackSlot);
        return this;
    }

    /// <summary>Define an own property keyed by a value in a register.</summary>
    public BytecodeArrayBuilder DefineKeyedOwnProperty(Register @object, Register key, DefineKeyedOwnPropertyFlags flags,
                                                       int feedbackSlot)
    {
        Output(Bytecode.DefineKeyedOwnProperty, @object, key, (int)flags, feedbackSlot);
        return this;
    }

    /// <summary>Store an own element in an array literal.</summary>
    public BytecodeArrayBuilder StoreInArrayLiteral(Register array, Register index, int feedbackSlot)
    {
        Output(Bytecode.StaInArrayLiteral, array, index, feedbackSlot);
        return this;
    }

    /// <summary>Store the class fields property. The initializer should be in the accumulator.</summary>
    public BytecodeArrayBuilder StoreClassFieldsInitializer(Register constructor, int feedbackSlot)
    {
        int name_index = ClassFieldsSymbolConstantPoolEntry();
        return SetNamedProperty(constructor, name_index, feedbackSlot, LanguageMode.Strict);
    }

    /// <summary>Load class fields property.</summary>
    public BytecodeArrayBuilder LoadClassFieldsInitializer(Register constructor, int feedbackSlot)
    {
        int name_index = ClassFieldsSymbolConstantPoolEntry();
        Output(Bytecode.GetNamedProperty, constructor, name_index, feedbackSlot);
        return this;
    }

    /// <summary>Create a new closure for a SharedFunctionInfo which will be
    /// inserted at constant pool index |sharedFunctionInfoEntry|.</summary>
    public BytecodeArrayBuilder CreateClosure(int sharedFunctionInfoEntry, int slot, int flags)
    {
        Output(Bytecode.CreateClosure, sharedFunctionInfoEntry, slot, flags);
        return this;
    }

    /// <summary>Create a new local context for a |scope|.</summary>
    public BytecodeArrayBuilder CreateBlockContext(object scope)
    {
        int entry = GetConstantPoolEntryForScope(scope);
        Output(Bytecode.CreateBlockContext, entry);
        return this;
    }

    /// <summary>Create a new context for a catch block with |exception| and |scope|.</summary>
    public BytecodeArrayBuilder CreateCatchContext(Register exception, object scope)
    {
        int scope_index = GetConstantPoolEntryForScope(scope);
        Output(Bytecode.CreateCatchContext, exception, scope_index);
        return this;
    }

    /// <summary>Create a new context with the given |scope| and size |slots|.
    /// |scopeHasContextCells| is scope->has_context_cells().</summary>
    public BytecodeArrayBuilder CreateFunctionContext(object scope, int slots, bool scopeHasContextCells)
    {
        int scope_index = GetConstantPoolEntryForScope(scope);
        if (scopeHasContextCells) Output(Bytecode.CreateFunctionContextWithCells, scope_index, slots);
        else Output(Bytecode.CreateFunctionContext, scope_index, slots);
        return this;
    }

    /// <summary>Create a new eval context with the given |scope| and size |slots|.</summary>
    public BytecodeArrayBuilder CreateEvalContext(object scope, int slots)
    {
        int scope_index = GetConstantPoolEntryForScope(scope);
        Output(Bytecode.CreateEvalContext, scope_index, slots);
        return this;
    }

    /// <summary>Creates a new context with the given |scope| for a with-statement
    /// with the |object| in a register.</summary>
    public BytecodeArrayBuilder CreateWithContext(Register @object, object scope)
    {
        int scope_index = GetConstantPoolEntryForScope(scope);
        Output(Bytecode.CreateWithContext, @object, scope_index);
        return this;
    }

    /// <summary>Create a new arguments object in the accumulator.</summary>
    public BytecodeArrayBuilder CreateArguments(CreateArgumentsType type)
    {
        switch (type)
        {
            case CreateArgumentsType.kMappedArguments: Output(Bytecode.CreateMappedArguments); break;
            case CreateArgumentsType.kUnmappedArguments: Output(Bytecode.CreateUnmappedArguments); break;
            case CreateArgumentsType.kRestParameter: Output(Bytecode.CreateRestParameter); break;
            default: throw new UnreachableException();
        }
        return this;
    }

    // ---- Literals -----------------------------------------------------------

    public BytecodeArrayBuilder CreateRegExpLiteral(object pattern, int literalIndex, int flags)
    {
        int pattern_entry = GetConstantPoolEntry(pattern);
        Output(Bytecode.CreateRegExpLiteral, pattern_entry, literalIndex, flags);
        return this;
    }

    public BytecodeArrayBuilder CreateArrayLiteral(int constantElementsEntry, int literalIndex, int flags)
    {
        Output(Bytecode.CreateArrayLiteral, constantElementsEntry, literalIndex, flags);
        return this;
    }

    public BytecodeArrayBuilder CreateEmptyArrayLiteral(int literalIndex)
    {
        Output(Bytecode.CreateEmptyArrayLiteral, literalIndex);
        return this;
    }

    public BytecodeArrayBuilder CreateArrayFromIterable()
    {
        Output(Bytecode.CreateArrayFromIterable);
        return this;
    }

    public BytecodeArrayBuilder CreateObjectLiteral(int constantPropertiesEntry, int literalIndex, int flags)
    {
        Output(Bytecode.CreateObjectLiteral, constantPropertiesEntry, literalIndex, flags);
        return this;
    }

    public BytecodeArrayBuilder CreateEmptyObjectLiteral()
    {
        Output(Bytecode.CreateEmptyObjectLiteral);
        return this;
    }

    public BytecodeArrayBuilder CloneObject(Register source, int flags, int feedbackSlot)
    {
        Output(Bytecode.CloneObject, source, flags, feedbackSlot);
        return this;
    }

    /// <summary>Gets or creates the template for a TemplateObjectDescription which
    /// will be inserted at constant pool index |templateObjectDescriptionEntry|.</summary>
    public BytecodeArrayBuilder GetTemplateObject(int templateObjectDescriptionEntry, int feedbackSlot)
    {
        Output(Bytecode.GetTemplateObject, templateObjectDescriptionEntry, feedbackSlot);
        return this;
    }

    /// <summary>Push the context in accumulator as the new context, and store in register |context|.</summary>
    public BytecodeArrayBuilder PushContext(Register context)
    {
        Output(Bytecode.PushContext, context);
        return this;
    }

    /// <summary>Pop the current context and replace with |context|.</summary>
    public BytecodeArrayBuilder PopContext(Register context)
    {
        Output(Bytecode.PopContext, context);
        return this;
    }

    // ---- Conversions --------------------------------------------------------

    public BytecodeArrayBuilder ToObject(Register @out)
    {
        Output(Bytecode.ToObject, @out);
        return this;
    }

    public BytecodeArrayBuilder ToName()
    {
        Output(Bytecode.ToName);
        return this;
    }

    /// <summary>ToString (hides object.ToString, as V8's builder method of the same name).</summary>
    public new BytecodeArrayBuilder ToString()
    {
        Output(Bytecode.ToString);
        return this;
    }

    public BytecodeArrayBuilder ToBoolean(ToBooleanMode mode)
    {
        if (mode == ToBooleanMode.AlreadyBoolean)
        {
            // No-op, the accumulator is already a boolean and ToBoolean both reads and
            // writes the accumulator.
        }
        else
        {
            Debug.Assert(mode == ToBooleanMode.ConvertToBoolean);
            Output(Bytecode.ToBoolean);
        }
        return this;
    }

    public BytecodeArrayBuilder ToNumber(int feedbackSlot)
    {
        Output(Bytecode.ToNumber, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder ToNumeric(int feedbackSlot)
    {
        Output(Bytecode.ToNumeric, feedbackSlot);
        return this;
    }

    // ---- Flow control -------------------------------------------------------

    public BytecodeArrayBuilder Bind(BytecodeLabel label)
    {
        // Don't generate code for a label which hasn't had a corresponding forward
        // jump generated already. For backwards jumps, use BindLoopHeader.
        if (!label.HasReferrerJump) return this;

        // Flush the register optimizer when binding a label to ensure all
        // expected registers are valid when jumping to this label.
        if (_registerOptimizer is not null)
        {
            _registerOptimizer.Flush();
            _registerOptimizer.ResetTypeHintForAccumulator();
        }
        _bytecodeArrayWriter.BindLabel(label);
        return this;
    }

    public BytecodeArrayBuilder Bind(BytecodeLoopHeader loopHeader)
    {
        // Flush the register optimizer when starting a loop to ensure all expected
        // registers are valid when jumping to the loop header.
        if (_registerOptimizer is not null)
        {
            _registerOptimizer.Flush();
            _registerOptimizer.ResetTypeHintForAccumulator();
        }
        _bytecodeArrayWriter.BindLoopHeader(loopHeader);
        return this;
    }

    public BytecodeArrayBuilder Bind(BytecodeJumpTable jumpTable, int caseValue)
    {
        // Flush the register optimizer when binding a jump table entry to ensure
        // all expected registers are valid when jumping to this location.
        if (_registerOptimizer is not null)
        {
            _registerOptimizer.Flush();
            _registerOptimizer.ResetTypeHintForAccumulator();
        }
        _bytecodeArrayWriter.BindJumpTableEntry(jumpTable, caseValue);
        return this;
    }

    public BytecodeArrayBuilder MarkHandler(int handlerId, HandlerTable.CatchPrediction catchPrediction)
    {
        // The handler starts a new basic block, and any reasonable try block won't
        // let control fall through into it.
        Debug.Assert(_registerOptimizer is null || _registerOptimizer.EnsureAllRegistersAreFlushed());
        Debug.Assert(_registerOptimizer is null || _registerOptimizer.IsAccumulatorReset());
        _bytecodeArrayWriter.BindHandlerTarget(_handlerTableBuilder, handlerId);
        _handlerTableBuilder.SetPrediction(handlerId, catchPrediction);
        return this;
    }

    public BytecodeArrayBuilder MarkTryBegin(int handlerId, Register context)
    {
        // Flush registers to make sure everything visible to the handler is
        // materialized.
        _registerOptimizer?.Flush();
        _bytecodeArrayWriter.BindTryRegionStart(_handlerTableBuilder, handlerId);
        _handlerTableBuilder.SetContextRegister(handlerId, context);
        return this;
    }

    public BytecodeArrayBuilder MarkTryEnd(int handlerId)
    {
        _registerOptimizer?.ResetTypeHintForAccumulator();
        _bytecodeArrayWriter.BindTryRegionEnd(_handlerTableBuilder, handlerId);
        return this;
    }

    public BytecodeArrayBuilder DropHandlerEntry(int handlerId)
    {
        _handlerTableBuilder.DropHandlerEntry(handlerId);
        return this;
    }

    public BytecodeArrayBuilder Jump(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.Jump, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfTrue(ToBooleanMode mode, BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        if (mode == ToBooleanMode.AlreadyBoolean)
        {
            OutputJump(Bytecode.JumpIfTrue, label, 0);
        }
        else
        {
            Debug.Assert(mode == ToBooleanMode.ConvertToBoolean);
            OutputJump(Bytecode.JumpIfToBooleanTrue, label, 0);
        }
        return this;
    }

    public BytecodeArrayBuilder JumpIfFalse(ToBooleanMode mode, BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        if (mode == ToBooleanMode.AlreadyBoolean)
        {
            OutputJump(Bytecode.JumpIfFalse, label, 0);
        }
        else
        {
            Debug.Assert(mode == ToBooleanMode.ConvertToBoolean);
            OutputJump(Bytecode.JumpIfToBooleanFalse, label, 0);
        }
        return this;
    }

    public BytecodeArrayBuilder JumpIfNull(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfNull, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfNotNull(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfNotNull, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfUndefined(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfUndefined, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfUndefinedOrNull(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfUndefinedOrNull, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfNotUndefined(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfNotUndefined, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfNil(BytecodeLabel label, Token op, NilValue nil)
    {
        if (op == Token.Eq)
        {
            // TODO(rmcilroy): Implement JumpIfUndetectable.
            return CompareUndetectable().JumpIfTrue(ToBooleanMode.AlreadyBoolean, label);
        }
        Debug.Assert(op == Token.EqStrict);
        if (nil == NilValue.UndefinedValue) return JumpIfUndefined(label);
        Debug.Assert(nil == NilValue.NullValue);
        return JumpIfNull(label);
    }

    public BytecodeArrayBuilder JumpIfNotNil(BytecodeLabel label, Token op, NilValue nil)
    {
        if (op == Token.Eq)
        {
            // TODO(rmcilroy): Implement JumpIfUndetectable.
            return CompareUndetectable().JumpIfFalse(ToBooleanMode.AlreadyBoolean, label);
        }
        Debug.Assert(op == Token.EqStrict);
        if (nil == NilValue.UndefinedValue) return JumpIfNotUndefined(label);
        Debug.Assert(nil == NilValue.NullValue);
        return JumpIfNotNull(label);
    }

    public BytecodeArrayBuilder JumpIfJSReceiver(BytecodeLabel label)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfJSReceiver, label, 0);
        return this;
    }

    public BytecodeArrayBuilder JumpIfForInDone(BytecodeLabel label, Register index, Register cacheLength)
    {
        Debug.Assert(!label.IsBound);
        OutputJump(Bytecode.JumpIfForInDone, label, 0, index, cacheLength);
        return this;
    }

    public BytecodeArrayBuilder JumpLoop(BytecodeLoopHeader loopHeader, int loopDepth, int position, int feedbackSlot)
    {
        if (position != InterpreterConstants.kNoSourcePosition)
        {
            // We need to attach a non-breakable source position to JumpLoop for its
            // implicit stack check, so we simply add it as expression position. There
            // can be a prior statement position from constructs like:
            //
            //    do var x;  while (false);
            //
            // A Nop could be inserted for empty statements, but since no code
            // is associated with these positions, instead we force the jump loop's
            // expression position which eliminates the empty statement's position.
            _latestSourceInfo.ForceExpressionPosition(position);
        }
        OutputJumpLoop(loopHeader, loopDepth, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder SwitchOnSmiNoFeedback(BytecodeJumpTable jumpTable)
    {
        OutputSwitchOnSmiNoFeedback(jumpTable);
        return this;
    }

    /// <summary>Sets the pending message to the value in the accumulator, and
    /// returns the previous pending message in the accumulator.</summary>
    public BytecodeArrayBuilder SetPendingMessage()
    {
        Output(Bytecode.SetPendingMessage);
        return this;
    }

    public BytecodeArrayBuilder Throw()
    {
        Output(Bytecode.Throw);
        return this;
    }

    public BytecodeArrayBuilder ReThrow()
    {
        Output(Bytecode.ReThrow);
        return this;
    }

    public BytecodeArrayBuilder Abort(AbortReason reason)
    {
        Debug.Assert(reason < AbortReason.LastErrorMessage);
        Output(Bytecode.Abort, (int)reason);
        return this;
    }

    public BytecodeArrayBuilder Return()
    {
        Output(Bytecode.Return);
        return this;
    }

    public BytecodeArrayBuilder ThrowReferenceErrorIfTdzHole(object name)
    {
        int entry = GetConstantPoolEntry(name);
        Output(Bytecode.ThrowReferenceErrorIfTdzHole, entry);
        return this;
    }

    public BytecodeArrayBuilder ThrowSuperNotCalledIfTdzHole()
    {
        Output(Bytecode.ThrowSuperNotCalledIfTdzHole);
        return this;
    }

    public BytecodeArrayBuilder ThrowSuperAlreadyCalledIfNotTdzHole()
    {
        Output(Bytecode.ThrowSuperAlreadyCalledIfNotTdzHole);
        return this;
    }

    public BytecodeArrayBuilder ThrowIfNotSuperConstructor(Register constructor)
    {
        Output(Bytecode.ThrowIfNotSuperConstructor, constructor);
        return this;
    }

    public BytecodeArrayBuilder Debugger()
    {
        Output(Bytecode.Debugger);
        return this;
    }

    /// <summary>Increment the block counter at the given slot (block code coverage).</summary>
    public BytecodeArrayBuilder IncBlockCounter(int coverageArraySlot)
    {
        Output(Bytecode.IncBlockCounter, coverageArraySlot);
        return this;
    }

    // ---- Complex flow control -----------------------------------------------

    public BytecodeArrayBuilder ForInEnumerate(Register receiver)
    {
        Output(Bytecode.ForInEnumerate, receiver);
        return this;
    }

    public BytecodeArrayBuilder ForInPrepare(RegisterList cacheInfoTriple, int feedbackSlot)
    {
        Debug.Assert(cacheInfoTriple.RegisterCount == 3);
        Output(Bytecode.ForInPrepare, cacheInfoTriple, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder ForInNext(Register receiver, Register index, RegisterList cacheTypeArrayPair,
                                          int feedbackSlot)
    {
        Debug.Assert(cacheTypeArrayPair.RegisterCount == 2);
        Output(Bytecode.ForInNext, receiver, index, cacheTypeArrayPair, feedbackSlot);
        return this;
    }

    public BytecodeArrayBuilder ForInStep(Register index)
    {
        Output(Bytecode.ForInStep, index);
        return this;
    }

    public BytecodeArrayBuilder StoreModuleVariable(int cellIndex, int depth)
    {
        Output(Bytecode.StaModuleVariable, cellIndex, depth);
        return this;
    }

    public BytecodeArrayBuilder LoadModuleVariable(int cellIndex, int depth)
    {
        Output(Bytecode.LdaModuleVariable, cellIndex, depth);
        return this;
    }

    // ---- Generators ---------------------------------------------------------

    public BytecodeArrayBuilder SuspendGenerator(Register generator, RegisterList registers, int suspendId)
    {
        Output(Bytecode.SuspendGenerator, generator, registers, registers.RegisterCount, suspendId);
        return this;
    }

    public BytecodeArrayBuilder SwitchOnGeneratorState(Register generator, BytecodeJumpTable jumpTable)
    {
        Debug.Assert(jumpTable.CaseValueBase == 0);
        BytecodeNode node = CreateNode(Bytecode.SwitchOnGeneratorState, generator, jumpTable.ConstantPoolIndex,
                                       jumpTable.Size);
        WriteSwitch(ref node, jumpTable);
        return this;
    }

    public BytecodeArrayBuilder ResumeGenerator(Register generator, RegisterList registers)
    {
        Output(Bytecode.ResumeGenerator, generator, registers, registers.RegisterCount);
        return this;
    }

    // ---- Calls --------------------------------------------------------------

    /// <summary>Call a JS function which is known to be a property of a JS object.
    /// The arguments should be in |args|, with the receiver in |args[0]|.</summary>
    public BytecodeArrayBuilder CallProperty(Register callable, RegisterList args, int feedbackSlot)
    {
        if (args.RegisterCount == 1)
            Output(Bytecode.CallProperty0, callable, args[0], feedbackSlot);
        else if (args.RegisterCount == 2)
            Output(Bytecode.CallProperty1, callable, args[0], args[1], feedbackSlot);
        else if (args.RegisterCount == 3)
            Output(Bytecode.CallProperty2, callable, args[0], args[1], args[2], feedbackSlot);
        else
            Output(Bytecode.CallProperty, callable, args, args.RegisterCount, feedbackSlot);
        return this;
    }

    /// <summary>Call a JS function with a known undefined receiver.</summary>
    public BytecodeArrayBuilder CallUndefinedReceiver(Register callable, RegisterList args, int feedbackSlot)
    {
        if (args.RegisterCount == 0)
            Output(Bytecode.CallUndefinedReceiver0, callable, feedbackSlot);
        else if (args.RegisterCount == 1)
            Output(Bytecode.CallUndefinedReceiver1, callable, args[0], feedbackSlot);
        else if (args.RegisterCount == 2)
            Output(Bytecode.CallUndefinedReceiver2, callable, args[0], args[1], feedbackSlot);
        else
            Output(Bytecode.CallUndefinedReceiver, callable, args, args.RegisterCount, feedbackSlot);
        return this;
    }

    /// <summary>Call a JS function with any receiver, possibly (but not necessarily) undefined.</summary>
    public BytecodeArrayBuilder CallAnyReceiver(Register callable, RegisterList args, int feedbackSlot)
    {
        Output(Bytecode.CallAnyReceiver, callable, args, args.RegisterCount, feedbackSlot);
        return this;
    }

    /// <summary>Call a JS function whose final argument is a spread.</summary>
    public BytecodeArrayBuilder CallWithSpread(Register callable, RegisterList args, int feedbackSlot)
    {
        Output(Bytecode.CallWithSpread, callable, args, args.RegisterCount, feedbackSlot);
        return this;
    }

    /// <summary>Call the Construct operator. The accumulator holds the |new_target|.</summary>
    public BytecodeArrayBuilder Construct(Register constructor, RegisterList args, int feedbackSlotId)
    {
        Output(Bytecode.Construct, constructor, args, args.RegisterCount, feedbackSlotId);
        return this;
    }

    public BytecodeArrayBuilder ConstructWithSpread(Register constructor, RegisterList args, int feedbackSlotId)
    {
        Output(Bytecode.ConstructWithSpread, constructor, args, args.RegisterCount, feedbackSlotId);
        return this;
    }

    /// <summary>Call the Construct operator, forwarding all arguments passed to the
    /// current interpreted frame, including the receiver.</summary>
    public BytecodeArrayBuilder ConstructForwardAllArgs(Register constructor, int feedbackSlotId)
    {
        Output(Bytecode.ConstructForwardAllArgs, constructor, feedbackSlotId);
        return this;
    }

    /// <summary>Call the runtime function with |functionId| and arguments |args|.</summary>
    public BytecodeArrayBuilder CallRuntime(FunctionId functionId, RegisterList args)
    {
        Debug.Assert(RuntimeFunctions.ResultSize(functionId) == 1);
        Debug.Assert(Bytecodes.SizeForUnsignedOperand((uint)functionId) <= OperandSize.Short);
        UpdateMaxArguments(args.RegisterCount);
        if (IntrinsicsHelper.IsSupported(functionId))
        {
            IntrinsicsHelper.IntrinsicId intrinsic_id = IntrinsicsHelper.FromRuntimeId(functionId);
            Output(Bytecode.InvokeIntrinsic, (int)intrinsic_id, args, args.RegisterCount);
        }
        else
        {
            Output(Bytecode.CallRuntime, (int)functionId, args, args.RegisterCount);
        }
        return this;
    }

    /// <summary>Call the runtime function with |functionId| with single argument |arg|.</summary>
    public BytecodeArrayBuilder CallRuntime(FunctionId functionId, Register arg) =>
        CallRuntime(functionId, new RegisterList(arg));

    /// <summary>Call the runtime function with |functionId| with no arguments.</summary>
    public BytecodeArrayBuilder CallRuntime(FunctionId functionId) =>
        CallRuntime(functionId, RegisterList.Empty);

    /// <summary>Call the runtime function with |functionId| and arguments |args|,
    /// that returns a pair of values in |returnPair|.</summary>
    public BytecodeArrayBuilder CallRuntimeForPair(FunctionId functionId, RegisterList args,
                                                   RegisterList returnPair)
    {
        Debug.Assert(RuntimeFunctions.ResultSize(functionId) == 2);
        Debug.Assert(Bytecodes.SizeForUnsignedOperand((uint)functionId) <= OperandSize.Short);
        Debug.Assert(returnPair.RegisterCount == 2);
        UpdateMaxArguments(args.RegisterCount);
        Output(Bytecode.CallRuntimeForPair, (int)(ushort)functionId, args, args.RegisterCount, returnPair);
        return this;
    }

    public BytecodeArrayBuilder CallRuntimeForPair(FunctionId functionId, Register arg, RegisterList returnPair) =>
        CallRuntimeForPair(functionId, new RegisterList(arg), returnPair);

    /// <summary>Call the JS runtime function with |contextIndex| and arguments
    /// |args|, with no receiver as it is implicitly set to undefined.</summary>
    public BytecodeArrayBuilder CallJSRuntime(int contextIndex, RegisterList args)
    {
        UpdateMaxArguments(args.RegisterCount);
        Output(Bytecode.CallJSRuntime, contextIndex, args, args.RegisterCount);
        return this;
    }

    /// <summary>Deletes property from an object. The accumulator contains the key
    /// and the register contains a reference to the object.</summary>
    public BytecodeArrayBuilder Delete(Register @object, LanguageMode languageMode)
    {
        if (languageMode == LanguageMode.Sloppy)
        {
            Output(Bytecode.DeletePropertySloppy, @object);
        }
        else
        {
            Debug.Assert(languageMode == LanguageMode.Strict);
            Output(Bytecode.DeletePropertyStrict, @object);
        }
        return this;
    }

    // ---- Constant pool ------------------------------------------------------

    /// <summary>Creates a new handler table entry and returns a {handler_id}.</summary>
    public int NewHandlerEntry() => _handlerTableBuilder.NewHandlerEntry();

    /// <summary>GetConstantPoolEntry(const AstRawString*).</summary>
    public int GetConstantPoolEntry(object rawString) => _constantArrayBuilder.InsertRawString(rawString);

    /// <summary>GetConstantPoolEntry(const AstConsString*).</summary>
    public int GetConstantPoolEntryForConsString(object consString) => _constantArrayBuilder.InsertConsString(consString);

    /// <summary>GetConstantPoolEntry(AstBigInt).</summary>
    public int GetConstantPoolEntryForBigInt(object bigint) => _constantArrayBuilder.InsertBigInt(bigint);

    /// <summary>GetConstantPoolEntry(const Scope*).</summary>
    public int GetConstantPoolEntryForScope(object scope) => _constantArrayBuilder.InsertScope(scope);

    public int GetConstantPoolEntry(double number) => _constantArrayBuilder.Insert(number);

    public int AsyncIteratorSymbolConstantPoolEntry() => _constantArrayBuilder.InsertAsyncIteratorSymbol();
    public int ClassFieldsSymbolConstantPoolEntry() => _constantArrayBuilder.InsertClassFieldsSymbol();
    public int EmptyObjectBoilerplateDescriptionConstantPoolEntry() =>
        _constantArrayBuilder.InsertEmptyObjectBoilerplateDescription();
    public int EmptyArrayBoilerplateDescriptionConstantPoolEntry() =>
        _constantArrayBuilder.InsertEmptyArrayBoilerplateDescription();
    public int EmptyFixedArrayConstantPoolEntry() => _constantArrayBuilder.InsertEmptyFixedArray();
    public int IteratorSymbolConstantPoolEntry() => _constantArrayBuilder.InsertIteratorSymbol();
    public int InterpreterTrampolineSymbolConstantPoolEntry() => _constantArrayBuilder.InsertInterpreterTrampolineSymbol();
    public int NaNConstantPoolEntry() => _constantArrayBuilder.InsertNaN();

    /// <summary>Allocates a new jump table of given |size| and |caseValueBase| in the constant pool.</summary>
    public BytecodeJumpTable AllocateJumpTable(int size, int caseValueBase)
    {
        Debug.Assert(size > 0);
        int constant_pool_index = _constantArrayBuilder.InsertJumpTable(size);
        return new BytecodeJumpTable(constant_pool_index, size, caseValueBase);
    }

    public void TrimJumpTable(BytecodeJumpTable jumpTable, int size)
    {
        if (size == jumpTable.Size) return;
        _bytecodeArrayWriter.PatchJumpTableSize(jumpTable, size);
    }

    /// <summary>Allocates a slot in the constant pool which can later be set.</summary>
    public int AllocateDeferredConstantPoolEntry() => _constantArrayBuilder.InsertDeferred();

    /// <summary>Sets the deferred value into an allocated constant pool entry.</summary>
    public void SetDeferredConstantPoolEntry(int entry, object obj) => _constantArrayBuilder.SetDeferredAt(entry, obj);

    public BytecodeRegisterOptimizer? GetRegisterOptimizer() => _registerOptimizer;

    // ---- Registers ----------------------------------------------------------

    bool RegisterIsValid(Register reg)
    {
        if (!reg.IsValid) return false;

        if (reg.IsCurrentContext || reg.IsFunctionClosure) return true;
        if (reg.IsParameter)
        {
            int parameter_index = reg.ToParameterIndex();
            return parameter_index >= 0 && parameter_index < ParameterCount();
        }
        if (reg.Index < FixedRegisterCount()) return true;
        return _registerAllocator.RegisterIsLive(reg);
    }

    bool RegisterListIsValid(RegisterList regList)
    {
        if (regList.RegisterCount == 0) return regList.FirstRegister() == new Register(0);
        int first_reg_index = regList.FirstRegister().Index;
        for (int i = 0; i < regList.RegisterCount; i++)
        {
            if (!RegisterIsValid(new Register(first_reg_index + i))) return false;
        }
        return true;
    }

    /// <summary>Returns the raw operand value for the given register or register list.</summary>
    public uint GetInputRegisterOperand(Register reg)
    {
        Debug.Assert(RegisterIsValid(reg));
        if (_registerOptimizer is not null) reg = _registerOptimizer.GetInputRegister(reg);
        return (uint)reg.ToOperand();
    }

    public uint GetOutputRegisterOperand(Register reg)
    {
        Debug.Assert(RegisterIsValid(reg));
        _registerOptimizer?.PrepareOutputRegister(reg);
        return (uint)reg.ToOperand();
    }

    public uint GetInputOutputRegisterOperand(Register reg)
    {
        Debug.Assert(RegisterIsValid(reg));
        if (_registerOptimizer is not null)
        {
            _registerOptimizer.PrepareOutputRegister(reg);
            Debug.Assert(reg == _registerOptimizer.GetInputRegister(reg));
        }
        return (uint)reg.ToOperand();
    }

    public uint GetInputRegisterListOperand(RegisterList regList)
    {
        Debug.Assert(RegisterListIsValid(regList));
        if (_registerOptimizer is not null) regList = _registerOptimizer.GetInputRegisterList(regList);
        return (uint)regList.FirstRegister().ToOperand();
    }

    public uint GetOutputRegisterListOperand(RegisterList regList)
    {
        Debug.Assert(RegisterListIsValid(regList));
        _registerOptimizer?.PrepareOutputRegisterList(regList);
        return (uint)regList.FirstRegister().ToOperand();
    }

    public BytecodeRegisterAllocator RegisterAllocator() => _registerAllocator;

    public static string ToString(ToBooleanMode mode) => mode switch
    {
        ToBooleanMode.AlreadyBoolean => "AlreadyBoolean",
        ToBooleanMode.ConvertToBoolean => "ConvertToBoolean",
        _ => throw new UnreachableException(),
    };
}

/// <summary>Tracks whether potentially throwing bytecodes were emitted since the scope started.</summary>
public readonly struct ThrowTrackingScope(BytecodeArrayBuilder builder)
{
    readonly int _startCount = builder.PotentiallyThrowingBytecodeCount();

    public bool HasEmittedThrowingBytecode() => builder.PotentiallyThrowingBytecodeCount() > _startCount;
}
