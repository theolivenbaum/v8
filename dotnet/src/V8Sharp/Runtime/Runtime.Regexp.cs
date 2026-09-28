// Port of src/runtime/runtime-regexp.cc: CompiledReplacement, the global
// replace paths (StringReplaceGlobalRegExpWithString, ...WithEmptyString,
// StringReplaceGlobalAtomRegExpWithString), RegExpExecMultiple
// (SearchRegExpMultiple), StringReplaceNonGlobalRegExpWithFunction,
// RegExpSplit, RegExpReplaceRT, RegExpMatchGlobalAtom, StringSplit,
// RegExpExec, RegExpBuildIndices, RegExpInitializeAndCompile,
// RegExpStringFromFlags, and the String::Match implementations
// (MatchInfoBackedMatch, VectorBackedMatch).
//
// These are plain static methods with typed parameters; the runtime dispatch
// table (the interpreter's CallRuntime) adapts its arguments to them.
using System.Buffers;
using V8Sharp.Builtins;
using V8Sharp.RegExp;

namespace V8Sharp.Runtime;

public static class RuntimeRegExp
{
    /// <summary>Code::kMaxArguments.</summary>
    const int kMaxArguments = (1 << 16) - 2;

    /// <summary>GetArgcForReplaceCallable: -1 when there are too many captures.</summary>
    internal static int GetArgcForReplaceCallable(int numCaptures, bool hasNamedCaptures)
    {
        const int kAdditionalArgsWithoutNamedCaptures = 2;
        const int kAdditionalArgsWithNamedCaptures = 3;
        if (numCaptures > kMaxArguments) return -1;
        int argc = hasNamedCaptures
            ? numCaptures + kAdditionalArgsWithNamedCaptures
            : numCaptures + kAdditionalArgsWithoutNamedCaptures;
        return argc > kMaxArguments ? -1 : argc;
    }

    // ---- CompiledReplacement ------------------------------------------------------------

    /// <summary>
    /// CompiledReplacement: a replacement pattern parsed once into parts
    /// (subject prefix/suffix/captures and literal pieces), equivalent to
    /// String::GetSubstitution applied at each match.
    /// </summary>
    internal sealed class CompiledReplacement
    {
        enum PartType
        {
            SUBJECT_PREFIX = 1,
            SUBJECT_SUFFIX,
            SUBJECT_CAPTURE,
            REPLACEMENT_SUBSTRING,
            REPLACEMENT_STRING,
            EMPTY_REPLACEMENT,
        }

        // tag > 0: a PartType; tag <= 0: -(start) of a literal substring ending at data.
        readonly record struct ReplacementPart(int Tag, int Data);

        readonly List<ReplacementPart> _parts = new(8);
        JSString _replacement = ReadOnlyRoots.empty_string;

        public int Parts => _parts.Count;

        /// <summary>CompiledReplacement::Compile: returns whether the replacement is simple (no '$' patterns).</summary>
        public bool Compile(RegExpData regexpData, JSString replacement, int captureCount, int subjectLength)
        {
            _replacement = replacement;
            RegExpData? captureNameMap = captureCount > 0 && regexpData.HasCaptureNameMap ? regexpData : null;
            return ParseReplacementPattern(replacement.FlatSpan(), captureNameMap, captureCount, subjectLength);
        }

        bool ParseReplacementPattern(ReadOnlySpan<char> characters, RegExpData? captureNameMap, int captureCount,
            int subjectLength)
        {
            // Equivalent to String::GetSubstitution, except that this method converts
            // the replacement string into an internal representation that avoids
            // repeated parsing when used repeatedly.
            int length = characters.Length;
            int last = 0;
            for (int i = 0; i < length; i++)
            {
                char c = characters[i];
                if (c != '$') continue;
                int nextIndex = i + 1;
                if (nextIndex == length) break;  // No next character!
                char c2 = characters[nextIndex];
                switch (c2)
                {
                    case '$':
                        if (i > last)
                        {
                            // There is a substring before. Include the first "$".
                            _parts.Add(new(-last, nextIndex));
                            last = nextIndex + 1;  // Continue after the second "$".
                        }
                        else
                        {
                            // Let the next substring start with the second "$".
                            last = nextIndex;
                        }
                        i = nextIndex;
                        break;
                    case '`':
                        if (i > last) _parts.Add(new(-last, i));
                        _parts.Add(new((int)PartType.SUBJECT_PREFIX, 0));
                        i = nextIndex;
                        last = i + 1;
                        break;
                    case '\'':
                        if (i > last) _parts.Add(new(-last, i));
                        _parts.Add(new((int)PartType.SUBJECT_SUFFIX, subjectLength));
                        i = nextIndex;
                        last = i + 1;
                        break;
                    case '&':
                        if (i > last) _parts.Add(new(-last, i));
                        _parts.Add(new((int)PartType.SUBJECT_CAPTURE, 0));
                        i = nextIndex;
                        last = i + 1;
                        break;
                    case >= '0' and <= '9':
                    {
                        int captureRef = c2 - '0';
                        if (captureRef > captureCount)
                        {
                            i = nextIndex;
                            continue;
                        }
                        int secondDigitIndex = nextIndex + 1;
                        if (secondDigitIndex < length)
                        {
                            // Peek ahead to see if we have two digits.
                            char c3 = characters[secondDigitIndex];
                            if (c3 is >= '0' and <= '9')
                            {
                                int doubleDigitRef = captureRef * 10 + c3 - '0';
                                if (doubleDigitRef <= captureCount)
                                {
                                    nextIndex = secondDigitIndex;
                                    captureRef = doubleDigitRef;
                                }
                            }
                        }
                        if (captureRef > 0)
                        {
                            if (i > last) _parts.Add(new(-last, i));
                            _parts.Add(new((int)PartType.SUBJECT_CAPTURE, captureRef));
                            last = nextIndex + 1;
                        }
                        i = nextIndex;
                        break;
                    }
                    case '<':
                    {
                        if (captureNameMap is null)
                        {
                            i = nextIndex;
                            break;
                        }
                        // Scan until the next '>', and let the enclosed substring be the
                        // groupName.
                        int nameStartIndex = nextIndex + 1;
                        int closingBracketIndex = characters[nameStartIndex..].IndexOf('>');
                        // If no closing bracket is found, '$<' is treated as a string
                        // literal.
                        if (closingBracketIndex == -1)
                        {
                            i = nextIndex;
                            break;
                        }
                        closingBracketIndex += nameStartIndex;
                        if (i > last) _parts.Add(new(-last, i));
                        ReadOnlySpan<char> requestedName = characters[nameStartIndex..closingBracketIndex];
                        // If capture is undefined or does not exist, replace the text
                        // through the following '>' with the empty string.
                        // For duplicated capture group names we don't know which of them
                        // matches at this point in time, so we create a separate
                        // replacement for each possible match. When applying the
                        // replacement unmatched groups will be skipped.
                        int captureIndex = 0;
                        int captureNameMapIndex = 0;
                        while (captureIndex != -1)
                        {
                            captureIndex = captureNameMap.LookupNamedCapture(requestedName, ref captureNameMapIndex);
                            _parts.Add(captureIndex == -1
                                ? new((int)PartType.EMPTY_REPLACEMENT, 0)
                                : new((int)PartType.SUBJECT_CAPTURE, captureIndex));
                        }
                        last = closingBracketIndex + 1;
                        i = closingBracketIndex;
                        break;
                    }
                    default:
                        i = nextIndex;
                        break;
                }
            }
            if (length > last)
            {
                // Replacement is simple.  Do not use Apply to do the replacement.
                if (last == 0) return true;
                _parts.Add(new(-last, length));
            }
            return false;
        }

