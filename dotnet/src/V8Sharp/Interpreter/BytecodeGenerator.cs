// Port of src/interpreter/bytecode-generator.h and bytecode-generator.cc.
//
// The generator is split over partial files in V8's source order:
//   BytecodeGenerator.cs            class, fields, construction, finalization,
//                                   feedback slot caches
//   BytecodeGenerator.Scopes.cs     the scoped helper classes (ContextScope,
//                                   ControlScope and subclasses,
//                                   ExpressionResultScope, ...)
//   BytecodeGenerator.Statements.cs declarations and statements
//   BytecodeGenerator.Literals.cs   functions, classes, object/array literals
//   BytecodeGenerator.Variables.cs  variable loads/stores, assignments,
//                                   destructuring
//   BytecodeGenerator.Expressions.cs suspends, properties, calls, operators
//
// V8's RAII scopes become IDisposable classes (or ref structs for the ones that
// are never referenced from the generator) used with `using`; the lambdas V8
// passes to BuildTryCatch/BuildTryFinally become delegates.
//
// Heap-dependent finalization (shared function infos, boilerplate
// descriptions, the global declarations array, ScopeInfos, strings) goes
// through IBytecodeGeneratorHeap, which the engine implements.
using V8Sharp.Ast;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Interpreter;

/// <summary>The flags of src/flags/flag-definitions.h that the bytecode
/// generator reads (v8_flags.*), with this revision's defaults.</summary>
// TODO(merge): route through the isolate's FlagList when it is ported.
public sealed class BytecodeGeneratorFlags
{
    public static BytecodeGeneratorFlags Default { get; } = new();

    // DEFINE_BOOL(proto_assign_seq_opt, true, ...)
    public bool proto_assign_seq_opt { get; init; } = true;
    // DEFINE_UINT(proto_assign_seq_opt_count, 2, ...)
    public uint proto_assign_seq_opt_count { get; init; } = 2;
    // DEFINE_BOOL(enable_enumerated_keyed_access_bytecode, true, ...)
    public bool enable_enumerated_keyed_access_bytecode { get; init; } = true;
    // DEFINE_EXPERIMENTAL_FEATURE(private_field_bytecodes, ...)
    public bool private_field_bytecodes { get; init; }
    // DEFINE_EXPERIMENTAL_FEATURE(for_of_optimization, ...)
    public bool for_of_optimization { get; init; }
    // DEFINE_EXPERIMENTAL_FEATURE(array_destructure_bytecode, ...)
    public bool array_destructure_bytecode { get; init; }
    // DEFINE_BOOL(super_ic, true, ...)
    public bool super_ic { get; init; } = true;
    // DEFINE_BOOL(omit_default_ctors, true, ...)
    public bool omit_default_ctors { get; init; } = true;
    // DEFINE_BOOL(cache_property_key_string_adds, true, ...)
    public bool cache_property_key_string_adds { get; init; } = true;
    // DEFINE_BOOL(ignition_share_named_property_feedback, true, ...)
    public bool ignition_share_named_property_feedback { get; init; } = true;
    // DEFINE_BOOL(ignition_elide_redundant_tdz_checks, true, ...)
    public bool ignition_elide_redundant_tdz_checks { get; init; } = true;
    // DEFINE_INT(switch_table_spread_threshold, 3, ...)
    public int switch_table_spread_threshold { get; init; } = 3;
    // DEFINE_INT(switch_table_min_cases, 6, ...)
    public int switch_table_min_cases { get; init; } = 6;
    // DEFINE_BOOL(trace, false, ...)
    public bool trace { get; init; }
    // DEFINE_BOOL(test_small_max_function_context_stub_size, false, ...)
    public bool test_small_max_function_context_stub_size { get; init; }
}

public sealed partial class BytecodeGenerator : AstVisitor
{
    // PropertyAttributes::NONE.
    const int NONE = 0;
    const int kNoSourcePosition = InterpreterConstants.kNoSourcePosition;
    const int kFeedbackIsEmbedded = InterpreterConstants.kFeedbackIsEmbedded;

    enum TestFallthrough { kThen, kElse, kNone }

    enum AccumulatorPreservingMode { kNone, kPreserve }

