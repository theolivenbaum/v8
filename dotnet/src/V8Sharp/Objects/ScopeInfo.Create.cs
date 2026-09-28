// Port of ScopeInfo::Create (src/objects/scope-info.cc): the heap ScopeInfo
// of an analysed parser Scope, and the IScopeInfo view scope analysis reads
// (Scope::DeserializeScopeChain, the lookups of lazily compiled functions
// and eval code). The bytecode compiler's IScopeInfoProvider is
// ScopeInfoProvider below.
using V8Sharp.Ast;

namespace V8Sharp.Objects;

public sealed partial class ScopeInfo : IScopeInfo
{
    /// <summary>ScopeInfo::Create(isolate, zone, scope, outer_scope).</summary>
    public static ScopeInfo Create(Isolate isolate, Scope scope, ScopeInfo? outerScope)
    {
        // Collect variables.
        int contextLocalCount = 0;
        int moduleVarsCount = 0;
        foreach (Variable var in scope.locals())
        {
            switch (var.location())
            {
                case VariableLocation.CONTEXT:
                case VariableLocation.REPL_GLOBAL:
                    contextLocalCount++;
                    break;
                case VariableLocation.MODULE:
                    moduleVarsCount++;
                    break;
            }
        }

        // Determine use and location of the "this" binding if it is present.
        VariableAllocationInfo receiverInfo;
        if (scope.is_declaration_scope() && scope.AsDeclarationScope().has_this_declaration())
        {
            Variable var = scope.AsDeclarationScope().receiver();
            if (!var.is_used()) receiverInfo = VariableAllocationInfo.UNUSED;
            else if (var.IsContextSlot()) receiverInfo = VariableAllocationInfo.CONTEXT;
            else receiverInfo = VariableAllocationInfo.STACK;
        }
        else
        {
            receiverInfo = VariableAllocationInfo.NONE;
        }

        bool allocatesArguments = scope.is_function_scope() && scope.AsDeclarationScope().arguments() is not null;
        // TODO(cbruni): Don't always waste a field for the inferred name.
        bool hasInferredFunctionName = scope.is_function_scope();

        // Determine use and location of the function variable if it is present.
        VariableAllocationInfo functionNameInfo;
        if (scope.is_function_scope())
        {
            Variable? var = scope.AsDeclarationScope().function_var();
            if (var is not null)
            {
                if (!var.is_used()) functionNameInfo = VariableAllocationInfo.UNUSED;
                else if (var.IsContextSlot()) functionNameInfo = VariableAllocationInfo.CONTEXT;
                else functionNameInfo = VariableAllocationInfo.STACK;
            }
            else
            {
                // Always reserve space for the debug name in the scope info.
                functionNameInfo = VariableAllocationInfo.UNUSED;
            }
        }
        else if (scope.is_module_scope() || scope.is_script_scope() || scope.is_eval_scope())
        {
            // Always reserve space for the debug name in the scope info.
            functionNameInfo = VariableAllocationInfo.UNUSED;
        }
        else
        {
            functionNameInfo = VariableAllocationInfo.NONE;
        }

        bool hasBrand = scope.is_class_scope()
            ? scope.AsClassScope().brand() is not null
            : scope.IsConstructorScope() && scope.AsDeclarationScope().class_scope_has_private_brand();
        bool shouldSaveClassVariable = scope.is_class_scope() && scope.AsClassScope().should_save_class_variable();
        int parameterCount = scope.is_declaration_scope() ? scope.AsDeclarationScope().num_parameters() : 0;

        FunctionKind functionKind = FunctionKind.NormalFunction;
        bool sloppyEvalCanExtendVars = false;
        if (scope.is_declaration_scope())
        {
            functionKind = scope.AsDeclarationScope().function_kind();
            sloppyEvalCanExtendVars = scope.AsDeclarationScope().sloppy_eval_can_extend_vars();
        }

        bool hasSimpleParameters = scope.is_function_scope() && scope.AsDeclarationScope().has_simple_parameters();

        var scopeInfo = new ScopeInfo
        {
            ScopeType = scope.scope_type(),
            SloppyEvalCanExtendVars = sloppyEvalCanExtendVars,
            LanguageMode = scope.language_mode(),
            IsDeclarationScope = scope.is_declaration_scope(),
            ReceiverVariable = receiverInfo,
            ClassScopeHasPrivateBrand = hasBrand,
            HasSavedClassVariable = shouldSaveClassVariable,
            AllocatesArguments = allocatesArguments,
            FunctionVariable = functionNameInfo,
            HasInferredFunctionName = hasInferredFunctionName,
            HasSimpleParameters = hasSimpleParameters,
            FunctionKind = functionKind,
            HasOuterScopeInfo = outerScope is not null,
            IsDebugEvaluateScope = false,
            ForceContextAllocation = scope.ForceContextForLanguageMode(),
            PrivateNameLookupSkipsOuterClass = scope.private_name_lookup_skips_outer_class(),
            HasContextExtensionSlot = scope.HasContextExtensionSlot(),
            IsHidden = scope.is_hidden(),
            IsWrappedFunctionScope = scope.is_wrapped_function(),
            HasContextCells = scope.has_context_cells(),
            IsHoistedInContext = scope.is_hoisted_in_context(),
            ParameterCount = parameterCount,
            StartPositionValue = scope.start_position(),
            EndPositionValue = scope.end_position(),
            OuterScopeInfoValue = outerScope,
        };

        // Add context locals' names and info, module variables' names and info.
        // Context locals are added using their index.
        Factory factory = isolate.Factory;
        var names = new JSString[contextLocalCount];
        var infos = new int[contextLocalCount];
        var moduleVariables = moduleVarsCount == 0 ? [] : new ModuleVariableEntry[moduleVarsCount];
        int moduleVarNumber = 0;
        int contextHeaderLength = scope.ContextHeaderLength();
        foreach (Variable var in scope.locals())
        {
            switch (var.location())
            {
                case VariableLocation.CONTEXT:
                case VariableLocation.REPL_GLOBAL:
                {
                    // Due to duplicate parameters, context locals aren't guaranteed to
                    // come in order.
                    int localIndex = var.index() - contextHeaderLength;
                    names[localIndex] = factory.InternalizeString(var.raw_name().Value);
                    infos[localIndex] = VariablePropertiesBits.Encode(var.mode(), var.initialization_flag(), var.maybe_assigned(),
                        false, RelativePosition(scope, var), var.is_static_flag());
                    break;
                }
                case VariableLocation.MODULE:
                {
                    int properties = VariablePropertiesBits.Encode(var.mode(), var.initialization_flag(), var.maybe_assigned(),
                        false, RelativePosition(scope, var), var.is_static_flag());
                    moduleVariables[moduleVarNumber++] =
                        new ModuleVariableEntry(factory.InternalizeString(var.raw_name().Value), var.index(), properties);
                    break;
                }
            }
        }

        if (scope.is_declaration_scope())
        {
            // Mark contexts slots with the parameter number they represent. We walk
            // the list of parameters. That can include duplicate entries if a
            // parameter name is repeated. By walking upwards, we'll automatically
            // mark the context slot with the highest parameter number that uses this
            // variable. That will be the parameter number that is represented by the
            // context slot. All lower parameters will only be available on the stack
            // through the arguments object.
            for (int i = 0; i < parameterCount; i++)
            {
                Variable parameter = scope.AsDeclarationScope().parameter(i);
                if (parameter.location() != VariableLocation.CONTEXT) continue;
                int paramIndex = parameter.index() - contextHeaderLength;
                int info = infos[paramIndex];
                infos[paramIndex] = (info & ~((1 << 6) | (0xFFFF << 7))) | (1 << 6) | ((i & 0xFFFF) << 7);
            }
        }
        scopeInfo.SetContextLocals(names, infos);
        scopeInfo.ModuleVariables = moduleVariables;

        // If the scope is a class scope and has used static private methods,
        // save context slot index if locals are inlined, otherwise save the name.
        if (shouldSaveClassVariable)
        {
            Variable classVariable = scope.AsClassScope().class_variable()!;
            scopeInfo.SavedClassVariableInfo = scopeInfo.HasInlinedLocalNames
                ? JSValue.FromInt(classVariable.index())
                : factory.InternalizeString(classVariable.raw_name().Value);
        }

        // If present, add the function variable name and its index.
        if (functionNameInfo != VariableAllocationInfo.NONE)
        {
            Variable? var = scope.is_declaration_scope() ? scope.AsDeclarationScope().function_var() : null;
            int varIndex = -1;
            JSValue name = JSValue.Zero;
            if (var is not null)
            {
                varIndex = var.index();
                name = factory.InternalizeString(var.raw_name().Value);
            }
            scopeInfo.FunctionVariableName = name;
            scopeInfo.FunctionVariableContextOrStackSlotIndex = varIndex;
        }

        // The inferred function name is taken from the SFI.
        if (hasInferredFunctionName) scopeInfo.InferredFunctionNameValue = ReadOnlyRoots.empty_string;

        if (scope.is_module_scope()) scopeInfo.ModuleInfo = scope.AsModuleScope().module();

        if (scope.is_function_scope())
        {
            ulong unusedParameterBits = 0;
            DeclarationScope funcScope = scope.AsDeclarationScope();
            int count = Math.Min(31, funcScope.num_parameters());
            for (int i = 0; i < count; ++i)
            {
                if (!funcScope.parameter(i).is_used()) unusedParameterBits |= 1UL << i;
            }
            scopeInfo.UnusedParameterBits = unusedParameterBits;
        }
        return scopeInfo;
    }

