// Port of src/interpreter/bytecode-array-writer.h/.cc: the final stage of the
// bytecode generation pipeline. Emits bytecodes into a growable buffer,
// patches forward jumps (reserving constant pool entries so a jump whose
// delta does not fit its operand can become a Jump*Constant), elides dead code
// after an exit and non-effectful accumulator loads, and records source
// positions.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

public sealed class BytecodeArrayWriter
{
    // Constants that act as placeholders for jump operands to be
    // patched. These have operand sizes that match the sizes of
    // reserved constant pool entries.
    const uint k8BitJumpPlaceholder = 0x7f;
    const uint k16BitJumpPlaceholder = k8BitJumpPlaceholder | (k8BitJumpPlaceholder << 8);
    const uint k32BitJumpPlaceholder = k16BitJumpPlaceholder | (k16BitJumpPlaceholder << 16);

    // Maximum sized packed bytecode is comprised of a prefix bytecode,
    // plus the actual bytecode, plus the maximum number of operands times
    // the maximum operand size.
    const int kMaxSizeOfPackedBytecode = 2 * sizeof(byte) + Bytecodes.kMaxOperands * (int)OperandSize.Last;

    byte[] _bytecodes = new byte[512]; // Derived via experimentation.
    int _size;
    int _unboundJumps;
    readonly SourcePositionTableBuilder _sourcePositionTableBuilder;
    readonly ConstantArrayBuilder _constantArrayBuilder;

    Bytecode _lastBytecode = Bytecode.Illegal;
    int _lastBytecodeOffset;
    bool _lastBytecodeHadSourceInfo;
    readonly bool _elideNoneffectfulBytecodes = InterpreterFlags.ignition_elide_noneffectful_bytecodes;

    bool _exitSeenInBlock;

    public BytecodeArrayWriter(ConstantArrayBuilder constantArrayBuilder,
                               SourcePositionTableBuilder.RecordingMode sourcePositionMode)
    {
        _constantArrayBuilder = constantArrayBuilder;
        _sourcePositionTableBuilder = new SourcePositionTableBuilder(sourcePositionMode);
    }

    /// <summary>The bytecodes emitted so far.</summary>
    public ReadOnlySpan<byte> BytecodesSpan => _bytecodes.AsSpan(0, _size);

    public BytecodeArray ToBytecodeArray(IConstantPoolMaterializer materializer, int registerCount,
                                         ushort parameterCount, ushort maxArguments, byte[] handlerTable)
    {
        Debug.Assert(_unboundJumps == 0);
        int frame_size = registerCount * 8; // kSystemPointerSize
        object[] constant_pool = _constantArrayBuilder.ToFixedArray(materializer);
        // BytecodeVerifier::Verify is a sandbox check; managed memory needs none.
        return new BytecodeArray(_bytecodes.AsSpan(0, _size).ToArray(), frame_size, parameterCount, maxArguments,
                                 constant_pool, handlerTable);
    }

    public byte[] ToSourcePositionTable()
    {
        Debug.Assert(!_sourcePositionTableBuilder.Lazy());
        return _sourcePositionTableBuilder.Omit() ? [] : _sourcePositionTableBuilder.ToSourcePositionTable();
    }

    /// <summary>Returns -1 if they match or the offset of the first mismatching
    /// byte, ignoring embedded feedback bytes (DEBUG-only in V8).</summary>
    public int CheckBytecodeMatches(BytecodeArray bytecode)
    {
        int bytecode_size = _size;
        // we only check up to the minimum length.
        int min_length = Math.Min(bytecode_size, bytecode.Length);

        var it = new BytecodeArrayIterator(bytecode);
        while (!it.Done() && it.CurrentOffset() < min_length)
        {
            int bytes_need_to_check = it.CurrentBytecodeSize();
            // skip embedded feedback value.
            if (Bytecodes.IsEmbeddedFeedbackBytecode(it.CurrentBytecode()))
            {
                // Unary bytecodes only have a single operand (the embedded feedback
                // itself), while binary/compare bytecodes have the embedded feedback
                // as their second operand.
                int operand_idx = Bytecodes.IsUnaryOpWithEmbeddedFeedback(it.CurrentBytecode())
                    ? InterpreterConstants.kUnaryEmbeddedFeedbackOperandIndex
                    : InterpreterConstants.kEmbeddedFeedbackOperandIndex;
                bytes_need_to_check -= (int)Bytecodes.GetOperandSize(it.CurrentBytecode(), operand_idx,
                                                                     it.CurrentOperandScale());
            }

            int start_idx = it.CurrentOffset();
            for (int i = start_idx; i < start_idx + bytes_need_to_check; ++i)
            {
                // boundary check
                if (i >= min_length) break;
                if (_bytecodes[i] != bytecode.Get(i)) return i;
            }
            it.Advance();
        }

        // all common bytes are checked and no mismatches founded, then the first
        // mismatch will be the first extra byte (if have).
        if (bytecode_size != bytecode.Length) return min_length;
        // no mismatches founded.
        return -1;
    }

