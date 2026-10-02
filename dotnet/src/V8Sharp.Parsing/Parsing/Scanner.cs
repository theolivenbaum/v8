// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/scanner.h, scanner-inl.h, scanner.cc and
// keywords-gen.h (the gperf perfect hash).

using System.Runtime.CompilerServices;
using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Parsing.CharPredicates;

namespace V8Sharp.Parsing;

// regexp::Flag (src/regexp/regexp-flags.h).
[Flags]
public enum RegExpFlags
{
    None = 0,
    Global = 1 << 0,
    IgnoreCase = 1 << 1,
    Multiline = 1 << 2,
    Sticky = 1 << 3,
    Unicode = 1 << 4,
    DotAll = 1 << 5,
    Linear = 1 << 6,
    HasIndices = 1 << 7,
    UnicodeSets = 1 << 8,
}

// ----------------------------------------------------------------------------
// JavaScript Scanner.
public sealed class Scanner
{
    // Scoped helper for a re-settable bookmark.
    public struct BookmarkScope
    {
        private const long kNoBookmark = long.MaxValue - 1;
        private const long kBookmarkWasApplied = long.MaxValue;

        private readonly Scanner _scanner;
        private long _bookmark;
        private readonly bool _hadParserError;

        public BookmarkScope(Scanner scanner)
        {
            _scanner = scanner;
            _bookmark = kNoBookmark;
            _hadParserError = scanner.has_parser_error();
        }

        public void Set(int position)
        {
            System.Diagnostics.Debug.Assert(_bookmark == kNoBookmark);
            _bookmark = position;
        }

        public void Apply()
        {
            System.Diagnostics.Debug.Assert(HasBeenSet()); // Caller hasn't called SetBookmark.
            if (_hadParserError)
            {
                _scanner.set_parser_error();
            }
            else
            {
                _scanner.reset_parser_error_flag();
                _scanner.SeekNext((int)_bookmark);
            }
            _bookmark = kBookmarkWasApplied;
        }

        public readonly bool HasBeenSet() => _bookmark != kNoBookmark && _bookmark != kBookmarkWasApplied;
        public readonly bool HasBeenApplied() => _bookmark == kBookmarkWasApplied;
    }

    // Representation of an interval of source positions.
    public struct Location(int b, int e)
    {
        public int beg_pos = b;
        public int end_pos = e;

        public readonly int length() => end_pos - beg_pos;
        public readonly bool IsValid() => beg_pos >= 0 && beg_pos <= end_pos;
        public static Location invalid() => new(-1, -1);
    }

    // -1 is outside of the range of any real source code.
    public const int kEndOfInput = Utf16CharacterStream.kEndOfInput;
    public const int kInvalidSequence = -1;

    public static int Invalid() => kInvalidSequence;
    public static bool IsInvalid(int c) => c == kInvalidSequence;

    private enum NumberKind
    {
        IMPLICIT_OCTAL,
        BINARY,
        OCTAL,
        HEX,
        DECIMAL,
        DECIMAL_WITH_LEADING_ZERO,
    }

    // The current and look-ahead tokens.
    private sealed class TokenDesc
    {
        public Location location = new(0, 0);
        public readonly LiteralBuffer literal_chars = new();
        public readonly LiteralBuffer raw_literal_chars = new();
        public Token token = Token.Uninitialized;
        public MessageTemplate invalid_template_escape_message = MessageTemplate.None;
        public Location invalid_template_escape_location;
        public NumberKind number_kind;
        public uint smi_value;
        public bool after_line_terminator;
    }

    private static bool IsValidBigIntKind(NumberKind kind) => kind >= NumberKind.BINARY && kind <= NumberKind.DECIMAL;
    private static bool IsDecimalNumberKind(NumberKind kind) => kind >= NumberKind.DECIMAL && kind <= NumberKind.DECIMAL_WITH_LEADING_ZERO;

    private const int kCharacterLookaheadBufferSize = 1;
    private const int kMaxAscii = 127;

    // BigInt::kMaxBits (64-bit, pointer-size digits).
    private const int kBigIntMaxBits = ((1 << 30) - 1) / 64 * 64;

    private readonly UnoptimizedCompileFlags _flags;
    private readonly bool _enableExperimentalRegExpEngine;

    // Returns the token literal buffers' storage to the per-thread pool when a
    // parse is done (see LiteralBuffer.ReleaseBackingStore).
    public void ReleaseLiteralBuffers()
    {
        foreach (TokenDesc desc in (ReadOnlySpan<TokenDesc>)[_current, _next, _nextNext, _nextNextNext])
        {
            if (desc == null) continue;
            desc.literal_chars.ReleaseBackingStore();
            desc.raw_literal_chars.ReleaseBackingStore();
        }
    }

    private TokenDesc _current = null!;        // desc for current token (as returned by Next())
    private TokenDesc _next = null!;           // desc for next token (one token look-ahead)
    private TokenDesc _nextNext = null!;       // desc for the token after next (after peek())
    private TokenDesc _nextNextNext = null!;   // desc for the token after next of next (after PeekAhead())

    // Input stream. Must be initialized to an Utf16CharacterStream.
    private readonly Utf16CharacterStream _source;

    // One Unicode character look-ahead; c0_ < 0 at the end of the input.
    private int c0_;

    private readonly TokenDesc[] _tokenStorage = [new(), new(), new(), new()];

    // Whether this scanner encountered an HTML comment.
    private bool _foundHtmlComment;

    // Values parsed from magic comments.
    private readonly LiteralBuffer _sourceUrl = new();
    private readonly LiteralBuffer _sourceMappingUrl = new();
    private readonly LiteralBuffer _debugId = new();
    private bool _sawSourceMappingUrlMagicCommentAtSign;
    private bool _sawMagicCommentCompileHintsAll;
    private bool _sawNonComment;
    private List<int> _perFunctionCompileHintPositions = [];
    private int _perFunctionCompileHintPositionsIdx;

    // Last-seen positions of potentially problematic tokens.
    private Location _octalPos;
    private MessageTemplate _octalMessage;

    private MessageTemplate _scannerError;
    private Location _scannerErrorLocation;

    public Scanner(Utf16CharacterStream source, UnoptimizedCompileFlags flags, bool enable_experimental_regexp_engine = false)
    {
        _flags = flags;
        _source = source;
        _enableExperimentalRegExpEngine = enable_experimental_regexp_engine;
        _foundHtmlComment = false;
        _octalPos = Location.invalid();
        _octalMessage = MessageTemplate.None;
    }

    // Sets the Scanner into an error state to stop further scanning and terminate
    // the parsing by only returning kIllegal tokens after that.
    public void set_parser_error()
    {
        if (!has_parser_error())
        {
            c0_ = kEndOfInput;
            _source.set_parser_error();
            foreach (TokenDesc desc in _tokenStorage)
            {
                if (desc.token != Token.Uninitialized) desc.token = Token.Illegal;
            }
        }
    }

    public void reset_parser_error_flag() => _source.reset_parser_error_flag();

    public bool has_parser_error() => _source.has_parser_error();

    public void Initialize()
    {
        // Need to capture identifiers in order to recognize "get" and "set"
        // in object literals.
        Init();
        next().after_line_terminator = true;
        Scan();
    }

    // Call this after setting source_ to the input.
    private void Init()
    {
        // Set c0_ (one character ahead)
        Advance();

        _current = _tokenStorage[0];
        _next = _tokenStorage[1];
        _nextNext = _tokenStorage[2];
        _nextNextNext = _tokenStorage[3];

        _foundHtmlComment = false;
        _scannerError = MessageTemplate.None;
    }

    // Returns the current token again.
    public Token current_token() => _current.token;

    // Returns the location information for the current token
    // (the token last returned by Next()).
    public Location location() => _current.location;

    // This error is specifically an invalid hex or unicode escape sequence.
    public bool has_error() => _scannerError != MessageTemplate.None;
    public MessageTemplate error() => _scannerError;
    public Location error_location() => _scannerErrorLocation;

    public bool has_invalid_template_escape() => _current.invalid_template_escape_message != MessageTemplate.None;

    public MessageTemplate invalid_template_escape_message() => _current.invalid_template_escape_message;

    public void clear_invalid_template_escape_message() => _current.invalid_template_escape_message = MessageTemplate.None;

    public Location invalid_template_escape_location() => _current.invalid_template_escape_location;

    // One token look-ahead (past the token returned by Next()).
    public Token peek() => _next.token;

    public Location peek_location() => _next.location;

    public bool literal_contains_escapes() => LiteralContainsEscapes(_current);

    public bool next_literal_contains_escapes() => LiteralContainsEscapes(_next);

    public AstRawString CurrentSymbol(AstValueFactory ast_value_factory)
        => ast_value_factory.GetString(_current.literal_chars.literal(), _current.literal_chars.is_one_byte());

