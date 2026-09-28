// Port of src/strings/uri.{h,cc}: Uri::Decode/Encode (decodeURI[Component],
// encodeURI[Component]) and Uri::Escape/Unescape (Annex B escape/unescape).
//
// V8 builds one-byte and two-byte buffers and picks the string representation
// from them; V8Sharp strings are always UTF-16, so each operation writes one
// char buffer. The results are the same strings.
using System.Buffers;
using BaseCharPredicates = V8Sharp.Base.Strings.CharPredicates;
using V8Sharp.Base.Unibrow;

namespace V8Sharp.Builtins;

/// <summary>V8's Uri (src/strings/uri.h).</summary>
public static class Uri
{
    /// <summary>ES5 section 15.1.3.1 decodeURI.</summary>
    public static JSString DecodeUri(Isolate isolate, JSString uri) => Decode(isolate, uri, true);

    /// <summary>ES5 section 15.1.3.2 decodeURIComponent.</summary>
    public static JSString DecodeUriComponent(Isolate isolate, JSString component) => Decode(isolate, component, false);

    /// <summary>ES5 section 15.1.3.3 encodeURI.</summary>
    public static JSString EncodeUri(Isolate isolate, JSString uri) => Encode(isolate, uri, true);

    /// <summary>ES5 section 15.1.3.4 encodeURIComponent.</summary>
    public static JSString EncodeUriComponent(Isolate isolate, JSString component) => Encode(isolate, component, false);

    // ---------------------------------------------------------------------
    // DecodeURI helper functions

    static bool IsReservedPredicate(char c) => c switch
    {
        '#' or '$' or '&' or '+' or ',' or '/' or ':' or ';' or '=' or '?' or '@' => true,
        _ => false,
    };

    static bool IsReplacementCharacter(scoped ReadOnlySpan<byte> octets)
    {
        // The replacement character is at codepoint U+FFFD in the Unicode Specials
        // table. Its UTF-8 encoding is 0xEF 0xBF 0xBD.
        return octets.Length == 3 && octets[0] == 0xEF && octets[1] == 0xBF && octets[2] == 0xBD;
    }

    static bool DecodeOctets(scoped ReadOnlySpan<byte> octets, ref CharBuffer buffer)
    {
        int cursor = 0;
        uint value = Utf8.ValueOf(octets, ref cursor);
        if (value == Utf8.kBadChar && !IsReplacementCharacter(octets)) return false;

        if (value <= Utf16.kMaxNonSurrogateCharCode)
        {
            buffer.Add((char)value);
        }
        else
        {
            buffer.Add(Utf16.LeadSurrogate(value));
            buffer.Add(Utf16.TrailSurrogate(value));
        }
        return true;
    }

    /// <summary>base::HexValue: the value of a hex digit, or -1.</summary>
    static int HexValue(int c)
    {
        c -= '0';
        if ((uint)c <= 9) return c;
        c = (c | 0x20) - ('a' - '0');  // detect 0x11..0x16 and 0x31..0x36.
        if ((uint)c <= 5) return c + 10;
        return -1;
    }

    static int TwoDigitHex(char character1, char character2)
    {
        if (character1 > 'f') return -1;
        int high = HexValue(character1);
        if (high == -1) return -1;
        if (character2 > 'f') return -1;
        int low = HexValue(character2);
        if (low == -1) return -1;
        return (high << 4) + low;
    }

    static void AddToBuffer(char decoded, ReadOnlySpan<char> uriContent, int index, bool isUri, ref CharBuffer buffer)
    {
        if (isUri && IsReservedPredicate(decoded))
        {
            buffer.Add('%');
            buffer.Add(uriContent[index + 1]);
            buffer.Add(uriContent[index + 2]);
        }
        else
        {
            buffer.Add(decoded);
        }
    }

    /// <summary>IntoOneAndTwoByte and IntoTwoByte, over one buffer.</summary>
    static bool DecodeInto(ReadOnlySpan<char> uriContent, bool isUri, ref CharBuffer buffer)
    {
        int uriLength = uriContent.Length;
        Span<byte> octets = stackalloc byte[Utf8.kMaxEncodedSize];
        for (int k = 0; k < uriLength; k++)
        {
            char code = uriContent[k];
            if (code == '%')
            {
                int twoDigits;
                if (k + 2 >= uriLength || (twoDigits = TwoDigitHex(uriContent[k + 1], uriContent[k + 2])) < 0)
                {
                    return false;
                }
                k += 2;
                char decoded = (char)twoDigits;
                if (decoded > Utf8.kMaxOneByteChar)
                {
                    octets[0] = (byte)decoded;

                    int numberOfContinuationBytes = 0;
                    while (((decoded << ++numberOfContinuationBytes) & 0x80) != 0)
                    {
                        if (numberOfContinuationBytes > 3 || k + 3 >= uriLength) return false;
                        if (uriContent[++k] != '%' || (twoDigits = TwoDigitHex(uriContent[k + 1], uriContent[k + 2])) < 0)
                        {
                            return false;
                        }
                        k += 2;
                        octets[numberOfContinuationBytes] = (byte)twoDigits;
                    }

                    if (!DecodeOctets(octets[..numberOfContinuationBytes], ref buffer)) return false;
                }
                else
                {
                    AddToBuffer(decoded, uriContent, k - 2, isUri, ref buffer);
                }
            }
            else
            {
                buffer.Add(code);
            }
        }
        return true;
    }

