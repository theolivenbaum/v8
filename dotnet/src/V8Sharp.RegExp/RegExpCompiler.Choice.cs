// Port of src/regexp/regexp-compiler.cc, part 2: atomic loop classification,
// LoopChoiceNode and ChoiceNode emission, Boyer-Moore lookahead, the
// unanchored-search accelerators, ActionNode/BackReferenceNode emission.

using System.Numerics;
using System.Runtime.InteropServices;
using static V8Sharp.RegExp.CompilerConstants;

namespace V8Sharp.RegExp;

public sealed partial class RegExpCompiler
{
    /// <summary>--regexp-quick-check (default true).</summary>
    public static bool s_regexpQuickCheck = true;
    /// <summary>--regexp-simd-in-rc (default true).</summary>
    public static bool s_regexpSimdInRc = true;
    /// <summary>--regexp-masked-dispatch (default true).</summary>
    public static bool s_regexpMaskedDispatch = true;
}

// Facts about a fixed-length loop's continuation alternative, computed by
// AnalyzeAtomicLoopContinuation in one walk of the continuation chain.
internal struct AtomicLoopContinuationAnalysis
{
    // The chain is AssertionNode(AT_END), zero or more position-insensitive
    // ActionNodes, EndNode(ACCEPT).  Such a continuation fails at every
    // retreat position (all strictly before the end).
    public bool AtEndAccept;
    // The continuation cannot fail at the greedy extent (see
    // ContinuationAlwaysSucceeds, AtomicLoopKind::kTotal).
    public bool AlwaysSucceeds;
    // The chain starts with a \b assertion.
    public bool StartsWithBoundary;
    // |first_set| holds FIRST(C): the characters the continuation can accept
    // as its first input character.
    public bool FirstSetKnown;
}

internal static partial class RegExpCompilerHelpers
{
    public static AtomicLoopContinuationAnalysis AnalyzeAtomicLoopContinuation(GuardedAlternative alt,
        List<CharacterRange> firstSet)
    {
        var result = new AtomicLoopContinuationAnalysis();
        if (alt.Guards is not null && alt.Guards.Count != 0) return result;
        RegExpNode node = alt.Node;
        // The always-succeeds fact needs a branch-descending walk (a disjunction
        // succeeds if any alternative does), unlike the single-spine facts below.
        int budget = kContinuationAlwaysSucceedsBudget;
        result.AlwaysSucceeds = ContinuationAlwaysSucceeds(node, 0, ref budget);
        AssertionNode? head = node.AsAssertionNode();
        if (head is not null) result.StartsWithBoundary = head.Type == AssertionNode.AssertionType.AT_BOUNDARY;
        // at_end_accept and first_set are computed over a single walk. Each tracks a
        // flag that its own disqualifying nodes clear while the walk continues for
        // the other.
        bool atEndAlive = head is not null && head.Type == AssertionNode.AssertionType.AT_END;
        bool firstAlive = true;
        for (int depth = 0; depth <= RegExpCompiler.kMaxRecursion; ++depth)
        {
            AssertionNode? assertion = node.AsAssertionNode();
            if (assertion is not null)
            {
                if (depth > 0) atEndAlive = false;  // Only a leading AT_END.
                node = assertion.OnSuccess;
                continue;
            }
            ActionNode? action = node.AsActionNode();
            if (action is not null)
            {
                switch (action.Type)
                {
                    case ActionNode.ActionType.STORE_POSITION:
                    case ActionNode.ActionType.CLEAR_CAPTURES:
                        break;
                    // Position-insensitive for the at-end walk, but carrying
                    // cross-attempt state (counted-loop registers) or changing what
                    // characters match, so the FIRST walk stops.
                    case ActionNode.ActionType.SET_REGISTER_FOR_LOOP:
                    case ActionNode.ActionType.INCREMENT_REGISTER:
                        firstAlive = false;
                        break;
                    // RESTORE_POSITION rewrites the position; submatch actions consume
                    // input; EMPTY_MATCH_CHECK reads a saved position that retreats
                    // invalidate; EATS_AT_LEAST is conservatively excluded.
                    case ActionNode.ActionType.RESTORE_POSITION:
                    case ActionNode.ActionType.BEGIN_POSITIVE_SUBMATCH:
                    case ActionNode.ActionType.BEGIN_NEGATIVE_SUBMATCH:
                    case ActionNode.ActionType.POSITIVE_SUBMATCH_SUCCESS:
                    case ActionNode.ActionType.EMPTY_MATCH_CHECK:
                    case ActionNode.ActionType.EATS_AT_LEAST:
                        return result;
                }
                node = action.OnSuccess;
                continue;
            }
            EndNode? end = node.AsEndNode();
            if (end is not null)
            {
                result.AtEndAccept = atEndAlive && end.EndAction == EndNode.Action.ACCEPT;
                return result;
            }
            // A text (or other) node: the at-end walk requires ACCEPT here; the
            // FIRST walk takes the first element's match set of a forward TextNode.
            if (firstAlive)
            {
                TextNode? text = node.AsTextNode();
                if (text is not null && !text.ReadBackward)
                {
                    // Note only the atom's first character enters FIRST (the check under
                    // analysis is the first character check), so the full-element helper
                    // does not apply to atoms here.
                    TextElement elm = text.Elements[0];
                    if (elm.Type == TextElement.TextType.CLASS_RANGES)
                    {
                        AppendClassRangesMatchSet(elm.ClassRanges, firstSet);
                        result.FirstSetKnown = true;
                    }
                    else if (!text.Flags.IsIgnoreCase())
                    {
                        firstSet.Add(CharacterRange.Singleton(elm.Atom.Data[0]));
                        result.FirstSetKnown = true;
                    }
                }
            }
            return result;
        }
        return result;
    }

    // Classifies |loop| as an atomic loop: alt 0 a fixed-length greedy body
    // chain back to |loop|, alt 1 a continuation that provably cannot benefit
    // from retreating (per kind; see AtomicLoopKind).
    public static AtomicLoopKind ClassifyAtomicLoop(LoopChoiceNode loop)
    {
        if (loop.Alternatives.Count != 2) return AtomicLoopKind.kNone;
        GuardedAlternative body = loop.Alternatives[0];
        GuardedAlternative continuation = loop.Alternatives[1];

        var bodySet = new List<CharacterRange>(4);
        AtomicLoopBodyAnalysis bodyInfo = AnalyzeAtomicLoopBody(body, loop, bodySet);
        if (!bodyInfo.FixedLengthEligible) return AtomicLoopKind.kNone;

        var firstSet = new List<CharacterRange>(2);
        AtomicLoopContinuationAnalysis cont = AnalyzeAtomicLoopContinuation(continuation, firstSet);

        // Mutually exclusive: a leading AT_END fails the always-succeeds walk (it
        // stops at the assertion), so only one of these fires.
        if (cont.AtEndAccept) return AtomicLoopKind.kAtEnd;
        if (cont.AlwaysSucceeds) return AtomicLoopKind.kTotal;
        if (!bodyInfo.SetKnown) return AtomicLoopKind.kNone;
        CharacterRange.Canonicalize(bodySet);
        // Disjointness before the boundary rule: where both apply (a \b followed
        // by a disjoint character, e.g. /\w+\b=/), kDisjoint is strictly stronger.
        if (cont.FirstSetKnown)
        {
            CharacterRange.Canonicalize(firstSet);
            if (!CharacterRange.Intersects(bodySet, firstSet)) return AtomicLoopKind.kDisjoint;
        }
        // The word characters as \b sees them (see EmitWordCheck).  A body that
        // folds outside ASCII (e.g. [k] under /iu matching U+212A, a non-word
        // character to \b) fails the subset check.
        if (cont.StartsWithBoundary && RangesSubsetOfSpecialClass(bodySet, kWordRanges))
        {
            return AtomicLoopKind.kBoundary;
        }
        return AtomicLoopKind.kNone;
    }

