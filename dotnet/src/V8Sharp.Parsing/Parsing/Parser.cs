// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser.h and the first part of src/parsing/parser.cc.
//
// Heap objects the Parser reads in V8 (Script, SharedFunctionInfo,
// ScopeInfo) reach it through the IParsingScript /
// IParsingSharedFunctionInfo / IScopeInfo interfaces; the engine implements
// them. Runtime call stats, trace events and --log-function-events are not
// ported.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public sealed class ParserFormalParameters(DeclarationScope scope) : FormalParametersBase(scope)
{
    public sealed class Parameter(Expression pattern, Expression initializer, int position,
                                  int initializer_end_position, bool is_rest)
        : IThreadedListNode<Parameter>
    {
        private readonly Expression initializer_ = initializer;
        private readonly bool is_rest_ = is_rest;

        public Expression pattern = pattern;
        public Expression initializer() => initializer_;
        public int position = position;
        public int initializer_end_position = initializer_end_position;
        public bool is_rest() => is_rest_;

        public Parameter next_parameter;

        public bool is_simple() => pattern.IsVariableProxy() && initializer() == null && !is_rest();

        public AstRawString name() => ((VariableProxy)pattern).raw_name();

        public Parameter NextNode
        {
            get => next_parameter;
            set => next_parameter = value;
        }
    }

    public override void set_strict_parameter_error(Scanner.Location loc, MessageTemplate message)
    {
        strict_error_loc = loc;
        strict_error_message = message;
    }

    public bool has_duplicate() => duplicate_loc.IsValid();

    public void ValidateDuplicate(Parser parser)
    {
        if (has_duplicate())
        {
            parser.ReportMessageAt(duplicate_loc, MessageTemplate.ParamDupe);
        }
    }

    public void ValidateStrictMode(Parser parser)
    {
        if (strict_error_loc.IsValid())
        {
            parser.ReportMessageAt(strict_error_loc, strict_error_message);
        }
    }

    public readonly ThreadedList<Parameter> @params = new();
    public Scanner.Location duplicate_loc = Scanner.Location.invalid();
    public Scanner.Location strict_error_loc = Scanner.Location.invalid();
    public MessageTemplate strict_error_message = MessageTemplate.None;
}

// What the Parser reads from a Script (the heap object in V8).
public interface IParsingScript : IScriptEvalOrigin
{
    // The script source (Script::source()).
    string source();
    int id();
    bool is_wrapped();
    // Script::wrapped_arguments(), when is_wrapped().
    IReadOnlyList<string> wrapped_arguments();
    int line_offset();
    int column_offset();
}

// What the Parser reads from a SharedFunctionInfo when reparsing a function.
public interface IParsingSharedFunctionInfo
{
    IParsingScript script();
    int StartPosition();
    int EndPosition();
    bool HasOuterScopeInfo();
    IScopeInfo GetOuterScopeInfo();
    bool is_wrapped();
    int function_literal_id();
    string Name();
    bool private_name_lookup_skips_outer_class();
}

public sealed partial class Parser : ParserBaseOfParser
{
    public static bool IsPreParser() => false;

    private enum Mode { PARSE_LAZILY, PARSE_EAGERLY }

    // Runtime encoding of different completion modes.
    private enum CompletionKind
    {
        kNormalCompletion,
        kThrowCompletion,
        kAbruptCompletion,
    }

    public override bool AllowsLazyParsingWithoutUnresolvedVariables()
        => !MaybeParsingArrowhead() && scope().AllowsLazyParsingWithoutUnresolvedVariables(original_scope_);

    public override bool parse_lazily() => mode_ == Mode.PARSE_LAZILY;

    private Variable NewTemporary(AstRawString name) => scope().NewTemporary(name);

    // RAII ScopedModification<Mode> for mode_.
    private readonly struct ModeScope : IDisposable
    {
        private readonly Parser _parser;
        private readonly Mode _old;

        public ModeScope(Parser parser, Mode mode)
        {
            _parser = parser;
            _old = parser.mode_;
            parser.mode_ = mode;
        }

        public void Dispose() => _parser.mode_ = _old;
    }

