// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/strings/char-predicates.h, char-predicates-inl.h and
// char-predicates.cc (the V8_INTL_SUPPORT path), plus the unibrow::Utf16 and
// line terminator helpers of src/strings/unicode.h that the scanner uses.
// TODO(merge): move to V8Sharp.Base CharPredicates.
//
// base::uc32 is `int` here; kEndOfInput is -1 (V8: static_cast<uc32>(-1)), and
// every range check casts to uint so the sentinel never matches.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace V8Sharp.Parsing;

public static class CharPredicates
{
    private const byte kIsIdentifierStart = 1 << 0;
    private const byte kIsIdentifierPart = 1 << 1;
    private const byte kIsWhiteSpace = 1 << 2;
    private const byte kIsWhiteSpaceOrLineTerminator = 1 << 3;
    private const byte kMaybeLineEnd = 1 << 4;

    private static readonly byte[] s_oneByteCharFlags = BuildOneByteCharFlagsTable();

    private static bool IsOneByteIDStart(int c) =>
        c == 0x0024 || (c >= 0x0041 && c <= 0x005A) || c == 0x005F ||
        (c >= 0x0061 && c <= 0x007A) || c == 0x00AA || c == 0x00B5 ||
        c == 0x00BA || (c >= 0x00C0 && c <= 0x00D6) ||
        (c >= 0x00D8 && c <= 0x00F6) || (c >= 0x00F8 && c <= 0x00FF);

    private static bool IsOneByteIDContinue(int c) =>
        c == 0x0024 || (c >= 0x0030 && c <= 0x0039) || c == 0x005F ||
        (c >= 0x0041 && c <= 0x005A) || (c >= 0x0061 && c <= 0x007A) ||
        c == 0x00AA || c == 0x00B5 || c == 0x00B7 || c == 0x00BA ||
        (c >= 0x00C0 && c <= 0x00D6) || (c >= 0x00D8 && c <= 0x00F6) ||
        (c >= 0x00F8 && c <= 0x00FF);

    private static bool IsOneByteWhitespace(int c) => c == '\t' || c == '\v' || c == '\f' || c == ' ' || c == 0xA0;

