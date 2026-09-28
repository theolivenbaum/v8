// Port of src/interpreter/bytecode-generator.cc: the scoped helper classes.
using V8Sharp.Ast;
using V8Sharp.Codegen;
using V8Sharp.Common;
using V8Sharp.Parsing;
using V8Sharp.Runtime;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Interpreter;

public sealed partial class BytecodeGenerator
{
    // Scoped class tracking context objects created by the visitor. Represents
    // mutations of the context chain within the function body, allowing pushing and
    // popping of the current {context_register} during visitation.
    sealed class ContextScope : IDisposable
    {
        readonly BytecodeGenerator _generator;
        readonly Scope _scope;
        readonly ContextScope? _outer;
        Register _register;
        readonly int _depth;

        public ContextScope(BytecodeGenerator generator, Scope scope) : this(generator, scope, Register.InvalidValue()) { }

        public ContextScope(BytecodeGenerator generator, Scope scope, Register outer_context_reg)
        {
            _generator = generator;
            _scope = scope;
            _outer = generator._executionContext;
            _register = Register.CurrentContext();
            _depth = 0;
            Debug.Assert(scope.NeedsContext() || _outer is null);
            if (_outer is not null)
            {
                _depth = _outer._depth + 1;

                // Push the outer context into a new context register.
                if (!outer_context_reg.IsValid)
                {
                    outer_context_reg = generator.register_allocator().NewRegister();
                }
                _outer.set_register(outer_context_reg);
                generator.builder().PushContext(outer_context_reg);
            }
            generator.set_execution_context(this);
        }

        public void Dispose()
        {
            if (_outer is not null)
            {
                Debug.Assert(_register.Index == Register.CurrentContext().Index);
                _generator.builder().PopContext(_outer.reg());
                _outer.set_register(_register);
            }
            _generator.set_execution_context(_outer);
        }

        // Returns the depth of the given |scope| for the current execution context.
        public int ContextChainDepth(Scope scope) => _scope.ContextChainLength(scope);

        // Returns the execution context at |depth| in the current context chain if it
        // is a function local execution context, otherwise returns nullptr.
        public ContextScope? Previous(int depth)
        {
            if (depth > _depth) return null;

            ContextScope previous = this;
            for (int i = depth; i > 0; --i)
            {
                previous = previous._outer!;
            }
            return previous;
        }

        public Register reg() => _register;

        void set_register(Register reg) => _register = reg;
    }

    // Scoped class for tracking control statements entered by the
    // visitor.
    abstract class ControlScope : IDisposable
    {
        readonly BytecodeGenerator _generator;
        readonly ControlScope? _outer;
        readonly ContextScope? _context;

        protected ControlScope(BytecodeGenerator generator)
        {
            _generator = generator;
            _outer = generator._executionControl;
            _context = generator._executionContext;
            generator.set_execution_control(this);
        }

        public virtual void Dispose() => _generator.set_execution_control(outer());

        public void Break(Statement stmt) => PerformCommand(Command.CMD_BREAK, stmt, kNoSourcePosition);
        public void Continue(Statement stmt) => PerformCommand(Command.CMD_CONTINUE, stmt, kNoSourcePosition);
        public void ReturnAccumulator(int source_position) => PerformCommand(Command.CMD_RETURN, null, source_position);
        public void AsyncReturnAccumulator(int source_position) =>
            PerformCommand(Command.CMD_ASYNC_RETURN, null, source_position);

        public enum Command
        {
            CMD_BREAK,
            CMD_CONTINUE,
            CMD_RETURN,
            CMD_ASYNC_RETURN,
            CMD_RETHROW,
        }

        public static bool CommandUsesAccumulator(Command command) =>
            command != Command.CMD_BREAK && command != Command.CMD_CONTINUE;

        public void PerformCommand(Command command, Statement? statement, int source_position)
        {
            ControlScope? current = this;
            do
            {
                if (current.Execute(command, statement, source_position))
                {
                    return;
                }
                current = current.outer();
            } while (current is not null);
            throw new UnreachableException();
        }

        protected abstract bool Execute(Command command, Statement? statement, int source_position);

        // Helper to pop the context chain to a depth expected by this control scope.
        // Note that it is the responsibility of each individual {Execute} method to
        // trigger this when commands are handled and control-flow continues locally.
        protected void PopContextToExpectedDepth()
        {
            // Pop context to the expected depth. Note that this can in fact pop multiple
            // contexts at once because the {PopContext} bytecode takes a saved register.
            if (generator().execution_context() != context())
            {
                generator().builder().PopContext(context()!.reg());
            }
        }

        protected BytecodeGenerator generator() => _generator;
        protected ControlScope? outer() => _outer;
        protected ContextScope? context() => _context;
    }

    // Helper class for a try-finally control scope. It can record intercepted
    // control-flow commands that cause entry into a finally-block, and re-apply
    // them after again leaving that block. Special tokens are used to identify
    // paths going through the finally-block to dispatch after leaving the block.
    sealed class DeferredCommands
    {
        // One recorded control-flow command.
        readonly record struct Entry(ControlScope.Command command, Statement? statement, int token);

        readonly BytecodeGenerator _generator;
        readonly List<Entry> _deferred = [];
        readonly Register _tokenRegister;
        readonly Register _resultRegister;
        readonly Register _messageRegister;

        // Tokens for commands that don't need a statement.
        int _returnToken = -1;
        int _asyncReturnToken = -1;

        // Whether a fallthrough is possible.
        bool _fallthroughFromTryBlockNeeded;

