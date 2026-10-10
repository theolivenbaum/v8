// Port of src/regexp/regexp-nodes.h.

namespace V8Sharp.RegExp;

public interface INodeVisitor
{
    void VisitEnd(EndNode that);
    void VisitAction(ActionNode that);
    void VisitChoice(ChoiceNode that);
    void VisitLoopChoice(LoopChoiceNode that);
    void VisitNegativeLookaroundChoice(NegativeLookaroundChoiceNode that);
    void VisitBackReference(BackReferenceNode that);
    void VisitAssertion(AssertionNode that);
    void VisitText(TextNode that);
    void VisitUnanchoredAdvance(UnanchoredAdvanceNode that);
}

public struct NodeInfo
{
    public bool BeingAnalyzed;
    public bool BeenAnalyzed;

    // These bits are set of this node has to know what the preceding
    // character was.
    public bool FollowsWordInterest;
    public bool FollowsNewlineInterest;
    public bool FollowsStartInterest;

    public bool AtEnd;
    public bool Visited;
    public bool ReplacementCalculated;

    // Returns true if the interests and assumptions of this node
    // matches the given one.
    public readonly bool Matches(in NodeInfo that) =>
        AtEnd == that.AtEnd && FollowsWordInterest == that.FollowsWordInterest &&
        FollowsNewlineInterest == that.FollowsNewlineInterest && FollowsStartInterest == that.FollowsStartInterest;

    // Updates the interests of this node given the interests of the
    // node preceding it.
    public void AddFromPreceding(in NodeInfo that)
    {
        AtEnd |= that.AtEnd;
        FollowsWordInterest |= that.FollowsWordInterest;
        FollowsNewlineInterest |= that.FollowsNewlineInterest;
        FollowsStartInterest |= that.FollowsStartInterest;
    }

    public readonly bool HasLookbehind() => FollowsWordInterest || FollowsNewlineInterest || FollowsStartInterest;

    // Sets the interests of this node to include the interests of the
    // following node.
    public void AddFromFollowing(in NodeInfo that)
    {
        FollowsWordInterest |= that.FollowsWordInterest;
        FollowsNewlineInterest |= that.FollowsNewlineInterest;
        FollowsStartInterest |= that.FollowsStartInterest;
    }

    public void ResetCompilationState()
    {
        BeingAnalyzed = false;
        BeenAnalyzed = false;
    }
}

public struct EatsAtLeastInfo
{
    // Any successful match starting from the current node will consume at least
    // this many characters. This does not necessarily mean that there is a
    // possible match with exactly this many characters, but we generally try to
    // get this number as high as possible to allow for early exit on failure.
    public byte FromPossiblyStart;

    // Like from_possibly_start, but with the additional assumption
    // that start-of-string assertions (^) can't match. This value is greater than
    // or equal to from_possibly_start.
    public byte FromNotStart;

    public EatsAtLeastInfo(byte eats)
    {
        FromPossiblyStart = eats;
        FromNotStart = eats;
    }

    public void SetMin(in EatsAtLeastInfo other)
    {
        FromPossiblyStart = Math.Min(FromPossiblyStart, other.FromPossiblyStart);
        FromNotStart = Math.Min(FromNotStart, other.FromNotStart);
    }

    public void SetMax(int other)
    {
        byte max = (byte)Math.Clamp(other, 0, 255);
        FromPossiblyStart = Math.Max(FromPossiblyStart, max);
        FromNotStart = Math.Max(FromNotStart, max);
    }

    public readonly bool IsZero => FromPossiblyStart == 0 && FromNotStart == 0;
}

public readonly struct EmitResult
{
    readonly bool _error;
    EmitResult(bool error) => _error = error;
    public static EmitResult Success() => new(false);
    public static EmitResult Error() => new(true);
    public bool IsSuccess => !_error;
    public bool IsError => _error;
}

// Terminology shared by the three drain-optimization enums below (DrainMode,
// AtomicLoopKind, ParkedGrant): see src/regexp/regexp-nodes.h. In short: a
// greedy loop grabs a maximal run (its "greedy extent"); the "drain" hands back
// one iteration at a time retrying the continuation; the "loop-exit backtrack"
// is taken once the drain is exhausted; "parking" leaves the position at the
// greedy extent when the loop-exit target does not care; the "implicit search
// loop" is the prepended `.*?` of unanchored regexps; a "uniform prefix" draws
// only from the trailing loop's own character source.

