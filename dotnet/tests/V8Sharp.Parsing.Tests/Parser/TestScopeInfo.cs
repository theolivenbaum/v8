// A managed ScopeInfo for parser tests: the parts of ScopeInfo::Create,
// ScopeInfo::CreateGlobalThisBinding and the ScopeInfo accessors
// (src/objects/scope-info.cc) that scope analysis reads through IScopeInfo.
// The engine has its own heap ScopeInfo; the tests use this one to lazily
// parse inner functions with the outer scope chain V8 would give them.

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing.Tests.Parser;

public sealed class TestScopeInfo : IScopeInfo
{
    private enum VariableAllocationInfo { NONE, STACK, CONTEXT, UNUSED }

    private sealed class ContextLocal
    {
        public string name = "";
        public VariableMode mode;
        public InitializationFlag init_flag;
        public MaybeAssignedFlag maybe_assigned;
        public IsStaticFlag is_static;
        public bool is_parameter;
        // Relative initializer position, or kMax.
        public int position;
    }

    private sealed class ModuleVariable
    {
        public string name = "";
        public int index;
        public VariableMode mode;
        public InitializationFlag init_flag;
        public MaybeAssignedFlag maybe_assigned;
        public int position;
    }

    private const int kPositionMax = (1 << 16) - 1;  // ParameterNumberOrPositionBits::kMax

    private readonly bool _isEmpty;
    private ScopeType _scopeType;
    private bool _sloppyEvalCanExtendVars;
    private LanguageMode _languageMode;
    private bool _isDeclarationScope;
    private VariableAllocationInfo _receiverInfo;
    private bool _hasBrand;
    private bool _shouldSaveClassVariable;
    private VariableAllocationInfo _functionNameInfo;
    private bool _hasSimpleParameters;
    private FunctionKind _functionKind;
    private bool _forceContextAllocation;
    private bool _privateNameLookupSkipsOuterClass;
    private bool _hasContextExtensionSlot;
    private bool _isWrappedFunction;
    private bool _hasContextCells;
    private bool _isHoistedInContext;
    private int _parameterCount;
    private int _startPosition;
    private int _endPosition;
    private ContextLocal[] _contextLocals = [];
    private readonly List<ModuleVariable> _moduleVariables = [];
    private int _savedClassVariableSlot = -1;
    private string? _functionName;
    private int _functionVarIndex = -1;
    private IScopeInfo? _outer;

    private TestScopeInfo(bool is_empty) => _isEmpty = is_empty;

    public static readonly TestScopeInfo Empty = new(true);

