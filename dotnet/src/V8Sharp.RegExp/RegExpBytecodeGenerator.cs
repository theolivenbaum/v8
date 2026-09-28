// Port of src/regexp/regexp-bytecode-generator.h,
// src/regexp/regexp-bytecode-generator-inl.h and
// src/regexp/regexp-bytecode-generator.cc.

using System.Buffers.Binary;

namespace V8Sharp.RegExp;

/// <summary>An operand value for BytecodeWriter.Emit: an integer, a label or a bit table.</summary>
public readonly struct BytecodeOperand
{
    public readonly long Value;
    public readonly Label? Label;
    public readonly byte[]? Table;
    public readonly bool IsLabel;

    BytecodeOperand(long value, Label? label, byte[]? table, bool isLabel)
    {
        Value = value;
        Label = label;
        Table = table;
        IsLabel = isLabel;
    }

    public static implicit operator BytecodeOperand(int v) => new(v, null, null, false);
    public static implicit operator BytecodeOperand(uint v) => new(v, null, null, false);
    public static implicit operator BytecodeOperand(char v) => new(v, null, null, false);
    public static implicit operator BytecodeOperand(Label? l) => new(0, l, null, true);
    public static implicit operator BytecodeOperand(byte[] t) => new(0, null, t, false);
    public static implicit operator BytecodeOperand(StandardCharacterSet s) => new((byte)s, null, null, false);
    public static implicit operator BytecodeOperand(RegExpMacroAssembler.StackCheckFlag s) => new((byte)s, null, null, false);
}

/// <summary>A sorted map of jump edges (V8's ZoneMap&lt;int, int&gt;) with lower_bound.</summary>
public sealed class JumpEdgeMap
{
    readonly List<int> _keys = [];
    readonly List<int> _values = [];

    public int Count => _keys.Count;
    public int KeyAt(int i) => _keys[i];
    public int ValueAt(int i) => _values[i];

    /// <summary>Index of the first key &gt;= key.</summary>
    public int LowerBound(int key)
    {
        int lo = 0, hi = _keys.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_keys[mid] < key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Index of the first key &gt; key.</summary>
    public int UpperBound(int key)
    {
        int lo = 0, hi = _keys.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_keys[mid] <= key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public bool ContainsKey(int key)
    {
        int i = LowerBound(key);
        return i < _keys.Count && _keys[i] == key;
    }

    /// <summary>std::map::emplace: inserts only if the key is absent.</summary>
    public bool Emplace(int key, int value)
    {
        int i = LowerBound(key);
        if (i < _keys.Count && _keys[i] == key) return false;
        _keys.Insert(i, key);
        _values.Insert(i, value);
        return true;
    }

    /// <summary>operator[] assignment.</summary>
    public void Set(int key, int value)
    {
        int i = LowerBound(key);
        if (i < _keys.Count && _keys[i] == key)
        {
            _values[i] = value;
            return;
        }
        _keys.Insert(i, key);
        _values.Insert(i, value);
    }

    public int Get(int key)
    {
        int i = LowerBound(key);
        return i < _keys.Count && _keys[i] == key ? _values[i] : 0;
    }

    public void Increment(int key, int by)
    {
        int i = LowerBound(key);
        if (i < _keys.Count && _keys[i] == key)
        {
            _values[i] += by;
            return;
        }
        _keys.Insert(i, key);
        _values.Insert(i, by);
    }

    public void SetValueAt(int index, int value) => _values[index] = value;
}

public class RegExpBytecodeWriter
{
    // The buffer into which code is generated.
    const int kInitialBufferSizeInBytes = 1024;
    const int kMaxBufferGrowthInBytes = 1024 * 1024;

    protected byte[] _buffer = [];
    // The program counter. Always points at the beginning of a bytecode while
    // we generate the ByteArray. Points to the end when we are done.
    protected int _pc;
    // Stores jump edges emitted for the bytecode (used by the peephole pass).
    // Key: jump source (offset in buffer where jump destination is stored).
    // Value: jump destination (offset in buffer to jump to).
    readonly JumpEdgeMap _jumpEdges = new();

    public int Pc => _pc;
    public byte[] Buffer => _buffer;
    public JumpEdgeMap JumpEdges => _jumpEdges;
    public int Length => _pc;

    void EnsureCapacity(int sizeDelta)
    {
        int requiredSize = _pc + sizeDelta;
        int size = _buffer.Length;
        if (size >= requiredSize) return;
        if (requiredSize < kInitialBufferSizeInBytes)
        {
            size = kInitialBufferSizeInBytes;
        }
        else if (requiredSize <= kMaxBufferGrowthInBytes)
        {
            // We use a doubling strategy until hitting the limit.
            size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)requiredSize);
        }
        else
        {
            // .. and kMaxBufferGrowthInBytes chunks afterwards.
            size = (requiredSize + kMaxBufferGrowthInBytes - 1) / kMaxBufferGrowthInBytes * kMaxBufferGrowthInBytes;
        }
        Array.Resize(ref _buffer, size);
    }

