// Port of RegExp.prototype[@@match], [@@matchAll], [@@replace], [@@search],
// [@@split] and %RegExpStringIteratorPrototype%.next: src/builtins/
// regexp-match.tq, regexp-match-all.tq, regexp-replace.tq, regexp-search.tq,
// regexp-split.tq and the fast bodies of builtins-regexp-gen.cc
// (RegExpMatchGlobal, RegExpReplaceGlobalSimpleString,
// RegExpPrototypeSplitBody, RegExpExecInternal_Batched,
// CreateRegExpStringIterator), plus regexp::Utils::RegExpExec.
using System.Buffers;
using V8Sharp.RegExp;
using V8Sharp.Runtime;

namespace V8Sharp.Builtins;

public static partial class BuiltinsRegExp
{
    /// <summary>
    /// regexp::Utils::RegExpExec: like RegExpExec, but calls the builtin exec
    /// function (%RegExp.prototype.exec%) when "exec" is not callable.
    /// </summary>
    internal static JSValue RegExpExecUtils(Isolate isolate, JSReceiver regexp, JSString s)
    {
        JSValue exec = JSReceiver.GetProperty(isolate, regexp, ReadOnlyRoots.exec_string);
        if (ObjectOps.IsCallable(exec))
        {
            JSValue result = Execution.Call(isolate, exec, regexp, [s]);
            if (!result.IsJSReceiver && !result.IsNull) isolate.ThrowTypeError(MessageTemplate.InvalidRegExpExecResult);
            return result;
        }
        if (regexp is not JSRegExp)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.exec",
                regexp);
        }
        return Execution.Call(isolate, isolate.NativeContext.RegExpExecFunction, regexp, [s]);
    }

    // ---- Batched global exec -----------------------------------------------------------------

    /// <summary>
    /// The per-match callback of RegExpExecInternal_Batched (the
    /// OncePerMatchFunction lambdas of RegExpMatchGlobal and
    /// RegExpReplaceGlobalSimpleString).
    /// </summary>
    internal interface IBatchedMatchHandler
    {
        void OnMatch(int matchStart, int matchEnd);
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::RegExpExecInternal_Batched: runs a global
    /// regexp over the subject from 0, a batch of matches per engine call,
    /// calling the handler for each match; sets lastIndex to 0 first and fills
    /// the last match info at the end. Returns the number of matches.
    /// </summary>
    static int RegExpExecInternalBatched<THandler>(Isolate isolate, JSRegExp regexp, JSString subject, RegExpData data,
        ref THandler handler) where THandler : struct, IBatchedMatchHandler
    {
        int registerCountPerMatch = JSRegExp.RegistersForCaptureCount(data.CaptureCount);
        int resultOffsetsVectorLength = Math.Max(registerCountPerMatch, GlobalExecRunner.kJSRegexpStaticOffsetsVectorSize);
        int[] vector = ArrayPool<int>.Shared.Rent(resultOffsetsVectorLength);
        try
        {
            Span<int> resultOffsetsVector = vector.AsSpan(0, resultOffsetsVectorLength);
            bool isUnicode = (regexp.Flags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
            int lastMatchOffsets = 0;
            int startOfLastMatch = 0;
            int lastIndex = 0;
            regexp.LastIndex = JSValue.Zero;
            int maxMatchesInBatch = resultOffsetsVectorLength / registerCountPerMatch;
            // Initialize such that we always enter the loop initially:
            int numMatchesInBatch = maxMatchesInBatch;
            int numMatches = 0;
            int subjectLength = subject.Length;

            // Loop over multiple batch executions:
            while (numMatchesInBatch >= maxMatchesInBatch)
            {
                // RegExpExecInternal fails for a lastIndex past the end.
                if (lastIndex > subjectLength) break;
                numMatchesInBatch = JSRegExp.ExecRaw(isolate, data, subject, lastIndex, resultOffsetsVector);
                if (numMatchesInBatch == 0) break;
                numMatches += numMatchesInBatch;

                // Loop over the current batch of results:
                for (int m = 0; m < numMatchesInBatch; m++)
                {
                    int offset = m * registerCountPerMatch;
                    int start = resultOffsetsVector[offset];
                    int end = resultOffsetsVector[offset + 1];
                    handler.OnMatch(start, end);
                    lastMatchOffsets = offset;
                    startOfLastMatch = start;
                    lastIndex = end;
                }

                if (startOfLastMatch != lastIndex) continue;
                // For zero-length matches we need to run AdvanceStringIndex.
                lastIndex = (int)AdvanceStringIndex(subject, lastIndex, isUnicode);
            }

            // If there were no matches, just return.
            if (numMatches == 0) return 0;

            // Otherwise initialize the last match info and the result JSArray.
            JSRegExp.SetLastMatchInfo(isolate, RegExpMatchInfo.Get(isolate), subject, data.CaptureCount,
                resultOffsetsVector.Slice(lastMatchOffsets, registerCountPerMatch));
            return numMatches;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(vector);
        }
    }

    struct MatchGlobalHandler(Isolate isolate, JSString subject, List<JSValue> array) : IBatchedMatchHandler
    {
        public void OnMatch(int matchStart, int matchEnd) => array.Add(isolate.Factory.NewSubString(subject, matchStart, matchEnd));
    }

    /// <summary>RegExpBuiltinsAssembler::RegExpMatchGlobal: the array of all matched strings, or null.</summary>
    static JSValue RegExpMatchGlobal(Isolate isolate, JSRegExp regexp, JSString subject, RegExpData data)
    {
        var array = new List<JSValue>(8);
        var handler = new MatchGlobalHandler(isolate, subject, array);
        int numMatches = RegExpExecInternalBatched(isolate, regexp, subject, data, ref handler);
        // No matches, return null.
        if (numMatches == 0) return JSValue.Null;
        var elements = new FixedArray(array.Count);
        array.CopyTo(elements.Data);
        return isolate.Factory.NewJSArrayWithElements(elements);
    }

    struct ReplaceGlobalSimpleStringHandler(IncrementalStringBuilder builder, JSString subject, JSString replaceString)
        : IBatchedMatchHandler
    {
        public int LastMatchEnd;

        public void OnMatch(int matchStart, int matchEnd)
        {
            // Append the slice between this and the previous match.
            builder.AppendChars(subject.FlatSpan()[LastMatchEnd..matchStart]);
            // Append the replace_string.
            if (replaceString.Length != 0) builder.AppendString(replaceString);
            LastMatchEnd = matchEnd;
        }
    }

    /// <summary>
    /// RegExpBuiltinsAssembler::RegExpReplaceGlobalSimpleString: global replace
    /// with a string that has no '$'.
    /// </summary>
    static JSString RegExpReplaceGlobalSimpleString(Isolate isolate, JSRegExp regexp, JSString subject, RegExpData data,
        JSString replaceString)
    {
        var builder = new IncrementalStringBuilder(isolate);
        var handler = new ReplaceGlobalSimpleStringHandler(builder, subject, replaceString);
        RegExpExecInternalBatched(isolate, regexp, subject, data, ref handler);
        builder.AppendChars(subject.FlatSpan()[handler.LastMatchEnd..]);
        return builder.Finish();
    }

    // ---- @@match (regexp-match.tq) ----------------------------------------------------------

    /// <summary>RegExpPrototypeMatchBody.</summary>
    internal static JSValue RegExpPrototypeMatchBody(Isolate isolate, JSReceiver regexp, JSString s, bool isFastPath)
    {
        bool isGlobal;
        JSString flagsString = ReadOnlyRoots.empty_string;
        // 4. Let flags be ? ToString(? Get(rx, "flags")).
        // 5. If flags does not contain "g", then
        //    a. Return ? RegExpExec(rx, S).
        if (isFastPath)
        {
            isGlobal = (((JSRegExp)regexp).Flags & RegExpFlags.Global) != 0;
        }
        else
        {
            JSValue flags = JSReceiver.GetProperty(isolate, regexp, ReadOnlyRoots.flags_string);
            flagsString = BuiltinsString.ToStringInline(isolate, flags);
            isGlobal = flagsString.FlatSpan().Contains('g');
        }

        if (!isGlobal)
        {
            return isFastPath ? RegExpPrototypeExecBody(isolate, (JSRegExp)regexp, s, true) : RegExpExec(isolate, regexp, s);
        }

        // The fast paths:
        if (isFastPath)
        {
            var jsregexp = (JSRegExp)regexp;
            RegExpData data = jsregexp.Data!;
            if (data.TypeTag == RegExpKind.Atom) return RuntimeRegExp.RegExpMatchGlobalAtom(isolate, jsregexp, s, data);
            return RegExpMatchGlobal(isolate, jsregexp, s, data);
        }

        // .. and the generic slow path.
        // a. If flags contains "u" or flags contains "v", let fullUnicode be
        // true. Otherwise, let fullUnicode be false.
        ReadOnlySpan<char> flagChars = flagsString.FlatSpan();
        bool fullUnicode = flagChars.Contains('u') || flagChars.Contains('v');

        // b. Perform ? Set(rx, "lastIndex", +0𝔽, true).
        StoreLastIndex(isolate, regexp, JSValue.Zero, false);

        var array = new List<JSValue>(8);
        while (true)
        {
            JSValue resultTemp = RegExpExec(isolate, regexp, s);
            if (resultTemp.IsNull)
            {
                if (array.Count == 0) return JSValue.Null;
                var elements = new FixedArray(array.Count);
                array.CopyTo(elements.Data);
                return isolate.Factory.NewJSArrayWithElements(elements);
            }
            JSString match = BuiltinsString.ToStringInline(isolate, ObjectOps.GetElement(isolate, resultTemp, 0));
            // Store the match, growing the fixed array if needed.
            array.Add(match);
            // Advance last index if the match is the empty string.
            if (match.Length != 0) continue;
            JSValue lastIndex = LoadLastIndex(isolate, regexp, false);
            lastIndex = ObjectOps.ToLength(isolate, lastIndex);
            double newLastIndex = AdvanceStringIndex(s, lastIndex.Number, fullUnicode);
            StoreLastIndex(isolate, regexp, JSValue.FromNumber(newLastIndex), false);
        }
    }

    /// <summary>RegExpMatchFast (regexp-match.tq).</summary>
    internal static JSValue RegExpMatchFast(Isolate isolate, JSRegExp receiver, JSString s) =>
        RegExpPrototypeMatchBody(isolate, receiver, s, true);

    /// <summary>RegExpPrototypeMatch (regexp-match.tq).</summary>
    public static JSValue RegExpPrototypeMatch(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.@@match",
                args.Receiver);
        }
        JSString s = BuiltinsString.ToStringInline(isolate, args.AtOrUndefined(1));
        // Strict: Reads global and unicode properties.
        if (!IsFastRegExpStrict(isolate, receiver)) return RegExpPrototypeMatchBody(isolate, receiver, s, false);
        return RegExpMatchFast(isolate, (JSRegExp)receiver, s);
    }

    // ---- @@search (regexp-search.tq) ---------------------------------------------------------

    /// <summary>RegExpPrototypeSearchBodyFast.</summary>
    static JSValue RegExpPrototypeSearchBodyFast(Isolate isolate, JSRegExp regexp, JSString s)
    {
        // Grab the initial value of last index.
        JSValue previousLastIndex = regexp.LastIndex;
        // Ensure last index is 0.
        regexp.LastIndex = JSValue.Zero;
        // Call exec.
        RegExpMatchInfo? matchIndices = RegExpPrototypeExecBodyWithoutResultFast(isolate, regexp, s);
        // Reset last index.
        regexp.LastIndex = previousLastIndex;
        // Return the index of the match, or -1.
        return JSValue.FromInt(matchIndices is null ? -1 : matchIndices.Capture(0));
    }

    /// <summary>RegExpPrototypeSearchBodySlow.</summary>
    static JSValue RegExpPrototypeSearchBodySlow(Isolate isolate, JSReceiver regexp, JSString s)
    {
        // Grab the initial value of last index.
        JSValue previousLastIndex = SlowLoadLastIndex(isolate, regexp);
        // Ensure last index is 0.
        if (!ObjectOps.SameValue(previousLastIndex, JSValue.Zero)) SlowStoreLastIndex(isolate, regexp, JSValue.Zero);
        // Call exec.
        JSValue execResult = RegExpExec(isolate, regexp, s);
        // Reset last index if necessary.
        JSValue currentLastIndex = SlowLoadLastIndex(isolate, regexp);
        if (!ObjectOps.SameValue(currentLastIndex, previousLastIndex)) SlowStoreLastIndex(isolate, regexp, previousLastIndex);
        // Return -1 if no match was found.
        if (execResult.IsNull) return JSValue.FromInt(-1);
        // Return the index of the match.
        if (IsRegExpResult(isolate, execResult)) return ((JSObject)execResult.Object).InObjectPropertyRef(kRegExpResultIndexIndex);
        return ObjectOps.GetProperty(isolate, execResult, ReadOnlyRoots.index_string);
    }

    /// <summary>RegExpSearchFast (regexp-search.tq).</summary>
    internal static JSValue RegExpSearchFast(Isolate isolate, JSRegExp receiver, JSString s) =>
        RegExpPrototypeSearchBodyFast(isolate, receiver, s);

    /// <summary>RegExpPrototypeSearch (regexp-search.tq).</summary>
    public static JSValue RegExpPrototypeSearch(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.@@search",
                args.Receiver);
        }
        JSString s = BuiltinsString.ToStringInline(isolate, args.AtOrUndefined(1));
        if (IsFastRegExpPermissive(isolate, receiver)) return RegExpSearchFast(isolate, (JSRegExp)receiver, s);
        return RegExpPrototypeSearchBodySlow(isolate, receiver, s);
    }

    // ---- @@replace (regexp-replace.tq) ------------------------------------------------------

    /// <summary>
    /// RegExpReplaceCallableNoExplicitCaptures: calls the replace function for
    /// each match string of the RegExpExecMultiple result, replacing it in place.
    /// </summary>
    static int RegExpReplaceCallableNoExplicitCaptures(Isolate isolate, FixedArray matchesElements, int matchesCapacity,
        JSString s, JSValue replaceFn)
    {
        int matchStart = 0;
        for (int i = 0; i < matchesCapacity; i++)
        {
            JSValue element = matchesElements[i];
            if (element.IsNumber)
            {
                // Element represents a slice. The slice's match start and end is either
                // encoded as one or two smis. A positive smi indicates a single smi
                // encoding (see ReplacementStringBuilder::AddSubjectSlice()).
                int elSmi = (int)element.Number;
                if (elSmi > 0)
                {
                    matchStart = (elSmi >> 11) + (elSmi & 0x7FF);
                }
                else
                {
                    // For two smi encoding, the length is negative followed by the
                    // match start.
                    int nextEl = (int)matchesElements[++i].Number;
                    matchStart = nextEl - elSmi;
                }
            }
            else if (element.HeapObjectOrNull is JSString elString)
            {
                // Element represents the matched substring, which is then passed to the
                // replace function.
                JSValue replacementObj = Execution.Call(isolate, replaceFn, JSValue.Undefined,
                    [elString, JSValue.FromInt(matchStart), s]);
                JSString replacement = BuiltinsString.ToStringInline(isolate, replacementObj);
                matchesElements[i] = replacement;
                matchStart += elString.Length;
            }
            else
            {
                // No more elements.
                return i;
            }
        }
        return matchesCapacity;
    }

    /// <summary>
    /// RegExpReplaceCallableWithExplicitCaptures: calls the replace function
    /// with the argument array of each match (Reflect.apply in V8).
    /// </summary>
    static int RegExpReplaceCallableWithExplicitCaptures(Isolate isolate, FixedArray matchesElements, int matchesCapacity,
        JSValue replaceFn)
    {
        for (int i = 0; i < matchesCapacity; i++)
        {
            JSValue element = matchesElements[i];
            if (element.IsTheHole) return i;  // No more elements.
            if (element.HeapObjectOrNull is not JSArray elArray) continue;
            // The JSArray is expanded into the function args.
            var args = (FixedArray)elArray.Elements;
            JSValue replacementObj = Execution.Call(isolate, replaceFn, JSValue.Undefined,
                args.Data.AsSpan(0, (int)elArray.Length.Number));
            // Overwrite the i'th element in the results with the string
            // we got back from the callback function.
            matchesElements[i] = BuiltinsString.ToStringInline(isolate, replacementObj);
        }
        return matchesCapacity;
    }

    /// <summary>RegExpReplaceFastGlobalCallable (regexp-replace.tq).</summary>
    static JSValue RegExpReplaceFastGlobalCallable(Isolate isolate, JSRegExp regexp, JSString s, JSValue replaceFn)
    {
        regexp.LastIndex = JSValue.Zero;
        FixedArray? result = RuntimeRegExp.RegExpExecMultiple(isolate, regexp, s, RegExpMatchInfo.Get(isolate));
        regexp.LastIndex = JSValue.Zero;
        // If no matches, return the subject string.
        if (result is null) return s;
        FixedArray matches = result;
        int matchesCapacityInt = matches.Length;
        // Reload last match info since it might have changed.
        int nofCaptures = RegExpMatchInfo.Get(isolate).NumberOfCaptureRegisters;
        // If the number of captures is two then there are no explicit captures in
        // the regexp, just the implicit capture that captures the whole match. In
        // this case we can simplify quite a bit and end up with something faster.
        int matchesLength = nofCaptures == 2
            ? RegExpReplaceCallableNoExplicitCaptures(isolate, matches, matchesCapacityInt, s, replaceFn)
            : RegExpReplaceCallableWithExplicitCaptures(isolate, matches, matchesCapacityInt, replaceFn);
        return RuntimeRegExp.StringBuilderConcat(isolate, matches, matchesLength, s);
    }

    /// <summary>RegExpReplaceFastString (regexp-replace.tq).</summary>
    static JSString RegExpReplaceFastString(Isolate isolate, JSRegExp regexp, JSString s, JSString replaceString)
    {
        // The fast path is reached only if {receiver} is an unmodified JSRegExp
        // instance, {replace_value} is non-callable, and ToString({replace_value})
        // does not contain '$', i.e. we're doing a simple string replacement.
        if ((regexp.Flags & RegExpFlags.Global) != 0)
        {
            regexp.LastIndex = JSValue.Zero;
            return RegExpReplaceGlobalSimpleString(isolate, regexp, s, regexp.Data!, replaceString);
        }
        RegExpMatchInfo? match = RegExpPrototypeExecBodyWithoutResultFast(isolate, regexp, s);
        if (match is null) return s;
        int matchStart = match.Capture(0);
        int matchEnd = match.Capture(1);
        ReadOnlySpan<char> chars = s.FlatSpan();
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendChars(chars[..matchStart]);
        if (replaceString.Length != 0) builder.AppendString(replaceString);
        builder.AppendChars(chars[matchEnd..]);
        return builder.Finish();
    }

    /// <summary>RegExpReplace (regexp-replace.tq): the fast @@replace of an unmodified regexp.</summary>
    internal static JSValue RegExpReplace(Isolate isolate, JSRegExp regexp, JSString s, JSValue replaceValue)
    {
        // 2. Is {replace_value} callable?
        if (ObjectOps.IsCallable(replaceValue))
        {
            return (regexp.Flags & RegExpFlags.Global) != 0
                ? RegExpReplaceFastGlobalCallable(isolate, regexp, s, replaceValue)
                : RuntimeRegExp.StringReplaceNonGlobalRegExpWithFunction(isolate, s, regexp, (JSReceiver)replaceValue.Object);
        }
        JSString replaceString = BuiltinsString.ToStringInline(isolate, replaceValue);
        // ToString(replaceValue) could potentially change the shape of the
        // RegExp object. Recheck that we are still on the fast path and bail
        // to runtime otherwise.
        if (!IsFastRegExpStrict(isolate, regexp) || StringSearch.IndexOf(replaceString.FlatSpan(), '$', 0) != -1)
        {
            return RuntimeRegExp.RegExpReplaceRT(isolate, regexp, s, replaceString);
        }
        return RegExpReplaceFastString(isolate, regexp, s, replaceString);
    }

    /// <summary>RegExpPrototypeReplace (regexp-replace.tq).</summary>
    public static JSValue RegExpPrototypeReplace(Isolate isolate, in BuiltinArguments args)
    {
        const string methodName = "RegExp.prototype.@@replace";
        JSValue stringArg = args.AtOrUndefined(1);
        JSValue replaceValue = args.AtOrUndefined(2);
        // Let rx be the this value.
        // If Type(rx) is not Object, throw a TypeError exception.
        if (args.Receiver.HeapObjectOrNull is not JSReceiver rx)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, methodName, args.Receiver);
        }
        // Let S be ? ToString(string).
        JSString s = BuiltinsString.ToStringInline(isolate, stringArg);
        // Fast-path checks: 1. Is the {receiver} an unmodified JSRegExp instance?
        if (IsFastRegExpStrict(isolate, rx)) return RegExpReplace(isolate, (JSRegExp)rx, s, replaceValue);
        isolate.CountUsage("kRegExpReplaceCalledOnSlowRegExp");
        return RuntimeRegExp.RegExpReplaceRT(isolate, rx, s, replaceValue);
    }

    // ---- @@split (regexp-split.tq, RegExpPrototypeSplitBody) ------------------------------------

    /// <summary>
    /// RegExpBuiltinsAssembler::RegExpPrototypeSplitBody: the fast @@split of an
    /// unmodified, non-sticky regexp with a Smi limit.
    /// </summary>
    static JSArray RegExpPrototypeSplitBody(Isolate isolate, JSRegExp regexp, JSString s, int limit)
    {
        Factory factory = isolate.Factory;
        RegExpData data = regexp.Data!;

        // Only an unlimited split is cacheable, since the cached array holds every
        // part.
        bool isCacheable = limit == JSValue.SmiMaxValue && s.IsInternalized;
        if (isCacheable)
        {
            FixedArray? cached = RegExpResultsCache.Lookup(isolate, s, data, out int[]? lastMatchCache,
                RegExpResultsCache.ResultsCacheType.REGEXP_SPLIT_SUBSTRINGS);
            if (cached is not null)
            {
                // ReplayLastMatchInfo: an empty snapshot means the split loop never
                // matched, so LastMatchInfo is left alone.
                if (lastMatchCache is { Length: > 0 })
                {
                    JSRegExp.SetLastMatchInfo(isolate, RegExpMatchInfo.Get(isolate), s,
                        JSRegExp.CaptureCountForRegisters(lastMatchCache.Length), lastMatchCache);
                }
                return factory.NewJSArrayWithElements(cached);
            }
        }

        // If the limit is zero, return an empty array.
        if (limit == 0) return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);

        int captureCount = data.CaptureCount;
        int registerCountPerMatch = JSRegExp.RegistersForCaptureCount(captureCount);
        int[] vector = ArrayPool<int>.Shared.Rent(registerCountPerMatch);
        try
        {
            // Allocate space for exactly one result, forcing the engine to return
            // after each match. This is necessary due to the specialized
            // AdvanceStringIndex logic below.
            Span<int> resultOffsetsVector = vector.AsSpan(0, registerCountPerMatch);
            int stringLength = s.Length;

            // If passed the empty {string}, return either an empty array or a singleton
            // array depending on whether the {regexp} matches.
            if (stringLength == 0)
            {
                int numMatches = JSRegExp.ExecRaw(isolate, data, s, 0, resultOffsetsVector);
                if (numMatches != 0)
                {
                    JSRegExp.SetLastMatchInfo(isolate, RegExpMatchInfo.Get(isolate), s, captureCount, resultOffsetsVector);
                    return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);
                }
                var single = new FixedArray(1);
                single[0] = s;
                return factory.NewJSArrayWithElements(single);
            }

            var array = new List<JSValue>(8);
            int lastMatchedUntil = 0;
            int nextSearchFrom = 0;
            // Whether the loop wrote LastMatchInfo. Only then may a hit replay it.
            bool didMatch = false;
            bool isUnicode = (regexp.Flags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
            RegExpMatchInfo? matchInfo = null;
            bool reachedEnd;
            while (true)
            {
                // We're done if we've reached the end of the string.
                if (nextSearchFrom == stringLength)
                {
                    reachedEnd = true;
                    break;
                }
                // Search for the given {regexp}.
                int numMatches = JSRegExp.ExecRaw(isolate, data, s, nextSearchFrom, resultOffsetsVector);
                // We're done if no match was found.
                if (numMatches == 0)
                {
                    reachedEnd = true;
                    break;
                }
                int matchFrom = resultOffsetsVector[0];
                // We're also done if the match is at the end of the string.
                if (matchFrom == stringLength)
                {
                    reachedEnd = true;
                    break;
                }
                // Set the LastMatchInfo.
                matchInfo = JSRegExp.SetLastMatchInfo(isolate, RegExpMatchInfo.Get(isolate), s, captureCount,
                    resultOffsetsVector);
                didMatch = true;
                int matchTo = matchInfo.Capture(1);

                // Advance index and continue if the match is empty.
                if (matchTo == nextSearchFrom && matchTo == lastMatchedUntil)
                {
                    nextSearchFrom = (int)AdvanceStringIndex(s, nextSearchFrom, isUnicode);
                    continue;
                }

                // A valid match was found, add the new substring to the array.
                array.Add(factory.NewSubString(s, lastMatchedUntil, matchFrom));
                if (array.Count == limit)
                {
                    reachedEnd = false;
                    break;
                }

                // Add all captures to the array.
                bool full = false;
                for (int reg = 2; reg < registerCountPerMatch; reg += 2)
                {
                    int from = matchInfo.Capture(reg);
                    int to = matchInfo.Capture(reg + 1);
                    array.Add(to == -1 ? JSValue.Undefined : factory.NewSubString(s, from, to));
                    if (array.Count == limit)
                    {
                        full = true;
                        break;
                    }
                }
                if (full)
                {
                    reachedEnd = false;
                    break;
                }

                lastMatchedUntil = matchTo;
                nextSearchFrom = matchTo;
            }

            if (reachedEnd)
            {
                // push_suffix_and_out
                array.Add(factory.NewSubString(s, lastMatchedUntil, stringLength));
            }
            var elements = new FixedArray(array.Count);
            array.CopyTo(elements.Data);
            JSArray result = factory.NewJSArrayWithElements(elements);

            // Only reached when the split ran to the end of the subject. A split cut
            // short by {limit} exits through {out} and must not be cached.
            if (reachedEnd && isCacheable)
            {
                // Snapshot rather than reconstruct which match was the last one: the
                // final match may contribute no element, e.g. one at the end of the
                // subject.
                int[] lastMatchSnapshot = didMatch ? RegExpMatchInfo.Get(isolate).Captures.ToArray() : [];
                RegExpResultsCache.Enter(isolate, s, data, elements, lastMatchSnapshot,
                    RegExpResultsCache.ResultsCacheType.REGEXP_SPLIT_SUBSTRINGS);
            }
            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(vector);
        }
    }

    /// <summary>RegExpSplit (regexp-split.tq): the fast @@split.</summary>
    internal static JSValue RegExpSplit(Isolate isolate, JSRegExp regexp, JSString s, JSValue limit)
    {
        int sanitizedLimit;
        // We need to be extra-strict and require the given limit to be either
        // undefined or a positive smi. We can't call ToUint32(maybe_limit) since
        // that might move us onto the slow path, resulting in ordering spec
        // violations (see https://crbug.com/801171).
        if (limit.IsUndefined)
        {
            sanitizedLimit = JSValue.SmiMaxValue;
        }
        else if (!(limit.IsSmi && limit.Number >= 0))
        {
            return RuntimeRegExp.RegExpSplit(isolate, regexp, s, limit);
        }
        else
        {
            sanitizedLimit = (int)limit.Number;
        }

        // Due to specific shortcuts we take on the fast path (specifically, we
        // don't allocate a new regexp instance as specced), we need to ensure that
        // the given regexp is non-sticky to avoid invalid results. See
        // crbug.com/v8/6706.
        if ((regexp.Flags & RegExpFlags.Sticky) != 0)
        {
            return RuntimeRegExp.RegExpSplit(isolate, regexp, s, JSValue.FromInt(sanitizedLimit));
        }

        // We're good to go on the fast path, which is inlined here.
        return RegExpPrototypeSplitBody(isolate, regexp, s, sanitizedLimit);
    }

    /// <summary>RegExpPrototypeSplit (regexp-split.tq).</summary>
    public static JSValue RegExpPrototypeSplit(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is not JSReceiver receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver, "RegExp.prototype.@@split",
                args.Receiver);
        }
        JSString s = BuiltinsString.ToStringInline(isolate, args.AtOrUndefined(1));
        JSValue limit = args.AtOrUndefined(2);
        // Strict: Reads the flags property.
        if (!IsFastRegExpStrict(isolate, receiver)) return RuntimeRegExp.RegExpSplit(isolate, receiver, s, limit);
        return RegExpSplit(isolate, (JSRegExp)receiver, s, limit);
    }

    // ---- @@matchAll (regexp-match-all.tq) ---------------------------------------------------

    /// <summary>RegExpMatchAllAssembler::CreateRegExpStringIterator.</summary>
    internal static JSRegExpStringIterator CreateRegExpStringIterator(Isolate isolate, JSValue regexp, JSString s, bool global,
        bool fullUnicode)
    {
        var iterator = (JSRegExpStringIterator)isolate.Factory.NewJSObjectFromMap(
            isolate.NativeContext.InitialRegExpStringIteratorPrototypeMap);
        iterator.IteratingRegExp = regexp;
        iterator.IteratedString = s;
        iterator.Global = global;
        iterator.Unicode = fullUnicode;
        iterator.Done = false;
        return iterator;
    }

    /// <summary>RegExpPrototypeMatchAllImpl (regexp-match-all.tq).</summary>
    internal static JSValue RegExpPrototypeMatchAllImpl(Isolate isolate, JSValue receiverValue, JSValue stringValue)
    {
        // 1. Let R be the this value.
        // 2. If Type(R) is not Object, throw a TypeError exception.
        if (receiverValue.HeapObjectOrNull is not JSReceiver receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver,
                "RegExp.prototype.@@matchAll", receiverValue);
        }
        // 3. Let S be ? ToString(O).
        JSString s = BuiltinsString.ToStringInline(isolate, stringValue);

        JSValue matcher;
        bool global;
        bool unicode;
        NativeContext nativeContext = isolate.NativeContext;

        // 'FastJSRegExp' uses the strict fast path check because following code
        // uses the flags property.
        if (IsFastRegExpStrict(isolate, receiver))
        {
            var fastRegExp = (JSRegExp)receiver;
            JSString source = fastRegExp.Source;
            // 4. Let C be ? SpeciesConstructor(R, %RegExp%).
            // 5. Let flags be ? ToString(? Get(R, "flags")).
            // 6. Let matcher be ? Construct(C, « R, flags »).
            JSString flags = FlagsGetter(isolate, fastRegExp, true);
            JSRegExp matcherRegExp = RegExpCreate(isolate, source, flags);
            matcher = matcherRegExp;
            // 7. Let lastIndex be ? ToLength(? Get(R, "lastIndex")).
            // 8. Perform ? Set(matcher, "lastIndex", lastIndex, true).
            matcherRegExp.LastIndex = fastRegExp.LastIndex;
            // 9. If flags contains "g", let global be true.
            global = (matcherRegExp.Flags & RegExpFlags.Global) != 0;
            // 11. If flags contains "u" or "v", let fullUnicode be true.
            unicode = (matcherRegExp.Flags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
        }
        else
        {
            // 4. Let C be ? SpeciesConstructor(R, %RegExp%).
            JSFunction regexpFun = nativeContext.RegExpFunction;
            JSValue speciesConstructor = ObjectOps.SpeciesConstructor(isolate, receiver, regexpFun);
            if (!ReferenceEquals(speciesConstructor.HeapObjectOrNull, regexpFun)) isolate.CountUsage("kRegExpCustomSpecies");
            // 5. Let flags be ? ToString(? Get(R, "flags")).
            JSValue flagsObj = JSReceiver.GetProperty(isolate, receiver, ReadOnlyRoots.flags_string);
            JSString flagsString = BuiltinsString.ToStringInline(isolate, flagsObj);
            // 6. Let matcher be ? Construct(C, « R, flags »).
            matcher = Execution.New(isolate, speciesConstructor, [receiver, flagsString]);
            // 7. Let lastIndex be ? ToLength(? Get(R, "lastIndex")).
            JSValue lastIndex = ObjectOps.ToLength(isolate, SlowLoadLastIndex(isolate, receiver));
            // 8. Perform ? Set(matcher, "lastIndex", lastIndex, true).
            SlowStoreLastIndex(isolate, matcher, lastIndex);
            // 9./10. global, 11./12. fullUnicode from the flags string.
            ReadOnlySpan<char> flagChars = flagsString.FlatSpan();
            global = flagChars.Contains('g');
            unicode = flagChars.Contains('u') || flagChars.Contains('v');
            bool hasFlagMismatch;
            if (matcher.HeapObjectOrNull is JSRegExp matcherRegExp)
            {
                bool isGlobal = (matcherRegExp.Flags & RegExpFlags.Global) != 0;
                bool isUnicode = (matcherRegExp.Flags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
                hasFlagMismatch = isGlobal != global || isUnicode != unicode;
            }
            else
            {
                hasFlagMismatch = true;
            }
            if (hasFlagMismatch) isolate.CountUsage("kRegExpMatcherFlagsMismatch");
        }
        // 13. Return ! CreateRegExpStringIterator(matcher, S, global, fullUnicode).
        return CreateRegExpStringIterator(isolate, matcher, s, global, unicode);
    }

    /// <summary>RegExpPrototypeMatchAll (regexp-match-all.tq).</summary>
    public static JSValue RegExpPrototypeMatchAll(Isolate isolate, in BuiltinArguments args) =>
        RegExpPrototypeMatchAllImpl(isolate, args.Receiver, args.AtOrUndefined(1));

    /// <summary>RegExpStringIteratorPrototypeNext (regexp-match-all.tq).</summary>
    public static JSValue RegExpStringIteratorPrototypeNext(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be the this value.
        // 2. If Type(O) is not Object, throw a TypeError exception.
        // 3. If O does not have all of the internal slots of a RegExp String
        // Iterator Object Instance (see 5.3), throw a TypeError exception.
        if (args.Receiver.HeapObjectOrNull is not JSRegExpStringIterator receiver)
        {
            return BuiltinsString.ThrowTypeError(isolate, MessageTemplate.IncompatibleMethodReceiver,
                "%RegExpStringIterator%.prototype.next", args.Receiver);
        }

        // 4. If O.[[Done]] is true, then
        //   a. Return ! CreateIterResultObject(undefined, true).
        if (receiver.Done) return BuiltinsString.CreateIterResultObject(isolate, JSValue.Undefined, true);

        // 5. Let R be O.[[iteratingRegExp]].
        var iteratingRegExp = (JSReceiver)receiver.IteratingRegExp.Object;
        // 6. Let S be O.[[IteratedString]].
        JSString iteratingString = receiver.IteratedString;

        // 7. Let global be O.[[Global]].
        // 8. Let fullUnicode be O.[[Unicode]].
        // 9. Let match be ? RegExpExec(R, S).
        JSValue match;
        bool isFastRegExp = false;
        if (IsFastRegExpPermissive(isolate, iteratingRegExp))
        {
            var regexp = (JSRegExp)iteratingRegExp;
            // Only use the fast path if there aren't flag mismatches.
            bool isGlobal = (regexp.Flags & RegExpFlags.Global) != 0;
            bool isUnicode = (regexp.Flags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
            if ((isGlobal == receiver.Global) & (isUnicode == receiver.Unicode))
            {
                JSValue lastIndex = LoadLastIndexAsLength(isolate, regexp, true);
                RegExpMatchInfo? matchIndices = RegExpPrototypeExecBodyWithoutResult(isolate, regexp, iteratingString,
                    lastIndex, true);
                if (matchIndices is null) return IfNoMatch(isolate, receiver);
                match = ConstructNewResultFromMatchInfo(isolate, regexp, matchIndices, iteratingString, lastIndex);
                isFastRegExp = true;
            }
            else
            {
                match = RegExpExec(isolate, iteratingRegExp, iteratingString);
                if (match.IsNull) return IfNoMatch(isolate, receiver);
            }
        }
        else
        {
            match = RegExpExec(isolate, iteratingRegExp, iteratingString);
            if (match.IsNull) return IfNoMatch(isolate, receiver);
        }

        // 11. Else,
        // b. Else, handle non-global case first.
        if (!receiver.Global)
        {
            // i. Set O.[[Done]] to true.
            receiver.Done = true;
            // ii. Return ! CreateIterResultObject(match, false).
            return BuiltinsString.CreateIterResultObject(isolate, match, false);
        }

        // a. If global is true,
        if (isFastRegExp)
        {
            // i. Let matchStr be ? ToString(? Get(match, "0")).
            var fastMatch = (JSArray)match.Object;
            var matchStr = (JSString)((FixedArray)fastMatch.Elements)[0].Object;
            // When iterating_regexp is fast, we assume it stays fast even after
            // accessing the first match from the RegExp result.
            var regexp = (JSRegExp)iteratingRegExp;
            if (matchStr.Length == 0)
            {
                // 1. Let thisIndex be ? ToLength(? Get(R, "lastIndex")).
                double thisIndex = regexp.LastIndex.Number;
                // 2. Let nextIndex be ! AdvanceStringIndex(S, thisIndex, fullUnicode).
                double nextIndex = AdvanceStringIndex(iteratingString, thisIndex, receiver.Unicode);
                // 3. Perform ? Set(R, "lastIndex", nextIndex, true).
                regexp.LastIndex = JSValue.FromNumber(nextIndex);
            }
            // iii. Return ! CreateIterResultObject(match, false).
            return BuiltinsString.CreateIterResultObject(isolate, match, false);
        }

        {
            // i. Let matchStr be ? ToString(? Get(match, "0")).
            JSString matchStr = BuiltinsString.ToStringInline(isolate, ObjectOps.GetElement(isolate, match, 0));
            if (matchStr.Length == 0)
            {
                // 1. Let thisIndex be ? ToLength(? Get(R, "lastIndex")).
                JSValue lastIndex = SlowLoadLastIndex(isolate, iteratingRegExp);
                double thisIndex = ObjectOps.ToLength(isolate, lastIndex).Number;
                // 2. Let nextIndex be ! AdvanceStringIndex(S, thisIndex, fullUnicode).
                double nextIndex = AdvanceStringIndex(iteratingString, thisIndex, receiver.Unicode);
                // 3. Perform ? Set(R, "lastIndex", nextIndex, true).
                SlowStoreLastIndex(isolate, iteratingRegExp, JSValue.FromNumber(nextIndex));
            }
            // iii. Return ! CreateIterResultObject(match, false).
            return BuiltinsString.CreateIterResultObject(isolate, match, false);
        }
    }

    /// <summary>The IfNoMatch label: done, and return { undefined, true }.</summary>
    static JSValue IfNoMatch(Isolate isolate, JSRegExpStringIterator receiver)
    {
        // a. Set O.[[Done]] to true.
        receiver.Done = true;
        // b. Return ! CreateIterResultObject(undefined, true).
        return BuiltinsString.CreateIterResultObject(isolate, JSValue.Undefined, true);
    }
}