        public DeferredCommands(BytecodeGenerator generator, Register token_register, Register result_register,
                                Register message_register)
        {
            _generator = generator;
            _tokenRegister = token_register;
            _resultRegister = result_register;
            _messageRegister = message_register;
            // There's always a rethrow path.
            // TODO(leszeks): We could decouple deferred_ index and token to allow us
            // to still push this lazily.
            Debug.Assert((int)TryFinallyContinuationToken.RethrowToken == 0);
            _deferred.Add(new Entry(ControlScope.Command.CMD_RETHROW, null, (int)TryFinallyContinuationToken.RethrowToken));
        }

        // Records a control-flow command while entering the finally-block. This also
        // generates a new dispatch token that identifies one particular path. This
        // expects the result to be in the accumulator.
        public void RecordCommand(ControlScope.Command command, Statement? statement)
        {
            int token = GetTokenForCommand(command, statement);

            Debug.Assert(token < _deferred.Count);
            Debug.Assert(_deferred[token].command == command);
            Debug.Assert(_deferred[token].statement == statement);
            Debug.Assert(_deferred[token].token == token);

            if (ControlScope.CommandUsesAccumulator(command))
            {
                builder().StoreAccumulatorInRegister(_resultRegister);
            }
            builder().LoadLiteral(Smi.FromInt(token));
            builder().StoreAccumulatorInRegister(_tokenRegister);
            if (!ControlScope.CommandUsesAccumulator(command))
            {
                // If we're not saving the accumulator in the result register, shove a
                // harmless value there instead so that it is still considered "killed" in
                // the liveness analysis. Normally we would LdaUndefined first, but the
                // Smi token value is just as good, and by reusing it we save a bytecode.
                builder().StoreAccumulatorInRegister(_resultRegister);
            }
            if (command == ControlScope.Command.CMD_RETHROW)
            {
                // Clear message object as we enter the catch block. It will be restored
                // if we rethrow.
                builder().LoadTheHole().SetPendingMessage().StoreAccumulatorInRegister(_messageRegister);
            }
        }

        // Records the dispatch token to be used to identify the re-throw path when
        // the finally-block has been entered through the exception handler. This
        // expects the exception to be in the accumulator.
        public void RecordHandlerReThrowPath()
        {
            // The accumulator contains the exception object.
            RecordCommand(ControlScope.Command.CMD_RETHROW, null);
        }

        // Records the dispatch token to be used to identify the implicit fall-through
        // path at the end of a try-block into the corresponding finally-block.
        public void RecordFallThroughPath()
        {
            _fallthroughFromTryBlockNeeded = true;
            builder().LoadLiteral(Smi.FromInt((int)TryFinallyContinuationToken.FallthroughToken));
            builder().StoreAccumulatorInRegister(_tokenRegister);
            // Since we're not saving the accumulator in the result register, shove a
            // harmless value there instead so that it is still considered "killed" in
            // the liveness analysis. Normally we would LdaUndefined first, but the Smi
            // token value is just as good, and by reusing it we save a bytecode.
            builder().StoreAccumulatorInRegister(_resultRegister);
        }

        void ApplyDeferredCommand(Entry entry)
        {
            if (entry.command == ControlScope.Command.CMD_RETHROW)
            {
                // Pending message object is restored on exit.
                builder().LoadAccumulatorWithRegister(_messageRegister).SetPendingMessage();
            }

            if (ControlScope.CommandUsesAccumulator(entry.command))
            {
                builder().LoadAccumulatorWithRegister(_resultRegister);
            }
            execution_control().PerformCommand(entry.command, entry.statement, kNoSourcePosition);
        }

        // Applies all recorded control-flow commands after the finally-block again.
        // This generates a dynamic dispatch on the token from the entry point.
        public void ApplyDeferredCommands()
        {
            if (_deferred.Count == 0) return;

            var fall_through_from_try_block = new BytecodeLabel();

            if (_deferred.Count == 1)
            {
                // For a single entry, just jump to the fallthrough if we don't match the
                // entry token.
                Entry entry = _deferred[0];

                if (_fallthroughFromTryBlockNeeded)
                {
                    builder()
                        .LoadLiteral(Smi.FromInt(entry.token))
                        .CompareReference(_tokenRegister)
                        .JumpIfFalse(BytecodeArrayBuilder.ToBooleanMode.AlreadyBoolean, fall_through_from_try_block);
                }

                ApplyDeferredCommand(entry);
            }
            else
            {
                // For multiple entries, build a jump table and switch on the token,
                // jumping to the fallthrough if none of them match.
                //
                // If fallthrough from the try block is not needed, generate a jump table
                // with one (1) fewer entries and reuse the fallthrough path for the final
                // entry.
                int jump_table_base_value = _fallthroughFromTryBlockNeeded ? 0 : 1;
                int jump_table_size = _deferred.Count - jump_table_base_value;

                if (jump_table_size == 1)
                {
                    Debug.Assert(_deferred.Count == 2);
                    var fall_through_to_final_entry = new BytecodeLabel();
                    Entry first_entry = _deferred[0];
                    Entry final_entry = _deferred[1];
                    builder()
                        .LoadLiteral(Smi.FromInt(first_entry.token))
                        .CompareReference(_tokenRegister)
                        .JumpIfFalse(BytecodeArrayBuilder.ToBooleanMode.AlreadyBoolean, fall_through_to_final_entry);
                    ApplyDeferredCommand(first_entry);
                    builder().Bind(fall_through_to_final_entry);
                    ApplyDeferredCommand(final_entry);
                }
                else
                {
                    BytecodeJumpTable jump_table = builder().AllocateJumpTable(jump_table_size, jump_table_base_value);
                    builder().LoadAccumulatorWithRegister(_tokenRegister).SwitchOnSmiNoFeedback(jump_table);

                    Entry first_entry = _deferred[0];
                    if (_fallthroughFromTryBlockNeeded)
                    {
                        builder().Jump(fall_through_from_try_block);
                        builder().Bind(jump_table, first_entry.token);
                    }
                    ApplyDeferredCommand(first_entry);

                    for (int i = 1; i < _deferred.Count; i++)
                    {
                        Entry entry = _deferred[i];
                        builder().Bind(jump_table, entry.token);
                        ApplyDeferredCommand(entry);
                    }
                }
            }

            if (_fallthroughFromTryBlockNeeded)
            {
                builder().Bind(fall_through_from_try_block);
            }
        }