    // Decides how much of the drain epilogue a fixed-length loop needs (see
    // DrainMode), from the strongest analysis that applies.
    //
    // kRetryAtEntry and kRestoreOnly reach the loop-exit backtrack in the same
    // state a fully-unwound kFull drain would, so they need no grant.  kOmit
    // instead leaves the position parked at the greedy extent when the loop
    // backtracks out, which each kind licenses separately at its case below.
    //
    // kOmit additionally requires a single-code-unit body: a wider body lets a
    // misaligned restart run past the extent and stop on a character the
    // continuation accepts (/(?:aa)+c/ on "aaaaac" must match "aaaac"@1).
    public static DrainMode ChooseFixedLengthLoopDrainMode(ChoiceNode choice, Trace trace)
    {
        LoopChoiceNode? loop = choice.AsLoopChoiceNode();
        if (loop is null) return DrainMode.kFull;
        AtomicLoopKind kind = loop.GetAtomicLoopKind();
        bool parkable = kind != AtomicLoopKind.kNone && loop.FixedLengthBodyIterationLength() == 1;
        switch (kind)
        {
            case AtomicLoopKind.kNone:
                return DrainMode.kFull;
            case AtomicLoopKind.kAtEnd:
            case AtomicLoopKind.kTotal:
                // Tail position (continuation ends in ACCEPT): parking is safe with a
                // null exit target, and under any grant with a non-null one.
                // Otherwise every retry is still futile, so restore-only suffices.
                return parkable && (trace.Backtrack is null || trace.ParkedGrant != ParkedGrant.kNone)
                    ? DrainMode.kOmit
                    : DrainMode.kRestoreOnly;
            case AtomicLoopKind.kBoundary:
                // A nonempty uniform prefix proves a word character before the loop
                // entry, making even the entry retry futile; without it, retry at entry.
                return parkable && trace.Backtrack is not null &&
                       trace.ParkedGrant == ParkedGrant.kParkedNonEmptyUniformPrefix
                    ? DrainMode.kOmit
                    : DrainMode.kRetryAtEntry;
            case AtomicLoopKind.kDisjoint:
                // Not necessarily tail position (/(?:[abc]*d)+e/ on "abcdabce"), so
                // parking needs a grant.
                return parkable && trace.Backtrack is not null && trace.ParkedGrant != ParkedGrant.kNone
                    ? DrainMode.kOmit
                    : DrainMode.kRestoreOnly;
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    // Tracks that a run of text all draws from one character source: a single
    // ClassRanges node (e.g. the `[a-z]` of /[a-z]+/) or a single literal code
    // unit.  Comparison is by AST-node identity, which suffices because quantifier
    // unrolling reuses the same node for a loop's mandatory copies and its body
    // (see Quantifier::ToNode).
    public struct UniformTextSource
    {
        RegExpClassRanges? _classRanges;
        int _atomChar;
        RegExpFlags _flags;
        bool _isAtom;
        bool _isNotEmpty;

        // Folds |text| into the source.  Returns false if |text| introduces a
        // second source (a different class node, or a different literal character).
        public bool Accumulate(TextNode text)
        {
            if (text.ReadBackward) return false;
            // Atoms are identified by their raw character, but the set one accepts
            // also depends on case folding, which a modifier group can change
            // between the prefix and the loop body.
            if (!_isNotEmpty)
            {
                _flags = text.Flags;
            }
            else if (text.Flags.IsIgnoreCase() != _flags.IsIgnoreCase())
            {
                return false;
            }
            List<TextElement> elms = text.Elements;
            for (int i = 0; i < elms.Count; i++)
            {
                TextElement elm = elms[i];
                if (elm.Type == TextElement.TextType.CLASS_RANGES)
                {
                    RegExpClassRanges cr = elm.ClassRanges;
                    if (_isNotEmpty && (_isAtom || _classRanges != cr)) return false;
                    _classRanges = cr;
                    _isNotEmpty = true;
                }
                else
                {
                    string data = elm.Atom.Data;
                    for (int j = 0; j < data.Length; j++)
                    {
                        if (_isNotEmpty && (!_isAtom || _atomChar != data[j])) return false;
                        _isAtom = true;
                        _atomChar = data[j];
                        _isNotEmpty = true;
                    }
                }
            }
            return true;
        }
    }

    // Whether the alternative rooted at |node| can only match at the input start,
    // i.e. its chain hits an AT_START (^) assertion before consuming or
    // repositioning (e.g. the `^b` arm of /a*|^b/).
    public static bool AlternativeMatchesOnlyAtStart(RegExpNode node)
    {
        for (int depth = 0; depth <= RegExpCompiler.kMaxRecursion; depth++)
        {
            ActionNode? action = node.AsActionNode();
            if (action is null)
            {
                AssertionNode? assertion = node.AsAssertionNode();
                return assertion is not null && assertion.Type == AssertionNode.AssertionType.AT_START;
            }
            if (!action.IsRegisterOnlyAction()) return false;
            node = action.OnSuccess;
        }
        return false;
    }

    // EATS_AT_LEAST tags are no-ops that propagate to on_success; skip past any
    // run of them.
    public static RegExpNode SkipEatsAtLeastTags(RegExpNode node)
    {
        for (ActionNode? a = node.AsActionNode(); a is not null; a = node.AsActionNode())
        {
            if (a.Type != ActionNode.ActionType.EATS_AT_LEAST) break;
            node = a.OnSuccess;
        }
        return node;
    }

    // Walks past EATS_AT_LEAST tags and at most one deferrable ActionNode wrapper
    // to expose the first "real" body node. Returns null if the chain has more
    // than one non-deferrable action.
    public static RegExpNode? FindBodyNodeUnderOneWrapper(RegExpNode body, out ActionNode? wrapper)
    {
        wrapper = null;
        RegExpNode node = SkipEatsAtLeastTags(body);
        ActionNode? a = node.AsActionNode();
        if (a is not null)
        {
            if (!a.IsSimpleAction()) return null;
            wrapper = a;
            node = SkipEatsAtLeastTags(a.OnSuccess);
        }
        return node;
    }

    // Emits `body` against a copy of `base_trace`, deferring `wrapper` (if any)
    // into the trace so its action runs as part of the body's flush.
    public static EmitResult EmitBodyMaybeWrapped(RegExpCompiler compiler, RegExpNode body, Trace baseTrace,
        ActionNode? wrapper)
    {
        var trace = new Trace(baseTrace);
        if (wrapper is not null) trace.AddAction(wrapper);
        return body.Emit(compiler, trace);
    }

    // Builds the SkipUntilBitInTable boolean table (and the SIMD nibble table when
    // want_nibble_table) from the leading ClassRanges. Returns false if the class
    // is unsuitable. 1 = "might match, stop scanning"; 0 = "skip".
    public static bool BuildBitTableForClassPrefix(RegExpClassRanges cr, bool oneByte, bool wantNibbleTable,
        out byte[]? tableOut, out byte[]? nibbleTableOut)
    {
        tableOut = null;
        nibbleTableOut = null;
        Debug.Assert(oneByte);
        if (cr.IsNegated) return false;

        List<CharacterRange> ranges = cr.Ranges;
        CharacterRange.Canonicalize(ranges);
        if (oneByte) CharacterRange.ClampToOneByte(ranges);
        if (ranges.Count == 0) return false;

        const int kMaxChar = kMaxOneByteCharCode;
        if (ranges[0].From > kMaxChar) return false;

        const int kMask = RegExpMacroAssembler.kTableMask;
        const int kSize = RegExpMacroAssembler.kTableSize;
        byte[] table = new byte[kSize];
        byte[]? nibbleTable = wantNibbleTable ? new byte[kSize / 8] : null;

        foreach (CharacterRange r in ranges)
        {
            int lo = r.From;
            int hi = Math.Min(r.To, kMaxChar);
            if (lo > hi) break;
            for (int c = lo; c <= hi; c++)
            {
                int idx = c & kMask;
                table[idx] = 1;
                if (nibbleTable is not null)
                {
                    int loNibble = idx & 0x0f;
                    int hiNibble = (idx >> 4) & 0x07;
                    nibbleTable[loNibble] |= (byte)(1 << hiNibble);
                }
            }
        }
        tableOut = table;
        nibbleTableOut = nibbleTable;
        return true;
    }

    // Collects up to two code units <= max_char from the canonical (ascending)
    // range list `ranges` into `chars`, in order. Returns the number collected (0,
    // 1, or 2), or kCharSetTooLarge if a third such unit exists.
    public const int kCharSetTooLarge = -1;

    public static int CollectSmallCharSet(List<CharacterRange> ranges, uint maxChar, Span<uint> chars)
    {
        int count = 0;
        foreach (CharacterRange r in ranges)
        {
            if ((uint)r.From > maxChar) break;  // canonical ascending: nothing lower left
            uint to = Math.Min((uint)r.To, maxChar);
            for (uint c = (uint)r.From; c <= to; c++)
            {
                if (count == 2) return kCharSetTooLarge;
                chars[count++] = c;
            }
        }
        return count;
    }

    // Emits the cheapest SkipUntil* op that stops the scan at one of `chars`
    // (`count` is 1 or 2 code units).
    public static void EmitSkipUntilSmallCharSet(RegExpMacroAssembler masm, bool oneByte, int cpOffset,
        int advanceBy, ReadOnlySpan<uint> chars, int count, int boundsCheckOffset, Label? onMatch,
        Label? onNoMatch)
    {
        Debug.Assert(count == 1 || count == 2);
        if (count == 1)
        {
            masm.SkipUntilChar(cpOffset, advanceBy, chars[0], boundsCheckOffset, onMatch, onNoMatch);
            return;
        }
        uint exor = chars[0] ^ chars[1];
        if ((exor & (exor - 1)) == 0)
        {
            uint mask = CharMask(oneByte) ^ exor;
            masm.SkipUntilCharAnd(cpOffset, advanceBy, chars[0], mask, boundsCheckOffset, onMatch, onNoMatch);
        }
        else
        {
            masm.SkipUntilCharOrChar(cpOffset, advanceBy, chars[0], chars[1], boundsCheckOffset, onMatch,
                onNoMatch);
        }
    }
}

public sealed partial class LoopChoiceNode
{
    public AtomicLoopKind GetAtomicLoopKind()
    {
        if (!_atomicLoopKindValid)
        {
            _atomicLoopKind = RegExpCompilerHelpers.ClassifyAtomicLoop(this);
            _atomicLoopKindValid = true;
        }
        return _atomicLoopKind;
    }

    public void AddLoopAlternative(GuardedAlternative alt)
    {
        Debug.Assert(_loopNode is null);
        AddAlternative(alt);
        _loopNode = alt.Node;
    }

    public void AddContinueAlternative(GuardedAlternative alt)
    {
        Debug.Assert(_continueNode is null);
        AddAlternative(alt);
        _continueNode = alt.Node;
    }

    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        if (trace.SpecialLoopState is not null && trace.SpecialLoopState.LoopChoiceNode == this)
        {
            // Back edge of fixed length optimized loop node graph.
            int textLength = FixedLengthLoopLengthForAlternative(ref CollectionsMarshal.AsSpan(_alternatives)[0]);
            Debug.Assert(textLength != kNodeIsTooComplexForFixedLengthLoops);
            // Update the counter-based backtracking info on the stack.  This is an
            // optimization for fixed length loops (see below).
            Debug.Assert(trace.CpOffset == textLength);
            macroAssembler.AdvanceCurrentPosition(textLength);
            trace.SpecialLoopState.GoToLoopTopLabel(macroAssembler);
            return EmitResult.Success();
        }
        Debug.Assert(trace.SpecialLoopState is null);
        if (!trace.IsTrivial) return trace.Flush(compiler, this);
        return EmitAsChoiceNode(compiler, trace);
    }

    // The machine-generated `.*?` loop that PreprocessRegExp prepends to every
    // unanchored, non-sticky pattern to retry the match at each start position.
    // Recognized structurally: a two-alternative loop whose second alternative is
    // an omnivorous (matches-anything) text looping back to the loop.
    public bool IsImplicitSearchLoop(RegExpCompiler compiler)
    {
        if (_alternatives.Count != 2) return false;
        GuardedAlternative alt1 = _alternatives[1];
        if (alt1.Guards is not null && alt1.Guards.Count != 0) return false;
        return alt1.Node.GetSuccessorOfOmnivorousTextNode(compiler) == this;
    }

    // The grant the search-loop body carries (kNone off the search loop).  The
    // body's loop-exit backtrack always targets the advance-and-retry alternative,
    // which rescans forward, so the base kParked always holds.  This walk looks for
    // the stronger kParkedUniformPrefix: the body reaches a trailing atomic loop
    // after consuming only that loop's own character source.
    public ParkedGrant ComputeSearchBodyParkedGrant(RegExpCompiler compiler)
    {
        if (!IsImplicitSearchLoop(compiler)) return ParkedGrant.kNone;
        var source = new RegExpCompilerHelpers.UniformTextSource();
        RegExpNode node = _alternatives[0].Node;
        for (int depth = 0; depth <= RegExpCompiler.kMaxRecursion; depth++)
        {
            AssertionNode? assertion = node.AsAssertionNode();
            if (assertion is not null)
            {
                // Zero-width and emitted with the trace unchanged, so prefix
                // uniformity is preserved (a leading \b as in /\b\w+\b/ is fine).
                node = assertion.OnSuccess;
                continue;
            }
            ActionNode? action = node.AsActionNode();
            if (action is not null)
            {
                if (!action.IsRegisterOnlyAction()) return ParkedGrant.kParked;
                node = action.OnSuccess;
                continue;
            }
            TextNode? text = node.AsTextNode();
            if (text is not null)
            {
                if (!source.Accumulate(text)) return ParkedGrant.kParked;
                node = text.OnSuccess;
                continue;
            }
            LoopChoiceNode? loop = node.AsLoopChoiceNode();
            if (loop is null || loop.GetAtomicLoopKind() == AtomicLoopKind.kNone)
            {
                return ParkedGrant.kParked;
            }
            // The loop body must draw from the same source as the prefix.
            RegExpNode body = loop.Alternatives[0].Node;
            for (int i = 0; i <= RegExpCompiler.kMaxRecursion; i++)
            {
                if (body == loop) return ParkedGrant.kParkedUniformPrefix;
                TextNode? bodyText = body.AsTextNode();
                if (bodyText is null || !source.Accumulate(bodyText)) break;
                body = bodyText.OnSuccess;
            }
            return ParkedGrant.kParked;
        }
        return ParkedGrant.kParked;
    }
}

// This class is used when generating the alternatives in a choice node.  It
// records the way the alternative is being code generated.
internal sealed class AlternativeGeneration
{
    public readonly Label PossibleSuccess = new();
    public bool ExpectsPreload;
    public readonly Label After = new();
    public readonly QuickCheckDetails QuickCheckDetails = new();
}

internal sealed class AlternativeGenerationList
{
    readonly AlternativeGeneration[] _altGens;

    public AlternativeGenerationList(int count)
    {
        _altGens = new AlternativeGeneration[count];
        for (int i = 0; i < count; i++) _altGens[i] = new AlternativeGeneration();
    }

    public AlternativeGeneration At(int i) => _altGens[i];
}

public sealed partial class BoyerMoorePositionInfo
{
    public void Set(int character) => SetInterval(new Interval(character, character));

    static ContainedInLattice AddRange(ContainedInLattice containment, int[] ranges, Interval newRange)
    {
        Debug.Assert((ranges.Length & 1) == 1);
        Debug.Assert(ranges[^1] == CharacterRange.kMaxCodePoint + 1);
        if (containment == ContainedInLattice.kLatticeUnknown) return containment;
        bool inside = false;
        int last = 0;
        for (int i = 0; i < ranges.Length; inside = !inside, last = ranges[i], i++)
        {
            // Consider the range from last to ranges[i].
            // We haven't got to the new range yet.
            if (ranges[i] <= newRange.From) continue;
            // New range is wholly inside last-ranges[i].  Note that new_range.to() is
            // inclusive, but the values in ranges are not.
            if (last <= newRange.From && newRange.To < ranges[i])
            {
                return Combine(containment, inside ? ContainedInLattice.kLatticeIn : ContainedInLattice.kLatticeOut);
            }
            return ContainedInLattice.kLatticeUnknown;
        }
        return containment;
    }

    internal static int BitsetFirstSetBit(UInt128 bitset)
    {
        if (bitset == UInt128.Zero) return -1;
        return (int)UInt128.TrailingZeroCount(bitset);
    }

    public void SetInterval(Interval interval)
    {
        _w = AddRange(_w, kWordRanges, interval);

        if (interval.Size >= kMapSize)
        {
            _mapCount = kMapSize;
            _map = UInt128.MaxValue;
            return;
        }

        for (int i = interval.From; i <= interval.To; i++)
        {
            int modCharacter = i & kMask;
            UInt128 bit = UInt128.One << modCharacter;
            if ((_map & bit) == UInt128.Zero)
            {
                _mapCount++;
                _map |= bit;
            }
            if (_mapCount == kMapSize) return;
        }
    }

    public void SetAll()
    {
        _w = ContainedInLattice.kLatticeUnknown;
        if (_mapCount != kMapSize)
        {
            _mapCount = kMapSize;
            _map = UInt128.MaxValue;
        }
    }
}

public sealed partial class BoyerMooreLookahead
{
    public BoyerMooreLookahead(int length, RegExpCompiler compiler)
    {
        _length = length;
        _compiler = compiler;
        _maxChar = (int)MaxCodeUnit(compiler.OneByte);
        _bitmaps = new List<BoyerMoorePositionInfo>(length);
        for (int i = 0; i < length; i++) _bitmaps.Add(new BoyerMoorePositionInfo());
    }

    // Find the longest range of lookahead that has the fewest number of different
    // characters that can occur at a given position.  Since we are optimizing two
    // different parameters at once this is a tradeoff.
    bool FindWorthwhileInterval(out int from, out int to)
    {
        from = 0;
        to = 0;
        int biggestPoints = 0;
        // If more than 32 characters out of 128 can occur it is unlikely that we can
        // be lucky enough to step forwards much of the time.
        const int kMaxMax = 32;
        for (int maxNumberOfChars = 4; maxNumberOfChars < kMaxMax; maxNumberOfChars *= 2)
        {
            biggestPoints = FindBestInterval(maxNumberOfChars, biggestPoints, ref from, ref to);
        }
        return biggestPoints != 0;
    }