/// <summary>
/// How much of the drain epilogue ChoiceNode::EmitFixedLengthLoop emits for a
/// fixed-length greedy loop.
/// </summary>
public enum DrainMode : byte
{
    // Emit the standard char-by-char drain (retries and restore).
    kFull,
    // Interior retries are provably futile but the entry retry is not
    // (AtomicLoopKind::kBoundary): on continuation failure, restore straight
    // to the entry marker for a single retry there.
    kRetryAtEntry,
    // Every retry is futile, entry included (AtomicLoopKind::kDisjoint); only
    // the drain's restore job remains.
    kRestoreOnly,
    // Every retry is futile and the loop-exit backtrack target tolerates the
    // parked position (see Trace::parked_grant): skip the marker and the entire
    // epilogue.
    kOmit,
}

/// <summary>
/// Classification of `&lt;fixed-length-loop&gt;&lt;retreat-insensitive-continuation&gt;`
/// shapes. Computed and cached by LoopChoiceNode::atomic_loop_kind.
/// </summary>
public enum AtomicLoopKind : byte
{
    kNone,
    kAtEnd,     // Continuation is AT_END + ACCEPT; every retry futile.
    kTotal,     // Continuation always succeeds at the greedy extent (a
                // nullable-to-ACCEPT chain), so the continuation never fails and
                // the drain is 100% dead.
    kBoundary,  // Continuation starts with \b over a word-character body;
                // interior retries futile, the entry retry only with a word
                // character proven immediately before the entry.
    kDisjoint,  // Continuation's first character set is disjoint from the
                // body's; every retry futile.
}

/// <summary>
/// What a drain-omitted atomic loop's loop-exit backtrack target is known to
/// tolerate. Levels are ordered from weakest to strongest permission.
/// </summary>
public enum ParkedGrant : byte
{
    // No permission: the exit target treats the current position as the failed
    // attempt's start, so the loop-exit backtrack must restore it.
    kNone,
    // The exit target tolerates any position (the implicit search loop's
    // advance-and-retry alternative). Valid only with no pending prefix advance
    // (cp_offset == 0).
    kParked,
    // As kParked, but also valid with a uniform mandatory prefix before the
    // trailing loop (e.g. /\s+$/).
    kParkedUniformPrefix,
    // As kParkedUniformPrefix, and the prefix's last character has actually been
    // consumed just before the loop entry.
    kParkedNonEmptyUniformPrefix,
}

public abstract partial class RegExpNode
{
    protected RegExpNode? _replacement;
    readonly Label _label = new();
    readonly RegExpFlags _flags;
    bool _onWorkList;
    NodeInfo _info;

    // Saved values for EatsAtLeast results, to avoid recomputation. Filled in
    // during analysis (valid if info_.been_analyzed is true).
    EatsAtLeastInfo _eatsAtLeast;

    // This variable keeps track of how many times code has been generated for
    // this node (in different traces).  We don't keep track of where the
    // generated code is located unless the code is generated at the start of
    // a trace, in which case it is generic and can be reused by flushing the
    // deferred operations in the current trace and generating a goto.
    int _traceCount;
    readonly BoyerMooreLookahead?[] _bmInfo = new BoyerMooreLookahead?[2];

    protected RegExpNode(RegExpFlags flags) => _flags = flags;

    public abstract void Accept(INodeVisitor visitor);
    // Generates a goto to this node or actually generates the code at this point.
    public abstract EmitResult Emit(RegExpCompiler compiler, Trace trace);

    public const uint kLargeEatsAtLeastValue = 255;

