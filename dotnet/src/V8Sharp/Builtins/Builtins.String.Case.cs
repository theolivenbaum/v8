// Port of the !V8_INTL_SUPPORT case conversion of src/builtins/builtins-string.cc
// (ConvertCase, ConvertCaseHelper) and the ASCII fast path of
// src/strings/string-case.cc (FastAsciiConvert).
//
// V8 without ICU maps each UTF-16 code unit through the unibrow tables, with
// the next code unit as context (for the final sigma); supplementary code
// points are not case mapped. toLocaleLowerCase/toLocaleUpperCase ignore the
// locale.
using System.Buffers;
using System.Text;
using V8Sharp.Base.Unibrow;

namespace V8Sharp.Builtins;

public static partial class BuiltinsString
{
    // Isolate::runtime_state()->to_lower_mapping() / to_upper_mapping(): the
    // mapping caches. They are mutable, so each thread gets its own.
    [ThreadStatic] static Mapping<ToLowercase>? t_toLowerMapping;
    [ThreadStatic] static Mapping<ToUppercase>? t_toUpperMapping;

    static Mapping<ToLowercase> ToLowerMapping => t_toLowerMapping ??= new Mapping<ToLowercase>(128);
    static Mapping<ToUppercase> ToUpperMapping => t_toUpperMapping ??= new Mapping<ToUppercase>(128);

    public static JSValue StringPrototypeToLocaleLowerCase(Isolate isolate, in BuiltinArguments args) =>
        ConvertCase(isolate, ToThisString(isolate, args.Receiver, "String.prototype.toLocaleLowerCase"), ToLowerMapping, true);

    public static JSValue StringPrototypeToLocaleUpperCase(Isolate isolate, in BuiltinArguments args) =>
        ConvertCase(isolate, ToThisString(isolate, args.Receiver, "String.prototype.toLocaleUpperCase"), ToUpperMapping, false);

    public static JSValue StringPrototypeToLowerCase(Isolate isolate, in BuiltinArguments args) =>
        ConvertCase(isolate, ToThisString(isolate, args.Receiver, "String.prototype.toLowerCase"), ToLowerMapping, true);

    public static JSValue StringPrototypeToUpperCase(Isolate isolate, in BuiltinArguments args) =>
        ConvertCase(isolate, ToThisString(isolate, args.Receiver, "String.prototype.toUpperCase"), ToUpperMapping, false);

    /// <summary>ConvertCase: returns the string itself when no character changes.</summary>
    internal static JSString ConvertCase<TConverter>(Isolate isolate, JSString s, Mapping<TConverter> mapping, bool toLower)
        where TConverter : ICharMapping
    {
        ReadOnlySpan<char> src = s.FlatSpan();
        int length = src.Length;
        if (length == 0) return s;

        // Simpler handling of ASCII strings (FastAsciiCasePrefixLength +
        // FastAsciiConvert): the upper/lower case of an ASCII character is ASCII.
        int firstNonAscii = src.IndexOfAnyExceptInRange('\0', '\u007f');
        int firstChange = toLower ? src.IndexOfAnyInRange('A', 'Z') : src.IndexOfAnyInRange('a', 'z');
        if (firstNonAscii < 0)
        {
            if (firstChange < 0) return s;
            string ascii = string.Create(length, s, toLower
                ? static (span, str) => Ascii.ToLower(str.FlatSpan(), span, out _)
                : static (span, str) => Ascii.ToUpper(str.FlatSpan(), span, out _));
            return new SeqString(ascii);
        }

        // The prefix before the first character that changes or is not ASCII is
        // copied unchanged.
        int prefix = firstChange < 0 ? firstNonAscii : Math.Min(firstChange, firstNonAscii);
        char[] buffer = ArrayPool<char>.Shared.Rent(Math.Max(length, 16));
        try
        {
            src[..prefix].CopyTo(buffer);
            int written = prefix;
            bool hasChangedCharacter = false;
            Span<uint> chars = stackalloc uint[Unicode.kMaxMappingSize];
            for (int i = prefix; i < length; i++)
            {
                uint current = src[i];
                uint next = i + 1 < length ? src[i + 1] : 0u;
                int charLength = mapping.get(current, next, chars);
                if (charLength == 0)
                {
                    // The case conversion of this character is the character itself.
                    EnsureCaseBuffer(isolate, ref buffer, written, 1);
                    buffer[written++] = (char)current;
                    continue;
                }
                EnsureCaseBuffer(isolate, ref buffer, written, charLength);
                for (int j = 0; j < charLength; j++) buffer[written++] = (char)chars[j];
                hasChangedCharacter = true;
            }
            // If we didn't actually change anything in doing the conversion
            // we simple return the original string; there is no reason to keep
            // two identical strings alive.
            if (!hasChangedCharacter) return s;
            return new SeqString(new string(buffer, 0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    static void EnsureCaseBuffer(Isolate isolate, ref char[] buffer, int written, int additional)
    {
        int needed = written + additional;
        // The buffer grows by doubling, so it can exceed String::kMaxLength:
        // check the result length before the capacity.
        if (needed > JSString.kMaxLength) isolate.Throw(isolate.Factory.NewInvalidStringLengthError());
        if (needed <= buffer.Length) return;
        char[] next = ArrayPool<char>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
        buffer.AsSpan(0, written).CopyTo(next);
        ArrayPool<char>.Shared.Return(buffer);
        buffer = next;
    }
}