    // Find the highest-points range between 0 and length_ where the character
    // information is not too vague.  'Too vague' means that there are more than
    // max_number_of_chars that can occur at this position.  Calculates the number
    // of points as the product of width-of-the-range and
    // probability-of-finding-one-of-the-characters, where the probability is
    // calculated using the frequency distribution of the sample subject string.
    int FindBestInterval(int maxNumberOfChars, int oldBiggestPoints, ref int from, ref int to)
    {
        int biggestPoints = oldBiggestPoints;
        const int kSize = RegExpMacroAssembler.kTableSize;
        for (int i = 0; i < _length;)
        {
            while (i < _length && Count(i) > maxNumberOfChars) i++;
            if (i == _length) break;
            int rememberedFrom = i;

            UInt128 unionBitset = UInt128.Zero;
            for (; i < _length && Count(i) <= maxNumberOfChars; i++) unionBitset |= _bitmaps[i].RawBitset;

            int frequency = 0;

            // Iterate only over set bits.
            int j;
            while ((j = BoyerMoorePositionInfo.BitsetFirstSetBit(unionBitset)) != -1)
            {
                // Add 1 to the frequency to give a small per-character boost for
                // the cases where our sampling is not good enough and many
                // characters have a frequency of zero.
                frequency += _compiler.FrequencyCollator.Frequency(j) + 1;
                unionBitset &= ~(UInt128.One << j);
            }

            // We use the probability of skipping times the distance we are skipping to
            // judge the effectiveness of this.  Actually we have a cut-off:  By
            // dividing by 2 we switch off the skipping if the probability of skipping
            // is less than 50%.  This is because the multibyte mask-and-compare
            // skipping in quickcheck is more likely to do well on this case.
            bool inQuickcheckRange = (i - rememberedFrom < 4) ||
                                     (_compiler.OneByte ? rememberedFrom <= 4 : rememberedFrom <= 2);
            // Called 'probability' but it is only a rough estimate and can actually
            // be outside the 0-kSize range.
            int probability = (inQuickcheckRange ? kSize / 2 : kSize) - frequency;
            int points = (i - rememberedFrom) * probability;
            if (points > biggestPoints)
            {
                from = rememberedFrom;
                to = i - 1;
                biggestPoints = points;
            }
        }
        return biggestPoints;
    }

    // Take all the characters that will not prevent a successful match if they
    // occur in the subject string in the range between min_lookahead and
    // max_lookahead (inclusive) measured from the current position.  If the
    // character at max_lookahead offset is not one of these characters, then we
    // can safely skip forwards by the number of characters in the range.
    //
    // nibble_table is only used for SIMD variants and encodes the same information
    // as boolean_skip_table but in only 128 bits.
    public int GetSkipTable(int minLookahead, int maxLookahead, byte[] booleanSkipTable,
        byte[]? nibbleTable = null)
    {
        const byte kSkipArrayEntry = 0;
        const byte kDontSkipArrayEntry = 1;

        Array.Fill(booleanSkipTable, kSkipArrayEntry);
        if (nibbleTable is not null) Array.Clear(nibbleTable);

        for (int i = maxLookahead; i >= minLookahead; i--)
        {
            UInt128 bitset = _bitmaps[i].RawBitset;

            // Iterate only over set bits.
            int j;
            while ((j = BoyerMoorePositionInfo.BitsetFirstSetBit(bitset)) != -1)
            {
                booleanSkipTable[j] = kDontSkipArrayEntry;
                if (nibbleTable is not null)
                {
                    int loNibble = j & 0x0f;
                    int hiNibble = (j >> 4) & 0x07;
                    nibbleTable[loNibble] |= (byte)(1 << hiNibble);
                }
                bitset &= ~(UInt128.One << j);
            }
        }

        int skip = maxLookahead + 1 - minLookahead;
        return skip;
    }

    // See comment above on the implementation of GetSkipTable.
    public bool EmitSkipInstructions(RegExpMacroAssembler masm)
    {
        const int kSize = RegExpMacroAssembler.kTableSize;

        if (!FindWorthwhileInterval(out int minLookahead, out int maxLookahead)) return false;

        // Check if we only have a single non-empty position info, and that info
        // contains only one or two characters.
        bool foundSinglePosition = false;
        const uint kNoChar = 0xffffffff;
        uint charOne = kNoChar;
        uint charTwo = kNoChar;
        for (int i = minLookahead; i <= maxLookahead; i++)
        {
            BoyerMoorePositionInfo map = _bitmaps[i];
            if (map.MapCount == 0)
            {
                // If we have a position where no characters can match then we just can't
                // match.
                masm.Fail();
                return true;
            }

            if (foundSinglePosition || map.MapCount > 2)
            {
                // We found a second position or there were more than two characters that
                // matched at this position.
                foundSinglePosition = false;
                break;
            }

            UInt128 bitset = map.RawBitset;
            charOne = (uint)BoyerMoorePositionInfo.BitsetFirstSetBit(bitset);
            if (map.MapCount == 2)
            {
                bitset &= ~(UInt128.One << (int)charOne);
                charTwo = (uint)BoyerMoorePositionInfo.BitsetFirstSetBit(bitset);
            }
            else
            {
                charTwo = charOne;  // Everything below here works for identical chars.
            }
            if (BitOperations.PopCount(charOne ^ charTwo) > 1)
            {
                // For case independent matches we often find two characters that differ
                // only at one bit positions, but in this case they differed more.  We
                // don't have a great bytecode for two characters that are too different.
                break;
            }

            foundSinglePosition = true;
        }

        Debug.Assert(!foundSinglePosition || maxLookahead == minLookahead);

        if (foundSinglePosition && maxLookahead < 3)
        {
            // The mask-compare can probably handle this better.
            return false;
        }

        // TODO(pthier): Remove condition once all architectures that support SIMD for
        // SkipUntilBitInTable also support SkipUntilCharAnd (RISCV missing).
        bool useCharAndSimd = masm.SkipUntilCharAndUseSimd(1) || !masm.SkipUntilBitInTableUseSimd(1);
        if (foundSinglePosition && useCharAndSimd)
        {
            Debug.Assert(_maxChar > kSize);  // This means we will have to do the 'and'.

            var cont = new Label();
            uint mask = RegExpMacroAssembler.kTableMask;
            mask &= ~(charOne ^ charTwo) & 0xffff;  // Mask out the bit where they differ.
            masm.SkipUntilCharAnd(maxLookahead, 1, charOne & mask, mask, Length - 1, cont, cont);

            masm.Bind(cont);
            return true;
        }

        byte[] booleanSkipTable = new byte[kSize];
        byte[]? nibbleTable = null;
        int skipDistance = maxLookahead + 1 - minLookahead;
        if (masm.SkipUntilBitInTableUseSimd(skipDistance)) nibbleTable = new byte[kSize / 8];
        GetSkipTable(minLookahead, maxLookahead, booleanSkipTable, nibbleTable);
        Debug.Assert(skipDistance != 0);

        var cont2 = new Label();
        masm.SkipUntilBitInTable(maxLookahead, booleanSkipTable, nibbleTable, skipDistance, Length - 1, cont2,
            cont2);
        masm.Bind(cont2);
        return true;
    }

    public bool BuildSkipTable(RegExpMacroAssembler masm, out int offset, out int advanceBy, out byte[]? table,
        out byte[]? nibbleTable)
    {
        const int kSize = RegExpMacroAssembler.kTableSize;
        offset = 0;
        advanceBy = 0;
        table = null;
        nibbleTable = null;
        if (!FindWorthwhileInterval(out int minLookahead, out int maxLookahead)) return false;

        byte[] booleanSkipTable = new byte[kSize];
        // Always build the SIMD nibble table: the SkipUntilOneOfMasked3 lowering that
        // consumes this table embeds it unconditionally.
        byte[] nibbles = new byte[kSize / 8];
        int skipDistance = maxLookahead + 1 - minLookahead;
        GetSkipTable(minLookahead, maxLookahead, booleanSkipTable, nibbles);

        offset = maxLookahead;
        advanceBy = skipDistance;
        table = booleanSkipTable;
        nibbleTable = nibbles;
        return true;
    }
}

public sealed partial class SpecialLoopState
{
    public SpecialLoopState(bool notAtStart, ChoiceNode loopChoiceNode)
    {
        _loopChoiceNode = loopChoiceNode;
        _backtrackTrace.SetBacktrack(_stepLabel);
        if (notAtStart) _backtrackTrace.AtStart = Trace.TriBool.FALSE_VALUE;
    }

