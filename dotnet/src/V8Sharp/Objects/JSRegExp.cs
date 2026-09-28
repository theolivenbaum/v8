// Port of src/objects/js-regexp.{h,cc}, js-regexp-inl.h, regexp-match-info.{h,cc}
// and the heap side of src/regexp/regexp.cc (RegExp::Compile with the
// compilation cache, RegExp::Exec, Exec_Single, SetLastMatchInfo,
// ThrowRegExpException, EnsureFullyCompiled, GlobalExecRunner).
//
// The regexp engine itself (parser, compiler, interpreter, IL tier) is
// V8Sharp.RegExp: RegExpData wraps its CompiledRegExp together with the
// strings V8 keeps in the trusted RegExpData object.
using System.Buffers;
using System.Runtime.CompilerServices;
using V8Sharp.RegExp;

namespace V8Sharp.Objects;

/// <summary>
/// V8's RegExpData (AtomRegExpData / IrRegExpData): the compiled regexp shared
/// by every JSRegExp with the same source and flags (via the compilation
/// cache). Its identity is also the key of the results caches (V8 keys them
/// on the RegExpDataWrapper).
/// </summary>
public sealed class RegExpData : HeapObject
{
    internal RegExpData(CompiledRegExp compiled, JSString originalSource, JSString escapedSource, JSString? atomPattern,
        JSString[]? captureNames, int[]? captureIndices) : base(InstanceType.RegExpDataType)
    {
        Compiled = compiled;
        OriginalSource = originalSource;
        EscapedSource = escapedSource;
        AtomPattern = atomPattern;
        CaptureNames = captureNames;
        CaptureIndices = captureIndices;
    }

    /// <summary>The engine's compiled regexp (code, bytecode, tier-up state).</summary>
    public CompiledRegExp Compiled { get; }

    /// <summary>RegExpData::type_tag.</summary>
    public RegExpKind TypeTag => Compiled.Kind;

    /// <summary>RegExpData::flags.</summary>
    public RegExpFlags Flags => Compiled.Flags;

    /// <summary>RegExpData::capture_count (0 for atoms, JSRegExp::kAtomCaptureCount).</summary>
    public int CaptureCount => Compiled.Kind == RegExpKind.Atom ? JSRegExp.kAtomCaptureCount : Compiled.CaptureCount;

    /// <summary>RegExpData::original_source (flat).</summary>
    public JSString OriginalSource { get; }

    /// <summary>RegExpData::escaped_source: the value of RegExp.prototype.source.</summary>
    public JSString EscapedSource { get; }

    /// <summary>AtomRegExpData::pattern.</summary>
    public JSString? AtomPattern { get; }

    /// <summary>
    /// IrRegExpData::capture_name_map as two parallel arrays sorted by capture
    /// index: internalized names and their (1-based) capture indices.
    /// </summary>
    public JSString[]? CaptureNames { get; }
    public int[]? CaptureIndices { get; }

    /// <summary>IrRegExpData::has_capture_name_map.</summary>
    public bool HasCaptureNameMap => CaptureNames is not null;

    /// <summary>RegExpData::TypeSupportsCaptures.</summary>
    public bool TypeSupportsCaptures => TypeTag != RegExpKind.Atom;

    /// <summary>
    /// LookupNamedCapture (runtime-regexp.cc): the capture index of the next
    /// group named <paramref name="name"/> at or after <paramref name="indexInOut"/>,
    /// or -1. On success <paramref name="indexInOut"/> is advanced past the entry.
    /// </summary>
    public int LookupNamedCapture(ReadOnlySpan<char> name, ref int indexInOut)
    {
        JSString[] names = CaptureNames!;
        for (int j = indexInOut; j < names.Length; j++)
        {
            if (!names[j].IsEqualTo(name)) continue;
            indexInOut = j + 1;
            return CaptureIndices![j];
        }
        return -1;
    }
}

/// <summary>
/// V8's RegExpMatchInfo: the capture registers of the last successful match
/// and the subject, read by the legacy RegExp statics (RegExp.$1 ...).
/// </summary>
public sealed class RegExpMatchInfo : HeapObject
{
    public const int kMinCapacity = 2;

    int[] _captures;

    RegExpMatchInfo(int capacity) : base(InstanceType.RegExpMatchInfoType) => _captures = new int[capacity];

