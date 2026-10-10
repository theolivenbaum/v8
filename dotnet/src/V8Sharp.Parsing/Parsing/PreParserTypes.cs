// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/preparser.h: the PreParser value types and factory,
// and src/parsing/preparser-logger.h.
//
// Whereas the Parser generates AST during the recursive descent,
// the PreParser doesn't create a tree. Instead, it passes around minimal
// data objects (PreParserExpression, PreParserIdentifier etc.) which contain
// just enough data for the upper layer functions. PreParserFactory is
// responsible for creating these dummy objects. It provides a similar kind of
// interface as AstNodeFactory, so ParserBase doesn't need to care which one is
// used.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public struct PreParserIdentifier
{
    internal enum Type : byte
    {
        kNullIdentifier,
        kUnknownIdentifier,
        kEvalIdentifier,
        kArgumentsIdentifier,
        kConstructorIdentifier,
        kAsyncIdentifier,
        kPrivateNameIdentifier,
    }

    internal AstRawString string_;
    internal Type type_;

    internal PreParserIdentifier(Type type)
    {
        string_ = null;
        type_ = type;
    }

    public static PreParserIdentifier Default() => new(Type.kUnknownIdentifier);
    public static PreParserIdentifier Null() => new(Type.kNullIdentifier);
    public static PreParserIdentifier Eval() => new(Type.kEvalIdentifier);
    public static PreParserIdentifier Arguments() => new(Type.kArgumentsIdentifier);
    public static PreParserIdentifier Constructor() => new(Type.kConstructorIdentifier);
    public static PreParserIdentifier Async() => new(Type.kAsyncIdentifier);
    public static PreParserIdentifier PrivateName() => new(Type.kPrivateNameIdentifier);

    public readonly bool IsNull() => type_ == Type.kNullIdentifier;
    public readonly bool IsEval() => type_ == Type.kEvalIdentifier;
    public readonly bool IsAsync() => type_ == Type.kAsyncIdentifier;
    public readonly bool IsArguments() => type_ == Type.kArgumentsIdentifier;
    public readonly bool IsEvalOrArguments() => type_ is >= Type.kEvalIdentifier and <= Type.kArgumentsIdentifier;
    public readonly bool IsConstructor() => type_ == Type.kConstructorIdentifier;
    public readonly bool IsPrivateName() => type_ == Type.kPrivateNameIdentifier;

    public readonly AstRawString raw_name() => string_;
}

public struct PreParserExpression : IParserExpression, IParserFunctionLiteral
{
    private enum Type : uint
    {
        kNull,
        kFailure,
        kExpression,
        kIdentifierExpression,
        kStringLiteralExpression,
        kArrayOrObjectLiteralExpression,
    }

    private enum ExpressionType : uint
    {
        kThisExpression,
        kThisPropertyExpression,
        kThisPrivateReferenceExpression,
        kPropertyExpression,
        kPrivateReferenceExpression,
        kCallExpression,
        kCallEvalExpression,
        kSuperCallReference,
        kAssignment,
        kImportCallExpression,
    }

    // The first three bits are for the Type.
    private const int kTypeShift = 0;
    private const uint kTypeMask = 0x7;

    // The high order bit applies only to nodes which would inherit from the
    // Expression ASTNode --- This is by necessity, due to the fact that
    // Expression nodes may be represented as multiple Types, not exclusively
    // through kExpression.
    private const int kIsParenthesizedShift = 3;

    // The rest of the bits are interpreted depending on the value
    // of the Type field, so they can share the storage.
    private const int kExpressionTypeShift = 4;
    private const uint kExpressionTypeMask = 0xF;
    private const int kIdentifierTypeShift = 4;
    private const uint kIdentifierTypeMask = 0xFF;

    private uint code_;

    private PreParserExpression(uint expression_code) => code_ = expression_code;

    private static uint EncodeType(Type type) => (uint)type << kTypeShift;
    private static uint EncodeExpressionType(ExpressionType type) => (uint)type << kExpressionTypeShift;

    private readonly Type TypeField => (Type)((code_ >> kTypeShift) & kTypeMask);
    private readonly ExpressionType ExpressionTypeField =>
        (ExpressionType)((code_ >> kExpressionTypeShift) & kExpressionTypeMask);

    public static PreParserExpression Null() => new(EncodeType(Type.kNull));
    public static PreParserExpression Failure() => new(EncodeType(Type.kFailure));
    public static PreParserExpression Default() => new(EncodeType(Type.kExpression));

    public static PreParserExpression FromIdentifier(PreParserIdentifier id)
        => new(EncodeType(Type.kIdentifierExpression) | ((uint)id.type_ << kIdentifierTypeShift));

