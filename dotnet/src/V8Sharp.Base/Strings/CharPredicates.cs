// Port of src/strings/char-predicates.h, char-predicates-inl.h and
// char-predicates.cc.
//
// V8 (with ICU) answers the non-Latin-1 cases from ICU's binary properties
// ID_Start / ID_Continue and the general category Zs. V8Sharp has no ICU; the
// slow paths below derive the same UAX #31 properties from .NET's Unicode
// data (CharUnicodeInfo): ID_Start = L + Nl + Other_ID_Start - Pattern_Syntax
// - Pattern_White_Space, ID_Continue = ID_Start + Mn + Mc + Nd + Pc +
// Other_ID_Continue - Pattern_Syntax - Pattern_White_Space. Other_ID_*,
// Pattern_Syntax and Pattern_White_Space are stable sets listed here.
// The answers for the BMP are cached in bit tables built on first use.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Strings;

public static class CharPredicates
{
    // Constexpr cache table for character flags.
    const byte kIsIdentifierStart = 1 << 0;
    const byte kIsIdentifierPart = 1 << 1;
    const byte kIsWhiteSpace = 1 << 2;
    const byte kIsWhiteSpaceOrLineTerminator = 1 << 3;
    const byte kMaybeLineEnd = 1 << 4;

    /// <summary>If c is in 'A'-'Z' or 'a'-'z', return its lower-case. Else,
    /// return something outside of 'A'-'Z' and 'a'-'z'.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AsciiAlphaToLower(int c) => c | 0x20;

    public static bool IsCarriageReturn(int c) => c == 0x000D;
    public static bool IsLineFeed(int c) => c == 0x000A;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAsciiIdentifier(int c) => IsAlphaNumeric(c) || c == '$' || c == '_';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlphaNumeric(int c) => (uint)(AsciiAlphaToLower(c) - 'a') <= 'z' - 'a' || IsDecimalDigit(c);

    /// <summary>ECMA-262, 3rd, 7.8.3 (p 16)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDecimalDigit(int c) => (uint)(c - '0') <= 9;

    /// <summary>ECMA-262, 3rd, 7.6 (p 15)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHexDigit(int c) => IsDecimalDigit(c) || (uint)(AsciiAlphaToLower(c) - 'a') <= 'f' - 'a';

    /// <summary>ECMA-262, 6th, 7.8.3</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOctalDigit(int c) => (uint)(c - '0') <= 7;

    public static bool IsNonOctalDecimalDigit(int c) => (uint)(c - '8') <= 1;

    /// <summary>ECMA-262, 6th, 7.8.3</summary>
    public static bool IsBinaryDigit(int c) => c == '0' || c == '1';

    public static bool IsAscii(int c) => (c & ~0x7F) == 0;
    public static bool IsAsciiLower(int c) => (uint)(c - 'a') <= 'z' - 'a';
    public static bool IsAsciiUpper(int c) => (uint)(c - 'A') <= 'Z' - 'A';
    public static int ToAsciiUpper(int c) => c & ~((IsAsciiLower(c) ? 1 : 0) << 5);
    public static int ToAsciiLower(int c) => c | ((IsAsciiUpper(c) ? 1 : 0) << 5);
    public static bool IsRegExpWord(int c) => IsAlphaNumeric(c) || c == '_';

    // See http://www.unicode.org/Public/UCD/latest/ucd/DerivedCoreProperties.txt
    // ID_Start. Additionally includes '_' and '$'.
    static bool IsOneByteIDStart(int c) =>
        c == 0x0024 || (c >= 0x0041 && c <= 0x005A) || c == 0x005F ||
        (c >= 0x0061 && c <= 0x007A) || c == 0x00AA || c == 0x00B5 ||
        c == 0x00BA || (c >= 0x00C0 && c <= 0x00D6) ||
        (c >= 0x00D8 && c <= 0x00F6) || (c >= 0x00F8 && c <= 0x00FF);