    public AstRawString NextSymbol(AstValueFactory ast_value_factory)
        => ast_value_factory.GetString(_next.literal_chars.literal(), _next.literal_chars.is_one_byte());

    public AstRawString CurrentRawSymbol(AstValueFactory ast_value_factory)
        => ast_value_factory.GetString(_current.raw_literal_chars.literal(), _current.raw_literal_chars.is_one_byte());

    public double DoubleValue()
    {
        ReadOnlySpan<char> literal = literal_one_byte_string();
        return _current.number_kind switch
        {
            NumberKind.IMPLICIT_OCTAL => NumberConversions.ImplicitOctalStringToDouble(literal),
            NumberKind.BINARY => NumberConversions.BinaryStringToDouble(literal),
            NumberKind.OCTAL => NumberConversions.OctalStringToDouble(literal),
            NumberKind.HEX => NumberConversions.HexStringToDouble(literal),
            _ => NumberConversions.StringToDouble(literal),
        };
    }

    // The literal of a BigInt token, e.g. "0x1f" or "123" (without the 'n').
    public ReadOnlySpan<char> BigIntLiteral() => literal_one_byte_string();

    public string CurrentLiteralAsCString() => _current.literal_chars.ToString();

    public bool CurrentMatches(Token token) => _current.token == token;

    public bool NextLiteralExactlyEquals(string s)
    {
        // The length of the token is used to make sure the literal equals without
        // taking escape sequences (e.g., "use \x73trict") or line continuations
        // (e.g., "use \(newline) strict") into account.
        if (!is_next_literal_one_byte()) return false;
        if (peek_location().length() != s.Length + 2) return false;
        return _next.literal_chars.literal().SequenceEqual(s);
    }

    public bool CurrentLiteralEquals(string s)
    {
        if (!is_literal_one_byte()) return false;
        return _current.literal_chars.literal().SequenceEqual(s);
    }

    // Returns the location of the last seen octal literal.
    public Location octal_position() => _octalPos;

    public void clear_octal_position()
    {
        _octalPos = Location.invalid();
        _octalMessage = MessageTemplate.None;
    }

    public MessageTemplate octal_message() => _octalMessage;

    // Returns the value of the last smi that was scanned.
    public uint smi_value() => _current.smi_value;

    // Returns true if there was a line terminator before the peek'ed token,
    // possibly inside a multi-line comment.
    public bool HasLineTerminatorBeforeNext() => _next.after_line_terminator;

    public bool HasLineTerminatorAfterNext()
    {
        PeekAhead();
        return _nextNext.after_line_terminator;
    }

    public bool HasLineTerminatorAfterNextNext()
    {
        PeekAheadAhead();
        return _nextNextNext.after_line_terminator;
    }

    // Scans the input as a template literal
    public Token ScanTemplateContinuation()
    {
        System.Diagnostics.Debug.Assert(_next.token == Token.RightBrace);
        return ScanTemplateSpan();
    }

    public string? SourceUrl() => _sourceUrl.length() > 0 ? _sourceUrl.Internalize() : null;
    public string? SourceMappingUrl() => _sourceMappingUrl.length() > 0 ? _sourceMappingUrl.Internalize() : null;
    public string? DebugId() => _debugId.length() > 0 ? _debugId.Internalize() : null;

    public bool SawSourceMappingUrlMagicCommentAtSign() => _sawSourceMappingUrlMagicCommentAtSign;
    public bool SawMagicCommentCompileHintsAll() => _sawMagicCommentCompileHintsAll;
    public bool FoundHtmlComment() => _foundHtmlComment;
    public Utf16CharacterStream stream() => _source;

    // The literal of the current token (V8: literal_one_byte_string() /
    // literal_two_byte_string(); both are the same code units here).
    public ReadOnlySpan<char> literal_one_byte_string() => _current.literal_chars.literal();
    public ReadOnlySpan<char> literal_two_byte_string() => _current.literal_chars.literal();
    public bool is_literal_one_byte() => _current.literal_chars.is_one_byte();
    public ReadOnlySpan<char> next_literal_one_byte_string() => _next.literal_chars.literal();
    public ReadOnlySpan<char> next_literal_two_byte_string() => _next.literal_chars.literal();
    public bool is_next_literal_one_byte() => _next.literal_chars.is_one_byte();
    public ReadOnlySpan<char> raw_literal_one_byte_string() => _current.raw_literal_chars.literal();
    public ReadOnlySpan<char> raw_literal_two_byte_string() => _current.raw_literal_chars.literal();
    public bool is_raw_literal_one_byte() => _current.raw_literal_chars.is_one_byte();

    // ------------------------------------------------------------------------

    private void ReportScannerError(Location location, MessageTemplate error)
    {
        if (has_error()) return;
        _scannerError = error;
        _scannerErrorLocation = location;
    }