    public static PreParserExpression Assignment()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kAssignment));

    public static PreParserExpression ObjectLiteral() => new(EncodeType(Type.kArrayOrObjectLiteralExpression));
    public static PreParserExpression ArrayLiteral() => new(EncodeType(Type.kArrayOrObjectLiteralExpression));
    public static PreParserExpression StringLiteral() => new(EncodeType(Type.kStringLiteralExpression));

    public static PreParserExpression This()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kThisExpression));

    public static PreParserExpression ThisPrivateReference()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kThisPrivateReferenceExpression));

    public static PreParserExpression ThisProperty()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kThisPropertyExpression));

    public static PreParserExpression Property()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kPropertyExpression));

    public static PreParserExpression PrivateReference()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kPrivateReferenceExpression));

    public static PreParserExpression Call()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kCallExpression));

    public static PreParserExpression CallEval()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kCallEvalExpression));

    public static PreParserExpression ImportCall()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kImportCallExpression));

    public static PreParserExpression SuperCallReference()
        => new(EncodeType(Type.kExpression) | EncodeExpressionType(ExpressionType.kSuperCallReference));

    public readonly bool IsNull() => TypeField == Type.kNull;
    public readonly bool IsFailureExpression() => TypeField == Type.kFailure;
    public readonly bool IsIdentifier() => TypeField == Type.kIdentifierExpression;

    public readonly PreParserIdentifier AsIdentifier()
        => new((PreParserIdentifier.Type)((code_ >> kIdentifierTypeShift) & kIdentifierTypeMask));

    public readonly bool IsAssignment()
        => TypeField == Type.kExpression && ExpressionTypeField == ExpressionType.kAssignment;

    public readonly bool IsPattern() => TypeField == Type.kArrayOrObjectLiteralExpression;

    public readonly bool IsStringLiteral() => TypeField == Type.kStringLiteralExpression;

    public readonly bool IsThis() => TypeField == Type.kExpression && ExpressionTypeField == ExpressionType.kThisExpression;

    public readonly bool IsThisProperty()
        => TypeField == Type.kExpression &&
           (ExpressionTypeField == ExpressionType.kThisPropertyExpression ||
            ExpressionTypeField == ExpressionType.kThisPrivateReferenceExpression);

    public readonly bool IsProperty()
        => TypeField == Type.kExpression &&
           (ExpressionTypeField == ExpressionType.kPropertyExpression ||
            ExpressionTypeField == ExpressionType.kThisPropertyExpression ||
            ExpressionTypeField == ExpressionType.kPrivateReferenceExpression ||
            ExpressionTypeField == ExpressionType.kThisPrivateReferenceExpression);

    public readonly bool IsPrivateReference()
        => TypeField == Type.kExpression &&
           (ExpressionTypeField == ExpressionType.kPrivateReferenceExpression ||
            ExpressionTypeField == ExpressionType.kThisPrivateReferenceExpression);

    public readonly bool IsCall()
        => TypeField == Type.kExpression &&
           (ExpressionTypeField == ExpressionType.kCallExpression ||
            ExpressionTypeField == ExpressionType.kCallEvalExpression);

    public readonly bool IsSuperCallReference()
        => TypeField == Type.kExpression && ExpressionTypeField == ExpressionType.kSuperCallReference;

    public readonly bool IsImportCallExpression()
        => TypeField == Type.kExpression && ExpressionTypeField == ExpressionType.kImportCallExpression;

    // At the moment PreParser doesn't track these expression types.
    public readonly bool IsFunctionLiteral() => false;
    public readonly bool IsCallNew() => false;
    public readonly bool is_tagged_template() => false;

    public readonly bool is_parenthesized() => ((code_ >> kIsParenthesizedShift) & 1) != 0;

    public void mark_parenthesized() => code_ |= 1u << kIsParenthesizedShift;

    public void clear_parenthesized() => code_ &= ~(1u << kIsParenthesizedShift);

    // More dummy implementations of things PreParser doesn't need to track:
    public readonly void SetShouldEagerCompile() { }

    public readonly int position() => kNoSourcePosition;
    public readonly void set_function_token_position(int position) { }
    public readonly void set_suspend_count(int suspend_count) { }
}

// PreParserStatement, with PreParserBlock folded in: V8's PreParserBlock is
// a PreParserStatement plus a place to store the scope, and ParserBase uses
// the two interchangeably (Block converts to Statement).
public struct PreParserStatement : IParserStatement, IParserBlock<PreParserStatement, PreParserScopedStatementList>
{
    internal enum Type : byte
    {
        kNullStatement,
        kUnknownStatement,
        kJumpStatement,
        kIterationStatement,
        kStringLiteralExpressionStatement,
    }