    public void Write(ref BytecodeNode node)
    {
        Debug.Assert(!Bytecodes.IsJump(node.Bytecode));

        if (_exitSeenInBlock) return; // Don't emit dead code.
        UpdateExitSeenInBlock(node.Bytecode);
        MaybeElideLastBytecode(node.Bytecode, node.SourceInfo.IsValid());

        UpdateSourcePositionTable(in node);
        EmitBytecode(in node);
    }

    public void WriteJump(ref BytecodeNode node, BytecodeLabel label)
    {
        Debug.Assert(Bytecodes.IsForwardJump(node.Bytecode));

        if (_exitSeenInBlock) return; // Don't emit dead code.
        UpdateExitSeenInBlock(node.Bytecode);
        MaybeElideLastBytecode(node.Bytecode, node.SourceInfo.IsValid());

        UpdateSourcePositionTable(in node);
        EmitJump(ref node, label);
    }

    public void WriteJumpLoop(ref BytecodeNode node, BytecodeLoopHeader loopHeader)
    {
        Debug.Assert(node.Bytecode == Bytecode.JumpLoop);

        if (_exitSeenInBlock) return; // Don't emit dead code.
        UpdateExitSeenInBlock(node.Bytecode);
        MaybeElideLastBytecode(node.Bytecode, node.SourceInfo.IsValid());

        UpdateSourcePositionTable(in node);
        EmitJumpLoop(ref node, loopHeader);
    }

    public void WriteSwitch(ref BytecodeNode node, BytecodeJumpTable jumpTable)
    {
        Debug.Assert(Bytecodes.IsSwitch(node.Bytecode));

        if (_exitSeenInBlock) return; // Don't emit dead code.
        UpdateExitSeenInBlock(node.Bytecode);
        MaybeElideLastBytecode(node.Bytecode, node.SourceInfo.IsValid());

        UpdateSourcePositionTable(in node);
        EmitSwitch(ref node, jumpTable);
    }

    public void BindLabel(BytecodeLabel label)
    {
        Debug.Assert(label.HasReferrerJump);
        int current_offset = _size;
        // Update the jump instruction's location.
        PatchJump(current_offset, label.JumpOffset);
        label.Bind();
        StartBasicBlock();
    }

    public void BindLoopHeader(BytecodeLoopHeader loopHeader)
    {
        int current_offset = _size;
        loopHeader.BindTo(current_offset);
        // Don't start a basic block when the entire loop is dead.
        if (_exitSeenInBlock) return;
        StartBasicBlock();
    }

    public void BindJumpTableEntry(BytecodeJumpTable jumpTable, int caseValue)
    {
        Debug.Assert(!jumpTable.IsBound(caseValue));

        int current_offset = _size;
        int relative_jump = current_offset - jumpTable.SwitchBytecodeOffset;

        _constantArrayBuilder.SetJumpTableSmi(jumpTable.ConstantPoolEntryFor(caseValue), Smi.FromInt(relative_jump));
        jumpTable.MarkBound(caseValue);

        StartBasicBlock();
    }

    public void BindHandlerTarget(HandlerTableBuilder handlerTableBuilder, int handlerId)
    {
        int current_offset = _size;
        StartBasicBlock();
        handlerTableBuilder.SetHandlerTarget(handlerId, current_offset);
    }

    public void BindTryRegionStart(HandlerTableBuilder handlerTableBuilder, int handlerId)
    {
        int current_offset = _size;
        // Try blocks don't have to be in a separate basic block, but we do have to
        // invalidate the bytecode to avoid eliding it and changing the offset.
        InvalidateLastBytecode();
        handlerTableBuilder.SetTryRegionStart(handlerId, current_offset);
    }

