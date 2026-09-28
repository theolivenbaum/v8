// Port of src/objects/string.{h,cc}, string-inl.h and string-comparator.{h,cc}:
// the operations on JSString (V8's String class). The representation classes
// are in Name.cs.
using System.Runtime.CompilerServices;
using System.Text;
using V8Sharp.Base.Numbers;
using V8Sharp.Common;
using V8Sharp.Strings;

namespace V8Sharp.Objects;

public abstract partial class JSString
{
    /// <summary>String::kMaxLength on 64-bit hosts: (1 &lt;&lt; 29) - 24.</summary>
    public const int kMaxLength = (1 << 29) - 24;
    public const int kMaxHashCalcLength = 16383;
    public const int kMaxOneByteCharCode = 0xFF;
    public const int kMaxUtf16CodeUnit = 0xFFFF;
    public const int kMaxCodePoint = 0x10FFFF;
    public const int kMaxShortPrintLength = 1024;

    /// <summary>The code unit at <paramref name="index"/> (V8's String::Get).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public char Get(int index)
    {
        if (this is SeqString s) return s.Value[index];
        return GetSlow(index);
    }

    char GetSlow(int index)
    {
        if (this is SlicedString sl) return sl.Parent.Flatten()[sl.Offset + index];
        return Flatten()[index];
    }

    /// <summary>The characters of the flattened string.</summary>
    public ReadOnlySpan<char> FlatSpan()
    {
        if (this is SlicedString sl) return sl.AsSpan();
        return Flatten().AsSpan();
    }

    /// <summary>
    /// The canonical (internalized) string for this one if it is already known,
    /// else this string (V8 resolves ThinStrings the same way).
    /// </summary>
    public JSString Actual => IsInternalized ? this : InternalizedForward ?? this;

    // ---- Hashing and array indices ------------------------------------------

    /// <summary>String::ComputeAndSetRawHash.</summary>
    internal uint ComputeAndSetRawHash()
    {
        if (InternalizedForward is { } canonical)
        {
            uint canonicalHash = canonical.EnsureRawHash();
            RawHashField = canonicalHash;
            return canonicalHash;
        }
        uint field = Length > kMaxHashCalcLength
            ? StringHasher.GetTrivialHash((uint)Length)
            : StringHasher.HashSequentialString(FlatSpan());
        RawHashField = field;
        return field;
    }

    /// <summary>String::AsArrayIndex: true if this string is a canonical array index (&lt; 2^32 - 1).</summary>
    public new bool AsArrayIndex(out uint index)
    {
        uint field = RawHashField;
        if (ContainsCachedArrayIndex(field))
        {
            index = StringHasher.DecodeArrayIndexFromHashField(field);
            return true;
        }
        if (IsHashFieldComputed(field) && !IsIntegerIndex(field))
        {
            index = 0;
            return false;
        }
        return SlowAsArrayIndex(out index);
    }

    /// <summary>String::AsIntegerIndex: true if this string is a canonical integer index (&lt;= 2^53 - 1).</summary>
    public new bool AsIntegerIndex(out ulong index)
    {
        uint field = RawHashField;
        if (ContainsCachedArrayIndex(field))
        {
            index = StringHasher.DecodeArrayIndexFromHashField(field);
            return true;
        }
        if (IsHashFieldComputed(field) && !IsIntegerIndex(field))
        {
            index = 0;
            return false;
        }
        return SlowAsIntegerIndex(out index);
    }

    bool SlowAsArrayIndex(out uint index)
    {
        int length = Length;
        if (length <= kMaxCachedArrayIndexLength)
        {
            uint field = EnsureRawHash();
            if (!IsIntegerIndex(field))
            {
                index = 0;
                return false;
            }
            index = StringHasher.DecodeArrayIndexFromHashField(field);
            return true;
        }
        index = 0;
        if (length == 0 || length > kMaxArrayIndexSize) return false;
        if (!StringToIndex(FlatSpan(), out ulong value) || value > kMaxArrayIndex) return false;
        index = (uint)value;
        return true;
    }

