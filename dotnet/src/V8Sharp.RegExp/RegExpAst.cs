// Port of src/regexp/regexp-ast.h and src/regexp/regexp-ast.cc.
//
// V8 has recently moved irregexp into namespace v8::internal::regexp and
// dropped the "RegExp" prefix from the AST classes (regexp::Tree,
// regexp::Disjunction ...). The C# port keeps the prefix (RegExpTree,
// RegExpDisjunction ...), which is also the name these classes had for most
// of V8's history, so they do not collide with V8Sharp.Ast.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace V8Sharp.RegExp;

/// <summary>Visitor over the regexp AST (regexp::Visitor).</summary>
public interface IRegExpVisitor
{
    object? VisitDisjunction(RegExpDisjunction that, object? data);
    object? VisitAlternative(RegExpAlternative that, object? data);
    object? VisitAssertion(RegExpAssertion that, object? data);
    object? VisitClassRanges(RegExpClassRanges that, object? data);
    object? VisitClassSetOperand(RegExpClassSetOperand that, object? data);
    object? VisitClassSetExpression(RegExpClassSetExpression that, object? data);
    object? VisitAtom(RegExpAtom that, object? data);
    object? VisitQuantifier(RegExpQuantifier that, object? data);
    object? VisitCapture(RegExpCapture that, object? data);
    object? VisitGroup(RegExpGroup that, object? data);
    object? VisitLookaround(RegExpLookaround that, object? data);
    object? VisitBackReference(RegExpBackReference that, object? data);
    object? VisitEmpty(RegExpEmpty that, object? data);
    object? VisitText(RegExpText that, object? data);
}

/// <summary>A simple closed interval.</summary>
public readonly struct Interval
{
    public const int kNone = -1;
    const int kInvalid = -2;

    readonly int _from;
    readonly int _to;

    public Interval() { _from = kNone; _to = kNone - 1; }  // '- 1' for branchless size().
    public Interval(int from, int to) { _from = from; _to = to; }

    public static Interval Empty => new();
    public static Interval Invalid => new(kInvalid, kInvalid);

    public Interval Union(Interval that)
    {
        if (!IsValid) return this;
        if (!that.IsValid) return that;
        if (that._from == kNone) return this;
        if (_from == kNone) return that;
        return new Interval(Math.Min(_from, that._from), Math.Max(_to, that._to));
    }

    public bool Contains(int value) => _from <= value && value <= _to;
    public bool IsEmpty => _from == kNone;
    public bool IsValid => _from != kInvalid;
    public int From => _from;
    public int To => _to;
    public int Size => _to - _from + 1;
}

/// <summary>Named standard character sets.</summary>
public enum StandardCharacterSet : byte
{
    kWhitespace = (byte)'s',         // Like /\s/.
    kNotWhitespace = (byte)'S',      // Like /\S/.
    kWord = (byte)'w',               // Like /\w/.
    kNotWord = (byte)'W',            // Like /\W/.
    kDigit = (byte)'d',              // Like /\d/.
    kNotDigit = (byte)'D',           // Like /\D/.
    kLineTerminator = (byte)'n',     // The inverse of /./.
    kNotLineTerminator = (byte)'.',  // Like /./.
    kEverything = (byte)'*',         // Matches every character, like /./s.
}

/// <summary>
/// Represents code points (with values up to 0x10FFFF) in the range from
/// <c>From</c> to <c>To</c>, both ends inclusive.
/// </summary>
public partial struct CharacterRange : IEquatable<CharacterRange>
{
    internal const int kMaxCodePoint = 0x10ffff;

    internal int _from;
    internal int _to;

    CharacterRange(int from, int to) { _from = from; _to = to; }

    public static CharacterRange Singleton(int value) => new(value, value);
    public static CharacterRange Range(int from, int to)
    {
        Debug.Assert(0 <= from && to <= kMaxCodePoint);
        Debug.Assert((uint)from <= (uint)to);
        return new CharacterRange(from, to);
    }
    public static CharacterRange Everything() => new(0, kMaxCodePoint);

    public static List<CharacterRange> List(CharacterRange range) => [range];

    public readonly bool Contains(int i) => _from <= i && i <= _to;
    public readonly int From => _from;
    public readonly int To => _to;
    public readonly bool IsEverything(int max) => _from == 0 && _to >= max;
    public readonly bool IsSingleton => _from == _to;

