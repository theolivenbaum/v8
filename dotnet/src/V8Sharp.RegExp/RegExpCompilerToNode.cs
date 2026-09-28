// Port of src/regexp/regexp-compiler-tonode.cc: tree to node graph
// conversion, and the CharacterRange set operations.

using V8Sharp.RegExp.Unicode;
using static V8Sharp.RegExp.CompilerConstants;

namespace V8Sharp.RegExp;

public abstract partial class RegExpTree
{
    public RegExpNode ToNode(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        // We try to remove entire subbranches of the node structure that can't
        // succeed by returning backtrack nodes instead of nodes that first match
        // something and then inevitably backtrack.
        if (onSuccess.IsBacktrack()) return onSuccess;
        compiler.ToNodeMaybeCheckForStackOverflow();
        if (compiler.IsRegExpTooBig)
        {
            // We can always return this even though it may not be the expected
            // subclass because all call sites already have to check for this case.
            return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
        }
        return ToNodeImpl(compiler, onSuccess);
    }

    protected abstract RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess);
}

public sealed partial class RegExpAtom
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        var elms = new List<TextElement>(1) { TextElement.FromAtom(this) };
        var result = new TextNode(elms, compiler.ReadBackward, onSuccess, compiler.Flags);
        if (compiler.OneByte && !result.CanMatchLatin1(compiler))
        {
            return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
        }
        return result;
    }
}

public sealed partial class RegExpText
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        var result = new TextNode(Elements, compiler.ReadBackward, onSuccess, compiler.Flags);
        if (compiler.OneByte && !result.CanMatchLatin1(compiler))
        {
            return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
        }
        return result;
    }
}

public sealed partial class RegExpClassRanges
{
    static bool CompareInverseRanges(List<CharacterRange> ranges, int[] specialClass)
    {
        int length = specialClass.Length - 1;  // Remove final marker.
        Debug.Assert(specialClass[length] == kRangeEndMarker);
        Debug.Assert(ranges.Count != 0);

        if (ranges.Count != (length >> 1) + 1) return false;

        CharacterRange range = ranges[0];
        if (range.From != 0) return false;

        for (int i = 0; i < length; i += 2)
        {
            if (specialClass[i] != range.To + 1) return false;
            range = ranges[(i >> 1) + 1];
            if (specialClass[i + 1] != range.From) return false;
        }

        return range.To == CharacterRange.kMaxCodePoint;
    }

    static bool CompareRanges(List<CharacterRange> ranges, int[] specialClass)
    {
        int length = specialClass.Length - 1;  // Remove final marker.
        Debug.Assert(specialClass[length] == kRangeEndMarker);
        if (ranges.Count * 2 != length) return false;

        for (int i = 0; i < length; i += 2)
        {
            CharacterRange range = ranges[i >> 1];
            if (range.From != specialClass[i] || range.To != specialClass[i + 1] - 1) return false;
        }
        return true;
    }