    private readonly Type code_;
    private Scope scope_;

    private PreParserStatement(Type code)
    {
        code_ = code;
        scope_ = null;
    }

    public static PreParserStatement Default() => new(Type.kUnknownStatement);
    public static PreParserStatement Iteration() => new(Type.kIterationStatement);
    public static PreParserStatement Null() => new(Type.kNullStatement);
    public static PreParserStatement Jump() => new(Type.kJumpStatement);

    // Creates expression statement from expression.
    // Preserves being an unparenthesized string literal, possibly
    // "use strict".
    public static PreParserStatement ExpressionStatement(PreParserExpression expression)
    {
        if (expression.IsStringLiteral())
        {
            return new PreParserStatement(Type.kStringLiteralExpressionStatement);
        }
        return Default();
    }

    public readonly bool IsStringLiteral() => code_ == Type.kStringLiteralExpressionStatement;
    public readonly bool IsJumpStatement() => code_ == Type.kJumpStatement;
    public readonly bool IsNull() => code_ == Type.kNullStatement;
    public readonly bool IsIterationStatement() => code_ == Type.kIterationStatement;
    public readonly bool IsEmptyStatement() => false;

    // PreParserBlock.
    public void set_scope(Scope scope) => scope_ = scope;
    public readonly Scope scope() => scope_;
    public readonly void InitializeStatements(PreParserScopedStatementList statements) { }
    public readonly void AddStatement(PreParserStatement statement) { }
}

public sealed class PreParserScopedStatementList : IScopedPtrList<PreParserScopedStatementList, PreParserStatement>
{
    // The list holds no state, so one instance serves every use.
    private static readonly PreParserScopedStatementList s_instance = new();

    public static PreParserScopedStatementList New(List<object> buffer) => s_instance;

    public void Dispose() { }
    public void Rewind() { }
    public void MergeInto(PreParserScopedStatementList other) { }
    public void Add(PreParserStatement element) { }
    public int length() => 0;
}

// The pre-parser doesn't need to build lists of expressions, identifiers, or
// the like. If the PreParser is used in variable tracking mode, it needs to
// build lists of variables though.
//
// V8's PreParserExpressionList is a value on the C++ stack. Here it is a class
// (ParserBase passes lists by reference), and the lists are recycled through
// a per-thread free list, since every list lives exactly as long as the
// `using` that creates it: preparsing a call allocates nothing.
public sealed class PreParserExpressionList
    : IScopedPtrList<PreParserExpressionList, PreParserExpression>
{
    private int length_;
    private PreParserExpressionList next_free_;

    [ThreadStatic] private static PreParserExpressionList t_free_;

    public static PreParserExpressionList New(List<object> buffer)
    {
        PreParserExpressionList list = t_free_;
        if (list == null) return new PreParserExpressionList();
        t_free_ = list.next_free_;
        list.next_free_ = null;
        list.length_ = 0;
        return list;
    }

    public int length() => length_;

    public void Add(PreParserExpression expression) => ++length_;

    public void Dispose()
    {
        next_free_ = t_free_;
        t_free_ = this;
    }
    public void Rewind() => length_ = 0;
    public void MergeInto(PreParserExpressionList parent) { }
}

public readonly struct PreParserPropertyList;

