// Port of src/objects/js-function.{h,cc,-inl.h},
// src/objects/shared-function-info.{h,cc,-inl.h}, src/objects/script.{h,cc}
// and src/objects/feedback-cell.h: the data shape of functions and the
// function-level operations the object model needs (initial maps, prototypes,
// name/length, toString).
//
// Code objects are not ported: a JSFunction runs either a builtin
// (SharedFunctionInfo.BuiltinId, dispatched through BuiltinRegistry) or its
// bytecode (SharedFunctionInfo.FunctionData, run by Isolate.InterpreterEntry,
// which enters the SharedFunctionInfo's baseline code when it has some).
using System.Runtime.CompilerServices;
using V8Sharp.Builtins;
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

/// <summary>V8's Script: a compiled source text and its metadata.</summary>
public sealed partial class Script : HeapObject
{
    /// <summary>Script::Type.</summary>
    public enum Type : byte { Native = 0, Extension = 1, Normal = 2, Wasm = 3, Inspector = 4 }

    /// <summary>Script::CompilationType.</summary>
    public enum CompilationType : byte { Host = 0, Eval = 1 }

    /// <summary>Script::CompilationState.</summary>
    public enum CompilationState : byte { Initial = 0, Compiled = 1 }

    public enum OffsetFlag { NoOffset, WithOffset }

    /// <summary>Script::PositionInfo.</summary>
    public struct PositionInfo
    {
        public int Line;        // Zero-based line number.
        public int Column;      // Zero-based column number.
        public int LineStart;   // Position of first character in line.
        public int LineEnd;     // Position of final linebreak character in line.
    }

    public const int kTemporaryScriptId = -2;

    public Script() : base(InstanceType.ScriptType) { }

    public JSValue Source;
    public JSValue Name;
    public int Id;
    public int LineOffset;
    public int ColumnOffset;
    public JSValue ContextData;
    public Type ScriptType = Type.Normal;
    public CompilationType Compilation = CompilationType.Host;
    public CompilationState State = CompilationState.Initial;
    public bool IsReplMode;
    public bool OriginOptionsIsModule;
    public bool OriginOptionsIsSharedCrossOrigin;
    public JSValue SourceUrl;
    public JSValue SourceMappingUrl;
    public JSValue HostDefinedOptions;

    /// <summary>eval_from_shared (for eval scripts).</summary>
    public SharedFunctionInfo? EvalFromShared;
    /// <summary>wrapped_arguments (for wrapped function scripts).</summary>
    public FixedArray? WrappedArguments;
    public int EvalFromPosition;

    /// <summary>Script::infos: the SharedFunctionInfos of the script by function literal id.</summary>
    public SharedFunctionInfo?[] Infos = [];

    int[]? _lineEnds;

    public bool HasEvalFromShared => EvalFromShared is not null;
    public bool IsWrapped => WrappedArguments is not null;
    public bool HasEvalOrigin => Compilation == CompilationType.Eval;
    public bool HasLineEnds => _lineEnds is not null;
    public bool IsUserJavaScript() => ScriptType == Type.Normal;
    public bool IsSubjectToDebugging() => ScriptType is Type.Normal or Type.Wasm;

    public static void InitLineEnds(Script script)
    {
        if (script._lineEnds is not null) return;
        script._lineEnds = script.Source.HeapObjectOrNull is JSString src
            ? JSString.CalculateLineEnds(src, true)
            : [];
    }

    /// <summary>
    /// Script::GetEvalPosition: the eval position, translated from the negated
    /// bytecode offset an indirect eval or dynamic function stored.
    /// </summary>
    public int GetEvalPosition()
    {
        int position = EvalFromPosition;
        if (position < 0)
        {
            // Due to laziness, the position may not have been translated from code
            // offset yet, which would be encoded as negative integer. In that case,
            // translate and set the position.
            if (EvalFromShared?.FunctionData is Interpreter.BytecodeArray bytecode)
            {
                position = bytecode.SourcePosition(-position);
            }
            else
            {
                position = 0;
            }
            EvalFromPosition = position;
        }
        return position;
    }

    public Script GetEvalOrigin()
    {
        Script originScript = this;
        while (originScript.HasEvalFromShared)
        {
            originScript = (Script)originScript.EvalFromShared!.Script!;
        }
        return originScript;
    }

    /// <summary>Script::GetPositionInfo (with line ends computed on demand).</summary>
    public bool GetPositionInfo(int position, out PositionInfo info, OffsetFlag offsetFlag = OffsetFlag.WithOffset)
    {
        info = default;
        InitLineEnds(this);
        int[] ends = _lineEnds!;
        int endsLen = ends.Length;
        if (endsLen == 0) return false;

        // Return early on invalid positions. Negative positions behave as if 0 was
        // passed, and positions beyond the end of the script return as failure.
        if (position < 0) position = 0;
        else if (position > ends[endsLen - 1]) return false;

        // Determine line number by doing a binary search on the line ends array.
        if (ends[0] >= position)
        {
            info.Line = 0;
            info.LineStart = 0;
            info.Column = position;
        }
        else
        {
            int left = 0;
            int right = endsLen - 1;
            while (right > 0)
            {
                int mid = left + (right - left) / 2;
                if (position > ends[mid]) left = mid + 1;
                else if (position <= ends[mid - 1]) right = mid - 1;
                else
                {
                    info.Line = mid;
                    break;
                }
            }
            info.LineStart = ends[info.Line - 1] + 1;
            info.Column = position - info.LineStart;
        }

        // Line end is position of the linebreak character.
        info.LineEnd = ends[info.Line];
        if (info.LineEnd > 0 && Source.HeapObjectOrNull is JSString s &&
            s.Length >= info.LineEnd && s.Get(info.LineEnd - 1) == '\r')
        {
            info.LineEnd--;
        }

        if (offsetFlag == OffsetFlag.WithOffset)
        {
            if (info.Line == 0) info.Column += ColumnOffset;
            info.Line += LineOffset;
        }
        return true;
    }

    public int GetColumnNumber(int codePos)
    {
        GetPositionInfo(codePos, out PositionInfo info);
        return info.Column;
    }

    public int GetLineNumber(int codePos)
    {
        GetPositionInfo(codePos, out PositionInfo info);
        return info.Line;
    }

    /// <summary>Script::GetNameOrSourceURL. Keep in sync with ScriptNameOrSourceURL in messages.js.</summary>
    public JSValue GetNameOrSourceURL() => !SourceUrl.IsUndefined ? SourceUrl : Name;

    /// <summary>Script::FindSharedFunctionInfo by function literal id.</summary>
    public SharedFunctionInfo? FindSharedFunctionInfo(int functionLiteralId) =>
        (uint)functionLiteralId < (uint)Infos.Length ? Infos[functionLiteralId] : null;
}

