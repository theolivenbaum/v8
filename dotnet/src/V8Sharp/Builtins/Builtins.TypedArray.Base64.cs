// Port of the Uint8Array base64/hex builtins of src/builtins/builtins-typed-array.cc
// (Uint8ArrayFromBase64, Uint8ArrayPrototypeSetFromBase64,
// Uint8ArrayPrototypeToBase64, Uint8ArrayFromHex, Uint8ArrayPrototypeSetFromHex,
// Uint8ArrayPrototypeToHex) and of ArrayBufferFromHex / Uint8ArrayToHex
// (src/objects/simd.cc).
//
// V8 decodes base64 with simdutf (third_party/simdutf, not in this checkout).
// V8Sharp implements the proposal's FromBase64 algorithm directly, choosing
// V8's error messages (simdutf's error codes) for each error step; see
// deviations.md for the trailing-garbage cases where simdutf reads further
// than the specification.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterTypedArrayBase64()
    {
        Register(Builtin.Uint8ArrayFromBase64, BuiltinsTypedArray.Uint8ArrayFromBase64);
        Register(Builtin.Uint8ArrayPrototypeSetFromBase64, BuiltinsTypedArray.Uint8ArrayPrototypeSetFromBase64);
        Register(Builtin.Uint8ArrayPrototypeToBase64, BuiltinsTypedArray.Uint8ArrayPrototypeToBase64);
        Register(Builtin.Uint8ArrayFromHex, BuiltinsTypedArray.Uint8ArrayFromHex);
        Register(Builtin.Uint8ArrayPrototypeSetFromHex, BuiltinsTypedArray.Uint8ArrayPrototypeSetFromHex);
        Register(Builtin.Uint8ArrayPrototypeToHex, BuiltinsTypedArray.Uint8ArrayPrototypeToHex);
    }
}

public static partial class BuiltinsTypedArray
{
    enum Base64Alphabet { Base64, Base64Url }

    enum LastChunkHandling { Loose, Strict, StopBeforePartial }

    /// <summary>GetOptionsObject (option-utils.cc).</summary>
    static JSValue GetOptionsObject(Isolate isolate, JSValue options)
    {
        // 1. If options is undefined, then
        //   a. Return ! ObjectCreate(null).
        if (options.IsUndefined) return isolate.Factory.NewJSObjectWithNullProto();
        // 2. If Type(options) is Object, then
        //   a. Return options.
        if (options.IsJSReceiver) return options;
        // 3. Throw a TypeError exception.
        return isolate.ThrowTypeError(MessageTemplate.InvalidArgument);
    }

    /// <summary>MapOptionToEnum over the alphabet option.</summary>
    static Base64Alphabet ReadAlphabet(Isolate isolate, JSValue options)
    {
        // 1. Let alphabet be ? Get(opts, "alphabet").
        JSValue optAlphabet = JSObject.ReadFromOptionsBag(options, ReadOnlyRoots.alphabet_string, isolate);
        // 2. If alphabet is undefined, set alphabet to "base64".
        if (optAlphabet.IsUndefined) return Base64Alphabet.Base64;
        if (optAlphabet.HeapObjectOrNull is JSString s)
        {
            // 3. If alphabet is neither "base64" nor "base64url", throw a TypeError
            //    exception.
            ReadOnlySpan<char> chars = s.FlatSpan();
            if (chars.SequenceEqual("base64")) return Base64Alphabet.Base64;
            if (chars.SequenceEqual("base64url")) return Base64Alphabet.Base64Url;
        }
        isolate.ThrowTypeError(MessageTemplate.InvalidOption, optAlphabet);
        return default;
    }