    private PreParser reusable_preparser()
    {
        if (reusable_preparser_ == null)
        {
            reusable_preparser_ = new PreParser(scanner_, ast_value_factory(), pending_error_handler(), flags(),
                                                v8_flags());
            reusable_preparser_.set_allow_eval_cache(allow_eval_cache());
        }
        return reusable_preparser_;
    }

    public override bool IdentifierEquals(AstRawString identifier, AstRawString other) => identifier == other;

    public override bool HasCheckedSyntax() => scope().GetDeclarationScope().has_checked_syntax();

    public override Expression InitializeObjectLiteral(Expression object_literal)
    {
        ((ObjectLiteral)object_literal).CalculateEmitStore();
        return object_literal;
    }

    // Helper functions for recursive descent.
    public override bool IsEval(AstRawString identifier) => identifier == ast_value_factory().eval_string();

    public bool IsAsync(AstRawString identifier) => identifier == ast_value_factory().async_string();

    public override bool IsArguments(AstRawString identifier) => identifier == ast_value_factory().arguments_string();

    public override bool IsEvalOrArguments(AstRawString identifier) => IsEval(identifier) || IsArguments(identifier);

    // Returns true if the expression is of type "this.foo".
    public override bool IsThisProperty(Expression expression)
    {
        Property property = expression.AsProperty();
        return property != null && property.obj().IsThisExpression();
    }

    // Returns true if the expression is of type "obj.#foo" or "obj?.#foo".
    public override bool IsPrivateReference(Expression expression)
    {
        Property property = expression.AsProperty();
        if (expression.IsOptionalChain())
        {
            Expression expr_inner = ((OptionalChain)expression).expression();
            property = expr_inner.AsProperty();
        }
        return property != null && property.IsPrivateReference();
    }

    // This returns true if the expression is an identifier (wrapped
    // inside a variable proxy).  We exclude the case of 'this', which
    // has been converted to a variable proxy.
    public override bool IsIdentifier(Expression expression)
    {
        VariableProxy operand = expression.AsVariableProxy();
        return operand != null && !operand.is_new_target();
    }

    public override AstRawString AsIdentifier(Expression expression) => expression.AsVariableProxy().raw_name();

    public VariableProxy AsIdentifierExpression(Expression expression) => expression.AsVariableProxy();

    public override bool IsConstructor(AstRawString identifier)
        => identifier == ast_value_factory().constructor_string();

    public override bool IsBoilerplateProperty(ObjectLiteralProperty property) => !property.IsPrototype();

    public object extension() => info_.extension();

    public override bool ParsingExtension() => extension() != null;

    public override bool IsNative(Expression expr)
        => expr.IsVariableProxy() && expr.AsVariableProxy().raw_name() == ast_value_factory().native_string();

    public override bool IsArrayIndex(AstRawString @string, out uint index) => @string.AsArrayIndex(out index);

    // Returns true if the statement is an expression statement containing
    // a single string literal.  If a second argument is given, the literal
    // is also compared with it and the result is true only if they are equal.
    public override bool IsStringLiteral(Statement statement) => IsStringLiteral(statement, null);

    public bool IsStringLiteral(Statement statement, AstRawString arg)
    {
        if (statement is not ExpressionStatement e_stat) return false;
        Literal literal = e_stat.expression().AsLiteral();
        if (literal == null || !literal.IsRawString()) return false;
        return arg == null || literal.AsRawString() == arg;
    }

    public override void GetDefaultStrings(out AstRawString default_string, out AstRawString dot_default_string)
    {
        default_string = ast_value_factory().default_string();
        dot_default_string = ast_value_factory().dot_default_string();
    }

    // Functions for encapsulating the differences between parsing and preparsing;
    // operations interleaved with the recursive descent.
    public override void PushLiteralName(AstRawString id) => fni_.PushLiteralName(id);

    public void PushVariableName(AstRawString id) => fni_.PushVariableName(id);

    public override void PushPropertyName(Expression expression)
    {
        if (expression.IsPropertyName())
        {
            fni_.PushLiteralName(expression.AsLiteral().AsRawPropertyName());
        }
        else
        {
            fni_.PushLiteralName(ast_value_factory().computed_string());
        }
    }

    public override void PushEnclosingName(AstRawString name) => fni_.PushEnclosingName(name);

    public override void AddFunctionForNameInference(FunctionLiteral func_to_infer) => fni_.AddFunction(func_to_infer);