    public readonly bool Equals(CharacterRange other) => _from == other._from && _to == other._to;
    public override readonly bool Equals(object? obj) => obj is CharacterRange r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(_from, _to);
    public static bool operator ==(CharacterRange lhs, CharacterRange rhs) => lhs.Equals(rhs);
    public static bool operator !=(CharacterRange lhs, CharacterRange rhs) => !lhs.Equals(rhs);
    public override readonly string ToString() => IsSingleton ? $"{_from:x}" : $"{_from:x}-{_to:x}";
}

/// <summary>StackLimiter: a recursion budget with a real stack check once exhausted.</summary>
public readonly struct StackLimiter(int budget)
{
    readonly int _budget = budget;

    public static StackLimiter operator -(StackLimiter s, int value) => new(s._budget - value);

    public bool IsOverflowed()
    {
        if (_budget > 0) return false;
        // This can be a little slow so we don't do it until the soft budget has
        // been exhausted.
        return !RuntimeHelpers.TryEnsureSufficientExecutionStack();
    }
}

internal static class Debug
{
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Assert([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException("DCHECK failed" + (message is null ? "" : ": " + message));
    }
}

/// <summary>regexp::Tree.</summary>
public abstract partial class RegExpTree
{
    public const int kInfinity = int.MaxValue;

    public abstract object? Accept(IRegExpVisitor visitor, object? data);
    public virtual bool IsTextElement() => false;
    public virtual bool IsCertainlyAnchoredAtStart(int budget) => false;
    public virtual bool IsCertainlyAnchoredAtEnd(int budget) => false;
    public abstract int MinMatch { get; }
    public abstract int MaxMatch { get; }
    /// <summary>Returns the interval of registers used for captures within this expression.</summary>
    public virtual Interval CaptureRegisters(StackLimiter limiter) => Interval.Empty;
    public virtual void AppendToText(RegExpText text) => throw new InvalidOperationException("UNREACHABLE");

    public virtual RegExpDisjunction? AsDisjunction() => null;
    public virtual RegExpAlternative? AsAlternative() => null;
    public virtual RegExpAssertion? AsAssertion() => null;
    public virtual RegExpClassRanges? AsClassRanges() => null;
    public virtual RegExpClassSetOperand? AsClassSetOperand() => null;
    public virtual RegExpClassSetExpression? AsClassSetExpression() => null;
    public virtual RegExpAtom? AsAtom() => null;
    public virtual RegExpQuantifier? AsQuantifier() => null;
    public virtual RegExpCapture? AsCapture() => null;
    public virtual RegExpGroup? AsGroup() => null;
    public virtual RegExpLookaround? AsLookaround() => null;
    public virtual RegExpBackReference? AsBackReference() => null;
    public virtual RegExpEmpty? AsEmpty() => null;
    public virtual RegExpText? AsText() => null;

    public bool IsDisjunction() => this is RegExpDisjunction;
    public bool IsAlternative() => this is RegExpAlternative;
    public bool IsAssertion() => this is RegExpAssertion;
    public bool IsClassRanges() => this is RegExpClassRanges;
    public bool IsClassSetOperand() => this is RegExpClassSetOperand;
    public bool IsClassSetExpression() => this is RegExpClassSetExpression;
    public bool IsAtom() => this is RegExpAtom;
    public bool IsQuantifier() => this is RegExpQuantifier;
    public bool IsCapture() => this is RegExpCapture;
    public bool IsGroup() => this is RegExpGroup;
    public bool IsLookaround() => this is RegExpLookaround;
    public bool IsBackReference() => this is RegExpBackReference;
    public bool IsEmpty() => this is RegExpEmpty;
    public bool IsText() => this is RegExpText;

    internal static Interval ListCaptureRegisters(StackLimiter limiter, List<RegExpTree> children)
    {
        if (limiter.IsOverflowed()) return Interval.Invalid;
        Interval result = Interval.Empty;
        for (int i = 0; i < children.Count; i++)
        {
            result = result.Union(children[i].CaptureRegisters(limiter - 1));
        }
        return result;
    }