    // ScopeInfo::Create.
    public static TestScopeInfo Create(Scope scope, IScopeInfo? outer_scope)
    {
        var info = new TestScopeInfo(false);
        int context_local_count = 0;
        foreach (Variable var in scope.locals())
        {
            if (var.location() is VariableLocation.CONTEXT or VariableLocation.REPL_GLOBAL) context_local_count++;
        }

        // Determine use and location of the "this" binding if it is present.
        if (scope.is_declaration_scope() && scope.AsDeclarationScope().has_this_declaration())
        {
            Variable var = scope.AsDeclarationScope().receiver();
            info._receiverInfo = !var.is_used() ? VariableAllocationInfo.UNUSED
                               : var.IsContextSlot() ? VariableAllocationInfo.CONTEXT
                               : VariableAllocationInfo.STACK;
        }
        else
        {
            info._receiverInfo = VariableAllocationInfo.NONE;
        }

        // Determine use and location of the function variable if it is present.
        if (scope.is_function_scope())
        {
            Variable? var = scope.AsDeclarationScope().function_var();
            if (var != null)
            {
                info._functionNameInfo = !var.is_used() ? VariableAllocationInfo.UNUSED
                                       : var.IsContextSlot() ? VariableAllocationInfo.CONTEXT
                                       : VariableAllocationInfo.STACK;
                info._functionName = var.raw_name().ToString();
                info._functionVarIndex = var.index();
            }
            else
            {
                info._functionNameInfo = VariableAllocationInfo.UNUSED;
            }
        }
        else if (scope.is_module_scope() || scope.is_script_scope() || scope.is_eval_scope())
        {
            info._functionNameInfo = VariableAllocationInfo.UNUSED;
        }
        else
        {
            info._functionNameInfo = VariableAllocationInfo.NONE;
        }

        info._hasBrand = scope.is_class_scope()
            ? scope.AsClassScope().brand() != null
            : scope.IsConstructorScope() && scope.AsDeclarationScope().class_scope_has_private_brand();
        info._shouldSaveClassVariable = scope.is_class_scope() && scope.AsClassScope().should_save_class_variable();
        info._parameterCount = scope.is_declaration_scope() ? scope.AsDeclarationScope().num_parameters() : 0;
        info._outer = outer_scope;

        info._functionKind = FunctionKind.NormalFunction;
        if (scope.is_declaration_scope())
        {
            info._functionKind = scope.AsDeclarationScope().function_kind();
            info._sloppyEvalCanExtendVars = scope.AsDeclarationScope().sloppy_eval_can_extend_vars();
        }
        info._hasSimpleParameters = scope.is_function_scope() && scope.AsDeclarationScope().has_simple_parameters();
        info._scopeType = scope.scope_type();
        info._languageMode = scope.language_mode();
        info._isDeclarationScope = scope.is_declaration_scope();
        info._forceContextAllocation = scope.ForceContextForLanguageMode();
        info._privateNameLookupSkipsOuterClass = scope.private_name_lookup_skips_outer_class();
        info._hasContextExtensionSlot = scope.HasContextExtensionSlot();
        info._isWrappedFunction = scope.is_wrapped_function();
        info._hasContextCells = scope.has_context_cells();
        info._isHoistedInContext = scope.is_hoisted_in_context();
        info._startPosition = scope.start_position();
        info._endPosition = scope.end_position();

        // Context locals are added using their index.
        info._contextLocals = new ContextLocal[context_local_count];
        foreach (Variable var in scope.locals())
        {
            int position = var.initializer_position();
            int relative_position = position == kNoSourcePosition
                ? kPositionMax
                : Math.Min(Math.Max(0, position - scope.start_position()), kPositionMax);
            switch (var.location())
            {
                case VariableLocation.CONTEXT:
                case VariableLocation.REPL_GLOBAL:
                {
                    // Due to duplicate parameters, context locals aren't
                    // guaranteed to come in order.
                    int local_index = var.index() - scope.ContextHeaderLength();
                    info._contextLocals[local_index] = new ContextLocal
                    {
                        name = var.raw_name().ToString(),
                        mode = var.mode(),
                        init_flag = var.initialization_flag(),
                        maybe_assigned = var.maybe_assigned(),
                        is_static = var.is_static_flag(),
                        position = relative_position,
                    };
                    break;
                }
                case VariableLocation.MODULE:
                    info._moduleVariables.Add(new ModuleVariable
                    {
                        name = var.raw_name().ToString(),
                        index = var.index(),
                        mode = var.mode(),
                        init_flag = var.initialization_flag(),
                        maybe_assigned = var.maybe_assigned(),
                        position = relative_position,
                    });
                    break;
            }
        }

        if (scope.is_declaration_scope())
        {
            // Mark contexts slots with the parameter number they represent.
            for (int i = 0; i < info._parameterCount; i++)
            {
                Variable parameter = scope.AsDeclarationScope().parameter(i);
                if (parameter.location() != VariableLocation.CONTEXT) continue;
                ContextLocal local = info._contextLocals[parameter.index() - scope.ContextHeaderLength()];
                local.is_parameter = true;
                local.position = i;
            }
        }

        if (info._shouldSaveClassVariable)
        {
            info._savedClassVariableSlot = scope.AsClassScope().class_variable()!.index();
        }

        return info;
    }

    // ScopeInfo::CreateGlobalThisBinding.
    public static TestScopeInfo CreateGlobalThisBinding()
    {
        var info = new TestScopeInfo(false)
        {
            _scopeType = ScopeType.SCRIPT_SCOPE,
            _languageMode = LanguageMode.Sloppy,
            _isDeclarationScope = true,
            _receiverInfo = VariableAllocationInfo.CONTEXT,
            _functionNameInfo = VariableAllocationInfo.NONE,
            _hasSimpleParameters = true,
            _functionKind = FunctionKind.NormalFunction,
            _contextLocals =
            [
                new ContextLocal
                {
                    name = "this",
                    mode = VariableMode.Const,
                    init_flag = InitializationFlag.kCreatedInitialized,
                    maybe_assigned = MaybeAssignedFlag.kNotAssigned,
                    position = kPositionMax,
                },
            ],
        };
        return info;
    }

    public ScopeType scope_type() => _scopeType;
    public LanguageMode language_mode() => _languageMode;
    public FunctionKind function_kind() => _functionKind;
    public bool is_declaration_scope() => _isDeclarationScope;
    public bool is_script_scope() => _scopeType is ScopeType.SCRIPT_SCOPE or ScopeType.REPL_MODE_SCOPE;
    public bool IsEmpty() => _isEmpty;
    public bool IsDebugEvaluateScope() => false;
    public bool PrivateNameLookupSkipsOuterClass() => _privateNameLookupSkipsOuterClass;
    public bool HasContextCells() => _hasContextCells;
    public bool is_hoisted_in_context() => _isHoistedInContext;
    public bool SloppyEvalCanExtendVars() => _sloppyEvalCanExtendVars;
    public bool ClassScopeHasPrivateBrand() => _hasBrand;
    public bool HasSavedClassVariable() => _shouldSaveClassVariable;

    public (string name, int index) SavedClassVariable()
    {
        // The saved class variable info corresponds to the context slot index.
        int index = _savedClassVariableSlot - ContextSlots.MIN_CONTEXT_SLOTS;
        return (_contextLocals[index].name, index);
    }

    public int StartPosition() => _startPosition;
    public int EndPosition() => _endPosition;

