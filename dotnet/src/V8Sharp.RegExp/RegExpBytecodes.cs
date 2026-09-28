// Port of src/regexp/regexp-bytecodes.h, src/regexp/regexp-bytecodes-inl.h and
// src/regexp/regexp-bytecodes.cc.
//
// V8 derives the bytecode layout at C++ compile time from REGEXP_BYTECODE_LIST
// (operands packed and aligned to their own size within 4-byte blocks). The
// C# port keeps the same list as a table and computes the same layout once at
// start-up, so the byte encoding is identical to V8's.

using System.Buffers.Binary;
using System.Text;

namespace V8Sharp.RegExp;

public enum BytecodeOperandType : byte
{
    // Basic operand types that have a direct mapping to a C-type.
    kInt16,
    kInt32,
    kUint32,
    kChar,
    kJumpTarget,
    // Basic operand types with limits.
    kOffset,             // int16_t [kMinCPOffset, kMaxCPOffset]
    kBoundsCheckOffset,  // int32_t
    kRegister,           // uint16_t [0, kMaxRegister]
    kStackCheckFlag,     // StackCheckFlag (uint8_t)
    kStandardCharacterSet,  // StandardCharacterSet (char)
    // Special operand types.
    kBitTable,           // 16 bytes, alignment 1
}

[Flags]
public enum BytecodeFlags : uint
{
    None = 0,
    // This bytecode doesn't fall through, i.e. it either terminates or jumps
    // unconditionally.
    kNoFallthrough = 1 << 0,
    // This bytecode has a kJumpTarget operand but doesn't branch.
    kNoBranchDespiteJumpTargetOperand = 1 << 1,
    // This bytecode loads from the subject string into the current_character
    // register.
    kLoadsCC = 1 << 2,
    // This bytecode uses the current_character register's value.
    kUsesCC = 1 << 3,
}

/// <summary>The irregexp bytecodes, in V8's numbering (Break must be 0).</summary>
public enum Bytecode : byte
{
    // INVALID_BYTECODE_LIST
    kBreak,
    // BASIC_BYTECODE_LIST
    kPushCurrentPosition,
    kPushBacktrack,
    kWriteCurrentPositionToRegister,
    kReadCurrentPositionFromRegister,
    kWriteStackPointerToRegister,
    kReadStackPointerFromRegister,
    kSetRegister,
    kClearRegisters,
    kAdvanceRegister,
    kPopCurrentPosition,
    kPushRegister,
    kPopRegister,
    kFail,
    kSucceed,
    kAdvanceCurrentPosition,
    kGoTo,
    kLoadCurrentCharacter,
    kCheckPosition,
    kCheckSpecialClassRanges,
    kCheckCharacter,
    kCheckNotCharacter,
    kCheckCharacterAfterAnd,
    kCheckNotCharacterAfterAnd,
    kCheckNotCharacterAfterMinusAnd,
    kCheckCharacterInRange,
    kCheckCharacterNotInRange,
    kCheckCharacterLT,
    kCheckCharacterGT,
    kIfRegisterLT,
    kIfRegisterGE,
    kIfRegisterEqPos,
    kCheckAtStart,
    kCheckNotAtStart,
    kCheckFixedLengthLoop,
    kSetCurrentPositionFromEnd,
    // SPECIAL_BYTECODE_LIST
    kBacktrack,
    kLoadCurrentCharacterUnchecked,
    kCheckBitInTable,
    kLoad2CurrentChars,
    kLoad2CurrentCharsUnchecked,
    kLoad4CurrentChars,
    kLoad4CurrentCharsUnchecked,
    kCheck4Chars,
    kCheckNot4Chars,
    kAndCheck4Chars,
    kAndCheckNot4Chars,
    kAdvanceCpAndGoto,
    kCheckNotBackRef,
    kCheckNotBackRefNoCase,
    kCheckNotBackRefNoCaseUnicode,
    kCheckNotBackRefBackward,
    kCheckNotBackRefNoCaseBackward,
    kCheckNotBackRefNoCaseUnicodeBackward,
    // PEEPHOLE_BYTECODE_LIST
    kSkipUntilBitInTable,
    kSkipUntilCharAnd,
    kSkipUntilChar,
    kSkipUntilCharOrChar,
    kSkipUntilGtOrNotBitInTable,
    kSkipUntilOneOfMasked,
    kSkipUntilOneOfMasked3,
}