    internal static int IncreaseBy(int previous, int increase)
    {
        if (kInfinity - previous < increase) return kInfinity;
        return previous + increase;
    }
}

public sealed partial class RegExpDisjunction : RegExpTree
{
    readonly List<RegExpTree> _alternatives;
    readonly int _minMatch;
    readonly int _maxMatch;

    public RegExpDisjunction(List<RegExpTree> alternatives)
    {
        Debug.Assert(alternatives.Count > 1);
        _alternatives = alternatives;
        RegExpTree first = alternatives[0];
        _minMatch = first.MinMatch;
        _maxMatch = first.MaxMatch;
        for (int i = 1; i < alternatives.Count; i++)
        {
            RegExpTree alternative = alternatives[i];
            _minMatch = Math.Min(_minMatch, alternative.MinMatch);
            _maxMatch = Math.Max(_maxMatch, alternative.MaxMatch);
        }
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitDisjunction(this, data);
    public override RegExpDisjunction AsDisjunction() => this;
    public override Interval CaptureRegisters(StackLimiter limiter) => ListCaptureRegisters(limiter - 1, _alternatives);

    public override bool IsCertainlyAnchoredAtStart(int budget)
    {
        if (budget < 0) return false;
        for (int i = 0; i < _alternatives.Count; i++)
        {
            if (!_alternatives[i].IsCertainlyAnchoredAtStart(budget - 1)) return false;
        }
        return true;
    }

    public override bool IsCertainlyAnchoredAtEnd(int budget)
    {
        if (budget < 0) return false;
        for (int i = 0; i < _alternatives.Count; i++)
        {
            if (!_alternatives[i].IsCertainlyAnchoredAtEnd(budget - 1)) return false;
        }
        return true;
    }

    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;
    public List<RegExpTree> Alternatives => _alternatives;
}

public sealed partial class RegExpAlternative : RegExpTree
{
    readonly List<RegExpTree> _nodes;
    readonly int _minMatch;
    readonly int _maxMatch;

    public RegExpAlternative(List<RegExpTree> nodes)
    {
        Debug.Assert(nodes.Count > 1);
        _nodes = nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            RegExpTree node = nodes[i];
            _minMatch = IncreaseBy(_minMatch, node.MinMatch);
            _maxMatch = IncreaseBy(_maxMatch, node.MaxMatch);
        }
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitAlternative(this, data);
    public override RegExpAlternative AsAlternative() => this;
    public override Interval CaptureRegisters(StackLimiter limiter) => ListCaptureRegisters(limiter - 1, _nodes);

    public override bool IsCertainlyAnchoredAtStart(int budget)
    {
        if (budget < 0) return false;
        for (int i = 0; i < _nodes.Count; i++)
        {
            RegExpTree node = _nodes[i];
            if (node.IsCertainlyAnchoredAtStart(budget - 1)) return true;
            if (node.MaxMatch > 0) return false;
        }
        return false;
    }

    public override bool IsCertainlyAnchoredAtEnd(int budget)
    {
        if (budget < 0) return false;
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            RegExpTree node = _nodes[i];
            if (node.IsCertainlyAnchoredAtEnd(budget - 1)) return true;
            if (node.MaxMatch > 0) return false;
        }
        return false;
    }

    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;
    public List<RegExpTree> Nodes => _nodes;
}

public sealed partial class RegExpAssertion(RegExpAssertion.Type type) : RegExpTree
{
    public enum Type
    {
        START_OF_LINE = 0,
        START_OF_INPUT = 1,
        END_OF_LINE = 2,
        END_OF_INPUT = 3,
        END_OF_BUFFER = 4,
        BOUNDARY = 5,
        NON_BOUNDARY = 6,
        LAST_ASSERTION_TYPE = NON_BOUNDARY,
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitAssertion(this, data);
    public override RegExpAssertion AsAssertion() => this;
    public override bool IsCertainlyAnchoredAtStart(int budget) => AssertionType == Type.START_OF_INPUT;
    public override bool IsCertainlyAnchoredAtEnd(int budget) => AssertionType == Type.END_OF_INPUT;
    public override int MinMatch => 0;
    public override int MaxMatch => 0;
    public Type AssertionType { get; } = type;
}

/// <summary>regexp::CharacterSet: either a standard set or explicit ranges.</summary>
public sealed class CharacterSet
{
    List<CharacterRange>? _ranges;
    StandardCharacterSet? _standardSetType;

