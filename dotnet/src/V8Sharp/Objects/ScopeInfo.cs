// Port of src/objects/scope-info.{h,cc,tq} (the data shape and its accessors).
// ScopeInfo::Create(Scope) belongs with the bytecode generator (it reads the
// parser's Scope); this file is the heap-side object it fills.
//
// V8 packs the variable part into one tagged array; here each part is a field.
// Context local names are kept in an array; V8 switches to a
// NameToIndexHashTable at kScopeInfoMaxInlinedLocalNamesSize locals, and so do
// we (a Dictionary keyed by the internalized name).
//
// TODO(merge): implement V8Sharp.Ast.IScopeInfo (from V8Sharp.Parsing) on this
// class; every member it needs exists here under its V8 name.
using V8Sharp.Common;

namespace V8Sharp.Objects;

/// <summary>V8's ScopeInfo: the static description of a scope's context layout.</summary>
public sealed partial class ScopeInfo : HeapObject
{
    public const int kScopeInfoMaxInlinedLocalNamesSize = 75;
    public const int kFunctionNameEntries = 2;

    // ScopeFlags bit layout (scope-info.tq).
    const int ScopeTypeShift = 0, ScopeTypeBitsCount = 4;
    const int SloppyEvalCanExtendVarsShift = 4;
    const int LanguageModeShift = 5;
    const int DeclarationScopeShift = 6;
    const int ReceiverVariableShift = 7;          // 2 bits
    const int ClassScopeHasPrivateBrandShift = 9;
    const int HasSavedClassVariableShift = 10;
    const int AllocatesArgumentsShift = 11;
    const int FunctionVariableShift = 12;         // 2 bits
    const int HasInferredFunctionNameShift = 14;
    const int HasSimpleParametersShift = 15;
    const int FunctionKindShift = 16;             // 5 bits
    const int HasOuterScopeInfoShift = 21;
    const int IsDebugEvaluateScopeShift = 22;
    const int ForceContextAllocationShift = 23;
    const int PrivateNameLookupSkipsOuterClassShift = 24;
    const int HasContextExtensionSlotShift = 25;
    const int SomeContextHasExtensionShift = 26;
    const int IsHiddenShift = 27;
    const int IsWrappedFunctionShift = 28;
    const int HasContextCellsShift = 29;
    const int IsHoistedInContextShift = 30;

    /// <summary>The packed ScopeFlags.</summary>
    public uint Flags;
    public int ParameterCount;
    public int StartPositionValue;
    public int EndPositionValue;

    /// <summary>Names of context-allocated locals, in slot order (internalized).</summary>
    public JSString[] ContextLocalNames = [];
    /// <summary>Packed VariableProperties per context local.</summary>
    public int[] ContextLocalInfos = [];
    Dictionary<JSString, int>? _contextLocalNamesHashtable;

    /// <summary>saved_class_variable_info: the context slot index (inlined names) or the name.</summary>
    public JSValue SavedClassVariableInfo;
    /// <summary>function_variable_info.name: a String or SharedFunctionInfo::kNoSharedNameSentinel (0).</summary>
    public JSValue FunctionVariableName;
    public int FunctionVariableContextOrStackSlotIndex;
    public JSValue InferredFunctionNameValue;
    public ScopeInfo? OuterScopeInfoValue;
    /// <summary>module_info (SourceTextModuleInfo), for module scopes.</summary>
    public SourceTextModuleInfo? ModuleInfo;

    /// <summary>ScopeInfo::ModuleDescriptorInfo.</summary>
    public SourceTextModuleInfo ModuleDescriptorInfo() => ModuleInfo!;
    public ModuleVariableEntry[] ModuleVariables = [];
    public ulong UnusedParameterBits;

    /// <summary>One module_variables entry.</summary>
    public readonly record struct ModuleVariableEntry(JSString Name, int Index, int Properties);

    public ScopeInfo() : base(InstanceType.ScopeInfoType) { }

    /// <summary>ReadOnlyRoots::empty_scope_info(). IsEmpty() compares against it.</summary>
    public static readonly ScopeInfo EmptyScopeInfo = new();