public sealed class PreParserFactory(AstValueFactory ast_value_factory)
    : IParserFactory<PreParserExpression, PreParserIdentifier, PreParserStatement, PreParserStatement,
        PreParserExpression, PreParserExpression, PreParserExpression, PreParserExpressionList,
        PreParserExpressionList, PreParserScopedStatementList>
{
    // For creating VariableProxy objects to track unresolved variables.
    private readonly AstNodeFactory ast_node_factory_ = new(ast_value_factory);

    public AstNodeFactory ast_node_factory() => ast_node_factory_;

    public PreParserExpression NewStringLiteral(PreParserIdentifier identifier, int pos) => PreParserExpression.Default();
    public PreParserExpression NewNumberLiteral(double number, int pos) => PreParserExpression.Default();
    public PreParserExpression NewUndefinedLiteral(int pos) => PreParserExpression.Default();
    public PreParserExpression NewTheHoleLiteral() => PreParserExpression.Default();
    public PreParserExpression NewRegExpLiteral(AstRawString js_pattern, int js_flags, int pos) => PreParserExpression.Default();

    public PreParserExpression NewArrayLiteral(PreParserExpressionList values, int first_spread_index, int pos)
        => PreParserExpression.ArrayLiteral();

    public PreParserExpression NewClassLiteralProperty(PreParserExpression key, PreParserExpression value,
                                                       ClassLiteralProperty.Kind kind, bool is_static,
                                                       bool is_computed_name, bool is_private)
        => PreParserExpression.Default();

    public PreParserExpression NewObjectLiteralProperty(PreParserExpression key, PreParserExpression value,
                                                        ObjectLiteralProperty.Kind kind, bool is_computed_name)
        => PreParserExpression.Default();

    public PreParserExpression NewObjectLiteralProperty(PreParserExpression key, PreParserExpression value,
                                                        bool is_computed_name)
        => PreParserExpression.Default();

    public PreParserExpression NewObjectLiteral(PreParserExpressionList properties, int boilerplate_properties,
                                                int pos, bool has_rest_property, Variable home_object = null)
        => PreParserExpression.ObjectLiteral();

    public PreParserExpression NewOptionalChain(PreParserExpression expr)
    {
        // Needed to track `delete a?.#b` early errors
        if (expr.IsPrivateReference())
        {
            return PreParserExpression.PrivateReference();
        }
        return PreParserExpression.Default();
    }

    public PreParserExpression NewProperty(PreParserExpression obj, PreParserExpression key, int pos,
                                           bool optional_chain = false)
    {
        if (key.IsIdentifier() && key.AsIdentifier().IsPrivateName())
        {
            if (obj.IsThis())
            {
                return PreParserExpression.ThisPrivateReference();
            }
            return PreParserExpression.PrivateReference();
        }

        if (obj.IsThis())
        {
            return PreParserExpression.ThisProperty();
        }
        return PreParserExpression.Property();
    }

    public PreParserExpression NewUnaryOperation(Token op, PreParserExpression expression, int pos)
        => PreParserExpression.Default();

    public PreParserExpression NewBinaryOperation(Token op, PreParserExpression left, PreParserExpression right,
                                                  int pos)
        => PreParserExpression.Default();

    public PreParserExpression NewCompareOperation(Token op, PreParserExpression left, PreParserExpression right,
                                                   int pos)
        => PreParserExpression.Default();

    public PreParserExpression NewAssignment(Token op, PreParserExpression left, PreParserExpression right, int pos)
    {
        // Identifiers need to be tracked since this might be a parameter with a
        // default value inside an arrow function parameter list.
        return PreParserExpression.Assignment();
    }

    public PreParserExpression NewYield(PreParserExpression expression, int pos,
                                        Suspend.OnAbruptResume on_abrupt_resume)
        => PreParserExpression.Default();

    public PreParserExpression NewAwait(PreParserExpression expression, int pos) => PreParserExpression.Default();

    public PreParserExpression NewYieldStar(PreParserExpression iterable, int pos) => PreParserExpression.Default();

    public PreParserExpression NewConditionalChain(int initial_size, int pos) => PreParserExpression.Default();

    public PreParserExpression NewConditional(PreParserExpression condition, PreParserExpression then_expression,
                                              PreParserExpression else_expression, int pos)
        => PreParserExpression.Default();

    public PreParserExpression NewCountOperation(Token op, bool is_prefix, PreParserExpression expression, int pos)
        => PreParserExpression.Default();

    public PreParserExpression NewCall(PreParserExpression expression, PreParserExpressionList arguments, int pos,
                                       bool has_spread, int eval_scope_info_index = 0, bool optional_chain = false)
    {
        if (eval_scope_info_index > 0)
        {
            return PreParserExpression.CallEval();
        }
        return PreParserExpression.Call();
    }

    public PreParserExpression NewCallNew(PreParserExpression expression, PreParserExpressionList arguments,
                                          int pos, bool has_spread)
        => PreParserExpression.Default();

    public PreParserStatement NewReturnStatement(PreParserExpression expression, int pos,
                                                 int continuation_pos = kNoSourcePosition)
        => PreParserStatement.Jump();

    public PreParserStatement NewAsyncReturnStatement(PreParserExpression expression, int pos,
                                                      int continuation_pos = kNoSourcePosition)
        => PreParserStatement.Jump();

    public PreParserExpression NewFunctionLiteral(PreParserIdentifier name, DeclarationScope scope,
                                                  PreParserScopedStatementList body, int expected_property_count,
                                                  int parameter_count, int function_length,
                                                  FunctionLiteral.ParameterFlag has_duplicate_parameters,
                                                  FunctionSyntaxKind function_syntax_kind,
                                                  FunctionLiteral.EagerCompileHint eager_compile_hint, int position,
                                                  bool has_braces, int function_literal_id,
                                                  ProducedPreparseData produced_preparse_data = null)
        => PreParserExpression.Default();

    public PreParserExpression NewSpread(PreParserExpression expression, int pos, int expr_pos)
        => PreParserExpression.Default();

    public PreParserExpression NewEmptyParentheses(int pos)
    {
        PreParserExpression result = PreParserExpression.Default();
        result.mark_parenthesized();
        return result;
    }

    public PreParserStatement EmptyStatement() => PreParserStatement.Default();

    public PreParserStatement NewBlock(int capacity, bool ignore_completion_value) => PreParserStatement.Default();

    public PreParserStatement NewBlock(bool ignore_completion_value, bool is_breakable) => PreParserStatement.Default();

    public PreParserStatement NewBlock(bool ignore_completion_value, PreParserScopedStatementList list)
        => PreParserStatement.Default();

    public PreParserStatement NewDebuggerStatement(int pos) => PreParserStatement.Default();

    public PreParserStatement NewExpressionStatement(PreParserExpression expr, int pos)
        => PreParserStatement.ExpressionStatement(expr);

    public PreParserStatement NewIfStatement(PreParserExpression condition, PreParserStatement then_statement,
                                             PreParserStatement else_statement, int pos)
    {
        // This must return a jump statement iff both clauses are jump statements.
        return else_statement.IsJumpStatement() ? then_statement : else_statement;
    }

    public PreParserStatement NewBreakStatement(PreParserStatement target, int pos) => PreParserStatement.Jump();

    public PreParserStatement NewContinueStatement(PreParserStatement target, int pos) => PreParserStatement.Jump();

    public PreParserStatement NewWithStatement(Scope scope, PreParserExpression expression,
                                               PreParserStatement statement, int pos)
        => PreParserStatement.Default();

    public PreParserStatement NewDoWhileStatement(int pos) => PreParserStatement.Iteration();

    public PreParserStatement NewWhileStatement(int pos) => PreParserStatement.Iteration();

    public PreParserStatement NewSwitchStatement(PreParserExpression tag, int pos) => PreParserStatement.Default();

    public object NewCaseClause(PreParserExpression label, PreParserScopedStatementList statements) => null;

    public PreParserStatement NewForStatement(int pos) => PreParserStatement.Iteration();

    public PreParserStatement NewForEachStatement(ForEachStatement.VisitMode visit_mode, int pos)
        => PreParserStatement.Iteration();

    public PreParserStatement NewForOfStatement(int pos, IteratorType type) => PreParserStatement.Iteration();

    public PreParserExpression NewImportCallExpression(PreParserExpression args, ModuleImportPhase phase, int pos)
        => PreParserExpression.ImportCall();

    public PreParserExpression NewImportCallExpression(PreParserExpression specifier, ModuleImportPhase phase,
                                                       PreParserExpression import_options, int pos)
        => PreParserExpression.ImportCall();
}

