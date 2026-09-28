// Port of src/regexp/regexp-interpreter.h and src/regexp/regexp-interpreter.cc.
//
// A simple interpreter for the Irregexp byte code. The subject is a UTF-16
// span; V8's RawMatch<uint8_t> (one-byte subjects, run with one-byte
// bytecode) is RawMatch<OneByte> here and requires every code unit to be at
// most 0xFF.
//
// V8's interrupt handling at backtracks (stack guard, GC relocation of the
// subject) has no equivalent: the subject is a managed span that cannot move.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace V8Sharp.RegExp;

public static class IrregexpInterpreter
{
    public const int FAILURE = RegExpResult.kInternalRegExpFailure;
    public const int SUCCESS = RegExpResult.kInternalRegExpSuccess;
    public const int EXCEPTION = RegExpResult.kInternalRegExpException;
    public const int RETRY = RegExpResult.kInternalRegExpRetry;
    public const int FALLBACK_TO_EXPERIMENTAL = RegExpResult.kInternalRegExpFallbackToExperimental;

    /// <summary>RegExpStack::kMaximumStackSize (64 MB) in int slots.</summary>
    const int kMaxBacktrackStackSize = 64 * 1024 * 1024 / sizeof(int);

    // Registers used during interpreter execution consist of output registers
    // in indices [0, output_register_count[ which will contain matcher results
    // as a {start,end} index tuple for each capture (where the whole match counts
    // as implicit capture 0); and internal registers in indices
    // [output_register_count, total_register_count[.
    const int kNoMatchValue = -1;

    /// <summary>
    /// IrregexpInterpreter::MatchInternal: runs the bytecode once at
    /// <paramref name="startPosition"/>. On SUCCESS the first
    /// <paramref name="outputRegisters"/>.Length registers are copied out. On
    /// EXCEPTION the backtrack stack overflowed (V8 throws "Maximum call stack
    /// size exceeded").
    /// </summary>
    public static int MatchInternal(byte[] code, ReadOnlySpan<char> subject, Span<int> outputRegisters,
        int totalRegisterCount, int startPosition, uint backtrackLimit, bool isOneByte = false)
    {
        uint previousChar = '\n';
        if (startPosition != 0) previousChar = subject[startPosition - 1];
        return isOneByte
            ? RawMatch<OneByte>(code, subject, outputRegisters, totalRegisterCount, startPosition, previousChar,
                backtrackLimit)
            : RawMatch<TwoByte>(code, subject, outputRegisters, totalRegisterCount, startPosition, previousChar,
                backtrackLimit);
    }

    // The Char template parameter of V8's RawMatch.
    interface ICharWidth
    {
        static abstract bool IsOneByte { get; }
    }

    struct OneByte : ICharWidth
    {
        public static bool IsOneByte => true;
    }

    struct TwoByte : ICharWidth
    {
        public static bool IsOneByte => false;
    }