/// <summary>V8's UncompiledData: positions (and preparse data) of a not yet compiled function.</summary>
public sealed class UncompiledData(JSString inferredName, int startPosition, int endPosition, object? preparseData = null)
    : HeapObject(InstanceType.FixedArrayType)
{
    public JSString InferredName = inferredName;
    public int StartPosition = startPosition;
    public int EndPosition = endPosition;
    /// <summary>PreparseData (the parser's ProducedPreparseData), for UncompiledDataWithPreparseData.</summary>
    public object? PreparseData = preparseData;
}

/// <summary>V8's ClassPositions: the source range of a class, for Function.prototype.toString.</summary>
public sealed class ClassPositions(int start, int end) : HeapObject(InstanceType.FixedArrayType)
{
    public readonly int Start = start;
    public readonly int End = end;
}

/// <summary>
/// V8's SharedFunctionInfo: everything about a function that does not depend
/// on the closure (kind, language mode, parameter counts, name, positions,
/// script, and the code to run).
/// </summary>
public sealed class SharedFunctionInfo : HeapObject
{
    public const int kMaximumFunctionTokenOffset = ushort.MaxValue - 1;
    public const ushort kFunctionTokenOutOfRange = ushort.MaxValue;
    /// <summary>kDontAdaptArgumentsSentinel: the parameter count of builtins that take any number of arguments.</summary>
    public const int kDontAdaptArgumentsSentinel = 0;

    public SharedFunctionInfo() : base(InstanceType.SharedFunctionInfoType) { }

    // --- function data -----------------------------------------------------

    /// <summary>The builtin this function runs, or <see cref="Builtin.NoBuiltinId"/>.</summary>
    public Builtin BuiltinId
    {
        get => _builtinId;
        set { _builtinId = value; InterpreterCallMode = 0; }
    }
    Builtin _builtinId = Builtin.NoBuiltinId;

    /// <summary>
    /// How the interpreter's dispatch loop calls this function, cached from
    /// the fields it depends on (function data, builtin id, baseline code,
    /// Maglev code, kind, language mode), whose setters reset it:
    /// 0 not computed, else an InterpreterInlineCalls.kCallMode* value.
    /// V8 keeps the equivalent in the JSFunction's code field (the
    /// interpreter entry trampoline vs. other code); see
    /// InterpreterInlineCalls.InlineCallMode.
    /// </summary>
    public byte InterpreterCallMode;

    /// <summary>
    /// V8's function_data: the BytecodeArray once compiled (the interpreter's
    /// type), an <see cref="Objects.UncompiledData"/> before, or null for builtins.
    /// TODO(merge): type as V8Sharp.Interpreter.BytecodeArray once the interpreter lands.
    /// </summary>
    public object? FunctionData
    {
        get => _functionData;
        set { _functionData = value; InterpreterCallMode = 0; }
    }
    object? _functionData;

    /// <summary>
    /// SharedFunctionInfo::baseline_code: the Sparkplug code compiled from the
    /// bytecode (a <see cref="Baseline.BaselineCode"/>), or null. V8 keeps it in
    /// function_data (a Code object that points to the bytecode); V8Sharp keeps
    /// function_data the BytecodeArray and holds the code beside it.
    /// </summary>
    public Baseline.BaselineCode? BaselineCode
    {
        get => _baselineCode;
        set { _baselineCode = value; InterpreterCallMode = 0; }
    }
    Baseline.BaselineCode? _baselineCode;

    /// <summary>
    /// Some closure of this function got Maglev code (on its feedback
    /// vector): calls must go through the entry that checks for it.
    /// </summary>
    public bool MayHaveMaglevCode
    {
        get => _mayHaveMaglevCode;
        set { _mayHaveMaglevCode = value; InterpreterCallMode = 0; }
    }
    bool _mayHaveMaglevCode;

    /// <summary>SharedFunctionInfo::HasBaselineCode.</summary>
    public bool HasBaselineCode => BaselineCode is not null;

    /// <summary>SharedFunctionInfo::cached_tiering_decision.</summary>
    public CachedTieringDecision CachedTieringDecision;

    public bool HasBuiltinId => BuiltinId != Builtin.NoBuiltinId;
    public bool HasUncompiledData => FunctionData is UncompiledData;
    public UncompiledData UncompiledData => (UncompiledData)FunctionData!;
    public bool IsCompiled => !HasUncompiledData && (HasBuiltinId || FunctionData is not null);
    public bool IsApiFunction => FunctionData is FunctionTemplateInfo;
    public FunctionTemplateInfo GetApiFunctionData() => (FunctionTemplateInfo)FunctionData!;

    // --- name and scope info ----------------------------------------------

    /// <summary>name_or_scope_info: a String, kNoSharedNameSentinel (Smi 0) or a ScopeInfo.</summary>
    public HeapObject? NameOrScopeInfo;
    JSValue _sharedName = JSValue.Zero;

    public bool HasSharedName =>
        NameOrScopeInfo is ScopeInfo si ? si.HasSharedFunctionName() : !_sharedName.IsIdenticalTo(JSValue.Zero);

    /// <summary>SharedFunctionInfo::Name: the shared name or the empty string.</summary>
    public JSString Name()
    {
        if (!HasSharedName) return ReadOnlyRoots.empty_string;
        if (NameOrScopeInfo is ScopeInfo si)
        {
            if (si.HasFunctionName()) return si.FunctionName().As<JSString>();
            return ReadOnlyRoots.empty_string;
        }
        return _sharedName.As<JSString>();
    }

    public void SetName(JSString name)
    {
        if (NameOrScopeInfo is ScopeInfo si) si.SetFunctionName(name);
        else _sharedName = name;
    }

    /// <summary>Sets the name to kNoSharedNameSentinel (anonymous functions).</summary>
    public void ClearName() => _sharedName = JSValue.Zero;

    public bool HasScopeInfo => NameOrScopeInfo is ScopeInfo;
    public ScopeInfo ScopeInfo => NameOrScopeInfo as ScopeInfo ?? ScopeInfo.EmptyScopeInfo;

    /// <summary>SetScopeInfo moves the shared name onto the ScopeInfo, as V8 does.</summary>
    public void SetScopeInfo(ScopeInfo scopeInfo)
    {
        if (NameOrScopeInfo is not V8Sharp.Objects.ScopeInfo && scopeInfo.HasFunctionName() && !_sharedName.IsIdenticalTo(JSValue.Zero))
        {
            scopeInfo.SetFunctionName(_sharedName);
        }
        JSString inferred = InferredName();
        if (inferred.Length != 0 && scopeInfo.HasInferredFunctionName) scopeInfo.SetInferredFunctionName(inferred);
        NameOrScopeInfo = scopeInfo;
    }