    public bool IsEmpty() => ReferenceEquals(this, EmptyScopeInfo);

    // --- flag accessors --------------------------------------------------

    static uint Bits(uint flags, int shift, int count) => (flags >> shift) & ((1u << count) - 1);
    bool Bit(int shift) => ((Flags >> shift) & 1) != 0;
    void SetBit(int shift, bool value) => Flags = value ? Flags | (1u << shift) : Flags & ~(1u << shift);
    void SetBits(int shift, int count, uint value)
    {
        uint mask = ((1u << count) - 1) << shift;
        Flags = (Flags & ~mask) | ((value << shift) & mask);
    }

    public ScopeType ScopeType { get => (ScopeType)Bits(Flags, ScopeTypeShift, ScopeTypeBitsCount); set => SetBits(ScopeTypeShift, 4, (uint)value); }
    public bool SloppyEvalCanExtendVars { get => Bit(SloppyEvalCanExtendVarsShift); set => SetBit(SloppyEvalCanExtendVarsShift, value); }
    public LanguageMode LanguageMode { get => (LanguageMode)Bits(Flags, LanguageModeShift, 1); set => SetBits(LanguageModeShift, 1, (uint)value); }
    public bool IsDeclarationScope { get => Bit(DeclarationScopeShift); set => SetBit(DeclarationScopeShift, value); }
    public VariableAllocationInfo ReceiverVariable { get => (VariableAllocationInfo)Bits(Flags, ReceiverVariableShift, 2); set => SetBits(ReceiverVariableShift, 2, (uint)value); }
    public bool ClassScopeHasPrivateBrand { get => Bit(ClassScopeHasPrivateBrandShift); set => SetBit(ClassScopeHasPrivateBrandShift, value); }
    public bool HasSavedClassVariable { get => Bit(HasSavedClassVariableShift); set => SetBit(HasSavedClassVariableShift, value); }
    public bool AllocatesArguments { get => Bit(AllocatesArgumentsShift); set => SetBit(AllocatesArgumentsShift, value); }
    public VariableAllocationInfo FunctionVariable { get => (VariableAllocationInfo)Bits(Flags, FunctionVariableShift, 2); set => SetBits(FunctionVariableShift, 2, (uint)value); }
    public bool HasInferredFunctionName { get => Bit(HasInferredFunctionNameShift); set => SetBit(HasInferredFunctionNameShift, value); }
    public bool HasSimpleParameters { get => Bit(HasSimpleParametersShift); set => SetBit(HasSimpleParametersShift, value); }
    public FunctionKind FunctionKind { get => (FunctionKind)Bits(Flags, FunctionKindShift, 5); set => SetBits(FunctionKindShift, 5, (uint)value); }
    public bool HasOuterScopeInfo { get => Bit(HasOuterScopeInfoShift); set => SetBit(HasOuterScopeInfoShift, value); }
    public bool IsDebugEvaluateScope { get => Bit(IsDebugEvaluateScopeShift); set => SetBit(IsDebugEvaluateScopeShift, value); }
    public bool ForceContextAllocation { get => Bit(ForceContextAllocationShift); set => SetBit(ForceContextAllocationShift, value); }
    public bool PrivateNameLookupSkipsOuterClass { get => Bit(PrivateNameLookupSkipsOuterClassShift); set => SetBit(PrivateNameLookupSkipsOuterClassShift, value); }
    public bool HasContextExtensionSlot { get => Bit(HasContextExtensionSlotShift); set => SetBit(HasContextExtensionSlotShift, value); }
    public bool SomeContextHasExtension { get => Bit(SomeContextHasExtensionShift); set => SetBit(SomeContextHasExtensionShift, value); }
    public bool IsHidden { get => Bit(IsHiddenShift); set => SetBit(IsHiddenShift, value); }
    public bool IsWrappedFunctionScope { get => Bit(IsWrappedFunctionShift); set => SetBit(IsWrappedFunctionShift, value); }
    public bool HasContextCells { get => Bit(HasContextCellsShift); set => SetBit(HasContextCellsShift, value); }
    public bool IsHoistedInContext { get => Bit(IsHoistedInContextShift); set => SetBit(IsHoistedInContextShift, value); }