    public void BindStepLabel(RegExpMacroAssembler macroAssembler) => macroAssembler.Bind(_stepLabel);
    public void BindLoopTopLabel(RegExpMacroAssembler macroAssembler) => macroAssembler.Bind(_loopTopLabel);
    public void GoToLoopTopLabel(RegExpMacroAssembler macroAssembler) => macroAssembler.GoTo(_loopTopLabel);
}

public partial class ChoiceNode
{
    internal static int CalculatePreloadCharacters(RegExpCompiler compiler, int eatsAtLeast)
    {
        int preloadCharacters = Math.Min(4, eatsAtLeast);
        Debug.Assert(preloadCharacters <= 4);
        if (compiler.MacroAssembler.CanReadUnaligned())
        {
            bool oneByte = compiler.OneByte;
            if (oneByte)
            {
                // We can't preload 3 characters because there is no machine instruction
                // to do that.  We can't just load 4 because we could be reading
                // beyond the end of the string, which could cause a memory fault.
                if (preloadCharacters == 3) preloadCharacters = 2;
            }
            else
            {
                if (preloadCharacters > 2) preloadCharacters = 2;
            }
        }
        else
        {
            if (preloadCharacters > 1) preloadCharacters = 1;
        }
        return preloadCharacters;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    void AssertGuardsMentionRegisters(Trace trace)
    {
        int choiceCount = _alternatives.Count;
        for (int i = 0; i < choiceCount - 1; i++)
        {
            List<Guard>? guards = _alternatives[i].Guards;
            int guardCount = guards?.Count ?? 0;
            for (int j = 0; j < guardCount; j++) Debug.Assert(!trace.MentionsReg(guards![j].Reg));
        }
    }

    void SetUpPreLoad(RegExpCompiler compiler, Trace currentTrace, ref PreloadState state)
    {
        if (state.EatsAtLeast == PreloadState.kEatsAtLeastNotYetInitialized)
        {
            // Save some time by looking at most one machine word ahead.
            state.EatsAtLeast = (int)EatsAtLeast(currentTrace.AtStart == Trace.TriBool.FALSE_VALUE);
        }
        state.PreloadCharacters = CalculatePreloadCharacters(compiler, state.EatsAtLeast);

        state.PreloadIsCurrent = currentTrace.CharactersPreloaded == state.PreloadCharacters;
        state.PreloadHasCheckedBounds = state.PreloadIsCurrent;
    }

    public override EmitResult Emit(RegExpCompiler compiler, Trace trace) => EmitAsChoiceNode(compiler, trace);

    /// <summary>ChoiceNode::Emit, callable non-virtually (V8's qualified ChoiceNode::Emit call).</summary>
    internal EmitResult EmitAsChoiceNode(RegExpCompiler compiler, Trace trace)
    {
        int choiceCount = _alternatives.Count;

        if (choiceCount == 1 && _alternatives[0].Guards is null) return _alternatives[0].Node.Emit(compiler, trace);

        AssertGuardsMentionRegisters(trace);

        LimitResult limitResult = LimitVersions(compiler, trace);
        if (limitResult == LimitResult.DONE) return EmitResult.Success();
        Debug.Assert(limitResult == LimitResult.CONTINUE);

        // For loop nodes we already flushed (see LoopChoiceNode::Emit), but for
        // other choice nodes we only flush if we are out of code size budget.
        if (trace.FlushBudget == 0 && trace.HasAnyActions) return trace.Flush(compiler, this);

        using var rc = new RecursionCheck(compiler);

        var preload = new PreloadState();
        preload.Init();
        // This must be outside the 'if' because the trace we use for what
        // comes after the special_loop is inside it and needs the lifetime.
        var specialLoopState = new SpecialLoopState(NotAtStart, this);

        int textLength = FixedLengthLoopLengthForAlternative(ref CollectionsMarshal.AsSpan(_alternatives)[0]);
        var altGens = new AlternativeGenerationList(choiceCount);

        // Grant the search-loop body its parked-position grant (see
        // ComputeSearchBodyParkedGrant, Trace::parked_grant).
        LoopChoiceNode? loopChoice = AsLoopChoiceNode();
        ParkedGrant bodyParkedGrant = loopChoice is not null
            ? loopChoice.ComputeSearchBodyParkedGrant(compiler)
            : ParkedGrant.kNone;
        if (choiceCount > 1 && textLength != kNodeIsTooComplexForFixedLengthLoops)
        {
            // If the continuation is provably retreat-insensitive, the drain
            // epilogue can be reduced or omitted entirely; see DrainMode and
            // ChooseFixedLengthLoopDrainMode.  Covers /\s+$/, /\w+\b=/, /[abc]*d/.
            DrainMode drainMode = RegExpCompilerHelpers.ChooseFixedLengthLoopDrainMode(this, trace);
            Debug.Assert(drainMode == DrainMode.kFull || trace.CpOffset == 0);
            Debug.Assert(!(drainMode == DrainMode.kOmit && trace.Backtrack is null) || !trace.HasAnyActions);
            Trace? next = EmitFixedLengthLoop(compiler, trace, altGens, ref preload, specialLoopState, textLength,
                drainMode, bodyParkedGrant);
            if (next is null) return EmitResult.Error();
            trace = next;
        }
        else
        {
            preload.EatsAtLeast = EmitOptimizedUnanchoredSearch(compiler, trace, specialLoopState,
                out bool bmScanEmitted);

            // Try the SkipUntilOneOfMasked dispatch and the SkipUntil* search prelude,
            // both restricted to the implicit `.*?` LoopChoice and only when BM
            // lookahead didn't already emit a competing scan.
            if (RegExpCompiler.s_regexpSimdInRc && !bmScanEmitted && AsLoopChoiceNode() is not null &&
                trace.IsTrivial)
            {
                RegExpNode? body = MatchLazyStarLoopBody(compiler, out ActionNode? wrapper);
                if (body is not null)
                {
                    EmitResult? result = EmitSkipUntilOneOfMaskedSearch(compiler, trace, body, wrapper);
                    if (result is not null)
                    {
                        if (result.Value.IsError) return result.Value;
                        return EmitResult.Success();
                    }
                    EmitSkipUntilSearchPrelude(compiler, trace, body);
                }
            }

            EmitResult r = EmitChoices(compiler, altGens, 0, trace, ref preload, bodyParkedGrant);
            if (r.IsError) return r;
        }

        // At this point we need to generate slow checks for the alternatives where
        // the quick check was inlined.  We can recognize these because the associated
        // label was bound.
        int newFlushBudget = trace.FlushBudget / choiceCount;
        for (int i = 0; i < choiceCount; i++)
        {
            AlternativeGeneration altGen = altGens.At(i);
            var newTrace = new Trace(trace);
            // If there are actions to be flushed we have to limit how many times
            // they are flushed.  Take the budget of the parent trace and distribute
            // it fairly amongst the children.
            if (newTrace.HasAnyActions) newTrace.FlushBudget = newFlushBudget;
            bool nextExpectsPreload = i != choiceCount - 1 && altGens.At(i + 1).ExpectsPreload;
            // Only the body (alternative 0) gets the grant; its out-of-line
            // continuation exits to the advance-and-retry alternative, same as its
            // inline emission in EmitChoices.
            ParkedGrant parkedGrant = i == 0 ? bodyParkedGrant : ParkedGrant.kNone;
            EmitResult r = EmitOutOfLineContinuation(compiler, newTrace, _alternatives[i], altGen,
                preload.PreloadCharacters, nextExpectsPreload, parkedGrant);
            if (r.IsError) return r;
        }

        return EmitResult.Success();
    }

    Trace? EmitFixedLengthLoop(RegExpCompiler compiler, Trace trace, AlternativeGenerationList altGens,
        ref PreloadState preload, SpecialLoopState fixedLengthLoopState, int textLength, DrainMode drainMode,
        ParkedGrant bodyParkedGrant)
    {
        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        // Here we have special handling for greedy loops containing only text nodes
        // and other simple nodes.  We call these fixed length loops.  These are
        // handled by pushing the current position on the stack and then incrementing
        // the current position each time around the switch.  On backtrack we
        // decrement the current position and check it against the pushed value.
        // This avoids pushing backtrack information for each iteration of the loop,
        // which could take up a lot of space.
        //
        //   PushCurrentPosition()             // step-back start marker
        //   loop_top:
        //     <body>                          // one iteration; backtracks to
        //                                     // after_body_match_attempt on failure
        //   after_body_match_attempt:
        //     <continuation>
        //   step_label:
        //     CheckFixedLengthLoop(backtrack) // at the marker? loop-exit backtrack
        //     AdvanceCurrentPosition(-len)    // else step back one iteration ...
        //     GoTo(after_body_match_attempt)  // ... and retry the continuation
        Debug.Assert(trace.SpecialLoopState is null);
        if (drainMode != DrainMode.kOmit) macroAssembler.PushCurrentPosition();
        // This is the label for trying to match what comes after the greedy
        // quantifier, either because the body of the quantifier failed, or because
        // we have stepped back to try again with one iteration fewer.
        var afterBodyMatchAttempt = new Label();
        var fixedLengthMatchTrace = new Trace();
        if (NotAtStart) fixedLengthMatchTrace.AtStart = Trace.TriBool.FALSE_VALUE;
        fixedLengthMatchTrace.SetBacktrack(afterBodyMatchAttempt);
        fixedLengthLoopState.BindLoopTopLabel(macroAssembler);
        fixedLengthMatchTrace.SpecialLoopState = fixedLengthLoopState;
        // Fuse a greedy character-class body into a single forward scan; otherwise
        // emit the per-iteration body.
        EmitResult result = EmitResult.Success();
        if (!MaybeEmitFixedLengthConsumeScan(compiler, afterBodyMatchAttempt, textLength))
        {
            result = _alternatives[0].Node.Emit(compiler, fixedLengthMatchTrace);
        }
        macroAssembler.Bind(afterBodyMatchAttempt);
        if (result.IsError) return null;

        Trace newTrace = fixedLengthLoopState.BacktrackTrace;
        if (drainMode == DrainMode.kOmit)
        {
            // Skip the drain epilogue entirely.  Continuation failure goes straight
            // to the outer backtrack handler.
            newTrace.SetBacktrack(trace.Backtrack);
        }

        // In a fixed length loop there is only one other choice, which is what
        // comes after the greedy quantifer.  Try to match that now.
        result = EmitChoices(compiler, altGens, 1, newTrace, ref preload, bodyParkedGrant);
        if (result.IsError) return null;

        // kOmit emitted no marker and no step label; everything below is the
        // step-label epilogue for the other modes.
        if (drainMode == DrainMode.kOmit) return newTrace;

        fixedLengthLoopState.BindStepLabel(macroAssembler);
        switch (drainMode)
        {
            case DrainMode.kFull:
                // If we have unwound to the bottom then backtrack.
                macroAssembler.CheckFixedLengthLoop(trace.Backtrack);
                // Otherwise try the second priority at an earlier position.
                macroAssembler.AdvanceCurrentPosition(-textLength);
                macroAssembler.GoTo(afterBodyMatchAttempt);
                break;
            case DrainMode.kRetryAtEntry:
                // If the failed attempt was already at the entry marker, pop it and
                // backtrack.
                macroAssembler.CheckFixedLengthLoop(trace.Backtrack);
                // Otherwise restore straight to the marker (interior positions are
                // futile) and retry the continuation there once.
                macroAssembler.PopCurrentPosition();
                macroAssembler.PushCurrentPosition();
                macroAssembler.GoTo(afterBodyMatchAttempt);
                break;
            case DrainMode.kRestoreOnly:
                // Every retry is futile: restore the position to the entry marker and
                // take the loop-exit backtrack.
                macroAssembler.PopCurrentPosition();
                macroAssembler.GoTo(trace.Backtrack);  // Backtracks if null.
                break;
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
        return newTrace;
    }

    int EmitOptimizedUnanchoredSearch(RegExpCompiler compiler, Trace trace, SpecialLoopState searchLoopState,
        out bool bmScanEmitted)
    {
        bmScanEmitted = false;
        int eatsAtLeast = PreloadState.kEatsAtLeastNotYetInitialized;
        LoopChoiceNode? loopChoice = AsLoopChoiceNode();
        if (loopChoice is null || !loopChoice.IsImplicitSearchLoop(compiler)) return eatsAtLeast;

        // Really we should be creating a new trace when we execute this function,
        // but there is no need, because the code it generates cannot backtrack, and
        // we always arrive here with a trivial trace (since it's the entry to a
        // loop.
        Debug.Assert(trace.IsTrivial);

        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        // At this point we know that we are at a non-greedy loop that will eat
        // any character one at a time.  Any non-anchored regexp has such a
        // loop prepended to it in order to find where it starts.  We look for
        // a pattern of the form ...abc... where we can look 6 characters ahead
        // and step forwards 3 if the character is not one of abc.
        BoyerMooreLookahead? bm = BmInfo(false);
        // The --no-regexp-quick-check is for testing.
        if (bm is null && RegExpCompiler.s_regexpQuickCheck)
        {
            eatsAtLeast = (int)Math.Min(kMaxLookaheadForBoyerMoore, EatsAtLeast(false));
            if (eatsAtLeast >= 1)
            {
                bm = new BoyerMooreLookahead(eatsAtLeast, compiler);
                GuardedAlternative alt0 = _alternatives[0];
                alt0.Node.FillInBMInfo(0, kRecursionBudget, bm, false);
            }
        }
        if (bm is not null)
        {
            // Prefer the fused SkipUntilOneOfMasked3 over a bare skip-table scan when
            // the body is a shared-prefix 3-way alternation.
            if (EmitOneOfMasked3Search(compiler, bm) || bm.EmitSkipInstructions(macroAssembler))
            {
                // BM owns the search; do not attempt the inline SkipUntil* scan paths.
                bmScanEmitted = true;
            }
        }
        return eatsAtLeast;
    }

    // Shared structural gate for the two inline SkipUntil* scan strategies.
    RegExpNode? MatchLazyStarLoopBody(RegExpCompiler compiler, out ActionNode? wrapper)
    {
        wrapper = null;
        // The caller has established that `this` is some LoopChoiceNode with a
        // trivial entry trace, but it might just be a `(?:foo)*` quantifier loop.
        // We only fire on the implicit `.*?` shape.
        if (!AsLoopChoiceNode()!.IsImplicitSearchLoop(compiler)) return null;
        return RegExpCompilerHelpers.FindBodyNodeUnderOneWrapper(_alternatives[0].Node, out wrapper);
    }

    EmitResult? EmitSkipUntilOneOfMaskedSearch(RegExpCompiler compiler, Trace trace, RegExpNode body,
        ActionNode? wrapper)
    {
        // Matches the implicit unanchored-search sub-graph whose body is a plain
        // 2-alternative Choice of TextNodes, and emits a SkipUntilOneOfMasked scan
        // that dispatches straight to the two alternative bodies.
        if (!RegExpCompiler.s_regexpQuickCheck) return null;
        // The op lowerings only handle 1-byte (LATIN1) input.
        // TODO(jgruber): Support 2-byte.
        if (!compiler.OneByte) return null;
        if (!compiler.MacroAssembler.CanReadUnaligned()) return null;

        Debug.Assert(trace.IsTrivial);
        ChoiceNode? inner = body.AsChoiceNode();
        if (inner is null) return null;
        const int kNumAlternatives = 2;
        if (inner.Alternatives.Count != kNumAlternatives) return null;
        if (inner.AsLoopChoiceNode() is not null) return null;
        if (inner.AsNegativeLookaroundChoiceNode() is not null) return null;

        for (int i = 0; i < kNumAlternatives; i++)
        {
            List<Guard>? guards = inner.Alternatives[i].Guards;
            if (guards is not null && guards.Count != 0) return null;
            TextNode? altText = inner.Alternatives[i].Node.AsTextNode();
            if (altText is null) return null;
            Debug.Assert(!altText.ReadBackward);
        }

        // The masked op loads a 4-char window at each candidate.
        const int kSkipChars = 4;
        const bool kPossiblyAtStart = false;

        int eatsAtLeast = (int)inner.EatsAtLeast(kPossiblyAtStart);
        if (eatsAtLeast < kSkipChars) return null;
        int maxOffset = eatsAtLeast - 1;

        const int kQCBudget = 1;
        const int kQCWindowStart = 0;
        var alt0Qc = new QuickCheckDetails(kSkipChars);
        var alt1Qc = new QuickCheckDetails(kSkipChars);
        inner.Alternatives[0].Node.GetQuickCheckDetails(alt0Qc, compiler, kQCWindowStart, kPossiblyAtStart,
            kQCBudget);
        if (alt0Qc.CannotMatch()) return null;
        inner.Alternatives[1].Node.GetQuickCheckDetails(alt1Qc, compiler, kQCWindowStart, kPossiblyAtStart,
            kQCBudget);
        if (alt1Qc.CannotMatch()) return null;

        // The union is the per-alt details merged. Merge mutates both its receiver
        // and its argument, so merge into copies.
        var unionQc = new QuickCheckDetails();
        unionQc.CopyFrom(alt0Qc);
        {
            var tmp = new QuickCheckDetails();
            tmp.CopyFrom(alt1Qc);
            unionQc.Merge(tmp, kQCWindowStart);
        }

        if (!alt0Qc.Rationalize(compiler.OneByte)) return null;
        if (!alt1Qc.Rationalize(compiler.OneByte)) return null;
        if (!unionQc.Rationalize(compiler.OneByte)) return null;
        if (alt0Qc.Mask == 0 || alt1Qc.Mask == 0 || unionQc.Mask == 0) return null;

        // The resulting control flow:
        //
        //   loop:       SkipUntilOneOfMasked(...) -> alt0_body / alt1_body / fail
        //   advance:    cp += 1; goto loop
        //   retry_alt1: reload, re-check alt 1's QC; miss -> advance, else fall
        //               through to alt1_body   (alt 0's backtrack target)
        //   alt1_body:  <alt 1>   backtrack -> advance
        //   alt0_body:  <alt 0>   backtrack -> retry_alt1   (try alt 1 here)
        //   fail:       Backtrack
        RegExpMacroAssembler masm = compiler.MacroAssembler;
        const int kScanCpOffset = 0;
        const int kScanAdvanceBy = 1;
        Label loop = new(), advance = new(), retryAlt1 = new(), alt0Body = new(), alt1Body = new(), fail = new();

        masm.Bind(loop);
        masm.SkipUntilOneOfMasked(kScanCpOffset, kScanAdvanceBy, unionQc.Value, unionQc.Mask, maxOffset,
            alt0Qc.Value, alt0Qc.Mask, alt1Qc.Value, alt1Qc.Mask, alt0Body, alt1Body, fail);

        masm.Bind(advance);
        masm.AdvanceCurrentPosition(kScanAdvanceBy);
        masm.GoTo(loop);

        // alt 0's body failed: try alt 1 at the same position.
        masm.Bind(retryAlt1);
        masm.LoadCurrentCharacter(kScanCpOffset, null, false, kSkipChars);
        masm.CheckNotCharacterAfterAnd(alt1Qc.Value, alt1Qc.Mask, advance);

        EmitResult EmitAlt(Label entry, int altIndex, QuickCheckDetails qc, Label backtrack)
        {
            masm.Bind(entry);
            var baseTrace = new Trace();
            baseTrace.CharactersPreloaded = kSkipChars;
            baseTrace.BoundCheckedUpTo = maxOffset;
            baseTrace.SetQuickCheckPerformed(qc);
            baseTrace.AtStart = Trace.TriBool.FALSE_VALUE;
            baseTrace.SetBacktrack(backtrack);
            return RegExpCompilerHelpers.EmitBodyMaybeWrapped(compiler, inner.Alternatives[altIndex].Node,
                baseTrace, wrapper);
        }

        // alt 1 is emitted first so it falls through from retry_alt1; alt 0
        // backtracks to retry_alt1 (the priority chain).
        EmitResult r = EmitAlt(alt1Body, 1, alt1Qc, advance);
        if (r.IsError) return r;
        r = EmitAlt(alt0Body, 0, alt0Qc, retryAlt1);
        if (r.IsError) return r;

        // SkipUntilOneOfMasked exhausted the input. Trace is trivial, so backtrack
        // pops the stack and lands on the bottom-of-stack PushBacktrack(fail).
        masm.Bind(fail);
        masm.Backtrack();

        return EmitResult.Success();
    }

    void EmitSkipUntilSearchPrelude(RegExpCompiler compiler, Trace trace, RegExpNode body)
    {
        // Matches the implicit unanchored-search sub-graph and accelerates the scan
        // to the first position where the body can match. The op is only a
        // position-finder: EmitChoices emits the body at the candidate and re-checks
        // it, so an over-approximate stop set is fine.
        //
        // Three strategies, cheapest first:
        //   (1) an exact small positive leading class [c] / [cd] -> SkipUntilChar /
        //       SkipUntilCharOrChar / SkipUntilCharAnd,
        //   (2) otherwise the body's 1-char quick check -> SkipUntilChar / CharAnd
        //       (covers leading atoms and sub-loops, e.g. /a+/),
        //   (3) otherwise a leading-class bit table -> SkipUntilBitInTable (1-byte).
        if (!RegExpCompiler.s_regexpQuickCheck) return;
        Debug.Assert(trace.IsTrivial);

        RegExpMacroAssembler masm = compiler.MacroAssembler;
        bool oneByte = compiler.OneByte;
        const int kScanCpOffset = 0;
        const int kScanAdvanceBy = 1;
        const int kBoundsCheckOffset = 0;
        uint maxChar = MaxCodeUnit(oneByte);
        var cont = new Label();

        // (1) Exact small positive leading class.
        TextNode? bodyText = body.AsTextNode();
        bool classLed = bodyText is not null && !bodyText.ReadBackward && bodyText.Elements.Count != 0 &&
                        bodyText.Elements[0].Type == TextElement.TextType.CLASS_RANGES;
        if (classLed)
        {
            RegExpClassRanges cr = bodyText!.Elements[0].ClassRanges;
            if (!cr.IsNegated)
            {
                // Work on a copy; Canonicalize mutates, and the ranges are shared with
                // the AST.
                var ranges = new List<CharacterRange>(2);
                ranges.AddRange(cr.Ranges);
                CharacterRange.Canonicalize(ranges);
                Span<uint> chars = stackalloc uint[2];
                int count = RegExpCompilerHelpers.CollectSmallCharSet(ranges, maxChar, chars);
                if (count >= 1)
                {
                    RegExpCompilerHelpers.EmitSkipUntilSmallCharSet(masm, oneByte, kScanCpOffset, kScanAdvanceBy,
                        chars, count, kBoundsCheckOffset, cont, cont);
                    masm.Bind(cont);
                    return;
                }
            }
        }

        // (2) General: the body's 1-char quick check, mirroring EmitQuickCheck's
        // masked/unmasked selection. The quick check is only a *necessary*
        // condition for a match when the body must consume at least one character.
        const bool kPossiblyAtStart = false;
        if (body.EatsAtLeast(kPossiblyAtStart) >= 1)
        {
            var qc = new QuickCheckDetails(1);
            body.GetQuickCheckDetails(qc, compiler, 0, kPossiblyAtStart, kRecursionBudget);
            if (!qc.CannotMatch() && qc.Rationalize(oneByte))
            {
                uint charMask = CharMask(oneByte);
                uint mask = qc.Mask & charMask;
                uint value = qc.Value & charMask;
                if (mask != 0)
                {
                    if (mask == charMask)
                    {
                        masm.SkipUntilChar(kScanCpOffset, kScanAdvanceBy, value, kBoundsCheckOffset, cont, cont);
                    }
                    else
                    {
                        masm.SkipUntilCharAnd(kScanCpOffset, kScanAdvanceBy, value, mask, kBoundsCheckOffset, cont,
                            cont);
                    }
                    masm.Bind(cont);
                    return;
                }
            }
        }

        // (3) Fall back to a leading-class bit table. The 128-entry table is 1-byte
        // only.
        if (!oneByte || !classLed) return;
        RegExpClassRanges cr2 = bodyText!.Elements[0].ClassRanges;
        if (!RegExpCompilerHelpers.BuildBitTableForClassPrefix(cr2, oneByte,
                masm.SkipUntilBitInTableUseSimd(kScanAdvanceBy), out byte[]? table, out byte[]? nibbleTable))
        {
            return;
        }

        masm.SkipUntilBitInTable(kScanCpOffset, table!, nibbleTable, kScanAdvanceBy, kBoundsCheckOffset, cont, cont);
        masm.Bind(cont);
    }

    bool EmitOneOfMasked3Search(RegExpCompiler compiler, BoyerMooreLookahead bm)
    {
        if (!RegExpCompiler.s_regexpSimdInRc) return false;
        // The op lowering only handles 1-byte (LATIN1) input.
        if (!compiler.OneByte) return false;
        if (!compiler.MacroAssembler.CanReadUnaligned()) return false;

        // Shape: alt0 = <wrapper> -> Text(prefix) -> Choice(3 forward Text alts),
        // what RationalizeConsecutiveAtoms produces for a shared-prefix 3-way
        // alternation (e.g. /<script|<style|<link/ -> '<' then (script|style|link)).
        RegExpNode? body = RegExpCompilerHelpers.FindBodyNodeUnderOneWrapper(_alternatives[0].Node, out _);
        TextNode? prefix = body?.AsTextNode();
        if (prefix is null || prefix.ReadBackward) return false;
        ChoiceNode? choice = RegExpCompilerHelpers.SkipEatsAtLeastTags(prefix.OnSuccess).AsChoiceNode();
        if (choice is null || choice.AsLoopChoiceNode() is not null ||
            choice.AsNegativeLookaroundChoiceNode() is not null)
        {
            return false;
        }
        const int kNumAlternatives = 3;  // The op dispatches three ways.
        if (choice.Alternatives.Count != kNumAlternatives) return false;
        for (int i = 0; i < kNumAlternatives; i++)
        {
            List<Guard>? guards = choice.Alternatives[i].Guards;
            if (guards is not null && guards.Count != 0) return false;
            TextNode? alt = choice.Alternatives[i].Node.AsTextNode();
            if (alt is null || alt.ReadBackward) return false;
        }
        int prefixLen = prefix.Length();

        // The op loads a 4-char window at offset 0 (combined check) and another at
        // the prefix end (per-alt dispatch); both must be in bounds.
        const int kSkipChars = 4;
        if (bm.Length < prefixLen + kSkipChars) return false;

        // bc0: the leading scan over BM's most discriminating lookahead position.
        if (!bm.BuildSkipTable(compiler.MacroAssembler, out int scanOffset, out int advanceBy, out byte[]? table,
                out byte[]? nibbleTable))
        {
            return false;
        }

        const bool kPossiblyAtStart = false;
        const int kCombinedBudget = 4;  // recurse prefix -> Choice
        const int kAltBudget = 1;       // each alt is a plain Text
        var combined = new QuickCheckDetails(kSkipChars);
        prefix.GetQuickCheckDetails(combined, compiler, 0, kPossiblyAtStart, kCombinedBudget);
        if (combined.CannotMatch() || !combined.Rationalize(compiler.OneByte) || combined.Mask == 0) return false;
        var altQc = new QuickCheckDetails[kNumAlternatives];
        for (int i = 0; i < kNumAlternatives; i++)
        {
            altQc[i] = new QuickCheckDetails(kSkipChars);
            choice.Alternatives[i].Node.GetQuickCheckDetails(altQc[i], compiler, 0, kPossiblyAtStart, kAltBudget);
            if (altQc[i].CannotMatch() || !altQc[i].Rationalize(compiler.OneByte) || altQc[i].Mask == 0)
            {
                return false;
            }
        }

        // cont-routing: every exit (end-of-input and all three dispatch matches)
        // lands on `cont`; the caller falls through to EmitChoices, which matches
        // the prefix + alternation (with correct priority) at the surviving candidate.
        RegExpMacroAssembler masm = compiler.MacroAssembler;
        var cont = new Label();
        int bounds = bm.Length - 1;
        var args = new RegExpMacroAssembler.SkipUntilOneOfMasked3Args
        {
            bc0_cp_offset = scanOffset,
            bc0_advance_by = advanceBy,
            bc0_table = table!,
            bc0_nibble_table = nibbleTable,
            bc1_bounds_check_offset = bounds,
            bc1_on_failure = cont,
            bc1_cp_offset = 0,
            bc2_characters = combined.Value,
            bc2_mask = combined.Mask,
            bc3_by = 1,
            bc4_bounds_check_offset = bounds,
            bc4_cp_offset = prefixLen,
            bc5_characters = altQc[0].Value,
            bc5_mask = altQc[0].Mask,
            bc5_on_equal = cont,
            bc6_characters = altQc[1].Value,
            bc6_mask = altQc[1].Mask,
            bc6_on_equal = cont,
            bc7_characters = altQc[2].Value,
            bc7_mask = altQc[2].Mask,
            fallthrough_jump_target = cont,
        };
        masm.SkipUntilOneOfMasked3(args);
        masm.Bind(cont);
        return true;
    }

    bool MaybeEmitFixedLengthConsumeScan(RegExpCompiler compiler, Label exit, int textLength)
    {
        // Called from EmitFixedLengthLoop. If the greedy loop body is a character
        // class [B], replace the per-iteration body + back-edge with a single forward
        // scan over its exit set (the complement of B: the chars that end the loop),
        // landing on |exit|; return true.
        if (!RegExpCompiler.s_regexpSimdInRc) return false;
        // The scan compares single code units and advances by one. Only a two-byte
        // subject under /u or /v is unsafe: there a class can match a supplementary
        // code point (a surrogate pair), so the loop's one-code-unit step-back could
        // land mid-pair.
        if (!compiler.OneByte && Flags.IsEitherUnicode()) return false;

        LoopChoiceNode? loop = AsLoopChoiceNode();
        if (loop is null || loop.ReadBackward) return false;

        // The body is alternative 0 (alternative 1 is the continuation). Counted
        // quantifiers carry guards that need per-iteration bookkeeping.
        GuardedAlternative bodyAlt = _alternatives[0];
        if (bodyAlt.Guards is not null && bodyAlt.Guards.Count != 0) return false;
        // Body must be a single character class.
        TextNode? body = bodyAlt.Node.AsTextNode();
        if (body is null || body.ReadBackward) return false;
        if (body.Elements.Count != 1) return false;
        TextElement el = body.Elements[0];
        if (el.Type != TextElement.TextType.CLASS_RANGES) return false;
        // The class must be the whole iteration.
        if (textLength != 1) return false;
        RegExpClassRanges cr = el.ClassRanges;

        // The loop ends on the first char the body does not match, so the exit set is
        // the complement of [B]'s match set.
        var exitSet = new List<CharacterRange>(2);
        exitSet.AddRange(cr.Ranges);
        CharacterRange.Canonicalize(exitSet);
        if (!cr.IsNegated)
        {
            var negated = new List<CharacterRange>(2);
            CharacterRange.Negate(exitSet, negated);
            exitSet = negated;
        }

        // Collect up to two exit chars within the subject's code-unit range.
        uint maxChar = MaxCodeUnit(compiler.OneByte);
        Span<uint> chars = stackalloc uint[2];
        int count = RegExpCompilerHelpers.CollectSmallCharSet(exitSet, maxChar, chars);
        if (count == RegExpCompilerHelpers.kCharSetTooLarge) return false;
        if (count == 0) return false;  // Body matches everything: never stops.

        const int kScanCpOffset = 0;
        const int kScanAdvanceBy = 1;
        const int kBoundsCheckOffset = 0;
        RegExpCompilerHelpers.EmitSkipUntilSmallCharSet(compiler.MacroAssembler, compiler.OneByte, kScanCpOffset,
            kScanAdvanceBy, chars, count, kBoundsCheckOffset, exit, exit);
        return true;
    }

    // Attempts to emit this choice as a masked-value dispatch instead of the
    // linear alternative chain in EmitChoices.  Eligible when every
    // alternative's first-load quick check rationalizes to the SAME mask over
    // the preloaded word: the masked input then selects the only group of
    // alternatives that can possibly match.
    //
    // Match priority is preserved: within a group, alternatives are tried in
    // their original order; across groups, the masked value is mutually
    // exclusive, so skipped alternatives could not have matched.
    EmitResult? TryEmitMaskedValueDispatch(RegExpCompiler compiler, AlternativeGenerationList altGens, Trace trace,
        ref PreloadState preload)
    {
        const int kMinAlternatives = 4;
        const int kMinGroups = 3;

        if (!RegExpCompiler.s_regexpMaskedDispatch) return null;
        if (AsLoopChoiceNode() is not null) return null;
        int choiceCount = _alternatives.Count;
        if (choiceCount < kMinAlternatives) return null;

        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        int preloadCharacters = preload.PreloadCharacters;
        if (preloadCharacters == 0) return null;
        if (preloadCharacters != 1 && !assembler.CanReadUnaligned()) return null;
        bool notAtStart = trace.AtStart == Trace.TriBool.FALSE_VALUE;

        // The bounds check below covers EatsAtLeast characters, so a failing
        // check rules out every alternative (see also Node::EmitQuickCheck).
        int eatsAtLeast = (int)EatsAtLeast(notAtStart);
        if (eatsAtLeast < preloadCharacters) return null;

        // Compute one rationalized (mask, value) per alternative and require a
        // common mask.
        uint commonMask = 0;
        var values = new uint[choiceCount];
        for (int i = 0; i < choiceCount; i++)
        {
            GuardedAlternative alternative = _alternatives[i];
            if (alternative.Guards is not null && alternative.Guards.Count != 0) return null;
            AlternativeGeneration altGen = altGens.At(i);
            altGen.QuickCheckDetails.Characters = preloadCharacters;
            QuickCheckDetails details = altGen.QuickCheckDetails;
            alternative.Node.GetQuickCheckDetails(details, compiler, 0, notAtStart, kRecursionBudget);
            if (details.CannotMatch()) return null;
            if (!details.Rationalize(compiler.OneByte)) return null;
            if (i == 0)
            {
                commonMask = details.Mask;
            }
            else if (details.Mask != commonMask)
            {
                return null;
            }
            values[i] = details.Value;
        }
        if (commonMask == 0) return null;

        // Group alternatives by value, preserving the order of first occurrence
        // (and, within a group, alternative order).
        var groupValues = new List<uint>(16);
        var groupOfAlt = new int[choiceCount];
        for (int i = 0; i < choiceCount; i++)
        {
            uint value = values[i];
            int groupIndex = groupValues.IndexOf(value);
            if (groupIndex < 0)
            {
                groupIndex = groupValues.Count;
                groupValues.Add(value);
            }
            groupOfAlt[i] = groupIndex;
        }
        int groupCount = groupValues.Count;
        if (groupCount < kMinGroups) return null;

        // Preload the word if the trace has not already; the bounds check covers
        // the minimum any alternative eats (mirrors Node::EmitQuickCheck).
        if (trace.CharactersPreloaded != preloadCharacters)
        {
            int cpOffset = trace.CpOffset;
            assembler.LoadCurrentCharacter(cpOffset, trace.Backtrack, !preload.PreloadHasCheckedBounds,
                preloadCharacters, assembler.CalculateBoundsCheckOffset(cpOffset, eatsAtLeast));
        }

        // The dispatch: one fused masked compare per group; no group matching
        // means no alternative can match.
        var groupLabels = new Label[groupCount];
        for (int g = 0; g < groupCount; g++) groupLabels[g] = new Label();
        // Bits the preload can produce (mirrors Node::EmitQuickCheck's masking).
        uint loadMask;
        if (preloadCharacters == 1)
        {
            loadMask = CharMask(compiler.OneByte);
        }
        else if (preloadCharacters == 2 && compiler.OneByte)
        {
            loadMask = 0xffff;
        }
        else
        {
            loadMask = 0xffffffff;
        }
        // The bits on which the group values differ select the group; the
        // remaining value bits are common to all groups.  When there are enough
        // groups and the differing bits span a small window, dispatch through a
        // jump table on that window instead of the compare chain.
        const int kMinGroupsForTableSwitch = 6;
        const int kMaxTableSwitchBits = 6;  // Up to 64 entries.
        uint valueDiff = 0;
        for (int g = 1; g < groupCount; g++) valueDiff |= groupValues[g] ^ groupValues[0];
        int tableShift = 0;
        int tableBits = 0;
        if (assembler.CanTableSwitchOnBits() && groupCount >= kMinGroupsForTableSwitch && valueDiff != 0)
        {
            tableShift = BitOperations.TrailingZeroCount(valueDiff);
            tableBits = 32 - BitOperations.LeadingZeroCount(valueDiff) - tableShift;
            if (tableBits > kMaxTableSwitchBits) tableBits = 0;
        }
        Label? tableLabel = tableBits > 0 ? new Label() : null;
        Label?[]? table = null;
        var noGroup = new Label();
        if (tableBits > 0)
        {
            int tableSize = 1 << tableBits;
            uint indexMask = (uint)tableSize - 1;
            uint spanMask = indexMask << tableShift;
            // known_mask/known_value hold the bits, and their values, that a prior
            // quick check on this path already proved.
            uint knownMask = 0;
            uint knownValue = 0;
            QuickCheckDetails prior = trace.QuickCheckPerformed;
            {
                uint charMask = CharMask(compiler.OneByte);
                int bitsPerChar = compiler.OneByte ? 8 : 16;
                for (int i = 0; i < prior.Characters && i < preloadCharacters; i++)
                {
                    ref QuickCheckDetails.Position pos = ref prior.Positions(i);
                    knownMask |= (pos.Mask & charMask) << (i * bitsPerChar);
                    knownValue |= (pos.Value & charMask) << (i * bitsPerChar);
                }
            }
            // Constrained bits outside the index window need a separate check.
            uint preMask = commonMask & ~spanMask;
            preMask &= ~(knownMask & ~(knownValue ^ groupValues[0]));
            if (preMask != 0) assembler.CheckNotCharacterAfterAnd(groupValues[0] & preMask, preMask, noGroup);
            // Within the window, the masked bits select the group; the remaining
            // (free) bits are unconstrained.
            uint maskedIndexBits = (commonMask >> tableShift) & indexMask;
            uint freeBits = indexMask & ~maskedIndexBits;
            table = new Label?[tableSize];
            for (int idx = 0; idx < tableSize; idx++) table[idx] = noGroup;
            for (int g = 0; g < groupCount; g++)
            {
                uint groupBits = (groupValues[g] >> tableShift) & maskedIndexBits;
                // Enumerate the submasks of free_bits (including 0).
                for (uint sub = freeBits;; sub = (sub - 1) & freeBits)
                {
                    Debug.Assert(table[groupBits | sub] == noGroup);
                    table[groupBits | sub] = groupLabels[g];
                    if (sub == 0) break;
                }
            }
            assembler.TableSwitchOnBits(tableShift, tableSize, tableLabel!);
            assembler.BindJumpTarget(noGroup);
        }
        else
        {
            // If the mask already covers every bit the load can produce, the AND is
            // a no-op and a plain character compare suffices.
            bool needMask = (commonMask & loadMask) != loadMask;
            for (int g = 0; g < groupCount; g++)
            {
                if (needMask)
                {
                    assembler.CheckCharacterAfterAnd(groupValues[g], commonMask, groupLabels[g]);
                }
                else
                {
                    assembler.CheckCharacter(groupValues[g], groupLabels[g]);
                }
            }
        }
        // A null backtrack target means Backtrack; GoTo handles that.
        assembler.GoTo(trace.Backtrack);

        // Group bodies: full checks in original alternative order, chained
        // within the group.
        int newFlushBudget = trace.FlushBudget / choiceCount;
        var chainLabels = new Label[choiceCount];
        for (int i = 0; i < choiceCount; i++) chainLabels[i] = new Label();
        for (int g = 0; g < groupCount; g++)
        {
            if (tableBits > 0)
            {
                assembler.BindJumpTarget(groupLabels[g]);
            }
            else
            {
                assembler.Bind(groupLabels[g]);
            }
            int lastInGroup = -1;
            for (int i = choiceCount - 1; i >= 0; i--)
            {
                if (groupOfAlt[i] == g)
                {
                    lastInGroup = i;
                    break;
                }
            }
            for (int i = 0; i < choiceCount; i++)
            {
                if (groupOfAlt[i] != g) continue;
                var newTrace = new Trace(trace);
                newTrace.CharactersPreloaded = preloadCharacters;
                newTrace.BoundCheckedUpTo = preloadCharacters;
                // The dispatch compare already established this alternative's
                // rationalized quick check, so its body need not repeat it.
                newTrace.SetQuickCheckPerformed(altGens.At(i).QuickCheckDetails);
                if (NotAtStart) newTrace.AtStart = Trace.TriBool.FALSE_VALUE;
                if (i != lastInGroup) newTrace.SetBacktrack(chainLabels[i]);
                if (newTrace.HasAnyActions) newTrace.FlushBudget = newFlushBudget;
                EmitResult result = _alternatives[i].Node.Emit(compiler, newTrace);
                if (result.IsError) return result;
                if (i != lastInGroup)
                {
                    assembler.Bind(chainLabels[i]);
                    // The intra-group retry re-reads the preloaded word, which the
                    // failed alternative may have invalidated via nested emission;
                    // reload for the next alternative's elided checks.
                    assembler.LoadCurrentCharacter(trace.CpOffset, null, false, preloadCharacters);
                }
            }
        }

        // All dispatch targets are bound now; emit the table data.
        if (tableBits > 0) assembler.EmitTableSwitchTable(tableLabel!, table!);

        preload.PreloadIsCurrent = false;
        return EmitResult.Success();
    }

    EmitResult EmitChoices(RegExpCompiler compiler, AlternativeGenerationList altGens, int firstChoice, Trace trace,
        ref PreloadState preload, ParkedGrant bodyParkedGrant)
    {
        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        SetUpPreLoad(compiler, trace, ref preload);

        if (firstChoice == 0)
        {
            EmitResult? dispatched = TryEmitMaskedValueDispatch(compiler, altGens, trace, ref preload);
            if (dispatched is not null) return dispatched.Value;
        }

        // For now we just call all choices one after the other.  The idea ultimately
        // is to use the Dispatch table to try only the relevant ones.
        int choiceCount = _alternatives.Count;

        int newFlushBudget = trace.FlushBudget / choiceCount;

        bool quickCheckFlags = RegExpCompiler.s_regexpOptimization && RegExpCompiler.s_regexpQuickCheck;

        // Landing stub for parked loop-exit backtracks from the body's inline
        // emission; see its use below and the binding after the loop.
        var parkedReentry = new Label();

        // An inherited parked-position grant may flow into an alternative only if no
        // sibling can match at a position the park skips (/:|\w*\s/ on "c:" must
        // still match ":" at index 1).
        int floatingAlternatives = 0;
        int lastFloating = -1;
        if (trace.ParkedGrant != ParkedGrant.kNone)
        {
            for (int i = 0; i < choiceCount; i++)
            {
                if (RegExpCompilerHelpers.AlternativeMatchesOnlyAtStart(_alternatives[i].Node)) continue;
                floatingAlternatives++;
                lastFloating = i;
            }
        }

        for (int i = firstChoice; i < choiceCount; i++)
        {
            bool isLast = i == choiceCount - 1;
            bool fallThroughOnFailure = !isLast;
            GuardedAlternative alternative = _alternatives[i];
            AlternativeGeneration altGen = altGens.At(i);
            altGen.QuickCheckDetails.Characters = preload.PreloadCharacters;
            List<Guard>? guards = alternative.Guards;
            int guardCount = guards?.Count ?? 0;
            var newTrace = new Trace(trace);
            bool siblingsAllAnchored = floatingAlternatives == 0 || (floatingAlternatives == 1 && lastFloating == i);
            if (!siblingsAllAnchored) newTrace.ResetParkedGrant();
            newTrace.CharactersPreloaded = preload.PreloadIsCurrent ? preload.PreloadCharacters : 0;
            if (preload.PreloadHasCheckedBounds) newTrace.BoundCheckedUpTo = preload.PreloadCharacters;
            newTrace.QuickCheckPerformed.Clear();
            if (NotAtStart) newTrace.AtStart = Trace.TriBool.FALSE_VALUE;
            if (!isLast)
            {
                // Parked loop-exit backtracks arrive with the position moved,
                // invalidating the preload register and bounds guarantee the next
                // alternative may assume; route them through a checked reload.
                if (bodyParkedGrant != ParkedGrant.kNone)
                {
                    // The grant is only ever set on the two-alternative search loop, so
                    // the granted alternative is the body at index 0.
                    Debug.Assert(i == 0);
                    newTrace.SetBacktrack(parkedReentry);
                    newTrace.SetParkedGrant(bodyParkedGrant);
                }
                else
                {
                    newTrace.SetBacktrack(altGen.After);
                }
            }
            altGen.ExpectsPreload = preload.PreloadIsCurrent;
            bool generateFullCheckInline = false;
            if (quickCheckFlags && TryToEmitQuickCheckForAlternative(i == 0) &&
                alternative.Node.EmitQuickCheck(compiler, trace, newTrace, preload.PreloadHasCheckedBounds,
                    altGen.PossibleSuccess, altGen.QuickCheckDetails, fallThroughOnFailure, this))
            {
                // Quick check was generated for this choice.
                preload.PreloadIsCurrent = true;
                preload.PreloadHasCheckedBounds = true;
                // If we generated the quick check to fall through on possible success,
                // we now need to generate the full check inline.
                if (!fallThroughOnFailure)
                {
                    macroAssembler.Bind(altGen.PossibleSuccess);
                    newTrace.SetQuickCheckPerformed(altGen.QuickCheckDetails);
                    newTrace.CharactersPreloaded = preload.PreloadCharacters;
                    newTrace.BoundCheckedUpTo = preload.PreloadCharacters;
                    generateFullCheckInline = true;
                }
            }
            else if (altGen.QuickCheckDetails.CannotMatch())
            {
                if (!fallThroughOnFailure) macroAssembler.GoTo(trace.Backtrack);
                continue;
            }
            else
            {
                // No quick check was generated.  Put the full code here.
                // If this is not the first choice then there could be slow checks from
                // previous cases that go here when they fail.  There's no reason to
                // insist that they preload characters since the slow check we are about
                // to generate probably can't use it.
                if (i != firstChoice)
                {
                    altGen.ExpectsPreload = false;
                    newTrace.InvalidateCurrentCharacter();
                }
                generateFullCheckInline = true;
            }
            if (generateFullCheckInline)
            {
                if (newTrace.HasAnyActions) newTrace.FlushBudget = newFlushBudget;
                for (int j = 0; j < guardCount; j++) GenerateGuard(macroAssembler, guards![j], newTrace);
                EmitResult r = alternative.Node.Emit(compiler, newTrace);
                if (r.IsError) return r;
                preload.PreloadIsCurrent = false;
            }
            macroAssembler.Bind(altGen.After);
        }

        if (parkedReentry.IsLinked)
        {
            // Re-establish the preload register and bounds guarantee with a checked
            // reload before re-entering the advance-and-retry alternative (a parked
            // position can be as far as the subject end).
            macroAssembler.Bind(parkedReentry);
            if (preload.PreloadCharacters > 0)
            {
                var outOfInput = new Label();
                macroAssembler.LoadCurrentCharacter(trace.CpOffset, outOfInput, true, preload.PreloadCharacters);
                macroAssembler.GoTo(altGens.At(0).After);
                macroAssembler.Bind(outOfInput);
                macroAssembler.GoTo(trace.Backtrack);  // Backtracks if null.
            }
            else
            {
                macroAssembler.GoTo(altGens.At(0).After);
            }
        }

        return EmitResult.Success();
    }

    EmitResult EmitOutOfLineContinuation(RegExpCompiler compiler, Trace trace, GuardedAlternative alternative,
        AlternativeGeneration altGen, int preloadCharacters, bool nextExpectsPreload, ParkedGrant parkedGrant)
    {
        if (!altGen.PossibleSuccess.IsLinked) return EmitResult.Success();

        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        macroAssembler.Bind(altGen.PossibleSuccess);
        var outOfLineTrace = new Trace(trace);
        outOfLineTrace.CharactersPreloaded = preloadCharacters;
        outOfLineTrace.SetQuickCheckPerformed(altGen.QuickCheckDetails);
        if (NotAtStart) outOfLineTrace.AtStart = Trace.TriBool.FALSE_VALUE;
        List<Guard>? guards = alternative.Guards;
        int guardCount = guards?.Count ?? 0;
        var reloadCurrentChar = new Label();
        var parkedLanding = new Label();
        if (parkedGrant != ParkedGrant.kNone)
        {
            // Parked loop-exit backtracks arrive with the position moved -- up to and
            // including the subject end.  That invalidates the preload register and
            // bounds guarantee the next alternative may assume; route everything
            // through a checked landing stub.
            outOfLineTrace.SetBacktrack(parkedLanding);
            outOfLineTrace.SetParkedGrant(parkedGrant);
        }
        else if (nextExpectsPreload)
        {
            // reload_current_char is a pure forwarder to alt_gen->after: it reloads
            // relative to the current position.
            outOfLineTrace.SetBacktrack(reloadCurrentChar);
        }
        else
        {
            outOfLineTrace.SetBacktrack(altGen.After);
        }
        for (int j = 0; j < guardCount; j++) GenerateGuard(macroAssembler, guards![j], outOfLineTrace);
        EmitResult r = alternative.Node.Emit(compiler, outOfLineTrace);
        if (r.IsError) return r;
        if (parkedGrant != ParkedGrant.kNone)
        {
            macroAssembler.Bind(parkedLanding);
            if (preloadCharacters > 0)
            {
                // Checked reload; running out of input here ends the search.
                var outOfInput = new Label();
                macroAssembler.LoadCurrentCharacter(trace.CpOffset, outOfInput, true, preloadCharacters);
                macroAssembler.GoTo(altGen.After);
                macroAssembler.Bind(outOfInput);
                macroAssembler.GoTo(trace.Backtrack);  // Backtracks if null.
            }
            else
            {
                // Preload width zero: there is no preload contract to re-establish,
                // and the next alternative checks its own bounds.
                macroAssembler.GoTo(altGen.After);
            }
        }
        else if (nextExpectsPreload)
        {
            macroAssembler.Bind(reloadCurrentChar);
            // Reload the current character, since the next quick check expects
            // that.  We don't need to check bounds here because we only get into
            // this code through a quick check which already did the checked load.
            macroAssembler.LoadCurrentCharacter(trace.CpOffset, null, false, preloadCharacters);
            macroAssembler.GoTo(altGen.After);
        }
        return EmitResult.Success();
    }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        List<GuardedAlternative> alts = _alternatives;
        budget = (budget - 1) / alts.Count;
        for (int i = 0; i < alts.Count; i++)
        {
            GuardedAlternative alt = alts[i];
            if (alt.Guards is not null && alt.Guards.Count != 0)
            {
                bm.SetRest(offset);  // Give up trying to fill in info.
                SaveBMInfo(bm, notAtStart, offset);
                return;
            }
            alt.Node.FillInBMInfo(offset, budget, bm, notAtStart);
        }
        SaveBMInfo(bm, notAtStart, offset);
    }
}

public sealed partial class ActionNode
{
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        LimitResult limitResult = LimitVersions(compiler, trace);
        if (limitResult == LimitResult.DONE) return EmitResult.Success();
        Debug.Assert(limitResult == LimitResult.CONTINUE);