    private static byte[] BuildOneByteCharFlagsTable()
    {
        var table = new byte[256];
        for (int c = 0; c < 256; c++)
        {
            byte result = 0;
            if (IsOneByteIDStart(c) || c == '\\') result |= kIsIdentifierStart;
            if (IsOneByteIDContinue(c) || c == '\\') result |= kIsIdentifierPart;
            if (IsOneByteWhitespace(c)) result |= kIsWhiteSpace | kIsWhiteSpaceOrLineTerminator;
            if (c == '\r' || c == '\n') result |= kIsWhiteSpaceOrLineTerminator | kMaybeLineEnd;
            // Add markers to identify 0x2028 and 0x2029.
            if (c == unchecked((byte)0x2028) || c == unchecked((byte)0x2029)) result |= kMaybeLineEnd;
            table[c] = result;
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AsciiAlphaToLower(int c) => c | 0x20;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCarriageReturn(int c) => c == 0x000D;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLineFeed(int c) => c == 0x000A;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAsciiIdentifier(int c) => IsAlphaNumeric(c) || c == '$' || c == '_';
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlphaNumeric(int c) => (uint)(AsciiAlphaToLower(c) - 'a') <= 'z' - 'a' || IsDecimalDigit(c);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDecimalDigit(int c) => (uint)(c - '0') <= 9;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHexDigit(int c) => IsDecimalDigit(c) || (uint)(AsciiAlphaToLower(c) - 'a') <= 'f' - 'a';
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOctalDigit(int c) => (uint)(c - '0') <= 7;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNonOctalDecimalDigit(int c) => (uint)(c - '8') <= 1;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBinaryDigit(int c) => c == '0' || c == '1';
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAscii(int c) => (c & ~0x7F) == 0;
    public static bool IsAsciiLower(int c) => (uint)(c - 'a') <= 'z' - 'a';
    public static bool IsAsciiUpper(int c) => (uint)(c - 'A') <= 'Z' - 'A';
    public static int ToAsciiUpper(int c) => c & ~((IsAsciiLower(c) ? 1 : 0) << 5);
    public static int ToAsciiLower(int c) => c | ((IsAsciiUpper(c) ? 1 : 0) << 5);
    public static bool IsRegExpWord(int c) => IsAlphaNumeric(c) || c == '_';

    // base::HexValue
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HexValue(int c)
    {
        c -= '0';
        if ((uint)c <= 9) return c;
        c = (c | 0x20) - ('a' - '0'); // detect 0x11..0x16 and 0x31..0x36.
        if ((uint)c <= 5) return c + 10;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierStart(int c)
    {
        if ((uint)c > 255) return IsIdentifierStartSlow(c);
        return (s_oneByteCharFlags[c] & kIsIdentifierStart) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdentifierPart(int c)
    {
        if ((uint)c > 255) return IsIdentifierPartSlow(c);
        return (s_oneByteCharFlags[c] & kIsIdentifierPart) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpace(int c)
    {
        if ((uint)c > 255) return IsWhiteSpaceSlow(c);
        return (s_oneByteCharFlags[c] & kIsWhiteSpace) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhiteSpaceOrLineTerminator(int c)
    {
        if ((uint)c > 255) return IsWhiteSpaceOrLineTerminatorSlow(c);
        return (s_oneByteCharFlags[c] & kIsWhiteSpaceOrLineTerminator) != 0;
    }

    public static bool IsWhiteSpaceOrLineTerminatorSlow(int c) => IsWhiteSpaceSlow(c) || IsLineTerminator(c);

    public static bool IsLineTerminatorSequence(int c, int next)
    {
        if ((s_oneByteCharFlags[(byte)c] & kMaybeLineEnd) != 0)
        {
            if (c == '\n') return true;
            if (c == '\r') return next != '\n';
            return (uint)(c - 0x2028) <= 1;
        }
        return false;
    }

    // --- ICU-equivalent slow paths (src/strings/char-predicates.cc) ---------
    // ICU's UCHAR_ID_START / UCHAR_ID_CONTINUE are derived properties:
    //   ID_Start    = L + Nl + Other_ID_Start - Pattern_Syntax - Pattern_White_Space
    //   ID_Continue = ID_Start + Mn + Mc + Nd + Pc + Other_ID_Continue
    //                 - Pattern_Syntax - Pattern_White_Space
    // computed here from .NET's Unicode category data. The Unicode version of
    // .NET's tables may differ from the ICU bundled with V8.

    public static bool IsIdentifierStartSlow(int c)
    {
        if (c < 0) return false;
        if (c < 0x60 && (c == '$' || c == '\\' || c == '_')) return true;
        return IsUnicodeIDStart(c);
    }

    public static bool IsIdentifierPartSlow(int c)
    {
        if (c < 0) return false;
        if (c < 0x60 && (c == '$' || c == '\\' || c == '_')) return true;
        if (c == 0x200C || c == 0x200D) return true;
        return IsUnicodeIDContinue(c);
    }

    public static bool IsWhiteSpaceSlow(int c)
    {
        if (c < 0 || c > 0x10FFFF) return false;
        if (c < 0x0D && (c == 0x09 || c == 0x0B || c == 0x0C)) return true;
        if (c == 0xFEFF) return true;
        return GetCategory(c) == UnicodeCategory.SpaceSeparator;
    }

    private static UnicodeCategory GetCategory(int c) =>
        c <= 0xFFFF ? CharUnicodeInfo.GetUnicodeCategory((char)c) : CharUnicodeInfo.GetUnicodeCategory(c);

    private static bool IsOtherIDStart(int c) =>
        c == 0x1885 || c == 0x1886 || c == 0x2118 || c == 0x212E || c == 0x309B || c == 0x309C;

    private static bool IsOtherIDContinue(int c) =>
        c == 0x00B7 || c == 0x0387 || (c >= 0x1369 && c <= 0x1371) || c == 0x19DA ||
        c == 0x200C || c == 0x200D || c == 0x30FB || c == 0xFF65;

    // Pattern_Syntax code points that are letters/marks (the only ones that can
    // intersect ID_Start/ID_Continue candidates): U+2E2F VERTICAL TILDE (Lm).
    private static bool IsPatternSyntaxLetter(int c) => c == 0x2E2F;

    public static bool IsUnicodeIDStart(int c)
    {
        if ((uint)c > 0x10FFFF) return false;
        if (c >= 0xD800 && c <= 0xDFFF) return false;
        if (IsOtherIDStart(c)) return true;
        if (IsPatternSyntaxLetter(c)) return false;
        switch (GetCategory(c))
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

    public static bool IsUnicodeIDContinue(int c)
    {
        if ((uint)c > 0x10FFFF) return false;
        if (c >= 0xD800 && c <= 0xDFFF) return false;
        if (IsOtherIDStart(c) || IsOtherIDContinue(c)) return true;
        if (IsPatternSyntaxLetter(c)) return false;
        switch (GetCategory(c))
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

    // --- unibrow (src/strings/unicode.h) -------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLineTerminator(int c) => c == 0x000A || c == 0x000D || c == 0x2028 || c == 0x2029;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStringLiteralLineTerminator(int c) => c == 0x000A || c == 0x000D;
}

// unibrow::Utf16
public static class Utf16
{
    public const int kMaxNonSurrogateCharCode = 0xFFFF;
    public const int kMaxExtraUtf8BytesForOneUtf16CodeUnit = 2;
    public const int kUtf8BytesToCodeASurrogate = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePair(int lead, int trail) => IsLeadSurrogate(lead) && IsTrailSurrogate(trail);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeadSurrogate(int code) => (code & 0x1FFC00) == 0xD800;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrailSurrogate(int code) => (code & 0x1FFC00) == 0xDC00;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CombineSurrogatePair(int lead, int trail) => 0x10000 + ((lead & 0x3FF) << 10) + (trail & 0x3FF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char LeadSurrogate(int char_code) => (char)(0xD800 + (((char_code - 0x10000) >> 10) & 0x3FF));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char TrailSurrogate(int char_code) => (char)(0xDC00 + (char_code & 0x3FF));
}
