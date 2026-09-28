// Port of the heap-independent parts of src/regexp/regexp.cc (v8::internal::RegExp
// and regexp::RegExpImpl) and src/regexp/experimental/experimental.cc.
//
// Public API:
//
//   RegExpCompileResult r = RegExpEngine.Compile(pattern, flags, backtrackLimit);
//   if (r.Error != RegExpError.None) -> SyntaxError at r.ErrorPosition with
//                                       RegExpErrors.ErrorString(r.Error)
//   CompiledRegExp re = r.RegExp;
//   Span<int> regs = stackalloc int[re.RegistersPerMatch];   // (captures + 1) * 2
//   int n = re.Exec(subject, lastIndex, regs);
//     n >= 1                         match; regs[2i], regs[2i + 1] = capture i
//                                    (-1 for unmatched captures). A span that
//                                    holds k * RegistersPerMatch registers is
//                                    filled with up to k successive (global)
//                                    matches and n is their count.
//     n == 0                         no match (also when the backtrack limit was
//                                    hit and no experimental fallback applies)
//     n == RegExpResult.RE_EXCEPTION the backtrack stack or the NFA memory
//                                    overflowed (V8: RangeError "Maximum call
//                                    stack size exceeded"), or lazy compilation
//                                    failed: then re.CompileError is set (V8:
//                                    SyntaxError "Regular expression too large").
//   re.CaptureNameMap: (name, index) pairs sorted by capture index, as V8's
//                      capture_name_map (duplicate names appear once per group).
//
// Like V8, irregexp code is compiled lazily on the first Exec, using that
// subject as the sample for the Boyer-Moore frequency heuristics, and
// separately for one-byte and two-byte subjects. It is compiled either to
// bytecode for IrregexpInterpreter or to native code (IL, see
// RegExpMacroAssemblerIL), following V8's tier-up policy (RegExpTierPolicy):
// by default (--regexp-tier-up --regexp-tier-up-ticks=0) straight to native
// code; with a nonzero tick count the first executions are interpreted. V8 decides by the string's
// representation; strings here are always UTF-16, so a subject counts as
// one-byte when all its code units are at most 0xFF (the caller may pass the
// representation it knows instead).

using V8Sharp.RegExp.Experimental;

namespace V8Sharp.RegExp;

public enum RegExpKind
{
    /// <summary>A plain string search (AtomRegExpData).</summary>
    Atom,
    /// <summary>The backtracking irregexp engine (IrRegExpData, IRREGEXP).</summary>
    Irregexp,
    /// <summary>The linear-time experimental engine (IrRegExpData, EXPERIMENTAL).</summary>
    Experimental,
}

public readonly struct RegExpCompileResult
{
    public RegExpCompileResult(CompiledRegExp regExp)
    {
        RegExp = regExp;
        Error = RegExpError.None;
        ErrorPosition = 0;
    }

    public RegExpCompileResult(RegExpError error, int errorPosition)
    {
        RegExp = null;
        Error = error;
        ErrorPosition = errorPosition;
    }

    public CompiledRegExp? RegExp { get; }
    public RegExpError Error { get; }
    public int ErrorPosition { get; }
    public string ErrorMessage => RegExpErrors.ErrorString(Error);
    public bool Succeeded => Error == RegExpError.None;
}

/// <summary>
/// V8's regexp tiering flags: --regexp-interpret-all, --regexp-tier-up and
/// --regexp-tier-up-ticks. The default (V8's) is single-tier: tier-up with a
/// zero-tick threshold, i.e. compile to native code on first use.
/// </summary>
public readonly record struct RegExpTierPolicy(bool InterpretAll, bool TierUp, int TierUpTicks)
{
    /// <summary>The policy of the static RegExpEngine flags.</summary>
    public static RegExpTierPolicy Current =>
        new(RegExpEngine.s_regexpInterpretAll, RegExpEngine.s_regexpTierUp, RegExpEngine.s_regexpTierUpTicks);

    /// <summary>--regexp-interpret-all: never compile to native code.</summary>
    public static RegExpTierPolicy Interpreted => new(true, false, 0);

    /// <summary>--regexp-jit-all: compile to native code on first use (the default).</summary>
    public static RegExpTierPolicy JitAll => new(false, true, 0);

    /// <summary>--no-regexp-tier-up: native code only, no interpreter.</summary>
    public static RegExpTierPolicy NativeOnly => new(false, false, 0);

    /// <summary>Interpret the first <paramref name="ticks"/> executions, then compile.</summary>
    public static RegExpTierPolicy TwoTier(int ticks) => new(false, true, ticks);

    // DEFINE_NEG_IMPLICATION(regexp_interpret_all, regexp_tier_up).
    internal RegExpTierPolicy Normalize() => InterpretAll ? this with { TierUp = false } : this;
}