    public void BindTryRegionEnd(HandlerTableBuilder handlerTableBuilder, int handlerId)
    {
        // Try blocks don't have to be in a separate basic block, but we do have to
        // invalidate the bytecode to avoid eliding it and changing the offset.
        InvalidateLastBytecode();
        int current_offset = _size;
        handlerTableBuilder.SetTryRegionEnd(handlerId, current_offset);
    }

    public void SetFunctionEntrySourcePosition(int position)
    {
        bool is_statement = false;
        _sourcePositionTableBuilder.AddPosition(InterpreterConstants.kFunctionEntryBytecodeOffset,
                                                new SourcePosition(position), is_statement);
    }

    public bool RemainderOfBlockIsDead() => _exitSeenInBlock;

    public int CurrentBytecodeSize() => _size;

    void StartBasicBlock()
    {
        InvalidateLastBytecode();
        _exitSeenInBlock = false;
    }

    void UpdateSourcePositionTable(in BytecodeNode node)
    {
        int bytecode_offset = _size;
        BytecodeSourceInfo source_info = node.SourceInfo;
        if (source_info.IsValid())
        {
            _sourcePositionTableBuilder.AddPosition(bytecode_offset, new SourcePosition(source_info.SourcePosition()),
                                                    source_info.IsStatement(), source_info.IsBreakable());
        }
    }

    void UpdateExitSeenInBlock(Bytecode bytecode)
    {
        switch (bytecode)
        {
            case Bytecode.Return:
            case Bytecode.Throw:
            case Bytecode.ReThrow:
            case Bytecode.Abort:
            case Bytecode.Jump:
            case Bytecode.JumpLoop:
            case Bytecode.JumpConstant:
            case Bytecode.SuspendGenerator:
                _exitSeenInBlock = true;
                break;
        }
    }

    void MaybeElideLastBytecode(Bytecode nextBytecode, bool hasSourceInfo)
    {
        if (!_elideNoneffectfulBytecodes) return;

        // If the last bytecode loaded the accumulator without any external effect,
        // and the next bytecode clobbers this load without reading the accumulator,
        // then the previous bytecode can be elided as it has no effect.
        if (Bytecodes.IsAccumulatorLoadWithoutEffects(_lastBytecode) &&
            Bytecodes.GetImplicitRegisterUse(nextBytecode) == ImplicitRegisterUse.WriteAccumulator &&
            (!_lastBytecodeHadSourceInfo || !hasSourceInfo))
        {
            Debug.Assert(_size > _lastBytecodeOffset);
            _size = _lastBytecodeOffset;
            // If the last bytecode had source info we will transfer the source info
            // to this bytecode.
            hasSourceInfo |= _lastBytecodeHadSourceInfo;
        }
        _lastBytecode = nextBytecode;
        _lastBytecodeHadSourceInfo = hasSourceInfo;
        _lastBytecodeOffset = _size;
    }

    void InvalidateLastBytecode() => _lastBytecode = Bytecode.Illegal;

    void EmitBytecode(in BytecodeNode node)
    {
        Debug.Assert(node.Bytecode != Bytecode.Illegal);

        Bytecode bytecode = node.Bytecode;
        OperandScale operand_scale = node.OperandScale;

        if (_size + kMaxSizeOfPackedBytecode > _bytecodes.Length)
        {
            Array.Resize(ref _bytecodes, _bytecodes.Length * 2);
        }

        if (operand_scale != OperandScale.Single)
        {
            Bytecode prefix = Bytecodes.OperandScaleToPrefixBytecode(operand_scale);
            _bytecodes[_size++] = Bytecodes.ToByte(prefix);
        }
        _bytecodes[_size++] = Bytecodes.ToByte(bytecode);

        ReadOnlySpan<uint> operands = node.Operands;
        ReadOnlySpan<OperandSize> operand_sizes = Bytecodes.GetOperandSizes(bytecode, operand_scale);
        for (int i = 0; i < operands.Length; ++i)
        {
            switch (operand_sizes[i])
            {
                case OperandSize.Byte:
                    _bytecodes[_size++] = (byte)operands[i];
                    break;
                case OperandSize.Short:
                    BinaryPrimitives.WriteUInt16LittleEndian(_bytecodes.AsSpan(_size), (ushort)operands[i]);
                    _size += 2;
                    break;
                case OperandSize.Quad:
                    BinaryPrimitives.WriteUInt32LittleEndian(_bytecodes.AsSpan(_size), operands[i]);
                    _size += 4;
                    break;
                default:
                    throw new UnreachableException();
            }
        }
    }