/// <summary>Layout of one bytecode: its operands' names, types and offsets.</summary>
public sealed class BytecodeInfo
{
    public required Bytecode Bytecode { get; init; }
    public required string Name { get; init; }
    public required string[] OperandNames { get; init; }
    public required BytecodeOperandType[] OperandTypes { get; init; }
    public required int[] OperandOffsets { get; init; }
    public required int Size { get; init; }
    public required BytecodeFlags Flags { get; init; }

    /// <summary>The byte offset of the named operand within the bytecode.</summary>
    public int Offset(string operand)
    {
        int i = Array.IndexOf(OperandNames, operand);
        if (i < 0) throw new ArgumentException($"{Name} has no operand {operand}");
        return OperandOffsets[i];
    }

    public BytecodeOperandType Type(string operand) => OperandTypes[Array.IndexOf(OperandNames, operand)];
}

public static class Bytecodes
{
    // Bytecode is 4-byte aligned.
    // We can pack operands if multiple operands fit into 4 bytes.
    public const int kBytecodeAlignment = 4;

    static readonly BytecodeInfo[] s_infos = BuildInfos();

    public static int kCount => s_infos.Length;

    public static BytecodeInfo Info(Bytecode bc) => s_infos[(int)bc];

    public static byte ToByte(Bytecode bc) => (byte)bc;
    public static Bytecode FromByte(byte b)
    {
        Debug.Assert(IsValid(b));
        return (Bytecode)b;
    }
    public static bool IsValid(byte b) => b < s_infos.Length;
    public static bool IsValidJumpTarget(byte b) => IsValid(b) && FromByte(b) != Bytecode.kBreak;

    public static string Name(Bytecode bc) => s_infos[(int)bc].Name;
    public static int Size(Bytecode bc) => s_infos[(int)bc].Size;
    public static int Size(byte bc) => s_infos[bc].Size;
    public static BytecodeFlags Flags(Bytecode bc) => s_infos[(int)bc].Flags;