    static int RelativePosition(Scope scope, Variable var)
    {
        int position = var.initializer_position();
        if (position == Globals.kNoSourcePosition) return VariablePropertiesBits.ParameterNumberOrPositionMax;
        int relativePosition = Math.Max(0, position - scope.start_position());
        return Math.Min(relativePosition, VariablePropertiesBits.ParameterNumberOrPositionMax);
    }

    // ---- IScopeInfo (what scope analysis reads) --------------------------------------------

    static JSString Internalize(string name) => Isolate.Current!.Factory.InternalizeString(name);

    ScopeType IScopeInfo.scope_type() => ScopeType;
    LanguageMode IScopeInfo.language_mode() => LanguageMode;
    FunctionKind IScopeInfo.function_kind() => FunctionKind;
    bool IScopeInfo.is_declaration_scope() => IsDeclarationScope;
    bool IScopeInfo.is_script_scope() => IsScriptScope;
    bool IScopeInfo.IsEmpty() => IsEmpty();
    bool IScopeInfo.IsDebugEvaluateScope() => IsDebugEvaluateScope;
    bool IScopeInfo.PrivateNameLookupSkipsOuterClass() => PrivateNameLookupSkipsOuterClass;
    bool IScopeInfo.HasContextCells() => HasContextCells;
    bool IScopeInfo.is_hoisted_in_context() => IsHoistedInContext;
    bool IScopeInfo.SloppyEvalCanExtendVars() => SloppyEvalCanExtendVars;
    bool IScopeInfo.ClassScopeHasPrivateBrand() => ClassScopeHasPrivateBrand;
    bool IScopeInfo.HasSavedClassVariable() => HasSavedClassVariable;