    public bool IsStandard()
    {
        // TODO(lrn): Remove need for this function, by not throwing away information
        // along the way.
        if (IsNegated) return false;
        if (_set.IsStandard) return true;
        if (CompareRanges(_set.Ranges, kSpaceRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kWhitespace;
            return true;
        }
        if (CompareInverseRanges(_set.Ranges, kSpaceRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kNotWhitespace;
            return true;
        }
        if (CompareInverseRanges(_set.Ranges, kLineTerminatorRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kNotLineTerminator;
            return true;
        }
        if (CompareRanges(_set.Ranges, kLineTerminatorRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kLineTerminator;
            return true;
        }
        if (CompareRanges(_set.Ranges, kWordRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kWord;
            return true;
        }
        if (CompareInverseRanges(_set.Ranges, kWordRanges))
        {
            _set.StandardSetType = StandardCharacterSet.kNotWord;
            return true;
        }
        return false;
    }

    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        _set.Canonicalize();
        List<CharacterRange> ranges = Ranges;

        bool needsCaseFolding = NeedsUnicodeCaseEquivalents(compiler.Flags) && !NoCaseFoldingNeeded;
        if (needsCaseFolding) CharacterRange.AddUnicodeCaseEquivalents(ranges);

        if (!compiler.Flags.IsEitherUnicode() || compiler.OneByte || ContainsSplitSurrogate)
        {
            var result = new TextNode(this, compiler.ReadBackward, onSuccess, compiler.Flags);
            if (compiler.OneByte && !result.CanMatchLatin1(compiler))
            {
                return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
            }
            return result;
        }

        if (IsNegated)
        {
            // With /v, character classes are never negated.
            // https://tc39.es/ecma262/#sec-compileatom
            // Instead the complement is created when evaluating the class set.
            // The only exception is the "nothing range" (negated everything), which is
            // internally created for an empty set.
            Debug.Assert(!compiler.Flags.IsUnicodeSets() ||
                         (ranges.Count == 1 && ranges[0].IsEverything(CharacterRange.kMaxCodePoint)));
            var negated = new List<CharacterRange>(2);
            CharacterRange.Negate(ranges, negated);
            ranges = negated;
        }

        if (ranges.Count == 0) return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);

        if (_set.IsStandard && StandardType == StandardCharacterSet.kEverything)
        {
            return ToNodeHelpers.UnanchoredAdvance(compiler, onSuccess);
        }

        // Split ranges in order to handle surrogates correctly:
        // - Surrogate pairs: translate the 32-bit code point into two uc16 code
        //   units (irregexp operates only on code units).
        // - Lone surrogates: these require lookarounds to ensure we don't match in
        //   the middle of a surrogate pair.
        var choice = new ChoiceNode(2, compiler.Flags);
        var splitter = new UnicodeRangeSplitter(ranges);
        ToNodeHelpers.AddBmpCharacters(compiler, choice, onSuccess, splitter);
        ToNodeHelpers.AddNonBmpSurrogatePairs(compiler, choice, onSuccess, splitter);
        ToNodeHelpers.AddLoneLeadSurrogates(compiler, choice, onSuccess, splitter);
        ToNodeHelpers.AddLoneTrailSurrogates(compiler, choice, onSuccess, splitter);

        const int kMaxRangesToInline = 32;  // Arbitrary.
        if (ranges.Count > kMaxRangesToInline) choice.SetDoNotInline();

        if (choice.Alternatives.Count == 1) return choice.Alternatives[0].Node;

        return choice;
    }
}

public sealed partial class UnicodeRangeSplitter
{
    // The unicode range splitter categorizes given character ranges into:
    // - Code points from the BMP representable by one code unit.
    // - Code points outside the BMP that need to be split into
    // surrogate pairs.
    // - Lone lead surrogates.
    // - Lone trail surrogates.
    // Lone surrogates are valid code points, even though no actual characters.
    // They require special matching to make sure we do not split surrogate pairs.
    public UnicodeRangeSplitter(List<CharacterRange> @base)
    {
        for (int i = 0; i < @base.Count; i++) AddRange(@base[i]);
    }

    const int kBmp1Start = 0;
    const int kBmp1End = RegExpMacroAssembler.kLeadSurrogateStart - 1;
    const int kBmp2Start = RegExpMacroAssembler.kTrailSurrogateEnd + 1;
    const int kBmp2End = RegExpMacroAssembler.kNonBmpStart - 1;

    static readonly int[] s_starts =
    [
        kBmp1Start, RegExpMacroAssembler.kLeadSurrogateStart, RegExpMacroAssembler.kTrailSurrogateStart,
        kBmp2Start, RegExpMacroAssembler.kNonBmpStart,
    ];

    static readonly int[] s_ends =
    [
        kBmp1End, RegExpMacroAssembler.kLeadSurrogateEnd, RegExpMacroAssembler.kTrailSurrogateEnd, kBmp2End,
        RegExpMacroAssembler.kNonBmpEnd,
    ];

    void AddRange(CharacterRange range)
    {
        List<CharacterRange>[] targets = [_bmp, _leadSurrogates, _trailSurrogates, _bmp, _nonBmp];
        for (int i = 0; i < s_starts.Length; i++)
        {
            if (s_starts[i] > range.To) break;
            int from = Math.Max(s_starts[i], range.From);
            int to = Math.Min(s_ends[i], range.To);
            if (from > to) continue;
            targets[i].Add(CharacterRange.Range(from, to));
        }
    }
}

internal static class ToNodeHelpers
{
    // Translates between new and old V8-isms (SmallVector, ZoneList).
    static List<CharacterRange>? ToCanonicalZoneList(List<CharacterRange> v)
    {
        if (v.Count == 0) return null;
        var result = new List<CharacterRange>(v);
        CharacterRange.Canonicalize(result);
        return result;
    }

    public static void AddBmpCharacters(RegExpCompiler compiler, ChoiceNode result, RegExpNode onSuccess,
        UnicodeRangeSplitter splitter)
    {
        List<CharacterRange>? bmp = ToCanonicalZoneList(splitter.Bmp);
        if (bmp is null) return;
        RegExpNode node = TextNode.CreateForCharacterRanges(bmp, compiler.ReadBackward, onSuccess, compiler.Flags);
        result.AddAlternative(new GuardedAlternative(node));
    }

    static int LeadSurrogate(int codePoint) => 0xD800 + (((codePoint - 0x10000) >> 10) & 0x3FF);
    static int TrailSurrogate(int codePoint) => 0xDC00 + ((codePoint - 0x10000) & 0x3FF);

    public static void AddNonBmpSurrogatePairs(RegExpCompiler compiler, ChoiceNode result, RegExpNode onSuccess,
        UnicodeRangeSplitter splitter)
    {
        Debug.Assert(!compiler.OneByte);
        List<CharacterRange>? nonBmp = ToCanonicalZoneList(splitter.NonBmp);
        if (nonBmp is null) return;

        // Translate each 32-bit code point range into the corresponding 16-bit code
        // unit representation consisting of the lead- and trail surrogate.
        //
        // The generated alternatives are grouped by the leading surrogate to avoid
        // emitting excessive code. We also create a dedicated grouping for full
        // trailing ranges, i.e. [dc00-dfff].
        //
        // V8 keeps the groups in a ZoneUnorderedMap and iterates it in hash order.
        // The alternatives match disjoint code point sets, so their order has no
        // observable effect; we iterate in insertion order to stay deterministic.
        var groupedByLeading = new Dictionary<uint, List<CharacterRange>>();
        var groupOrder = new List<uint>();
        var leadingWithFullTrailingRange = new List<CharacterRange>(1);

        void AddRange(int fromL, int toL, int fromT, int toT)
        {
            uint leadingRange = ((uint)fromL << 16) | (uint)toL;
            if (!groupedByLeading.TryGetValue(leadingRange, out List<CharacterRange>? list))
            {
                if (fromT == RegExpMacroAssembler.kTrailSurrogateStart &&
                    toT == RegExpMacroAssembler.kTrailSurrogateEnd)
                {
                    leadingWithFullTrailingRange.Add(CharacterRange.Range(fromL, toL));
                    return;
                }
                list = new List<CharacterRange>(2);
                groupedByLeading[leadingRange] = list;
                groupOrder.Add(leadingRange);
            }
            list.Add(CharacterRange.Range(fromT, toT));
        }

        // First, create the grouped ranges.
        CharacterRange.Canonicalize(nonBmp);
        for (int i = 0; i < nonBmp.Count; i++)
        {
            // Match surrogate pair.
            // E.g. [က5-ᄀ5] becomes
            //      \ud800[\udc05-\udfff]|
            //      [\ud801-\ud803][\udc00-\udfff]|
            //      \ud804[\udc00-\udc05]
            int from = nonBmp[i].From;
            int to = nonBmp[i].To;
            int fromL = LeadSurrogate(from);
            int fromT = TrailSurrogate(from);
            int toL = LeadSurrogate(to);
            int toT = TrailSurrogate(to);

            if (fromL == toL)
            {
                // The lead surrogate is the same.
                AddRange(fromL, toL, fromT, toT);
                continue;
            }

            if (fromT != RegExpMacroAssembler.kTrailSurrogateStart)
            {
                // Add [from_l][from_t-\udfff].
                AddRange(fromL, fromL, fromT, RegExpMacroAssembler.kTrailSurrogateEnd);
                fromL++;
            }
            if (toT != RegExpMacroAssembler.kTrailSurrogateEnd)
            {
                // Add [to_l][\udc00-to_t].
                AddRange(toL, toL, RegExpMacroAssembler.kTrailSurrogateStart, toT);
                toL--;
            }
            if (fromL <= toL)
            {
                // Add [from_l-to_l][\udc00-\udfff].
                AddRange(fromL, toL, RegExpMacroAssembler.kTrailSurrogateStart,
                    RegExpMacroAssembler.kTrailSurrogateEnd);
            }
        }

        // Create the actual TextNode now that ranges are fully grouped.
        if (leadingWithFullTrailingRange.Count != 0)
        {
            CharacterRange.Canonicalize(leadingWithFullTrailingRange);
            RegExpNode node = TextNode.CreateForSurrogatePair(leadingWithFullTrailingRange,
                CharacterRange.Range(RegExpMacroAssembler.kTrailSurrogateStart,
                    RegExpMacroAssembler.kTrailSurrogateEnd),
                compiler.ReadBackward, onSuccess, compiler.Flags);
            result.AddAlternative(new GuardedAlternative(node));
        }
        foreach (uint key in groupOrder)
        {
            var leadingRange = CharacterRange.Range((int)(key >> 16), (int)(key & 0xffff));
            List<CharacterRange> trailingRanges = groupedByLeading[key];
            CharacterRange.Canonicalize(trailingRanges);
            RegExpNode node = TextNode.CreateForSurrogatePair(leadingRange, trailingRanges, compiler.ReadBackward,
                onSuccess, compiler.Flags);
            result.AddAlternative(new GuardedAlternative(node));
        }
    }

    static RegExpNode NegativeLookaroundAgainstReadDirectionAndMatch(RegExpCompiler compiler,
        List<CharacterRange> lookbehind, List<CharacterRange> match, RegExpNode onSuccess, bool readBackward)
    {
        RegExpNode matchNode = TextNode.CreateForCharacterRanges(match, readBackward, onSuccess, compiler.Flags);
        int stackRegister = compiler.UnicodeLookaroundStackRegister();
        int positionRegister = compiler.UnicodeLookaroundPositionRegister();
        var lookaround = new RegExpLookaround.Builder(false, matchNode, compiler, stackRegister, positionRegister);
        RegExpNode negativeMatch = TextNode.CreateForCharacterRanges(lookbehind, !readBackward,
            lookaround.OnMatchSuccess, compiler.Flags);
        return lookaround.ForMatch(compiler, negativeMatch);
    }

    static RegExpNode MatchAndNegativeLookaroundInReadDirection(RegExpCompiler compiler,
        List<CharacterRange> match, List<CharacterRange> lookahead, RegExpNode onSuccess, bool readBackward)
    {
        int stackRegister = compiler.UnicodeLookaroundStackRegister();
        int positionRegister = compiler.UnicodeLookaroundPositionRegister();
        var lookaround = new RegExpLookaround.Builder(false, onSuccess, compiler, stackRegister, positionRegister);
        RegExpNode negativeMatch = TextNode.CreateForCharacterRanges(lookahead, readBackward,
            lookaround.OnMatchSuccess, compiler.Flags);
        return TextNode.CreateForCharacterRanges(match, readBackward, lookaround.ForMatch(compiler, negativeMatch),
            compiler.Flags);
    }

    public static void AddLoneLeadSurrogates(RegExpCompiler compiler, ChoiceNode result, RegExpNode onSuccess,
        UnicodeRangeSplitter splitter)
    {
        List<CharacterRange>? leadSurrogates = ToCanonicalZoneList(splitter.LeadSurrogates);
        if (leadSurrogates is null) return;
        // E.g. \ud801 becomes \ud801(?![\udc00-\udfff]).
        List<CharacterRange> trailSurrogates = CharacterRange.List(
            CharacterRange.Range(RegExpMacroAssembler.kTrailSurrogateStart, RegExpMacroAssembler.kTrailSurrogateEnd));

        RegExpNode match;
        if (compiler.ReadBackward)
        {
            // Reading backward. Assert that reading forward, there is no trail
            // surrogate, and then backward match the lead surrogate.
            match = NegativeLookaroundAgainstReadDirectionAndMatch(compiler, trailSurrogates, leadSurrogates,
                onSuccess, true);
        }
        else
        {
            // Reading forward. Forward match the lead surrogate and assert that
            // no trail surrogate follows.
            match = MatchAndNegativeLookaroundInReadDirection(compiler, leadSurrogates, trailSurrogates, onSuccess,
                false);
        }
        result.AddAlternative(new GuardedAlternative(match));
    }

    public static void AddLoneTrailSurrogates(RegExpCompiler compiler, ChoiceNode result, RegExpNode onSuccess,
        UnicodeRangeSplitter splitter)
    {
        List<CharacterRange>? trailSurrogates = ToCanonicalZoneList(splitter.TrailSurrogates);
        if (trailSurrogates is null) return;
        // E.g. \udc01 becomes (?<![\ud800-\udbff])\udc01
        List<CharacterRange> leadSurrogates = CharacterRange.List(
            CharacterRange.Range(RegExpMacroAssembler.kLeadSurrogateStart, RegExpMacroAssembler.kLeadSurrogateEnd));

        RegExpNode match;
        if (compiler.ReadBackward)
        {
            // Reading backward. Backward match the trail surrogate and assert that no
            // lead surrogate precedes it.
            match = MatchAndNegativeLookaroundInReadDirection(compiler, trailSurrogates, leadSurrogates, onSuccess,
                true);
        }
        else
        {
            // Reading forward. Assert that reading backward, there is no lead
            // surrogate, and then forward match the trail surrogate.
            match = NegativeLookaroundAgainstReadDirectionAndMatch(compiler, leadSurrogates, trailSurrogates,
                onSuccess, false);
        }
        result.AddAlternative(new GuardedAlternative(match));
    }

    public static RegExpNode UnanchoredAdvance(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        // This implements ES2015 21.2.5.2.3, AdvanceStringIndex.
        Debug.Assert(!compiler.ReadBackward);
        return new UnanchoredAdvanceNode(onSuccess, compiler.Flags);
    }

    // Desugar \b to (?<=\w)(?=\W)|(?<=\W)(?=\w) and
    //         \B to (?<=\w)(?=\w)|(?<=\W)(?=\W)
    public static RegExpNode BoundaryAssertionAsLookaround(RegExpCompiler compiler, RegExpNode onSuccess,
        RegExpAssertion.Type type)
    {
        if (!NeedsUnicodeCaseEquivalents(compiler.Flags)) throw new InvalidOperationException("CHECK failed");
        var wordRange = new List<CharacterRange>(2);
        CharacterRange.AddClassEscape(StandardCharacterSet.kWord, wordRange, true);
        int stackRegister = compiler.UnicodeLookaroundStackRegister();
        int positionRegister = compiler.UnicodeLookaroundPositionRegister();
        var result = new ChoiceNode(2, compiler.Flags);
        // Add two choices. The (non-)boundary could start with a word or
        // a non-word-character.
        for (int i = 0; i < 2; i++)
        {
            bool lookbehindForWord = i == 0;
            bool lookaheadForWord = (type == RegExpAssertion.Type.BOUNDARY) ^ lookbehindForWord;
            // Look to the left.
            var lookbehind = new RegExpLookaround.Builder(lookbehindForWord, onSuccess, compiler, stackRegister,
                positionRegister);
            RegExpNode backward = TextNode.CreateForCharacterRanges(wordRange, true, lookbehind.OnMatchSuccess,
                compiler.Flags);
            // Look to the right.
            var lookahead = new RegExpLookaround.Builder(lookaheadForWord, lookbehind.ForMatch(compiler, backward),
                compiler, stackRegister, positionRegister);
            RegExpNode forward = TextNode.CreateForCharacterRanges(wordRange, false, lookahead.OnMatchSuccess,
                compiler.Flags);
            result.AddAlternative(new GuardedAlternative(lookahead.ForMatch(compiler, forward)));
        }
        return result;
    }
}

public partial struct CharacterRange
{
    // Only for /ui and /vi, not for /i regexps.
    public static void AddUnicodeCaseEquivalents(List<CharacterRange> ranges)
    {
        Debug.Assert(IsCanonical(ranges));

        // Micro-optimization to avoid passing large ranges to UnicodeSet::closeOver.
        // See also https://crbug.com/v8/6727.
        if (ranges.Count == 1 && ranges[0].IsEverything(RegExpMacroAssembler.kNonBmpEnd)) return;

        // V8 uses ICU to compute the case fold closure over the ranges.
        var set = new CodePointSet();
        for (int i = 0; i < ranges.Count; i++) set.Add(ranges[i].From, ranges[i].To);
        // Clear the ranges list without freeing the backing store.
        ranges.Clear();
        CaseFolding.CloseOver(set, CaseFolding.Mode.kUnicode);
        for (int i = 0; i < set.RangeCount; i++) ranges.Add(Range(set.GetRangeStart(i), set.GetRangeEnd(i)));
        // No errors and everything we collected have been ranges.
        Canonicalize(ranges);
    }

    static void AddClass(int[] elmv, List<CharacterRange> ranges)
    {
        int elmc = elmv.Length - 1;
        Debug.Assert(elmv[elmc] == kRangeEndMarker);
        for (int i = 0; i < elmc; i += 2)
        {
            Debug.Assert(elmv[i] < elmv[i + 1]);
            ranges.Add(Range(elmv[i], elmv[i + 1] - 1));
        }
    }

    static void AddClassNegated(int[] elmv, List<CharacterRange> ranges)
    {
        int elmc = elmv.Length - 1;
        Debug.Assert(elmv[elmc] == kRangeEndMarker);
        Debug.Assert(elmv[0] != 0x0000);
        Debug.Assert(elmv[elmc - 1] != kMaxCodePoint);
        int last = 0x0000;
        for (int i = 0; i < elmc; i += 2)
        {
            Debug.Assert(last <= elmv[i] - 1);
            Debug.Assert(elmv[i] < elmv[i + 1]);
            ranges.Add(Range(last, elmv[i] - 1));
            last = elmv[i + 1] & 0xffff;  // base::uc16 in V8.
        }
        ranges.Add(Range(last, kMaxCodePoint));
    }

    public static void AddClassEscape(StandardCharacterSet standardCharacterSet, List<CharacterRange> ranges,
        bool addUnicodeCaseEquivalents)
    {
        if (addUnicodeCaseEquivalents &&
            (standardCharacterSet == StandardCharacterSet.kWord ||
             standardCharacterSet == StandardCharacterSet.kNotWord))
        {
            // See
            // https://tc39.es/ecma262/#sec-runtime-semantics-wordcharacters-abstract-operation
            // In case of unicode and ignore_case, we need to create the closure over
            // case equivalent characters before negating.
            var newRanges = new List<CharacterRange>(2);
            AddClass(kWordRanges, newRanges);
            AddUnicodeCaseEquivalents(newRanges);
            if (standardCharacterSet == StandardCharacterSet.kNotWord)
            {
                var negated = new List<CharacterRange>(2);
                Negate(newRanges, negated);
                newRanges = negated;
            }
            ranges.AddRange(newRanges);
            return;
        }

        switch (standardCharacterSet)
        {
            case StandardCharacterSet.kWhitespace:
                AddClass(kSpaceRanges, ranges);
                break;
            case StandardCharacterSet.kNotWhitespace:
                AddClassNegated(kSpaceRanges, ranges);
                break;
            case StandardCharacterSet.kWord:
                AddClass(kWordRanges, ranges);
                break;
            case StandardCharacterSet.kNotWord:
                AddClassNegated(kWordRanges, ranges);
                break;
            case StandardCharacterSet.kDigit:
                AddClass(kDigitRanges, ranges);
                break;
            case StandardCharacterSet.kNotDigit:
                AddClassNegated(kDigitRanges, ranges);
                break;
            // This is the set of characters matched by the $ and ^ symbols
            // in multiline mode.
            case StandardCharacterSet.kLineTerminator:
                AddClass(kLineTerminatorRanges, ranges);
                break;
            case StandardCharacterSet.kNotLineTerminator:
                AddClassNegated(kLineTerminatorRanges, ranges);
                break;
            // This is not a character range as defined by the spec but a
            // convenient shorthand for a character class that matches any
            // character.
            case StandardCharacterSet.kEverything:
                ranges.Add(Everything());
                break;
        }
    }

    // Only for /i, not for /ui or /vi.
    public static void AddCaseEquivalents(List<CharacterRange> ranges, bool isOneByte)
    {
        Canonicalize(ranges);
        int rangeCount = ranges.Count;
        var others = new CodePointSet();
        for (int i = 0; i < rangeCount; i++)
        {
            CharacterRange range = ranges[i];
            int from = range.From;
            if (from > kMaxUtf16CodeUnit) continue;
            int to = Math.Min(range.To, kMaxUtf16CodeUnit);
            // Nothing to be done for surrogates.
            if (from >= RegExpMacroAssembler.kLeadSurrogateStart && to <= RegExpMacroAssembler.kTrailSurrogateEnd)
            {
                continue;
            }
            if (isOneByte && !RegExpCompilerHelpers.RangeContainsLatin1Equivalents(range))
            {
                if (from > kMaxOneByteCharCode) continue;
                if (to > kMaxOneByteCharCode) to = kMaxOneByteCharCode;
            }
            others.Add(from, to);
        }

        var alreadyAdded = new CodePointSet(others);
        CaseFolding.CloseOver(others, CaseFolding.Mode.kNonUnicode);
        others.RemoveAll(alreadyAdded);

        // Add others to the ranges
        for (int i = 0; i < others.RangeCount; i++)
        {
            int from = others.GetRangeStart(i);
            int to = others.GetRangeEnd(i);
            ranges.Add(from == to ? Singleton(from) : Range(from, to));
        }
    }

    public static bool IsCanonical(List<CharacterRange> ranges)
    {
        int n = ranges.Count;
        if (n <= 1) return true;
        int max = ranges[0].To;
        for (int i = 1; i < n; i++)
        {
            CharacterRange nextRange = ranges[i];
            if (nextRange.From <= max + 1) return false;
            max = nextRange.To;
        }
        return true;
    }

    // Move a number of elements in a zonelist to another position
    // in the same list. Handles overlapping source and target areas.
    static void MoveRanges(List<CharacterRange> list, int from, int to, int count)
    {
        // Ranges are potentially overlapping.
        if (from < to)
        {
            for (int i = count - 1; i >= 0; i--) list[to + i] = list[from + i];
        }
        else
        {
            for (int i = 0; i < count; i++) list[to + i] = list[from + i];
        }
    }

    static int InsertRangeInCanonicalList(List<CharacterRange> list, int count, CharacterRange insert)
    {
        // Inserts a range into list[0..count[, which must be sorted
        // by from value and non-overlapping and non-adjacent, using at most
        // list[0..count] for the result. Returns the number of resulting
        // canonicalized ranges. Inserting a range may collapse existing ranges into
        // fewer ranges, so the return value can be anything in the range 1..count+1.
        int from = insert.From;
        int to = insert.To;
        int startPos = 0;
        int endPos = count;
        for (int i = count - 1; i >= 0; i--)
        {
            CharacterRange current = list[i];
            if (current.From > to + 1)
            {
                endPos = i;
            }
            else if (current.To + 1 < from)
            {
                startPos = i + 1;
                break;
            }
        }

        // Inserted range overlaps, or is adjacent to, ranges at positions
        // [start_pos..end_pos[. Ranges before start_pos or at or after end_pos are
        // not affected by the insertion.
        // If start_pos == end_pos, the range must be inserted before start_pos.
        // if start_pos < end_pos, the entire range from start_pos to end_pos
        // must be merged with the insert range.

        if (startPos == endPos)
        {
            // Insert between existing ranges at position start_pos.
            if (startPos < count) MoveRanges(list, startPos, startPos + 1, count - startPos);
            list[startPos] = insert;
            return count + 1;
        }
        if (startPos + 1 == endPos)
        {
            // Replace single existing range at position start_pos.
            CharacterRange toReplace = list[startPos];
            int newFrom = Math.Min(toReplace.From, from);
            int newTo = Math.Max(toReplace.To, to);
            list[startPos] = Range(newFrom, newTo);
            return count;
        }
        // Replace a number of existing ranges from start_pos to end_pos - 1.
        // Move the remaining ranges down.

        int newFrom2 = Math.Min(list[startPos].From, from);
        int newTo2 = Math.Max(list[endPos - 1].To, to);
        if (endPos < count) MoveRanges(list, endPos, startPos + 1, count - endPos);
        list[startPos] = Range(newFrom2, newTo2);
        return count - (endPos - startPos) + 1;
    }

    public static void Canonicalize(List<CharacterRange> characterRanges)
    {
        if (characterRanges.Count <= 1) return;
        // Check whether ranges are already canonical (increasing, non-overlapping,
        // non-adjacent).
        int n = characterRanges.Count;
        int max = characterRanges[0].To;
        int i = 1;
        while (i < n)
        {
            CharacterRange current = characterRanges[i];
            if (current.From <= max + 1) break;
            max = current.To;
            i++;
        }
        // Canonical until the i'th range. If that's all of them, we are done.
        if (i == n) return;

        // The ranges at index i and forward are not canonicalized. Make them so by
        // doing the equivalent of insertion sort (inserting each into the previous
        // list, in order).
        // Notice that inserting a range can reduce the number of ranges in the
        // result due to combining of adjacent and overlapping ranges.
        int read = i;           // Range to insert.
        int numCanonical = i;  // Length of canonicalized part of list.
        do
        {
            numCanonical = InsertRangeInCanonicalList(characterRanges, numCanonical, characterRanges[read]);
            read++;
        } while (read < n);
        characterRanges.Rewind(numCanonical);

        Debug.Assert(IsCanonical(characterRanges));
    }

    public static void Negate(List<CharacterRange> ranges, List<CharacterRange> negatedRanges)
    {
        Debug.Assert(IsCanonical(ranges));
        Debug.Assert(negatedRanges.Count == 0);
        int rangeCount = ranges.Count;
        int from = 0;
        int i = 0;
        if (rangeCount > 0 && ranges[0].From == 0)
        {
            from = ranges[0].To + 1;
            i = 1;
        }
        while (i < rangeCount)
        {
            CharacterRange range = ranges[i];
            negatedRanges.Add(Range(from, range.From - 1));
            from = range.To + 1;
            i++;
        }
        if (from < kMaxCodePoint) negatedRanges.Add(Range(from, kMaxCodePoint));
    }

    public static void Intersect(List<CharacterRange> lhs, List<CharacterRange> rhs,
        List<CharacterRange> intersection)
    {
        Debug.Assert(IsCanonical(lhs));
        Debug.Assert(IsCanonical(rhs));
        Debug.Assert(intersection.Count == 0);
        int lhsIndex = 0;
        int rhsIndex = 0;
        while (lhsIndex < lhs.Count && rhsIndex < rhs.Count)
        {
            // Skip non-overlapping ranges.
            if (lhs[lhsIndex].To < rhs[rhsIndex].From)
            {
                lhsIndex++;
                continue;
            }
            if (rhs[rhsIndex].To < lhs[lhsIndex].From)
            {
                rhsIndex++;
                continue;
            }

            int from = Math.Max(lhs[lhsIndex].From, rhs[rhsIndex].From);
            int to = Math.Min(lhs[lhsIndex].To, rhs[rhsIndex].To);
            intersection.Add(Range(from, to));
            if (to == lhs[lhsIndex].To)
            {
                lhsIndex++;
            }
            else
            {
                rhsIndex++;
            }
        }

        Debug.Assert(IsCanonical(intersection));
    }

    public static bool Intersects(List<CharacterRange> lhs, List<CharacterRange> rhs)
    {
        Debug.Assert(IsCanonical(lhs));
        Debug.Assert(IsCanonical(rhs));
        int lhsIndex = 0;
        int rhsIndex = 0;
        while (lhsIndex < lhs.Count && rhsIndex < rhs.Count)
        {
            CharacterRange lhsRange = lhs[lhsIndex];
            CharacterRange rhsRange = rhs[rhsIndex];
            if (lhsRange.To < rhsRange.From)
            {
                lhsIndex++;
            }
            else if (rhsRange.To < lhsRange.From)
            {
                rhsIndex++;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    // Advance |index| and set |from| and |to| to the new range, if not out of
    // bounds of |range|, otherwise |from| is set to a code point beyond the legal
    // unicode character range.
    static void SafeAdvanceRange(List<CharacterRange> range, ref int index, ref int from, ref int to)
    {
        ++index;
        if (index < range.Count)
        {
            from = range[index].From;
            to = range[index].To;
        }
        else
        {
            from = kMaxCodePoint + 1;
        }
    }

    public static void Subtract(List<CharacterRange> src, List<CharacterRange> toRemove,
        List<CharacterRange> result)
    {
        Debug.Assert(IsCanonical(src));
        Debug.Assert(IsCanonical(toRemove));
        Debug.Assert(result.Count == 0);

        if (src.Count == 0) return;

        int srcIndex = 0;
        int toRemoveIndex = 0;
        int from = src[srcIndex].From;
        int to = src[srcIndex].To;
        while (srcIndex < src.Count && toRemoveIndex < toRemove.Count)
        {
            CharacterRange removeRange = toRemove[toRemoveIndex];
            if (removeRange.To < from)
            {
                // (a) Non-overlapping case, ignore current to_remove range.
                toRemoveIndex++;
            }
            else if (to < removeRange.From)
            {
                // (b) Non-overlapping case, add full current range to result.
                result.Add(Range(from, to));
                SafeAdvanceRange(src, ref srcIndex, ref from, ref to);
            }
            else if (from >= removeRange.From && to <= removeRange.To)
            {
                // (c) Current to_remove range fully covers current range.
                SafeAdvanceRange(src, ref srcIndex, ref from, ref to);
            }
            else if (from < removeRange.From && to > removeRange.To)
            {
                // (d) Split current range.
                result.Add(Range(from, removeRange.From - 1));
                from = removeRange.To + 1;
                toRemoveIndex++;
            }
            else if (from < removeRange.From)
            {
                // (e) End current range.
                to = removeRange.From - 1;
                result.Add(Range(from, to));
                SafeAdvanceRange(src, ref srcIndex, ref from, ref to);
            }
            else if (to > removeRange.To)
            {
                // (f) Modify start of current range.
                from = removeRange.To + 1;
                toRemoveIndex++;
            }
            else
            {
                throw new InvalidOperationException("UNREACHABLE");
            }
        }
        // The last range needs special treatment after |to_remove| is exhausted, as
        // |from| might have been modified by the last |to_remove| range and |to| was
        // not yet known (i.e. cases d and f).
        if (from <= to) result.Add(Range(from, to));
        srcIndex++;

        // Add remaining ranges after |to_remove| is exhausted.
        for (; srcIndex < src.Count; srcIndex++) result.Add(src[srcIndex]);

        Debug.Assert(IsCanonical(result));
    }

    public static void ClampToOneByte(List<CharacterRange> ranges)
    {
        Debug.Assert(IsCanonical(ranges));

        // Drop all ranges that don't contain one-byte code units, and clamp the last
        // range s.t. it likewise only contains one-byte code units. Note this relies
        // on `ranges` being canonicalized, i.e. sorted and non-overlapping.

        const int maxChar = kMaxOneByteCharCode;
        int n = ranges.Count;
        for (; n > 0; n--)
        {
            CharacterRange r = ranges[n - 1];
            if (r.From <= maxChar)
            {
                ranges[n - 1] = Range(r.From, Math.Min(r.To, maxChar));
                break;
            }
        }

        ranges.Rewind(n);
    }

    public static bool Equals(List<CharacterRange> lhs, List<CharacterRange> rhs)
    {
        Debug.Assert(IsCanonical(lhs));
        Debug.Assert(IsCanonical(rhs));
        if (lhs.Count != rhs.Count) return false;
        for (int i = 0; i < lhs.Count; i++)
        {
            if (lhs[i] != rhs[i]) return false;
        }
        return true;
    }
}

public sealed partial class RegExpClassSetOperand
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        int size = (HasStrings ? Strings.Count : 0) + (Ranges.Count == 0 ? 0 : 1);
        if (size == 0)
        {
            // If neither ranges nor strings are present, the operand is equal to an
            // empty range (matching nothing).
            return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
        }
        var alternatives = new List<RegExpTree>(size);
        // Strings are sorted by length first (larger strings before shorter ones).
        // See the comment on CharacterClassStrings.
        // Empty strings (if present) are added after character ranges.
        RegExpTree? emptyString = null;
        if (HasStrings)
        {
            foreach (KeyValuePair<int[], RegExpTree> str in Strings)
            {
                if (str.Value.IsEmpty())
                {
                    emptyString = str.Value;
                }
                else
                {
                    alternatives.Add(str.Value);
                }
            }
        }
        if (Ranges.Count != 0)
        {
            // In unicode sets mode case folding has to be done at precise locations
            // (e.g. before building complements).
            // It is therefore the parsers responsibility to case fold (sub-) ranges
            // before creating ClassSetOperands.
            alternatives.Add(new RegExpClassRanges(Ranges, RegExpClassRanges.ClassRangesFlags.NO_CASE_FOLDING_NEEDED));
        }
        if (emptyString is not null) alternatives.Add(emptyString);

        RegExpTree tree;
        if (size == 1)
        {
            Debug.Assert(alternatives.Count == 1);
            tree = alternatives[0];
        }
        else
        {
            tree = new RegExpDisjunction(alternatives);
        }
        return tree.ToNode(compiler, onSuccess);
    }

    // Replaces the contents of |ranges| by those of |temp| and empties |temp|
    // (V8's std::swap of the two ZoneLists followed by Rewind(0)).
    static void SwapInto(List<CharacterRange> ranges, List<CharacterRange> temp)
    {
        ranges.Clear();
        ranges.AddRange(temp);
        temp.Clear();
    }

    public void Union(RegExpClassSetOperand other)
    {
        Ranges.AddRange(other.Ranges);
        if (other.HasStrings)
        {
            StringsOrNull ??= new CharacterClassStrings();
            foreach (KeyValuePair<int[], RegExpTree> kv in other.Strings) Strings.TryAdd(kv.Key, kv.Value);
        }
    }

    public void Intersect(RegExpClassSetOperand other, List<CharacterRange> tempRanges)
    {
        CharacterRange.Intersect(Ranges, other.Ranges, tempRanges);
        SwapInto(Ranges, tempRanges);
        if (HasStrings)
        {
            if (!other.HasStrings)
            {
                Strings.Clear();
            }
            else
            {
                var toRemove = new List<int[]>();
                foreach (int[] key in Strings.Keys)
                {
                    if (!other.Strings.ContainsKey(key)) toRemove.Add(key);
                }
                foreach (int[] key in toRemove) Strings.Remove(key);
            }
        }
    }

    public void Subtract(RegExpClassSetOperand other, List<CharacterRange> tempRanges)
    {
        CharacterRange.Subtract(Ranges, other.Ranges, tempRanges);
        SwapInto(Ranges, tempRanges);
        if (HasStrings && other.HasStrings)
        {
            var toRemove = new List<int[]>();
            foreach (int[] key in Strings.Keys)
            {
                if (other.Strings.ContainsKey(key)) toRemove.Add(key);
            }
            foreach (int[] key in toRemove) Strings.Remove(key);
        }
    }

    internal static void SwapRanges(List<CharacterRange> ranges, List<CharacterRange> temp) => SwapInto(ranges, temp);
}

public sealed partial class RegExpClassSetExpression
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        var tempRanges = new List<CharacterRange>(4);
        RegExpClassSetOperand root = ComputeExpression(this, tempRanges);
        return root.ToNode(compiler, onSuccess);
    }

    // Evaluates the expression tree rooted at |root| in place: the result is
    // stored as the single operand of each visited expression node.
    public static RegExpClassSetOperand ComputeExpression(RegExpTree root, List<CharacterRange> tempRanges)
    {
        Debug.Assert(tempRanges.Count == 0);
        if (root.IsClassSetOperand()) return root.AsClassSetOperand()!;
        Debug.Assert(root.IsClassSetExpression());
        RegExpClassSetExpression node = root.AsClassSetExpression()!;
        RegExpClassSetOperand result = ComputeExpression(node.Operands[0], tempRanges);
        switch (node.Operation)
        {
            case OperationType.kUnion:
            {
                for (int i = 1; i < node.Operands.Count; i++)
                {
                    RegExpClassSetOperand op = ComputeExpression(node.Operands[i], tempRanges);
                    result.Union(op);
                }
                CharacterRange.Canonicalize(result.Ranges);
                break;
            }
            case OperationType.kIntersection:
            {
                for (int i = 1; i < node.Operands.Count; i++)
                {
                    RegExpClassSetOperand op = ComputeExpression(node.Operands[i], tempRanges);
                    result.Intersect(op, tempRanges);
                }
                break;
            }
            case OperationType.kSubtraction:
            {
                for (int i = 1; i < node.Operands.Count; i++)
                {
                    RegExpClassSetOperand op = ComputeExpression(node.Operands[i], tempRanges);
                    result.Subtract(op, tempRanges);
                }
                break;
            }
        }
        if (node.IsNegated)
        {
            Debug.Assert(!result.HasStrings);
            CharacterRange.Negate(result.Ranges, tempRanges);
            RegExpClassSetOperand.SwapRanges(result.Ranges, tempRanges);
            node._isNegated = false;
        }
        // Store the result as single operand of the current node.
        node.Operands[0] = result;
        node.Operands.Rewind(1);

        return result;
    }
}

public sealed partial class RegExpDisjunction
{
    static bool StartsWithAtom(RegExpTree tree)
    {
        if (tree.IsAtom()) return true;
        return tree.IsText() && tree.AsText()!.StartsWithAtom();
    }

    static RegExpAtom FirstAtom(RegExpTree tree)
    {
        if (tree.IsAtom()) return tree.AsAtom()!;
        return tree.AsText()!.FirstAtom();
    }

    static int CompareFirstChar(RegExpTree a, RegExpTree b)
    {
        char character1 = FirstAtom(a).Data[0];
        char character2 = FirstAtom(b).Data[0];
        if (character1 < character2) return -1;
        if (character1 > character2) return 1;
        return 0;
    }

    static CaseFolding.Mode CaseFoldingMode(RegExpFlags flags) =>
        flags.IsEitherUnicode() ? CaseFolding.Mode.kUnicode : CaseFolding.Mode.kNonUnicode;

    // Use the matcher's case equivalence (see
    // TextNode::GetCaseIndependentLetters). Full case folding would, for example,
    // conflate U+017F and 's' under /i.
    static int CompareCaseInsensitive(CaseFolding.Mode mode, char a, char b)
    {
        if (a == b) return 0;
        return CaseFolding.EquivalenceKey(a, mode) - CaseFolding.EquivalenceKey(b, mode);
    }

    static bool Equals(bool ignoreCase, CaseFolding.Mode mode, char a, char b)
    {
        if (a == b) return true;
        if (ignoreCase) return CompareCaseInsensitive(mode, a, b) == 0;
        return false;  // Case-sensitive equality already checked above.
    }

    static bool CharAtEquals(bool ignoreCase, CaseFolding.Mode mode, int index, RegExpAtom a, RegExpAtom b) =>
        Equals(ignoreCase, mode, a.Data[index], b.Data[index]);

    // ZoneList::StableSort over [start, start + length): std::stable_sort with
    // cmp(a, b) < 0 as the ordering.
    static void StableSort(List<RegExpTree> list, int start, int length, bool ignoreCase, CaseFolding.Mode mode)
    {
        // Insertion sort is stable and the runs are short.
        for (int i = start + 1; i < start + length; i++)
        {
            RegExpTree item = list[i];
            int j = i - 1;
            while (j >= start && Compare(item, list[j], ignoreCase, mode) < 0)
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = item;
        }
    }

    static int Compare(RegExpTree a, RegExpTree b, bool ignoreCase, CaseFolding.Mode mode) =>
        ignoreCase ? CompareCaseInsensitive(mode, FirstAtom(a).Data[0], FirstAtom(b).Data[0]) : CompareFirstChar(a, b);

    // We can stable sort runs of atoms, since the order does not matter if they
    // start with different characters.
    // Returns true if any consecutive atoms were found.
    public bool SortConsecutiveAtoms(RegExpCompiler compiler)
    {
        List<RegExpTree> alternatives = _alternatives;
        int length = alternatives.Count;
        bool foundConsecutiveAtoms = false;
        for (int i = 0; i < length; i++)
        {
            while (i < length)
            {
                RegExpTree alternative = alternatives[i];
                if (StartsWithAtom(alternative)) break;
                i++;
            }
            // i is length or it is the index of an atom.
            if (i == length) break;
            int firstAtom = i;
            i++;
            while (i < length)
            {
                RegExpTree alternative = alternatives[i];
                if (!StartsWithAtom(alternative)) break;
                i++;
            }
            // Sort atoms to bring common prefixes together. A case-insensitive sort
            // must preserve the order of alternatives whose first characters are
            // equivalent: changing /is|I/ to /I|is/ would change the match result.
            bool ignoreCase = compiler.Flags.IsIgnoreCase();
            StableSort(alternatives, firstAtom, i - firstAtom, ignoreCase, CaseFoldingMode(compiler.Flags));
            if (i - firstAtom > 1) foundConsecutiveAtoms = true;
        }
        return foundConsecutiveAtoms;
    }

    // Optimizes ab|ac|az to a(?:b|c|d).
    public void RationalizeConsecutiveAtoms(RegExpCompiler compiler)
    {
        List<RegExpTree> alternatives = _alternatives;
        int length = alternatives.Count;
        bool ignoreCase = compiler.Flags.IsIgnoreCase();
        CaseFolding.Mode mode = CaseFoldingMode(compiler.Flags);

        int writePosn = 0;
        int i = 0;
        while (i < length)
        {
            RegExpTree alternative = alternatives[i];
            if (!StartsWithAtom(alternative))
            {
                alternatives[writePosn++] = alternatives[i];
                i++;
                continue;
            }
            RegExpAtom atom = FirstAtom(alternative);
            char commonPrefix = atom.Data[0];
            int firstWithPrefix = i;
            int prefixLength = atom.Length;
            i++;
            while (i < length)
            {
                alternative = alternatives[i];
                if (!StartsWithAtom(alternative)) break;
                RegExpAtom altAtom = FirstAtom(alternative);
                char newPrefix = altAtom.Data[0];
                if (!Equals(ignoreCase, mode, newPrefix, commonPrefix)) break;
                prefixLength = Math.Min(prefixLength, altAtom.Length);
                i++;
            }
            if (i > firstWithPrefix + 2)
            {
                // Found worthwhile run of alternatives with common prefix of at least one
                // character.  The sorting function above did not sort on more than one
                // character for reasons of correctness, but there may still be a longer
                // common prefix if the terms were similar or presorted in the input.
                // Find out how long the common prefix is.
                int runLength = i - firstWithPrefix;
                RegExpAtom altAtom0 = FirstAtom(alternatives[firstWithPrefix]);
                for (int j = 1; j < runLength && prefixLength > 1; j++)
                {
                    RegExpAtom oldAtom = FirstAtom(alternatives[j + firstWithPrefix]);
                    for (int k = 1; k < prefixLength; k++)
                    {
                        if (!CharAtEquals(ignoreCase, mode, k, altAtom0, oldAtom))
                        {
                            prefixLength = k;
                            break;
                        }
                    }
                }
                var prefix = new RegExpAtom(altAtom0.Data.Substring(0, prefixLength));
                var pair = new List<RegExpTree>(2) { prefix };
                var suffixes = new List<RegExpTree>(runLength);
                for (int j = 0; j < runLength; j++)
                {
                    if (alternatives[j + firstWithPrefix].IsAtom())
                    {
                        RegExpAtom oldAtom = alternatives[j + firstWithPrefix].AsAtom()!;
                        int len = oldAtom.Length;
                        if (len == prefixLength)
                        {
                            suffixes.Add(new RegExpEmpty());
                        }
                        else
                        {
                            suffixes.Add(new RegExpAtom(oldAtom.Data.Substring(prefixLength, len - prefixLength)));
                        }
                    }
                    else
                    {
                        var newText = new RegExpText();
                        RegExpText oldText = alternatives[j + firstWithPrefix].AsText()!;
                        RegExpAtom oldAtom = oldText.FirstAtom();
                        int len = oldAtom.Length;
                        if (len != prefixLength)
                        {
                            var suffix = new RegExpAtom(oldAtom.Data.Substring(prefixLength, len - prefixLength));
                            newText.AddElement(TextElement.FromAtom(suffix));
                        }
                        for (int k = 1; k < oldText.Elements.Count; k++) newText.AddElement(oldText.Elements[k]);
                        if (newText.Elements.Count != 0)
                        {
                            suffixes.Add(newText);
                        }
                        else
                        {
                            suffixes.Add(new RegExpEmpty());
                        }
                    }
                }
                pair.Add(new RegExpDisjunction(suffixes));
                alternatives[writePosn++] = new RegExpAlternative(pair);
            }
            else
            {
                // Just copy any non-worthwhile alternatives.
                for (int j = firstWithPrefix; j < i; j++) alternatives[writePosn++] = alternatives[j];
            }
        }
        alternatives.Rewind(writePosn);  // Trim end of array.
    }

    // Optimizes b|c|z to [bcz].
    public void FixSingleCharacterDisjunctions(RegExpCompiler compiler)
    {
        List<RegExpTree> alternatives = _alternatives;
        int length = alternatives.Count;

        int writePosn = 0;
        int i = 0;
        while (i < length)
        {
            RegExpTree alternative = alternatives[i];
            if (!alternative.IsAtom())
            {
                alternatives[writePosn++] = alternatives[i];
                i++;
                continue;
            }
            RegExpAtom atom = alternative.AsAtom()!;
            if (atom.Length != 1)
            {
                alternatives[writePosn++] = alternatives[i];
                i++;
                continue;
            }
            RegExpFlags flags = compiler.Flags;
            Debug.Assert(!flags.IsEitherUnicode() || !char.IsHighSurrogate(atom.Data[0]));
            bool containsTrailSurrogate = char.IsLowSurrogate(atom.Data[0]);
            int firstInRun = i;
            i++;
            // Find a run of single-character atom alternatives that have identical
            // flags (case independence and unicode-ness).
            while (i < length)
            {
                alternative = alternatives[i];
                if (!alternative.IsAtom()) break;
                RegExpAtom altAtom = alternative.AsAtom()!;
                if (altAtom.Length != 1) break;
                Debug.Assert(!flags.IsEitherUnicode() || !char.IsHighSurrogate(altAtom.Data[0]));
                containsTrailSurrogate |= char.IsLowSurrogate(altAtom.Data[0]);
                i++;
            }
            if (i > firstInRun + 1)
            {
                // Found non-trivial run of single-character alternatives.
                int runLength = i - firstInRun;
                var ranges = new List<CharacterRange>(2);
                for (int j = 0; j < runLength; j++)
                {
                    RegExpAtom oldAtom = alternatives[j + firstInRun].AsAtom()!;
                    Debug.Assert(oldAtom.Length == 1);
                    ranges.Add(CharacterRange.Singleton(oldAtom.Data[0]));
                }
                RegExpClassRanges.ClassRangesFlags classRangesFlags = RegExpClassRanges.ClassRangesFlags.None;
                if (flags.IsEitherUnicode() && containsTrailSurrogate)
                {
                    classRangesFlags = RegExpClassRanges.ClassRangesFlags.CONTAINS_SPLIT_SURROGATE;
                }
                alternatives[writePosn++] = new RegExpClassRanges(ranges, classRangesFlags);
            }
            else
            {
                // Just copy any trivial alternatives.
                for (int j = firstInRun; j < i; j++) alternatives[writePosn++] = alternatives[j];
            }
        }
        alternatives.Rewind(writePosn);  // Trim end of array.
    }

    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        List<RegExpTree> alternatives = _alternatives;

        if (alternatives.Count > 2)
        {
            if (!compiler.ReadBackward && RegExpCompiler.s_regexpOptimization)
            {
                // We deliberately disable SortConsecutiveAtoms and
                // RationalizeConsecutiveAtoms in lookbehinds rather than keep
                // rationalizing already-adjacent runs. Lookbehind disjunctions must keep
                // source order.
                bool foundConsecutiveAtoms = SortConsecutiveAtoms(compiler);
                if (foundConsecutiveAtoms) RationalizeConsecutiveAtoms(compiler);
            }
            FixSingleCharacterDisjunctions(compiler);
            if (alternatives.Count == 1) return alternatives[0].ToNode(compiler, onSuccess);
        }

        int length = alternatives.Count;

        var result = new ChoiceNode(length, compiler.Flags);
        for (int i = 0; i < length; i++)
        {
            var alternative = new GuardedAlternative(alternatives[i].ToNode(compiler, onSuccess));
            if (!alternative.Node.IsBacktrack()) result.AddAlternative(alternative);
        }
        int nodeLength = result.Alternatives.Count;
        if (nodeLength >= 2) return result;
        if (nodeLength == 1) return result.Alternatives[0].Node;
        return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
    }
}

public sealed partial class RegExpQuantifier
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess) =>
        ToNode(Min, Max, IsGreedy, Body, compiler, onSuccess);

    // Scoped object to keep track of how much we unroll quantifier loops in the
    // regexp graph generator.
    ref struct ExpansionLimiter
    {
        public const int kMaxExpansionFactor = 6;

        readonly RegExpCompiler _compiler;
        readonly int _savedExpansionFactor;
        readonly bool _okToExpand;

        public ExpansionLimiter(RegExpCompiler compiler, int factor)
        {
            _compiler = compiler;
            _savedExpansionFactor = compiler.CurrentExpansionFactor;
            _okToExpand = _savedExpansionFactor <= kMaxExpansionFactor;
            Debug.Assert(0 < factor);
            if (_okToExpand)
            {
                if (factor > kMaxExpansionFactor)
                {
                    // Avoid integer overflow of the current expansion factor.
                    _okToExpand = false;
                    compiler.CurrentExpansionFactor = kMaxExpansionFactor + 1;
                }
                else
                {
                    int newFactor = _savedExpansionFactor * factor;
                    _okToExpand = newFactor <= kMaxExpansionFactor;
                    compiler.CurrentExpansionFactor = newFactor;
                }
            }
        }

        public void Dispose() => _compiler.CurrentExpansionFactor = _savedExpansionFactor;

        public readonly bool OkToExpand => _okToExpand;
    }

    public static RegExpNode ToNode(int min, int max, bool isGreedy, RegExpTree body, RegExpCompiler compiler,
        RegExpNode onSuccess, bool notAtStart = false)
    {
        // x{f, t} becomes this:
        //
        //             (r++)<-.
        //               |     `
        //               |     (x)
        //               v     ^
        //      (r=0)-->(?)---/ [if r < t]
        //               |
        //   [if r >= f] \----> ...
        //

        // 15.10.2.5 RepeatMatcher algorithm.
        // The parser has already eliminated the case where max is 0.  In the case
        // where max_match is zero the parser has removed the quantifier if min was
        // > 0 and removed the atom if min was 0.  See AddQuantifierToAtom.

        // If we know that we cannot match zero length then things are a little
        // simpler since we don't need to make the special zero length match check
        // from step 2.1.  If the min and max are small we can unroll a little in
        // this case.
        const int kMaxUnrolledMinMatches = 3;  // Unroll (foo)+ and (foo){3,}
        const int kMaxUnrolledMaxMatches = 3;  // Unroll (foo)? and (foo){x,3}
        if (max == 0) return onSuccess;  // This can happen due to recursion.
        bool bodyCanBeEmpty = body.MinMatch == 0;
        int bodyStartReg = RegExpCompiler.kNoRegister;
        Interval captureRegisters = body.CaptureRegisters(new StackLimiter(RegExpNode.kRecursionBudget));
        if (!captureRegisters.IsValid)
        {
            compiler.SetRegExpTooBig();
            return new EndNode(EndNode.Action.BACKTRACK, compiler.Flags);
        }
        // At the start of the next iteration of a quantifier the captures must be
        // cleared, so that /(?:x(.)?z){2}/ when applied to "xyzxz" captures ""
        // (rather than "y" from the first repeat). However, if the max number of
        // iterations is 1 then there is no 'next repeat' so we don't need to do this.
        bool needsCaptureClearing = !captureRegisters.IsEmpty && max != 1;
        bool wantUnroll = compiler.Optimize && RegExpCompiler.s_regexpUnroll;
        if (bodyCanBeEmpty)
        {
            bodyStartReg = compiler.AllocateRegister();
        }
        else if (wantUnroll && !needsCaptureClearing)
        {
            // Only unroll if there are no captures and the body can't be
            // empty.
            {
                using var limiter = new ExpansionLimiter(compiler, min + (max != min ? 1 : 0));
                if (min > 0 && min <= kMaxUnrolledMinMatches && limiter.OkToExpand)
                {
                    int newMax = max == kInfinity ? max : max - min;
                    // Recurse once to get the loop or optional matches after the fixed
                    // ones.
                    RegExpNode answer = ToNode(0, newMax, isGreedy, body, compiler, onSuccess, true);
                    // Unroll the forced matches from 0 to min.  This can cause chains of
                    // TextNodes (which the parser does not generate).  These should be
                    // combined if it turns out they hinder good code generation.
                    for (int i = 0; i < min; i++) answer = body.ToNode(compiler, answer);
                    return answer;
                }
            }
            if (max <= kMaxUnrolledMaxMatches && min == 0)
            {
                Debug.Assert(0 < max);  // Due to the 'if' above.
                using var limiter = new ExpansionLimiter(compiler, max);
                if (limiter.OkToExpand)
                {
                    // Unroll the optional matches up to max.
                    RegExpNode answer = onSuccess;
                    for (int i = 0; i < max; i++)
                    {
                        var alternation = new ChoiceNode(2, compiler.Flags);
                        if (isGreedy)
                        {
                            alternation.AddAlternative(new GuardedAlternative(body.ToNode(compiler, answer)));
                            alternation.AddAlternative(new GuardedAlternative(onSuccess));
                        }
                        else
                        {
                            alternation.AddAlternative(new GuardedAlternative(onSuccess));
                            alternation.AddAlternative(new GuardedAlternative(body.ToNode(compiler, answer)));
                        }
                        answer = alternation;
                        if (notAtStart && !compiler.ReadBackward) alternation.SetNotAtStart();
                    }
                    return answer;
                }
            }
        }
        bool hasMin = min > 0;
        bool hasMax = max < kInfinity;
        bool needsCounter = hasMin || hasMax;
        int regCtr = needsCounter ? compiler.AllocateRegister() : RegExpCompiler.kNoRegister;
        var center = new LoopChoiceNode(body.MinMatch == 0, compiler.ReadBackward, compiler.Flags);
        if (notAtStart && !compiler.ReadBackward) center.SetNotAtStart();
        RegExpNode loopReturn = center;
        if (needsCounter) loopReturn = ActionNode.IncrementRegister(regCtr, loopReturn, compiler.Flags);
        if (bodyCanBeEmpty)
        {
            // If the body can be empty we need to check if it was and then
            // backtrack.
            loopReturn = ActionNode.EmptyMatchCheck(bodyStartReg, regCtr, min, loopReturn, compiler.Flags);
        }
        RegExpNode bodyNode = body.ToNode(compiler, loopReturn);
        if (bodyNode.IsBacktrack())
        {
            // Body can never match. If there is a minimum number of iterations that
            // means this whole part of the regexp can't match, so we just return the
            // never-match (backtrack) node.
            if (hasMin) return bodyNode;
            // Since there is no minimum number of iterations and the body can't match
            // we can go straight to whatever comes after the quantifier.
            return onSuccess;
        }
        if (bodyCanBeEmpty)
        {
            // If the body can be empty we need to store the start position
            // so we can bail out if it was empty.
            bodyNode = ActionNode.RestorePosition(bodyStartReg, bodyNode, compiler.Flags);
        }
        if (needsCaptureClearing)
        {
            // Before entering the body of this loop we need to clear captures.
            bodyNode = ActionNode.ClearCaptures(captureRegisters, bodyNode, compiler.Flags);
        }
        var bodyAlt = new GuardedAlternative(bodyNode);
        if (hasMax) bodyAlt.AddGuard(new Guard(regCtr, Guard.Relation.LT, max));
        var restAlt = new GuardedAlternative(onSuccess);
        if (hasMin) restAlt.AddGuard(new Guard(regCtr, Guard.Relation.GEQ, min));
        if (isGreedy)
        {
            center.AddLoopAlternative(bodyAlt);
            center.AddContinueAlternative(restAlt);
        }
        else
        {
            center.AddContinueAlternative(restAlt);
            center.AddLoopAlternative(bodyAlt);
        }
        RegExpNode result = center;
        if (min > 0 && body.MinMatch > 0 && !compiler.ReadBackward)
        {
            int eats = Math.Min(255, Math.Min(256, min) * Math.Min(256, body.MinMatch));
            result = ActionNode.EatsAtLeastAction(eats, result, compiler.Flags);
        }
        if (needsCounter) result = ActionNode.SetRegisterForLoop(regCtr, 0, result, compiler.Flags);
        return result;
    }
}