    // ID_Continue. Additionally includes '_' and '$'.
    static bool IsOneByteIDContinue(int c) =>
        c == 0x0024 || (c >= 0x0030 && c <= 0x0039) || c == 0x005F ||
        (c >= 0x0041 && c <= 0x005A) || (c >= 0x0061 && c <= 0x007A) ||
        c == 0x00AA || c == 0x00B5 || c == 0x00B7 || c == 0x00BA ||
        (c >= 0x00C0 && c <= 0x00D6) || (c >= 0x00D8 && c <= 0x00F6) ||
        (c >= 0x00F8 && c <= 0x00FF);

    static bool IsOneByteWhitespace(int c) => c == '\t' || c == '\v' || c == '\f' || c == ' ' || c == 0xA0;

    static byte BuildOneByteCharFlags(int c)
    {
        byte result = 0;
        if (IsOneByteIDStart(c) || c == '\\') result |= kIsIdentifierStart;
        if (IsOneByteIDContinue(c) || c == '\\') result |= kIsIdentifierPart;
        if (IsOneByteWhitespace(c)) result |= kIsWhiteSpace | kIsWhiteSpaceOrLineTerminator;
        if (c == '\r' || c == '\n') result |= kIsWhiteSpaceOrLineTerminator | kMaybeLineEnd;
        // Add markers to identify 0x2028 and 0x2029.
        if (c == 0x28 || c == 0x29) result |= kMaybeLineEnd;
        return result;
    }

    static readonly byte[] kOneByteCharFlags = BuildTable();

    static byte[] BuildTable()
    {
        byte[] t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = BuildOneByteCharFlags(i);
        return t;
    }

    /// <summary>https://tc39.es/ecma262/#sec-names-and-keywords: '_', '$',
    /// '\' and ID_Start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierStart(int c)
    {
        if ((uint)c > 255) return IsIdentifierStartSlow(c);
        return (kOneByteCharFlags[c] & kIsIdentifierStart) != 0;
    }

    /// <summary>'$', '_', '\', ZWNJ, ZWJ and ID_Continue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierPart(int c)
    {
        if ((uint)c > 255) return IsIdentifierPartSlow(c);
        return (kOneByteCharFlags[c] & kIsIdentifierPart) != 0;
    }

    /// <summary>ES6 draft section 11.2: category Zs, \u0009, \u000b, \u000c
    /// and U+FEFF.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpace(int c)
    {
        if ((uint)c > 255) return IsWhiteSpaceSlow(c);
        return (kOneByteCharFlags[c] & kIsWhiteSpace) != 0;
    }

    /// <summary>WhiteSpace and LineTerminator according to ES6 draft section
    /// 11.2 and 11.3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpaceOrLineTerminator(int c)
    {
        if ((uint)c > 255) return IsWhiteSpaceOrLineTerminatorSlow(c);
        return (kOneByteCharFlags[c] & kIsWhiteSpaceOrLineTerminator) != 0;
    }

