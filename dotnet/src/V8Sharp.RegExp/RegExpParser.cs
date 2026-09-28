// Port of src/regexp/regexp-parser.h and src/regexp/regexp-parser.cc.
//
// Deviations:
// - V8 instantiates the parser for one-byte and two-byte pattern strings and
//   for two modes (kBuildAST, kVerifySyntax). The C# parser works on UTF-16
//   input and always builds the AST; the one-byte instantiation's only
//   observable difference (IS_CERTAINLY_ONE_CODE_POINT on non-negated classes)
//   is reproduced by checking whether the pattern is Latin-1, and syntax
//   verification builds and discards the AST (the kVerifySyntax specialisation
//   only saves the allocations).
// - The V8_INTL_SUPPORT paths (\p{...}, case closure) use the generated
//   Unicode tables in V8Sharp.RegExp.Unicode instead of ICU.

using System.Runtime.CompilerServices;
using V8Sharp.RegExp.Unicode;

namespace V8Sharp.RegExp;

public static class RegExpParser
{
    /// <summary>Parser::ParseRegExpFromHeapString.</summary>
    public static bool ParseRegExp(string input, RegExpFlags flags, RegExpCompileData result)
    {
        return new RegExpParserImpl(input, flags).Parse(result);
    }

    /// <summary>Parser::VerifyRegExpSyntax.</summary>
    public static bool VerifyRegExpSyntax(string input, RegExpFlags flags, RegExpCompileData result)
    {
        return new RegExpParserImpl(input, flags).Parse(result);
    }
}

internal static class Utf16
{
    public const int kMaxNonSurrogateCharCode = 0xffff;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeadSurrogate(int code) => (code & 0x1ffc00) == 0xd800;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrailSurrogate(int code) => (code & 0x1ffc00) == 0xdc00;
    public static int CombineSurrogatePair(int lead, int trail) =>
        0x10000 + ((lead & 0x3ff) << 10) + (trail & 0x3ff);
    public static int LeadSurrogate(int charCode) => 0xd800 + (((charCode - 0x10000) >> 10) & 0x3ff);
    public static int TrailSurrogate(int charCode) => 0xdc00 + (charCode & 0x3ff);
}

// Whether we're currently inside the ClassEscape production
// (tc39.es/ecma262/#prod-annexB-CharacterEscape).
internal enum InClassEscapeState { kInClass, kNotInClass }

// The production used to derive ClassSetOperand.
internal enum ClassSetOperandType
{
    kClassSetCharacter,
    kClassStringDisjunction,
    kNestedClass,
    kCharacterClassEscape,  // \ CharacterClassEscape is a special nested class,
                            // as we can fold it directly into another range.
    kClassSetRange,
}

internal sealed class RegExpTextBuilder(List<RegExpTree> termsStorage, RegExpFlags flags)
{
    const int kNoPendingSurrogate = 0;

    readonly RegExpFlags _flags = flags;
    List<char>? _characters;
    int _pendingSurrogate = kNoPendingSurrogate;
    readonly List<RegExpTree> _terms = termsStorage;
    readonly List<RegExpTree> _text = new(8);

    bool IgnoreCase => _flags.IsIgnoreCase();
    // Either /v or /u enable UnicodeMode
    // https://tc39.es/ecma262/#sec-parsepattern
    bool IsUnicodeMode => _flags.IsUnicode() || _flags.IsUnicodeSets();

    void AddLeadSurrogate(int leadSurrogate)
    {
        FlushPendingSurrogate();
        // Hold onto the lead surrogate, waiting for a trail surrogate to follow.
        _pendingSurrogate = leadSurrogate;
    }

    void AddTrailSurrogate(int trailSurrogate)
    {
        if (_pendingSurrogate != kNoPendingSurrogate)
        {
            int leadSurrogate = _pendingSurrogate;
            _pendingSurrogate = kNoPendingSurrogate;
            int combined = Utf16.CombineSurrogatePair(leadSurrogate, trailSurrogate);
            if (NeedsDesugaringForIgnoreCase(combined))
            {
                AddClassRangesForDesugaring(combined);
            }
            else
            {
                var atom = new RegExpAtom(new string([(char)leadSurrogate, (char)trailSurrogate]));
                AddAtom(atom);
            }
        }
        else
        {
            _pendingSurrogate = trailSurrogate;
            FlushPendingSurrogate();
        }
    }

    public void FlushPendingSurrogate()
    {
        if (_pendingSurrogate != kNoPendingSurrogate)
        {
            int c = _pendingSurrogate;
            _pendingSurrogate = kNoPendingSurrogate;
            AddClassRangesForDesugaring(c);
        }
    }

    void FlushCharacters()
    {
        FlushPendingSurrogate();
        if (_characters is not null)
        {
            RegExpTree atom = new RegExpAtom(new string(_characters.ToArray()));
            _characters = null;
            _text.Add(atom);
        }
    }

    public void FlushText()
    {
        FlushCharacters();
        int numText = _text.Count;
        if (numText == 0) return;
        if (numText == 1)
        {
            _terms.Add(_text[^1]);
        }
        else
        {
            var text = new RegExpText();
            for (int i = 0; i < numText; i++) _text[i].AppendToText(text);
            _terms.Add(text);
        }
        _text.Clear();
    }

    public void AddCharacter(int c)
    {
        FlushPendingSurrogate();
        _characters ??= new List<char>(4);
        _characters.Add((char)c);
    }

    public void AddUnicodeCharacter(int c)
    {
        if (c > Utf16.kMaxNonSurrogateCharCode)
        {
            Debug.Assert(IsUnicodeMode);
            AddLeadSurrogate(Utf16.LeadSurrogate(c));
            AddTrailSurrogate(Utf16.TrailSurrogate(c));
        }
        else if (IsUnicodeMode && Utf16.IsLeadSurrogate(c))
        {
            AddLeadSurrogate(c);
        }
        else if (IsUnicodeMode && Utf16.IsTrailSurrogate(c))
        {
            AddTrailSurrogate(c);
        }
        else
        {
            AddCharacter(c);
        }
    }

    public void AddEscapedUnicodeCharacter(int character)
    {
        // A lead or trail surrogate parsed via escape sequence will not
        // pair up with any preceding lead or following trail surrogate.
        FlushPendingSurrogate();
        AddUnicodeCharacter(character);
        FlushPendingSurrogate();
    }

    public void AddClassRanges(RegExpClassRanges cr)
    {
        if (NeedsDesugaringForUnicode(cr))
        {
            // With /u or /v, character class needs to be desugared, so it
            // must be a standalone term instead of being part of a Text.
            AddTerm(cr);
        }
        else
        {
            AddAtom(cr);
        }
    }

    void AddClassRangesForDesugaring(int c)
    {
        AddTerm(new RegExpClassRanges(CharacterRange.List(CharacterRange.Singleton(c))));
    }

    public void AddAtom(RegExpTree atom)
    {
        Debug.Assert(atom.IsTextElement());
        FlushCharacters();
        _text.Add(atom);
    }

    public void AddTerm(RegExpTree term)
    {
        Debug.Assert(term.IsTextElement());
        FlushText();
        _terms.Add(term);
    }

    bool NeedsDesugaringForUnicode(RegExpClassRanges cc)
    {
        if (!IsUnicodeMode) return false;
        // TODO(yangguo): we could be smarter than this. Case-insensitivity does not
        // necessarily mean that we need to desugar. It's probably nicer to have a
        // separate pass to figure out unicode desugarings.
        if (IgnoreCase) return true;
        List<CharacterRange> ranges = cc.Ranges;
        CharacterRange.Canonicalize(ranges);

        if (cc.IsNegated)
        {
            var negatedRanges = new List<CharacterRange>(ranges.Count);
            CharacterRange.Negate(ranges, negatedRanges);
            ranges = negatedRanges;
        }

        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            int from = ranges[i].From;
            int to = ranges[i].To;
            // Check for non-BMP characters.
            if (to >= RegExpMacroAssembler.kNonBmpStart) return true;
            // Check for lone surrogates.
            if (from <= RegExpMacroAssembler.kTrailSurrogateEnd && to >= RegExpMacroAssembler.kLeadSurrogateStart) return true;
        }
        return false;
    }

    // We only use this for characters made of surrogate pairs.  All other
    // characters outside of character classes are made case independent in the
    // code generation.
    bool NeedsDesugaringForIgnoreCase(int c)
    {
        if (IsUnicodeMode && IgnoreCase)
        {
            // icu::UnicodeSet set(c, c); set.closeOver(USET_CASE_INSENSITIVE);
            // set.removeAllStrings(); return set.size() > 1;
            // Full and simple case closure agree for supplementary code points.
            return UnicodeTables.SimpleCaseClosure(c) is { Length: > 1 };
        }
        return false;
    }

    public RegExpTree? PopLastAtom()
    {
        FlushPendingSurrogate();
        RegExpTree atom;
        if (_characters is not null)
        {
            string chars = new(_characters.ToArray());
            int numChars = chars.Length;
            if (numChars > 1)
            {
                _text.Add(new RegExpAtom(chars[..(numChars - 1)]));
                chars = chars[(numChars - 1)..];
            }
            _characters = null;
            atom = new RegExpAtom(chars);
            return atom;
        }
        else if (_text.Count != 0)
        {
            atom = _text[^1];
            _text.RemoveAt(_text.Count - 1);
            return atom;
        }
        return null;
    }

    public RegExpTree ToRegExp()
    {
        FlushText();
        int numberOfTerms = _terms.Count;
        if (numberOfTerms == 0) return new RegExpEmpty();
        if (numberOfTerms == 1) return _terms[^1];
        return new RegExpAlternative([.. _terms]);
    }
}

// Result type for character class set operation.
internal readonly struct ClassSetResult
{
    public readonly RegExpTree? Tree;
    public readonly bool MayContainStrings;
    public readonly bool Success;

    public ClassSetResult(RegExpTree? tree, bool mayContainStrings = false)
    {
        Tree = tree;
        MayContainStrings = mayContainStrings;
        Success = true;
    }

    ClassSetResult(RegExpTree? tree, bool mayContainStrings, bool success)
    {
        Tree = tree;
        MayContainStrings = mayContainStrings;
        Success = success;
    }

    public ClassSetResult WithTree(RegExpTree tree) => new(tree, MayContainStrings, Success);

    public static ClassSetResult Failure() => new(null, false, false);
}

// Accumulates RegExp atoms and assertions into lists of terms and alternatives.
internal sealed class RegExpBuilder
{
    bool _pendingEmpty;
    readonly RegExpFlags _flags;
    readonly List<RegExpTree> _terms = new(8);
    readonly List<RegExpTree> _alternatives = new(8);
    readonly RegExpTextBuilder _textBuilder;

    public RegExpBuilder(RegExpFlags flags)
    {
        _flags = flags;
        _textBuilder = new RegExpTextBuilder(_terms, flags);
    }

    public RegExpFlags Flags => _flags;
    public bool IgnoreCase => _flags.IsIgnoreCase();
    public bool Multiline => _flags.IsMultiline();
    public bool DotAll => _flags.IsDotAll();
    bool IsUnicodeMode => _flags.IsUnicode() || _flags.IsUnicodeSets();

    public void FlushText() => _textBuilder.FlushText();

    public void AddCharacter(int c)
    {
        _pendingEmpty = false;
        _textBuilder.AddCharacter(c);
    }

    public void AddUnicodeCharacter(int c)
    {
        _pendingEmpty = false;
        _textBuilder.AddUnicodeCharacter(c);
    }

    public void AddEscapedUnicodeCharacter(int character)
    {
        _pendingEmpty = false;
        _textBuilder.AddEscapedUnicodeCharacter(character);
    }