    public static int Size(BytecodeOperandType type) => type switch
    {
        BytecodeOperandType.kInt16 => 2,
        BytecodeOperandType.kInt32 => 4,
        BytecodeOperandType.kUint32 => 4,
        BytecodeOperandType.kChar => 2,
        BytecodeOperandType.kJumpTarget => 4,
        BytecodeOperandType.kOffset => 2,
        BytecodeOperandType.kBoundsCheckOffset => 4,
        BytecodeOperandType.kRegister => 2,
        BytecodeOperandType.kStackCheckFlag => 1,
        BytecodeOperandType.kStandardCharacterSet => 1,
        BytecodeOperandType.kBitTable => 16,
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    static int Alignment(BytecodeOperandType type) => type == BytecodeOperandType.kBitTable ? 1 : Size(type);

    static int RoundUp(int x, int m) => (x + m - 1) / m * m;

    static BytecodeInfo[] BuildInfos()
    {
        const BytecodeOperandType Int16 = BytecodeOperandType.kInt16;
        const BytecodeOperandType Uint32 = BytecodeOperandType.kUint32;
        const BytecodeOperandType Int32 = BytecodeOperandType.kInt32;
        const BytecodeOperandType Char = BytecodeOperandType.kChar;
        const BytecodeOperandType JumpTarget = BytecodeOperandType.kJumpTarget;
        const BytecodeOperandType Offset = BytecodeOperandType.kOffset;
        const BytecodeOperandType BoundsCheckOffset = BytecodeOperandType.kBoundsCheckOffset;
        const BytecodeOperandType Register = BytecodeOperandType.kRegister;
        const BytecodeOperandType StackCheckFlag = BytecodeOperandType.kStackCheckFlag;
        const BytecodeOperandType StandardCharacterSet = BytecodeOperandType.kStandardCharacterSet;
        const BytecodeOperandType BitTable = BytecodeOperandType.kBitTable;
        const BytecodeFlags NoFallthrough = BytecodeFlags.kNoFallthrough;
        const BytecodeFlags NoBranch = BytecodeFlags.kNoBranchDespiteJumpTargetOperand;
        const BytecodeFlags LoadsCC = BytecodeFlags.kLoadsCC;
        const BytecodeFlags UsesCC = BytecodeFlags.kUsesCC;
        const BytecodeFlags None = BytecodeFlags.None;

        var list = new List<(Bytecode, string[], BytecodeOperandType[], BytecodeFlags)>
        {
            (Bytecode.kBreak, [], [], None),
            (Bytecode.kPushCurrentPosition, [], [], None),
            (Bytecode.kPushBacktrack, ["label"], [JumpTarget], NoBranch),
            (Bytecode.kWriteCurrentPositionToRegister, ["register_index", "cp_offset"], [Register, Offset], None),
            (Bytecode.kReadCurrentPositionFromRegister, ["register_index"], [Register], None),
            (Bytecode.kWriteStackPointerToRegister, ["register_index"], [Register], None),
            (Bytecode.kReadStackPointerFromRegister, ["register_index"], [Register], None),
            (Bytecode.kSetRegister, ["register_index", "value"], [Register, Int32], None),
            (Bytecode.kClearRegisters, ["from_register", "to_register"], [Register, Register], None),
            (Bytecode.kAdvanceRegister, ["register_index", "by"], [Register, Offset], None),
            (Bytecode.kPopCurrentPosition, [], [], None),
            (Bytecode.kPushRegister, ["register_index", "stack_check"], [Register, StackCheckFlag], None),
            (Bytecode.kPopRegister, ["register_index"], [Register], None),
            (Bytecode.kFail, [], [], NoFallthrough),
            (Bytecode.kSucceed, [], [], NoFallthrough),
            (Bytecode.kAdvanceCurrentPosition, ["by"], [Offset], None),
            (Bytecode.kGoTo, ["label"], [JumpTarget], NoFallthrough),
            (Bytecode.kLoadCurrentCharacter, ["cp_offset", "bounds_check_offset", "on_failure"],
                [Offset, BoundsCheckOffset, JumpTarget], LoadsCC),
            (Bytecode.kCheckPosition, ["cp_offset", "on_failure"], [Offset, JumpTarget], None),
            (Bytecode.kCheckSpecialClassRanges, ["character_set", "on_no_match"], [StandardCharacterSet, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacter, ["character", "on_equal"], [Char, JumpTarget], UsesCC),
            (Bytecode.kCheckNotCharacter, ["character", "on_not_equal"], [Char, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacterAfterAnd, ["character", "mask", "on_equal"], [Char, Uint32, JumpTarget], UsesCC),
            (Bytecode.kCheckNotCharacterAfterAnd, ["character", "mask", "on_not_equal"], [Char, Uint32, JumpTarget], UsesCC),
            (Bytecode.kCheckNotCharacterAfterMinusAnd, ["character", "minus", "mask", "on_not_equal"],
                [Char, Char, Char, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacterInRange, ["from", "to", "on_in_range"], [Char, Char, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacterNotInRange, ["from", "to", "on_not_in_range"], [Char, Char, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacterLT, ["limit", "on_less"], [Char, JumpTarget], UsesCC),
            (Bytecode.kCheckCharacterGT, ["limit", "on_greater"], [Char, JumpTarget], UsesCC),
            (Bytecode.kIfRegisterLT, ["register_index", "comparand", "on_less_than"], [Register, Int32, JumpTarget], None),
            (Bytecode.kIfRegisterGE, ["register_index", "comparand", "on_greater_or_equal"], [Register, Int32, JumpTarget], None),
            (Bytecode.kIfRegisterEqPos, ["register_index", "on_eq"], [Register, JumpTarget], None),
            (Bytecode.kCheckAtStart, ["cp_offset", "on_at_start"], [Offset, JumpTarget], None),
            (Bytecode.kCheckNotAtStart, ["cp_offset", "on_not_at_start"], [Offset, JumpTarget], None),
            (Bytecode.kCheckFixedLengthLoop, ["on_tos_equals_current_position"], [JumpTarget], None),
            (Bytecode.kSetCurrentPositionFromEnd, ["by"], [Offset], None),
            (Bytecode.kBacktrack, ["return_code"], [Int16], NoFallthrough),
            (Bytecode.kLoadCurrentCharacterUnchecked, ["cp_offset"], [Offset], LoadsCC),
            (Bytecode.kCheckBitInTable, ["on_bit_set", "table"], [JumpTarget, BitTable], UsesCC),
            (Bytecode.kLoad2CurrentChars, ["cp_offset", "bounds_check_offset", "on_failure"],
                [Offset, BoundsCheckOffset, JumpTarget], LoadsCC),
            (Bytecode.kLoad2CurrentCharsUnchecked, ["cp_offset"], [Offset], LoadsCC),
            (Bytecode.kLoad4CurrentChars, ["cp_offset", "bounds_check_offset", "on_failure"],
                [Offset, BoundsCheckOffset, JumpTarget], LoadsCC),
            (Bytecode.kLoad4CurrentCharsUnchecked, ["cp_offset"], [Offset], LoadsCC),
            (Bytecode.kCheck4Chars, ["characters", "on_equal"], [Uint32, JumpTarget], UsesCC),
            (Bytecode.kCheckNot4Chars, ["characters", "on_not_equal"], [Uint32, JumpTarget], UsesCC),
            (Bytecode.kAndCheck4Chars, ["characters", "mask", "on_equal"], [Uint32, Uint32, JumpTarget], UsesCC),
            (Bytecode.kAndCheckNot4Chars, ["characters", "mask", "on_not_equal"], [Uint32, Uint32, JumpTarget], UsesCC),
            (Bytecode.kAdvanceCpAndGoto, ["by", "on_goto"], [Offset, JumpTarget], NoFallthrough),
            (Bytecode.kCheckNotBackRef, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kCheckNotBackRefNoCase, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kCheckNotBackRefNoCaseUnicode, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kCheckNotBackRefBackward, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kCheckNotBackRefNoCaseBackward, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kCheckNotBackRefNoCaseUnicodeBackward, ["start_reg", "on_not_equal"], [Register, JumpTarget], None),
            (Bytecode.kSkipUntilBitInTable,
                ["cp_offset", "advance_by", "table", "bounds_check_offset", "on_match", "on_no_match"],
                [Offset, Offset, BitTable, BoundsCheckOffset, JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilCharAnd,
                ["cp_offset", "advance_by", "character", "mask", "bounds_check_offset", "on_match", "on_no_match"],
                [Offset, Offset, Char, Uint32, BoundsCheckOffset, JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilChar,
                ["cp_offset", "advance_by", "character", "bounds_check_offset", "on_match", "on_no_match"],
                [Offset, Offset, Char, BoundsCheckOffset, JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilCharOrChar,
                ["cp_offset", "advance_by", "char1", "char2", "bounds_check_offset", "on_match", "on_no_match"],
                [Offset, Offset, Char, Char, BoundsCheckOffset, JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilGtOrNotBitInTable,
                ["cp_offset", "advance_by", "character", "table", "bounds_check_offset", "on_match", "on_no_match"],
                [Offset, Offset, Char, BitTable, BoundsCheckOffset, JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilOneOfMasked,
                ["cp_offset", "advance_by", "both_chars", "both_mask", "max_offset", "chars1", "mask1", "chars2",
                 "mask2", "on_match1", "on_match2", "on_failure"],
                [Offset, Offset, Uint32, Uint32, BoundsCheckOffset, Uint32, Uint32, Uint32, Uint32, JumpTarget,
                 JumpTarget, JumpTarget], LoadsCC),
            (Bytecode.kSkipUntilOneOfMasked3,
                ["bc0_cp_offset", "bc0_advance_by", "bc0_table", "bc1_bounds_check_offset", "bc1_on_failure",
                 "bc1_cp_offset", "bc2_characters", "bc2_mask", "bc3_by", "bc4_bounds_check_offset", "bc4_cp_offset",
                 "bc5_characters", "bc5_mask", "bc5_on_equal", "bc6_characters", "bc6_mask", "bc6_on_equal",
                 "bc7_characters", "bc7_mask", "fallthrough_jump_target"],
                [Offset, Offset, BitTable, BoundsCheckOffset, JumpTarget, Offset, Uint32, Uint32, Offset,
                 BoundsCheckOffset, Offset, Uint32, Uint32, JumpTarget, Uint32, Uint32, JumpTarget, Uint32, Uint32,
                 JumpTarget], LoadsCC),
        };

        var result = new BytecodeInfo[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var (bc, names, types, flags) = list[i];
            if ((int)bc != i) throw new InvalidOperationException("bytecode list out of order");
            // CalculateAlignedOffsets: all operands are aligned to their own size;
            // an operand that doesn't fit into the current 4-byte block starts a
            // new one.
            int[] offsets = new int[types.Length];
            int offset = 1;  // sizeof(Bytecode)
            for (int j = 0; j < types.Length; j++)
            {
                int operandSize = Size(types[j]);
                int operandAlignment = Alignment(types[j]);
                offset = RoundUp(offset, operandAlignment);
                if ((offset % kBytecodeAlignment) + operandSize > kBytecodeAlignment)
                {
                    offset = RoundUp(offset, kBytecodeAlignment);
                }
                offsets[j] = offset;
                offset += operandSize;
            }
            int size = RoundUp(types.Length == 0 ? 1 : offsets[^1] + Size(types[^1]), kBytecodeAlignment);
            result[i] = new BytecodeInfo
            {
                Bytecode = bc,
                Name = bc.ToString()[1..],
                OperandNames = names,
                OperandTypes = types,
                OperandOffsets = offsets,
                Size = size,
                Flags = flags,
            };
        }
        return result;
    }

    /// <summary>Reads a basic operand as a sign- or zero-extended int.</summary>
    public static int ReadOperand(ReadOnlySpan<byte> code, int pos, BytecodeOperandType type) => type switch
    {
        BytecodeOperandType.kInt16 or BytecodeOperandType.kOffset => BinaryPrimitives.ReadInt16LittleEndian(code[pos..]),
        BytecodeOperandType.kInt32 or BytecodeOperandType.kBoundsCheckOffset or BytecodeOperandType.kUint32
            or BytecodeOperandType.kJumpTarget => BinaryPrimitives.ReadInt32LittleEndian(code[pos..]),
        BytecodeOperandType.kChar or BytecodeOperandType.kRegister => BinaryPrimitives.ReadUInt16LittleEndian(code[pos..]),
        BytecodeOperandType.kStackCheckFlag or BytecodeOperandType.kStandardCharacterSet => code[pos],
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    static string StandardCharacterSetName(StandardCharacterSet val) => val switch
    {
        StandardCharacterSet.kWhitespace => "Whitespace",
        StandardCharacterSet.kNotWhitespace => "NotWhitespace",
        StandardCharacterSet.kDigit => "Digit",
        StandardCharacterSet.kNotDigit => "NotDigit",
        StandardCharacterSet.kLineTerminator => "LineTerminator",
        StandardCharacterSet.kNotLineTerminator => "NotLineTerminator",
        StandardCharacterSet.kWord => "Word",
        StandardCharacterSet.kNotWord => "NotWord",
        StandardCharacterSet.kEverything => "Everything",
        _ => throw new InvalidOperationException("UNREACHABLE"),
    };

    /// <summary>RegExpBytecodeDisassembleSingle.</summary>
    public static string DisassembleSingle(ReadOnlySpan<byte> code, int pc)
    {
        var info = Info((Bytecode)code[pc]);
        var sb = new StringBuilder(info.Name);
        for (int i = 0; i < info.OperandTypes.Length; i++)
        {
            BytecodeOperandType type = info.OperandTypes[i];
            int pos = pc + info.OperandOffsets[i];
            sb.Append(", ").Append(info.OperandNames[i]).Append(": ");
            switch (type)
            {
                case BytecodeOperandType.kBitTable:
                    for (int j = 0; j < Size(type); j++) sb.Append(code[pos + j].ToString("x2"));
                    break;
                case BytecodeOperandType.kChar:
                {
                    int c = ReadOperand(code, pos, type);
                    sb.Append(AsUC32(c));
                    break;
                }
                case BytecodeOperandType.kStackCheckFlag:
                    sb.Append(code[pos] == 0 ? "NoCheck" : "Check");
                    break;
                case BytecodeOperandType.kStandardCharacterSet:
                    sb.Append(StandardCharacterSetName((StandardCharacterSet)code[pos]));
                    break;
                default:
                {
                    int v = ReadOperand(code, pos, type);
                    sb.Append("0x").Append(((uint)v).ToString(Size(type) == 2 ? "x2" : "x2"));
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>RegExpBytecodeDisassemble.</summary>
    public static string Disassemble(ReadOnlySpan<byte> code, string pattern)
    {
        var sb = new StringBuilder();
        sb.Append("[generated bytecode for regexp pattern: '").Append(pattern).Append("']\n");
        int offset = 0;
        while (offset < code.Length)
        {
            sb.Append(offset.ToString("x").PadLeft(4)).Append("  ");
            sb.Append(DisassembleSingle(code, offset)).Append('\n');
            offset += Size(code[offset]);
        }
        return sb.ToString();
    }

    /// <summary>AsUC32 (src/utils/ostreams): printable ASCII as-is, else \uXXXX or \u{XXXXXX}.</summary>
    internal static string AsUC32(int c)
    {
        if (c <= 0xFFFF) return AsUC16(c);
        return "\\u{" + c.ToString("x6") + "}";
    }

    /// <summary>AsUC16 (src/utils/ostreams).</summary>
    internal static string AsUC16(int c)
    {
        if (c >= 0x20 && c < 0x7f) return ((char)c).ToString();
        return c <= 0xFF ? "\\x" + c.ToString("x2") : "\\u" + c.ToString("x4");
    }
}