        /// <summary>CompiledReplacement::Apply: appends the replacement for one match.</summary>
        public void Apply(IncrementalStringBuilder builder, ReadOnlySpan<char> subject, int matchFrom, int matchTo,
            ReadOnlySpan<int> match)
        {
            ReadOnlySpan<char> replacement = _replacement.FlatSpan();
            foreach (ReplacementPart part in _parts)
            {
                int tag = part.Tag;
                if (tag <= 0)
                {
                    // A replacement string slice.
                    builder.AppendChars(replacement[(-tag)..part.Data]);
                    continue;
                }
                switch ((PartType)tag)
                {
                    case PartType.SUBJECT_PREFIX:
                        if (matchFrom > 0) builder.AppendChars(subject[..matchFrom]);
                        break;
                    case PartType.SUBJECT_SUFFIX:
                    {
                        int subjectLength = part.Data;
                        if (matchTo < subjectLength) builder.AppendChars(subject[matchTo..subjectLength]);
                        break;
                    }
                    case PartType.SUBJECT_CAPTURE:
                    {
                        int capture = part.Data;
                        int from = match[capture * 2];
                        int to = match[capture * 2 + 1];
                        if (from >= 0 && to > from) builder.AppendChars(subject[from..to]);
                        break;
                    }
                    case PartType.EMPTY_REPLACEMENT:
                        break;
                }
            }
        }
    }

    // ---- String::Match implementations -------------------------------------------------

    /// <summary>MatchInfoBackedMatch: the match described by the last match info.</summary>
    internal sealed class MatchInfoBackedMatch : JSString.Match
    {
        readonly Isolate _isolate;
        readonly JSString _subject;
        readonly RegExpMatchInfo _matchInfo;
        readonly RegExpData? _captureNameMap;

        public MatchInfoBackedMatch(Isolate isolate, RegExpData regexpData, JSString subject, RegExpMatchInfo matchInfo)
        {
            _isolate = isolate;
            _matchInfo = matchInfo;
            _subject = JSString.Flatten(isolate, subject);
            if (regexpData.TypeSupportsCaptures && regexpData.HasCaptureNameMap) _captureNameMap = regexpData;
        }

        public override JSString GetMatch() => BuiltinsRegExp.GenericCaptureGetter(_isolate, _matchInfo, 0, out _);

        public override JSString GetPrefix() => _isolate.Factory.NewSubString(_subject, 0, _matchInfo.Capture(0));

        public override JSString GetSuffix() =>
            _isolate.Factory.NewSubString(_subject, _matchInfo.Capture(1), _subject.Length);

        public override bool HasNamedCaptures() => _captureNameMap is not null;

        public override int CaptureCount() => _matchInfo.NumberOfCaptureRegisters / 2;

        public override JSString? GetCapture(int i, out bool captureExists) =>
            BuiltinsRegExp.GenericCaptureGetter(_isolate, _matchInfo, i, out captureExists);

        public override JSString? GetNamedCapture(JSString name, out CaptureState state)
        {
            int captureNameMapIndex = 0;
            ReadOnlySpan<char> nameChars = name.FlatSpan();
            while (true)
            {
                int captureIndex = _captureNameMap!.LookupNamedCapture(nameChars, ref captureNameMapIndex);
                if (captureIndex == -1)
                {
                    state = CaptureState.Unmatched;
                    return ReadOnlyRoots.empty_string;
                }
                if (BuiltinsRegExp.IsMatchedCapture(_matchInfo, captureIndex))
                {
                    state = CaptureState.Matched;
                    return BuiltinsRegExp.GenericCaptureGetter(_isolate, _matchInfo, captureIndex, out _);
                }
            }
        }
    }

    /// <summary>VectorBackedMatch: the match of a user-visible exec result (RegExpReplaceRT).</summary>
    internal sealed class VectorBackedMatch : JSString.Match
    {
        readonly Isolate _isolate;
        readonly JSString _subject;
        readonly JSString _match;
        readonly uint _matchPosition;
        readonly List<JSValue> _captures;
        readonly JSReceiver? _groupsObj;

        public VectorBackedMatch(Isolate isolate, JSString subject, JSString match, uint matchPosition, List<JSValue> captures,
            JSValue groupsObj)
        {
            _isolate = isolate;
            _match = match;
            _matchPosition = matchPosition;
            _captures = captures;
            _subject = JSString.Flatten(isolate, subject);
            if (!groupsObj.IsUndefined) _groupsObj = (JSReceiver)groupsObj.Object;
        }

        public override JSString GetMatch() => _match;

        public override JSString GetPrefix()
        {
            // match_position_ and match_ are user-controlled, hence we manually clamp
            // the index here.
            uint end = Math.Min((uint)_subject.Length, _matchPosition);
            return _isolate.Factory.NewSubString(_subject, 0, (int)end);
        }

        public override JSString GetSuffix()
        {
            // match_position_ and match_ are user-controlled, hence we manually clamp
            // the index here.
            uint start = (uint)Math.Min((long)_subject.Length, (long)_matchPosition + _match.Length);
            return _isolate.Factory.NewSubString(_subject, (int)start, _subject.Length);
        }

        public override bool HasNamedCaptures() => _groupsObj is not null;

        public override int CaptureCount() => _captures.Count;

        public override JSString? GetCapture(int i, out bool captureExists)
        {
            JSValue captureObj = _captures[i];
            if (captureObj.IsUndefined)
            {
                captureExists = false;
                return ReadOnlyRoots.empty_string;
            }
            captureExists = true;
            return ObjectOps.ToString(_isolate, captureObj);
        }

        public override JSString? GetNamedCapture(JSString name, out CaptureState state)
        {
            // Strings representing integer indices are not valid identifiers (and
            // therefore not valid capture names).
            if (name.AsIntegerIndex(out _))
            {
                state = CaptureState.Unmatched;
                return ReadOnlyRoots.empty_string;
            }
            JSValue captureObj = JSReceiver.GetProperty(_isolate, _groupsObj!, _isolate.Factory.InternalizeString(name));
            if (captureObj.IsUndefined)
            {
                state = CaptureState.Unmatched;
                return ReadOnlyRoots.empty_string;
            }
            state = CaptureState.Matched;
            return ObjectOps.ToString(_isolate, captureObj);
        }
    }