        BytecodeArrayBuilder builder() => _generator.builder();
        ControlScope execution_control() => _generator.execution_control();

        int GetTokenForCommand(ControlScope.Command command, Statement? statement)
        {
            switch (command)
            {
                case ControlScope.Command.CMD_RETURN:
                    return GetReturnToken();
                case ControlScope.Command.CMD_ASYNC_RETURN:
                    return GetAsyncReturnToken();
                case ControlScope.Command.CMD_RETHROW:
                    return (int)TryFinallyContinuationToken.RethrowToken;
                default:
                    // TODO(leszeks): We could also search for entries with the same
                    // command and statement.
                    return GetNewTokenForCommand(command, statement);
            }
        }

        int GetReturnToken()
        {
            if (_returnToken == -1)
            {
                _returnToken = GetNewTokenForCommand(ControlScope.Command.CMD_RETURN, null);
            }
            return _returnToken;
        }

        int GetAsyncReturnToken()
        {
            if (_asyncReturnToken == -1)
            {
                _asyncReturnToken = GetNewTokenForCommand(ControlScope.Command.CMD_ASYNC_RETURN, null);
            }
            return _asyncReturnToken;
        }

        int GetNewTokenForCommand(ControlScope.Command command, Statement? statement)
        {
            int token = _deferred.Count;
            _deferred.Add(new Entry(command, statement, token));
            return token;
        }
    }

    // Scoped class for dealing with control flow reaching the function level.
    sealed class ControlScopeForTopLevel(BytecodeGenerator generator) : ControlScope(generator)
    {
        protected override bool Execute(Command command, Statement? statement, int source_position)
        {
            switch (command)
            {
                case Command.CMD_BREAK: // We should never see break/continue in top-level.
                case Command.CMD_CONTINUE:
                    throw new UnreachableException();
                case Command.CMD_RETURN:
                    // No need to pop contexts, execution leaves the method body.
                    generator().BuildReturn(source_position);
                    return true;
                case Command.CMD_ASYNC_RETURN:
                    // No need to pop contexts, execution leaves the method body.
                    generator().BuildAsyncReturn(source_position);
                    return true;
                case Command.CMD_RETHROW:
                    // No need to pop contexts, execution leaves the method body.
                    generator().BuildReThrow();
                    return true;
            }
            return false;
        }
    }

    // Scoped class to help elide hole checks within a conditionally executed basic
    // block. Each conditionally executed basic block must have a scope to emit
    // hole checks correctly.
    //
    // The duration of the scope must correspond to a basic block. Numbered
    // Variables (see Variable::HoleCheckBitmap) are remembered in the bitmap when
    // the first hole check is emitted. Subsequent hole checks are elided.
    //
    // On scope exit, the hole check state at construction time is restored.
    readonly ref struct HoleCheckElisionScope
    {
        readonly BytecodeGenerator _generator;
        readonly ulong _prevBitmapValue;

        public HoleCheckElisionScope(BytecodeGenerator bytecode_generator)
        {
            _generator = bytecode_generator;
            _prevBitmapValue = bytecode_generator._holeCheckBitmap;
        }

        public void Dispose() => _generator._holeCheckBitmap = _prevBitmapValue;
    }

    // Scoped class to help elide hole checks within control flow that branch and
    // merge.
    //
    // Each such control flow construct (e.g., if-else, ternary expressions) must
    // have a scope to emit hole checks correctly. Additionally, each branch must
    // have a Branch.
    //
    // The Merge or MergeIf method must be called to merge variables that have been
    // hole-checked along every branch are marked as no longer needing a hole check.
    //
    // Conversely, it is incorrect to use this class for control flow constructs
    // that do not merge (e.g., if without else). HoleCheckElisionScope should be
    // used for those cases.
    sealed class HoleCheckElisionMergeScope(BytecodeGenerator bytecode_generator)
    {
        readonly BytecodeGenerator _generator = bytecode_generator;
        ulong _mergeValue = ulong.MaxValue;

        public void MergeBranch(BytecodeGenerator generator) => _mergeValue &= generator._holeCheckBitmap;

        public void Merge()
        {
            Debug.Assert(_mergeValue != ulong.MaxValue);
            _generator._holeCheckBitmap = _mergeValue;
        }

        public void MergeIf(bool cond)
        {
            if (cond) Merge();
        }

        public Branch NewBranch() => new(this);

        public readonly ref struct Branch
        {
            readonly HoleCheckElisionMergeScope _mergeInto;
            readonly ulong _prevBitmapValue;

            public Branch(HoleCheckElisionMergeScope merge_into)
            {
                _mergeInto = merge_into;
                _prevBitmapValue = merge_into._generator._holeCheckBitmap;
            }

            public void Dispose()
            {
                BytecodeGenerator generator = _mergeInto._generator;
                _mergeInto._mergeValue &= generator._holeCheckBitmap;
                generator._holeCheckBitmap = _prevBitmapValue;
            }
        }
    }