    public static bool IsWhiteSpaceOrLineTerminatorSlow(int c) => IsWhiteSpaceSlow(c) || IsLineTerminator(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLineTerminator(int c) => c == 0x000A || c == 0x000D || c == 0x2028 || c == 0x2029;

    public static bool IsStringLiteralLineTerminator(int c) => c == 0x000A || c == 0x000D;

    /// <summary>Two-byte version of IsLineTerminatorSequence.</summary>
    public static bool IsLineTerminatorSequence(char c, char next)
    {
        if ((kOneByteCharFlags[(byte)c] & kMaybeLineEnd) != 0)
        {
            if (c == '\n') return true;
            if (c == '\r') return next != '\n';
            return c == 0x2028 || c == 0x2029;
        }
        return false;
    }

    // ---- The ICU-equivalent slow paths ----------------------------------

    // Bit tables for the BMP: bit c of s_idStart is ID_Start(c), etc.
    static ulong[]? s_idStartBmp;
    static ulong[]? s_idContinueBmp;

    /// <summary>UnicodeIDStart, '$', '_' and '\'.</summary>
    public static bool IsIdentifierStartSlow(int c)
    {
        if ((uint)c <= 0xFFFF)
        {
            ulong[] t = s_idStartBmp ??= BuildBmpTable(start: true);
            return (t[c >> 6] & (1UL << c)) != 0 || (c < 0x60 && (c == '$' || c == '\\' || c == '_'));
        }
        return IsIDStart(c);
    }

    /// <summary>UnicodeIDContinue, '$', '_', '\', ZWJ, and ZWNJ.</summary>
    public static bool IsIdentifierPartSlow(int c)
    {
        if ((uint)c <= 0xFFFF)
        {
            ulong[] t = s_idContinueBmp ??= BuildBmpTable(start: false);
            return (t[c >> 6] & (1UL << c)) != 0 || (c < 0x60 && (c == '$' || c == '\\' || c == '_')) ||
                   c == 0x200C || c == 0x200D;
        }
        return IsIDContinue(c);
    }

    /// <summary>gC=Zs, U+0009, U+000B, U+000C, U+FEFF.</summary>
    public static bool IsWhiteSpaceSlow(int c)
    {
        return ((uint)c <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator) ||
               (c < 0x0D && (c == 0x09 || c == 0x0B || c == 0x0C)) || c == 0xFEFF;
    }

    static ulong[] BuildBmpTable(bool start)
    {
        ulong[] t = new ulong[0x10000 / 64];
        for (int c = 0; c <= 0xFFFF; c++)
        {
            if (start ? IsIDStart(c) : IsIDContinue(c)) t[c >> 6] |= 1UL << c;
        }
        return t;
    }

    /// <summary>The Unicode ID_Start derived property.</summary>
    public static bool IsIDStart(int c)
    {
        if ((uint)c > 0x10FFFF) return false;
        if (IsPatternSyntax(c) || IsPatternWhiteSpace(c)) return false;
        if (IsOtherIDStart(c)) return true;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The Unicode ID_Continue derived property.</summary>
    public static bool IsIDContinue(int c)
    {
        if ((uint)c > 0x10FFFF) return false;
        if (IsPatternSyntax(c) || IsPatternWhiteSpace(c)) return false;
        if (IsOtherIDStart(c) || IsOtherIDContinue(c)) return true;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.ConnectorPunctuation:
                return true;
            default:
                return false;
        }
    }

    // PropList.txt: Other_ID_Start.
    static bool IsOtherIDStart(int c) =>
        c == 0x1885 || c == 0x1886 || c == 0x2118 || c == 0x212E || c == 0x309B || c == 0x309C;

    // PropList.txt: Other_ID_Continue (Unicode 15.1 and later).
    static bool IsOtherIDContinue(int c) =>
        c == 0x00B7 || c == 0x0387 || (c >= 0x1369 && c <= 0x1371) || c == 0x19DA ||
        c == 0x200C || c == 0x200D || c == 0x30FB || c == 0xFF65;

    // PropList.txt: Pattern_White_Space (a stable set).
    static bool IsPatternWhiteSpace(int c) =>
        (c >= 0x0009 && c <= 0x000D) || c == 0x0020 || c == 0x0085 ||
        c == 0x200E || c == 0x200F || c == 0x2028 || c == 0x2029;

    // PropList.txt: Pattern_Syntax (a stable set).
    static bool IsPatternSyntax(int c)
    {
        if (c < 0x80)
        {
            return (c >= 0x21 && c <= 0x2F) || (c >= 0x3A && c <= 0x40) || (c >= 0x5B && c <= 0x5E) ||
                   c == 0x60 || (c >= 0x7B && c <= 0x7E);
        }
        if (c < 0x100)
        {
            return (c >= 0xA1 && c <= 0xA7) || c == 0xA9 || c == 0xAB || c == 0xAC || c == 0xAE ||
                   c == 0xB0 || c == 0xB1 || c == 0xB6 || c == 0xBB || c == 0xBF || c == 0xD7 || c == 0xF7;
        }
        return (c >= 0x2010 && c <= 0x2027) || (c >= 0x2030 && c <= 0x203E) || (c >= 0x2041 && c <= 0x2053) ||
               (c >= 0x2055 && c <= 0x205E) || (c >= 0x2190 && c <= 0x245F) || (c >= 0x2500 && c <= 0x2775) ||
               (c >= 0x2794 && c <= 0x2BFF) || (c >= 0x2E00 && c <= 0x2E7F) || (c >= 0x3001 && c <= 0x3003) ||
               (c >= 0x3008 && c <= 0x3020) || c == 0x3030 || c == 0xFD3E || c == 0xFD3F || c == 0xFE45 ||
               c == 0xFE46;
    }
}
