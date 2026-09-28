// Port of RegExp::VerifySyntax (src/regexp/regexp.cc) as used by
// ParserBase::ValidateRegExpLiteral: the early SyntaxError for a malformed
// regexp literal. V8Sharp.Parsing does not reference the regexp engine, so the
// engine hands this validator to the parser (ParseInfo.set_regexp_syntax_validator).
using V8Sharp.Parsing;
using V8Sharp.RegExp;

namespace V8Sharp.Objects;

/// <summary>The parser's regexp literal validator, backed by V8Sharp.RegExp.</summary>
public sealed class RegExpSyntaxValidator : IRegExpSyntaxValidator
{
    public static readonly RegExpSyntaxValidator Instance = new();

    /// <summary>
    /// RegExp::VerifySyntax: true if the pattern parses with the flags (the
    /// parser's RegExpFlags have V8's regexp::Flags layout). On failure,
    /// <paramref name="error_message"/> is RegExpErrorString(error).
    /// </summary>
    public bool VerifySyntax(string pattern, int flags, out string error_message, out bool is_stack_overflow)
    {
        if (RegExpEngine.VerifySyntax(pattern, (V8Sharp.RegExp.RegExpFlags)flags, out RegExpError error, out _))
        {
            error_message = null!;
            is_stack_overflow = false;
            return true;
        }
        error_message = RegExpErrors.ErrorString(error);
        is_stack_overflow = error.ErrorIsStackOverflow();
        return false;
    }
}