    static Bytecode GetJumpWithConstantOperand(Bytecode jumpBytecode) => jumpBytecode switch
    {
        Bytecode.Jump => Bytecode.JumpConstant,
        Bytecode.JumpIfTrue => Bytecode.JumpIfTrueConstant,
        Bytecode.JumpIfFalse => Bytecode.JumpIfFalseConstant,
        Bytecode.JumpIfToBooleanTrue => Bytecode.JumpIfToBooleanTrueConstant,
        Bytecode.JumpIfToBooleanFalse => Bytecode.JumpIfToBooleanFalseConstant,
        Bytecode.JumpIfNull => Bytecode.JumpIfNullConstant,
        Bytecode.JumpIfNotNull => Bytecode.JumpIfNotNullConstant,
        Bytecode.JumpIfUndefined => Bytecode.JumpIfUndefinedConstant,
        Bytecode.JumpIfNotUndefined => Bytecode.JumpIfNotUndefinedConstant,
        Bytecode.JumpIfUndefinedOrNull => Bytecode.JumpIfUndefinedOrNullConstant,
        Bytecode.JumpIfJSReceiver => Bytecode.JumpIfJSReceiverConstant,
        Bytecode.JumpIfForInDone => Bytecode.JumpIfForInDoneConstant,
        _ => throw new UnreachableException(),
    };

    void PatchJumpWith8BitOperand(int jumpLocation, int delta)
    {
        Bytecode jump_bytecode = Bytecodes.FromByte(_bytecodes[jumpLocation]);
        Debug.Assert(Bytecodes.IsForwardJump(jump_bytecode));
        Debug.Assert(Bytecodes.IsJumpImmediate(jump_bytecode));
        Debug.Assert(Bytecodes.GetOperandType(jump_bytecode, 0) == OperandType.UImm);
        Debug.Assert(delta > 0);
        int operand_location = jumpLocation + 1;
        Debug.Assert(_bytecodes[operand_location] == k8BitJumpPlaceholder);
        if (Bytecodes.ScaleForUnsignedOperand((uint)delta) == OperandScale.Single)
        {
            // The jump fits within the range of an UImm8 operand, so cancel
            // the reservation and jump directly.
            _constantArrayBuilder.DiscardReservedEntry(OperandSize.Byte);
            _bytecodes[operand_location] = (byte)delta;
        }
        else
        {
            // The jump does not fit within the range of an UImm8 operand, so
            // commit reservation putting the offset into the constant pool,
            // and update the jump instruction and operand.
            int entry = _constantArrayBuilder.CommitReservedEntry(OperandSize.Byte, Smi.FromInt(delta));
            Debug.Assert(Bytecodes.SizeForUnsignedOperand((uint)entry) == OperandSize.Byte);
            jump_bytecode = GetJumpWithConstantOperand(jump_bytecode);
            _bytecodes[jumpLocation] = Bytecodes.ToByte(jump_bytecode);
            _bytecodes[operand_location] = (byte)entry;
        }
    }

    void PatchJumpWith16BitOperand(int jumpLocation, int delta)
    {
        Bytecode jump_bytecode = Bytecodes.FromByte(_bytecodes[jumpLocation]);
        Debug.Assert(Bytecodes.IsForwardJump(jump_bytecode));
        Debug.Assert(Bytecodes.IsJumpImmediate(jump_bytecode));
        Debug.Assert(Bytecodes.GetOperandType(jump_bytecode, 0) == OperandType.UImm);
        Debug.Assert(delta > 0);
        int operand_location = jumpLocation + 1;
        ushort operand;
        if (Bytecodes.ScaleForUnsignedOperand((uint)delta) <= OperandScale.Double)
        {
            // The jump fits within the range of an Imm16 operand, so cancel
            // the reservation and jump directly.
            _constantArrayBuilder.DiscardReservedEntry(OperandSize.Short);
            operand = (ushort)delta;
        }
        else
        {
            // The jump does not fit within the range of an Imm16 operand, so
            // commit reservation putting the offset into the constant pool,
            // and update the jump instruction and operand.
            int entry = _constantArrayBuilder.CommitReservedEntry(OperandSize.Short, Smi.FromInt(delta));
            jump_bytecode = GetJumpWithConstantOperand(jump_bytecode);
            _bytecodes[jumpLocation] = Bytecodes.ToByte(jump_bytecode);
            operand = (ushort)entry;
        }
        Debug.Assert(_bytecodes[operand_location] == k8BitJumpPlaceholder &&
                     _bytecodes[operand_location + 1] == k8BitJumpPlaceholder);
        BinaryPrimitives.WriteUInt16LittleEndian(_bytecodes.AsSpan(operand_location), operand);
    }

