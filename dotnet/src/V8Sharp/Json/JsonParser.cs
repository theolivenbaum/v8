// Port of src/json/json-parser.{h,cc}: JsonParser (the scanner tables,
// tokens, strings, numbers, the iterative value parser with source tracking,
// error reporting with context) and JsonParseInternalizer (the reviver, with
// the context argument of the JSON.parse source text access proposal).
//
// V8 instantiates the parser for one-byte and two-byte strings; V8Sharp's
// strings are UTF-16, the two-byte instantiation. See deviations.md (JSON)
// for the parts of V8's object construction that are not ported.
using System.Buffers;
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using CharPredicates = V8Sharp.Base.Strings.CharPredicates;

namespace V8Sharp.Json;

/// <summary>JsonToken (json-parser.h).</summary>
public enum JsonToken : byte
{
    NUMBER,
    STRING,
    LBRACE,
    RBRACE,
    LBRACK,
    RBRACK,
    TRUE_LITERAL,
    FALSE_LITERAL,
    NULL_LITERAL,
    WHITESPACE,
    COLON,
    COMMA,
    ILLEGAL,
    EOS,
}

/// <summary>JsonString: a scanned string, as a range of the source (or an array index).</summary>
readonly struct JsonString
{
    public readonly int Start;  // or the index, when IsIndex
    public readonly int Length;
    public readonly bool Internalize;
    public readonly bool HasEscape;
    public readonly bool IsIndex;

    public JsonString(uint index)
    {
        Start = unchecked((int)index);
        Length = 0;
        Internalize = false;
        HasEscape = false;
        IsIndex = true;
    }

    public JsonString(int start, int length, bool internalize, bool hasEscape)
    {
        Start = start;
        Length = length;
        Internalize = internalize;
        HasEscape = hasEscape;
        IsIndex = false;
    }

    public uint Index => unchecked((uint)Start);
}

/// <summary>
/// The parse node of a value, recorded when the reviver may receive the
/// source text: the source string of a primitive, the property nodes of an
/// object (V8: an ObjectTwoHashTable) or the element nodes of an array (V8:
/// a FixedArray of node/snapshot pairs).
/// </summary>
abstract class JsonValNode
{
    public sealed class Primitive(JSString source) : JsonValNode
    {
        public readonly JSString Source = source;
    }

    public sealed class Object(Dictionary<JSString, (JsonValNode? Node, JSValue Snapshot)> properties) : JsonValNode
    {
        public readonly Dictionary<JSString, (JsonValNode? Node, JSValue Snapshot)> Properties = properties;
    }

    public sealed class Array(JsonValNode?[] nodes, JSValue[] snapshots) : JsonValNode
    {
        public readonly JsonValNode?[] Nodes = nodes;
        public readonly JSValue[] Snapshots = snapshots;
    }
}

/// <summary>JsonParser (json-parser.h/.cc).</summary>
public sealed class JsonParser
{
    // ---------------------------------------------------------------------
    // Scanner tables.

    enum EscapeKind : byte
    {
        kIllegal,
        kSelf,
        kBackspace,
        kTab,
        kNewLine,
        kFormFeed,
        kCarriageReturn,
        kUnicode,
    }

    // EscapeKindField = BitField8<EscapeKind, 0, 3>; MayTerminateStringField
    // and NumberPartField follow.
    const byte kEscapeKindMask = 0x7;
    const byte kMayTerminateString = 1 << 3;
    const byte kNumberPart = 1 << 4;

    static JsonToken GetOneCharJsonToken(int c) => c switch
    {
        '"' => JsonToken.STRING,
        >= '0' and <= '9' => JsonToken.NUMBER,
        '-' => JsonToken.NUMBER,
        '[' => JsonToken.LBRACK,
        '{' => JsonToken.LBRACE,
        ']' => JsonToken.RBRACK,
        '}' => JsonToken.RBRACE,
        't' => JsonToken.TRUE_LITERAL,
        'f' => JsonToken.FALSE_LITERAL,
        'n' => JsonToken.NULL_LITERAL,
        ' ' or '\t' or '\r' or '\n' => JsonToken.WHITESPACE,
        ':' => JsonToken.COLON,
        ',' => JsonToken.COMMA,
        _ => JsonToken.ILLEGAL,
    };

    static byte GetJsonScanFlags(int c)
    {
        EscapeKind kind = c switch
        {
            'b' => EscapeKind.kBackspace,
            't' => EscapeKind.kTab,
            'n' => EscapeKind.kNewLine,
            'f' => EscapeKind.kFormFeed,
            'r' => EscapeKind.kCarriageReturn,
            'u' => EscapeKind.kUnicode,
            '"' or '\\' or '/' => EscapeKind.kSelf,
            _ => EscapeKind.kIllegal,
        };
        int flags = (int)kind;
        if (c < 0x20 || c == '"' || c == '\\') flags |= kMayTerminateString;
        if (c == '.' || c == 'e' || c == 'E' || CharPredicates.IsDecimalDigit(c) || c == '-' || c == '+') flags |= kNumberPart;
        return (byte)flags;
    }

    /// <summary>Table of one-character tokens, by character (0x00..0xFF only).</summary>
    static readonly JsonToken[] s_oneCharJsonTokens = BuildTokens();

    /// <summary>Table of one-character scan flags, by character (0x00..0xFF only).</summary>
    static readonly byte[] s_characterJsonScanFlags = BuildFlags();

