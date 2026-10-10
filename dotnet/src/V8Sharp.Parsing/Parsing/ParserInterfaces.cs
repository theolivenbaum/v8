// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// The shapes ParserBase expects of ParserTypes<Impl>. In V8 these are
// implicit template requirements satisfied by AstNodeFactory /
// PreParserFactory, Block* / PreParserBlock and so on; C# generics need them
// spelled out.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing;

// Types::Block operations used by ParserBase.
public interface IParserBlock<TStatement, TStatementList>
{
    void set_scope(Scope scope);
    Scope scope();
    void InitializeStatements(TStatementList statements);

    // block->statements()->Add(statement, zone()).
    void AddStatement(TStatement statement);
}

// Types::FunctionLiteral operations used by ParserBase.
public interface IParserFunctionLiteral
{
    void set_suspend_count(int suspend_count);
    void set_function_token_position(int pos);
}

// Types::Factory: the AstNodeFactory / PreParserFactory methods ParserBase
// calls.
public interface IParserFactory<TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
                                TObjectLiteralProperty, TClassLiteralProperty, TExpressionList,
                                TObjectPropertyList, TStatementList>
{
    AstNodeFactory ast_node_factory();

    TStatement EmptyStatement();
    TExpression NewArrayLiteral(TExpressionList values, int first_spread_index, int pos);
    TExpression NewAssignment(Token op, TExpression target, TExpression value, int pos);
    TStatement NewAsyncReturnStatement(TExpression expression, int pos, int end_position);
    TExpression NewAwait(TExpression expression, int pos);
    TExpression NewBinaryOperation(Token op, TExpression left, TExpression right, int pos);
    TBlock NewBlock(int capacity, bool ignore_completion_value);
    TBlock NewBlock(bool ignore_completion_value, bool is_breakable);
    TBlock NewBlock(bool ignore_completion_value, TStatementList statements);
    TStatement NewBreakStatement(TStatement target, int pos);
    TExpression NewCall(TExpression expression, TExpressionList arguments, int pos, bool has_spread,
                        int eval_scope_info_index = 0, bool optional_chain = false);
    TExpression NewCallNew(TExpression expression, TExpressionList arguments, int pos, bool has_spread);

    // CaseClause is not a Statement in V8 (the PreParser returns a
    // PreParserStatement); ParserBase only passes it on.
    object NewCaseClause(TExpression label, TStatementList statements);
    TClassLiteralProperty NewClassLiteralProperty(TExpression key, TExpression value,
                                                  ClassLiteralProperty.Kind kind, bool is_static,
                                                  bool is_computed_name, bool is_private);
    TExpression NewCompareOperation(Token op, TExpression left, TExpression right, int pos);
    TExpression NewConditional(TExpression condition, TExpression then_expression, TExpression else_expression,
                               int pos);
    TExpression NewConditionalChain(int initial_size, int pos);
    TStatement NewContinueStatement(TStatement target, int pos);
    TExpression NewCountOperation(Token op, bool is_prefix, TExpression expression, int pos);
    TStatement NewDebuggerStatement(int pos);
    TStatement NewDoWhileStatement(int pos);
    TExpression NewEmptyParentheses(int pos);
    TStatement NewExpressionStatement(TExpression expression, int pos);
    TStatement NewForEachStatement(ForEachStatement.VisitMode visit_mode, int pos);
    TStatement NewForOfStatement(int pos, IteratorType type);
    TStatement NewForStatement(int pos);
    TFunctionLiteral NewFunctionLiteral(TIdentifier name, DeclarationScope scope, TStatementList body,
                                        int expected_property_count, int parameter_count, int function_length,
                                        FunctionLiteral.ParameterFlag has_duplicate_parameters,
                                        FunctionSyntaxKind function_syntax_kind,
                                        FunctionLiteral.EagerCompileHint eager_compile_hint, int position,
                                        bool has_braces, int function_literal_id,
                                        ProducedPreparseData produced_preparse_data = null);
    TStatement NewIfStatement(TExpression condition, TStatement then_statement, TStatement else_statement, int pos);
    TExpression NewImportCallExpression(TExpression specifier, ModuleImportPhase phase, int pos);
    TExpression NewImportCallExpression(TExpression specifier, ModuleImportPhase phase, TExpression import_options,
                                        int pos);
    TExpression NewNumberLiteral(double number, int pos);
    TExpression NewObjectLiteral(TObjectPropertyList properties, int boilerplate_properties, int pos,
                                 bool has_rest_property, Variable home_object);
    TObjectLiteralProperty NewObjectLiteralProperty(TExpression key, TExpression value,
                                                    ObjectLiteralProperty.Kind kind, bool is_computed_name);
    TObjectLiteralProperty NewObjectLiteralProperty(TExpression key, TExpression value, bool is_computed_name);
    TExpression NewOptionalChain(TExpression expression);
    TExpression NewProperty(TExpression obj, TExpression key, int pos, bool optional_chain = false);
    TExpression NewRegExpLiteral(AstRawString pattern, int flags, int pos);
    TStatement NewReturnStatement(TExpression expression, int pos, int end_position);
    TExpression NewSpread(TExpression expression, int pos, int expr_pos);
    TExpression NewStringLiteral(TIdentifier name, int pos);
    TStatement NewSwitchStatement(TExpression tag, int pos);
    TExpression NewTheHoleLiteral();
    TExpression NewUnaryOperation(Token op, TExpression expression, int pos);
    TExpression NewUndefinedLiteral(int pos);
    TStatement NewWhileStatement(int pos);
    TStatement NewWithStatement(Scope scope, TExpression expression, TStatement statement, int pos);
    TExpression NewYield(TExpression expression, int pos, Suspend.OnAbruptResume on_abrupt_resume);
    TExpression NewYieldStar(TExpression expression, int pos);
}

// RegExp::VerifySyntax, supplied by the engine (V8Sharp.Parsing does not
// reference the regexp compiler). flags are the RegExpFlags bits.
public interface IRegExpSyntaxValidator
{
    bool VerifySyntax(string pattern, int flags, out string error_message, out bool is_stack_overflow);
}
