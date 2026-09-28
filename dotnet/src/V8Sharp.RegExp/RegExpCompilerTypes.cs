// Port of src/regexp/regexp-compiler.h: QuickCheckDetails, Boyer-Moore
// lookahead, Trace, SpecialLoopState, PreloadState, FrequencyCollator,
// RegExpCompiler (regexp::Compiler) and UnicodeRangeSplitter.

namespace V8Sharp.RegExp;

internal static class CompilerConstants
{
    // The '2' variant is has inclusive from and exclusive to.
    // This covers \s as defined in ECMA-262 5.1, 15.10.2.12,
    // which include WhiteSpace (7.2) or LineTerminator (7.3) values.
    public const int kRangeEndMarker = 0x110000;
    public static readonly int[] kSpaceRanges =
    [
        '\t', '\r' + 1, ' ', ' ' + 1, 0x00A0, 0x00A1, 0x1680,
        0x1681, 0x2000, 0x200B, 0x2028, 0x202A, 0x202F, 0x2030,
        0x205F, 0x2060, 0x3000, 0x3001, 0xFEFF, 0xFF00, kRangeEndMarker,
    ];
    public static readonly int[] kWordRanges = ['0', '9' + 1, 'A', 'Z' + 1, '_', '_' + 1, 'a', 'z' + 1, kRangeEndMarker];
    public static readonly int[] kDigitRanges = ['0', '9' + 1, kRangeEndMarker];
    public static readonly int[] kSurrogateRanges =
        [RegExpMacroAssembler.kLeadSurrogateStart, RegExpMacroAssembler.kLeadSurrogateStart + 1, kRangeEndMarker];
    public static readonly int[] kLineTerminatorRanges = [0x000A, 0x000B, 0x000D, 0x000E, 0x2028, 0x202A, kRangeEndMarker];

    // More makes code generation slower, less makes V8 benchmark score lower.
    public const uint kMaxLookaheadForBoyerMoore = 8;
    // In a 3-character pattern you can maximally step forwards 3 characters
    // at a time, which is not always enough to pay for the extra logic.
    public const uint kPatternTooShortForBoyerMoore = 2;

    public static bool NeedsUnicodeCaseEquivalents(RegExpFlags flags) =>
        // Both unicode (or unicode sets) and ignore_case flags are set. We need to
        // use ICU to find the closure over case equivalents.
        flags.IsEitherUnicode() && flags.IsIgnoreCase();

    // String::kMaxOneByteCharCode etc.
    public const int kMaxOneByteCharCode = 0xff;
    public const int kMaxUtf16CodeUnit = 0xffff;

    public static uint MaxCodeUnit(bool oneByte) => oneByte ? 0xffu : 0xffffu;
    public static uint CharMask(bool oneByte) => MaxCodeUnit(oneByte);
}

/// <summary>Details of a quick mask-compare check that can look ahead in the input stream.</summary>
public sealed partial class QuickCheckDetails
{
    const int kMaxPositions = 4;

    public struct Position
    {
        public uint Mask;
        public uint Value;
        public bool DeterminesPerfectly;
        public bool CannotMatch;

        public void Clear()
        {
            Mask = 0;
            Value = 0;
            DeterminesPerfectly = false;
            CannotMatch = false;
        }
    }

    // How many characters do we have quick check information from.  This is
    // the same for all branches of a choice node.
    int _characters;
    readonly Position[] _positions = new Position[kMaxPositions];
    // These values are the condensate of the above array after Rationalize().
    uint _mask;
    uint _value;

    public QuickCheckDetails() { }
    public QuickCheckDetails(int characters)
    {
        Debug.Assert(characters <= kMaxPositions);
        _characters = characters;
    }

    /// <summary>Value copy (the C++ class is copied by value).</summary>
    public void CopyFrom(QuickCheckDetails other)
    {
        _characters = other._characters;
        Array.Copy(other._positions, _positions, kMaxPositions);
        _mask = other._mask;
        _value = other._value;
    }

    public bool CannotMatch()
    {
        for (int i = 0; i < _characters; i++)
        {
            if (_positions[i].CannotMatch) return true;
        }
        return false;
    }

    public void SetCannotMatchFrom(int index)
    {
        Debug.Assert(index >= 0);
        for (int i = index; i < _characters; i++) _positions[i].CannotMatch = true;
    }

    public int Characters
    {
        get => _characters;
        set
        {
            Debug.Assert(0 <= value && value <= kMaxPositions);
            _characters = value;
        }
    }

