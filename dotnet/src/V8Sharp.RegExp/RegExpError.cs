// Port of src/regexp/regexp-error.h and src/regexp/regexp-error.cc.

namespace V8Sharp.RegExp;

/// <summary>regexp::Error. The message text is V8's, byte for byte.</summary>
public enum RegExpError : uint
{
    None,
    StackOverflow,
    AnalysisStackOverflow,
    TooLarge,
    UnterminatedGroup,
    UnmatchedParen,
    EscapeAtEndOfPattern,
    InvalidPropertyName,
    InvalidEscape,
    InvalidDecimalEscape,
    InvalidUnicodeEscape,
    NothingToRepeat,
    LoneQuantifierBrackets,
    RangeOutOfOrder,
    IncompleteQuantifier,
    InvalidQuantifier,
    InvalidGroup,
    MultipleFlagDashes,
    NotLinear,
    RepeatedFlag,
    InvalidFlagGroup,
    TooManyCaptures,
    InvalidCaptureGroupName,
    DuplicateCaptureGroupName,
    InvalidNamedReference,
    InvalidNamedCaptureReference,
    InvalidClassPropertyName,
    InvalidCharacterClass,
    UnterminatedCharacterClass,
    OutOfOrderCharacterClass,
    InvalidClassSetOperation,
    InvalidCharacterInClass,
    NegatedCharacterClassWithStrings,
    UnsupportedBytecode,
    NumErrors,
}

public static class RegExpErrors
{
    static readonly string[] kErrorStrings =
    [
        "",
        "Maximum call stack size exceeded",
        "Stack overflow",
        "Regular expression too large",
        "Unterminated group",
        "Unmatched ')'",
        "\\ at end of pattern",
        "Invalid property name",
        "Invalid escape",
        "Invalid decimal escape",
        "Invalid Unicode escape",
        "Nothing to repeat",
        "Lone quantifier brackets",
        "numbers out of order in {} quantifier",
        "Incomplete quantifier",
        "Invalid quantifier",
        "Invalid group",
        "Multiple dashes in flag group",
        "Cannot be executed in linear time",
        "Repeated flag in flag group",
        "Invalid flag group",
        "Too many captures",
        "Invalid capture group name",
        "Duplicate capture group name",
        "Invalid named reference",
        "Invalid named capture referenced",
        "Invalid property name in character class",
        "Invalid character class",
        "Unterminated character class",
        "Range out of order in character class",
        "Invalid set operation in character class",
        "Invalid character in character class",
        "Negated character class may contain strings",
        "Unsupported Bytecode",
    ];

    /// <summary>ErrorString.</summary>
    public static string ErrorString(this RegExpError error) => kErrorStrings[(int)error];

    public static bool ErrorIsStackOverflow(this RegExpError error) =>
        error == RegExpError.StackOverflow || error == RegExpError.AnalysisStackOverflow ||
        error == RegExpError.TooLarge;
}
