// Port of src/interpreter/bytecode-generator.cc: variable loads and stores,
// hole checks, assignments and destructuring.
using V8Sharp.Ast;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;
using ToBooleanMode = V8Sharp.Interpreter.BytecodeArrayBuilder.ToBooleanMode;

namespace V8Sharp.Interpreter;

public sealed partial class BytecodeGenerator
{
    public override void VisitVariableProxy(VariableProxy proxy)
    {
        builder().SetExpressionPosition(proxy);
        BuildVariableLoad(proxy.var(), proxy.hole_check_mode());
    }

    bool IsVariableInRegister(Variable var, Register reg)
    {
        BytecodeRegisterOptimizer? optimizer = builder().GetRegisterOptimizer();
        if (optimizer is not null)
        {
            return optimizer.IsVariableInRegister(var, reg);
        }
        return false;
    }

    void SetVariableInRegister(Variable var, Register reg) =>
        builder().GetRegisterOptimizer()?.SetVariableInRegister(var, reg);

    Variable? GetPotentialVariableInAccumulator()
    {
        BytecodeRegisterOptimizer? optimizer = builder().GetRegisterOptimizer();
        if (optimizer is not null)
        {
            return (Variable?)optimizer.GetPotentialVariableInAccumulator();
        }
        return null;
    }

    void BuildVariableLoad(Variable variable, HoleCheckMode hole_check_mode,
                           TypeofMode typeof_mode = TypeofMode.NotInside)
    {
        switch (variable.location())
        {
            case VariableLocation.LOCAL:
            {
                Register source = builder().Local(variable.index());
                // We need to load the variable into the accumulator, even when in a
                // VisitForRegisterScope, in order to avoid register aliasing if
                // subsequent expressions assign to the same variable.
                builder().LoadAccumulatorWithRegister(source);
                if (VariableNeedsHoleCheckInCurrentBlock(variable, hole_check_mode))
                {
                    BuildThrowIfTdzHole(variable);
                }
                break;
            }
            case VariableLocation.PARAMETER:
            {
                Register source;
                if (variable.IsReceiver())
                {
                    source = builder().Receiver();
                }
                else
                {
                    source = builder().Parameter(variable.index());
                }
                // We need to load the variable into the accumulator, even when in a
                // VisitForRegisterScope, in order to avoid register aliasing if
                // subsequent expressions assign to the same variable.
                builder().LoadAccumulatorWithRegister(source);
                if (VariableNeedsHoleCheckInCurrentBlock(variable, hole_check_mode))
                {
                    BuildThrowIfTdzHole(variable);
                }
                break;
            }
            case VariableLocation.UNALLOCATED:
            {
                // The global identifier "undefined" is immutable. Everything
                // else could be reassigned. For performance, we do a pointer comparison
                // rather than checking if the raw_name is really "undefined".
                if (variable.raw_name() == ast_string_constants().undefined_string)
                {
                    builder().LoadUndefined();
                }
                else
                {
                    FeedbackSlot slot = GetCachedLoadGlobalICSlot(typeof_mode, variable);
                    builder().LoadGlobal(variable.raw_name(), feedback_index(slot), typeof_mode);
                }
                break;
            }
            case VariableLocation.CONTEXT:
            {
                int depth = execution_context().ContextChainDepth(variable.scope()!);
                ContextScope? context = execution_context().Previous(depth);
                Register context_reg;
                if (context is not null)
                {
                    context_reg = context.reg();
                    depth = 0;
                }
                else
                {
                    context_reg = execution_context().reg();
                }

                bool is_immutable = variable.maybe_assigned() == MaybeAssignedFlag.kNotAssigned;
                Register acc = Register.VirtualAccumulator();
                if (is_immutable && IsVariableInRegister(variable, acc))
                {
                    return;
                }

                builder().LoadContextSlot(context_reg, variable, depth);
                if (VariableNeedsHoleCheckInCurrentBlock(variable, hole_check_mode))
                {
                    BuildThrowIfTdzHole(variable);
                }
                if (is_immutable)
                {
                    SetVariableInRegister(variable, acc);
                }
                break;
            }
            case VariableLocation.LOOKUP:
            {
                switch (variable.mode())
                {
                    case VariableMode.DynamicLocal:
                    {
                        Variable local_variable = variable.local_if_not_shadowed();
                        int depth = execution_context().ContextChainDepth(local_variable.scope()!);
                        ContextMode context_mode = local_variable.scope()!.has_context_cells()
                            ? ContextMode.HasContextCells
                            : ContextMode.NoContextCells;
                        builder().LoadLookupContextSlot(variable.raw_name(), typeof_mode, context_mode,
                                                        local_variable.index(), depth);
                        if (VariableNeedsHoleCheckInCurrentBlock(local_variable, hole_check_mode))
                        {
                            BuildThrowIfTdzHole(local_variable);
                        }
                        break;
                    }
                    case VariableMode.DynamicGlobal:
                    {
                        int depth = current_scope().ContextChainLengthUntilOutermostSloppyEval();
                        // TODO(1008414): Add back caching here when bug is fixed properly.
                        FeedbackSlot slot = feedback_spec().AddLoadGlobalICSlot(typeof_mode);

                        builder().LoadLookupGlobalSlot(variable.raw_name(), typeof_mode, feedback_index(slot), depth);
                        break;
                    }
                    default:
                    {
                        // Normally, private names should not be looked up dynamically,
                        // but we make an exception in debug-evaluate, in that case the
                        // lookup will be done in %SetPrivateMember() and %GetPrivateMember()
                        // calls, not here.
                        Debug.Assert(!variable.raw_name().IsPrivateName());
                        builder().LoadLookupSlot(variable.raw_name(), typeof_mode);
                        break;
                    }
                }
                break;
            }
            case VariableLocation.MODULE:
            {
                int depth = execution_context().ContextChainDepth(variable.scope()!);
                builder().LoadModuleVariable(variable.index(), depth);
                if (VariableNeedsHoleCheckInCurrentBlock(variable, hole_check_mode))
                {
                    BuildThrowIfTdzHole(variable);
                }
                break;
            }
            case VariableLocation.REPL_GLOBAL:
            {
                Debug.Assert(variable.IsReplGlobal());
                FeedbackSlot slot = GetCachedLoadGlobalICSlot(typeof_mode, variable);
                builder().LoadGlobal(variable.raw_name(), feedback_index(slot), typeof_mode);
                break;
            }
        }
    }

    void BuildVariableLoadForAccumulatorValue(Variable variable, HoleCheckMode hole_check_mode,
                                              TypeofMode typeof_mode = TypeofMode.NotInside)
    {
        using ExpressionResultScope accumulator_result = ValueResultScope();
        BuildVariableLoad(variable, hole_check_mode, typeof_mode);
    }

    void BuildReturn(int source_position)
    {
        if (v8_flags.trace)
        {
            using var register_scope = new RegisterAllocationScope(this);
            Register result = register_allocator().NewRegister();
            // Runtime returns {result} value, preserving accumulator.
            builder().StoreAccumulatorInRegister(result).CallRuntime(FunctionId.TraceExit, result);
        }
        builder().SetStatementPosition(source_position);
        builder().Return();
    }