    static JsonToken[] BuildTokens()
    {
        var t = new JsonToken[256];
        for (int i = 0; i < 256; i++) t[i] = GetOneCharJsonToken(i);
        return t;
    }

    static byte[] BuildFlags()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = GetJsonScanFlags(i);
        return t;
    }

    /// <summary>The characters that may end the scan of a string's plain run: '"', '\' and controls.</summary>
    static readonly SearchValues<char> s_stringTerminators = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MayTerminateJsonString(byte flags) => (flags & kMayTerminateString) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static EscapeKind GetEscapeKind(byte flags) => (EscapeKind)(flags & kEscapeKindMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsNumberPart(byte flags) => (flags & kNumberPart) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JsonToken GetTokenForCharacter(char c) => c <= 0xFF ? s_oneCharJsonTokens[c] : JsonToken.ILLEGAL;

    const int kEndOfString = -1;
    const int kInvalidUnicodeCharacter = -1;
    const int kMaxContextCharacters = 10;
    const int kMinOriginalSourceLengthForContext = kMaxContextCharacters * 2 + 1;

    // ---------------------------------------------------------------------
    // State.

    readonly Isolate _isolate;
    readonly JSString _originalSource;
    // The characters (a SlicedString's parent's, as V8 parses sliced strings
    // in place) and the source range.
    readonly string _chars;
    readonly int _offset;
    int _cursor;
    readonly int _end;
    JsonToken _next;
    bool _failed;

    JsonValNode? _parsedValNode;

    // The property and element stacks of the iterative parser.
    JsonProperty[] _propertyStack = new JsonProperty[16];
    int _propertyCount;
    JSValue[] _elementStack = new JSValue[16];
    int _elementCount;

    struct JsonProperty(JsonString @string)
    {
        public readonly JsonString String = @string;
        public JSValue Value;
    }

    JsonParser(Isolate isolate, JSString source)
    {
        _isolate = isolate;
        _originalSource = source;
        if (source is SlicedString sliced)
        {
            _chars = sliced.Parent.Flatten();
            _offset = sliced.Offset;
        }
        else
        {
            _chars = source.Flatten();
            _offset = 0;
        }
        _cursor = _offset;
        _end = _offset + source.Length;
    }

    /// <summary>JsonParser::Parse.</summary>
    public static JSValue Parse(Isolate isolate, JSString source, JSValue reviver)
    {
        bool collectSourceStrings = ObjectOps.IsCallable(reviver);
        // Deviation: V8 skips collecting the source strings when the reviver is
        // a function that can only access fewer than three formal parameters
        // (SharedFunctionInfo::CanOnlyAccessFixedFormalParameters); such a
        // reviver cannot observe the context argument, so V8Sharp always
        // collects them.
        var parser = new JsonParser(isolate, source);
        JSValue result = parser.ParseJson(collectSourceStrings);
        if (collectSourceStrings)
        {
            return JsonParseInternalizer.Internalize(isolate, result, (JSReceiver)reviver.Object, source,
                parser._parsedValNode, passContextArgument: true);
        }
        return result;
    }

    /// <summary>JsonParser::CheckRawJson.</summary>
    public static bool CheckRawJson(Isolate isolate, JSString source) => new JsonParser(isolate, source).ParseRawJson();

    // ---------------------------------------------------------------------
    // Characters and tokens.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Advance() => ++_cursor;

    bool IsAtEnd => _cursor == _end;

    int RemainingChars => _end - _cursor;

    int Position => _cursor;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int CurrentCharacter() => _cursor >= _end ? kEndOfString : _chars[_cursor];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int NextCharacter()
    {
        Advance();
        return CurrentCharacter();
    }

    JsonToken Peek() => _next;

    void Consume(JsonToken token)
    {
        Debug.Assert(Peek() == token);
        Advance();
    }

    static int TokenCharacter(JsonToken token) => token switch
    {
        JsonToken.STRING => '"',
        JsonToken.LBRACE => '{',
        JsonToken.RBRACE => '}',
        JsonToken.LBRACK => '[',
        JsonToken.RBRACK => ']',
        JsonToken.TRUE_LITERAL => 't',
        JsonToken.FALSE_LITERAL => 'f',
        JsonToken.NULL_LITERAL => 'n',
        JsonToken.COLON => ':',
        JsonToken.COMMA => ',',
        _ => -2,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool IsNextToken(JsonToken token)
    {
        if (token == JsonToken.EOS) return IsAtEnd;
        int expected = TokenCharacter(token);
        return expected >= 0 && expected == CurrentCharacter();
    }

    /// <summary>Check&lt;token&gt;: consumes the token if it is next (after whitespace).</summary>
    bool Check(JsonToken token)
    {
        if (IsNextToken(token))
        {
            Advance();
            return true;
        }
        GetNextNonWhitespaceToken();
        if (Peek() == token)
        {
            Advance();
            return true;
        }
        return false;
    }

    bool Expect(JsonToken token, MessageTemplate? errorMessage = null)
    {
        if (Peek() == token)
        {
            Advance();
            return true;
        }
        ReportUnexpectedToken(Peek(), errorMessage);
        return false;
    }

    bool ExpectNext(JsonToken token, MessageTemplate? errorMessage)
    {
        if (Check(token)) return true;
        ReportUnexpectedToken(Peek(), errorMessage);
        return false;
    }

    /// <summary>
    /// The JSON lexical grammar is specified in the ECMAScript 5 standard,
    /// section 15.12.1.1. The only allowed whitespace characters between tokens
    /// are tab, carriage-return, newline and space.
    /// </summary>
    void GetNextNonWhitespaceToken()
    {
        JsonToken localNext = JsonToken.EOS;
        string chars = _chars;
        int cursor = _cursor;
        int end = _end;
        while (cursor < end)
        {
            JsonToken current = GetTokenForCharacter(chars[cursor]);
            if (current != JsonToken.WHITESPACE)
            {
                localNext = current;
                break;
            }
            cursor++;
        }
        _cursor = cursor;
        _next = localNext;
    }

    void ScanLiteral(string s)
    {
        Debug.Assert(!IsAtEnd);
        // There's at least 1 character, we always consume a character and compare
        // the next character. The first character was compared before we jumped
        // to ScanLiteral.
        int n = s.Length + 1;
        int remaining = RemainingChars;
        if (remaining >= n - 1 && _chars.AsSpan(_cursor + 1, n - 2).SequenceEqual(s.AsSpan(1)))
        {
            _cursor += n - 1;
            return;
        }

        _cursor++;
        int limit = Math.Min(n - 2, remaining - 1);
        for (int i = 0; i < limit; i++)
        {
            if (s[1 + i] != _chars[_cursor])
            {
                ReportUnexpectedCharacter(_chars[_cursor]);
                return;
            }
            _cursor++;
        }

        Debug.Assert(IsAtEnd);
        ReportUnexpectedToken(JsonToken.EOS);
    }

    // ---------------------------------------------------------------------
    // Errors.

    bool IsSpecialString()
    {
        // The special cases are undefined, NaN, Infinity, and {} being passed to the
        // parse method
        ReadOnlySpan<char> source = _chars.AsSpan(_offset, _originalSource.Length);
        return source.SequenceEqual("[object Object]") || source.SequenceEqual("undefined") ||
               source.SequenceEqual("Infinity") || source.SequenceEqual("NaN");
    }

    MessageTemplate GetErrorMessageWithEllipses(ref JSValue arg, ref JSValue arg2, int pos)
    {
        MessageTemplate message;
        Factory factory = _isolate.Factory;
        arg = factory.LookupSingleCharacterStringFromCode(_chars[_cursor]);
        int originSourceLength = _originalSource.Length;
        // Only provide context for strings with at least
        // kMinOriginalSourceLengthForContext characters in length.
        if (originSourceLength >= kMinOriginalSourceLengthForContext)
        {
            int substringStart = 0;
            int substringEnd = originSourceLength;
            if (pos < kMaxContextCharacters)
            {
                message = MessageTemplate.JsonParseUnexpectedTokenStartStringWithContext;
                // Output the string followed by ellipses.
                substringEnd = pos + kMaxContextCharacters;
            }
            else if (pos >= kMaxContextCharacters && pos < originSourceLength - kMaxContextCharacters)
            {
                message = MessageTemplate.JsonParseUnexpectedTokenSurroundStringWithContext;
                // Add context before and after position of bad token surrounded by
                // ellipses.
                substringStart = pos - kMaxContextCharacters;
                substringEnd = pos + kMaxContextCharacters;
            }
            else
            {
                message = MessageTemplate.JsonParseUnexpectedTokenEndStringWithContext;
                // Add ellipses followed by some context before bad token.
                substringStart = pos - kMaxContextCharacters;
            }
            arg2 = factory.NewSubString(_originalSource, substringStart, substringEnd);
        }
        else
        {
            arg2 = _originalSource;
            // Output the entire string without ellipses but provide the token which
            // was unexpected.
            message = MessageTemplate.JsonParseUnexpectedTokenShortString;
        }
        return message;
    }

    MessageTemplate LookUpErrorMessageForJsonToken(JsonToken token, ref JSValue arg, ref JSValue arg2, int pos)
    {
        switch (token)
        {
            case JsonToken.EOS:
                return MessageTemplate.JsonParseUnexpectedEOS;
            case JsonToken.NUMBER:
                return MessageTemplate.JsonParseUnexpectedTokenNumber;
            case JsonToken.STRING:
                return MessageTemplate.JsonParseUnexpectedTokenString;
            default:
                // Output entire string without ellipses and don't provide the token
                // that was unexpected because it makes the error messages more confusing
                if (IsSpecialString())
                {
                    arg = _originalSource;
                    return MessageTemplate.JsonParseShortString;
                }
                return GetErrorMessageWithEllipses(ref arg, ref arg2, pos);
        }
    }

    /// <summary>Calculate line and column based on the current cursor position. Both values start at 1.</summary>
    void CalculateFileLocation(out JSValue line, out JSValue column)
    {
        // JSON allows only \r and \n as line terminators.
        // (See https://www.json.org/json-en.html - "whitespace")
        int lineNumber = 1;
        int start = _offset;
        int lastLineBreak = start;
        int cursor = start;
        int end = _cursor;  // cursor_ points to the position of the error.
        for (; cursor < end; ++cursor)
        {
            if (_chars[cursor] == '\r' && cursor < end - 1 && _chars[cursor + 1] == '\n')
            {
                // \r\n counts as a single line terminator, as of
                // https://tc39.es/ecma262/#sec-line-terminators. JSON itself does not
                // have a notion of lines or line terminators.
                ++cursor;
            }
            if (_chars[cursor] == '\r' || _chars[cursor] == '\n')
            {
                ++lineNumber;
                lastLineBreak = cursor + 1;
            }
        }
        int columnNumber = 1 + (cursor - lastLineBreak);
        line = JSValue.FromInt(lineNumber);
        column = JSValue.FromInt(columnNumber);
    }

    /// <summary>Mark that a parsing error has happened at the current token (throws the SyntaxError).</summary>
    void ReportUnexpectedToken(JsonToken token, MessageTemplate? errorMessage = null)
    {
        // Parse failed. Current character is the unexpected token.
        Factory factory = _isolate.Factory;
        int pos = Position - _offset;
        JSValue arg = JSValue.FromInt(pos);
        JSValue arg2 = default;
        CalculateFileLocation(out JSValue line, out JSValue column);
        arg2 = line;
        JSValue arg3 = column;

        MessageTemplate message = errorMessage ?? LookUpErrorMessageForJsonToken(token, ref arg, ref arg2, pos);

        Script script = factory.NewScript(_originalSource);
        // Move the cursor to the end so we won't be able to proceed parsing.
        _cursor = _end;
        _failed = true;
        var location = new MessageLocation(script, pos, pos + 1);
        _isolate.ThrowAt(factory.NewSyntaxError(message, arg, arg2, arg3), location);
    }

    void ReportUnexpectedCharacter(int c)
    {
        JsonToken token = JsonToken.ILLEGAL;
        if (c == kEndOfString)
        {
            token = JsonToken.EOS;
        }
        else if (c <= 0xFF)
        {
            token = s_oneCharJsonTokens[c];
        }
        ReportUnexpectedToken(token);
    }

    // ---------------------------------------------------------------------
    // Values.

    JSValue ParseJson(bool shouldTrackJsonSource)
    {
        JSValue result = ParseJsonValue(shouldTrackJsonSource);
        ExpectNext(JsonToken.EOS, MessageTemplate.JsonParseUnexpectedNonWhiteSpaceCharacter);
        return result;
    }

    /// <summary>Parse rawJSON value.</summary>
    bool ParseRawJson()
    {
        if (IsAtEnd)
        {
            _isolate.Throw(_isolate.Factory.NewSyntaxError(MessageTemplate.InvalidRawJsonValue));
            return false;
        }
        _next = GetTokenForCharacter(_chars[_cursor]);
        switch (Peek())
        {
            case JsonToken.STRING:
                Consume(JsonToken.STRING);
                ScanJsonString(false);
                break;
            case JsonToken.NUMBER:
                ParseJsonNumber();
                break;
            case JsonToken.TRUE_LITERAL:
                ScanLiteral("true");
                break;
            case JsonToken.FALSE_LITERAL:
                ScanLiteral("false");
                break;
            case JsonToken.NULL_LITERAL:
                ScanLiteral("null");
                break;
            default:
                ReportUnexpectedCharacter(CurrentCharacter());
                return false;
        }
        if (_cursor != _end)
        {
            _isolate.Throw(_isolate.Factory.NewSyntaxError(MessageTemplate.InvalidRawJsonValue));
            return false;
        }
        return true;
    }

    enum ContinuationType : byte { kReturn, kObjectProperty, kArrayElement }

    struct JsonContinuation(ContinuationType type, int index)
    {
        public readonly ContinuationType Type = type;
        public readonly int Index = index;
        public uint MaxIndex;
        public uint Elements;
    }

    void PushProperty(JsonString key)
    {
        if (_propertyCount == _propertyStack.Length) System.Array.Resize(ref _propertyStack, _propertyCount * 2);
        _propertyStack[_propertyCount++] = new JsonProperty(key);
    }

    void PushElement(JSValue value)
    {
        if (_elementCount == _elementStack.Length) System.Array.Resize(ref _elementStack, _elementCount * 2);
        _elementStack[_elementCount++] = value;
    }

    /// <summary>
    /// Parse any JSON value. Iterative: objects and arrays push a
    /// continuation instead of recursing, so nesting depth is bounded by memory.
    /// When shouldTrackJsonSource is true, the parse nodes of every value are
    /// recorded for the reviver (see JsonValNode).
    /// </summary>
    JSValue ParseJsonValue(bool shouldTrackJsonSource)
    {
        var contStack = new List<JsonContinuation>(16);
        var cont = new JsonContinuation(ContinuationType.kReturn, 0);
        JSValue value = default;
        JsonValNode? valNode = null;
        int startPosition = 0;
        List<JsonValNode?>? elementValNodeStack = shouldTrackJsonSource ? [] : null;
        List<JsonValNode?>? propertyValNodeStack = shouldTrackJsonSource ? [] : null;
        Factory factory = _isolate.Factory;

        while (true)
        {
            // Produce a json value.
            //
            // Iterate until a value is produced. Starting but not immediately finishing
            // objects and arrays will cause the loop to continue until a first member
            // is completed.
            while (true)
            {
                GetNextNonWhitespaceToken();
                if (shouldTrackJsonSource) startPosition = Position;
                switch (Peek())
                {
                    case JsonToken.STRING:
                        Consume(JsonToken.STRING);
                        value = MakeString(ScanJsonString(false));
                        if (_failed) return default;
                        if (shouldTrackJsonSource) valNode = new JsonValNode.Primitive(SubSource(startPosition, Position));
                        break;

                    case JsonToken.NUMBER:
                        value = ParseJsonNumber();
                        if (_failed) return default;
                        if (shouldTrackJsonSource) valNode = new JsonValNode.Primitive(SubSource(startPosition, Position));
                        break;

                    case JsonToken.LBRACE:
                    {
                        Consume(JsonToken.LBRACE);
                        if (Check(JsonToken.RBRACE))
                        {
                            value = factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
                            if (shouldTrackJsonSource) valNode = new JsonValNode.Object(new Dictionary<JSString, (JsonValNode?, JSValue)>());
                            break;
                        }

                        // Start parsing an object with properties.
                        contStack.Add(cont);
                        cont = new JsonContinuation(ContinuationType.kObjectProperty, _propertyCount);

                        // Parse the property key.
                        // GetNextNonWhitespaceToken was already performed by
                        // Check(JsonToken::RBRACE).
                        if (!Expect(JsonToken.STRING, MessageTemplate.JsonParseExpectedPropNameOrRBrace)) return default;
                        PushProperty(ScanJsonPropertyKey(ref cont));
                        if (_failed) return default;
                        propertyValNodeStack?.Add(null);

                        if (!ExpectNext(JsonToken.COLON, MessageTemplate.JsonParseExpectedColonAfterPropertyName)) return default;

                        // Continue to start producing the first property value.
                        continue;
                    }

                    case JsonToken.LBRACK:
                        Consume(JsonToken.LBRACK);
                        if (Check(JsonToken.RBRACK))
                        {
                            value = factory.NewJSArray(ElementsKind.PACKED_SMI_ELEMENTS, 0, 0);
                            if (shouldTrackJsonSource) valNode = new JsonValNode.Array([], []);
                            break;
                        }

                        // Start parsing an array with elements.
                        contStack.Add(cont);
                        cont = new JsonContinuation(ContinuationType.kArrayElement, _elementCount);

                        // Continue to start producing the first array element.
                        continue;

                    case JsonToken.TRUE_LITERAL:
                        ScanLiteral("true");
                        if (_failed) return default;
                        value = JSValue.True;
                        if (shouldTrackJsonSource) valNode = new JsonValNode.Primitive(ReadOnlyRoots.true_string);
                        break;

                    case JsonToken.FALSE_LITERAL:
                        ScanLiteral("false");
                        if (_failed) return default;
                        value = JSValue.False;
                        if (shouldTrackJsonSource) valNode = new JsonValNode.Primitive(ReadOnlyRoots.false_string);
                        break;

                    case JsonToken.NULL_LITERAL:
                        ScanLiteral("null");
                        if (_failed) return default;
                        value = JSValue.Null;
                        if (shouldTrackJsonSource) valNode = new JsonValNode.Primitive(ReadOnlyRoots.null_string);
                        break;

                    default:
                        ReportUnexpectedCharacter(CurrentCharacter());
                        return default;
                }
                // Done producing a value, consume it.
                break;
            }

            // Consume a produced json value.
            //
            // Iterate as long as values are produced (arrays or object literals are
            // finished).
            while (true)
            {
                switch (cont.Type)
                {
                    case ContinuationType.kReturn:
                        if (shouldTrackJsonSource) _parsedValNode = valNode;
                        return value;

                    case ContinuationType.kObjectProperty:
                    {
                        // Store the previous property value into its property info.
                        _propertyStack[_propertyCount - 1].Value = value;
                        if (propertyValNodeStack is not null) propertyValNodeStack[^1] = valNode;

                        if (Check(JsonToken.COMMA))
                        {
                            // Parse the property key.
                            if (!ExpectNext(JsonToken.STRING, MessageTemplate.JsonParseExpectedDoubleQuotedPropertyName)) return default;
                            PushProperty(ScanJsonPropertyKey(ref cont));
                            if (_failed) return default;
                            propertyValNodeStack?.Add(null);
                            if (!ExpectNext(JsonToken.COLON, MessageTemplate.JsonParseExpectedColonAfterPropertyName)) return default;

                            // Break to start producing the subsequent property value.
                            break;
                        }

                        value = BuildJsonObject(in cont);
                        if (!Expect(JsonToken.RBRACE, MessageTemplate.JsonParseExpectedCommaOrRBrace)) return default;
                        // Return the object.
                        if (shouldTrackJsonSource)
                        {
                            int start = cont.Index;
                            int numProperties = _propertyCount - start;
                            var table = new Dictionary<JSString, (JsonValNode?, JSValue)>(numProperties);
                            for (int i = 0; i < numProperties; i++)
                            {
                                ref JsonProperty property = ref _propertyStack[start + i];
                                JSString key = property.String.IsIndex
                                    ? factory.InternalizeString(factory.SizeToString(property.String.Index))
                                    : (JSString)MakeString(property.String).Object;
                                table[key] = (propertyValNodeStack![start + i], property.Value);
                            }
                            propertyValNodeStack!.RemoveRange(start, propertyValNodeStack.Count - start);
                            valNode = new JsonValNode.Object(table);
                        }
                        ClearProperties(cont.Index);

                        // Pop the continuation.
                        cont = contStack[^1];
                        contStack.RemoveAt(contStack.Count - 1);
                        // Consume to produced object.
                        continue;
                    }

                    case ContinuationType.kArrayElement:
                    {
                        // Store the previous element on the stack.
                        PushElement(value);
                        elementValNodeStack?.Add(valNode);
                        // Break to start producing the subsequent element value.
                        if (Check(JsonToken.COMMA)) break;

                        value = BuildJsonArray(cont.Index);
                        if (!Expect(JsonToken.RBRACK, MessageTemplate.JsonParseExpectedCommaOrRBrack)) return default;
                        // Return the array.
                        if (shouldTrackJsonSource)
                        {
                            int start = cont.Index;
                            int numElements = _elementCount - start;
                            var nodes = new JsonValNode?[numElements];
                            var snapshots = new JSValue[numElements];
                            for (int i = 0; i < numElements; i++)
                            {
                                nodes[i] = elementValNodeStack![start + i];
                                snapshots[i] = _elementStack[start + i];
                            }
                            elementValNodeStack!.RemoveRange(start, elementValNodeStack.Count - start);
                            valNode = new JsonValNode.Array(nodes, snapshots);
                        }
                        ClearElements(cont.Index);
                        // Pop the continuation.
                        cont = contStack[^1];
                        contStack.RemoveAt(contStack.Count - 1);
                        // Consume the produced array.
                        continue;
                    }
                }

                // Done consuming a value. Produce next value.
                break;
            }
        }
    }

    JSString SubSource(int start, int end) =>
        _isolate.Factory.NewStringFromUtf16(_chars.AsSpan(start, end - start));

    void ClearProperties(int start)
    {
        System.Array.Clear(_propertyStack, start, _propertyCount - start);
        _propertyCount = start;
    }

    void ClearElements(int start)
    {
        System.Array.Clear(_elementStack, start, _elementCount - start);
        _elementCount = start;
    }

    // ---------------------------------------------------------------------
    // Objects and arrays.

    /// <summary>
    /// BuildJsonObject: the parsed properties, elements first. Deviation: V8
    /// allocates the elements store directly and builds the named properties
    /// with JSDataObjectBuilder, using the previous array element's map as
    /// feedback; V8Sharp defines each property with CreateDataProperty in
    /// source order. The resulting objects are equal (same keys, order, values,
    /// and duplicate keys keep their first position with the last value); only
    /// the backing-store choices can differ.
    /// </summary>
    JSValue BuildJsonObject(in JsonContinuation cont)
    {
        JSObject obj = _isolate.Factory.NewJSObject(_isolate.NativeContext.ObjectFunction);
        int start = cont.Index;
        int length = _propertyCount - start;

        // First store the elements.
        if (cont.Elements > 0)
        {
            for (int i = 0; i < length; i++)
            {
                ref JsonProperty property = ref _propertyStack[start + i];
                if (!property.String.IsIndex) continue;
                var key = new PropertyKey(_isolate, (double)property.String.Index);
                JSObject.CreateDataProperty(_isolate, obj, in key, property.Value, ShouldThrow.ThrowOnError);
            }
        }

        for (int i = 0; i < length; i++)
        {
            ref JsonProperty property = ref _propertyStack[start + i];
            if (property.String.IsIndex) continue;
            var name = (JSString)MakeString(property.String).Object;
            var key = new PropertyKey(_isolate, name);
            JSObject.CreateDataProperty(_isolate, obj, in key, property.Value, ShouldThrow.ThrowOnError);
        }
        return obj;
    }

    /// <summary>BuildJsonArray: PACKED_SMI, PACKED_DOUBLE or PACKED elements, by the values.</summary>
    JSValue BuildJsonArray(int start)
    {
        int length = _elementCount - start;

        ElementsKind kind = ElementsKind.PACKED_SMI_ELEMENTS;
        for (int i = start; i < _elementCount; i++)
        {
            JSValue value = _elementStack[i];
            if (value.IsSmi) continue;
            if (value.IsNumber)
            {
                kind = ElementsKind.PACKED_DOUBLE_ELEMENTS;
            }
            else
            {
                kind = ElementsKind.PACKED_ELEMENTS;
                break;
            }
        }

        FixedArrayBase elements;
        if (kind == ElementsKind.PACKED_DOUBLE_ELEMENTS)
        {
            var doubles = new FixedDoubleArray(length);
            for (int i = 0; i < length; i++) doubles.Set(i, _elementStack[start + i].Number);
            elements = doubles;
        }
        else
        {
            var values = new JSValue[length];
            System.Array.Copy(_elementStack, start, values, 0, length);
            elements = new FixedArray(values);
        }
        return _isolate.Factory.NewJSArrayWithElements(elements, kind, length);
    }

    // ---------------------------------------------------------------------
    // Numbers.

    void AdvanceToNonDecimal()
    {
        int cursor = _cursor;
        while (cursor < _end && CharPredicates.IsDecimalDigit(_chars[cursor])) cursor++;
        _cursor = cursor;
    }

    /// <summary>
    /// A JSON number (production JSONNumber) is a subset of the valid JavaScript
    /// decimal number literals. It includes an optional minus sign, must have at
    /// least one digit before and after a decimal point, may not have prefixed
    /// zeros (unless the integer part is zero), and may include an exponent part
    /// (e.g., "e-10"). Hexadecimal and octal numbers are not allowed.
    /// </summary>
    JSValue ParseJsonNumber()
    {
        int sign = 1;
        int start = _cursor;

        int c = _chars[_cursor];
        if (c == '-')
        {
            sign = -1;
            c = NextCharacter();
        }

        if (c == '0')
        {
            // Prefix zero is only allowed if it's the only digit before
            // a decimal point or exponent.
            c = NextCharacter();
            if (c >= 0 && c <= 0xFF && IsNumberPart(s_characterJsonScanFlags[c]))
            {
                if (CharPredicates.IsDecimalDigit(c))
                {
                    ReportUnexpectedToken(JsonToken.NUMBER);
                    return JSValue.Zero;
                }
            }
            else if (sign > 0)
            {
                return JSValue.Zero;
            }
        }
        else
        {
            int smiStart = _cursor;
            const int kMaxSmiLength = 9;
            int i = 0;
            int stop = Math.Min(_cursor + kMaxSmiLength, _end);
            while (_cursor < stop && CharPredicates.IsDecimalDigit(_chars[_cursor]))
            {
                i = i * 10 + (_chars[_cursor] - '0');
                _cursor++;
            }
            if (smiStart == _cursor)
            {
                ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseNoNumberAfterMinusSign);
                return JSValue.Zero;
            }
            c = CurrentCharacter();
            if (!(c >= 0 && c <= 0xFF) || !IsNumberPart(s_characterJsonScanFlags[c]))
            {
                // Smi.
                return JSValue.FromInt(i * sign);
            }
            AdvanceToNonDecimal();
        }

        if (CurrentCharacter() == '.')
        {
            c = NextCharacter();
            if (!CharPredicates.IsDecimalDigit(c))
            {
                ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseUnterminatedFractionalNumber);
                return JSValue.Zero;
            }
            AdvanceToNonDecimal();
        }

        if (CharPredicates.AsciiAlphaToLower(CurrentCharacter()) == 'e')
        {
            c = NextCharacter();
            if (c == '-' || c == '+') c = NextCharacter();
            if (!CharPredicates.IsDecimalDigit(c))
            {
                ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseExponentPartMissingNumber);
                return JSValue.Zero;
            }
            AdvanceToNonDecimal();
        }

        double number = Conversions.StringToDouble(_chars.AsSpan(start, _cursor - start), ConversionFlag.NoConversionFlag, double.NaN);
        Debug.Assert(!double.IsNaN(number));
        return JSValue.FromNumber(number);
    }

    // ---------------------------------------------------------------------
    // Strings.

    int ScanUnicodeCharacter()
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            int digit = HexValue(NextCharacter());
            if (digit < 0) return kInvalidUnicodeCharacter;
            value = value * 16 + digit;
        }
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int HexValue(int c)
    {
        c -= '0';
        if ((uint)c <= 9) return c;
        c = (c | 0x20) - ('a' - '0');  // detect 0x11..0x16 and 0x31..0x36.
        if ((uint)c <= 5) return c + 10;
        return -1;
    }

    /// <summary>TryAddArrayIndexChar (utils.h): index*10 + digit, failing past kMaxArrayIndex.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryAddArrayIndexChar(ref uint index, int c)
    {
        Debug.Assert(CharPredicates.IsDecimalDigit(c));
        int d = c - '0';
        // (kMaxArrayIndex - d) / 10, with kMaxArrayIndex = 2^32 - 2.
        if (index > (429496729U - ((uint)(d + 3) >> 3))) return false;
        index = index * 10 + (uint)d;
        return true;
    }

    JsonString ScanJsonPropertyKey(ref JsonContinuation cont)
    {
        int start = _cursor;
        int first = CurrentCharacter();
        if (first == '\\' && NextCharacter() == 'u') first = ScanUnicodeCharacter();
        if (CharPredicates.IsDecimalDigit(first))
        {
            if (first == '0')
            {
                if (NextCharacter() == '"')
                {
                    Advance();
                    // Record element information.
                    cont.Elements++;
                    return new JsonString(0);
                }
            }
            else
            {
                uint index = (uint)(first - '0');
                while (true)
                {
                    int cursor = _cursor + 1;
                    while (cursor < _end && CharPredicates.IsDecimalDigit(_chars[cursor]) &&
                           TryAddArrayIndexChar(ref index, _chars[cursor]))
                    {
                        cursor++;
                    }
                    _cursor = cursor;

                    if (CurrentCharacter() == '"')
                    {
                        Advance();
                        // Record element information.
                        cont.Elements++;
                        cont.MaxIndex = Math.Max(cont.MaxIndex, index);
                        return new JsonString(index);
                    }

                    if (CurrentCharacter() == '\\' && NextCharacter() == 'u')
                    {
                        int c = ScanUnicodeCharacter();
                        if (CharPredicates.IsDecimalDigit(c) && TryAddArrayIndexChar(ref index, c)) continue;
                    }

                    break;
                }
            }
        }
        // Reset cursor_ to start if the key is not an index.
        _cursor = start;
        return ScanJsonString(true);
    }

    /// <summary>
    /// A JSON string (production JSONString) is subset of valid JavaScript string
    /// literals. The string must only be double-quoted (not single-quoted), and
    /// the only allowed backslash-escapes are ", /, \, b, f, n, r, t and
    /// four-digit hex escapes (uXXXX). Any other use of backslashes is invalid.
    /// </summary>
    JsonString ScanJsonString(bool needsInternalization)
    {
        int start = Position;
        int offset = start;
        bool hasEscape = false;
        string chars = _chars;

        while (true)
        {
            // Vectorized scan for a character that may terminate the plain run
            // (V8 scans 16 one-byte characters at a time with Highway; two-byte
            // strings take a scalar loop).
            int found = chars.AsSpan(_cursor, _end - _cursor).IndexOfAny(s_stringTerminators);
            _cursor = found < 0 ? _end : _cursor + found;

            if (IsAtEnd)
            {
                ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseUnterminatedString);
                break;
            }

            char ch = chars[_cursor];
            if (ch == '"')
            {
                int end = Position;
                Advance();
                int length = end - offset;
                return new JsonString(start, length, needsInternalization, hasEscape);
            }

            if (ch == '\\')
            {
                hasEscape = true;
                int c = NextCharacter();
                if (!(c >= 0 && c <= 0xFF))
                {
                    ReportUnexpectedCharacter(c);
                    break;
                }

                switch (GetEscapeKind(s_characterJsonScanFlags[c]))
                {
                    case EscapeKind.kSelf:
                    case EscapeKind.kBackspace:
                    case EscapeKind.kTab:
                    case EscapeKind.kNewLine:
                    case EscapeKind.kFormFeed:
                    case EscapeKind.kCarriageReturn:
                        offset += 1;
                        break;

                    case EscapeKind.kUnicode:
                    {
                        int value = ScanUnicodeCharacter();
                        if (value == kInvalidUnicodeCharacter)
                        {
                            ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseBadUnicodeEscape);
                            return default;
                        }
                        // \uXXXX results in either 1 or 2 Utf16 characters, depending on
                        // whether the decoded value requires a surrogate pair.
                        offset += 5 - (value > 0xFFFF ? 1 : 0);
                        break;
                    }

                    case EscapeKind.kIllegal:
                        ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseBadEscapedCharacter);
                        return default;
                }

                Advance();
                continue;
            }

            Debug.Assert(ch < 0x20);
            ReportUnexpectedToken(JsonToken.ILLEGAL, MessageTemplate.JsonParseBadControlCharacter);
            break;
        }

        return default;
    }

    /// <summary>MakeString: the string value of a scanned JsonString.</summary>
    JSValue MakeString(in JsonString @string)
    {
        Factory factory = _isolate.Factory;
        if (@string.Length == 0) return ReadOnlyRoots.empty_string;
        if (@string.Length == 1)
        {
            char firstChar;
            if (!@string.HasEscape)
            {
                firstChar = _chars[@string.Start];
            }
            else
            {
                Span<char> one = stackalloc char[1];
                DecodeString(one, @string.Start, 1);
                firstChar = one[0];
            }
            return factory.LookupSingleCharacterStringFromCode(firstChar);
        }

        if (!@string.HasEscape)
        {
            ReadOnlySpan<char> span = _chars.AsSpan(@string.Start, @string.Length);
            // Deviation: V8 also internalizes short one-byte string values within
            // a heuristic budget (--json-parse-max-heuristically-internalized-
            // strings); only property keys are internalized here. Internalization
            // is not observable.
            if (@string.Internalize) return factory.InternalizeString(span);
            return factory.NewStringFromUtf16(span);
        }

        char[]? rented = null;
        Span<char> buffer = @string.Length <= 256
            ? stackalloc char[@string.Length]
            : (rented = ArrayPool<char>.Shared.Rent(@string.Length)).AsSpan(0, @string.Length);
        try
        {
            DecodeString(buffer, @string.Start, @string.Length);
            if (@string.Internalize) return factory.InternalizeString(buffer);
            return factory.NewStringFromUtf16(buffer);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    void DecodeString(Span<char> sink, int start, int length)
    {
        string chars = _chars;
        int cursor = start;
        int s = 0;
        while (length > 0)
        {
            // Copy everything until the first escape character
            int backslash = chars.AsSpan(cursor).IndexOf('\\');
            int toCopy = backslash < 0 ? length : Math.Min(backslash, length);
            chars.AsSpan(cursor, toCopy).CopyTo(sink[s..]);
            length -= toCopy;
            cursor += toCopy;
            s += toCopy;

            if (length == 0) return;

            cursor++;

            switch (GetEscapeKind(s_characterJsonScanFlags[chars[cursor]]))
            {
                case EscapeKind.kSelf:
                    sink[s++] = chars[cursor];
                    length--;
                    break;
                case EscapeKind.kBackspace:
                    sink[s++] = '\x08';
                    length--;
                    break;
                case EscapeKind.kTab:
                    sink[s++] = '\x09';
                    length--;
                    break;
                case EscapeKind.kNewLine:
                    sink[s++] = '\x0A';
                    length--;
                    break;
                case EscapeKind.kFormFeed:
                    sink[s++] = '\x0C';
                    length--;
                    break;
                case EscapeKind.kCarriageReturn:
                    sink[s++] = '\x0D';
                    length--;
                    break;
                case EscapeKind.kUnicode:
                {
                    int value = 0;
                    for (int i = 0; i < 4; i++) value = value * 16 + HexValue(chars[++cursor]);
                    // A \uXXXX escape is at most 0xFFFF, a single UTF-16 unit.
                    sink[s++] = (char)value;
                    length--;
                    break;
                }
                case EscapeKind.kIllegal:
                    throw new InvalidOperationException("unreachable");
            }
            cursor++;
        }
    }
}