    public ref Position Positions(int index)
    {
        Debug.Assert(0 <= index && index < _characters);
        return ref _positions[index];
    }

    public uint Mask { get => _mask; set => _mask = value; }
    public uint Value { get => _value; set => _value = value; }
}

// Improve the speed that we scan for an initial point where a non-anchored
// regexp can match by using a Boyer-Moore-like table. This is done by
// identifying non-greedy non-capturing loops in the nodes that eat any
// character one at a time.  For example in the middle of the regexp
// /foo[\s\S]*?bar/ we find such a loop.  There is also such a loop implicitly
// inserted at the start of any non-anchored regexp.
//
// When we have found such a loop we look ahead in the nodes to find the set of
// characters that can come at given distances. For example for the regexp
// /.?foo/ we know that there are at least 3 characters ahead of us, and the
// sets of characters that can occur are [any, [f, o], [o]]. We find a range in
// the lookahead info where the set of characters is reasonably constrained. In
// our example this is from index 1 to 2 (0 is not constrained). We can now
// look 3 characters ahead and if we don't find one of [f, o] (the union of
// [f, o] and [o]) then we can skip forwards by the range size (in this case 2).
//
// For Unicode input strings we do the same, but modulo 128.
//
// We also look at the first string fed to the regexp and use that to get a hint
// of the character frequencies in the inputs. This affects the assessment of
// whether the set of characters is 'reasonably constrained'.
//
// We also have another lookahead mechanism (called quick check in the code),
// which uses a wide load of multiple characters followed by a mask and compare
// to determine whether a match is possible at this point.
public enum ContainedInLattice
{
    kNotYet = 0,
    kLatticeIn = 1,
    kLatticeOut = 2,
    kLatticeUnknown = 3,  // Can also mean both in and out.
}

public sealed partial class BoyerMoorePositionInfo
{
    public const int kMapSize = 128;
    public const int kMask = kMapSize - 1;

    readonly bool[] _map = new bool[kMapSize];
    int _mapCount;  // Number of set bits in the map.
    ContainedInLattice _w = ContainedInLattice.kNotYet;  // The \w character class.

    public bool At(int i) => _map[i];
    public int MapCount => _mapCount;
    public bool IsNonWord => _w == ContainedInLattice.kLatticeOut;
    public bool IsWord => _w == ContainedInLattice.kLatticeIn;
    public bool[] RawBitset => _map;

    internal static ContainedInLattice Combine(ContainedInLattice a, ContainedInLattice b) =>
        (ContainedInLattice)((int)a | (int)b);
}

public sealed partial class BoyerMooreLookahead
{
    // This is the value obtained by EatsAtLeast.  If we do not have at least this
    // many characters left in the sample string then the match is bound to fail.
    // Therefore it is OK to read a character this far ahead of the current match
    // point.
    readonly int _length;
    readonly RegExpCompiler _compiler;
    // 0xff for Latin1, 0xffff for UTF-16.
    readonly int _maxChar;
    readonly List<BoyerMoorePositionInfo> _bitmaps;
    bool _cachesNodeInfo = true;

    public int Length => _length;
    public int MaxChar => _maxChar;
    public RegExpCompiler Compiler => _compiler;

    public int Count(int mapNumber) => _bitmaps[mapNumber].MapCount;

    public BoyerMoorePositionInfo At(int i) => _bitmaps[i];

    public void Set(int mapNumber, int character)
    {
        if (character > _maxChar) return;
        BoyerMoorePositionInfo info = _bitmaps[mapNumber];
        info.Set(character);
    }

    public void SetInterval(int mapNumber, Interval interval)
    {
        if (interval.From > _maxChar) return;
        BoyerMoorePositionInfo info = _bitmaps[mapNumber];
        if (interval.To > _maxChar) info.SetInterval(new Interval(interval.From, _maxChar));
        else info.SetInterval(interval);
    }

    public void SetAll(int mapNumber) => _bitmaps[mapNumber].SetAll();

    public void SetRest(int fromMap)
    {
        for (int i = fromMap; i < _length; i++) SetAll(i);
    }

    // Transient probes opt out so they don't clobber the shared bm_info_ (see
    // Node::set_bm_info).
    public bool CachesNodeInfo { get => _cachesNodeInfo; set => _cachesNodeInfo = value; }
}