    // JSGeneratorObject::ResumeMode.
    const int kNext = 0;
    const int kReturn = 1;
    const int kThrow = 2;

    // ClassBoilerplate::kFirstDynamicArgumentIndex.
    const int kFirstDynamicArgumentIndex = 3;

    // DisposableStackResourcesType.
    const int kAllSync = 0;
    const int kAtLeastOneAsync = 1;

    readonly BytecodeArrayBuilder _builder;
    readonly UnoptimizedCompilationInfo _info;
    readonly AstStringConstants _astStringConstants;
    readonly DeclarationScope _closureScope;
    Scope _currentScope;
    readonly BytecodeGeneratorFlags _flags;

    // External vector of literals to be eagerly compiled.
    readonly List<FunctionLiteral>? _eagerInnerLiterals;

    readonly FeedbackSlotCache _feedbackSlotCache = new();

    readonly TopLevelDeclarationsBuilder _topLevelBuilder = new();
    readonly BlockCoverageBuilder? _blockCoverageBuilder;
    readonly List<(FunctionLiteral, int)> _functionLiterals = [];
    readonly List<(NativeFunctionLiteral, int)> _nativeFunctionLiterals = [];
    readonly List<(ObjectLiteralBoilerplateBuilder, int)> _objectLiterals = [];
    readonly List<(ArrayLiteralBoilerplateBuilder, int)> _arrayLiterals = [];
    readonly List<(ClassLiteral, int)> _classLiterals = [];
    readonly List<(GetTemplateObject, int)> _templateObjects = [];
    readonly List<Variable> _varsInHoleCheckBitmap = [];
    readonly List<(Call, Scope)> _evalCalls = [];
    readonly List<(ProtoAssignmentSeqBuilder, int)> _protoAssignSeq = [];

    ControlScope? _executionControl;
    ContextScope? _executionContext;
    ExpressionResultScope? _executionResult;

    Register _incomingNewTargetOrGenerator = Register.InvalidValue();
    Register _currentDisposablesStack = Register.InvalidValue();

    BytecodeLabels? _optionalChainingNullLabels;

    // Dummy feedback slot for compare operations, where we don't care about
    // feedback
    SharedFeedbackSlot _dummyFeedbackSlot;

    BytecodeJumpTable? _generatorJumpTable;
    int _suspendCount;
    // TODO(solanes): assess if we can move loop_depth_ into LoopScope.
    int _loopDepth;

    // Variables for which hole checks have been emitted in the current basic
    // block. Managed by HoleCheckElisionScope and HoleCheckElisionMergeScope.
    ulong _holeCheckBitmap;

    LoopScope? _currentLoopScope;
    ForInScope? _currentForInScope;

    HandlerTable.CatchPrediction _catchPrediction = HandlerTable.CatchPrediction.UNCAUGHT;

    // Free list of expression result scopes (they are created for every
    // visited expression).
    ExpressionResultScope? _freeResultScopes;

    public BytecodeGenerator(UnoptimizedCompilationInfo info, AstStringConstants astStringConstants,
                             List<FunctionLiteral>? eagerInnerLiterals, BytecodeGeneratorFlags? flags = null)
    {
        _info = info;
        _flags = flags ?? BytecodeGeneratorFlags.Default;
        _builder = new BytecodeArrayBuilder(info.num_parameters_including_this(), info.scope().num_stack_slots(),
                                            info.SourcePositionRecordingMode());
        _astStringConstants = astStringConstants;
        _closureScope = info.scope();
        _currentScope = info.scope();
        _eagerInnerLiterals = eagerInnerLiterals;
        _dummyFeedbackSlot = new SharedFeedbackSlot(feedback_spec(), FeedbackSlotKind.kCompareOp);
        Debug.Assert(closure_scope() == closure_scope().GetClosureScope());
        if (info.has_source_range_map())
        {
            _blockCoverageBuilder = new BlockCoverageBuilder(builder(), info.source_range_map()!);
        }
    }

    // ---- Accessors -----------------------------------------------------------

    BytecodeArrayBuilder builder() => _builder;
    DeclarationScope closure_scope() => _closureScope;
    UnoptimizedCompilationInfo info() => _info;
    AstStringConstants ast_string_constants() => _astStringConstants;
    BytecodeGeneratorFlags v8_flags => _flags;