    void BuildAsyncReturn(int source_position)
    {
        using var register_scope = new RegisterAllocationScope(this);

        if (IsAsyncGeneratorFunction(info().literal().kind()))
        {
            RegisterList args = register_allocator().NewRegisterList(3);
            builder()
                .MoveRegister(generator_object(), args[0]) // generator
                .StoreAccumulatorInRegister(args[1])       // value
                .LoadTrue()
                .StoreAccumulatorInRegister(args[2]) // done
                .CallRuntime(FunctionId.InlineAsyncGeneratorResolve, args);
        }
        else
        {
            Debug.Assert(IsAsyncFunction(info().literal().kind()) ||
                         IsModuleWithTopLevelAwait(info().literal().kind()));
            RegisterList args = register_allocator().NewRegisterList(2);
            builder()
                .MoveRegister(generator_object(), args[0]) // generator
                .StoreAccumulatorInRegister(args[1])       // value
                .CallRuntime(FunctionId.InlineAsyncFunctionResolve, args);
        }

        BuildReturn(source_position);
    }

    void BuildReThrow() => builder().ReThrow();

    void RememberHoleCheckInCurrentBlock(Variable variable)
    {
        if (!v8_flags.ignition_elide_redundant_tdz_checks) return;

        // The first N-1 variables that need hole checks may be cached in a bitmap to
        // elide subsequent hole checks in the same basic block, where N is
        // Variable::kHoleCheckBitmapBits.
        //
        // This numbering is done during bytecode generation instead of scope analysis
        // for 2 reasons:
        //
        // 1. There may be multiple eagerly compiled inner functions during a single
        // run of scope analysis, so a global numbering will result in fewer variables
        // with cacheable hole checks.
        //
        // 2. Compiler::CollectSourcePositions reparses functions and checks that the
        // recompiled bytecode is identical. Therefore the numbering must be kept
        // identical regardless of whether a function is eagerly compiled as part of
        // an outer compilation or recompiled during source position collection. The
        // simplest way to guarantee identical numbering is to scope it to the
        // compilation instead of scope analysis.
        variable.RememberHoleCheckInBitmap(ref _holeCheckBitmap, _varsInHoleCheckBitmap);
    }

    void BuildThrowIfTdzHole(Variable variable)
    {
        if (variable.is_this())
        {
            Debug.Assert(variable.mode() == VariableMode.Const);
            builder().ThrowSuperNotCalledIfTdzHole();
        }
        else
        {
            builder().ThrowReferenceErrorIfTdzHole(variable.raw_name());
        }
        RememberHoleCheckInCurrentBlock(variable);
    }

    bool VariableNeedsHoleCheckInCurrentBlock(Variable variable, HoleCheckMode hole_check_mode) =>
        hole_check_mode == HoleCheckMode.kRequired && !variable.HasRememberedHoleCheck(_holeCheckBitmap);

    bool VariableNeedsHoleCheckInCurrentBlockForAssignment(Variable variable, Token op, HoleCheckMode hole_check_mode) =>
        VariableNeedsHoleCheckInCurrentBlock(variable, hole_check_mode) ||
        (variable.is_this() && variable.mode() == VariableMode.Const && op == Token.Init);

    void BuildHoleCheckForVariableAssignment(Variable variable, Token op)
    {
        Debug.Assert(!IsPrivateMethodOrAccessorVariableMode(variable.mode()));
        Debug.Assert(VariableNeedsHoleCheckInCurrentBlockForAssignment(variable, op, HoleCheckMode.kRequired));
        if (variable.is_this())
        {
            Debug.Assert(variable.mode() == VariableMode.Const && op == Token.Init);
            // Perform an initialization check for 'this'. 'this' variable is the
            // only variable able to trigger bind operations outside the TDZ
            // via 'super' calls.
            //
            // Do not remember the hole check because this bytecode throws if 'this' is
            // *not* the hole, i.e. the opposite of the TDZ hole check.
            builder().ThrowSuperAlreadyCalledIfNotTdzHole();
        }
        else
        {
            // Perform an initialization check for let/const declared variables.
            // E.g. let x = (x = 20); is not allowed.
            Debug.Assert(IsLexicalVariableMode(variable.mode()));
            BuildThrowIfTdzHole(variable);
        }
    }

    void AddDisposableValue(VariableMode mode)
    {
        if (mode == VariableMode.Using)
        {
            RegisterList args = register_allocator().NewRegisterList(2);
            builder()
                .MoveRegister(current_disposables_stack(), args[0])
                .StoreAccumulatorInRegister(args[1])
                .CallRuntime(FunctionId.AddDisposableValue, args);
        }
        else if (mode == VariableMode.AwaitUsing)
        {
            RegisterList args = register_allocator().NewRegisterList(2);
            builder()
                .MoveRegister(current_disposables_stack(), args[0])
                .StoreAccumulatorInRegister(args[1])
                .CallRuntime(FunctionId.AddAsyncDisposableValue, args);
        }
    }

