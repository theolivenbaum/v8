// Port of src/strings/unicode.h, unicode-inl.h and unicode.cc (unibrow),
// in the !V8_INTL_SUPPORT configuration: the case mapping and predicates
// come from V8's own generated tables (UnicodeTables.*.cs), not from ICU.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Unibrow;

/// <summary>A character predicate with a static Is (unibrow's predicate structs).</summary>
public interface ICharPredicate
{
    static abstract bool Is(uint c);
}

/// <summary>A case mapping with a static Convert (unibrow's mapping structs).
/// Convert writes up to kMaxWidth characters to result and returns their
/// number (0 means "maps to itself"). allowCaching is cleared if the result
/// has several characters or depends on the context (the next character n).</summary>
public interface ICharMapping
{
    static abstract int kMaxWidth { get; }
    static abstract int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching);
}

/// <summary>The max length of the result of converting the case of a single character.</summary>
public static class Unicode
{
    public const int kMaxMappingSize = 4;
}

// Uppercase:            point.category == 'Lu'
public readonly struct Uppercase : ICharPredicate
{
    public static bool Is(uint c) => UnicodeTables.Uppercase_Is(c);
}

// Letter:               point.category in ['Lu', 'Ll', 'Lt', 'Lm', 'Lo', 'Nl']
public readonly struct Letter : ICharPredicate
{
    public static bool Is(uint c) => UnicodeTables.Letter_Is(c);
}

// ID_Start:             ((point.category in ['Lu', 'Ll', 'Lt', 'Lm', 'Lo',
// 'Nl'] or 'Other_ID_Start' in point.properties) and ('Pattern_Syntax' not in
// point.properties) and ('Pattern_White_Space' not in point.properties)) or
// ('JS_ID_Start' in point.properties)
public readonly struct ID_Start : ICharPredicate
{
    public static bool Is(uint c) => UnicodeTables.ID_Start_Is(c);
}

// ID_Continue:          point.category in ['Nd', 'Mn', 'Mc', 'Pc'] or
// 'Other_ID_Continue' in point.properties or 'JS_ID_Continue' in
// point.properties
public readonly struct ID_Continue : ICharPredicate
{
    public static bool Is(uint c) => UnicodeTables.ID_Continue_Is(c);
}

// WhiteSpace:           (point.category == 'Zs') or ('JS_White_Space' in
// point.properties)
public readonly struct WhiteSpace : ICharPredicate
{
    public static bool Is(uint c) => UnicodeTables.WhiteSpace_Is(c);
}

public readonly struct ToLowercase : ICharMapping
{
    public const int MaxWidth = 3;
    public const bool kIsToLower = true;
    public static int kMaxWidth => MaxWidth;
    public static int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching) =>
        UnicodeTables.ToLowercase_Convert(c, n, result, ref allowCaching);
}

public readonly struct ToUppercase : ICharMapping
{
    public const int MaxWidth = 3;
    public const bool kIsToLower = false;
    public static int kMaxWidth => MaxWidth;
    public static int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching) =>
        UnicodeTables.ToUppercase_Convert(c, n, result, ref allowCaching);
}

public readonly struct Ecma262Canonicalize : ICharMapping
{
    public const int MaxWidth = 1;
    public static int kMaxWidth => MaxWidth;
    public static int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching) =>
        UnicodeTables.Ecma262Canonicalize_Convert(c, n, result, ref allowCaching);
}

public readonly struct Ecma262UnCanonicalize : ICharMapping
{
    public const int MaxWidth = 4;
    public static int kMaxWidth => MaxWidth;
    public static int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching) =>
        UnicodeTables.Ecma262UnCanonicalize_Convert(c, n, result, ref allowCaching);
}