public sealed partial class RegExpAssertion
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        switch (AssertionType)
        {
            case Type.START_OF_LINE:
                return AssertionNode.AfterNewline(onSuccess, compiler.Flags);
            case Type.START_OF_INPUT:
                return AssertionNode.AtStart(onSuccess, compiler.Flags);
            case Type.BOUNDARY:
                return NeedsUnicodeCaseEquivalents(compiler.Flags)
                    ? ToNodeHelpers.BoundaryAssertionAsLookaround(compiler, onSuccess, Type.BOUNDARY)
                    : AssertionNode.AtBoundary(onSuccess, compiler.Flags);
            case Type.NON_BOUNDARY:
                return NeedsUnicodeCaseEquivalents(compiler.Flags)
                    ? ToNodeHelpers.BoundaryAssertionAsLookaround(compiler, onSuccess, Type.NON_BOUNDARY)
                    : AssertionNode.AtNonBoundary(onSuccess, compiler.Flags);
            case Type.END_OF_INPUT:
                return AssertionNode.AtEnd(onSuccess, compiler.Flags);
            case Type.END_OF_BUFFER:
            {
                // \Z matches at end-of-input, OR end-of-input preceded by a single
                // line terminator, OR end-of-input preceded by a trailing \r\n.
                // Desugar as:
                //   (?: (?:\r\n | [LF CR LS PS]) AT_END ) | AT_END
                // wrapped so that the inner consumption is a positive lookahead
                // (state is restored on success).
                int stackPointerRegister = compiler.AllocateRegister();
                int positionRegister = compiler.AllocateRegister();
                var lookahead = new RegExpLookaround.Builder(true, onSuccess, compiler, stackPointerRegister,
                    positionRegister);
                RegExpNode submatchSuccess = lookahead.OnMatchSuccess;
                // Alt A: \r\n atom then AT_END (inside the lookahead).
                var crlfAtom = new RegExpAtom("\r\n");
                var crlfElms = new List<TextElement>(1) { TextElement.FromAtom(crlfAtom) };
                AssertionNode crlfAtEnd = AssertionNode.AtEnd(submatchSuccess, compiler.Flags);
                var crlfMatcher = new TextNode(crlfElms, false, crlfAtEnd, compiler.Flags);
                // Alt B: [LF CR LS PS] then AT_END.
                var ltAtom = new RegExpClassRanges(StandardCharacterSet.kLineTerminator);
                AssertionNode ltAtEnd = AssertionNode.AtEnd(submatchSuccess, compiler.Flags);
                var ltMatcher = new TextNode(ltAtom, false, ltAtEnd, compiler.Flags);
                // Inner choice: CRLF first, then single LT.
                var innerChoice = new ChoiceNode(2, compiler.Flags);
                innerChoice.AddAlternative(new GuardedAlternative(crlfMatcher));
                innerChoice.AddAlternative(new GuardedAlternative(ltMatcher));
                // Wrap inner choice in a positive lookahead.
                RegExpNode lookaheadNode = lookahead.ForMatch(compiler, innerChoice);
                // Outer choice: either the trailing-terminator lookahead matches, or
                // we're already at end-of-input.
                var result = new ChoiceNode(2, compiler.Flags);
                result.AddAlternative(new GuardedAlternative(lookaheadNode));
                result.AddAlternative(new GuardedAlternative(AssertionNode.AtEnd(onSuccess, compiler.Flags)));
                return result;
            }
            case Type.END_OF_LINE:
            {
                // Compile $ in multiline regexps as an alternation with a positive
                // lookahead in one side and an end-of-input on the other side.
                // We need two registers for the lookahead.
                int stackPointerRegister = compiler.AllocateRegister();
                int positionRegister = compiler.AllocateRegister();
                // The ChoiceNode to distinguish between a newline and end-of-input.
                var result = new ChoiceNode(2, compiler.Flags);
                // Create a newline atom.
                var newlineRanges = new List<CharacterRange>(3);
                CharacterRange.AddClassEscape(StandardCharacterSet.kLineTerminator, newlineRanges, false);
                ActionNode submatchSuccess = ActionNode.PositiveSubmatchSuccess(stackPointerRegister,
                    positionRegister, 0, -1, onSuccess, compiler.Flags);
                var newlineAtom = new RegExpClassRanges(StandardCharacterSet.kLineTerminator);
                var newlineMatcher = new TextNode(newlineAtom, false, submatchSuccess, compiler.Flags);
                // Create an end-of-input matcher.
                RegExpNode endOfLine = ActionNode.BeginPositiveSubmatch(stackPointerRegister, positionRegister,
                    newlineMatcher, submatchSuccess, compiler.Flags);
                // Add the two alternatives to the ChoiceNode.
                result.AddAlternative(new GuardedAlternative(endOfLine));
                result.AddAlternative(new GuardedAlternative(AssertionNode.AtEnd(onSuccess, compiler.Flags)));
                return result;
            }
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
    }
}