    private void ReportScannerError(int pos, MessageTemplate error)
    {
        if (has_error()) return;
        _scannerError = error;
        _scannerErrorLocation = new Location(pos, pos + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddLiteralChar(int c) => _next.literal_chars.AddChar(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddRawLiteralChar(int c) => _next.raw_literal_chars.AddChar(c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddLiteralCharAdvance()
    {
        AddLiteralChar(c0_);
        Advance();
    }

    // Low-level scanning support.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Advance() => c0_ = _source.Advance();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Advance(bool capture_raw)
    {
        if (capture_raw) AddRawLiteralChar(c0_);
        c0_ = _source.Advance();
    }

    private bool CombineSurrogatePair()
    {
        if (Utf16.IsLeadSurrogate(c0_))
        {
            int c1 = _source.Advance();
            if (Utf16.IsTrailSurrogate(c1))
            {
                c0_ = Utf16.CombineSurrogatePair(c0_, c1);
                return true;
            }
            _source.Back();
        }
        return false;
    }

    private void PushBack(int ch)
    {
        _source.Back();
        c0_ = ch;
    }

    private int Peek() => _source.Peek();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Token Select(Token tok)
    {
        Advance();
        return tok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Token Select(int next, Token then, Token else_)
    {
        Advance();
        if (c0_ == next)
        {
            Advance();
            return then;
        }
        return else_;
    }

    // Return the current source position.
    public int source_pos() => _source.pos() - kCharacterLookaheadBufferSize;

    private static bool LiteralContainsEscapes(TokenDesc token)
    {
        Location location = token.location;
        int source_length = location.end_pos - location.beg_pos;
        if (token.token == Token.String)
        {
            // Subtract delimiters.
            source_length -= 2;
        }
        return token.literal_chars.length() != source_length;
    }

    private TokenDesc next() => _next;

    // ------------------------------------------------------------------------
    // Keyword Matcher (keywords-gen.h, generated by gperf).

    private static readonly byte[] s_assoValues =
    [
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65,
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65,
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65,
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65,
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65,
        65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 33, 0, 24, 18, 17,
        0, 31, 65, 15, 33, 65, 0, 25, 24, 14, 1, 65, 0, 10, 3, 36, 4,
        23, 26, 13, 1, 65, 65, 65, 65, 65, 65,
    ];

    private static readonly (string Name, Token Value)[] s_perfectKeywordHashTable = BuildKeywordTable();

    private static (string, Token)[] BuildKeywordTable()
    {
        var table = new (string, Token)[128];
        for (int i = 0; i < table.Length; i++) table[i] = ("", Token.Identifier);
        (string, Token)[] entries =
        [
            ("let", Token.Let), ("for", Token.For), ("false", Token.FalseLiteral), ("return", Token.Return),
            ("var", Token.Var), ("package", Token.FutureStrictReservedWord), ("void", Token.Void),
            ("typeof", Token.TypeOf), ("public", Token.FutureStrictReservedWord), ("function", Token.Function),
            ("set", Token.Set), ("break", Token.Break), ("try", Token.Try), ("true", Token.TrueLiteral),
            ("private", Token.FutureStrictReservedWord), ("super", Token.Super),
            ("protected", Token.FutureStrictReservedWord), ("do", Token.Do), ("this", Token.This),
            ("throw", Token.Throw), ("delete", Token.Delete), ("default", Token.Default),
            ("debugger", Token.Debugger), ("new", Token.New), ("case", Token.Case), ("catch", Token.Catch),
            ("const", Token.Const), ("in", Token.In), ("null", Token.NullLiteral), ("continue", Token.Continue),
            ("get", Token.Get), ("enum", Token.Enum), ("export", Token.Export), ("extends", Token.Extends),
            ("interface", Token.FutureStrictReservedWord), ("instanceof", Token.InstanceOf),
            ("finally", Token.Finally), ("async", Token.Async), ("switch", Token.Switch), ("while", Token.While),
            ("using", Token.Using), ("import", Token.Import), ("else", Token.Else), ("of", Token.Of),
            ("if", Token.If), ("implements", Token.FutureStrictReservedWord), ("yield", Token.Yield),
            ("static", Token.Static), ("class", Token.Class), ("accessor", Token.Accessor), ("with", Token.With),
            ("await", Token.Await),
        ];
        foreach (var (name, value) in entries)
        {
            uint key = PerfectKeywordHash(name) & 0x7f;
            System.Diagnostics.Debug.Assert(table[key].Item1.Length == 0);
            table[key] = (name, value);
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PerfectKeywordHash(ReadOnlySpan<char> str)
        => (uint)(str.Length + s_assoValues[(byte)(str[1] + 1)] + s_assoValues[(byte)str[0]]);

    private const int MIN_WORD_LENGTH = 2;
    private const int MAX_WORD_LENGTH = 10;

    // KeywordOrIdentifierToken / PerfectKeywordHash::GetToken.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Token KeywordOrIdentifierToken(ReadOnlySpan<char> input)
    {
        int len = input.Length;
        if ((uint)(len - MIN_WORD_LENGTH) <= MAX_WORD_LENGTH - MIN_WORD_LENGTH)
        {
            // Keyword characters are all ASCII; anything else cannot match and
            // must not index the table out of range.
            if (input[0] >= 128 || input[1] >= 128) return Token.Identifier;
            uint key = PerfectKeywordHash(input) & 0x7f;
            var entry = s_perfectKeywordHashTable[key];
            if (len == entry.Name.Length && input.SequenceEqual(entry.Name))
            {
                return entry.Value;
            }
        }
        return Token.Identifier;
    }

    private const string kKeywordCharacters =
        "asyncawaitbreakcasecatchclassconstcontinuedebuggerdefaultdeletedoelseenumexportextends" +
        "falsefinallyforfunctiongetifimplementsimportininstanceofinterfaceletnewnullofpackage" +
        "privateprotectedpublicreturnsetstaticsuperswitchthisthrowtruetrytypeofusingvarvoidwhilewithyield";

    private static bool IsKeywordStart(int c) =>
        c == 'a' || c == 'b' || c == 'c' || c == 'd' || c == 'e' || c == 'f' || c == 'g' || c == 'i' ||
        c == 'l' || c == 'n' || c == 'o' || c == 'p' || c == 'r' || c == 's' || c == 't' || c == 'u' ||
        c == 'v' || c == 'w' || c == 'y';

    private static bool CanBeKeywordCharacter(int c) => kKeywordCharacters.Contains((char)c);

    // Character flags for the fast path of scanning a keyword or identifier token.
    private const byte kTerminatesLiteral = 1 << 0;
    // "Cannot" rather than "can" so that this flag can be ORed together across
    // multiple characters.
    private const byte kCannotBeKeyword = 1 << 1;
    private const byte kCannotBeKeywordStart = 1 << 2;
    private const byte kStringTerminator = 1 << 3;
    private const byte kIdentifierNeedsSlowPath = 1 << 4;
    private const byte kMultilineCommentCharacterNeedsSlowPath = 1 << 5;

    private static byte GetScanFlags(int c) => (byte)(
        // Keywords are all lowercase and only contain letters.
        // Note that non-identifier characters do not set this flag, so
        // that it plays well with kTerminatesLiteral.
        (IsAsciiIdentifier(c) && !CanBeKeywordCharacter(c) ? kCannotBeKeyword : 0) |
        (IsKeywordStart(c) ? 0 : kCannotBeKeywordStart) |
        // Anything that isn't an identifier character will terminate the
        // literal, or at least terminates the literal fast path processing
        // (like an escape).
        (!IsAsciiIdentifier(c) ? kTerminatesLiteral : 0) |
        // Possible string termination characters.
        ((c == '\'' || c == '"' || c == '\n' || c == '\r' || c == '\\') ? kStringTerminator : 0) |
        // Escapes are processed on the slow path.
        (c == '\\' ? kIdentifierNeedsSlowPath : 0) |
        // Newlines and * are interesting characters for multiline comment
        // scanning.
        (c == '\n' || c == '\r' || c == '*' ? kMultilineCommentCharacterNeedsSlowPath : 0));

    private static bool TerminatesLiteral(byte scan_flags) => (scan_flags & kTerminatesLiteral) != 0;
    private static bool CanBeKeyword(byte scan_flags) => (scan_flags & kCannotBeKeyword) == 0;
    private static bool IdentifierNeedsSlowPath(byte scan_flags) => (scan_flags & kIdentifierNeedsSlowPath) != 0;
    private static bool MultilineCommentCharacterNeedsSlowPath(byte scan_flags) => (scan_flags & kMultilineCommentCharacterNeedsSlowPath) != 0;
    private static bool MayTerminateString(byte scan_flags) => (scan_flags & kStringTerminator) != 0;

    // Table of precomputed scan flags for the 128 ASCII characters, for branchless
    // flag calculation during the scan.
    private static readonly byte[] s_characterScanFlags = BuildScanFlags();

    private static byte[] BuildScanFlags()
    {
        var t = new byte[128];
        for (int i = 0; i < 128; i++) t[i] = GetScanFlags(i);
        return t;
    }

    private static bool CharCanBeKeyword(int c) => (uint)c < 128 && CanBeKeyword(s_characterScanFlags[c]);

    // Get the shortest token that this character starts, the token may change
    // depending on subsequent characters.
    private static Token GetOneCharToken(int c) => c switch
    {
        '(' => Token.LeftParen,
        ')' => Token.RightParen,
        '{' => Token.LeftBrace,
        '}' => Token.RightBrace,
        '[' => Token.LeftBracket,
        ']' => Token.RightBracket,
        '?' => Token.Conditional,
        ':' => Token.Colon,
        ';' => Token.Semicolon,
        ',' => Token.Comma,
        '.' => Token.Period,
        '|' => Token.BitOr,
        '&' => Token.BitAnd,
        '^' => Token.BitXor,
        '~' => Token.BitNot,
        '!' => Token.Not,
        '<' => Token.LessThan,
        '>' => Token.GreaterThan,
        '%' => Token.Mod,
        '=' => Token.Assign,
        '+' => Token.Add,
        '-' => Token.Sub,
        '*' => Token.Mul,
        '/' => Token.Div,
        '#' => Token.PrivateName,
        '"' => Token.String,
        '\'' => Token.String,
        '`' => Token.TemplateSpan,
        '\\' => Token.Identifier,
        // Whitespace or line terminator
        ' ' or '\t' or '\v' or '\f' or '\r' or '\n' => Token.Whitespace,
        // IsDecimalDigit must be tested before IsAsciiIdentifier
        _ when IsDecimalDigit(c) => Token.Number,
        _ when IsAsciiIdentifier(c) => Token.Identifier,
        _ => Token.Illegal,
    };

    // Table of one-character tokens, by character (0x00..0x7F only).
    private static readonly Token[] s_oneCharTokens = BuildOneCharTokens();

    private static Token[] BuildOneCharTokens()
    {
        var t = new Token[128];
        for (int i = 0; i < 128; i++) t[i] = GetOneCharToken(i);
        return t;
    }

    // ------------------------------------------------------------------------
    // Token scanning.

    // Returns the next token and advances input.
    public Token Next()
    {
        // Rotate through tokens.
        TokenDesc previous = _current;
        _current = _next;
        // Either we already have the next token lined up, in which case next_next_
        // simply becomes next_. In that case we use current_ as new next_next_ and
        // clear its token to indicate that it wasn't scanned yet. Otherwise we use
        // current_ as next_ and scan into it, leaving next_next_ uninitialized.
        if (_nextNext.token == Token.Uninitialized)
        {
            _next = previous;
            previous.after_line_terminator = false;
            Scan(previous);
        }
        else
        {
            _next = _nextNext;

            if (_nextNextNext.token == Token.Uninitialized)
            {
                _nextNext = previous;
            }
            else
            {
                _nextNext = _nextNextNext;
                _nextNextNext = previous;
            }

            previous.token = Token.Uninitialized;
        }
        return _current.token;
    }

    // Returns the token following peek()
    public Token PeekAhead()
    {
        if (_nextNext.token != Token.Uninitialized)
        {
            return _nextNext.token;
        }
        TokenDesc temp = _next;
        _next = _nextNext;
        _next.after_line_terminator = false;
        Scan();
        _nextNext = _next;
        _next = temp;
        return _nextNext.token;
    }

    // Returns the token following PeekAhead()
    public Token PeekAheadAhead()
    {
        if (_nextNextNext.token != Token.Uninitialized)
        {
            return _nextNextNext.token;
        }
        // PeekAhead() must be called first in order to call PeekAheadAhead().
        TokenDesc temp = _next;
        TokenDesc temp_next = _nextNext;
        _next = _nextNextNext;
        _next.after_line_terminator = false;
        Scan();
        _nextNextNext = _next;
        _nextNext = temp_next;
        _next = temp;
        return _nextNextNext.token;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Scan() => Scan(_next);

    private void Scan(TokenDesc next_desc)
    {
        next_desc.token = ScanSingleToken();
        next_desc.location.end_pos = source_pos();
    }

    private Token ScanSingleToken()
    {
        bool old_saw_non_comment = _sawNonComment;
        // Assume the token we'll parse is not a comment; if it is, saw_non_comment_
        // is restored.
        _sawNonComment = true;
        Token token;
        do
        {
            _next.location.beg_pos = source_pos();

            if ((uint)c0_ <= kMaxAscii)
            {
                token = s_oneCharTokens[c0_];

                switch (token)
                {
                    case Token.LeftParen:
                    case Token.RightParen:
                    case Token.LeftBrace:
                    case Token.RightBrace:
                    case Token.LeftBracket:
                    case Token.RightBracket:
                    case Token.Colon:
                    case Token.Semicolon:
                    case Token.Comma:
                    case Token.BitNot:
                    case Token.Illegal:
                        // One character tokens.
                        return Select(token);

                    case Token.Conditional:
                        // ? ?. ?? ??=
                        Advance();
                        if (c0_ == '.')
                        {
                            Advance();
                            if (!IsDecimalDigit(c0_)) return Token.QuestionPeriod;
                            PushBack('.');
                        }
                        else if (c0_ == '?')
                        {
                            return Select('=', Token.AssignNullish, Token.Nullish);
                        }
                        return Token.Conditional;

                    case Token.String:
                        return ScanString();

                    case Token.LessThan:
                        // < <= << <<= <!--
                        Advance();
                        if (c0_ == '=') return Select(Token.LessThanEq);
                        if (c0_ == '<') return Select('=', Token.AssignShl, Token.Shl);
                        if (c0_ == '!')
                        {
                            token = ScanHtmlComment();
                            continue;
                        }
                        return Token.LessThan;

                    case Token.GreaterThan:
                        // > >= >> >>= >>> >>>=
                        Advance();
                        if (c0_ == '=') return Select(Token.GreaterThanEq);
                        if (c0_ == '>')
                        {
                            // >> >>= >>> >>>=
                            Advance();
                            if (c0_ == '=') return Select(Token.AssignSar);
                            if (c0_ == '>') return Select('=', Token.AssignShr, Token.Shr);
                            return Token.Sar;
                        }
                        return Token.GreaterThan;

                    case Token.Assign:
                        // = == === =>
                        Advance();
                        if (c0_ == '=') return Select('=', Token.EqStrict, Token.Eq);
                        if (c0_ == '>') return Select(Token.Arrow);
                        return Token.Assign;

                    case Token.Not:
                        // ! != !==
                        Advance();
                        if (c0_ == '=')
                        {
                            return Select('=', Token.NotEqStrict, Token.NotEq);
                        }
                        return Token.Not;

                    case Token.Add:
                        // + ++ +=
                        Advance();
                        if (c0_ == '+') return Select(Token.Inc);
                        if (c0_ == '=') return Select(Token.AssignAdd);
                        return Token.Add;

                    case Token.Sub:
                        // - -- --> -=
                        Advance();
                        if (c0_ == '-')
                        {
                            Advance();
                            if (c0_ == '>' && _next.after_line_terminator)
                            {
                                // For compatibility with SpiderMonkey, we skip lines that
                                // start with an HTML comment end '-->'.
                                token = SkipSingleHTMLComment();
                                continue;
                            }
                            return Token.Dec;
                        }
                        if (c0_ == '=') return Select(Token.AssignSub);
                        return Token.Sub;

                    case Token.Mul:
                        // * *=
                        Advance();
                        if (c0_ == '*') return Select('=', Token.AssignExp, Token.Exp);
                        if (c0_ == '=') return Select(Token.AssignMul);
                        return Token.Mul;

                    case Token.Mod:
                        // % %=
                        return Select('=', Token.AssignMod, Token.Mod);

                    case Token.Div:
                        // /  // /* /=
                        Advance();
                        if (c0_ == '/')
                        {
                            _sawNonComment = old_saw_non_comment;
                            int c = Peek();
                            if (c == '#' || c == '@')
                            {
                                Advance();
                                Advance();
                                token = SkipMagicComment(c);
                                continue;
                            }
                            token = SkipSingleLineComment();
                            continue;
                        }
                        if (c0_ == '*')
                        {
                            _sawNonComment = old_saw_non_comment;
                            token = SkipMultiLineComment();
                            continue;
                        }
                        if (c0_ == '=') return Select(Token.AssignDiv);
                        return Token.Div;

                    case Token.BitAnd:
                        // & && &= &&=
                        Advance();
                        if (c0_ == '&') return Select('=', Token.AssignAnd, Token.And);
                        if (c0_ == '=') return Select(Token.AssignBitAnd);
                        return Token.BitAnd;

                    case Token.BitOr:
                        // | || |= ||=
                        Advance();
                        if (c0_ == '|') return Select('=', Token.AssignOr, Token.Or);
                        if (c0_ == '=') return Select(Token.AssignBitOr);
                        return Token.BitOr;

                    case Token.BitXor:
                        // ^ ^=
                        return Select('=', Token.AssignBitXor, Token.BitXor);

                    case Token.Period:
                        // . Number
                        Advance();
                        if (IsDecimalDigit(c0_)) return ScanNumber(true);
                        if (c0_ == '.')
                        {
                            if (Peek() == '.')
                            {
                                Advance();
                                Advance();
                                return Token.Ellipsis;
                            }
                        }
                        return Token.Period;

                    case Token.TemplateSpan:
                        Advance();
                        return ScanTemplateSpan();

                    case Token.PrivateName:
                        if (source_pos() == 0 && Peek() == '!')
                        {
                            token = SkipSingleLineComment();
                            continue;
                        }
                        return ScanPrivateName();

                    case Token.Whitespace:
                        token = SkipWhiteSpace();
                        continue;

                    case Token.Number:
                        return ScanNumber(false);

                    case Token.Identifier:
                        return ScanIdentifierOrKeyword();

                    default:
                        throw new InvalidOperationException("UNREACHABLE");
                }
            }

            if (IsIdentifierStart(c0_) || (CombineSurrogatePair() && IsIdentifierStart(c0_)))
            {
                return ScanIdentifierOrKeyword();
            }
            if (c0_ == kEndOfInput)
            {
                return _source.has_parser_error() ? Token.Illegal : Token.Eos;
            }
            token = SkipWhiteSpace();

            // Continue scanning for tokens as long as we're just skipping whitespace.
        } while (token == Token.Whitespace);

        return token;
    }

    private struct WhiteSpaceCheck(Scanner scanner) : IAdvanceCheck
    {
        private int _hint = ' ';

        public bool Check(int c0)
        {
            if (c0 == _hint) return false;
            if (IsWhiteSpaceOrLineTerminator(c0))
            {
                if (!scanner._next.after_line_terminator && IsLineTerminator(c0))
                {
                    scanner._next.after_line_terminator = true;
                }
                _hint = c0;
                return false;
            }
            return true;
        }
    }

    private Token SkipWhiteSpace()
    {
        if (!IsWhiteSpaceOrLineTerminator(c0_)) return Token.Illegal;

        if (!_next.after_line_terminator && IsLineTerminator(c0_))
        {
            _next.after_line_terminator = true;
        }

        // Advance as long as character is a WhiteSpace or LineTerminator.
        var check = new WhiteSpaceCheck(this);
        c0_ = _source.AdvanceUntil(ref check);

        return Token.Whitespace;
    }

    private Token SkipSingleHTMLComment()
    {
        if (_flags.is_module())
        {
            ReportScannerError(source_pos(), MessageTemplate.HtmlCommentInModule);
            return Token.Illegal;
        }
        return SkipSingleLineComment();
    }

    private struct LineTerminatorCheck : IAdvanceCheck
    {
        public readonly bool Check(int c0) => IsLineTerminator(c0);
    }

    private Token SkipSingleLineComment()
    {
        // The line terminator at the end of the line is not considered
        // to be part of the single-line comment; it is recognized
        // separately by the lexical grammar and becomes part of the
        // stream of input elements for the syntactic grammar (see
        // ECMA-262, section 7.4).
        var check = new LineTerminatorCheck();
        c0_ = _source.AdvanceUntil(ref check);

        return Token.Whitespace;
    }

    private Token SkipMagicComment(int hash_or_at_sign)
    {
        TryToParseMagicComment(hash_or_at_sign);
        if (IsLineTerminator(c0_) || c0_ == kEndOfInput)
        {
            return Token.Whitespace;
        }
        return SkipSingleLineComment();
    }

    private static readonly sbyte[] s_vlqCharToDigit =
    [
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, 0x3e, -1, -1, -1, 0x3f,
        0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, -1, -1,
        -1, -1, -1, -1, -1, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06,
        0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10, 0x11, 0x12,
        0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, -1, -1, -1, -1, -1,
        -1, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24,
        0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f, 0x30,
        0x31, 0x32, 0x33, -1, -1, -1, -1, -1,
    ];

    // base::VLQBase64Decode (src/base/vlq-base64.cc).
    private static int VLQBase64Decode(ReadOnlySpan<char> start, ref int pos)
    {
        const int kContinueShift = 5;
        const int kContinueMask = 1 << kContinueShift;
        const int kDataMask = kContinueMask - 1;
        uint res = 0;
        int shift = 0;
        int digit;

        do
        {
            if (pos >= start.Length)
            {
                return int.MinValue;
            }
            char ch = start[pos];
            // charToDigitDecode takes a uint8_t.
            byte b = (byte)ch;
            digit = b < 128 ? s_vlqCharToDigit[b] : -1;
            bool is_last_byte = shift + kContinueShift >= 32;
            if (digit == -1 || (is_last_byte && (digit >> 2) != 0))
            {
                return int.MinValue;
            }
            res += shift < 32 ? (uint)(digit & kDataMask) << shift : 0;
            shift += kContinueShift;
            pos++;
        } while ((digit & kContinueMask) != 0);
        return (res & 1) != 0 ? -(int)(res >> 1) : (int)(res >> 1);
    }

    private static void ProcessPerFunctionCompileHints(ReadOnlySpan<char> data, int current_position, List<int> positions)
    {
        // Compile hints are relative to the position of the comment end.
        int last_position = current_position;
        int pos = 0;
        while (pos < data.Length)
        {
            int delta = VLQBase64Decode(data, ref pos);
            if (delta == int.MinValue)
            {
                // Invalid data, bail out and clear the data we read so far.
                positions.Clear();
                return;
            }
            last_position += delta;
            positions.Add(last_position);
        }
    }

    private void TryToParseMagicComment(int hash_or_at_sign)
    {
        // Magic comments are of the form: //[#@]\s<name>=\s*<value>\s*.* and this
        // function will just return if it cannot parse a magic comment.
        if (!IsWhiteSpace(c0_)) return;
        Advance();
        var name = new LiteralBuffer();
        name.Start();

        while (c0_ != kEndOfInput && !IsWhiteSpaceOrLineTerminator(c0_) && c0_ != '=')
        {
            name.AddChar(c0_);
            Advance();
        }
        if (!name.is_one_byte()) return;
        ReadOnlySpan<char> name_literal = name.one_byte_literal();
        LiteralBuffer value;
        LiteralBuffer? per_function_compile_hints_value = null;
        if (name_literal.SequenceEqual("sourceURL"))
        {
            value = _sourceUrl;
        }
        else if (name_literal.SequenceEqual("sourceMappingURL"))
        {
            value = _sourceMappingUrl;
            _sawSourceMappingUrlMagicCommentAtSign = hash_or_at_sign == '@';
        }
        else if (!_sawNonComment && name_literal.SequenceEqual("allFunctionsCalledOnLoad") &&
                 hash_or_at_sign == '#' && c0_ != '=')
        {
            _sawMagicCommentCompileHintsAll = true;
            // V8 falls through with `value` unset and returns at the `c0_ != '='`
            // check below, which is guaranteed by the condition above.
            return;
        }
        else if (name_literal.SequenceEqual("functionsCalledOnLoad") && hash_or_at_sign == '#')
        {
            per_function_compile_hints_value = new LiteralBuffer();
            value = per_function_compile_hints_value;
        }
        else if (name_literal.SequenceEqual("debugId") && hash_or_at_sign == '#')
        {
            value = _debugId;
        }
        else
        {
            return;
        }
        if (c0_ != '=')
        {
            return;
        }
        value.Start();
        Advance();
        while (IsWhiteSpace(c0_))
        {
            Advance();
        }
        while (c0_ != kEndOfInput && !IsLineTerminator(c0_))
        {
            if (IsWhiteSpace(c0_))
            {
                break;
            }
            value.AddChar(c0_);
            Advance();
        }
        // Allow whitespace at the end.
        while (c0_ != kEndOfInput && !IsLineTerminator(c0_))
        {
            if (!IsWhiteSpace(c0_))
            {
                value.Start();
                break;
            }
            Advance();
        }
        if (value == per_function_compile_hints_value && per_function_compile_hints_value.is_one_byte())
        {
            _perFunctionCompileHintPositions.Clear();
            _perFunctionCompileHintPositionsIdx = 0;
            ProcessPerFunctionCompileHints(per_function_compile_hints_value.one_byte_literal(), source_pos(),
                _perFunctionCompileHintPositions);
        }
    }

    public bool HasPerFunctionCompileHint(int position)
    {
        // Allow off-by-<slack> in the compile hints positions, to account for adding
        // newlines at the end of the comment, function positions being off-by-one,
        // etc.
        const int kSlack = 3;
        while (_perFunctionCompileHintPositionsIdx < _perFunctionCompileHintPositions.Count &&
               _perFunctionCompileHintPositions[_perFunctionCompileHintPositionsIdx] < position - kSlack)
        {
            ++_perFunctionCompileHintPositionsIdx;
        }
        if (_perFunctionCompileHintPositionsIdx >= _perFunctionCompileHintPositions.Count)
        {
            return false;
        }
        int hint_position = _perFunctionCompileHintPositions[_perFunctionCompileHintPositionsIdx];
        return hint_position >= position - kSlack && hint_position <= position + kSlack;
    }

    private struct MultilineCommentFirstLineCheck : IAdvanceCheck
    {
        public readonly bool Check(int c0)
        {
            if ((uint)c0 > kMaxAscii)
            {
                return IsLineTerminator(c0);
            }
            return MultilineCommentCharacterNeedsSlowPath(s_characterScanFlags[c0]);
        }
    }

    private struct StarCheck : IAdvanceCheck
    {
        public readonly bool Check(int c0) => c0 == '*';
    }

    private Token SkipMultiLineComment()
    {
        // Until we see the first newline, check for * and newline characters.
        if (!_next.after_line_terminator)
        {
            do
            {
                var check = new MultilineCommentFirstLineCheck();
                c0_ = _source.AdvanceUntil(ref check);

                while (c0_ == '*')
                {
                    Advance();
                    if (c0_ == '/')
                    {
                        Advance();
                        return Token.Whitespace;
                    }
                }

                if (IsLineTerminator(c0_))
                {
                    _next.after_line_terminator = true;
                    break;
                }
            } while (c0_ != kEndOfInput);
        }

        // After we've seen newline, simply try to find '*/'.
        while (c0_ != kEndOfInput)
        {
            var check = new StarCheck();
            c0_ = _source.AdvanceUntil(ref check);

            while (c0_ == '*')
            {
                Advance();
                if (c0_ == '/')
                {
                    Advance();
                    return Token.Whitespace;
                }
            }
        }

        return Token.Illegal;
    }

    // Scans a possible HTML comment -- begins with '<!'.
    private Token ScanHtmlComment()
    {
        // Check for <!-- comments.
        Advance();
        if (c0_ != '-' || Peek() != '-')
        {
            PushBack('!'); // undo Advance()
            return Token.LessThan;
        }
        Advance();

        _foundHtmlComment = true;
        return SkipSingleHTMLComment();
    }

    // Seek forward to the given position.  This operation does not
    // work in general, for instance when there are pushed back
    // characters, but works for seeking forward until simple delimiter
    // tokens, which is what it is used for.
    public void SeekForward(int pos)
    {
        // After this call, we will have the token at the given position as
        // the "next" token. The "current" token will be invalid.
        if (pos == _next.location.beg_pos) return;
        int current_pos = source_pos();
        // Positions inside the lookahead token aren't supported.
        System.Diagnostics.Debug.Assert(pos >= current_pos);
        if (pos != current_pos)
        {
            _source.Seek(pos);
            Advance();
            // This function is only called to seek to the location
            // of the end of a function (at the "}" token). It doesn't matter
            // whether there was a line terminator in the part we skip.
            _next.after_line_terminator = false;
        }
        Scan();
    }

    // Scans an escape-sequence which is part of a string and adds the
    // decoded character to the current literal. Returns true if a pattern
    // is scanned.
    private bool ScanEscape(bool capture_raw)
    {
        int c = c0_;
        Advance(capture_raw);

        // Skip escaped newlines.
        if (!capture_raw && IsLineTerminator(c))
        {
            // Allow escaped CR+LF newlines in multiline string literals.
            if (IsCarriageReturn(c) && IsLineFeed(c0_)) Advance();
            return true;
        }

        switch (c)
        {
            case 'b': c = '\b'; break;
            case 'f': c = '\f'; break;
            case 'n': c = '\n'; break;
            case 'r': c = '\r'; break;
            case 't': c = '\t'; break;
            case 'u':
                c = ScanUnicodeEscape(capture_raw);
                if (IsInvalid(c)) return false;
                break;
            case 'v':
                c = '\v';
                break;
            case 'x':
                c = ScanHexNumber(capture_raw, false, 2);
                if (IsInvalid(c)) return false;
                break;
            case '0':
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
                c = ScanOctalEscape(capture_raw, c, 2);
                break;
            case '8':
            case '9':
                // '\8' and '\9' are disallowed in strict mode.
                // Reuse the octal error state to propagate the error.
                _octalPos = new Location(source_pos() - 2, source_pos() - 1);
                _octalMessage = capture_raw ? MessageTemplate.Template8Or9Escape : MessageTemplate.Strict8Or9Escape;
                break;
        }

        // Other escaped characters are interpreted as their non-escaped version.
        AddLiteralChar(c);
        return true;
    }

    // Scans octal escape sequence. Also accepts "\0" decimal escape sequence.
    private int ScanOctalEscape(bool capture_raw, int c, int length)
    {
        int x = c - '0';
        int i = 0;
        for (; i < length; i++)
        {
            int d = c0_ - '0';
            if (d < 0 || d > 7) break;
            int nx = x * 8 + d;
            if (nx >= 256) break;
            x = nx;
            Advance(capture_raw);
        }
        // Anything except '\0' is an octal escape sequence, illegal in strict mode.
        // Remember the position of octal escape sequences so that an error
        // can be reported later (in strict mode).
        // We don't report the error immediately, because the octal escape can
        // occur before the "use strict" directive.
        if (c != '0' || i > 0 || IsNonOctalDecimalDigit(c0_))
        {
            _octalPos = new Location(source_pos() - i - 1, source_pos() - 1);
            _octalMessage = capture_raw ? MessageTemplate.TemplateOctalLiteral : MessageTemplate.StrictOctalEscape;
        }
        return x;
    }

    private int ScanHexNumber(bool capture_raw, bool unicode, int expected_length)
    {
        int begin = source_pos() - 2;
        int x = 0;
        for (int i = 0; i < expected_length; i++)
        {
            int d = HexValue(c0_);
            if (d < 0)
            {
                ReportScannerError(new Location(begin, begin + expected_length + 2),
                    unicode ? MessageTemplate.InvalidUnicodeEscapeSequence : MessageTemplate.InvalidHexEscapeSequence);
                return Invalid();
            }
            x = x * 16 + d;
            Advance(capture_raw);
        }

        return x;
    }

    // Scan a number of any length but not bigger than max_value. For example, the
    // number can be 000000001, so it's very long in characters but its value is
    // small.
    private int ScanUnlimitedLengthHexNumber(bool capture_raw, int max_value, int beg_pos)
    {
        int x = 0;
        int d = HexValue(c0_);
        if (d < 0) return Invalid();

        while (d >= 0)
        {
            x = x * 16 + d;
            if (x > max_value)
            {
                ReportScannerError(new Location(beg_pos, source_pos() + 1), MessageTemplate.UndefinedUnicodeCodePoint);
                return Invalid();
            }
            Advance(capture_raw);
            d = HexValue(c0_);
        }

        return x;
    }

    private struct StringCheck(Scanner scanner) : IAdvanceRangeCheck
    {
        public readonly bool Check(int c0)
        {
            if ((uint)c0 > kMaxAscii) return true;
            return MayTerminateString(s_characterScanFlags[c0]);
        }

        public readonly void OnRange(ReadOnlySpan<char> range) => scanner._next.literal_chars.AddRangeFromUtf16(range);
    }

    private Token ScanString()
    {
        int quote = c0_;

        _next.literal_chars.Start();
        while (true)
        {
            // Consumed ASCII characters are bulk-appended to the literal buffer via
            // the range callback. Non-ASCII characters terminate the range scan and
            // are handled by the outer loop below (via the final AddLiteralChar),
            // which keeps the common all-ASCII scan fast.
            var check = new StringCheck(this);
            c0_ = _source.AdvanceUntilRange(ref check);

            while (c0_ == '\\')
            {
                Advance();
                // TODO(verwaest): Check whether we can remove the additional check.
                if (c0_ == kEndOfInput || !ScanEscape(false))
                {
                    return Token.Illegal;
                }
            }

            if (c0_ == quote)
            {
                Advance();
                return Token.String;
            }

            if (c0_ == kEndOfInput || IsStringLiteralLineTerminator(c0_))
            {
                return Token.Illegal;
            }

            AddLiteralChar(c0_);
        }
    }

    private Token ScanPrivateName()
    {
        _next.literal_chars.Start();
        int pos = source_pos();
        Advance();
        if (IsIdentifierStart(c0_) || (CombineSurrogatePair() && IsIdentifierStart(c0_)))
        {
            AddLiteralChar('#');
            Token token = ScanIdentifierOrKeywordInner();
            return token == Token.Illegal ? Token.Illegal : Token.PrivateName;
        }

        ReportScannerError(pos, MessageTemplate.InvalidOrUnexpectedToken);
        return Token.Illegal;
    }

    private Token ScanTemplateSpan()
    {
        // When scanning a TemplateSpan, we are looking for the following construct:
        // kTemplateSpan ::
        //     ` LiteralChars* ${
        //   | } LiteralChars* ${
        //
        // kTemplateTail ::
        //     ` LiteralChars* `
        //   | } LiteralChar* `
        //
        // A kTemplateSpan should always be followed by an Expression, while a
        // kTemplateTail terminates a TemplateLiteral and does not need to be
        // followed by an Expression.

        // These scoped helpers save and restore the original error state, so that we
        // can specially treat invalid escape sequences in templates (which are
        // handled by the parser). (V8: ErrorState.)
        MessageTemplate old_scanner_error = _scannerError;
        Location old_scanner_error_location = _scannerErrorLocation;
        _scannerError = MessageTemplate.None;
        _scannerErrorLocation = Location.invalid();
        MessageTemplate old_octal_message = _octalMessage;
        Location old_octal_pos = _octalPos;
        _octalMessage = MessageTemplate.None;
        _octalPos = Location.invalid();

        Token result = Token.TemplateSpan;
        _next.literal_chars.Start();
        _next.raw_literal_chars.Start();
        const bool capture_raw = true;
        while (true)
        {
            int c = c0_;
            if (c == '`')
            {
                Advance(); // Consume '`'
                result = Token.TemplateTail;
                break;
            }
            else if (c == '$' && Peek() == '{')
            {
                Advance(); // Consume '$'
                Advance(); // Consume '{'
                break;
            }
            else if (c == '\\')
            {
                Advance(); // Consume '\\'
                AddRawLiteralChar('\\');
                if (IsLineTerminator(c0_))
                {
                    // The TV of LineContinuation :: \ LineTerminatorSequence is the empty
                    // code unit sequence.
                    int lastChar = c0_;
                    Advance();
                    if (lastChar == '\r')
                    {
                        // Also skip \n.
                        if (c0_ == '\n') Advance();
                        lastChar = '\n';
                    }
                    AddRawLiteralChar(lastChar);
                }
                else
                {
                    ScanEscape(capture_raw);
                    // For templates, invalid escape sequence checking is handled in the
                    // parser.
                    MoveErrorTo(ref _scannerError, ref _scannerErrorLocation, _next);
                    MoveErrorTo(ref _octalMessage, ref _octalPos, _next);
                }
            }
            else if (c == kEndOfInput)
            {
                // Unterminated template literal
                break;
            }
            else
            {
                Advance(); // Consume c.
                // The TRV of LineTerminatorSequence :: <CR> is the CV 0x000A.
                // The TRV of LineTerminatorSequence :: <CR><LF> is the sequence
                // consisting of the CV 0x000A.
                if (c == '\r')
                {
                    if (c0_ == '\n') Advance(); // Consume '\n'
                    c = '\n';
                }
                AddRawLiteralChar(c);
                AddLiteralChar(c);
            }
        }
        _next.location.end_pos = source_pos();
        _next.token = result;

        _octalMessage = old_octal_message;
        _octalPos = old_octal_pos;
        _scannerError = old_scanner_error;
        _scannerErrorLocation = old_scanner_error_location;

        return result;
    }

    // ErrorState::MoveErrorTo
    private static void MoveErrorTo(ref MessageTemplate message_stack, ref Location location_stack, TokenDesc dest)
    {
        if (message_stack == MessageTemplate.None)
        {
            return;
        }
        if (dest.invalid_template_escape_message == MessageTemplate.None)
        {
            dest.invalid_template_escape_message = message_stack;
            dest.invalid_template_escape_location = location_stack;
        }
        message_stack = MessageTemplate.None;
        location_stack = Location.invalid();
    }

    private delegate bool DigitPredicate(int ch);

    private bool ScanDigitsWithNumericSeparators(DigitPredicate predicate, bool is_check_first_digit)
    {
        // we must have at least one digit after 'x'/'b'/'o'
        if (is_check_first_digit && !predicate(c0_)) return false;

        bool separator_seen = false;
        while (predicate(c0_) || c0_ == '_')
        {
            if (c0_ == '_')
            {
                Advance();
                if (c0_ == '_')
                {
                    ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.ContinuousNumericSeparator);
                    return false;
                }
                separator_seen = true;
                continue;
            }
            separator_seen = false;
            AddLiteralCharAdvance();
        }

        if (separator_seen)
        {
            ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.TrailingNumericSeparator);
            return false;
        }

        return true;
    }

    private static readonly DigitPredicate s_isDecimalDigit = IsDecimalDigit;
    private static readonly DigitPredicate s_isHexDigit = IsHexDigit;
    private static readonly DigitPredicate s_isOctalDigit = IsOctalDigit;
    private static readonly DigitPredicate s_isBinaryDigit = IsBinaryDigit;

    private bool ScanDecimalDigits(bool allow_numeric_separator)
    {
        if (allow_numeric_separator)
        {
            return ScanDigitsWithNumericSeparators(s_isDecimalDigit, false);
        }
        while (IsDecimalDigit(c0_))
        {
            AddLiteralCharAdvance();
        }
        if (c0_ == '_')
        {
            ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.InvalidOrUnexpectedToken);
            return false;
        }
        return true;
    }

    private bool ScanDecimalAsSmiWithNumericSeparators(ref ulong value)
    {
        bool separator_seen = false;
        while (IsDecimalDigit(c0_) || c0_ == '_')
        {
            if (c0_ == '_')
            {
                Advance();
                if (c0_ == '_')
                {
                    ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.ContinuousNumericSeparator);
                    return false;
                }
                separator_seen = true;
                continue;
            }
            separator_seen = false;
            value = unchecked(10 * value + (ulong)(c0_ - '0'));
            int first_char = c0_;
            Advance();
            AddLiteralChar(first_char);
        }

        if (separator_seen)
        {
            ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.TrailingNumericSeparator);
            return false;
        }

        return true;
    }

    // Optimized function to scan decimal number as Smi.
    private bool ScanDecimalAsSmi(ref ulong value, bool allow_numeric_separator)
    {
        if (allow_numeric_separator)
        {
            return ScanDecimalAsSmiWithNumericSeparators(ref value);
        }

        while (IsDecimalDigit(c0_))
        {
            value = unchecked(10 * value + (ulong)(c0_ - '0'));
            int first_char = c0_;
            Advance();
            AddLiteralChar(first_char);
        }
        return true;
    }

    private bool ScanBinaryDigits() => ScanDigitsWithNumericSeparators(s_isBinaryDigit, true);

    private bool ScanOctalDigits() => ScanDigitsWithNumericSeparators(s_isOctalDigit, true);

    private bool ScanImplicitOctalDigits(int start_pos, ref NumberKind kind)
    {
        while (true)
        {
            // (possible) octal number
            if (IsNonOctalDecimalDigit(c0_))
            {
                kind = NumberKind.DECIMAL_WITH_LEADING_ZERO;
                return true;
            }
            if (!IsOctalDigit(c0_))
            {
                // Octal literal finished.
                _octalPos = new Location(start_pos, source_pos());
                _octalMessage = MessageTemplate.StrictOctalLiteral;
                return true;
            }
            AddLiteralCharAdvance();
        }
    }

    private bool ScanHexDigits() => ScanDigitsWithNumericSeparators(s_isHexDigit, true);

    private bool ScanSignedInteger()
    {
        if (c0_ == '+' || c0_ == '-') AddLiteralCharAdvance();
        // we must have at least one decimal digit after 'e'/'E'
        if (!IsDecimalDigit(c0_)) return false;
        return ScanDecimalDigits(true);
    }

    private Token ScanNumber(bool seen_period)
    {
        NumberKind kind = NumberKind.DECIMAL;

        _next.literal_chars.Start();
        bool at_start = !seen_period;
        int start_pos = source_pos(); // For reporting octal positions.
        if (seen_period)
        {
            // we have already seen a decimal point of the float
            AddLiteralChar('.');
            if (c0_ == '_')
            {
                return Token.Illegal;
            }
            // we know we have at least one digit
            if (!ScanDecimalDigits(true)) return Token.Illegal;
        }
        else
        {
            // if the first character is '0' we must check for octals and hex
            if (c0_ == '0')
            {
                AddLiteralCharAdvance();

                // either 0, 0exxx, 0Exxx, 0.xxx, a hex number, a binary number or
                // an octal number.
                if (AsciiAlphaToLower(c0_) == 'x')
                {
                    AddLiteralCharAdvance();
                    kind = NumberKind.HEX;
                    if (!ScanHexDigits()) return Token.Illegal;
                }
                else if (AsciiAlphaToLower(c0_) == 'o')
                {
                    AddLiteralCharAdvance();
                    kind = NumberKind.OCTAL;
                    if (!ScanOctalDigits()) return Token.Illegal;
                }
                else if (AsciiAlphaToLower(c0_) == 'b')
                {
                    AddLiteralCharAdvance();
                    kind = NumberKind.BINARY;
                    if (!ScanBinaryDigits()) return Token.Illegal;
                }
                else if (IsOctalDigit(c0_))
                {
                    kind = NumberKind.IMPLICIT_OCTAL;
                    if (!ScanImplicitOctalDigits(start_pos, ref kind))
                    {
                        return Token.Illegal;
                    }
                    if (kind == NumberKind.DECIMAL_WITH_LEADING_ZERO)
                    {
                        at_start = false;
                    }
                }
                else if (IsNonOctalDecimalDigit(c0_))
                {
                    kind = NumberKind.DECIMAL_WITH_LEADING_ZERO;
                }
                else if (c0_ == '_')
                {
                    ReportScannerError(new Location(source_pos(), source_pos() + 1), MessageTemplate.ZeroDigitNumericSeparator);
                    return Token.Illegal;
                }
            }

            // Parse decimal digits and allow trailing fractional part.
            if (IsDecimalNumberKind(kind))
            {
                bool allow_numeric_separator = kind != NumberKind.DECIMAL_WITH_LEADING_ZERO;
                // This is an optimization for parsing Decimal numbers as Smi's.
                if (at_start)
                {
                    ulong value = 0;
                    // scan subsequent decimal digits
                    if (!ScanDecimalAsSmi(ref value, allow_numeric_separator))
                    {
                        return Token.Illegal;
                    }

                    if (_next.literal_chars.length() <= 10 && value <= Globals.kSmiMaxValue && c0_ != '.' &&
                        !IsIdentifierStart(c0_))
                    {
                        _next.smi_value = (uint)value;

                        if (kind == NumberKind.DECIMAL_WITH_LEADING_ZERO)
                        {
                            _octalPos = new Location(start_pos, source_pos());
                            _octalMessage = MessageTemplate.StrictDecimalWithLeadingZero;
                        }
                        return Token.Smi;
                    }
                }

                if (!ScanDecimalDigits(allow_numeric_separator))
                {
                    return Token.Illegal;
                }
                if (c0_ == '.')
                {
                    seen_period = true;
                    AddLiteralCharAdvance();
                    if (c0_ == '_')
                    {
                        return Token.Illegal;
                    }
                    if (!ScanDecimalDigits(true)) return Token.Illegal;
                }
            }
        }

        bool is_bigint = false;
        if (c0_ == 'n' && !seen_period && IsValidBigIntKind(kind))
        {
            // Check that the literal is within our limits for BigInt length.
            // For simplicity, use 4 bits per character to calculate the maximum
            // allowed literal length.
            const int kMaxBigIntCharacters = kBigIntMaxBits / 4;
            int length = source_pos() - start_pos - (kind != NumberKind.DECIMAL ? 2 : 0);
            if (length > kMaxBigIntCharacters)
            {
                ReportScannerError(new Location(start_pos, source_pos()), MessageTemplate.BigIntTooBig);
                return Token.Illegal;
            }

            is_bigint = true;
            Advance();
        }
        else if (AsciiAlphaToLower(c0_) == 'e')
        {
            // scan exponent, if any
            if (!IsDecimalNumberKind(kind)) return Token.Illegal;

            // scan exponent
            AddLiteralCharAdvance();

            if (!ScanSignedInteger()) return Token.Illegal;
        }

        // The source character immediately following a numeric literal must
        // not be an identifier start or a decimal digit; see ECMA-262
        // section 7.8.3, page 17 (note that we read only one decimal digit
        // if the value is 0).
        if (IsDecimalDigit(c0_) || IsIdentifierStart(c0_))
        {
            return Token.Illegal;
        }

        if (kind == NumberKind.DECIMAL_WITH_LEADING_ZERO)
        {
            _octalPos = new Location(start_pos, source_pos());
            _octalMessage = MessageTemplate.StrictDecimalWithLeadingZero;
        }

        _next.number_kind = kind;
        return is_bigint ? Token.BigInt : Token.Number;
    }

    // Decodes a Unicode escape-sequence which is part of an identifier.
    // If the escape sequence cannot be decoded the result is kBadChar.
    private int ScanIdentifierUnicodeEscape()
    {
        Advance();
        if (c0_ != 'u') return Invalid();
        Advance();
        return ScanUnicodeEscape(false);
    }

    private int ScanUnicodeEscape(bool capture_raw)
    {
        // Accept both \uxxxx and \u{xxxxxx}. In the latter case, the number of
        // hex digits between { } is arbitrary. \ and u have already been read.
        if (c0_ == '{')
        {
            int begin = source_pos() - 2;
            Advance(capture_raw);
            int cp = ScanUnlimitedLengthHexNumber(capture_raw, Globals.kMaxCodePoint, begin);
            if (cp == kInvalidSequence || c0_ != '}')
            {
                ReportScannerError(source_pos(), MessageTemplate.InvalidUnicodeEscapeSequence);
                return Invalid();
            }
            Advance(capture_raw);
            return cp;
        }
        const bool unicode = true;
        return ScanHexNumber(capture_raw, unicode, 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Token ScanIdentifierOrKeyword()
    {
        _next.literal_chars.Start();
        return ScanIdentifierOrKeywordInner();
    }

    private struct IdentifierCheck(Scanner scanner, byte scanFlags) : IAdvanceRangeCheck
    {
        public byte ScanFlags = scanFlags;

        public bool Check(int c0)
        {
            if ((uint)c0 > kMaxAscii)
            {
                // A non-ascii character means we need to drop through to the
                // slow path.
                ScanFlags |= kIdentifierNeedsSlowPath;
                return true;
            }
            byte char_flags = s_characterScanFlags[c0];
            ScanFlags |= char_flags;
            return TerminatesLiteral(char_flags);
        }

        public readonly void OnRange(ReadOnlySpan<char> range) => scanner._next.literal_chars.AddRangeFromUtf16(range);
    }

    private Token ScanIdentifierOrKeywordInner()
    {
        bool escaped = false;
        bool can_be_keyword = true;

        if ((uint)c0_ <= kMaxAscii)
        {
            if (c0_ != '\\')
            {
                byte scan_flags = s_characterScanFlags[c0_];
                scan_flags >>= 1;
                AddLiteralChar(c0_);
                // Characters consumed by the scan are bulk-appended to the literal
                // buffer via the range callback.
                var check = new IdentifierCheck(this, scan_flags);
                c0_ = _source.AdvanceUntilRange(ref check);
                scan_flags = check.ScanFlags;

                if (!IdentifierNeedsSlowPath(scan_flags))
                {
                    if (!CanBeKeyword(scan_flags)) return Token.Identifier;
                    // Could be a keyword or identifier.
                    return KeywordOrIdentifierToken(_next.literal_chars.literal());
                }

                can_be_keyword = CanBeKeyword(scan_flags);
            }
            else
            {
                // Special case for escapes at the start of an identifier.
                escaped = true;
                int c = ScanIdentifierUnicodeEscape();
                if (c == '\\' || !IsIdentifierStart(c))
                {
                    return Token.Illegal;
                }
                AddLiteralChar(c);
                can_be_keyword = CharCanBeKeyword(c);
            }
        }

        return ScanIdentifierOrKeywordInnerSlow(escaped, can_be_keyword);
    }

    private Token ScanIdentifierOrKeywordInnerSlow(bool escaped, bool can_be_keyword)
    {
        while (true)
        {
            if (c0_ == '\\')
            {
                escaped = true;
                int c = ScanIdentifierUnicodeEscape();
                // Only allow legal identifier part characters.
                if (c == '\\' || !IsIdentifierPart(c))
                {
                    return Token.Illegal;
                }
                can_be_keyword = can_be_keyword && CharCanBeKeyword(c);
                AddLiteralChar(c);
            }
            else if (IsIdentifierPart(c0_) || (CombineSurrogatePair() && IsIdentifierPart(c0_)))
            {
                can_be_keyword = can_be_keyword && CharCanBeKeyword(c0_);
                AddLiteralCharAdvance();
            }
            else
            {
                break;
            }
        }

        if (can_be_keyword && _next.literal_chars.is_one_byte())
        {
            Token token = KeywordOrIdentifierToken(_next.literal_chars.literal());
            if (TokenInfo.InRange(token, Token.Identifier, Token.Yield)) return token;

            if (token == Token.FutureStrictReservedWord)
            {
                if (escaped) return Token.EscapedStrictReservedWord;
                return token;
            }

            if (!escaped) return token;

            if (TokenInfo.InRange(token, Token.Let, Token.Static))
            {
                return Token.EscapedStrictReservedWord;
            }
            return Token.EscapedKeyword;
        }

        return Token.Identifier;
    }

    // Scans the input as a regular expression pattern, next token must be /(=).
    // Returns true if a pattern is scanned.
    public bool ScanRegExpPattern()
    {
        System.Diagnostics.Debug.Assert(_next.token == Token.Div || _next.token == Token.AssignDiv);

        // Scan: ('/' | '/=') RegularExpressionBody '/' RegularExpressionFlags
        bool in_character_class = false;

        // Scan regular expression body: According to ECMA-262, 3rd, 7.8.5,
        // the scanner should pass uninterpreted bodies to the RegExp
        // constructor.
        _next.literal_chars.Start();
        if (_next.token == Token.AssignDiv)
        {
            AddLiteralChar('=');
        }

        while (c0_ != '/' || in_character_class)
        {
            if (c0_ == kEndOfInput || IsLineTerminator(c0_))
            {
                return false;
            }
            if (c0_ == '\\')
            {
                // Escape sequence.
                AddLiteralCharAdvance();
                if (c0_ == kEndOfInput || IsLineTerminator(c0_))
                {
                    return false;
                }
                AddLiteralCharAdvance();
                // If the escape allows more characters, i.e., \x??, \u????, or \c?,
                // only "safe" characters are allowed (letters, digits, underscore),
                // otherwise the escape isn't valid and the invalid character has
                // its normal meaning. I.e., we can just continue scanning without
                // worrying whether the following characters are part of the escape
                // or not, since any '/', '\\' or '[' is guaranteed to not be part
                // of the escape sequence.
            }
            else
            {
                // Unescaped character.
                if (c0_ == '[') in_character_class = true;
                if (c0_ == ']') in_character_class = false;
                AddLiteralCharAdvance();
            }
        }
        Advance(); // consume '/'

        _next.token = Token.RegExpLiteral;
        return true;
    }

    // regexp::TryFlagFromChar + JSRegExp::FlagFromChar. V8 passes the uc32 c0_
    // to a `char` parameter, so only the low byte is examined; that truncation
    // is reproduced.
    private RegExpFlags FlagFromChar(int c)
    {
        RegExpFlags f = (char)(byte)c switch
        {
            'd' => RegExpFlags.HasIndices,
            'g' => RegExpFlags.Global,
            'i' => RegExpFlags.IgnoreCase,
            'l' => RegExpFlags.Linear,
            'm' => RegExpFlags.Multiline,
            's' => RegExpFlags.DotAll,
            'u' => RegExpFlags.Unicode,
            'v' => RegExpFlags.UnicodeSets,
            'y' => RegExpFlags.Sticky,
            _ => RegExpFlags.None,
        };
        if (f == RegExpFlags.Linear && !_enableExperimentalRegExpEngine) return RegExpFlags.None;
        return f;
    }

    // Scans the input as regular expression flags. Returns the flags on success.
    public RegExpFlags? ScanRegExpFlags()
    {
        RegExpFlags flags = RegExpFlags.None;
        _next.literal_chars.Start();
        while (IsIdentifierPart(c0_))
        {
            RegExpFlags flag = FlagFromChar(c0_);
            if (flag == RegExpFlags.None) return null;
            if ((flags & flag) != 0) return null;
            AddLiteralCharAdvance();
            flags |= flag;
        }

        _next.location.end_pos = source_pos();
        return flags;
    }

    // Seek to the next_ token at the given position.
    internal void SeekNext(int position)
    {
        // Use with care: This cleanly resets most, but not all scanner state.

        // To re-scan from a given character position, we need to:
        // 1, Reset the current_, next_ and next_next_ tokens
        //    (next_ + next_next_ will be overwrittem by Next(),
        //     current_ will remain unchanged, so overwrite it fully.)
        foreach (TokenDesc token in _tokenStorage)
        {
            token.token = Token.Uninitialized;
            token.invalid_template_escape_message = MessageTemplate.None;
        }
        // 2, reset the source to the desired position,
        _source.Seek(position);
        // 3, re-scan, by scanning the look-ahead char + 1 token (next_).
        c0_ = _source.Advance();
        _next.after_line_terminator = false;
        Scan();
    }
}