    /// <summary>HandleOptionsBag: the alphabet and lastChunkHandling options.</summary>
    static (Base64Alphabet, LastChunkHandling) HandleOptionsBag(Isolate isolate, JSValue options)
    {
        Base64Alphabet alphabet = ReadAlphabet(isolate, options);

        // 4. Let lastChunkHandling be ? Get(opts, "lastChunkHandling").
        JSValue optLastChunkHandling = JSObject.ReadFromOptionsBag(options, ReadOnlyRoots.last_chunk_handling_string, isolate);

        // 5. If lastChunkHandling is undefined, set lastChunkHandling to "loose".
        LastChunkHandling lastChunkHandling = LastChunkHandling.Loose;
        if (!optLastChunkHandling.IsUndefined)
        {
            // 6. If lastChunkHandling is not one of "loose", "strict", or
            //     "stop-before-partial", throw a TypeError exception.
            ReadOnlySpan<char> chars = optLastChunkHandling.HeapObjectOrNull is JSString s ? s.FlatSpan() : default;
            if (optLastChunkHandling.IsString && chars.SequenceEqual("loose")) lastChunkHandling = LastChunkHandling.Loose;
            else if (optLastChunkHandling.IsString && chars.SequenceEqual("strict")) lastChunkHandling = LastChunkHandling.Strict;
            else if (optLastChunkHandling.IsString && chars.SequenceEqual("stop-before-partial")) lastChunkHandling = LastChunkHandling.StopBeforePartial;
            else isolate.ThrowTypeError(MessageTemplate.InvalidOption, optLastChunkHandling);
        }
        return (alphabet, lastChunkHandling);
    }

    static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    static int SkipAsciiWhitespace(ReadOnlySpan<char> s, int index)
    {
        while (index < s.Length && IsAsciiWhitespace(s[index])) index++;
        return index;
    }

