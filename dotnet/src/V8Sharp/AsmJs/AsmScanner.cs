// Port of src/asmjs/asm-scanner.{h,cc} of V8 14.7.
//
// A custom scanner to extract the token stream needed to parse valid
// asm.js: http://asmjs.org/spec/latest/
// This scanner intentionally avoids the portion of JavaScript lexing
// that are not required to determine if code is valid asm.js code.
// * Strings are disallowed except for 'use asm'.
// * Only the subset of keywords needed to check asm.js invariants are
//   included.
// * Identifiers are accumulated into local + global string tables
//   (for performance).
//
// V8 reads the whole character stream from the function's start into a
// buffer; V8Sharp scans the script source string from that position.
using V8Sharp.Base.Numbers;
using static V8Sharp.AsmJs.AsmToken;

namespace V8Sharp.AsmJs;

public sealed class AsmJsScanner
{
    // Cap number of identifiers to ensure we can assign both global and
    // local ones a token id in the range of an int32_t.
    const int kMaxIdentifierCount = 0xF000000;

    readonly string _input;
    // The end of the input (V8: the end of the character stream).
    readonly int _inputEnd;
    // Start position of the input; positions are offsets into the source.
    readonly int _inputOffset;
    int _inputPosition;

    int _token = kUninitialized;
    int _precedingToken = kUninitialized;
    int _nextToken = kUninitialized;   // Only set when in {rewind} state.
    int _position;            // Corresponds to {token} position.
    int _precedingPosition;   // Corresponds to {preceding_token} position.
    int _nextPosition;        // Only set when in {rewind} state.
    bool _rewind;
    string _identifierString = "";
    bool _inLocalScope;
    readonly Dictionary<string, int> _localNames = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _globalNames = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _propertyNames = new(StringComparer.Ordinal);
    int _globalCount;
    double _doubleValue;
    uint _unsignedValue;
    bool _precededByNewline;

    readonly System.Text.StringBuilder _buffer = new();

    /// <summary>
    /// A scanner over <paramref name="source"/> from <paramref name="start"/>
    /// to <paramref name="end"/> (V8: the character stream seeked to the
    /// function literal's start).
    /// </summary>
    public AsmJsScanner(string source, int start, int end)
    {
        foreach ((string name, int token) in AsmNames.StdlibMathFunctions) _propertyNames[name] = token;
        foreach ((string name, int token) in AsmNames.StdlibArrayTypes) _propertyNames[name] = token;
        foreach ((string name, int token, _) in AsmNames.StdlibMathValues) _propertyNames[name] = token;
        foreach ((string name, int token) in AsmNames.StdlibOther) _propertyNames[name] = token;
        foreach ((string name, int token) in AsmNames.Keywords) _globalNames[name] = token;

        _input = source;
        _inputEnd = Math.Min(end, source.Length);
        _inputOffset = start;
        _inputPosition = start;
        Next();
    }

    /// <summary>Get current token.</summary>
    public int Token => _token;
    /// <summary>Get position of current token.</summary>
    public int Position => _position;

    /// <summary>
    /// Get raw string for current identifier. Note that the returned string will
    /// become invalid when the scanner advances, create a copy to preserve it.
    /// </summary>
    public string GetIdentifierString() => _identifierString;

    /// <summary>Check if we just passed a newline.</summary>
    public bool IsPrecededByNewline() => _precededByNewline;

    // Select whether identifiers are resolved in global or local scope,
    // and which scope new identifiers are added to.
    public void EnterLocalScope() => _inLocalScope = true;
    public void EnterGlobalScope() => _inLocalScope = false;

    /// <summary>Drop all current local identifiers.</summary>
    public void ResetLocals() => _localNames.Clear();

    // Methods to check if a token is an identifier and which scope.
    public bool IsLocal() => IsLocal(Token);
    public bool IsGlobal() => IsGlobal(Token);
    public static bool IsLocal(int token) => token <= kLocalsStart;
    public static bool IsGlobal(int token) => token >= kGlobalsStart;