    /// <summary>RegExpMatchInfo::number_of_capture_registers.</summary>
    public int NumberOfCaptureRegisters;

    /// <summary>RegExpMatchInfo::last_subject: the subject of the last match.</summary>
    public JSString LastSubject = ReadOnlyRoots.empty_string;

    /// <summary>RegExpMatchInfo::last_input: RegExp.input (settable; undefined initially).</summary>
    public JSValue LastInput;

    public int Capacity => _captures.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Capture(int index) => _captures[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCapture(int index, int value) => _captures[index] = value;

    /// <summary>The capture registers, NumberOfCaptureRegisters long.</summary>
    public ReadOnlySpan<int> Captures => _captures.AsSpan(0, NumberOfCaptureRegisters);

    public static int CaptureStartIndex(int captureIndex) => captureIndex * 2;
    public static int CaptureEndIndex(int captureIndex) => captureIndex * 2 + 1;

    /// <summary>RegExpMatchInfo::New.</summary>
    public static RegExpMatchInfo New(int captureCount)
    {
        int capacity = JSRegExp.RegistersForCaptureCount(captureCount);
        return new RegExpMatchInfo(capacity) { NumberOfCaptureRegisters = capacity };
    }

    /// <summary>
    /// RegExpMatchInfo::ReserveCaptures. V8 allocates a larger match info and
    /// installs it on the native context; V8Sharp grows the backing array in
    /// place, which is equivalent because the object is never exposed.
    /// </summary>
    public static RegExpMatchInfo ReserveCaptures(RegExpMatchInfo matchInfo, int captureCount)
    {
        int requiredCapacity = JSRegExp.RegistersForCaptureCount(captureCount);
        if (requiredCapacity > matchInfo._captures.Length)
        {
            Array.Resize(ref matchInfo._captures, requiredCapacity);
        }
        matchInfo.NumberOfCaptureRegisters = requiredCapacity;
        return matchInfo;
    }

    /// <summary>
    /// Isolate::regexp_last_match_info: the native context's match info.
    /// Bootstrapper::InitializeGlobal creates it with RegExpMatchInfo::New(0);
    /// V8Sharp creates it on first use.
    /// </summary>
    public static RegExpMatchInfo Get(Isolate isolate)
    {
        NativeContext nativeContext = isolate.NativeContext;
        if (nativeContext.RegExpLastMatchInfo.HeapObjectOrNull is RegExpMatchInfo info) return info;
        info = New(0);
        nativeContext.RegExpLastMatchInfo = info;
        return info;
    }
}

/// <summary>JSRegExpResultIndices (src/objects/js-regexp.{h,cc}).</summary>
public static class JSRegExpResultIndices
{
    /// <summary>JSRegExpResultIndices::kGroupsDescriptorIndex (after length).</summary>
    public const int kGroupsDescriptorIndex = 1;