        using var rc = new RecursionCheck(compiler);

        switch (_actionType)
        {
            // Start with the actions we know how to defer. These are just recorded in
            // the new trace, no code is emitted right now.  (If we backtrack then we
            // don't have to perform and undo these actions.)
            case ActionType.STORE_POSITION:
            case ActionType.RESTORE_POSITION:
            case ActionType.INCREMENT_REGISTER:
            case ActionType.SET_REGISTER_FOR_LOOP:
            case ActionType.CLEAR_CAPTURES:
            {
                var newTrace = new Trace(trace);
                newTrace.AddAction(this);
                EmitResult r = OnSuccess.Emit(compiler, newTrace);
                if (r.IsError) return r;
                break;
            }
            case ActionType.EATS_AT_LEAST:
            {
                EmitResult r = OnSuccess.Emit(compiler, trace);
                if (r.IsError) return r;
                break;  // Doesn't actually do anything.
            }
            // We don't yet have the ability to defer these.
            case ActionType.BEGIN_POSITIVE_SUBMATCH:
            case ActionType.BEGIN_NEGATIVE_SUBMATCH:
                if (!trace.IsTrivial)
                {
                    // Complex situation: Flush the trace state to the assembler and
                    // generate a generic version of this action.  This call will
                    // recurse back to the else clause here.
                    trace.Flush(compiler, this);
                }
                else
                {
                    assembler.WriteCurrentPositionToRegister(_currentPositionRegister, 0);
                    assembler.WriteStackPointerToRegister(_stackPointerRegister);
                    EmitResult r = OnSuccess.Emit(compiler, trace);
                    if (r.IsError) return r;
                }
                break;
            case ActionType.EMPTY_MATCH_CHECK:
            {
                int startPosReg = _startRegister;
                int repReg = _repetitionRegister;
                bool hasMinimum = repReg != RegExpCompiler.kNoRegister;
                bool knowDist = trace.GetStoredPosition(startPosReg, out int storedPos);
                if (knowDist && !hasMinimum && storedPos == trace.CpOffset)
                {
                    // If we know we haven't advanced and there is no minimum we
                    // can just backtrack immediately.
                    assembler.GoTo(trace.Backtrack);
                }
                else if (knowDist && storedPos < trace.CpOffset)
                {
                    // If we know we've advanced we can generate the continuation
                    // immediately.
                    EmitResult r = OnSuccess.Emit(compiler, trace);
                    if (r.IsError) return r;
                }
                else if (!trace.IsTrivial)
                {
                    trace.Flush(compiler, this);
                }
                else
                {
                    var skipEmptyCheck = new Label();
                    // If we have a minimum number of repetitions we check the current
                    // number first and skip the empty check if it's not enough.
                    if (hasMinimum)
                    {
                        int limit = _repetitionLimit;
                        assembler.IfRegisterLT(repReg, limit, skipEmptyCheck);
                    }
                    // If the match is empty we bail out, otherwise we fall through
                    // to the on-success continuation.
                    assembler.IfRegisterEqPos(startPosReg, trace.Backtrack);
                    assembler.Bind(skipEmptyCheck);
                    EmitResult r = OnSuccess.Emit(compiler, trace);
                    if (r.IsError) return r;
                }
                break;
            }
            case ActionType.POSITIVE_SUBMATCH_SUCCESS:
            {
                if (!trace.IsTrivial) return trace.Flush(compiler, this, Trace.FlushMode.kFlushSuccess);
                assembler.ReadCurrentPositionFromRegister(_currentPositionRegister);
                assembler.ReadStackPointerFromRegister(_stackPointerRegister);
                int clearRegisterCount = _clearRegisterCount;
                if (clearRegisterCount == 0) return OnSuccess.Emit(compiler, trace);
                int clearRegistersFrom = _clearRegisterFrom;
                var clearRegistersBacktrack = new Label();
                var newTrace = new Trace(trace);
                newTrace.SetBacktrack(clearRegistersBacktrack);
                EmitResult r = OnSuccess.Emit(compiler, newTrace);
                if (r.IsError) return r;

                assembler.Bind(clearRegistersBacktrack);
                int clearRegistersTo = clearRegistersFrom + clearRegisterCount - 1;
                assembler.ClearRegisters(clearRegistersFrom, clearRegistersTo);

                Debug.Assert(trace.Backtrack is null);
                assembler.Backtrack();
                return EmitResult.Success();
            }
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
        return EmitResult.Success();
    }
}

public sealed partial class UnanchoredAdvanceNode
{
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        if (!trace.IsTrivial) return trace.Flush(compiler, this);
        assembler.UnanchoredAdvance(Flags.IsEitherUnicode(), trace.Backtrack);