    // For a given number of characters this returns a mask and a value.  The
    // next n characters are anded with the mask and compared with the value.
    // A comparison failure indicates the node cannot match the next n characters.
    // A comparison success indicates the node may match.
    public abstract void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget);

    public const int kNodeIsTooComplexForFixedLengthLoops = int.MinValue;
    public virtual int FixedLengthLoopLength() => kNodeIsTooComplexForFixedLengthLoops;

    // Only returns the successor for a text node of length 1 that matches any
    // character and that has no guards on it.
    public virtual RegExpNode? GetSuccessorOfOmnivorousTextNode(RegExpCompiler compiler) => null;

    // Collects information on the possible code units (mod 128) that can match if
    // we look forward.  This is used for a Boyer-Moore-like string searching
    // implementation.  The budget argument is used to limit the number of nodes
    // we are willing to look at in order to create this data.
    public const int kRecursionBudget = 200;
    public virtual void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart) { }

    // We want to avoid recalculating the lookahead info, so we store it on the
    // node.  Only info that is for this node is stored.  We can tell that the
    // info is for this node when offset == 0, so the information is calculated
    // relative to this node.
    public void SaveBMInfo(BoyerMooreLookahead bm, bool notAtStart, int offset)
    {
        if (offset == 0) SetBmInfo(notAtStart, bm);
    }

    public Label Label => _label;
    // If non-generic code is generated for a node (i.e. the node is not at the
    // start of the trace) then it cannot be reused.  This variable sets a limit
    // on how often we allow that to happen before we insist on starting a new
    // trace and generating generic code for a node that can be reused by flushing
    // the deferred actions in the current trace and generating a goto.
    public const int kMaxCopiesCodeGenerated = 10;

    public bool OnWorkList { get => _onWorkList; set => _onWorkList = value; }

    public ref NodeInfo Info => ref _info;
    public EatsAtLeastInfo EatsAtLeastInfo { get => _eatsAtLeast; set => _eatsAtLeast = value; }

    // TODO(v8:10441): This is a hacky way to avoid exponential code size growth
    // for very large choice nodes that can be generated by unicode property
    // escapes. In order to avoid inlining (i.e. trace recursion), we pretend to
    // have generated the maximum count of code copies already.
    public void SetDoNotInline() => _traceCount = kMaxCopiesCodeGenerated;

    public BoyerMooreLookahead? BmInfo(bool notAtStart) => _bmInfo[notAtStart ? 1 : 0];

    public virtual EndNode? AsEndNode() => null;
    public virtual ActionNode? AsActionNode() => null;
    public virtual ChoiceNode? AsChoiceNode() => null;
    public virtual LoopChoiceNode? AsLoopChoiceNode() => null;
    public virtual NegativeLookaroundChoiceNode? AsNegativeLookaroundChoiceNode() => null;
    public virtual BackReferenceNode? AsBackReferenceNode() => null;
    public virtual AssertionNode? AsAssertionNode() => null;
    public virtual TextNode? AsTextNode() => null;
    public virtual UnanchoredAdvanceNode? AsUnanchoredAdvanceNode() => null;
    public virtual NegativeSubmatchSuccess? AsNegativeSubmatchSuccess() => null;
    public virtual SeqNode? AsSeqNode() => null;

    public RegExpFlags Flags => _flags;

    public virtual bool IsBacktrack() => false;

    protected enum LimitResult { DONE, CONTINUE }

    // Caches |bm| as this node's own lookahead, unless |bm| is a transient probe
    // that opted out of caching (see BoyerMooreLookahead::caches_node_info).
    protected void SetBmInfo(bool notAtStart, BoyerMooreLookahead bm)
    {
        if (!bm.CachesNodeInfo) return;
        _bmInfo[notAtStart ? 1 : 0] = bm;
    }

    // How many characters must this node consume at a minimum in order to
    // succeed.  The not_at_start argument is used to indicate that we know we are
    // not at the start of the input.  In this case anchored branches will always
    // fail and can be ignored when determining how many characters are consumed
    // on success.  If this node has not been analyzed yet, EatsAtLeast returns 0.
    public uint EatsAtLeast(bool notAtStart) =>
        notAtStart ? _eatsAtLeast.FromNotStart : _eatsAtLeast.FromPossiblyStart;

    public bool KeepRecursing(RegExpCompiler compiler) =>
        !compiler.LimitingRecursion && compiler.RecursionDepth <= RegExpCompiler.kMaxRecursion;

    protected ref int TraceCount => ref _traceCount;
}

public abstract partial class SeqNode(RegExpNode onSuccess, RegExpFlags flags) : RegExpNode(flags)
{
    RegExpNode _onSuccess = onSuccess;