    void BuildVariableAssignment(Variable variable, Token op, HoleCheckMode hole_check_mode,
                                 LookupHoistingMode lookup_hoisting_mode = LookupHoistingMode.kNormal)
    {
        VariableMode mode = variable.mode();
        using var assignment_register_scope = new RegisterAllocationScope(this);
        switch (variable.location())
        {
            case VariableLocation.PARAMETER:
            case VariableLocation.LOCAL:
            {
                Register destination;
                if (VariableLocation.PARAMETER == variable.location())
                {
                    if (variable.IsReceiver())
                    {
                        destination = builder().Receiver();
                    }
                    else
                    {
                        destination = builder().Parameter(variable.index());
                    }
                }
                else
                {
                    destination = builder().Local(variable.index());
                }

                if (VariableNeedsHoleCheckInCurrentBlockForAssignment(variable, op, hole_check_mode))
                {
                    // Load destination to check for hole.
                    Register value_temp = register_allocator().NewRegister();
                    builder()
                        .StoreAccumulatorInRegister(value_temp)
                        .LoadAccumulatorWithRegister(destination);
                    BuildHoleCheckForVariableAssignment(variable, op);
                    builder().LoadAccumulatorWithRegister(value_temp);
                }

                if ((mode != VariableMode.Const && mode != VariableMode.Using && mode != VariableMode.AwaitUsing) ||
                    op == Token.Init)
                {
                    if (op == Token.Init)
                    {
                        if (variable.HasHoleCheckUseInSameClosureScope())
                        {
                            // After initializing a variable it won't be the hole anymore, so
                            // elide subsequent checks.
                            RememberHoleCheckInCurrentBlock(variable);
                        }
                        AddDisposableValue(mode);
                    }
                    builder().StoreAccumulatorInRegister(destination);
                }
                else if (variable.throw_on_const_assignment(language_mode()) && mode == VariableMode.Const)
                {
                    builder().CallRuntime(FunctionId.ThrowConstAssignError);
                }
                else if (variable.throw_on_const_assignment(language_mode()) && mode == VariableMode.Using)
                {
                    builder().CallRuntime(FunctionId.ThrowUsingAssignError);
                }
                else if (variable.throw_on_const_assignment(language_mode()) && mode == VariableMode.AwaitUsing)
                {
                    builder().CallRuntime(FunctionId.ThrowAwaitUsingAssignError);
                }
                break;
            }
            case VariableLocation.UNALLOCATED:
            {
                BuildStoreGlobal(variable);
                break;
            }
            case VariableLocation.CONTEXT:
            {
                int depth = execution_context().ContextChainDepth(variable.scope()!);
                ContextScope? context = execution_context().Previous(depth);
                Register context_reg;

                if (context is not null)
                {
                    context_reg = context.reg();
                    depth = 0;
                }
                else
                {
                    context_reg = execution_context().reg();
                }

                if (VariableNeedsHoleCheckInCurrentBlockForAssignment(variable, op, hole_check_mode))
                {
                    // Load destination to check for hole.
                    Register value_temp = register_allocator().NewRegister();
                    builder()
                        .StoreAccumulatorInRegister(value_temp)
                        .LoadContextSlot(context_reg, variable, depth);

                    BuildHoleCheckForVariableAssignment(variable, op);
                    builder().LoadAccumulatorWithRegister(value_temp);
                }

                if ((mode != VariableMode.Const && mode != VariableMode.Using && mode != VariableMode.AwaitUsing) ||
                    op == Token.Init)
                {
                    if (op == Token.Init)
                    {
                        if (variable.HasHoleCheckUseInSameClosureScope())
                        {
                            // After initializing a variable it won't be the hole anymore, so
                            // elide subsequent checks.
                            RememberHoleCheckInCurrentBlock(variable);
                        }
                        AddDisposableValue(mode);
                    }
                    builder().StoreContextSlot(context_reg, variable, depth);
                }
                else if (variable.throw_on_const_assignment(language_mode()))
                {
                    if (mode == VariableMode.Using)
                    {
                        builder().CallRuntime(FunctionId.ThrowUsingAssignError);
                    }
                    else if (mode == VariableMode.AwaitUsing)
                    {
                        builder().CallRuntime(FunctionId.ThrowAwaitUsingAssignError);
                    }
                    else
                    {
                        builder().CallRuntime(FunctionId.ThrowConstAssignError);
                    }
                }
                break;
            }
            case VariableLocation.LOOKUP:
            {
                builder().StoreLookupSlot(variable.raw_name(), language_mode(), lookup_hoisting_mode);
                break;
            }
            case VariableLocation.MODULE:
            {
                Debug.Assert(IsDeclaredVariableMode(mode));

                if (IsImmutableLexicalVariableMode(mode) && op != Token.Init)
                {
                    if (mode == VariableMode.Using)
                    {
                        builder().CallRuntime(FunctionId.ThrowUsingAssignError);
                    }
                    else if (mode == VariableMode.AwaitUsing)
                    {
                        builder().CallRuntime(FunctionId.ThrowAwaitUsingAssignError);
                    }
                    else
                    {
                        builder().CallRuntime(FunctionId.ThrowConstAssignError);
                    }
                    break;
                }

                // If we don't throw above, we know that we're dealing with an
                // export because imports are const and we do not generate initializing
                // assignments for them.
                Debug.Assert(variable.IsExport());

                int depth = execution_context().ContextChainDepth(variable.scope()!);
                if (VariableNeedsHoleCheckInCurrentBlockForAssignment(variable, op, hole_check_mode))
                {
                    Register value_temp = register_allocator().NewRegister();
                    builder()
                        .StoreAccumulatorInRegister(value_temp)
                        .LoadModuleVariable(variable.index(), depth);
                    BuildHoleCheckForVariableAssignment(variable, op);
                    builder().LoadAccumulatorWithRegister(value_temp);
                }
                builder().StoreModuleVariable(variable.index(), depth);
                break;
            }
            case VariableLocation.REPL_GLOBAL:
            {
                // A let or const declaration like 'let x = 7' is effectively translated
                // to:
                //   <top of the script>:
                //     ScriptContext.x = TheHole;
                //   ...
                //   <where the actual 'let' is>:
                //     ScriptContextTable.x = 7; // no hole check
                //
                // The ScriptContext slot for 'x' that we store to here is not
                // necessarily the ScriptContext of this script, but rather the
                // first ScriptContext that has a slot for name 'x'.
                Debug.Assert(variable.IsReplGlobal());
                if (op == Token.Init)
                {
                    RegisterList store_args = register_allocator().NewRegisterList(2);
                    builder()
                        .StoreAccumulatorInRegister(store_args[1])
                        .LoadLiteral(variable.raw_name())
                        .StoreAccumulatorInRegister(store_args[0]);
                    builder().CallRuntime(FunctionId.StoreGlobalNoHoleCheckForReplLetOrConst, store_args);
                }
                else
                {
                    if (mode == VariableMode.Const)
                    {
                        builder().CallRuntime(FunctionId.ThrowConstAssignError);
                    }
                    else
                    {
                        BuildStoreGlobal(variable);
                    }
                }
                break;
            }
        }
    }

    void BuildLoadNamedProperty(Expression object_expr, Register @object, AstRawString name)
    {
        FeedbackSlot slot = GetCachedLoadICSlot(object_expr, name);
        builder().LoadNamedProperty(@object, name, feedback_index(slot));
    }

    void BuildSetNamedProperty(Expression object_expr, Register @object, AstRawString name)
    {
        Register value = Register.InvalidValue();
        if (!execution_result().IsEffect())
        {
            value = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(value);
        }

        FeedbackSlot slot = GetCachedStoreICSlot(object_expr, name);
        builder().SetNamedProperty(@object, name, feedback_index(slot), language_mode());

        if (!execution_result().IsEffect())
        {
            builder().LoadAccumulatorWithRegister(value);
        }
    }

    void BuildStoreGlobal(Variable variable)
    {
        Register value = Register.InvalidValue();
        if (!execution_result().IsEffect())
        {
            value = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(value);
        }

        FeedbackSlot slot = GetCachedStoreGlobalICSlot(language_mode(), variable);
        builder().StoreGlobal(variable.raw_name(), feedback_index(slot));

        if (!execution_result().IsEffect())
        {
            builder().LoadAccumulatorWithRegister(value);
        }
    }

    void BuildLoadKeyedProperty(Register @object, FeedbackSlot slot)
    {
        if (v8_flags.enable_enumerated_keyed_access_bytecode && current_for_in_scope() is not null)
        {
            Variable? key = GetPotentialVariableInAccumulator();
            if (key is not null)
            {
                ForInScope? scope = current_for_in_scope()!.GetForInScope(key);
                if (scope is not null)
                {
                    Register enum_index = scope.enum_index();
                    Register cache_type = scope.cache_type();
                    builder().LoadEnumeratedKeyedProperty(@object, enum_index, cache_type, feedback_index(slot));
                    return;
                }
            }
        }
        builder().LoadKeyedProperty(@object, feedback_index(slot));
    }