        var successorTrace = new Trace(trace);
        successorTrace.InvalidateCurrentCharacter();
        successorTrace.AtStart = Trace.TriBool.FALSE_VALUE;

        return OnSuccess.Emit(compiler, successorTrace);
    }

    // UnanchoredAdvance dynamically shifts position, so we do not propagate
    // details through it.
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
    }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
    }
}

public sealed partial class BackReferenceNode
{
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        if (!trace.IsTrivial) return trace.Flush(compiler, this);

        LimitResult limitResult = LimitVersions(compiler, trace);
        if (limitResult == LimitResult.DONE) return EmitResult.Success();
        Debug.Assert(limitResult == LimitResult.CONTINUE);

        using var rc = new RecursionCheck(compiler);

        Debug.Assert(StartRegister + 1 == EndRegister);
        if (Flags.IsIgnoreCase())
        {
            bool unicode = Flags.IsEitherUnicode();
            assembler.CheckNotBackReferenceIgnoreCase(StartRegister, ReadBackward, unicode, trace.Backtrack);
        }
        else
        {
            assembler.CheckNotBackReference(StartRegister, ReadBackward, trace.Backtrack);
        }
        // We are going to advance backward, so we may end up at the start.
        if (ReadBackward) trace.AtStart = Trace.TriBool.UNKNOWN;