    /// <summary>JSRegExpResultIndices::BuildIndices: the "indices" array of a /d match.</summary>
    public static JSArray BuildIndices(Isolate isolate, RegExpMatchInfo matchInfo, RegExpData reData)
    {
        Factory factory = isolate.Factory;
        int numIndices = matchInfo.NumberOfCaptureRegisters;
        int numResults = numIndices >> 1;
        var indicesArray = new FixedArray(numResults);
        var indices = (JSArray)factory.NewJSObjectFromMap(isolate.NativeContext.RegExpResultIndicesMap);
        indices.Elements = indicesArray;
        indices.Length = JSValue.FromInt(numResults);

        for (int i = 0; i < numResults; i++)
        {
            int startOffset = matchInfo.Capture(RegExpMatchInfo.CaptureStartIndex(i));
            int endOffset = matchInfo.Capture(RegExpMatchInfo.CaptureEndIndex(i));
            // Any unmatched captures are set to undefined, otherwise we set them to a
            // subarray of the indices.
            if (startOffset == -1)
            {
                indicesArray[i] = JSValue.Undefined;
            }
            else
            {
                var sub = new FixedArray(2);
                sub[0] = JSValue.FromInt(startOffset);
                sub[1] = JSValue.FromInt(endOffset);
                indicesArray[i] = factory.NewJSArrayWithElements(sub, ElementsKind.PACKED_SMI_ELEMENTS, 2);
            }
        }

        // The groups field is the first in-object field (after the length accessor).
        if (reData.TypeTag != RegExpKind.Irregexp || !reData.HasCaptureNameMap)
        {
            indices.RawFields[0] = JSValue.Undefined;
            return indices;
        }

        // Create a groups property which returns a dictionary of named captures to
        // their corresponding capture indices.
        JSString[] names = reData.CaptureNames!;
        int[] captureIndices = reData.CaptureIndices!;
        JSObject groupNames = factory.NewSlowJSObjectWithNullProto();
        for (int i = 0; i < names.Length; i++)
        {
            JSValue captureIndicesValue = indicesArray[captureIndices[i]];
            NameDictionary dictionary = groupNames.PropertyDictionary;
            InternalIndex entry = dictionary.FindEntry(names[i]);
            // Duplicate group entries are possible if the capture groups are in
            // different alternatives, i.e. only one of them can actually match.
            if (entry.IsFound)
            {
                if (!captureIndicesValue.IsUndefined) dictionary.ValueAtPut(entry, captureIndicesValue);
            }
            else
            {
                JSObject.SetNormalizedProperty(isolate, groupNames, names[i], captureIndicesValue,
                    PropertyDetails.Empty());
            }
        }
        indices.RawFields[0] = groupNames;
        return indices;
    }
}

public sealed partial class JSRegExp
{
    /// <summary>JSRegExp::kFlagCount.</summary>
    public const int kFlagCount = 9;
    /// <summary>JSRegExp::kAtomCaptureCount.</summary>
    public const int kAtomCaptureCount = 0;
    /// <summary>JSRegExp::kAtomRegisterCount.</summary>
    public const int kAtomRegisterCount = 2;
    /// <summary>JSRegExp::kInitialLastIndexValue.</summary>
    public const int kInitialLastIndexValue = 0;
    /// <summary>JSRegExp::kNoBacktrackLimit.</summary>
    public const uint kNoBacktrackLimit = 0;
    /// <summary>JSRegExp::kTierUpForSubjectLengthValue.</summary>
    public const int kTierUpForSubjectLengthValue = 1000;
    /// <summary>JSRegExp::kMaxCaptures.</summary>
    public const int kMaxCaptures = 1 << 16;

    // Descriptor array index to important methods in the prototype.
    public const int kExecFunctionDescriptorIndex = 1;
    public const int kSymbolMatchFunctionDescriptorIndex = 15;
    public const int kSymbolMatchAllFunctionDescriptorIndex = 16;
    public const int kSymbolReplaceFunctionDescriptorIndex = 17;
    public const int kSymbolSearchFunctionDescriptorIndex = 18;
    public const int kSymbolSplitFunctionDescriptorIndex = 19;

    /// <summary>JSRegExp::kLastIndexFieldIndex: lastIndex is the first in-object property.</summary>
    public const int kLastIndexFieldIndex = 0;

    /// <summary>JSRegExp::data: null until the regexp is initialized.</summary>
    public RegExpData? Data;

    /// <summary>JSRegExp::flags (valid once <see cref="Data"/> is set).</summary>
    public RegExpFlags Flags;

    /// <summary>JSRegExp::source: the escaped source (RegExpData::escaped_source).</summary>
    public JSString Source => Data!.EscapedSource;

    /// <summary>
    /// JSRegExp::last_index: the in-object lastIndex field. Only meaningful
    /// while the regexp has its initial map (V8 reads kLastIndexOffset).
    /// </summary>
    public JSValue LastIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => RawFields[kLastIndexFieldIndex];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => RawFields[kLastIndexFieldIndex] = value;
    }

    /// <summary>JSRegExp::RegistersForCaptureCount.</summary>
    public static int RegistersForCaptureCount(int count) => (count + 1) * 2;

    /// <summary>JSRegExp::CaptureCountForRegisters.</summary>
    public static int CaptureCountForRegisters(int registerCount) => (registerCount - 2) / 2;

    // ---- Flags ------------------------------------------------------------------------

    /// <summary>
    /// JSRegExp::FlagFromChar (regexp::TryFlagFromChar): 'l' only with
    /// --enable-experimental-regexp-engine.
    /// </summary>
    public static RegExpFlags FlagFromChar(Isolate isolate, char c) => c switch
    {
        'd' => RegExpFlags.HasIndices,
        'g' => RegExpFlags.Global,
        'i' => RegExpFlags.IgnoreCase,
        'l' => isolate.Flags.enable_experimental_regexp_engine ? RegExpFlags.Linear : RegExpFlags.None,
        'm' => RegExpFlags.Multiline,
        's' => RegExpFlags.DotAll,
        'u' => RegExpFlags.Unicode,
        'v' => RegExpFlags.UnicodeSets,
        'y' => RegExpFlags.Sticky,
        _ => RegExpFlags.None,
    };