public static class RegExpEngine
{
    /// <summary>--regexp-interpret-all (default false).</summary>
    public static bool s_regexpInterpretAll;
    /// <summary>--regexp-tier-up (default true).</summary>
    public static bool s_regexpTierUp = true;
    /// <summary>--regexp-tier-up-ticks (default 0).</summary>
    public static int s_regexpTierUpTicks;

    /// <summary>JSRegExp::kTierUpForSubjectLengthValue.</summary>
    public const int kTierUpForSubjectLengthValue = 1000;

    /// <summary>--enable-experimental-regexp-engine (default false).</summary>
    public static bool s_enableExperimentalRegExpEngine;
    /// <summary>--default-to-experimental-regexp-engine (default false).</summary>
    public static bool s_defaultToExperimentalRegExpEngine;
    /// <summary>--enable-experimental-regexp-engine-on-excessive-backtracks (default false).</summary>
    public static bool s_enableExperimentalRegExpEngineOnExcessiveBacktracks;
    /// <summary>--regexp-backtracks-before-fallback (default 50000).</summary>
    public static uint s_regexpBacktracksBeforeFallback = 50000;

    /// <summary>JSRegExp::kNoBacktrackLimit.</summary>
    public const uint kNoBacktrackLimit = 0;
    /// <summary>RegExp::kMaxOptimizedPatternLength.</summary>
    public const int kMaxOptimizedPatternLength = 20 * 1024;
    /// <summary>JSRegExp::kAtomRegisterCount.</summary>
    public const int kAtomRegisterCount = 2;

    /// <summary>JSRegExp::RegistersForCaptureCount.</summary>
    public static int RegistersForCaptureCount(int count) => (count + 1) * 2;

    /// <summary>RegExp::VerifyFlags: /u and /v are mutually exclusive.</summary>
    public static bool VerifyFlags(RegExpFlags flags) => !(flags.IsUnicode() && flags.IsUnicodeSets());

    /// <summary>RegExp::VerifySyntax.</summary>
    public static bool VerifySyntax(string pattern, RegExpFlags flags, out RegExpError error, out int errorPos)
    {
        var data = new RegExpCompileData();
        bool pass = RegExpParser.VerifyRegExpSyntax(pattern, flags, data);
        error = data.Error;
        errorPos = data.ErrorPos;
        return pass;
    }