    /// <summary>outer_scope_info (before compilation), or null.</summary>
    public ScopeInfo? OuterScopeInfo;
    /// <summary>feedback_metadata (after compilation); the interpreter's FeedbackMetadata.</summary>
    public object? FeedbackMetadata;

    public JSString InferredName()
    {
        if (NameOrScopeInfo is ScopeInfo si && si.HasInferredFunctionName && si.InferredFunctionName().HeapObjectOrNull is JSString s) return s;
        if (FunctionData is UncompiledData u) return u.InferredName;
        return ReadOnlyRoots.empty_string;
    }

    // --- script and positions --------------------------------------------

    /// <summary>The Script, or null (V8: undefined) for builtins.</summary>
    public Script? Script;
    public int FunctionLiteralId;
    public ushort RawFunctionTokenOffset;
    public int UniqueId;

    public void SetScript(Script script, int functionLiteralId)
    {
        Script = script;
        FunctionLiteralId = functionLiteralId;
        if ((uint)functionLiteralId >= (uint)script.Infos.Length)
        {
            Array.Resize(ref script.Infos, Math.Max(functionLiteralId + 1, script.Infos.Length * 2));
        }
        script.Infos[functionLiteralId] = this;
    }

    public int StartPosition()
    {
        if (NameOrScopeInfo is ScopeInfo info && !info.IsEmpty()) return info.StartPosition();
        if (FunctionData is UncompiledData u) return u.StartPosition;
        if (HasBuiltinId) return BuiltinId == Builtin.CompileLazy ? Globals.kNoSourcePosition : 0;
        return Globals.kNoSourcePosition;
    }

    public int EndPosition()
    {
        if (NameOrScopeInfo is ScopeInfo info && !info.IsEmpty()) return info.EndPosition();
        if (FunctionData is UncompiledData u) return u.EndPosition;
        if (HasBuiltinId) return BuiltinId == Builtin.CompileLazy ? Globals.kNoSourcePosition : 0;
        return Globals.kNoSourcePosition;
    }

    public void SetFunctionTokenPosition(int functionTokenPosition, int startPosition)
    {
        int offset = functionTokenPosition == Globals.kNoSourcePosition ? 0 : startPosition - functionTokenPosition;
        if (offset > kMaximumFunctionTokenOffset) offset = kFunctionTokenOutOfRange;
        RawFunctionTokenOffset = (ushort)offset;
    }

    public int FunctionTokenPosition()
    {
        int offset = RawFunctionTokenOffset;
        return offset == kFunctionTokenOutOfRange ? Globals.kNoSourcePosition : StartPosition() - offset;
    }

    /// <summary>Needs to be kept in sync with Scope::UniqueIdInScript and ScopeInfo::UniqueIdInScript.</summary>
    public int UniqueIdInScript()
    {
        if (FunctionLiteralId == Globals.kFunctionLiteralIdTopLevel) return -2;
        if (SyntaxKind == FunctionSyntaxKind.Wrapped) return -1;
        return StartPosition() + (Globals.IsDefaultConstructor(Kind) ? 1 : 0);
    }

    public bool HasSourceCode() =>
        Script is not null && Script.Source.HeapObjectOrNull is JSString s && s.Length > 0;

    public bool IsUserJavaScript() => Script is not null && Script.IsUserJavaScript();
    public bool IsSubjectToDebugging() => IsUserJavaScript();

    // --- flags ---------------------------------------------------------------

    // Kind, LanguageMode and Native reset InterpreterCallMode like the function data.
    public FunctionKind Kind
    {
        get => _kind;
        set { _kind = value; InterpreterCallMode = 0; }
    }
    FunctionKind _kind;
    public LanguageMode LanguageMode
    {
        get => _languageMode;
        set { _languageMode = value; InterpreterCallMode = 0; }
    }
    LanguageMode _languageMode;
    public FunctionSyntaxKind SyntaxKind;
    public bool Native
    {
        get => _native;
        set { _native = value; InterpreterCallMode = 0; }
    }
    bool _native;
    public bool IsToplevel;
    public bool AllowsLazyCompilation;
    public bool HasDuplicateParameters;
    public bool NameShouldPrintAsAnonymous;
    public bool ArePropertiesFinal;
    public bool ClassScopeHasPrivateBrand;
    public bool HasStaticPrivateMethodsOrAccessors;
    public bool RequiresInstanceMembersInitializer;
    public bool PrivateNameLookupSkipsOuterClass;
    public bool IsHoistedInContext;
    public bool HasSimpleParameters = true;
    public int FunctionMapIndex;
    public byte ExpectedNofProperties;

    /// <summary>SharedFunctionInfo::UpdateFunctionMapIndex.</summary>
    public void UpdateFunctionMapIndex() =>
        FunctionMapIndex = Context.FunctionMapIndex(LanguageMode, Kind, HasSharedName);

    public bool IsWrapped => SyntaxKind == FunctionSyntaxKind.Wrapped;
    public bool IsClassConstructor => Globals.IsClassConstructor(Kind);
    public bool IsScript => IsToplevel && NameOrScopeInfo is ScopeInfo si && si.IsScriptScope;

    // --- parameter counts ---------------------------------------------------

    /// <summary>The JS-visible "length" (number of parameters before the first default/rest).</summary>
    public ushort Length;

    ushort _internalFormalParameterCountWithReceiver = 1;

    /// <summary>set_internal_formal_parameter_count (the value includes the receiver).</summary>
    public void SetInternalFormalParameterCount(int valueWithReceiver) =>
        _internalFormalParameterCountWithReceiver = (ushort)valueWithReceiver;

    public ushort InternalFormalParameterCountWithReceiver => _internalFormalParameterCountWithReceiver;

    public ushort InternalFormalParameterCountWithoutReceiver =>
        _internalFormalParameterCountWithReceiver == kDontAdaptArgumentsSentinel
            ? (ushort)0
            : (ushort)(_internalFormalParameterCountWithReceiver - 1);

    /// <summary>DontAdaptArguments: builtins with a variable argument count.</summary>
    public void DontAdaptArguments() => _internalFormalParameterCountWithReceiver = kDontAdaptArgumentsSentinel;

    // --- names for printing ---------------------------------------------------

    /// <summary>SharedFunctionInfo::DebugName.</summary>
    public static JSString DebugName(Isolate isolate, SharedFunctionInfo shared)
    {
        FunctionKind functionKind = shared.Kind;
        if (Globals.IsClassInitializerFunction(functionKind))
        {
            return Globals.IsClassInstanceInitializerFunction(functionKind)
                ? ReadOnlyRoots.instance_members_initializer_string
                : ReadOnlyRoots.static_initializer_string;
        }
        JSString functionName = shared.Name();
        if (functionName.Length == 0) functionName = shared.InferredName();
        return functionName;
    }