public sealed partial class RegExpBackReference
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        RegExpNode backrefNode = onSuccess;
        // Only one of the captures in the list can actually match. Since
        // back-references to unmatched captures are treated as empty, we can simply
        // create back-references to all possible captures.
        foreach (RegExpCapture capture in _captures)
        {
            backrefNode = new BackReferenceNode(RegExpCapture.StartRegister(capture.Index),
                RegExpCapture.EndRegister(capture.Index), compiler.ReadBackward, backrefNode, compiler.Flags);
        }
        return backrefNode;
    }
}

public sealed partial class RegExpEmpty
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess) => onSuccess;
}

public sealed partial class RegExpGroup
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        // ModifiersScope.
        RegExpFlags previousFlags = compiler.Flags;
        compiler.Flags = Flags;
        try
        {
            return Body.ToNode(compiler, onSuccess);
        }
        finally
        {
            compiler.Flags = previousFlags;
        }
    }
}

public sealed partial class RegExpLookaround
{
    public sealed class Builder
    {
        readonly bool _isPositive;
        readonly RegExpNode _onMatchSuccess;
        readonly RegExpNode _onSuccess;
        readonly int _stackPointerRegister;
        readonly int _positionRegister;

        public Builder(bool isPositive, RegExpNode onSuccess, RegExpCompiler compiler, int stackPointerRegister,
            int positionRegister, int captureRegisterCount = 0, int captureRegisterStart = 0)
        {
            _isPositive = isPositive;
            _onSuccess = onSuccess;
            _stackPointerRegister = stackPointerRegister;
            _positionRegister = positionRegister;
            if (_isPositive)
            {
                _onMatchSuccess = ActionNode.PositiveSubmatchSuccess(stackPointerRegister, positionRegister,
                    captureRegisterCount, captureRegisterStart, _onSuccess, compiler.Flags);
            }
            else
            {
                _onMatchSuccess = new NegativeSubmatchSuccess(stackPointerRegister, positionRegister,
                    captureRegisterCount, captureRegisterStart, compiler.Flags);
            }
        }

