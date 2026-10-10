// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// AstNodeFactory as the Parser's Types::Factory (src/parsing/parser.h):
// ParserBase sees the factory through IParserFactory, whose methods return
// the base node types; these forward to the typed AstNodeFactory methods.

#nullable disable

using V8Sharp.Common;
using V8Sharp.Parsing;

namespace V8Sharp.Ast;

public sealed partial class AstNodeFactory
    : IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>
{
    private IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>> AsParserFactory => this;

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.EmptyStatement() => EmptyStatement();

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewArrayLiteral(ScopedPtrList<Expression> values, int first_spread_index, int pos)
        => NewArrayLiteral(values, first_spread_index, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewAssignment(Token op, Expression target, Expression value, int pos)
        => NewAssignment(op, target, value, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewAsyncReturnStatement(Expression expression, int pos, int end_position)
        => NewAsyncReturnStatement(expression, pos, end_position);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewAwait(Expression expression, int pos) => NewAwait(expression, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewBinaryOperation(Token op, Expression left, Expression right, int pos)
        => NewBinaryOperation(op, left, right, pos);

    Block IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewBlock(bool ignore_completion_value, ScopedPtrList<Statement> statements)
        => NewBlock(ignore_completion_value, statements);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewBreakStatement(Statement target, int pos)
        => NewBreakStatement((BreakableStatement)target, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewCall(Expression expression, ScopedPtrList<Expression> arguments, int pos,
                                          bool has_spread, int eval_scope_info_index, bool optional_chain)
        => NewCall(expression, arguments, pos, has_spread, eval_scope_info_index, optional_chain);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewCallNew(Expression expression, ScopedPtrList<Expression> arguments, int pos,
                                             bool has_spread)
        => NewCallNew(expression, arguments, pos, has_spread);

    object IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewCaseClause(Expression label, ScopedPtrList<Statement> statements)
        => NewCaseClause(label, statements);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewCompareOperation(Token op, Expression left, Expression right, int pos)
        => NewCompareOperation(op, left, right, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewConditional(Expression condition, Expression then_expression,
                                                 Expression else_expression, int pos)
        => NewConditional(condition, then_expression, else_expression, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewConditionalChain(int initial_size, int pos)
        => NewConditionalChain(initial_size, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewContinueStatement(Statement target, int pos)
        => NewContinueStatement((IterationStatement)target, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewCountOperation(Token op, bool is_prefix, Expression expression, int pos)
        => NewCountOperation(op, is_prefix, expression, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewDebuggerStatement(int pos) => NewDebuggerStatement(pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewDoWhileStatement(int pos) => NewDoWhileStatement(pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewEmptyParentheses(int pos) => NewEmptyParentheses(pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewExpressionStatement(Expression expression, int pos)
        => NewExpressionStatement(expression, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewForEachStatement(ForEachStatement.VisitMode visit_mode, int pos)
        => NewForEachStatement(visit_mode, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewForOfStatement(int pos, IteratorType type) => NewForOfStatement(pos, type);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewForStatement(int pos) => NewForStatement(pos);

    FunctionLiteral IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewFunctionLiteral(AstRawString name, DeclarationScope scope,
                                                     ScopedPtrList<Statement> body, int expected_property_count,
                                                     int parameter_count, int function_length,
                                                     FunctionLiteral.ParameterFlag has_duplicate_parameters,
                                                     FunctionSyntaxKind function_syntax_kind,
                                                     FunctionLiteral.EagerCompileHint eager_compile_hint,
                                                     int position, bool has_braces, int function_literal_id,
                                                     ProducedPreparseData produced_preparse_data)
        => NewFunctionLiteral(name, scope, body, expected_property_count, parameter_count, function_length,
                              has_duplicate_parameters, function_syntax_kind, eager_compile_hint, position,
                              has_braces, function_literal_id, produced_preparse_data);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewIfStatement(Expression condition, Statement then_statement,
                                                 Statement else_statement, int pos)
        => NewIfStatement(condition, then_statement, else_statement, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewImportCallExpression(Expression specifier, ModuleImportPhase phase, int pos)
        => NewImportCallExpression(specifier, phase, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewImportCallExpression(Expression specifier, ModuleImportPhase phase,
                                                          Expression import_options, int pos)
        => NewImportCallExpression(specifier, phase, import_options, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewNumberLiteral(double number, int pos) => NewNumberLiteral(number, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewObjectLiteral(ScopedPtrList<ObjectLiteralProperty> properties,
                                                   int boilerplate_properties, int pos, bool has_rest_property,
                                                   Variable home_object)
        => NewObjectLiteral(properties, (uint)boilerplate_properties, pos, has_rest_property, home_object);

    ObjectLiteralProperty IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral,
        ObjectLiteralProperty, ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewObjectLiteralProperty(Expression key, Expression value,
                                                           ObjectLiteralProperty.Kind kind, bool is_computed_name)
        => NewObjectLiteralProperty(key, value, kind, is_computed_name);

    ObjectLiteralProperty IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral,
        ObjectLiteralProperty, ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewObjectLiteralProperty(Expression key, Expression value, bool is_computed_name)
        => NewObjectLiteralProperty(key, value, is_computed_name);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewOptionalChain(Expression expression) => NewOptionalChain(expression);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewProperty(Expression obj, Expression key, int pos, bool optional_chain)
        => NewProperty(obj, key, pos, optional_chain);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewRegExpLiteral(AstRawString pattern, int flags, int pos)
        => NewRegExpLiteral(pattern, flags, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewReturnStatement(Expression expression, int pos, int end_position)
        => NewReturnStatement(expression, pos, end_position);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewSpread(Expression expression, int pos, int expr_pos)
        => NewSpread(expression, pos, expr_pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewStringLiteral(AstRawString name, int pos) => NewStringLiteral(name, pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewSwitchStatement(Expression tag, int pos) => NewSwitchStatement(tag, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewTheHoleLiteral() => NewTheHoleLiteral();

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewUnaryOperation(Token op, Expression expression, int pos)
        => NewUnaryOperation(op, expression, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewUndefinedLiteral(int pos) => NewUndefinedLiteral(pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewWhileStatement(int pos) => NewWhileStatement(pos);

    Statement IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewWithStatement(Scope scope, Expression expression, Statement statement, int pos)
        => NewWithStatement(scope, expression, statement, pos);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewYield(Expression expression, int pos, Suspend.OnAbruptResume on_abrupt_resume)
        => NewYield(expression, pos, on_abrupt_resume);

    Expression IParserFactory<Expression, AstRawString, Statement, Block, FunctionLiteral, ObjectLiteralProperty,
        ClassLiteralProperty, ScopedPtrList<Expression>, ScopedPtrList<ObjectLiteralProperty>,
        ScopedPtrList<Statement>>.NewYieldStar(Expression expression, int pos) => NewYieldStar(expression, pos);
}