public readonly struct CanonicalizationRange : ICharMapping
{
    public const int MaxWidth = 1;
    public static int kMaxWidth => MaxWidth;
    public static int Convert(uint c, uint n, Span<uint> result, ref bool allowCaching) =>
        UnicodeTables.CanonicalizationRange_Convert(c, n, result, ref allowCaching);
}

/// <summary>A small direct-mapped cache in front of a predicate (unibrow::Predicate).</summary>
public sealed class Predicate<T> where T : ICharPredicate
{
    // CacheEntry: code point in bits 0..20, value in bit 21.
    const int kCodePointBits = 21;
    const uint kCodePointMask = (1u << kCodePointBits) - 1;
    readonly uint[] _entries;
    readonly int _mask;

    public Predicate(int size = 256)
    {
        _entries = new uint[size];
        _mask = size - 1;
    }

    public bool get(uint codePoint)
    {
        uint entry = _entries[(int)(codePoint & (uint)_mask)];
        if ((entry & kCodePointMask) == codePoint) return (entry >> kCodePointBits) != 0;
        return CalculateValue(codePoint);
    }

    bool CalculateValue(uint codePoint)
    {
        bool result = T.Is(codePoint);
        _entries[(int)(codePoint & (uint)_mask)] = (codePoint & kCodePointMask) | ((result ? 1u : 0u) << kCodePointBits);
        return result;
    }
}

/// <summary>A small direct-mapped cache in front of a case mapping (unibrow::Mapping).</summary>
public sealed class Mapping<T> where T : ICharMapping
{
    const uint kNoChar = (1 << 21) - 1;
    readonly uint[] _codePoints;
    readonly int[] _offsets;
    readonly int _mask;

    public Mapping(int size = 256)
    {
        _codePoints = new uint[size];
        _offsets = new int[size];
        _mask = size - 1;
        Array.Fill(_codePoints, kNoChar);
    }

    public int get(uint c, uint n, Span<uint> result)
    {
        int index = (int)(c & (uint)_mask);
        if (_codePoints[index] == c)
        {
            int offset = _offsets[index];
            if (offset == 0) return 0;
            result[0] = (uint)(c + offset);
            return 1;
        }
        return CalculateValue(c, n, result);
    }

    int CalculateValue(uint c, uint n, Span<uint> result)
    {
        bool allowCaching = true;
        int length = T.Convert(c, n, result, ref allowCaching);
        if (!allowCaching) return length;
        int index = (int)(c & (uint)_mask);
        if (length == 1)
        {
            _codePoints[index] = c;
            _offsets[index] = (int)(result[0] - c);
            return 1;
        }
        _codePoints[index] = c;
        _offsets[index] = 0;
        return 0;
    }
}