    /// <summary>
    /// IrregexpInterpreter::Match: in global mode, when outputRegisters has
    /// space for more than one match, keeps running until all matches are
    /// filled in. Returns the number of matches, or a negative result code.
    /// </summary>
    public static int Match(byte[] code, ReadOnlySpan<char> subject, Span<int> outputRegisters,
        int registersPerMatch, int totalRegisterCount, int startPosition, uint backtrackLimit, bool isAnyUnicode,
        bool isOneByte = false)
    {
        int numberOfMatchesInOutputRegisters = outputRegisters.Length / registersPerMatch;
        int numMatches = 0;
        int offset = 0;
        for (int i = 0; i < numberOfMatchesInOutputRegisters; i++)
        {
            Span<int> current = outputRegisters.Slice(offset, registersPerMatch);
            int currentResult = MatchInternal(code, subject, current, totalRegisterCount, startPosition,
                backtrackLimit, isOneByte);
            if (currentResult == SUCCESS)
            {
                // Fall through.
            }
            else if (currentResult == FAILURE)
            {
                break;
            }
            else
            {
                return currentResult;
            }

            // Found a match. Advance the index.
            numMatches++;

            int nextStartPosition = current[1];
            if (nextStartPosition == current[0])
            {
                // Zero-length matches.
                nextStartPosition = RegExpUtils.AdvanceStringIndex(subject, nextStartPosition, isAnyUnicode);
                if (nextStartPosition > subject.Length) break;
            }

            startPosition = nextStartPosition;
            offset += registersPerMatch;
        }
        return numMatches;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short I16(byte[] c, int p) => BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(p, 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ushort U16(byte[] c, int p) => BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(p, 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int I32(byte[] c, int p) => BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(p, 4));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint U32(byte[] c, int p) => BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(p, 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CheckBitInTable(uint currentChar, byte[] code, int tableOffset)
    {
        int mask = RegExpMacroAssembler.kTableMask;
        int b = code[tableOffset + (int)((currentChar & mask) >> 3)];
        int bit = (int)(currentChar & 7);
        return (b & (1 << bit)) != 0;
    }

    // Returns true iff 0 <= index < length.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IndexIsInBounds(int index, int length) => (uint)index < (uint)length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Load2Characters<TWidth>(ReadOnlySpan<char> s, int index) where TWidth : struct, ICharWidth =>
        s[index] | ((uint)s[index + 1] << (TWidth.IsOneByte ? 8 : 16));

    // Only valid for one-byte subjects.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Load4Characters(ReadOnlySpan<char> s, int index) =>
        s[index] | ((uint)s[index + 1] << 8) | ((uint)s[index + 2] << 16) | ((uint)s[index + 3] << 24);

    static bool CheckSpecialClassRanges<TWidth>(uint currentChar, StandardCharacterSet characterSet)
        where TWidth : struct, ICharWidth
    {
        bool isOneByte = TWidth.IsOneByte;
        switch (characterSet)
        {
            case StandardCharacterSet.kWhitespace:
                Debug.Assert(isOneByte);
                return currentChar == ' ' || currentChar - '\t' <= '\r' - '\t' || currentChar == 0xA0;
            case StandardCharacterSet.kNotWhitespace:
                throw new InvalidOperationException("UNREACHABLE");
            case StandardCharacterSet.kWord:
                if (!isOneByte && currentChar > 'z') return false;
                return RegExpMacroAssembler.WordCharacterMap[(int)currentChar] != 0;
            case StandardCharacterSet.kNotWord:
                if (!isOneByte && currentChar > 'z') return true;
                return RegExpMacroAssembler.WordCharacterMap[(int)currentChar] == 0;
            case StandardCharacterSet.kDigit:
                return currentChar - '0' <= 9;
            case StandardCharacterSet.kNotDigit:
                return currentChar - '0' > 9;
            case StandardCharacterSet.kLineTerminator:
                if (currentChar == '\n' || currentChar == '\r') return true;
                return !isOneByte && (currentChar == 0x2028 || currentChar == 0x2029);
            case StandardCharacterSet.kNotLineTerminator:
            {
                bool isOneByteMatch = currentChar != '\n' && currentChar != '\r';
                if (isOneByte) return isOneByteMatch;
                return isOneByteMatch && currentChar != 0x2028 && currentChar != 0x2029;
            }
            case StandardCharacterSet.kEverything:
                return true;
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    static int Operand(byte[] code, int pc, Bytecode bc, string name) =>
        Bytecodes.ReadOperand(code, pc + Bytecodes.Info(bc).Offset(name), Bytecodes.Info(bc).Type(name));

    static bool BackRefMatchesNoCase(int from, int current, int len, ReadOnlySpan<char> subject, bool unicode)
    {
        ReadOnlySpan<char> a = subject.Slice(from, len);
        ReadOnlySpan<char> b = subject.Slice(current, len);
        return unicode
            ? RegExpMacroAssembler.CaseInsensitiveCompareUnicode(a, b)
            : RegExpMacroAssembler.CaseInsensitiveCompareNonUnicode(a, b);
    }

    /// <summary>The backtracking stack (despite its name, a generic int stack).</summary>
    struct BacktrackStack
    {
        int[] _data;
        int _sp;

        public BacktrackStack()
        {
            _data = ArrayPool<int>.Shared.Rent(64);
            _sp = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Push(int v)
        {
            if (_sp == _data.Length) Grow();
            _data[_sp++] = v;
            return _sp <= kMaxBacktrackStackSize;
        }

        void Grow()
        {
            int[] bigger = ArrayPool<int>.Shared.Rent(_data.Length * 2);
            Array.Copy(_data, bigger, _sp);
            ArrayPool<int>.Shared.Return(_data);
            _data = bigger;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Peek()
        {
            if (_sp == 0) throw new InvalidOperationException("SBXCHECK failed: empty backtrack stack");
            return _data[_sp - 1];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Pop()
        {
            int v = Peek();
            _sp--;
            return v;
        }

        // The 'sp' is the index of the first empty element in the stack.
        public readonly int Sp => _sp;
        public void SetSp(int newSp)
        {
            Debug.Assert(newSp <= _sp);
            _sp = newSp;
        }

        public readonly void Release() => ArrayPool<int>.Shared.Return(_data);
    }

    static int RawMatch<TWidth>(byte[] code, ReadOnlySpan<char> subject, Span<int> outputRegisters,
        int totalRegisterCount, int current, uint currentChar, uint backtrackLimit) where TWidth : struct, ICharWidth
    {
        int outputRegisterCount = outputRegisters.Length;
        if (outputRegisterCount < 2 || totalRegisterCount < outputRegisterCount ||
            totalRegisterCount > RegExpMacroAssembler.kMaxRegisterCount)
        {
            throw new InvalidOperationException("SBXCHECK failed: register counts");
        }

        int[]? rented = null;
        Span<int> registers = totalRegisterCount <= 64
            ? stackalloc int[64]
            : (rented = ArrayPool<int>.Shared.Rent(totalRegisterCount));
        registers = registers[..totalRegisterCount];
        registers.Fill(kNoMatchValue);

        var backtrackStack = new BacktrackStack();
        try
        {
            uint backtrackCount = 0;
            int subjectLength = subject.Length;
            int pc = 0;

            while (true)
            {
                switch ((Bytecode)code[pc])
                {
                    case Bytecode.kBreak:
                        throw new InvalidOperationException("UNREACHABLE");
                    case Bytecode.kPushCurrentPosition:
                        pc += 4;
                        if (!backtrackStack.Push(current)) return EXCEPTION;
                        break;
                    case Bytecode.kPushBacktrack:
                    {
                        int label = I32(code, pc + 4);
                        pc += 8;
                        if (!backtrackStack.Push(label)) return EXCEPTION;
                        break;
                    }
                    case Bytecode.kPushRegister:
                    {
                        int registerIndex = U16(code, pc + 2);
                        pc += 8;
                        if (!backtrackStack.Push(registers[registerIndex])) return EXCEPTION;
                        break;
                    }
                    case Bytecode.kSetRegister:
                        registers[U16(code, pc + 2)] = I32(code, pc + 4);
                        pc += 8;
                        break;
                    case Bytecode.kClearRegisters:
                    {
                        int from = U16(code, pc + 2);
                        int to = U16(code, pc + 4);
                        if (from > to) throw new InvalidOperationException("SBXCHECK failed");
                        registers.Slice(from, to - from + 1).Fill(kNoMatchValue);
                        pc += 8;
                        break;
                    }
                    case Bytecode.kAdvanceRegister:
                        registers[U16(code, pc + 2)] += I16(code, pc + 4);
                        pc += 8;
                        break;
                    case Bytecode.kWriteCurrentPositionToRegister:
                        registers[U16(code, pc + 2)] = current + I16(code, pc + 4);
                        pc += 8;
                        break;
                    case Bytecode.kReadCurrentPositionFromRegister:
                        current = registers[U16(code, pc + 2)];
                        pc += 4;
                        break;
                    case Bytecode.kWriteStackPointerToRegister:
                        registers[U16(code, pc + 2)] = backtrackStack.Sp;
                        pc += 4;
                        break;
                    case Bytecode.kReadStackPointerFromRegister:
                        backtrackStack.SetSp(registers[U16(code, pc + 2)]);
                        pc += 4;
                        break;
                    case Bytecode.kPopCurrentPosition:
                        current = backtrackStack.Pop();
                        pc += 4;
                        break;
                    case Bytecode.kBacktrack:
                    {
                        // JSRegExp::kNoBacktrackLimit == 0.
                        if (++backtrackCount == backtrackLimit)
                        {
                            return I16(code, pc + 2);
                        }
                        pc = backtrackStack.Pop();
                        break;
                    }
                    case Bytecode.kPopRegister:
                        registers[U16(code, pc + 2)] = backtrackStack.Pop();
                        pc += 4;
                        break;
                    case Bytecode.kFail:
                        return FAILURE;
                    case Bytecode.kSucceed:
                        registers[..outputRegisterCount].CopyTo(outputRegisters);
                        return SUCCESS;
                    case Bytecode.kAdvanceCurrentPosition:
                        current += I16(code, pc + 2);
                        pc += 4;
                        break;
                    case Bytecode.kGoTo:
                        pc = I32(code, pc + 4);
                        break;
                    case Bytecode.kAdvanceCpAndGoto:
                        current += I16(code, pc + 2);
                        pc = I32(code, pc + 4);
                        break;
                    case Bytecode.kCheckFixedLengthLoop:
                        if (current == backtrackStack.Peek())
                        {
                            pc = I32(code, pc + 4);
                            backtrackStack.Pop();
                        }
                        else
                        {
                            pc += 8;
                        }
                        break;
                    case Bytecode.kLoadCurrentCharacter:
                        if (!IndexIsInBounds(current + I32(code, pc + 4), subjectLength))
                        {
                            pc = I32(code, pc + 8);
                        }
                        else
                        {
                            currentChar = subject[current + I16(code, pc + 2)];
                            pc += 12;
                        }
                        break;
                    case Bytecode.kLoadCurrentCharacterUnchecked:
                        currentChar = subject[current + I16(code, pc + 2)];
                        pc += 4;
                        break;
                    case Bytecode.kLoad2CurrentChars:
                        if (!IndexIsInBounds(current + I32(code, pc + 4), subjectLength))
                        {
                            pc = I32(code, pc + 8);
                        }
                        else
                        {
                            currentChar = Load2Characters<TWidth>(subject, current + I16(code, pc + 2));
                            pc += 12;
                        }
                        break;
                    case Bytecode.kLoad2CurrentCharsUnchecked:
                        currentChar = Load2Characters<TWidth>(subject, current + I16(code, pc + 2));
                        pc += 4;
                        break;
                    case Bytecode.kLoad4CurrentChars:
                        Debug.Assert(TWidth.IsOneByte);
                        if (!IndexIsInBounds(current + I32(code, pc + 4), subjectLength))
                        {
                            pc = I32(code, pc + 8);
                        }
                        else
                        {
                            currentChar = Load4Characters(subject, current + I16(code, pc + 2));
                            pc += 12;
                        }
                        break;
                    case Bytecode.kLoad4CurrentCharsUnchecked:
                        Debug.Assert(TWidth.IsOneByte);
                        currentChar = Load4Characters(subject, current + I16(code, pc + 2));
                        pc += 4;
                        break;
                    case Bytecode.kSkipUntilOneOfMasked:
                    {
                        // We should only get here in 1-byte mode.
                        Debug.Assert(TWidth.IsOneByte);
                        const Bytecode bc = Bytecode.kSkipUntilOneOfMasked;
                        int cpOffset = Operand(code, pc, bc, "cp_offset");
                        int advanceBy = Operand(code, pc, bc, "advance_by");
                        uint bothChars = (uint)Operand(code, pc, bc, "both_chars");
                        uint bothMask = (uint)Operand(code, pc, bc, "both_mask");
                        int maxOffset = Operand(code, pc, bc, "max_offset");
                        uint chars1 = (uint)Operand(code, pc, bc, "chars1");
                        uint mask1 = (uint)Operand(code, pc, bc, "mask1");
                        uint chars2 = (uint)Operand(code, pc, bc, "chars2");
                        uint mask2 = (uint)Operand(code, pc, bc, "mask2");
                        int next = Operand(code, pc, bc, "on_failure");
                        while (IndexIsInBounds(current + maxOffset, subjectLength))
                        {
                            currentChar = Load4Characters(subject, current + cpOffset);
                            if (bothChars == (currentChar & bothMask))
                            {
                                if (chars1 == (currentChar & mask1))
                                {
                                    next = Operand(code, pc, bc, "on_match1");
                                    break;
                                }
                                if (chars2 == (currentChar & mask2))
                                {
                                    next = Operand(code, pc, bc, "on_match2");
                                    break;
                                }
                            }
                            current += advanceBy;
                        }
                        pc = next;
                        break;
                    }
                    case Bytecode.kSkipUntilOneOfMasked3:
                    {
                        // We should only get here in 1-byte mode.
                        Debug.Assert(TWidth.IsOneByte);
                        const Bytecode bc = Bytecode.kSkipUntilOneOfMasked3;
                        int bc0CpOffset = Operand(code, pc, bc, "bc0_cp_offset");
                        int bc0AdvanceBy = Operand(code, pc, bc, "bc0_advance_by");
                        int bc0Table = pc + Bytecodes.Info(bc).Offset("bc0_table");
                        int bc1BoundsCheckOffset = Operand(code, pc, bc, "bc1_bounds_check_offset");
                        int bc1CpOffset = Operand(code, pc, bc, "bc1_cp_offset");
                        uint bc2Characters = (uint)Operand(code, pc, bc, "bc2_characters");
                        uint bc2Mask = (uint)Operand(code, pc, bc, "bc2_mask");
                        int bc3By = Operand(code, pc, bc, "bc3_by");
                        int bc4BoundsCheckOffset = Operand(code, pc, bc, "bc4_bounds_check_offset");
                        int bc4CpOffset = Operand(code, pc, bc, "bc4_cp_offset");
                        uint bc5Characters = (uint)Operand(code, pc, bc, "bc5_characters");
                        uint bc5Mask = (uint)Operand(code, pc, bc, "bc5_mask");
                        uint bc6Characters = (uint)Operand(code, pc, bc, "bc6_characters");
                        uint bc6Mask = (uint)Operand(code, pc, bc, "bc6_mask");
                        uint bc7Characters = (uint)Operand(code, pc, bc, "bc7_characters");
                        uint bc7Mask = (uint)Operand(code, pc, bc, "bc7_mask");
                        int next;
                        while (true)
                        {
                            // bc0: kSkipUntilBitInTable
                            while (IndexIsInBounds(current + bc0CpOffset, subjectLength))
                            {
                                currentChar = subject[current + bc0CpOffset];
                                if (CheckBitInTable(currentChar, code, bc0Table)) break;
                                current += bc0AdvanceBy;
                            }

                            // bc1: kLoad4CurrentChars
                            if (!IndexIsInBounds(current + bc1BoundsCheckOffset, subjectLength))
                            {
                                next = Operand(code, pc, bc, "bc1_on_failure");
                                break;
                            }

                            currentChar = Load4Characters(subject, current + bc1CpOffset);

                            // bc2: AndCheck4Chars
                            if (bc2Characters == (currentChar & bc2Mask))
                            {
                                // bc4: Load4CurrentChars
                                if (!IndexIsInBounds(current + bc4BoundsCheckOffset, subjectLength))
                                {
                                    // bc3: AdvanceCpAndGoto
                                    current += bc3By;
                                    continue;
                                }
                                currentChar = Load4Characters(subject, current + bc4CpOffset);

                                // bc5: AndCheck4Chars
                                if (bc5Characters == (currentChar & bc5Mask))
                                {
                                    next = Operand(code, pc, bc, "bc5_on_equal");
                                    break;
                                }
                                // bc6: AndCheck4Chars
                                if (bc6Characters == (currentChar & bc6Mask))
                                {
                                    next = Operand(code, pc, bc, "bc6_on_equal");
                                    break;
                                }
                                // bc7: AndCheckNot4Chars
                                if (bc7Characters == (currentChar & bc7Mask))
                                {
                                    next = Operand(code, pc, bc, "fallthrough_jump_target");
                                    break;
                                }
                            }

                            // bc3: AdvanceCpAndGoto
                            current += bc3By;
                        }
                        pc = next;
                        break;
                    }
                    case Bytecode.kCheck4Chars:
                        pc = U32(code, pc + 4) == currentChar ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kCheckCharacter:
                        pc = U16(code, pc + 2) == currentChar ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kCheckNot4Chars:
                        pc = U32(code, pc + 4) != currentChar ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kCheckNotCharacter:
                        pc = U16(code, pc + 2) != currentChar ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kAndCheck4Chars:
                        pc = U32(code, pc + 4) == (currentChar & U32(code, pc + 8)) ? I32(code, pc + 12) : pc + 16;
                        break;
                    case Bytecode.kCheckCharacterAfterAnd:
                        pc = U16(code, pc + 2) == (currentChar & U32(code, pc + 4)) ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kAndCheckNot4Chars:
                        pc = U32(code, pc + 4) != (currentChar & U32(code, pc + 8)) ? I32(code, pc + 12) : pc + 16;
                        break;
                    case Bytecode.kCheckNotCharacterAfterAnd:
                        pc = U16(code, pc + 2) != (currentChar & U32(code, pc + 4)) ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kCheckNotCharacterAfterMinusAnd:
                    {
                        uint character = U16(code, pc + 2);
                        uint minus = U16(code, pc + 4);
                        uint mask = U16(code, pc + 6);
                        pc = character != ((currentChar - minus) & mask) ? I32(code, pc + 8) : pc + 12;
                        break;
                    }
                    case Bytecode.kCheckCharacterInRange:
                    {
                        uint from = U16(code, pc + 2);
                        uint to = U16(code, pc + 4);
                        pc = from <= currentChar && currentChar <= to ? I32(code, pc + 8) : pc + 12;
                        break;
                    }
                    case Bytecode.kCheckCharacterNotInRange:
                    {
                        uint from = U16(code, pc + 2);
                        uint to = U16(code, pc + 4);
                        pc = from > currentChar || currentChar > to ? I32(code, pc + 8) : pc + 12;
                        break;
                    }
                    case Bytecode.kCheckBitInTable:
                        pc = CheckBitInTable(currentChar, code, pc + 8) ? I32(code, pc + 4) : pc + 24;
                        break;
                    case Bytecode.kCheckCharacterLT:
                        pc = currentChar < U16(code, pc + 2) ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kCheckCharacterGT:
                        pc = currentChar > U16(code, pc + 2) ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kIfRegisterLT:
                        pc = registers[U16(code, pc + 2)] < I32(code, pc + 4) ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kIfRegisterGE:
                        pc = registers[U16(code, pc + 2)] >= I32(code, pc + 4) ? I32(code, pc + 8) : pc + 12;
                        break;
                    case Bytecode.kIfRegisterEqPos:
                        pc = registers[U16(code, pc + 2)] == current ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kCheckNotBackRef:
                    {
                        int startReg = U16(code, pc + 2);
                        int from = registers[startReg];
                        int len = registers[startReg + 1] - from;
                        if (from >= 0 && len > 0)
                        {
                            if (current + len > subjectLength ||
                                !subject.Slice(from, len).SequenceEqual(subject.Slice(current, len)))
                            {
                                pc = I32(code, pc + 4);
                                break;
                            }
                            current += len;
                        }
                        pc += 8;
                        break;
                    }
                    case Bytecode.kCheckNotBackRefBackward:
                    {
                        int startReg = U16(code, pc + 2);
                        int from = registers[startReg];
                        int len = registers[startReg + 1] - from;
                        if (from >= 0 && len > 0)
                        {
                            if (current - len < 0 ||
                                !subject.Slice(from, len).SequenceEqual(subject.Slice(current - len, len)))
                            {
                                pc = I32(code, pc + 4);
                                break;
                            }
                            current -= len;
                        }
                        pc += 8;
                        break;
                    }
                    case Bytecode.kCheckNotBackRefNoCaseUnicode:
                    case Bytecode.kCheckNotBackRefNoCase:
                    {
                        bool unicode = (Bytecode)code[pc] == Bytecode.kCheckNotBackRefNoCaseUnicode;
                        int startReg = U16(code, pc + 2);
                        int from = registers[startReg];
                        int len = registers[startReg + 1] - from;
                        if (from >= 0 && len > 0)
                        {
                            if (current + len > subjectLength ||
                                !BackRefMatchesNoCase(from, current, len, subject, unicode))
                            {
                                pc = I32(code, pc + 4);
                                break;
                            }
                            current += len;
                        }
                        pc += 8;
                        break;
                    }
                    case Bytecode.kCheckNotBackRefNoCaseUnicodeBackward:
                    case Bytecode.kCheckNotBackRefNoCaseBackward:
                    {
                        bool unicode = (Bytecode)code[pc] == Bytecode.kCheckNotBackRefNoCaseUnicodeBackward;
                        int startReg = U16(code, pc + 2);
                        int from = registers[startReg];
                        int len = registers[startReg + 1] - from;
                        if (from >= 0 && len > 0)
                        {
                            if (current - len < 0 ||
                                !BackRefMatchesNoCase(from, current - len, len, subject, unicode))
                            {
                                pc = I32(code, pc + 4);
                                break;
                            }
                            current -= len;
                        }
                        pc += 8;
                        break;
                    }
                    case Bytecode.kCheckAtStart:
                        pc = current + I16(code, pc + 2) == 0 ? I32(code, pc + 4) : pc + 8;
                        break;
                    case Bytecode.kCheckNotAtStart:
                        pc = current + I16(code, pc + 2) == 0 ? pc + 8 : I32(code, pc + 4);
                        break;
                    case Bytecode.kSetCurrentPositionFromEnd:
                    {
                        int by = I16(code, pc + 2);
                        pc += 4;
                        if (subjectLength - current > by)
                        {
                            current = subjectLength - by;
                            currentChar = subject[current - 1];
                        }
                        break;
                    }
                    case Bytecode.kCheckPosition:
                    {
                        int pos = current + I16(code, pc + 2);
                        pc = pos >= subjectLength || pos < 0 ? I32(code, pc + 4) : pc + 8;
                        break;
                    }
                    case Bytecode.kCheckSpecialClassRanges:
                        pc = CheckSpecialClassRanges<TWidth>(currentChar, (StandardCharacterSet)code[pc + 1])
                            ? pc + 8
                            : I32(code, pc + 4);
                        break;
                    case Bytecode.kSkipUntilChar:
                    {
                        int cpOffset = I16(code, pc + 2);
                        int advanceBy = I16(code, pc + 4);
                        uint character = U16(code, pc + 6);
                        int boundsCheckOffset = I32(code, pc + 8);
                        int onMatch = I32(code, pc + 12);
                        int onNoMatch = I32(code, pc + 16);
                        pc = onNoMatch;
                        while (IndexIsInBounds(current + boundsCheckOffset, subjectLength))
                        {
                            currentChar = subject[current + cpOffset];
                            if (character == currentChar)
                            {
                                pc = onMatch;
                                break;
                            }
                            current += advanceBy;
                        }
                        break;
                    }
                    case Bytecode.kSkipUntilCharAnd:
                    {
                        int cpOffset = I16(code, pc + 2);
                        int advanceBy = I16(code, pc + 4);
                        uint character = U16(code, pc + 6);
                        uint mask = U32(code, pc + 8);
                        int boundsCheckOffset = I32(code, pc + 12);
                        int onMatch = I32(code, pc + 16);
                        int onNoMatch = I32(code, pc + 20);
                        pc = onNoMatch;
                        while (IndexIsInBounds(current + boundsCheckOffset, subjectLength))
                        {
                            currentChar = subject[current + cpOffset];
                            if (character == (currentChar & mask))
                            {
                                pc = onMatch;
                                break;
                            }
                            current += advanceBy;
                        }
                        break;
                    }
                    case Bytecode.kSkipUntilBitInTable:
                    {
                        int cpOffset = I16(code, pc + 2);
                        int advanceBy = I16(code, pc + 4);
                        int table = pc + 8;
                        int boundsCheckOffset = I32(code, pc + 24);
                        int onMatch = I32(code, pc + 28);
                        int onNoMatch = I32(code, pc + 32);
                        int next = onNoMatch;
                        while (IndexIsInBounds(current + boundsCheckOffset, subjectLength))
                        {
                            currentChar = subject[current + cpOffset];
                            if (CheckBitInTable(currentChar, code, table))
                            {
                                next = onMatch;
                                break;
                            }
                            current += advanceBy;
                        }
                        pc = next;
                        break;
                    }
                    case Bytecode.kSkipUntilGtOrNotBitInTable:
                    {
                        int cpOffset = I16(code, pc + 2);
                        int advanceBy = I16(code, pc + 4);
                        uint character = U16(code, pc + 6);
                        int table = pc + 8;
                        int boundsCheckOffset = I32(code, pc + 24);
                        int onMatch = I32(code, pc + 28);
                        int onNoMatch = I32(code, pc + 32);
                        int next = onNoMatch;
                        while (IndexIsInBounds(current + boundsCheckOffset, subjectLength))
                        {
                            currentChar = subject[current + cpOffset];
                            if (currentChar > character)
                            {
                                next = onMatch;
                                break;
                            }
                            if (!CheckBitInTable(currentChar, code, table))
                            {
                                next = onMatch;
                                break;
                            }
                            current += advanceBy;
                        }
                        pc = next;
                        break;
                    }
                    case Bytecode.kSkipUntilCharOrChar:
                    {
                        int cpOffset = I16(code, pc + 2);
                        int advanceBy = I16(code, pc + 4);
                        uint char1 = U16(code, pc + 6);
                        uint char2 = U16(code, pc + 8);
                        int boundsCheckOffset = I32(code, pc + 12);
                        int onMatch = I32(code, pc + 16);
                        int onNoMatch = I32(code, pc + 20);
                        pc = onNoMatch;
                        while (IndexIsInBounds(current + boundsCheckOffset, subjectLength))
                        {
                            currentChar = subject[current + cpOffset];
                            // The two if-statements below are split up intentionally, as combining
                            // them seems to result in register allocation behaving quite
                            // differently and slowing down the resulting code.
                            if (char1 == currentChar)
                            {
                                pc = onMatch;
                                break;
                            }
                            if (char2 == currentChar)
                            {
                                pc = onMatch;
                                break;
                            }
                            current += advanceBy;
                        }
                        break;
                    }
                    default:
                        throw new InvalidOperationException("UNREACHABLE");
                }
            }
        }
        finally
        {
            backtrackStack.Release();
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }
}