    // Methods to find the index position of an identifier (count starting from
    // 0 for each scope separately).
    public static int LocalIndex(int token) => -(token - kLocalsStart);
    public static int GlobalIndex(int token) => token - kGlobalsStart;

    // Methods to check if the current token is a numeric literal considered an
    // asm.js "double" (contains a dot) or an "unsigned" (without a dot). Note
    // that numbers without a dot outside the [0 .. 2^32) range are errors.
    public bool IsUnsigned() => Token == kUnsigned;
    public uint AsUnsigned() => _unsignedValue;
    public bool IsDouble() => Token == kDouble;
    public double AsDouble() => _doubleValue;

    bool HasMoreChars() => _inputPosition < _inputEnd;
    char PeekChar() => _input[_inputPosition];
    char NextChar() => _input[_inputPosition++];

    bool Consume(char next)
    {
        if (!HasMoreChars() || PeekChar() != next) return false;
        ++_inputPosition;
        return true;
    }

    void Advance() => ++_inputPosition;

    /// <summary>Advance to the next token.</summary>
    public void Next()
    {
        if (_rewind)
        {
            _precedingToken = _token;
            _precedingPosition = _position;
            _token = _nextToken;
            _position = _nextPosition;
            _nextToken = kUninitialized;
            _nextPosition = 0;
            _rewind = false;
            return;
        }

        if (_token == kEndOfInput || _token == kParseError) return;

        _precededByNewline = false;
        _precedingToken = _token;
        _precedingPosition = _position;

        while (true)
        {
            _position = _inputPosition;
            if (!HasMoreChars())
            {
                _token = kEndOfInput;
                return;
            }

            char ch = NextChar();
            switch (ch)
            {
                case ' ':
                case '\t':
                    // Ignore whitespace.
                    break;

                case '\r':
                case '\n':
                    // Track when we've passed a line terminator for optional semicolon
                    // support, but keep scanning.
                    _precededByNewline = true;
                    break;

                case '\'':
                case '"':
                    ConsumeString(ch);
                    return;

                case '/':
                    if (Consume('/'))
                    {
                        ConsumeCPPComment();
                    }
                    else if (Consume('*'))
                    {
                        if (!ConsumeCComment())
                        {
                            _token = kParseError;
                            return;
                        }
                    }
                    else
                    {
                        _token = '/';
                        return;
                    }
                    // Breaks out of switch, but loops again (i.e. the case when we parsed
                    // a comment, but need to continue to look for the next token).
                    break;

                case '<':
                case '>':
                case '=':
                case '!':
                    ConsumeCompareOrShift(ch);
                    return;

                // SIMPLE_SINGLE_TOKEN_LIST: use fixed token IDs for ASCII.
                case '+': case '-': case '*': case '%': case '~': case '^': case '&': case '|':
                case '(': case ')': case '[': case ']': case '{': case '}': case ':': case ';':
                case ',': case '?':
                    _token = ch;
                    return;

                default:
                    if (IsIdentifierStart(ch))
                    {
                        ConsumeIdentifier(ch);
                    }
                    else if (IsNumberStart(ch))
                    {
                        ConsumeNumber(ch);
                    }
                    else
                    {
                        // TODO(bradnelson): Support unicode (probably via UnicodeCache).
                        _token = kParseError;
                    }
                    return;
            }
        }
    }

    /// <summary>Back up by one token.</summary>
    public void Rewind()
    {
        // TODO(bradnelson): Currently rewinding needs to leave in place the
        // preceding newline state (in case a |0 ends a line).
        // This is weird and stateful, fix me.
        _nextToken = _token;
        _nextPosition = _position;
        _token = _precedingToken;
        _position = _precedingPosition;
        _precedingToken = kUninitialized;
        _precedingPosition = 0;
        _rewind = true;
        _identifierString = "";
    }