    public override void InferFunctionName() => fni_.Infer();

    // If we assign a function literal to a property we pretenure the
    // literal so it can be added as a constant function property.
    public override void CheckAssigningFunctionLiteralToProperty(Expression left, Expression right)
    {
        if (left.IsProperty() && right.IsFunctionLiteral())
        {
            ((FunctionLiteral)right).set_pretenure();
        }
    }

    // Generate AST node that throws a ReferenceError with the given type.
    public override Expression NewThrowReferenceError(MessageTemplate message, int pos)
        => NewThrowError(V8Sharp.Runtime.FunctionId.NewReferenceError, message, ast_value_factory().empty_string(),
                         pos);

    public override AstRawString GetRawNameFromIdentifier(AstRawString arg) => arg;

    public override AstRawString IdentifierToAstRawString(AstRawString arg) => arg;

    public override Statement AsIterationStatement(Statement s) => s.AsIterationStatement();

    // "null" return type creators.
    public override AstRawString NullIdentifier() => null;
    public override Expression NullExpression() => null;
    public override Statement NullStatement() => null;
    public override Block NullBlock() => null;
    public override FunctionLiteral NullFunctionLiteral() => null;
    public override Expression FailureExpression() => factory().FailureExpression();

    public override bool IsNull(AstRawString subject) => subject == null;
    public override bool IsNull(Expression subject) => subject == null;
    public override bool IsNull(Statement subject) => subject == null;
    public override bool IsNullProperty(ObjectLiteralProperty subject) => subject == null;

    public override bool IsIterationStatement(Statement subject) => subject.AsIterationStatement() != null;

    // Non-null empty string.
    public override AstRawString EmptyIdentifierString() => ast_value_factory().empty_string();

    public override bool IsEmptyIdentifier(AstRawString subject) => subject.IsEmpty();

    // Producing data during the recursive descent.
    public override AstRawString GetSymbol() => scanner().CurrentSymbol(ast_value_factory());

    public override AstRawString GetIdentifier() => GetSymbol();

    public AstRawString GetNextSymbol() => scanner().NextSymbol(ast_value_factory());

    public override AstRawString GetNumberAsSymbol()
    {
        double double_value = scanner().DoubleValue();
        string @string = NumberConversions.DoubleToCString(double_value);
        return ast_value_factory().GetOneByteString(@string);
    }

    public override Expression ThisExpression()
    {
        UseThis();
        return factory().ThisExpression();
    }

    public override Expression NewThisExpression(int pos)
    {
        UseThis();
        return factory().NewThisExpression(pos);
    }

    public override Expression ExpressionFromPrivateName(ref PrivateNameScopeIterator private_name_scope,
                                                         AstRawString name, int start_position)
    {
        VariableProxy proxy = factory().ast_node_factory().NewVariableProxy(name, VariableKind.NORMAL_VARIABLE,
                                                                            start_position);
        private_name_scope.AddUnresolvedPrivateName(proxy);
        return proxy;
    }

    public override Expression ExpressionFromIdentifier(AstRawString name, int start_position,
                                                        InferName infer = InferName.kYes)
    {
        if (infer == InferName.kYes)
        {
            fni_.PushVariableName(name);
        }
        return expression_scope().NewVariable(name, start_position);
    }

    public override void DeclareIdentifier(AstRawString name, int start_position)
        => expression_scope().Declare(name, start_position);

    public override Variable DeclareCatchVariableName(Scope scope, AstRawString name)
        => scope.DeclareCatchVariableName(name);

    public override List<ClassLiteralProperty> NewClassPropertyList(int size) => new(size);

    public override List<ClassLiteralStaticElement> NewClassStaticElementList(int size) => new(size);

    public override Statement NewThrowStatement(Expression exception, int pos)
        => factory().NewExpressionStatement(factory().NewThrow(exception, pos), pos);

    public override void AddFormalParameter(ParserFormalParameters parameters, Expression pattern,
                                            Expression initializer, int initializer_end_position, bool is_rest)
    {
        parameters.UpdateArityAndFunctionLength(initializer != null, is_rest);
        ParserFormalParameters.Parameter parameter = new(pattern, initializer, scanner().location().beg_pos,
                                                         initializer_end_position, is_rest);

        parameters.@params.Add(parameter);
    }