/// <summary>
/// There are many ways to generate code for a node.  This class encapsulates
/// the current way we should be generating.  In other words it encapsulates
/// the current state of the code generator.  The effect of this is that we
/// generate code for paths that the matcher can take through the regular
/// expression.  A given node in the regexp can be code-generated several times
/// as it can be part of several traces.  For example for the regexp:
/// /foo(bar|ip)baz/ the code to match baz will be generated twice, once as part
/// of the foo-bar-baz trace and once as part of the foo-ip-baz trace.  The code
/// to match foo is generated only once (the traces have a common prefix).  The
/// code to store the capture is deferred and generated (twice) after the places
/// where baz has been matched.
/// </summary>
public sealed partial class Trace
{
    // A value for a property that is either known to be true, known to be false,
    // or not known.
    public enum TriBool { FALSE_VALUE = 0, TRUE_VALUE = 1, UNKNOWN = 2 }

    // End the trace.  This involves flushing the deferred actions in the trace
    // and pushing a backtrack location onto the backtrack stack.  Once this is
    // done we can start a new trace or go to one that has already been
    // generated.
    public enum FlushMode
    {
        // Normal flush of the deferred actions, generates code for backtracking.
        kFlushFull,
        // Matching has succeeded, so current position and backtrack stack will be
        // ignored and need not be written.
        kFlushSuccess,
    }

    // Some callers add/subtract 1 from cp_offset, assuming that the result is
    // still valid. That's obviously not the case when our `cp_offset` is only
    // checked against kMinCPOffset/kMaxCPOffset, so we need to apply the some
    // slack.
    public const int kCPOffsetSlack = 1;

    int _cpOffset;
    int _flushBudget = 100;  // Note: this is a 16 bit field.
    TriBool _atStart = TriBool.UNKNOWN;
    bool _hasAnyActions;
    ParkedGrant _parkedGrant = ParkedGrant.kNone;
    ActionNode? _action;
    Label? _backtrack;
    SpecialLoopState? _specialLoopState;
    int _charactersPreloaded;
    int _boundCheckedUpTo;
    readonly QuickCheckDetails _quickCheckPerformed = new();
    readonly Trace? _next;

    public Trace() { }

    public Trace(Trace other)
    {
        _cpOffset = other._cpOffset;
        _flushBudget = other._flushBudget;
        _atStart = other._atStart;
        _hasAnyActions = other._hasAnyActions;
        _parkedGrant = other._parkedGrant;
        _action = null;
        _backtrack = other._backtrack;
        _specialLoopState = other._specialLoopState;
        _charactersPreloaded = other._charactersPreloaded;
        _boundCheckedUpTo = other._boundCheckedUpTo;
        _quickCheckPerformed.CopyFrom(other._quickCheckPerformed);
        _next = other;
    }

    public int CpOffset => _cpOffset;

    // Does any trace in the chain have an action?
    public bool HasAnyActions => _hasAnyActions;
    // Does this particular trace object have an action?
    public bool HasAction => _action is not null;
    public ActionNode? Action => _action;

    // A trivial trace is one that has no deferred actions or other state that
    // affects the assumptions used when generating code.  There is no recorded
    // backtrack location in a trivial trace, so with a trivial trace we will
    // generate code that, on a failure to match, gets the backtrack location
    // from the backtrack stack rather than using a direct jump instruction.  We
    // always start code generation with a trivial trace and non-trivial traces
    // are created as we emit code for nodes or add to the list of deferred
    // actions in the trace.  The location of the code generated for a node using
    // a trivial trace is recorded in a label in the node so that gotos can be
    // generated to that code.
    public bool IsTrivial =>
        _backtrack is null && !HasAnyActions && _cpOffset == 0 && _charactersPreloaded == 0 &&
        _boundCheckedUpTo == 0 && _quickCheckPerformed.Characters == 0 && _atStart == TriBool.UNKNOWN;

    public TriBool AtStart
    {
        get => _atStart;
        set => _atStart = value;
    }

    public Label? Backtrack => _backtrack;

    // What the loop-exit backtrack target tolerates when a drain-omitted loop
    // unwinds to it with the input position parked at the loop's greedy extent
    // (see ParkedGrant). The grant is issued only at emission sites where the
    // target's behavior is known by construction, and is revoked automatically
    // whenever the backtrack target changes (see set_backtrack).
    public ParkedGrant ParkedGrant => _parkedGrant;
    public SpecialLoopState? SpecialLoopState { get => _specialLoopState; set => _specialLoopState = value; }
    public int CharactersPreloaded { get => _charactersPreloaded; set => _charactersPreloaded = value; }
    public int BoundCheckedUpTo { get => _boundCheckedUpTo; set => _boundCheckedUpTo = value; }
    public int FlushBudget
    {
        get => _flushBudget;
        set
        {
            Debug.Assert(value <= ushort.MaxValue);  // Flush-budget is 16 bit.
            _flushBudget = value;
        }
    }
    public QuickCheckDetails QuickCheckPerformed => _quickCheckPerformed;
    public Trace? Next => _next;

