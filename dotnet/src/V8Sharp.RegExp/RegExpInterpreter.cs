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
using System.Runtime.InteropServices;

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

    static bool BackRefMatchesNoCase(int from, int current, int len, ReadOnlySpan<char> subject, bool unicode)
    {
        ReadOnlySpan<char> a = subject.Slice(from, len);
        ReadOnlySpan<char> b = subject.Slice(current, len);
        return unicode
            ? RegExpMacroAssembler.CaseInsensitiveCompareUnicode(a, b)
            : RegExpMacroAssembler.CaseInsensitiveCompareNonUnicode(a, b);
    }

    // Operand reads. The bytecode is generated by RegExpBytecodeGenerator and
    // read as V8 reads it (unaligned loads at fixed offsets, no bounds checks:
    // the generator guarantees every operand of a bytecode lies inside the
    // array). The reads are JIT intrinsics, so they cost nothing to inline in
    // a method as large as RawMatch, where RyuJIT's inlining budget runs out
    // (span-based reads were calls there).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short I16(ref byte c, int p) => Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref c, p));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ushort U16(ref byte c, int p) => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref c, p));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int I32(ref byte c, int p) => Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref c, p));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint U32(ref byte c, int p) => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref c, p));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CheckBitInTable(uint currentChar, ref byte code, int tableOffset)
    {
        int b = Unsafe.Add(ref code, tableOffset + (int)((currentChar & RegExpMacroAssembler.kTableMask) >> 3));
        return (b & (1 << (int)(currentChar & 7))) != 0;
    }

    /// <summary>The initial size of the backtrack stack.</summary>
    const int kInitialBacktrackStackSize = 256;
    /// <summary>The largest backtrack stack kept for the next match on this thread.</summary>
    const int kMaxCachedBacktrackStackSize = 64 * 1024;

    /// <summary>
    /// The backtrack stack of the last match on this thread (V8's RegExpStack,
    /// which the isolate keeps between matches); null while a match uses it.
    /// </summary>
    [ThreadStatic] static int[]? t_backtrackStack;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int[] NewBacktrackStack() => new int[kInitialBacktrackStackSize];

    /// <summary>The backtrack stack is full: an array of twice the size with its contents.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int[] GrowBacktrackStack(int[] stack)
    {
        int[] bigger = new int[stack.Length * 2];
        stack.CopyTo(bigger, 0);
        return bigger;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Exception Unreachable(string what = "UNREACHABLE") => new InvalidOperationException(what);

    // Deviation from V8 (structure only): RawMatch is one switch as in V8's
    // non-threaded build, with the backtrack stack in locals (a span over the
    // machine stack, then a rented array) instead of a struct with methods,
    // no try/finally (locals live into a handler are written to memory at
    // every assignment), and the two large peephole bytecodes and the
    // case-insensitive back references in separate methods, so that RyuJIT
    // keeps the dispatch state in registers.
    static int RawMatch<TWidth>(byte[] code, ReadOnlySpan<char> subject, Span<int> outputRegisters,
        int totalRegisterCount, int current, uint currentChar, uint backtrackLimit) where TWidth : struct, ICharWidth
    {
        int outputRegisterCount = outputRegisters.Length;
        if (outputRegisterCount < 2 || totalRegisterCount < outputRegisterCount ||
            totalRegisterCount > RegExpMacroAssembler.kMaxRegisterCount)
        {
            throw Unreachable("SBXCHECK failed: register counts");
        }

        Span<int> registers = totalRegisterCount <= 64
            ? stackalloc int[totalRegisterCount]
            : new int[totalRegisterCount];
        registers.Fill(kNoMatchValue);

        int[] stack = t_backtrackStack ?? NewBacktrackStack();
        t_backtrackStack = null;
        int sp = 0;

        ref byte c = ref MemoryMarshal.GetArrayDataReference(code);
        uint backtrackCount = 0;
        int subjectLength = subject.Length;
        int pc = 0;
        int result;

        while (true)
        {
            switch ((Bytecode)Unsafe.Add(ref c, pc))
            {
                case Bytecode.kPushCurrentPosition:
                    pc += 4;
                    if (sp == stack.Length)
                    {
                        if (sp >= kMaxBacktrackStackSize) { result = EXCEPTION; goto done; }
                        stack = GrowBacktrackStack(stack);
                    }
                    stack[sp++] = current;
                    break;
                case Bytecode.kPushBacktrack:
                {
                    int label = I32(ref c, pc + 4);
                    pc += 8;
                    if (sp == stack.Length)
                    {
                        if (sp >= kMaxBacktrackStackSize) { result = EXCEPTION; goto done; }
                        stack = GrowBacktrackStack(stack);
                    }
                    stack[sp++] = label;
                    break;
                }
                case Bytecode.kPushRegister:
                {
                    int value = registers[U16(ref c, pc + 2)];
                    pc += 8;
                    if (sp == stack.Length)
                    {
                        if (sp >= kMaxBacktrackStackSize) { result = EXCEPTION; goto done; }
                        stack = GrowBacktrackStack(stack);
                    }
                    stack[sp++] = value;
                    break;
                }
                case Bytecode.kSetRegister:
                    registers[U16(ref c, pc + 2)] = I32(ref c, pc + 4);
                    pc += 8;
                    break;
                case Bytecode.kClearRegisters:
                {
                    int from = U16(ref c, pc + 2);
                    int to = U16(ref c, pc + 4);
                    if (from > to) throw Unreachable("SBXCHECK failed");
                    registers.Slice(from, to - from + 1).Fill(kNoMatchValue);
                    pc += 8;
                    break;
                }
                case Bytecode.kAdvanceRegister:
                    registers[U16(ref c, pc + 2)] += I16(ref c, pc + 4);
                    pc += 8;
                    break;
                case Bytecode.kWriteCurrentPositionToRegister:
                    registers[U16(ref c, pc + 2)] = current + I16(ref c, pc + 4);
                    pc += 8;
                    break;
                case Bytecode.kReadCurrentPositionFromRegister:
                    current = registers[U16(ref c, pc + 2)];
                    pc += 4;
                    break;
                case Bytecode.kWriteStackPointerToRegister:
                    registers[U16(ref c, pc + 2)] = sp;
                    pc += 4;
                    break;
                case Bytecode.kReadStackPointerFromRegister:
                {
                    int newSp = registers[U16(ref c, pc + 2)];
                    if ((uint)newSp > (uint)sp) throw Unreachable("SBXCHECK failed: stack pointer");
                    sp = newSp;
                    pc += 4;
                    break;
                }
                case Bytecode.kPopCurrentPosition:
                    current = stack[--sp];
                    pc += 4;
                    break;
                case Bytecode.kBacktrack:
                    // JSRegExp::kNoBacktrackLimit == 0.
                    if (++backtrackCount == backtrackLimit)
                    {
                        result = I16(ref c, pc + 2);
                        goto done;
                    }
                    pc = stack[--sp];
                    break;
                case Bytecode.kPopRegister:
                    registers[U16(ref c, pc + 2)] = stack[--sp];
                    pc += 4;
                    break;
                case Bytecode.kFail:
                    result = FAILURE;
                    goto done;
                case Bytecode.kSucceed:
                    registers[..outputRegisterCount].CopyTo(outputRegisters);
                    result = SUCCESS;
                    goto done;
                case Bytecode.kAdvanceCurrentPosition:
                    current += I16(ref c, pc + 2);
                    pc += 4;
                    break;
                case Bytecode.kGoTo:
                    pc = I32(ref c, pc + 4);
                    break;
                case Bytecode.kAdvanceCpAndGoto:
                    current += I16(ref c, pc + 2);
                    pc = I32(ref c, pc + 4);
                    break;
                case Bytecode.kCheckFixedLengthLoop:
                    if (current == stack[sp - 1])
                    {
                        pc = I32(ref c, pc + 4);
                        sp--;
                    }
                    else
                    {
                        pc += 8;
                    }
                    break;
                case Bytecode.kLoadCurrentCharacter:
                    if ((uint)(current + I32(ref c, pc + 4)) >= (uint)subjectLength)
                    {
                        pc = I32(ref c, pc + 8);
                    }
                    else
                    {
                        currentChar = subject[current + I16(ref c, pc + 2)];
                        pc += 12;
                    }
                    break;
                case Bytecode.kLoadCurrentCharacterUnchecked:
                    currentChar = subject[current + I16(ref c, pc + 2)];
                    pc += 4;
                    break;
                case Bytecode.kLoad2CurrentChars:
                    if ((uint)(current + I32(ref c, pc + 4)) >= (uint)subjectLength)
                    {
                        pc = I32(ref c, pc + 8);
                    }
                    else
                    {
                        currentChar = Load2Characters<TWidth>(subject, current + I16(ref c, pc + 2));
                        pc += 12;
                    }
                    break;
                case Bytecode.kLoad2CurrentCharsUnchecked:
                    currentChar = Load2Characters<TWidth>(subject, current + I16(ref c, pc + 2));
                    pc += 4;
                    break;
                case Bytecode.kLoad4CurrentChars:
                    Debug.Assert(TWidth.IsOneByte);
                    if ((uint)(current + I32(ref c, pc + 4)) >= (uint)subjectLength)
                    {
                        pc = I32(ref c, pc + 8);
                    }
                    else
                    {
                        currentChar = Load4Characters(subject, current + I16(ref c, pc + 2));
                        pc += 12;
                    }
                    break;
                case Bytecode.kLoad4CurrentCharsUnchecked:
                    Debug.Assert(TWidth.IsOneByte);
                    currentChar = Load4Characters(subject, current + I16(ref c, pc + 2));
                    pc += 4;
                    break;
                case Bytecode.kSkipUntilOneOfMasked:
                    pc = SkipUntilOneOfMasked(ref c, pc, subject, ref current, ref currentChar);
                    break;
                case Bytecode.kSkipUntilOneOfMasked3:
                    pc = SkipUntilOneOfMasked3(ref c, pc, subject, ref current, ref currentChar);
                    break;
                case Bytecode.kCheck4Chars:
                    pc = U32(ref c, pc + 4) == currentChar ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kCheckCharacter:
                    pc = U16(ref c, pc + 2) == currentChar ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kCheckNot4Chars:
                    pc = U32(ref c, pc + 4) != currentChar ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kCheckNotCharacter:
                    pc = U16(ref c, pc + 2) != currentChar ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kAndCheck4Chars:
                    pc = U32(ref c, pc + 4) == (currentChar & U32(ref c, pc + 8)) ? I32(ref c, pc + 12) : pc + 16;
                    break;
                case Bytecode.kCheckCharacterAfterAnd:
                    pc = U16(ref c, pc + 2) == (currentChar & U32(ref c, pc + 4)) ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kAndCheckNot4Chars:
                    pc = U32(ref c, pc + 4) != (currentChar & U32(ref c, pc + 8)) ? I32(ref c, pc + 12) : pc + 16;
                    break;
                case Bytecode.kCheckNotCharacterAfterAnd:
                    pc = U16(ref c, pc + 2) != (currentChar & U32(ref c, pc + 4)) ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kCheckNotCharacterAfterMinusAnd:
                {
                    uint character = U16(ref c, pc + 2);
                    uint minus = U16(ref c, pc + 4);
                    uint mask = U16(ref c, pc + 6);
                    pc = character != ((currentChar - minus) & mask) ? I32(ref c, pc + 8) : pc + 12;
                    break;
                }
                case Bytecode.kCheckCharacterInRange:
                {
                    uint from = U16(ref c, pc + 2);
                    uint to = U16(ref c, pc + 4);
                    pc = from <= currentChar && currentChar <= to ? I32(ref c, pc + 8) : pc + 12;
                    break;
                }
                case Bytecode.kCheckCharacterNotInRange:
                {
                    uint from = U16(ref c, pc + 2);
                    uint to = U16(ref c, pc + 4);
                    pc = from > currentChar || currentChar > to ? I32(ref c, pc + 8) : pc + 12;
                    break;
                }
                case Bytecode.kCheckBitInTable:
                    pc = CheckBitInTable(currentChar, ref c, pc + 8) ? I32(ref c, pc + 4) : pc + 24;
                    break;
                case Bytecode.kCheckCharacterLT:
                    pc = currentChar < U16(ref c, pc + 2) ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kCheckCharacterGT:
                    pc = currentChar > U16(ref c, pc + 2) ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kIfRegisterLT:
                    pc = registers[U16(ref c, pc + 2)] < I32(ref c, pc + 4) ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kIfRegisterGE:
                    pc = registers[U16(ref c, pc + 2)] >= I32(ref c, pc + 4) ? I32(ref c, pc + 8) : pc + 12;
                    break;
                case Bytecode.kIfRegisterEqPos:
                    pc = registers[U16(ref c, pc + 2)] == current ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kCheckNotBackRef:
                {
                    int startReg = U16(ref c, pc + 2);
                    int from = registers[startReg];
                    int len = registers[startReg + 1] - from;
                    if (from >= 0 && len > 0)
                    {
                        if (current + len > subjectLength ||
                            !subject.Slice(from, len).SequenceEqual(subject.Slice(current, len)))
                        {
                            pc = I32(ref c, pc + 4);
                            break;
                        }
                        current += len;
                    }
                    pc += 8;
                    break;
                }
                case Bytecode.kCheckNotBackRefBackward:
                {
                    int startReg = U16(ref c, pc + 2);
                    int from = registers[startReg];
                    int len = registers[startReg + 1] - from;
                    if (from >= 0 && len > 0)
                    {
                        if (current - len < 0 ||
                            !subject.Slice(from, len).SequenceEqual(subject.Slice(current - len, len)))
                        {
                            pc = I32(ref c, pc + 4);
                            break;
                        }
                        current -= len;
                    }
                    pc += 8;
                    break;
                }
                case Bytecode.kCheckNotBackRefNoCaseUnicode:
                case Bytecode.kCheckNotBackRefNoCase:
                case Bytecode.kCheckNotBackRefNoCaseUnicodeBackward:
                case Bytecode.kCheckNotBackRefNoCaseBackward:
                    pc = CheckNotBackRefNoCase(ref c, pc, subject, registers, ref current);
                    break;
                case Bytecode.kCheckAtStart:
                    pc = current + I16(ref c, pc + 2) == 0 ? I32(ref c, pc + 4) : pc + 8;
                    break;
                case Bytecode.kCheckNotAtStart:
                    pc = current + I16(ref c, pc + 2) == 0 ? pc + 8 : I32(ref c, pc + 4);
                    break;
                case Bytecode.kSetCurrentPositionFromEnd:
                {
                    int by = I16(ref c, pc + 2);
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
                    int pos = current + I16(ref c, pc + 2);
                    pc = pos >= subjectLength || pos < 0 ? I32(ref c, pc + 4) : pc + 8;
                    break;
                }
                case Bytecode.kCheckSpecialClassRanges:
                    pc = CheckSpecialClassRanges<TWidth>(currentChar, (StandardCharacterSet)Unsafe.Add(ref c, pc + 1))
                        ? pc + 8
                        : I32(ref c, pc + 4);
                    break;
                case Bytecode.kSkipUntilChar:
                {
                    int cpOffset = I16(ref c, pc + 2);
                    int advanceBy = I16(ref c, pc + 4);
                    uint character = U16(ref c, pc + 6);
                    int boundsCheckOffset = I32(ref c, pc + 8);
                    int onMatch = I32(ref c, pc + 12);
                    pc = I32(ref c, pc + 16);
                    while ((uint)(current + boundsCheckOffset) < (uint)subjectLength)
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
                    int cpOffset = I16(ref c, pc + 2);
                    int advanceBy = I16(ref c, pc + 4);
                    uint character = U16(ref c, pc + 6);
                    uint mask = U32(ref c, pc + 8);
                    int boundsCheckOffset = I32(ref c, pc + 12);
                    int onMatch = I32(ref c, pc + 16);
                    pc = I32(ref c, pc + 20);
                    while ((uint)(current + boundsCheckOffset) < (uint)subjectLength)
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
                    int cpOffset = I16(ref c, pc + 2);
                    int advanceBy = I16(ref c, pc + 4);
                    int table = pc + 8;
                    int boundsCheckOffset = I32(ref c, pc + 24);
                    int onMatch = I32(ref c, pc + 28);
                    int next = I32(ref c, pc + 32);
                    while ((uint)(current + boundsCheckOffset) < (uint)subjectLength)
                    {
                        currentChar = subject[current + cpOffset];
                        if (CheckBitInTable(currentChar, ref c, table))
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
                    int cpOffset = I16(ref c, pc + 2);
                    int advanceBy = I16(ref c, pc + 4);
                    uint character = U16(ref c, pc + 6);
                    int table = pc + 8;
                    int boundsCheckOffset = I32(ref c, pc + 24);
                    int onMatch = I32(ref c, pc + 28);
                    int next = I32(ref c, pc + 32);
                    while ((uint)(current + boundsCheckOffset) < (uint)subjectLength)
                    {
                        currentChar = subject[current + cpOffset];
                        if (currentChar > character)
                        {
                            next = onMatch;
                            break;
                        }
                        if (!CheckBitInTable(currentChar, ref c, table))
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
                    int cpOffset = I16(ref c, pc + 2);
                    int advanceBy = I16(ref c, pc + 4);
                    uint char1 = U16(ref c, pc + 6);
                    uint char2 = U16(ref c, pc + 8);
                    int boundsCheckOffset = I32(ref c, pc + 12);
                    int onMatch = I32(ref c, pc + 16);
                    pc = I32(ref c, pc + 20);
                    while ((uint)(current + boundsCheckOffset) < (uint)subjectLength)
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
                    throw Unreachable();
            }
        }

    done:
        if (stack.Length <= kMaxCachedBacktrackStackSize) t_backtrackStack = stack;
        return result;
    }

    /// <summary>
    /// kCheckNotBackRefNoCase(Unicode)(Backward): the next pc; on a match
    /// <paramref name="current"/> moves past the back reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int CheckNotBackRefNoCase(ref byte c, int pc, ReadOnlySpan<char> subject, Span<int> registers, ref int current)
    {
        var bc = (Bytecode)Unsafe.Add(ref c, pc);
        bool unicode = bc is Bytecode.kCheckNotBackRefNoCaseUnicode or Bytecode.kCheckNotBackRefNoCaseUnicodeBackward;
        bool backward = bc is Bytecode.kCheckNotBackRefNoCaseBackward or Bytecode.kCheckNotBackRefNoCaseUnicodeBackward;
        int startReg = U16(ref c, pc + 2);
        int from = registers[startReg];
        int len = registers[startReg + 1] - from;
        if (from >= 0 && len > 0)
        {
            if (backward)
            {
                if (current - len < 0 || !BackRefMatchesNoCase(from, current - len, len, subject, unicode)) return I32(ref c, pc + 4);
                current -= len;
            }
            else
            {
                if (current + len > subject.Length || !BackRefMatchesNoCase(from, current, len, subject, unicode)) return I32(ref c, pc + 4);
                current += len;
            }
        }
        return pc + 8;
    }

    /// <summary>kSkipUntilOneOfMasked: the next pc (one-byte subjects only).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SkipUntilOneOfMasked(ref byte c, int pc, ReadOnlySpan<char> subject, ref int current, ref uint currentChar)
    {
        int[] o = Bytecodes.Info(Bytecode.kSkipUntilOneOfMasked).OperandOffsets;
        // Operands: cp_offset, advance_by, both_chars, both_mask, max_offset,
        // chars1, mask1, chars2, mask2, on_match1, on_match2, on_failure.
        int cpOffset = I16(ref c, pc + o[0]);
        int advanceBy = I16(ref c, pc + o[1]);
        uint bothChars = U32(ref c, pc + o[2]);
        uint bothMask = U32(ref c, pc + o[3]);
        int maxOffset = I32(ref c, pc + o[4]);
        uint chars1 = U32(ref c, pc + o[5]);
        uint mask1 = U32(ref c, pc + o[6]);
        uint chars2 = U32(ref c, pc + o[7]);
        uint mask2 = U32(ref c, pc + o[8]);
        int subjectLength = subject.Length;
        int pos = current;
        uint ch = currentChar;
        int next = I32(ref c, pc + o[11]);
        while ((uint)(pos + maxOffset) < (uint)subjectLength)
        {
            ch = Load4Characters(subject, pos + cpOffset);
            if (bothChars == (ch & bothMask))
            {
                if (chars1 == (ch & mask1))
                {
                    next = I32(ref c, pc + o[9]);
                    break;
                }
                if (chars2 == (ch & mask2))
                {
                    next = I32(ref c, pc + o[10]);
                    break;
                }
            }
            pos += advanceBy;
        }
        current = pos;
        currentChar = ch;
        return next;
    }

    /// <summary>kSkipUntilOneOfMasked3: the next pc (one-byte subjects only).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SkipUntilOneOfMasked3(ref byte c, int pc, ReadOnlySpan<char> subject, ref int current, ref uint currentChar)
    {
        int[] o = Bytecodes.Info(Bytecode.kSkipUntilOneOfMasked3).OperandOffsets;
        // Operands: 0 bc0_cp_offset, 1 bc0_advance_by, 2 bc0_table,
        // 3 bc1_bounds_check_offset, 4 bc1_on_failure, 5 bc1_cp_offset,
        // 6 bc2_characters, 7 bc2_mask, 8 bc3_by, 9 bc4_bounds_check_offset,
        // 10 bc4_cp_offset, 11 bc5_characters, 12 bc5_mask, 13 bc5_on_equal,
        // 14 bc6_characters, 15 bc6_mask, 16 bc6_on_equal, 17 bc7_characters,
        // 18 bc7_mask, 19 fallthrough_jump_target.
        int bc0CpOffset = I16(ref c, pc + o[0]);
        int bc0AdvanceBy = I16(ref c, pc + o[1]);
        int bc0Table = pc + o[2];
        int bc1BoundsCheckOffset = I32(ref c, pc + o[3]);
        int bc1CpOffset = I16(ref c, pc + o[5]);
        uint bc2Characters = U32(ref c, pc + o[6]);
        uint bc2Mask = U32(ref c, pc + o[7]);
        int bc3By = I16(ref c, pc + o[8]);
        int bc4BoundsCheckOffset = I32(ref c, pc + o[9]);
        int bc4CpOffset = I16(ref c, pc + o[10]);
        uint bc5Characters = U32(ref c, pc + o[11]);
        uint bc5Mask = U32(ref c, pc + o[12]);
        uint bc6Characters = U32(ref c, pc + o[14]);
        uint bc6Mask = U32(ref c, pc + o[15]);
        uint bc7Characters = U32(ref c, pc + o[17]);
        uint bc7Mask = U32(ref c, pc + o[18]);
        int subjectLength = subject.Length;
        int pos = current;
        uint ch = currentChar;
        int next;
        while (true)
        {
            // bc0: kSkipUntilBitInTable
            while ((uint)(pos + bc0CpOffset) < (uint)subjectLength)
            {
                ch = subject[pos + bc0CpOffset];
                if (CheckBitInTable(ch, ref c, bc0Table)) break;
                pos += bc0AdvanceBy;
            }

            // bc1: kLoad4CurrentChars
            if ((uint)(pos + bc1BoundsCheckOffset) >= (uint)subjectLength)
            {
                next = I32(ref c, pc + o[4]);
                break;
            }

            ch = Load4Characters(subject, pos + bc1CpOffset);

            // bc2: AndCheck4Chars
            if (bc2Characters == (ch & bc2Mask))
            {
                // bc4: Load4CurrentChars
                if ((uint)(pos + bc4BoundsCheckOffset) >= (uint)subjectLength)
                {
                    // bc3: AdvanceCpAndGoto
                    pos += bc3By;
                    continue;
                }
                ch = Load4Characters(subject, pos + bc4CpOffset);

                // bc5: AndCheck4Chars
                if (bc5Characters == (ch & bc5Mask))
                {
                    next = I32(ref c, pc + o[13]);
                    break;
                }
                // bc6: AndCheck4Chars
                if (bc6Characters == (ch & bc6Mask))
                {
                    next = I32(ref c, pc + o[16]);
                    break;
                }
                // bc7: AndCheckNot4Chars
                if (bc7Characters == (ch & bc7Mask))
                {
                    next = I32(ref c, pc + o[19]);
                    break;
                }
            }

            // bc3: AdvanceCpAndGoto
            pos += bc3By;
        }
        current = pos;
        currentChar = ch;
        return next;
    }
}