    public void MarkSomeContextHasExtension() => SomeContextHasExtension = true;

    public bool IsScriptScope => ScopeType is ScopeType.SCRIPT_SCOPE or ScopeType.REPL_MODE_SCOPE;
    public bool IsReplModeScope => ScopeType == ScopeType.REPL_MODE_SCOPE;

    // --- VariableProperties -----------------------------------------------

    /// <summary>ScopeInfo::VariableProperties bit layout.</summary>
    public static class VariablePropertiesBits
    {
        public const int ParameterNumberOrPositionMax = 0xFFFF;

        public static int Encode(VariableMode mode, InitializationFlag init, MaybeAssignedFlag maybeAssigned,
            bool isParameter, int parameterNumberOrPosition, IsStaticFlag isStatic) =>
            (int)mode | ((int)init << 4) | ((int)maybeAssigned << 5) | ((isParameter ? 1 : 0) << 6) |
            ((parameterNumberOrPosition & 0xFFFF) << 7) | ((int)isStatic << 23);

        public static VariableMode Mode(int v) => (VariableMode)(v & 0xF);
        public static InitializationFlag InitFlag(int v) => (InitializationFlag)((v >> 4) & 1);
        public static MaybeAssignedFlag MaybeAssigned(int v) => (MaybeAssignedFlag)((v >> 5) & 1);
        public static bool IsParameter(int v) => ((v >> 6) & 1) != 0;
        public static int ParameterNumberOrPosition(int v) => (v >> 7) & 0xFFFF;
        public static IsStaticFlag IsStatic(int v) => (IsStaticFlag)((v >> 23) & 1);
    }

    /// <summary>Sets the context locals (names internalized, infos packed VariableProperties).</summary>
    public void SetContextLocals(JSString[] names, int[] infos)
    {
        ContextLocalNames = names;
        ContextLocalInfos = infos;
        _contextLocalNamesHashtable = null;
        if (names.Length >= kScopeInfoMaxInlinedLocalNamesSize)
        {
            _contextLocalNamesHashtable = new Dictionary<JSString, int>(names.Length, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < names.Length; i++) _contextLocalNamesHashtable[names[i]] = i;
        }
    }

    public int ContextLocalCount => ContextLocalNames.Length;
    public bool HasInlinedLocalNames => ContextLocalCount < kScopeInfoMaxInlinedLocalNamesSize;

    public JSString ContextInlinedLocalName(int var) => ContextLocalNames[var];
    public JSString ContextLocalName(int var) => ContextLocalNames[var];
    public VariableMode ContextLocalMode(int var) => VariablePropertiesBits.Mode(ContextLocalInfos[var]);
    public IsStaticFlag ContextLocalIsStaticFlag(int var) => VariablePropertiesBits.IsStatic(ContextLocalInfos[var]);
    public InitializationFlag ContextLocalInitFlag(int var) => VariablePropertiesBits.InitFlag(ContextLocalInfos[var]);
    public bool ContextLocalIsParameter(int var) => VariablePropertiesBits.IsParameter(ContextLocalInfos[var]);
    public uint ContextLocalParameterNumber(int var) => (uint)VariablePropertiesBits.ParameterNumberOrPosition(ContextLocalInfos[var]);
    public MaybeAssignedFlag ContextLocalMaybeAssignedFlag(int var) => VariablePropertiesBits.MaybeAssigned(ContextLocalInfos[var]);

    public int ContextLocalInitializerPosition(int var)
    {
        int position = VariablePropertiesBits.ParameterNumberOrPosition(ContextLocalInfos[var]);
        if (position == VariablePropertiesBits.ParameterNumberOrPositionMax) return int.MaxValue;
        return StartPosition() + position;
    }

    // --- derived queries (scope-info.cc) ----------------------------------