    /// <summary>A match without captures (Runtime_GetSubstitution's SimpleMatch).</summary>
    internal sealed class SimpleMatch(JSString match, JSString prefix, JSString suffix) : JSString.Match
    {
        public override JSString GetMatch() => match;
        public override JSString GetPrefix() => prefix;
        public override JSString GetSuffix() => suffix;
        public override int CaptureCount() => 0;
        public override bool HasNamedCaptures() => false;

        public override JSString? GetCapture(int i, out bool captureExists)
        {
            captureExists = false;
            return match;  // Return arbitrary string handle.
        }

        public override JSString? GetNamedCapture(JSString name, out CaptureState state) =>
            throw new InvalidOperationException("UNREACHABLE");
    }

    // ---- Global replace with a string -----------------------------------------------------

    /// <summary>
    /// FindStringIndices: up to <paramref name="limit"/> non-overlapping
    /// occurrences of the pattern, from <paramref name="startIndex"/>.
    /// </summary>
    internal static void FindStringIndices(ReadOnlySpan<char> subject, ReadOnlySpan<char> pattern, List<int> indices,
        uint limit, int startIndex = 0)
    {
        int patternLength = pattern.Length;
        int index = startIndex;
        while (limit > 0)
        {
            index = StringSearch.IndexOf(subject, pattern, index);
            if (index < 0) return;
            indices.Add(index);
            index += patternLength;
            limit--;
        }
    }

    [ThreadStatic] static List<int>? t_regexpIndices;

    /// <summary>GetRewoundRegexpIndicesList: the isolate's reusable index list, cleared.</summary>
    static List<int> GetRewoundRegexpIndicesList()
    {
        List<int> list = t_regexpIndices ??= new List<int>();
        list.Clear();
        return list;
    }

    /// <summary>TruncateRegexpIndicesList: drop a large backing store.</summary>
    static void TruncateRegexpIndicesList()
    {
        const int kMaxRegexpIndicesListCapacity = 8 * 1024 / sizeof(int);
        List<int>? indices = t_regexpIndices;
        if (indices is not null && indices.Capacity > kMaxRegexpIndicesListCapacity)
        {
            indices.Clear();
            indices.TrimExcess();
        }
    }

    /// <summary>StringReplaceGlobalAtomRegExpWithString.</summary>
    static JSString StringReplaceGlobalAtomRegExpWithString(Isolate isolate, JSString subject, RegExpData data,
        JSString replacement, RegExpMatchInfo lastMatchInfo)
    {
        List<int> indices = GetRewoundRegexpIndicesList();
        JSString pattern = data.AtomPattern!;
        ReadOnlySpan<char> subjectChars = subject.FlatSpan();
        ReadOnlySpan<char> patternChars = pattern.FlatSpan();
        ReadOnlySpan<char> replacementChars = replacement.FlatSpan();
        int subjectLen = subjectChars.Length;
        int patternLen = patternChars.Length;
        int replacementLen = replacementChars.Length;

        FindStringIndices(subjectChars, patternChars, indices, 0xFFFFFFFF);
        if (indices.Count == 0) return subject;

        // Detect integer overflow.
        long resultLen64 = ((long)replacementLen - patternLen) * indices.Count + subjectLen;
        if (resultLen64 > JSString.kMaxLength)
        {
            return (JSString)isolate.Throw(isolate.Factory.NewInvalidStringLengthError()).Object;
        }
        int resultLen = (int)resultLen64;
        if (resultLen == 0) return ReadOnlyRoots.empty_string;

        char[] result = new char[resultLen];
        int subjectPos = 0;
        int resultPos = 0;
        foreach (int index in indices)
        {
            // Copy non-matched subject content.
            if (subjectPos < index)
            {
                subjectChars[subjectPos..index].CopyTo(result.AsSpan(resultPos));
                resultPos += index - subjectPos;
            }
            // Replace match.
            if (replacementLen > 0)
            {
                replacementChars.CopyTo(result.AsSpan(resultPos));
                resultPos += replacementLen;
            }
            subjectPos = index + patternLen;
        }
        // Add remaining subject content at the end.
        if (subjectPos < subjectLen) subjectChars[subjectPos..].CopyTo(result.AsSpan(resultPos));

        int last = indices[^1];
        JSRegExp.SetLastMatchInfo(isolate, lastMatchInfo, subject, 0, [last, last + patternLen]);
        TruncateRegexpIndicesList();
        return isolate.Factory.NewStringFromUtf16(new string(result));
    }

    /// <summary>StringReplaceGlobalRegExpWithString.</summary>
    static JSString StringReplaceGlobalRegExpWithString(Isolate isolate, JSString subject, RegExpData regexpData,
        JSString replacement, RegExpMatchInfo lastMatchInfo)
    {
        int captureCount = regexpData.CaptureCount;
        int subjectLength = subject.Length;

        // Ensure the RegExp is compiled so we can access the capture-name map.
        JSRegExp.EnsureFullyCompiled(isolate, regexpData, subject);

        var compiledReplacement = new CompiledReplacement();
        bool simpleReplace = compiledReplacement.Compile(regexpData, replacement, captureCount, subjectLength);

        // Shortcut for simple non-regexp global replacements.
        if (regexpData.TypeTag == RegExpKind.Atom && simpleReplace)
        {
            return StringReplaceGlobalAtomRegExpWithString(isolate, subject, regexpData, replacement, lastMatchInfo);
        }

        using var runner = new GlobalExecRunner(isolate, regexpData, subject);
        ReadOnlySpan<int> currentMatch = runner.FetchNext();
        if (currentMatch.IsEmpty) return subject;

        var builder = new IncrementalStringBuilder(isolate);
        ReadOnlySpan<char> subjectChars = subject.FlatSpan();
        ReadOnlySpan<char> replacementChars = replacement.FlatSpan();
        int prev = 0;
        do
        {
            int start = currentMatch[0];
            int end = currentMatch[1];
            if (prev < start) builder.AppendChars(subjectChars[prev..start]);
            if (simpleReplace) builder.AppendChars(replacementChars);
            else compiledReplacement.Apply(builder, subjectChars, start, end, currentMatch);
            prev = end;
            currentMatch = runner.FetchNext();
        } while (!currentMatch.IsEmpty);

        if (prev < subjectLength) builder.AppendChars(subjectChars[prev..subjectLength]);

        JSRegExp.SetLastMatchInfo(isolate, lastMatchInfo, subject, captureCount, runner.LastSuccessfulMatch());
        return builder.Finish();
    }