    public override void DeclareFormalParameters(ParserFormalParameters parameters)
    {
        bool is_simple = parameters.is_simple;
        DeclarationScope scope = parameters.scope;
        if (!is_simple) scope.MakeParametersNonSimple();
        foreach (ParserFormalParameters.Parameter parameter in parameters.@params)
        {
            bool is_optional = parameter.initializer() != null;
            // If the parameter list is simple, declare the parameters normally with
            // their names. If the parameter list is not simple, declare a temporary
            // for each parameter - the corresponding named variable is declared by
            // BuildParameterInitializationBlock.
            scope.DeclareParameter(is_simple ? parameter.name() : ast_value_factory().empty_string(),
                                   is_simple ? VariableMode.Var : VariableMode.Temporary, is_optional,
                                   parameter.is_rest(), ast_value_factory(), parameter.position);
        }
    }

    public override void CountUsage(UseCounterFeature feature) => ++use_counts_[(int)feature];

    // Returns true iff we're parsing the first function literal during
    // CreateDynamicFunction().
    public override bool ParsingDynamicFunctionDeclaration() => parameters_end_pos_ != kNoSourcePosition;

    private void ConvertBinaryToNaryOperationSourceRange(BinaryOperation binary_op, NaryOperation nary_op)
    {
        if (source_range_map_ == null) return;

        if (source_range_map_.Find(binary_op) is not BinaryOperationSourceRanges ranges) return;

        SourceRange range = ranges.GetRange(SourceRangeKind.kRight);
        source_range_map_.Insert(nary_op, new NaryOperationSourceRanges(range));
    }

    private void AppendNaryOperationSourceRange(NaryOperation node, SourceRange range)
    {
        if (source_range_map_ == null) return;
        if (source_range_map_.Find(node) is not NaryOperationSourceRanges ranges) return;

        ranges.AddRange(range);
    }