    // Scoped class for enabling break inside blocks and switch blocks.
    sealed class ControlScopeForBreakable(BytecodeGenerator generator, BreakableStatement statement,
                                          BreakableControlFlowBuilder control_builder) : ControlScope(generator)
    {
        readonly HoleCheckElisionMergeScope _mergeElider = new(generator);

        public HoleCheckElisionMergeScope merge_elider() => _mergeElider;

        protected override bool Execute(Command command, Statement? stmt, int source_position)
        {
            if (stmt != statement) return false;
            switch (command)
            {
                case Command.CMD_BREAK:
                    _mergeElider.MergeBranch(generator());
                    PopContextToExpectedDepth();
                    control_builder.Break();
                    return true;
                case Command.CMD_CONTINUE:
                case Command.CMD_RETURN:
                case Command.CMD_ASYNC_RETURN:
                case Command.CMD_RETHROW:
                    break;
            }
            return false;
        }
    }

    // Scoped class for enabling 'break' and 'continue' in iteration
    // constructs, e.g. do...while, while..., for...
    sealed class ControlScopeForIteration(BytecodeGenerator generator, IterationStatement statement,
                                          LoopBuilder loop_builder) : ControlScope(generator)
    {
        readonly HoleCheckElisionMergeScope _mergeElider = new(generator);

        public HoleCheckElisionMergeScope merge_elider() => _mergeElider;

        protected override bool Execute(Command command, Statement? stmt, int source_position)
        {
            if (stmt != statement) return false;
            switch (command)
            {
                case Command.CMD_BREAK:
                    PopContextToExpectedDepth();
                    loop_builder.Break();
                    return true;
                case Command.CMD_CONTINUE:
                    _mergeElider.MergeBranch(generator());
                    PopContextToExpectedDepth();
                    loop_builder.Continue();
                    return true;
                case Command.CMD_RETURN:
                case Command.CMD_ASYNC_RETURN:
                case Command.CMD_RETHROW:
                    break;
            }
            return false;
        }
    }

    // Scoped class for enabling 'throw' in try-catch constructs.
#pragma warning disable CS9113 // try_catch_builder is unused, as in V8.
    sealed class ControlScopeForTryCatch(BytecodeGenerator generator, TryCatchBuilder try_catch_builder)
        : ControlScope(generator)
    {
        protected override bool Execute(Command command, Statement? statement, int source_position)
        {
            switch (command)
            {
                case Command.CMD_BREAK:
                case Command.CMD_CONTINUE:
                case Command.CMD_RETURN:
                case Command.CMD_ASYNC_RETURN:
                    break;
                case Command.CMD_RETHROW:
                    // No need to pop contexts, execution re-enters the method body via the
                    // stack unwinding mechanism which itself restores contexts correctly.
                    generator().BuildReThrow();
                    return true;
            }
            return false;
        }
    }
#pragma warning restore CS9113

    // Scoped class for enabling control flow through try-finally constructs.
    sealed class ControlScopeForTryFinally(BytecodeGenerator generator, TryFinallyBuilder try_finally_builder,
                                           DeferredCommands commands) : ControlScope(generator)
    {
        protected override bool Execute(Command command, Statement? statement, int source_position)
        {
            switch (command)
            {
                case Command.CMD_BREAK:
                case Command.CMD_CONTINUE:
                case Command.CMD_RETURN:
                case Command.CMD_ASYNC_RETURN:
                case Command.CMD_RETHROW:
                    PopContextToExpectedDepth();
                    // We don't record source_position here since we don't generate return
                    // bytecode right here and will generate it later as part of finally
                    // block. Each return bytecode generated in finally block will get own
                    // return source position from corresponded return statement or we'll
                    // use end of function if no return statement is presented.
                    commands.RecordCommand(command, statement);
                    try_finally_builder.LeaveTry();
                    return true;
            }
            return false;
        }
    }

    // Scoped class for collecting 'return' statements in a derived constructor.
    // Derived constructors can only return undefined or objects, and this check
    // must occur right before return (e.g., after `finally` blocks execute).
    sealed class ControlScopeForDerivedConstructor(BytecodeGenerator generator, Register result_register,
                                                   BytecodeLabels check_return_value_labels)
        : ControlScope(generator)
    {
        protected override bool Execute(Command command, Statement? statement, int source_position)
        {
            // Constructors are never async.
            Debug.Assert(command != Command.CMD_ASYNC_RETURN);
            if (command == Command.CMD_RETURN)
            {
                PopContextToExpectedDepth();
                generator().builder().SetStatementPosition(source_position);
                generator().builder().StoreAccumulatorInRegister(result_register);
                generator().builder().Jump(check_return_value_labels.New());
                return true;
            }
            return false;
        }
    }

    // Allocate and fetch the coverage indices tracking NaryLogical Expressions.
    sealed class NaryCodeCoverageSlots
    {
        readonly BytecodeGenerator _generator;
        readonly List<int> _coverageSlots = [];

        public NaryCodeCoverageSlots(BytecodeGenerator generator, NaryOperation expr)
        {
            _generator = generator;
            if (generator._blockCoverageBuilder is null) return;
            for (int i = 0; i < expr.subsequent_length(); i++)
            {
                _coverageSlots.Add(generator.AllocateNaryBlockCoverageSlotIfEnabled(expr, i));
            }
        }

        public int GetSlotFor(int subsequent_expr_index)
        {
            if (_generator._blockCoverageBuilder is null)
            {
                return BlockCoverageBuilder.kNoCoverageArraySlot;
            }
            Debug.Assert(_coverageSlots.Count > subsequent_expr_index);
            return _coverageSlots[subsequent_expr_index];
        }
    }