    public int ContextLength()
    {
        if (IsEmpty()) return 0;
        int contextLocals = ContextLocalCount;
        bool functionNameContextSlot = HasContextAllocatedFunctionName();
        bool forceContext = ForceContextAllocation;
        ScopeType type = ScopeType;
        bool hasContext =
            contextLocals > 0 || forceContext || functionNameContextSlot ||
            type == ScopeType.WITH_SCOPE || type == ScopeType.CLASS_SCOPE ||
            (type == ScopeType.BLOCK_SCOPE && SloppyEvalCanExtendVars && IsDeclarationScope) ||
            (type == ScopeType.FUNCTION_SCOPE && SloppyEvalCanExtendVars) ||
            type == ScopeType.MODULE_SCOPE;
        if (!hasContext) return 0;
        return ContextHeaderLength() + contextLocals + (functionNameContextSlot ? 1 : 0);
    }

    public bool HasContext() => ContextLength() > 0;

    public int ContextHeaderLength() =>
        HasContextExtensionSlot ? (int)Context.Field.MIN_CONTEXT_EXTENDED_SLOTS : (int)Context.Field.MIN_CONTEXT_SLOTS;

    /// <summary>Needs to be kept in sync with Scope::UniqueIdInScript and SharedFunctionInfo::UniqueIdInScript.</summary>
    public int UniqueIdInScript()
    {
        if (IsScriptScope || ScopeType == ScopeType.EVAL_SCOPE || ScopeType == ScopeType.MODULE_SCOPE) return -2;
        if (IsWrappedFunctionScope) return -1;
        return StartPosition() + (Globals.IsDefaultConstructor(FunctionKind) ? 1 : 0);
    }

    public bool HasReceiver() => ReceiverVariable != VariableAllocationInfo.NONE;

    public bool HasAllocatedReceiver()
    {
        VariableAllocationInfo allocation = ReceiverVariable;
        return allocation == VariableAllocationInfo.STACK || allocation == VariableAllocationInfo.CONTEXT || IsDebugEvaluateScope;
    }

    public bool IsSloppyNormalJSFunction() => FunctionKind == FunctionKind.NormalFunction && LanguageMode == LanguageMode.Sloppy;

    public bool CanOnlyAccessFixedFormalParameters()
    {
        FunctionKind kind = FunctionKind;
        return !IsSloppyNormalJSFunction() &&
               (kind == FunctionKind.NormalFunction || kind == FunctionKind.ArrowFunction) &&
               !AllocatesArguments && HasSimpleParameters;
    }

    public bool HasFunctionName() => FunctionVariable != VariableAllocationInfo.NONE;
    public bool HasContextAllocatedFunctionName() => FunctionVariable == VariableAllocationInfo.CONTEXT;

    /// <summary>SharedFunctionInfo::kNoSharedNameSentinel is Smi 0.</summary>
    public bool HasSharedFunctionName() => !FunctionName().IsIdenticalTo(JSValue.Zero);

    public JSValue FunctionName() => FunctionVariableName;

    public void SetFunctionName(JSValue name) => FunctionVariableName = name;

    public JSValue InferredFunctionName() => InferredFunctionNameValue;

    public void SetInferredFunctionName(JSString name) => InferredFunctionNameValue = name;

    public JSString FunctionDebugName()
    {
        if (!HasFunctionName()) return Roots.ReadOnlyRoots.empty_string;
        JSValue name = FunctionName();
        if (name.HeapObjectOrNull is JSString s && s.Length > 0) return s;
        if (HasInferredFunctionName && InferredFunctionName().HeapObjectOrNull is JSString inferred) return inferred;
        return Roots.ReadOnlyRoots.empty_string;
    }

    public int StartPosition() => StartPositionValue;
    public int EndPosition() => EndPositionValue;

    public void SetPositionInfo(int start, int end)
    {
        StartPositionValue = start;
        EndPositionValue = end;
    }

    public ScopeInfo OuterScopeInfo() => OuterScopeInfoValue!;

    /// <summary>ScopeInfo::VariableIsSynthetic: compiler temporaries start with '.' (or '#'), or are 'this'.</summary>
    public static bool VariableIsSynthetic(JSString name) =>
        name.Length == 0 || name.Get(0) == '.' || name.Get(0) == '#' ||
        JSString.Equals(name, Roots.ReadOnlyRoots.this_string);

    public int ModuleVariableCount() => ModuleVariables.Length;