    Scope current_scope() => _currentScope;
    void set_current_scope(Scope scope) => _currentScope = scope;

    ControlScope execution_control() => _executionControl!;
    void set_execution_control(ControlScope? scope) => _executionControl = scope;
    ContextScope execution_context() => _executionContext!;
    void set_execution_context(ContextScope? context) => _executionContext = context;
    void set_execution_result(ExpressionResultScope? executionResult) => _executionResult = executionResult;
    ExpressionResultScope execution_result() => _executionResult!;
    BytecodeRegisterAllocator register_allocator() => builder().RegisterAllocator();

    TopLevelDeclarationsBuilder top_level_builder() => _topLevelBuilder;

    LanguageMode language_mode() => current_scope().language_mode();
    FunctionKind function_kind() => info().literal().kind();
    FeedbackVectorSpec feedback_spec() => info().feedback_vector_spec();
    static int feedback_index(FeedbackSlot slot)
    {
        Debug.Assert(!slot.IsInvalid());
        return FeedbackSlot.GetIndex(slot);
    }

    FeedbackSlotCache feedback_slot_cache() => _feedbackSlotCache;

    HandlerTable.CatchPrediction catch_prediction() => _catchPrediction;
    void set_catch_prediction(HandlerTable.CatchPrediction value) => _catchPrediction = value;

    LoopScope? current_loop_scope() => _currentLoopScope;
    void set_current_loop_scope(LoopScope? loopScope) => _currentLoopScope = loopScope;

    ForInScope? current_for_in_scope() => _currentForInScope;
    void set_current_for_in_scope(ForInScope? forInScope) => _currentForInScope = forInScope;

    Register current_disposables_stack()
    {
        if (!_currentDisposablesStack.IsValid) throw new InvalidOperationException("no disposables stack");
        return _currentDisposablesStack;
    }

    void set_current_disposables_stack(Register disposablesStack) => _currentDisposablesStack = disposablesStack;

    Register incoming_new_target()
    {
        Debug.Assert(!IsResumableFunction(info().literal().kind()));
        if (!_incomingNewTargetOrGenerator.IsValid) throw new InvalidOperationException("no new.target register");
        return _incomingNewTargetOrGenerator;
    }

    Register generator_object()
    {
        Debug.Assert(IsResumableFunction(info().literal().kind()));
        if (!_incomingNewTargetOrGenerator.IsValid) throw new InvalidOperationException("no generator register");
        return _incomingNewTargetOrGenerator;
    }

    /// <summary>Check if hint2 is same or the subtype of hint1.</summary>
    public static bool IsSameOrSubTypeHint(TypeHint hint1, TypeHint hint2) => hint1 == (hint1 | hint2);

    public static bool IsStringTypeHint(TypeHint hint) => IsSameOrSubTypeHint(TypeHint.String, hint);

    static BytecodeArrayBuilder.ToBooleanMode ToBooleanModeFromTypeHint(TypeHint type_hint) =>
        type_hint == TypeHint.Boolean
            ? BytecodeArrayBuilder.ToBooleanMode.AlreadyBoolean
            : BytecodeArrayBuilder.ToBooleanMode.ConvertToBoolean;

    // ---- Finalization --------------------------------------------------------

    /// <summary>FinalizeBytecode: allocates the deferred constants through
    /// |heap| and returns the bytecode array, or null on stack overflow.</summary>
    public BytecodeArray? FinalizeBytecode(IBytecodeGeneratorHeap heap)
    {
        AllocateDeferredConstants(heap);

        if (_blockCoverageBuilder is not null)
        {
            info().set_coverage_info(heap.NewCoverageInfo(_blockCoverageBuilder.Slots));
        }

        if (HasStackOverflow()) return null;
        BytecodeArray bytecode_array = builder().ToBytecodeArray(heap);

        if (_incomingNewTargetOrGenerator.IsValid)
        {
            bytecode_array.IncomingNewTargetOrGeneratorRegister = _incomingNewTargetOrGenerator;
        }

        return bytecode_array;
    }