    public RegExpNode OnSuccess { get => _onSuccess; set => _onSuccess = value; }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        _onSuccess.FillInBMInfo(offset, budget - 1, bm, notAtStart);
        if (offset == 0) SetBmInfo(notAtStart, bm);
    }

    public override SeqNode AsSeqNode() => this;
}

public sealed partial class ActionNode : SeqNode
{
    public enum ActionType
    {
        SET_REGISTER_FOR_LOOP,
        INCREMENT_REGISTER,
        STORE_POSITION,
        RESTORE_POSITION,
        BEGIN_POSITIVE_SUBMATCH,
        BEGIN_NEGATIVE_SUBMATCH,
        POSITIVE_SUBMATCH_SUCCESS,
        EMPTY_MATCH_CHECK,
        CLEAR_CAPTURES,
        EATS_AT_LEAST,
    }

    // data_ (a union in V8).
    int _registerFrom;
    int _registerTo;
    int _value;
    int _stackPointerRegister;
    int _currentPositionRegister;
    int _clearRegisterCount;
    int _clearRegisterFrom;
    ActionNode? _successNode;  // Only used for positive submatch.
    int _startRegister;
    int _repetitionRegister;
    int _repetitionLimit;
    int _characters;

    readonly ActionType _actionType;

    ActionNode(ActionType actionType, RegExpNode onSuccess, RegExpFlags flags) : base(onSuccess, flags)
    {
        _actionType = actionType;
    }

    ActionNode(ActionType actionType, RegExpNode onSuccess, RegExpFlags flags, int from, int to = -1, int value = 0)
        : base(onSuccess, flags)
    {
        _actionType = actionType;
        _registerFrom = from;
        _registerTo = to == -1 ? from : to;
        _value = value;
        Debug.Assert(IsSimpleAction());
    }

    public static ActionNode SetRegisterForLoop(int reg, int val, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.SET_REGISTER_FOR_LOOP, onSuccess, flags, reg, reg, val);

    public static ActionNode IncrementRegister(int reg, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.INCREMENT_REGISTER, onSuccess, flags, reg);

    public static ActionNode StorePosition(int reg, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.STORE_POSITION, onSuccess, flags, reg);

    public static ActionNode RestorePosition(int reg, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.RESTORE_POSITION, onSuccess, flags, reg);

    public static ActionNode ClearCaptures(Interval range, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.CLEAR_CAPTURES, onSuccess, flags, range.From, range.To);

    public static ActionNode BeginPositiveSubmatch(int stackReg, int positionReg, RegExpNode body,
        ActionNode successNode, RegExpFlags flags) =>
        new(ActionType.BEGIN_POSITIVE_SUBMATCH, body, flags)
        {
            _stackPointerRegister = stackReg,
            _currentPositionRegister = positionReg,
            _successNode = successNode,
        };

    public static ActionNode BeginNegativeSubmatch(int stackReg, int positionReg, RegExpNode onSuccess,
        RegExpFlags flags) =>
        new(ActionType.BEGIN_NEGATIVE_SUBMATCH, onSuccess, flags)
        {
            _stackPointerRegister = stackReg,
            _currentPositionRegister = positionReg,
        };

    public static ActionNode PositiveSubmatchSuccess(int stackReg, int positionReg, int clearRegisterCount,
        int clearRegisterFrom, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.POSITIVE_SUBMATCH_SUCCESS, onSuccess, flags)
        {
            _stackPointerRegister = stackReg,
            _currentPositionRegister = positionReg,
            _clearRegisterCount = clearRegisterCount,
            _clearRegisterFrom = clearRegisterFrom,
        };

    public static ActionNode EmptyMatchCheck(int startRegister, int repetitionRegister, int repetitionLimit,
        RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.EMPTY_MATCH_CHECK, onSuccess, flags)
        {
            _startRegister = startRegister,
            _repetitionRegister = repetitionRegister,
            _repetitionLimit = repetitionLimit,
        };

    public static ActionNode EatsAtLeastAction(int characters, RegExpNode onSuccess, RegExpFlags flags) =>
        new(ActionType.EATS_AT_LEAST, onSuccess, flags) { _characters = characters };