    // An assignment has to evaluate its LHS before its RHS, but has to assign
    // to the LHS after both evaluations are done. This class stores the data
    // computed in the LHS evaluation that has to live across the RHS
    // evaluation, and is used in the actual LHS assignment.
    readonly struct AssignmentLhsData
    {
        readonly AssignType _assignType;

        // Different assignment types use different fields:
        //
        // NON_PROPERTY: expr
        // NAMED_PROPERTY: object_expr, object, name
        // KEYED_PROPERTY, PRIVATE_METHOD: object, key
        // NAMED_SUPER_PROPERTY: super_property_args
        // KEYED_SUPER_PROPERT:  super_property_args
        // PRIVATE_FIELD: object, name
        readonly Expression? _expr;
        readonly RegisterList _superPropertyArgs;
        readonly Register _object;
        readonly Register _key;
        readonly Expression? _objectExpr;
        readonly AstRawString? _name;
        readonly bool _isPrivateField;
        readonly int _slotIndex;
        readonly int _depth;

        AssignmentLhsData(AssignType assign_type, Expression? expr, RegisterList super_property_args, Register @object,
                          Register key, Expression? object_expr, AstRawString? name, bool is_private_field = false,
                          int slot_index = 0, int depth = 0)
        {
            _assignType = assign_type;
            _expr = expr;
            _superPropertyArgs = super_property_args;
            _object = @object;
            _key = key;
            _objectExpr = object_expr;
            _name = name;
            _isPrivateField = is_private_field;
            _slotIndex = slot_index;
            _depth = depth;
        }

        public static AssignmentLhsData NonProperty(Expression expr) =>
            new(AssignType.NON_PROPERTY, expr, RegisterList.Empty, Register.InvalidValue(), Register.InvalidValue(),
                null, null);

        public static AssignmentLhsData NamedProperty(Expression object_expr, Register @object, AstRawString name) =>
            new(AssignType.NAMED_PROPERTY, null, RegisterList.Empty, @object, Register.InvalidValue(), object_expr,
                name);

        public static AssignmentLhsData KeyedProperty(Register @object, Register key) =>
            new(AssignType.KEYED_PROPERTY, null, RegisterList.Empty, @object, key, null, null);

        public static AssignmentLhsData PrivateMethodOrAccessor(AssignType type, Property property, Register @object,
                                                                Register key) =>
            new(type, property, RegisterList.Empty, @object, key, null, null);

        public static AssignmentLhsData PrivateDebugEvaluate(AssignType type, Property property, Register @object) =>
            new(type, property, RegisterList.Empty, @object, Register.InvalidValue(), null, null);

        public static AssignmentLhsData NamedSuperProperty(RegisterList super_property_args) =>
            new(AssignType.NAMED_SUPER_PROPERTY, null, super_property_args, Register.InvalidValue(),
                Register.InvalidValue(), null, null);

        public static AssignmentLhsData KeyedSuperProperty(RegisterList super_property_args) =>
            new(AssignType.KEYED_SUPER_PROPERTY, null, super_property_args, Register.InvalidValue(),
                Register.InvalidValue(), null, null);

        public static AssignmentLhsData PrivateField(Register @object, int slot_index, int depth) =>
            new(AssignType.KEYED_PROPERTY, null, RegisterList.Empty, @object, Register.InvalidValue(), null, null,
                true, slot_index, depth);

        public AssignType assign_type() => _assignType;

        public bool is_private_assign_type() =>
            _assignType == AssignType.PRIVATE_METHOD || _assignType == AssignType.PRIVATE_GETTER_ONLY ||
            _assignType == AssignType.PRIVATE_SETTER_ONLY || _assignType == AssignType.PRIVATE_GETTER_AND_SETTER ||
            _assignType == AssignType.PRIVATE_DEBUG_DYNAMIC;

        public bool is_private_field() => _isPrivateField;

        public Expression expr() => _expr!;
        public Expression object_expr() => _objectExpr!;
        public Register @object() => _object;
        public Register key() => _key;
        public AstRawString name() => _name!;
        public RegisterList super_property_args() => _superPropertyArgs;
        public int slot_index() => _slotIndex;
        public int depth() => _depth;
    }