    /// <summary>JSRegExp::FlagsFromString: null for an invalid or duplicated flag.</summary>
    public static RegExpFlags? FlagsFromString(Isolate isolate, JSString flags)
    {
        int length = flags.Length;
        // A longer flags string cannot be valid.
        if (length > kFlagCount) return null;
        ReadOnlySpan<char> chars = flags.FlatSpan();
        RegExpFlags value = RegExpFlags.None;
        for (int i = 0; i < length; i++)
        {
            RegExpFlags flag = FlagFromChar(isolate, chars[i]);
            if (flag == RegExpFlags.None) return null;
            if ((value & flag) != 0) return null;  // Duplicate.
            value |= flag;
        }
        return value;
    }

    /// <summary>JSRegExp::FlagsToString: the flag characters in REGEXP_FLAG_LIST order.</summary>
    public static int FlagsToString(RegExpFlags flags, Span<char> buffer)
    {
        int i = 0;
        if ((flags & RegExpFlags.HasIndices) != 0) buffer[i++] = 'd';
        if ((flags & RegExpFlags.Global) != 0) buffer[i++] = 'g';
        if ((flags & RegExpFlags.IgnoreCase) != 0) buffer[i++] = 'i';
        if ((flags & RegExpFlags.Linear) != 0) buffer[i++] = 'l';
        if ((flags & RegExpFlags.Multiline) != 0) buffer[i++] = 'm';
        if ((flags & RegExpFlags.DotAll) != 0) buffer[i++] = 's';
        if ((flags & RegExpFlags.Unicode) != 0) buffer[i++] = 'u';
        if ((flags & RegExpFlags.UnicodeSets) != 0) buffer[i++] = 'v';
        if ((flags & RegExpFlags.Sticky) != 0) buffer[i++] = 'y';
        return i;
    }

    /// <summary>JSRegExp::StringFromFlags.</summary>
    public static JSString StringFromFlags(Isolate isolate, RegExpFlags flags)
    {
        Span<char> buffer = stackalloc char[kFlagCount];
        int length = FlagsToString(flags, buffer);
        return length == 0 ? ReadOnlyRoots.empty_string : isolate.Factory.InternalizeString(buffer[..length]);
    }

    // ---- Creation -----------------------------------------------------------------------

    /// <summary>JSRegExp::New: a fresh regexp from the %RegExp% initial map.</summary>
    public static JSRegExp New(Isolate isolate, JSString originalSource, RegExpFlags flags,
        uint backtrackLimit = kNoBacktrackLimit)
    {
        var regexp = (JSRegExp)isolate.Factory.NewJSObject(isolate.NativeContext.RegExpFunction);
        return Initialize(isolate, regexp, originalSource, flags, backtrackLimit);
    }

    /// <summary>
    /// The boilerplate copy of ConstructorBuiltinsAssembler::CreateRegExpLiteral:
    /// a fresh regexp from the initial map sharing the boilerplate's data and
    /// flags (RegExpBoilerplateDescription), with lastIndex 0. The bytecode's
    /// flags operand is V8's JSRegExp::Flags, which has RegExpFlags' layout.
    /// </summary>
    public static JSRegExp CreateFromBoilerplate(Isolate isolate, RegExpData data, RegExpFlags flags)
    {
        var regexp = (JSRegExp)isolate.Factory.NewJSObjectFromMap(isolate.NativeContext.RegExpFunction.InitialMap);
        regexp.Data = data;
        regexp.Flags = flags;
        regexp.LastIndex = JSValue.FromInt(kInitialLastIndexValue);
        return regexp;
    }