    /// <summary>StringReplaceGlobalRegExpWithEmptyString.</summary>
    static JSString StringReplaceGlobalRegExpWithEmptyString(Isolate isolate, JSString subject, RegExpData regexpData,
        RegExpMatchInfo lastMatchInfo)
    {
        // Shortcut for simple non-regexp global replacements.
        if (regexpData.TypeTag == RegExpKind.Atom)
        {
            return StringReplaceGlobalAtomRegExpWithString(isolate, subject, regexpData, ReadOnlyRoots.empty_string,
                lastMatchInfo);
        }

        using var runner = new GlobalExecRunner(isolate, regexpData, subject);
        ReadOnlySpan<int> currentMatch = runner.FetchNext();
        if (currentMatch.IsEmpty) return subject;

        int captureCount = regexpData.CaptureCount;
        int subjectLength = subject.Length;
        int newLength = subjectLength - (currentMatch[1] - currentMatch[0]);
        if (newLength == 0) return ReadOnlyRoots.empty_string;

        ReadOnlySpan<char> subjectChars = subject.FlatSpan();
        char[] answer = ArrayPool<char>.Shared.Rent(newLength);
        try
        {
            int prev = 0;
            int position = 0;
            do
            {
                int start = currentMatch[0];
                int end = currentMatch[1];
                if (prev < start)
                {
                    // Add substring subject[prev;start] to answer string.
                    subjectChars[prev..start].CopyTo(answer.AsSpan(position));
                    position += start - prev;
                }
                prev = end;
                currentMatch = runner.FetchNext();
            } while (!currentMatch.IsEmpty);

            JSRegExp.SetLastMatchInfo(isolate, lastMatchInfo, subject, captureCount, runner.LastSuccessfulMatch());

            if (prev < subjectLength)
            {
                // Add substring subject[prev;length] to answer string.
                subjectChars[prev..].CopyTo(answer.AsSpan(position));
                position += subjectLength - prev;
            }
            if (position == 0) return ReadOnlyRoots.empty_string;
            return isolate.Factory.NewStringFromUtf16(answer.AsSpan(0, position));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(answer);
        }
    }

    /// <summary>
    /// RegExpReplace (runtime-regexp.cc): the legacy @@replace of an unmodified
    /// regexp with a string replacement, which does not call exec.
    /// </summary>
    public static JSString RegExpReplace(Isolate isolate, JSRegExp regexp, JSString s, JSString replace)
    {
        RegExpFlags flags = regexp.Flags;
        bool global = (flags & RegExpFlags.Global) != 0;
        bool sticky = (flags & RegExpFlags.Sticky) != 0;
        replace = JSString.Flatten(isolate, replace);
        RegExpMatchInfo lastMatchInfo = RegExpMatchInfo.Get(isolate);
        RegExpData data = regexp.Data!;
        Factory factory = isolate.Factory;

        if (!global)
        {
            // Non-global regexp search, string replace.
            uint lastIndex = 0;
            if (sticky)
            {
                JSValue lastIndexObj = ObjectOps.ToLength(isolate, regexp.LastIndex);
                lastIndex = PositiveNumberToUint32(lastIndexObj.Number);
            }
            RegExpMatchInfo? matchIndices = null;
            // A lastIndex exceeding the string length always returns null (signalling
            // failure) in RegExpBuiltinExec, thus we can skip the call.
            if (lastIndex <= (uint)s.Length)
            {
                matchIndices = JSRegExp.ExecSingle(isolate, regexp, s, (int)lastIndex, lastMatchInfo);
            }
            if (matchIndices is null)
            {
                if (sticky) regexp.LastIndex = JSValue.Zero;
                return s;
            }
            int startIndex = matchIndices.Capture(0);
            int endIndex = matchIndices.Capture(1);
            if (sticky) regexp.LastIndex = JSValue.FromInt(endIndex);

            var builder = new IncrementalStringBuilder(isolate);
            builder.AppendString(factory.NewSubString(s, 0, startIndex));
            if (replace.Length > 0)
            {
                var m = new MatchInfoBackedMatch(isolate, data, s, matchIndices);
                builder.AppendString(JSString.GetSubstitution(isolate, m, replace));
            }
            builder.AppendString(factory.NewSubString(s, endIndex, s.Length));
            return builder.Finish();
        }

        // Global regexp search, string replace.
        BuiltinsRegExp.UtilsSetLastIndex(isolate, regexp, 0);
        if (replace.Length == 0) return StringReplaceGlobalRegExpWithEmptyString(isolate, s, data, lastMatchInfo);
        return StringReplaceGlobalRegExpWithString(isolate, s, data, replace, lastMatchInfo);
    }

    /// <summary>PositiveNumberToUint32: saturating conversion of a non-negative number.</summary>
    static uint PositiveNumberToUint32(double d)
    {
        if (!(d >= 0)) return 0;
        if (d >= uint.MaxValue) return uint.MaxValue;
        return (uint)d;
    }

    // ---- RegExpExecMultiple ---------------------------------------------------------------

    /// <summary>
    /// ReplacementStringBuilder::AddSubjectSlice: a subject slice encoded as one
    /// Smi (length in bits 0-10, position in bits 11-29) or as two (-length, position).
    /// </summary>
    internal static void AddSubjectSlice(List<JSValue> builder, int from, int to)
    {
        int length = to - from;
        if (length < (1 << 11) && from < (1 << 19))
        {
            builder.Add(JSValue.FromInt(length | (from << 11)));
        }
        else
        {
            // Otherwise encode as two smis.
            builder.Add(JSValue.FromInt(-length));
            builder.Add(JSValue.FromInt(from));
        }
    }

    /// <summary>
    /// CopyMatches: a copy of <paramref name="matches"/> sharing nothing mutable
    /// with it (the per-match argument arrays and their groups objects are copied
    /// when there are named captures).
    /// </summary>
    static FixedArray CopyMatches(Isolate isolate, FixedArray matches, bool hasNamedCaptures)
    {
        var copy = new FixedArray(matches.Data.AsSpan().ToArray());
        if (!hasNamedCaptures) return copy;
        for (int i = 0; i < copy.Length; i++)
        {
            // Subject slices are encoded as smis; only matches carry captures.
            if (copy[i].HeapObjectOrNull is not JSArray match) continue;
            var elements = (FixedArray)match.Elements;
            var newElements = new FixedArray(elements.Data.AsSpan(0, (int)match.Length.Number).ToArray());
            // The groups object is appended last, after match, captures, index and
            // subject.
            int groupsIndex = newElements.Length - 1;
            newElements[groupsIndex] = isolate.Factory.CopyJSObject((JSObject)newElements[groupsIndex].Object);
            copy[i] = isolate.Factory.NewJSArrayWithElements(newElements);
        }
        return copy;
    }