    AssignmentLhsData PrepareAssignmentLhs(Expression lhs,
                                           AccumulatorPreservingMode accumulator_preserving_mode =
                                               AccumulatorPreservingMode.kNone)
    {
        // Left-hand side can only be a property, a global or a variable slot.
        Property? property = lhs.AsProperty();
        AssignType assign_type = Property.GetAssignType(property);

        // Evaluate LHS expression.
        switch (assign_type)
        {
            case AssignType.NON_PROPERTY:
                return AssignmentLhsData.NonProperty(lhs);
            case AssignType.NAMED_PROPERTY:
            {
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                Register @object = VisitForRegisterValue(property!.obj());
                AstRawString name = property.key().AsLiteral()!.AsRawPropertyName();
                return AssignmentLhsData.NamedProperty(property.obj(), @object, name);
            }
            case AssignType.KEYED_PROPERTY:
            {
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                Register @object = VisitForRegisterValue(property!.obj());

                if (v8_flags.private_field_bytecodes && property.IsPrivateReference())
                {
                    Variable var = property.key().AsVariableProxy()!.var();
                    int depth = execution_context().ContextChainDepth(var.scope()!);
                    return AssignmentLhsData.PrivateField(@object, var.index(), depth);
                }
                Register key = VisitForRegisterValue(property.key());
                return AssignmentLhsData.KeyedProperty(@object, key);
            }
            case AssignType.PRIVATE_METHOD:
            case AssignType.PRIVATE_GETTER_ONLY:
            case AssignType.PRIVATE_SETTER_ONLY:
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                Debug.Assert(!property!.IsSuperAccess());
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                Register @object = VisitForRegisterValue(property.obj());
                Register key = VisitForRegisterValue(property.key());
                return AssignmentLhsData.PrivateMethodOrAccessor(assign_type, property, @object, key);
            }
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
            {
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                Register @object = VisitForRegisterValue(property!.obj());
                // Do not visit the key here, instead we will look them up at run time.
                return AssignmentLhsData.PrivateDebugEvaluate(assign_type, property, @object);
            }
            case AssignType.NAMED_SUPER_PROPERTY:
            {
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                RegisterList super_property_args = register_allocator().NewRegisterList(4);
                BuildThisVariableLoad();
                builder().StoreAccumulatorInRegister(super_property_args[0]);
                BuildVariableLoad(property!.obj().AsSuperPropertyReference()!.home_object().var(),
                                  HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(super_property_args[1]);
                builder()
                    .LoadLiteral(property.key().AsLiteral()!.AsRawPropertyName())
                    .StoreAccumulatorInRegister(super_property_args[2]);
                return AssignmentLhsData.NamedSuperProperty(super_property_args);
            }
            case AssignType.KEYED_SUPER_PROPERTY:
            {
                using var scope = new AccumulatorPreservingScope(this, accumulator_preserving_mode);
                RegisterList super_property_args = register_allocator().NewRegisterList(4);
                BuildThisVariableLoad();
                builder().StoreAccumulatorInRegister(super_property_args[0]);
                BuildVariableLoad(property!.obj().AsSuperPropertyReference()!.home_object().var(),
                                  HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(super_property_args[1]);
                VisitForRegisterValue(property.key(), super_property_args[2]);
                return AssignmentLhsData.KeyedSuperProperty(super_property_args);
            }
        }
        throw new UnreachableException();
    }

    // Build the iteration finalizer called in the finally block of an iteration
    // protocol execution. This closes the iterator if needed, and suppresses any
    // exception it throws if necessary, including the exception when the return
    // method is not callable.
    //
    // In pseudo-code, this builds:
    //
    // if (!done) {
    //   try {
    //     let method = iterator.return
    //     if (method !== null && method !== undefined) {
    //       let return_val = method.call(iterator)
    //       if (!%IsObject(return_val)) throw TypeError
    //     }
    //   } catch (e) {
    //     if (iteration_continuation != RETHROW)
    //       rethrow e
    //   }
    // }
    //
    // For async iterators, iterator.close() becomes await iterator.close().
    void BuildFinalizeIteration(IteratorRecord iterator, Register done, Register iteration_continuation_token)
    {
        using var register_scope = new RegisterAllocationScope(this);
        var iterator_is_done = new BytecodeLabels();

        // if (!done) {
        builder().LoadAccumulatorWithRegister(done).JumpIfTrue(ToBooleanMode.ConvertToBoolean, iterator_is_done.New());

        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            BuildTryCatch(
                // try {
                //   let method = iterator.return
                //   if (method !== null && method !== undefined) {
                //     let return_val = method.call(iterator)
                //     if (!%IsObject(return_val)) throw TypeError
                //   }
                // }
                () =>
                {
                    Register method = register_allocator().NewRegister();
                    builder()
                        .LoadNamedProperty(iterator.@object(), ast_string_constants().return_string,
                                           feedback_index(feedback_spec().AddLoadICSlot()))
                        .JumpIfUndefinedOrNull(iterator_is_done.New())
                        .StoreAccumulatorInRegister(method);

                    var args = new RegisterList(iterator.@object());
                    builder().CallProperty(method, args, feedback_index(feedback_spec().AddCallICSlot()));
                    if (iterator.type() == IteratorType.kAsync)
                    {
                        BuildAwait();
                    }
                    builder().JumpIfJSReceiver(iterator_is_done.New());
                    {
                        // Throw this exception inside the try block so that it is
                        // suppressed by the iteration continuation if necessary.
                        using var register_scope2 = new RegisterAllocationScope(this);
                        Register return_result = register_allocator().NewRegister();
                        builder()
                            .StoreAccumulatorInRegister(return_result)
                            .CallRuntime(FunctionId.ThrowIteratorResultNotAnObject, return_result);
                    }
                },

                // catch (e) {
                //   if (iteration_continuation != RETHROW)
                //     rethrow e
                // }
                context =>
                {
                    // Reuse context register to store the exception.
                    Register close_exception = context;
                    builder().StoreAccumulatorInRegister(close_exception);

                    var suppress_close_exception = new BytecodeLabel();
                    builder()
                        .LoadLiteral(Smi.FromInt((int)TryFinallyContinuationToken.RethrowToken))
                        .CompareReference(iteration_continuation_token)
                        .JumpIfTrue(ToBooleanMode.AlreadyBoolean, suppress_close_exception)
                        .LoadAccumulatorWithRegister(close_exception)
                        .ReThrow()
                        .Bind(suppress_close_exception);
                },
                catch_prediction());
        }

        iterator_is_done.Bind(builder());
    }

    // Get the default value of a destructuring target. Will mutate the
    // destructuring target expression if there is a default value.
    //
    // For
    //   a = b
    // in
    //   let {a = b} = c
    // returns b and mutates the input into a.
    static Expression? GetDestructuringDefaultValue(ref Expression target)
    {
        Expression? default_value = null;
        if (target.IsAssignment())
        {
            Assignment default_init = target.AsAssignment()!;
            Debug.Assert(default_init.op() == Token.Assign);
            default_value = default_init.value();
            target = default_init.target();
            Debug.Assert(target.IsValidReferenceExpression() || target.IsPattern());
        }
        return default_value;
    }