    public CharacterSet(StandardCharacterSet standardSetType) => _standardSetType = standardSetType;
    public CharacterSet(List<CharacterRange> ranges) => _ranges = ranges;

    public List<CharacterRange> Ranges
    {
        get
        {
            if (_ranges is null)
            {
                _ranges = new List<CharacterRange>(2);
                CharacterRange.AddClassEscape(_standardSetType!.Value, _ranges, false);
            }
            return _ranges;
        }
    }

    public StandardCharacterSet StandardSetType
    {
        get => _standardSetType!.Value;
        set => _standardSetType = value;
    }
    public bool IsStandard => _standardSetType.HasValue;

    public void Canonicalize()
    {
        // Special/default classes are always considered canonical. The result
        // of calling ranges() will be sorted.
        if (_ranges is null) return;
        CharacterRange.Canonicalize(_ranges);
    }
}

public sealed partial class RegExpClassRanges : RegExpTree
{
    /// <summary>
    /// NEGATED: The character class is negated and should match everything but
    ///     the specified ranges.
    /// CONTAINS_SPLIT_SURROGATE: The character class contains part of a split
    ///     surrogate and should not be unicode-desugared (crbug.com/641091).
    /// NO_CASE_FOLDING_NEEDED: If case folding is required (/i), it was already
    ///     performed on individual ranges and should not be applied again.
    /// </summary>
    [Flags]
    public enum ClassRangesFlags
    {
        None = 0,
        NEGATED = 1 << 0,
        CONTAINS_SPLIT_SURROGATE = 1 << 1,
        NO_CASE_FOLDING_NEEDED = 1 << 2,
        IS_CERTAINLY_ONE_CODE_POINT = 1 << 3,
        IS_CERTAINLY_TWO_CODE_POINTS = 1 << 4,
    }

    readonly CharacterSet _set;
    ClassRangesFlags _classRangesFlags;

    public RegExpClassRanges(List<CharacterRange> ranges, ClassRangesFlags classRangesFlags = ClassRangesFlags.None)
    {
        _set = new CharacterSet(ranges);
        _classRangesFlags = classRangesFlags;
        // Convert the empty set of ranges to the negated Everything() range.
        if (ranges.Count == 0)
        {
            ranges.Add(CharacterRange.Everything());
            _classRangesFlags ^= ClassRangesFlags.NEGATED;
        }
        if (!IsNegated && !IsCertainlyTwoCodePoints && NoCaseFoldingNeeded)
        {
            // Perhaps we can detect that it is always two code points.
            bool foundBasicPlane = false;
            for (int i = 0; i < ranges.Count; i++)
            {
                if (ranges[i].From < 0x10000)
                {
                    foundBasicPlane = true;
                    break;
                }
            }
            if (!foundBasicPlane) _classRangesFlags |= ClassRangesFlags.IS_CERTAINLY_TWO_CODE_POINTS;
        }
    }

    public RegExpClassRanges(StandardCharacterSet standardSetType)
    {
        _set = new CharacterSet(standardSetType);
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitClassRanges(this, data);
    public override RegExpClassRanges AsClassRanges() => this;
    public override bool IsTextElement() => true;
    public override int MinMatch => IsCertainlyTwoCodePoints ? 2 : 1;
    // The character class may match two code units for unicode regexps.
    public override int MaxMatch => IsCertainlyOneCodePoint ? 1 : 2;
    public override void AppendToText(RegExpText text) => text.AddElement(TextElement.FromClassRanges(this));

    public StandardCharacterSet StandardType => _set.StandardSetType;
    public CharacterSet CharacterSet => _set;
    public List<CharacterRange> Ranges => _set.Ranges;

    public bool IsNegated => (_classRangesFlags & ClassRangesFlags.NEGATED) != 0;
    public bool ContainsSplitSurrogate => (_classRangesFlags & ClassRangesFlags.CONTAINS_SPLIT_SURROGATE) != 0;
    public bool NoCaseFoldingNeeded => (_classRangesFlags & ClassRangesFlags.NO_CASE_FOLDING_NEEDED) != 0;
    public bool IsCertainlyOneCodePoint => (_classRangesFlags & ClassRangesFlags.IS_CERTAINLY_ONE_CODE_POINT) != 0;
    public bool IsCertainlyTwoCodePoints => (_classRangesFlags & ClassRangesFlags.IS_CERTAINLY_TWO_CODE_POINTS) != 0;
}

/// <summary>
/// CharacterClassStringLess: longer strings first so we generate matches for
/// the largest string possible, then code point order.
/// </summary>
public sealed class CharacterClassStringLess : IComparer<int[]>
{
    public static readonly CharacterClassStringLess Instance = new();