    readonly ref struct RegisterAllocationScope
    {
        readonly BytecodeGenerator _generator;
        readonly int _outerNextRegisterIndex;

        public RegisterAllocationScope(BytecodeGenerator generator)
        {
            _generator = generator;
            _outerNextRegisterIndex = generator.register_allocator().NextRegisterIndex();
        }

        public void Dispose() => _generator.register_allocator().ReleaseRegisters(_outerNextRegisterIndex);

        public BytecodeGenerator generator() => _generator;
    }

    readonly ref struct AccumulatorPreservingScope
    {
        readonly BytecodeGenerator _generator;
        readonly Register _savedAccumulatorRegister;

        public AccumulatorPreservingScope(BytecodeGenerator generator, AccumulatorPreservingMode mode)
        {
            _generator = generator;
            _savedAccumulatorRegister = Register.InvalidValue();
            if (mode == AccumulatorPreservingMode.kPreserve)
            {
                _savedAccumulatorRegister = generator.register_allocator().NewRegister();
                generator.builder().StoreAccumulatorInRegister(_savedAccumulatorRegister);
            }
        }

        public void Dispose()
        {
            if (_savedAccumulatorRegister.IsValid)
            {
                _generator.builder().LoadAccumulatorWithRegister(_savedAccumulatorRegister);
            }
        }
    }

    // Scoped base class for determining how the result of an expression will be
    // used. V8's EffectResultScope, ValueResultScope and TestResultScope are the
    // kinds of this one class (V8 reinterpret_casts to TestResultScope in AsTest);
    // instances are recycled through the generator's free list since one is
    // created for every visited expression.
    sealed class ExpressionResultScope : IDisposable
    {
        public enum Kind : byte
        {
            // Evaluated for its side effects.
            kEffect,
            // Evaluated for its value (and side effects).
            kValue,
            kValueAsPropertyKey,
            // Evaluated for control flow (and side effects).
            kTest,
        }

        BytecodeGenerator _generator = null!;
        ExpressionResultScope? _outer;
        int _outerNextRegisterIndex;
        Kind _kind;
        TypeHint _typeHint;

        // TestResultScope state.
        bool _resultConsumedByTest;
        TestFallthrough _fallthrough;
        BytecodeLabels? _thenLabels;
        BytecodeLabels? _elseLabels;

        // Free list link.
        ExpressionResultScope? _nextFree;

        public static ExpressionResultScope Enter(BytecodeGenerator generator, Kind kind)
        {
            ExpressionResultScope scope = generator._freeResultScopes ?? new ExpressionResultScope();
            generator._freeResultScopes = scope._nextFree;
            scope._nextFree = null;
            scope._generator = generator;
            scope._outer = generator._executionResult;
            scope._outerNextRegisterIndex = generator.register_allocator().NextRegisterIndex();
            scope._kind = kind;
            scope._typeHint = TypeHint.Unknown;
            scope._resultConsumedByTest = false;
            scope._fallthrough = TestFallthrough.kNone;
            scope._thenLabels = null;
            scope._elseLabels = null;
            generator.set_execution_result(scope);
            return scope;
        }

        public static ExpressionResultScope EnterTest(BytecodeGenerator generator, BytecodeLabels then_labels,
                                                      BytecodeLabels else_labels, TestFallthrough fallthrough)
        {
            ExpressionResultScope scope = Enter(generator, Kind.kTest);
            scope._fallthrough = fallthrough;
            scope._thenLabels = then_labels;
            scope._elseLabels = else_labels;
            return scope;
        }

        public void Dispose()
        {
            BytecodeGenerator generator = _generator;
            generator.register_allocator().ReleaseRegisters(_outerNextRegisterIndex);
            generator.set_execution_result(_outer);
            _outer = null;
            _thenLabels = null;
            _elseLabels = null;
            _nextFree = generator._freeResultScopes;
            generator._freeResultScopes = this;
        }

        public bool IsEffect() => _kind == Kind.kEffect;
        public bool IsValue() => _kind == Kind.kValue;
        public bool IsValueAsPropertyKey() => _kind == Kind.kValueAsPropertyKey;
        public bool IsTest() => _kind == Kind.kTest;

        public ExpressionResultScope AsTest()
        {
            Debug.Assert(IsTest());
            return this;
        }

        // Specify expression always returns a Boolean result value.
        public void SetResultIsBoolean()
        {
            Debug.Assert(_typeHint == TypeHint.Unknown);
            _typeHint = TypeHint.Boolean;
        }

        public void SetResultIsString()
        {
            Debug.Assert(_typeHint == TypeHint.Unknown);
            _typeHint = TypeHint.String;
        }

        public void SetResultIsInternalizedString()
        {
            Debug.Assert(_typeHint == TypeHint.Unknown);
            _typeHint = TypeHint.InternalizedString;
        }

        public TypeHint type_hint() => _typeHint;

        // ---- TestResultScope ----

        // Used when code special cases for TestResultScope and consumes any
        // possible value by testing and jumping to a then/else label.
        public void SetResultConsumedByTest() => _resultConsumedByTest = true;
        public bool result_consumed_by_test() => _resultConsumedByTest;

        // Inverts the control flow of the operation, swapping the then and else
        // labels and the fallthrough.
        public void InvertControlFlow()
        {
            (_thenLabels, _elseLabels) = (_elseLabels, _thenLabels);
            _fallthrough = inverted_fallthrough();
        }

        public BytecodeLabel NewThenLabel() => _thenLabels!.New();
        public BytecodeLabel NewElseLabel() => _elseLabels!.New();

        public BytecodeLabels then_labels() => _thenLabels!;
        public BytecodeLabels else_labels() => _elseLabels!;

