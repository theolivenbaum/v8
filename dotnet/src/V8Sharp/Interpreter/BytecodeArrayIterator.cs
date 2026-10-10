// Port of src/interpreter/bytecode-array-iterator.h/.cc.
//
// V8 walks raw pointers (and re-bases them after a moving GC); the port keeps
// integer offsets into the managed byte[], which the GC never invalidates.
using V8Sharp.Runtime;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

public readonly record struct JumpTableTargetOffset(int CaseValue, int TargetOffset);

/// <summary>The absolute offsets of the targets of a switch bytecode's jump table.</summary>
public readonly struct JumpTableTargetOffsets(BytecodeArrayIterator iterator, int tableStart, int tableSize,
                                              int caseValueBase) : IEnumerable<JumpTableTargetOffset>
{
    public int Size => tableSize;

    public Enumerator GetEnumerator() => new(iterator, tableStart, tableSize, caseValueBase);
    IEnumerator<JumpTableTargetOffset> IEnumerable<JumpTableTargetOffset>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(BytecodeArrayIterator iterator, int tableStart, int tableSize, int caseValueBase)
        : IEnumerator<JumpTableTargetOffset>
    {
        int _tableOffset = tableStart - 1;
        int _index = caseValueBase - 1;
        readonly int _tableEnd = tableStart + tableSize;

        public readonly JumpTableTargetOffset Current =>
            new(_index, iterator.GetAbsoluteOffset(iterator.GetConstantAtIndexAsSmi(_tableOffset).Value));

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            ++_tableOffset;
            ++_index;
            return _tableOffset < _tableEnd;
        }

        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }
}

public class BytecodeArrayIterator
{
    readonly BytecodeArray _bytecodeArray;
    readonly byte[] _bytes;
    readonly int _end;
    // The cursor always points to the active bytecode. If there's a prefix, the
    // prefix is at (cursor - 1). -1 when done (V8's nullptr).
    int _cursor;
    Bytecode _currentBytecode;
    OperandScale _operandScale;
    int _prefixSize;