        public RegExpNode OnMatchSuccess => _onMatchSuccess;

        public RegExpNode ForMatch(RegExpCompiler compiler, RegExpNode match)
        {
            if (_isPositive)
            {
                ActionNode onMatchSuccess = _onMatchSuccess.AsActionNode()!;
                return ActionNode.BeginPositiveSubmatch(_stackPointerRegister, _positionRegister, match,
                    onMatchSuccess, compiler.Flags);
            }
            // We use a ChoiceNode to represent the negative lookaround. The first
            // alternative is the negative match. On success, the end node backtracks.
            // On failure, the second alternative is tried and leads to success.
            // NegativeLookaroundChoiceNode is a special ChoiceNode that ignores the
            // first exit when calculating quick checks.
            ChoiceNode choiceNode = new NegativeLookaroundChoiceNode(new GuardedAlternative(match),
                new GuardedAlternative(_onSuccess), compiler.Flags);
            return ActionNode.BeginNegativeSubmatch(_stackPointerRegister, _positionRegister, choiceNode,
                compiler.Flags);
        }
    }

    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        int stackPointerRegister = compiler.AllocateRegister();
        int positionRegister = compiler.AllocateRegister();

        const int registersPerCapture = 2;
        const int registerOfFirstCapture = 2;
        int registerCount = CaptureCount * registersPerCapture;
        int registerStart = registerOfFirstCapture + CaptureFrom * registersPerCapture;

