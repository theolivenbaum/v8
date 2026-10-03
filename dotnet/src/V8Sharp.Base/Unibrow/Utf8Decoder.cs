// Port of src/strings/unicode-decoder.h and unicode-decoder.cc: the lossy
// Utf8Decoder (invalid sequences decode to U+FFFD). The WebAssembly-only
// Wtf8Decoder and StrictUtf8Decoder are not ported.

namespace V8Sharp.Base.Unibrow;

/// <summary>
/// Measures UTF-8 input on construction (length in UTF-16 code units, whether
/// it is ASCII / Latin-1) and decodes it with <see cref="Decode(Span{char}, ReadOnlySpan{byte})"/>.
/// This decoder never fails; an invalid byte sequence decodes to U+FFFD and
/// then the decode continues.
/// </summary>
public readonly struct Utf8Decoder
{
    public enum Encoding : byte { kAscii, kLatin1, kUtf16, kInvalid }

    readonly Encoding _encoding;
    readonly int _nonAsciiStart;
    readonly int _utf16Length;

    public bool is_invalid => false;
    public bool is_ascii => _encoding == Encoding.kAscii;
    public bool is_one_byte => _encoding <= Encoding.kLatin1;
    public int utf16_length => _utf16Length;
    public int non_ascii_start => _nonAsciiStart;

    /// <summary>Index of the first byte above 0x7F (or data.Length).</summary>
    public static int NonAsciiStart(ReadOnlySpan<byte> chars)
    {
        int i = chars.IndexOfAnyInRange((byte)0x80, (byte)0xFF);
        return i < 0 ? chars.Length : i;
    }

    public Utf8Decoder(ReadOnlySpan<byte> data)
    {
        _encoding = Encoding.kAscii;
        _nonAsciiStart = NonAsciiStart(data);
        _utf16Length = _nonAsciiStart;
        if (_nonAsciiStart == data.Length) return;

        bool isOneByte = true;
        Utf8DfaDecoder.State state = Utf8DfaDecoder.State.kAccept;
        uint current = 0;
        int cursor = _nonAsciiStart;
        int end = data.Length;

        while (cursor < end)
        {
            if (data[cursor] <= Utf8.kMaxOneByteChar && state == Utf8DfaDecoder.State.kAccept)
            {
                Debug.Assert(current == 0);
                _utf16Length++;
                cursor++;
                continue;
            }

            Utf8DfaDecoder.State previousState = state;
            Utf8DfaDecoder.Decode(data[cursor], ref state, ref current);
            if (state < Utf8DfaDecoder.State.kAccept)
            {
                Debug.Assert(state == Utf8DfaDecoder.State.kReject);
                // kAllowIncompleteSequences
                state = Utf8DfaDecoder.State.kAccept;
                isOneByte = false;
                _utf16Length++;
                current = 0;
                // If we were trying to continue a multibyte sequence, try this byte
                // again.
                if (previousState != Utf8DfaDecoder.State.kAccept) continue;
            }
            else if (state == Utf8DfaDecoder.State.kAccept)
            {
                // The DfaDecoder will only ever decode Unicode scalar values, and all
                // sequences of USVs are valid.
                isOneByte = isOneByte && current <= Latin1.kMaxChar;
                _utf16Length++;
                if (current > Utf16.kMaxNonSurrogateCharCode) _utf16Length++;
                current = 0;
            }
            cursor++;
        }

        if (state == Utf8DfaDecoder.State.kAccept)
        {
            _encoding = isOneByte ? Encoding.kLatin1 : Encoding.kUtf16;
        }
        else
        {
            _encoding = Encoding.kUtf16;
            _utf16Length++;
        }
    }

    /// <summary>Decodes data (the same bytes given to the constructor) into
    /// out, which must hold utf16_length characters.</summary>
    public void Decode(Span<char> @out, ReadOnlySpan<byte> data)
    {
        int o = 0;
        for (; o < _nonAsciiStart; o++) @out[o] = (char)data[o];

        Utf8DfaDecoder.State state = Utf8DfaDecoder.State.kAccept;
        uint current = 0;
        int cursor = _nonAsciiStart;
        int end = data.Length;

        while (cursor < end)
        {
            if (data[cursor] <= Utf8.kMaxOneByteChar && state == Utf8DfaDecoder.State.kAccept)
            {
                Debug.Assert(current == 0);
                @out[o++] = (char)data[cursor];
                cursor++;
                continue;
            }

            Utf8DfaDecoder.State previousState = state;
            Utf8DfaDecoder.Decode(data[cursor], ref state, ref current);
            if (state < Utf8DfaDecoder.State.kAccept)
            {
                state = Utf8DfaDecoder.State.kAccept;
                @out[o++] = (char)Utf8.kBadChar;
                current = 0;
                // If we were trying to continue a multibyte sequence, try this byte
                // again.
                if (previousState != Utf8DfaDecoder.State.kAccept) continue;
            }
            else if (state == Utf8DfaDecoder.State.kAccept)
            {
                if (current <= Utf16.kMaxNonSurrogateCharCode)
                {
                    @out[o++] = (char)current;
                }
                else
                {
                    @out[o++] = Utf16.LeadSurrogate(current);
                    @out[o++] = Utf16.TrailSurrogate(current);
                }
                current = 0;
            }
            cursor++;
        }

        if (state != Utf8DfaDecoder.State.kAccept) @out[o] = (char)Utf8.kBadChar;
    }

    /// <summary>Decodes into a one-byte (Latin-1) buffer; only valid when is_one_byte.</summary>
    public void Decode(Span<byte> @out, ReadOnlySpan<byte> data)
    {
        Debug.Assert(is_one_byte);
        Span<char> tmp = _utf16Length <= 256 ? stackalloc char[_utf16Length] : new char[_utf16Length];
        Decode(tmp, data);
        for (int i = 0; i < tmp.Length; i++) @out[i] = (byte)tmp[i];
    }

    /// <summary>Convenience: decodes UTF-8 bytes to a string, lossily.</summary>
    public static string DecodeToString(ReadOnlySpan<byte> data)
    {
        Utf8Decoder decoder = new(data);
        if (decoder._utf16Length == 0) return string.Empty;
        char[] buffer = new char[decoder._utf16Length];
        decoder.Decode(buffer, data);
        return new string(buffer);
    }
}