        public void set_then_labels(BytecodeLabels then_labels) => _thenLabels = then_labels;
        public void set_else_labels(BytecodeLabels else_labels) => _elseLabels = else_labels;

        public TestFallthrough fallthrough() => _fallthrough;

        public TestFallthrough inverted_fallthrough() => _fallthrough switch
        {
            TestFallthrough.kThen => TestFallthrough.kElse,
            TestFallthrough.kElse => TestFallthrough.kThen,
            _ => TestFallthrough.kNone,
        };

        public void set_fallthrough(TestFallthrough fallthrough) => _fallthrough = fallthrough;
    }

    ExpressionResultScope EffectResultScope() => ExpressionResultScope.Enter(this, ExpressionResultScope.Kind.kEffect);
    ExpressionResultScope ValueResultScope() => ExpressionResultScope.Enter(this, ExpressionResultScope.Kind.kValue);

    // Used to build a list of toplevel declaration data.
    sealed class TopLevelDeclarationsBuilder
    {
        const int kGlobalVariableDeclarationSize = 1;
        const int kGlobalFunctionDeclarationSize = 2;
        const int kModuleVariableDeclarationSize = 1;
        const int kModuleFunctionDeclarationSize = 3;

        int _constantPoolEntry;
        int _entrySlots;
        bool _hasConstantPoolEntry;
        bool _processed;

        public object? AllocateDeclarations(UnoptimizedCompilationInfo info, BytecodeGenerator generator,
                                            IBytecodeGeneratorHeap heap)
        {
            Debug.Assert(_hasConstantPoolEntry);

            var data = new object[_entrySlots];

            int array_index = 0;
            if (info.scope().is_module_scope())
            {
                foreach (Declaration decl in info.scope().declarations())
                {
                    Variable var = decl.var()!;
                    if (!var.is_used()) continue;
                    if (var.location() != VariableLocation.MODULE) continue;
#if DEBUG
                    int start = array_index;
#endif
                    if (decl.IsFunctionDeclaration())
                    {
                        FunctionLiteral f = ((FunctionDeclaration)decl).fun();
                        object? sfi = heap.GetSharedFunctionInfo(f);
                        // Return a null handle if any initial values can't be created. Caller
                        // will set stack overflow.
                        if (sfi is null) return null;
                        data[array_index++] = sfi;
                        int literal_index = generator.GetNewClosureSlot(f);
                        data[array_index++] = heap.Smi(Smi.FromInt(literal_index));
                        Debug.Assert(var.IsExport());
                        data[array_index++] = heap.Smi(Smi.FromInt(var.index()));
#if DEBUG
                        Debug.Assert(start + kModuleFunctionDeclarationSize == array_index);
#endif
                    }
                    else if (var.IsExport() && var.binding_needs_init())
                    {
                        data[array_index++] = heap.Smi(Smi.FromInt(var.index()));
#if DEBUG
                        Debug.Assert(start + kModuleVariableDeclarationSize == array_index);
#endif
                    }
                }
            }
            else
            {
                foreach (Declaration decl in info.scope().declarations())
                {
                    Variable var = decl.var()!;
                    if (!var.is_used()) continue;
                    if (var.location() != VariableLocation.UNALLOCATED) continue;
#if DEBUG
                    int start = array_index;
#endif
                    if (decl.IsVariableDeclaration())
                    {
                        data[array_index++] = heap.RawString(var.raw_name());
#if DEBUG
                        Debug.Assert(start + kGlobalVariableDeclarationSize == array_index);
#endif
                    }
                    else
                    {
                        FunctionLiteral f = ((FunctionDeclaration)decl).fun();
                        object? sfi = heap.GetSharedFunctionInfo(f);
                        // Return a null handle if any initial values can't be created. Caller
                        // will set stack overflow.
                        if (sfi is null) return null;
                        data[array_index++] = sfi;
                        int literal_index = generator.GetNewClosureSlot(f);
                        data[array_index++] = heap.Smi(Smi.FromInt(literal_index));
#if DEBUG
                        Debug.Assert(start + kGlobalFunctionDeclarationSize == array_index);
#endif
                    }
                }
            }
            Debug.Assert(array_index == data.Length);
            return heap.NewFixedArray(data);
        }

        public int constant_pool_entry()
        {
            Debug.Assert(_hasConstantPoolEntry);
            return _constantPoolEntry;
        }

        public void set_constant_pool_entry(int constant_pool_entry)
        {
            Debug.Assert(has_top_level_declaration());
            Debug.Assert(!_hasConstantPoolEntry);
            _constantPoolEntry = constant_pool_entry;
            _hasConstantPoolEntry = true;
        }

        public void record_global_variable_declaration() => _entrySlots += kGlobalVariableDeclarationSize;
        public void record_global_function_declaration() => _entrySlots += kGlobalFunctionDeclarationSize;
        public void record_module_variable_declaration() => _entrySlots += kModuleVariableDeclarationSize;
        public void record_module_function_declaration() => _entrySlots += kModuleFunctionDeclarationSize;
        public bool has_top_level_declaration() => _entrySlots > 0;
        public bool processed() => _processed;
        public void mark_processed() => _processed = true;
    }

    readonly ref struct CurrentScope
    {
        readonly BytecodeGenerator _generator;
        readonly Scope _outerScope;

        public CurrentScope(BytecodeGenerator generator, Scope? scope)
        {
            _generator = generator;
            _outerScope = generator.current_scope();
            if (scope is not null)
            {
                Debug.Assert(_outerScope == scope.outer_scope());
                generator.set_current_scope(scope);
            }
        }

        public void Dispose()
        {
            if (_outerScope != _generator.current_scope())
            {
                _generator.set_current_scope(_outerScope);
            }
        }
    }