public static class Utf16
{
    public const int kNoPreviousCharacter = -1;
    public const uint kMaxNonSurrogateCharCode = 0xffff;
    // Encoding a single UTF-16 code unit will produce 1, 2 or 3 bytes
    // of UTF-8 data.  The special case where the unit is a surrogate
    // trail produces 1 byte net, because the encoding of the pair is
    // 4 bytes and the 3 bytes that were used to encode the lead surrogate
    // can be reclaimed.
    public const int kMaxExtraUtf8BytesForOneUtf16CodeUnit = 3;
    // One UTF-16 surrogate is encoded (illegally) as 3 UTF-8 bytes.
    // The illegality stems from the surrogate not being part of a pair.
    public const int kUtf8BytesToCodeASurrogate = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePair(int lead, int trail) => IsLeadSurrogate(lead) && IsTrailSurrogate(trail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeadSurrogate(int code) => (code & 0x1ffc00) == 0xd800;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrailSurrogate(int code) => (code & 0x1ffc00) == 0xdc00;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CombineSurrogatePair(uint lead, uint trail) => (int)(0x10000 + ((lead & 0x3ff) << 10) + (trail & 0x3ff));

    public static char LeadSurrogate(uint charCode) => (char)(0xd800 + (((charCode - 0x10000) >> 10) & 0x3ff));

    public static char TrailSurrogate(uint charCode) => (char)(0xdc00 + (charCode & 0x3ff));

    /// <summary>Whether the code units contain a lone surrogate.</summary>
    public static bool HasUnpairedSurrogate(ReadOnlySpan<char> codeUnits)
    {
        for (int i = 0; i < codeUnits.Length; i++)
        {
            char c = codeUnits[i];
            if (IsLeadSurrogate(c))
            {
                if (i + 1 < codeUnits.Length && IsTrailSurrogate(codeUnits[i + 1]))
                {
                    i++;
                    continue;
                }
                return true;
            }
            if (IsTrailSurrogate(c)) return true;
        }
        return false;
    }

    /// <summary>Copies source to dest, replacing lone surrogates with U+FFFD
    /// (String.prototype.toWellFormed).</summary>
    public static void ReplaceUnpairedSurrogates(ReadOnlySpan<char> source, Span<char> dest)
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (IsLeadSurrogate(c) && i + 1 < source.Length && IsTrailSurrogate(source[i + 1]))
            {
                dest[i] = c;
                dest[i + 1] = source[i + 1];
                i++;
            }
            else if (IsLeadSurrogate(c) || IsTrailSurrogate(c))
            {
                dest[i] = (char)Utf8.kBadChar;
            }
            else
            {
                dest[i] = c;
            }
        }
    }
}

public static class Latin1
{
    public const ushort kMaxChar = 0xff;
}

/// <summary>The UTF-8 DFA decoder of third_party/utf8-decoder/utf8-decoder.h
/// (Bjoern Hoehrmann's design).</summary>
public static class Utf8DfaDecoder
{
    public enum State : byte
    {
        kReject = 0,
        kAccept = 12,
        kTwoByte = 24,
        kThreeByte = 36,
        kThreeByteLowMid = 48,
        kFourByte = 60,
        kFourByteLow = 72,
        kThreeByteHigh = 84,
        kFourByteMidHigh = 96,
    }

    // This first table maps bytes to character to a transition.
    static ReadOnlySpan<byte> Transitions => [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 00-0F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 10-1F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 20-2F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 30-3F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 40-4F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 50-5F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 60-6F
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,  // 70-7F
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,  // 80-8F
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,  // 90-9F
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,  // A0-AF
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,  // B0-BF
        9, 9, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,  // C0-CF
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,  // D0-DF
        10, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 5, 5,  // E0-EF
        11, 7, 7, 7, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,  // F0-FF
    ];

    // This second table maps a state to a new state when adding a transition.
    static ReadOnlySpan<byte> States => [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,          // REJECT = 0
        12, 0, 0, 0, 24, 36, 48, 60, 72, 0, 84, 96,  // ACCEPT = 12
        0, 12, 12, 12, 0, 0, 0, 0, 0, 0, 0, 0,       // 2-byte = 24
        0, 24, 24, 24, 0, 0, 0, 0, 0, 0, 0, 0,       // 3-byte = 36
        0, 24, 24, 0, 0, 0, 0, 0, 0, 0, 0, 0,        // 3-byte low/mid = 48
        0, 36, 36, 36, 0, 0, 0, 0, 0, 0, 0, 0,       // 4-byte = 60
        0, 36, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,         // 4-byte low = 72
        0, 0, 0, 24, 0, 0, 0, 0, 0, 0, 0, 0,         // 3-byte high = 84
        0, 0, 36, 36, 0, 0, 0, 0, 0, 0, 0, 0,        // 4-byte mid/high = 96
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(byte b, ref State state, ref uint buffer)
    {
        byte type = Transitions[b];
        state = (State)States[(int)state + type];
        buffer = (buffer << 6) | (uint)(b & (0x7F >> (type >> 1)));
    }
}

public static class Utf8
{
    /// <summary>The unicode replacement character, used to signal invalid
    /// unicode sequences (e.g. an orphan surrogate) when converting to UTF-8.</summary>
    public const uint kBadChar = 0xFFFD;
    public const uint kBufferEmpty = 0x0;
    public const uint kIncomplete = 0xFFFFFFFC;  // any non-valid code point.
    public const int kMaxEncodedSize = 4;
    public const uint kMaxOneByteChar = 0x7f;
    public const uint kMaxTwoByteChar = 0x7ff;
    public const uint kMaxThreeByteChar = 0xffff;
    public const uint kMaxFourByteChar = 0x1fffff;