    public int ModuleIndex(JSString name, out VariableMode mode, out InitializationFlag initFlag,
        out MaybeAssignedFlag maybeAssignedFlag, out int initializerPosition)
    {
        for (int i = 0; i < ModuleVariables.Length; ++i)
        {
            if (JSString.Equals(name, ModuleVariables[i].Name))
            {
                ModuleVariable(i, out _, out int index, out mode, out initFlag, out maybeAssignedFlag, out initializerPosition);
                return index;
            }
        }
        mode = default;
        initFlag = default;
        maybeAssignedFlag = default;
        initializerPosition = 0;
        return 0;
    }

    public void ModuleVariable(int i, out JSString name, out int index, out VariableMode mode,
        out InitializationFlag initFlag, out MaybeAssignedFlag maybeAssignedFlag, out int initializerPosition)
    {
        ModuleVariableEntry e = ModuleVariables[i];
        int properties = e.Properties;
        name = e.Name;
        index = e.Index;
        mode = VariablePropertiesBits.Mode(properties);
        initFlag = VariablePropertiesBits.InitFlag(properties);
        maybeAssignedFlag = VariablePropertiesBits.MaybeAssigned(properties);
        int position = VariablePropertiesBits.ParameterNumberOrPosition(properties);
        initializerPosition = position == VariablePropertiesBits.ParameterNumberOrPositionMax
            ? int.MaxValue
            : StartPosition() + position;
    }

    int InlinedLocalNamesLookup(JSString name)
    {
        JSString[] names = ContextLocalNames;
        for (int i = 0; i < names.Length; ++i)
        {
            if (ReferenceEquals(name, names[i])) return i;
        }
        return -1;
    }

    /// <summary>Result of a context slot lookup (V8's VariableLookupResult).</summary>
    public struct VariableLookupResult
    {
        public int ContextIndex;
        public int SlotIndex;
        public bool IsReplMode;
        public IsStaticFlag IsStaticFlag;
        public VariableMode Mode;
        public InitializationFlag InitFlag;
        public MaybeAssignedFlag MaybeAssignedFlag;
        public int InitializerPosition;
    }

    /// <summary>ScopeInfo::ContextSlotIndex: the context slot of <paramref name="name"/> (internalized), or -1.</summary>
    public int ContextSlotIndex(JSString name, out VariableLookupResult lookupResult)
    {
        lookupResult = default;
        int index;
        if (HasInlinedLocalNames) index = InlinedLocalNamesLookup(name);
        else index = _contextLocalNamesHashtable!.TryGetValue(name, out int found) ? found : -1;

        if (index != -1)
        {
            lookupResult.Mode = ContextLocalMode(index);
            lookupResult.IsStaticFlag = ContextLocalIsStaticFlag(index);
            lookupResult.InitFlag = ContextLocalInitFlag(index);
            lookupResult.MaybeAssignedFlag = ContextLocalMaybeAssignedFlag(index);
            lookupResult.IsReplMode = IsReplModeScope;
            lookupResult.InitializerPosition = ContextLocalIsParameter(index)
                ? Globals.kNoSourcePosition
                : ContextLocalInitializerPosition(index);
            return ContextHeaderLength() + index;
        }
        return -1;
    }

    public int ContextSlotIndex(JSString name) => ContextSlotIndex(name, out _);

    public (JSString Name, int Index) SavedClassVariable()
    {
        JSValue info = SavedClassVariableInfo;
        if (HasInlinedLocalNames)
        {
            int index = (int)info.Number - (int)Context.Field.MIN_CONTEXT_SLOTS;
            return (ContextInlinedLocalName(index), index);
        }
        var name = info.As<JSString>();
        return (name, _contextLocalNamesHashtable![name]);
    }

    public int ReceiverContextSlotIndex() =>
        ReceiverVariable == VariableAllocationInfo.CONTEXT ? ContextHeaderLength() : -1;

    public int ParametersStartIndex() =>
        ReceiverVariable == VariableAllocationInfo.CONTEXT ? ContextHeaderLength() + 1 : ContextHeaderLength();

    public int FunctionContextSlotIndex() =>
        HasContextAllocatedFunctionName() ? FunctionVariableContextOrStackSlotIndex : -1;