        // Check that the back reference does not end inside a surrogate pair.
        if (Flags.IsEitherUnicode() && !compiler.OneByte)
        {
            assembler.CheckNotInSurrogatePair(trace.CpOffset, trace.Backtrack);
        }
        return OnSuccess.Emit(compiler, trace);
    }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        // Working out the set of characters that a backreference can match is too
        // hard, so we just say that any character can match.
        bm.SetRest(offset);
        SaveBMInfo(bm, notAtStart, offset);
    }
}

public sealed partial class TextNode
{
    public void CalculateOffsets()
    {
        int elementCount = _elms.Count;
        // Set up the offsets of the elements relative to the start.  This is a fixed
        // quantity since a TextNode can only contain fixed-width things.
        int cpOffset = 0;
        Span<TextElement> elms = CollectionsMarshal.AsSpan(_elms);
        for (int i = 0; i < elementCount; i++)
        {
            ref TextElement elm = ref elms[i];
            elm.CpOffset = cpOffset;
            cpOffset += elm.Length;
        }
    }

    public override void FillInBMInfo(int initialOffset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        if (initialOffset >= bm.Length) return;
        if (_readBackward) return;
        int offset = initialOffset;
        int maxChar = bm.MaxChar;
        Span<int> chars = stackalloc int[4];
        for (int i = 0; i < _elms.Count; i++)
        {
            if (offset >= bm.Length)
            {
                if (initialOffset == 0) SetBmInfo(notAtStart, bm);
                return;
            }
            TextElement text = _elms[i];
            if (text.Type == TextElement.TextType.ATOM)
            {
                string atom = text.Atom.Data;
                for (int j = 0; j < atom.Length; j++, offset++)
                {
                    if (offset >= bm.Length)
                    {
                        if (initialOffset == 0) SetBmInfo(notAtStart, bm);
                        return;
                    }
                    char character = atom[j];
                    if (Flags.IsIgnoreCase())
                    {
                        int length = GetCaseIndependentLetters(bm.Compiler, character, chars);
                        for (int k = 0; k < length; k++) bm.Set(offset, chars[k]);
                    }
                    else
                    {
                        if (character <= maxChar) bm.Set(offset, character);
                    }
                }
            }
            else
            {
                RegExpClassRanges classRanges = text.ClassRanges;
                List<CharacterRange> ranges = classRanges.Ranges;
                if (classRanges.IsNegated)
                {
                    bm.SetAll(offset);
                }
                else
                {
                    for (int k = 0; k < ranges.Count; k++)
                    {
                        CharacterRange range = ranges[k];
                        if (range.From > maxChar) continue;
                        int to = Math.Min(maxChar, range.To);
                        bm.SetInterval(offset, new Interval(range.From, to));
                    }
                }
                offset++;
            }
        }
        if (offset >= bm.Length)
        {
            if (initialOffset == 0) SetBmInfo(notAtStart, bm);
            return;
        }
        OnSuccess.FillInBMInfo(offset, budget - 1, bm, true);  // Not at start after a text node.
        if (initialOffset == 0) SetBmInfo(notAtStart, bm);
    }
}