    /// <summary>SharedFunctionInfo::GetSourceCode.</summary>
    public static JSValue GetSourceCode(Isolate isolate, SharedFunctionInfo shared)
    {
        if (!shared.HasSourceCode()) return JSValue.Undefined;
        var source = shared.Script!.Source.As<JSString>();
        return isolate.Factory.NewSubString(source, shared.StartPosition(), shared.EndPosition());
    }

    /// <summary>SharedFunctionInfo::GetSourceCodeHarmony: the source from the function token (toString).</summary>
    public static JSValue GetSourceCodeHarmony(Isolate isolate, SharedFunctionInfo shared)
    {
        if (!shared.HasSourceCode()) return JSValue.Undefined;
        var scriptSource = shared.Script!.Source.As<JSString>();
        int startPos = shared.FunctionTokenPosition();
        JSString source = isolate.Factory.NewSubString(scriptSource, startPos, shared.EndPosition());
        if (!shared.IsWrapped) return source;

        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("function ");
        builder.AppendString(shared.Name());
        builder.AppendCharacter('(');
        FixedArray args = shared.Script.WrappedArguments!;
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) builder.AppendCStringLiteral(", ");
            builder.AppendString(args[i].As<JSString>());
        }
        builder.AppendCStringLiteral(") {\n");
        builder.AppendString(source);
        builder.AppendCStringLiteral("\n}");
        return builder.Finish();
    }

    public override string ToString() => $"<SharedFunctionInfo {Name()}>";
}

/// <summary>V8's FeedbackCell: the per-closure link to the feedback vector.</summary>
public sealed class FeedbackCell : HeapObject
{
    public FeedbackCell() : base(InstanceType.FeedbackCellType) { }

    /// <summary>The FeedbackVector (the interpreter's type), a ClosureFeedbackCellArray, or undefined.</summary>
    public object? Value;

    /// <summary>The interrupt budget (TieringManager).</summary>
    public int InterruptBudget;

    public static readonly FeedbackCell ManyClosuresCell = new();
}

/// <summary>A (value1, value2) pair; V8's Tuple2, used for non-instance prototypes.</summary>
public sealed class Tuple2(HeapObject? value1, JSValue value2) : HeapObject(InstanceType.FixedArrayType)
{
    public HeapObject? Value1 = value1;
    public JSValue Value2 = value2;
}

/// <summary>V8's JSFunctionOrBoundFunctionOrWrappedFunction: the callable objects with name and length.</summary>
public abstract class JSFunctionOrBoundFunctionOrWrappedFunction(Map map) : JSObject(map)
{
    public const int kLengthDescriptorIndex = 0;
    public const int kNameDescriptorIndex = 1;

    /// <summary>
    /// CopyNameAndLength: sets up "length" and "name" of a bound or wrapped
    /// function from its target (Function.prototype.bind and ShadowRealm wrapping).
    /// </summary>
    public static bool CopyNameAndLength(Isolate isolate, JSFunctionOrBoundFunctionOrWrappedFunction function,
        JSReceiver target, JSString? prefix, int argCount)
    {
        // Setup the "length" property based on the "length" of the {target}.
        // If the targets length is the default JSFunction accessor, we can keep the
        // accessor that's installed by default on the
        // JSBoundFunction/JSWrappedFunction. It lazily computes the value from the
        // underlying internal length.
        AccessorInfo functionLengthAccessor = Accessors.FunctionLengthAccessor;
        var lengthLookup = new LookupIterator(isolate, target, ReadOnlyRoots.length_string, target, LookupIterator.Configuration.OWN);
        if (target is not JSFunction || lengthLookup.State != LookupIterator.StateKind.ACCESSOR ||
            !ReferenceEquals(lengthLookup.GetAccessors(), functionLengthAccessor))
        {
            JSValue length = JSValue.Zero;
            PropertyAttributes? attributes = JSReceiver.GetPropertyAttributes(ref lengthLookup);
            if (attributes is null) return false;
            if (attributes.Value != PropertyAttributes.ABSENT)
            {
                JSValue targetLength = ObjectOps.GetProperty(ref lengthLookup);
                if (targetLength.IsNumber)
                {
                    length = JSValue.FromNumber(Math.Max(0.0, ObjectOps.DoubleToInteger(targetLength.Number) - argCount));
                }
            }
            var it = new LookupIterator(isolate, function, ReadOnlyRoots.length_string, function);
            JSObject.DefineOwnPropertyIgnoreAttributes(ref it, length, it.PropertyAttributes());
        }

        // Setup the "name" property based on the "name" of the {target}.
        // If the target's name is the default JSFunction accessor, we can keep the
        // accessor that's installed by default on the
        // JSBoundFunction/JSWrappedFunction. It lazily computes the value from the
        // underlying internal name.
        AccessorInfo functionNameAccessor = Accessors.FunctionNameAccessor;
        var nameLookup = new LookupIterator(isolate, target, ReadOnlyRoots.name_string, target);
        if (target is not JSFunction || nameLookup.State != LookupIterator.StateKind.ACCESSOR ||
            !ReferenceEquals(nameLookup.GetAccessors(), functionNameAccessor) ||
            (nameLookup.IsFound && !nameLookup.HolderIsReceiver()))
        {
            JSValue targetName = ObjectOps.GetProperty(ref nameLookup);
            JSString name;
            if (targetName.HeapObjectOrNull is JSString targetNameString)
            {
                name = Name.ToFunctionName(isolate, targetNameString);
                if (prefix is not null) name = isolate.Factory.NewConsString(prefix, name);
            }
            else if (prefix is null)
            {
                name = ReadOnlyRoots.empty_string;
            }
            else
            {
                name = prefix;
            }
            var it = new LookupIterator(isolate, function, ReadOnlyRoots.name_string);
            JSObject.DefineOwnPropertyIgnoreAttributes(ref it, name, it.PropertyAttributes());
        }
        return true;
    }
}