    private bool HasContextAllocatedFunctionName() => _functionNameInfo == VariableAllocationInfo.CONTEXT;

    private int ContextHeaderLength() =>
        _hasContextExtensionSlot ? ContextSlots.MIN_CONTEXT_EXTENDED_SLOTS : ContextSlots.MIN_CONTEXT_SLOTS;

    public int ContextLength()
    {
        if (_isEmpty) return 0;
        int context_locals = ContextLocalCount();
        bool function_name_context_slot = HasContextAllocatedFunctionName();
        bool has_context =
            context_locals > 0 || _forceContextAllocation || function_name_context_slot ||
            _scopeType == ScopeType.WITH_SCOPE || _scopeType == ScopeType.CLASS_SCOPE ||
            (_scopeType == ScopeType.BLOCK_SCOPE && SloppyEvalCanExtendVars() && is_declaration_scope()) ||
            (_scopeType == ScopeType.FUNCTION_SCOPE && SloppyEvalCanExtendVars()) ||
            _scopeType == ScopeType.MODULE_SCOPE;

        if (!has_context) return 0;
        return ContextHeaderLength() + context_locals + (function_name_context_slot ? 1 : 0);
    }

    public bool HasContext() => ContextLength() > 0;
    public bool HasSimpleParameters() => _hasSimpleParameters;
    public int ParameterCount() => _parameterCount;

    public int UniqueIdInScript()
    {
        if (is_script_scope() || _scopeType == ScopeType.EVAL_SCOPE || _scopeType == ScopeType.MODULE_SCOPE)
        {
            return -2;
        }
        if (_isWrappedFunction) return -1;
        return StartPosition() + (IsDefaultConstructor(function_kind()) ? 1 : 0);
    }

    public bool HasOuterScopeInfo() => _outer != null;
    public IScopeInfo? OuterScopeInfo() => _outer;
    public int ContextLocalCount() => _contextLocals.Length;
    public bool HasInlinedLocalNames() => true;
    public string ContextInlinedLocalName(int var) => _contextLocals[var].name;
    public VariableMode ContextLocalMode(int var) => _contextLocals[var].mode;
    public InitializationFlag ContextLocalInitFlag(int var) => _contextLocals[var].init_flag;
    public MaybeAssignedFlag ContextLocalMaybeAssignedFlag(int var) => _contextLocals[var].maybe_assigned;

    public int ContextSlotIndex(string name)
    {
        VariableLookupResult lookup_result = default;
        return ContextSlotIndex(name, ref lookup_result);
    }

    public int ContextSlotIndex(string name, ref VariableLookupResult lookup_result)
    {
        for (int index = 0; index < _contextLocals.Length; index++)
        {
            ContextLocal local = _contextLocals[index];
            if (local.name != name) continue;
            lookup_result.mode = local.mode;
            lookup_result.is_static_flag = local.is_static;
            lookup_result.init_flag = local.init_flag;
            lookup_result.maybe_assigned_flag = local.maybe_assigned;
            lookup_result.is_repl_mode = _scopeType == ScopeType.REPL_MODE_SCOPE;
            lookup_result.initializer_position = local.is_parameter
                ? kNoSourcePosition
                : local.position == kPositionMax ? int.MaxValue : StartPosition() + local.position;
            return ContextHeaderLength() + index;
        }
        return -1;
    }

    public int ModuleIndex(string name, out VariableMode mode, out InitializationFlag init_flag,
                           out MaybeAssignedFlag maybe_assigned_flag, out int initializer_position)
    {
        foreach (ModuleVariable v in _moduleVariables)
        {
            if (v.name != name) continue;
            mode = v.mode;
            init_flag = v.init_flag;
            maybe_assigned_flag = v.maybe_assigned;
            initializer_position = v.position == kPositionMax ? int.MaxValue : StartPosition() + v.position;
            return v.index;
        }
        mode = default;
        init_flag = default;
        maybe_assigned_flag = default;
        initializer_position = 0;
        return 0;
    }

    public int FunctionContextSlotIndex(string name) =>
        HasContextAllocatedFunctionName() && _functionName == name ? _functionVarIndex : -1;

    public int ReceiverContextSlotIndex() =>
        _receiverInfo == VariableAllocationInfo.CONTEXT ? ContextHeaderLength() : -1;

    public bool HasAllocatedReceiver() =>
        _receiverInfo is VariableAllocationInfo.STACK or VariableAllocationInfo.CONTEXT;
}

public sealed class TestScopeInfoProvider : IScopeInfoProvider
{
    public static readonly TestScopeInfoProvider Instance = new();

    private readonly TestScopeInfo _globalThisBinding = TestScopeInfo.CreateGlobalThisBinding();

    public IScopeInfo empty_scope_info() => TestScopeInfo.Empty;
    public IScopeInfo global_this_binding_scope_info() => _globalThisBinding;
    public IScopeInfo Create(Scope scope, IScopeInfo? outer_scope) => TestScopeInfo.Create(scope, outer_scope);
}