        bool wasReadingBackward = compiler.ReadBackward;
        compiler.ReadBackward = LookaroundType == Type.LOOKBEHIND;
        var builder = new Builder(IsPositive, onSuccess, compiler, stackPointerRegister, positionRegister,
            registerCount, registerStart);
        RegExpNode match = Body.ToNode(compiler, builder.OnMatchSuccess);
        if (match.IsBacktrack() && (IsPositive || compiler.IsRegExpTooBig))
        {
            compiler.ReadBackward = wasReadingBackward;
            return match;
        }
        RegExpNode result = builder.ForMatch(compiler, match);
        compiler.ReadBackward = wasReadingBackward;
        return result;
    }
}

public sealed partial class RegExpCapture
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess) =>
        ToNode(Body, Index, compiler, onSuccess);

    public static RegExpNode ToNode(RegExpTree body, int index, RegExpCompiler compiler, RegExpNode onSuccess)
    {
        int startReg = StartRegister(index);
        int endReg = EndRegister(index);
        if (compiler.ReadBackward) (startReg, endReg) = (endReg, startReg);
        RegExpNode storeEnd = ActionNode.StorePosition(endReg, onSuccess, compiler.Flags);
        RegExpNode bodyNode = body.ToNode(compiler, storeEnd);
        if (bodyNode.IsBacktrack()) return bodyNode;
        return ActionNode.StorePosition(startReg, bodyNode, compiler.Flags);
    }
}