    // A single surrogate is coded as a 3 byte UTF-8 sequence, but two together
    // that match are coded as a 4 byte UTF-8 sequence.
    public const int kBytesSavedByCombiningSurrogates = 2;
    public const int kSizeOfUnmatchedSurrogate = 3;
    // The maximum size a single UTF-16 code unit may take up when encoded as
    // UTF-8.
    public const int kMax16BitCodeUnitSize = 3;
    // The maximum size a single UTF-16 code unit known to be in the range
    // [0,0xff] may take up when encoded as UTF-8.
    public const int kMax8BitCodeUnitSize = 2;

    public static int LengthOneByte(byte c) => c <= kMaxOneByteChar ? 1 : 2;

    public static int Length(uint c, int previous)
    {
        if (c <= kMaxOneByteChar) return 1;
        if (c <= kMaxTwoByteChar) return 2;
        if (c <= kMaxThreeByteChar)
        {
            if (Utf16.IsSurrogatePair(previous, (int)c)) return kSizeOfUnmatchedSurrogate - kBytesSavedByCombiningSurrogates;
            return 3;
        }
        return 4;
    }

    public static int EncodeOneByte(Span<byte> str, byte c)
    {
        const int kMask = ~(1 << 6);
        if (c <= kMaxOneByteChar)
        {
            str[0] = c;
            return 1;
        }
        str[0] = (byte)(0xC0 | (c >> 6));
        str[1] = (byte)(0x80 | (c & kMask));
        return 2;
    }

    /// <summary>
    /// Encodes c at str[pos..]. If c completes a surrogate pair with previous,
    /// the three bytes already written for previous (at pos-3) are replaced by
    /// the four-byte encoding and the return value is the net size (1).
    /// </summary>
    public static int Encode(Span<byte> str, int pos, uint c, int previous, bool replaceInvalid = false)
    {
        const int kMask = ~(1 << 6);
        if (c <= kMaxOneByteChar)
        {
            str[pos] = (byte)c;
            return 1;
        }
        if (c <= kMaxTwoByteChar)
        {
            str[pos] = (byte)(0xC0 | (c >> 6));
            str[pos + 1] = (byte)(0x80 | (c & kMask));
            return 2;
        }
        if (c <= kMaxThreeByteChar)
        {
            if (Utf16.IsSurrogatePair(previous, (int)c))
            {
                const int kUnmatchedSize = kSizeOfUnmatchedSurrogate;
                return Encode(str, pos - kUnmatchedSize, (uint)Utf16.CombineSurrogatePair((uint)previous, c),
                              Utf16.kNoPreviousCharacter, replaceInvalid) - kUnmatchedSize;
            }
            if (replaceInvalid && (Utf16.IsLeadSurrogate((int)c) || Utf16.IsTrailSurrogate((int)c))) c = kBadChar;
            str[pos] = (byte)(0xE0 | (c >> 12));
            str[pos + 1] = (byte)(0x80 | ((c >> 6) & kMask));
            str[pos + 2] = (byte)(0x80 | (c & kMask));
            return 3;
        }
        str[pos] = (byte)(0xF0 | (c >> 18));
        str[pos + 1] = (byte)(0x80 | ((c >> 12) & kMask));
        str[pos + 2] = (byte)(0x80 | ((c >> 6) & kMask));
        str[pos + 3] = (byte)(0x80 | (c & kMask));
        return 4;
    }