    (string name, int index) IScopeInfo.SavedClassVariable()
    {
        (JSString name, int index) = SavedClassVariable();
        return (name.ToString(), index);
    }

    int IScopeInfo.StartPosition() => StartPosition();
    int IScopeInfo.EndPosition() => EndPosition();
    int IScopeInfo.ContextLength() => ContextLength();
    bool IScopeInfo.HasContext() => HasContext();
    bool IScopeInfo.HasSimpleParameters() => HasSimpleParameters;
    int IScopeInfo.ParameterCount() => ParameterCount;
    int IScopeInfo.UniqueIdInScript() => UniqueIdInScript();
    bool IScopeInfo.HasOuterScopeInfo() => HasOuterScopeInfo;
    IScopeInfo? IScopeInfo.OuterScopeInfo() => OuterScopeInfoValue;
    int IScopeInfo.ContextLocalCount() => ContextLocalCount;
    bool IScopeInfo.HasInlinedLocalNames() => HasInlinedLocalNames;
    string IScopeInfo.ContextInlinedLocalName(int var) => ContextInlinedLocalName(var).ToString();
    VariableMode IScopeInfo.ContextLocalMode(int var) => ContextLocalMode(var);
    InitializationFlag IScopeInfo.ContextLocalInitFlag(int var) => ContextLocalInitFlag(var);
    MaybeAssignedFlag IScopeInfo.ContextLocalMaybeAssignedFlag(int var) => ContextLocalMaybeAssignedFlag(var);

    int IScopeInfo.ContextSlotIndex(string name) => ContextSlotIndex(Internalize(name));

    int IScopeInfo.ContextSlotIndex(string name, ref Ast.VariableLookupResult lookupResult)
    {
        int index = ContextSlotIndex(Internalize(name), out VariableLookupResult result);
        if (index >= 0)
        {
            lookupResult.slot_index = index;
            lookupResult.is_repl_mode = result.IsReplMode;
            lookupResult.is_static_flag = result.IsStaticFlag;
            lookupResult.mode = result.Mode;
            lookupResult.init_flag = result.InitFlag;
            lookupResult.maybe_assigned_flag = result.MaybeAssignedFlag;
            lookupResult.initializer_position = result.InitializerPosition;
        }
        return index;
    }

    int IScopeInfo.ModuleIndex(string name, out VariableMode mode, out InitializationFlag initFlag,
        out MaybeAssignedFlag maybeAssignedFlag, out int initializerPosition) =>
        ModuleIndex(Internalize(name), out mode, out initFlag, out maybeAssignedFlag, out initializerPosition);

    int IScopeInfo.FunctionContextSlotIndex(string name) => FunctionContextSlotIndex(Internalize(name));
    int IScopeInfo.ReceiverContextSlotIndex() => ReceiverContextSlotIndex();
    bool IScopeInfo.HasAllocatedReceiver() => HasAllocatedReceiver();
}

/// <summary>The engine's IScopeInfoProvider: heap ScopeInfos for scope analysis and the bytecode generator.</summary>
public sealed class ScopeInfoProvider(Isolate isolate) : IScopeInfoProvider
{
    static ScopeInfo? s_globalThisBinding;

    public IScopeInfo empty_scope_info() => ScopeInfo.EmptyScopeInfo;

    public IScopeInfo global_this_binding_scope_info() => s_globalThisBinding ??= ScopeInfo.CreateGlobalThisBinding();

    public IScopeInfo Create(Scope scope, IScopeInfo? outerScope) =>
        ScopeInfo.Create(isolate, scope, (ScopeInfo?)outerScope);
}