/// <summary>V8's JSBoundFunction (Function.prototype.bind).</summary>
public sealed class JSBoundFunction(Map map, JSReceiver boundTargetFunction, JSValue boundThis, FixedArray boundArguments)
    : JSFunctionOrBoundFunctionOrWrappedFunction(map)
{
    public readonly JSReceiver BoundTargetFunction = boundTargetFunction;
    public readonly JSValue BoundThis = boundThis;
    public readonly FixedArray BoundArguments = boundArguments;

    /// <summary>JSBoundFunction::GetName: "bound " prefixes up to the last non-bound target.</summary>
    public static JSString GetName(Isolate isolate, JSBoundFunction function)
    {
        JSString prefix = ReadOnlyRoots.bound__string;
        JSString targetName = prefix;
        Factory factory = isolate.Factory;
        // Concatenate the "bound " up to the last non-bound target.
        while (function.BoundTargetFunction is JSBoundFunction inner)
        {
            targetName = factory.NewConsString(prefix, targetName);
            function = inner;
        }
        if (function.BoundTargetFunction is JSWrappedFunction wrapped)
        {
            return factory.NewConsString(targetName, JSWrappedFunction.GetName(isolate, wrapped));
        }
        if (function.BoundTargetFunction is JSFunction target)
        {
            return factory.NewConsString(targetName, JSFunction.GetName(isolate, target));
        }
        // This will omit the proper target name for bound JSProxies.
        return targetName;
    }

    /// <summary>JSBoundFunction::GetLength.</summary>
    public static uint GetLength(Isolate isolate, JSBoundFunction function)
    {
        uint nofBoundArguments = (uint)function.BoundArguments.Length;
        while (function.BoundTargetFunction is JSBoundFunction inner)
        {
            function = inner;
            // Make sure we never overflow {nof_bound_arguments}, the number of
            // arguments of a function is strictly limited by the max length of an
            // JSAarray, Smi::kMaxValue is thus a reasonably good overestimate.
            uint length = (uint)function.BoundArguments.Length;
            if ((uint)JSValue.SmiMaxValue - nofBoundArguments > length) nofBoundArguments += length;
            else nofBoundArguments = (uint)JSValue.SmiMaxValue;
        }
        uint targetLength;
        if (function.BoundTargetFunction is JSWrappedFunction wrapped)
        {
            targetLength = JSWrappedFunction.GetLength(isolate, wrapped);
        }
        else
        {
            // All non JSFunction targets get a direct property and don't use this
            // accessor.
            targetLength = ((JSFunction)function.BoundTargetFunction).Length;
        }
        return targetLength > nofBoundArguments ? targetLength - nofBoundArguments : 0;
    }

    public static JSString ToString(Isolate isolate, JSBoundFunction function) => ReadOnlyRoots.function_native_code_string;
}

/// <summary>V8's JSWrappedFunction (ShadowRealm).</summary>
public sealed partial class JSWrappedFunction(Map map, JSReceiver wrappedTargetFunction, NativeContext context)
    : JSFunctionOrBoundFunctionOrWrappedFunction(map)
{
    public readonly JSReceiver WrappedTargetFunction = wrappedTargetFunction;
    public readonly NativeContext Context = context;

    public static JSString GetName(Isolate isolate, JSWrappedFunction function)
    {
        isolate.StackGuard.StackCheck(isolate);
        JSReceiver target = function.WrappedTargetFunction;
        if (target is JSBoundFunction bound) return JSBoundFunction.GetName(isolate, bound);
        if (target is JSFunction f) return JSFunction.GetName(isolate, f);
        // This will omit the proper target name for bound JSProxies.
        return ReadOnlyRoots.empty_string;
    }

    public static uint GetLength(Isolate isolate, JSWrappedFunction function)
    {
        isolate.StackGuard.StackCheck(isolate);
        JSReceiver target = function.WrappedTargetFunction;
        if (target is JSBoundFunction bound) return JSBoundFunction.GetLength(isolate, bound);
        // All non JSFunction targets get a direct property and don't use this
        // accessor.
        return ((JSFunction)target).Length;
    }

    public static JSString ToString(Isolate isolate, JSWrappedFunction function) => ReadOnlyRoots.function_native_code_string;
}

/// <summary>V8's JSFunction: a closure (SharedFunctionInfo + Context + FeedbackCell).</summary>
public sealed class JSFunction(Map map, SharedFunctionInfo shared, Context context) : JSFunctionOrBoundFunctionOrWrappedFunction(map)
{
    // Fast binding requires length and name accessors.
    public const int kMinDescriptorsForFastBindAndWrap = 2;

    public SharedFunctionInfo Shared = shared;
    public Context Context = context;
    public FeedbackCell RawFeedbackCell = FeedbackCell.ManyClosuresCell;

    /// <summary>
    /// prototype_or_initial_map: a Map (initial map), a JSReceiver (instance
    /// prototype), a Tuple2 (non-instance prototype mode) or null (V8's the_hole).
    /// </summary>
    public HeapObject? PrototypeOrInitialMap;

    public NativeContext NativeContext => Context.NativeContext;
    public JSGlobalProxy GlobalProxy => Context.GlobalProxy;

    public uint Length => Shared.Length;

    public bool HasPrototypeSlot => Map.InstanceType != InstanceType.JSFunctionWithoutPrototypeType;

    /// <summary>JSFunction::PrototypeOrInitialMapData.</summary>
    public struct PrototypeOrInitialMapData
    {
        public JSReceiver? InstancePrototype;
        public Tuple2? NonInstancePrototypeTuple;
        public JSValue NonInstancePrototype;
        public Map? InitialMap;
        public bool HasInstancePrototype;
        public bool HasNonInstancePrototype;
        public bool HasInitialMap;
    }