    // These set methods and AdvanceCurrentPositionInTrace should be used only on
    // new traces - the intention is that traces are immutable after creation.
    public void AddAction(ActionNode newAction)
    {
        Debug.Assert(_action is null);  // Otherwise we lose an action.
        _action = newAction;
        _hasAnyActions = true;
    }

    // Clears any inherited parked-position grant; see parked_grant() for when
    // an alternative must do this.
    public void ResetParkedGrant() => _parkedGrant = ParkedGrant.kNone;

    public void SetBacktrack(Label? backtrack)
    {
        _backtrack = backtrack;
        // A parked-position grant is tied to the specific target it was issued
        // for; a new target must obtain its own.
        ResetParkedGrant();
    }

    public void SetParkedGrant(ParkedGrant grant)
    {
        Debug.Assert(_backtrack is not null);
        Debug.Assert(grant != ParkedGrant.kNone);
        _parkedGrant = grant;
    }

    public void SetQuickCheckPerformed(QuickCheckDetails d) => _quickCheckPerformed.CopyFrom(d);

    enum DeferredActionUndoType { IGNORE, RESTORE, CLEAR }
    const int kNoStore = int.MinValue;

    // For a given register, records the actions recorded in the trace.
    // See ScanDeferredActions.
    struct RegisterFlushInfo
    {
        public DeferredActionUndoType UndoAction;
        public int Value;
        public bool Absolute;  // Set register to value.
        public bool Clear;     // Clear register (set to zero):
        public int StorePosition;  // Store current position plus value to register.

        public RegisterFlushInfo()
        {
            UndoAction = DeferredActionUndoType.IGNORE;
            StorePosition = kNoStore;
        }
    }
}

/// <summary>
/// Used for fixed length greedy loops (counted loops like .*) and for
/// omnivorous non-greedy loops (the initial loop ahead of a non-anchored
/// regexp).
/// </summary>
public sealed partial class SpecialLoopState
{
    // Step backwards (fixed length greed loop) or forwards (non-greedy
    // omnivourous loop.
    readonly Label _stepLabel = new();
    readonly Label _loopTopLabel = new();
    readonly ChoiceNode _loopChoiceNode;
    readonly Trace _backtrackTrace = new();

    public ChoiceNode LoopChoiceNode => _loopChoiceNode;
    public Trace BacktrackTrace => _backtrackTrace;
}

public struct PreloadState
{
    public const int kEatsAtLeastNotYetInitialized = -1;
    public bool PreloadIsCurrent;
    public bool PreloadHasCheckedBounds;
    public int PreloadCharacters;
    public int EatsAtLeast;
    public void Init() => EatsAtLeast = kEatsAtLeastNotYetInitialized;
}

public sealed class FrequencyCollator
{
    readonly int[] _counters = new int[RegExpMacroAssembler.kTableSize];
    int _totalSamples;

    public void CountCharacter(int character)
    {
        int index = character & RegExpMacroAssembler.kTableMask;
        _counters[index]++;
        _totalSamples++;
    }

    // Does not measure in percent, but rather per-128 (the table size from the
    // regexp macro assembler).
    public int Frequency(int inCharacter)
    {
        Debug.Assert((inCharacter & RegExpMacroAssembler.kTableMask) == inCharacter);
        if (_totalSamples < 1) return 1;  // Division by zero.
        return _counters[inCharacter] * 128 / _totalSamples;
    }
}

/// <summary>regexp::Compiler.</summary>
public sealed partial class RegExpCompiler
{
    public const int kNoRegister = -1;
    public const int kMaxRecursion = 100;

    readonly EndNode _accept;
    int _nextRegister;
    int _unicodeLookaroundStackRegister = kNoRegister;
    int _unicodeLookaroundPositionRegister = kNoRegister;
    List<RegExpNode>? _workList;
    int _recursionDepth;
    RegExpFlags _flags;
    RegExpMacroAssembler? _macroAssembler;
    readonly bool _oneByte;
    bool _regExpTooBig;
    bool _limitingRecursion;
    int _toNodeOverflowCheckTicks;
    bool _optimize;
    bool _readBackward;
    // Set by PreprocessRegExp when it prepends the `.*?` search loop.
    bool _hasSearchPrefix;
    int _currentExpansionFactor = 1;
    readonly FrequencyCollator _frequencyCollator = new();