    public override ActionNode AsActionNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitAction(this);
    public ActionType Type => _actionType;
    // TODO(erikcorry): We should allow some action nodes in fixed length loops.
    public override int FixedLengthLoopLength() => kNodeIsTooComplexForFixedLengthLoops;
    public ActionNode SuccessNode
    {
        get
        {
            Debug.Assert(_actionType == ActionType.BEGIN_POSITIVE_SUBMATCH);
            return _successNode!;
        }
    }
    public int StoredEatsAtLeast
    {
        get
        {
            Debug.Assert(_actionType == ActionType.EATS_AT_LEAST);
            return _characters;
        }
    }

    public bool Mentions(int reg) => reg >= RegisterFrom && reg <= RegisterTo;

    public int Value
    {
        get
        {
            Debug.Assert(_actionType == ActionType.SET_REGISTER_FOR_LOOP);
            return _value;
        }
    }

    public bool IsSimpleAction() =>
        _actionType is ActionType.STORE_POSITION or ActionType.RESTORE_POSITION or ActionType.INCREMENT_REGISTER
            or ActionType.SET_REGISTER_FOR_LOOP or ActionType.CLEAR_CAPTURES;

    // Register/capture updates only: no input consumed, no repositioning, no
    // flag changes.
    public bool IsRegisterOnlyAction() =>
        _actionType is ActionType.EATS_AT_LEAST or ActionType.STORE_POSITION or ActionType.INCREMENT_REGISTER
            or ActionType.SET_REGISTER_FOR_LOOP or ActionType.CLEAR_CAPTURES;

    // V8 reads the union member register_from for any action type (Mentions
    // is only reached for simple actions); non-simple actions have from = to = 0
    // there only by accident of the union layout, so mirror that: the fields
    // share storage with the submatch data in V8.
    public int RegisterFrom => IsSimpleAction() ? _registerFrom : UnionRegisterFrom;
    public int RegisterTo => IsSimpleAction() ? _registerTo : UnionRegisterTo;

    // In V8's union, u_simple.register_from/register_to alias the first two ints
    // of the other members.
    int UnionRegisterFrom => _actionType switch
    {
        ActionType.BEGIN_POSITIVE_SUBMATCH or ActionType.BEGIN_NEGATIVE_SUBMATCH
            or ActionType.POSITIVE_SUBMATCH_SUCCESS => _stackPointerRegister,
        ActionType.EMPTY_MATCH_CHECK => _startRegister,
        ActionType.EATS_AT_LEAST => _characters,
        _ => _registerFrom,
    };

    int UnionRegisterTo => _actionType switch
    {
        ActionType.BEGIN_POSITIVE_SUBMATCH or ActionType.BEGIN_NEGATIVE_SUBMATCH
            or ActionType.POSITIVE_SUBMATCH_SUCCESS => _currentPositionRegister,
        ActionType.EMPTY_MATCH_CHECK => _repetitionRegister,
        ActionType.EATS_AT_LEAST => 0,
        _ => _registerTo,
    };

    internal int StackPointerRegister => _stackPointerRegister;
    internal int CurrentPositionRegister => _currentPositionRegister;
    internal int ClearRegisterCount => _clearRegisterCount;
    internal int ClearRegisterFrom => _clearRegisterFrom;
    internal int StartRegister => _startRegister;
    internal int RepetitionRegister => _repetitionRegister;
    internal int RepetitionLimit => _repetitionLimit;
}

public sealed partial class TextNode : SeqNode
{
    readonly List<TextElement> _elms;
    readonly bool _readBackward;

    public TextNode(List<TextElement> elms, bool readBackward, RegExpNode onSuccess, RegExpFlags flags)
        : base(onSuccess, flags)
    {
        _elms = elms;
        _readBackward = readBackward;
    }

    public TextNode(RegExpClassRanges that, bool readBackward, RegExpNode onSuccess, RegExpFlags flags)
        : base(onSuccess, flags)
    {
        _elms = new List<TextElement>(1) { TextElement.FromClassRanges(that) };
        _readBackward = readBackward;
    }

    public override TextNode AsTextNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitText(this);
    public List<TextElement> Elements => _elms;
    public bool ReadBackward => _readBackward;