    public int Compare(int[]? lhs, int[]? rhs)
    {
        if (lhs!.Length != rhs!.Length) return lhs.Length > rhs.Length ? -1 : 1;
        for (int i = 0; i < lhs.Length; i++)
        {
            if (lhs[i] != rhs[i]) return lhs[i] < rhs[i] ? -1 : 1;
        }
        return 0;
    }
}

/// <summary>
/// Strings as part of character classes (only possible in unicode sets mode).
/// An ordered map (V8's ZoneMap) because the longest alternatives must be
/// matched first.
/// </summary>
public sealed class CharacterClassStrings() : SortedDictionary<int[], RegExpTree>(CharacterClassStringLess.Instance);

public sealed partial class RegExpClassSetOperand : RegExpTree
{
    List<CharacterRange> _ranges;
    CharacterClassStrings? _strings;
    readonly int _minMatch;
    readonly int _maxMatch;

    public RegExpClassSetOperand(List<CharacterRange> ranges, CharacterClassStrings? strings)
    {
        _ranges = ranges;
        _strings = strings;
        if (ranges.Count != 0)
        {
            _minMatch = 1;
            _maxMatch = 2;
        }
        if (HasStrings)
        {
            foreach (var kv in strings!)
            {
                _minMatch = Math.Min(_minMatch, kv.Value.MinMatch);
                _maxMatch = Math.Max(_maxMatch, kv.Value.MaxMatch);
            }
        }
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitClassSetOperand(this, data);
    public override RegExpClassSetOperand AsClassSetOperand() => this;
    public override bool IsTextElement() => true;
    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;

    public bool HasStrings => _strings is not null && _strings.Count != 0;
    public List<CharacterRange> Ranges { get => _ranges; internal set => _ranges = value; }
    public CharacterClassStrings Strings => _strings!;
    internal CharacterClassStrings? StringsOrNull { get => _strings; set => _strings = value; }
}

public sealed partial class RegExpClassSetExpression : RegExpTree
{
    public enum OperationType { kUnion, kIntersection, kSubtraction }

    readonly OperationType _operation;
    bool _isNegated;
    readonly bool _mayContainStrings;
    readonly List<RegExpTree> _operands;
    readonly int _maxMatch;

    public RegExpClassSetExpression(OperationType op, bool isNegated, bool mayContainStrings, List<RegExpTree> operands)
    {
        _operation = op;
        _isNegated = isNegated;
        _mayContainStrings = mayContainStrings;
        _operands = operands;
        if (isNegated)
        {
            Debug.Assert(!mayContainStrings);
            // We don't know anything about max matches for negated classes.
            // As there are no strings involved, assume that we can match a unicode
            // character (2 code points).
            _maxMatch = 2;
        }
        else
        {
            _maxMatch = 0;
            foreach (RegExpTree operand in operands) _maxMatch = Math.Max(_maxMatch, operand.MaxMatch);
        }
    }

    /// <summary>Create an empty class set expression (matches everything if
    /// isNegated, nothing otherwise).</summary>
    public static RegExpClassSetExpression Empty(bool isNegated)
    {
        var op = new RegExpClassSetOperand(new List<CharacterRange>(0), null);
        var operands = new List<RegExpTree>(1) { op };
        return new RegExpClassSetExpression(OperationType.kUnion, isNegated, false, operands);
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitClassSetExpression(this, data);
    public override RegExpClassSetExpression AsClassSetExpression() => this;
    public override bool IsTextElement() => true;
    public override int MinMatch => 0;
    public override int MaxMatch => _maxMatch;

    public OperationType Operation => _operation;
    public bool IsNegated => _isNegated;
    public bool MayContainStrings => _mayContainStrings;
    public List<RegExpTree> Operands => _operands;
}

public sealed partial class RegExpAtom(string data) : RegExpTree
{
    public override object? Accept(IRegExpVisitor visitor, object? d) => visitor.VisitAtom(this, d);
    public override RegExpAtom AsAtom() => this;
    public override bool IsTextElement() => true;
    public override int MinMatch => Data.Length;
    public override int MaxMatch => Data.Length;
    public override void AppendToText(RegExpText text) => text.AddElement(TextElement.FromAtom(this));