    /// <summary>
    /// JSRegExp::Initialize(isolate, regexp, source, flags_string): parses the
    /// flags (SyntaxError "Invalid flags supplied to RegExp constructor") and
    /// compiles. This is what RegExpInitializeAndCompile and the
    /// CreateRegExpLiteral bytecode call.
    /// </summary>
    public static JSRegExp Initialize(Isolate isolate, JSRegExp regexp, JSString originalSource, JSString flagsString)
    {
        RegExpFlags? flags = FlagsFromString(isolate, flagsString);
        if (flags is null || !RegExpEngine.VerifyFlags(flags.Value))
        {
            isolate.Throw(isolate.Factory.NewSyntaxError(MessageTemplate.InvalidRegExpFlags, flagsString));
        }
        return Initialize(isolate, regexp, originalSource, flags!.Value);
    }

    /// <summary>JSRegExp::Initialize(isolate, regexp, source, flags, backtrack_limit).</summary>
    public static JSRegExp Initialize(Isolate isolate, JSRegExp regexp, JSString originalSource, RegExpFlags flags,
        uint backtrackLimit = kNoBacktrackLimit)
    {
        // If source is the empty string we set it to "(?:)" instead as
        // suggested by ECMA-262, 5th, section 15.10.4.1.
        if (originalSource.Length == 0) originalSource = ReadOnlyRoots.query_colon_string;

        originalSource = JSString.Flatten(isolate, originalSource);

        regexp.Data = Compile(isolate, originalSource, flags, backtrackLimit);
        regexp.Flags = flags;

        Map map = regexp.Map;
        if (map.GetConstructor() is JSFunction constructor && constructor.HasInitialMap &&
            ReferenceEquals(constructor.InitialMap, map))
        {
            // If we still have the original map, set in-object properties directly.
            regexp.LastIndex = JSValue.FromInt(kInitialLastIndexValue);
        }
        else
        {
            // Map has changed, so use generic, but slower, method.
            ObjectOps.SetProperty(isolate, regexp, ReadOnlyRoots.lastIndex_string, JSValue.FromInt(kInitialLastIndexValue),
                StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        }
        return regexp;
    }

    // ---- RegExp::Compile ----------------------------------------------------------------

    /// <summary>
    /// The regexp part of V8's CompilationCache (compilation-cache.cc,
    /// CompilationCacheRegExp): RegExpData by (source, flags). V8 ages the
    /// table by GC generation; V8Sharp clears it when it grows past
    /// <see cref="kCompilationCacheCapacity"/> entries.
    /// </summary>
    sealed class CompilationCache
    {
        public readonly Dictionary<(string Source, RegExpFlags Flags), RegExpData> Table = new();
    }

    const int kCompilationCacheCapacity = 4096;
    static readonly ConditionalWeakTable<Isolate, CompilationCache> s_compilationCaches = new();

    /// <summary>
    /// RegExp::Compile: parses the pattern (SyntaxError on failure) and creates
    /// the RegExpData, reusing the isolate's cached one for the same source and
    /// flags. Irregexp code is compiled lazily on the first exec.
    /// </summary>
    public static RegExpData Compile(Isolate isolate, JSString originalSource, RegExpFlags flags, uint backtrackLimit)
    {
        // Caching is based only on the pattern and flags, but code also differs when
        // a backtrack limit is set. A present backtrack limit is very much *not* the
        // common case, so just skip the cache for these.
        bool isCompilationCacheEnabled = backtrackLimit == kNoBacktrackLimit;
        string source = originalSource.Flatten();
        CompilationCache? cache = null;
        if (isCompilationCacheEnabled)
        {
            cache = s_compilationCaches.GetValue(isolate, static _ => new CompilationCache());
            if (cache.Table.TryGetValue((source, flags), out RegExpData? cached)) return cached;
        }

        SyncEngineFlags(isolate);
        RegExpCompileResult result = RegExpEngine.Compile(source, flags, backtrackLimit, TierPolicy(isolate));
        if (!result.Succeeded)
        {
            // Throw an exception if we fail to parse the pattern.
            ThrowRegExpException(isolate, flags, originalSource, result.Error);
        }
        CompiledRegExp compiled = result.RegExp!;

        JSString escapedSource = ReferenceEquals(compiled.EscapedSource, source)
            ? originalSource
            : isolate.Factory.NewStringFromUtf16(compiled.EscapedSource);
        JSString? atomPattern = null;
        if (compiled.Kind == RegExpKind.Atom)
        {
            atomPattern = ReferenceEquals(compiled.AtomPattern, source)
                ? originalSource
                : isolate.Factory.NewStringFromUtf16(compiled.AtomPattern!);
        }

        // RegExp::CreateCaptureNameMap: names are internalized so they can be used
        // as property names in the exec results.
        JSString[]? captureNames = null;
        int[]? captureIndices = null;
        if (compiled.CaptureNameMap is { Count: > 0 } map)
        {
            captureNames = new JSString[map.Count];
            captureIndices = new int[map.Count];
            for (int i = 0; i < map.Count; i++)
            {
                captureNames[i] = isolate.Factory.InternalizeString(map[i].Key);
                captureIndices[i] = map[i].Value;
            }
        }

        var data = new RegExpData(compiled, originalSource, escapedSource, atomPattern, captureNames, captureIndices);
        if (cache is not null)
        {
            if (cache.Table.Count >= kCompilationCacheCapacity) cache.Table.Clear();
            cache.Table[(source, flags)] = data;
        }
        return data;
    }

    /// <summary>The tiering flags of the isolate (V8 reads the global v8_flags at each exec).</summary>
    static RegExpTierPolicy TierPolicy(Isolate isolate)
    {
        FlagList f = isolate.Flags;
        bool interpretAll = f.regexp_interpret_all || f.jitless;
        return new RegExpTierPolicy(interpretAll, f.regexp_tier_up && !interpretAll, f.regexp_tier_up_ticks);
    }

    /// <summary>
    /// Deviation: V8Sharp.RegExp keeps the experimental-engine flags as
    /// process-wide statics (they are process-global in V8 too); the isolate's
    /// flags are copied into them before compiling.
    /// </summary>
    static void SyncEngineFlags(Isolate isolate)
    {
        FlagList f = isolate.Flags;
        RegExpEngine.s_enableExperimentalRegExpEngine = f.enable_experimental_regexp_engine;
        RegExpEngine.s_defaultToExperimentalRegExpEngine = f.default_to_experimental_regexp_engine;
        RegExpEngine.s_enableExperimentalRegExpEngineOnExcessiveBacktracks =
            f.enable_experimental_regexp_engine_on_excessive_backtracks;
        RegExpEngine.s_regexpBacktracksBeforeFallback = f.regexp_backtracks_before_fallback;
    }

    /// <summary>
    /// RegExp::ThrowRegExpException: SyntaxError "Invalid regular expression:
    /// /source/flags: message".
    /// </summary>
    public static JSValue ThrowRegExpException(Isolate isolate, RegExpFlags flags, JSString originalSource, RegExpError error)
    {
        JSString errorText = isolate.Factory.NewStringFromAsciiChecked(RegExpErrors.ErrorString(error));
        JSString flagString = StringFromFlags(isolate, flags);
        return isolate.Throw(isolate.Factory.NewSyntaxError(MessageTemplate.MalformedRegExp, originalSource, flagString,
            errorText));
    }

    // ---- RegExp::Exec ---------------------------------------------------------------------

    [ThreadStatic] static JSString? t_oneByteCacheKey0;
    [ThreadStatic] static bool t_oneByteCacheValue0;
    [ThreadStatic] static JSString? t_oneByteCacheKey1;
    [ThreadStatic] static bool t_oneByteCacheValue1;

    /// <summary>
    /// String::IsOneByteRepresentationUnderneath. V8 knows it from the string's
    /// representation; V8Sharp strings are always UTF-16, so it is computed from
    /// the contents (vectorized) and remembered for the last two subjects, so a
    /// loop of execs over one long subject does not rescan it each time.
    /// </summary>
    public static bool IsOneByteSubject(JSString subject, ReadOnlySpan<char> flat)
    {
        if (flat.Length < 64) return CompiledRegExp.IsOneByteSubject(flat);
        if (ReferenceEquals(t_oneByteCacheKey0, subject)) return t_oneByteCacheValue0;
        if (ReferenceEquals(t_oneByteCacheKey1, subject)) return t_oneByteCacheValue1;
        bool value = CompiledRegExp.IsOneByteSubject(flat);
        t_oneByteCacheKey1 = t_oneByteCacheKey0;
        t_oneByteCacheValue1 = t_oneByteCacheValue0;
        t_oneByteCacheKey0 = subject;
        t_oneByteCacheValue0 = value;
        return value;
    }

    /// <summary>
    /// RegExp::EnsureFullyCompiled: compiles irregexp/experimental code for the
    /// subject's width (SyntaxError if the pattern is too large to compile).
    /// </summary>
    public static void EnsureFullyCompiled(Isolate isolate, RegExpData data, JSString subject)
    {
        CompiledRegExp re = data.Compiled;
        if (re.Kind == RegExpKind.Atom) return;
        ReadOnlySpan<char> flat = subject.FlatSpan();
        bool isOneByte = re.Kind == RegExpKind.Irregexp && IsOneByteSubject(subject, flat);
        if (!re.EnsureCompiled(flat, isOneByte)) ThrowRegExpException(isolate, re.Flags, data.OriginalSource, re.CompileError);
    }

    /// <summary>
    /// RegExp::Exec (RegExpImpl::AtomExec / IrregexpExec / ExperimentalRegExp::Exec):
    /// runs the regexp from <paramref name="index"/> (0 &lt;= index &lt;= length)
    /// and returns the number of matches written to <paramref name="output"/>
    /// (more than one only for global regexps given room for several).
    /// Throws RangeError on backtrack-stack overflow and SyntaxError when lazy
    /// compilation fails, as V8's RE_EXCEPTION paths do.
    /// </summary>
    public static int ExecRaw(Isolate isolate, RegExpData data, JSString subject, int index, Span<int> output)
    {
        ReadOnlySpan<char> flat = subject.FlatSpan();
        CompiledRegExp re = data.Compiled;
        bool isOneByte = re.Kind == RegExpKind.Irregexp && IsOneByteSubject(subject, flat);
        int result = re.Exec(flat, index, output, isOneByte);
        if (result < 0) ThrowExecException(isolate, data);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowExecException(Isolate isolate, RegExpData data)
    {
        CompiledRegExp re = data.Compiled;
        // Lazy compilation failed (RegExpImpl::CompileIrregexpFromSource).
        if (re.CompileError != RegExpError.None)
        {
            ThrowRegExpException(isolate, re.Flags, data.OriginalSource, re.CompileError);
        }
        // A stack overflow was detected in RegExp code.
        isolate.StackOverflow();
    }

    /// <summary>
    /// RegExp::Exec_Single: one exec; on success fills the last match info and
    /// returns it, else null.
    /// </summary>
    public static RegExpMatchInfo? ExecSingle(Isolate isolate, JSRegExp regexp, JSString subject, int index,
        RegExpMatchInfo lastMatchInfo)
    {
        RegExpData data = regexp.Data!;
        int captureCount = data.CaptureCount;
        int registerCount = RegistersForCaptureCount(captureCount);
        int[]? rented = null;
        Span<int> registers = registerCount <= 64
            ? stackalloc int[registerCount]
            : (rented = ArrayPool<int>.Shared.Rent(registerCount)).AsSpan(0, registerCount);
        try
        {
            int result = ExecRaw(isolate, data, subject, index, registers);
            if (result == 0) return null;
            return SetLastMatchInfo(isolate, lastMatchInfo, subject, captureCount, registers);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>RegExp::SetLastMatchInfo.</summary>
    public static RegExpMatchInfo SetLastMatchInfo(Isolate isolate, RegExpMatchInfo lastMatchInfo, JSString subject,
        int captureCount, ReadOnlySpan<int> match)
    {
        RegExpMatchInfo result = RegExpMatchInfo.ReserveCaptures(lastMatchInfo, captureCount);
        int captureRegisterCount = RegistersForCaptureCount(captureCount);
        if (!match.IsEmpty)
        {
            for (int i = 0; i < captureRegisterCount; i++) result.SetCapture(i, match[i]);
        }
        result.LastSubject = subject;
        result.LastInput = subject;
        return result;
    }
}

/// <summary>
/// regexp::GlobalExecRunner (regexp.cc): iterates the matches of a global
/// regexp, asking the engine for a batch of matches at a time where it can.
/// Dispose returns the register array to the pool.
/// </summary>
public sealed class GlobalExecRunner : IDisposable
{
    /// <summary>Isolate::kJSRegexpStaticOffsetsVectorSize.</summary>
    public const int kJSRegexpStaticOffsetsVectorSize = 128;

    readonly Isolate _isolate;
    readonly RegExpData _data;
    readonly JSString _subject;
    readonly int _registersPerMatch;
    readonly int _registerArraySize;
    int[] _registerArray;
    int _numMatches;
    int _currentMatchIndex;

    public GlobalExecRunner(Isolate isolate, RegExpData data, JSString subject)
    {
        Debug.Assert(data.Flags.IsGlobal());
        _isolate = isolate;
        _data = data;
        _subject = subject;
        switch (data.TypeTag)
        {
            case RegExpKind.Atom:
                _registersPerMatch = JSRegExp.kAtomRegisterCount;
                _registerArraySize = kJSRegexpStaticOffsetsVectorSize;
                break;
            default:
                JSRegExp.EnsureFullyCompiled(isolate, data, subject);
                _registersPerMatch = JSRegExp.RegistersForCaptureCount(data.CaptureCount);
                // Global loop in interpreted regexp is not implemented in V8, so the
                // vector holds one match there; V8Sharp's engine fills the vector
                // for every tier (CompiledRegExp.Exec), so a batch is always used.
                _registerArraySize = Math.Max(_registersPerMatch, kJSRegexpStaticOffsetsVectorSize);
                break;
        }
        _registerArray = ArrayPool<int>.Shared.Rent(_registerArraySize);
        // Set state so that fetching the results the first time triggers a call
        // to the compiled regexp.
        _currentMatchIndex = MaxMatches - 1;
        _numMatches = MaxMatches;
        int lastMatch = _currentMatchIndex * _registersPerMatch;
        _registerArray[lastMatch] = -1;
        _registerArray[lastMatch + 1] = 0;
    }

    int MaxMatches => _registerArraySize / _registersPerMatch;

    public int RegistersPerMatch => _registersPerMatch;

    int AdvanceZeroLength(int lastIndex)
    {
        ReadOnlySpan<char> s = _subject.FlatSpan();
        if (_data.Flags.IsEitherUnicode() && (uint)(lastIndex + 1) < (uint)s.Length &&
            char.IsHighSurrogate(s[lastIndex]) && char.IsLowSurrogate(s[lastIndex + 1]))
        {
            // Advance over the surrogate pair.
            return lastIndex + 2;
        }
        return lastIndex + 1;
    }

    /// <summary>
    /// GlobalExecRunner::FetchNext: the registers of the next match, or an
    /// empty span when there are no more matches.
    /// </summary>
    public ReadOnlySpan<int> FetchNext()
    {
        _currentMatchIndex++;
        if (_currentMatchIndex >= _numMatches)
        {
            // Current batch of results exhausted.
            // Fail if last batch was not even fully filled.
            if (_numMatches < MaxMatches)
            {
                _numMatches = 0;  // Signal failed match.
                return default;
            }

            int lastMatch = (_currentMatchIndex - 1) * _registersPerMatch;
            int lastEndIndex = _registerArray[lastMatch + 1];
            Span<int> registers = _registerArray.AsSpan(0, _registerArraySize);

            if (_data.TypeTag == RegExpKind.Irregexp)
            {
                int lastStartIndex = _registerArray[lastMatch];
                if (lastStartIndex == lastEndIndex)
                {
                    // Zero-length match. Advance by one code point.
                    lastEndIndex = AdvanceZeroLength(lastEndIndex);
                }
                if ((uint)lastEndIndex > (uint)_subject.Length)
                {
                    _numMatches = 0;  // Signal failed match.
                    return default;
                }
            }
            else if (_data.TypeTag == RegExpKind.Experimental && lastEndIndex > _subject.Length)
            {
                _numMatches = 0;
                return default;
            }
            _numMatches = JSRegExp.ExecRaw(_isolate, _data, _subject, lastEndIndex, registers);
            if (_numMatches <= 0) return default;

            _currentMatchIndex = 0;
            return _registerArray.AsSpan(0, _registersPerMatch);
        }
        return _registerArray.AsSpan(_currentMatchIndex * _registersPerMatch, _registersPerMatch);
    }

    /// <summary>GlobalExecRunner::LastSuccessfulMatch.</summary>
    public ReadOnlySpan<int> LastSuccessfulMatch()
    {
        int index = _currentMatchIndex * _registersPerMatch;
        if (_numMatches == 0)
        {
            // After a failed match we shift back by one result.
            index -= _registersPerMatch;
        }
        return _registerArray.AsSpan(index, _registersPerMatch);
    }

    public void Dispose()
    {
        int[] array = _registerArray;
        _registerArray = [];
        if (array.Length > 0) ArrayPool<int>.Shared.Return(array);
    }
}