    /// <summary>
    /// Restores old position (token after that position). Note that it is not
    /// allowed to rewind right after a seek, because previous tokens are unknown.
    /// </summary>
    public void Seek(int pos)
    {
        _inputPosition = pos;
        _precedingToken = kUninitialized;
        _token = kUninitialized;
        _nextToken = kUninitialized;
        _precedingPosition = 0;
        _position = 0;
        _nextPosition = 0;
        _rewind = false;
        Next();
    }

    /// <summary>Debug-only in V8: a token's name (--trace-asm-parser).</summary>
    public string Name(int token)
    {
        if (token >= 32 && token < 127) return ((char)token).ToString();
        foreach (KeyValuePair<string, int> i in _localNames) if (i.Value == token) return i.Key;
        foreach (KeyValuePair<string, int> i in _globalNames) if (i.Value == token) return i.Key;
        foreach (KeyValuePair<string, int> i in _propertyNames) if (i.Value == token) return i.Key;
        return token switch
        {
            kToken_LE => "<=",
            kToken_GE => ">=",
            kToken_EQ => "==",
            kToken_NE => "!=",
            kToken_SHL => "<<",
            kToken_SAR => ">>",
            kToken_SHR => ">>>",
            kToken_UseAsm => "'use asm'",
            kUninitialized => "{uninitialized}",
            kEndOfInput => "{end of input}",
            kParseError => "{parse error}",
            kUnsigned => "{unsigned value}",
            kDouble => "{double value}",
            _ => "{unknown}",
        };
    }

    void ConsumeIdentifier(char ch)
    {
        // Consume characters while still part of the identifier.
        _buffer.Clear();
        _buffer.Append(ch);
        while (HasMoreChars() && IsIdentifierPart(PeekChar()))
        {
            _buffer.Append(NextChar());
        }
        _identifierString = _buffer.ToString();

        // Decode what the identifier means.
        if (_precedingToken == '.')
        {
            if (_propertyNames.TryGetValue(_identifierString, out int property))
            {
                _token = property;
                return;
            }
        }
        else
        {
            if (_localNames.TryGetValue(_identifierString, out int local))
            {
                _token = local;
                return;
            }
            if (!_inLocalScope && _globalNames.TryGetValue(_identifierString, out int global))
            {
                _token = global;
                return;
            }
        }
        if (_precedingToken == '.')
        {
            if (_globalCount >= kMaxIdentifierCount) throw new InvalidOperationException("too many asm.js identifiers");
            _token = kGlobalsStart + _globalCount++;
            _propertyNames[_identifierString] = _token;
        }
        else if (_inLocalScope)
        {
            if (_localNames.Count >= kMaxIdentifierCount) throw new InvalidOperationException("too many asm.js identifiers");
            _token = kLocalsStart - _localNames.Count;
            _localNames[_identifierString] = _token;
        }
        else
        {
            if (_globalCount >= kMaxIdentifierCount) throw new InvalidOperationException("too many asm.js identifiers");
            _token = kGlobalsStart + _globalCount++;
            _globalNames[_identifierString] = _token;
        }
    }

    static bool IsValidImplicitOctal(string number)
    {
        for (int i = 1; i < number.Length; i++)
        {
            if ((uint)(number[i] - '0') > 7) return false;
        }
        return true;
    }