    /// <summary>Uri::Decode.</summary>
    public static JSString Decode(Isolate isolate, JSString uri, bool isUri)
    {
        ReadOnlySpan<char> content = uri.Flatten();
        var buffer = new CharBuffer(content.Length);
        try
        {
            if (!DecodeInto(content, isUri, ref buffer))
            {
                isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.UriErrorFunction, MessageTemplate.URIMalformed, []));
            }
            return isolate.Factory.NewStringFromUtf16(buffer.Span);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Encode

    static bool IsUnescapePredicateInUriComponent(char c)
    {
        if (BaseCharPredicates.IsAlphaNumeric(c)) return true;
        return c switch
        {
            '!' or '\'' or '(' or ')' or '*' or '-' or '.' or '_' or '~' => true,
            _ => false,
        };
    }

    static bool IsUriSeparator(char c) => c switch
    {
        '#' or ':' or ';' or '/' or '?' or '$' or '&' or '+' or ',' or '@' or '=' => true,
        _ => false,
    };

    /// <summary>base::HexCharOfValue (upper case).</summary>
    static char HexCharOfValue(int value) => value < 10 ? (char)(value + '0') : (char)(value - 10 + 'A');

    static bool AddEncodedOctetToBuffer(byte octet, ref CharBuffer buffer) =>
        buffer.TryAdd('%') && buffer.TryAdd(HexCharOfValue(octet >> 4)) && buffer.TryAdd(HexCharOfValue(octet & 0x0F));

    static bool EncodeCodePoint(uint c, ref CharBuffer buffer)
    {
        Span<byte> s = stackalloc byte[4];
        int numberOfBytes = Utf8.Encode(s, 0, c, Utf16.kNoPreviousCharacter, false);
        for (int k = 0; k < numberOfBytes; k++)
        {
            if (!AddEncodedOctetToBuffer(s[k], ref buffer)) return false;
        }
        return true;
    }

    enum EncodeStatus { Success, UriError, AllocationFailure }

    /// <summary>EncodeHelperTwoByte (the one-byte helper is its restriction to Latin-1).</summary>
    static EncodeStatus EncodeHelper(ReadOnlySpan<char> uriContent, bool isUri, ref CharBuffer buffer)
    {
        for (int k = 0; k < uriContent.Length; k++)
        {
            char cc1 = uriContent[k];
            if (Utf16.IsLeadSurrogate(cc1))
            {
                k++;
                if (k >= uriContent.Length)
                {
                    // A lead surrogate was found without a tail surrogate.
                    return EncodeStatus.UriError;
                }
                char cc2 = uriContent[k];
                if (!Utf16.IsTrailSurrogate(cc2))
                {
                    // A lead surrogate was found without a tail surrogate.
                    return EncodeStatus.UriError;
                }

                if (!EncodeCodePoint((uint)Utf16.CombineSurrogatePair(cc1, cc2), ref buffer)) return EncodeStatus.AllocationFailure;
                continue;
            }

            if (Utf16.IsTrailSurrogate(cc1))
            {
                // A tail surrogate was found without a preceding lead surrogate.
                return EncodeStatus.UriError;
            }

            if (IsUnescapePredicateInUriComponent(cc1) || (isUri && IsUriSeparator(cc1)))
            {
                if (!buffer.TryAdd(cc1)) return EncodeStatus.AllocationFailure;
            }
            else
            {
                if (!EncodeCodePoint(cc1, ref buffer)) return EncodeStatus.AllocationFailure;
            }
        }
        return EncodeStatus.Success;
    }