    // Convert a destructuring assignment to an array literal into a sequence of
    // iterator accesses into the value being assigned (in the accumulator).
    //
    // [a().x, ...b] = accumulator
    //
    //   becomes
    //
    // iterator = %GetIterator(accumulator)
    // try {
    //
    //   // Individual assignments read off the value from iterator.next() This gets
    //   // repeated per destructuring element.
    //   if (!done) {
    //     // Make sure we are considered 'done' if .next(), .done or .value fail.
    //     done = true
    //     var next_result = iterator.next()
    //     var tmp_done = next_result.done
    //     if (!tmp_done) {
    //       value = next_result.value
    //       done = false
    //     }
    //   }
    //   if (done)
    //     value = undefined
    //   a().x = value
    //
    //   // A spread receives the remaining items in the iterator.
    //   var array = []
    //   var index = 0
    //   %FillArrayWithIterator(iterator, array, index, done)
    //   done = true
    //   b = array
    //
    // } catch(e) {
    //   iteration_continuation = RETHROW
    // } finally {
    //   %FinalizeIteration(iterator, done, iteration_continuation)
    // }
    void BuildDestructuringArrayAssignment(ArrayLiteral pattern, Token op, LookupHoistingMode lookup_hoisting_mode)
    {
        // A pattern can use the fast destructuring bytecode if the feature flag is
        // enabled and all targets are simple variable assignments without defaults,
        // holes, spreads, or property assignments that could trigger observable side
        // effects.
        bool can_use_destructure_bytecode = v8_flags.array_destructure_bytecode;
        if (can_use_destructure_bytecode)
        {
            foreach (Expression target in pattern.values())
            {
                if (!target.IsVariableProxy())
                {
                    can_use_destructure_bytecode = false;
                    break;
                }
            }
        }

        if (can_use_destructure_bytecode)
        {
            using var allocation_scope = new RegisterAllocationScope(this);
            Register rhs = Register.InvalidValue();
            if (!execution_result().IsEffect())
            {
                rhs = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(rhs);
            }
            int count = pattern.values().Count;
            // TODO(leszeks): If all the targets are registers that all happen to be
            // sequential (likely with sequential local variables like in `let [x,y] =
            // a`), write into those directly instead of allocating a temporary
            // RegisterList.
            RegisterList outputs = register_allocator().NewRegisterList(count);
            if (count > 0)
            {
                Expression first_target = pattern.values()[0];
                builder().SetExpressionPosition(first_target);
            }
            builder().ArrayDestructure(outputs, count);
            for (int i = 0; i < count; ++i)
            {
                Expression target = pattern.values()[i];
                builder().SetExpressionPosition(target);
                AssignmentLhsData lhs = PrepareAssignmentLhs(target);
                builder().LoadAccumulatorWithRegister(outputs[i]);
                BuildAssignment(lhs, op, lookup_hoisting_mode);
            }
            if (!execution_result().IsEffect())
            {
                builder().LoadAccumulatorWithRegister(rhs);
            }
            return;
        }

        using var scope = new RegisterAllocationScope(this);

        Register value = register_allocator().NewRegister();
        builder().StoreAccumulatorInRegister(value);

        // Store the iterator in a dedicated register so that it can be closed on
        // exit, and the 'done' value in a dedicated register so that it can be
        // changed and accessed independently of the iteration result.
        IteratorRecord iterator = BuildGetIteratorRecord(IteratorType.kNormal);
        Register done = register_allocator().NewRegister();
        builder().LoadFalse();
        builder().StoreAccumulatorInRegister(done);

        BuildTryFinally(
            // Try block.
            () =>
            {
                Register next_result = register_allocator().NewRegister();
                FeedbackSlot next_value_load_slot = feedback_spec().AddLoadICSlot();
                FeedbackSlot next_done_load_slot = feedback_spec().AddLoadICSlot();

                Spread? spread = null;
                foreach (Expression pattern_target in pattern.values())
                {
                    Expression target = pattern_target;
                    if (target.IsSpread())
                    {
                        spread = target.AsSpread();
                        break;
                    }

                    Expression? default_value = GetDestructuringDefaultValue(ref target);
                    builder().SetExpressionPosition(target);

                    AssignmentLhsData lhs_data = PrepareAssignmentLhs(target);

                    // if (!done) {
                    //   // Make sure we are considered done if .next(), .done or .value
                    //   // fail.
                    //   done = true
                    //   var next_result = iterator.next()
                    //   var tmp_done = next_result.done
                    //   if (!tmp_done) {
                    //     value = next_result.value
                    //     done = false
                    //   }
                    // }
                    // if (done)
                    //   value = undefined
                    var is_done = new BytecodeLabels();

                    builder().LoadAccumulatorWithRegister(done);
                    builder().JumpIfTrue(ToBooleanMode.ConvertToBoolean, is_done.New());

                    builder().LoadTrue().StoreAccumulatorInRegister(done);
                    BuildIteratorNext(iterator, next_result);
                    builder()
                        .LoadNamedProperty(next_result, ast_string_constants().done_string,
                                           feedback_index(next_done_load_slot))
                        .JumpIfTrue(ToBooleanMode.ConvertToBoolean, is_done.New());

                    // Only do the assignment if this is not a hole (i.e. 'elided').
                    if (!target.IsTheHoleLiteral())
                    {
                        builder()
                            .LoadNamedProperty(next_result, ast_string_constants().value_string,
                                               feedback_index(next_value_load_slot))
                            .StoreAccumulatorInRegister(next_result)
                            .LoadFalse()
                            .StoreAccumulatorInRegister(done)
                            .LoadAccumulatorWithRegister(next_result);

                        // [<pattern> = <init>] = <value>
                        //   becomes (roughly)
                        // temp = <value>.next();
                        // <pattern> = temp === undefined ? <init> : temp;
                        var do_assignment = new BytecodeLabel();
                        if (default_value is not null)
                        {
                            builder().JumpIfNotUndefined(do_assignment);
                            // Since done == true => temp == undefined, jump directly to using
                            // the default value for that case.
                            is_done.Bind(builder());
                            VisitInHoleCheckElisionScopeForAccumulatorValue(default_value);
                        }
                        else
                        {
                            builder().Jump(do_assignment);
                            is_done.Bind(builder());
                            builder().LoadUndefined();
                        }
                        builder().Bind(do_assignment);

                        BuildAssignment(lhs_data, op, lookup_hoisting_mode);
                    }
                    else
                    {
                        builder().LoadFalse().StoreAccumulatorInRegister(done);
                        Debug.Assert(lhs_data.assign_type() == AssignType.NON_PROPERTY);
                        is_done.Bind(builder());
                    }
                }

                if (spread is not null)
                {
                    using var spread_scope = new RegisterAllocationScope(this);
                    var spread_is_done = new BytecodeLabel();

                    // A spread is turned into a loop over the remainer of the iterator.
                    Expression target = spread.expression();
                    builder().SetExpressionPosition(spread);

                    AssignmentLhsData lhs_data = PrepareAssignmentLhs(target);

                    // var array = [];
                    Register array = register_allocator().NewRegister();
                    builder().CreateEmptyArrayLiteral(feedback_index(feedback_spec().AddLiteralSlot()));
                    builder().StoreAccumulatorInRegister(array);

                    // If done, jump to assigning empty array
                    builder().LoadAccumulatorWithRegister(done);
                    builder().JumpIfTrue(ToBooleanMode.ConvertToBoolean, spread_is_done);

                    // var index = 0;
                    Register index = register_allocator().NewRegister();
                    builder().LoadLiteral(Smi.Zero);
                    builder().StoreAccumulatorInRegister(index);

                    // Set done to true, since it's guaranteed to be true by the time the
                    // array fill completes.
                    builder().LoadTrue().StoreAccumulatorInRegister(done);

                    // Fill the array with the iterator.
                    FeedbackSlot element_slot = feedback_spec().AddStoreInArrayLiteralICSlot();
                    BuildFillArrayWithIterator(iterator, array, index, next_result, next_value_load_slot,
                                               next_done_load_slot, element_slot);

                    builder().Bind(spread_is_done);
                    // Assign the array to the LHS.
                    builder().LoadAccumulatorWithRegister(array);
                    BuildAssignment(lhs_data, op, lookup_hoisting_mode);
                }
            },
            // Finally block.
            (iteration_continuation_token, iteration_continuation_result, message) =>
            {
                // Finish the iteration in the finally block.
                BuildFinalizeIteration(iterator, done, iteration_continuation_token);
            },
            HandlerTable.CatchPrediction.UNCAUGHT);

        if (!execution_result().IsEffect())
        {
            builder().LoadAccumulatorWithRegister(value);
        }
    }

