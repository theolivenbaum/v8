// Port of src/interpreter/bytecode-generator.cc: suspends (yield, yield*,
// await), property loads, private members, super, optional chains, calls,
// unary, count, binary, n-ary and compare operations, iterators, templates,
// the logical operators and the Visit* result helpers.
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
    // Suspends the generator to resume at the next suspend_id, with output stored
    // in the accumulator. When the generator is resumed, the sent value is loaded
    // in the accumulator.
    void BuildSuspendPoint(int position)
    {
        // Because we eliminate jump targets in dead code, we also eliminate resumes
        // when the suspend is not emitted because otherwise the below call to Bind
        // would start a new basic block and the code would be considered alive.
        if (builder().RemainderOfBlockIsDead())
        {
            return;
        }
        int suspend_id = _suspendCount++;

        RegisterList registers = register_allocator().AllLiveRegisters();

        // Save context, registers, and state. This bytecode then returns the value
        // in the accumulator.
        builder().SetExpressionPosition(position);
        builder().SuspendGenerator(generator_object(), registers, suspend_id);

        // Upon resume, we continue here.
        builder().Bind(_generatorJumpTable!, suspend_id);

        // Clobbers all registers and sets the accumulator to the
        // [[input_or_debug_pos]] slot of the generator object.
        builder().ResumeGenerator(generator_object(), registers);
    }

    public override void VisitYield(Yield expr)
    {
        builder().SetExpressionPosition(expr);
        VisitForAccumulatorValue(expr.expression());

        bool is_async = IsAsyncGeneratorFunction(function_kind());
        // If this is not the first yield
        if (_suspendCount > 0)
        {
            if (is_async)
            {
                // AsyncGenerator yields (with the exception of the initial yield)
                // delegate work to the AsyncGeneratorYieldWithAwait stub, which Awaits
                // the operand and on success, wraps the value in an IteratorResult.
                //
                // In the spec the Await is a separate operation, but they are combined
                // here to reduce bytecode size.
                using var register_scope = new RegisterAllocationScope(this);
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .MoveRegister(generator_object(), args[0]) // generator
                    .StoreAccumulatorInRegister(args[1])       // value
                    .CallRuntime(FunctionId.InlineAsyncGeneratorYieldWithAwait, args);
            }
            else
            {
                // Generator yields (with the exception of the initial yield) wrap the
                // value into IteratorResult.
                using var register_scope = new RegisterAllocationScope(this);
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .StoreAccumulatorInRegister(args[0])        // value
                    .MoveRegister(generator_object(), args[1]) // generator
                    .CallRuntime(FunctionId.InlineGeneratorYieldResult, args);
            }
        }

        BuildSuspendPoint(expr.position());
        // At this point, the generator has been resumed, with the received value in
        // the accumulator.

        // TODO(caitp): remove once yield* desugaring for async generators is handled
        // in BytecodeGenerator.
        if (expr.on_abrupt_resume() == Suspend.OnAbruptResume.kNoControl)
        {
            Debug.Assert(is_async);
            return;
        }

        Register input = register_allocator().NewRegister();
        builder().StoreAccumulatorInRegister(input).CallRuntime(FunctionId.InlineGeneratorGetResumeMode,
                                                                generator_object());

        // Now dispatch on resume mode.
        BytecodeJumpTable jump_table = builder().AllocateJumpTable(is_async ? 3 : 2, kNext);

        builder().SwitchOnSmiNoFeedback(jump_table);

        if (is_async)
        {
            // Resume with rethrow (switch fallthrough).
            // This case is only necessary in async generators.
            builder().SetExpressionPosition(expr);
            builder().LoadAccumulatorWithRegister(input);
            builder().ReThrow();

            // Add label for kThrow (next case).
            builder().Bind(jump_table, kThrow);
        }

        {
            // Resume with throw (switch fallthrough in sync case).
            // TODO(leszeks): Add a debug-only check that the accumulator is
            // JSGeneratorObject::kThrow.
            builder().SetExpressionPosition(expr);
            builder().LoadAccumulatorWithRegister(input);
            builder().Throw();
        }

        {
            // Resume with return.
            builder().Bind(jump_table, kReturn);
            builder().LoadAccumulatorWithRegister(input);
            if (is_async)
            {
                execution_control().AsyncReturnAccumulator(kNoSourcePosition);
            }
            else
            {
                execution_control().ReturnAccumulator(kNoSourcePosition);
            }
        }

        {
            // Resume with next.
            builder().Bind(jump_table, kNext);
            BuildIncrementBlockCoverageCounterIfEnabled(expr, SourceRangeKind.kContinuation);
            builder().LoadAccumulatorWithRegister(input);
        }
    }

    // Desugaring of (yield* iterable)
    //
    //   do {
    //     const kNext = 0;
    //     const kReturn = 1;
    //     const kThrow = 2;
    //
    //     let output; // uninitialized
    //
    //     let iteratorRecord = GetIterator(iterable);
    //     let iterator = iteratorRecord.[[Iterator]];
    //     let next = iteratorRecord.[[NextMethod]];
    //     let input = undefined;
    //     let resumeMode = kNext;
    //
    //     while (true) {
    //       // From the generator to the iterator:
    //       // Forward input according to resumeMode and obtain output.
    //       switch (resumeMode) {
    //         case kNext:
    //           output = next.[[Call]](iterator, « »);;
    //           break;
    //         case kReturn:
    //           let iteratorReturn = iterator.return;
    //           if (IS_NULL_OR_UNDEFINED(iteratorReturn)) {
    //             if (IS_ASYNC_GENERATOR) input = await input;
    //             return input;
    //           }
    //           output = iteratorReturn.[[Call]](iterator, «input»);
    //           break;
    //         case kThrow:
    //           let iteratorThrow = iterator.throw;
    //           if (IS_NULL_OR_UNDEFINED(iteratorThrow)) {
    //             let iteratorReturn = iterator.return;
    //             if (!IS_NULL_OR_UNDEFINED(iteratorReturn)) {
    //               output = iteratorReturn.[[Call]](iterator, « »);
    //               if (IS_ASYNC_GENERATOR) output = await output;
    //               if (!IS_RECEIVER(output)) %ThrowIterResultNotAnObject(output);
    //             }
    //             throw MakeTypeError(kThrowMethodMissing);
    //           }
    //           output = iteratorThrow.[[Call]](iterator, «input»);
    //           break;
    //       }
    //
    //       if (IS_ASYNC_GENERATOR) output = await output;
    //       if (!IS_RECEIVER(output)) %ThrowIterResultNotAnObject(output);
    //       if (output.done) break;
    //
    //       // From the generator to its user:
    //       // Forward output, receive new input, and determine resume mode.
    //       if (IS_ASYNC_GENERATOR) {
    //         // Resolve the promise for the current AsyncGeneratorRequest.
    //         %_AsyncGeneratorResolve(output.value, /* done = */ false)
    //       }
    //       input = Suspend(output);
    //       resumeMode = %GeneratorGetResumeMode();
    //     }
    //
    //     if (resumeMode === kReturn) {
    //       return output.value;
    //     }
    //     output.value
    //   }
    public override void VisitYieldStar(YieldStar expr)
    {
        Register output = register_allocator().NewRegister();
        Register resume_mode = register_allocator().NewRegister();
        IteratorType iterator_type = IsAsyncGeneratorFunction(function_kind())
            ? IteratorType.kAsync
            : IteratorType.kNormal;

        {
            using var register_scope = new RegisterAllocationScope(this);
            RegisterList iterator_and_input = register_allocator().NewRegisterList(2);
            VisitForAccumulatorValue(expr.expression());
            IteratorRecord iterator = BuildGetIteratorRecord(register_allocator().NewRegister() /* next method */,
                                                             iterator_and_input[0], iterator_type);

            Register input = iterator_and_input[1];
            builder().LoadUndefined().StoreAccumulatorInRegister(input);
            builder()
                .LoadLiteral(Smi.FromInt(kNext))
                .StoreAccumulatorInRegister(resume_mode);

            {
                // This loop builder does not construct counters as the loop is not
                // visible to the user, and we therefore neither pass the block coverage
                // builder nor the expression.
                //
                // In addition to the normal suspend for yield*, a yield* in an async
                // generator has 2 additional suspends:
                //   - One for awaiting the iterator result of closing the generator when
                //     resumed with a "throw" completion, and a throw method is not
                //     present on the delegated iterator
                //   - One for awaiting the iterator result yielded by the delegated
                //     iterator

                using LoopBuilder loop_builder = NewLoopBuilder(null);
                using var loop_scope = new LoopScope(this, loop_builder);

                {
                    var after_switch = new BytecodeLabels();
                    BytecodeJumpTable switch_jump_table = builder().AllocateJumpTable(2, 1);

                    builder()
                        .LoadAccumulatorWithRegister(resume_mode)
                        .SwitchOnSmiNoFeedback(switch_jump_table);

                    // Fallthrough to default case.
                    // TODO(ignition): Add debug code to check that {resume_mode} really is
                    // {JSGeneratorObject::kNext} in this case.
                    {
                        FeedbackSlot slot = feedback_spec().AddCallICSlot();
                        builder().CallProperty(iterator.next(), iterator_and_input, feedback_index(slot));
                        builder().Jump(after_switch.New());
                    }

                    builder().Bind(switch_jump_table, kReturn);
                    {
                        AstRawString return_string = ast_string_constants().return_string;
                        var no_return_method = new BytecodeLabels();

                        BuildCallIteratorMethod(iterator.@object(), return_string, iterator_and_input,
                                                after_switch.New(), no_return_method);
                        no_return_method.Bind(builder());
                        builder().LoadAccumulatorWithRegister(input);
                        if (iterator_type == IteratorType.kAsync)
                        {
                            // Await input.
                            BuildAwait(expr.position());
                            execution_control().AsyncReturnAccumulator(kNoSourcePosition);
                        }
                        else
                        {
                            execution_control().ReturnAccumulator(kNoSourcePosition);
                        }
                    }

                    builder().Bind(switch_jump_table, kThrow);
                    {
                        AstRawString throw_string = ast_string_constants().throw_string;
                        var no_throw_method = new BytecodeLabels();
                        BuildCallIteratorMethod(iterator.@object(), throw_string, iterator_and_input,
                                                after_switch.New(), no_throw_method);

                        // If there is no "throw" method, perform IteratorClose, and finally
                        // throw a TypeError.
                        no_throw_method.Bind(builder());
                        BuildIteratorClose(iterator, expr);
                        builder().CallRuntime(FunctionId.ThrowThrowMethodMissing);
                    }

                    after_switch.Bind(builder());
                }

                if (iterator_type == IteratorType.kAsync)
                {
                    // Await the result of the method invocation.
                    BuildAwait(expr.position());
                }

                // Check that output is an object.
                var check_if_done = new BytecodeLabel();
                builder()
                    .StoreAccumulatorInRegister(output)
                    .JumpIfJSReceiver(check_if_done)
                    .CallRuntime(FunctionId.ThrowIteratorResultNotAnObject, output);

                builder().Bind(check_if_done);
                // Break once output.done is true.
                builder().LoadNamedProperty(output, ast_string_constants().done_string,
                                            feedback_index(feedback_spec().AddLoadICSlot()));

                loop_builder.BreakIfTrue(ToBooleanMode.ConvertToBoolean);

                // Suspend the current generator.
                if (iterator_type == IteratorType.kNormal)
                {
                    builder().LoadAccumulatorWithRegister(output);
                }
                else
                {
                    using var inner_register_scope = new RegisterAllocationScope(this);
                    Debug.Assert(iterator_type == IteratorType.kAsync);
                    // If generatorKind is async, perform
                    // AsyncGeneratorResolve(output.value, /* done = */ false), which will
                    // resolve the current AsyncGeneratorRequest's promise with
                    // output.value.
                    builder().LoadNamedProperty(output, ast_string_constants().value_string,
                                                feedback_index(feedback_spec().AddLoadICSlot()));

                    RegisterList args = register_allocator().NewRegisterList(3);
                    builder()
                        .MoveRegister(generator_object(), args[0]) // generator
                        .StoreAccumulatorInRegister(args[1])       // value
                        .LoadFalse()
                        .StoreAccumulatorInRegister(args[2]) // done
                        .CallRuntime(FunctionId.InlineAsyncGeneratorResolve, args);
                }

                BuildSuspendPoint(expr.position());
                builder().StoreAccumulatorInRegister(input);
                builder()
                    .CallRuntime(FunctionId.InlineGeneratorGetResumeMode, generator_object())
                    .StoreAccumulatorInRegister(resume_mode);

                loop_builder.BindContinueTarget();
            }
        }

        // Decide if we trigger a return or if the yield* expression should just
        // produce a value.
        var completion_is_output_value = new BytecodeLabel();
        Register output_value = register_allocator().NewRegister();
        builder()
            .LoadNamedProperty(output, ast_string_constants().value_string,
                               feedback_index(feedback_spec().AddLoadICSlot()))
            .StoreAccumulatorInRegister(output_value)
            .LoadLiteral(Smi.FromInt(kReturn))
            .CompareReference(resume_mode)
            .JumpIfFalse(ToBooleanMode.AlreadyBoolean, completion_is_output_value)
            .LoadAccumulatorWithRegister(output_value);
        if (iterator_type == IteratorType.kAsync)
        {
            execution_control().AsyncReturnAccumulator(kNoSourcePosition);
        }
        else
        {
            execution_control().ReturnAccumulator(kNoSourcePosition);
        }

        builder().Bind(completion_is_output_value);
        BuildIncrementBlockCoverageCounterIfEnabled(expr, SourceRangeKind.kContinuation);
        builder().LoadAccumulatorWithRegister(output_value);
    }

    void BuildAwait(int position = kNoSourcePosition)
    {
        // Rather than HandlerTable::UNCAUGHT, async functions use
        // HandlerTable::ASYNC_AWAIT to communicate that top-level exceptions are
        // transformed into promise rejections. This is necessary to prevent emitting
        // multiple debug events for the same uncaught exception. There is no point
        // in the body of an async function where catch prediction is
        // HandlerTable::UNCAUGHT.
        Debug.Assert(catch_prediction() != HandlerTable.CatchPrediction.UNCAUGHT ||
                     info().scope().is_repl_mode_scope());

        {
            // Await(operand) and suspend.
            using var register_scope = new RegisterAllocationScope(this);

            FunctionId await_intrinsic_id;
            if (IsAsyncGeneratorFunction(function_kind()))
            {
                await_intrinsic_id = FunctionId.InlineAsyncGeneratorAwait;
            }
            else
            {
                await_intrinsic_id = FunctionId.InlineAsyncFunctionAwait;
            }
            RegisterList args = register_allocator().NewRegisterList(2);
            builder()
                .MoveRegister(generator_object(), args[0])
                .StoreAccumulatorInRegister(args[1])
                .CallRuntime(await_intrinsic_id, args);
        }

        BuildSuspendPoint(position);

        Register input = register_allocator().NewRegister();
        Register resume_mode = register_allocator().NewRegister();

        // Now dispatch on resume mode.
        var resume_next = new BytecodeLabel();
        builder()
            .StoreAccumulatorInRegister(input)
            .CallRuntime(FunctionId.InlineGeneratorGetResumeMode, generator_object())
            .StoreAccumulatorInRegister(resume_mode)
            .LoadLiteral(Smi.FromInt(kNext))
            .CompareReference(resume_mode)
            .JumpIfTrue(ToBooleanMode.AlreadyBoolean, resume_next);

        // Resume with "throw" completion (rethrow the received value).
        // TODO(leszeks): Add a debug-only check that the accumulator is
        // JSGeneratorObject::kThrow.
        builder().LoadAccumulatorWithRegister(input).ReThrow();

        // Resume with next.
        builder().Bind(resume_next);
        builder().LoadAccumulatorWithRegister(input);
    }

    public override void VisitAwait(Await expr)
    {
        builder().SetExpressionPosition(expr);
        VisitForAccumulatorValue(expr.expression());
        BuildAwait(expr.position());
        BuildIncrementBlockCoverageCounterIfEnabled(expr, SourceRangeKind.kContinuation);
    }

    public override void VisitThrow(Throw expr)
    {
        AllocateBlockCoverageSlotIfEnabled(expr, SourceRangeKind.kContinuation);
        VisitForAccumulatorValue(expr.exception());
        builder().SetExpressionPosition(expr);
        builder().Throw();
    }

    void VisitPropertyLoad(Register obj, Property property)
    {
        if (property.is_optional_chain_link())
        {
            Debug.Assert(_optionalChainingNullLabels is not null);
            int right_range = AllocateBlockCoverageSlotIfEnabled(property, SourceRangeKind.kRight);
            builder().LoadAccumulatorWithRegister(obj).JumpIfUndefinedOrNull(_optionalChainingNullLabels!.New());
            BuildIncrementBlockCoverageCounterIfEnabled(right_range);
        }

        AssignType property_kind = Property.GetAssignType(property);

        switch (property_kind)
        {
            case AssignType.NON_PROPERTY:
                throw new UnreachableException();
            case AssignType.NAMED_PROPERTY:
            {
                builder().SetExpressionPosition(property);
                AstRawString name = property.key().AsLiteral()!.AsRawPropertyName();
                BuildLoadNamedProperty(property.obj(), obj, name);
                break;
            }
            case AssignType.KEYED_PROPERTY:
            {
                if (v8_flags.private_field_bytecodes && property.IsPrivateReference())
                {
                    builder().SetExpressionPosition(property);
                    Debug.Assert(property.key().IsVariableProxy());
                    Variable var = property.key().AsVariableProxy()!.var();
                    int depth = execution_context().ContextChainDepth(var.scope()!);
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
                    builder().GetPrivateField(context_reg, var.index(), depth, obj,
                                              feedback_index(feedback_spec().AddKeyedLoadICSlot()));
                    break;
                }
                VisitForAccumulatorValueAsPropertyKey(property.key());
                builder().SetExpressionPosition(property);
                BuildLoadKeyedProperty(obj, feedback_spec().AddKeyedLoadICSlot());
                break;
            }
            case AssignType.NAMED_SUPER_PROPERTY:
                VisitNamedSuperPropertyLoad(property, Register.InvalidValue());
                break;
            case AssignType.KEYED_SUPER_PROPERTY:
                VisitKeyedSuperPropertyLoad(property, Register.InvalidValue());
                break;
            case AssignType.PRIVATE_SETTER_ONLY:
            {
                BuildPrivateBrandCheck(property, obj);
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateGetterAccess, property);
                break;
            }
            case AssignType.PRIVATE_GETTER_ONLY:
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                Register key = VisitForRegisterValue(property.key());
                BuildPrivateBrandCheck(property, obj);
                BuildPrivateGetterAccess(obj, key);
                break;
            }
            case AssignType.PRIVATE_METHOD:
            {
                BuildPrivateBrandCheck(property, obj);
                // In the case of private methods, property->key() is the function to be
                // loaded (stored in a context slot), so load this directly.
                VisitForAccumulatorValue(property.key());
                break;
            }
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
            {
                BuildPrivateDebugDynamicGet(property, obj);
                break;
            }
        }
    }

    void BuildPrivateDebugDynamicGet(Property property, Register obj)
    {
        using var scope = new RegisterAllocationScope(this);
        RegisterList args = register_allocator().NewRegisterList(2);

        Variable private_name = property.key().AsVariableProxy()!.var();
        builder()
            .MoveRegister(obj, args[0])
            .LoadLiteral(private_name.raw_name())
            .StoreAccumulatorInRegister(args[1])
            .CallRuntime(FunctionId.GetPrivateMember, args);
    }

    void BuildPrivateDebugDynamicSet(Property property, Register obj, Register value)
    {
        using var scope = new RegisterAllocationScope(this);
        RegisterList args = register_allocator().NewRegisterList(3);

        Variable private_name = property.key().AsVariableProxy()!.var();
        builder()
            .MoveRegister(obj, args[0])
            .LoadLiteral(private_name.raw_name())
            .StoreAccumulatorInRegister(args[1])
            .MoveRegister(value, args[2])
            .CallRuntime(FunctionId.SetPrivateMember, args);
    }

    void BuildPrivateGetterAccess(Register @object, Register accessor_pair)
    {
        using var scope = new RegisterAllocationScope(this);
        Register accessor = register_allocator().NewRegister();
        RegisterList args = register_allocator().NewRegisterList(1);

        builder()
            .CallRuntime(FunctionId.LoadPrivateGetter, accessor_pair)
            .StoreAccumulatorInRegister(accessor)
            .MoveRegister(@object, args[0])
            .CallProperty(accessor, args, feedback_index(feedback_spec().AddCallICSlot()));
    }

    void BuildPrivateSetterAccess(Register @object, Register accessor_pair, Register value)
    {
        using var scope = new RegisterAllocationScope(this);
        Register accessor = register_allocator().NewRegister();
        RegisterList args = register_allocator().NewRegisterList(2);

        builder()
            .CallRuntime(FunctionId.LoadPrivateSetter, accessor_pair)
            .StoreAccumulatorInRegister(accessor)
            .MoveRegister(@object, args[0])
            .MoveRegister(value, args[1])
            .CallProperty(accessor, args, feedback_index(feedback_spec().AddCallICSlot()));
    }

    void BuildPrivateMethodIn(Variable private_name, Expression object_expression)
    {
        Debug.Assert(IsPrivateMethodOrAccessorVariableMode(private_name.mode()));
        ClassScope scope = private_name.scope()!.AsClassScope();
        if (private_name.is_static())
        {
            // For static private methods, "#privatemethod in ..." only returns true for
            // the class constructor.
            if (scope.class_variable() is null)
            {
                // Can only happen via the debugger. See comment in
                // BuildPrivateBrandCheck.
                using var register_scope = new RegisterAllocationScope(this);
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .LoadLiteral(Smi.FromInt((int)MessageTemplate.InvalidUnusedPrivateStaticMethodAccessedByDebugger))
                    .StoreAccumulatorInRegister(args[0])
                    .LoadLiteral(private_name.raw_name())
                    .StoreAccumulatorInRegister(args[1])
                    .CallRuntime(FunctionId.NewError, args)
                    .Throw();
            }
            else
            {
                VisitForAccumulatorValue(object_expression);
                Register @object = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(@object);

                var is_object = new BytecodeLabel();
                builder().JumpIfJSReceiver(is_object);

                RegisterList args = register_allocator().NewRegisterList(3);
                builder()
                    .StoreAccumulatorInRegister(args[2])
                    .LoadLiteral(Smi.FromInt((int)MessageTemplate.InvalidInOperatorUse))
                    .StoreAccumulatorInRegister(args[0])
                    .LoadLiteral(private_name.raw_name())
                    .StoreAccumulatorInRegister(args[1])
                    .CallRuntime(FunctionId.NewTypeError, args)
                    .Throw();

                builder().Bind(is_object);
                BuildVariableLoadForAccumulatorValue(scope.class_variable()!, HoleCheckMode.kElided);
                builder().CompareReference(@object);
            }
        }
        else
        {
            BuildVariableLoadForAccumulatorValue(scope.brand()!, HoleCheckMode.kElided);
            Register brand = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(brand);

            VisitForAccumulatorValue(object_expression);
            builder().SetExpressionPosition(object_expression);

            FeedbackSlot slot = feedback_spec().AddKeyedHasICSlot();
            builder().CompareOperation(Token.In, brand, feedback_index(slot));
            execution_result().SetResultIsBoolean();
        }
    }

    void BuildPrivateBrandCheck(Property property, Register @object)
    {
        Variable private_name = property.key().AsVariableProxy()!.var();
        Debug.Assert(IsPrivateMethodOrAccessorVariableMode(private_name.mode()));
        ClassScope scope = private_name.scope()!.AsClassScope();
        builder().SetExpressionPosition(property);
        if (private_name.is_static())
        {
            // For static private methods, the only valid receiver is the class.
            // Load the class constructor.
            if (scope.class_variable() is null)
            {
                // If the static private method has not been used used in source
                // code (either explicitly or through the presence of eval), but is
                // accessed by the debugger at runtime, reference to the class variable
                // is not available since it was not be context-allocated. Therefore we
                // can't build a branch check, and throw an ReferenceError as if the
                // method was optimized away.
                // TODO(joyee): get a reference to the class constructor through
                // something other than scope->class_variable() in this scenario.
                using var register_scope = new RegisterAllocationScope(this);
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .LoadLiteral(Smi.FromInt((int)MessageTemplate.InvalidUnusedPrivateStaticMethodAccessedByDebugger))
                    .StoreAccumulatorInRegister(args[0])
                    .LoadLiteral(private_name.raw_name())
                    .StoreAccumulatorInRegister(args[1])
                    .CallRuntime(FunctionId.NewError, args)
                    .Throw();
            }
            else
            {
                BuildVariableLoadForAccumulatorValue(scope.class_variable()!, HoleCheckMode.kElided);
                var return_check = new BytecodeLabel();
                builder().CompareReference(@object).JumpIfTrue(ToBooleanMode.AlreadyBoolean, return_check);
                AstRawString name = scope.class_variable()!.raw_name();
                using var register_scope = new RegisterAllocationScope(this);
                RegisterList args = register_allocator().NewRegisterList(2);
                builder()
                    .LoadLiteral(Smi.FromInt((int)MessageTemplate.InvalidPrivateBrandStatic))
                    .StoreAccumulatorInRegister(args[0])
                    .LoadLiteral(name)
                    .StoreAccumulatorInRegister(args[1])
                    .CallRuntime(FunctionId.NewTypeError, args)
                    .Throw();
                builder().Bind(return_check);
            }
        }
        else
        {
            BuildVariableLoadForAccumulatorValue(scope.brand()!, HoleCheckMode.kElided);
            builder().LoadKeyedProperty(@object, feedback_index(feedback_spec().AddKeyedLoadICSlot()));
        }
    }

    void VisitPropertyLoadForRegister(Register obj, Property expr, Register destination)
    {
        using ExpressionResultScope result_scope = ValueResultScope();
        VisitPropertyLoad(obj, expr);
        builder().StoreAccumulatorInRegister(destination);
    }

    void VisitNamedSuperPropertyLoad(Property property, Register opt_receiver_out)
    {
        using var register_scope = new RegisterAllocationScope(this);
        if (v8_flags.super_ic)
        {
            Register receiver = register_allocator().NewRegister();
            BuildThisVariableLoad();
            builder().StoreAccumulatorInRegister(receiver);
            BuildVariableLoad(property.obj().AsSuperPropertyReference()!.home_object().var(), HoleCheckMode.kElided);
            builder().SetExpressionPosition(property);
            AstRawString name = property.key().AsLiteral()!.AsRawPropertyName();
            FeedbackSlot slot = GetCachedLoadSuperICSlot(name);
            builder().LoadNamedPropertyFromSuper(receiver, name, feedback_index(slot));
            if (opt_receiver_out.IsValid)
            {
                builder().MoveRegister(receiver, opt_receiver_out);
            }
        }
        else
        {
            RegisterList args = register_allocator().NewRegisterList(3);
            BuildThisVariableLoad();
            builder().StoreAccumulatorInRegister(args[0]);
            BuildVariableLoad(property.obj().AsSuperPropertyReference()!.home_object().var(), HoleCheckMode.kElided);
            builder().StoreAccumulatorInRegister(args[1]);
            builder().SetExpressionPosition(property);
            builder()
                .LoadLiteral(property.key().AsLiteral()!.AsRawPropertyName())
                .StoreAccumulatorInRegister(args[2])
                .CallRuntime(FunctionId.LoadFromSuper, args);

            if (opt_receiver_out.IsValid)
            {
                builder().MoveRegister(args[0], opt_receiver_out);
            }
        }
    }

    void VisitKeyedSuperPropertyLoad(Property property, Register opt_receiver_out)
    {
        using var register_scope = new RegisterAllocationScope(this);
        RegisterList args = register_allocator().NewRegisterList(3);
        BuildThisVariableLoad();
        builder().StoreAccumulatorInRegister(args[0]);
        BuildVariableLoad(property.obj().AsSuperPropertyReference()!.home_object().var(), HoleCheckMode.kElided);
        builder().StoreAccumulatorInRegister(args[1]);
        VisitForRegisterValue(property.key(), args[2]);

        builder().SetExpressionPosition(property);
        builder().CallRuntime(FunctionId.LoadKeyedFromSuper, args);

        if (opt_receiver_out.IsValid)
        {
            builder().MoveRegister(args[0], opt_receiver_out);
        }
    }

    void BuildOptionalChain(Action expression_func)
    {
        var done = new BytecodeLabel();
        using var label_scope = new OptionalChainNullLabelScope(this);
        expression_func();
        builder().Jump(done);
        label_scope.labels().Bind(builder());
        builder().LoadUndefined();
        builder().Bind(done);
    }

    public override void VisitOptionalChain(OptionalChain expr) =>
        BuildOptionalChain(() => VisitForAccumulatorValue(expr.expression()));

    public override void VisitProperty(Property expr)
    {
        AssignType property_kind = Property.GetAssignType(expr);
        if (property_kind != AssignType.NAMED_SUPER_PROPERTY && property_kind != AssignType.KEYED_SUPER_PROPERTY)
        {
            Register obj = VisitForRegisterValue(expr.obj());
            VisitPropertyLoad(obj, expr);
        }
        else
        {
            VisitPropertyLoad(Register.InvalidValue(), expr);
        }
    }

    void VisitArguments(List<Expression> args, ref RegisterList arg_regs)
    {
        // Visit arguments.
        builder().UpdateMaxArguments((ushort)args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            VisitAndPushIntoRegisterList(args[i], ref arg_regs);
        }
    }

    public override void VisitCall(Call expr)
    {
        Expression callee_expr = expr.expression();
        Call.CallType call_type = expr.GetCallType();

        if (call_type == Call.CallType.SUPER_CALL)
        {
            VisitCallSuper(expr);
            return;
        }

        // We compile the call differently depending on the presence of spreads and
        // their positions.
        //
        // If there is only one spread and it is the final argument, there is a
        // special CallWithSpread bytecode.
        //
        // If there is a non-final spread, we rewrite calls like
        //     callee(1, ...x, 2)
        // to
        //     %reflect_apply(callee, receiver, [1, ...x, 2])
        //
        // For direct eval calls with a final spread like eval(...iter), we also use
        // %reflect_apply so we can extract the first argument for eval resolution.
        CallBase.SpreadPosition spread_position = expr.spread_position();
        bool eval_with_final_spread = spread_position == CallBase.SpreadPosition.kHasFinalSpread &&
                                      expr.is_possibly_eval() && expr.arguments().Count > 0;
        bool use_reflect_apply = spread_position == CallBase.SpreadPosition.kHasNonFinalSpread || eval_with_final_spread;

        // Grow the args list as we visit receiver / arguments to avoid allocating all
        // the registers up-front. Otherwise these registers are unavailable during
        // receiver / argument visiting and we can end up with memory leaks due to
        // registers keeping objects alive.
        RegisterList args = register_allocator().NewGrowableRegisterList();

        // The callee is the first register in args for ease of calling %reflect_apply
        // if we have a non-final spread. For all other cases it is popped from args
        // before emitting the call below.
        Register callee = register_allocator().GrowRegisterList(ref args);

        bool implicit_undefined_receiver = false;

        // TODO(petermarshall): We have a lot of call bytecodes that are very similar,
        // see if we can reduce the number by adding a separate argument which
        // specifies the call type (e.g., property, spread, tailcall, etc.).

        // Prepare the callee and the receiver to the function call. This depends on
        // the semantics of the underlying call type.
        switch (call_type)
        {
            case Call.CallType.NAMED_PROPERTY_CALL:
            case Call.CallType.KEYED_PROPERTY_CALL:
            case Call.CallType.PRIVATE_CALL:
            {
                Property property = callee_expr.AsProperty()!;
                VisitAndPushIntoRegisterList(property.obj(), ref args);
                VisitPropertyLoadForRegister(args.LastRegister(), property, callee);
                break;
            }
            case Call.CallType.GLOBAL_CALL:
            {
                // Receiver is undefined for global calls.
                if (spread_position == CallBase.SpreadPosition.kNoSpread)
                {
                    implicit_undefined_receiver = true;
                }
                else
                {
                    // TODO(leszeks): There's no special bytecode for tail calls or spread
                    // calls with an undefined receiver, so just push undefined ourselves.
                    BuildPushUndefinedIntoRegisterList(ref args);
                }
                // Load callee as a global variable.
                VariableProxy proxy = callee_expr.AsVariableProxy()!;
                BuildVariableLoadForAccumulatorValue(proxy.var(), proxy.hole_check_mode());
                builder().StoreAccumulatorInRegister(callee);
                break;
            }
            case Call.CallType.WITH_CALL:
            {
                Register receiver = register_allocator().GrowRegisterList(ref args);
                Debug.Assert(callee_expr.AsVariableProxy()!.var().IsLookupSlot());
                {
                    using var inner_register_scope = new RegisterAllocationScope(this);
                    Register name = register_allocator().NewRegister();

                    // Call %LoadLookupSlotForCall to get the callee and receiver.
                    RegisterList result_pair = register_allocator().NewRegisterList(2);
                    Variable variable = callee_expr.AsVariableProxy()!.var();
                    builder()
                        .LoadLiteral(variable.raw_name())
                        .StoreAccumulatorInRegister(name)
                        .CallRuntimeForPair(FunctionId.LoadLookupSlotForCall, name, result_pair)
                        .MoveRegister(result_pair[0], callee)
                        .MoveRegister(result_pair[1], receiver);
                }
                break;
            }
            case Call.CallType.OTHER_CALL:
            {
                // Receiver is undefined for other calls.
                if (spread_position == CallBase.SpreadPosition.kNoSpread)
                {
                    implicit_undefined_receiver = true;
                }
                else
                {
                    // TODO(leszeks): There's no special bytecode for tail calls or spread
                    // calls with an undefined receiver, so just push undefined ourselves.
                    BuildPushUndefinedIntoRegisterList(ref args);
                }
                VisitForRegisterValue(callee_expr, callee);
                break;
            }
            case Call.CallType.NAMED_SUPER_PROPERTY_CALL:
            {
                Register receiver = register_allocator().GrowRegisterList(ref args);
                Property property = callee_expr.AsProperty()!;
                VisitNamedSuperPropertyLoad(property, receiver);
                builder().StoreAccumulatorInRegister(callee);
                break;
            }
            case Call.CallType.KEYED_SUPER_PROPERTY_CALL:
            {
                Register receiver = register_allocator().GrowRegisterList(ref args);
                Property property = callee_expr.AsProperty()!;
                VisitKeyedSuperPropertyLoad(property, receiver);
                builder().StoreAccumulatorInRegister(callee);
                break;
            }
            case Call.CallType.NAMED_OPTIONAL_CHAIN_PROPERTY_CALL:
            case Call.CallType.KEYED_OPTIONAL_CHAIN_PROPERTY_CALL:
            case Call.CallType.PRIVATE_OPTIONAL_CHAIN_CALL:
            {
                OptionalChain chain = callee_expr.AsOptionalChain()!;
                Property property = chain.expression().AsProperty()!;
                // BuildOptionalChain's lambda captures the args list by reference.
                var done = new BytecodeLabel();
                using (var label_scope = new OptionalChainNullLabelScope(this))
                {
                    VisitAndPushIntoRegisterList(property.obj(), ref args);
                    VisitPropertyLoad(args.LastRegister(), property);
                    builder().Jump(done);
                    label_scope.labels().Bind(builder());
                    builder().LoadUndefined();
                    builder().Bind(done);
                }
                builder().StoreAccumulatorInRegister(callee);
                break;
            }
            case Call.CallType.SUPER_CALL:
                throw new UnreachableException();
        }

        if (expr.is_optional_chain_link())
        {
            Debug.Assert(_optionalChainingNullLabels is not null);
            int right_range = AllocateBlockCoverageSlotIfEnabled(expr, SourceRangeKind.kRight);
            builder().LoadAccumulatorWithRegister(callee).JumpIfUndefinedOrNull(_optionalChainingNullLabels!.New());
            BuildIncrementBlockCoverageCounterIfEnabled(right_range);
        }

        int receiver_arg_count = -1;
        if (use_reflect_apply)
        {
            // If we're building %reflect_apply, build the array literal and put it in
            // the 3rd argument.
            Debug.Assert(!implicit_undefined_receiver);
            Debug.Assert(args.RegisterCount == 2);
            BuildCreateArrayLiteral(expr.arguments(), null);
            builder().StoreAccumulatorInRegister(register_allocator().GrowRegisterList(ref args));
        }
        else
        {
            // If we're not building %reflect_apply and don't need to build an array
            // literal, pop the callee and evaluate all arguments to the function call
            // and store in sequential args registers.
            args = args.PopLeft();
            VisitArguments(expr.arguments(), ref args);
            receiver_arg_count = implicit_undefined_receiver ? 0 : 1;
            if (receiver_arg_count + expr.arguments().Count != args.RegisterCount)
            {
                throw new InvalidOperationException("CHECK_EQ(receiver_arg_count + arguments, register_count)");
            }
        }

        // Resolve callee for a potential direct eval call. This block will mutate the
        // callee value.
        if (expr.is_possibly_eval() && expr.arguments().Count > 0)
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            RegisterList runtime_call_args = register_allocator().NewRegisterList(6);
            // Set up arguments for ResolvePossiblyDirectEval by copying callee, source
            // strings and function closure, and loading language and
            // position.

            // Move the first arg.
            if (use_reflect_apply)
            {
                int feedback_slot_index = feedback_index(feedback_spec().AddKeyedLoadICSlot());
                Register args_array = args[2];
                builder()
                    .LoadLiteral(Smi.FromInt(0))
                    .LoadKeyedProperty(args_array, feedback_slot_index)
                    .StoreAccumulatorInRegister(runtime_call_args[1]);
            }
            else
            {
                Debug.Assert(receiver_arg_count >= 0);
                builder().MoveRegister(args[receiver_arg_count], runtime_call_args[1]);
            }
            Scope? scope_with_context = current_scope();
            if (!scope_with_context.NeedsContext())
            {
                scope_with_context = scope_with_context.GetOuterScopeWithContext();
            }
            if (scope_with_context is not null)
            {
                _evalCalls.Add((expr, scope_with_context));
            }
            builder()
                .MoveRegister(callee, runtime_call_args[0])
                .MoveRegister(Register.FunctionClosure(), runtime_call_args[2])
                .LoadLiteral(Smi.FromInt((int)language_mode()))
                .StoreAccumulatorInRegister(runtime_call_args[3])
                .LoadLiteral(Smi.FromInt((int)expr.eval_scope_info_index()))
                .StoreAccumulatorInRegister(runtime_call_args[4])
                .LoadLiteral(Smi.FromInt(expr.position()))
                .StoreAccumulatorInRegister(runtime_call_args[5]);

            // Call ResolvePossiblyDirectEval and modify the callee.
            builder()
                .CallRuntime(FunctionId.ResolvePossiblyDirectEval, runtime_call_args)
                .StoreAccumulatorInRegister(callee);
        }

        builder().SetExpressionPosition(expr);

        if (use_reflect_apply)
        {
            builder().CallJSRuntime((int)NativeContextFields.REFLECT_APPLY_INDEX, args);
        }
        else if (spread_position == CallBase.SpreadPosition.kHasFinalSpread)
        {
            Debug.Assert(!implicit_undefined_receiver);
            builder().CallWithSpread(callee, args, feedback_index(feedback_spec().AddCallICSlot()));
        }
        else if (call_type == Call.CallType.NAMED_PROPERTY_CALL || call_type == Call.CallType.KEYED_PROPERTY_CALL)
        {
            Debug.Assert(!implicit_undefined_receiver);
            builder().CallProperty(callee, args, feedback_index(feedback_spec().AddCallICSlot()));
        }
        else if (implicit_undefined_receiver)
        {
            builder().CallUndefinedReceiver(callee, args, feedback_index(feedback_spec().AddCallICSlot()));
        }
        else
        {
            builder().CallAnyReceiver(callee, args, feedback_index(feedback_spec().AddCallICSlot()));
        }
    }

    void VisitCallSuper(Call expr)
    {
        using var register_scope = new RegisterAllocationScope(this);
        SuperCallReference super = expr.expression().AsSuperCallReference()!;
        List<Expression> args = expr.arguments();

        // We compile the super call differently depending on the presence of spreads
        // and their positions.
        //
        // If there is only one spread and it is the final argument, there is a
        // special ConstructWithSpread bytecode.
        //
        // It there is a non-final spread, we rewrite something like
        //    super(1, ...x, 2)
        // to
        //    %reflect_construct(constructor, [1, ...x, 2], new_target)
        //
        // That is, we implement (non-last-arg) spreads in super calls via our
        // mechanism for spreads in array literals.
        CallBase.SpreadPosition spread_position = expr.spread_position();

        // Prepare the constructor to the super call.
        Register this_function = VisitForRegisterValue(super.this_function_var());
        // This register will initially hold the constructor, then afterward it will
        // hold the instance -- the lifetimes of the two don't need to overlap, and
        // this way FindNonDefaultConstructorOrConstruct can choose to write either
        // the instance or the constructor into the same register.
        Register constructor_then_instance = register_allocator().NewRegister();

        var super_ctor_call_done = new BytecodeLabel();

        if (spread_position == CallBase.SpreadPosition.kHasNonFinalSpread)
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            var construct_args = new RegisterList(constructor_then_instance);
            Register constructor = constructor_then_instance;

            // Generate the array containing all arguments.
            BuildCreateArrayLiteral(args, null);
            Register args_array = register_allocator().GrowRegisterList(ref construct_args);
            builder().StoreAccumulatorInRegister(args_array);

            Register new_target = register_allocator().GrowRegisterList(ref construct_args);
            VisitForRegisterValue(super.new_target_var(), new_target);

            BuildGetAndCheckSuperConstructor(this_function, new_target, constructor, super_ctor_call_done);

            // Now pass that array to %reflect_construct.
            builder().CallJSRuntime((int)NativeContextFields.REFLECT_CONSTRUCT_INDEX, construct_args);
        }
        else
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            RegisterList args_regs = register_allocator().NewGrowableRegisterList();
            VisitArguments(args, ref args_regs);

            // The new target is loaded into the new_target register from the
            // {new.target} variable.
            Register new_target = register_allocator().NewRegister();
            VisitForRegisterValue(super.new_target_var(), new_target);

            Register constructor = constructor_then_instance;
            BuildGetAndCheckSuperConstructor(this_function, new_target, constructor, super_ctor_call_done);

            builder().LoadAccumulatorWithRegister(new_target);
            builder().SetExpressionPosition(expr);

            int feedback_slot_index = feedback_index(feedback_spec().AddCallICSlot());

            if (spread_position == CallBase.SpreadPosition.kHasFinalSpread)
            {
                builder().ConstructWithSpread(constructor, args_regs, feedback_slot_index);
            }
            else
            {
                Debug.Assert(spread_position == CallBase.SpreadPosition.kNoSpread);
                // Call construct.
                // TODO(turbofan): For now we do gather feedback on super constructor
                // calls, utilizing the existing machinery to inline the actual call
                // target and the JSCreate for the implicit receiver allocation. This
                // is not an ideal solution for super constructor calls, but it gets
                // the job done for now. In the long run we might want to revisit this
                // and come up with a better way.
                builder().Construct(constructor, args_regs, feedback_slot_index);
            }
        }

        // From here onwards, constructor_then_instance will hold the instance.
        Register instance = constructor_then_instance;
        builder().StoreAccumulatorInRegister(instance);
        builder().Bind(super_ctor_call_done);

        BuildInstanceInitializationAfterSuperCall(this_function, instance);
        builder().LoadAccumulatorWithRegister(instance);
    }

    void BuildInstanceInitializationAfterSuperCall(Register this_function, Register instance)
    {
        // Explicit calls to the super constructor using super() perform an
        // implicit binding assignment to the 'this' variable.
        //
        // Default constructors don't need have to do the assignment because
        // 'this' isn't accessed in default constructors.
        if (!IsDefaultConstructor(info().literal().kind()))
        {
            Variable var = closure_scope().GetReceiverScope().receiver();
            builder().LoadAccumulatorWithRegister(instance);
            BuildVariableAssignment(var, Token.Init, HoleCheckMode.kRequired);
        }

        // The constructor scope always needs ScopeInfo, so we are certain that
        // the first constructor scope found in the outer scope chain is the
        // scope that we are looking for for this super() call.
        // Note that this doesn't necessarily mean that the constructor needs
        // a context, if it doesn't this would get handled specially in
        // BuildPrivateBrandInitialization().
        DeclarationScope constructor_scope = info().scope().GetConstructorScope()!;

        // We can rely on the class_scope_has_private_brand bit to tell if the
        // constructor needs private brand initialization, and if that's
        // the case we are certain that its outer class scope requires a context to
        // keep the brand variable, so we can just get the brand variable
        // from the outer scope.
        if (constructor_scope.class_scope_has_private_brand())
        {
            Debug.Assert(constructor_scope.outer_scope()!.is_class_scope());
            ClassScope class_scope = constructor_scope.outer_scope()!.AsClassScope();
            Debug.Assert(class_scope.brand() is not null);
            Variable brand = class_scope.brand()!;
            BuildPrivateBrandInitialization(instance, brand);
        }

        // The derived constructor has the correct bit set always, so we
        // don't emit code to load and call the initializer if not
        // required.
        //
        // For the arrow function or eval case, we always emit code to load
        // and call the initializer.
        //
        // TODO(gsathya): In the future, we could tag nested arrow functions
        // or eval with the correct bit so that we do the load conditionally
        // if required.
        if (info().literal().requires_instance_members_initializer() ||
            !IsDerivedConstructor(info().literal().kind()))
        {
            BuildInstanceMemberInitialization(this_function, instance);
        }
    }

    void BuildGetAndCheckSuperConstructor(Register this_function, Register new_target, Register constructor,
                                          BytecodeLabel super_ctor_call_done)
    {
        bool omit_super_ctor = v8_flags.omit_default_ctors && IsDerivedConstructor(info().literal().kind());

        if (omit_super_ctor)
        {
            BuildSuperCallOptimization(this_function, new_target, constructor, super_ctor_call_done);
        }
        else
        {
            builder()
                .LoadAccumulatorWithRegister(this_function)
                .GetSuperConstructor(constructor);
        }

        // Check if the constructor is in fact a constructor.
        builder().ThrowIfNotSuperConstructor(constructor);
    }

    void BuildSuperCallOptimization(Register this_function, Register new_target, Register constructor_then_instance,
                                    BytecodeLabel super_ctor_call_done)
    {
        Debug.Assert(v8_flags.omit_default_ctors);
        RegisterList output = register_allocator().NewRegisterList(2);
        builder().FindNonDefaultConstructorOrConstruct(this_function, new_target, output);
        builder().MoveRegister(output[1], constructor_then_instance);
        builder().LoadAccumulatorWithRegister(output[0]).JumpIfTrue(ToBooleanMode.AlreadyBoolean, super_ctor_call_done);
    }

    public override void VisitCallNew(CallNew expr)
    {
        RegisterList args = register_allocator().NewGrowableRegisterList();

        // Load the constructor. It's in the first register in args for ease of
        // calling %reflect_construct if we have a non-final spread. For all other
        // cases it is popped before emitting the construct below.
        VisitAndPushIntoRegisterList(expr.expression(), ref args);

        // We compile the new differently depending on the presence of spreads and
        // their positions.
        //
        // If there is only one spread and it is the final argument, there is a
        // special ConstructWithSpread bytecode.
        //
        // If there is a non-final spread, we rewrite calls like
        //     new ctor(1, ...x, 2)
        // to
        //     %reflect_construct(ctor, [1, ...x, 2])
        CallBase.SpreadPosition spread_position = expr.spread_position();

        if (spread_position == CallBase.SpreadPosition.kHasNonFinalSpread)
        {
            BuildCreateArrayLiteral(expr.arguments(), null);
            builder().SetExpressionPosition(expr);
            builder()
                .StoreAccumulatorInRegister(register_allocator().GrowRegisterList(ref args))
                .CallJSRuntime((int)NativeContextFields.REFLECT_CONSTRUCT_INDEX, args);
            return;
        }

        Register constructor = args.FirstRegister();
        args = args.PopLeft();
        VisitArguments(expr.arguments(), ref args);

        // The accumulator holds new target which is the same as the
        // constructor for CallNew.
        builder().SetExpressionPosition(expr);
        builder().LoadAccumulatorWithRegister(constructor);

        int feedback_slot_index = feedback_index(feedback_spec().AddCallICSlot());
        if (spread_position == CallBase.SpreadPosition.kHasFinalSpread)
        {
            builder().ConstructWithSpread(constructor, args, feedback_slot_index);
        }
        else
        {
            Debug.Assert(spread_position == CallBase.SpreadPosition.kNoSpread);
            builder().Construct(constructor, args, feedback_slot_index);
        }
    }

    public override void VisitSuperCallForwardArgs(SuperCallForwardArgs expr)
    {
        using var register_scope = new RegisterAllocationScope(this);

        SuperCallReference super = expr.expression();
        Register this_function = VisitForRegisterValue(super.this_function_var());
        Register new_target = VisitForRegisterValue(super.new_target_var());

        // This register initially holds the constructor, then the instance.
        Register constructor_then_instance = register_allocator().NewRegister();

        var super_ctor_call_done = new BytecodeLabel();

        {
            Register constructor = constructor_then_instance;
            BuildGetAndCheckSuperConstructor(this_function, new_target, constructor, super_ctor_call_done);

            builder().LoadAccumulatorWithRegister(new_target);
            builder().SetExpressionPosition(expr);
            int feedback_slot_index = feedback_index(feedback_spec().AddCallICSlot());

            builder().ConstructForwardAllArgs(constructor, feedback_slot_index);
        }

        // From here onwards, constructor_then_instance holds the instance.
        Register instance = constructor_then_instance;
        builder().StoreAccumulatorInRegister(instance);
        builder().Bind(super_ctor_call_done);

        BuildInstanceInitializationAfterSuperCall(this_function, instance);
        builder().LoadAccumulatorWithRegister(instance);
    }

    public override void VisitCallRuntime(CallRuntime expr)
    {
        // Evaluate all arguments to the runtime call.
        RegisterList args = register_allocator().NewGrowableRegisterList();
        VisitArguments(expr.arguments(), ref args);
        FunctionId function_id = expr.function().function_id;
        builder().CallRuntime(function_id, args);
    }

    void VisitVoid(UnaryOperation expr)
    {
        VisitForEffect(expr.expression());
        builder().LoadUndefined();
    }

    void VisitForTypeOfValue(Expression expr)
    {
        if (expr.IsVariableProxy())
        {
            // Typeof does not throw a reference error on global variables, hence we
            // perform a non-contextual load in case the operand is a variable proxy.
            VariableProxy proxy = expr.AsVariableProxy()!;
            BuildVariableLoadForAccumulatorValue(proxy.var(), proxy.hole_check_mode(), TypeofMode.Inside);
        }
        else
        {
            VisitForAccumulatorValue(expr);
        }
    }

    void VisitTypeOf(UnaryOperation expr)
    {
        VisitForTypeOfValue(expr.expression());
        builder().TypeOf(feedback_index(feedback_spec().AddTypeOfSlot()));
        execution_result().SetResultIsInternalizedString();
    }

    void VisitNot(UnaryOperation expr)
    {
        if (execution_result().IsEffect())
        {
            VisitForEffect(expr.expression());
        }
        else if (execution_result().IsTest())
        {
            // No actual logical negation happening, we just swap the control flow, by
            // swapping the target labels and the fallthrough branch, and visit in the
            // same test result context.
            ExpressionResultScope test_result = execution_result().AsTest();
            test_result.InvertControlFlow();
            VisitInSameTestExecutionScope(expr.expression());
        }
        else
        {
            UnaryOperation? unary_op = expr.expression().AsUnaryOperation();
            if (unary_op is not null && unary_op.op() == Token.Not)
            {
                // Shortcut repeated nots, to capture the `!!foo` pattern for converting
                // expressions to booleans.
                TypeHint type_hint = VisitForAccumulatorValue(unary_op.expression());
                builder().ToBoolean(ToBooleanModeFromTypeHint(type_hint));
            }
            else
            {
                TypeHint type_hint = VisitForAccumulatorValue(expr.expression());
                builder().LogicalNot(ToBooleanModeFromTypeHint(type_hint));
            }
            // Always returns a boolean value.
            execution_result().SetResultIsBoolean();
        }
    }

    public override void VisitUnaryOperation(UnaryOperation expr)
    {
        switch (expr.op())
        {
            case Token.Not:
                VisitNot(expr);
                break;
            case Token.TypeOf:
                VisitTypeOf(expr);
                break;
            case Token.Void:
                VisitVoid(expr);
                break;
            case Token.Delete:
                VisitDelete(expr);
                break;
            case Token.Add:
                VisitForAccumulatorValue(expr.expression());
                builder().SetExpressionPosition(expr);
                builder().UnaryOperation(expr.op(), feedback_index(feedback_spec().AddBinaryOpICSlot()));
                break;
            case Token.Sub:
            case Token.BitNot:
                VisitForAccumulatorValue(expr.expression());
                builder().SetExpressionPosition(expr);
                builder().UnaryOperation(expr.op(), kFeedbackIsEmbedded);
                break;
            default:
                throw new UnreachableException();
        }
    }

    void VisitDelete(UnaryOperation unary)
    {
        Expression expr = unary.expression();
        if (expr.IsProperty())
        {
            // Delete of an object property is allowed both in sloppy
            // and strict modes.
            Property property = expr.AsProperty()!;
            Debug.Assert(!property.IsPrivateReference());
            if (property.IsSuperAccess())
            {
                // Delete of super access is not allowed.
                BuildThisVariableLoad();
                VisitForEffect(property.key());
                builder().CallRuntime(FunctionId.ThrowUnsupportedSuperError);
            }
            else
            {
                Register @object = VisitForRegisterValue(property.obj());
                VisitForAccumulatorValue(property.key());
                builder().Delete(@object, language_mode());
            }
        }
        else if (expr.IsOptionalChain())
        {
            Expression expr_inner = expr.AsOptionalChain()!.expression();
            if (expr_inner.IsProperty())
            {
                Property property = expr_inner.AsProperty()!;
                Debug.Assert(!property.IsPrivateReference());
                var done = new BytecodeLabel();
                using var label_scope = new OptionalChainNullLabelScope(this);
                VisitForAccumulatorValue(property.obj());
                if (property.is_optional_chain_link())
                {
                    int right_range = AllocateBlockCoverageSlotIfEnabled(property, SourceRangeKind.kRight);
                    builder().JumpIfUndefinedOrNull(label_scope.labels().New());
                    BuildIncrementBlockCoverageCounterIfEnabled(right_range);
                }
                Register @object = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(@object);
                if (property.is_optional_chain_link())
                {
                    VisitInHoleCheckElisionScopeForAccumulatorValue(property.key());
                }
                else
                {
                    VisitForAccumulatorValue(property.key());
                }
                builder().Delete(@object, language_mode());
                builder().Jump(done);
                label_scope.labels().Bind(builder());
                builder().LoadTrue();
                builder().Bind(done);
            }
            else
            {
                VisitForEffect(expr);
                builder().LoadTrue();
            }
        }
        else if (expr.IsVariableProxy() && !expr.AsVariableProxy()!.is_new_target())
        {
            // Delete of an unqualified identifier is allowed in sloppy mode but is
            // not allowed in strict mode.
            Debug.Assert(is_sloppy(language_mode()));
            Variable variable = expr.AsVariableProxy()!.var();
            switch (variable.location())
            {
                case VariableLocation.PARAMETER:
                case VariableLocation.LOCAL:
                case VariableLocation.CONTEXT:
                case VariableLocation.REPL_GLOBAL:
                {
                    // Deleting local var/let/const, context variables, and arguments
                    // does not have any effect.
                    builder().LoadFalse();
                    break;
                }
                case VariableLocation.UNALLOCATED:
                // TODO(adamk): Falling through to the runtime results in correct
                // behavior, but does unnecessary context-walking (since scope
                // analysis has already proven that the variable doesn't exist in
                // any non-global scope). Consider adding a DeleteGlobal bytecode
                // that knows how to deal with ScriptContexts as well as global
                // object properties.
                case VariableLocation.LOOKUP:
                {
                    Register name_reg = register_allocator().NewRegister();
                    builder()
                        .LoadLiteral(variable.raw_name())
                        .StoreAccumulatorInRegister(name_reg)
                        .CallRuntime(FunctionId.DeleteLookupSlot, name_reg);
                    break;
                }
                case VariableLocation.MODULE:
                    // Modules are always in strict mode and unqualified identifiers are not
                    // allowed in strict mode.
                    throw new UnreachableException();
            }
        }
        else
        {
            // Delete of an unresolvable reference, new.target, and this returns true.
            VisitForEffect(expr);
            builder().LoadTrue();
        }
    }

    public override void VisitCountOperation(CountOperation expr)
    {
        Debug.Assert(expr.expression().IsValidReferenceExpression());

        // Left-hand side can only be a property, a global or a variable slot.
        Property? property = expr.expression().AsProperty();
        AssignType assign_type = Property.GetAssignType(property);

        bool is_postfix = expr.is_postfix() && !execution_result().IsEffect();

        // Evaluate LHS expression and get old value.
        Register @object = Register.InvalidValue();
        Register key = Register.InvalidValue();
        Register old_value = Register.InvalidValue();
        RegisterList super_property_args = RegisterList.Empty;
        AstRawString? name = null;
        switch (assign_type)
        {
            case AssignType.NON_PROPERTY:
            {
                VariableProxy proxy = expr.expression().AsVariableProxy()!;
                BuildVariableLoadForAccumulatorValue(proxy.var(), proxy.hole_check_mode());
                break;
            }
            case AssignType.NAMED_PROPERTY:
            {
                @object = VisitForRegisterValue(property!.obj());
                name = property.key().AsLiteral()!.AsRawPropertyName();
                builder().LoadNamedProperty(@object, name, feedback_index(GetCachedLoadICSlot(property.obj(), name)));
                break;
            }
            case AssignType.KEYED_PROPERTY:
            {
                @object = VisitForRegisterValue(property!.obj());
                if (v8_flags.private_field_bytecodes && property.IsPrivateReference())
                {
                    Variable var = property.key().AsVariableProxy()!.var();
                    int depth = execution_context().ContextChainDepth(var.scope()!);
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
                    builder().GetPrivateField(context_reg, var.index(), depth, @object,
                                              feedback_index(feedback_spec().AddKeyedLoadICSlot()));
                    break;
                }
                // Use visit for accumulator here since we need the key in the accumulator
                // for the LoadKeyedProperty.
                key = register_allocator().NewRegister();
                VisitForAccumulatorValue(property.key());
                builder().StoreAccumulatorInRegister(key).LoadKeyedProperty(
                    @object, feedback_index(feedback_spec().AddKeyedLoadICSlot()));
                break;
            }
            case AssignType.NAMED_SUPER_PROPERTY:
            {
                super_property_args = register_allocator().NewRegisterList(4);
                RegisterList load_super_args = super_property_args.Truncate(3);
                BuildThisVariableLoad();
                builder().StoreAccumulatorInRegister(load_super_args[0]);
                BuildVariableLoad(property!.obj().AsSuperPropertyReference()!.home_object().var(),
                                  HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(load_super_args[1]);
                builder()
                    .LoadLiteral(property.key().AsLiteral()!.AsRawPropertyName())
                    .StoreAccumulatorInRegister(load_super_args[2])
                    .CallRuntime(FunctionId.LoadFromSuper, load_super_args);
                break;
            }
            case AssignType.KEYED_SUPER_PROPERTY:
            {
                super_property_args = register_allocator().NewRegisterList(4);
                RegisterList load_super_args = super_property_args.Truncate(3);
                BuildThisVariableLoad();
                builder().StoreAccumulatorInRegister(load_super_args[0]);
                BuildVariableLoad(property!.obj().AsSuperPropertyReference()!.home_object().var(),
                                  HoleCheckMode.kElided);
                builder().StoreAccumulatorInRegister(load_super_args[1]);
                VisitForRegisterValue(property.key(), load_super_args[2]);
                builder().CallRuntime(FunctionId.LoadKeyedFromSuper, load_super_args);
                break;
            }
            case AssignType.PRIVATE_METHOD:
            {
                @object = VisitForRegisterValue(property!.obj());
                BuildPrivateBrandCheck(property, @object);
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateMethodWrite, property);
                return;
            }
            case AssignType.PRIVATE_GETTER_ONLY:
            {
                @object = VisitForRegisterValue(property!.obj());
                BuildPrivateBrandCheck(property, @object);
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateSetterAccess, property);
                return;
            }
            case AssignType.PRIVATE_SETTER_ONLY:
            {
                @object = VisitForRegisterValue(property!.obj());
                BuildPrivateBrandCheck(property, @object);
                BuildInvalidPropertyAccess(MessageTemplate.InvalidPrivateGetterAccess, property);
                return;
            }
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                @object = VisitForRegisterValue(property!.obj());
                key = VisitForRegisterValue(property.key());
                BuildPrivateBrandCheck(property, @object);
                BuildPrivateGetterAccess(@object, key);
                break;
            }
            case AssignType.PRIVATE_DEBUG_DYNAMIC:
            {
                @object = VisitForRegisterValue(property!.obj());
                BuildPrivateDebugDynamicGet(property, @object);
                break;
            }
        }

        if (is_postfix)
        {
            // Save result for postfix expressions.
            FeedbackSlot count_slot = feedback_spec().AddBinaryOpICSlot();
            old_value = register_allocator().NewRegister();
            // Convert old value into a number before saving it.
            // TODO(ignition): Think about adding proper PostInc/PostDec bytecodes
            // instead of this ToNumeric + Inc/Dec dance.
            builder()
                .ToNumeric(feedback_index(count_slot))
                .StoreAccumulatorInRegister(old_value);
        }

        // Perform +1/-1 operation.
        builder().UnaryOperation(expr.op(), kFeedbackIsEmbedded);

        // Store the value.
        builder().SetExpressionPosition(expr);
        switch (assign_type)
        {
            case AssignType.NON_PROPERTY:
            {
                VariableProxy proxy = expr.expression().AsVariableProxy()!;
                BuildVariableAssignment(proxy.var(), expr.op(), proxy.hole_check_mode());
                break;
            }
            case AssignType.NAMED_PROPERTY:
            {
                FeedbackSlot slot = GetCachedStoreICSlot(property!.obj(), name!);
                Register value = Register.InvalidValue();
                if (!execution_result().IsEffect())
                {
                    value = register_allocator().NewRegister();
                    builder().StoreAccumulatorInRegister(value);
                }
                builder().SetNamedProperty(@object, name!, feedback_index(slot), language_mode());
                if (!execution_result().IsEffect())
                {
                    builder().LoadAccumulatorWithRegister(value);
                }
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
                if (v8_flags.private_field_bytecodes && property!.IsPrivateReference())
                {
                    Variable var = property.key().AsVariableProxy()!.var();
                    int depth = execution_context().ContextChainDepth(var.scope()!);
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
                    builder().SetPrivateField(context_reg, var.index(), depth, @object, feedback_index(slot));
                }
                else
                {
                    builder().SetKeyedProperty(@object, key, feedback_index(slot), language_mode());
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
                    .StoreAccumulatorInRegister(super_property_args[3])
                    .CallRuntime(FunctionId.StoreToSuper, super_property_args);
                break;
            }
            case AssignType.KEYED_SUPER_PROPERTY:
            {
                builder()
                    .StoreAccumulatorInRegister(super_property_args[3])
                    .CallRuntime(FunctionId.StoreKeyedToSuper, super_property_args);
                break;
            }
            case AssignType.PRIVATE_SETTER_ONLY:
            case AssignType.PRIVATE_GETTER_ONLY:
            case AssignType.PRIVATE_METHOD:
                throw new UnreachableException();
            case AssignType.PRIVATE_GETTER_AND_SETTER:
            {
                Register value = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(value);
                BuildPrivateSetterAccess(@object, key, value);
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
                BuildPrivateDebugDynamicSet(property!, @object, value);
                break;
            }
        }

        // Restore old value for postfix expressions.
        if (is_postfix)
        {
            builder().LoadAccumulatorWithRegister(old_value);
        }
    }

    public override void VisitBinaryOperation(BinaryOperation binop)
    {
        switch (binop.op())
        {
            case Token.Comma:
                VisitCommaExpression(binop);
                break;
            case Token.Or:
                VisitLogicalOrExpression(binop);
                break;
            case Token.And:
                VisitLogicalAndExpression(binop);
                break;
            case Token.Nullish:
                VisitNullishExpression(binop);
                break;
            default:
                VisitArithmeticExpression(binop);
                break;
        }
    }

    public override void VisitNaryOperation(NaryOperation expr)
    {
        switch (expr.op())
        {
            case Token.Comma:
                VisitNaryCommaExpression(expr);
                break;
            case Token.Or:
                VisitNaryLogicalOrExpression(expr);
                break;
            case Token.And:
                VisitNaryLogicalAndExpression(expr);
                break;
            case Token.Nullish:
                VisitNaryNullishExpression(expr);
                break;
            default:
                VisitNaryArithmeticExpression(expr);
                break;
        }
    }

    void BuildLiteralCompareNil(Token op, BytecodeArrayBuilder.NilValue nil)
    {
        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            switch (test_result.fallthrough())
            {
                case TestFallthrough.kThen:
                    builder().JumpIfNotNil(test_result.NewElseLabel(), op, nil);
                    break;
                case TestFallthrough.kElse:
                    builder().JumpIfNil(test_result.NewThenLabel(), op, nil);
                    break;
                case TestFallthrough.kNone:
                    builder()
                        .JumpIfNil(test_result.NewThenLabel(), op, nil)
                        .Jump(test_result.NewElseLabel());
                    break;
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            builder().CompareNil(op, nil);
        }
    }

    void BuildLiteralStrictCompareBoolean(Literal literal)
    {
        Debug.Assert(literal.IsBooleanLiteral());
        Register result = register_allocator().NewRegister();
        builder().StoreAccumulatorInRegister(result);
        builder().LoadBoolean(literal.AsBooleanLiteral());
        builder().CompareReference(result);
    }

    bool IsLocalVariableWithInternalizedStringHint(Expression expr)
    {
        VariableProxy? proxy = expr.AsVariableProxy();
        return proxy is not null && proxy.is_resolved() && proxy.var().IsStackLocal() &&
               GetTypeHintForLocalVariable(proxy.var()) == TypeHint.InternalizedString;
    }

    static bool IsTypeof(Expression expr)
    {
        UnaryOperation? maybe_unary = expr.AsUnaryOperation();
        return maybe_unary is not null && maybe_unary.op() == Token.TypeOf;
    }

    static bool IsCharU(AstRawString str) => str.length() == 1 && str.FirstCharacter() == 'u';

    static bool IsLiteralCompareTypeof(CompareOperation expr, out Expression? sub_expr,
                                       out TestTypeOfFlags.LiteralFlag flag, out bool negate)
    {
        // Note: The parser normalizes `typeof x !== 'string'` to
        // `!(typeof x === 'string')` at the AST level, so we only see Eq/EqStrict
        // here, never NotEq/NotEqStrict. The wrapping Not is handled by VisitNot,
        // which applies InvertControlFlow in test context.
        // See ParseBinaryContinuation in parser-base.h.
        Debug.Assert(expr.op() != Token.NotEq);
        Debug.Assert(expr.op() != Token.NotEqStrict);
        sub_expr = null;
        flag = TestTypeOfFlags.LiteralFlag.Other;
        negate = false;

        if (IsTypeof(expr.left()) && expr.right().IsStringLiteral())
        {
            Literal right_lit = expr.right().AsLiteral()!;

            if (Token.IsEqualityOp(expr.op()))
            {
                // typeof(x) === 'string'
                flag = TestTypeOfFlags.GetFlagForLiteral(right_lit.AsRawString().Value);
                negate = false;
            }
            else if (expr.op() == Token.GreaterThan && IsCharU(right_lit.AsRawString()))
            {
                // typeof(x) > 'u'
                // Minifier may convert `typeof(x) === 'undefined'` to this form,
                // since `undefined` is the only valid value that is greater than 'u'.
                // Check the test OnlyUndefinedGreaterThanU in bytecodes-unittest.cc
                flag = TestTypeOfFlags.LiteralFlag.Undefined;
                negate = false;
            }
            else if (expr.op() == Token.LessThan && IsCharU(right_lit.AsRawString()))
            {
                // typeof(x) < 'u'
                // Minifier may convert `typeof(x) !== 'undefined'` to this form,
                // since `undefined` is the only valid value that is greater than 'u'.
                flag = TestTypeOfFlags.LiteralFlag.Undefined;
                negate = true;
            }
            else
            {
                return false;
            }

            sub_expr = expr.left().AsUnaryOperation()!.expression();
            return true;
        }

        if (IsTypeof(expr.right()) && expr.left().IsStringLiteral())
        {
            Literal left_lit = expr.left().AsLiteral()!;

            if (Token.IsEqualityOp(expr.op()))
            {
                // 'string' === typeof(x)
                flag = TestTypeOfFlags.GetFlagForLiteral(left_lit.AsRawString().Value);
                negate = false;
            }
            else if (expr.op() == Token.LessThan && IsCharU(left_lit.AsRawString()))
            {
                // 'u' < typeof(x)
                flag = TestTypeOfFlags.LiteralFlag.Undefined;
                negate = false;
            }
            else if (expr.op() == Token.GreaterThan && IsCharU(left_lit.AsRawString()))
            {
                // 'u' > typeof(x)
                flag = TestTypeOfFlags.LiteralFlag.Undefined;
                negate = true;
            }
            else
            {
                return false;
            }

            sub_expr = expr.right().AsUnaryOperation()!.expression();
            return true;
        }

        return false;
    }

    public override void VisitCompareOperation(CompareOperation expr)
    {
        if (IsLiteralCompareTypeof(expr, out Expression? sub_expr, out TestTypeOfFlags.LiteralFlag flag,
                                   out bool negate))
        {
            // Emit a fast literal comparison for expressions of the form:
            // typeof(x) === 'string'.
            VisitForTypeOfValue(sub_expr!);
            builder().SetExpressionPosition(expr);
            if (flag == TestTypeOfFlags.LiteralFlag.Other)
            {
                Debug.Assert(!negate);
                builder().LoadFalse();
            }
            else
            {
                builder().CompareTypeOf(flag);
                if (negate)
                {
                    if (execution_result().IsTest())
                    {
                        execution_result().AsTest().InvertControlFlow();
                    }
                    else
                    {
                        builder().LogicalNot(ToBooleanMode.AlreadyBoolean);
                    }
                }
            }
        }
        else if (expr.IsLiteralStrictCompareBoolean(out sub_expr, out Literal? literal))
        {
            Debug.Assert(expr.op() == Token.EqStrict);
            VisitForAccumulatorValue(sub_expr!);
            builder().SetExpressionPosition(expr);
            BuildLiteralStrictCompareBoolean(literal!);
        }
        else if (expr.IsLiteralCompareUndefined(out sub_expr))
        {
            VisitForAccumulatorValue(sub_expr!);
            builder().SetExpressionPosition(expr);
            BuildLiteralCompareNil(expr.op(), BytecodeArrayBuilder.NilValue.UndefinedValue);
        }
        else if (expr.IsLiteralCompareNull(out sub_expr))
        {
            VisitForAccumulatorValue(sub_expr!);
            builder().SetExpressionPosition(expr);
            BuildLiteralCompareNil(expr.op(), BytecodeArrayBuilder.NilValue.NullValue);
        }
        else if (expr.IsLiteralCompareEqualVariable(out sub_expr, out literal) &&
                 IsLocalVariableWithInternalizedStringHint(sub_expr!))
        {
            builder().LoadLiteral(literal!.AsRawString());
            builder().CompareReference(GetRegisterForLocalVariable(sub_expr!.AsVariableProxy()!.var()));
        }
        else
        {
            if (expr.op() == Token.In && expr.left().IsPrivateName())
            {
                Variable var = expr.left().AsVariableProxy()!.var();
                if (IsPrivateMethodOrAccessorVariableMode(var.mode()))
                {
                    BuildPrivateMethodIn(var, expr.right());
                    return;
                }
                // For private fields, the code below does the right thing.
            }

            Register lhs = VisitForRegisterValue(expr.left());
            VisitForAccumulatorValue(expr.right());
            builder().SetExpressionPosition(expr);
            FeedbackSlot slot = default;
            if (expr.op() == Token.In)
            {
                slot = feedback_spec().AddKeyedHasICSlot();
            }
            else if (expr.op() == Token.InstanceOf)
            {
                slot = feedback_spec().AddInstanceOfSlot();
            }
            // feedback is embedded for other compare operations

            builder().CompareOperation(expr.op(), lhs, slot.IsInvalid() ? kFeedbackIsEmbedded : feedback_index(slot));
        }
        // Always returns a boolean value.
        execution_result().SetResultIsBoolean();
    }

    void VisitArithmeticExpression(BinaryOperation expr)
    {
        FeedbackSlot slot;

        // We special-case string concatenation when the result is used as a property
        // key. In this case, we know it will eventually be internalized and it's
        // better to do so early.
        //
        // For now, we handle only the specialized situation in which one side is a
        // string constant.
        // TODO(jgruber): Generalize. ConsString literals, property-key but no
        // string-literal, string-literal but no property-key.
        bool maybe_emit_specialized_string_add = expr.op() == Token.Add &&
                                                 execution_result().IsValueAsPropertyKey() &&
                                                 v8_flags.cache_property_key_string_adds;
        bool emit_add_string_constant_internalize = false;
        AddStringConstantAndInternalizeVariant as_variant = AddStringConstantAndInternalizeVariant.LhsIsStringConstant;
        if (maybe_emit_specialized_string_add)
        {
            // Is lhs a string constant?
            emit_add_string_constant_internalize = expr.left().IsLiteral() && expr.left().AsLiteral()!.IsRawString();
            if (!emit_add_string_constant_internalize)
            {
                // Is rhs a string constant?
                emit_add_string_constant_internalize =
                    expr.right().IsLiteral() && expr.right().AsLiteral()!.IsRawString();
                if (emit_add_string_constant_internalize)
                {
                    as_variant = AddStringConstantAndInternalizeVariant.RhsIsStringConstant;
                }
            }
        }

        if (expr.IsSmiLiteralOperation(out Expression? subexpr, out int literal))
        {
            TypeHint type_hint = VisitForAccumulatorValue(subexpr!);
            builder().SetExpressionPosition(expr);
            builder().BinaryOperationSmiLiteral(expr.op(), Smi.FromInt(literal), kFeedbackIsEmbedded);
            if (expr.op() == Token.Add && IsStringTypeHint(type_hint))
            {
                execution_result().SetResultIsString();
            }
        }
        else
        {
            TypeHint lhs_type = VisitForAccumulatorValue(expr.left());
            Register lhs = register_allocator().NewRegister();
            builder().StoreAccumulatorInRegister(lhs);
            TypeHint rhs_type = VisitForAccumulatorValue(expr.right());
            if (expr.op() == Token.Add && (IsStringTypeHint(lhs_type) || IsStringTypeHint(rhs_type)))
            {
                execution_result().SetResultIsString();
            }

            if (emit_add_string_constant_internalize)
            {
                slot = feedback_spec().AddStringAddAndInternalizeICSlot();
                // Subtle: Stack overflows can cause the AST to be visited only
                // partially. Visitation is eventually aborted and the resulting
                // bytecode discarded.
                Debug.Assert(HasStackOverflow() ||
                             IsStringTypeHint(as_variant == AddStringConstantAndInternalizeVariant.LhsIsStringConstant
                                                  ? lhs_type
                                                  : rhs_type));
                builder().SetExpressionPosition(expr);
                builder().Add_StringConstant_Internalize(expr.op(), lhs, feedback_index(slot), as_variant);
            }
            else
            {
                builder().SetExpressionPosition(expr);
                builder().BinaryOperation(expr.op(), lhs, kFeedbackIsEmbedded);
            }
        }
    }

    void VisitNaryArithmeticExpression(NaryOperation expr)
    {
        // TODO(leszeks): Add support for lhs smi in commutative ops.
        TypeHint type_hint = VisitForAccumulatorValue(expr.first());

        for (int i = 0; i < expr.subsequent_length(); ++i)
        {
            using var register_scope = new RegisterAllocationScope(this);
            if (expr.subsequent(i).IsSmiLiteral())
            {
                builder().SetExpressionPosition(expr.subsequent_op_position(i));
                builder().BinaryOperationSmiLiteral(expr.op(),
                                                    Smi.FromInt(expr.subsequent(i).AsLiteral()!.AsSmiLiteral()),
                                                    kFeedbackIsEmbedded);
            }
            else
            {
                Register lhs = register_allocator().NewRegister();
                builder().StoreAccumulatorInRegister(lhs);
                TypeHint rhs_hint = VisitForAccumulatorValue(expr.subsequent(i));
                if (IsStringTypeHint(rhs_hint)) type_hint = TypeHint.String;
                builder().SetExpressionPosition(expr.subsequent_op_position(i));
                builder().BinaryOperation(expr.op(), lhs, kFeedbackIsEmbedded);
            }
        }

        if (IsStringTypeHint(type_hint) && expr.op() == Token.Add)
        {
            // If any operand of an ADD is a String, a String is produced.
            execution_result().SetResultIsString();
        }
    }

    // Note: the actual spreading is performed by the surrounding expression's
    // visitor.
    public override void VisitSpread(Spread expr) => Visit(expr.expression());

    public override void VisitEmptyParentheses(EmptyParentheses expr) => throw new UnreachableException();

    public override void VisitImportCallExpression(ImportCallExpression expr)
    {
        int register_count = expr.import_options() is not null ? 4 : 3;
        // args is a list of [ function_closure, specifier, phase, import_options ].
        RegisterList args = register_allocator().NewRegisterList(register_count);

        builder().MoveRegister(Register.FunctionClosure(), args[0]);
        VisitForRegisterValue(expr.specifier(), args[1]);
        builder()
            .LoadLiteral(Smi.FromInt((int)expr.phase()))
            .StoreAccumulatorInRegister(args[2]);

        if (expr.import_options() is not null)
        {
            VisitForRegisterValue(expr.import_options()!, args[3]);
        }

        builder().CallRuntime(FunctionId.DynamicImportCall, args);
    }

    void BuildGetIterator(IteratorType hint)
    {
        if (hint == IteratorType.kAsync)
        {
            using var scope = new RegisterAllocationScope(this);

            Register obj = register_allocator().NewRegister();
            Register method = register_allocator().NewRegister();

            // Set method to GetMethod(obj, @@asyncIterator)
            builder().StoreAccumulatorInRegister(obj).LoadAsyncIteratorProperty(
                obj, feedback_index(feedback_spec().AddLoadICSlot()));

            var async_iterator_undefined = new BytecodeLabel();
            var done = new BytecodeLabel();
            builder().JumpIfUndefinedOrNull(async_iterator_undefined);

            // Let iterator be Call(method, obj)
            builder().StoreAccumulatorInRegister(method).CallProperty(
                method, new RegisterList(obj), feedback_index(feedback_spec().AddCallICSlot()));

            // If Type(iterator) is not Object, throw a TypeError exception.
            builder().JumpIfJSReceiver(done);
            builder().CallRuntime(FunctionId.ThrowSymbolAsyncIteratorInvalid);

            builder().Bind(async_iterator_undefined);
            // If method is undefined,
            //     Let syncMethod be GetMethod(obj, @@iterator)
            builder()
                .LoadIteratorProperty(obj, feedback_index(feedback_spec().AddLoadICSlot()))
                .StoreAccumulatorInRegister(method);

            //     Let syncIterator be Call(syncMethod, obj)
            builder().CallProperty(method, new RegisterList(obj), feedback_index(feedback_spec().AddCallICSlot()));

            // Return CreateAsyncFromSyncIterator(syncIterator)
            // alias `method` register as it's no longer used
            Register sync_iter = method;
            builder().StoreAccumulatorInRegister(sync_iter).CallRuntime(
                FunctionId.InlineCreateAsyncFromSyncIterator, sync_iter);

            builder().Bind(done);
        }
        else
        {
            {
                using var scope = new RegisterAllocationScope(this);

                Register obj = register_allocator().NewRegister();
                int load_feedback_index = feedback_index(feedback_spec().AddLoadICSlot());
                int call_feedback_index = feedback_index(feedback_spec().AddCallICSlot());

                // Let method be GetMethod(obj, @@iterator) and
                // iterator be Call(method, obj). If iterator is
                // not JSReceiver, then throw TypeError.
                builder().StoreAccumulatorInRegister(obj).GetIterator(obj, load_feedback_index, call_feedback_index);
            }
        }
    }

    // Returns an IteratorRecord which is valid for the lifetime of the current
    // register_allocation_scope.
    IteratorRecord BuildGetIteratorRecord(Register next, Register @object, IteratorType hint)
    {
        Debug.Assert(next.IsValid && @object.IsValid);
        BuildGetIterator(hint);

        builder()
            .StoreAccumulatorInRegister(@object)
            .LoadNamedProperty(@object, ast_string_constants().next_string,
                               feedback_index(feedback_spec().AddLoadICSlot()))
            .StoreAccumulatorInRegister(next);
        return new IteratorRecord(@object, next, hint);
    }

    IteratorRecord BuildGetIteratorRecord(IteratorType hint)
    {
        Register next = register_allocator().NewRegister();
        Register @object = register_allocator().NewRegister();
        return BuildGetIteratorRecord(next, @object, hint);
    }

    void BuildIteratorNext(IteratorRecord iterator, Register next_result)
    {
        Debug.Assert(next_result.IsValid);
        builder().CallProperty(iterator.next(), new RegisterList(iterator.@object()),
                               feedback_index(feedback_spec().AddCallICSlot()));

        // TODO(408061015): Optimize AsyncArrayIterators by splitting the optimized
        // bytecode to get the next result, await the result, and then get value and
        // done.
        if (iterator.type() == IteratorType.kAsync)
        {
            BuildAwait();
        }

        var is_object = new BytecodeLabel();
        builder()
            .StoreAccumulatorInRegister(next_result)
            .JumpIfJSReceiver(is_object)
            .CallRuntime(FunctionId.ThrowIteratorResultNotAnObject, next_result)
            .Bind(is_object);
    }

    void BuildCallIteratorMethod(Register iterator, AstRawString method_name, RegisterList receiver_and_args,
                                 BytecodeLabel if_called, BytecodeLabels if_notcalled)
    {
        using var register_scope = new RegisterAllocationScope(this);

        Register method = register_allocator().NewRegister();
        FeedbackSlot slot = feedback_spec().AddLoadICSlot();
        builder()
            .LoadNamedProperty(iterator, method_name, feedback_index(slot))
            .JumpIfUndefinedOrNull(if_notcalled.New())
            .StoreAccumulatorInRegister(method)
            .CallProperty(method, receiver_and_args, feedback_index(feedback_spec().AddCallICSlot()))
            .Jump(if_called);
    }

    void BuildIteratorClose(IteratorRecord iterator, Expression? expr = null)
    {
        using var register_scope = new RegisterAllocationScope(this);
        var done = new BytecodeLabels();
        var if_called = new BytecodeLabel();
        var args = new RegisterList(iterator.@object());
        BuildCallIteratorMethod(iterator.@object(), ast_string_constants().return_string, args, if_called, done);
        builder().Bind(if_called);

        if (iterator.type() == IteratorType.kAsync)
        {
            Debug.Assert(expr is not null);
            BuildAwait(expr!.position());
        }

        builder().JumpIfJSReceiver(done.New());
        {
            using var inner_register_scope = new RegisterAllocationScope(this);
            Register return_result = register_allocator().NewRegister();
            builder()
                .StoreAccumulatorInRegister(return_result)
                .CallRuntime(FunctionId.ThrowIteratorResultNotAnObject, return_result);
        }

        done.Bind(builder());
    }

    public override void VisitGetTemplateObject(GetTemplateObject expr)
    {
        builder().SetExpressionPosition(expr);
        int entry = builder().AllocateDeferredConstantPoolEntry();
        _templateObjects.Add((expr, entry));
        FeedbackSlot literal_slot = feedback_spec().AddLiteralSlot();
        builder().GetTemplateObject(entry, feedback_index(literal_slot));
    }

    public override void VisitTemplateLiteral(TemplateLiteral expr)
    {
        List<AstRawString> parts = expr.string_parts();
        List<Expression> substitutions = expr.substitutions();
        // Template strings with no substitutions are turned into StringLiterals.
        Debug.Assert(substitutions.Count > 0);
        Debug.Assert(parts.Count == substitutions.Count + 1);

        // Generate string concatenation.
        Register last_part = register_allocator().NewRegister();
        bool last_part_valid = false;

        builder().SetExpressionPosition(expr);
        for (int i = 0; i < substitutions.Count; ++i)
        {
            if (i != 0)
            {
                builder().StoreAccumulatorInRegister(last_part);
                last_part_valid = true;
            }

            if (!parts[i].IsEmpty())
            {
                builder().LoadLiteral(parts[i]);
                if (last_part_valid)
                {
                    builder().BinaryOperation(Token.Add, last_part, kFeedbackIsEmbedded);
                }
                builder().StoreAccumulatorInRegister(last_part);
                last_part_valid = true;
            }

            TypeHint type_hint = VisitForAccumulatorValue(substitutions[i]);
            if (!IsStringTypeHint(type_hint))
            {
                builder().ToString();
            }
            if (last_part_valid)
            {
                builder().BinaryOperation(Token.Add, last_part, kFeedbackIsEmbedded);
            }
            last_part_valid = false;
        }

        if (!parts[^1].IsEmpty())
        {
            builder().StoreAccumulatorInRegister(last_part);
            builder().LoadLiteral(parts[^1]);
            builder().BinaryOperation(Token.Add, last_part, kFeedbackIsEmbedded);
        }
    }

    void BuildThisVariableLoad()
    {
        DeclarationScope receiver_scope = closure_scope().GetReceiverScope();
        Variable var = receiver_scope.receiver();
        // TODO(littledan): implement 'this' hole check elimination.
        HoleCheckMode hole_check_mode = IsDerivedConstructor(receiver_scope.function_kind())
            ? HoleCheckMode.kRequired
            : HoleCheckMode.kElided;
        BuildVariableLoad(var, hole_check_mode);
    }

    public override void VisitThisExpression(ThisExpression expr) => BuildThisVariableLoad();

    // Handled by VisitCall().
    public override void VisitSuperCallReference(SuperCallReference expr) => throw new UnreachableException();

    // Handled by VisitAssignment(), VisitCall(), VisitDelete() and
    // VisitPropertyLoad().
    public override void VisitSuperPropertyReference(SuperPropertyReference expr) =>
        throw new UnreachableException();

    void VisitCommaExpression(BinaryOperation binop)
    {
        VisitForEffect(binop.left());
        builder().SetExpressionAsStatementPosition(binop.right());
        Visit(binop.right());
    }

    void VisitNaryCommaExpression(NaryOperation expr)
    {
        Debug.Assert(expr.subsequent_length() > 0);

        VisitForEffect(expr.first());
        for (int i = 0; i < expr.subsequent_length() - 1; ++i)
        {
            builder().SetExpressionAsStatementPosition(expr.subsequent(i));
            VisitForEffect(expr.subsequent(i));
        }
        builder().SetExpressionAsStatementPosition(expr.subsequent(expr.subsequent_length() - 1));
        Visit(expr.subsequent(expr.subsequent_length() - 1));
    }

    void VisitLogicalTestSubExpression(Token token, Expression expr, BytecodeLabels then_labels,
                                       BytecodeLabels else_labels, int coverage_slot)
    {
        Debug.Assert(token == Token.Or || token == Token.And || token == Token.Nullish);

        var test_next = new BytecodeLabels();
        if (token == Token.Or)
        {
            VisitForTest(expr, then_labels, test_next, TestFallthrough.kElse);
        }
        else if (token == Token.And)
        {
            VisitForTest(expr, test_next, else_labels, TestFallthrough.kThen);
        }
        else
        {
            Debug.Assert(token == Token.Nullish);
            VisitForNullishTest(expr, then_labels, test_next, else_labels);
        }
        test_next.Bind(builder());

        BuildIncrementBlockCoverageCounterIfEnabled(coverage_slot);
    }

    void VisitLogicalTest(Token token, Expression left, Expression right, int right_coverage_slot)
    {
        Debug.Assert(token == Token.Or || token == Token.And || token == Token.Nullish);
        ExpressionResultScope test_result = execution_result().AsTest();
        BytecodeLabels then_labels = test_result.then_labels();
        BytecodeLabels else_labels = test_result.else_labels();
        TestFallthrough fallthrough = test_result.fallthrough();

        VisitLogicalTestSubExpression(token, left, then_labels, else_labels, right_coverage_slot);
        // The last test has the same then, else and fallthrough as the parent test.
        using var elider = new HoleCheckElisionScope(this);
        VisitForTest(right, then_labels, else_labels, fallthrough);
    }

    void VisitNaryLogicalTest(Token token, NaryOperation expr, NaryCodeCoverageSlots coverage_slots)
    {
        Debug.Assert(token == Token.Or || token == Token.And || token == Token.Nullish);
        Debug.Assert(expr.subsequent_length() > 0);

        ExpressionResultScope test_result = execution_result().AsTest();
        BytecodeLabels then_labels = test_result.then_labels();
        BytecodeLabels else_labels = test_result.else_labels();
        TestFallthrough fallthrough = test_result.fallthrough();

        VisitLogicalTestSubExpression(token, expr.first(), then_labels, else_labels, coverage_slots.GetSlotFor(0));
        using var elider = new HoleCheckElisionScope(this);
        for (int i = 0; i < expr.subsequent_length() - 1; ++i)
        {
            VisitLogicalTestSubExpression(token, expr.subsequent(i), then_labels, else_labels,
                                          coverage_slots.GetSlotFor(i + 1));
        }
        // The last test has the same then, else and fallthrough as the parent test.
        VisitForTest(expr.subsequent(expr.subsequent_length() - 1), then_labels, else_labels, fallthrough);
    }

    bool VisitLogicalOrSubExpression(Expression expr, BytecodeLabels end_labels, int coverage_slot)
    {
        if (expr.ToBooleanIsTrue())
        {
            VisitForAccumulatorValue(expr);
            end_labels.Bind(builder());
            return true;
        }
        else if (!expr.ToBooleanIsFalse())
        {
            TypeHint type_hint = VisitForAccumulatorValue(expr);
            builder().JumpIfTrue(ToBooleanModeFromTypeHint(type_hint), end_labels.New());
        }

        BuildIncrementBlockCoverageCounterIfEnabled(coverage_slot);

        return false;
    }

    bool VisitLogicalAndSubExpression(Expression expr, BytecodeLabels end_labels, int coverage_slot)
    {
        if (expr.ToBooleanIsFalse())
        {
            VisitForAccumulatorValue(expr);
            end_labels.Bind(builder());
            return true;
        }
        else if (!expr.ToBooleanIsTrue())
        {
            TypeHint type_hint = VisitForAccumulatorValue(expr);
            builder().JumpIfFalse(ToBooleanModeFromTypeHint(type_hint), end_labels.New());
        }

        BuildIncrementBlockCoverageCounterIfEnabled(coverage_slot);

        return false;
    }

    bool VisitNullishSubExpression(Expression expr, BytecodeLabels end_labels, int coverage_slot)
    {
        if (expr.IsLiteralButNotNullOrUndefined())
        {
            VisitForAccumulatorValue(expr);
            end_labels.Bind(builder());
            return true;
        }
        else if (!expr.IsNullOrUndefinedLiteral())
        {
            VisitForAccumulatorValue(expr);
            var is_null_or_undefined = new BytecodeLabel();
            builder()
                .JumpIfUndefinedOrNull(is_null_or_undefined)
                .Jump(end_labels.New());
            builder().Bind(is_null_or_undefined);
        }

        BuildIncrementBlockCoverageCounterIfEnabled(coverage_slot);

        return false;
    }

    void VisitLogicalOrExpression(BinaryOperation binop)
    {
        Expression left = binop.left();
        Expression right = binop.right();

        int right_coverage_slot = AllocateBlockCoverageSlotIfEnabled(binop, SourceRangeKind.kRight);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (left.ToBooleanIsTrue())
            {
                builder().Jump(test_result.NewThenLabel());
            }
            else if (left.ToBooleanIsFalse() && right.ToBooleanIsFalse())
            {
                BuildIncrementBlockCoverageCounterIfEnabled(right_coverage_slot);
                builder().Jump(test_result.NewElseLabel());
            }
            else
            {
                VisitLogicalTest(Token.Or, left, right, right_coverage_slot);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitLogicalOrSubExpression(left, end_labels, right_coverage_slot))
            {
                return;
            }
            VisitInHoleCheckElisionScopeForAccumulatorValue(right);
            end_labels.Bind(builder());
        }
    }

    void VisitNaryLogicalOrExpression(NaryOperation expr)
    {
        Expression first = expr.first();
        Debug.Assert(expr.subsequent_length() > 0);

        var coverage_slots = new NaryCodeCoverageSlots(this, expr);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (first.ToBooleanIsTrue())
            {
                builder().Jump(test_result.NewThenLabel());
            }
            else
            {
                VisitNaryLogicalTest(Token.Or, expr, coverage_slots);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitLogicalOrSubExpression(first, end_labels, coverage_slots.GetSlotFor(0)))
            {
                return;
            }

            using var elider = new HoleCheckElisionScope(this);
            for (int i = 0; i < expr.subsequent_length() - 1; ++i)
            {
                if (VisitLogicalOrSubExpression(expr.subsequent(i), end_labels, coverage_slots.GetSlotFor(i + 1)))
                {
                    return;
                }
            }
            // We have to visit the last value even if it's true, because we need its
            // actual value.
            VisitForAccumulatorValue(expr.subsequent(expr.subsequent_length() - 1));
            end_labels.Bind(builder());
        }
    }

    void VisitLogicalAndExpression(BinaryOperation binop)
    {
        Expression left = binop.left();
        Expression right = binop.right();

        int right_coverage_slot = AllocateBlockCoverageSlotIfEnabled(binop, SourceRangeKind.kRight);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (left.ToBooleanIsFalse())
            {
                builder().Jump(test_result.NewElseLabel());
            }
            else if (left.ToBooleanIsTrue() && right.ToBooleanIsTrue())
            {
                BuildIncrementBlockCoverageCounterIfEnabled(right_coverage_slot);
                builder().Jump(test_result.NewThenLabel());
            }
            else
            {
                VisitLogicalTest(Token.And, left, right, right_coverage_slot);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitLogicalAndSubExpression(left, end_labels, right_coverage_slot))
            {
                return;
            }
            VisitInHoleCheckElisionScopeForAccumulatorValue(right);
            end_labels.Bind(builder());
        }
    }

    void VisitNaryLogicalAndExpression(NaryOperation expr)
    {
        Expression first = expr.first();
        Debug.Assert(expr.subsequent_length() > 0);

        var coverage_slots = new NaryCodeCoverageSlots(this, expr);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (first.ToBooleanIsFalse())
            {
                builder().Jump(test_result.NewElseLabel());
            }
            else
            {
                VisitNaryLogicalTest(Token.And, expr, coverage_slots);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitLogicalAndSubExpression(first, end_labels, coverage_slots.GetSlotFor(0)))
            {
                return;
            }
            using var elider = new HoleCheckElisionScope(this);
            for (int i = 0; i < expr.subsequent_length() - 1; ++i)
            {
                if (VisitLogicalAndSubExpression(expr.subsequent(i), end_labels, coverage_slots.GetSlotFor(i + 1)))
                {
                    return;
                }
            }
            // We have to visit the last value even if it's false, because we need its
            // actual value.
            VisitForAccumulatorValue(expr.subsequent(expr.subsequent_length() - 1));
            end_labels.Bind(builder());
        }
    }

    void VisitNullishExpression(BinaryOperation binop)
    {
        Expression left = binop.left();
        Expression right = binop.right();

        int right_coverage_slot = AllocateBlockCoverageSlotIfEnabled(binop, SourceRangeKind.kRight);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (left.IsLiteralButNotNullOrUndefined() && left.ToBooleanIsTrue())
            {
                builder().Jump(test_result.NewThenLabel());
            }
            else if (left.IsNullOrUndefinedLiteral() && right.IsNullOrUndefinedLiteral())
            {
                BuildIncrementBlockCoverageCounterIfEnabled(right_coverage_slot);
                builder().Jump(test_result.NewElseLabel());
            }
            else
            {
                VisitLogicalTest(Token.Nullish, left, right, right_coverage_slot);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitNullishSubExpression(left, end_labels, right_coverage_slot))
            {
                return;
            }
            VisitInHoleCheckElisionScopeForAccumulatorValue(right);
            end_labels.Bind(builder());
        }
    }

    void VisitNaryNullishExpression(NaryOperation expr)
    {
        Expression first = expr.first();
        Debug.Assert(expr.subsequent_length() > 0);

        var coverage_slots = new NaryCodeCoverageSlots(this, expr);

        if (execution_result().IsTest())
        {
            ExpressionResultScope test_result = execution_result().AsTest();
            if (first.IsLiteralButNotNullOrUndefined() && first.ToBooleanIsTrue())
            {
                builder().Jump(test_result.NewThenLabel());
            }
            else
            {
                VisitNaryLogicalTest(Token.Nullish, expr, coverage_slots);
            }
            test_result.SetResultConsumedByTest();
        }
        else
        {
            var end_labels = new BytecodeLabels();
            if (VisitNullishSubExpression(first, end_labels, coverage_slots.GetSlotFor(0)))
            {
                return;
            }
            using var elider = new HoleCheckElisionScope(this);
            for (int i = 0; i < expr.subsequent_length() - 1; ++i)
            {
                if (VisitNullishSubExpression(expr.subsequent(i), end_labels, coverage_slots.GetSlotFor(i + 1)))
                {
                    return;
                }
            }
            // We have to visit the last value even if it's nullish, because we need its
            // actual value.
            VisitForAccumulatorValue(expr.subsequent(expr.subsequent_length() - 1));
            end_labels.Bind(builder());
        }
    }

    void BuildNewLocalActivationContext()
    {
        using ExpressionResultScope value_execution_result = ValueResultScope();
        Scope scope = closure_scope();
        if (current_scope() != closure_scope())
        {
            throw new InvalidOperationException("CHECK_EQ(current_scope(), closure_scope())");
        }

        // Create the appropriate context.
        Debug.Assert(scope.is_function_scope() || scope.is_eval_scope());
        int slot_count = scope.num_heap_slots() - ContextSlots.MIN_CONTEXT_SLOTS;
        if (slot_count <= MaximumFunctionContextSlots())
        {
            switch (scope.scope_type())
            {
                case ScopeType.EVAL_SCOPE:
                    builder().CreateEvalContext(scope, slot_count);
                    break;
                case ScopeType.FUNCTION_SCOPE:
                    builder().CreateFunctionContext(scope, slot_count);
                    break;
                default:
                    throw new UnreachableException();
            }
        }
        else
        {
            Register arg = register_allocator().NewRegister();
            builder().LoadLiteral(scope).StoreAccumulatorInRegister(arg).CallRuntime(FunctionId.NewFunctionContext,
                                                                                    arg);
            register_allocator().ReleaseRegister(arg);
        }
    }

    // ConstructorBuiltins::MaximumFunctionContextSlots(): kMaximumSlots is
    // (kMaxRegularHeapObjectSize - Context::kTodoHeaderSize) / kTaggedSize - 1
    // with 256 KB pages and pointer compression: (131072 - 16) / 4 - 1.
    int MaximumFunctionContextSlots() => v8_flags.test_small_max_function_context_stub_size ? 10 : 32763;

    void BuildLocalActivationContextInitialization()
    {
        DeclarationScope scope = closure_scope();

        if (scope.has_this_declaration() && scope.receiver().IsContextSlot())
        {
            Variable variable = scope.receiver();
            Register receiver = builder().Receiver();
            // Context variable (at bottom of the context chain).
            Debug.Assert(scope.ContextChainLength(variable.scope()) == 0);
            builder().LoadAccumulatorWithRegister(receiver).StoreContextSlot(execution_context().reg(), variable, 0);
        }

        // Copy parameters into context if necessary.
        int num_parameters = scope.num_parameters();
        for (int i = 0; i < num_parameters; i++)
        {
            Variable variable = scope.parameter(i);
            if (!variable.IsContextSlot()) continue;

            Register parameter = builder().Parameter(i);
            // Context variable (at bottom of the context chain).
            Debug.Assert(scope.ContextChainLength(variable.scope()) == 0);
            builder().LoadAccumulatorWithRegister(parameter).StoreContextSlot(execution_context().reg(), variable, 0);
        }
    }

    void BuildNewLocalBlockContext(Scope scope)
    {
        using ExpressionResultScope value_execution_result = ValueResultScope();
        Debug.Assert(scope.is_block_scope());

        builder().CreateBlockContext(scope);
    }

    void BuildNewLocalWithContext(Scope scope)
    {
        using ExpressionResultScope value_execution_result = ValueResultScope();

        Register extension_object = register_allocator().NewRegister();

        builder().ToObject(extension_object);
        builder().CreateWithContext(extension_object, scope);

        register_allocator().ReleaseRegister(extension_object);
    }

    void BuildNewLocalCatchContext(Scope scope)
    {
        using ExpressionResultScope value_execution_result = ValueResultScope();
        Debug.Assert(scope.catch_variable().IsContextSlot());

        Register exception = register_allocator().NewRegister();
        builder().StoreAccumulatorInRegister(exception);
        builder().CreateCatchContext(exception, scope);
        register_allocator().ReleaseRegister(exception);
    }

    void VisitLiteralAccessor(LiteralProperty? property, Register value_out)
    {
        if (property is null)
        {
            builder().LoadNull().StoreAccumulatorInRegister(value_out);
        }
        else
        {
            VisitForRegisterValue(property.value(), value_out);
        }
    }

    void VisitArgumentsObject(Variable? variable)
    {
        if (variable is null) return;

        Debug.Assert(variable.IsContextSlot() || variable.IsStackAllocated());

        // Allocate and initialize a new arguments object and assign to the
        // {arguments} variable.
        builder().CreateArguments(closure_scope().GetArgumentsType());
        BuildVariableAssignment(variable, Token.Assign, HoleCheckMode.kElided);
    }

    void VisitRestArgumentsArray(Variable? rest)
    {
        if (rest is null) return;

        // Allocate and initialize a new rest parameter and assign to the {rest}
        // variable.
        builder().CreateArguments(CreateArgumentsType.kRestParameter);
        Debug.Assert(rest.IsContextSlot() || rest.IsStackAllocated());
        BuildVariableAssignment(rest, Token.Assign, HoleCheckMode.kElided);
    }

    void VisitThisFunctionVariable(Variable? variable)
    {
        if (variable is null) return;

        // Store the closure we were called with in the given variable.
        builder().LoadAccumulatorWithRegister(Register.FunctionClosure());
        BuildVariableAssignment(variable, Token.Init, HoleCheckMode.kElided);
    }

    void VisitNewTargetVariable(Variable? variable)
    {
        if (variable is null) return;

        // The generator resume trampoline abuses the new.target register
        // to pass in the generator object.  In ordinary calls, new.target is always
        // undefined because generator functions are non-constructible, so don't
        // assign anything to the new.target variable.
        if (IsResumableFunction(info().literal().kind())) return;

        if (variable.location() == VariableLocation.LOCAL)
        {
            // The new.target register was already assigned by entry trampoline.
            Debug.Assert(incoming_new_target().Index == GetRegisterForLocalVariable(variable).Index);
            return;
        }

        // Store the new target we were called with in the given variable.
        builder().LoadAccumulatorWithRegister(incoming_new_target());
        BuildVariableAssignment(variable, Token.Init, HoleCheckMode.kElided);
    }

    // Create a generator object if necessary and initialize the
    // {.generator_object} variable.
    void BuildGeneratorObjectVariableInitialization()
    {
        Debug.Assert(IsResumableFunction(info().literal().kind()));

        Variable generator_object_var = closure_scope().generator_object_var()!;
        using var register_scope = new RegisterAllocationScope(this);
        RegisterList args = register_allocator().NewRegisterList(2);
        FunctionId function_id =
            (IsAsyncFunction(info().literal().kind()) && !IsAsyncGeneratorFunction(info().literal().kind())) ||
            IsModuleWithTopLevelAwait(info().literal().kind())
                ? FunctionId.InlineAsyncFunctionEnter
                : FunctionId.InlineCreateJSGeneratorObject;
        builder()
            .MoveRegister(Register.FunctionClosure(), args[0])
            .MoveRegister(builder().Receiver(), args[1])
            .CallRuntime(function_id, args)
            .StoreAccumulatorInRegister(generator_object());

        if (generator_object_var.location() == VariableLocation.LOCAL)
        {
            // The generator object register is already set to the variable's local
            // register.
            Debug.Assert(generator_object().Index == GetRegisterForLocalVariable(generator_object_var).Index);
        }
        else
        {
            BuildVariableAssignment(generator_object_var, Token.Init, HoleCheckMode.kElided);
        }
    }

    void BuildPushUndefinedIntoRegisterList(ref RegisterList reg_list)
    {
        Register reg = register_allocator().GrowRegisterList(ref reg_list);
        builder().LoadUndefined().StoreAccumulatorInRegister(reg);
    }

    void BuildLoadPropertyKey(LiteralProperty property, Register out_reg)
    {
        if (property.key().IsStringLiteral())
        {
            builder()
                .LoadLiteral(property.key().AsLiteral()!.AsRawString())
                .StoreAccumulatorInRegister(out_reg);
        }
        else
        {
            VisitForAccumulatorValue(property.key());
            builder().ToName().StoreAccumulatorInRegister(out_reg);
        }
    }

    int AllocateBlockCoverageSlotIfEnabled(AstNode node, SourceRangeKind kind) =>
        _blockCoverageBuilder is null
            ? BlockCoverageBuilder.kNoCoverageArraySlot
            : _blockCoverageBuilder.AllocateBlockCoverageSlot(node, kind);

    int AllocateNaryBlockCoverageSlotIfEnabled(NaryOperation node, int index) =>
        _blockCoverageBuilder is null
            ? BlockCoverageBuilder.kNoCoverageArraySlot
            : _blockCoverageBuilder.AllocateNaryBlockCoverageSlot(node, index);

    int AllocateConditionalChainBlockCoverageSlotIfEnabled(ConditionalChain node, SourceRangeKind kind, int index) =>
        _blockCoverageBuilder is null
            ? BlockCoverageBuilder.kNoCoverageArraySlot
            : _blockCoverageBuilder.AllocateConditionalChainBlockCoverageSlot(node, kind, index);

    void BuildIncrementBlockCoverageCounterIfEnabled(AstNode node, SourceRangeKind kind)
    {
        if (_blockCoverageBuilder is null) return;
        _blockCoverageBuilder.IncrementBlockCounter(node, kind);
    }

    void BuildIncrementBlockCoverageCounterIfEnabled(int coverage_array_slot) =>
        _blockCoverageBuilder?.IncrementBlockCounter(coverage_array_slot);

    // Visits the expression |expr| and places the result in the accumulator.
    TypeHint VisitForAccumulatorValue(Expression expr)
    {
        using ExpressionResultScope accumulator_scope = ValueResultScope();
        return VisitForAccumulatorValueImpl(expr, accumulator_scope);
    }

    TypeHint VisitForAccumulatorValueAsPropertyKey(Expression expr)
    {
        using ExpressionResultScope accumulator_scope =
            ExpressionResultScope.Enter(this, ExpressionResultScope.Kind.kValueAsPropertyKey);
        return VisitForAccumulatorValueImpl(expr, accumulator_scope);
    }

    TypeHint VisitForAccumulatorValueImpl(Expression expr, ExpressionResultScope accumulator_scope)
    {
        Visit(expr);
        // Record the type hint for the result of current expression in accumulator.
        TypeHint type_hint = accumulator_scope.type_hint();
        BytecodeRegisterOptimizer? optimizer = builder().GetRegisterOptimizer();
        if (optimizer is not null && type_hint != TypeHint.Unknown)
        {
            optimizer.SetTypeHintForAccumulator(type_hint);
        }
        return type_hint;
    }

    void VisitForAccumulatorValueOrTheHole(Expression? expr)
    {
        if (expr is null)
        {
            builder().LoadTheHole();
        }
        else
        {
            VisitForAccumulatorValue(expr);
        }
    }

    // Visits the expression |expr| and discards the result.
    void VisitForEffect(Expression expr)
    {
        using ExpressionResultScope effect_scope = EffectResultScope();
        Visit(expr);
    }

    // Visits the expression |expr| and returns the register containing
    // the expression result.
    Register VisitForRegisterValue(Expression expr)
    {
        VisitForAccumulatorValue(expr);
        Register result = register_allocator().NewRegister();
        builder().StoreAccumulatorInRegister(result);
        return result;
    }

    // Visits the expression |expr| and stores the expression result in
    // |destination|.
    void VisitForRegisterValue(Expression expr, Register destination)
    {
        using ExpressionResultScope register_scope = ValueResultScope();
        Visit(expr);
        builder().StoreAccumulatorInRegister(destination);
    }

    // Visits the expression |expr| and pushes the result into a new register
    // added to the end of |reg_list|.
    void VisitAndPushIntoRegisterList(Expression expr, ref RegisterList reg_list)
    {
        {
            using ExpressionResultScope register_scope = ValueResultScope();
            Visit(expr);
        }
        // Grow the register list after visiting the expression to avoid reserving
        // the register across the expression evaluation, which could cause memory
        // leaks for deep expressions due to dead objects being kept alive by pointers
        // in registers.
        Register destination = register_allocator().GrowRegisterList(ref reg_list);
        builder().StoreAccumulatorInRegister(destination);
    }

    void BuildTest(ToBooleanMode mode, BytecodeLabels then_labels, BytecodeLabels else_labels,
                   TestFallthrough fallthrough)
    {
        switch (fallthrough)
        {
            case TestFallthrough.kThen:
                builder().JumpIfFalse(mode, else_labels.New());
                break;
            case TestFallthrough.kElse:
                builder().JumpIfTrue(mode, then_labels.New());
                break;
            case TestFallthrough.kNone:
                builder().JumpIfTrue(mode, then_labels.New());
                builder().Jump(else_labels.New());
                break;
        }
    }

    // Visits the expression |expr| for testing its boolean value and jumping to the
    // |then| or |other| label depending on value and short-circuit semantics
    void VisitForTest(Expression expr, BytecodeLabels then_labels, BytecodeLabels else_labels,
                      TestFallthrough fallthrough)
    {
        bool result_consumed;
        TypeHint type_hint;
        {
            // To make sure that all temporary registers are returned before generating
            // jumps below, we ensure that the result scope is deleted before doing so.
            // Dead registers might be materialized otherwise.
            using ExpressionResultScope test_result =
                ExpressionResultScope.EnterTest(this, then_labels, else_labels, fallthrough);
            Visit(expr);
            result_consumed = test_result.result_consumed_by_test();
            type_hint = test_result.type_hint();
            // Labels and fallthrough might have been mutated, so update based on
            // TestResultScope.
            then_labels = test_result.then_labels();
            else_labels = test_result.else_labels();
            fallthrough = test_result.fallthrough();
        }
        if (!result_consumed)
        {
            BuildTest(ToBooleanModeFromTypeHint(type_hint), then_labels, else_labels, fallthrough);
        }
    }

    // Visits the expression |expr| for testing its nullish value and jumping to the
    // |then| or |other| label depending on value and short-circuit semantics
    void VisitForNullishTest(Expression expr, BytecodeLabels then_labels, BytecodeLabels test_next_labels,
                             BytecodeLabels else_labels)
    {
        // Nullish short circuits on undefined or null, otherwise we fall back to
        // BuildTest with no fallthrough.
        // TODO(joshualitt): We should do this in a TestResultScope.
        TypeHint type_hint = VisitForAccumulatorValue(expr);
        ToBooleanMode mode = ToBooleanModeFromTypeHint(type_hint);

        // Skip the nullish shortcircuit if we already have a boolean.
        if (mode != ToBooleanMode.AlreadyBoolean)
        {
            builder().JumpIfUndefinedOrNull(test_next_labels.New());
        }
        BuildTest(mode, then_labels, else_labels, TestFallthrough.kNone);
    }

    void VisitInSameTestExecutionScope(Expression expr)
    {
        Debug.Assert(execution_result().IsTest());
        {
            using var reg_scope = new RegisterAllocationScope(this);
            Visit(expr);
        }
        if (!execution_result().AsTest().result_consumed_by_test())
        {
            ExpressionResultScope result_scope = execution_result().AsTest();
            BuildTest(ToBooleanModeFromTypeHint(result_scope.type_hint()), result_scope.then_labels(),
                      result_scope.else_labels(), result_scope.fallthrough());
            result_scope.SetResultConsumedByTest();
        }
    }

    void VisitInScope(Statement stmt, Scope scope)
    {
        Debug.Assert(scope.declarations().is_empty());
        using var current_scope = new CurrentScope(this, scope);
        using var context_scope = new ContextScope(this, scope);
        Visit(stmt);
    }

    TypeHint VisitInHoleCheckElisionScopeForAccumulatorValue(Expression expr)
    {
        using var elider = new HoleCheckElisionScope(this);
        return VisitForAccumulatorValue(expr);
    }

    Register GetRegisterForLocalVariable(Variable variable)
    {
        Debug.Assert(variable.location() == VariableLocation.LOCAL);
        return builder().Local(variable.index());
    }

    TypeHint GetTypeHintForLocalVariable(Variable variable)
    {
        BytecodeRegisterOptimizer? optimizer = builder().GetRegisterOptimizer();
        if (optimizer is not null)
        {
            Register reg = GetRegisterForLocalVariable(variable);
            return optimizer.GetTypeHint(reg);
        }
        return TypeHint.Any;
    }
}