    void ConsumeNumber(char ch)
    {
        _buffer.Clear();
        _buffer.Append(ch);
        bool hasDot = ch == '.';
        bool hasPrefix = false;
        while (HasMoreChars())
        {
            ch = PeekChar();
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F') || ch == '.' || ch == 'b' || ch == 'o' ||
                ch == 'x' ||
                ((ch == '-' || ch == '+') && !hasPrefix &&
                 (_buffer[^1] == 'e' || _buffer[^1] == 'E')))
            {
                // TODO(bradnelson): Test weird cases ending in -.
                if (ch == '.') hasDot = true;
                if (ch == 'b' || ch == 'o' || ch == 'x') hasPrefix = true;
                _buffer.Append(ch);
                Advance();
            }
            else
            {
                break;
            }
        }
        string number = _buffer.ToString();
        // Special case the most common number.
        if (number.Length == 1 && number[0] == '0')
        {
            _unsignedValue = 0;
            _token = kUnsigned;
            return;
        }
        // Pick out dot.
        if (number.Length == 1 && number[0] == '.')
        {
            _token = '.';
            return;
        }
        // Decode numbers, with separate paths for prefixes and implicit octals.
        if (hasPrefix && number[0] == '0')
        {
            // "0[xob]" by itself is a parse error.
            if (number.Length <= 2)
            {
                _token = kParseError;
                return;
            }
            switch (number[1])
            {
                case 'b':
                    _doubleValue = Conversions.BinaryStringToDouble(number);
                    break;
                case 'o':
                    _doubleValue = Conversions.OctalStringToDouble(number);
                    break;
                case 'x':
                    _doubleValue = Conversions.HexStringToDouble(number);
                    break;
                default:
                    // If there is a prefix character, but it's not the second character,
                    // then there's a parse error somewhere.
                    _token = kParseError;
                    break;
            }
        }
        else if (number[0] == '0' && !hasPrefix && IsValidImplicitOctal(number))
        {
            _doubleValue = Conversions.ImplicitOctalStringToDouble(number);
        }
        else
        {
            _doubleValue = Conversions.StringToDouble(number, ConversionFlag.NoConversionFlag);
        }
        if (double.IsNaN(_doubleValue))
        {
            // Check if string to number conversion didn't consume all the characters.
            // This happens if the character filter let through something invalid
            // like: 0123ef for example.
            // TODO(bradnelson): Check if this happens often enough to be a perf
            // problem.
            if (number[0] == '.')
            {
                int rewindBy = number.Length - 1;
                _inputPosition -= rewindBy;
                _token = '.';
                return;
            }
            // Anything else that doesn't parse is an error.
            _token = kParseError;
            return;
        }
        if (hasDot || Math.Truncate(_doubleValue) != _doubleValue)
        {
            _token = kDouble;
        }
        else
        {
            // Exceeding safe integer range is an error.
            if (_doubleValue > uint.MaxValue)
            {
                _token = kParseError;
                return;
            }
            _unsignedValue = (uint)_doubleValue;
            _token = kUnsigned;
        }
    }

    bool ConsumeCComment()
    {
        while (HasMoreChars())
        {
            while (Consume('*'))
            {
                if (Consume('/')) return true;
            }
            if (Consume('\n') || Consume('\r'))
            {
                _precededByNewline = true;
            }
            else
            {
                Advance();
            }
        }
        return false;
    }

    void ConsumeCPPComment()
    {
        while (HasMoreChars())
        {
            if (Consume('\n') || Consume('\r'))
            {
                _precededByNewline = true;
                return;
            }
            Advance();
        }
    }

    void ConsumeString(char quote)
    {
        // Only string allowed is 'use asm' / "use asm".
        foreach (char expected in "use asm")
        {
            if (!Consume(expected))
            {
                _token = kParseError;
                return;
            }
        }
        if (!Consume(quote))
        {
            _token = kParseError;
            return;
        }
        _token = kToken_UseAsm;
    }

    void ConsumeCompareOrShift(char ch)
    {
        if (Consume('='))
        {
            _token = ch switch
            {
                '<' => kToken_LE,
                '>' => kToken_GE,
                '=' => kToken_EQ,
                _ => kToken_NE,
            };
        }
        else if (ch == '<' && Consume('<'))
        {
            _token = kToken_SHL;
        }
        else if (ch == '>' && Consume('>'))
        {
            _token = Consume('>') ? kToken_SHR : kToken_SAR;
        }
        else
        {
            _token = ch;
        }
    }

    static bool IsIdentifierStart(char ch) =>
        (uint)((ch | 0x20) - 'a') <= 'z' - 'a' || ch == '_' || ch == '$';

    static bool IsIdentifierPart(char ch) =>
        (uint)((ch | 0x20) - 'a') <= 'z' - 'a' || (uint)(ch - '0') <= 9 || ch == '$' || ch == '_';

    static bool IsNumberStart(char ch) => ch == '.' || (uint)(ch - '0') <= 9;
}