    // Convert a destructuring assignment to an object literal into a sequence of
    // property accesses into the value being assigned (in the accumulator).
    //
    // { y, [x++]: a(), ...b.c } = value
    //
    //   becomes
    //
    // var rest_runtime_callargs = new Array(3);
    // rest_runtime_callargs[0] = value;
    //
    // rest_runtime_callargs[1] = "y";
    // y = value.y;
    //
    // var temp1 = %ToName(x++);
    // rest_runtime_callargs[2] = temp1;
    // a() = value[temp1];
    //
    // b.c =
    // %CopyDataPropertiesWithExcludedPropertiesOnStack.call(rest_runtime_callargs);
    void BuildDestructuringObjectAssignment(ObjectLiteral pattern, Token op, LookupHoistingMode lookup_hoisting_mode)
    {
        using var register_scope = new RegisterAllocationScope(this);

        // Store the assignment value in a register.
        Register value;
        RegisterList rest_runtime_callargs = RegisterList.Empty;
        if (pattern.builder().has_rest_property())
        {
            rest_runtime_callargs = register_allocator().NewRegisterList(pattern.properties().Count);
            value = rest_runtime_callargs[0];
        }
        else
        {
            value = register_allocator().NewRegister();
        }
        builder().StoreAccumulatorInRegister(value);

        // if (value === null || value === undefined)
        //   throw new TypeError(kNonCoercible);
        //
        // Since the first property access on null/undefined will also trigger a
        // TypeError, we can elide this check. The exception is when there are no
        // properties and no rest property (this is an empty literal), or when there
        // is only a rest property (as it uses CloneObject, which does not throw on
        // null/undefined), or when the first property is a computed name and
        // accessing it can have side effects.
        //
        // TODO(leszeks): Also eliminate this check if the value is known to be
        // non-null (e.g. an object literal).
        List<ObjectLiteralProperty> properties = pattern.properties();
        bool is_empty_pattern = properties.Count == 0;
        bool is_only_rest_property = properties.Count == 1 && properties[0].kind() == ObjectLiteralProperty.Kind.SPREAD;
        bool is_first_property_computed_name = !is_empty_pattern && properties[0].is_computed_name() &&
                                               properties[0].kind() != ObjectLiteralProperty.Kind.SPREAD;

        if (is_empty_pattern || is_only_rest_property || is_first_property_computed_name)
        {
            var is_null_or_undefined = new BytecodeLabel();
            var not_null_or_undefined = new BytecodeLabel();
            builder()
                .JumpIfUndefinedOrNull(is_null_or_undefined)
                .Jump(not_null_or_undefined);

            {
                builder().Bind(is_null_or_undefined);
                builder().SetExpressionPosition(pattern);
                builder().CallRuntime(FunctionId.ThrowPatternAssignmentNonCoercible, value);
            }
            builder().Bind(not_null_or_undefined);
        }

        int i = 0;
        foreach (ObjectLiteralProperty pattern_property in properties)
        {
            using var inner_register_scope = new RegisterAllocationScope(this);

            // The key of the pattern becomes the key into the RHS value, and the value
            // of the pattern becomes the target of the assignment.
            //
            // e.g. { a: b } = o becomes b = o.a
            Expression pattern_key = pattern_property.key();
            Expression target = pattern_property.value();
            Expression? default_value = GetDestructuringDefaultValue(ref target);
            builder().SetExpressionPosition(target);

            // Calculate this property's key into the assignment RHS value, additionally
            // storing the key for rest_runtime_callargs if needed.
            //
            // The RHS is accessed using the key either by LoadNamedProperty (if
            // value_name is valid) or by LoadKeyedProperty (otherwise).
            AstRawString? value_name = null;
            Register value_key = Register.InvalidValue();

            if (pattern_property.kind() != ObjectLiteralProperty.Kind.SPREAD)
            {
                if (pattern_key.IsPropertyName())
                {
                    value_name = pattern_key.AsLiteral()!.AsRawPropertyName();
                }
                if (pattern.builder().has_rest_property() || value_name is null)
                {
                    if (pattern.builder().has_rest_property())
                    {
                        value_key = rest_runtime_callargs[i + 1];
                    }
                    else
                    {
                        value_key = register_allocator().NewRegister();
                    }
                    if (pattern_property.is_computed_name())
                    {
                        // { [a()]: b().x } = c
                        // becomes
                        // var tmp = a()
                        // b().x = c[tmp]
                        Debug.Assert(!pattern_key.IsPropertyName() || !pattern_key.IsNumberLiteral());
                        VisitForAccumulatorValue(pattern_key);
                        builder().ToName().StoreAccumulatorInRegister(value_key);
                    }
                    else
                    {
                        // We only need the key for non-computed properties when it is numeric
                        // or is being saved for the rest_runtime_callargs.
                        Debug.Assert(pattern_key.IsNumberLiteral() ||
                                     (pattern.builder().has_rest_property() && pattern_key.IsPropertyName()));
                        VisitForRegisterValue(pattern_key, value_key);
                    }
                }
            }

            AssignmentLhsData lhs_data = PrepareAssignmentLhs(target);

            // Get the value from the RHS.
            if (pattern_property.kind() == ObjectLiteralProperty.Kind.SPREAD)
            {
                Debug.Assert(i == properties.Count - 1);
                Debug.Assert(!value_key.IsValid);
                Debug.Assert(value_name is null);
                if (properties.Count == 1)
                {
                    // If there is only the rest property, we have no excluded properties.
                    // We can use the CloneObject bytecode instead of the more expensive
                    // CopyDataPropertiesWithExcludedPropertiesOnStack runtime call.
                    // E.g. for `let { ...rest } = obj;`.
                    Debug.Assert(pattern.builder().has_rest_property());
                    int flags = CreateObjectLiteralFlags.Encode(0, false);
                    int clone_index = feedback_index(feedback_spec().AddCloneObjectSlot());
                    builder().CloneObject(value, flags, clone_index);
                }
                else
                {
                    builder().CallRuntime(FunctionId.InlineCopyDataPropertiesWithExcludedPropertiesOnStack,
                                          rest_runtime_callargs);
                }
            }
            else if (value_name is not null)
            {
                builder().LoadNamedProperty(value, value_name, feedback_index(feedback_spec().AddLoadICSlot()));
            }
            else
            {
                Debug.Assert(value_key.IsValid);
                builder().LoadAccumulatorWithRegister(value_key)
                    .LoadKeyedProperty(value, feedback_index(feedback_spec().AddKeyedLoadICSlot()));
            }

            // {<pattern> = <init>} = <value>
            //   becomes
            // temp = <value>;
            // <pattern> = temp === undefined ? <init> : temp;
            if (default_value is not null)
            {
                var value_not_undefined = new BytecodeLabel();
                builder().JumpIfNotUndefined(value_not_undefined);
                VisitInHoleCheckElisionScopeForAccumulatorValue(default_value);
                builder().Bind(value_not_undefined);
            }

            BuildAssignment(lhs_data, op, lookup_hoisting_mode);

            i++;
        }

        if (!execution_result().IsEffect())
        {
            builder().LoadAccumulatorWithRegister(value);
        }
    }