    public BytecodeArrayIterator(BytecodeArray bytecodeArray, int initialOffset = 0)
    {
        _bytecodeArray = bytecodeArray;
        _bytes = bytecodeArray.Bytecodes;
        _end = bytecodeArray.Length;
        _cursor = 0;
        _operandScale = OperandScale.Single;
        _prefixSize = 0;
        UpdateCurrentBytecode();
        if (initialOffset != 0) AdvanceTo(initialOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance()
    {
        _cursor += CurrentBytecodeSizeWithoutPrefix();
        UpdateCurrentBytecode();
    }

    /// <summary>Prefer AdvanceTo over SetOffset if the new offset is greater than the
    /// current offset as it is more efficient.</summary>
    public void AdvanceTo(int offset)
    {
        Debug.Assert(CurrentOffset() <= offset);
        while (CurrentOffset() < offset) Advance();
        // Make sure we're always at a valid offset.
        if (CurrentOffset() != offset) throw new InvalidOperationException("Check failed: current_offset() == offset");
    }

    public void SetOffset(int offset)
    {
        Debug.Assert(offset >= 0);
        if (Done() || _cursor - _prefixSize > offset) Reset();
        // Advance to the given offset instead of just setting cursor_.
        // This way, we can guarantee that the offset is always valid.
        AdvanceTo(offset);
    }

    public void Reset()
    {
        _cursor = 0;
        UpdateCurrentBytecode();
    }

    protected void SetOffsetUnchecked(int offset)
    {
        Debug.Assert(offset >= 0);
        _cursor = offset;
        UpdateCurrentBytecode();
    }

    /// <summary>Whether the given offset is reachable in this bytecode array.</summary>
    public static bool IsValidOffset(BytecodeArray bytecodeArray, int offset)
    {
        for (var it = new BytecodeArrayIterator(bytecodeArray); !it.Done(); it.Advance())
        {
            if (it.CurrentOffset() == offset) return true;
            if (it.CurrentOffset() > offset) break;
        }
        return false;
    }

    public static bool IsValidOSREntryOffset(BytecodeArray bytecodeArray, int offset) =>
        new BytecodeArrayIterator(bytecodeArray, offset).CurrentBytecodeIsValidOSREntry();

    public bool CurrentBytecodeIsValidOSREntry() => CurrentBytecode() == Bytecode.JumpLoop;

    public void ApplyDebugBreak()
    {
        // Get the raw bytecode from the bytecode array. This may give us a
        // scaling prefix, which we can patch with the matching debug-break
        // variant.
        int cursor = _cursor - _prefixSize;
        Bytecode bytecode = Bytecodes.FromByte(_bytes[cursor]);
        if (Bytecodes.IsDebugBreak(bytecode)) return;
        Bytecode debugbreak = Bytecodes.GetDebugBreak(bytecode);
        _bytes[cursor] = Bytecodes.ToByte(debugbreak);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bytecode CurrentBytecode() => _currentBytecode;

    public int CurrentBytecodeSize() => _prefixSize + CurrentBytecodeSizeWithoutPrefix();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CurrentBytecodeSizeWithoutPrefix() => Bytecodes.Size(_currentBytecode, CurrentOperandScale());

    public int CurrentOffset()
    {
        Debug.Assert(!Done());
        return _cursor - _prefixSize;
    }

    public int CurrentOperandOffset(int operandIndex) =>
        Bytecodes.GetOperandOffset(CurrentBytecode(), operandIndex, CurrentOperandScale());

    /// <summary>The bytes of the current bytecode, starting at its prefix (V8's current_address()).</summary>
    public ReadOnlySpan<byte> CurrentAddress() => _bytes.AsSpan(_cursor - _prefixSize);

    public int NextOffset() => CurrentOffset() + CurrentBytecodeSize();

    public Bytecode NextBytecode()
    {
        int next_cursor = _cursor + CurrentBytecodeSizeWithoutPrefix();
        if (next_cursor == _end) return Bytecode.Illegal;
        Bytecode next_bytecode = Bytecodes.FromByte(_bytes[next_cursor]);
        if (Bytecodes.IsPrefixScalingBytecode(next_bytecode))
        {
            next_bytecode = Bytecodes.FromByte(_bytes[next_cursor + 1]);
        }
        return next_bytecode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OperandScale CurrentOperandScale() => _operandScale;

    public BytecodeArray BytecodeArray() => _bytecodeArray;

    ReadOnlySpan<byte> OperandStart(int operandIndex) =>
        _bytes.AsSpan(_cursor + Bytecodes.GetOperandOffset(CurrentBytecode(), operandIndex, CurrentOperandScale()));

    uint GetUnsignedOperand(int operandIndex, OperandType operandType)
    {
        Debug.Assert(operandIndex >= 0 && operandIndex < Bytecodes.NumberOfOperands(CurrentBytecode()));
        Debug.Assert(operandType == Bytecodes.GetOperandType(CurrentBytecode(), operandIndex));
        Debug.Assert(Bytecodes.IsUnsignedOperandType(operandType));
        return BytecodeDecoder.DecodeUnsignedOperand(OperandStart(operandIndex), operandType, CurrentOperandScale());
    }

    int GetSignedOperand(int operandIndex, OperandType operandType)
    {
        Debug.Assert(operandIndex >= 0 && operandIndex < Bytecodes.NumberOfOperands(CurrentBytecode()));
        Debug.Assert(operandType == Bytecodes.GetOperandType(CurrentBytecode(), operandIndex));
        Debug.Assert(!Bytecodes.IsUnsignedOperandType(operandType));
        return BytecodeDecoder.DecodeSignedOperand(OperandStart(operandIndex), operandType, CurrentOperandScale());
    }

    public uint GetFlag8Operand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.Flag8);
    public uint GetFlag16Operand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.Flag16);
    public uint GetUnsignedImmediateOperand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.UImm);
    public int GetImmediateOperand(int operandIndex) => GetSignedOperand(operandIndex, OperandType.Imm);
    public uint GetRegisterCountOperand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.RegCount);
    public uint GetConstantPoolIndexOperand(int operandIndex) =>
        GetUnsignedOperand(operandIndex, OperandType.ConstantPoolIndex);
    public uint GetFeedbackSlotOperand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.FeedbackSlot);
    public uint GetContextSlotOperand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.ContextSlot);
    public uint GetCoverageSlotOperand(int operandIndex) => GetUnsignedOperand(operandIndex, OperandType.CoverageSlot);

    /// <summary>GetSlotOperand: the feedback slot (FeedbackVector::ToSlot is the identity on the index).</summary>
    public int GetSlotOperand(int operandIndex) => (int)GetFeedbackSlotOperand(operandIndex);

    public Register GetParameter(int parameterIndex)
    {
        Debug.Assert(parameterIndex >= 0);
        // The parameter indices are shifted by 1 (receiver is the first entry).
        return Register.FromParameterIndex(parameterIndex + 1);
    }

    public Register GetRegisterOperand(int operandIndex)
    {
        OperandType operand_type = Bytecodes.GetOperandType(CurrentBytecode(), operandIndex);
        return BytecodeDecoder.DecodeRegisterOperand(OperandStart(operandIndex), operand_type, CurrentOperandScale());
    }

    public Register GetStarTargetRegister()
    {
        Bytecode bytecode = CurrentBytecode();
        Debug.Assert(Bytecodes.IsAnyStar(bytecode));
        if (Bytecodes.IsShortStar(bytecode)) return Register.FromShortStar(bytecode);
        Debug.Assert(bytecode == Bytecode.Star);
        return GetRegisterOperand(0);
    }

    public (Register First, Register Second) GetRegisterPairOperand(int operandIndex)
    {
        Register first = GetRegisterOperand(operandIndex);
        return (first, new Register(first.Index + 1));
    }

    public RegisterList GetRegisterListOperand(int operandIndex)
    {
        Register first = GetRegisterOperand(operandIndex);
        uint count = GetRegisterCountOperand(operandIndex + 1);
        return new RegisterList(first.Index, (int)count);
    }

    public uint GetRegisterOperandRange(int operandIndex)
    {
        Debug.Assert(operandIndex <= Bytecodes.NumberOfOperands(CurrentBytecode()));
        OperandType operand_type = Bytecodes.GetOperandTypes(CurrentBytecode())[operandIndex];
        Debug.Assert(Bytecodes.IsRegisterOperandType(operand_type));
        if (operand_type is OperandType.RegList or OperandType.RegOutList)
        {
            return GetRegisterCountOperand(operandIndex + 1);
        }
        return Bytecodes.GetNumberOfRegistersRepresentedBy(operand_type);
    }

    public FunctionId GetRuntimeIdOperand(int operandIndex) =>
        (FunctionId)GetUnsignedOperand(operandIndex, OperandType.RuntimeId);

    public uint GetNativeContextIndexOperand(int operandIndex) =>
        GetUnsignedOperand(operandIndex, OperandType.NativeContextIndex);

    public FunctionId GetIntrinsicIdOperand(int operandIndex)
    {
        uint raw_id = GetUnsignedOperand(operandIndex, OperandType.IntrinsicId);
        return IntrinsicsHelper.ToRuntimeId((IntrinsicsHelper.IntrinsicId)raw_id);
    }

    public AbortReason GetAbortReasonOperand(int operandIndex) =>
        (AbortReason)GetUnsignedOperand(operandIndex, OperandType.AbortReason);

    public object GetConstantAtIndex(int index)
    {
        object[] constant_pool = _bytecodeArray.ConstantPool;
        if ((uint)index >= (uint)constant_pool.Length)
            throw new InvalidOperationException("Constant pool index out of bounds");
        return constant_pool[index];
    }

    public Smi GetConstantAtIndexAsSmi(int index)
    {
        object obj = GetConstantAtIndex(index);
        return obj is Smi smi ? smi : throw new InvalidOperationException("Constant pool entry is not a Smi");
    }

    public object GetConstantForOperand(int operandIndex) =>
        GetConstantAtIndex((int)GetConstantPoolIndexOperand(operandIndex));

    /// <summary>The relative offset of the branch target at the current bytecode.
    /// Negative for backward jumps. Only valid for jumps.</summary>
    public int GetRelativeJumpTargetOffset()
    {
        Bytecode bytecode = CurrentBytecode();
        if (Bytecodes.IsJumpImmediate(bytecode))
        {
            int relative_offset = (int)GetUnsignedImmediateOperand(0);
            if (bytecode == Bytecode.JumpLoop) relative_offset = -relative_offset;
            return relative_offset;
        }
        if (Bytecodes.IsJumpConstant(bytecode))
        {
            Smi smi = GetConstantAtIndexAsSmi((int)GetConstantPoolIndexOperand(0));
            return smi.Value;
        }
        throw new UnreachableException();
    }

    /// <summary>The absolute offset of the branch target at the current bytecode.</summary>
    public int GetJumpTargetOffset() => GetAbsoluteOffset(GetRelativeJumpTargetOffset());

    /// <summary>The absolute offsets of the targets of the current switch bytecode's jump table.</summary>
    public JumpTableTargetOffsets GetJumpTableTargetOffsets()
    {
        uint table_start, table_size;
        int case_value_base;
        if (CurrentBytecode() == Bytecode.SwitchOnGeneratorState)
        {
            table_start = GetConstantPoolIndexOperand(1);
            table_size = GetUnsignedImmediateOperand(2);
            case_value_base = 0;
        }
        else
        {
            Debug.Assert(CurrentBytecode() == Bytecode.SwitchOnSmiNoFeedback);
            table_start = GetConstantPoolIndexOperand(0);
            table_size = GetUnsignedImmediateOperand(1);
            case_value_base = GetImmediateOperand(2);
        }
        return new JumpTableTargetOffsets(this, (int)table_start, (int)table_size, case_value_base);
    }

    /// <summary>The absolute offset of the bytecode at |relativeOffset| from the current bytecode.</summary>
    public int GetAbsoluteOffset(int relativeOffset) => CurrentOffset() + relativeOffset + _prefixSize;

    public StringBuilder PrintCurrentBytecodeTo(StringBuilder os) =>
        BytecodeDecoder.Decode(os, CurrentAddress(), _bytecodeArray.ConstantPool);

    public byte GetEmbeddedFeedback(int operandIndex)
    {
        Debug.Assert(operandIndex >= 0 && operandIndex < Bytecodes.NumberOfOperands(CurrentBytecode()));
        Debug.Assert(Bytecodes.GetOperandType(CurrentBytecode(), operandIndex) == OperandType.EmbeddedFeedback);
        return BytecodeDecoder.RacyDecodeEmbeddedFeedback(OperandStart(operandIndex));
    }

    /// <summary>The feedback of the current binary/unary operation, decoded from its embedded type index.</summary>
    // TODO(merge): V8 returns a BinaryOperationHint (src/objects/type-hints.h) via
    // BinaryOperationHintFromFeedback; port that with the feedback/IC code.
    public BinaryOperationFeedback.Type GetEmbeddedBinaryOperationFeedback()
    {
        Debug.Assert(Bytecodes.IsBinaryOpWithEmbeddedFeedback(CurrentBytecode()) ||
                     Bytecodes.IsUnaryOpWithEmbeddedFeedback(CurrentBytecode()));
        int operand_index = Bytecodes.IsUnaryOpWithEmbeddedFeedback(CurrentBytecode())
            ? InterpreterConstants.kUnaryEmbeddedFeedbackOperandIndex
            : InterpreterConstants.kEmbeddedFeedbackOperandIndex;
        return BinaryOperationFeedback.DecodeTypeIndex((BinaryOperationFeedback.TypeIndex)GetEmbeddedFeedback(operand_index));
    }

    /// <summary>The feedback of the current compare operation, decoded from its embedded type index.</summary>
    // TODO(merge): V8 returns a CompareOperationHint via CompareOperationHintFromFeedback.
    public CompareOperationFeedback.Type GetEmbeddedCompareOperationFeedback()
    {
        Debug.Assert(Bytecodes.IsCompareWithEmbeddedFeedback(CurrentBytecode()));
        return CompareOperationFeedback.DecodeTypeIndex(
            (CompareOperationFeedback.TypeIndex)GetEmbeddedFeedback(InterpreterConstants.kEmbeddedFeedbackOperandIndex));
    }

    /// <summary>The offset of an embedded feedback operand within the bytecode
    /// array's bytes (V8 returns it relative to the tagged object pointer).</summary>
    public int GetEmbeddedFeedbackOffset(int operandIndex)
    {
        Debug.Assert(Bytecodes.GetOperandType(CurrentBytecode(), operandIndex) == OperandType.EmbeddedFeedback);
        return CurrentOffset() + _prefixSize + CurrentOperandOffset(operandIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Done() => _cursor < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UpdateCurrentBytecode()
    {
        if (_cursor >= _end)
        {
            _cursor = -1;
            _prefixSize = 0;
            _currentBytecode = Bytecode.Illegal;
            return;
        }

        _currentBytecode = Bytecodes.FromByte(_bytes[_cursor]);
        if (Bytecodes.IsPrefixScalingBytecode(_currentBytecode))
        {
            _operandScale = Bytecodes.PrefixBytecodeToOperandScale(_currentBytecode);
            ++_cursor;
            _currentBytecode = Bytecodes.FromByte(_bytes[_cursor]);
            Debug.Assert(!Bytecodes.IsPrefixScalingBytecode(_currentBytecode));
            _prefixSize = 1;
        }
        else
        {
            _operandScale = OperandScale.Single;
            _prefixSize = 0;
        }
    }
}

/// <summary>Port of src/interpreter/bytecode-array-random-iterator.h/.cc.</summary>
public sealed class BytecodeArrayRandomIterator : BytecodeArrayIterator
{
    readonly List<int> _offsets;
    int _currentIndex;

    public BytecodeArrayRandomIterator(BytecodeArray bytecodeArray) : base(bytecodeArray, 0)
    {
        _offsets = new List<int>(bytecodeArray.Length / 2);
        Initialize();
    }

    void Initialize()
    {
        // Run forwards through the bytecode array to determine the offset of each
        // bytecode.
        while (!Done())
        {
            _offsets.Add(CurrentOffset());
            Advance();
        }
        GoToStart();
    }

    public static BytecodeArrayRandomIterator operator ++(BytecodeArrayRandomIterator it)
    {
        ++it._currentIndex;
        it.UpdateOffsetFromIndex();
        return it;
    }

    public static BytecodeArrayRandomIterator operator --(BytecodeArrayRandomIterator it)
    {
        --it._currentIndex;
        it.UpdateOffsetFromIndex();
        return it;
    }

    /// <summary>operator+=.</summary>
    public void AdvanceBy(int offset)
    {
        _currentIndex += offset;
        UpdateOffsetFromIndex();
    }

    /// <summary>operator-=.</summary>
    public void RetreatBy(int offset)
    {
        _currentIndex -= offset;
        UpdateOffsetFromIndex();
    }

    public int CurrentIndex => _currentIndex;

    public int Size => _offsets.Count;

    public void GoToIndex(int index)
    {
        _currentIndex = index;
        UpdateOffsetFromIndex();
    }

    public void GoToStart()
    {
        _currentIndex = 0;
        UpdateOffsetFromIndex();
    }

    public void GoToEnd()
    {
        _currentIndex = Size - 1;
        UpdateOffsetFromIndex();
    }

    public bool IsValid() => _currentIndex >= 0 && _currentIndex < _offsets.Count;

    void UpdateOffsetFromIndex()
    {
        if (IsValid()) SetOffsetUnchecked(_offsets[_currentIndex]);
    }
}