    /// <summary>FinalizeSourcePositionTable.</summary>
    public byte[] FinalizeSourcePositionTable() => builder().ToSourcePositionTable();

    public int CheckBytecodeMatches(BytecodeArray bytecode) => builder().CheckBytecodeMatches(bytecode);

    void AllocateDeferredConstants(IBytecodeGeneratorHeap heap)
    {
        if (top_level_builder().has_top_level_declaration())
        {
            // Build global declaration pair array.
            object? declarations = top_level_builder().AllocateDeclarations(info(), this, heap);
            if (declarations is null)
            {
                SetStackOverflow();
                return;
            }
            builder().SetDeferredConstantPoolEntry(top_level_builder().constant_pool_entry(), declarations);
        }

        // Find or build shared function infos.
        foreach ((FunctionLiteral expr, int entry) in _functionLiterals)
        {
            object? shared_info = heap.GetSharedFunctionInfo(expr);
            if (shared_info is null)
            {
                SetStackOverflow();
                return;
            }
            builder().SetDeferredConstantPoolEntry(entry, shared_info);
        }

        // Find or build shared function infos for the native function templates.
        foreach ((NativeFunctionLiteral expr, int entry) in _nativeFunctionLiterals)
        {
            builder().SetDeferredConstantPoolEntry(entry, heap.GetNativeFunctionSharedFunctionInfo(expr));
        }

        foreach ((Call call, Scope scope) in _evalCalls)
        {
            heap.RecordEvalScopeInfo((int)call.eval_scope_info_index(), scope);
        }

        // Build object literal constant properties
        foreach ((ObjectLiteralBoilerplateBuilder object_literal_builder, int entry) in _objectLiterals)
        {
            if (object_literal_builder.properties_count() > 0)
            {
                // If constant properties is an empty fixed array, we've already added it
                // to the constant pool when visiting the object literal.
                object constant_properties = LiteralBoilerplates.GetOrBuildBoilerplateDescription(object_literal_builder, heap);
                builder().SetDeferredConstantPoolEntry(entry, constant_properties);
            }
        }

        // Build array literal constant elements
        foreach ((ArrayLiteralBoilerplateBuilder array_literal_builder, int entry) in _arrayLiterals)
        {
            object constant_elements = LiteralBoilerplates.GetOrBuildBoilerplateDescription(array_literal_builder, heap);
            builder().SetDeferredConstantPoolEntry(entry, constant_elements);
        }

        // Build class literal boilerplates.
        foreach ((ClassLiteral class_literal, int entry) in _classLiterals)
        {
            builder().SetDeferredConstantPoolEntry(entry, heap.NewClassBoilerplate(class_literal));
        }

        // Build template literals.
        foreach ((GetTemplateObject get_template_object, int entry) in _templateObjects)
        {
            object description = LiteralBoilerplates.GetOrBuildDescription(get_template_object, heap);
            builder().SetDeferredConstantPoolEntry(entry, description);
        }

        // Build sequence proto object literal constant properties
        foreach ((ProtoAssignmentSeqBuilder seq, int entry) in _protoAssignSeq)
        {
            object constant_properties = seq.GetOrBuildBoilerplateDescription(heap);
            builder().SetDeferredConstantPoolEntry(entry, constant_properties);
        }
    }

    // ---- Feedback slot caches ------------------------------------------------

    // Returns a cached slot, or create and cache a new slot if one doesn't
    // already exists.
    FeedbackSlot GetCachedLoadGlobalICSlot(TypeofMode typeof_mode, Variable variable)
    {
        FeedbackSlotCache.SlotKind slot_kind = typeof_mode == TypeofMode.Inside
            ? FeedbackSlotCache.SlotKind.kLoadGlobalInsideTypeof
            : FeedbackSlotCache.SlotKind.kLoadGlobalNotInsideTypeof;
        int cached = feedback_slot_cache().Get(slot_kind, variable);
        if (cached != -1) return new FeedbackSlot(cached);
        FeedbackSlot slot = feedback_spec().AddLoadGlobalICSlot(typeof_mode);
        feedback_slot_cache().Put(slot_kind, variable, feedback_index(slot));
        return slot;
    }