    void BuildAssignment(in AssignmentLhsData lhs_data, Token op, LookupHoistingMode lookup_hoisting_mode)
    {
        // Assign the value to the LHS.
        switch (lhs_data.assign_type())
        {
            case AssignType.NON_PROPERTY:
            {
                if (lhs_data.expr().AsObjectLiteral() is { } pattern_as_object)
                {
                    // Split object literals into destructuring.
                    BuildDestructuringObjectAssignment(pattern_as_object, op, lookup_hoisting_mode);
                }
                else if (lhs_data.expr().AsArrayLiteral() is { } pattern_as_array)
                {
                    // Split object literals into destructuring.
                    BuildDestructuringArrayAssignment(pattern_as_array, op, lookup_hoisting_mode);
                }
                else
                {
                    Debug.Assert(lhs_data.expr().IsVariableProxy());
                    VariableProxy proxy = lhs_data.expr().AsVariableProxy()!;
                    BuildVariableAssignment(proxy.var(), op, proxy.hole_check_mode(), lookup_hoisting_mode);
                }
                break;
            }
            case AssignType.NAMED_PROPERTY:
            {
                BuildSetNamedProperty(lhs_data.object_expr(), lhs_data.@object(), lhs_data.name());
                break;
            }
            case AssignType.KEYED_PROPERTY:
            {
                FeedbackSlot slot = feedback_spec().AddKeyedStoreICSlot(language_mode());
                Register value = Register.InvalidValue();
                if (!execution_result().IsEffect())
                {
                    value = register_allocator().NewRegister();
                    builder().StoreAccumulatorInRegister(value);
                }
                if (v8_flags.private_field_bytecodes && lhs_data.is_private_field())
                {
                    int depth = lhs_data.depth();
                    ContextScope? context = execution_context().Previous(depth);
                    Register context_reg;
                    if (context is not null)
                    {
                        context_reg = context.reg();
                        depth = 0;
                    }
                    else
                    {
                        context_reg = execution_context().reg();
                    }
                    builder().SetPrivateField(context_reg, lhs_data.slot_index(), depth, lhs_data.@object(),
                                              feedback_index(slot));
                }
                else
                {
                    builder().SetKeyedProperty(lhs_data.@object(), lhs_data.key(), feedback_index(slot),
                                               language_mode());
                }
                if (!execution_result().IsEffect())
                {
                    builder().LoadAccumulatorWithRegister(value);
                }
                break;
            }
            case AssignType.NAMED_SUPER_PROPERTY:
            {
                builder()
                    .StoreAccumulatorInRegister(lhs_data.super_property_args()[3])
                    .CallRuntime(FunctionId.StoreToSuper, lhs_data.super_property_args());
                break;
            }
            case AssignType.KEYED_SUPER_PROPERTY:
            {
                builder()
                    .StoreAccumulatorInRegister(lhs_data.super_property_args()[3])
                    .CallRuntime(FunctionId.StoreKeyedToSuper, lhs_data.super_property_args());
                break;
            }
            case AssignType.PRIVATE_METHOD:
            {
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateMethodWrite, lhs_data.expr().AsProperty()!);
                break;
            }
            case AssignType.PRIVATE_GETTER_ONLY:
            {
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateSetterAccess, lhs_data.expr().AsProperty()!);
                break;
            }
            case AssignType.PRIVATE_SETTER_ONLY:
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                Register value = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(value);
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                BuildPrivateSetterAccess(lhs_data.@object(), lhs_data.key(), value);
                if (!execution_result().IsEffect())
                {
                    builder().LoadAccumulatorWithRegister(value);
                }
                break;
            }
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
            {
                Register value = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(value);
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateDebugDynamicSet(property, lhs_data.@object(), value);
                if (!execution_result().IsEffect())
                {
                    builder().LoadAccumulatorWithRegister(value);
                }
                break;
            }
        }
    }

    public override void VisitAssignment(Assignment expr)
    {
        AssignmentLhsData lhs_data = PrepareAssignmentLhs(expr.target());

        VisitForAccumulatorValue(expr.value());

        builder().SetExpressionPosition(expr);
        BuildAssignment(lhs_data, expr.op(), expr.lookup_hoisting_mode());
    }

    public override void VisitCompoundAssignment(CompoundAssignment expr)
    {
        AssignmentLhsData lhs_data = PrepareAssignmentLhs(expr.target());

        // Evaluate the value and potentially handle compound assignments by loading
        // the left-hand side value and performing a binary operation.
        switch (lhs_data.assign_type())
        {
            case AssignType.NON_PROPERTY:
            {
                VariableProxy proxy = expr.target().AsVariableProxy()!;
                BuildVariableLoad(proxy.var(), proxy.hole_check_mode());
                break;
            }
            case AssignType.NAMED_PROPERTY:
            {
                BuildLoadNamedProperty(lhs_data.object_expr(), lhs_data.@object(), lhs_data.name());
                break;
            }
            case AssignType.KEYED_PROPERTY:
            {
                FeedbackSlot slot = feedback_spec().AddKeyedLoadICSlot();
                if (v8_flags.private_field_bytecodes && lhs_data.is_private_field())
                {
                    int depth = lhs_data.depth();
                    ContextScope? context = execution_context().Previous(depth);
                    Register context_reg;
                    if (context is not null)
                    {
                        context_reg = context.reg();
                        depth = 0;
                    }
                    else
                    {
                        context_reg = execution_context().reg();
                    }
                    builder().GetPrivateField(context_reg, lhs_data.slot_index(), depth, lhs_data.@object(),
                                              feedback_index(slot));
                    break;
                }
                builder().LoadAccumulatorWithRegister(lhs_data.key());
                BuildLoadKeyedProperty(lhs_data.@object(), slot);
                break;
            }
            case AssignType.NAMED_SUPER_PROPERTY:
            {
                builder().CallRuntime(FunctionId.LoadFromSuper, lhs_data.super_property_args().Truncate(3));
                break;
            }
            case AssignType.KEYED_SUPER_PROPERTY:
            {
                builder().CallRuntime(FunctionId.LoadKeyedFromSuper, lhs_data.super_property_args().Truncate(3));
                break;
            }
            // BuildAssignment() will throw an error about the private method being
            // read-only.
            case AssignType.PRIVATE_METHOD:
            {
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                builder().LoadAccumulatorWithRegister(lhs_data.key());
                break;
            }
            // For read-only properties, BuildAssignment() will throw an error about
            // the missing setter.
            case AssignType.PRIVATE_GETTER_ONLY:
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                BuildPrivateGetterAccess(lhs_data.@object(), lhs_data.key());
                break;
            }
            case AssignType.PRIVATE_SETTER_ONLY:
            {
                // The property access is invalid, but if the brand check fails too, we
                // need to return the error from the brand check.
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateBrandCheck(property, lhs_data.@object());
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateGetterAccess, lhs_data.expr().AsProperty()!);
                break;
            }
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
            {
                Property property = lhs_data.expr().AsProperty()!;
                BuildPrivateDebugDynamicGet(property, lhs_data.@object());
                break;
            }
        }

        BinaryOperation binop = expr.binary_operation();
        var short_circuit = new BytecodeLabel();
        if (binop.op() == Token.Nullish)
        {
            var nullish = new BytecodeLabel();
            builder()
                .JumpIfUndefinedOrNull(nullish)
                .Jump(short_circuit)
                .Bind(nullish);
            VisitInHoleCheckElisionScopeForAccumulatorValue(expr.value());
        }
        else if (binop.op() == Token.Or)
        {
            builder().JumpIfTrue(ToBooleanMode.ConvertToBoolean, short_circuit);
            VisitInHoleCheckElisionScopeForAccumulatorValue(expr.value());
        }
        else if (binop.op() == Token.And)
        {
            builder().JumpIfFalse(ToBooleanMode.ConvertToBoolean, short_circuit);
            VisitInHoleCheckElisionScopeForAccumulatorValue(expr.value());
        }
        else if (expr.value().IsSmiLiteral())
        {
            builder().BinaryOperationSmiLiteral(binop.op(), Smi.FromInt(expr.value().AsLiteral()!.AsSmiLiteral()),
                                                kFeedbackIsEmbedded);
        }
        else
        {
            Register old_value = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(old_value);
            VisitForAccumulatorValue(expr.value());
            builder().BinaryOperation(binop.op(), old_value, kFeedbackIsEmbedded);
        }
        builder().SetExpressionPosition(expr);

        BuildAssignment(lhs_data, expr.op(), expr.lookup_hoisting_mode());
        builder().Bind(short_circuit);
    }
}