    enum TextEmitPassType
    {
        NON_LATIN1_MATCH,            // Check for characters that can never match.
        SIMPLE_CHARACTER_MATCH,      // Case-dependent single character check.
        NON_LETTER_CHARACTER_MATCH,  // Check characters that have no case equivs.
        CASE_CHARACTER_MATCH,        // Case-independent single character check.
        CHARACTER_CLASS_MATCH,       // Character class.
    }
}

public sealed partial class AssertionNode : SeqNode
{
    public enum AssertionType
    {
        AT_END,
        AT_START,
        AT_BOUNDARY,
        AT_NON_BOUNDARY,
        AFTER_NEWLINE,
    }

    readonly AssertionType _assertionType;

    AssertionNode(AssertionType t, RegExpNode onSuccess, RegExpFlags flags) : base(onSuccess, flags) =>
        _assertionType = t;

    public static AssertionNode AtEnd(RegExpNode onSuccess, RegExpFlags flags) => new(AssertionType.AT_END, onSuccess, flags);
    public static AssertionNode AtStart(RegExpNode onSuccess, RegExpFlags flags) => new(AssertionType.AT_START, onSuccess, flags);
    public static AssertionNode AtBoundary(RegExpNode onSuccess, RegExpFlags flags) => new(AssertionType.AT_BOUNDARY, onSuccess, flags);
    public static AssertionNode AtNonBoundary(RegExpNode onSuccess, RegExpFlags flags) => new(AssertionType.AT_NON_BOUNDARY, onSuccess, flags);
    public static AssertionNode AfterNewline(RegExpNode onSuccess, RegExpFlags flags) => new(AssertionType.AFTER_NEWLINE, onSuccess, flags);

    public override AssertionNode AsAssertionNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitAssertion(this);
    public AssertionType Type => _assertionType;

    enum IfPrevious { kIsNonWord, kIsWord }
}

public sealed partial class BackReferenceNode(int startReg, int endReg, bool readBackward, RegExpNode onSuccess,
    RegExpFlags flags) : SeqNode(onSuccess, flags)
{
    public override BackReferenceNode AsBackReferenceNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitBackReference(this);
    public int StartRegister { get; } = startReg;
    public int EndRegister { get; } = endReg;
    public bool ReadBackward { get; } = readBackward;
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
    }
}

public sealed partial class UnanchoredAdvanceNode(RegExpNode onSuccess, RegExpFlags flags) : SeqNode(onSuccess, flags)
{
    public override UnanchoredAdvanceNode AsUnanchoredAdvanceNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitUnanchoredAdvance(this);
}

public partial class EndNode : RegExpNode
{
    public enum Action { ACCEPT, BACKTRACK, NEGATIVE_SUBMATCH_SUCCESS }

    readonly Action _action;

    public EndNode(Action action, RegExpFlags flags) : base(flags)
    {
        _action = action;
        var large = new EatsAtLeastInfo((byte)kLargeEatsAtLeastValue);
        if (action == Action.BACKTRACK) EatsAtLeastInfo = large;
    }

    public override EndNode AsEndNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitEnd(this);
    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart) { }
    public Action EndAction => _action;
    public override bool IsBacktrack() => _action == Action.BACKTRACK;
}

public sealed partial class NegativeSubmatchSuccess(int stackPointerReg, int positionReg, int clearCaptureCount,
    int clearCaptureStart, RegExpFlags flags) : EndNode(Action.NEGATIVE_SUBMATCH_SUCCESS, flags)
{
    readonly int _stackPointerRegister = stackPointerReg;
    readonly int _currentPositionRegister = positionReg;
    readonly int _clearCaptureCount = clearCaptureCount;
    readonly int _clearCaptureStart = clearCaptureStart;

    public override NegativeSubmatchSuccess AsNegativeSubmatchSuccess() => this;

    internal int StackPointerRegister => _stackPointerRegister;
    internal int CurrentPositionRegister => _currentPositionRegister;
    internal int ClearCaptureCount => _clearCaptureCount;
    internal int ClearCaptureStart => _clearCaptureStart;
}

public sealed class Guard(int reg, Guard.Relation op, int value)
{
    public enum Relation { LT, GEQ }
    public int Reg { get; } = reg;
    public Relation Op { get; } = op;
    public int Value { get; } = value;
}

public struct GuardedAlternative(RegExpNode node)
{
    RegExpNode _node = node;
    // TODO(pthier): There are currently no uses of multiple guards. Consider
    // removing the ZoneList.
    List<Guard>? _guards;