    /// <summary>Uri::Encode.</summary>
    public static JSString Encode(Isolate isolate, JSString uri, bool isUri)
    {
        ReadOnlySpan<char> content = uri.Flatten();
        var buffer = new CharBuffer(content.Length, JSString.kMaxLength);
        try
        {
            EncodeStatus status = EncodeHelper(content, isUri, ref buffer);
            switch (status)
            {
                case EncodeStatus.Success:
                    return isolate.Factory.NewStringFromUtf16(buffer.Span);
                case EncodeStatus.UriError:
                    isolate.Throw(isolate.Factory.NewError(isolate.NativeContext.UriErrorFunction, MessageTemplate.URIMalformed, []));
                    break;
                case EncodeStatus.AllocationFailure:
                    isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
                    break;
            }
            throw new InvalidOperationException("unreachable");
        }
        finally
        {
            buffer.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Escape and Unescape

    static int UnescapeChar(ReadOnlySpan<char> vector, int i, int length, out int step)
    {
        char character = vector[i];
        int hi;
        int lo;
        if (character == '%' && i <= length - 6 && vector[i + 1] == 'u' &&
            (hi = TwoDigitHex(vector[i + 2], vector[i + 3])) > -1 &&
            (lo = TwoDigitHex(vector[i + 4], vector[i + 5])) > -1)
        {
            step = 6;
            return (hi << 8) + lo;
        }
        if (character == '%' && i <= length - 3 && (lo = TwoDigitHex(vector[i + 1], vector[i + 2])) > -1)
        {
            step = 3;
            return lo;
        }
        step = 1;
        return character;
    }

    static bool IsNotEscaped(char c)
    {
        if (BaseCharPredicates.IsAlphaNumeric(c)) return true;
        //  @*_+-./
        return c switch
        {
            '@' or '*' or '_' or '+' or '-' or '.' or '/' => true,
            _ => false,
        };
    }

    /// <summary>ES6 section B.2.1.1 escape (Uri::Escape / EscapePrivate).</summary>
    public static JSString Escape(Isolate isolate, JSString str)
    {
        ReadOnlySpan<char> vector = str.Flatten();
        int length = vector.Length;
        long escapedLength = 0;
        for (int i = 0; i < length; i++)
        {
            char c = vector[i];
            if (c >= 256) escapedLength += 6;
            else if (IsNotEscaped(c)) escapedLength++;
            else escapedLength += 3;

            // We don't allow strings that are longer than a maximal length.
            if (escapedLength > JSString.kMaxLength) break;  // Provoke exception.
        }

        // No length change implies no change.  Return original string if no change.
        if (escapedLength == length) return str;
        if (escapedLength > JSString.kMaxLength) isolate.Throw(isolate.Factory.NewInvalidStringLengthError());

        return isolate.Factory.NewStringFromUtf16(string.Create((int)escapedLength, vector, static (dest, source) =>
        {
            int destPosition = 0;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c >= 256)
                {
                    dest[destPosition] = '%';
                    dest[destPosition + 1] = 'u';
                    dest[destPosition + 2] = HexCharOfValue(c >> 12);
                    dest[destPosition + 3] = HexCharOfValue((c >> 8) & 0xF);
                    dest[destPosition + 4] = HexCharOfValue((c >> 4) & 0xF);
                    dest[destPosition + 5] = HexCharOfValue(c & 0xF);
                    destPosition += 6;
                }
                else if (IsNotEscaped(c))
                {
                    dest[destPosition] = c;
                    destPosition++;
                }
                else
                {
                    dest[destPosition] = '%';
                    dest[destPosition + 1] = HexCharOfValue(c >> 4);
                    dest[destPosition + 2] = HexCharOfValue(c & 0xF);
                    destPosition += 3;
                }
            }
        }));
    }

    /// <summary>ES6 section B.2.1.2 unescape (Uri::Unescape / UnescapePrivate / UnescapeSlow).</summary>
    public static JSString Unescape(Isolate isolate, JSString str)
    {
        ReadOnlySpan<char> vector = str.Flatten();
        int startIndex = vector.IndexOf('%');
        if (startIndex < 0) return str;

        int length = vector.Length;
        int unescapedLength = 0;
        for (int i = startIndex; i < length; unescapedLength++)
        {
            UnescapeChar(vector, i, length, out int step);
            i += step;
        }

        JSString firstPart = isolate.Factory.NewProperSubString(str, 0, startIndex);
        JSString secondPart = isolate.Factory.NewStringFromUtf16(string.Create(unescapedLength, vector[startIndex..], static (dest, source) =>
        {
            int destPosition = 0;
            for (int i = 0; i < source.Length; destPosition++)
            {
                dest[destPosition] = (char)UnescapeChar(source, i, source.Length, out int step);
                i += step;
            }
        }));
        return isolate.Factory.NewConsString(firstPart, secondPart);
    }

    /// <summary>
    /// A growable char buffer over ArrayPool storage (V8's std::vector and
    /// ResizableBuffer); TryAdd fails past <c>maxCapacity</c>.
    /// </summary>
    ref struct CharBuffer
    {
        char[] _data;
        int _size;
        readonly int _maxCapacity;

        public CharBuffer(int initialCapacity, int maxCapacity = int.MaxValue)
        {
            _data = ArrayPool<char>.Shared.Rent(Math.Max(initialCapacity, 16));
            _size = 0;
            _maxCapacity = maxCapacity;
        }

        public readonly ReadOnlySpan<char> Span => _data.AsSpan(0, _size);

        public void Add(char c)
        {
            if (_size == _data.Length) Grow(1);
            _data[_size++] = c;
        }

        public bool TryAdd(char c)
        {
            if (_size + 1 > _maxCapacity) return false;
            Add(c);
            return true;
        }

        void Grow(int required)
        {
            int newCapacity = (int)Math.Min((long)_data.Length * 2 + required, Array.MaxLength);
            char[] newData = ArrayPool<char>.Shared.Rent(newCapacity);
            _data.AsSpan(0, _size).CopyTo(newData);
            ArrayPool<char>.Shared.Return(_data);
            _data = newData;
        }

        public void Dispose()
        {
            ArrayPool<char>.Shared.Return(_data);
            _data = [];
        }
    }
}