    // "Adds" an empty expression. Does nothing except consume a following
    // quantifier.
    public void AddEmpty()
    {
        _textBuilder.FlushPendingSurrogate();
        _pendingEmpty = true;
    }

    public void AddClassRanges(RegExpClassRanges cc)
    {
        _pendingEmpty = false;
        _textBuilder.AddClassRanges(cc);
    }

    public void AddAtom(RegExpTree term)
    {
        if (term.IsEmpty())
        {
            AddEmpty();
            return;
        }
        _pendingEmpty = false;
        if (term.IsTextElement())
        {
            _textBuilder.AddAtom(term);
        }
        else
        {
            FlushText();
            _terms.Add(term);
        }
    }

    public void AddTerm(RegExpTree term)
    {
        _pendingEmpty = false;
        Debug.Assert(!term.IsEmpty());
        if (term.IsTextElement())
        {
            _textBuilder.AddTerm(term);
        }
        else
        {
            FlushText();
            _terms.Add(term);
        }
    }

    public void AddAssertion(RegExpTree assert)
    {
        _pendingEmpty = false;
        FlushText();
        _terms.Add(assert);
    }

    public void NewAlternative() => FlushTerms();

    void FlushTerms()
    {
        FlushText();
        int numTerms = _terms.Count;
        RegExpTree alternative;
        if (numTerms == 0) alternative = new RegExpEmpty();
        else if (numTerms == 1) alternative = _terms[^1];
        else alternative = new RegExpAlternative([.. _terms]);
        _alternatives.Add(alternative);
        _terms.Clear();
    }

    public RegExpTree ToRegExp()
    {
        FlushTerms();
        int numAlternatives = _alternatives.Count;
        if (numAlternatives == 0) return new RegExpEmpty();
        if (numAlternatives == 1) return _alternatives[^1];
        return new RegExpDisjunction([.. _alternatives]);
    }

    public bool AddQuantifierToAtom(int min, int max, int index, RegExpQuantifier.QuantifierType quantifierType)
    {
        if (_pendingEmpty)
        {
            _pendingEmpty = false;
            return true;
        }
        RegExpTree? atom = _textBuilder.PopLastAtom();
        if (atom is not null)
        {
            FlushText();
        }
        else if (_terms.Count != 0)
        {
            atom = _terms[^1];
            _terms.RemoveAt(_terms.Count - 1);
            if (atom.IsLookaround())
            {
                // With /u or /v, lookarounds are not quantifiable.
                if (IsUnicodeMode) return false;
                // Lookbehinds are not quantifiable.
                if (atom.AsLookaround()!.LookaroundType == RegExpLookaround.Type.LOOKBEHIND) return false;
            }
            if (atom.MaxMatch == 0)
            {
                // Guaranteed to only match an empty string.
                if (min == 0) return true;
                _terms.Add(atom);
                return true;
            }
        }
        else
        {
            // Only call immediately after adding an atom or character!
            throw new InvalidOperationException("UNREACHABLE");
        }
        _terms.Add(new RegExpQuantifier(min, max, quantifierType, index, atom));
        return true;
    }
}

internal enum SubexpressionType
{
    INITIAL,
    CAPTURE,  // All positive values represent captures.
    POSITIVE_LOOKAROUND,
    NEGATIVE_LOOKAROUND,
    GROUPING,
}

internal sealed class RegExpParserState
{
    readonly List<Interval> _nonParticipatingCaptureGroupIntervals = new(1);

    // Push a state on the stack.
    public RegExpParserState(RegExpParserState? previousState, SubexpressionType groupType,
        RegExpLookaround.Type lookaroundType, int disjunctionCaptureIndex, string? captureName, RegExpFlags flags)
    {
        PreviousState = previousState;
        Builder = new RegExpBuilder(flags);
        GroupType = groupType;
        LookaroundType = lookaroundType;
        CaptureIndex = disjunctionCaptureIndex;
        CaptureName = captureName;
        if (previousState is not null)
        {
            _nonParticipatingCaptureGroupIntervals.InsertRange(0, previousState._nonParticipatingCaptureGroupIntervals);
        }
    }

    // Parser state of containing expression, if any.
    public RegExpParserState? PreviousState { get; }
    public bool IsSubexpression => PreviousState is not null;
    // Builder building this regexp's AST.
    public RegExpBuilder Builder { get; }
    // Type of regexp being parsed (parenthesized group or entire regexp).
    public SubexpressionType GroupType { get; }
    // Lookahead or Lookbehind.
    public RegExpLookaround.Type LookaroundType { get; }
    // Index in captures array of first capture in this sub-expression, if any.
    // Also the capture index of this sub-expression itself, if group_type
    // is CAPTURE.
    public int CaptureIndex { get; }
    // The name of the current sub-expression, if group_type is CAPTURE. Only
    // used for named captures.
    public string? CaptureName { get; }
    public List<Interval> NonParticipatingCaptureGroupIntervals => _nonParticipatingCaptureGroupIntervals;

    public bool IsNamedCapture => CaptureName is not null;

    // Check whether the parser is inside a capture group with the given index.
    public bool IsInsideCaptureGroup(int index)
    {
        for (RegExpParserState? s = this; s is not null; s = s.PreviousState)
        {
            if (s.GroupType != SubexpressionType.CAPTURE) continue;
            // Return true if we found the matching capture index.
            if (index == s.CaptureIndex) return true;
            // Abort if index is larger than what has been parsed up till this state.
            if (index > s.CaptureIndex) return false;
        }
        return false;
    }

    // Check whether the parser is inside a capture group with the given name.
    public bool IsInsideCaptureGroup(string name)
    {
        for (RegExpParserState? s = this; s is not null; s = s.PreviousState)
        {
            if (s.CaptureName is null) continue;
            if (s.CaptureName == name) return true;
        }
        return false;
    }

    public void NewAlternative(int capturesStarted)
    {
        // Nothing to do if there were no new captures started before the
        // alternative.
        if (CaptureIndex == capturesStarted) return;

        // +1 to create a closed interval (capture_index() is exclusive).
        int from = CaptureIndex + 1;
        int to = capturesStarted;
        Debug.Assert(from <= to);
        // Extend the last interval if we increase its range by exactly 1.
        if (_nonParticipatingCaptureGroupIntervals.Count != 0 &&
            _nonParticipatingCaptureGroupIntervals[^1].To + 1 == to)
        {
            Interval interval = _nonParticipatingCaptureGroupIntervals[^1];
            _nonParticipatingCaptureGroupIntervals[^1] = interval.Union(new Interval(from, to));
        }
        else
        {
            _nonParticipatingCaptureGroupIntervals.Add(new Interval(from, to));
        }
    }
}

internal sealed class RegExpParserImpl
{
    const int kEndMarker = 1 << 21;

    RegExpError _error = RegExpError.None;
    int _errorPos;
    List<RegExpCapture>? _captures;
    // Maps capture names to a list of capture indices with this name.
    SortedDictionary<string, List<int>>? _namedCaptures;
    List<RegExpBackReference>? _namedBackReferences;
    readonly string _input;
    readonly int _inputLength;
    // V8 instantiates the parser over one-byte strings when the pattern is
    // Latin-1 (see the header comment).
    readonly bool _isOneByte;
    int _current;
    RegExpFlags _flags;
    bool _forceUnicode;  // Force parser to act as if unicode were set.
    int _nextPos;
    int _capturesStarted;
    int _captureCount;  // Only valid after we have scanned for captures.
    int _quantifierCount;
    int _lookaroundCount;  // Only valid after we have scanned for lookbehinds.
    bool _hasMore;
    bool _simple;
    bool _containsAnchor;
    bool _isScannedForCaptures;
    bool _hasNamedCaptures;  // Only valid after we have scanned for captures.
    bool _failed;

    public RegExpParserImpl(string input, RegExpFlags flags)
    {
        _input = input;
        _inputLength = input.Length;
        bool oneByte = true;
        foreach (char ch in input)
        {
            if (ch > 0xff) { oneByte = false; break; }
        }
        _isOneByte = oneByte;
        _current = kEndMarker;
        _flags = flags;
        _hasMore = true;
        Advance();
    }

    bool AddUnicodeCaseEquivalents => IsUnicodeMode && IgnoreCase;

    // Reports whether the pattern might be used as a literal search string.
    // Only use if the result of the parse is a single atom node.
    bool Simple => _simple;
    bool ContainsAnchor => _containsAnchor;
    void SetContainsAnchor() => _containsAnchor = true;
    int CapturesStarted => _capturesStarted;
    int Position
    {
        get
        {
            bool currentIsSurrogate = Current != kEndMarker && Current > Utf16.kMaxNonSurrogateCharCode;
            int rewindBytes = currentIsSurrogate ? 2 : 1;
            return _nextPos - rewindBytes;
        }
    }
    bool Failed => _failed;
    RegExpFlags Flags => _flags;
    bool IsUnicodeMode => _flags.IsUnicode() || _flags.IsUnicodeSets() || _forceUnicode;
    bool UnicodeSets => _flags.IsUnicodeSets();
    bool IgnoreCase => _flags.IsIgnoreCase();

    int Current => _current;
    bool HasMore => _hasMore;
    bool HasNext => _nextPos < _inputLength;

    char InputAt(int index) => _input[index];

    int ReadNext(bool updatePosition)
    {
        int position = _nextPos;
        int c0 = InputAt(position);
        position++;
        int res = c0;
        if (!_isOneByte && IsUnicodeMode && position < _inputLength && Utf16.IsLeadSurrogate(c0))
        {
            int c1 = InputAt(position);
            if (Utf16.IsTrailSurrogate(c1))
            {
                res = Utf16.CombineSurrogatePair(c0, c1);
                position++;
            }
        }
        if (updatePosition) _nextPos = position;
        return res;
    }

    int Next() => HasNext ? ReadNext(false) : kEndMarker;