    public void AddGuard(Guard guard)
    {
        _guards ??= new List<Guard>(1);
        _guards.Add(guard);
    }

    public RegExpNode Node { readonly get => _node; set => _node = value; }
    public readonly List<Guard>? Guards => _guards;
}

public partial class ChoiceNode : RegExpNode
{
    protected readonly List<GuardedAlternative> _alternatives;
    // If true, this node is never checked at the start of the input.
    // Allows a new trace to start with at_start() set to false.
    bool _notAtStart;
    bool _beingCalculated;

    public ChoiceNode(int expectedSize, RegExpFlags flags) : base(flags)
    {
        _alternatives = new List<GuardedAlternative>(expectedSize);
    }

    public override ChoiceNode AsChoiceNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitChoice(this);
    public virtual void AddAlternative(GuardedAlternative node) => _alternatives.Add(node);
    public List<GuardedAlternative> Alternatives => _alternatives;

    public bool BeingCalculated { get => _beingCalculated; set => _beingCalculated = value; }
    public bool NotAtStart => _notAtStart;
    public void SetNotAtStart() => _notAtStart = true;
    public virtual bool TryToEmitQuickCheckForAlternative(bool isFirst) => true;
    public virtual bool ReadBackward => false;
}

public sealed partial class NegativeLookaroundChoiceNode : ChoiceNode
{
    public const int kLookaroundIndex = 0;
    public const int kContinueIndex = 1;

    public NegativeLookaroundChoiceNode(GuardedAlternative thisMustFail, GuardedAlternative thenDoThis,
        RegExpFlags flags) : base(2, flags)
    {
        AddAlternative(thisMustFail);
        AddAlternative(thenDoThis);
    }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        ContinueNode.FillInBMInfo(offset, budget - 1, bm, notAtStart);
        if (offset == 0) SetBmInfo(notAtStart, bm);
    }

    public RegExpNode LookaroundNode => _alternatives[kLookaroundIndex].Node;
    public RegExpNode ContinueNode => _alternatives[kContinueIndex].Node;

    // For a negative lookahead we don't emit the quick check for the
    // alternative that is expected to fail.  This is because quick check code
    // starts by loading enough characters for the alternative that takes fewest
    // characters, but on a negative lookahead the negative branch did not take
    // part in that calculation (EatsAtLeast) so the assumptions don't hold.
    public override bool TryToEmitQuickCheckForAlternative(bool isFirst) => !isFirst;
    public override NegativeLookaroundChoiceNode AsNegativeLookaroundChoiceNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitNegativeLookaroundChoice(this);
}

public sealed partial class LoopChoiceNode(bool bodyCanBeZeroLength, bool readBackward, RegExpFlags flags)
    : ChoiceNode(2, flags)
{
    RegExpNode? _loopNode;
    RegExpNode? _continueNode;
    readonly bool _bodyCanBeZeroLength = bodyCanBeZeroLength;
    readonly bool _readBackward = readBackward;
    // Memo for atomic_loop_kind.
    bool _atomicLoopKindValid;
    AtomicLoopKind _atomicLoopKind = AtomicLoopKind.kNone;

    public RegExpNode? LoopNode => _loopNode;
    public RegExpNode? ContinueNode => _continueNode;
    public bool BodyCanBeZeroLength => _bodyCanBeZeroLength;
    public override bool ReadBackward => _readBackward;
    public override LoopChoiceNode AsLoopChoiceNode() => this;
    public override void Accept(INodeVisitor visitor) => visitor.VisitLoopChoice(this);

    // The fixed match length of one body iteration, or
    // kNodeIsTooComplexForFixedLengthLoops.  Meaningful when atomic_loop_kind()
    // is not kNone (the body is then a fixed-length chain).
    public int FixedLengthBodyIterationLength()
    {
        GuardedAlternative alt = _alternatives[0];
        return FixedLengthLoopLengthForAlternative(ref alt);
    }

    // AddAlternative is made private for loop nodes because alternatives
    // should not be added freely, we need to keep track of which node
    // goes back to the node itself.
    public override void AddAlternative(GuardedAlternative node) => base.AddAlternative(node);
}