    /// <summary>
    /// SearchRegExpMultiple: all matches of a global regexp as the parts of a
    /// ReplacementStringBuilder (subject slices and, per match, the match string
    /// or the replace-callback argument array), or null for no match.
    /// </summary>
    static FixedArray? SearchRegExpMultiple(Isolate isolate, JSString subject, JSRegExp regexp, RegExpData regexpData,
        RegExpMatchInfo lastMatchArray)
    {
        bool hasCapture = regexpData.CaptureCount != 0;
        int captureCount = regexpData.CaptureCount;
        int subjectLength = subject.Length;
        const int kMinLengthToCache = 0x1000;

        if (subjectLength > kMinLengthToCache)
        {
            FixedArray? cachedAnswer = RegExpResultsCache.Lookup(isolate, subject, regexpData, out int[]? lastMatchCache,
                RegExpResultsCache.ResultsCacheType.REGEXP_MULTIPLE_INDICES);
            if (cachedAnswer is not null)
            {
                // The cache FixedArray is a COW-array and we need to return a copy.
                FixedArray copied = CopyMatches(isolate, cachedAnswer, hasCapture && regexpData.HasCaptureNameMap);
                JSRegExp.SetLastMatchInfo(isolate, lastMatchArray, subject, captureCount, lastMatchCache);
                return copied;
            }
        }

        using var runner = new GlobalExecRunner(isolate, regexpData, subject);
        var builder = new List<JSValue>(16);
        // Position to search from.
        int matchStart = -1;
        int matchEnd = 0;
        bool first = true;
        bool hasNamedCaptures = hasCapture && regexpData.HasCaptureNameMap;
        ReadOnlySpan<char> _ = subject.FlatSpan();
        Factory factory = isolate.Factory;

        while (true)
        {
            ReadOnlySpan<int> currentMatch = runner.FetchNext();
            if (currentMatch.IsEmpty) break;
            matchStart = currentMatch[0];
            if (matchEnd < matchStart) AddSubjectSlice(builder, matchEnd, matchStart);
            matchEnd = currentMatch[1];
            JSString match;
            if (!first)
            {
                match = factory.NewProperSubString(subject, matchStart, matchEnd);
            }
            else
            {
                match = factory.NewSubString(subject, matchStart, matchEnd);
                first = false;
            }

            if (hasCapture)
            {
                // Arguments array to replace function is match, captures, index and
                // subject. If the RegExp contains named captures, they are also passed
                // as the last argument.
                int argc = GetArgcForReplaceCallable(captureCount + 1, hasNamedCaptures);
                var elements = new FixedArray(argc);
                int cursor = 0;
                elements[cursor++] = match;
                for (int i = 1; i <= captureCount; i++)
                {
                    int start = currentMatch[i * 2];
                    if (start >= 0)
                    {
                        int end = currentMatch[i * 2 + 1];
                        elements[cursor++] = factory.NewSubString(subject, start, end);
                    }
                    else
                    {
                        elements[cursor++] = JSValue.Undefined;
                    }
                }
                elements[cursor++] = JSValue.FromInt(matchStart);
                elements[cursor++] = subject;
                if (hasNamedCaptures)
                {
                    elements[cursor++] = BuiltinsRegExp.ConstructNamedCaptureGroupsObject(isolate, regexpData, elements.Data);
                }
                builder.Add(factory.NewJSArrayWithElements(elements));
            }
            else
            {
                builder.Add(match);
            }
        }

        if (matchStart < 0) return null;  // No matches at all.

        // Finished matching, with at least one match.
        if (matchEnd < subjectLength) AddSubjectSlice(builder, matchEnd, subjectLength);

        ReadOnlySpan<int> lastMatch = runner.LastSuccessfulMatch();
        JSRegExp.SetLastMatchInfo(isolate, lastMatchArray, subject, captureCount, lastMatch);

        var result = new FixedArray(builder.Count);
        builder.CopyTo(result.Data);
        if (subjectLength > kMinLengthToCache)
        {
            // Store the last successful match into the array for caching.
            int captureRegisters = JSRegExp.RegistersForCaptureCount(captureCount);
            int[] lastMatchCache = lastMatch[..captureRegisters].ToArray();
            // Cache the result and copy the FixedArray into a COW array. The copy
            // must be deep, or the entry would share its groups objects with the
            // array returned below.
            FixedArray copied = CopyMatches(isolate, result, hasNamedCaptures);
            RegExpResultsCache.Enter(isolate, subject, regexpData, copied, lastMatchCache,
                RegExpResultsCache.ResultsCacheType.REGEXP_MULTIPLE_INDICES);
        }
        return result;
    }

    /// <summary>Runtime_RegExpExecMultiple (only used by the global callable replace).</summary>
    public static FixedArray? RegExpExecMultiple(Isolate isolate, JSRegExp regexp, JSString subject, RegExpMatchInfo lastMatchInfo)
    {
        subject = JSString.Flatten(isolate, subject);
        Debug.Assert((regexp.Flags & RegExpFlags.Global) != 0);
        return SearchRegExpMultiple(isolate, subject, regexp, regexp.Data!, lastMatchInfo);
    }

    /// <summary>
    /// Runtime_StringBuilderConcat: concatenates the parts of a
    /// ReplacementStringBuilder array (strings and encoded subject slices).
    /// </summary>
    public static JSString StringBuilderConcat(Isolate isolate, FixedArray array, int arrayLength, JSString special)
    {
        ReadOnlySpan<char> subject = special.FlatSpan();
        var builder = new IncrementalStringBuilder(isolate);
        for (int i = 0; i < arrayLength; i++)
        {
            JSValue element = array[i];
            if (element.IsNumber)
            {
                int encoded = (int)element.Number;
                int position, length;
                if (encoded > 0)
                {
                    // Position and length encoded in one smi.
                    position = encoded >> 11;
                    length = encoded & 0x7FF;
                }
                else
                {
                    // Position and length encoded in two smis.
                    length = -encoded;
                    position = (int)array[++i].Number;
                }
                builder.AppendChars(subject.Slice(position, length));
            }
            else
            {
                builder.AppendString((JSString)element.Object);
            }
        }
        return builder.Finish();
    }

    // ---- Non-global replace with a function ------------------------------------------------