    public bool TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData data)
    {
        data = default;
        HeapObject? protoOrMap = PrototypeOrInitialMap;
        if (protoOrMap is null) return false;
        if (protoOrMap is Tuple2 tuple)
        {
            data.NonInstancePrototypeTuple = tuple;
            data.HasNonInstancePrototype = true;
            data.NonInstancePrototype = tuple.Value2;
            protoOrMap = tuple.Value1;
        }
        if (protoOrMap is Map initialMap)
        {
            data.InstancePrototype = initialMap.Prototype;
            data.HasInitialMap = true;
            data.InitialMap = initialMap;
            data.HasInstancePrototype = initialMap.Prototype is not null;
            return true;
        }
        data.HasInstancePrototype = true;
        data.InstancePrototype = (JSReceiver)protoOrMap!;
        return true;
    }

    public bool TryGetInitialMap(out Map initialMap)
    {
        // The common case first: prototype_or_initial_map holds the map itself.
        if (PrototypeOrInitialMap is Map map)
        {
            initialMap = map;
            return true;
        }
        if (TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd) && pomd.HasInitialMap)
        {
            initialMap = pomd.InitialMap!;
            return true;
        }
        initialMap = null!;
        return false;
    }

    public Map InitialMap
    {
        get
        {
            TryGetInitialMap(out Map map);
            return map;
        }
    }

    public bool HasInitialMap => TryGetInitialMap(out _);

    public bool HasInstancePrototype => TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd) && pomd.HasInstancePrototype;

    public bool HasNonInstancePrototype => TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd) && pomd.HasNonInstancePrototype;

    public bool HasPrototype => TryGetPrototypeOrInitialMap(out _);

    public bool HasPrototypeProperty =>
        (HasPrototypeSlot && Map.IsConstructor) || Globals.IsGeneratorFunction(Shared.Kind);

    public bool PrototypeRequiresRuntimeLookup()
    {
        if (!HasPrototypeProperty) return true;
        return PrototypeOrInitialMap is Tuple2;
    }

    public JSReceiver GetIntrinsicDefaultProto()
    {
        FunctionKind kind = Shared.Kind;
        NativeContext nativeContext = Context.NativeContext;
        return Globals.IsGeneratorFunction(kind)
            ? Globals.IsAsyncFunction(kind)
                ? nativeContext.InitialAsyncGeneratorPrototype
                : nativeContext.InitialGeneratorPrototype
            : nativeContext.InitialObjectPrototype;
    }

    public JSReceiver InstancePrototype
    {
        get
        {
            TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd);
            return pomd.InstancePrototype!;
        }
    }

    /// <summary>JSFunction::prototype: the non-instance prototype value or the instance prototype.</summary>
    public JSValue Prototype
    {
        get
        {
            TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd);
            if (pomd.HasNonInstancePrototype) return pomd.NonInstancePrototype;
            return JSValue.FromObject(pomd.InstancePrototype);
        }
    }

    /// <summary>JSFunction::GetFunctionPrototype: allocates .prototype lazily.</summary>
    public static JSValue GetFunctionPrototype(Isolate isolate, JSFunction function)
    {
        if (!function.HasPrototype)
        {
            JSObject proto = isolate.Factory.NewFunctionPrototype(function);
            SetPrototype(isolate, function, proto);
        }
        return function.Prototype;
    }

    // Trigger initial map change dependency and optionally create a new initial
    // map instead of the old one.
    static void SetInstancePrototype(Isolate isolate, JSFunction function, Map oldInitialMap, JSReceiver prototype)
    {
        // Complete any in-object slack tracking that is in progress at this point
        // because it is still tracking the old copy.
        if (oldInitialMap.IsInobjectSlackTrackingInProgress())
        {
            MapUpdater.CompleteInobjectSlackTracking(isolate, oldInitialMap);
        }

        // Make sure the initial map stays for essential JavaScript function objects
        // (the bootstrapper active case) and for some less common cases like builtin
        // function subclassing, while keeping lazy initial maps mode for regular
        // functions.
        if (isolate.BootstrapperActive || oldInitialMap.InstanceType != InstanceType.JSObjectType)
        {
            Map newMap = Map.Copy(isolate, oldInitialMap, "SetInstancePrototype");
            SetInitialMap(isolate, function, newMap, prototype);
        }

        // Deoptimize all code that embeds the previous initial map.
        DependentCode.DeoptimizeDependencyGroups(isolate, oldInitialMap, DependentCode.DependencyGroups.InitialMapChanged);
    }

    /// <summary>JSFunction::SetPrototype (the "prototype" property of constructors).</summary>
    public static void SetPrototype(Isolate isolate, JSFunction function, JSValue value)
    {
        Map? oldInitialMap = null;
        JSReceiver newInstancePrototype;

        // If the value is not a JSReceiver, switch to a Tuple2 mode where the
        // first field stores an initial map (if it exists) or an instance
        // prototype (the prototype used for constructing objects) and the second
        // field stores the non-instance prototype value. See
        // https://tc39.es/ecma262/#sec-ordinarycreatefromconstructor and
        // https://tc39.es/ecma262/#sec-getprototypefromconstructor.
        if (!value.IsJSReceiver)
        {
            if (function.TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd))
            {
                if (pomd.HasNonInstancePrototype)
                {
                    // The function is already in a non-instance prototype mode, changing
                    // the prototype value does not affect the state of initial map. Since
                    // we don't track constness of the non-instance prototype value, we
                    // just need to update the tuple.
                    pomd.NonInstancePrototypeTuple!.Value2 = value;
                    return;
                }
                if (pomd.HasInitialMap) oldInitialMap = pomd.InitialMap;
            }
            // The function is currently in instance prototype mode or prototype is not
            // initialized yet, switch it to non-instance prototype mode by allocating
            // a Tuple2.
            newInstancePrototype = function.GetIntrinsicDefaultProto();

            HeapObject newPrototypeOrInitialMap;
            if (oldInitialMap is not null && ReferenceEquals(oldInitialMap.Prototype, newInstancePrototype))
            {
                // We are setting the same prototype value, so initial map does not
                // change.
                newPrototypeOrInitialMap = oldInitialMap;
                // Bypass invalidation of the old initial map.
                oldInitialMap = null;
            }
            else
            {
                newPrototypeOrInitialMap = newInstancePrototype;
            }

            // Switch the function to non-instance prototype mode.
            function.PrototypeOrInitialMap = new Tuple2(newPrototypeOrInitialMap, value);
        }
        else
        {
            // New prototype value is a JSReceiver, i.e. instance prototype.
            newInstancePrototype = value.As<JSReceiver>();

            if (function.TryGetInitialMap(out Map rawInitialMap))
            {
                // Function has initial map.
                if (ReferenceEquals(rawInitialMap.Prototype, newInstancePrototype))
                {
                    // We are setting the same prototype value, so initial map does not
                    // change. Just switch the function to instance prototype mode.
                    function.PrototypeOrInitialMap = rawInitialMap;
                    return;
                }
                // Old initial map can't be reused. Fall through to switch the function
                // to instance prototype mode.
                oldInitialMap = rawInitialMap;
            }

            // Switch function to instance prototype mode and let the initial map
            // be created lazily when needed.
            function.PrototypeOrInitialMap = newInstancePrototype;
            if (JSObject.IsJSObjectThatCanBeTrackedAsPrototype(newInstancePrototype))
            {
                // Optimize as prototype to detach it from its transition tree.
                JSObject.OptimizeAsPrototype(isolate, (JSObject)newInstancePrototype);
            }
        }

        if (oldInitialMap is not null)
        {
            // The function used to have initial map, notify the old initial map
            // users about the function's prototype change.
            SetInstancePrototype(isolate, function, oldInitialMap, newInstancePrototype);
        }
    }

    public static void SetInitialMap(Isolate isolate, JSFunction function, Map initialMap, JSReceiver? prototype) =>
        SetInitialMap(isolate, function, initialMap, prototype, function);

    public static void SetInitialMap(Isolate isolate, JSFunction function, Map initialMap, JSReceiver? prototype, JSFunction constructor)
    {
        if (!ReferenceEquals(initialMap.Prototype, prototype))
        {
            Map.SetPrototype(isolate, initialMap, prototype);
        }
        initialMap.SetConstructor(constructor);
        initialMap.NativeContext ??= constructor.Context.NativeContext;

        // Set initial map while keeping the instance/non-instance prototype mode
        // intact.
        if (function.TryGetPrototypeOrInitialMap(out PrototypeOrInitialMapData pomd) && pomd.HasNonInstancePrototype)
        {
            pomd.NonInstancePrototypeTuple!.Value1 = initialMap;
        }
        else
        {
            function.PrototypeOrInitialMap = initialMap;
        }
    }

    /// <summary>JSFunction::EnsureHasInitialMap.</summary>
    public static void EnsureHasInitialMap(Isolate isolate, JSFunction function)
    {
        if (function.HasInitialMap) return;

        int expectedNofProperties = CalculateExpectedNofProperties(isolate, function);

        // {CalculateExpectedNofProperties} can have had the side effect of creating
        // the initial map (e.g. it could have triggered an optimized compilation
        // whose dependency installation reentered {EnsureHasInitialMap}).
        if (function.HasInitialMap) return;

        // Create a new map with the size and number of in-object properties suggested
        // by the function.
        InstanceType instanceType;
        if (Globals.IsResumableFunction(function.Shared.Kind))
        {
            instanceType = Globals.IsAsyncGeneratorFunction(function.Shared.Kind)
                ? InstanceType.JSAsyncGeneratorObjectType
                : InstanceType.JSGeneratorObjectType;
        }
        else
        {
            instanceType = InstanceType.JSObjectType;
        }

        CalculateInstanceSizeHelper(instanceType, 0, expectedNofProperties, out int instanceSize, out int inobjectProperties);

        NativeContext creationContext = function.NativeContext;
        Map map = isolate.Factory.NewContextfulMap(creationContext, instanceType, instanceSize,
            ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, inobjectProperties);

        // Fetch or allocate prototype.
        JSReceiver prototype;
        if (function.HasInstancePrototype)
        {
            prototype = function.InstancePrototype;
            map.Prototype = prototype;
        }
        else
        {
            prototype = isolate.Factory.NewFunctionPrototype(function);
            Map.SetPrototype(isolate, map, prototype);
        }

        // Finally link initial map and constructor function.
        SetInitialMap(isolate, function, map, prototype);
        map.StartInobjectSlackTracking();
    }

    static bool FastInitializeDerivedMap(Isolate isolate, JSFunction newTarget, JSFunction constructor, Map constructorInitialMap)
    {
        // Use the default intrinsic prototype instead.
        if (!newTarget.HasPrototypeSlot) return false;
        // Check that |function|'s initial map still in sync with the |constructor|,
        // otherwise we must create a new initial map for |function|.
        if (newTarget.HasInitialMap && ReferenceEquals(newTarget.InitialMap.GetConstructor(), constructor))
        {
            return true;
        }
        InstanceType instanceType = constructorInitialMap.InstanceType;
        // Link initial map and constructor function if the new.target is actually a
        // subclass constructor.
        if (!Globals.IsDerivedConstructor(newTarget.Shared.Kind)) return false;

        // Constructor expects certain number of in-object properties to be in the
        // object. However, CalculateExpectedNofProperties() may return smaller value
        // if 1) the constructor is not in the prototype chain of new_target, or
        // 2) the prototype chain is modified during iteration, or 3) compilation
        // failure occur during prototype chain iteration.
        // So we take the maximum of two values.
        int expectedNofProperties = Math.Max(constructor.Shared.ExpectedNofProperties,
            CalculateExpectedNofProperties(isolate, newTarget));
        CalculateInstanceSizeHelper(instanceType, 0, expectedNofProperties, out int instanceSize, out int inObjectProperties);

        int preAllocated = constructorInitialMap.GetInObjectProperties() - constructorInitialMap.UnusedPropertyFields();
        int unusedPropertyFields = inObjectProperties - preAllocated;
        Map map = Map.CopyInitialMap(isolate, constructorInitialMap, instanceSize, inObjectProperties, unusedPropertyFields);
        map.NewTargetIsBase = false;
        JSReceiver prototype = newTarget.InstancePrototype;
        SetInitialMap(isolate, newTarget, map, prototype, constructor);
        map.ConstructionCounter = Map.kNoSlackTracking;
        map.StartInobjectSlackTracking();
        return true;
    }

    /// <summary>JSFunction::GetDerivedMap: the map for `new constructor` with this new.target.</summary>
    public static Map GetDerivedMap(Isolate isolate, JSFunction constructor, JSReceiver newTarget)
    {
        EnsureHasInitialMap(isolate, constructor);

        Map constructorInitialMap = constructor.InitialMap;
        if (ReferenceEquals(newTarget, constructor)) return constructorInitialMap;

        // Fast case, new.target is a subclass of constructor. The map is cacheable
        // (and may already have been cached). new.target.prototype is guaranteed to
        // be a JSReceiver.
        if (newTarget is JSFunction function && FastInitializeDerivedMap(isolate, function, constructor, constructorInitialMap))
        {
            return function.InitialMap;
        }

        // Slow path, new.target is either a proxy object or can't cache the map.
        // new.target.prototype is not guaranteed to be a JSReceiver, and may need to
        // fall back to the intrinsicDefaultProto.
        JSValue prototype;
        if (newTarget is JSFunction f && f.HasPrototypeSlot)
        {
            // Make sure the new.target.prototype is cached.
            EnsureHasInitialMap(isolate, f);
            prototype = f.Prototype;
        }
        else
        {
            // The new.target is a constructor but it's not a JSFunction with
            // a prototype slot, so get the prototype property.
            prototype = JSReceiver.GetProperty(isolate, newTarget, ReadOnlyRoots.prototype_string);
            // The above prototype lookup might change the constructor and its
            // prototype, hence we have to reload the initial map.
            EnsureHasInitialMap(isolate, constructor);
            constructorInitialMap = constructor.InitialMap;
        }

        // If prototype is not a JSReceiver, fetch the intrinsicDefaultProto from the
        // correct realm. Rather than directly fetching the .prototype, we fetch the
        // constructor that points to the .prototype. This relies on
        // constructor.prototype being FROZEN for those constructors.
        if (!prototype.IsJSReceiver)
        {
            NativeContext nativeContext = JSReceiver.GetFunctionRealm(isolate, newTarget);
            JSValue maybeIndex = JSReceiver.GetDataProperty(isolate, constructor, ReadOnlyRoots.native_context_index_symbol);
            int index = maybeIndex.IsSmi ? (int)maybeIndex.Number : (int)Context.Field.OBJECT_FUNCTION_INDEX;
            JSValue maybeRealmConstructor = nativeContext.Slots[index];
            if (maybeRealmConstructor.IsUndefined)
            {
                // The constructor might belong to a lazily initialized part of the
                // context. Try to initialize it.
                Genesis.InitializeLazyPartOfContext(isolate, nativeContext, (Context.Field)index);
                maybeRealmConstructor = nativeContext.Slots[index];
            }
            if (maybeRealmConstructor.HeapObjectOrNull is not JSFunction realmConstructor)
            {
                throw new InvalidOperationException($"Context does not have a constructor at index {index}");
            }
            prototype = realmConstructor.Prototype;
        }
        return Map.GetDerivedMap(isolate, constructorInitialMap, prototype.As<JSReceiver>());
    }

    /// <summary>JSFunction::GetName: "anonymous" or the shared name.</summary>
    public static JSString GetName(Isolate isolate, JSFunction function)
    {
        if (function.Shared.NameShouldPrintAsAnonymous) return ReadOnlyRoots.anonymous_string;
        return function.Shared.Name();
    }

    /// <summary>JSFunction::GetDebugName.</summary>
    public static JSString GetDebugName(Isolate isolate, JSFunction function, bool allowAllocation = true)
    {
        // Below we use the same fast-path that we already established for
        // Function.prototype.bind(), where we avoid a slow "name" property
        // lookup if the DescriptorArray for the |function| still has the
        // "name" property at the original spot and that property is still
        // implemented via an AccessorInfo (which effectively means that
        // it must be the FunctionNameGetter).
        if (!Accessors.UseFastFunctionNameLookup(isolate, function.Map))
        {
            JSValue name = JSReceiver.GetDataProperty(isolate, function, ReadOnlyRoots.name_string, allowAllocation);
            if (name.HeapObjectOrNull is JSString s) return s;
        }
        return SharedFunctionInfo.DebugName(isolate, function.Shared);
    }

    /// <summary>JSFunction::SetName: defines "name" as (prefix + " ") + ToFunctionName(name).</summary>
    public static bool SetName(Isolate isolate, JSFunction function, Name name, JSString prefix)
    {
        JSString functionName = Objects.Name.ToFunctionName(isolate, name);
        if (prefix.Length > 0)
        {
            var builder = new IncrementalStringBuilder(isolate);
            builder.AppendString(prefix);
            builder.AppendCharacter(' ');
            builder.AppendString(functionName);
            functionName = builder.Finish();
        }
        JSObject.DefinePropertyOrElementIgnoreAttributes(isolate, function, ReadOnlyRoots.name_string, functionName,
            PropertyAttributes.DONT_ENUM | PropertyAttributes.READ_ONLY);
        return true;
    }

    static JSString NativeCodeFunctionSourceString(Isolate isolate, SharedFunctionInfo sharedInfo)
    {
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendCStringLiteral("function ");
        builder.AppendString(sharedInfo.Name());
        builder.AppendCStringLiteral("() { [native code] }");
        return builder.Finish();
    }

    /// <summary>JSFunction::ToString (Function.prototype.toString).</summary>
    public static JSString ToString(Isolate isolate, JSFunction function)
    {
        SharedFunctionInfo sharedInfo = function.Shared;

        // Check if {function} should hide its source code.
        if (!sharedInfo.IsUserJavaScript()) return NativeCodeFunctionSourceString(isolate, sharedInfo);

        if (Globals.IsClassConstructor(sharedInfo.Kind))
        {
            // Check if we should print {function} as a class.
            JSValue maybeClassPositions = JSReceiver.GetDataProperty(isolate, function, ReadOnlyRoots.class_positions_symbol);
            if (maybeClassPositions.HeapObjectOrNull is ClassPositions classPositions)
            {
                var scriptSource = sharedInfo.Script!.Source.As<JSString>();
                return isolate.Factory.NewSubString(scriptSource, classPositions.Start, classPositions.End);
            }
        }

        // Check if we have source code for the {function}.
        if (!sharedInfo.HasSourceCode()) return NativeCodeFunctionSourceString(isolate, sharedInfo);

        if (sharedInfo.FunctionTokenPosition() == Globals.kNoSourcePosition)
        {
            // If the function token position isn't valid, return [native code] to
            // ensure calling eval on the returned source code throws rather than
            // giving inconsistent call behaviour.
            return NativeCodeFunctionSourceString(isolate, sharedInfo);
        }
        return SharedFunctionInfo.GetSourceCodeHarmony(isolate, sharedInfo).As<JSString>();
    }

    /// <summary>JSFunction::CalculateExpectedNofProperties.</summary>
    public static int CalculateExpectedNofProperties(Isolate isolate, JSFunction function)
    {
        int expectedNofProperties = 0;
        for (var iter = new PrototypeIterator(isolate, function, WhereToStart.StartAtReceiver); !iter.IsAtEnd; iter.Advance())
        {
            JSReceiver? current = iter.GetCurrent();
            if (current is not JSFunction func) break;
            // The super constructor should be compiled for the number of expected
            // properties to be available.
            SharedFunctionInfo shared = func.Shared;
            if (shared.IsCompiled || isolate.CompileLazy(func))
            {
                int count = shared.ExpectedNofProperties;
                // Check that the estimate is sensible.
                if (expectedNofProperties <= JSObject.kMaxInObjectProperties - count)
                {
                    expectedNofProperties += count;
                }
                else
                {
                    return JSObject.kMaxInObjectProperties;
                }
            }
        }
        // Inobject slack tracking will reclaim redundant inobject space
        // later, so we can afford to adjust the estimate generously,
        // meaning we over-allocate by at least 8 slots in the beginning.
        if (expectedNofProperties > 0)
        {
            expectedNofProperties += 8;
            if (expectedNofProperties > JSObject.kMaxInObjectProperties)
            {
                expectedNofProperties = JSObject.kMaxInObjectProperties;
            }
        }
        return expectedNofProperties;
    }

    /// <summary>JSFunction::CalculateInstanceSizeHelper.</summary>
    public static void CalculateInstanceSizeHelper(InstanceType instanceType, int requestedEmbedderFields,
        int requestedInObjectProperties, out int instanceSize, out int inObjectProperties)
    {
        int headerSize = JSObject.GetHeaderSize(instanceType);
        int maxNofFields = (JSObject.kMaxInstanceSize - headerSize) / Map.kTaggedSize;
        inObjectProperties = Math.Min(requestedInObjectProperties, maxNofFields - requestedEmbedderFields);
        instanceSize = headerSize + ((requestedEmbedderFields + inObjectProperties) * Map.kTaggedSize);
    }

    public int ComputeInstanceSizeWithMinSlack(Isolate isolate)
    {
        Map initialMap = InitialMap;
        if (initialMap.IsInobjectSlackTrackingInProgress())
        {
            int slack = initialMap.ComputeMinObjectSlack(isolate);
            return initialMap.InstanceSizeFromSlack(slack);
        }
        return initialMap.InstanceSize;
    }

    public override string ToString() => $"<JSFunction {Shared.Name()}>";
}