    public override void RecordBlockSourceRange(Block node, int continuation_position)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert(node, new BlockSourceRanges(continuation_position));
    }

    public override void RecordCaseClauseSourceRange(object node, SourceRange body_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((CaseClause)node, new CaseClauseSourceRanges(body_range));
    }

    private void AppendConditionalChainSourceRange(ConditionalChain node, SourceRange range)
    {
        if (source_range_map_ == null) return;
        ConditionalChainSourceRanges ranges = (ConditionalChainSourceRanges)source_range_map_.Find(node);
        if (ranges == null)
        {
            source_range_map_.Insert(node, new ConditionalChainSourceRanges());
        }
        ranges = (ConditionalChainSourceRanges)source_range_map_.Find(node);
        if (ranges == null) return;
        ranges.AddThenRanges(range);
    }

    private void AppendConditionalChainElseSourceRange(ConditionalChain node, SourceRange range)
    {
        if (source_range_map_ == null) return;
        if (source_range_map_.Find(node) is not ConditionalChainSourceRanges ranges) return;
        ranges.AddElseRange(range);
    }

    public override void RecordConditionalSourceRange(Expression node, SourceRange then_range, SourceRange else_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((Conditional)node, new ConditionalSourceRanges(then_range, else_range));
    }

    public override void RecordFunctionLiteralSourceRange(FunctionLiteral node)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert(node, new FunctionLiteralSourceRanges());
    }

    public override void RecordBinaryOperationSourceRange(Expression node, SourceRange right_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((BinaryOperation)node, new BinaryOperationSourceRanges(right_range));
    }

    public override void RecordJumpStatementSourceRange(Statement node, int continuation_position)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((JumpStatement)node, new JumpStatementSourceRanges(continuation_position));
    }

    public override void RecordIfStatementSourceRange(Statement node, SourceRange then_range, SourceRange else_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((IfStatement)node, new IfStatementSourceRanges(then_range, else_range));
    }

    public override void RecordIterationStatementSourceRange(Statement node, SourceRange body_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((IterationStatement)node, new IterationStatementSourceRanges(body_range));
    }

    // Used to record source ranges of expressions associated with optional chain:
    public override void RecordExpressionSourceRange(Expression node, SourceRange right_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert(node, new ExpressionSourceRanges(right_range));
    }

    public override void RecordSuspendSourceRange(Expression node, int continuation_position)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((Suspend)node, new SuspendSourceRanges(continuation_position));
    }

    public override void RecordSwitchStatementSourceRange(Statement node, int continuation_position)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert((SwitchStatement)node, new SwitchStatementSourceRanges(continuation_position));
    }

    public override void RecordThrowSourceRange(Statement node, int continuation_position)
    {
        if (source_range_map_ == null) return;
        ExpressionStatement expr_stmt = (ExpressionStatement)node;
        Throw throw_expr = (Throw)expr_stmt.expression();
        source_range_map_.Insert(throw_expr, new ThrowSourceRanges(continuation_position));
    }

    private void RecordTryCatchStatementSourceRange(TryCatchStatement node, SourceRange body_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert(node, new TryCatchStatementSourceRanges(body_range));
    }

    private void RecordTryFinallyStatementSourceRange(TryFinallyStatement node, SourceRange body_range)
    {
        if (source_range_map_ == null) return;
        source_range_map_.Insert(node, new TryFinallyStatementSourceRanges(body_range));
    }

    public override FunctionLiteral.EagerCompileHint GetEmbedderCompileHint(
        FunctionLiteral.EagerCompileHint current_compile_hint, int position)
    {
        if (current_compile_hint == FunctionLiteral.EagerCompileHint.kShouldLazyCompile)
        {
            Func<int, bool> callback = info_.compile_hint_callback();
            if (callback != null && callback(position))
            {
                return FunctionLiteral.EagerCompileHint.kShouldEagerCompile;
            }
        }
        return current_compile_hint;
    }

    public ParseInfo info() => info_;

    public List<byte> preparse_data_buffer() => preparse_data_buffer_;

    public override IRegExpSyntaxValidator regexp_syntax_validator() => info_.regexp_syntax_validator();

    public override bool IsTaggedTemplateCall(Expression expression) => ((Call)expression).is_tagged_template();

    public override void SetShouldEagerCompile(Expression function_literal)
        => ((FunctionLiteral)function_literal).SetShouldEagerCompile();

    public override void InitializeConditionalLoop(Statement loop, Expression cond, Statement body)
    {
        if (loop is DoWhileStatement do_while) do_while.Initialize(cond, body);
        else ((WhileStatement)loop).Initialize(cond, body);
    }

    public override void InitializeForLoop(Statement loop, Statement init, Expression cond, Statement next,
                                           Statement body)
        => ((ForStatement)loop).Initialize(init, cond, next, body);

    public override void InitializeForEachStatement(Statement loop, Expression each, Expression subject,
                                                    Statement body, Scope subject_scope)
        => ((ForEachStatement)loop).Initialize(each, subject, body, subject_scope);

    public override void AddCaseClause(Statement switch_statement, object clause)
        => ((SwitchStatement)switch_statement).cases().Add((CaseClause)clause);

    public override ParserFormalParameters NewFormalParameters(DeclarationScope scope) => new(scope);

    public override void ValidateDuplicate(ParserFormalParameters parameters) => parameters.ValidateDuplicate(this);

    public override void ValidateStrictMode(ParserFormalParameters parameters) => parameters.ValidateStrictMode(this);

    // Parser's private field members.

    private readonly ParseInfo info_;
    private readonly Scanner scanner_;
    private PreParser reusable_preparser_;
    private Mode mode_;

    private IReadOnlyList<string> maybe_wrapped_arguments_;

    private readonly SourceRangeMap source_range_map_;

    // For NextInternalNamespaceExportName().
    private int number_of_named_namespace_exports_;

    // Other information which will be stored in Parser and moved to Isolate after
    // parsing.
    private readonly int[] use_counts_ = new int[(int)UseCounterFeature.kUseCounterFeatureCount];
    private int total_preparse_skipped_;
    private bool allow_lazy_;
    private readonly ConsumedPreparseData consumed_preparse_data_;
    private readonly List<byte> preparse_data_buffer_ = new(128);

    // If not kNoSourcePosition, indicates that the first function literal
    // encountered is a dynamic function, see CreateDynamicFunction(). This field
    // indicates the correct position of the ')' that closes the parameter list.
    // After that ')' is encountered, this field is reset to kNoSourcePosition.
    private int parameters_end_pos_;
}
