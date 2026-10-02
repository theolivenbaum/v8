// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser-base.h (class declaration, parser state helpers).
//
// ParserBase<Impl> is V8's CRTP base shared by Parser and PreParser. Here it
// is a generic base class whose type parameters are the members of
// ParserTypes<Impl>:
//
//   TExpression            Types::Expression
//   TIdentifier            Types::Identifier
//   TStatement             Types::Statement, BreakableStatement,
//                          IterationStatement and ForStatement
//   TBlock                 Types::Block (a TStatement)
//   TFunctionLiteral       Types::FunctionLiteral (a TExpression)
//   TObjectLiteralProperty Types::ObjectLiteralProperty
//   TClassLiteralProperty  Types::ClassLiteralProperty
//   TExpressionList        Types::ExpressionList
//   TObjectPropertyList    Types::ObjectPropertyList
//   TStatementList         Types::StatementList
//   TClassPropertyList     Types::ClassPropertyList
//   TClassStaticElementList Types::ClassStaticElementList
//   TFormalParameters      Types::FormalParameters
//   TFactory               Types::Factory
//   TFuncNameInferrer      Types::FuncNameInferrer
//
// Types::SourceRange and SourceRangeScope are the real SourceRange for both
// (the PreParser never records the ranges it computes). impl()->X calls are
// abstract methods overridden by Parser and PreParser (ParserBase.Impl.cs).
//
// The generic class is a template: it is compiled (so it is checked) but
// never instantiated. ParserBase.Specialize.targets writes the two
// instantiations V8 has, ParserBaseOfParser and ParserBaseOfPreParser, as
// non-generic classes before compilation, so the JIT sees concrete types and
// binds impl() and factory() calls directly (a generic class instantiated
// over reference types runs as shared code with runtime lookups).

#nullable disable

using System.Runtime.CompilerServices;
using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public enum FunctionNameValidity
{
    kFunctionNameIsStrictReserved,
    kSkipFunctionNameCheck,
    kFunctionNameValidityUnknown,
}

public enum AllowLabelledFunctionStatement
{
    kAllowLabelledFunctionStatement,
    kDisallowLabelledFunctionStatement,
}

public enum ParsingArrowHeadFlag { kCertainlyNotArrowHead, kMaybeArrowHead }

[Flags]
public enum ParseFunctionFlags : byte
{
    kIsNormal = 0,
    kIsGenerator = 1 << 0,
    kIsAsync = 1 << 1,
}

public enum ParsePropertyKind : byte
{
    kAutoAccessorClassField,
    kAccessorGetter,
    kAccessorSetter,
    kValue,
    kShorthand,
    kAssign,
    kMethod,
    kClassField,
    kShorthandOrClassField,
    kSpread,
    kNotSet,
}

public enum InferName { kYes, kNo }

public abstract class FormalParametersBase(DeclarationScope scope)
{
    public abstract void set_strict_parameter_error(Scanner.Location loc, MessageTemplate message);

    public int num_parameters()
    {
        // Don't include the rest parameter into the function's formal parameter
        // count (esp. the SharedFunctionInfo::internal_formal_parameter_count,
        // which says whether we need to create an inlined arguments frame).
        return arity - (has_rest ? 1 : 0);
    }

    public void UpdateArityAndFunctionLength(bool is_optional, bool is_rest)
    {
        if (!is_optional && !is_rest && function_length == arity)
        {
            ++function_length;
        }
        ++arity;
    }

    public DeclarationScope scope = scope;
    public bool has_rest;
    public bool is_simple = true;
    public int function_length;
    public int arity;
}

// Stack-allocated scope to collect source ranges from the parser.
public ref struct SourceRangeScope
{
    private readonly Scanner _scanner;
    private ref SourceRange _range;

    public SourceRangeScope(Scanner scanner, ref SourceRange range)
    {
        _scanner = scanner;
        _range = ref range;
        _range.start = scanner.peek_location().beg_pos;
    }

    public void Dispose()
    {
        _range.end = _scanner.location().end_pos;
    }
}

// The operations ParserBase performs directly on Types::Expression values.
public interface IParserExpression
{
    int position();
    bool is_parenthesized();
    void mark_parenthesized();
    void clear_parenthesized();
    bool IsPattern();
    bool IsAssignment();
    bool IsProperty();
    bool IsCall();
    bool IsCallNew();
    bool IsFunctionLiteral();
    bool IsFailureExpression();
    bool IsSuperCallReference();
    bool IsImportCallExpression();
}

// The operations ParserBase performs directly on Types::Statement values.
public interface IParserStatement
{
    bool IsEmptyStatement();
}