    /// <summary>Runtime_StringReplaceNonGlobalRegExpWithFunction.</summary>
    public static JSString StringReplaceNonGlobalRegExpWithFunction(Isolate isolate, JSString subject, JSRegExp regexp,
        JSReceiver replaceObj)
    {
        Factory factory = isolate.Factory;
        RegExpMatchInfo lastMatchInfo = RegExpMatchInfo.Get(isolate);
        RegExpData data = regexp.Data!;
        RegExpFlags flags = regexp.Flags;
        bool sticky = (flags & RegExpFlags.Sticky) != 0;
        uint lastIndex = 0;
        if (sticky)
        {
            JSValue lastIndexObj = ObjectOps.ToLength(isolate, regexp.LastIndex);
            lastIndex = PositiveNumberToUint32(lastIndexObj.Number);
        }

        RegExpMatchInfo? matchIndices = null;
        // A lastIndex exceeding the string length always returns null (signalling
        // failure) in RegExpBuiltinExec, thus we can skip the call.
        if (lastIndex <= (uint)subject.Length)
        {
            matchIndices = JSRegExp.ExecSingle(isolate, regexp, subject, (int)lastIndex, lastMatchInfo);
        }
        if (matchIndices is null)
        {
            if (sticky) regexp.LastIndex = JSValue.Zero;
            return subject;
        }

        int index = matchIndices.Capture(0);
        int endOfMatch = matchIndices.Capture(1);
        if (sticky) regexp.LastIndex = JSValue.FromInt(endOfMatch);

        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendString(factory.NewSubString(subject, 0, index));

        // Compute the parameter list consisting of the match, captures, index,
        // and subject for the replace function invocation. If the RegExp contains
        // named captures, they are also passed as the last argument.

        // The number of captures plus one for the match.
        int m = matchIndices.NumberOfCaptureRegisters / 2;
        bool hasNamedCaptures = m > 1 && data.HasCaptureNameMap;

        int argc = GetArgcForReplaceCallable(m, hasNamedCaptures);
        if (argc == -1) isolate.ThrowRangeError(MessageTemplate.TooManyArguments);
        var arguments = new JSValue[argc];
        int cursor = 0;
        for (int j = 0; j < m; j++)
        {
            JSString capture = BuiltinsRegExp.GenericCaptureGetter(isolate, matchIndices, j, out bool ok);
            arguments[cursor++] = ok ? capture : JSValue.Undefined;
        }
        arguments[cursor++] = JSValue.FromInt(index);
        arguments[cursor++] = subject;
        if (hasNamedCaptures)
        {
            arguments[cursor++] = BuiltinsRegExp.ConstructNamedCaptureGroupsObject(isolate, data, arguments);
        }

        JSValue replacementObj = Execution.Call(isolate, replaceObj, JSValue.Undefined, arguments);
        JSString replacement = ObjectOps.ToString(isolate, replacementObj);
        builder.AppendString(replacement);
        builder.AppendString(factory.NewSubString(subject, endOfMatch, subject.Length));
        return builder.Finish();
    }

    // ---- @@split slow path --------------------------------------------------------------

    /// <summary>
    /// Runtime_RegExpSplit: the spec's RegExp.prototype[@@split] through a
    /// species-constructed sticky splitter.
    /// </summary>
    public static JSValue RegExpSplit(Isolate isolate, JSReceiver recv, JSString s, JSValue limitObj)
    {
        Factory factory = isolate.Factory;
        JSFunction regexpFun = isolate.NativeContext.RegExpFunction;
        JSValue ctor = ObjectOps.SpeciesConstructor(isolate, recv, regexpFun);
        if (!ReferenceEquals(ctor.HeapObjectOrNull, regexpFun)) isolate.CountUsage("kRegExpCustomSpecies");

        JSValue flagsObj = JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.flags_string);
        JSString flags = ObjectOps.ToString(isolate, flagsObj);
        ReadOnlySpan<char> flagChars = flags.FlatSpan();
        bool unicode = flagChars.Contains('u') || flagChars.Contains('v');
        bool sticky = flagChars.Contains('y');
        JSString newFlags = flags;
        if (!sticky) newFlags = factory.NewConsString(flags, factory.LookupSingleCharacterStringFromCode('y'));

        JSValue splitterObj = Execution.New(isolate, ctor, [recv, newFlags]);
        var splitter = (JSReceiver)splitterObj.Object;
        if (splitter is JSRegExp splitterRegExp)
        {
            RegExpFlags splitterFlags = splitterRegExp.Flags;
            bool splitterSticky = (splitterFlags & RegExpFlags.Sticky) != 0;
            bool splitterUnicode = (splitterFlags & (RegExpFlags.Unicode | RegExpFlags.UnicodeSets)) != 0;
            if (!splitterSticky || splitterUnicode != unicode) isolate.CountUsage("kRegExpMatcherFlagsMismatch");
        }
        else
        {
            isolate.CountUsage("kRegExpMatcherFlagsMismatch");
        }

        uint limit = limitObj.IsUndefined
            ? uint.MaxValue
            : V8Sharp.Base.Numbers.Conversions.DoubleToUint32(ObjectOps.ToNumber(isolate, limitObj).Number);