    /// <summary>The UTF-16 code units of the atom.</summary>
    public string Data { get; } = data;
    public int Length => Data.Length;
}

public struct TextElement
{
    public enum TextType { ATOM, CLASS_RANGES }

    public static TextElement FromAtom(RegExpAtom atom) => new(TextType.ATOM, atom);
    public static TextElement FromClassRanges(RegExpClassRanges classRanges) => new(TextType.CLASS_RANGES, classRanges);

    TextElement(TextType textType, RegExpTree tree)
    {
        CpOffset = -1;
        Type = textType;
        Tree = tree;
    }

    public int CpOffset { get; set; }
    public readonly TextType Type { get; }
    public readonly RegExpTree Tree { get; }

    public readonly int Length => Type switch
    {
        TextType.ATOM => Atom.Length,
        // Length of a character class is one code unit.
        _ => 1,
    };

    public readonly RegExpAtom Atom => (RegExpAtom)Tree;
    public readonly RegExpClassRanges ClassRanges => (RegExpClassRanges)Tree;
}

public sealed partial class RegExpText : RegExpTree
{
    readonly List<TextElement> _elements = new(2);
    int _length;

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitText(this, data);
    public override RegExpText AsText() => this;
    public override bool IsTextElement() => true;
    public override int MinMatch => _length;
    public override int MaxMatch => _length;
    public override void AppendToText(RegExpText text)
    {
        for (int i = 0; i < _elements.Count; i++) text.AddElement(_elements[i]);
    }

    public void AddElement(TextElement elm)
    {
        _elements.Add(elm);
        _length += elm.Length;
    }

    public List<TextElement> Elements => _elements;

    public bool StartsWithAtom() => _elements.Count != 0 && _elements[0].Type == TextElement.TextType.ATOM;
    public RegExpAtom FirstAtom() => _elements[0].Atom;
}

public sealed partial class RegExpQuantifier : RegExpTree
{
    public enum QuantifierType { GREEDY, NON_GREEDY, POSSESSIVE }

    readonly int _minMatch;
    readonly int _maxMatch;

    public RegExpQuantifier(int min, int max, QuantifierType type, int index, RegExpTree body)
    {
        Body = body;
        Min = min;
        Max = max;
        Type = type;
        Index = index;
        if (min > 0 && body.MinMatch > kInfinity / min) _minMatch = kInfinity;
        else _minMatch = min * body.MinMatch;
        if (max > 0 && body.MaxMatch > kInfinity / max) _maxMatch = kInfinity;
        else _maxMatch = max * body.MaxMatch;
    }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitQuantifier(this, data);
    public override RegExpQuantifier AsQuantifier() => this;
    public override Interval CaptureRegisters(StackLimiter limiter)
    {
        if (limiter.IsOverflowed()) return Interval.Invalid;
        return Body.CaptureRegisters(limiter - 1);
    }
    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;
    public int Min { get; }
    public int Max { get; }
    public QuantifierType Type { get; }
    public int Index { get; }
    public bool IsPossessive => Type == QuantifierType.POSSESSIVE;
    public bool IsNonGreedy => Type == QuantifierType.NON_GREEDY;
    public bool IsGreedy => Type == QuantifierType.GREEDY;
    public RegExpTree Body { get; }
}

public sealed partial class RegExpCapture(int index) : RegExpTree
{
    RegExpTree? _body;
    int _minMatch;
    int _maxMatch;

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitCapture(this, data);
    public override RegExpCapture AsCapture() => this;

    public override bool IsCertainlyAnchoredAtStart(int budget)
    {
        if (budget < 0) return false;
        return Body.IsCertainlyAnchoredAtStart(budget - 1);
    }

    public override bool IsCertainlyAnchoredAtEnd(int budget)
    {
        if (budget < 0) return false;
        return Body.IsCertainlyAnchoredAtEnd(budget - 1);
    }