    bool SlowAsIntegerIndex(out ulong index)
    {
        int length = Length;
        if (length <= kMaxCachedArrayIndexLength)
        {
            uint field = EnsureRawHash();
            if (!IsIntegerIndex(field))
            {
                index = 0;
                return false;
            }
            index = StringHasher.DecodeArrayIndexFromHashField(field);
            return true;
        }
        index = 0;
        if (length == 0 || length > kMaxIntegerIndexSize) return false;
        return StringToIndex(FlatSpan(), out index) && index <= kMaxSafeIntegerUint64;
    }

    /// <summary>V8's StringToIndex (src/utils/utils-inl.h): canonical decimal digits, no leading zeros.</summary>
    static bool StringToIndex(ReadOnlySpan<char> s, out ulong index)
    {
        index = 0;
        if (s.IsEmpty) return false;
        uint d = (uint)(s[0] - '0');
        if (d > 9) return false;
        if (d == 0) return s.Length == 1;
        ulong result = d;
        for (int i = 1; i < s.Length; i++)
        {
            d = (uint)(s[i] - '0');
            if (d > 9) return false;
            if (result > (ulong.MaxValue - 9) / 10) return false;
            result = result * 10 + d;
        }
        index = result;
        return true;
    }

    /// <summary>String::ToArrayIndex: the index as an int, or -1.</summary>
    public static int ToArrayIndex(JSString key)
    {
        if (!key.AsArrayIndex(out uint index)) return -1;
        return index <= int.MaxValue ? (int)index : -1;
    }

    // ---- Equality and comparison --------------------------------------------

    /// <summary>String::Equals.</summary>
    public static bool Equals(JSString one, JSString two)
    {
        if (ReferenceEquals(one, two)) return true;
        if (one.IsInternalized && two.IsInternalized) return false;
        return one.SlowEquals(two);
    }

    /// <summary>String::SlowEquals: length, then hash (if both known), then contents.</summary>
    public bool SlowEquals(JSString other)
    {
        int len = Length;
        if (len != other.Length) return false;
        if (len == 0) return true;
        if (HasHashCode && other.HasHashCode)
        {
            if (RawHashField != other.RawHashField) return false;
        }
        if (Get(0) != other.Get(0)) return false;
        return FlatSpan().SequenceEqual(other.FlatSpan());
    }

    /// <summary>String::IsEqualTo for a C# string.</summary>
    public bool IsEqualTo(ReadOnlySpan<char> str) => Length == str.Length && FlatSpan().SequenceEqual(str);

    /// <summary>Whether this string begins with <paramref name="str"/> (HasOneBytePrefix).</summary>
    public bool HasPrefix(ReadOnlySpan<char> str) => Length >= str.Length && FlatSpan()[..str.Length].SequenceEqual(str);

    /// <summary>String::Compare: code-unit lexicographic order.</summary>
    public static ComparisonResult Compare(JSString x, JSString y)
    {
        if (ReferenceEquals(x, y)) return ComparisonResult.Equal;
        if (y.Length == 0) return x.Length == 0 ? ComparisonResult.Equal : ComparisonResult.GreaterThan;
        if (x.Length == 0) return ComparisonResult.LessThan;
        int d = x.Get(0) - y.Get(0);
        if (d < 0) return ComparisonResult.LessThan;
        if (d > 0) return ComparisonResult.GreaterThan;
        int r = x.FlatSpan().SequenceCompareTo(y.FlatSpan());
        return r < 0 ? ComparisonResult.LessThan : r > 0 ? ComparisonResult.GreaterThan : ComparisonResult.Equal;
    }

    // ---- Searching ----------------------------------------------------------

    /// <summary>String::IndexOf(receiver, search, start_index).</summary>
    public static int IndexOf(JSString receiver, JSString search, int startIndex)
    {
        Debug.Assert(startIndex <= receiver.Length);
        int searchLength = search.Length;
        if (searchLength == 0) return startIndex;
        int receiverLength = receiver.Length;
        if ((long)startIndex + searchLength > receiverLength) return -1;
        int r = receiver.FlatSpan()[startIndex..].IndexOf(search.FlatSpan(), StringComparison.Ordinal);
        return r < 0 ? -1 : r + startIndex;
    }

