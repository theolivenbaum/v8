// Port of src/interpreter/bytecode-flags-and-tokens.h/.cc. The bit layouts
// are V8's (base::BitField8 positions).
namespace V8Sharp.Interpreter;

public static class CreateArrayLiteralFlags
{
    // FlagsBits = BitField8<int, 0, 5>; FastCloneSupportedBit = FlagsBits::Next<bool, 1>.
    public const int FlagsBitsShift = 0;
    public const int FlagsBitsSize = 5;
    public const byte FlagsBitsMask = 0x1F;
    public const int FastCloneSupportedBitShift = 5;
    public const byte FastCloneSupportedBitMask = 1 << 5;

    public static byte Encode(bool use_fast_shallow_clone, int runtime_flags)
    {
        byte result = (byte)((runtime_flags << FlagsBitsShift) & FlagsBitsMask);
        if (use_fast_shallow_clone) result |= FastCloneSupportedBitMask;
        return result;
    }

    public static int DecodeFlags(byte flags) => flags & FlagsBitsMask;
    public static bool DecodeFastCloneSupported(byte flags) => (flags & FastCloneSupportedBitMask) != 0;
}

public static class CreateObjectLiteralFlags
{
    public const byte FlagsBitsMask = 0x1F;
    public const byte FastCloneSupportedBitMask = 1 << 5;

    public static byte Encode(int runtime_flags, bool fast_clone_supported)
    {
        byte result = (byte)(runtime_flags & FlagsBitsMask);
        if (fast_clone_supported) result |= FastCloneSupportedBitMask;
        return result;
    }

    public static int DecodeFlags(byte flags) => flags & FlagsBitsMask;
    public static bool DecodeFastCloneSupported(byte flags) => (flags & FastCloneSupportedBitMask) != 0;
}

public static class CreateClosureFlags
{
    // PretenuredBit = BitField8<bool, 0, 1>; FastNewClosureBit = PretenuredBit::Next<bool, 1>.
    public const byte PretenuredBitMask = 1 << 0;
    public const byte FastNewClosureBitMask = 1 << 1;

    public static byte Encode(bool pretenure, bool is_function_scope)
    {
        byte result = pretenure ? PretenuredBitMask : (byte)0;
        if (!pretenure && is_function_scope) result |= FastNewClosureBitMask;
        return result;
    }

    public static bool DecodePretenured(byte flags) => (flags & PretenuredBitMask) != 0;
    public static bool DecodeFastNewClosure(byte flags) => (flags & FastNewClosureBitMask) != 0;
}

public static class TestTypeOfFlags
{
    /// <summary>TYPEOF_LITERAL_LIST.</summary>
    public enum LiteralFlag : byte
    {
        Number,
        String,
        Symbol,
        Boolean,
        BigInt,
        Undefined,
        Function,
        Object,
        Other,
    }

    static readonly string[] s_names =
        ["number", "string", "symbol", "boolean", "bigint", "undefined", "function", "object", "other"];

    /// <summary>
    /// The flag for a typeof comparison literal. V8 compares the literal's
    /// AstRawString against the AstStringConstants; the port takes the
    /// literal's characters.
    /// </summary>
    // TODO(merge): the bytecode generator may prefer an overload taking the AST
    // literal and the AstStringConstants (identity comparison, as V8 does).
    public static LiteralFlag GetFlagForLiteral(string raw_literal) => raw_literal switch
    {
        "number" => LiteralFlag.Number,
        "string" => LiteralFlag.String,
        "symbol" => LiteralFlag.Symbol,
        "boolean" => LiteralFlag.Boolean,
        "bigint" => LiteralFlag.BigInt,
        "undefined" => LiteralFlag.Undefined,
        "function" => LiteralFlag.Function,
        "object" => LiteralFlag.Object,
        _ => LiteralFlag.Other,
    };

    public static byte Encode(LiteralFlag literal_flag) => (byte)literal_flag;

    public static LiteralFlag Decode(byte raw_flag)
    {
        Debug.Assert(raw_flag <= (byte)LiteralFlag.Other);
        return (LiteralFlag)raw_flag;
    }

    public static string ToString(LiteralFlag literal_flag) =>
        (uint)literal_flag < (uint)s_names.Length ? s_names[(int)literal_flag] : "<invalid>";
}

public static class StoreLookupSlotFlags
{
    // LanguageModeBit = BitField8<LanguageMode, 0, 1>; LookupHoistingModeBit = Next<bool, 1>.
    public const byte LanguageModeBitMask = 1 << 0;
    public const byte LookupHoistingModeBitMask = 1 << 1;

    public static byte Encode(LanguageMode language_mode, LookupHoistingMode lookup_hoisting_mode)
    {
        Debug.Assert(lookup_hoisting_mode != LookupHoistingMode.LegacySloppy ||
                     language_mode == LanguageMode.Sloppy);
        return (byte)((language_mode == LanguageMode.Strict ? LanguageModeBitMask : 0) |
                      (lookup_hoisting_mode != LookupHoistingMode.Normal ? LookupHoistingModeBitMask : 0));
    }

    public static LanguageMode GetLanguageMode(byte flags) =>
        (flags & LanguageModeBitMask) != 0 ? LanguageMode.Strict : LanguageMode.Sloppy;

    public static bool IsLookupHoistingMode(byte flags) => (flags & LookupHoistingModeBitMask) != 0;
}

public enum TryFinallyContinuationToken
{
    // Fixed value tokens for paths we know we need.
    // Fallthrough is set to -1 to make it the fallthrough case of the jump table,
    // where the remaining cases start at 0.
    FallthroughToken = -1,
    // TODO(leszeks): Rethrow being 0 makes it use up a valuable LdaZero, which
    // means that other commands (such as break or return) have to use LdaSmi.
    // This can very slightly bloat bytecode, so perhaps token values should all
    // be shifted down by 1.
    RethrowToken = 0,
}