    sealed class MultipleEntryBlockContextScope
    {
        readonly BytecodeGenerator _generator;
        readonly Scope? _scope;
        readonly Register _innerContext = Register.InvalidValue();
        readonly Register _outerContext = Register.InvalidValue();
        bool _isInScope;
        Scope? _savedOuterScope;
        ContextScope? _contextScope;

        public MultipleEntryBlockContextScope(BytecodeGenerator generator, Scope? scope)
        {
            _generator = generator;
            _scope = scope;
            if (scope is not null)
            {
                _innerContext = generator.register_allocator().NewRegister();
                _outerContext = generator.register_allocator().NewRegister();
                generator.BuildNewLocalBlockContext(scope);
                generator.builder().StoreAccumulatorInRegister(_innerContext);
            }
        }

        public void SetEnteredIf(bool condition)
        {
            using var register_scope = new RegisterAllocationScope(_generator);
            if (condition && _scope is not null && !_isInScope)
            {
                EnterScope();
            }
            else if (!condition && _isInScope)
            {
                ExitScope();
            }
        }

        void EnterScope()
        {
            Debug.Assert(_innerContext.IsValid);
            Debug.Assert(_outerContext.IsValid);
            Debug.Assert(!_isInScope);
            _generator.builder().LoadAccumulatorWithRegister(_innerContext);
            // current_scope_.emplace(generator_, scope_);
            _savedOuterScope = _generator.current_scope();
            Debug.Assert(_savedOuterScope == _scope!.outer_scope());
            _generator.set_current_scope(_scope);
            _contextScope = new ContextScope(_generator, _scope, _outerContext);
            _isInScope = true;
        }

        void ExitScope()
        {
            Debug.Assert(_innerContext.IsValid);
            Debug.Assert(_outerContext.IsValid);
            Debug.Assert(_isInScope);
            _contextScope!.Dispose();
            _contextScope = null;
            if (_savedOuterScope != _generator.current_scope())
            {
                _generator.set_current_scope(_savedOuterScope!);
            }
            _isInScope = false;
        }
    }

    sealed class FeedbackSlotCache
    {
        public enum SlotKind
        {
            kStoreGlobalSloppy,
            kStoreGlobalStrict,
            kSetNamedStrict,
            kSetNamedSloppy,
            kLoadProperty,
            kLoadSuperProperty,
            kLoadGlobalNotInsideTypeof,
            kLoadGlobalInsideTypeof,
            kClosureFeedbackCell,
        }

        readonly record struct Key(SlotKind Kind, int Index, object? Node)
        {
            public bool Equals(Key other) =>
                Kind == other.Kind && Index == other.Index && ReferenceEquals(Node, other.Node);

            public override int GetHashCode() =>
                HashCode.Combine(Kind, Index, Node is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Node));
        }

        readonly Dictionary<Key, int> _map = [];

        public void Put(SlotKind slot_kind, Variable variable, int slot_index) => PutImpl(slot_kind, 0, variable, slot_index);
        public void Put(SlotKind slot_kind, AstNode node, int slot_index) => PutImpl(slot_kind, 0, node, slot_index);
        public void Put(SlotKind slot_kind, int variable_index, AstRawString name, int slot_index) =>
            PutImpl(slot_kind, variable_index, name, slot_index);
        public void Put(SlotKind slot_kind, AstRawString name, int slot_index) => PutImpl(slot_kind, 0, name, slot_index);

        public int Get(SlotKind slot_kind, Variable variable) => GetImpl(slot_kind, 0, variable);
        public int Get(SlotKind slot_kind, AstNode node) => GetImpl(slot_kind, 0, node);
        public int Get(SlotKind slot_kind, int variable_index, AstRawString name) => GetImpl(slot_kind, variable_index, name);
        public int Get(SlotKind slot_kind, AstRawString name) => GetImpl(slot_kind, 0, name);

        // map_.insert: an existing entry is kept.
        void PutImpl(SlotKind slot_kind, int index, object node, int slot_index) =>
            _map.TryAdd(new Key(slot_kind, index, node), slot_index);