    FeedbackSlot GetCachedStoreGlobalICSlot(LanguageMode language_mode, Variable variable)
    {
        FeedbackSlotCache.SlotKind slot_kind = is_strict(language_mode)
            ? FeedbackSlotCache.SlotKind.kStoreGlobalStrict
            : FeedbackSlotCache.SlotKind.kStoreGlobalSloppy;
        int cached = feedback_slot_cache().Get(slot_kind, variable);
        if (cached != -1) return new FeedbackSlot(cached);
        FeedbackSlot slot = feedback_spec().AddStoreGlobalICSlot(language_mode);
        feedback_slot_cache().Put(slot_kind, variable, feedback_index(slot));
        return slot;
    }

    FeedbackSlot GetCachedLoadICSlot(Expression expr, AstRawString name)
    {
        Debug.Assert(!expr.IsSuperPropertyReference());
        if (!v8_flags.ignition_share_named_property_feedback)
        {
            return feedback_spec().AddLoadICSlot();
        }
        const FeedbackSlotCache.SlotKind slot_kind = FeedbackSlotCache.SlotKind.kLoadProperty;
        if (!expr.IsVariableProxy())
        {
            return feedback_spec().AddLoadICSlot();
        }
        VariableProxy proxy = expr.AsVariableProxy()!;
        int cached = feedback_slot_cache().Get(slot_kind, proxy.var().index(), name);
        if (cached != -1) return new FeedbackSlot(cached);
        FeedbackSlot slot = feedback_spec().AddLoadICSlot();
        feedback_slot_cache().Put(slot_kind, proxy.var().index(), name, feedback_index(slot));
        return slot;
    }

    FeedbackSlot GetCachedLoadSuperICSlot(AstRawString name)
    {
        if (!v8_flags.ignition_share_named_property_feedback)
        {
            return feedback_spec().AddLoadICSlot();
        }
        const FeedbackSlotCache.SlotKind slot_kind = FeedbackSlotCache.SlotKind.kLoadSuperProperty;

        int cached = feedback_slot_cache().Get(slot_kind, name);
        if (cached != -1) return new FeedbackSlot(cached);
        FeedbackSlot slot = feedback_spec().AddLoadICSlot();
        feedback_slot_cache().Put(slot_kind, name, feedback_index(slot));
        return slot;
    }

    FeedbackSlot GetCachedStoreICSlot(Expression expr, AstRawString name)
    {
        if (!v8_flags.ignition_share_named_property_feedback)
        {
            return feedback_spec().AddStoreICSlot(language_mode());
        }
        FeedbackSlotCache.SlotKind slot_kind = is_strict(language_mode())
            ? FeedbackSlotCache.SlotKind.kSetNamedStrict
            : FeedbackSlotCache.SlotKind.kSetNamedSloppy;
        if (!expr.IsVariableProxy())
        {
            return feedback_spec().AddStoreICSlot(language_mode());
        }
        VariableProxy proxy = expr.AsVariableProxy()!;
        int cached = feedback_slot_cache().Get(slot_kind, proxy.var().index(), name);
        if (cached != -1) return new FeedbackSlot(cached);
        FeedbackSlot slot = feedback_spec().AddStoreICSlot(language_mode());
        feedback_slot_cache().Put(slot_kind, proxy.var().index(), name, feedback_index(slot));
        return slot;
    }

    internal int GetNewClosureSlot(FunctionLiteral literal)
    {
        Debug.Assert(feedback_slot_cache().Get(FeedbackSlotCache.SlotKind.kClosureFeedbackCell, literal) == -1);

        int index = feedback_spec().AddCreateClosureParameterCount(
            checked((ushort)JSParameterCount(literal.parameter_count())));
#if DEBUG
        feedback_slot_cache().Put(FeedbackSlotCache.SlotKind.kClosureFeedbackCell, literal, index);
#endif
        return index;
    }

    // JSParameterCount (src/common/globals.h).
    static int JSParameterCount(int param_count_without_receiver) =>
        param_count_without_receiver + InterpreterConstants.kJSArgcReceiverSlots;

    FeedbackSlot GetDummyCompareICSlot() => _dummyFeedbackSlot.Get();
}