    // Identifies the sort of regexps where the regexp engine is faster
    // than the code used for atom matches.
    static bool HasFewDifferentCharacters(string pattern)
    {
        int length = Math.Min((int)CompilerConstants.kMaxLookaheadForBoyerMoore, pattern.Length);
        if (length <= CompilerConstants.kPatternTooShortForBoyerMoore) return false;
        const int kMod = 128;
        Span<bool> characterFound = stackalloc bool[kMod];
        int different = 0;
        for (int i = 0; i < length; i++)
        {
            int ch = pattern[i] & (kMod - 1);
            if (!characterFound[ch])
            {
                characterFound[ch] = true;
                different++;
                // We declare a regexp low-alphabet if it has at least 3 times as many
                // characters as it has different characters.
                if (different * 3 > length) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// RegExp::Compile: parses the pattern and chooses the implementation (atom,
    /// irregexp, or experimental). Irregexp code itself is compiled lazily.
    /// </summary>
    public static RegExpCompileResult Compile(string pattern, RegExpFlags flags,
        uint backtrackLimit = kNoBacktrackLimit, RegExpTierPolicy? tierPolicy = null)
    {
        RegExpTierPolicy tier = (tierPolicy ?? RegExpTierPolicy.Current).Normalize();
        var parseResult = new RegExpCompileData();
        if (!RegExpParser.ParseRegExp(pattern, flags, parseResult))
        {
            // Throw an exception if we fail to parse the pattern.
            return new RegExpCompileResult(parseResult.Error, parseResult.ErrorPos);
        }

        bool hasBeenCompiled = false;
        bool isLinearExecutable = false;
        CompiledRegExp? result = null;

        if (s_enableExperimentalRegExpEngine || s_enableExperimentalRegExpEngineOnExcessiveBacktracks ||
            flags.IsLinear())
        {
            isLinearExecutable = ExperimentalCompiler.CanBeHandled(parseResult.Tree!, flags,
                parseResult.CaptureCount);
        }
        string escapedSource = EscapeRegExpSource(pattern);
        List<KeyValuePair<string, int>>? captureNameMap = CreateCaptureNameMap(parseResult.NamedCaptures);
        if (s_defaultToExperimentalRegExpEngine && isLinearExecutable)
        {
            result = new CompiledRegExp(pattern, escapedSource, flags, RegExpKind.Experimental,
                parseResult.CaptureCount, backtrackLimit, captureNameMap, null, tier);
            hasBeenCompiled = true;
        }
        else if (flags.IsLinear())
        {
            // V8 only accepts the 'l' flag under --enable-experimental-regexp-engine;
            // that check belongs to flag parsing in the engine.
            if (!isLinearExecutable)
            {
                // TODO(mbid): The error could provide a reason for why the regexp can't
                // be executed in linear time (e.g. due to back references).
                return new RegExpCompileResult(RegExpError.NotLinear, 0);
            }
            result = new CompiledRegExp(pattern, escapedSource, flags, RegExpKind.Experimental,
                parseResult.CaptureCount, backtrackLimit, captureNameMap, null, tier);
            hasBeenCompiled = true;
        }
        else if (parseResult.Simple && !flags.IsIgnoreCase() && !flags.IsSticky() &&
                 !HasFewDifferentCharacters(pattern))
        {
            // Parse-tree is a single atom that is equal to the pattern.
            result = new CompiledRegExp(pattern, escapedSource, flags, RegExpKind.Atom, 0, backtrackLimit, null,
                pattern, tier);
            hasBeenCompiled = true;
        }
        else if (parseResult.Tree!.IsAtom() && !flags.IsSticky() && parseResult.CaptureCount == 0)
        {
            RegExpAtom atom = parseResult.Tree.AsAtom()!;
            // The pattern source might (?) contain escape sequences, but they're
            // resolved in atom_string.
            string atomString = atom.Data;
            if (!flags.IsIgnoreCase() && !HasFewDifferentCharacters(atomString))
            {
                result = new CompiledRegExp(pattern, escapedSource, flags, RegExpKind.Atom, 0, backtrackLimit, null,
                    atomString, tier);
                hasBeenCompiled = true;
            }
        }
        if (!hasBeenCompiled)
        {
            result = new CompiledRegExp(pattern, escapedSource, flags, RegExpKind.Irregexp,
                parseResult.CaptureCount, backtrackLimit, captureNameMap, null, tier)
            {
                CanBeZeroLength = parseResult.Tree!.MinMatch == 0,
                IsLinearExecutable = isLinearExecutable,
            };
        }
        return new RegExpCompileResult(result!);
    }

    /// <summary>
    /// RegExp::CreateCaptureNameMap. Named captures are sorted by name (because
    /// the set is used to ensure name uniqueness), but the capture name map must
    /// be sorted by index.
    /// </summary>
    internal static List<KeyValuePair<string, int>>? CreateCaptureNameMap(List<RegExpCapture>? namedCaptures)
    {
        if (namedCaptures is null) return null;
        Debug.Assert(namedCaptures.Count != 0);
        var sorted = new List<RegExpCapture>(namedCaptures);
        sorted.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        var map = new List<KeyValuePair<string, int>>(sorted.Count);
        foreach (RegExpCapture capture in sorted) map.Add(new(capture.Name!, capture.Index));
        return map;
    }

    /// <summary>
    /// RegExpImpl::Compile: compiles the parsed pattern to irregexp bytecode
    /// (data.Code) or, for data.CompilationTarget == kNative, to IL
    /// (data.NativeCode). Returns false and sets data.Error on failure.
    /// </summary>
    public static bool CompileIrregexp(RegExpCompileData data, RegExpFlags flags, ReadOnlySpan<char> sampleSubject,
        int originalSourceLength, ref uint backtrackLimit, bool isLinearExecutable, bool isOneByte = false,
        bool peepholeOptimization = true)
    {
        if (RegistersForCaptureCount(data.CaptureCount) > RegExpMacroAssembler.kMaxRegisterCount)
        {
            data.Error = RegExpError.TooLarge;
            return false;
        }

        var compiler = new RegExpCompiler(data.CaptureCount, flags, isOneByte);

        if (compiler.Optimize) compiler.Optimize = originalSourceLength <= kMaxOptimizedPatternLength;

        // Sample some characters from the middle of the string.
        const int kSampleSize = 128;

        int start, end;
        if (sampleSubject.Length > kSampleSize)
        {
            start = (sampleSubject.Length - kSampleSize) / 2;
            end = start + kSampleSize;
        }
        else
        {
            start = 0;
            end = sampleSubject.Length;
        }
        for (int i = start; i < end; i++) compiler.FrequencyCollator.CountCharacter(sampleSubject[i]);

        data.Node = compiler.PreprocessRegExp(data, isOneByte);
        if (data.Error != RegExpError.None) return false;
        data.Error = Analysis.AnalyzeRegExp(isOneByte, data.Node);
        if (data.Error != RegExpError.None) return false;

        RegExpMacroAssembler.Mode assemblerMode = isOneByte ? RegExpMacroAssembler.Mode.LATIN1
            : RegExpMacroAssembler.Mode.UC16;
        RegExpMacroAssembler macroAssembler;
        if (data.CompilationTarget == CompilationTarget.kNative)
        {
            // Native regexp implementation.
            int outputRegisterCount = RegistersForCaptureCount(data.CaptureCount);
            macroAssembler = new RegExpMacroAssemblerIL(assemblerMode, outputRegisterCount);
        }
        else
        {
            // Interpreted regexp implementation.
            macroAssembler = new RegExpBytecodeGenerator(assemblerMode) { PeepholeOptimization = peepholeOptimization };
        }

        backtrackLimit = SetBacktrackAndExperimentalFallback(macroAssembler, backtrackLimit, isLinearExecutable);

        // Inserted here, instead of in Assembler, because it depends on information
        // in the AST that isn't replicated in the Node structure.
        bool isEndAnchored = data.Tree!.IsCertainlyAnchoredAtEnd(RegExpNode.kRecursionBudget);
        bool isStartAnchored = data.Tree.IsCertainlyAnchoredAtStart(RegExpNode.kRecursionBudget);
        int maxLength = data.Tree.MaxMatch;
        const int kMaxBacksearchLimit = 1024;
        if (isEndAnchored && !isStartAnchored && !flags.IsSticky() && maxLength < kMaxBacksearchLimit)
        {
            macroAssembler.SetCurrentPositionFromEnd(maxLength);
        }

        if (flags.IsGlobal())
        {
            RegExpMacroAssembler.GlobalMode mode = RegExpMacroAssembler.GlobalMode.GLOBAL;
            if (data.Tree.MinMatch > 0)
            {
                mode = RegExpMacroAssembler.GlobalMode.GLOBAL_NO_ZERO_LENGTH_CHECK;
            }
            else if (flags.IsEitherUnicode())
            {
                mode = RegExpMacroAssembler.GlobalMode.GLOBAL_UNICODE;
            }
            macroAssembler.SetGlobalMode(mode);
        }

        CompilationResult result = compiler.Assemble(macroAssembler, data.Node, data.CaptureCount, "");

        if (result.Error != RegExpError.None) data.Error = result.Error;

        data.Code = result.Code as byte[];
        data.NativeCode = result.Code as RegExpILCode;
        data.RegisterCount = result.NumRegisters;

        return result.Succeeded;
    }

    // Returns the backtrack limit V8 stores back into the IrRegExpData.
    static uint SetBacktrackAndExperimentalFallback(RegExpMacroAssembler macroAssembler, uint backtrackLimit,
        bool isLinearExecutable)
    {
        if (s_enableExperimentalRegExpEngineOnExcessiveBacktracks && isLinearExecutable)
        {
            backtrackLimit = backtrackLimit == kNoBacktrackLimit
                ? s_regexpBacktracksBeforeFallback
                : Math.Min(backtrackLimit, s_regexpBacktracksBeforeFallback);
            macroAssembler.SetBacktrackLimit(backtrackLimit);
            macroAssembler.SetCanFallback(true);
        }
        else
        {
            macroAssembler.SetBacktrackLimit(backtrackLimit);
            macroAssembler.SetCanFallback(false);
        }
        return backtrackLimit;
    }

    // RegExpImpl::AtomExecRaw.
    internal static int AtomExecRaw(ReadOnlySpan<char> pattern, ReadOnlySpan<char> subject, int index,
        RegExpFlags flags, Span<int> output)
    {
        Debug.Assert(index >= 0);
        Debug.Assert(index <= subject.Length);
        if (output.Length % kAtomRegisterCount != 0) throw new InvalidOperationException("CHECK failed");

        int subjectLength = subject.Length;
        int patternLength = pattern.Length;
        Debug.Assert(patternLength > 0);
        int maxIndex = subjectLength - patternLength;

        for (int i = 0; i < output.Length; i += kAtomRegisterCount)
        {
            if (index > 0 && index < subjectLength && flags.ShouldOptionallyStepBackToLeadSurrogate())
            {
                // See https://github.com/tc39/ecma262/issues/128 and
                // https://codereview.chromium.org/1608693003.
                if (char.IsLowSurrogate(subject[index]) && char.IsHighSurrogate(subject[index - 1])) index--;
            }

            if (index > maxIndex) return i / kAtomRegisterCount;  // Return number of matches.
            int found = subject.Slice(index).IndexOf(pattern);
            if (found == -1) return i / kAtomRegisterCount;  // Return number of matches.
            index += found;
            output[i] = index;  // match start
            index += patternLength;
            output[i + 1] = index;  // match end
        }

        return output.Length / kAtomRegisterCount;
    }

    static bool IsLineTerminator(int c) => c == '\n' || c == '\r' || c == 0x2028 || c == 0x2029;

    /// <summary>
    /// EscapeRegExpSource (regexp.cc): the value of RegExp.prototype.source.
    /// </summary>
    public static string EscapeRegExpSource(string source)
    {
        if (source.Length == 0) return "(?:)";
        bool needsEscapes = false;
        bool inCharacterClass = false;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\\')
            {
                // Escape. Skip next character, which will be copied verbatim;
                if (!(i + 1 < source.Length && IsLineTerminator(source[i + 1]))) i++;
            }
            else if (c == '/' && !inCharacterClass)
            {
                // Not escaped forward-slash needs escape.
                needsEscapes = true;
            }
            else if (c == '[')
            {
                inCharacterClass = true;
            }
            else if (c == ']')
            {
                inCharacterClass = false;
            }
            else if (c == '\n' || c == '\r' || c == 0x2028 || c == 0x2029)
            {
                needsEscapes = true;
            }
        }
        if (!needsEscapes) return source;

        var dst = new System.Text.StringBuilder(source.Length + 8);
        int s = 0;
        inCharacterClass = false;
        while (s < source.Length)
        {
            char c = source[s];
            if (c == '\\')
            {
                if (s + 1 < source.Length && IsLineTerminator(source[s + 1]))
                {
                    // This '\' is ignored since the next character itself will be escaped.
                    s++;
                    continue;
                }
                // Escape. Copy this and next character.
                dst.Append(source[s++]);
                if (s == source.Length) break;
            }
            else if (c == '/' && !inCharacterClass)
            {
                // Not escaped forward-slash needs escape.
                dst.Append('\\');
            }
            else if (c == '[')
            {
                inCharacterClass = true;
            }
            else if (c == ']')
            {
                inCharacterClass = false;
            }
            else if (c == '\n')
            {
                dst.Append("\\n");
                s++;
                continue;
            }
            else if (c == '\r')
            {
                dst.Append("\\r");
                s++;
                continue;
            }
            else if (c == 0x2028)
            {
                dst.Append("\\u2028");
                s++;
                continue;
            }
            else if (c == 0x2029)
            {
                dst.Append("\\u2029");
                s++;
                continue;
            }
            dst.Append(source[s++]);
        }
        return dst.ToString();
    }
}

/// <summary>
/// A compiled regular expression: V8's RegExpData (AtomRegExpData or
/// IrRegExpData) together with the Exec entry points.
/// </summary>
public sealed class CompiledRegExp
{
    readonly string? _atomPattern;
    // Indexed by is_one_byte, as IrRegExpData's latin1/uc16 bytecode slots.
    readonly byte[]?[] _bytecode = new byte[]?[2];
    // Indexed by is_one_byte, as IrRegExpData's latin1/uc16 code slots (when
    // they hold native code rather than the interpreter trampoline).
    readonly RegExpILCode?[] _nativeCode = new RegExpILCode?[2];
    readonly RegExpTierPolicy _tier;
    int _ticksUntilTierUp;
    int _maxRegisterCount;
    Instruction[]? _experimentalBytecode;
    uint _backtrackLimit;

    internal CompiledRegExp(string source, string escapedSource, RegExpFlags flags, RegExpKind kind, int captureCount,
        uint backtrackLimit, List<KeyValuePair<string, int>>? captureNameMap, string? atomPattern,
        RegExpTierPolicy tier)
    {
        _tier = tier;
        // Factory::SetRegExpIrregexpData.
        _ticksUntilTierUp = tier.TierUp ? tier.TierUpTicks : 0;
        Source = source;
        EscapedSource = escapedSource;
        Flags = flags;
        Kind = kind;
        CaptureCount = captureCount;
        _backtrackLimit = backtrackLimit;
        CaptureNameMap = captureNameMap;
        _atomPattern = atomPattern;
    }

    public string Source { get; }
    /// <summary>The escaped source (RegExp.prototype.source).</summary>
    public string EscapedSource { get; }
    public RegExpFlags Flags { get; }
    public RegExpKind Kind { get; }
    /// <summary>The number of capture groups, without the implicit group 0.</summary>
    public int CaptureCount { get; }
    public int RegistersPerMatch => RegExpEngine.RegistersForCaptureCount(CaptureCount);
    /// <summary>Named groups as (name, index), sorted by index; null if there are none.</summary>
    public IReadOnlyList<KeyValuePair<string, int>>? CaptureNameMap { get; }
    public bool CanBeZeroLength { get; internal set; }
    public bool IsLinearExecutable { get; internal set; }
    public uint BacktrackLimit => _backtrackLimit;
    /// <summary>The error of a failed lazy compilation (e.g. TooLarge), or None.</summary>
    public RegExpError CompileError { get; private set; }
    /// <summary>The atom pattern (for Kind == Atom).</summary>
    public string? AtomPattern => _atomPattern;
    /// <summary>The irregexp bytecode for two-byte subjects, once compiled.</summary>
    public byte[]? Bytecode => _bytecode[0];

    /// <summary>The irregexp bytecode for the given subject width, once compiled
    /// (and until tier-up replaces it with native code).</summary>
    public byte[]? GetBytecode(bool isOneByte) => _bytecode[isOneByte ? 1 : 0];

    /// <summary>The native (IL) code for the given subject width, once compiled.</summary>
    public RegExpILCode? GetNativeCode(bool isOneByte) => _nativeCode[isOneByte ? 1 : 0];

    /// <summary>IrRegExpData::has_code: bytecode or native code exists for the width.</summary>
    public bool HasCode(bool isOneByte) => _bytecode[isOneByte ? 1 : 0] is not null ||
                                           _nativeCode[isOneByte ? 1 : 0] is not null;

    /// <summary>The tiering policy this regexp was created with.</summary>
    public RegExpTierPolicy TierPolicy => _tier;

    /// <summary>IrRegExpData::ticks_until_tier_up.</summary>
    public int TicksUntilTierUp => _ticksUntilTierUp;

    // IrRegExpData::CanTierUp.
    bool CanTierUp => _tier.TierUp && Kind == RegExpKind.Irregexp;

    /// <summary>IrRegExpData::MarkedForTierUp.</summary>
    public bool MarkedForTierUp => CanTierUp && _ticksUntilTierUp == 0;

    /// <summary>IrRegExpData::ShouldProduceBytecode.</summary>
    public bool ShouldProduceBytecode => _tier.InterpretAll || (_tier.TierUp && !MarkedForTierUp);

    // IrRegExpData::TierUpTick.
    void TierUpTick()
    {
        if (_ticksUntilTierUp == 0) return;
        _ticksUntilTierUp--;
    }

    // IrRegExpData::MarkTierUpForNextExec.
    void MarkTierUpForNextExec()
    {
        Debug.Assert(_tier.TierUp);
        _ticksUntilTierUp = 0;
    }

    /// <summary>String::IsOneByteRepresentationUnderneath, by content.</summary>
    public static bool IsOneByteSubject(ReadOnlySpan<char> subject) =>
        subject.IndexOfAnyExceptInRange('\0', '\u00ff') < 0;
    /// <summary>The number of registers (captures and internal) the bytecode uses.</summary>
    public int MaxRegisterCount => _maxRegisterCount;

    /// <summary>
    /// RegExpImpl::EnsureCompiledIrregexp / CompileIrregexpFromSource: compiles
    /// to bytecode or native code as the tier-up state asks. Returns false and
    /// sets CompileError if compilation fails.
    /// </summary>
    public bool EnsureCompiled(ReadOnlySpan<char> sampleSubject) =>
        EnsureCompiled(sampleSubject, IsOneByteSubject(sampleSubject));

    public bool EnsureCompiled(ReadOnlySpan<char> sampleSubject, bool isOneByte)
    {
        if (Kind == RegExpKind.Atom) return true;
        if (Kind == RegExpKind.Experimental) return EnsureExperimentalCompiled();
        int width = isOneByte ? 1 : 0;
        bool hasBytecode = _bytecode[width] is not null;
        bool needsInitialCompilation = !HasCode(isOneByte);
        // Recompile is needed when we're dealing with the first execution of the
        // regexp after the decision to tier up has been made. If the tiering up
        // strategy is not in use, this value is always false.
        // The hasBytecode check detects post-tier-up state: after successful
        // tier-up, bytecode is cleared. With tier-up ticks 0 however, tier-up is
        // requested immediately before any compilation, so !hasBytecode &&
        // needsInitialCompilation means we need to compile native code first.
        bool needsTierUpCompilation = MarkedForTierUp && (hasBytecode || needsInitialCompilation);
        if (!needsInitialCompilation && !needsTierUpCompilation) return true;
        if (CompileError != RegExpError.None) return false;

        // Since we can't abort gracefully during compilation, check for sufficient
        // stack space here in advance.
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            CompileError = RegExpError.AnalysisStackOverflow;
            return false;
        }

        var compileData = new RegExpCompileData();
        if (!RegExpParser.ParseRegExp(Source, Flags, compileData))
        {
            // THIS SHOULD NOT HAPPEN. We already pre-parsed it successfully once.
            CompileError = compileData.Error;
            return false;
        }
        if (compileData.CaptureCount != CaptureCount) throw new InvalidOperationException("SBXCHECK failed");

        CanBeZeroLength = compileData.Tree!.MinMatch == 0;
        // The compilation target is kBytecode if we're interpreting all regexp
        // objects, or if we're using the tier-up strategy but the tier-up hasn't
        // happened yet. The compilation target is kNative if we're using the
        // tier-up strategy and we need to recompile to tier-up, or if we're
        // producing native code for all regexp objects.
        compileData.CompilationTarget = ShouldProduceBytecode ? CompilationTarget.kBytecode
            : CompilationTarget.kNative;
        if (!RegExpEngine.CompileIrregexp(compileData, Flags, sampleSubject, Source.Length, ref _backtrackLimit,
                IsLinearExecutable, isOneByte))
        {
            Debug.Assert(compileData.Error != RegExpError.None);
            CompileError = compileData.Error;
            return false;
        }
        if (compileData.CompilationTarget == CompilationTarget.kNative)
        {
            _nativeCode[width] = compileData.NativeCode;
            // Reset bytecode to uninitialized. In case we use tier-up we know that
            // tier-up has happened this way.
            _bytecode[width] = null;
        }
        else
        {
            _bytecode[width] = compileData.Code;
        }
        if (compileData.RegisterCount > _maxRegisterCount) _maxRegisterCount = compileData.RegisterCount;
        return true;
    }

    bool EnsureExperimentalCompiled()
    {
        if (_experimentalBytecode is not null) return true;
        Instruction[]? code = CompileExperimental();
        if (code is null) return false;
        _experimentalBytecode = code;
        return true;
    }

    // experimental.cc CompileImpl: compiles source pattern, but doesn't change
    // the regexp object.
    Instruction[]? CompileExperimental()
    {
        var parseResult = new RegExpCompileData();
        if (!RegExpParser.ParseRegExp(Source, Flags, parseResult))
        {
            // The pattern was already parsed successfully during initialization, so
            // the only way parsing can fail now is because of stack overflow.
            CompileError = parseResult.Error;
            return null;
        }
        if (parseResult.CaptureCount != CaptureCount) throw new InvalidOperationException("SBXCHECK failed");
        return ExperimentalCompiler.Compile(parseResult.Tree!, Flags).ToArray();
    }

    /// <summary>
    /// RegExp::Exec: runs the regexp on <paramref name="subject"/> from
    /// <paramref name="index"/>. See the file comment for the result protocol.
    /// </summary>
    public int Exec(ReadOnlySpan<char> subject, int index, Span<int> registers) =>
        Exec(subject, index, registers, Kind == RegExpKind.Irregexp && IsOneByteSubject(subject));

    /// <summary>Exec with the subject representation given by the caller
    /// (<paramref name="isOneByte"/> requires all code units &lt;= 0xFF).</summary>
    public int Exec(ReadOnlySpan<char> subject, int index, Span<int> registers, bool isOneByte)
    {
        if ((uint)index > (uint)subject.Length) throw new ArgumentOutOfRangeException(nameof(index));
        switch (Kind)
        {
            case RegExpKind.Atom:
                return RegExpEngine.AtomExecRaw(_atomPattern, subject, index, Flags,
                    registers.Slice(0, registers.Length - registers.Length % RegExpEngine.kAtomRegisterCount));
            case RegExpKind.Experimental:
                return ExperimentalExec(subject, index, registers);
            default:
                return IrregexpExec(subject, index, registers, isOneByte);
        }
    }

    int RoundedRegisterCount(int length) => length - length % RegistersPerMatch;

    // RegExpImpl::IrregexpExec and IrregexpExecRaw.
    int IrregexpExec(ReadOnlySpan<char> subject, int previousIndex, Span<int> registers, bool isOneByte)
    {
        int outputRegisterCount = RegistersPerMatch;
        if (registers.Length < outputRegisterCount) throw new ArgumentException("register span too small");

        // Maybe force early tier up:
        if (_tier.TierUp)
        {
            if (subject.Length >= RegExpEngine.kTierUpForSubjectLengthValue)
            {
                // For very long subject strings, the regexp interpreter is currently much
                // slower than the jitted code execution. If the tier-up strategy is
                // turned on, we want to avoid this performance penalty so we eagerly
                // tier-up if the subject string length is equal or greater than the given
                // heuristic value.
                MarkTierUpForNextExec();
            }
            else if (outputRegisterCount < registers.Length)
            {
                // Tier up because the interpreter doesn't do global execution.
                MarkTierUpForNextExec();
            }
        }

        if (!EnsureCompiled(subject, isOneByte)) return RegExpResult.RE_EXCEPTION;
        Span<int> output = registers.Slice(0, RoundedRegisterCount(registers.Length));

        int width = isOneByte ? 1 : 0;
        int res;
        if (!ShouldProduceBytecode)
        {
            res = _nativeCode[width]!.Execute(subject, previousIndex, output);
        }
        else
        {
            // IrregexpInterpreter::MatchForCallFromRuntime.
            if (_tier.TierUp) TierUpTick();
            res = IrregexpInterpreter.Match(_bytecode[width]!, subject, output, outputRegisterCount,
                _maxRegisterCount, previousIndex, _backtrackLimit, Flags.IsEitherUnicode(), isOneByte);
        }

        if (res >= RegExpResult.RE_SUCCESS) return res;
        if (res == RegExpResult.RE_FALLBACK_TO_EXPERIMENTAL)
        {
            return ExperimentalOneshotExec(subject, previousIndex, output);
        }
        if (res == RegExpResult.RE_EXCEPTION) return RegExpResult.RE_EXCEPTION;
        Debug.Assert(res == RegExpResult.RE_FAILURE);
        return 0;
    }

    // ExperimentalRegExp::Exec.
    int ExperimentalExec(ReadOnlySpan<char> subject, int index, Span<int> registers)
    {
        if (!EnsureExperimentalCompiled()) return RegExpResult.RE_EXCEPTION;
        if (registers.Length < RegistersPerMatch) throw new ArgumentException("register span too small");
        int numMatches = ExperimentalRegExpInterpreter.FindMatches(_experimentalBytecode!, RegistersPerMatch,
            subject, index, registers.Slice(0, RoundedRegisterCount(registers.Length)));
        return numMatches >= 0 ? numMatches : RegExpResult.RE_EXCEPTION;
    }

    // ExperimentalRegExp::OneshotExec: compile and execute a regexp with the
    // experimental engine, regardless of its type tag.
    int ExperimentalOneshotExec(ReadOnlySpan<char> subject, int index, Span<int> registers)
    {
        Instruction[]? code = CompileExperimental();
        if (code is null) return RegExpResult.RE_EXCEPTION;
        int numMatches = ExperimentalRegExpInterpreter.FindMatches(code, RegistersPerMatch, subject, index,
            registers);
        return numMatches >= 0 ? numMatches : RegExpResult.RE_EXCEPTION;
    }
}