        int GetImpl(SlotKind slot_kind, int index, object node) =>
            _map.TryGetValue(new Key(slot_kind, index, node), out int slot) ? slot : -1;
    }

    readonly struct IteratorRecord
    {
        readonly IteratorType _type;
        readonly Register _object;
        readonly Register _next;

        public IteratorRecord(Register object_register, Register next_register, IteratorType type = IteratorType.kNormal)
        {
            _type = type;
            _object = object_register;
            _next = next_register;
            Debug.Assert(_object.IsValid && _next.IsValid);
        }

        public IteratorType type() => _type;
        public Register @object() => _object;
        public Register next() => _next;
    }

    readonly ref struct OptionalChainNullLabelScope
    {
        readonly BytecodeGenerator _bytecodeGenerator;
        readonly BytecodeLabels _labels;
        readonly BytecodeLabels? _prev;
        // Use the same scope for the entire optional chain, as links earlier in the
        // chain dominate later links, linearly.
        readonly HoleCheckElisionScope _holeCheckScope;

        public OptionalChainNullLabelScope(BytecodeGenerator bytecode_generator)
        {
            _bytecodeGenerator = bytecode_generator;
            _labels = new BytecodeLabels();
            _holeCheckScope = new HoleCheckElisionScope(bytecode_generator);
            _prev = bytecode_generator._optionalChainingNullLabels;
            bytecode_generator._optionalChainingNullLabels = _labels;
        }

        public void Dispose()
        {
            _bytecodeGenerator._optionalChainingNullLabels = _prev;
            _holeCheckScope.Dispose();
        }

        public BytecodeLabels labels() => _labels;
    }

    // LoopScope delimits the scope of {loop}, from its header to its final jump.
    // It should be constructed iff a (conceptual) back edge should be produced. In
    // the case of creating a LoopBuilder but never emitting the loop, it is valid
    // to skip the creation of LoopScope.
    sealed class LoopScope : IDisposable
    {
        readonly BytecodeGenerator _bytecodeGenerator;
        readonly LoopScope? _parentLoopScope;
        readonly LoopBuilder _loopBuilder;

        public LoopScope(BytecodeGenerator bytecode_generator, LoopBuilder loop)
        {
            _bytecodeGenerator = bytecode_generator;
            _parentLoopScope = bytecode_generator.current_loop_scope();
            _loopBuilder = loop;
            _loopBuilder.LoopHeader();
            _bytecodeGenerator.set_current_loop_scope(this);
            _bytecodeGenerator._loopDepth++;
        }

        public void Dispose()
        {
            _bytecodeGenerator._loopDepth--;
            _bytecodeGenerator.set_current_loop_scope(_parentLoopScope);
            Debug.Assert(_bytecodeGenerator._loopDepth >= 0);
            _loopBuilder.JumpToHeader(_bytecodeGenerator._loopDepth, _parentLoopScope?._loopBuilder);
        }
    }

    sealed class ForInScope : IDisposable
    {
        readonly BytecodeGenerator _bytecodeGenerator;
        readonly ForInScope? _parentForInScope;
        readonly Variable? _eachVar;
        readonly Register _enumIndex;
        readonly Register _cacheType;

        public ForInScope(BytecodeGenerator bytecode_generator, ForInStatement stmt, Register enum_index,
                          Register cache_type)
        {
            _bytecodeGenerator = bytecode_generator;
            _parentForInScope = bytecode_generator.current_for_in_scope();
            _eachVar = null;
            _enumIndex = enum_index;
            _cacheType = cache_type;
            if (bytecode_generator.v8_flags.enable_enumerated_keyed_access_bytecode)
            {
                Expression each = stmt.each();
                if (each.IsVariableProxy())
                {
                    Variable each_var = each.AsVariableProxy()!.var();
                    if (each_var.IsStackLocal())
                    {
                        _eachVar = each_var;
                        _bytecodeGenerator.SetVariableInRegister(_eachVar,
                                                                 _bytecodeGenerator.builder().Local(_eachVar.index()));
                    }
                }
                _bytecodeGenerator.set_current_for_in_scope(this);
            }
        }

        public void Dispose()
        {
            if (_bytecodeGenerator.v8_flags.enable_enumerated_keyed_access_bytecode)
            {
                _bytecodeGenerator.set_current_for_in_scope(_parentForInScope);
            }
        }

        // Get corresponding {ForInScope} for a given {each} variable.
        public ForInScope? GetForInScope(Variable each)
        {
            Debug.Assert(_bytecodeGenerator.v8_flags.enable_enumerated_keyed_access_bytecode);
            ForInScope? scope = this;
            do
            {
                if (each == scope._eachVar) break;
                scope = scope._parentForInScope;
            } while (scope is not null);
            return scope;
        }

        public Register enum_index() => _enumIndex;
        public Register cache_type() => _cacheType;
    }

    readonly ref struct DisposablesStackScope
    {
        readonly BytecodeGenerator _bytecodeGenerator;
        readonly Register _prevDisposablesStack;

        public DisposablesStackScope(BytecodeGenerator bytecode_generator)
        {
            _bytecodeGenerator = bytecode_generator;
            _prevDisposablesStack = bytecode_generator._currentDisposablesStack;
            bytecode_generator.set_current_disposables_stack(bytecode_generator.register_allocator().NewRegister());
            bytecode_generator.builder().CallRuntime(FunctionId.InitializeDisposableStack);
            bytecode_generator.builder().StoreAccumulatorInRegister(bytecode_generator.current_disposables_stack());
        }

        public void Dispose() => _bytecodeGenerator.set_current_disposables_stack(_prevDisposablesStack);
    }

    sealed class Accessors<TProperty> where TProperty : class
    {
        public TProperty? getter;
        public TProperty? setter;
    }

    // A map from property names to getter/setter pairs allocated in the zone that
    // also provides a way of accessing the pairs in the order they were first
    // added so that the generated bytecode is always the same.
    sealed class AccessorTable<TProperty> where TProperty : class
    {
        readonly Dictionary<LiteralKey, Accessors<TProperty>> _map = [];
        readonly List<(Literal, Accessors<TProperty>)> _orderedAccessors = [];

        public Accessors<TProperty> LookupOrInsert(Literal key)
        {
            var k = new LiteralKey(key);
            if (!_map.TryGetValue(k, out Accessors<TProperty>? accessors))
            {
                accessors = new Accessors<TProperty>();
                _map.Add(k, accessors);
                _orderedAccessors.Add((key, accessors));
            }
            return accessors;
        }

        public List<(Literal, Accessors<TProperty>)> ordered_accessors() => _orderedAccessors;
    }

    // Literal as a hash map key, with Literal::Hash / Literal::Match semantics.
    readonly struct LiteralKey(Literal literal) : IEquatable<LiteralKey>
    {
        readonly Literal _literal = literal;
        public bool Equals(LiteralKey other) => Literal.Match(_literal, other._literal);
        public override bool Equals(object? obj) => obj is LiteralKey k && Equals(k);
        public override int GetHashCode() => (int)_literal.Hash();
    }
}