    void PatchJumpWith32BitOperand(int jumpLocation, int delta)
    {
        Debug.Assert(Bytecodes.IsJumpImmediate(Bytecodes.FromByte(_bytecodes[jumpLocation])));
        _constantArrayBuilder.DiscardReservedEntry(OperandSize.Quad);
        int operand_location = jumpLocation + 1;
        Debug.Assert(_bytecodes[operand_location] == k8BitJumpPlaceholder &&
                     _bytecodes[operand_location + 1] == k8BitJumpPlaceholder &&
                     _bytecodes[operand_location + 2] == k8BitJumpPlaceholder &&
                     _bytecodes[operand_location + 3] == k8BitJumpPlaceholder);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytecodes.AsSpan(operand_location), (uint)delta);
    }

    void PatchJump(int jumpTarget, int jumpLocation)
    {
        Bytecode jump_bytecode = Bytecodes.FromByte(_bytecodes[jumpLocation]);
        int delta = jumpTarget - jumpLocation;
        int prefix_offset = 0;
        OperandScale operand_scale = OperandScale.Single;
        if (Bytecodes.IsPrefixScalingBytecode(jump_bytecode))
        {
            // If a prefix scaling bytecode is emitted the target offset is one
            // less than the case of no prefix scaling bytecode.
            delta -= 1;
            prefix_offset = 1;
            operand_scale = Bytecodes.PrefixBytecodeToOperandScale(jump_bytecode);
            jump_bytecode = Bytecodes.FromByte(_bytecodes[jumpLocation + prefix_offset]);
        }

        Debug.Assert(Bytecodes.IsJump(jump_bytecode));
        switch (operand_scale)
        {
            case OperandScale.Single:
                PatchJumpWith8BitOperand(jumpLocation, delta);
                break;
            case OperandScale.Double:
                PatchJumpWith16BitOperand(jumpLocation + prefix_offset, delta);
                break;
            case OperandScale.Quadruple:
                PatchJumpWith32BitOperand(jumpLocation + prefix_offset, delta);
                break;
            default:
                throw new UnreachableException();
        }
        _unboundJumps--;
    }

    public void PatchJumpTableSize(BytecodeJumpTable table, int size)
    {
        if (size >= table.Size) throw new InvalidOperationException("Check failed: size < table->size()");

        int switch_location = table.SwitchBytecodeOffset;
        OperandScale operand_scale = table.SwitchBytecodeOperandScale;

        Bytecode switch_bytecode = Bytecodes.FromByte(_bytecodes[switch_location]);

        // Currently only SwitchOnGeneratorState is supported, since uses of
        // SwitchOnSmiNoFeedback can ensure that it is correctly sized.
        if (switch_bytecode != Bytecode.SwitchOnGeneratorState)
            throw new InvalidOperationException("Check failed: switch_bytecode == Bytecode::kSwitchOnGeneratorState");
        Debug.Assert(Bytecodes.IsSwitch(switch_bytecode));
        // Verify that there is a prefix bytecode if the operand scale would need one.
        Debug.Assert(operand_scale <= OperandScale.Single ||
                     operand_scale == Bytecodes.PrefixBytecodeToOperandScale(
                         Bytecodes.FromByte(_bytecodes[switch_location - 1])));

        Debug.Assert(Bytecodes.GetOperandType(switch_bytecode, 0) == OperandType.Reg);
        Debug.Assert(Bytecodes.GetOperandType(switch_bytecode, 1) == OperandType.ConstantPoolIndex);
        Debug.Assert(Bytecodes.GetOperandType(switch_bytecode, 2) == OperandType.UImm);

        int size_operand_location = switch_location + Bytecodes.GetOperandOffset(switch_bytecode, 2, operand_scale);
        Span<byte> operand_ptr = _bytecodes.AsSpan(size_operand_location);

        switch (operand_scale)
        {
            case OperandScale.Single:
                operand_ptr[0] = checked((byte)size);
                break;
            case OperandScale.Double:
                BinaryPrimitives.WriteUInt16LittleEndian(operand_ptr, checked((ushort)size));
                break;
            case OperandScale.Quadruple:
                BinaryPrimitives.WriteUInt32LittleEndian(operand_ptr, checked((uint)size));
                break;
            default:
                throw new UnreachableException();
        }

        // Set the dead constant pool entries to Smi::zero, so that an invalid access
        // to one of them ends up hanging rather than jumping randomly.
        int constant_pool_index = table.ConstantPoolIndex;
        for (int i = size; i < table.Size; ++i)
        {
            _constantArrayBuilder.SetJumpTableSmi(constant_pool_index + i, Smi.Zero);
        }
        table.SetSize(size);
    }

    void EmitJumpLoop(ref BytecodeNode node, BytecodeLoopHeader loopHeader)
    {
        Debug.Assert(node.Bytecode == Bytecode.JumpLoop);
        Debug.Assert(node.Operand(0) == 0u);

        int current_offset = _size;

        if (current_offset < loopHeader.Offset)
            throw new InvalidOperationException("Check failed: current_offset >= loop_header->offset()");

        // Update the actual jump offset now that we know the bytecode offset of both
        // the target loop header and this JumpLoop bytecode.
        //
        // The label has been bound already so this is a backwards jump.
        uint delta = (uint)(current_offset - loopHeader.Offset);
        // This JumpLoop bytecode itself may have a kWide or kExtraWide prefix; if
        // so, bump the delta to account for it.
        bool emits_prefix_bytecode =
            Bytecodes.OperandScaleRequiresPrefixBytecode(node.OperandScale) ||
            Bytecodes.OperandScaleRequiresPrefixBytecode(Bytecodes.ScaleForUnsignedOperand(delta));
        if (emits_prefix_bytecode)
        {
            const int kPrefixBytecodeSize = 1;
            delta += kPrefixBytecodeSize;
            Debug.Assert(Bytecodes.Size(Bytecode.Wide, OperandScale.Single) == kPrefixBytecodeSize);
            Debug.Assert(Bytecodes.Size(Bytecode.ExtraWide, OperandScale.Single) == kPrefixBytecodeSize);
        }
        node.UpdateOperand0(delta);
        Debug.Assert(Bytecodes.OperandScaleRequiresPrefixBytecode(node.OperandScale) == emits_prefix_bytecode);

        EmitBytecode(in node);
    }

    void EmitJump(ref BytecodeNode node, BytecodeLabel label)
    {
        Debug.Assert(Bytecodes.IsForwardJump(node.Bytecode));
        Debug.Assert(node.Operand(0) == 0u);

        int current_offset = _size;

        // The label has not yet been bound so this is a forward reference
        // that will be patched when the label is bound. We create a
        // reservation in the constant pool so the jump can be patched
        // when the label is bound. The reservation means the maximum size
        // of the operand for the constant is known and the jump can
        // be emitted into the bytecode stream with space for the operand.
        _unboundJumps++;
        label.SetReferrer(current_offset);
        OperandSize reserved_operand_size = _constantArrayBuilder.CreateReservedEntry((OperandSize)node.OperandScale);
        Debug.Assert(node.Bytecode != Bytecode.JumpLoop);
        switch (reserved_operand_size)
        {
            case OperandSize.Byte:
                node.UpdateOperand0(k8BitJumpPlaceholder);
                break;
            case OperandSize.Short:
                node.UpdateOperand0(k16BitJumpPlaceholder);
                break;
            case OperandSize.Quad:
                node.UpdateOperand0(k32BitJumpPlaceholder);
                break;
            default:
                throw new UnreachableException();
        }
        EmitBytecode(in node);
    }

    void EmitSwitch(ref BytecodeNode node, BytecodeJumpTable jumpTable)
    {
        Debug.Assert(Bytecodes.IsSwitch(node.Bytecode));

        int current_offset = _size;
        if (node.OperandScale > OperandScale.Single)
        {
            // Adjust for scaling byte prefix.
            current_offset += 1;
        }
        jumpTable.SetSwitchBytecodeOffset(current_offset, node.OperandScale);

        EmitBytecode(in node);
    }
}