        uint length = (uint)s.Length;
        if (limit == 0) return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);

        if (length == 0)
        {
            JSValue result = BuiltinsRegExp.RegExpExecUtils(isolate, splitter, s);
            if (!result.IsNull) return factory.NewJSArray(ElementsKind.PACKED_ELEMENTS);
            var single = new FixedArray(1);
            single[0] = s;
            return factory.NewJSArrayWithElements(single);
        }

        var elems = new List<JSValue>(8);
        uint stringIndex = 0;
        uint prevStringIndex = 0;
        while (stringIndex < length)
        {
            BuiltinsRegExp.UtilsSetLastIndex(isolate, splitter, stringIndex);
            JSValue result = BuiltinsRegExp.RegExpExecUtils(isolate, splitter, s);
            if (result.IsNull)
            {
                stringIndex = (uint)BuiltinsRegExp.AdvanceStringIndex(s, stringIndex, unicode);
                continue;
            }

            JSValue lastIndexObj = BuiltinsRegExp.UtilsGetLastIndex(isolate, splitter);
            lastIndexObj = ObjectOps.ToLength(isolate, lastIndexObj);
            uint end = Math.Min(PositiveNumberToUint32(lastIndexObj.Number), length);
            if (end == prevStringIndex)
            {
                stringIndex = (uint)BuiltinsRegExp.AdvanceStringIndex(s, stringIndex, unicode);
                continue;
            }

            elems.Add(factory.NewSubString(s, (int)prevStringIndex, (int)stringIndex));
            if ((uint)elems.Count == limit) return NewJSArrayWithElements(isolate, elems);

            prevStringIndex = end;

            JSValue numCapturesObj = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.length_string);
            numCapturesObj = ObjectOps.ToLength(isolate, numCapturesObj);
            uint numCaptures = PositiveNumberToUint32(numCapturesObj.Number);
            for (uint i = 1; i < numCaptures; i++)
            {
                JSValue capture = ObjectOps.GetElement(isolate, result, i);
                elems.Add(capture);
                if ((uint)elems.Count == limit) return NewJSArrayWithElements(isolate, elems);
            }
            stringIndex = prevStringIndex;
        }

        elems.Add(factory.NewSubString(s, (int)prevStringIndex, (int)length));
        return NewJSArrayWithElements(isolate, elems);
    }

    static JSArray NewJSArrayWithElements(Isolate isolate, List<JSValue> elems)
    {
        var elements = new FixedArray(elems.Count);
        elems.CopyTo(elements.Data);
        return isolate.Factory.NewJSArrayWithElements(elements);
    }

    // ---- @@replace slow path -------------------------------------------------------------

    /// <summary>
    /// Runtime_RegExpReplaceRT: the spec's RegExp.prototype[@@replace] for
    /// modified regexps (and the legacy path for unmodified ones with a string).
    /// </summary>
    public static JSString RegExpReplaceRT(Isolate isolate, JSReceiver recv, JSString s, JSValue replaceObj)
    {
        Factory factory = isolate.Factory;
        s = JSString.Flatten(isolate, s);
        bool functionalReplace = ObjectOps.IsCallable(replaceObj);
        JSString? replace = null;
        if (!functionalReplace) replace = ObjectOps.ToString(isolate, replaceObj);

        // Fast-path for unmodified JSRegExps (and non-functional replace).
        if (BuiltinsRegExp.IsUnmodifiedRegExp(isolate, recv))
        {
            // We should never get here with functional replace because unmodified
            // regexp and functional replace should be fully handled in CSA code.
            if (functionalReplace) throw new InvalidOperationException("CHECK failed: !functional_replace");
            return RegExpReplace(isolate, (JSRegExp)recv, s, replace!);
        }

        uint length = (uint)s.Length;
        bool global = false;
        bool fullUnicode = false;

        // 7. Let flags be ? ToString(? Get(rx, "flags")).
        // 8. If flags contains "g", let global be true. Otherwise, let global be
        // false.
        JSValue flagsObj = JSReceiver.GetProperty(isolate, recv, ReadOnlyRoots.flags_string);
        JSString flagStr = ObjectOps.ToString(isolate, flagsObj);
        ReadOnlySpan<char> flatFlag = flagStr.FlatSpan();
        global = flatFlag.Contains('g');
        if (global)
        {
            // b. If flags contains "u" or flags contains "v", let fullUnicode be
            // true. Otherwise, let fullUnicode be false.
            fullUnicode = flatFlag.Contains('u') || flatFlag.Contains('v');
            BuiltinsRegExp.UtilsSetLastIndex(isolate, recv, 0);
        }

        var results = new List<JSValue>(8);
        while (true)
        {
            JSValue result = BuiltinsRegExp.RegExpExecUtils(isolate, recv, s);
            if (result.IsNull) break;
            results.Add(result);
            if (!global) break;
            JSValue matchObj = ObjectOps.GetElement(isolate, result, 0);
            JSString match = ObjectOps.ToString(isolate, matchObj);
            if (match.Length == 0) BuiltinsRegExp.SetAdvancedStringIndex(isolate, recv, s, fullUnicode);
        }

        var builder = new IncrementalStringBuilder(isolate);
        uint nextSourcePosition = 0;
        foreach (JSValue result in results)
        {
            JSValue capturesLengthObj = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.length_string);
            capturesLengthObj = ObjectOps.ToLength(isolate, capturesLengthObj);
            uint capturesLength = PositiveNumberToUint32(capturesLengthObj.Number);

            JSValue matchObj = ObjectOps.GetElement(isolate, result, 0);
            JSString match = ObjectOps.ToString(isolate, matchObj);
            int matchLength = match.Length;

            JSValue positionObj = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.index_string);
            positionObj = ObjectOps.ToInteger(isolate, positionObj);
            uint position = Math.Min(PositiveNumberToUint32(positionObj.Number), length);

            // Do not reserve capacity since captures_length is user-controlled.
            var captures = new List<JSValue> { match };
            for (uint n = 1; n < capturesLength; n++)
            {
                JSValue capture = ObjectOps.GetElement(isolate, result, n);
                if (!capture.IsUndefined) capture = ObjectOps.ToString(isolate, capture);
                captures.Add(capture);
            }

            JSValue groupsObj = ObjectOps.GetProperty(isolate, result, ReadOnlyRoots.groups_string);
            bool hasNamedCaptures = !groupsObj.IsUndefined;

            JSString replacement;
            if (functionalReplace)
            {
                // The first argument is always match string itself. So min argc value
                // should be 1.
                int argc = GetArgcForReplaceCallable(captures.Count, hasNamedCaptures);
                if (argc == -1) isolate.ThrowRangeError(MessageTemplate.TooManyArguments);
                var callArgs = new JSValue[argc];
                int cursor = 0;
                for (int j = 0; j < captures.Count; j++) callArgs[cursor++] = captures[j];
                callArgs[cursor++] = JSValue.FromNumber(position);
                callArgs[cursor++] = s;
                if (hasNamedCaptures) callArgs[cursor++] = groupsObj;
                JSValue replacementObj = Execution.Call(isolate, replaceObj, JSValue.Undefined, callArgs);
                replacement = ObjectOps.ToString(isolate, replacementObj);
            }
            else
            {
                if (!groupsObj.IsUndefined) groupsObj = ObjectOps.ToObject(isolate, groupsObj);
                var m = new VectorBackedMatch(isolate, s, match, position, captures, groupsObj);
                replacement = JSString.GetSubstitution(isolate, m, replace!);
            }

            if (position >= nextSourcePosition)
            {
                builder.AppendString(factory.NewSubString(s, (int)nextSourcePosition, (int)position));
                builder.AppendString(replacement);
                nextSourcePosition = (uint)Math.Min((long)position + matchLength, uint.MaxValue);
            }
        }

        if (nextSourcePosition < length)
        {
            builder.AppendString(factory.NewSubString(s, (int)nextSourcePosition, (int)length));
        }
        return builder.Finish();
    }

    // ---- Match of a global atom -----------------------------------------------------------

    /// <summary>
    /// Runtime_RegExpMatchGlobalAtom: str.match(/atom/g) is the pattern
    /// repeated once per (non-overlapping) occurrence.
    /// </summary>
    public static JSValue RegExpMatchGlobalAtom(Isolate isolate, JSRegExp regexp, JSString subject, RegExpData data)
    {
        subject = JSString.Flatten(isolate, subject);
        JSString pattern = data.AtomPattern!;
        int patternLength = pattern.Length;
        uint numberOfMatches = 0;
        int lastMatchIndex = -1;

        regexp.LastIndex = JSValue.Zero;

        int startIndex = 0;  // Start matching at the beginning.
        if (RegExpResultsCache.MatchGlobalAtomTryGet(isolate, subject, pattern, out numberOfMatches, out lastMatchIndex))
        {
            startIndex = lastMatchIndex + patternLength;
        }

        bool isUnicode = (regexp.Flags & RegExpFlags.Unicode) != 0;
        ReadOnlySpan<char> subjectChars = subject.FlatSpan();
        ReadOnlySpan<char> patternChars = pattern.FlatSpan();
        if (patternLength == 1)
        {
            // RegExpMatchGlobalAtom_OneCharPattern: a vectorized count.
            ReadOnlySpan<char> rest = subjectChars[startIndex..];
            int count = rest.Count(patternChars[0]);
            if (count > 0)
            {
                numberOfMatches += (uint)count;
                lastMatchIndex = startIndex + rest.LastIndexOf(patternChars[0]);
            }
        }
        else
        {
            // RegExpMatchGlobalAtom_Generic.
            while (true)
            {
                int foundAtIndex = StringSearch.IndexOf(subjectChars, patternChars, startIndex);
                if (foundAtIndex == -1) break;
                numberOfMatches++;
                lastMatchIndex = foundAtIndex;
                startIndex = patternLength > 0
                    ? foundAtIndex + patternLength
                    : (int)BuiltinsRegExp.AdvanceStringIndex(subject, startIndex, isUnicode);
            }
        }

        if (lastMatchIndex == -1) return JSValue.Null;

        RegExpResultsCache.MatchGlobalAtomTryInsert(isolate, subject, pattern, numberOfMatches, lastMatchIndex);

        JSRegExp.SetLastMatchInfo(isolate, RegExpMatchInfo.Get(isolate), subject, 0,
            [lastMatchIndex, lastMatchIndex + patternLength]);

        var elems = new FixedArray((int)numberOfMatches);
        elems.Data.AsSpan().Fill(pattern);
        return isolate.Factory.NewJSArrayWithElements(elems, ElementsKind.PACKED_ELEMENTS, (int)numberOfMatches);
    }

    // ---- Test runtime functions (runtime-test.cc) -----------------------------------------

    /// <summary>Runtime_RegexpHasBytecode (%RegexpHasBytecode(re, isLatin1)).</summary>
    public static bool RegexpHasBytecode(JSRegExp regexp, bool isLatin1) =>
        regexp.Data is { TypeTag: RegExpKind.Irregexp } data && data.Compiled.GetBytecode(isLatin1) is not null;

    /// <summary>Runtime_RegexpHasNativeCode (%RegexpHasNativeCode(re, isLatin1)).</summary>
    public static bool RegexpHasNativeCode(JSRegExp regexp, bool isLatin1) =>
        regexp.Data is { TypeTag: RegExpKind.Irregexp } data && data.Compiled.HasCode(isLatin1);

    /// <summary>
    /// Runtime_RegexpQuickCheckRejects. V8Sharp.RegExp does not build V8's
    /// quick-check filters (RegExpData::QuickCheckRejects), so nothing is
    /// rejected (deviations.md).
    /// </summary>
    public static bool RegexpQuickCheckRejects(JSRegExp regexp, JSString c) => false;

    /// <summary>Runtime_RegexpTypeTag (%RegexpTypeTag(re)).</summary>
    public static JSString RegexpTypeTag(Isolate isolate, JSRegExp regexp)
    {
        string typeStr = regexp.Data?.TypeTag switch
        {
            RegExpKind.Atom => "ATOM",
            RegExpKind.Irregexp => "IRREGEXP",
            RegExpKind.Experimental => "EXPERIMENTAL",
            _ => "NOT_COMPILED",
        };
        return isolate.Factory.NewStringFromAsciiChecked(typeStr);
    }

    /// <summary>Runtime_RegexpIsUnmodified (%RegexpIsUnmodified(re)).</summary>
    public static bool RegexpIsUnmodified(Isolate isolate, JSRegExp regexp) => BuiltinsRegExp.IsUnmodifiedRegExp(isolate, regexp);

    // ---- Small runtime functions -------------------------------------------------------------

    /// <summary>Runtime_RegExpInitializeAndCompile.</summary>
    public static JSRegExp RegExpInitializeAndCompile(Isolate isolate, JSRegExp regexp, JSString source, JSString flags) =>
        JSRegExp.Initialize(isolate, regexp, source, flags);

    /// <summary>Runtime_RegExpStringFromFlags.</summary>
    public static JSString RegExpStringFromFlags(Isolate isolate, JSRegExp regexp) =>
        JSRegExp.StringFromFlags(isolate, regexp.Flags);

    /// <summary>Runtime_RegExpBuildIndices.</summary>
    public static JSArray RegExpBuildIndices(Isolate isolate, JSRegExp regexp, RegExpMatchInfo matchInfo) =>
        JSRegExpResultIndices.BuildIndices(isolate, matchInfo, regexp.Data!);

    /// <summary>
    /// Runtime_RegExpExec: one exec of the regexp data into the result vector;
    /// the number of matches.
    /// </summary>
    public static int RegExpExec(Isolate isolate, RegExpData regexpData, JSString subject, int index, Span<int> resultOffsetsVector)
    {
        // Due to the way the JS calls are constructed this must be less than the
        // length of a string, i.e. it is always a Smi.  We check anyway for security.
        if (index < 0 || index > subject.Length) throw new InvalidOperationException("CHECK failed");
        return JSRegExp.ExecRaw(isolate, regexpData, subject, index, resultOffsetsVector);
    }

    // ---- String split (string separator) -------------------------------------------------

    /// <summary>
    /// Runtime_StringSplit: splits by a non-empty string separator that occurs at
    /// <paramref name="firstIndex"/> (the builtin's IndexOf), caching unlimited
    /// splits of internalized strings.
    /// </summary>
    public static JSArray StringSplit(Isolate isolate, JSString subject, JSString pattern, uint limit, int firstIndex)
    {
        if (limit == 0) throw new InvalidOperationException("CHECK failed: 0 < limit");
        int subjectLength = subject.Length;
        int patternLength = pattern.Length;
        if (patternLength == 0) throw new InvalidOperationException("CHECK failed: 0 < pattern_length");
        Factory factory = isolate.Factory;

        if (limit == 0xFFFFFFFFu)
        {
            FixedArray? cachedAnswer = RegExpResultsCache.Lookup(isolate, subject, pattern, out _,
                RegExpResultsCache.ResultsCacheType.STRING_SPLIT_SUBSTRINGS);
            if (cachedAnswer is not null)
            {
                // The cache FixedArray is a COW-array and can therefore be reused.
                return factory.NewJSArrayWithElements(cachedAnswer);
            }
        }

        // The limit can be very large (0xFFFFFFFFu), but since the pattern
        // isn't empty, we can never create more parts than ~half the length
        // of the subject.
        subject = JSString.Flatten(isolate, subject);
        pattern = JSString.Flatten(isolate, pattern);
        ReadOnlySpan<char> subjectChars = subject.FlatSpan();
        ReadOnlySpan<char> patternChars = pattern.FlatSpan();

        List<int> indices = GetRewoundRegexpIndicesList();
        // FindStringIndicesDispatch with a known first index.
        int startIndex = 0;
        uint remaining = limit;
        if (firstIndex >= 0)
        {
            indices.Add(firstIndex);
            remaining--;
            startIndex = firstIndex + patternLength;
        }
        if (remaining > 0) FindStringIndices(subjectChars, patternChars, indices, remaining, startIndex);

        if ((uint)indices.Count < limit) indices.Add(subjectLength);

        // The list indices now contains the end of each part to create.

        // Create JSArray of substrings separated by separator.
        int partCount = indices.Count;
        var elements = new FixedArray(partCount);
        if (partCount == 1 && indices[0] == subjectLength)
        {
            elements[0] = subject;
        }
        else
        {
            int partStart = 0;
            for (int i = 0; i < partCount; i++)
            {
                int partEnd = indices[i];
                elements[i] = factory.NewProperSubString(subject, partStart, partEnd);
                partStart = partEnd + patternLength;
            }
        }
        JSArray result = factory.NewJSArrayWithElements(elements);

        if (limit == 0xFFFFFFFFu)
        {
            RegExpResultsCache.Enter(isolate, subject, pattern, elements, [],
                RegExpResultsCache.ResultsCacheType.STRING_SPLIT_SUBSTRINGS);
        }
        TruncateRegexpIndicesList();
        return result;
    }
}