    public void EmitBytecode(Bytecode bc)
    {
        int size = Bytecodes.Size(bc);
        EnsureCapacity(size);
        // V8 zero-fills the padding in debug builds; the port always does.
        _buffer.AsSpan(_pc, size).Clear();
        _buffer[_pc] = (byte)bc;
    }

    public void Finalize(Bytecode bc) => _pc += Bytecodes.Size(bc);

    // Update bookkeeping at bytecode boundaries.
    public void ResetPc(int newPc)
    {
        Debug.Assert(newPc <= _pc);
        _pc = newPc;
    }

    public void EmitRaw(int value, int size, int offset)
    {
        int pos = _pc + offset;
        switch (size)
        {
            case 1: _buffer[pos] = (byte)value; break;
            case 2: BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(pos), (short)value); break;
            case 4: BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(pos), value); break;
            default: throw new InvalidOperationException("UNREACHABLE");
        }
    }

    public void OverwriteValue(uint value, int absoluteOffset) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(absoluteOffset), value);

    // MUST start and end at a bytecode boundary.
    public void EmitRawBytecodeStream(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        Debug.Assert(data.Length % Bytecodes.kBytecodeAlignment == 0);
        data.CopyTo(_buffer.AsSpan(_pc));
        _pc += data.Length;
    }

    public void EmitRawBytecodeStream(RegExpBytecodeWriter srcWriter, int srcOffset, int length)
    {
        int startPc = _pc;
        EmitRawBytecodeStream(srcWriter._buffer.AsSpan(srcOffset, length));

        // Copy jumps in range.
        JumpEdgeMap srcEdges = srcWriter._jumpEdges;
        // Iterate over all jumps that start in the copied range.
        for (int i = srcEdges.LowerBound(srcOffset); i < srcEdges.Count && srcEdges.KeyAt(i) < srcOffset + length; i++)
        {
            int oldSource = srcEdges.KeyAt(i);
            int oldTarget = srcEdges.ValueAt(i);
            int newSource = startPc + (oldSource - srcOffset);
            _jumpEdges.Emplace(newSource, oldTarget);
        }
    }

    public void PatchJump(int target, int absoluteOffset)
    {
        Debug.Assert(_jumpEdges.ContainsKey(absoluteOffset));
        OverwriteValue((uint)target, absoluteOffset);
        _jumpEdges.Set(absoluteOffset, target);
    }

    /// <summary>Emits one operand of the bytecode being emitted at <see cref="Pc"/>.</summary>
    public void EmitOperand(BytecodeOperandType type, in BytecodeOperand value, int offset)
    {
        switch (type)
        {
            case BytecodeOperandType.kJumpTarget:
                if (value.IsLabel)
                {
                    EmitJumpTarget(value.Label!, offset);
                }
                else
                {
                    _jumpEdges.Emplace(_pc + offset, (int)value.Value);
                    EmitRaw((int)value.Value, 4, offset);
                }
                return;
            case BytecodeOperandType.kBitTable:
                if (value.Table!.Length == RegExpMacroAssembler.kTableSize)
                {
                    // A boolean table (one byte per character): pack into bits.
                    for (int i = 0; i < RegExpMacroAssembler.kTableSize; i += 8)
                    {
                        int b = 0;
                        for (int j = 0; j < 8; j++)
                        {
                            if (value.Table[i + j] != 0) b |= 1 << j;
                        }
                        _buffer[_pc + offset + i / 8] = (byte)b;
                    }
                }
                else
                {
                    // An already packed 16-byte table (peephole copies).
                    Debug.Assert(value.Table.Length == 16);
                    value.Table.CopyTo(_buffer.AsSpan(_pc + offset));
                }
                return;
            default:
                CheckOperandLimits(type, value.Value);
                EmitRaw((int)value.Value, Bytecodes.Size(type), offset);
                return;
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    static void CheckOperandLimits(BytecodeOperandType type, long value)
    {
        (long min, long max) = type switch
        {
            BytecodeOperandType.kInt16 => (short.MinValue, short.MaxValue),
            BytecodeOperandType.kInt32 => (int.MinValue, int.MaxValue),
            BytecodeOperandType.kUint32 => (uint.MinValue, (long)uint.MaxValue),
            BytecodeOperandType.kChar => (char.MinValue, char.MaxValue),
            BytecodeOperandType.kOffset => (RegExpMacroAssembler.kMinCPOffset, RegExpMacroAssembler.kMaxCPOffset),
            BytecodeOperandType.kBoundsCheckOffset => (RegExpMacroAssembler.kMinCPOffset,
                RegExpMacroAssembler.kMaxCPOffset + RegExpMacroAssembler.kMaxEatsAtLeastValue),
            BytecodeOperandType.kRegister => (0, RegExpMacroAssembler.kMaxRegister),
            _ => (long.MinValue, long.MaxValue),
        };
        Debug.Assert(value >= min && value <= max, $"operand {type} out of range: {value}");
    }

    void EmitJumpTarget(Label label, int offset)
    {
        int currentPc = _pc + offset;
        int pos = 0;
        if (label.IsBound)
        {
            pos = label.Pos;
            _jumpEdges.Emplace(currentPc, pos);
        }
        else
        {
            if (label.IsLinked) pos = label.Pos;
            label.LinkTo(currentPc);
        }
        EmitRaw(pos, 4, offset);
    }

    /// <summary>Templated code emission: the bytecode, then each operand at its offset.</summary>
    public void Emit(Bytecode bytecode, ReadOnlySpan<BytecodeOperand> args)
    {
        BytecodeInfo info = Bytecodes.Info(bytecode);
        Debug.Assert(args.Length == info.OperandTypes.Length, "Wrong number of operands");
        EmitBytecode(bytecode);
        for (int i = 0; i < args.Length; i++)
        {
            EmitOperand(info.OperandTypes[i], args[i], info.OperandOffsets[i]);
        }
        Finalize(bytecode);
    }

    public byte[] CopyBuffer() => _buffer.AsSpan(0, _pc).ToArray();
}

/// <summary>An assembler/generator for the Irregexp byte code.</summary>
public sealed class RegExpBytecodeGenerator : RegExpMacroAssembler
{
    readonly RegExpBytecodeWriter _writer = new();
    readonly Label _backtrack = new();

    // Tracks a just-emitted AdvanceCurrentPosition so the next GoTo can fuse
    // both into a single kAdvanceCpAndGoto bytecode.
    const int kInvalidPC = -1;
    int _advanceCurrentStart;
    int _advanceCurrentOffset;
    int _advanceCurrentEnd = kInvalidPC;

    /// <summary>--regexp-peephole-optimization (default true).</summary>
    public bool PeepholeOptimization { get; set; } = true;

    public RegExpBytecodeGenerator(Mode mode) : base(mode) { }

    public RegExpBytecodeWriter Writer => _writer;

    public override IrregexpImplementation Implementation() => IrregexpImplementation.kBytecodeImplementation;

    // Converts null labels into our internal backtrack_ label.
    Label FixLabel(Label? l) => l ?? _backtrack;

    void Emit(Bytecode bc, ReadOnlySpan<BytecodeOperand> args) => _writer.Emit(bc, args);

    public override void Bind(Label l)
    {
        _advanceCurrentEnd = kInvalidPC;
        Debug.Assert(!l.IsBound);
        if (l.IsLinked)
        {
            int pos = l.Pos;
            while (pos != 0)
            {
                int fixup = pos;
                pos = BinaryPrimitives.ReadInt32LittleEndian(_writer.Buffer.AsSpan(fixup));
                _writer.OverwriteValue((uint)_writer.Pc, fixup);
                _writer.JumpEdges.Emplace(fixup, _writer.Pc);
            }
        }
        l.BindTo(_writer.Pc);
    }

    public override void PopRegister(int registerIndex) => Emit(Bytecode.kPopRegister, [registerIndex]);

    public override void PushRegister(int registerIndex, StackCheckFlag checkStackLimit) =>
        Emit(Bytecode.kPushRegister, [registerIndex, checkStackLimit]);

    public override void WriteCurrentPositionToRegister(int registerIndex, int cpOffset) =>
        Emit(Bytecode.kWriteCurrentPositionToRegister, [registerIndex, cpOffset]);

    public override void ClearRegisters(int regFrom, int regTo)
    {
        Debug.Assert(regFrom <= regTo);
        Emit(Bytecode.kClearRegisters, [regFrom, regTo]);
    }

    public override void ReadCurrentPositionFromRegister(int registerIndex) =>
        Emit(Bytecode.kReadCurrentPositionFromRegister, [registerIndex]);

    public override void WriteStackPointerToRegister(int registerIndex) =>
        Emit(Bytecode.kWriteStackPointerToRegister, [registerIndex]);

    public override void ReadStackPointerFromRegister(int registerIndex) =>
        Emit(Bytecode.kReadStackPointerFromRegister, [registerIndex]);

    public override void SetCurrentPositionFromEnd(int by) => Emit(Bytecode.kSetCurrentPositionFromEnd, [by]);

    public override void SetRegister(int registerIndex, int to) => Emit(Bytecode.kSetRegister, [registerIndex, to]);

    public override void AdvanceRegister(int registerIndex, int by) =>
        Emit(Bytecode.kAdvanceRegister, [registerIndex, by]);

    public override void PopCurrentPosition() => Emit(Bytecode.kPopCurrentPosition, []);

    public override void PushCurrentPosition() => Emit(Bytecode.kPushCurrentPosition, []);

    public override void Backtrack()
    {
        int errorCode = CanFallback ? RegExpResult.RE_FALLBACK_TO_EXPERIMENTAL : RegExpResult.RE_FAILURE;
        Emit(Bytecode.kBacktrack, [errorCode]);
    }

    public override void GoTo(Label? label)
    {
        if (_advanceCurrentEnd == _writer.Pc)
        {
            // Fuse the preceding AdvanceCurrentPosition into kAdvanceCpAndGoto.
            _writer.ResetPc(_advanceCurrentStart);
            Emit(Bytecode.kAdvanceCpAndGoto, [_advanceCurrentOffset, FixLabel(label)]);
            _advanceCurrentEnd = kInvalidPC;
        }
        else
        {
            Emit(Bytecode.kGoTo, [FixLabel(label)]);
        }
    }

    public override void PushBacktrack(Label label) => Emit(Bytecode.kPushBacktrack, [FixLabel(label)]);

    public override bool Succeed()
    {
        Emit(Bytecode.kSucceed, []);
        return false;  // Restart matching for global regexp not supported.
    }

    public override void Fail() => Emit(Bytecode.kFail, []);

    public override void AdvanceCurrentPosition(int by)
    {
        _advanceCurrentStart = _writer.Pc;
        _advanceCurrentOffset = by;
        Emit(Bytecode.kAdvanceCurrentPosition, [by]);
        _advanceCurrentEnd = _writer.Pc;
    }

    public override void CheckFixedLengthLoop(Label? onTosEqualsCurrentPosition) =>
        Emit(Bytecode.kCheckFixedLengthLoop, [FixLabel(onTosEqualsCurrentPosition)]);

    public override void CheckPosition(int cpOffset, Label? onOutsideInput) =>
        Emit(Bytecode.kCheckPosition, [cpOffset, FixLabel(onOutsideInput)]);

    public override void CheckSpecialClassRanges(StandardCharacterSet type, Label? onNoMatch)
    {
        Debug.Assert(CanOptimizeSpecialClassRanges(type));
        Emit(Bytecode.kCheckSpecialClassRanges, [type, FixLabel(onNoMatch)]);
    }

    public override void LoadCurrentCharacterImpl(int cpOffset, Label? onFailure, bool checkBounds, int characters,
        int boundsCheckOffset)
    {
        if (!(cpOffset >= kMinCPOffset && cpOffset <= kMaxCPOffset)) throw new InvalidOperationException("CHECK failed");
        if (checkBounds)
        {
            Debug.Assert(cpOffset <= boundsCheckOffset);
            if (characters == 4)
            {
                Emit(Bytecode.kLoad4CurrentChars, [cpOffset, boundsCheckOffset, FixLabel(onFailure)]);
            }
            else if (characters == 2)
            {
                Emit(Bytecode.kLoad2CurrentChars, [cpOffset, boundsCheckOffset, FixLabel(onFailure)]);
            }
            else
            {
                Debug.Assert(characters == 1);
                Emit(Bytecode.kLoadCurrentCharacter, [cpOffset, boundsCheckOffset, FixLabel(onFailure)]);
            }
        }
        else
        {
            if (characters == 4)
            {
                Emit(Bytecode.kLoad4CurrentCharsUnchecked, [cpOffset]);
            }
            else if (characters == 2)
            {
                Emit(Bytecode.kLoad2CurrentCharsUnchecked, [cpOffset]);
            }
            else
            {
                Debug.Assert(characters == 1);
                Emit(Bytecode.kLoadCurrentCharacterUnchecked, [cpOffset]);
            }
        }
    }

    // Used to decide whether we use the `Char` or `4Chars` variant of a bytecode.
    const uint kMaxSingleCharValue = char.MaxValue;

    public override void CheckCharacterLT(char limit, Label? onLess) =>
        Emit(Bytecode.kCheckCharacterLT, [limit, FixLabel(onLess)]);

    public override void CheckCharacterGT(char limit, Label? onGreater) =>
        Emit(Bytecode.kCheckCharacterGT, [limit, FixLabel(onGreater)]);

    public override void CheckCharacter(uint c, Label? onEqual)
    {
        if (c > kMaxSingleCharValue) Emit(Bytecode.kCheck4Chars, [c, FixLabel(onEqual)]);
        else Emit(Bytecode.kCheckCharacter, [c, FixLabel(onEqual)]);
    }

    public override void CheckAtStart(int cpOffset, Label? onAtStart) =>
        Emit(Bytecode.kCheckAtStart, [cpOffset, FixLabel(onAtStart)]);

    public override void CheckNotAtStart(int cpOffset, Label? onNotAtStart) =>
        Emit(Bytecode.kCheckNotAtStart, [cpOffset, FixLabel(onNotAtStart)]);

    public override void CheckNotCharacter(uint c, Label? onNotEqual)
    {
        if (c > kMaxSingleCharValue) Emit(Bytecode.kCheckNot4Chars, [c, FixLabel(onNotEqual)]);
        else Emit(Bytecode.kCheckNotCharacter, [c, FixLabel(onNotEqual)]);
    }

    public override void CheckCharacterAfterAnd(uint c, uint mask, Label? onEqual)
    {
        // TODO(pthier): This is super hacky. We could still check for 4 characters
        // (with the last 2 being 0 after masking them), but not emit AndCheck4Chars.
        // This is rather confusing and should be changed.
        if (c > kMaxSingleCharValue) Emit(Bytecode.kAndCheck4Chars, [c, mask, FixLabel(onEqual)]);
        else Emit(Bytecode.kCheckCharacterAfterAnd, [c, mask, FixLabel(onEqual)]);
    }

    public override void CheckNotCharacterAfterAnd(uint c, uint mask, Label? onNotEqual)
    {
        if (c > kMaxSingleCharValue) Emit(Bytecode.kAndCheckNot4Chars, [c, mask, FixLabel(onNotEqual)]);
        else Emit(Bytecode.kCheckNotCharacterAfterAnd, [c, mask, FixLabel(onNotEqual)]);
    }

    public override void CheckNotCharacterAfterMinusAnd(char c, char minus, char mask, Label? onNotEqual) =>
        Emit(Bytecode.kCheckNotCharacterAfterMinusAnd, [c, minus, mask, FixLabel(onNotEqual)]);

    public override void CheckCharacterInRange(char from, char to, Label? onInRange) =>
        Emit(Bytecode.kCheckCharacterInRange, [from, to, FixLabel(onInRange)]);

    public override void CheckCharacterNotInRange(char from, char to, Label? onNotInRange) =>
        Emit(Bytecode.kCheckCharacterNotInRange, [from, to, FixLabel(onNotInRange)]);

    // Disabled in the interpreter, because 1) there is no constant pool that
    // could store the ByteArray pointer, 2) bytecode size limits are not as
    // restrictive as code (e.g. branch distances on arm), 3) bytecode for
    // large character classes is already quite compact.
    public override bool CheckCharacterInRangeArray(List<CharacterRange> ranges, Label? onInRange) => false;
    public override bool CheckCharacterNotInRangeArray(List<CharacterRange> ranges, Label? onNotInRange) => false;

    public override void CheckBitInTable(byte[] table, Label? onBitSet) =>
        Emit(Bytecode.kCheckBitInTable, [FixLabel(onBitSet), table]);

    public override void SkipUntilBitInTable(int cpOffset, byte[] table, byte[]? nibbleTable, int advanceBy,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) =>
        Emit(Bytecode.kSkipUntilBitInTable,
            [cpOffset, advanceBy, table, boundsCheckOffset, FixLabel(onMatch), FixLabel(onNoMatch)]);

    public override void SkipUntilCharAnd(int cpOffset, int advanceBy, uint character, uint mask,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) =>
        Emit(Bytecode.kSkipUntilCharAnd,
            [cpOffset, advanceBy, character, mask, boundsCheckOffset, FixLabel(onMatch), FixLabel(onNoMatch)]);

    public override void SkipUntilChar(int cpOffset, int advanceBy, uint character, int boundsCheckOffset,
        Label? onMatch, Label? onNoMatch) =>
        Emit(Bytecode.kSkipUntilChar,
            [cpOffset, advanceBy, character, boundsCheckOffset, FixLabel(onMatch), FixLabel(onNoMatch)]);

    public override void SkipUntilCharOrChar(int cpOffset, int advanceBy, uint char1, uint char2,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) =>
        Emit(Bytecode.kSkipUntilCharOrChar,
            [cpOffset, advanceBy, char1, char2, boundsCheckOffset, FixLabel(onMatch), FixLabel(onNoMatch)]);

    public override void SkipUntilGtOrNotBitInTable(int cpOffset, int advanceBy, uint character, byte[] table,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) =>
        // Only generated by peephole optimization.
        throw new InvalidOperationException("UNREACHABLE");

    public override void SkipUntilOneOfMasked(int cpOffset, int advanceBy, uint bothChars, uint bothMask,
        int maxOffset, uint chars1, uint mask1, uint chars2, uint mask2, Label? onMatch1, Label? onMatch2,
        Label? onFailure) =>
        Emit(Bytecode.kSkipUntilOneOfMasked,
            [cpOffset, advanceBy, bothChars, bothMask, maxOffset, chars1, mask1, chars2, mask2,
             FixLabel(onMatch1), FixLabel(onMatch2), FixLabel(onFailure)]);

    public override void SkipUntilOneOfMasked3(SkipUntilOneOfMasked3Args args) =>
        // The nibble table is a native-only SIMD acceleration and has no bytecode
        // operand; it is reconstructed from the boolean table when the interpreter
        // tier compiles this bytecode.
        Emit(Bytecode.kSkipUntilOneOfMasked3,
            [args.bc0_cp_offset, args.bc0_advance_by, args.bc0_table, args.bc1_bounds_check_offset,
             FixLabel(args.bc1_on_failure), args.bc1_cp_offset, args.bc2_characters, args.bc2_mask, args.bc3_by,
             args.bc4_bounds_check_offset, args.bc4_cp_offset, args.bc5_characters, args.bc5_mask,
             FixLabel(args.bc5_on_equal), args.bc6_characters, args.bc6_mask, FixLabel(args.bc6_on_equal),
             args.bc7_characters, args.bc7_mask, FixLabel(args.fallthrough_jump_target)]);

    public override void CheckNotBackReference(int startReg, bool readBackward, Label? onNotEqual)
    {
        if (readBackward) Emit(Bytecode.kCheckNotBackRefBackward, [startReg, FixLabel(onNotEqual)]);
        else Emit(Bytecode.kCheckNotBackRef, [startReg, FixLabel(onNotEqual)]);
    }

    public override void CheckNotBackReferenceIgnoreCase(int startReg, bool readBackward, bool unicode,
        Label? onNotEqual)
    {
        if (readBackward)
        {
            if (unicode) Emit(Bytecode.kCheckNotBackRefNoCaseUnicodeBackward, [startReg, FixLabel(onNotEqual)]);
            else Emit(Bytecode.kCheckNotBackRefNoCaseBackward, [startReg, FixLabel(onNotEqual)]);
        }
        else
        {
            if (unicode) Emit(Bytecode.kCheckNotBackRefNoCaseUnicode, [startReg, FixLabel(onNotEqual)]);
            else Emit(Bytecode.kCheckNotBackRefNoCase, [startReg, FixLabel(onNotEqual)]);
        }
    }

    public override void IfRegisterLT(int registerIndex, int comparand, Label? onLessThan) =>
        Emit(Bytecode.kIfRegisterLT, [registerIndex, comparand, FixLabel(onLessThan)]);

    public override void IfRegisterGE(int registerIndex, int comparand, Label? onGreaterOrEqual) =>
        Emit(Bytecode.kIfRegisterGE, [registerIndex, comparand, FixLabel(onGreaterOrEqual)]);

    public override void IfRegisterEqPos(int registerIndex, Label? onEqual) =>
        Emit(Bytecode.kIfRegisterEqPos, [registerIndex, FixLabel(onEqual)]);

    public override void RecordComment(string comment) { }

    /// <summary>Returns the bytecode array (byte[]).</summary>
    public override object GetCode(string source, RegExpFlags flags)
    {
        Bind(_backtrack);
        Backtrack();

        if (PeepholeOptimization)
        {
            return RegExpBytecodePeepholeOptimization.OptimizeBytecode(_writer);
        }
        return _writer.CopyBuffer();
    }
}