    /// <summary>StringMatchBackwards: the last match starting at or before <paramref name="idx"/>.</summary>
    public static int LastIndexOf(JSString receiver, JSString search, int idx)
    {
        ReadOnlySpan<char> subject = receiver.FlatSpan();
        ReadOnlySpan<char> pattern = search.FlatSpan();
        int patternLength = pattern.Length;
        if (patternLength == 0) return idx;
        if (idx + patternLength > subject.Length) idx = subject.Length - patternLength;
        if (idx < 0) return -1;
        return subject[..(idx + patternLength)].LastIndexOf(pattern, StringComparison.Ordinal);
    }

    // ---- Predicates ---------------------------------------------------------

    /// <summary>String::IsWellFormedUnicode: no lone surrogates.</summary>
    public bool IsWellFormedUnicode()
    {
        ReadOnlySpan<char> s = FlatSpan();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return false;
            }
            if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    /// <summary>String::IsIdentifier.</summary>
    public static bool IsIdentifier(JSString str)
    {
        ReadOnlySpan<char> s = str.FlatSpan();
        if (s.IsEmpty) return false;
        if (!CharPredicates.IsIdentifierStart(s[0])) return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!CharPredicates.IsIdentifierPart(s[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// String::CalculateLineEnds: positions of line terminators (\n, \r not
    /// followed by \n, U+2028, U+2029), plus the end if <paramref name="includeEndingLine"/>.
    /// </summary>
    public static int[] CalculateLineEnds(JSString src, bool includeEndingLine)
    {
        ReadOnlySpan<char> s = src.FlatSpan();
        var ends = new List<int>(s.Length / 32 + 1);
        int length = s.Length;
        for (int i = 0; i < length - 1; i++)
        {
            char current = s[i];
            char next = s[i + 1];
            if (IsLineTerminatorSequence(current, next)) ends.Add(i);
        }
        if (length > 0 && IsLineTerminatorSequence(s[length - 1], '\0'))
        {
            ends.Add(length - 1);
        }
        if (includeEndingLine)
        {
            ends.Add(length);
        }
        return ends.ToArray();
    }

    static bool IsLineTerminatorSequence(char c, char next)
    {
        if (c == '\n' || c == '\u2028' || c == '\u2029') return true;
        if (c == '\r') return next != '\n';
        return false;
    }

    // ---- Replacement patterns -----------------------------------------------

    /// <summary>String::Match: the match a replacement pattern refers to.</summary>
    public abstract class Match
    {
        public enum CaptureState { Unmatched, Matched }

        public abstract JSString GetMatch();
        public abstract JSString GetPrefix();
        public abstract JSString GetSuffix();
        public abstract int CaptureCount();
        public abstract bool HasNamedCaptures();
        public abstract JSString? GetCapture(int i, out bool captureExists);
        public abstract JSString? GetNamedCapture(JSString name, out CaptureState state);
    }

    /// <summary>String::GetSubstitution: expands $$, $&amp;, $`, $', $n, $nn and $&lt;name&gt;.</summary>
    public static JSString GetSubstitution(Isolate isolate, Match match, JSString replacement, int startIndex = 0)
    {
        Heap.Factory factory = isolate.Factory;
        int replacementLength = replacement.Length;
        int capturesLength = match.CaptureCount();
        ReadOnlySpan<char> rep = replacement.FlatSpan();

        int nextDollar = IndexOfChar(rep, '$', startIndex);
        if (nextDollar < 0) return replacement;

        var builder = new IncrementalStringBuilder(isolate);
        if (nextDollar > 0) builder.AppendString(factory.NewSubString(replacement, 0, nextDollar));

        while (true)
        {
            int peekIx = nextDollar + 1;
            if (peekIx >= replacementLength)
            {
                builder.AppendCharacter('$');
                return builder.Finish();
            }
            int continueFromIx;
            char peek = replacement.Get(peekIx);
            switch (peek)
            {
                case '$':
                    builder.AppendCharacter('$');
                    continueFromIx = peekIx + 1;
                    break;
                case '&':
                    builder.AppendString(match.GetMatch());
                    continueFromIx = peekIx + 1;
                    break;
                case '`':
                    builder.AppendString(match.GetPrefix());
                    continueFromIx = peekIx + 1;
                    break;
                case '\'':
                    builder.AppendString(match.GetSuffix());
                    continueFromIx = peekIx + 1;
                    break;
                case >= '0' and <= '9':
                {
                    int scaledIndex = peek - '0';
                    int advance = 1;
                    if (peekIx + 1 < replacementLength)
                    {
                        char nextPeek = replacement.Get(peekIx + 1);
                        if (nextPeek is >= '0' and <= '9')
                        {
                            int newScaledIndex = scaledIndex * 10 + (nextPeek - '0');
                            if (newScaledIndex < capturesLength)
                            {
                                scaledIndex = newScaledIndex;
                                advance = 2;
                            }
                        }
                    }
                    if (scaledIndex == 0 || scaledIndex >= capturesLength)
                    {
                        builder.AppendCharacter('$');
                        continueFromIx = peekIx;
                        break;
                    }
                    JSString? capture = match.GetCapture(scaledIndex, out bool captureExists);
                    if (captureExists && capture is not null) builder.AppendString(capture);
                    continueFromIx = peekIx + advance;
                    break;
                }
                case '<':
                {
                    if (!match.HasNamedCaptures())
                    {
                        builder.AppendCharacter('$');
                        continueFromIx = peekIx;
                        break;
                    }
                    int closingBracketIx = IndexOfChar(rep, '>', peekIx + 1);
                    if (closingBracketIx == -1)
                    {
                        builder.AppendCharacter('$');
                        continueFromIx = peekIx;
                        break;
                    }
                    JSString captureName = factory.NewSubString(replacement, peekIx + 1, closingBracketIx);
                    JSString? capture = match.GetNamedCapture(captureName, out Match.CaptureState state);
                    if (state == Match.CaptureState.Matched && capture is not null) builder.AppendString(capture);
                    continueFromIx = closingBracketIx + 1;
                    break;
                }
                default:
                    builder.AppendCharacter('$');
                    continueFromIx = peekIx;
                    break;
            }

            nextDollar = IndexOfChar(rep, '$', continueFromIx);
            if (nextDollar < 0)
            {
                if (continueFromIx < replacementLength)
                {
                    builder.AppendString(factory.NewSubString(replacement, continueFromIx, replacementLength));
                }
                return builder.Finish();
            }
            if (nextDollar > continueFromIx)
            {
                builder.AppendString(factory.NewSubString(replacement, continueFromIx, nextDollar));
            }
        }
    }

    static int IndexOfChar(ReadOnlySpan<char> s, char c, int start)
    {
        if (start >= s.Length) return -1;
        int r = s[start..].IndexOf(c);
        return r < 0 ? -1 : r + start;
    }

    // ---- Conversions --------------------------------------------------------

    /// <summary>String::ToNumber (StringToDouble with ALLOW_NON_DECIMAL_PREFIX).</summary>
    public static double ToNumber(JSString subject)
    {
        // Fast path: a cached array index (String::ToNumber's callers check it
        // first in Object::ConvertToNumber).
        if (subject.AsArrayIndex(out uint index)) return index;
        return Conversions.StringToDouble(subject.FlatSpan(), ConversionFlag.AllowNonDecimalPrefix);
    }

    /// <summary>A printable form for diagnostics (String::ToCString).</summary>
    public string ToCString() => Flatten();
}

/// <summary>
/// ECMAScript identifier predicates for String::IsIdentifier.
/// TODO(merge): use V8Sharp.Base's char-predicates port.
/// </summary>
internal static class CharPredicates
{
    public static bool IsIdentifierStart(char c)
    {
        if (c < 128) return (uint)((c | 0x20) - 'a') <= 25 || c == '$' || c == '_';
        return char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.UppercaseLetter or
            System.Globalization.UnicodeCategory.LowercaseLetter or
            System.Globalization.UnicodeCategory.TitlecaseLetter or
            System.Globalization.UnicodeCategory.ModifierLetter or
            System.Globalization.UnicodeCategory.OtherLetter or
            System.Globalization.UnicodeCategory.LetterNumber => true,
            _ => false,
        };
    }

    public static bool IsIdentifierPart(char c)
    {
        if (c < 128) return (uint)((c | 0x20) - 'a') <= 25 || (uint)(c - '0') <= 9 || c == '$' || c == '_';
        if (c == '\u200C' || c == '\u200D') return true;
        if (IsIdentifierStart(c)) return true;
        return char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.DecimalDigitNumber or
            System.Globalization.UnicodeCategory.ConnectorPunctuation => true,
            _ => false,
        };
    }
}