    public override Interval CaptureRegisters(StackLimiter limiter)
    {
        if (limiter.IsOverflowed()) return Interval.Invalid;
        var self = new Interval(StartRegister(Index), EndRegister(Index));
        return self.Union(Body.CaptureRegisters(limiter - 1));
    }

    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;
    public RegExpTree Body
    {
        get => _body!;
        set
        {
            _body = value;
            _minMatch = value.MinMatch;
            _maxMatch = value.MaxMatch;
        }
    }
    public int Index { get; } = index;
    /// <summary>The capture group name as UTF-16 code units, or null.</summary>
    public string? Name { get; set; }
    public static int StartRegister(int index) => index * 2;
    public static int EndRegister(int index) => index * 2 + 1;
}

public sealed partial class RegExpGroup(RegExpTree body, RegExpFlags flags) : RegExpTree
{
    readonly int _minMatch = body.MinMatch;
    readonly int _maxMatch = body.MaxMatch;

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitGroup(this, data);
    public override RegExpGroup AsGroup() => this;

    public override bool IsCertainlyAnchoredAtStart(int budget)
    {
        if (budget < 0) return false;
        return Body.IsCertainlyAnchoredAtStart(budget - 1);
    }

    public override bool IsCertainlyAnchoredAtEnd(int budget)
    {
        if (budget < 0) return false;
        return Body.IsCertainlyAnchoredAtEnd(budget - 1);
    }

    public override int MinMatch => _minMatch;
    public override int MaxMatch => _maxMatch;
    public override Interval CaptureRegisters(StackLimiter limiter)
    {
        if (limiter.IsOverflowed()) return Interval.Invalid;
        return Body.CaptureRegisters(limiter - 1);
    }
    public RegExpTree Body { get; } = body;
    public RegExpFlags Flags { get; } = flags;
}

public sealed partial class RegExpLookaround(RegExpTree body, bool isPositive, int captureCount, int captureFrom,
    RegExpLookaround.Type type, int index) : RegExpTree
{
    public enum Type { LOOKAHEAD, LOOKBEHIND }

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitLookaround(this, data);
    public override RegExpLookaround AsLookaround() => this;

    public override Interval CaptureRegisters(StackLimiter limiter)
    {
        if (limiter.IsOverflowed()) return Interval.Invalid;
        return Body.CaptureRegisters(limiter - 1);
    }

    public override bool IsCertainlyAnchoredAtStart(int budget)
    {
        if (budget < 0) return false;
        return IsPositive && LookaroundType == Type.LOOKAHEAD && Body.IsCertainlyAnchoredAtStart(budget - 1);
    }

    public override int MinMatch => 0;
    public override int MaxMatch => 0;
    public RegExpTree Body { get; } = body;
    public bool IsPositive { get; } = isPositive;
    public int CaptureCount { get; } = captureCount;
    public int CaptureFrom { get; } = captureFrom;
    public Type LookaroundType { get; } = type;
    public int Index { get; } = index;
}

public sealed partial class RegExpBackReference : RegExpTree
{
    readonly List<RegExpCapture> _captures = new(1);

    public RegExpBackReference() { }
    public RegExpBackReference(RegExpCapture capture) => _captures.Add(capture);

    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitBackReference(this, data);
    public override RegExpBackReference AsBackReference() => this;
    public override int MinMatch => 0;
    // The back reference may be recursive, e.g. /(\2)(\1)/. To avoid infinite
    // recursion, we give up. Ignorance is bliss.
    public override int MaxMatch => kInfinity;
    public List<RegExpCapture> Captures => _captures;
    public void AddCapture(RegExpCapture capture) => _captures.Add(capture);
    public string? Name { get; set; }
}

public sealed partial class RegExpEmpty : RegExpTree
{
    public override object? Accept(IRegExpVisitor visitor, object? data) => visitor.VisitEmpty(this, data);
    public override RegExpEmpty AsEmpty() => this;
    public override int MinMatch => 0;
    public override int MaxMatch => 0;
}

internal static class ListExtensions
{
    /// <summary>ZoneList::Rewind.</summary>
    public static void Rewind<T>(this List<T> list, int pos)
    {
        if (pos < list.Count) list.RemoveRange(pos, list.Count - pos);
    }

    public static ref T At<T>(this List<T> list, int index) => ref CollectionsMarshal.AsSpan(list)[index];
}