    void Advance()
    {
        if (HasNext)
        {
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                ReportError(RegExpError.StackOverflow);
            }
            else
            {
                _current = ReadNext(true);
            }
        }
        else
        {
            _current = kEndMarker;
            // Advance so that position() points to 1-after-the-last-character. This is
            // important so that Reset() to this position works correctly.
            _nextPos = _inputLength + 1;
            _hasMore = false;
        }
    }

    // Rewinds to before the previous Advance().
    void RewindByOneCodepoint()
    {
        if (!HasMore) return;
        // Rewinds by one code point, i.e.: two code units if `current` is outside
        // the basic multilingual plane (= composed of a lead and trail surrogate),
        // or one code unit otherwise.
        int rewindBy = Current > Utf16.kMaxNonSurrogateCharCode ? -2 : -1;
        Advance(rewindBy);  // Undo the last Advance.
    }

    void Reset(int pos)
    {
        _nextPos = pos;
        _hasMore = pos < _inputLength;
        Advance();
    }

    void Advance(int dist)
    {
        _nextPos += dist - 1;
        Advance();
    }

    static bool IsSyntaxCharacterOrSlash(int c) => c switch
    {
        '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/' => true,
        _ => false,
    };

    static bool IsClassSetSyntaxCharacter(int c) => c switch
    {
        '(' or ')' or '[' or ']' or '{' or '}' or '/' or '-' or '\\' or '|' => true,
        _ => false,
    };

    static bool IsClassSetReservedPunctuator(int c) => c switch
    {
        '&' or '-' or '!' or '#' or '%' or ',' or ':' or ';' or '<' or '=' or '>' or '@' or '`' or '~' => true,
        _ => false,
    };

    bool IsClassSetReservedDoublePunctuator(int c) => c switch
    {
        '&' or '!' or '#' or '$' or '%' or '*' or '+' or ',' or '.' or ':' or ';' or '<' or '=' or '>' or '?'
            or '@' or '^' or '`' or '~' => Next() == c,
        _ => false,
    };

    RegExpTree? ReportError(RegExpError error)
    {
        if (_failed) return null;  // Do not overwrite any existing error.
        _failed = true;
        _error = error;
        _errorPos = Position;
        // Zip to the end to make sure no more input is read.
        _current = kEndMarker;
        _nextPos = _inputLength;
        _hasMore = false;
        return null;
    }

    static bool IsDecimalDigit(int c) => (uint)(c - '0') <= 9;

    static int HexValue(int c)
    {
        c -= '0';
        if ((uint)c <= 9) return c;
        c = (c | 0x20) - ('a' - '0');  // detect 0x11..0x16 and 0x31..0x36.
        if ((uint)c <= 5) return c + 10;
        return -1;
    }

    // Pattern ::
    //   Disjunction
    RegExpTree? ParsePattern()
    {
        RegExpTree? result = ParseDisjunction();
        if (_failed) return null;
        PatchNamedBackReferences();
        if (_failed) return null;
        Debug.Assert(!HasMore);
        // If the result of parsing is a literal string atom, and it has the
        // same length as the input, then the atom is identical to the input.
        if (result!.IsAtom() && result.AsAtom()!.Length == _inputLength)
        {
            _simple = true;
        }
        return result;
    }

    // Disjunction ::
    //   Alternative
    //   Alternative | Disjunction
    // Alternative ::
    //   [empty]
    //   Term Alternative
    // Term ::
    //   Assertion
    //   Atom
    //   Atom Quantifier
    RegExpTree? ParseDisjunction()
    {
        // Used to store current state while parsing subexpressions.
        var initialState = new RegExpParserState(null, SubexpressionType.INITIAL,
            RegExpLookaround.Type.LOOKAHEAD, 0, null, Flags);
        RegExpParserState state = initialState;
        // Cache the builder in a local variable for quick access.
        RegExpBuilder builder = initialState.Builder;
        while (true)
        {
            switch (Current)
            {
                case kEndMarker:
                    if (Failed) return null;  // E.g. the initial Advance failed.
                    if (state.IsSubexpression)
                    {
                        // Inside a parenthesized group when hitting end of input.
                        return ReportError(RegExpError.UnterminatedGroup);
                    }
                    Debug.Assert(state.GroupType == SubexpressionType.INITIAL);
                    // Parsing completed successfully.
                    return builder.ToRegExp();
                case ')':
                {
                    if (!state.IsSubexpression) return ReportError(RegExpError.UnmatchedParen);
                    Debug.Assert(state.GroupType != SubexpressionType.INITIAL);

                    Advance();
                    // End disjunction parsing and convert builder content to new single
                    // regexp atom.
                    RegExpTree body = builder.ToRegExp();

                    int endCaptureIndex = CapturesStarted;

                    int captureIndex = state.CaptureIndex;
                    SubexpressionType groupType = state.GroupType;

                    // Build result of subexpression.
                    if (groupType == SubexpressionType.CAPTURE)
                    {
                        if (state.IsNamedCapture)
                        {
                            CreateNamedCaptureAtIndex(state, captureIndex);
                            if (_failed) return null;
                        }
                        RegExpCapture capture = GetCapture(captureIndex);
                        capture.Body = body;
                        body = capture;
                    }
                    else if (groupType == SubexpressionType.GROUPING)
                    {
                        body = new RegExpGroup(body, builder.Flags);
                    }
                    else
                    {
                        Debug.Assert(groupType is SubexpressionType.POSITIVE_LOOKAROUND or SubexpressionType.NEGATIVE_LOOKAROUND);
                        bool isPositive = groupType == SubexpressionType.POSITIVE_LOOKAROUND;
                        body = new RegExpLookaround(body, isPositive, endCaptureIndex - captureIndex,
                            captureIndex, state.LookaroundType, _lookaroundCount);
                        _lookaroundCount++;
                    }

                    // Restore previous state.
                    state = state.PreviousState!;
                    builder = state.Builder;
                    _flags = builder.Flags;

                    builder.AddAtom(body);
                    // For compatibility with JSC and ES3, we allow quantifiers after
                    // lookaheads, and break in all cases.
                    break;
                }
                case '|':
                    Advance();
                    state.NewAlternative(CapturesStarted);
                    builder.NewAlternative();
                    continue;
                case '*':
                case '+':
                case '?':
                    return ReportError(RegExpError.NothingToRepeat);
                case '^':
                {
                    Advance();
                    var assertion = new RegExpAssertion(builder.Multiline
                        ? RegExpAssertion.Type.START_OF_LINE
                        : RegExpAssertion.Type.START_OF_INPUT);
                    builder.AddAssertion(assertion);
                    SetContainsAnchor();
                    continue;
                }
                case '$':
                {
                    Advance();
                    RegExpAssertion.Type assertionType = builder.Multiline
                        ? RegExpAssertion.Type.END_OF_LINE
                        : RegExpAssertion.Type.END_OF_INPUT;
                    builder.AddAssertion(new RegExpAssertion(assertionType));
                    continue;
                }
                case '.':
                {
                    Advance();
                    var ranges = new List<CharacterRange>(2);
                    if (builder.DotAll)
                    {
                        // Everything.
                        CharacterRange.AddClassEscape(StandardCharacterSet.kEverything, ranges, false);
                    }
                    else
                    {
                        // Everything except \x0A, \x0D, \u2028 and \u2029.
                        CharacterRange.AddClassEscape(StandardCharacterSet.kNotLineTerminator, ranges, false);
                    }
                    var cc = new RegExpClassRanges(ranges);
                    builder.AddClassRanges(cc);
                    break;
                }
                case '(':
                {
                    RegExpParserState? newState = ParseOpenParenthesis(state);
                    if (_failed) return null;
                    state = newState!;
                    builder = state.Builder;
                    _flags = builder.Flags;
                    continue;
                }
                case '[':
                {
                    ClassSetResult cc = ParseCharacterClass(builder);
                    if (_failed) return null;
                    if (cc.Tree!.IsClassRanges())
                    {
                        builder.AddClassRanges(cc.Tree.AsClassRanges()!);
                    }
                    else
                    {
                        Debug.Assert(cc.Tree.IsClassSetExpression());
                        builder.AddTerm(cc.Tree);
                    }
                    break;
                }
                // Atom ::
                //   \ AtomEscape
                case '\\':
                {
                    int escapedChar = Next();
                    // v8_flags.js_regexp_buffer_boundaries (\A, \z, \Z) is off by
                    // default and not ported as a flag.
                    if (s_jsRegExpBufferBoundaries && IsUnicodeMode)
                    {
                        bool isBufferBoundaryAssertion = true;
                        RegExpAssertion.Type assertionType = RegExpAssertion.Type.START_OF_INPUT;
                        switch (escapedChar)
                        {
                            // Assertion ::
                            //   [+UnicodeMode] \A
                            //   [+UnicodeMode] \z
                            //   [+UnicodeMode] \Z
                            case 'A':
                                assertionType = RegExpAssertion.Type.START_OF_INPUT;
                                SetContainsAnchor();
                                break;
                            case 'z':
                                assertionType = RegExpAssertion.Type.END_OF_INPUT;
                                break;
                            case 'Z':
                                assertionType = RegExpAssertion.Type.END_OF_BUFFER;
                                break;
                            default:
                                isBufferBoundaryAssertion = false;
                                break;
                        }
                        if (isBufferBoundaryAssertion)
                        {
                            Advance(2);
                            builder.AddAssertion(new RegExpAssertion(assertionType));
                            continue;
                        }
                    }

                    bool doCharacterEscape = false;
                    switch (escapedChar)
                    {
                        case kEndMarker:
                            return ReportError(RegExpError.EscapeAtEndOfPattern);
                        // AtomEscape ::
                        //   [+UnicodeMode] DecimalEscape
                        //   [~UnicodeMode] DecimalEscape but only if the CapturingGroupNumber
                        //                  of DecimalEscape is ≤ NcapturingParens
                        //   CharacterEscape (some cases of this mixed in too)
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        {
                            bool isBackref = ParseBackReferenceIndex(out int index);
                            if (_failed) return null;
                            if (isBackref)
                            {
                                if (state.IsInsideCaptureGroup(index))
                                {
                                    // The back reference is inside the capture group it refers to.
                                    // Nothing can possibly have been captured yet, so we use empty
                                    // instead. This ensures that, when checking a back reference,
                                    // the capture registers of the referenced capture are either
                                    // both set or both cleared.
                                    builder.AddEmpty();
                                }
                                else
                                {
                                    RegExpCapture capture = GetCapture(index);
                                    builder.AddAtom(new RegExpBackReference(capture));
                                }
                                break;
                            }
                            // With /u and /v, no identity escapes except for syntax characters
                            // are allowed. Otherwise, all identity escapes are allowed.
                            if (IsUnicodeMode) return ReportError(RegExpError.InvalidEscape);
                            int firstDigit = Next();
                            if (firstDigit == '8' || firstDigit == '9')
                            {
                                builder.AddCharacter(firstDigit);
                                Advance(2);
                                break;
                            }
                            goto case '0';
                        }
                        case '0':
                        {
                            Advance();
                            if (IsUnicodeMode && Next() >= '0' && Next() <= '9')
                            {
                                // Decimal escape with leading 0 are not parsed as octal.
                                return ReportError(RegExpError.InvalidDecimalEscape);
                            }
                            int octal = ParseOctalLiteral();
                            builder.AddCharacter(octal);
                            break;
                        }
                        case 'b':
                            Advance(2);
                            builder.AddAssertion(new RegExpAssertion(RegExpAssertion.Type.BOUNDARY));
                            continue;
                        case 'B':
                            Advance(2);
                            builder.AddAssertion(new RegExpAssertion(RegExpAssertion.Type.NON_BOUNDARY));
                            continue;
                        // AtomEscape ::
                        //   CharacterClassEscape
                        case 'd':
                        case 'D':
                        case 's':
                        case 'S':
                        case 'w':
                        case 'W':
                        {
                            int next = Next();
                            var ranges = new List<CharacterRange>(2);
                            ClassSetResult parsedCharacterClassEscape = TryParseCharacterClassEscape(
                                next, InClassEscapeState.kNotInClass, ranges, null, AddUnicodeCaseEquivalents);
                            if (_failed) return null;

                            if (parsedCharacterClassEscape.Success)
                            {
                                builder.AddClassRanges(new RegExpClassRanges(ranges));
                            }
                            else
                            {
                                if (IsUnicodeMode) throw new InvalidOperationException("CHECK failed");
                                Advance(2);
                                builder.AddCharacter(next);  // IdentityEscape.
                            }
                            break;
                        }
                        case 'p':
                        case 'P':
                        {
                            int next = Next();
                            var ranges = new List<CharacterRange>(2);
                            CharacterClassStrings? strings = UnicodeSets ? new CharacterClassStrings() : null;
                            ClassSetResult parsedCharacterClassEscape = TryParseCharacterClassEscape(
                                next, InClassEscapeState.kNotInClass, ranges, strings, AddUnicodeCaseEquivalents);
                            if (_failed) return null;

                            if (parsedCharacterClassEscape.Success)
                            {
                                RegExpTree term = UnicodeSets
                                    ? new RegExpClassSetOperand(ranges, strings)
                                    : new RegExpClassRanges(ranges);
                                builder.AddTerm(term);
                            }
                            else
                            {
                                if (IsUnicodeMode) throw new InvalidOperationException("CHECK failed");
                                Advance(2);
                                builder.AddCharacter(next);  // IdentityEscape.
                            }
                            break;
                        }
                        // AtomEscape ::
                        //   k GroupName
                        case 'k':
                        {
                            // Either an identity escape or a named back-reference.  The two
                            // interpretations are mutually exclusive: '\k' is interpreted as
                            // an identity escape for non-Unicode patterns without named
                            // capture groups, and as the beginning of a named back-reference
                            // in all other cases.
                            bool hasNamedCaptures = HasNamedCaptures(InClassEscapeState.kNotInClass);
                            if (_failed) return null;
                            if (IsUnicodeMode || hasNamedCaptures)
                            {
                                Advance(2);
                                ParseNamedBackReference(builder, state);
                                if (_failed) return null;
                                break;
                            }
                            doCharacterEscape = true;
                            break;
                        }
                        // AtomEscape ::
                        //   CharacterEscape
                        default:
                            doCharacterEscape = true;
                            break;
                    }
                    if (doCharacterEscape)
                    {
                        bool isEscapedUnicodeCharacter = false;
                        int c = ParseCharacterEscape(InClassEscapeState.kNotInClass, ref isEscapedUnicodeCharacter);
                        if (_failed) return null;
                        if (isEscapedUnicodeCharacter) builder.AddEscapedUnicodeCharacter(c);
                        else builder.AddCharacter(c);
                    }
                    break;
                }
                case '{':
                {
                    bool parsed = ParseIntervalQuantifier(out _, out _);
                    if (_failed) return null;
                    if (parsed) return ReportError(RegExpError.NothingToRepeat);
                    goto case '}';
                }
                case '}':
                case ']':
                    if (IsUnicodeMode) return ReportError(RegExpError.LoneQuantifierBrackets);
                    goto default;
                default:
                    builder.AddUnicodeCharacter(Current);
                    Advance();
                    break;
            }  // end switch(current())

            int min;
            int max;
            switch (Current)
            {
                // QuantifierPrefix ::
                //   *
                //   +
                //   ?
                //   {
                case '*':
                    min = 0;
                    max = RegExpTree.kInfinity;
                    Advance();
                    break;
                case '+':
                    min = 1;
                    max = RegExpTree.kInfinity;
                    Advance();
                    break;
                case '?':
                    min = 0;
                    max = 1;
                    Advance();
                    break;
                case '{':
                    if (ParseIntervalQuantifier(out min, out max))
                    {
                        if (max < min) return ReportError(RegExpError.RangeOutOfOrder);
                        break;
                    }
                    else if (IsUnicodeMode)
                    {
                        // Incomplete quantifiers are not allowed.
                        return ReportError(RegExpError.IncompleteQuantifier);
                    }
                    continue;
                default:
                    continue;
            }
            RegExpQuantifier.QuantifierType quantifierType = RegExpQuantifier.QuantifierType.GREEDY;
            if (Current == '?')
            {
                quantifierType = RegExpQuantifier.QuantifierType.NON_GREEDY;
                Advance();
            }
            // v8_flags.regexp_possessive_quantifier is a debug-only flag and is
            // not ported.
            if (!builder.AddQuantifierToAtom(min, max, _quantifierCount, quantifierType))
            {
                return ReportError(RegExpError.InvalidQuantifier);
            }
            ++_quantifierCount;
        }
    }

    /// <summary>--js-regexp-buffer-boundaries (\A, \z, \Z). Off by default in V8.</summary>
    internal static bool s_jsRegExpBufferBoundaries;

    RegExpParserState? ParseOpenParenthesis(RegExpParserState state)
    {
        RegExpLookaround.Type lookaroundType = state.LookaroundType;
        bool isNamedCapture = false;
        string? captureName = null;
        SubexpressionType subexprType = SubexpressionType.CAPTURE;
        RegExpFlags flags = state.Builder.Flags;
        bool parsingModifiers = false;
        bool modifiersPolarity = true;
        RegExpFlags modifiers = RegExpFlags.None;
        Advance();
        if (Current == '?')
        {
            do
            {
                int next = Next();
                switch (next)
                {
                    case '-':
                        Advance();
                        parsingModifiers = true;
                        if (modifiersPolarity == false)
                        {
                            ReportError(RegExpError.MultipleFlagDashes);
                            return null;
                        }
                        modifiersPolarity = false;
                        break;
                    case 'm':
                    case 'i':
                    case 's':
                    {
                        Advance();
                        parsingModifiers = true;
                        RegExpFlags flag = RegExpFlagsExtensions.TryFlagFromChar((char)next)!.Value;
                        if ((modifiers & flag) != 0)
                        {
                            ReportError(RegExpError.RepeatedFlag);
                            return null;
                        }
                        modifiers |= flag;
                        if (modifiersPolarity) flags |= flag;
                        else flags &= ~flag;
                        break;
                    }
                    case ':':
                        Advance(2);
                        parsingModifiers = false;
                        subexprType = SubexpressionType.GROUPING;
                        break;
                    case '=':
                        Advance(2);
                        if (parsingModifiers)
                        {
                            ReportError(RegExpError.InvalidGroup);
                            return null;
                        }
                        lookaroundType = RegExpLookaround.Type.LOOKAHEAD;
                        subexprType = SubexpressionType.POSITIVE_LOOKAROUND;
                        break;
                    case '!':
                        Advance(2);
                        if (parsingModifiers)
                        {
                            ReportError(RegExpError.InvalidGroup);
                            return null;
                        }
                        lookaroundType = RegExpLookaround.Type.LOOKAHEAD;
                        subexprType = SubexpressionType.NEGATIVE_LOOKAROUND;
                        break;
                    case '<':
                        Advance();
                        if (parsingModifiers)
                        {
                            ReportError(RegExpError.InvalidGroup);
                            return null;
                        }
                        if (Next() == '=')
                        {
                            Advance(2);
                            lookaroundType = RegExpLookaround.Type.LOOKBEHIND;
                            subexprType = SubexpressionType.POSITIVE_LOOKAROUND;
                            break;
                        }
                        else if (Next() == '!')
                        {
                            Advance(2);
                            lookaroundType = RegExpLookaround.Type.LOOKBEHIND;
                            subexprType = SubexpressionType.NEGATIVE_LOOKAROUND;
                            break;
                        }
                        isNamedCapture = true;
                        _hasNamedCaptures = true;
                        Advance();
                        break;
                    default:
                        ReportError(RegExpError.InvalidGroup);
                        return null;
                }
            } while (parsingModifiers);
        }
        if (modifiersPolarity == false)
        {
            // We encountered a dash.
            if (modifiers == 0)
            {
                ReportError(RegExpError.InvalidFlagGroup);
                return null;
            }
        }
        if (subexprType == SubexpressionType.CAPTURE)
        {
            if (_capturesStarted >= RegExpMacroAssembler.kMaxCaptures)
            {
                ReportError(RegExpError.TooManyCaptures);
                return null;
            }
            _capturesStarted++;

            if (isNamedCapture)
            {
                captureName = ParseCaptureGroupName();
                if (_failed) return null;
            }
        }
        // Store current state and begin new disjunction parsing.
        return new RegExpParserState(state, subexprType, lookaroundType, _capturesStarted, captureName, flags);
    }

    // In order to know whether an escape is a backreference or not we have to scan
    // the entire regexp and find the number of capturing parentheses.  However we
    // don't want to scan the regexp twice unless it is necessary.  This mini-parser
    // is called when needed.  It can see the difference between capturing and
    // noncapturing parentheses and can skip character classes and backslash-escaped
    // characters.
    //
    // Important: The scanner has to be in a consistent state when calling
    // ScanForCaptures, e.g. not in the middle of an escape sequence '\[' or while
    // parsing a nested class.
    void ScanForCaptures(InClassEscapeState inClassEscapeState)
    {
        Debug.Assert(!_isScannedForCaptures);
        int savedPosition = Position;
        // Start with captures started previous to current position
        int captureCount = CapturesStarted;
        // When we start inside a character class, skip everything inside the class.
        if (inClassEscapeState == InClassEscapeState.kInClass)
        {
            // \k is always invalid within a class in unicode mode, thus we should never
            // call ScanForCaptures within a class.
            Debug.Assert(!IsUnicodeMode);
            int c;
            while ((c = Current) != kEndMarker)
            {
                Advance();
                if (c == '\\')
                {
                    Advance();
                }
                else
                {
                    if (c == ']') break;
                }
            }
        }
        // Add count of captures after this position.
        int n;
        while ((n = Current) != kEndMarker)
        {
            Advance();
            switch (n)
            {
                case '\\':
                    Advance();
                    break;
                case '[':
                {
                    int classNestLevel = 0;
                    int c;
                    while ((c = Current) != kEndMarker)
                    {
                        Advance();
                        if (c == '\\')
                        {
                            Advance();
                        }
                        else if (c == '[')
                        {
                            // With /v, '[' inside a class is treated as a nested class.
                            // Without /v, '[' is a normal character.
                            if (UnicodeSets) classNestLevel++;
                        }
                        else if (c == ']')
                        {
                            if (classNestLevel == 0) break;
                            classNestLevel--;
                        }
                    }
                    break;
                }
                case '(':
                    if (Current == '?')
                    {
                        // At this point we could be in
                        // * a non-capturing group '(:',
                        // * a lookbehind assertion '(?<=' '(?<!'
                        // * or a named capture '(?<'.
                        //
                        // Of these, only named captures are capturing groups.

                        Advance();
                        if (Current != '<') break;

                        Advance();
                        if (Current == '=' || Current == '!') break;

                        // Found a possible named capture. It could turn out to be a syntax
                        // error (e.g. an unterminated or invalid name), but that distinction
                        // does not matter for our purposes.
                        _hasNamedCaptures = true;
                    }
                    captureCount++;
                    break;
            }
        }
        _captureCount = captureCount;
        _isScannedForCaptures = true;
        Reset(savedPosition);
    }

    bool ParseBackReferenceIndex(out int indexOut)
    {
        Debug.Assert(Current == '\\');
        Debug.Assert('1' <= Next() && Next() <= '9');
        // Try to parse a decimal literal that is no greater than the total number
        // of left capturing parentheses in the input.
        int start = Position;
        int value = Next() - '0';
        Advance(2);
        indexOut = 0;
        while (true)
        {
            int c = Current;
            if (IsDecimalDigit(c))
            {
                value = 10 * value + (c - '0');
                if (value > RegExpMacroAssembler.kMaxCaptures)
                {
                    Reset(start);
                    return false;
                }
                Advance();
            }
            else
            {
                break;
            }
        }
        if (value > CapturesStarted)
        {
            if (!_isScannedForCaptures) ScanForCaptures(InClassEscapeState.kNotInClass);
            if (value > _captureCount)
            {
                Reset(start);
                return false;
            }
        }
        indexOut = value;
        return true;
    }

    static void PushCodeUnit(List<char> v, int codeUnit)
    {
        if (codeUnit <= Utf16.kMaxNonSurrogateCharCode)
        {
            v.Add((char)codeUnit);
        }
        else
        {
            v.Add((char)Utf16.LeadSurrogate(codeUnit));
            v.Add((char)Utf16.TrailSurrogate(codeUnit));
        }
    }

    static bool IsIdentifierStart(int c) =>
        UnicodeTables.IsIdStart(c) || (c < 0x60 && (c == '$' || c == '\\' || c == '_'));

    static bool IsIdentifierPart(int c) =>
        UnicodeTables.IsIdContinue(c) || (c < 0x60 && (c == '$' || c == '\\' || c == '_')) ||
        c == 0x200C || c == 0x200D;

    // Parses the name of a capture group (?<name>pattern). The name must adhere
    // to IdentifierName in the ECMAScript standard.
    string? ParseCaptureGroupName()
    {
        // Due to special Advance requirements (see the next comment), rewind by one
        // such that names starting with a surrogate pair are parsed correctly for
        // patterns where the unicode flag is unset.
        //
        // Note that we use this odd pattern of rewinding the last advance in order
        // to adhere to the common parser behavior of expecting `current` to point at
        // the first candidate character for a function (e.g. when entering ParseFoo,
        // `current` should point at the first character of Foo).
        RewindByOneCodepoint();

        var name = new List<char>();

        // Advance behavior inside this function is tricky since
        // RegExpIdentifierName explicitly enables unicode (in spec terms, sets +U)
        // and thus allows surrogate pairs and \u{}-style escapes even in
        // non-unicode patterns. Therefore Advance within the capture group name
        // has to force-enable unicode, and outside the name revert to default
        // behavior.
        Debug.Assert(!_forceUnicode);
        _forceUnicode = true;
        try
        {
            bool atStart = true;
            while (true)
            {
                Advance();
                int c = Current;

                // Convert unicode escapes.
                if (c == '\\' && Next() == 'u')
                {
                    Advance(2);
                    if (!ParseUnicodeEscape(out c))
                    {
                        ReportError(RegExpError.InvalidUnicodeEscape);
                        return null;
                    }
                    RewindByOneCodepoint();
                }

                // The backslash char is misclassified as both ID_Start and ID_Continue.
                if (c == '\\')
                {
                    ReportError(RegExpError.InvalidCaptureGroupName);
                    return null;
                }

                if (atStart)
                {
                    if (!IsIdentifierStart(c))
                    {
                        ReportError(RegExpError.InvalidCaptureGroupName);
                        return null;
                    }
                    PushCodeUnit(name, c);
                    atStart = false;
                }
                else
                {
                    if (c == '>')
                    {
                        break;
                    }
                    else if (IsIdentifierPart(c))
                    {
                        PushCodeUnit(name, c);
                    }
                    else
                    {
                        ReportError(RegExpError.InvalidCaptureGroupName);
                        return null;
                    }
                }
            }
        }
        finally
        {
            _forceUnicode = false;
        }

        // This final advance goes back into the state of pointing at the next
        // relevant char, which the rest of the parser expects. See also the previous
        // comments in this function.
        Advance();
        return new string(name.ToArray());
    }

    // Creates a new named capture at the specified index. Must be called exactly
    // once for each named capture. Fails if a capture with the same name is
    // encountered.
    bool CreateNamedCaptureAtIndex(RegExpParserState state, int index)
    {
        string name = state.CaptureName!;
        List<Interval> nonParticipatingCaptureGroupIntervals = state.NonParticipatingCaptureGroupIntervals;
        Debug.Assert(0 < index && index <= _capturesStarted);

        RegExpCapture capture = GetCapture(index);
        Debug.Assert(capture.Name is null);

        capture.Name = name;

        if (_namedCaptures is null)
        {
            _namedCaptures = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        }
        else
        {
            // Check for duplicates and bail if we find any.
            if (_namedCaptures.TryGetValue(name, out List<int>? namedCaptureIndices))
            {
                foreach (int namedIndex in namedCaptureIndices)
                {
                    bool isDuplicate = true;
                    foreach (Interval interval in nonParticipatingCaptureGroupIntervals)
                    {
                        // We can stop as soon as we are inside one non-participating
                        // interval. There can't be a non-participating and participating
                        // interval, as intervals are never decreasing.
                        if (interval.Contains(namedIndex))
                        {
                            isDuplicate = false;
                            break;
                        }
                        // Intervals are ordered strictly increasing, so we can stop early
                        // when the current interval is past the current index.
                        if (namedIndex <= interval.From) break;
                    }
                    if (isDuplicate)
                    {
                        ReportError(RegExpError.DuplicateCaptureGroupName);
                        return false;
                    }
                }
            }
        }
        // Check for nested named captures. This is necessary to find duplicate
        // named captures within the same disjunct.
        RegExpParserState? parentState = state.PreviousState;
        if (parentState is not null && parentState.IsInsideCaptureGroup(name))
        {
            ReportError(RegExpError.DuplicateCaptureGroupName);
            return false;
        }

        if (!_namedCaptures.TryGetValue(name, out List<int>? entry))
        {
            entry = new List<int>(1);
            _namedCaptures[name] = entry;
        }
        entry.Add(index);
        return true;
    }

    bool ParseNamedBackReference(RegExpBuilder builder, RegExpParserState state)
    {
        // The parser is assumed to be on the '<' in \k<name>.
        if (Current != '<')
        {
            ReportError(RegExpError.InvalidNamedReference);
            return false;
        }

        Advance();
        string? name = ParseCaptureGroupName();
        if (name is null) return false;

        if (state.IsInsideCaptureGroup(name))
        {
            builder.AddEmpty();
        }
        else
        {
            var atom = new RegExpBackReference { Name = name };
            builder.AddAtom(atom);
            _namedBackReferences ??= new List<RegExpBackReference>(1);
            _namedBackReferences.Add(atom);
        }

        return true;
    }

    // After the initial parsing pass, patch corresponding RegExpCapture objects
    // into all RegExpBackReferences. This is done after initial parsing in order
    // to avoid complicating cases in which references comes before the capture.
    void PatchNamedBackReferences()
    {
        if (_namedBackReferences is null) return;

        if (_namedCaptures is null)
        {
            ReportError(RegExpError.InvalidNamedCaptureReference);
            return;
        }

        // Look up and patch the actual capture for each named back reference.
        for (int i = 0; i < _namedBackReferences.Count; i++)
        {
            RegExpBackReference reference = _namedBackReferences[i];
            if (!_namedCaptures.TryGetValue(reference.Name!, out List<int>? indices))
            {
                ReportError(RegExpError.InvalidNamedCaptureReference);
                return;
            }
            foreach (int index in indices) reference.AddCapture(GetCapture(index));
        }
    }

    // Return the 1-indexed RegExpCapture object, allocate if necessary.
    RegExpCapture GetCapture(int index)
    {
        // The index for the capture groups are one-based. Its index in the list is
        // zero-based.
        int knownCaptures = _isScannedForCaptures ? _captureCount : _capturesStarted;
        if (!(index >= 1 && index <= knownCaptures)) throw new InvalidOperationException("SBXCHECK failed");
        _captures ??= new List<RegExpCapture>(knownCaptures);
        while (_captures.Count < knownCaptures)
        {
            _captures.Add(new RegExpCapture(_captures.Count + 1));
        }
        return _captures[index - 1];
    }

    List<RegExpCapture>? GetNamedCaptures()
    {
        if (_namedCaptures is null) return null;
        Debug.Assert(_namedCaptures.Count != 0);

        var flattenedNamedCaptures = new List<RegExpCapture>();
        foreach (var kv in _namedCaptures)
        {
            foreach (int index in kv.Value) flattenedNamedCaptures.Add(GetCapture(index));
        }
        return flattenedNamedCaptures;
    }

    // Returns true iff the pattern contains named captures. May call
    // ScanForCaptures to look ahead at the remaining pattern.
    bool HasNamedCaptures(InClassEscapeState inClassEscapeState)
    {
        if (_hasNamedCaptures || _isScannedForCaptures) return _hasNamedCaptures;

        ScanForCaptures(inClassEscapeState);
        Debug.Assert(_isScannedForCaptures);
        return _hasNamedCaptures;
    }

    // QuantifierPrefix ::
    //   { DecimalDigits }
    //   { DecimalDigits , }
    //   { DecimalDigits , DecimalDigits }
    //
    // Returns true if parsing succeeds, and set the min_out and max_out
    // values. Values are truncated to RegExpTree::kInfinity if they overflow.
    bool ParseIntervalQuantifier(out int minOut, out int maxOut)
    {
        Debug.Assert(Current == '{');
        minOut = maxOut = 0;
        int start = Position;
        Advance();
        int min = 0;
        if (!IsDecimalDigit(Current))
        {
            Reset(start);
            return false;
        }
        while (IsDecimalDigit(Current))
        {
            int next = Current - '0';
            if (min > (RegExpTree.kInfinity - next) / 10)
            {
                // Overflow. Skip past remaining decimal digits and return -1.
                do
                {
                    Advance();
                } while (IsDecimalDigit(Current));
                min = RegExpTree.kInfinity;
                break;
            }
            min = 10 * min + next;
            Advance();
        }
        int max;
        if (Current == '}')
        {
            max = min;
            Advance();
        }
        else if (Current == ',')
        {
            Advance();
            if (Current == '}')
            {
                max = RegExpTree.kInfinity;
                Advance();
            }
            else
            {
                max = 0;
                while (IsDecimalDigit(Current))
                {
                    int next = Current - '0';
                    if (max > (RegExpTree.kInfinity - next) / 10)
                    {
                        do
                        {
                            Advance();
                        } while (IsDecimalDigit(Current));
                        max = RegExpTree.kInfinity;
                        break;
                    }
                    max = 10 * max + next;
                    Advance();
                }
                if (Current != '}')
                {
                    Reset(start);
                    return false;
                }
                Advance();
            }
        }
        else
        {
            Reset(start);
            return false;
        }
        minOut = min;
        maxOut = max;
        return true;
    }

    int ParseOctalLiteral()
    {
        Debug.Assert(('0' <= Current && Current <= '7') || !HasMore);
        // For compatibility with some other browsers (not all), we parse
        // up to three octal digits with a value below 256.
        // ES#prod-annexB-LegacyOctalEscapeSequence
        int value = Current - '0';
        Advance();
        if ('0' <= Current && Current <= '7')
        {
            value = value * 8 + Current - '0';
            Advance();
            if (value < 32 && '0' <= Current && Current <= '7')
            {
                value = value * 8 + Current - '0';
                Advance();
            }
        }
        return value;
    }

    // Checks whether the following is a length-digit hexadecimal number,
    // and sets the value if it is.
    bool ParseHexEscape(int length, out int value)
    {
        int start = Position;
        int val = 0;
        for (int i = 0; i < length; ++i)
        {
            int c = Current;
            int d = HexValue(c);
            if (d < 0)
            {
                Reset(start);
                value = 0;
                return false;
            }
            val = val * 16 + d;
            Advance();
        }
        value = val;
        return true;
    }

    // This parses RegExpUnicodeEscapeSequence as described in ECMA262.
    bool ParseUnicodeEscape(out int value)
    {
        // Accept both \uxxxx and \u{xxxxxx} (if harmony unicode escapes are
        // allowed). In the latter case, the number of hex digits between { } is
        // arbitrary. \ and u have already been read.
        if (Current == '{' && IsUnicodeMode)
        {
            int start = Position;
            Advance();
            if (ParseUnlimitedLengthHexNumber(0x10FFFF, out value))
            {
                if (Current == '}')
                {
                    Advance();
                    return true;
                }
            }
            Reset(start);
            return false;
        }
        // \u but no {, or \u{...} escapes not allowed.
        bool result = ParseHexEscape(4, out value);
        if (result && IsUnicodeMode && Utf16.IsLeadSurrogate(value) && Current == '\\')
        {
            // Attempt to read trail surrogate.
            int start = Position;
            if (Next() == 'u')
            {
                Advance(2);
                if (ParseHexEscape(4, out int trail) && Utf16.IsTrailSurrogate(trail))
                {
                    value = Utf16.CombineSurrogatePair(value, trail);
                    return true;
                }
            }
            Reset(start);
        }
        return result;
    }

    static bool IsUnicodePropertyValueCharacter(int c)
    {
        // https://tc39.es/proposal-regexp-unicode-property-escapes/
        //
        // Note that using this to validate each parsed char is quite conservative.
        // A possible alternative solution would be to only ensure the parsed
        // property name/value candidate string does not contain '\0' characters and
        // let ICU lookups trigger the final failure.
        if ('a' <= c && c <= 'z') return true;
        if ('A' <= c && c <= 'Z') return true;
        if ('0' <= c && c <= '9') return true;
        return c == '_';
    }

    bool ParsePropertyClassName(out string name1, out string? name2)
    {
        // Parse the property class as follows:
        // - In \p{name}, 'name' is interpreted
        //   - either as a general category property value name.
        //   - or as a binary property name.
        // - In \p{name=value}, 'name' is interpreted as an enumerated property name,
        //   and 'value' is interpreted as one of the available property value names.
        // - Aliases in PropertyAlias.txt and PropertyValueAlias.txt can be used.
        // - Loose matching is not applied.
        name1 = "";
        name2 = null;
        var n1 = new System.Text.StringBuilder();
        if (Current == '{')
        {
            // Parse \p{[PropertyName=]PropertyNameValue}
            for (Advance(); Current != '}' && Current != '='; Advance())
            {
                if (!IsUnicodePropertyValueCharacter(Current)) return false;
                if (!HasNext) return false;
                n1.Append((char)Current);
            }
            if (Current == '=')
            {
                var n2 = new System.Text.StringBuilder();
                for (Advance(); Current != '}'; Advance())
                {
                    if (!IsUnicodePropertyValueCharacter(Current)) return false;
                    if (!HasNext) return false;
                    n2.Append((char)Current);
                }
                name2 = n2.ToString();
            }
        }
        else
        {
            return false;
        }
        Advance();
        name1 = n1.ToString();
        return true;
    }

    void ExtractStringsFromUnicodeSet(int[][] setStrings, CharacterClassStrings strings, RegExpFlags flags)
    {
        Debug.Assert(flags.IsUnicodeSets());
        var stringStorage = new List<RegExpTree>(8);
        var stringBuilder = new RegExpTextBuilder(stringStorage, flags);
        bool needsCaseFolding = flags.IsIgnoreCase();
        foreach (int[] s in setStrings)
        {
            int[] str = new int[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                stringBuilder.AddUnicodeCharacter(c);
                if (needsCaseFolding) c = UnicodeTables.SimpleFold(c);
                str[i] = c;
            }
            strings.TryAdd(str, stringBuilder.ToRegExp());
            stringStorage.Clear();
        }
    }

    ClassSetResult LookupPropertyValueName(UnicodeProperties.PropertyKind property, int binaryIndex,
        string propertyValueName, bool negate, List<CharacterRange> resultRanges,
        CharacterClassStrings? resultStrings, RegExpFlags flags)
    {
        if (!UnicodeProperties.TryGetPropertyValueSet(property, binaryIndex, propertyValueName,
                out int[] ranges, out int[][]? setStrings))
        {
            return ClassSetResult.Failure();
        }
        bool containsStrings = setStrings is { Length: > 0 };
        if (ranges.Length == 0 && !containsStrings) return ClassSetResult.Failure();

        if (containsStrings)
        {
            ExtractStringsFromUnicodeSet(setStrings!, resultStrings!, flags);
        }
        bool needsCaseFolding = flags.IsUnicodeSets() && flags.IsIgnoreCase();
        CodePointSet set = CodePointSet.FromRanges(ranges);
        if (needsCaseFolding) set.CloseOverSimpleCaseInsensitive();
        if (negate) set.Complement();
        for (int i = 0; i < set.RangeCount; i++)
        {
            resultRanges.Add(CharacterRange.Range(set.GetRangeStart(i), set.GetRangeEnd(i)));
        }
        return new ClassSetResult(null, containsStrings);
    }

    bool LookupSpecialPropertyValueName(string name, List<CharacterRange> result, bool negate, RegExpFlags flags)
    {
        if (name == "Any")
        {
            if (negate)
            {
                // Leave the list of character ranges empty, since the negation of 'Any'
                // is the empty set.
            }
            else
            {
                result.Add(CharacterRange.Everything());
            }
        }
        else if (name == "ASCII")
        {
            result.Add(negate ? CharacterRange.Range(0x80, 0x10ffff) : CharacterRange.Range(0x0, 0x7F));
        }
        else if (name == "Assigned")
        {
            return LookupPropertyValueName(UnicodeProperties.PropertyKind.GeneralCategoryMask, -1, "Unassigned",
                !negate, result, null, flags).Success;
        }
        else
        {
            return false;
        }
        return true;
    }

    ClassSetResult AddPropertyClassRange(List<CharacterRange> addToRanges, CharacterClassStrings? addToStrings,
        bool negate, string name1, string? name2)
    {
        if (name2 is null)
        {
            // First attempt to interpret as general category property value name.
            ClassSetResult result = LookupPropertyValueName(UnicodeProperties.PropertyKind.GeneralCategoryMask, -1,
                name1, negate, addToRanges, addToStrings, Flags);
            if (result.Success) return result;
            // Interpret "Any", "ASCII", and "Assigned".
            if (LookupSpecialPropertyValueName(name1, addToRanges, negate, Flags))
            {
                return new ClassSetResult(null, false);
            }
            // Then attempt to interpret as binary property name with value name 'Y'.
            UnicodeProperties.Property? property = UnicodeProperties.LookupBinaryProperty(name1, UnicodeSets);
            if (property is null) return ClassSetResult.Failure();
            // Negation of properties with strings is not allowed.
            // See
            // https://tc39.es/ecma262/#sec-static-semantics-maycontainstrings
            if (negate && property.Value.Kind == UnicodeProperties.PropertyKind.BinaryOfStrings)
            {
                return ClassSetResult.Failure();
            }
            if (UnicodeSets)
            {
                // In /v mode we can't simple lookup the "false" binary property values,
                // as the spec requires us to perform case folding before calculating the
                // complement.
                // See https://tc39.es/ecma262/#sec-compiletocharset
                // UnicodePropertyValueExpression :: LoneUnicodePropertyNameOrValue
                return LookupPropertyValueName(property.Value.Kind, property.Value.Index, "Y", negate,
                    addToRanges, addToStrings, Flags);
            }
            else
            {
                return LookupPropertyValueName(property.Value.Kind, property.Value.Index, negate ? "N" : "Y",
                    false, addToRanges, addToStrings, Flags);
            }
        }
        else
        {
            // Both property name and value name are specified. Attempt to interpret
            // the property name as enumerated property.
            UnicodeProperties.PropertyKind? property = UnicodeProperties.LookupEnumeratedProperty(name1);
            if (property is null) return ClassSetResult.Failure();
            return LookupPropertyValueName(property.Value, -1, name2, negate, addToRanges, addToStrings, Flags);
        }
    }

    bool ParseUnlimitedLengthHexNumber(int maxValue, out int value)
    {
        int x = 0;
        int d = HexValue(Current);
        value = 0;
        if (d < 0) return false;
        while (d >= 0)
        {
            x = x * 16 + d;
            if (x > maxValue) return false;
            Advance();
            d = HexValue(Current);
        }
        value = x;
        return true;
    }

    // https://tc39.es/ecma262/#prod-CharacterEscape
    int ParseCharacterEscape(InClassEscapeState inClassEscapeState, ref bool isEscapedUnicodeCharacter)
    {
        Debug.Assert(Current == '\\');
        Debug.Assert(HasNext);

        Advance();

        int c = Current;
        switch (c)
        {
            // CharacterEscape ::
            //   ControlEscape :: one of
            //     f n r t v
            case 'f':
                Advance();
                return '\f';
            case 'n':
                Advance();
                return '\n';
            case 'r':
                Advance();
                return '\r';
            case 't':
                Advance();
                return '\t';
            case 'v':
                Advance();
                return '\v';
            // CharacterEscape ::
            //   c ControlLetter
            case 'c':
            {
                int controlLetter = Next();
                int letter = controlLetter & ~('A' ^ 'a');
                if (letter >= 'A' && letter <= 'Z')
                {
                    Advance(2);
                    // Control letters mapped to ASCII control characters in the range
                    // 0x00-0x1F.
                    return controlLetter & 0x1F;
                }
                if (IsUnicodeMode)
                {
                    // With /u and /v, invalid escapes are not treated as identity escapes.
                    ReportError(RegExpError.InvalidUnicodeEscape);
                    return 0;
                }
                if (inClassEscapeState == InClassEscapeState.kInClass)
                {
                    // Inside a character class, we also accept digits and underscore as
                    // control characters, unless with /u or /v. See Annex B:
                    // ES#prod-annexB-ClassControlLetter
                    if ((controlLetter >= '0' && controlLetter <= '9') || controlLetter == '_')
                    {
                        Advance(2);
                        return controlLetter & 0x1F;
                    }
                }
                // We match JSC in reading the backslash as a literal
                // character instead of as starting an escape.
                return '\\';
            }
            // CharacterEscape ::
            //   0 [lookahead ∉ DecimalDigit]
            //   [~UnicodeMode] LegacyOctalEscapeSequence
            case '0':
                // \0 is interpreted as NUL if not followed by another digit.
                if (Next() < '0' || Next() > '9')
                {
                    Advance();
                    return 0;
                }
                goto case '1';
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
                // For compatibility, we interpret a decimal escape that isn't
                // a back reference (and therefore either \0 or not valid according
                // to the specification) as a 1..3 digit octal character code.
                // ES#prod-annexB-LegacyOctalEscapeSequence
                if (IsUnicodeMode)
                {
                    // With /u or /v, decimal escape is not interpreted as octal character
                    // code.
                    ReportError(RegExpError.InvalidDecimalEscape);
                    return 0;
                }
                return ParseOctalLiteral();
            // CharacterEscape ::
            //   HexEscapeSequence
            case 'x':
            {
                Advance();
                if (ParseHexEscape(2, out int value)) return value;
                if (IsUnicodeMode)
                {
                    // With /u or /v, invalid escapes are not treated as identity escapes.
                    ReportError(RegExpError.InvalidEscape);
                    return 0;
                }
                // If \x is not followed by a two-digit hexadecimal, treat it
                // as an identity escape.
                return 'x';
            }
            // CharacterEscape ::
            //   RegExpUnicodeEscapeSequence [?UnicodeMode]
            case 'u':
            {
                Advance();
                if (ParseUnicodeEscape(out int value))
                {
                    isEscapedUnicodeCharacter = true;
                    return value;
                }
                if (IsUnicodeMode)
                {
                    // With /u or /v, invalid escapes are not treated as identity escapes.
                    ReportError(RegExpError.InvalidUnicodeEscape);
                    return 0;
                }
                // If \u is not followed by a two-digit hexadecimal, treat it
                // as an identity escape.
                return 'u';
            }
        }

        // CharacterEscape ::
        //   IdentityEscape[?UnicodeMode, ?N]
        //
        // * With /u, no identity escapes except for syntax characters are
        //   allowed.
        // * With /v, no identity escapes except for syntax characters and
        //   ClassSetReservedPunctuators (if within a class) are allowed.
        // * Without /u or /v:
        //   * '\c' is not an IdentityEscape.
        //   * '\k' is not an IdentityEscape when named captures exist.
        //   * Otherwise, all identity escapes are allowed.
        if (UnicodeSets && inClassEscapeState == InClassEscapeState.kInClass)
        {
            if (IsClassSetReservedPunctuator(c))
            {
                Advance();
                return c;
            }
        }
        if (IsUnicodeMode)
        {
            if (!IsSyntaxCharacterOrSlash(c))
            {
                ReportError(RegExpError.InvalidEscape);
                return 0;
            }
            Advance();
            return c;
        }
        Debug.Assert(!IsUnicodeMode);
        if (c == 'c')
        {
            ReportError(RegExpError.InvalidEscape);
            return 0;
        }
        Advance();
        // Note: It's important to Advance before the HasNamedCaptures call s.t. we
        // don't start scanning in the middle of an escape.
        if (c == 'k' && HasNamedCaptures(inClassEscapeState))
        {
            ReportError(RegExpError.InvalidEscape);
            return 0;
        }
        return c;
    }

    // https://tc39.es/ecma262/#prod-ClassRanges
    RegExpTree? ParseClassRanges(List<CharacterRange> ranges, bool addUnicodeCaseEquivalents)
    {
        while (HasMore && Current != ']')
        {
            ParseClassEscape(ranges, addUnicodeCaseEquivalents, out int char1, out bool isClass1);
            if (_failed) return null;
            // ClassAtom
            if (Current == '-')
            {
                Advance();
                if (!HasMore)
                {
                    // If we reach the end we break out of the loop and let the
                    // following code report an error.
                    break;
                }
                else if (Current == ']')
                {
                    if (!isClass1) ranges.Add(CharacterRange.Singleton(char1));
                    ranges.Add(CharacterRange.Singleton('-'));
                    break;
                }
                ParseClassEscape(ranges, addUnicodeCaseEquivalents, out int char2, out bool isClass2);
                if (_failed) return null;
                if (isClass1 || isClass2)
                {
                    // Either end is an escaped character class. Treat the '-' verbatim.
                    if (IsUnicodeMode)
                    {
                        // ES2015 21.2.2.15.1 step 1.
                        return ReportError(RegExpError.InvalidCharacterClass);
                    }
                    if (!isClass1) ranges.Add(CharacterRange.Singleton(char1));
                    ranges.Add(CharacterRange.Singleton('-'));
                    if (!isClass2) ranges.Add(CharacterRange.Singleton(char2));
                    continue;
                }
                // ES2015 21.2.2.15.1 step 6.
                if (char1 > char2) return ReportError(RegExpError.OutOfOrderCharacterClass);
                ranges.Add(CharacterRange.Range(char1, char2));
            }
            else
            {
                if (!isClass1) ranges.Add(CharacterRange.Singleton(char1));
            }
        }
        return null;
    }

    // https://tc39.es/ecma262/#prod-ClassEscape
    // Parse inside a class. Either add escaped class to the range, or return
    // false and pass parsed single character through |char_out|.
    void ParseClassEscape(List<CharacterRange> ranges, bool addUnicodeCaseEquivalents, out int charOut,
        out bool isClassEscape)
    {
        isClassEscape = false;
        charOut = 0;

        if (Current != '\\')
        {
            // Not a ClassEscape.
            charOut = Current;
            Advance();
            return;
        }

        int next = Next();
        switch (next)
        {
            case 'b':
                charOut = '\b';
                Advance(2);
                return;
            case '-':
                if (IsUnicodeMode)
                {
                    charOut = next;
                    Advance(2);
                    return;
                }
                break;
            case kEndMarker:
                ReportError(RegExpError.EscapeAtEndOfPattern);
                return;
        }

        isClassEscape = TryParseCharacterClassEscape(next, InClassEscapeState.kInClass, ranges, null,
            addUnicodeCaseEquivalents).Success;
        if (Failed) return;
        if (isClassEscape) return;

        bool dummy = false;  // Unused.
        charOut = ParseCharacterEscape(InClassEscapeState.kInClass, ref dummy);
    }

    // https://tc39.es/ecma262/#prod-CharacterClassEscape
    ClassSetResult TryParseCharacterClassEscape(int next, InClassEscapeState inClassEscapeState,
        List<CharacterRange> ranges, CharacterClassStrings? strings, bool addUnicodeCaseEquivalents)
    {
        Debug.Assert(Current == '\\');
        Debug.Assert(Next() == next);

        switch (next)
        {
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
                CharacterRange.AddClassEscape((StandardCharacterSet)next, ranges, addUnicodeCaseEquivalents);
                Advance(2);
                return new ClassSetResult(null, false);
            case 'p':
            case 'P':
            {
                if (!IsUnicodeMode) return ClassSetResult.Failure();
                bool negate = next == 'P';
                Advance(2);
                if (!ParsePropertyClassName(out string name1, out string? name2))
                {
                    ReportError(inClassEscapeState == InClassEscapeState.kInClass
                        ? RegExpError.InvalidClassPropertyName
                        : RegExpError.InvalidPropertyName);
                    return ClassSetResult.Failure();
                }
                ClassSetResult result = AddPropertyClassRange(ranges, strings, negate, name1, name2);
                if (!result.Success)
                {
                    ReportError(inClassEscapeState == InClassEscapeState.kInClass
                        ? RegExpError.InvalidClassPropertyName
                        : RegExpError.InvalidPropertyName);
                    return ClassSetResult.Failure();
                }
                return result;
            }
            default:
                return ClassSetResult.Failure();
        }
    }

    // Add |string| to |ranges| if length of |string| == 1, otherwise add |string|
    // to |strings|. Returns true if a string (length != 1) was added.
    static bool AddClassString(List<int> normalizedString, RegExpTree regexpString,
        List<CharacterRange> ranges, CharacterClassStrings strings)
    {
        bool isString = normalizedString.Count != 1;
        if (!isString)
        {
            ranges.Add(CharacterRange.Singleton(normalizedString[0]));
        }
        else
        {
            strings.TryAdd([.. normalizedString], regexpString);
        }
        return isString;
    }

    // https://tc39.es/ecma262/#prod-ClassStringDisjunction
    ClassSetResult ParseClassStringDisjunction(List<CharacterRange> ranges, CharacterClassStrings strings)
    {
        Debug.Assert(UnicodeSets);
        Debug.Assert(Current == '\\');
        Debug.Assert(Next() == 'q');
        Advance(2);
        if (Current != '{')
        {
            // Identity escape of 'q' is not allowed in unicode mode.
            return new ClassSetResult(ReportError(RegExpError.InvalidEscape));
        }
        Advance();

        var str = new List<int>(4);
        var stringStorage = new List<RegExpTree>(8);
        var stringBuilder = new RegExpTextBuilder(stringStorage, Flags);

        bool containsStrings = false;
        while (HasMore && Current != '}')
        {
            if (Current == '|')
            {
                containsStrings |= AddClassString(str, stringBuilder.ToRegExp(), ranges, strings);
                str = new List<int>(4);
                stringStorage.Clear();
                Advance();
            }
            else
            {
                int c = ParseClassSetCharacter();
                if (_failed) return new ClassSetResult(null);
                if (IgnoreCase) c = UnicodeTables.SimpleFold(c);
                str.Add(c);
                stringBuilder.AddUnicodeCharacter(c);
            }
        }

        containsStrings |= AddClassString(str, stringBuilder.ToRegExp(), ranges, strings);
        CharacterRange.Canonicalize(ranges);

        // We don't need to handle missing closing '}' here.
        // If the character class is correctly closed, ParseClassSetCharacter will
        // report an error.
        Advance();
        return new ClassSetResult(null, containsStrings);
    }

    // https://tc39.es/ecma262/#prod-ClassSetOperand
    // Tree returned based on type_out:
    //  * kNestedClass: RegExpClassSetExpression
    //  * For all other types: RegExpClassSetOperand
    ClassSetResult ParseClassSetOperand(RegExpBuilder builder, out ClassSetOperandType typeOut)
    {
        var ranges = new List<CharacterRange>(1);
        var strings = new CharacterClassStrings();
        ClassSetResult operand = ParseClassSetOperand(builder, out typeOut, ranges, strings, out int character);
        if (_failed) return new ClassSetResult(null);
        // ClassSetRange is only used within ClassSetUnion().
        Debug.Assert(typeOut != ClassSetOperandType.kClassSetRange);
        // There are no restrictions for kCharacterClassEscape.
        // CharacterClassEscape includes \p{}, which can contain ranges, strings or
        // both and \P{}, which could contain nothing (i.e. \P{Any}).
        if (operand.Tree is null)
        {
            if (typeOut == ClassSetOperandType.kClassSetCharacter)
            {
                AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Singleton(character));
            }
            operand = operand.WithTree(new RegExpClassSetOperand(ranges, strings));
        }
        return operand;
    }

    // https://tc39.es/ecma262/#prod-ClassSetOperand
    // Based on |type_out| either a tree is returned or
    // |ranges|/|strings|/|character| modified. If a tree is returned,
    // ranges/strings are not modified. If |type_out| is kNestedClass, a tree of
    // type RegExpClassSetExpression is returned. If | type_out| is
    // kClassSetCharacter, |character| is set and nullptr returned. For all other
    // types, |ranges|/|strings|/|character| is modified and nullptr is returned.
    ClassSetResult ParseClassSetOperand(RegExpBuilder builder, out ClassSetOperandType typeOut,
        List<CharacterRange> ranges, CharacterClassStrings strings, out int character)
    {
        Debug.Assert(UnicodeSets);
        character = 0;
        int c = Current;
        if (c == '\\')
        {
            int next = Next();
            if (next == 'q')
            {
                typeOut = ClassSetOperandType.kClassStringDisjunction;
                ClassSetResult r = ParseClassStringDisjunction(ranges, strings);
                if (_failed) return new ClassSetResult(null);
                return r;
            }
            ClassSetResult escapeResult = TryParseCharacterClassEscape(next, InClassEscapeState.kInClass,
                ranges, strings, AddUnicodeCaseEquivalents);
            if (_failed)
            {
                typeOut = ClassSetOperandType.kCharacterClassEscape;
                return new ClassSetResult(null);
            }
            if (escapeResult.Success)
            {
                typeOut = ClassSetOperandType.kCharacterClassEscape;
                return escapeResult;
            }
        }

        if (c == '[')
        {
            typeOut = ClassSetOperandType.kNestedClass;
            return ParseCharacterClass(builder);
        }

        typeOut = ClassSetOperandType.kClassSetCharacter;
        c = ParseClassSetCharacter();
        if (_failed) return new ClassSetResult(null);
        character = c;
        return new ClassSetResult(null, false);
    }

    int ParseClassSetCharacter()
    {
        Debug.Assert(UnicodeSets);
        int c = Current;
        if (c == '\\')
        {
            int next = Next();
            switch (next)
            {
                case 'b':
                    Advance(2);
                    return '\b';
                case kEndMarker:
                    ReportError(RegExpError.EscapeAtEndOfPattern);
                    return 0;
            }
            bool dummy = false;  // Unused.
            return ParseCharacterEscape(InClassEscapeState.kInClass, ref dummy);
        }
        if (IsClassSetSyntaxCharacter(c))
        {
            ReportError(RegExpError.InvalidCharacterInClass);
            return 0;
        }
        if (IsClassSetReservedDoublePunctuator(c))
        {
            ReportError(RegExpError.InvalidClassSetOperation);
            return 0;
        }
        Advance();
        return c;
    }

    static bool MayContainStrings(ClassSetOperandType type, ClassSetResult operand) => type switch
    {
        ClassSetOperandType.kClassSetCharacter or ClassSetOperandType.kClassSetRange => false,
        _ => operand.MayContainStrings,
    };

    void AddMaybeSimpleCaseFoldedRange(List<CharacterRange> ranges, CharacterRange newRange)
    {
        Debug.Assert(UnicodeSets);
        if (IgnoreCase)
        {
            var newRanges = new List<CharacterRange>(2) { newRange };
            CharacterRange.AddUnicodeCaseEquivalents(newRanges);
            ranges.AddRange(newRanges);
        }
        else
        {
            ranges.Add(newRange);
        }
        CharacterRange.Canonicalize(ranges);
    }

    // https://tc39.es/ecma262/#prod-ClassUnion
    ClassSetResult ParseClassUnion(RegExpBuilder builder, bool isNegated, ClassSetResult firstOperand,
        ClassSetOperandType firstOperandType, List<CharacterRange> ranges, CharacterClassStrings strings,
        int character)
    {
        Debug.Assert(UnicodeSets);
        var operands = new List<RegExpTree>(2);
        // Add the lhs to operands if necessary.
        // Either the lhs values were added to |ranges|/|strings| (in which case
        // |first_operand| is nullptr), or the lhs was evaluated to a tree and
        // passed as |first_operand| (in which case |ranges| and |strings| are empty).
        bool mayContainStrings = MayContainStrings(firstOperandType, firstOperand);
        if (firstOperand.Tree is not null) operands.Add(firstOperand.Tree);
        ClassSetOperandType lastType = firstOperandType;
        while (HasMore && Current != ']')
        {
            if (Current == '-')
            {
                // Mix of ClassSetRange and ClassSubtraction is not allowed.
                if (Next() == '-')
                {
                    return new ClassSetResult(ReportError(RegExpError.InvalidClassSetOperation));
                }
                Advance();
                if (!HasMore)
                {
                    // If we reach the end we break out of the loop and let the
                    // following code report an error.
                    break;
                }
                // If the lhs and rhs around '-' are both ClassSetCharacters, they
                // represent a character range.
                // In case one of them is not a ClassSetCharacter, it is a syntax error,
                // as '-' can not be used unescaped within a class with /v.
                // See
                // https://tc39.es/ecma262/#prod-ClassSetRange
                if (lastType != ClassSetOperandType.kClassSetCharacter)
                {
                    return new ClassSetResult(ReportError(RegExpError.InvalidCharacterClass));
                }
                int from = character;
                ParseClassSetOperand(builder, out lastType, ranges, strings, out character);
                if (_failed) return new ClassSetResult(null);
                if (lastType != ClassSetOperandType.kClassSetCharacter)
                {
                    return new ClassSetResult(ReportError(RegExpError.InvalidCharacterClass));
                }
                if (from > character)
                {
                    return new ClassSetResult(ReportError(RegExpError.OutOfOrderCharacterClass));
                }
                AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Range(from, character));
                lastType = ClassSetOperandType.kClassSetRange;
            }
            else
            {
                Debug.Assert(Current != '-');
                if (lastType == ClassSetOperandType.kClassSetCharacter)
                {
                    AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Singleton(character));
                }
                ClassSetResult operand = ParseClassSetOperand(builder, out lastType, ranges, strings, out character);
                if (_failed) return new ClassSetResult(null);
                mayContainStrings |= MayContainStrings(lastType, operand);
                // Add the range we started building as operand and reset the current
                // range.
                if (operand.Tree is not null)
                {
                    if (ranges.Count != 0 || strings.Count != 0)
                    {
                        mayContainStrings |= strings.Count != 0;
                        operands.Add(new RegExpClassSetOperand(ranges, strings));
                        ranges = new List<CharacterRange>(2);
                        strings = new CharacterClassStrings();
                    }
                    operands.Add(operand.Tree);
                }
            }
        }

        if (!HasMore)
        {
            return new ClassSetResult(ReportError(RegExpError.UnterminatedCharacterClass));
        }

        if (lastType == ClassSetOperandType.kClassSetCharacter)
        {
            AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Singleton(character));
        }

        // Add the range we started building as operand.
        if (ranges.Count != 0 || strings.Count != 0)
        {
            mayContainStrings |= strings.Count != 0;
            operands.Add(new RegExpClassSetOperand(ranges, strings));
        }

        Debug.Assert(Current == ']');
        Advance();

        if (isNegated && mayContainStrings)
        {
            return new ClassSetResult(ReportError(RegExpError.NegatedCharacterClassWithStrings));
        }

        RegExpTree tree;
        if (operands.Count == 0)
        {
            // Return empty expression if no operands were added (e.g. [\P{Any}]
            // produces an empty range).
            Debug.Assert(ranges.Count == 0);
            Debug.Assert(strings.Count == 0);
            tree = RegExpClassSetExpression.Empty(isNegated);
        }
        else
        {
            tree = new RegExpClassSetExpression(RegExpClassSetExpression.OperationType.kUnion, isNegated,
                mayContainStrings, operands);
        }

        return new ClassSetResult(tree, mayContainStrings);
    }

    // https://tc39.es/ecma262/#prod-ClassIntersection
    ClassSetResult ParseClassIntersection(RegExpBuilder builder, bool isNegated, ClassSetResult firstOperand,
        ClassSetOperandType firstOperandType)
    {
        Debug.Assert(UnicodeSets);
        Debug.Assert(Current == '&' && Next() == '&');
        bool mayContainStrings = MayContainStrings(firstOperandType, firstOperand);
        var operands = new List<RegExpTree>(2) { firstOperand.Tree! };
        while (HasMore && Current != ']')
        {
            if (Current != '&' || Next() != '&')
            {
                return new ClassSetResult(ReportError(RegExpError.InvalidClassSetOperation));
            }
            Advance(2);
            // [lookahead ≠ &]
            if (Current == '&')
            {
                return new ClassSetResult(ReportError(RegExpError.InvalidCharacterInClass));
            }

            ClassSetResult operand = ParseClassSetOperand(builder, out ClassSetOperandType operandType);
            if (_failed) return new ClassSetResult(null);
            mayContainStrings &= MayContainStrings(operandType, operand);
            operands.Add(operand.Tree!);
        }
        if (!HasMore)
        {
            return new ClassSetResult(ReportError(RegExpError.UnterminatedCharacterClass));
        }
        if (isNegated && mayContainStrings)
        {
            return new ClassSetResult(ReportError(RegExpError.NegatedCharacterClassWithStrings));
        }
        Debug.Assert(Current == ']');
        Advance();
        RegExpTree tree = new RegExpClassSetExpression(RegExpClassSetExpression.OperationType.kIntersection,
            isNegated, mayContainStrings, operands);
        return new ClassSetResult(tree, mayContainStrings);
    }

    // https://tc39.es/ecma262/#prod-ClassSubtraction
    ClassSetResult ParseClassSubtraction(RegExpBuilder builder, bool isNegated, ClassSetResult firstOperand,
        ClassSetOperandType firstOperandType)
    {
        Debug.Assert(UnicodeSets);
        Debug.Assert(Current == '-' && Next() == '-');
        bool mayContainStrings = MayContainStrings(firstOperandType, firstOperand);
        if (isNegated && mayContainStrings)
        {
            return new ClassSetResult(ReportError(RegExpError.NegatedCharacterClassWithStrings));
        }
        var operands = new List<RegExpTree>(2) { firstOperand.Tree! };
        while (HasMore && Current != ']')
        {
            if (Current != '-' || Next() != '-')
            {
                return new ClassSetResult(ReportError(RegExpError.InvalidClassSetOperation));
            }
            Advance(2);
            ClassSetResult operand = ParseClassSetOperand(builder, out _);
            if (_failed) return new ClassSetResult(null);
            operands.Add(operand.Tree!);
        }
        if (!HasMore)
        {
            return new ClassSetResult(ReportError(RegExpError.UnterminatedCharacterClass));
        }
        Debug.Assert(Current == ']');
        Advance();
        RegExpTree tree = new RegExpClassSetExpression(RegExpClassSetExpression.OperationType.kSubtraction,
            isNegated, mayContainStrings, operands);
        return new ClassSetResult(tree, mayContainStrings);
    }

    // https://tc39.es/ecma262/#prod-CharacterClass
    ClassSetResult ParseCharacterClass(RegExpBuilder builder)
    {
        Debug.Assert(Current == '[');
        Advance();
        bool isNegated = false;
        if (Current == '^')
        {
            isNegated = true;
            Advance();
        }
        var ranges = new List<CharacterRange>(2);
        if (Current == ']')
        {
            Advance();
            RegExpTree tree;
            if (UnicodeSets)
            {
                tree = RegExpClassSetExpression.Empty(isNegated);
            }
            else
            {
                RegExpClassRanges.ClassRangesFlags classRangesFlags = RegExpClassRanges.ClassRangesFlags.None;
                if (isNegated) classRangesFlags = RegExpClassRanges.ClassRangesFlags.NEGATED;
                tree = new RegExpClassRanges(ranges, classRangesFlags);
            }
            return new ClassSetResult(tree, false);
        }

        if (!UnicodeSets)
        {
            ParseClassRanges(ranges, AddUnicodeCaseEquivalents);
            if (_failed) return new ClassSetResult(null);
            if (!HasMore)
            {
                return new ClassSetResult(ReportError(RegExpError.UnterminatedCharacterClass));
            }
            Debug.Assert(Current == ']');
            Advance();
            RegExpClassRanges.ClassRangesFlags characterClassFlags = RegExpClassRanges.ClassRangesFlags.None;
            if (isNegated) characterClassFlags = RegExpClassRanges.ClassRangesFlags.NEGATED;
            if (!IgnoreCase) characterClassFlags |= RegExpClassRanges.ClassRangesFlags.NO_CASE_FOLDING_NEEDED;
            if (_isOneByte && !isNegated)
            {
                // No surrogate pairs.
                characterClassFlags |= RegExpClassRanges.ClassRangesFlags.IS_CERTAINLY_ONE_CODE_POINT;
            }
            RegExpTree tree = new RegExpClassRanges(ranges, characterClassFlags);
            return new ClassSetResult(tree, false);
        }
        else
        {
            var strings = new CharacterClassStrings();
            ClassSetResult operand = ParseClassSetOperand(builder, out ClassSetOperandType operandType, ranges,
                strings, out int character);
            if (_failed) return new ClassSetResult(null);
            switch (Current)
            {
                case '-':
                    if (Next() == '-')
                    {
                        if (operand.Tree is null)
                        {
                            if (operandType == ClassSetOperandType.kClassSetCharacter)
                            {
                                AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Singleton(character));
                            }
                            operand = operand.WithTree(new RegExpClassSetOperand(ranges, strings));
                        }
                        return ParseClassSubtraction(builder, isNegated, operand, operandType);
                    }
                    // ClassSetRange is handled in ParseClassUnion().
                    break;
                case '&':
                    if (Next() == '&')
                    {
                        if (operand.Tree is null)
                        {
                            if (operandType == ClassSetOperandType.kClassSetCharacter)
                            {
                                AddMaybeSimpleCaseFoldedRange(ranges, CharacterRange.Singleton(character));
                            }
                            operand = operand.WithTree(new RegExpClassSetOperand(ranges, strings));
                        }
                        return ParseClassIntersection(builder, isNegated, operand, operandType);
                    }
                    break;
            }
            return ParseClassUnion(builder, isNegated, operand, operandType, ranges, strings, character);
        }
    }

    public bool Parse(RegExpCompileData result)
    {
        RegExpTree? tree = ParsePattern();

        if (Failed)
        {
            Debug.Assert(tree is null);
            Debug.Assert(_error != RegExpError.None);
            result.Error = _error;
            result.ErrorPos = _errorPos;
            return false;
        }

        Debug.Assert(_error == RegExpError.None);
        Debug.Assert(tree is not null);
        result.Tree = tree;
        int captureCount = CapturesStarted;
        result.Simple = tree!.IsAtom() && Simple && captureCount == 0;
        result.ContainsAnchor = ContainsAnchor;
        result.CaptureCount = captureCount;
        result.NamedCaptures = GetNamedCaptures();
        return true;
    }
}