// The FuncNameInferrer operations ParserBase uses; FuncNameInferrer::State is
// EnterState/LeaveState.
public interface IFuncNameInferrer
{
    int EnterState();
    void LeaveState(int top);
    void RemoveAsyncKeywordFromEnd();
    void Infer();
    void RemoveLastFunction();
}

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
    where TImpl : ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
        TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
        TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
    where TExpression : IParserExpression
    where TStatement : IParserStatement
    where TBlock : TStatement, IParserBlock<TStatement, TStatementList>
    where TFunctionLiteral : TExpression, IParserFunctionLiteral
    where TExpressionList : IScopedPtrList<TExpressionList, TExpression>
    where TObjectPropertyList : IScopedPtrList<TObjectPropertyList, TObjectLiteralProperty>
    where TStatementList : IScopedPtrList<TStatementList, TStatement>
    where TFormalParameters : FormalParametersBase
    where TFactory : IParserFactory<TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
        TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList>
    where TFuncNameInferrer : class, IFuncNameInferrer
{
    // All implementation-specific methods must be called through this.
    private readonly TImpl _impl;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected TImpl impl() => _impl;

    protected ParserBase(Scanner scanner, AstValueFactory ast_value_factory,
                         PendingCompilationErrorHandler pending_error_handler, UnoptimizedCompileFlags flags,
                         ParsingFlags v8_flags, TFactory factory, TFuncNameInferrer fni,
                         bool compile_hints_magic_enabled, bool compile_hints_per_function_magic_enabled)
    {
        _impl = (TImpl)this;
        scope_ = null;
        original_scope_ = null;
        function_state_ = null;
        fni_ = fni;
        ast_value_factory_ = ast_value_factory;
        ast_node_factory_ = factory;
        pending_error_handler_ = pending_error_handler;
        expression_scope_ = null;
        scanner_ = scanner;
        flags_ = flags;
        v8_flags_ = v8_flags;
        info_id_ = 0;
        has_module_in_scope_chain_ = flags_.is_module();
        default_eager_compile_hint_ = FunctionLiteral.EagerCompileHint.kShouldLazyCompile;
        compile_hints_magic_enabled_ = compile_hints_magic_enabled;
        compile_hints_per_function_magic_enabled_ = compile_hints_per_function_magic_enabled;
        pointer_buffer_ = new List<object>(32);
        variable_buffer_ = new List<(VariableProxy, int)>(32);
        stack_limit_ = GetCurrentStackPosition() - (nint)Math.Min(
            (long)Math.Max(v8_flags.stack_size, 1) * 1024 * kManagedStackBytesPerV8Byte, int.MaxValue);
    }

    // The machine stack position (the address of a local), as
    // GetCurrentStackPosition returns it. Unsafe.ByteOffset from the null ref
    // is the address without an unsafe context; stack locals do not move.
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected static nint GetCurrentStackPosition()
    {
        byte local = 0;
        return Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref local);
    }

    // V8's parser frames are smaller than the managed ones: one parenthesized
    // expression level takes about 430 bytes of machine stack in V8 and about
    // this many times more in V8Sharp's parser. Scaling --stack-size by it puts
    // the parser's RangeError at about V8's nesting depth.
    const int kManagedStackBytesPerV8Byte = 4;

    // stack_limit_: V8 takes the isolate's C stack limit; V8Sharp gives each
    // parser a budget of --stack-size from where it starts (the parser runs on
    // the .NET stack, which the JS register stack limit does not describe).
    readonly nint stack_limit_;

    public UnoptimizedCompileFlags flags() => flags_;

    // The flag-definitions.h values in effect (v8_flags in V8).
    public ParsingFlags v8_flags() => v8_flags_;

    public bool has_module_in_scope_chain() => has_module_in_scope_chain_;

    // DebugEvaluate code
    public bool IsParsingWhileDebugging() => flags().parsing_while_debugging() == ParsingWhileDebugging.Yes;

    public bool allow_eval_cache() => allow_eval_cache_;
    public void set_allow_eval_cache(bool allow) => allow_eval_cache_ = allow;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool has_error() => scanner().has_parser_error();

    public void set_default_eager_compile_hint(FunctionLiteral.EagerCompileHint eager_compile_hint)
        => default_eager_compile_hint_ = eager_compile_hint;

    public FunctionLiteral.EagerCompileHint default_eager_compile_hint() => default_eager_compile_hint_;

    public int loop_nesting_depth() => function_state_.loop_nesting_depth();
    public int PeekNextInfoId() => info_id_ + 1;
    public int GetNextInfoId() => ++info_id_;
    public int GetLastInfoId() => info_id_;

    public void SkipInfos(int delta) => info_id_ += delta;

    public void ResetInfoId(int id) => info_id_ = id;

    protected enum VariableDeclarationContext
    {
        kStatementListItem,
        kStatement,
        kForStatement,
    }

    // ---------------------------------------------------------------------------
    // BlockState and FunctionState implement the parser's scope stack.
    // The parser's current scope is in scope_. BlockState and FunctionState
    // constructors push on the scope stack and Dispose pops. They are also
    // used to hold the parser's per-funcion state.
    //
    // V8's BlockState takes a Scope** (the stack); the two stacks it is used
    // with are scope_ and object_literal_scope_.
    public readonly struct BlockState : IDisposable
    {
        private readonly ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;
        private readonly Scope _outerScope;
        private readonly bool _objectLiteralStack;

        public BlockState(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                              TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
                              TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
                              TFactory, TFuncNameInferrer> parser, Scope scope, bool object_literal_stack = false)
        {
            _parser = parser;
            _objectLiteralStack = object_literal_stack;
            if (object_literal_stack)
            {
                _outerScope = parser.object_literal_scope_;
                parser.object_literal_scope_ = scope;
            }
            else
            {
                _outerScope = parser.scope_;
                parser.scope_ = scope;
            }
        }

        // BlockState(Zone*, Scope** scope_stack): a new block scope.
        public static BlockState NewBlock(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock,
            TFunctionLiteral, TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
            TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory,
            TFuncNameInferrer> parser)
            => new(parser, new Scope(parser.scope_, ScopeType.BLOCK_SCOPE));

        public void Dispose()
        {
            if (_parser == null) return;
            if (_objectLiteralStack) _parser.object_literal_scope_ = _outerScope;
            else _parser.scope_ = _outerScope;
        }
    }

    // ---------------------------------------------------------------------------
    // Target is a support class to facilitate manipulation of the
    // Parser's target_stack_ (the stack of potential 'break' and
    // 'continue' statement targets). Upon construction, a new target is
    // added; it is removed upon disposal.

    // |labels| is a list of all labels that can be used as a target for break.
    // |own_labels| is a list of all labels that an iteration statement is
    // directly prefixed with, i.e. all the labels that a continue statement in
    // the body can use to continue this iteration statement. This is always a
    // subset of |labels|.
    //
    // Example: "l1: { l2: if (b) l3: l4: for (;;) s }"
    // labels() of the Block will be l1.
    // labels() of the ForStatement will be l2, l3, l4.
    // own_labels() of the ForStatement will be l3, l4.
    //
    // V8's Targets live on the C++ stack; here they are recycled through a
    // per-parser free list (as the expression scopes are, ExpressionScope.cs).
    public sealed class Target : IDisposable
    {
        public enum TargetType { TARGET_FOR_ANONYMOUS, TARGET_FOR_NAMED_ONLY }

        private ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;
        private FunctionState _functionState;
        private TStatement _statement;
        private List<AstRawString> _labels;
        private List<AstRawString> _ownLabels;
        private TargetType _targetType;
        private Target _previous;
        private bool _isIteration;
        private Target _nextFree;

        public static Target New(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                          TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
                          TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
                          TFactory, TFuncNameInferrer> parser, TStatement statement, List<AstRawString> labels,
                                 List<AstRawString> own_labels, TargetType target_type)
        {
            Target t = parser.free_targets_;
            if (t != null) parser.free_targets_ = t._nextFree;
            else t = new Target();
            t.Enter(parser, statement, labels, own_labels, target_type);
            return t;
        }

        private void Enter(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                          TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
                          TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
                          TFactory, TFuncNameInferrer> parser, TStatement statement, List<AstRawString> labels,
                           List<AstRawString> own_labels, TargetType target_type)
        {
            _parser = parser;
            _functionState = parser.function_state_;
            _statement = statement;
            _labels = labels;
            _ownLabels = own_labels;
            _targetType = target_type;
            _previous = _functionState.target_stack_;
            _isIteration = parser.impl().IsIterationStatement(statement);
            _functionState.target_stack_ = this;
        }

        public void Dispose()
        {
            _functionState.target_stack_ = _previous;
            _functionState = null;
            _statement = default;
            _labels = null;
            _ownLabels = null;
            _previous = null;
            _nextFree = _parser.free_targets_;
            _parser.free_targets_ = this;
        }

        public Target previous() => _previous;
        public TStatement statement() => _statement;
        public List<AstRawString> labels() => _labels;
        public List<AstRawString> own_labels() => _ownLabels;
        public bool is_iteration() => _isIteration;
        public bool is_target_for_anonymous() => _targetType == TargetType.TARGET_FOR_ANONYMOUS;
    }

    protected Target target_stack() => function_state_.target_stack_;

    // The free lists of the recycled Targets and FunctionStates.
    private Target free_targets_;
    private FunctionState free_function_states_;

    protected TStatement LookupBreakTarget(TIdentifier label)
    {
        bool anonymous = impl().IsNull(label);
        for (Target t = target_stack(); t != null; t = t.previous())
        {
            if ((anonymous && t.is_target_for_anonymous()) ||
                (!anonymous && ContainsLabel(t.labels(), impl().GetRawNameFromIdentifier(label))))
            {
                return t.statement();
            }
        }
        return impl().NullStatement();
    }

    protected TStatement LookupContinueTarget(TIdentifier label)
    {
        bool anonymous = impl().IsNull(label);
        for (Target t = target_stack(); t != null; t = t.previous())
        {
            if (!t.is_iteration()) continue;

            if (anonymous || ContainsLabel(t.own_labels(), impl().GetRawNameFromIdentifier(label)))
            {
                return impl().AsIterationStatement(t.statement());
            }
        }
        return impl().NullStatement();
    }

    //
    // Recycled through a per-parser free list, like Target.
    public sealed class FunctionState : IDisposable
    {
        private ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;

        // BlockState part.
        private Scope _outerScope;

        // Properties count estimation.
        private int _expectedPropertyCount;

        // How many suspends are needed for this function.
        private int _suspendCount;

        // How deeply nested we currently are in this function.
        internal int loop_nesting_depth_;

        private FunctionState _outerFunctionState;
        private DeclarationScope _scope;
        internal Target target_stack_; // for break, continue statements
        private FunctionState _nextFree;

        // A reason, if any, why this function should not be optimized.
        private BailoutReason _dontOptimizeReason;

        // Record whether the next (=== immediately following) function literal is
        // preceded by a parenthesis / exclamation mark. Also record the previous
        // state.
        // These are managed by the FunctionState constructor; the caller may only
        // call set_next_function_is_likely_called.
        internal bool next_function_is_likely_called_;
        internal bool previous_function_was_likely_called_;

        // Track if a function or eval occurs within this FunctionState
        internal bool contains_function_or_eval_;

        public static FunctionState New(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                          TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
                          TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
                          TFactory, TFuncNameInferrer> parser, DeclarationScope scope)
        {
            FunctionState state = parser.free_function_states_;
            if (state != null) parser.free_function_states_ = state._nextFree;
            else state = new FunctionState();
            state.Enter(parser, scope);
            return state;
        }

        private void Enter(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                          TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList,
                          TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
                          TFactory, TFuncNameInferrer> parser, DeclarationScope scope)
        {
            _parser = parser;
            _suspendCount = 0;
            loop_nesting_depth_ = 0;
            target_stack_ = null;
            _outerScope = parser.scope_;
            parser.scope_ = scope;
            _expectedPropertyCount = 0;
            _suspendCount = 0;
            _outerFunctionState = parser.function_state_;
            _scope = scope;
            _dontOptimizeReason = BailoutReason.kNoReason;
            next_function_is_likely_called_ = false;
            previous_function_was_likely_called_ = false;
            contains_function_or_eval_ = false;
            parser.function_state_ = this;
            if (_outerFunctionState != null)
            {
                _outerFunctionState.previous_function_was_likely_called_ =
                    _outerFunctionState.next_function_is_likely_called_;
                _outerFunctionState.next_function_is_likely_called_ = false;
            }
        }

        public void Dispose()
        {
            _parser.function_state_ = _outerFunctionState;
            _parser.scope_ = _outerScope;
            _outerFunctionState = null;
            _outerScope = null;
            _scope = null;
            target_stack_ = null;
            _nextFree = _parser.free_function_states_;
            _parser.free_function_states_ = this;
        }

        public DeclarationScope scope() => _scope.AsDeclarationScope();

        public void AddProperty() => _expectedPropertyCount++;
        public int expected_property_count() => _expectedPropertyCount;

        public void DisableOptimization(BailoutReason reason) => _dontOptimizeReason = reason;
        public BailoutReason dont_optimize_reason() => _dontOptimizeReason;

        public void AddSuspend() => _suspendCount++;
        public int suspend_count() => _suspendCount;
        public bool CanSuspend() => _suspendCount > 0;

        public FunctionKind kind() => scope().function_kind();

        public bool next_function_is_likely_called() => next_function_is_likely_called_;

        public bool previous_function_was_likely_called() => previous_function_was_likely_called_;

        public void set_next_function_is_likely_called()
            => next_function_is_likely_called_ = !_parser.v8_flags().max_lazy;

        public void RecordFunctionOrEvalCall() => contains_function_or_eval_ = true;
        public bool contains_function_or_eval() => contains_function_or_eval_;

        public int loop_nesting_depth() => loop_nesting_depth_;
    }

    public readonly struct FunctionOrEvalRecordingScope : IDisposable
    {
        private readonly FunctionState _state;
        private readonly bool _prevValue;

        public FunctionOrEvalRecordingScope(FunctionState state)
        {
            _state = state;
            _prevValue = state.contains_function_or_eval_;
            state.contains_function_or_eval_ = false;
        }

        public void Dispose()
        {
            bool found = _state.contains_function_or_eval_;
            if (!found)
            {
                _state.contains_function_or_eval_ = _prevValue;
            }
        }
    }

    public readonly struct LoopScope : IDisposable
    {
        private readonly FunctionState _functionState;

        public LoopScope(FunctionState function_state)
        {
            _functionState = function_state;
            _functionState.loop_nesting_depth_++;
        }

        public void Dispose() => _functionState.loop_nesting_depth_--;
    }

    public struct DeclarationDescriptor
    {
        public VariableMode mode;
        public VariableKind kind;
        public int declaration_pos;
        public int initialization_pos;
    }

    public sealed class DeclarationParsingResult
    {
        public struct Declaration(TExpression pattern, TExpression initializer)
        {
            public TExpression pattern = pattern;
            public TExpression initializer = initializer;
            public int value_beg_pos = kNoSourcePosition;
        }

        public DeclarationDescriptor descriptor;
        public readonly List<Declaration> declarations = [];
        public Scanner.Location first_initializer_loc = Scanner.Location.invalid();
        public Scanner.Location bindings_loc = Scanner.Location.invalid();
    }

    public sealed class CatchInfo(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
        TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
        TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> parser)
    {
        public TExpression pattern = parser.impl().NullExpression();
        public Variable variable;
        public Scope scope;
    }

    public sealed class ForInfo
    {
        public readonly List<AstRawString> bound_names = new(1);
        public ForEachStatement.VisitMode mode = ForEachStatement.VisitMode.ENUMERATE;
        public int position = kNoSourcePosition;
        public readonly DeclarationParsingResult parsing_result = new();
    }

    public sealed class ClassInfo(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
        TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
        TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> parser)
    {
        public TExpression extends = parser.impl().NullExpression();
        public TClassPropertyList public_members = parser.impl().NewClassPropertyList(4);
        public TClassPropertyList private_members = parser.impl().NewClassPropertyList(4);
        public TClassStaticElementList static_elements = parser.impl().NewClassStaticElementList(4);
        public TClassPropertyList instance_fields = parser.impl().NewClassPropertyList(4);
        public TFunctionLiteral constructor = parser.impl().NullFunctionLiteral();

        public bool has_static_elements() => static_elements_scope != null;
        public bool has_instance_members() => instance_members_scope != null;

        public DeclarationScope EnsureStaticElementsScope(ParserBase<TImpl, TExpression, TIdentifier, TStatement,
            TBlock, TFunctionLiteral, TObjectLiteralProperty, TClassLiteralProperty, TExpressionList,
            TObjectPropertyList, TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
            TFactory, TFuncNameInferrer> parser, int beg_pos, int info_id)
        {
            if (!has_static_elements())
            {
                FunctionKind kind = has_instance_members()
                    ? FunctionKind.ClassStaticInitializerFunctionPrecededByMember
                    : FunctionKind.ClassStaticInitializerFunction;
                static_elements_scope = parser.NewFunctionScope(kind);
                static_elements_scope.SetLanguageMode(LanguageMode.Strict);
                static_elements_scope.set_start_position(beg_pos);
                static_elements_function_id = info_id;
                // Actually consume the id. The id that was passed in might be an
                // earlier id in case of computed property names.
                parser.GetNextInfoId();
            }
            return static_elements_scope;
        }

        public DeclarationScope EnsureInstanceMembersScope(ParserBase<TImpl, TExpression, TIdentifier, TStatement,
            TBlock, TFunctionLiteral, TObjectLiteralProperty, TClassLiteralProperty, TExpressionList,
            TObjectPropertyList, TStatementList, TClassPropertyList, TClassStaticElementList, TFormalParameters,
            TFactory, TFuncNameInferrer> parser, int beg_pos, int info_id)
        {
            if (!has_instance_members())
            {
                FunctionKind kind = has_static_elements()
                    ? FunctionKind.ClassMembersInitializerFunctionPrecededByStatic
                    : FunctionKind.ClassMembersInitializerFunction;
                instance_members_scope = parser.NewFunctionScope(kind);
                instance_members_scope.SetLanguageMode(LanguageMode.Strict);
                instance_members_scope.set_start_position(beg_pos);
                instance_members_function_id = info_id;
                // Actually consume the id. The id that was passed in might be an
                // earlier id in case of computed property names.
                parser.GetNextInfoId();
            }
            return instance_members_scope;
        }

        public DeclarationScope static_elements_scope;
        public DeclarationScope instance_members_scope;
        public Variable home_object_variable;
        public Variable static_home_object_variable;
        public int autoaccessor_count;
        public int static_elements_function_id = -1;
        public int instance_members_function_id = -1;
        public int computed_field_count;
        public bool has_seen_constructor;
        public bool has_static_computed_names;
        public bool has_static_private_methods_or_accessors;
        public bool has_static_blocks;
        public bool requires_brand;
        public bool is_anonymous;
    }

    public enum PropertyPosition { kObjectLiteral, kClassLiteral }

    public sealed class ParsePropertyInfo : IDisposable
    {
        public ParsePropertyInfo(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                                     TObjectLiteralProperty, TClassLiteralProperty, TExpressionList,
                                     TObjectPropertyList, TStatementList, TClassPropertyList,
                                     TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> parser,
                                 AccumulationScope accumulation_scope = null)
        {
            this.accumulation_scope = accumulation_scope;
            name = parser.impl().NullIdentifier();
            position = PropertyPosition.kClassLiteral;
            function_flags = ParseFunctionFlags.kIsNormal;
            kind = ParsePropertyKind.kNotSet;
        }

        public bool ParsePropertyKindFromToken(Token token)
        {
            // This returns true, setting the property kind, iff the given token is
            // one which must occur after a property name, indicating that the
            // previous token was in fact a name and not a modifier (like the "get" in
            // "get x").
            switch (token)
            {
                case Token.Colon:
                    kind = ParsePropertyKind.kValue;
                    return true;
                case Token.Comma:
                    kind = ParsePropertyKind.kShorthand;
                    return true;
                case Token.RightBrace:
                    kind = ParsePropertyKind.kShorthandOrClassField;
                    return true;
                case Token.Assign:
                    kind = ParsePropertyKind.kAssign;
                    return true;
                case Token.LeftParen:
                    kind = ParsePropertyKind.kMethod;
                    return true;
                case Token.Mul:
                case Token.Semicolon:
                    kind = ParsePropertyKind.kClassField;
                    return true;
                default:
                    break;
            }
            return false;
        }

        public void Dispose() => allow_reindex_scope?.Dispose();

        public AccumulationScope accumulation_scope;
        public TIdentifier name;
        public PropertyPosition position;
        public ParseFunctionFlags function_flags;
        public ParsePropertyKind kind;
        public bool is_computed_name;
        public bool is_private;
        public bool is_static;
        public bool is_rest;
        public AllowReindexScope? allow_reindex_scope;
    }

    protected void DeclareLabel(ref List<AstRawString> labels, ref List<AstRawString> own_labels,
                                AstRawString label)
    {
        if (ContainsLabel(labels, label) || TargetStackContainsLabel(label))
        {
            ReportMessage(MessageTemplate.LabelRedeclaration, label);
            return;
        }

        // Add {label} to both {labels} and {own_labels}.
        if (labels == null)
        {
            labels = new List<AstRawString>(1);
            own_labels = new List<AstRawString>(1);
        }
        else
        {
            own_labels ??= new List<AstRawString>(1);
        }
        labels.Add(label);
        own_labels.Add(label);
    }

    protected static bool ContainsLabel(List<AstRawString> labels, AstRawString label)
    {
        if (labels != null)
        {
            for (int i = labels.Count; i-- > 0;)
            {
                if (labels[i] == label) return true;
            }
        }
        return false;
    }

    protected bool TargetStackContainsLabel(AstRawString label)
    {
        for (Target t = target_stack(); t != null; t = t.previous())
        {
            if (ContainsLabel(t.labels(), label)) return true;
        }
        return false;
    }

    protected static ClassLiteralProperty.Kind ClassPropertyKindFor(ParsePropertyKind kind)
    {
        switch (kind)
        {
            case ParsePropertyKind.kAutoAccessorClassField:
                return ClassLiteralProperty.Kind.AUTO_ACCESSOR;
            case ParsePropertyKind.kAccessorGetter:
                return ClassLiteralProperty.Kind.GETTER;
            case ParsePropertyKind.kAccessorSetter:
                return ClassLiteralProperty.Kind.SETTER;
            case ParsePropertyKind.kMethod:
                return ClassLiteralProperty.Kind.METHOD;
            case ParsePropertyKind.kClassField:
                return ClassLiteralProperty.Kind.FIELD;
            default:
                // Only returns for deterministic kinds
                throw new InvalidOperationException("UNREACHABLE");
        }
    }

    protected static VariableMode GetVariableMode(ClassLiteralProperty.Kind kind)
    {
        switch (kind)
        {
            case ClassLiteralProperty.Kind.FIELD:
                return VariableMode.Const;
            case ClassLiteralProperty.Kind.METHOD:
                return VariableMode.PrivateMethod;
            case ClassLiteralProperty.Kind.GETTER:
                return VariableMode.PrivateGetterOnly;
            case ClassLiteralProperty.Kind.SETTER:
                return VariableMode.PrivateSetterOnly;
            case ClassLiteralProperty.Kind.AUTO_ACCESSOR:
                return VariableMode.PrivateGetterAndSetter;
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    protected static AstRawString ClassFieldVariableName(AstValueFactory ast_value_factory, int index)
    {
        string name = ".class-field-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ast_value_factory.GetOneByteString(name);
    }

    protected static AstRawString AutoAccessorVariableName(AstValueFactory ast_value_factory, int index)
    {
        string name = ".accessor-storage-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ast_value_factory.GetOneByteString(name);
    }

    protected DeclarationScope NewScriptScope(REPLMode repl_mode)
        => new(ast_value_factory(), repl_mode, v8_flags_);

    protected DeclarationScope NewVarblockScope() => new(scope(), ScopeType.BLOCK_SCOPE);

    protected ModuleScope NewModuleScope(DeclarationScope parent) => new(parent, ast_value_factory());

    protected DeclarationScope NewEvalScope(Scope parent) => new(parent, ScopeType.EVAL_SCOPE);

    protected ClassScope NewClassScope(Scope parent, bool is_anonymous) => new(parent, is_anonymous);

    protected Scope NewBlockScopeForObjectLiteral()
    {
        Scope scope = NewScope(ScopeType.BLOCK_SCOPE);
        scope.set_is_block_scope_for_object_literal();
        return scope;
    }

    protected Scope NewScope(ScopeType scope_type) => NewScopeWithParent(scope(), scope_type);

    // This constructor should only be used when absolutely necessary. Most scopes
    // should automatically use scope() as parent, and be fine with
    // NewScope(ScopeType) above.
    protected static Scope NewScopeWithParent(Scope parent, ScopeType scope_type)
    {
        // Must always use the specific constructors for the blocklisted scope
        // types.
        return new Scope(parent, scope_type);
    }

    // Creates a function scope.
    public DeclarationScope NewFunctionScope(FunctionKind kind)
    {
        DeclarationScope result = new(scope(), ScopeType.FUNCTION_SCOPE, kind);

        // Record presence of an inner function scope
        function_state_.RecordFunctionOrEvalCall();

        // TODO(verwaest): Move into the DeclarationScope constructor.
        if (!IsArrowFunction(kind))
        {
            result.DeclareDefaultFunctionVariables(ast_value_factory());
        }
        return result;
    }

    protected DeclarationScope GetDeclarationScope() => scope().GetDeclarationScope();
    protected DeclarationScope GetClosureScope() => scope().GetClosureScope();

    public VariableProxy NewRawVariable(AstRawString name, int pos)
        => factory().ast_node_factory().NewVariableProxy(name, VariableKind.NORMAL_VARIABLE, pos);

    protected VariableProxy NewUnresolved(AstRawString name)
        => scope().NewUnresolved(factory().ast_node_factory(), name, scanner().location().beg_pos);

    protected VariableProxy NewUnresolved(AstRawString name, int begin_pos,
                                          VariableKind kind = VariableKind.NORMAL_VARIABLE)
        => scope().NewUnresolved(factory().ast_node_factory(), name, begin_pos, kind);

    public Scanner scanner() => scanner_;
    public AstValueFactory ast_value_factory() => ast_value_factory_;
    public int position() => scanner_.location().beg_pos;
    public int peek_position() => scanner_.peek_location().beg_pos;
    public int end_position() => scanner_.location().end_pos;
    public int peek_end_position() => scanner_.peek_location().end_pos;
    public bool stack_overflow() => pending_error_handler().stack_overflow();

    public void set_stack_overflow()
    {
        scanner_.set_parser_error();
        pending_error_handler().set_stack_overflow();
    }

    protected void CheckStackOverflow()
    {
        // Any further calls to Next or peek will return the illegal token.
        // The runtime check guards the .NET thread's own stack as well.
        if (GetCurrentStackPosition() < stack_limit_ || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            set_stack_overflow();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected Token peek() => scanner().peek();

    // Returns the position past the following semicolon (if it exists), and the
    // position past the end of the current token otherwise.
    protected int PositionAfterSemicolon() => (peek() == Token.Semicolon) ? peek_end_position() : end_position();

    protected Token PeekAheadAhead() => scanner().PeekAheadAhead();

    protected Token PeekAhead() => scanner().PeekAhead();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected Token Next() => scanner().Next();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void Consume(Token token) => scanner().Next();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool Check(Token token)
    {
        Token next = scanner().peek();
        if (next == token)
        {
            Consume(next);
            return true;
        }
        return false;
    }

    protected void Expect(Token token)
    {
        Token next = Next();
        if (next != token)
        {
            ReportUnexpectedToken(next);
        }
    }

    protected void ExpectSemicolon()
    {
        // Check for automatic semicolon insertion according to
        // the rules given in ECMA-262, section 7.9, page 21.
        Token tok = peek();
        if (tok == Token.Semicolon)
        {
            Next();
            return;
        }
        if (scanner().HasLineTerminatorBeforeNext() || Token.IsAutoSemicolon(tok))
        {
            return;
        }

        if (scanner().current_token() == Token.Await && !is_async_function())
        {
            if (flags().parsing_while_debugging() == ParsingWhileDebugging.Yes)
            {
                ReportMessageAt(scanner().location(), MessageTemplate.AwaitNotInDebugEvaluate);
            }
            else
            {
                ReportMessageAt(scanner().location(), MessageTemplate.AwaitNotInAsyncContext);
            }
            return;
        }

        ReportUnexpectedToken(Next());
    }

    protected bool peek_any_identifier() => Token.IsAnyIdentifier(peek());

    protected bool PeekContextualKeyword(AstRawString name)
        => peek() == Token.Identifier && !scanner().next_literal_contains_escapes() &&
           scanner().NextSymbol(ast_value_factory()) == name;

    protected bool PeekContextualKeyword(Token token)
        => peek() == token && !scanner().next_literal_contains_escapes();

    protected bool CheckContextualKeyword(AstRawString name)
    {
        if (PeekContextualKeyword(name))
        {
            Consume(Token.Identifier);
            return true;
        }
        return false;
    }

    protected bool CheckContextualKeyword(Token token)
    {
        if (PeekContextualKeyword(token))
        {
            Consume(token);
            return true;
        }
        return false;
    }

    protected void ExpectContextualKeyword(AstRawString name, string fullname = null, int pos = -1)
    {
        Expect(Token.Identifier);
        if (scanner().CurrentSymbol(ast_value_factory()) != name)
        {
            ReportUnexpectedToken(scanner().current_token());
        }
        if (scanner().literal_contains_escapes())
        {
            string full = fullname ?? name.Value;
            int start = pos == -1 ? position() : pos;
            impl().ReportMessageAt(new Scanner.Location(start, end_position()),
                                   MessageTemplate.InvalidEscapedMetaProperty, full);
        }
    }

    protected void ExpectContextualKeyword(Token token)
    {
        // Token Should be in range of Token::kIdentifier + 1 to Token::kAsync
        Token next = Next();
        if (next != token)
        {
            ReportUnexpectedToken(next);
        }
        if (scanner().literal_contains_escapes())
        {
            impl().ReportUnexpectedToken(Token.EscapedKeyword);
        }
    }

    protected bool CheckInOrOf(ref ForEachStatement.VisitMode visit_mode)
    {
        if (Check(Token.In))
        {
            visit_mode = ForEachStatement.VisitMode.ENUMERATE;
            return true;
        }
        else if (CheckContextualKeyword(Token.Of))
        {
            visit_mode = ForEachStatement.VisitMode.ITERATE;
            return true;
        }
        return false;
    }

    protected bool PeekInOrOf() => peek() == Token.In || PeekContextualKeyword(Token.Of);

    // Checks whether an octal literal was last seen between beg_pos and end_pos.
    // Only called for strict mode strings.
    protected void CheckStrictOctalLiteral(int beg_pos, int end_pos)
    {
        Scanner.Location octal = scanner().octal_position();
        if (octal.IsValid() && beg_pos <= octal.beg_pos && octal.end_pos <= end_pos)
        {
            MessageTemplate message = scanner().octal_message();
            impl().ReportMessageAt(octal, message);
            scanner().clear_octal_position();
            if (message == MessageTemplate.StrictDecimalWithLeadingZero)
            {
                impl().CountUsage(UseCounterFeature.kDecimalWithLeadingZeroInStrictMode);
            }
        }
    }

    // Checks if an octal literal or an invalid hex or unicode escape sequence
    // appears in the current template literal token. In the presence of such,
    // either returns false or reports an error, depending on should_throw.
    // Otherwise returns true.
    protected bool CheckTemplateEscapes(bool should_throw)
    {
        if (!scanner().has_invalid_template_escape()) return true;

        // Handle error case(s)
        if (should_throw)
        {
            impl().ReportMessageAt(scanner().invalid_template_escape_location(),
                                   scanner().invalid_template_escape_message());
        }
        scanner().clear_invalid_template_escape_message();
        return should_throw;
    }

    // Checking the name of a function literal. This has to be done after parsing
    // the function, since the function can declare itself strict.
    protected void CheckFunctionName(LanguageMode language_mode, TIdentifier function_name,
                                     FunctionNameValidity function_name_validity,
                                     Scanner.Location function_name_loc)
    {
        if (impl().IsNull(function_name)) return;
        if (function_name_validity == FunctionNameValidity.kSkipFunctionNameCheck) return;
        // The function name needs to be checked in strict mode.
        if (is_sloppy(language_mode)) return;

        if (impl().IsEvalOrArguments(function_name))
        {
            impl().ReportMessageAt(function_name_loc, MessageTemplate.StrictEvalArguments);
            return;
        }
        if (function_name_validity == FunctionNameValidity.kFunctionNameIsStrictReserved)
        {
            impl().ReportMessageAt(function_name_loc, MessageTemplate.UnexpectedStrictReserved);
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TFactory factory() => ast_node_factory_;

    protected DeclarationScope GetReceiverScope() => scope().GetReceiverScope();
    public LanguageMode language_mode() => scope().language_mode();

    protected void RaiseLanguageMode(LanguageMode mode)
    {
        LanguageMode old = scope().language_mode();
        impl().SetLanguageMode(scope(), old > mode ? old : mode);
    }

    protected bool is_generator() => IsGeneratorFunction(function_state_.kind());
    protected bool is_async_function() => IsAsyncFunction(function_state_.kind());
    protected bool is_async_generator() => IsAsyncGeneratorFunction(function_state_.kind());
    protected bool is_resumable() => IsResumableFunction(function_state_.kind());
    protected bool is_await_allowed() => is_async_function() || IsModule(function_state_.kind());

    protected bool is_await_as_identifier_disallowed()
        => flags().is_module() || IsAwaitAsIdentifierDisallowed(function_state_.kind());

    protected static bool IsAwaitAsIdentifierDisallowed(FunctionKind kind)
    {
        // 'await' is always disallowed as an identifier in module contexts. Callers
        // should short-circuit the module case instead of calling this.
        //
        // There is one special case: direct eval inside a module. In that case,
        // even though the eval script itself is parsed as a Script (not a Module,
        // i.e. flags().is_module() is false), thus allowing await as an identifier
        // by default, the immediate outer scope is a module scope.
        return IsAsyncFunction(kind) || IsClassStaticInitializerFunction(kind);
    }

    protected bool is_using_allowed()
    {
        // UsingDeclaration and AwaitUsingDeclaration are Syntax Errors if the goal
        // symbol is Script. UsingDeclaration and AwaitUsingDeclaration are Syntax
        // Errors if they are not contained, either directly or indirectly, within a
        // Block, ForStatement, ForInOfStatement, FunctionBody, GeneratorBody,
        // AsyncGeneratorBody, AsyncFunctionBody, ClassStaticBlockBody, or
        // ClassBody. They are disallowed in 'bare' switch cases.
        // Unless the current scope's ScopeType is ScriptScope, the
        // current position is directly or indirectly within one of the productions
        // listed above since they open a new scope.
        return ((scope().scope_type() != ScopeType.SCRIPT_SCOPE &&
                 scope().scope_type() != ScopeType.EVAL_SCOPE) ||
                scope().scope_type() == ScopeType.REPL_MODE_SCOPE) &&
               !scope().is_nonlinear();
    }

    protected bool IsNextUsingKeyword(bool is_await_using)
    {
        // using and await using declarations in for-of statements must be followed
        // by a non-pattern ForBinding.
        //
        // `of`: for ( [lookahead ≠ using of] ForDeclaration[?Yield, ?Await, +Using]
        //       of AssignmentExpression[+In, ?Yield, ?Await] )
        Token token_after_using = is_await_using ? PeekAheadAhead() : PeekAhead();
        switch (token_after_using)
        {
            case Token.Identifier:
            case Token.Static:
            case Token.Let:
            case Token.Yield:
            case Token.Await:
            case Token.Get:
            case Token.Set:
            case Token.Using:
            case Token.Accessor:
            case Token.Async:
                return true;
            case Token.Of:
                if (is_await_using)
                {
                    return true;
                }
                else
                {
                    // In the case of synchronous `using`, `of` is disallowed as well
                    // with a negative lookahead for for-of loops. But, cursedly,
                    // `using of` is allowed as the initializer of C-style for loops,
                    // e.g. `for (using of = null;;)` parses.
                    Token token_after_of = PeekAheadAhead();
                    return token_after_of == Token.Assign;
                }
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                return is_sloppy(language_mode());
            default:
                return false;
        }
    }

    protected bool IfStartsWithUsingOrAwaitUsingKeyword()
    {
        // ForDeclaration[Yield, Await, Using] : ...
        //    [+Using] using [no LineTerminator here] ForBinding[?Yield, ?Await,
        //    ~Pattern]
        //    [+Using, +Await] await [no LineTerminator here] using [no
        //    LineTerminator here] ForBinding[?Yield, +Await, ~Pattern]
        return (peek() == Token.Using && !scanner().HasLineTerminatorAfterNext() &&
                IsNextUsingKeyword(/* is_await_using */ false)) ||
               (is_await_allowed() && peek() == Token.Await && !scanner().HasLineTerminatorAfterNext() &&
                PeekAhead() == Token.Using && !scanner().HasLineTerminatorAfterNextNext() &&
                IsNextUsingKeyword(/* is_await_using */ true));
    }

    public PendingCompilationErrorHandler pending_error_handler() => pending_error_handler_;

    // Report syntax errors.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessage(MessageTemplate message)
        => ReportMessageAt(scanner().location(), message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessage(MessageTemplate message, string arg)
        => ReportMessageAt(scanner().location(), message, arg);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessage(MessageTemplate message, AstRawString arg)
        => ReportMessageAt(scanner().location(), message, arg);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessage(MessageTemplate message, AstRawString arg0, AstRawString arg1, string arg2)
        => ReportMessageAt(scanner().location(), message, arg0, arg1, arg2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessageAt(Scanner.Location source_location, MessageTemplate message)
    {
        impl().pending_error_handler().ReportMessageAt(source_location.beg_pos, source_location.end_pos, message);
        scanner().set_parser_error();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessageAt(Scanner.Location source_location, MessageTemplate message, string arg)
    {
        impl().pending_error_handler().ReportMessageAt(source_location.beg_pos, source_location.end_pos, message,
                                                       arg);
        scanner().set_parser_error();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessageAt(Scanner.Location source_location, MessageTemplate message, AstRawString arg)
    {
        impl().pending_error_handler().ReportMessageAt(source_location.beg_pos, source_location.end_pos, message,
                                                       arg);
        scanner().set_parser_error();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessageAt(Scanner.Location source_location, MessageTemplate message, AstRawString arg0,
                                string arg1)
    {
        impl().pending_error_handler().ReportMessageAt(source_location.beg_pos, source_location.end_pos, message,
                                                       arg0, arg1);
        scanner().set_parser_error();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportMessageAt(Scanner.Location source_location, MessageTemplate message, AstRawString arg0,
                                AstRawString arg1, string arg2)
    {
        impl().pending_error_handler().ReportMessageAt(source_location.beg_pos, source_location.end_pos, message,
                                                       arg0, arg1, arg2);
        scanner().set_parser_error();
    }


    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReportUnexpectedToken(Token token) => impl().ReportUnexpectedTokenAt(scanner_.location(), token);

    protected void ValidateFormalParameters(LanguageMode language_mode, TFormalParameters parameters,
                                            bool allow_duplicates)
    {
        if (!allow_duplicates) impl().ValidateDuplicate(parameters);
        if (is_strict(language_mode)) impl().ValidateStrictMode(parameters);
    }

    // Needs to be called if the reference needs to be available from the current
    // point. It causes the receiver to be context allocated if necessary.
    // Returns the receiver variable that we're referencing.
    protected void UseThis()
    {
        Scope scope = this.scope();
        DeclarationScope closure_scope = scope.GetClosureScope();
        if (closure_scope.from_scope_info()) return;
        DeclarationScope receiver_scope = closure_scope.GetReceiverScope();
        Variable var = receiver_scope.receiver();
        var.set_is_used();
        if (closure_scope == receiver_scope)
        {
            // It's possible that we're parsing the head of an arrow function, in
            // which case we haven't realized yet that closure_scope !=
            // receiver_scope. Mark through the ExpressionScope for now.
            expression_scope().RecordThisUse();
        }
        else
        {
            closure_scope.set_has_this_reference(true);
            var.ForceContextAllocation();
        }
    }

    protected AstRawString GetNextSymbolForRegExpLiteral() => scanner().NextSymbol(ast_value_factory());

    protected static bool IsAccessor(ParsePropertyKind kind)
        => kind is >= ParsePropertyKind.kAccessorGetter and <= ParsePropertyKind.kAccessorSetter;

    // Whether we're parsing a single-expression arrow function or something else.
    protected enum FunctionBodyType { kExpression, kBlock }

    // Check if the scope has conflicting var/let declarations from different
    // scopes. This covers for example
    //
    // function f() { { { var x; } let x; } }
    // function g() { { var x; let x; } }
    //
    // The var declarations are hoisted to the function scope, but originate from
    // a scope where the name has also been let bound or the var declaration is
    // hoisted over such a scope.
    protected void CheckConflictingVarDeclarations(DeclarationScope scope)
    {
        bool allowed_catch_binding_var_redeclaration = false;
        Declaration decl = scope.CheckConflictingVarDeclarations(ref allowed_catch_binding_var_redeclaration);
        if (allowed_catch_binding_var_redeclaration)
        {
            impl().CountUsage(UseCounterFeature.kVarRedeclaredCatchBinding);
        }
        if (decl != null)
        {
            // In ES6, conflicting variable bindings are early errors.
            AstRawString name = decl.var().raw_name();
            int position = decl.position();
            Scanner.Location location = position == kNoSourcePosition
                ? Scanner.Location.invalid()
                : new Scanner.Location(position, position + 1);
            impl().ReportMessageAt(location, MessageTemplate.VarRedeclaration, name);
        }
    }

    protected bool IsLet(AstRawString identifier) => identifier == ast_value_factory().let_string();

    protected bool IsAssignableIdentifier(TExpression expression)
    {
        if (!impl().IsIdentifier(expression)) return false;
        if (is_strict(language_mode()) && impl().IsEvalOrArguments(impl().AsIdentifier(expression)))
        {
            return false;
        }
        return true;
    }

    protected enum SubFunctionKind { kFunction, kNonStaticMethod, kStaticMethod }

    private static readonly FunctionKind[] s_functionKinds =
    [
        // SubFunctionKind::kNormalFunction
        // is_generator=false
        FunctionKind.NormalFunction, FunctionKind.AsyncFunction,
        // is_generator=true
        FunctionKind.GeneratorFunction, FunctionKind.AsyncGeneratorFunction,
        // SubFunctionKind::kNonStaticMethod
        // is_generator=false
        FunctionKind.ConciseMethod, FunctionKind.AsyncConciseMethod,
        // is_generator=true
        FunctionKind.ConciseGeneratorMethod, FunctionKind.AsyncConciseGeneratorMethod,
        // SubFunctionKind::kStaticMethod
        // is_generator=false
        FunctionKind.StaticConciseMethod, FunctionKind.StaticAsyncConciseMethod,
        // is_generator=true
        FunctionKind.StaticConciseGeneratorMethod, FunctionKind.StaticAsyncConciseGeneratorMethod,
    ];

    protected static FunctionKind FunctionKindForImpl(SubFunctionKind sub_function_kind, ParseFunctionFlags flags)
    {
        int is_generator = (flags & ParseFunctionFlags.kIsGenerator) != 0 ? 1 : 0;
        int is_async = (flags & ParseFunctionFlags.kIsAsync) != 0 ? 1 : 0;
        return s_functionKinds[(int)sub_function_kind * 4 + is_generator * 2 + is_async];
    }

    protected static FunctionKind FunctionKindFor(ParseFunctionFlags flags)
        => FunctionKindForImpl(SubFunctionKind.kFunction, flags);

    protected static FunctionKind MethodKindFor(bool is_static, ParseFunctionFlags flags)
        => FunctionKindForImpl(is_static ? SubFunctionKind.kStaticMethod : SubFunctionKind.kNonStaticMethod, flags);

    // Keep track of eval() calls since they disable all local variable
    // optimizations. This checks if expression is an eval call, and if yes,
    // forwards the information to scope.
    protected bool CheckPossibleEvalCall(TExpression expression, bool is_optional_call, Scope scope)
    {
        if (impl().IsIdentifier(expression) && impl().IsEval(impl().AsIdentifier(expression)) && !is_optional_call)
        {
            function_state_.RecordFunctionOrEvalCall();
            scope.RecordEvalCall();
            return true;
        }
        return false;
    }

    // Convenience method which determines the type of return statement to emit
    // depending on the current function type.
    protected TStatement BuildReturnStatement(TExpression expr, int pos,
                                              int end_pos = ReturnStatement.kFunctionLiteralReturnPosition)
    {
        if (impl().IsNull(expr))
        {
            expr = factory().NewUndefinedLiteral(kNoSourcePosition);
        }
        else if (is_async_generator())
        {
            // In async generators, if there is an explicit operand to the return
            // statement, await the operand.
            expr = factory().NewAwait(expr, kNoSourcePosition);
            function_state_.AddSuspend();
        }
        if (is_async_function())
        {
            return factory().NewAsyncReturnStatement(expr, pos, end_pos);
        }
        return factory().NewReturnStatement(expr, pos, end_pos);
    }

    protected SourceTextModuleDescriptor module() => scope().AsModuleScope().module();

    public Scope scope() => scope_;

    // Stack of expression expression_scopes.
    // The top of the stack is always pointed to by expression_scope().
    protected ExpressionScope expression_scope() => expression_scope_;

    public void set_max_drift(int drift) => max_drift_.Value = drift;
    public int max_drift() => max_drift_.Value;

    public bool MaybeParsingArrowhead()
        => expression_scope_ != null && expression_scope_.has_possible_arrow_parameter_in_scope_chain();

    public readonly struct AcceptINScope : IDisposable
    {
        private readonly ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;
        private readonly bool _previousAcceptIN;

        public AcceptINScope(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                                 TObjectLiteralProperty, TClassLiteralProperty, TExpressionList,
                                 TObjectPropertyList, TStatementList, TClassPropertyList, TClassStaticElementList,
                                 TFormalParameters, TFactory, TFuncNameInferrer> parser, bool accept_IN)
        {
            _parser = parser;
            _previousAcceptIN = parser.accept_IN_;
            parser.accept_IN_ = accept_IN;
        }

        public void Dispose() => _parser.accept_IN_ = _previousAcceptIN;
    }

    public readonly struct ParameterParsingScope : IDisposable
    {
        private readonly ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;
        private readonly TFormalParameters _parentParameters;

        public ParameterParsingScope(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock,
                                         TFunctionLiteral, TObjectLiteralProperty, TClassLiteralProperty,
                                         TExpressionList, TObjectPropertyList, TStatementList, TClassPropertyList,
                                         TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
                                         parser, TFormalParameters parameters)
        {
            _parser = parser;
            _parentParameters = parser.parameters_;
            parser.parameters_ = parameters;
        }

        public void Dispose() => _parser.parameters_ = _parentParameters;
    }

    public readonly struct FunctionParsingScope : IDisposable
    {
        private readonly ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
            TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
            TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer> _parser;
        private readonly ExpressionScope _expressionScope;

        public FunctionParsingScope(ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock,
                                        TFunctionLiteral, TObjectLiteralProperty, TClassLiteralProperty,
                                        TExpressionList, TObjectPropertyList, TStatementList, TClassPropertyList,
                                        TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
                                        parser)
        {
            _parser = parser;
            _expressionScope = parser.expression_scope_;
            parser.expression_scope_ = null;
        }

        public void Dispose() => _parser.expression_scope_ = _expressionScope;
    }

    // FuncNameInferrer::State.
    public readonly struct FuncNameInferrerState : IDisposable
    {
        private readonly TFuncNameInferrer _fni;
        private readonly int _top;

        public FuncNameInferrerState(TFuncNameInferrer fni)
        {
            _fni = fni;
            _top = fni.EnterState();
        }

        public void Dispose() => _fni.LeaveState(_top);
    }

    public List<object> pointer_buffer() => pointer_buffer_;
    public List<(VariableProxy, int)> variable_buffer() => variable_buffer_;

    // Parser base's protected field members.

    protected internal Scope scope_; // Scope stack.
    // Stack of scopes for object literals we're currently parsing.
    protected internal Scope object_literal_scope_;
    protected internal Scope original_scope_; // The top scope for the current parsing item.
    protected internal FunctionState function_state_; // Function state stack.
    protected readonly TFuncNameInferrer fni_;
    protected readonly AstValueFactory ast_value_factory_; // Not owned.
    protected readonly TFactory ast_node_factory_;
    protected internal readonly StrongBox<int> max_drift_ = new(0);
    protected readonly PendingCompilationErrorHandler pending_error_handler_;

    // Parser base's private field members.
    protected void set_has_module_in_scope_chain() => has_module_in_scope_chain_ = true;

    private ExpressionScope expression_scope_;

    private readonly List<object> pointer_buffer_;
    private readonly List<(VariableProxy, int)> variable_buffer_;

    private readonly Scanner scanner_;

    private readonly UnoptimizedCompileFlags flags_;
    private readonly ParsingFlags v8_flags_;
    private int info_id_;

    private bool has_module_in_scope_chain_;

    private FunctionLiteral.EagerCompileHint default_eager_compile_hint_;
    protected readonly bool compile_hints_magic_enabled_;
    protected readonly bool compile_hints_per_function_magic_enabled_;

    // This struct is used to move information about the next arrow function from
    // the place where the arrow head was parsed to where the body will be parsed.
    // Nothing can be parsed between the head and the body, so it will be consumed
    // immediately after it's produced.
    // Preallocating the struct as part of the parser minimizes the cost of
    // supporting arrow functions on non-arrow expressions.
    protected internal struct NextArrowFunctionInfo
    {
        public Scanner.Location strict_parameter_error_location;
        public MessageTemplate strict_parameter_error_message;
        public DeclarationScope scope;
        public int function_literal_id;
        public bool could_be_immediately_invoked;
        public bool has_allow_reindex_scope;

        public static NextArrowFunctionInfo Initial() => new()
        {
            strict_parameter_error_location = Scanner.Location.invalid(),
            strict_parameter_error_message = MessageTemplate.None,
            function_literal_id = -1,
        };

        public readonly bool HasInitialState() => scope == null;

        public void Reset()
        {
            scope = null;
            function_literal_id = -1;
            ClearStrictParameterError();
            could_be_immediately_invoked = false;
            has_allow_reindex_scope = false;
        }

        // Tracks strict-mode parameter violations of sloppy-mode arrow heads in
        // case the function ends up becoming strict mode. Only one global place to
        // track this is necessary since arrow functions with none-simple parameters
        // cannot become strict-mode later on.
        public void ClearStrictParameterError()
        {
            strict_parameter_error_location = Scanner.Location.invalid();
            strict_parameter_error_message = MessageTemplate.None;
        }
    }

    protected internal TFormalParameters parameters_;
    protected internal NextArrowFunctionInfo next_arrow_function_info_ = NextArrowFunctionInfo.Initial();

    // The position of the token following the start parenthesis in the production
    // PrimaryExpression :: '(' Expression ')'
    protected int position_after_last_primary_expression_open_parenthesis_ = -1;

    protected internal bool accept_IN_ = true;
    private bool allow_eval_cache_ = true;
}