    /// <summary>--regexp-optimization (default true).</summary>
    public static bool s_regexpOptimization = true;
    /// <summary>--regexp-unroll (default true).</summary>
    public static bool s_regexpUnroll = true;

    public RegExpCompiler(int captureCount, RegExpFlags flags, bool oneByte)
    {
        _nextRegister = RegistersForCaptureCount(captureCount);
        _flags = flags;
        _oneByte = oneByte;
        _optimize = s_regexpOptimization;
        _accept = new EndNode(EndNode.Action.ACCEPT, flags);
        Debug.Assert(RegExpMacroAssembler.kMaxRegister >= _nextRegister - 1);
    }

    /// <summary>JSRegExp::RegistersForCaptureCount.</summary>
    public static int RegistersForCaptureCount(int count) => (count + 1) * 2;

    public int AllocateRegister()
    {
        if (_nextRegister >= RegExpMacroAssembler.kMaxRegister)
        {
            _regExpTooBig = true;
            return _nextRegister;
        }
        return _nextRegister++;
    }

    // Lookarounds to match lone surrogates for unicode character class matches
    // are never nested. We can therefore reuse registers.
    public int UnicodeLookaroundStackRegister()
    {
        if (_unicodeLookaroundStackRegister == kNoRegister) _unicodeLookaroundStackRegister = AllocateRegister();
        return _unicodeLookaroundStackRegister;
    }

    public int UnicodeLookaroundPositionRegister()
    {
        if (_unicodeLookaroundPositionRegister == kNoRegister) _unicodeLookaroundPositionRegister = AllocateRegister();
        return _unicodeLookaroundPositionRegister;
    }

    public void AddWork(RegExpNode node)
    {
        if (!node.OnWorkList && !node.Label.IsBound)
        {
            node.OnWorkList = true;
            _workList!.Add(node);
        }
    }

    public RegExpMacroAssembler MacroAssembler => _macroAssembler!;
    public EndNode Accept => _accept;

    public int RecursionDepth => _recursionDepth;
    public void IncrementRecursionDepth() => _recursionDepth++;
    public void DecrementRecursionDepth() => _recursionDepth--;

    public RegExpFlags Flags { get => _flags; set => _flags = value; }

    public void SetRegExpTooBig() => _regExpTooBig = true;
    public bool IsRegExpTooBig => _regExpTooBig;

    public bool OneByte => _oneByte;
    public bool Optimize { get => _optimize; set => _optimize = value; }
    public bool LimitingRecursion { get => _limitingRecursion; set => _limitingRecursion = value; }
    public bool ReadBackward { get => _readBackward; set => _readBackward = value; }
    public bool HasSearchPrefix => _hasSearchPrefix;
    public FrequencyCollator FrequencyCollator => _frequencyCollator;

    public int CurrentExpansionFactor { get => _currentExpansionFactor; set => _currentExpansionFactor = value; }

    // The recursive nature of ToNode node generation means we may run into stack
    // overflow issues. We introduce periodic checks to detect these, and the
    // tick counter helps limit overhead of these checks.
    public void ToNodeMaybeCheckForStackOverflow()
    {
        if (_toNodeOverflowCheckTicks++ % 64 == 0) ToNodeCheckForStackOverflow();
    }

    public void ToNodeCheckForStackOverflow()
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _regExpTooBig = true;  // Abort compilation.
        }
    }
}

/// <summary>Categorizes character ranges into BMP, non-BMP, lead, and trail surrogates.</summary>
public sealed partial class UnicodeRangeSplitter
{
    public const int kInitialSize = 8;

    readonly List<CharacterRange> _bmp = new(kInitialSize);
    readonly List<CharacterRange> _leadSurrogates = new(kInitialSize);
    readonly List<CharacterRange> _trailSurrogates = new(kInitialSize);
    readonly List<CharacterRange> _nonBmp = new(kInitialSize);

    public List<CharacterRange> Bmp => _bmp;
    public List<CharacterRange> LeadSurrogates => _leadSurrogates;
    public List<CharacterRange> TrailSurrogates => _trailSurrogates;
    public List<CharacterRange> NonBmp => _nonBmp;
}