    static int Base64Value(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    /// <summary>
    /// FromBase64 (proposal-arraybuffer-base64): decodes into
    /// <paramref name="output"/> (at most output.Length bytes). Returns the
    /// error to throw (0 for none) and sets read/written.
    /// </summary>
    static MessageTemplate FromBase64(ReadOnlySpan<char> input, Base64Alphabet alphabet, LastChunkHandling lastChunkHandling,
        Span<byte> output, out int read, out int written)
    {
        int maxLength = output.Length;
        read = 0;
        written = 0;
        // 3. If maxLength = 0, return { read: 0, bytes: «», error: none }.
        if (maxLength == 0) return 0;

        Span<int> chunk = stackalloc int[4];
        int chunkLength = 0;
        int index = 0;
        int length = input.Length;
        while (true)
        {
            // a. Set index to SkipAsciiWhitespace(string, index).
            index = SkipAsciiWhitespace(input, index);
            // b. If index = length, then
            if (index == length)
            {
                if (chunkLength > 0)
                {
                    if (lastChunkHandling == LastChunkHandling.StopBeforePartial) return 0;
                    if (lastChunkHandling == LastChunkHandling.Loose)
                    {
                        if (chunkLength == 1) return MessageTemplate.Base64InputRemainder;
                        DecodeFinalBase64Chunk(chunk, chunkLength, output, ref written);
                    }
                    else
                    {
                        // strict
                        return MessageTemplate.Base64InputRemainder;
                    }
                }
                read = length;
                return 0;
            }

            // c-d.
            char c = input[index++];

            // e. If char is "=", then
            if (c == '=')
            {
                // i. If chunkLength < 2, then return error.
                if (chunkLength < 2)
                {
                    return chunkLength == 0 ? MessageTemplate.InvalidBase64Character : MessageTemplate.Base64InputRemainder;
                }
                // ii. Set index to SkipAsciiWhitespace(string, index).
                index = SkipAsciiWhitespace(input, index);
                // iii. If chunkLength = 2, then
                if (chunkLength == 2)
                {
                    // 1. If index = length, then
                    if (index == length)
                    {
                        if (lastChunkHandling == LastChunkHandling.StopBeforePartial) return 0;
                        return lastChunkHandling == LastChunkHandling.Strict
                            ? MessageTemplate.Base64InputRemainder
                            : MessageTemplate.InvalidBase64Character;
                    }
                    // 2. Set char to the code unit at index index within string.
                    // 3. If char is "=", then set index to SkipAsciiWhitespace(string, index + 1).
                    if (input[index] == '=') index = SkipAsciiWhitespace(input, index + 1);
                }
                // iv. If index < length, then return error.
                if (index < length) return MessageTemplate.InvalidBase64Character;
                // v. If lastChunkHandling is "strict", let throwOnExtraBits be true.
                bool throwOnExtraBits = lastChunkHandling == LastChunkHandling.Strict;
                // vi. Let decoded be ? DecodeFinalBase64Chunk(chunk, throwOnExtraBits).
                if (throwOnExtraBits && HasExtraBits(chunk, chunkLength)) return MessageTemplate.Base64ExtraBits;
                // vii. Set bytes to the list-concatenation of bytes and decoded.
                DecodeFinalBase64Chunk(chunk, chunkLength, output, ref written);
                // viii. Return { read: length, bytes, error: none }.
                read = length;
                return 0;
            }

            // f. If alphabet is "base64url", then
            if (alphabet == Base64Alphabet.Base64Url)
            {
                if (c is '+' or '/') return MessageTemplate.InvalidBase64Character;
                if (c == '-') c = '+';
                else if (c == '_') c = '/';
            }

            // g. If the sole code unit of char is not an element of the standard
            //    base64 alphabet, then return error.
            int value = Base64Value(c);
            if (value < 0) return MessageTemplate.InvalidBase64Character;

            // h. Let remaining be maxLength - the length of bytes.
            int remaining = maxLength - written;
            // i. If remaining = 1 and chunkLength = 2, or if remaining = 2 and chunkLength = 3, then
            if ((remaining == 1 && chunkLength == 2) || (remaining == 2 && chunkLength == 3)) return 0;

            // j. Set chunk to the string-concatenation of chunk and char.
            // k. Set chunkLength to chunkLength + 1.
            chunk[chunkLength++] = value;

            // l. If chunkLength = 4, then
            if (chunkLength == 4)
            {
                // i. Set bytes to the list-concatenation of bytes and ! DecodeBase64Chunk(chunk).
                int triple = (chunk[0] << 18) | (chunk[1] << 12) | (chunk[2] << 6) | chunk[3];
                output[written++] = (byte)(triple >> 16);
                output[written++] = (byte)(triple >> 8);
                output[written++] = (byte)triple;
                // ii-iii.
                chunkLength = 0;
                // iv. Set read to index.
                read = index;
                // v. If the number of elements in bytes = maxLength, return.
                if (written == maxLength) return 0;
            }
        }
    }

    static bool HasExtraBits(ReadOnlySpan<int> chunk, int chunkLength) =>
        chunkLength == 2 ? (chunk[1] & 0xF) != 0 : (chunk[2] & 0x3) != 0;

    /// <summary>DecodeFinalBase64Chunk without the extra-bits check.</summary>
    static void DecodeFinalBase64Chunk(ReadOnlySpan<int> chunk, int chunkLength, Span<byte> output, ref int written)
    {
        if (chunkLength == 2)
        {
            output[written++] = (byte)((chunk[0] << 2) | (chunk[1] >> 4));
        }
        else
        {
            Debug.Assert(chunkLength == 3);
            output[written++] = (byte)((chunk[0] << 2) | (chunk[1] >> 4));
            output[written++] = (byte)(((chunk[1] & 0xF) << 4) | (chunk[2] >> 2));
        }
    }

    /// <summary>simdutf::maximal_binary_length_from_base64: an upper bound of the decoded length.</summary>
    static int MaximalBinaryLengthFromBase64(ReadOnlySpan<char> input)
    {
        long padding = 0;
        int end = input.Length;
        if (end > 0 && input[end - 1] == '=') { padding++; end--; }
        if (end > 0 && input[end - 1] == '=') { padding++; end--; }
        long actualLength = input.Length - padding;
        return (int)Math.Min(int.MaxValue, actualLength / 4 * 3 + (actualLength % 4 > 1 ? actualLength % 4 - 1 : 0));
    }

    static JSTypedArray ValidateUint8Receiver(Isolate isolate, JSValue receiver, string methodName, bool validate)
    {
        JSTypedArray uint8array;
        if (validate)
        {
            // Perform ? ValidateUint8Array(into, write).
            uint8array = JSTypedArray.Validate(isolate, receiver, methodName, TypedArrayAccessMode.kWrite);
        }
        else
        {
            // CHECK_RECEIVER(JSTypedArray, uint8array, method_name)
            if (receiver.HeapObjectOrNull is not JSTypedArray ta)
            {
                isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, ArrayBuiltinsUtils.NewString(isolate, methodName),
                    receiver);
                return null!;
            }
            uint8array = ta;
        }
        ElementsKind elementsKind = uint8array.Kind;
        if (elementsKind != ElementsKind.UINT8_ELEMENTS && elementsKind != ElementsKind.RAB_GSAB_UINT8_ELEMENTS)
        {
            // (V8 passes only the method name; the receiver renders as undefined.)
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver, ArrayBuiltinsUtils.NewString(isolate, methodName),
                JSValue.Undefined);
        }
        return uint8array;
    }

    static JSString InputStringOrThrow(Isolate isolate, JSValue input)
    {
        // If string is not a String, throw a TypeError exception.
        if (input.HeapObjectOrNull is JSString s) return s;
        isolate.ThrowTypeError(MessageTemplate.ArgumentIsNonString, ReadOnlyRoots.input_string);
        return null!;
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.frombase64</summary>
    public static JSValue Uint8ArrayFromBase64(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If string is not a String, throw a TypeError exception.
        JSString inputString = InputStringOrThrow(isolate, args.AtOrUndefined(1));

        // 2. Let opts be ? GetOptionsObject(options).
        JSValue options = GetOptionsObject(isolate, args.AtOrUndefined(2));

        // Steps 3-8 handled in HandleOptionsBag
        (Base64Alphabet alphabet, LastChunkHandling lastChunkHandling) = HandleOptionsBag(isolate, options);

        // 9. Let result be ? FromBase64(string, alphabet, lastChunkHandling).
        ReadOnlySpan<char> input = inputString.FlatSpan();
        byte[] output = new byte[MaximalBinaryLengthFromBase64(input)];
        MessageTemplate error = FromBase64(input, alphabet, lastChunkHandling, output, out _, out int outputLength);

        JSArrayBuffer? buffer = isolate.Factory.NewJSArrayBufferAndBackingStore((ulong)outputLength, initialized: false);
        if (buffer is null)
        {
            return isolate.ThrowRangeError(MessageTemplate.OutOfMemory, ArrayBuiltinsUtils.NewString(isolate, "Uint8Array.fromBase64"));
        }
        output.AsSpan(0, outputLength).CopyTo(buffer.BackingStoreBuffer);

        // 10. If result.[[Error]] is not none, then
        //    a. Throw result.[[Error]].
        if (error != 0) return isolate.ThrowSyntaxError(error);

        // 11-14.
        return isolate.Factory.NewJSTypedArray(ElementsKind.UINT8_ELEMENTS, buffer, 0, (ulong)outputLength);
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.prototype.setfrombase64</summary>
    public static JSValue Uint8ArrayPrototypeSetFromBase64(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Uint8Array.prototype.setFromBase64";
        // 1. Let into be the this value.
        // 2. Perform ? ValidateUint8Array(into, write).
        JSTypedArray uint8array = ValidateUint8Receiver(isolate, args.Receiver, methodName, validate: true);

        // 3. If string is not a String, throw a TypeError exception.
        JSString inputString = InputStringOrThrow(isolate, args.AtOrUndefined(1));

        // 4. Let opts be ? GetOptionsObject(options).
        JSValue options = GetOptionsObject(isolate, args.AtOrUndefined(2));

        // Steps 5-10 handled in HandleOptionsBag
        (Base64Alphabet alphabet, LastChunkHandling lastChunkHandling) = HandleOptionsBag(isolate, options);

        // 11. Let taRecord be MakeTypedArrayWithBufferWitnessRecord(into, seq-cst).
        // 12. If IsTypedArrayOutOfBounds(taRecord) is true, throw a TypeError
        //     exception.
        // 13. Let byteLength be TypedArrayLength(taRecord).
        ulong arrayLength = uint8array.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds || uint8array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        // If the receiver has length of 0, we should return early
        // with 0 bytes read and 0 bytes write.
        if (arrayLength == 0) return isolate.Factory.NewJSUint8ArraySetFromResult(JSValue.Zero, JSValue.Zero);

        // 14-19. FromBase64 does not invoke any user code, so the ArrayBuffer
        // backing into cannot have been detached or shrunk; decode directly into it.
        MessageTemplate error = FromBase64(inputString.FlatSpan(), alphabet, lastChunkHandling,
            uint8array.DataSpan(0, arrayLength), out int read, out int written);

        // 20. If result.[[Error]] is not none, then
        //    a. Throw result.[[Error]].
        if (error != 0) return isolate.ThrowSyntaxError(error);

        // 21-24.
        return isolate.Factory.NewJSUint8ArraySetFromResult(JSValue.FromNumber(read), JSValue.FromNumber(written));
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.prototype.tobase64</summary>
    public static JSValue Uint8ArrayPrototypeToBase64(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Uint8Array.prototype.toBase64";
        // 1. Let O be the this value.
        // 2. Perform ? ValidateUint8Array(O).
        JSTypedArray uint8array = ValidateUint8Receiver(isolate, args.Receiver, methodName, validate: false);

        // 3. Let opts be ? GetOptionsObject(options).
        JSValue options = GetOptionsObject(isolate, args.AtOrUndefined(1));

        // 4-6. Let alphabet be ? Get(opts, "alphabet").
        Base64Alphabet alphabet = ReadAlphabet(isolate, options);

        // 7. Let omitPadding be ToBoolean(? Get(opts, "omitPadding")).
        JSValue omitPaddingObject = JSObject.ReadFromOptionsBag(options, isolate.Factory.InternalizeString("omitPadding"), isolate);
        bool omitPadding = ObjectOps.BooleanValue(omitPaddingObject);

        ulong length = uint8array.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds || uint8array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        // base64_length_from_binary
        ulong outputLength = omitPadding ? (length * 4 + 2) / 3 : (length + 2) / 3 * 4;
        if (outputLength > JSString.kMaxLength) return isolate.ThrowTypeError(MessageTemplate.InvalidStringLength);
        if (outputLength == 0) return ReadOnlyRoots.empty_string;

        // 8-11. Encode (RFC 4648 section 4 or 5), with padding unless omitPadding.
        string encoded = Convert.ToBase64String(uint8array.DataSpan(0, length));
        Span<char> chars = encoded.Length <= 1024 ? stackalloc char[encoded.Length] : new char[encoded.Length];
        encoded.AsSpan().CopyTo(chars);
        int outLength = chars.Length;
        if (omitPadding)
        {
            while (outLength > 0 && chars[outLength - 1] == '=') outLength--;
        }
        if (alphabet == Base64Alphabet.Base64Url)
        {
            for (int i = 0; i < outLength; i++)
            {
                if (chars[i] == '+') chars[i] = '-';
                else if (chars[i] == '/') chars[i] = '_';
            }
        }
        return isolate.Factory.NewStringFromUtf16(chars[..outLength]);
    }

    static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>ArrayBufferFromHex (simd.cc, scalar path): false on the first invalid pair.</summary>
    static bool ArrayBufferFromHex(ReadOnlySpan<char> input, Span<byte> buffer, int outputLength)
    {
        for (int i = 0, index = 0; index < outputLength; i += 2, index++)
        {
            int high = HexValue(input[i]);
            int low = HexValue(input[i + 1]);
            if (high < 0 || low < 0) return false;
            buffer[index] = (byte)((high << 4) | low);
        }
        return true;
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.fromhex</summary>
    public static JSValue Uint8ArrayFromHex(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If string is not a String, throw a TypeError exception.
        JSString inputString = InputStringOrThrow(isolate, args.AtOrUndefined(1));

        // 2-4.
        ReadOnlySpan<char> input = inputString.FlatSpan();
        if (input.Length % 2 != 0) return isolate.ThrowSyntaxError(MessageTemplate.InvalidHexString);

        int outputLength = input.Length / 2;
        JSArrayBuffer? buffer = isolate.Factory.NewJSArrayBufferAndBackingStore((ulong)outputLength, initialized: false);
        if (buffer is null)
        {
            return isolate.ThrowRangeError(MessageTemplate.OutOfMemory, ArrayBuiltinsUtils.NewString(isolate, "Uint8Array.fromHex"));
        }

        if (!ArrayBufferFromHex(input, buffer.BackingStoreBuffer, outputLength))
        {
            return isolate.ThrowSyntaxError(MessageTemplate.InvalidHexString);
        }
        // 5-7.
        return isolate.Factory.NewJSTypedArray(ElementsKind.UINT8_ELEMENTS, buffer, 0, (ulong)outputLength);
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.prototype.setfromhex</summary>
    public static JSValue Uint8ArrayPrototypeSetFromHex(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Uint8Array.prototype.setFromHex";
        // 1. Let into be the this value.
        // 2. Perform ? ValidateUint8Array(into, write).
        JSTypedArray uint8array = ValidateUint8Receiver(isolate, args.Receiver, methodName, validate: true);

        // 3. If string is not a String, throw a TypeError exception.
        JSString inputString = InputStringOrThrow(isolate, args.AtOrUndefined(1));

        // 4-6.
        ulong arrayLength = uint8array.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds || uint8array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        ReadOnlySpan<char> input = inputString.FlatSpan();
        if (input.Length % 2 != 0) return isolate.ThrowSyntaxError(MessageTemplate.InvalidHexString);

        // If the receiver has length of 0, we should return early
        // with 0 bytes read and 0 bytes write.
        if (arrayLength == 0) return isolate.Factory.NewJSUint8ArraySetFromResult(JSValue.Zero, JSValue.Zero);

        int outputLength = (int)Math.Min((ulong)(input.Length / 2), arrayLength);

        // 7-12.
        if (!ArrayBufferFromHex(input, uint8array.DataSpan(0, (ulong)outputLength), outputLength))
        {
            // 13. If result.[[Error]] is not none, then
            //     a. Throw result.[[Error]].
            return isolate.ThrowSyntaxError(MessageTemplate.InvalidHexString);
        }

        // 14-17.
        return isolate.Factory.NewJSUint8ArraySetFromResult(JSValue.FromNumber(outputLength * 2), JSValue.FromNumber(outputLength));
    }

    /// <summary>https://tc39.es/proposal-arraybuffer-base64/spec/#sec-uint8array.prototype.tohex</summary>
    public static JSValue Uint8ArrayPrototypeToHex(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "Uint8Array.prototype.toHex";
        //  1. Let O be the this value.
        //  2. Perform ? ValidateUint8Array(O).
        JSTypedArray uint8array = ValidateUint8Receiver(isolate, args.Receiver, methodName, validate: false);

        //  3. Let toEncode be ? GetUint8ArrayBytes(O).
        ulong length = uint8array.GetLengthOrOutOfBounds(out bool outOfBounds);
        if (outOfBounds || uint8array.WasDetached)
        {
            return isolate.ThrowTypeError(MessageTemplate.TypedArrayValidateErrorOperation,
                ArrayBuiltinsUtils.NewString(isolate, methodName));
        }

        if (length > (ulong)(JSString.kMaxLength / 2)) return isolate.ThrowTypeError(MessageTemplate.InvalidStringLength);
        if (length == 0) return ReadOnlyRoots.empty_string;

        //   4-6. Lowercase hex of each byte.
        string hex = Convert.ToHexStringLower(uint8array.DataSpan(0, length));
        return isolate.Factory.NewStringFromUtf16(hex);
    }
}