// -------------------------------------------------------------------
// Analysis

// Iterates the node graph and provides the opportunity for propagators to set
// values that depend on successor nodes. V8 instantiates
// Analysis<AssertionPropagator, EatsAtLeastPropagator>; both are applied here
// in that order at each visit.
internal sealed class Analysis(bool isOneByte) : INodeVisitor
{
    readonly bool _isOneByte = isOneByte;
    RegExpError _error = RegExpError.None;

    public void EnsureAnalyzed(RegExpNode that)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Fail(RegExpError.AnalysisStackOverflow);
            return;
        }
        ref NodeInfo info = ref that.Info;
        if (info.BeenAnalyzed || info.BeingAnalyzed) return;
        info.BeingAnalyzed = true;
        that.Accept(this);
        ref NodeInfo info2 = ref that.Info;
        info2.BeingAnalyzed = false;
        info2.BeenAnalyzed = true;
    }

    public bool HasFailed => _error != RegExpError.None;
    public RegExpError Error
    {
        get
        {
            Debug.Assert(_error != RegExpError.None);
            return _error;
        }
    }
    public void Fail(RegExpError error) => _error = error;

    public void VisitEnd(EndNode that)
    {
        // nothing to do
    }

    public void VisitText(TextNode that)
    {
        that.MakeCaseIndependent(_isOneByte);
        EnsureAnalyzed(that.OnSuccess);
        if (HasFailed) return;
        that.CalculateOffsets();
        // AssertionPropagator::VisitText: nothing.
        // EatsAtLeastPropagator::VisitText.
        // The eats_at_least value is not used if reading backward.
        if (!that.ReadBackward)
        {
            // We are not at the start after this node, and thus we can use the
            // successor's from_not_start value.
            byte eatsAtLeast = SaturatedByte(that.Length() + that.OnSuccess.EatsAtLeastInfo.FromNotStart);
            that.EatsAtLeastInfo = new EatsAtLeastInfo(eatsAtLeast);
        }
    }

    public void VisitAction(ActionNode that)
    {
        EnsureAnalyzed(that.OnSuccess);
        if (HasFailed) return;
        // AssertionPropagator: if the next node is interested in what it follows
        // then this node has to be interested too so it can pass the information on.
        that.Info.AddFromFollowing(that.OnSuccess.Info);
        // EatsAtLeastPropagator.
        switch (that.Type)
        {
            case ActionNode.ActionType.BEGIN_POSITIVE_SUBMATCH:
                // For a begin positive submatch we propagate the eats_at_least
                // data from the successor of the success node, ignoring the body of
                // the lookahead, which eats nothing, since it is a zero-width
                // assertion.
                that.EatsAtLeastInfo = that.SuccessNode.OnSuccess.EatsAtLeastInfo;
                break;
            case ActionNode.ActionType.POSITIVE_SUBMATCH_SUCCESS:
                // We do not propagate eats_at_least data through positive submatch
                // success because it rewinds input.
                Debug.Assert(that.EatsAtLeastInfo.IsZero);
                break;
            case ActionNode.ActionType.EATS_AT_LEAST:
            {
                EatsAtLeastInfo eats = that.OnSuccess.EatsAtLeastInfo;
                eats.SetMax(that.StoredEatsAtLeast);
                that.EatsAtLeastInfo = eats;
                break;
            }
            default:
                // Otherwise, the current node eats at least as much as its successor.
                that.EatsAtLeastInfo = that.OnSuccess.EatsAtLeastInfo;
                break;
        }
    }

    public void VisitUnanchoredAdvance(UnanchoredAdvanceNode that)
    {
        EnsureAnalyzed(that.OnSuccess);
        if (HasFailed) return;
        that.Info.AddFromFollowing(that.OnSuccess.Info);
        byte eatsAtLeast = SaturatedByte(1 + that.OnSuccess.EatsAtLeastInfo.FromNotStart);
        that.EatsAtLeastInfo = new EatsAtLeastInfo(eatsAtLeast);
    }

    static void PropagateChoice(ChoiceNode that, int i)
    {
        RegExpNode node = that.Alternatives[i].Node;
        // AssertionPropagator::VisitChoice.
        that.Info.AddFromFollowing(node.Info);
        // EatsAtLeastPropagator::VisitChoice: the minimum possible match from a
        // choice node is the minimum of its successors.
        EatsAtLeastInfo eatsAtLeast = i == 0 ? new EatsAtLeastInfo(byte.MaxValue) : that.EatsAtLeastInfo;
        eatsAtLeast.SetMin(node.EatsAtLeastInfo);
        that.EatsAtLeastInfo = eatsAtLeast;
    }

    public void VisitChoice(ChoiceNode that)
    {
        for (int i = 0; i < that.Alternatives.Count; i++)
        {
            EnsureAnalyzed(that.Alternatives[i].Node);
            if (HasFailed) return;
            PropagateChoice(that, i);
        }
    }

    public void VisitLoopChoice(LoopChoiceNode that)
    {
        Debug.Assert(that.Alternatives.Count == 2);  // Just loop and continue.

        // First propagate all information from the continuation node.
        EnsureAnalyzed(that.ContinueNode!);
        if (HasFailed) return;
        that.Info.AddFromFollowing(that.ContinueNode!.Info);
        if (!that.ReadBackward) that.EatsAtLeastInfo = that.ContinueNode.EatsAtLeastInfo;

        // Check the loop last since it may need the value of this node
        // to get a correct result.
        EnsureAnalyzed(that.LoopNode!);
        if (HasFailed) return;
        that.Info.AddFromFollowing(that.LoopNode!.Info);
        // EatsAtLeastPropagator::VisitLoopChoiceLoopNode: nothing.
    }

    public void VisitNegativeLookaroundChoice(NegativeLookaroundChoiceNode that)
    {
        Debug.Assert(that.Alternatives.Count == 2);  // Lookaround and continue.

        EnsureAnalyzed(that.LookaroundNode);
        if (HasFailed) return;
        that.Info.AddFromFollowing(that.Alternatives[NegativeLookaroundChoiceNode.kLookaroundIndex].Node.Info);
        // EatsAtLeastPropagator::VisitNegativeLookaroundChoiceLookaroundNode: nothing.

        EnsureAnalyzed(that.ContinueNode);
        if (HasFailed) return;
        that.Info.AddFromFollowing(that.Alternatives[NegativeLookaroundChoiceNode.kContinueIndex].Node.Info);
        that.EatsAtLeastInfo = that.ContinueNode.EatsAtLeastInfo;
    }

    public void VisitBackReference(BackReferenceNode that)
    {
        EnsureAnalyzed(that.OnSuccess);
        if (HasFailed) return;
        if (!that.ReadBackward) that.EatsAtLeastInfo = that.OnSuccess.EatsAtLeastInfo;
    }

    public void VisitAssertion(AssertionNode that)
    {
        EnsureAnalyzed(that.OnSuccess);
        if (HasFailed) return;
        EatsAtLeastInfo eatsAtLeast = that.OnSuccess.EatsAtLeastInfo;
        if (that.Type == AssertionNode.AssertionType.AT_START)
        {
            // If we know we are not at the start and we are asked "how many
            // characters will you match if you succeed?" then we can answer anything
            // since false implies false.  So let's just set the max answer
            // (UINT8_MAX) since that won't prevent us from preloading a lot of
            // characters for the other branches in the node graph.
            eatsAtLeast.FromNotStart = byte.MaxValue;
        }
        that.EatsAtLeastInfo = eatsAtLeast;
    }

    static byte SaturatedByte(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);

    public static RegExpError AnalyzeRegExp(bool isOneByte, RegExpNode node)
    {
        var analysis = new Analysis(isOneByte);
        Debug.Assert(!node.Info.BeenAnalyzed);
        analysis.EnsureAnalyzed(node);
        return analysis.HasFailed ? analysis.Error : RegExpError.None;
    }
}

public sealed partial class RegExpCompiler
{
    public RegExpNode OptionallyStepBackToLeadSurrogate(RegExpNode onSuccess)
    {
        Debug.Assert(!ReadBackward);
        List<CharacterRange> leadSurrogates = CharacterRange.List(
            CharacterRange.Range(RegExpMacroAssembler.kLeadSurrogateStart, RegExpMacroAssembler.kLeadSurrogateEnd));
        List<CharacterRange> trailSurrogates = CharacterRange.List(
            CharacterRange.Range(RegExpMacroAssembler.kTrailSurrogateStart, RegExpMacroAssembler.kTrailSurrogateEnd));

        var optionalStepBack = new ChoiceNode(2, Flags);

        int stackRegister = UnicodeLookaroundStackRegister();
        int positionRegister = UnicodeLookaroundPositionRegister();
        RegExpNode stepBack = TextNode.CreateForCharacterRanges(leadSurrogates, true, onSuccess, Flags);
        var builder = new RegExpLookaround.Builder(true, stepBack, this, stackRegister, positionRegister);
        RegExpNode matchTrail = TextNode.CreateForCharacterRanges(trailSurrogates, false, builder.OnMatchSuccess,
            Flags);

        optionalStepBack.AddAlternative(new GuardedAlternative(builder.ForMatch(this, matchTrail)));
        optionalStepBack.AddAlternative(new GuardedAlternative(onSuccess));

        return optionalStepBack;
    }

    public RegExpNode PreprocessRegExp(RegExpCompileData data, bool isOneByte)
    {
        // Wrap the body of the regexp in capture #0.
        RegExpNode capturedBody = RegExpCapture.ToNode(data.Tree!, 0, this, Accept);
        RegExpNode node = capturedBody;
        if (!data.Tree!.IsCertainlyAnchoredAtStart(RegExpNode.kRecursionBudget) && !Flags.IsSticky())
        {
            // Add a .*? at the beginning, outside the body capture, unless
            // this expression is anchored at the beginning or sticky.
            _hasSearchPrefix = true;
            RegExpNode loopNode = RegExpQuantifier.ToNode(0, RegExpTree.kInfinity, false,
                new RegExpClassRanges(StandardCharacterSet.kEverything), this, capturedBody, data.ContainsAnchor);

            if (data.ContainsAnchor)
            {
                // Unroll loop once, to take care of the case that might start
                // at the start of input.
                var firstStepNode = new ChoiceNode(2, Flags);
                firstStepNode.AddAlternative(new GuardedAlternative(capturedBody));
                firstStepNode.AddAlternative(new GuardedAlternative(
                    new TextNode(new RegExpClassRanges(StandardCharacterSet.kEverything), false, loopNode, Flags)));
                node = firstStepNode;
            }
            else
            {
                node = loopNode;
            }
        }
        if (!isOneByte && Flags.IsEitherUnicode() && (Flags.IsGlobal() || Flags.IsSticky()))
        {
            node = OptionallyStepBackToLeadSurrogate(node);
        }

        // We can run out of registers during preprocessing, or we can recurse too
        // deep during ToNode. Indicate an error in either case.
        if (_regExpTooBig) data.Error = RegExpError.TooLarge;
        return node;
    }
}
