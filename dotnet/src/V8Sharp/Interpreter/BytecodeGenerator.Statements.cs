// Port of src/interpreter/bytecode-generator.cc: function bodies,
// declarations and statements.
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
    static bool NeedsContextInitialization(DeclarationScope scope) =>
        scope.NeedsContext() && !scope.is_script_scope() && !scope.is_module_scope();

    public void GenerateBytecode()
    {
        // Initialize the incoming context.
        using var incoming_context = new ContextScope(this, closure_scope());

        // Initialize control scope.
        using var control = new ControlScopeForTopLevel(this);

        using var register_scope = new RegisterAllocationScope(this);

        AllocateTopLevelRegisters();

        builder().EmitFunctionStartSourcePosition(info().literal().start_position());

        if (info().literal().CanSuspend())
        {
            BuildGeneratorPrologue();
        }

        if (NeedsContextInitialization(closure_scope()))
        {
            // Push a new inner context scope for the function.
            BuildNewLocalActivationContext();
            using var local_function_context = new ContextScope(this, closure_scope());
            BuildLocalActivationContextInitialization();
            GenerateBytecodeBody();
        }
        else
        {
            GenerateBytecodeBody();
        }

        // Reset variables with hole check bitmap indices for subsequent compilations
        // in the same parsing zone.
        foreach (Variable var in _varsInHoleCheckBitmap)
        {
            var.ResetHoleCheckBitmapIndex();
        }

        // Check that we are not falling off the end.
        Debug.Assert(HasStackOverflow() || builder().RemainderOfBlockIsDead());

        if (info().literal().CanSuspend())
        {
            BuildGeneratorEpilogue();
        }
    }

    void GenerateBytecodeBody()
    {
        GenerateBodyPrologue();

        if (IsBaseConstructor(function_kind()))
        {
            GenerateBaseConstructorBody();
        }
        else if (function_kind() == FunctionKind.DerivedConstructor)
        {
            GenerateDerivedConstructorBody();
        }
        else if (IsAsyncFunction(function_kind()) || IsModuleWithTopLevelAwait(function_kind()))
        {
            if (IsAsyncGeneratorFunction(function_kind()))
            {
                GenerateAsyncGeneratorFunctionBody();
            }
            else
            {
                GenerateAsyncFunctionBody();
            }
        }
        else
        {
            int start = 0;
            if (BuildInitializationBlockForParametersIfExist())
            {
                start = 1;
            }
            if (IsResumableFunction(info().literal().kind()))
            {
                BuildGeneratorObjectVariableInitialization();
            }

            GenerateBodyStatements(start);
        }
    }

    void GenerateBodyPrologue()
    {
        // Build the arguments object if it is used.
        VisitArgumentsObject(closure_scope().arguments());

        // Build rest arguments array if it is used.
        Variable? rest_parameter = closure_scope().rest_parameter();
        VisitRestArgumentsArray(rest_parameter);

        // Build assignment to the function name or {.this_function}
        // variables if used.
        VisitThisFunctionVariable(closure_scope().function_var());
        VisitThisFunctionVariable(closure_scope().this_function_var());

        // Build assignment to {new.target} variable if it is used.
        VisitNewTargetVariable(closure_scope().new_target_var());

        FunctionLiteral literal = info().literal();
        // Emit tracing call if requested to do so.
        if (v8_flags.trace) builder().CallRuntime(FunctionId.TraceEnter);

        // Increment the function-scope block coverage counter.
        BuildIncrementBlockCoverageCounterIfEnabled(literal, SourceRangeKind.kBody);

        // Visit declarations within the function scope.
        if (closure_scope().is_script_scope())
        {
            VisitGlobalDeclarations(closure_scope().declarations());
        }
        else if (closure_scope().is_module_scope())
        {
            VisitModuleDeclarations(closure_scope().declarations());
        }
        else
        {
            VisitDeclarations(closure_scope().declarations());
        }

        // Emit initializing assignments for module namespace imports (if any).
        VisitModuleNamespaceImports();
    }

    void GenerateBaseConstructorBody()
    {
        Debug.Assert(IsBaseConstructor(function_kind()));

        FunctionLiteral literal = info().literal();

        // The derived constructor case is handled in VisitCallSuper.
        if (literal.class_scope_has_private_brand())
        {
            ClassScope scope = info().scope().outer_scope()!.AsClassScope();
            Debug.Assert(scope.brand() is not null);
            BuildPrivateBrandInitialization(builder().Receiver(), scope.brand()!);
        }

        if (literal.requires_instance_members_initializer())
        {
            BuildInstanceMemberInitialization(Register.FunctionClosure(), builder().Receiver());
        }

        GenerateBodyStatements();
    }

    void GenerateDerivedConstructorBody()
    {
        Debug.Assert(function_kind() == FunctionKind.DerivedConstructor);

        FunctionLiteral literal = info().literal();

        // Per spec, derived constructors can only return undefined or an object;
        // other primitives trigger an exception in ConstructStub.
        //
        // Since the receiver is popped by the callee, derived constructors return
        // <this> if the original return value was undefined.
        //
        // Also per spec, this return value check is done after all user code (e.g.,
        // finally blocks) are executed. For example, the following code does not
        // throw.
        //
        //   class C extends class {} {
        //     constructor() {
        //       try { throw 42; }
        //       catch(e) { return; }
        //       finally { super(); }
        //     }
        //   }
        //   new C();
        //
        // This check is implemented by jumping to the check instead of emitting a
        // return bytecode in-place inside derived constructors.
        //
        // Note that default derived constructors do not need this check as they
        // just forward a super call.

        var check_return_value = new BytecodeLabels();
        Register result = register_allocator().NewRegister();
        using var control = new ControlScopeForDerivedConstructor(this, result, check_return_value);

        {
            using var elider = new HoleCheckElisionScope(this);
            GenerateBodyStatementsWithoutImplicitFinalReturn();
        }

        if (check_return_value.Empty)
        {
            if (!builder().RemainderOfBlockIsDead())
            {
                BuildThisVariableLoad();
                BuildReturn(literal.return_position());
            }
        }
        else
        {
            var return_this = new BytecodeLabels();

            if (!builder().RemainderOfBlockIsDead())
            {
                builder().Jump(return_this.New());
            }

            check_return_value.Bind(builder());
            builder().LoadAccumulatorWithRegister(result);
            builder().JumpIfUndefined(return_this.New());
            BuildReturn(literal.return_position());

            {
                return_this.Bind(builder());
                BuildThisVariableLoad();
                BuildReturn(literal.return_position());
            }
        }
    }

    void GenerateAsyncFunctionBody()
    {
        Debug.Assert((IsAsyncFunction(function_kind()) && !IsAsyncGeneratorFunction(function_kind())) ||
                     IsModuleWithTopLevelAwait(function_kind()));

        // Async functions always return promises. Return values fulfill that promise,
        // while synchronously thrown exceptions reject that promise. This is handled
        // by surrounding the body statements in a try-catch block as follows:
        //
        // try {
        //   <inner_block>
        // } catch (.catch) {
        //   return %_AsyncFunctionReject(.generator_object, .catch);
        // }

        FunctionLiteral literal = info().literal();
        BuildGeneratorObjectVariableInitialization();

        HandlerTable.CatchPrediction outer_catch_prediction = catch_prediction();
        // When compiling a REPL script, use UNCAUGHT_ASYNC_AWAIT to preserve the
        // pending message so DevTools can inspect it.
        set_catch_prediction(literal.scope().is_repl_mode_scope()
                                 ? HandlerTable.CatchPrediction.UNCAUGHT_ASYNC_AWAIT
                                 : HandlerTable.CatchPrediction.ASYNC_AWAIT);

        BuildTryCatch(
            () =>
            {
                GenerateBodyStatements();
                set_catch_prediction(outer_catch_prediction);
            },
            context =>
            {
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .MoveRegister(generator_object(), args[0])
                    .StoreAccumulatorInRegister(args[1]); // exception
                if (!literal.scope().is_repl_mode_scope())
                {
                    builder().LoadTheHole().SetPendingMessage();
                }
                builder().CallRuntime(FunctionId.InlineAsyncFunctionReject, args);
                // TODO(358404372): Should this return have a statement position?
                // Without one it is not possible to apply a debugger breakpoint.
                BuildReturn(kNoSourcePosition);
            },
            catch_prediction());
    }

    bool BuildInitializationBlockForParametersIfExist()
    {
        List<Statement> body = info().literal().body();
        if (body.Count > 0 && body[0].IsBlock())
        {
            Block block = body[0].AsBlock()!;
            if (block.is_initialization_block_for_parameters())
            {
                using var allocation_scope = new RegisterAllocationScope(this);
                VisitBlockDeclarationsAndStatements(block);
                return true;
            }
        }
        return false;
    }

    void GenerateAsyncGeneratorFunctionBody()
    {
        Debug.Assert(IsAsyncGeneratorFunction(function_kind()));
        set_catch_prediction(HandlerTable.CatchPrediction.ASYNC_AWAIT);

        // For ES2017 Async Generators, we produce:
        //
        // try {
        //   InitialYield;
        //   ...body...;
        // } catch (.catch) {
        //   %AsyncGeneratorReject(generator, .catch);
        // } finally {
        //   %_GeneratorClose(generator);
        // }
        //
        // - InitialYield yields the actual generator object.
        // - Any return statement inside the body will have its argument wrapped
        //   in an iterator result object with a "done" property set to `true`.
        // - If the generator terminates for whatever reason, we must close it.
        //   Hence the finally clause.
        // - BytecodeGenerator performs special handling for ReturnStatements in
        //   async generator functions, resolving the appropriate Promise with an
        //   "done" iterator result object containing a Promise-unwrapped value.

        // In async generator functions, when parameters are not simple,
        // a parameter initialization block will be added as the first block to the
        // AST. Since this block can throw synchronously, it should not be wrapped
        // in the following try-finally. We visit this block outside the try-finally
        // and remove it from the AST.
        int start = 0;
        if (BuildInitializationBlockForParametersIfExist())
        {
            start = 1;
        }
        BuildGeneratorObjectVariableInitialization();

        BuildTryFinally(
            () =>
            {
                BuildTryCatch(
                    () => GenerateBodyStatements(start),
                    context =>
                    {
                        using var register_scope = new RegisterAllocationScope(this);
                        RegisterList args = register_allocator().NewRegisterList(2);
                        builder()
                            .MoveRegister(generator_object(), args[0])
                            .StoreAccumulatorInRegister(args[1]) // exception
                            .LoadTheHole()
                            .SetPendingMessage()
                            .CallRuntime(FunctionId.InlineAsyncGeneratorReject, args);
                        execution_control().ReturnAccumulator(kNoSourcePosition);
                    },
                    catch_prediction());
            },
            (body_continuation_token, body_continuation_result, message) =>
            {
                using var register_scope = new RegisterAllocationScope(this);
                Register arg = register_allocator().NewRegister();
                builder()
                    .MoveRegister(generator_object(), arg)
                    .CallRuntime(FunctionId.InlineGeneratorClose, arg);
            },
            catch_prediction());
    }

    void GenerateBodyStatements(int start = 0)
    {
        GenerateBodyStatementsWithoutImplicitFinalReturn(start);

        // Emit an implicit return instruction in case control flow can fall off the
        // end of the function without an explicit return being present on all paths.
        //
        // ControlScope is used instead of building the Return bytecode directly, as
        // the entire body is wrapped in a try-finally block for async generators.
        if (!builder().RemainderOfBlockIsDead())
        {
            builder().LoadUndefined();
            int pos = info().literal().return_position();
            if (IsAsyncFunction(function_kind()) || IsModuleWithTopLevelAwait(function_kind()))
            {
                execution_control().AsyncReturnAccumulator(pos);
            }
            else
            {
                execution_control().ReturnAccumulator(pos);
            }
        }
    }

    void GenerateBodyStatementsWithoutImplicitFinalReturn(int start = 0)
    {
        List<Statement> body = info().literal().body();
        if (closure_scope() is not null &&
            (closure_scope().has_using_declaration() || closure_scope().has_await_using_declaration()))
        {
            BuildDisposeScope(() => VisitStatements(body, start), closure_scope().has_await_using_declaration());
        }
        else
        {
            VisitStatements(body, start);
        }
    }

    void AllocateTopLevelRegisters()
    {
        if (IsResumableFunction(info().literal().kind()))
        {
            // Either directly use generator_object_var or allocate a new register for
            // the incoming generator object.
            Variable generator_object_var = closure_scope().generator_object_var()!;
            if (generator_object_var.location() == VariableLocation.LOCAL)
            {
                _incomingNewTargetOrGenerator = GetRegisterForLocalVariable(generator_object_var);
            }
            else
            {
                _incomingNewTargetOrGenerator = register_allocator().NewRegister();
            }
        }
        else if (closure_scope().new_target_var() is not null)
        {
            // Either directly use new_target_var or allocate a new register for
            // the incoming new target object.
            Variable new_target_var = closure_scope().new_target_var()!;
            if (new_target_var.location() == VariableLocation.LOCAL)
            {
                _incomingNewTargetOrGenerator = GetRegisterForLocalVariable(new_target_var);
            }
            else
            {
                _incomingNewTargetOrGenerator = register_allocator().NewRegister();
            }
        }
    }

    void BuildGeneratorPrologue()
    {
        Debug.Assert(info().literal().suspend_count() > 0);
        _generatorJumpTable = builder().AllocateJumpTable(info().literal().suspend_count(), 0);

        // If the generator is not undefined, this is a resume, so perform state
        // dispatch.
        builder().SwitchOnGeneratorState(generator_object(), _generatorJumpTable);

        // Otherwise, fall-through to the ordinary function prologue, after which we
        // will run into the generator object creation and other extra code inserted
        // by the parser.
    }

    void BuildGeneratorEpilogue()
    {
        builder().TrimJumpTable(_generatorJumpTable!, _suspendCount);
        if (_suspendCount != _generatorJumpTable!.Size)
        {
            throw new InvalidOperationException("CHECK_EQ(suspend_count_, generator_jump_table_->size())");
        }
    }

    public override void VisitBlock(Block stmt)
    {
        // Visit declarations and statements.
        using var current_scope = new CurrentScope(this, stmt.scope());
        if (stmt.scope() is not null && stmt.scope()!.NeedsContext())
        {
            BuildNewLocalBlockContext(stmt.scope()!);
            using var scope = new ContextScope(this, stmt.scope()!);
            VisitBlockMaybeDispose(stmt);
        }
        else
        {
            VisitBlockMaybeDispose(stmt);
        }
    }

    void VisitBlockMaybeDispose(Block stmt)
    {
        if (stmt.scope() is not null &&
            (stmt.scope()!.has_using_declaration() || stmt.scope()!.has_await_using_declaration()))
        {
            BuildDisposeScope(() => VisitBlockDeclarationsAndStatements(stmt),
                              stmt.scope()!.has_await_using_declaration());
        }
        else
        {
            VisitBlockDeclarationsAndStatements(stmt);
        }
    }

    void VisitBlockDeclarationsAndStatements(Block stmt)
    {
        using var block_builder = new BlockBuilder(builder(), _blockCoverageBuilder, stmt);

        if (stmt.scope() is not null)
        {
            VisitDeclarations(stmt.scope()!.declarations());
        }
        if (stmt.is_breakable())
        {
            using var execution_control = new ControlScopeForBreakable(this, stmt, block_builder);
            {
                using var branch_elider = execution_control.merge_elider().NewBranch();
                VisitStatements(stmt.statements());
            }
            execution_control.merge_elider().Merge();
        }
        else
        {
            VisitStatements(stmt.statements());
        }
    }

    public override void VisitVariableDeclaration(VariableDeclaration decl)
    {
        Variable variable = decl.var()!;
        // Unused variables don't need to be visited.
        if (!variable.is_used()) return;

        switch (variable.location())
        {
            case VariableLocation.UNALLOCATED:
            case VariableLocation.MODULE:
                throw new UnreachableException();
            case VariableLocation.LOCAL:
                if (variable.binding_needs_init())
                {
                    Register destination = builder().Local(variable.index());
                    builder().LoadTdzHole().StoreAccumulatorInRegister(destination);
                }
                break;
            case VariableLocation.PARAMETER:
                if (variable.binding_needs_init())
                {
                    Register destination = builder().Parameter(variable.index());
                    builder().LoadTdzHole().StoreAccumulatorInRegister(destination);
                }
                break;
            case VariableLocation.REPL_GLOBAL:
            // REPL let's are stored in script contexts. They get initialized
            // with the hole the same way as normal context allocated variables.
            case VariableLocation.CONTEXT:
                if (variable.binding_needs_init())
                {
                    Debug.Assert(execution_context().ContextChainDepth(variable.scope()!) == 0);
                    builder().LoadTdzHole().StoreContextSlot(execution_context().reg(), variable, 0);
                }
                break;
            case VariableLocation.LOOKUP:
            {
                Debug.Assert(variable.mode() == VariableMode.Dynamic);
                Debug.Assert(!variable.binding_needs_init());

                Register name = register_allocator().NewRegister();

                builder()
                    .LoadLiteral(variable.raw_name())
                    .StoreAccumulatorInRegister(name)
                    .CallRuntime(FunctionId.DeclareEvalVar, name);
                break;
            }
        }
    }

    public override void VisitFunctionDeclaration(FunctionDeclaration decl)
    {
        Variable variable = decl.var()!;
        Debug.Assert(variable.mode() == VariableMode.Let || variable.mode() == VariableMode.Var ||
                     variable.mode() == VariableMode.Dynamic);
        // Unused variables don't need to be visited.
        if (!variable.is_used()) return;

        switch (variable.location())
        {
            case VariableLocation.UNALLOCATED:
            case VariableLocation.MODULE:
                throw new UnreachableException();
            case VariableLocation.PARAMETER:
            case VariableLocation.LOCAL:
            {
                VisitFunctionLiteral(decl.fun());
                BuildVariableAssignment(variable, Token.Init, HoleCheckMode.kElided);
                break;
            }
            case VariableLocation.REPL_GLOBAL:
            case VariableLocation.CONTEXT:
            {
                Debug.Assert(execution_context().ContextChainDepth(variable.scope()!) == 0);
                VisitFunctionLiteral(decl.fun());
                builder().StoreContextSlot(execution_context().reg(), variable, 0);
                break;
            }
            case VariableLocation.LOOKUP:
            {
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .LoadLiteral(variable.raw_name())
                    .StoreAccumulatorInRegister(args[0]);
                VisitFunctionLiteral(decl.fun());
                builder().StoreAccumulatorInRegister(args[1]).CallRuntime(FunctionId.DeclareEvalFunction, args);
                break;
            }
        }
    }

    void VisitModuleNamespaceImports()
    {
        if (!closure_scope().is_module_scope()) return;

        using var register_scope = new RegisterAllocationScope(this);
        Register module_request = register_allocator().NewRegister();

        SourceTextModuleDescriptor descriptor = closure_scope().AsModuleScope().module()!;
        foreach (KeyValuePair<AstRawString, SourceTextModuleDescriptor.Entry> entry in descriptor.namespace_imports())
        {
            Variable var = closure_scope().LookupInModule(entry.Key);
            if (!var.is_used()) continue;
            builder()
                .LoadLiteral(Smi.FromInt(entry.Value.module_request))
                .StoreAccumulatorInRegister(module_request)
                .CallRuntime(FunctionId.GetModuleNamespace, module_request);
            BuildVariableAssignment(var, Token.Init, HoleCheckMode.kElided);
        }
    }

    void BuildDeclareCall(FunctionId id)
    {
        if (!top_level_builder().has_top_level_declaration()) return;
        Debug.Assert(!top_level_builder().processed());

        top_level_builder().set_constant_pool_entry(builder().AllocateDeferredConstantPoolEntry());

        // Emit code to declare globals.
        RegisterList args = register_allocator().NewRegisterList(2);
        builder()
            .LoadConstantPoolEntry(top_level_builder().constant_pool_entry())
            .StoreAccumulatorInRegister(args[0])
            .MoveRegister(Register.FunctionClosure(), args[1])
            .CallRuntime(id, args);

        top_level_builder().mark_processed();
    }

    void VisitModuleDeclarations(ThreadedList<Declaration> decls)
    {
        using var register_scope = new RegisterAllocationScope(this);
        foreach (Declaration decl in decls)
        {
            Variable var = decl.var()!;
            if (!var.is_used()) continue;
            if (var.location() == VariableLocation.MODULE)
            {
                if (decl.IsFunctionDeclaration())
                {
                    Debug.Assert(var.IsExport());
                    var f = (FunctionDeclaration)decl;
                    AddToEagerLiteralsIfEager(f.fun());
                    top_level_builder().record_module_function_declaration();
                }
                else if (var.IsExport() && var.binding_needs_init())
                {
                    Debug.Assert(decl.IsVariableDeclaration());
                    top_level_builder().record_module_variable_declaration();
                }
            }
            else
            {
                using var inner_register_scope = new RegisterAllocationScope(this);
                Visit(decl);
            }
        }
        BuildDeclareCall(FunctionId.DeclareModuleExports);
    }

    void VisitGlobalDeclarations(ThreadedList<Declaration> decls)
    {
        using var register_scope = new RegisterAllocationScope(this);
        foreach (Declaration decl in decls)
        {
            Variable var = decl.var()!;
            Debug.Assert(var.is_used());
            if (var.location() == VariableLocation.UNALLOCATED)
            {
                // var or function.
                if (decl.IsFunctionDeclaration())
                {
                    top_level_builder().record_global_function_declaration();
                    var f = (FunctionDeclaration)decl;
                    AddToEagerLiteralsIfEager(f.fun());
                }
                else
                {
                    top_level_builder().record_global_variable_declaration();
                }
            }
            else
            {
                // let or const. Handled in NewScriptContext.
                Debug.Assert(decl.IsVariableDeclaration());
                Debug.Assert(IsLexicalVariableMode(var.mode()));
            }
        }

        BuildDeclareCall(FunctionId.DeclareGlobals);
    }

    new void VisitDeclarations(ThreadedList<Declaration> declarations)
    {
        foreach (Declaration decl in declarations)
        {
            using var register_scope = new RegisterAllocationScope(this);
            Visit(decl);
        }
    }

    sealed class PrototypeAssignments(Variable var, HoleCheckMode hole_check_mode)
    {
        public readonly Variable var = var;
        public readonly HoleCheckMode hole_check_mode = hole_check_mode;
        public readonly List<PrototypeAssignment> properties = new(8);
        public readonly HashSet<AstRawString> duplicates = new(ReferenceEqualityComparer.Instance);
    }

    // Helper to match a sequence of statements of the form:
    //   <Var>.prototype.<key> = <literal|function literal>
    // The first one in the sequence will assign <var>.
    // The subsequent ones must be about the same <var> to return true.
    bool IsPrototypeAssignment(Statement stmt, ref PrototypeAssignments? assignments)
    {
        // The expression Statement is an assignment
        // ========================================
        ExpressionStatement? expr_stmt = stmt.AsExpressionStatement();
        if (expr_stmt is null)
        {
            return false;
        }
        Assignment? assign = expr_stmt.expression().AsAssignment();
        if (assign is null)
        {
            return false;
        }

        AstNode target = assign.target();
        Expression value = assign.value();

        if (assign.op() != Token.Assign)
        {
            return false;
        }

        // The value (RHS) is something reasonable
        // =======================================
        if (!value.IsLiteral() && !value.IsFunctionLiteral())
        {
            return false;
        }

        // The target (LHS) is a property
        // ==============================
        if (!target.IsProperty())
        {
            return false;
        }
        Property prop = target.AsProperty()!;

        if (!prop.key().IsPropertyName())
        {
            return false;
        }

        AstRawString prop_str = prop.key().AsLiteral()!.AsRawString();
        if (prop_str == ast_string_constants().proto_string ||
            prop_str == ast_string_constants().constructor_string)
        {
            return false;
        }

        // The target Object is the "prototype" property
        // =============================================
        if (!prop.obj().IsProperty())
        {
            return false;
        }
        Property proto_prop = prop.obj().AsProperty()!;

        if (!proto_prop.key().IsStringLiteral() ||
            (ast_string_constants().prototype_string != proto_prop.key().AsLiteral()!.AsRawString()))
        {
            return false;
        }

        // Immediately on the left of "prototype" should be the leftmost object
        // ====================================================================
        if (!proto_prop.obj().IsVariableProxy())
        {
            return false;
        }

        Variable tmp_var = proto_prop.obj().AsVariableProxy()!.var();
        VariableLocation loc = tmp_var.location();
        if (loc != VariableLocation.PARAMETER && loc != VariableLocation.LOCAL &&
            !(loc == VariableLocation.CONTEXT && tmp_var.maybe_assigned() == MaybeAssignedFlag.kNotAssigned))
        {
            return false;
        }

        if (assignments is null)
        {
            // This is the first prototype assignment in the sequence.
            assignments = new PrototypeAssignments(tmp_var, proto_prop.obj().AsVariableProxy()!.hole_check_mode());
            assignments.duplicates.Add(prop_str);
        }
        else
        {
            if (!assignments.duplicates.Add(prop_str)) return false;
            if (assignments.var != tmp_var)
            {
                // This prototype assignment is about another var.
                return false;
            }
            Debug.Assert(assignments.hole_check_mode == proto_prop.obj().AsVariableProxy()!.hole_check_mode());
        }

        // Success!
        assignments.properties.Add(new PrototypeAssignment(prop_str, value)); // This will be reused as part of an ObjectLiteral.

        return true;
    }

    void VisitConsecutivePrototypeAssignments(PrototypeAssignments assignments)
    {
        // Create a boiler plate object in the constant pool to be merged into the
        // proto.
        int entry = builder().AllocateDeferredConstantPoolEntry();
        var seq = new ProtoAssignmentSeqBuilder(assignments.properties);
        _protoAssignSeq.Add((seq, entry));
        List<PrototypeAssignment> props = seq.properties();

        int first_idx = -1;
        foreach (PrototypeAssignment p in props)
        {
            FunctionLiteral? func = p.Value.AsFunctionLiteral();
            if (func is not null)
            {
                int idx = GetNewClosureSlot(func);
                Debug.Assert(idx != -1);
                if (first_idx == -1)
                {
                    first_idx = idx;
                }
                AddToEagerLiteralsIfEager(func);
            }
        }

        // We need {first_idx} to be valid, even if it's unused.
        if (first_idx == -1)
        {
            first_idx = 0;
        }
        // Load the variable whose prototype is to be set into the Accumulator.
        BuildVariableLoad(assignments.var, assignments.hole_check_mode);
        // Merge in-place proto-def boilerplate object into the Accumulator.
        builder().SetPrototypeProperties(entry, first_idx);
    }

    void VisitStatements(List<Statement> statements, int start = 0)
    {
        if (builder().RemainderOfBlockIsDead()) return;

        for (int stmt_idx = start; stmt_idx < statements.Count; stmt_idx++)
        {
            if (v8_flags.proto_assign_seq_opt)
            {
                int proto_assign_idx = stmt_idx;
                PrototypeAssignments? assignments = null;
                while (proto_assign_idx < statements.Count &&
                       IsPrototypeAssignment(statements[proto_assign_idx], ref assignments))
                {
                    ++proto_assign_idx;
                }

                int num_assignments = proto_assign_idx - stmt_idx;
                if (num_assignments >= (int)v8_flags.proto_assign_seq_opt_count)
                {
                    Debug.Assert(num_assignments == assignments!.properties.Count);
                    VisitConsecutivePrototypeAssignments(assignments);
                    stmt_idx = proto_assign_idx - 1; // The outer loop should now ignore
                                                     // these statements.
                    Debug.Assert(!builder().RemainderOfBlockIsDead());
                    continue;
                }
            }

            // Allocate an outer register allocations scope for the statement.
            Statement stmt = statements[stmt_idx];
            using var allocation_scope = new RegisterAllocationScope(this);
            Visit(stmt);
            if (builder().RemainderOfBlockIsDead()) break;
        }
    }

    public override void VisitExpressionStatement(ExpressionStatement stmt)
    {
        builder().SetStatementPosition(stmt);
        VisitForEffect(stmt.expression());
    }

    public override void VisitEmptyStatement(EmptyStatement stmt) { }

    public override void VisitIfStatement(IfStatement stmt)
    {
        using var conditional_builder =
            new ConditionalControlFlowBuilder(builder(), _blockCoverageBuilder, stmt, nodeIsIfStatement: true);
        builder().SetStatementPosition(stmt);

        if (stmt.condition().ToBooleanIsTrue())
        {
            // Generate then block unconditionally as always true.
            conditional_builder.Then();
            Visit(stmt.then_statement());
        }
        else if (stmt.condition().ToBooleanIsFalse())
        {
            // Generate else block unconditionally if it exists.
            if (stmt.HasElseStatement())
            {
                conditional_builder.Else();
                Visit(stmt.else_statement());
            }
        }
        else
        {
            // TODO(oth): If then statement is BreakStatement or
            // ContinueStatement we can reduce number of generated
            // jump/jump_ifs here. See BasicLoops test.
            VisitForTest(stmt.condition(), conditional_builder.ThenLabels, conditional_builder.ElseLabels,
                         TestFallthrough.kThen);

            var merge_elider = new HoleCheckElisionMergeScope(this);
            {
                using var branch = merge_elider.NewBranch();
                conditional_builder.Then();
                Visit(stmt.then_statement());
            }

            {
                using var branch = merge_elider.NewBranch();
                if (stmt.HasElseStatement())
                {
                    conditional_builder.JumpToEnd();
                    conditional_builder.Else();
                    Visit(stmt.else_statement());
                }
            }

            merge_elider.Merge();
        }
    }

    public override void VisitSloppyBlockFunctionStatement(SloppyBlockFunctionStatement stmt) =>
        Visit(stmt.statement());

    public override void VisitContinueStatement(ContinueStatement stmt)
    {
        AllocateBlockCoverageSlotIfEnabled(stmt, SourceRangeKind.kContinuation);
        builder().SetStatementPosition(stmt);
        execution_control().Continue(stmt.target());
    }

    public override void VisitBreakStatement(BreakStatement stmt)
    {
        AllocateBlockCoverageSlotIfEnabled(stmt, SourceRangeKind.kContinuation);
        builder().SetStatementPosition(stmt);
        execution_control().Break(stmt.target());
    }

    public override void VisitReturnStatement(ReturnStatement stmt)
    {
        AllocateBlockCoverageSlotIfEnabled(stmt, SourceRangeKind.kContinuation);
        builder().SetStatementPosition(stmt);
        VisitForAccumulatorValue(stmt.expression());
        int return_position = stmt.end_position();
        if (return_position == ReturnStatement.kFunctionLiteralReturnPosition)
        {
            return_position = info().literal().return_position();
        }
        if (stmt.is_async_return())
        {
            execution_control().AsyncReturnAccumulator(return_position);
        }
        else
        {
            execution_control().ReturnAccumulator(return_position);
        }
    }

    public override void VisitWithStatement(WithStatement stmt)
    {
        builder().SetStatementPosition(stmt);
        VisitForAccumulatorValue(stmt.expression());
        BuildNewLocalWithContext(stmt.scope());
        VisitInScope(stmt.statement(), stmt.scope());
    }

    static int? TryReduceToSmiSwitchCaseValue(Expression expr)
    {
        if (expr.IsSmiLiteral())
        {
            return expr.AsLiteral()!.AsSmiLiteral();
        }
        if (expr.IsLiteral() && expr.AsLiteral()!.IsNumber() && expr.AsLiteral()!.AsNumber() == 0.0)
        {
            return 0;
        }
        return null;
    }

    // Is the range of Smi's small enough relative to number of cases?
    bool IsSpreadAcceptable(int spread, int ncases) => spread < v8_flags.switch_table_spread_threshold * ncases;

    sealed class SwitchInfo
    {
        public const int kDefaultNotFound = -1;

        public readonly SortedDictionary<int, object> covered_cases = [];
        public int default_case = kDefaultNotFound;

        public bool DefaultExists() => default_case != kDefaultNotFound;
        public bool CaseExists(int j) => covered_cases.ContainsKey(j);

        public bool CaseExists(Expression expr)
        {
            int? j = TryReduceToSmiSwitchCaseValue(expr);
            if (j is null) return false;
            return CaseExists(j.Value);
        }

        // Returns true if this clause represents a case value, but there already
        // exists another clause with the same case value.
        public bool IsDuplicate(CaseClause clause)
        {
            int? j = TryReduceToSmiSwitchCaseValue(clause.label());
            if (j is null) return false;
            if (!covered_cases.TryGetValue(j.Value, out object? it)) return false;
            return it != clause;
        }

        public int MinCase()
        {
            Debug.Assert(covered_cases.Count > 0);
            foreach (int k in covered_cases.Keys) return k;
            throw new UnreachableException();
        }

        public int MaxCase()
        {
            Debug.Assert(covered_cases.Count > 0);
            int max = 0;
            foreach (int k in covered_cases.Keys) max = k;
            return max;
        }
    }

    // Checks whether we should use a jump table to implement a switch operation.
    bool IsSwitchOptimizable(SwitchStatement stmt, SwitchInfo info)
    {
        List<CaseClause> cases = stmt.cases();

        for (int i = 0; i < cases.Count; ++i)
        {
            CaseClause clause = cases[i];
            if (clause.is_default())
            {
                continue;
            }
            else if (!clause.label().IsLiteral())
            {
                // Don't consider Smi cases after a non-literal, because we
                // need to evaluate the non-literal.
                break;
            }
            else if (TryReduceToSmiSwitchCaseValue(clause.label()) is int maybe_value)
            {
                // std::map::insert: an existing entry is kept.
                info.covered_cases.TryAdd(maybe_value, clause);
            }
        }

        // This flag is not allowed to be <= 0.
        Debug.Assert(v8_flags.switch_table_min_cases > 0);

        // GCC also jump-table optimizes switch statements with 6 cases or more.
        if (info.covered_cases.Count >= v8_flags.switch_table_min_cases)
        {
            // Due to case spread will be used as the size of jump-table,
            // we need to check if it doesn't overflow by casting its
            // min and max bounds to int64_t, and calculate if the difference is less
            // than or equal to INT_MAX.
            long min = info.MinCase();
            long max = info.MaxCase();
            long spread = max - min + 1;

            Debug.Assert(spread > 0);

            // Check if casted spread is acceptable and doesn't overflow.
            if (spread <= int.MaxValue && IsSpreadAcceptable((int)spread, cases.Count))
            {
                return true;
            }
        }
        // Invariant- covered_cases has all cases and only cases that will go in the
        // jump table.
        info.covered_cases.Clear();
        return false;
    }

    // This adds a jump table optimization for switch statements with Smi cases.
    // If there are 5+ non-duplicate Smi clauses, and they are sufficiently compact,
    // we generate a jump table. In the fall-through path, we put the compare-jumps
    // for the non-Smi cases.
    public override void VisitSwitchStatement(SwitchStatement stmt)
    {
        // We need this scope because we visit for register values. We have to
        // maintain an execution result scope where registers can be allocated.
        List<CaseClause> clauses = stmt.cases();

        var info = new SwitchInfo();
        BytecodeJumpTable? jump_table = null;
        bool use_jump_table = IsSwitchOptimizable(stmt, info);

        // N_comp_cases is number of cases we will generate comparison jumps for.
        // Note we ignore duplicate cases, since they are very unlikely.

        int n_comp_cases = clauses.Count;
        if (use_jump_table)
        {
            n_comp_cases -= info.covered_cases.Count;
            jump_table = builder().AllocateJumpTable(info.MaxCase() - info.MinCase() + 1, info.MinCase());
        }

        // Are we still using any if-else bytecodes to evaluate the switch?
        bool use_jumps = n_comp_cases != 0;

        // Does the comparison for non-jump table jumps need an elision scope?
        bool jump_comparison_needs_hole_check_elision_scope = false;

        using var switch_builder = new SwitchBuilder(builder(), _blockCoverageBuilder, stmt, n_comp_cases, jump_table);
        using var scope = new ControlScopeForBreakable(this, stmt, switch_builder);
        builder().SetStatementPosition(stmt);

        VisitForAccumulatorValue(stmt.tag());

        if (use_jump_table)
        {
            // Release temps so that they can be reused in clauses.
            using var allocation_scope = new RegisterAllocationScope(this);
            // This also fills empty slots in jump table.
            Register r2 = register_allocator().NewRegister();

            Register r1 = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(r1);

            builder().CompareTypeOf(TestTypeOfFlags.LiteralFlag.Number);
            switch_builder.JumpToFallThroughIfFalse();
            builder().LoadAccumulatorWithRegister(r1);

            // TODO(leszeks): Note these are duplicated range checks with the
            // SwitchOnSmi handler for the most part.

            builder().LoadLiteral(Smi.FromInt(Smi.kMinValue));
            builder().StoreAccumulatorInRegister(r2);
            builder().CompareOperation(Token.GreaterThanEq, r1, kFeedbackIsEmbedded);

            switch_builder.JumpToFallThroughIfFalse();
            builder().LoadAccumulatorWithRegister(r1);

            builder().LoadLiteral(Smi.FromInt(Smi.kMaxValue));
            builder().StoreAccumulatorInRegister(r2);
            builder().CompareOperation(Token.LessThanEq, r1, kFeedbackIsEmbedded);

            switch_builder.JumpToFallThroughIfFalse();
            builder().LoadAccumulatorWithRegister(r1);

            builder().BinaryOperationSmiLiteral(Token.BitOr, Smi.FromInt(0), kFeedbackIsEmbedded);

            builder().StoreAccumulatorInRegister(r2);
            builder().CompareOperation(Token.EqStrict, r1, kFeedbackIsEmbedded);

            switch_builder.JumpToFallThroughIfFalse();
            builder().LoadAccumulatorWithRegister(r2);

            switch_builder.EmitJumpTableIfExists(info.MinCase(), info.MaxCase(), info.covered_cases);

            if (use_jumps)
            {
                // When using a jump table, the first jump comparison is conditionally
                // executed if the discriminant wasn't matched by anything in the jump
                // table, and so needs its own elision scope.
                jump_comparison_needs_hole_check_elision_scope = true;
                builder().LoadAccumulatorWithRegister(r1);
            }
        }

        int case_compare_ctr = 0;

        if (use_jumps)
        {
            Register tag_holder = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(tag_holder);

            {
                // The comparisons linearly dominate, so no need to open a new elision
                // scope for each one.
                bool has_elider = false;
                ulong elider_prev = 0;
                for (int i = 0; i < clauses.Count; ++i)
                {
                    CaseClause clause = clauses[i];
                    if (clause.is_default())
                    {
                        info.default_case = i;
                    }
                    else if (!info.CaseExists(clause.label()))
                    {
                        if (jump_comparison_needs_hole_check_elision_scope && !has_elider)
                        {
                            // elider.emplace(this);
                            has_elider = true;
                            elider_prev = _holeCheckBitmap;
                        }

                        // Perform label comparison as if via '===' with tag.
                        VisitForAccumulatorValue(clause.label());
                        builder().CompareOperation(Token.EqStrict, tag_holder, kFeedbackIsEmbedded);
                        switch_builder.JumpToCaseIfTrue(ToBooleanMode.AlreadyBoolean, case_compare_ctr++);
                        // The second and subsequent non-default comparisons are always
                        // conditionally executed, and need an elision scope.
                        jump_comparison_needs_hole_check_elision_scope = true;
                    }
                }
                if (has_elider) _holeCheckBitmap = elider_prev;
            }
            register_allocator().ReleaseRegister(tag_holder);
        }

        // For fall-throughs after comparisons (or out-of-range/non-Smi's for jump
        // tables).
        if (info.DefaultExists())
        {
            switch_builder.JumpToDefault();
        }
        else
        {
            switch_builder.Break();
        }

        case_compare_ctr = 0;
        for (int i = 0; i < clauses.Count; ++i)
        {
            CaseClause clause = clauses[i];
            if (i != info.default_case)
            {
                if (!info.IsDuplicate(clause))
                {
                    bool use_table = use_jump_table && info.CaseExists(clause.label());
                    if (!use_table)
                    {
                        // Guarantee that we should generate compare/jump if no table.
                        switch_builder.BindCaseTargetForCompareJump(case_compare_ctr++, clause);
                    }
                    else
                    {
                        // Use jump table if this is not a duplicate label.
                        switch_builder.BindCaseTargetForJumpTable(TryReduceToSmiSwitchCaseValue(clause.label())!.Value,
                                                                  clause);
                    }
                }
            }
            else
            {
                switch_builder.BindDefault(clause);
            }
            // Regardless, generate code (in case of fall throughs).
            using var branch_elider = scope.merge_elider().NewBranch();
            VisitStatements(clause.statements());
        }

        scope.merge_elider().MergeIf(info.DefaultExists());
    }

    void BuildTryCatch(Action try_body_func, Action<Register> catch_body_func,
                       HandlerTable.CatchPrediction catch_prediction, TryCatchStatement? stmt_for_coverage = null)
    {
        if (builder().RemainderOfBlockIsDead()) return;

        using var try_control_builder = new TryCatchBuilder(
            builder(), stmt_for_coverage is null ? null : _blockCoverageBuilder, stmt_for_coverage, catch_prediction);

        // Preserve the context in a dedicated register, so that it can be restored
        // when the handler is entered by the stack-unwinding machinery.
        // TODO(ignition): Be smarter about register allocation.
        Register context = register_allocator().NewRegister();
        builder().MoveRegister(Register.CurrentContext(), context);

        // Evaluate the try-block inside a control scope. This simulates a handler
        // that is intercepting 'throw' control commands.
        try_control_builder.BeginTry(context);

        var throw_tracker = new ThrowTrackingScope(builder());

        var merge_elider = new HoleCheckElisionMergeScope(this);

        {
            using var scope = new ControlScopeForTryCatch(this, try_control_builder);
            // The try-block itself, even though unconditionally executed, can throw
            // basically at any point, and so must be treated as conditional from the
            // perspective of the hole check elision analysis.
            //
            // try { x } catch (e) { }
            // use(x); <-- Still requires a TDZ check
            //
            // However, if both the try-block and the catch-block emit a hole check,
            // subsequent TDZ checks can be elided.
            //
            // try { x; } catch (e) { x; }
            // use(x); <-- TDZ check can be elided
            using var branch_elider = merge_elider.NewBranch();
            try_body_func();
        }

        bool emit_catch = throw_tracker.HasEmittedThrowingBytecode() || _blockCoverageBuilder is not null;
        try_control_builder.EndTry(emit_catch);

        if (emit_catch)
        {
            using var branch_elider = merge_elider.NewBranch();
            catch_body_func(context);
            try_control_builder.EndCatch();
        }

        merge_elider.Merge();
    }

    void BuildTryFinally(Action try_body_func, Action<Register, Register, Register> finally_body_func,
                         HandlerTable.CatchPrediction catch_prediction, TryFinallyStatement? stmt_for_coverage = null)
    {
        if (builder().RemainderOfBlockIsDead()) return;

        // We can't know whether the finally block will override ("catch") an
        // exception thrown in the try block, so we just adopt the outer prediction.
        using var try_control_builder = new TryFinallyBuilder(
            builder(), stmt_for_coverage is null ? null : _blockCoverageBuilder, stmt_for_coverage, catch_prediction);

        // We keep a record of all paths that enter the finally-block to be able to
        // dispatch to the correct continuation point after the statements in the
        // finally-block have been evaluated.
        //
        // The try-finally construct can enter the finally-block in three ways:
        // 1. By exiting the try-block normally, falling through at the end.
        // 2. By exiting the try-block with a function-local control flow transfer
        //    (i.e. through break/continue/return statements).
        // 3. By exiting the try-block with a thrown exception.
        //
        // The result register semantics depend on how the block was entered:
        //  - ReturnStatement: It represents the return value being returned.
        //  - ThrowStatement: It represents the exception being thrown.
        //  - BreakStatement/ContinueStatement: Undefined and not used.
        //  - Falling through into finally-block: Undefined and not used.
        Register token = register_allocator().NewRegister();
        Register result = register_allocator().NewRegister();
        Register message = register_allocator().NewRegister();
        builder().LoadTheHole().StoreAccumulatorInRegister(message);
        var commands = new DeferredCommands(this, token, result, message);

        // Preserve the context in a dedicated register, so that it can be restored
        // when the handler is entered by the stack-unwinding machinery.
        // TODO(ignition): Be smarter about register allocation.
        Register context = register_allocator().NewRegister();
        builder().MoveRegister(Register.CurrentContext(), context);

        // Evaluate the try-block inside a control scope. This simulates a handler
        // that is intercepting all control commands.
        try_control_builder.BeginTry(context);
        {
            using var scope = new ControlScopeForTryFinally(this, try_control_builder, commands);
            // The try-block itself, even though unconditionally executed, can throw
            // basically at any point, and so must be treated as conditional from the
            // perspective of the hole check elision analysis.
            using var elider = new HoleCheckElisionScope(this);
            try_body_func();
        }
        try_control_builder.EndTry();

        // Record fall-through and exception cases.
        if (!builder().RemainderOfBlockIsDead())
        {
            commands.RecordFallThroughPath();
        }
        try_control_builder.LeaveTry();
        try_control_builder.BeginHandler();
        commands.RecordHandlerReThrowPath();

        try_control_builder.BeginFinally();

        // Evaluate the finally-block.
        finally_body_func(token, result, message);
        try_control_builder.EndFinally();

        // Dynamic dispatch after the finally-block.
        commands.ApplyDeferredCommands();
    }

    void BuildDisposeScope(Action wrapped_func, bool has_await_using)
    {
        using var allocation_scope = new RegisterAllocationScope(this);
        using var disposables_stack_scope = new DisposablesStackScope(this);
        if (has_await_using)
        {
            set_catch_prediction(info().scope().is_repl_mode_scope()
                                     ? HandlerTable.CatchPrediction.UNCAUGHT_ASYNC_AWAIT
                                     : HandlerTable.CatchPrediction.ASYNC_AWAIT);
        }

        BuildTryFinally(
            // Try block
            wrapped_func,
            // Finally block
            (body_continuation_token, body_continuation_result, message) =>
            {
                if (has_await_using)
                {
                    Register result_register = register_allocator().NewRegister();
                    Register disposable_stack_register = register_allocator().NewRegister();
                    builder().MoveRegister(current_disposables_stack(), disposable_stack_register);
                    using var loop_builder = new LoopBuilder(builder(), null, null, kNoSourcePosition, feedback_spec());
                    using var loop_scope = new LoopScope(this, loop_builder);

                    {
                        using var inner_allocation_scope = new RegisterAllocationScope(this);
                        RegisterList args = register_allocator().NewRegisterList(5);
                        builder()
                            .MoveRegister(disposable_stack_register, args[0])
                            .MoveRegister(body_continuation_token, args[1])
                            .MoveRegister(body_continuation_result, args[2])
                            .MoveRegister(message, args[3])
                            .LoadLiteral(Smi.FromInt(kAtLeastOneAsync))
                            .StoreAccumulatorInRegister(args[4]);
                        builder().CallRuntime(FunctionId.DisposeDisposableStack, args);
                    }

                    builder()
                        .StoreAccumulatorInRegister(result_register)
                        .LoadTrue()
                        .CompareReference(result_register);

                    loop_builder.BreakIfTrue(ToBooleanMode.ConvertToBoolean);

                    builder().LoadAccumulatorWithRegister(result_register);
                    BuildTryCatch(
                        () => BuildAwait(),
                        context =>
                        {
                            RegisterList args = register_allocator().NewRegisterList(3);
                            builder()
                                .MoveRegister(current_disposables_stack(), args[0])
                                .StoreAccumulatorInRegister(args[1]) // exception
                                .LoadTheHole()
                                .SetPendingMessage()
                                .StoreAccumulatorInRegister(args[2])
                                .CallRuntime(FunctionId.HandleExceptionsInDisposeDisposableStack, args);

                            builder().StoreAccumulatorInRegister(disposable_stack_register);
                        },
                        catch_prediction());

                    loop_builder.BindContinueTarget();
                }
                else
                {
                    RegisterList args = register_allocator().NewRegisterList(5);
                    builder()
                        .MoveRegister(current_disposables_stack(), args[0])
                        .MoveRegister(body_continuation_token, args[1])
                        .MoveRegister(body_continuation_result, args[2])
                        .MoveRegister(message, args[3])
                        .LoadLiteral(Smi.FromInt(kAllSync))
                        .StoreAccumulatorInRegister(args[4]);
                    builder().CallRuntime(FunctionId.DisposeDisposableStack, args);
                }
            },
            catch_prediction());
    }

    void VisitIterationBody(IterationStatement stmt, LoopBuilder loop_builder)
    {
        loop_builder.LoopBody();
        using var execution_control = new ControlScopeForIteration(this, stmt, loop_builder);
        {
            using var branch_elider = execution_control.merge_elider().NewBranch();
            Visit(stmt.body());
        }
        execution_control.merge_elider().Merge();
        loop_builder.BindContinueTarget();
    }

    void VisitIterationBodyInHoleCheckElisionScope(IterationStatement stmt, LoopBuilder loop_builder)
    {
        using var elider = new HoleCheckElisionScope(this);
        VisitIterationBody(stmt, loop_builder);
    }

    LoopBuilder NewLoopBuilder(IterationStatement? stmt) =>
        new(builder(), stmt is null ? null : _blockCoverageBuilder, stmt,
            stmt?.position() ?? kNoSourcePosition, feedback_spec());

    public override void VisitDoWhileStatement(DoWhileStatement stmt)
    {
        using LoopBuilder loop_builder = NewLoopBuilder(stmt);
        if (stmt.cond().ToBooleanIsFalse())
        {
            // Since we know that the condition is false, we don't create a loop.
            // Therefore, we don't create a LoopScope (and thus we don't create a header
            // and a JumpToHeader). However, we still need to iterate once through the
            // body.
            VisitIterationBodyInHoleCheckElisionScope(stmt, loop_builder);
        }
        else if (stmt.cond().ToBooleanIsTrue())
        {
            using var loop_scope = new LoopScope(this, loop_builder);
            VisitIterationBodyInHoleCheckElisionScope(stmt, loop_builder);
        }
        else
        {
            using var loop_scope = new LoopScope(this, loop_builder);
            VisitIterationBodyInHoleCheckElisionScope(stmt, loop_builder);
            builder().SetExpressionAsStatementPosition(stmt.cond());
            var loop_backbranch = new BytecodeLabels();
            if (!loop_builder.BreakLabels.Empty)
            {
                // The test may be conditionally executed if there was a break statement
                // inside the loop body, and therefore requires its own elision scope.
                using var elider = new HoleCheckElisionScope(this);
                VisitForTest(stmt.cond(), loop_backbranch, loop_builder.BreakLabels, TestFallthrough.kThen);
            }
            else
            {
                VisitForTest(stmt.cond(), loop_backbranch, loop_builder.BreakLabels, TestFallthrough.kThen);
            }
            loop_backbranch.Bind(builder());
        }
    }

    public override void VisitWhileStatement(WhileStatement stmt)
    {
        using LoopBuilder loop_builder = NewLoopBuilder(stmt);

        if (stmt.cond().ToBooleanIsFalse())
        {
            // If the condition is false there is no need to generate the loop.
            return;
        }

        using var loop_scope = new LoopScope(this, loop_builder);
        if (!stmt.cond().ToBooleanIsTrue())
        {
            builder().SetExpressionAsStatementPosition(stmt.cond());
            var loop_body = new BytecodeLabels();
            VisitForTest(stmt.cond(), loop_body, loop_builder.BreakLabels, TestFallthrough.kThen);
            loop_body.Bind(builder());
        }
        VisitIterationBodyInHoleCheckElisionScope(stmt, loop_builder);
    }

    public override void VisitForStatement(ForStatement stmt)
    {
        if (stmt.init() is not null)
        {
            Visit(stmt.init()!);
        }

        using LoopBuilder loop_builder = NewLoopBuilder(stmt);
        if (stmt.cond() is not null && stmt.cond()!.ToBooleanIsFalse())
        {
            // If the condition is known to be false there is no need to generate
            // body, next or condition blocks. Init block should be generated.
            return;
        }

        using var loop_scope = new LoopScope(this, loop_builder);
        if (stmt.cond() is not null && !stmt.cond()!.ToBooleanIsTrue())
        {
            builder().SetExpressionAsStatementPosition(stmt.cond()!);
            var loop_body = new BytecodeLabels();
            VisitForTest(stmt.cond()!, loop_body, loop_builder.BreakLabels, TestFallthrough.kThen);
            loop_body.Bind(builder());
        }

        // C-style for loops' textual order differs from dominator order.
        //
        // for (INIT; TEST; NEXT) BODY
        // REST
        //
        //   has the dominator order of
        //
        // INIT dominates TEST dominates BODY dominates NEXT
        //   and
        // INIT dominates TEST dominates REST
        //
        // INIT and TEST are always evaluated and so do not have their own
        // HoleCheckElisionScope. BODY, like all iteration bodies, can contain control
        // flow like breaks or continues, has its own HoleCheckElisionScope. NEXT is
        // therefore conditionally evaluated and also so has its own
        // HoleCheckElisionScope.
        using var elider = new HoleCheckElisionScope(this);
        VisitIterationBody(stmt, loop_builder);
        if (stmt.next() is not null)
        {
            builder().SetStatementPosition(stmt.next()!);
            Visit(stmt.next()!);
        }
    }

    public override void VisitForInStatement(ForInStatement stmt)
    {
        if (stmt.subject().IsNullLiteral() || stmt.subject().IsUndefinedLiteral())
        {
            // ForIn generates lots of code, skip if it wouldn't produce any effects.
            return;
        }

        var subject_undefined_label = new BytecodeLabel();
        FeedbackSlot slot = feedback_spec().AddForInSlot();

        // Prepare the state for executing ForIn.
        builder().SetExpressionAsStatementPosition(stmt.subject());
        {
            using var current_scope = new CurrentScope(this, stmt.subject_scope());
            VisitForAccumulatorValue(stmt.subject());
        }
        builder().JumpIfUndefinedOrNull(subject_undefined_label);
        Register receiver = register_allocator().NewRegister();
        builder().ToObject(receiver);

        // Used as kRegTriple and kRegPair in ForInPrepare and ForInNext.
        RegisterList triple = register_allocator().NewRegisterList(3);
        Register cache_length = triple[2];
        builder().ForInEnumerate(receiver);
        builder().ForInPrepare(triple, feedback_index(slot));

        // Set up loop counter
        Register index = register_allocator().NewRegister();
        builder().LoadLiteral(Smi.Zero);
        builder().StoreAccumulatorInRegister(index);

        // The loop
        {
            using LoopBuilder loop_builder = NewLoopBuilder(stmt);
            using var loop_scope = new LoopScope(this, loop_builder);
            using var elider = new HoleCheckElisionScope(this);
            builder().SetExpressionAsStatementPosition(stmt.each(), isBreakable: false);
            loop_builder.BreakIfForInDone(index, cache_length);
            builder().ForInNext(receiver, index, triple.Truncate(2), feedback_index(slot));
            loop_builder.ContinueIfUndefined();

            // Assign accumulator value to the 'each' target.
            {
                using ExpressionResultScope scope = EffectResultScope();
                // Make sure to preserve the accumulator across the PrepareAssignmentLhs
                // call.
                builder().SetExpressionAsStatementPosition(stmt.each());
                AssignmentLhsData lhs_data = PrepareAssignmentLhs(stmt.each(), AccumulatorPreservingMode.kPreserve);
                builder().SetExpressionPosition(stmt.each());
                BuildAssignment(lhs_data, Token.Assign, LookupHoistingMode.kNormal);
            }

            {
                Register cache_type = triple[0];
                using var scope = new ForInScope(this, stmt, index, cache_type);
                VisitIterationBody(stmt, loop_builder);
                builder().ForInStep(index);
            }
        }
        builder().Bind(subject_undefined_label);
    }

    // Desugar a for-of statement into an application of the iteration protocol.
    //
    // for (EACH of SUBJECT) BODY
    //
    //   becomes
    //
    // iterator = %GetIterator(SUBJECT)
    // try {
    //
    //   loop {
    //     // Make sure we are considered 'done' if .next(), .done or .value fail.
    //     done = true
    //     value = iterator.next()
    //     if (value.done) break;
    //     value = value.value
    //     done = false
    //
    //     EACH = value
    //     BODY
    //   }
    //   done = true
    //
    // } catch(e) {
    //   iteration_continuation = RETHROW
    // } finally {
    //   %FinalizeIteration(iterator, done, iteration_continuation)
    // }
    public override void VisitForOfStatement(ForOfStatement stmt)
    {
        using ExpressionResultScope effect_scope = EffectResultScope();

        builder().SetExpressionAsStatementPosition(stmt.subject());
        {
            using var current_scope = new CurrentScope(this, stmt.subject_scope());
            VisitForAccumulatorValue(stmt.subject());
        }

        // Store the iterator in a dedicated register so that it can be closed on
        // exit, and the 'done' value in a dedicated register so that it can be
        // changed and accessed independently of the iteration result.
        IteratorRecord iterator = BuildGetIteratorRecord(stmt.type());
        Register next_result = register_allocator().NewRegister();
        Register done = register_allocator().NewRegister();
        builder().LoadFalse();
        builder().StoreAccumulatorInRegister(done);

        BuildTryFinally(
            // Try block.
            () =>
            {
                using LoopBuilder loop_builder = NewLoopBuilder(stmt);
                using var loop_scope = new LoopScope(this, loop_builder);

                // This doesn't need a HoleCheckElisionScope because BuildTryFinally
                // already makes one for try blocks.

                builder().LoadTrue().StoreAccumulatorInRegister(done);

                {
                    using var allocation_scope = new RegisterAllocationScope(this);

                    // Call the iterator's .next() method. Break from the loop if the
                    // `done` property is truthy, otherwise load the value from the
                    // iterator result and append the argument.
                    builder().SetExpressionAsStatementPosition(stmt.each(), isBreakable: false);
                    if (v8_flags.for_of_optimization && iterator.type() != IteratorType.kAsync)
                    {
                        FeedbackSlot call_slot = feedback_spec().AddCallICSlot();
                        feedback_spec().AddLoadICSlot(); // iterated_object_slot
                        feedback_spec().AddLoadICSlot(); // value_slot
                        feedback_spec().AddLoadICSlot(); // done_slot

                        builder()
                            .ForOfNext(iterator.@object(), iterator.next(), feedback_index(call_slot))
                            .StoreAccumulatorInRegister(next_result);

                        // TODO(marja): Consider adding a BreakIfHole helper.
                        builder().LoadTheHole().CompareReference(next_result);
                        loop_builder.BreakIfTrue(ToBooleanMode.AlreadyBoolean);
                    }
                    else
                    {
                        BuildIteratorNext(iterator, next_result);
                        builder().LoadNamedProperty(next_result, ast_string_constants().done_string,
                                                    feedback_index(feedback_spec().AddLoadICSlot()));
                        loop_builder.BreakIfTrue(ToBooleanMode.ConvertToBoolean);

                        builder()
                            // value = value.value
                            .LoadNamedProperty(next_result, ast_string_constants().value_string,
                                               feedback_index(feedback_spec().AddLoadICSlot()));
                        builder().StoreAccumulatorInRegister(next_result);
                    }

                    // done = false, before the assignment to each happens, so that done
                    // is false if the assignment throws.

                    // TODO(marja): consider removing "done" completely and passing
                    // next_result directly to BuildFinalizeIteration.
                    builder().LoadFalse().StoreAccumulatorInRegister(done);

                    // Assign to the 'each' target.
                    builder().SetExpressionAsStatementPosition(stmt.each());
                    AssignmentLhsData lhs_data = PrepareAssignmentLhs(stmt.each());
                    builder().LoadAccumulatorWithRegister(next_result);
                    BuildAssignment(lhs_data, Token.Assign, LookupHoistingMode.kNormal);
                }

                VisitIterationBody(stmt, loop_builder);
            },
            // Finally block.
            (iteration_continuation_token, iteration_continuation_result, message) =>
            {
                // Finish the iteration in the finally block.
                BuildFinalizeIteration(iterator, done, iteration_continuation_token);
            },
            catch_prediction());
    }

    public override void VisitTryCatchStatement(TryCatchStatement stmt)
    {
        // Update catch prediction tracking. The updated catch_prediction value lasts
        // until the end of the try_block in the AST node, and does not apply to the
        // catch_block.
        HandlerTable.CatchPrediction outer_catch_prediction = catch_prediction();
        set_catch_prediction((HandlerTable.CatchPrediction)stmt.GetCatchPrediction((CatchPrediction)outer_catch_prediction));

        BuildTryCatch(
            // Try body.
            () =>
            {
                Visit(stmt.try_block());
                set_catch_prediction(outer_catch_prediction);
            },
            // Catch body.
            context =>
            {
                if (stmt.scope() is not null)
                {
                    // Create a catch scope that binds the exception.
                    BuildNewLocalCatchContext(stmt.scope()!);
                    builder().StoreAccumulatorInRegister(context);
                }

                // If requested, clear message object as we enter the catch block.
                if (stmt.ShouldClearException((CatchPrediction)outer_catch_prediction))
                {
                    builder().LoadTheHole().SetPendingMessage();
                }

                // Load the catch context into the accumulator.
                builder().LoadAccumulatorWithRegister(context);

                // Evaluate the catch-block.
                if (stmt.scope() is not null)
                {
                    VisitInScope(stmt.catch_block(), stmt.scope()!);
                }
                else
                {
                    VisitBlock(stmt.catch_block());
                }
            },
            catch_prediction(), stmt);
    }

    public override void VisitTryFinallyStatement(TryFinallyStatement stmt)
    {
        BuildTryFinally(
            // Try block.
            () => Visit(stmt.try_block()),
            // Finally block.
            (body_continuation_token, body_continuation_result, message) => Visit(stmt.finally_block()),
            catch_prediction(), stmt);
    }

    public override void VisitDebuggerStatement(DebuggerStatement stmt)
    {
        builder().SetStatementPosition(stmt);
        builder().Debugger();
    }
}