internal static class AssertionSequenceRewriter
{
    // TODO(jgruber): Consider moving this to a separate AST tree rewriter pass
    // instead of sprinkling rewrites into the AST->Node conversion process.
    public static List<RegExpTree> MaybeRewrite(List<RegExpTree> terms, RegExpCompiler compiler)
    {
        RegExpFlags flags = compiler.Flags;

        bool hasAssertion = false;
        bool needsFlattening = false;

        for (int i = 0; i < terms.Count; i++)
        {
            RegExpTree t = terms[i];
            if (t.IsAssertion())
            {
                hasAssertion = true;
            }
            else if (t.IsAlternative() || (t.IsGroup() && t.AsGroup()!.Flags == flags))
            {
                needsFlattening = true;
            }
        }

        if (!hasAssertion && !needsFlattening) return terms;

        var flattened = new List<RegExpTree>(terms.Count);
        uint seenAssertions = 0;
        int sequenceStartIdx = -1;

        for (int i = 0; i < terms.Count; i++)
        {
            AppendFlattenedAndFold(flattened, terms[i], flags, ref seenAssertions, ref sequenceStartIdx);
        }

        return flattened;
    }

    // Appends a term to the flattened list, unwrapping same-flag Groups and
    // Alternatives while folding consecutive assertions in a single pass.
    //
    // All assertions are zero width, so a consecutive sequence of assertions is
    // order-independent. We optimize consecutive assertions by:
    // 1. Folding all identical assertions (e.g. \b\b -> \b).
    // 2. Collapsing conflicting assertion combinations (e.g. \b\B) into a single
    //    failure node (ClassRanges).
    static void AppendFlattenedAndFold(List<RegExpTree> list, RegExpTree term, RegExpFlags flags,
        ref uint seenAssertions, ref int sequenceStartIdx)
    {
        if (term.IsAssertion())
        {
            RegExpAssertion assertion = term.AsAssertion()!;
            uint bit = 1u << (int)assertion.AssertionType;

            if ((seenAssertions & bit) != 0)
            {
                // Duplicate assertion! Skip adding it to the list.
                return;
            }

            seenAssertions |= bit;

            const uint alwaysFailsMask =
                1u << (int)RegExpAssertion.Type.BOUNDARY | 1u << (int)RegExpAssertion.Type.NON_BOUNDARY;

            if ((seenAssertions & alwaysFailsMask) == alwaysFailsMask)
            {
                // Replace the current assertion sequence with a single node that always
                // fails.
                Debug.Assert(sequenceStartIdx != -1);
                list.Rewind(sequenceStartIdx);
                var ranges = new List<CharacterRange>(0);
                list.Add(new RegExpClassRanges(ranges));
                return;
            }

            if (sequenceStartIdx == -1) sequenceStartIdx = list.Count;
            list.Add(term);
        }
        else if (term.IsAlternative())
        {
            List<RegExpTree> nodes = term.AsAlternative()!.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                AppendFlattenedAndFold(list, nodes[i], flags, ref seenAssertions, ref sequenceStartIdx);
            }
        }
        else if (term.IsGroup())
        {
            RegExpGroup group = term.AsGroup()!;
            if (group.Flags == flags)
            {
                AppendFlattenedAndFold(list, group.Body, flags, ref seenAssertions, ref sequenceStartIdx);
            }
            else
            {
                seenAssertions = 0;
                sequenceStartIdx = -1;
                list.Add(term);
            }
        }
        else
        {
            seenAssertions = 0;
            sequenceStartIdx = -1;
            list.Add(term);
        }
    }
}

public sealed partial class RegExpAlternative
{
    protected override RegExpNode ToNodeImpl(RegExpCompiler compiler, RegExpNode onSuccess)
    {
        List<RegExpTree> children = AssertionSequenceRewriter.MaybeRewrite(Nodes, compiler);

        RegExpNode current = onSuccess;
        if (compiler.ReadBackward)
        {
            for (int i = 0; i < children.Count; i++) current = children[i].ToNode(compiler, current);
        }
        else
        {
            for (int i = children.Count - 1; i >= 0; i--) current = children[i].ToNode(compiler, current);
        }
        return current;
    }
}