    public int FunctionContextSlotIndex(JSString name)
    {
        if (HasContextAllocatedFunctionName() && ReferenceEquals(FunctionName().HeapObjectOrNull, name))
        {
            return FunctionVariableContextOrStackSlotIndex;
        }
        return -1;
    }

    // --- bootstrapping scope infos (ScopeInfo::CreateForBootstrapping) ----

    public enum BootstrappingType { Script, Function, Native, ShadowRealm }

    public static ScopeInfo CreateGlobalThisBinding() => CreateForBootstrapping(BootstrappingType.Script);
    public static ScopeInfo CreateForEmptyFunction(Isolate isolate)
    {
        ScopeInfo scopeInfo = CreateForBootstrapping(BootstrappingType.Function);
        if (isolate.Flags.function_context_cells) scopeInfo.HasContextCells = true;
        return scopeInfo;
    }
    public static ScopeInfo CreateForNativeContext() => CreateForBootstrapping(BootstrappingType.Native);
    public static ScopeInfo CreateForShadowRealmNativeContext() => CreateForBootstrapping(BootstrappingType.ShadowRealm);

    public static ScopeInfo CreateForBootstrapping(BootstrappingType type)
    {
        bool isEmptyFunction = type == BootstrappingType.Function;
        bool isNativeContext = type is BootstrappingType.Native or BootstrappingType.ShadowRealm;
        bool isScript = type == BootstrappingType.Script;
        bool isShadowRealm = type == BootstrappingType.ShadowRealm;
        int contextLocalCount = isEmptyFunction || isNativeContext ? 0 : 1;
        bool hasInferredFunctionName = isEmptyFunction;

        var scopeInfo = new ScopeInfo
        {
            ScopeType = isEmptyFunction ? ScopeType.FUNCTION_SCOPE : (isShadowRealm ? ScopeType.SHADOW_REALM_SCOPE : ScopeType.SCRIPT_SCOPE),
            LanguageMode = LanguageMode.Sloppy,
            IsDeclarationScope = true,
            ReceiverVariable = isScript ? VariableAllocationInfo.CONTEXT : VariableAllocationInfo.UNUSED,
            AllocatesArguments = false,
            FunctionVariable = isEmptyFunction ? VariableAllocationInfo.UNUSED : VariableAllocationInfo.NONE,
            HasInferredFunctionName = hasInferredFunctionName,
            HasSimpleParameters = true,
            FunctionKind = FunctionKind.NormalFunction,
            HasContextExtensionSlot = isNativeContext,
            ParameterCount = 0,
        };
        if (contextLocalCount > 0)
        {
            int value = VariablePropertiesBits.Encode(VariableMode.Const, InitializationFlag.kCreatedInitialized,
                MaybeAssignedFlag.kNotAssigned, false, VariablePropertiesBits.ParameterNumberOrPositionMax, IsStaticFlag.NotStatic);
            scopeInfo.SetContextLocals([Roots.ReadOnlyRoots.this_string], [value]);
        }
        if (isEmptyFunction)
        {
            scopeInfo.FunctionVariableName = Roots.ReadOnlyRoots.empty_string;
            scopeInfo.FunctionVariableContextOrStackSlotIndex = 0;
        }
        if (hasInferredFunctionName) scopeInfo.InferredFunctionNameValue = Roots.ReadOnlyRoots.empty_string;
        return scopeInfo;
    }

    /// <summary>ScopeInfo::CreateForWithScope.</summary>
    public static ScopeInfo CreateForWithScope(ScopeInfo? outerScopeInfo)
    {
        var scopeInfo = new ScopeInfo
        {
            ScopeType = ScopeType.WITH_SCOPE,
            LanguageMode = LanguageMode.Sloppy,
            IsDeclarationScope = false,
            ReceiverVariable = VariableAllocationInfo.NONE,
            FunctionVariable = VariableAllocationInfo.NONE,
            HasSimpleParameters = true,
            FunctionKind = FunctionKind.NormalFunction,
            HasOuterScopeInfo = outerScopeInfo is not null,
            OuterScopeInfoValue = outerScopeInfo,
        };
        return scopeInfo;
    }
}