    /// <summary>Decodes one code point at bytes[cursor..]; advances cursor.</summary>
    public static uint ValueOf(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        if (cursor >= bytes.Length) return kBadChar;
        byte first = bytes[cursor];
        // Characters between 0000 and 007F are encoded as a single character
        if (first <= kMaxOneByteChar)
        {
            cursor += 1;
            return first;
        }
        return CalculateValue(bytes, ref cursor);
    }

    /// <summary>Decodes a UTF-8 value according to RFC 3629 and
    /// https://encoding.spec.whatwg.org/#utf-8-decoder .</summary>
    public static uint CalculateValue(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        Debug.Assert(bytes[cursor] > kMaxOneByteChar);
        Utf8DfaDecoder.State state = Utf8DfaDecoder.State.kAccept;
        uint buffer = 0;
        uint t;
        int str = cursor;
        do
        {
            t = ValueOfIncremental(bytes, ref str, ref state, ref buffer);
        } while (str < bytes.Length && t == kIncomplete);
        cursor = str;
        return state == Utf8DfaDecoder.State.kAccept ? t : kBadChar;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ValueOfIncremental(ReadOnlySpan<byte> bytes, ref int cursor, ref Utf8DfaDecoder.State state, ref uint buffer)
    {
        Utf8DfaDecoder.State oldState = state;
        byte next = bytes[cursor];
        cursor += 1;

        if (next <= kMaxOneByteChar && oldState == Utf8DfaDecoder.State.kAccept)
        {
            Debug.Assert(buffer == 0);
            return next;
        }

        // So we're at the lead byte of a 2/3/4 sequence, or we're at a continuation
        // char in that sequence.
        Utf8DfaDecoder.Decode(next, ref state, ref buffer);

        switch (state)
        {
            case Utf8DfaDecoder.State.kAccept:
            {
                uint t = buffer;
                buffer = 0;
                return t;
            }
            case Utf8DfaDecoder.State.kReject:
                state = Utf8DfaDecoder.State.kAccept;
                buffer = 0;
                // If we hit a bad byte, we need to determine if we were trying to start
                // a sequence or continue one. If we were trying to start a sequence,
                // that means it's just an invalid lead byte and we need to continue to
                // the next (which we already did above). If we were already in a
                // sequence, we need to reprocess this same byte after resetting to the
                // initial state.
                if (oldState != Utf8DfaDecoder.State.kAccept)
                {
                    // We were trying to continue a sequence, so let's reprocess this byte
                    // next time.
                    cursor -= 1;
                }
                return kBadChar;
            default:
                return kIncomplete;
        }
    }

    /// <summary>Finishes the incremental decoding, ensuring that if an
    /// unfinished sequence is left that it is replaced by a replacement char.</summary>
    public static uint ValueOfIncrementalFinish(ref Utf8DfaDecoder.State state)
    {
        if (state == Utf8DfaDecoder.State.kAccept) return kBufferEmpty;
        state = Utf8DfaDecoder.State.kAccept;
        return kBadChar;
    }

    /// <summary>Excludes non-characters from the set of valid code points.</summary>
    public static bool IsValidCharacter(uint c) =>
        c < 0xD800u || (c >= 0xE000u && c < 0xFDD0u) ||
        (c > 0xFDEFu && c <= 0x10FFFFu && (c & 0xFFFEu) != 0xFFFEu && c != kBadChar);

    /// <summary>
    /// Validates the input as UTF-8: valid encoding (e.g. no over-long
    /// encodings), no surrogates, valid code point range. Unlike JS source
    /// code this accepts any code point, including kBadChar and BOMs.
    /// </summary>
    public static bool ValidateEncoding(ReadOnlySpan<byte> bytes)
    {
        Utf8DfaDecoder.State state = Utf8DfaDecoder.State.kAccept;
        uint buffer = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            Utf8DfaDecoder.Decode(bytes[i], ref state, ref buffer);
            if (state == Utf8DfaDecoder.State.kReject) return false;
            if (state == Utf8DfaDecoder.State.kAccept) buffer = 0;
        }
        return state == Utf8DfaDecoder.State.kAccept;
    }