public sealed class PreParserFormalParameters(DeclarationScope scope) : FormalParametersBase(scope)
{
    private bool has_duplicate_;
    private bool strict_parameter_error_;

    public void set_has_duplicate() => has_duplicate_ = true;
    public bool has_duplicate() => has_duplicate_;

    public void ValidateDuplicate(PreParser preparser)
    {
        if (has_duplicate_) preparser.ReportUnidentifiableError();
    }

    public override void set_strict_parameter_error(Scanner.Location loc, MessageTemplate message)
        => strict_parameter_error_ = loc.IsValid();

    public void ValidateStrictMode(PreParser preparser)
    {
        if (strict_parameter_error_) preparser.ReportUnidentifiableError();
    }
}

public sealed class PreParserFuncNameInferrer : IFuncNameInferrer
{
    public static readonly PreParserFuncNameInferrer Instance = new();

    public int EnterState() => 0;
    public void LeaveState(int top) { }
    public void RemoveAsyncKeywordFromEnd() { }
    public void Infer() { }
    public void RemoveLastFunction() { }
}

// Port of src/parsing/preparser-logger.h.
public sealed class PreParserLogger
{
    private int end_ = -1;
    // For function entries.
    private int num_parameters_ = -1;
    private int function_length_ = -1;
    private int num_inner_infos_ = -1;

    public void LogFunction(int end, int num_parameters, int function_length, int num_inner_infos)
    {
        end_ = end;
        num_parameters_ = num_parameters;
        function_length_ = function_length;
        num_inner_infos_ = num_inner_infos;
    }

    public int end() => end_;
    public int num_parameters() => num_parameters_;
    public int function_length() => function_length_;
    public int num_inner_infos() => num_inner_infos_;
}