    /// <summary>Copies the leading ASCII characters of src to dest; returns
    /// how many were copied.</summary>
    public static int WriteLeadingAscii(ReadOnlySpan<char> src, Span<byte> dest)
    {
        int n = Math.Min(src.Length, dest.Length);
        int i = 0;
        for (; i < n; i++)
        {
            char c = src[i];
            if (c > kMaxOneByteChar) break;
            dest[i] = (byte)c;
        }
        return i;
    }

    /// <summary>
    /// Encodes the UTF-16 string as UTF-8 into buffer, stopping (without
    /// splitting a surrogate pair) when the capacity runs out. Returns the
    /// bytes written and the characters processed.
    /// </summary>
    public static (int bytesWritten, int charactersProcessed) Encode(ReadOnlySpan<char> str, Span<byte> buffer,
                                                                    bool writeNull, bool replaceInvalidUtf8)
    {
        int capacity = buffer.Length;
        int writeIndex = 0;
        int contentCapacity = capacity - (writeNull ? 1 : 0);
        if (contentCapacity > capacity) throw new ArgumentException("capacity");
        int readIndex = 0;
        int last = Utf16.kNoPreviousCharacter;
        for (; readIndex < str.Length; readIndex++)
        {
            char character = str[readIndex];
            int requiredCapacity = Length(character, last);
            int remainingCapacity = contentCapacity - writeIndex;
            if (remainingCapacity < requiredCapacity)
            {
                // Not enough space left, so stop here.
                if (Utf16.IsSurrogatePair(last, character))
                {
                    Debug.Assert(writeIndex >= kSizeOfUnmatchedSurrogate);
                    // We're in the middle of a surrogate pair. Delete the first part again.
                    writeIndex -= kSizeOfUnmatchedSurrogate;
                    // We've already read at least one character which is a lead surrogate
                    --readIndex;
                }
                break;
            }

            // Handle the case where we cut off in the middle of a surrogate pair.
            if (readIndex + 1 < str.Length && Utf16.IsSurrogatePair(character, str[readIndex + 1]))
            {
                writeIndex += kSizeOfUnmatchedSurrogate;
            }
            else
            {
                writeIndex += Encode(buffer, writeIndex, character, last, replaceInvalidUtf8);
            }

            last = character;
        }

        if (writeNull) buffer[writeIndex++] = 0;
        return (writeIndex, readIndex);
    }
}

static partial class UnicodeTables
{
    const int kStartBit = 1 << 30;
    const int kChunkBits = 1 << 13;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint GetEntry(int entry) => (uint)(entry & (kStartBit - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsStart(int entry) => (entry & kStartBit) != 0;

    /// <summary>
    /// Look up a character in the Unicode table using a mix of binary and
    /// interpolation search. The average number of steps to look up the
    /// information about a character is around 10, slightly higher if there
    /// is no information available about the character.
    /// </summary>
    static bool LookupPredicate(ReadOnlySpan<int> table, ushort size, uint chr)
    {
        const int kEntryDist = 1;
        uint value = chr & (kChunkBits - 1);
        uint low = 0;
        uint high = (uint)size - 1;
        while (high != low)
        {
            uint mid = low + ((high - low) >> 1);
            uint currentValue = GetEntry(table[(int)(kEntryDist * mid)]);
            // If we've found an entry less than or equal to this one, and the
            // next one is not also less than this one, we've arrived.
            if ((currentValue <= value) &&
                (mid + 1 == size || GetEntry(table[(int)(kEntryDist * (mid + 1))]) > value))
            {
                low = mid;
                break;
            }
            else if (currentValue < value)
            {
                low = mid + 1;
            }
            else if (currentValue > value)
            {
                // If we've just checked the bottom-most value and it's not
                // the one we're looking for, we're done.
                if (mid == 0) break;
                high = mid - 1;
            }
        }
        int field = table[(int)(kEntryDist * low)];
        uint entry = GetEntry(field);
        bool isStart = IsStart(field);
        return (entry == value) || (entry < value && isStart);
    }

    // Look up the mapping for the given character in the specified table,
    // which is of the specified length and uses the specified special case
    // mapping for multi-char mappings.  The next parameter is the character
    // following the one to map.  The result will be written in to the result
    // buffer and the number of characters written will be returned.  Finally,
    // allowCaching is set to false if the result contains multiple characters
    // or depends on the context.
    // If ranges are linear, a match between a start and end point is
    // offset by the distance between the match and the start. Otherwise
    // the result is the same as for the start point on the entire range.
    static int LookupMapping(bool rangesAreLinear, ReadOnlySpan<int> table, ushort size, ReadOnlySpan<uint> multiChars,
                             int kW, uint chr, uint next, Span<uint> result, ref bool allowCaching)
    {
        const int kEntryDist = 2;
        ushort key = (ushort)(chr & (kChunkBits - 1));
        ushort chunkStart = (ushort)(chr - key);
        uint low = 0;
        uint high = (uint)size - 1;
        while (high != low)
        {
            uint mid = low + ((high - low) >> 1);
            uint currentValue = GetEntry(table[(int)(kEntryDist * mid)]);
            // If we've found an entry less than or equal to this one, and the next one
            // is not also less than this one, we've arrived.
            if ((currentValue <= key) &&
                (mid + 1 == size || GetEntry(table[(int)(kEntryDist * (mid + 1))]) > key))
            {
                low = mid;
                break;
            }
            else if (currentValue < key)
            {
                low = mid + 1;
            }
            else if (currentValue > key)
            {
                // If we've just checked the bottom-most value and it's not
                // the one we're looking for, we're done.
                if (mid == 0) break;
                high = mid - 1;
            }
        }
        int field = table[(int)(kEntryDist * low)];
        uint entry = GetEntry(field);
        bool isStart = IsStart(field);
        bool found = (entry == key) || (entry < key && isStart);
        if (!found) return 0;
        int value = table[(int)(2 * low + 1)];
        if (value == 0)
        {
            // 0 means not present
            return 0;
        }
        if ((value & 3) == 0)
        {
            // Low bits 0 means a constant offset from the given character.
            if (rangesAreLinear)
            {
                result[0] = (uint)(chr + (value >> 2));
            }
            else
            {
                result[0] = (uint)(entry + chunkStart + (value >> 2));
            }
            return 1;
        }
        if ((value & 3) == 1)
        {
            // Low bits 1 means a special case mapping
            allowCaching = false;
            ReadOnlySpan<uint> mapping = multiChars.Slice((value >> 2) * kW, kW);
            int length;
            for (length = 0; length < kW; length++)
            {
                uint mapped = mapping[length];
                if (mapped == kSentinel) break;
                if (rangesAreLinear)
                {
                    result[length] = mapped + (key - entry);
                }
                else
                {
                    result[length] = mapped;
                }
            }
            return length;
        }
        // Low bits 2 means a really really special case
        allowCaching = false;
        // The cases of this switch are defined in unicode.py in the
        // really_special_cases mapping.
        switch (value >> 2)
        {
            case 1:
                // Really special case 1: upper case sigma.  This letter
                // converts to two different lower case sigmas depending on
                // whether or not it occurs at the end of a word.
                if (next != 0 && Letter.Is(next))
                {
                    result[0] = 0x03C3;
                }
                else
                {
                    result[0] = 0x03C2;
                }
                return 1;
            default:
                return 0;
        }
    }
}
