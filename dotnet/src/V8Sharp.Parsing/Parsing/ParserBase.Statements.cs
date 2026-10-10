// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser-base.h: statements.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    protected TStatement ParseStatementListItem()
    {
        // ECMA 262 6th Edition
        // StatementListItem[Yield, Return] :
        //   Statement[?Yield, ?Return]
        //   Declaration[?Yield]
        //
        // Declaration[Yield] :
        //   HoistableDeclaration[?Yield]
        //   ClassDeclaration[?Yield]
        //   LexicalDeclaration[In, ?Yield]
        //
        // HoistableDeclaration[Yield, Default] :
        //   FunctionDeclaration[?Yield, ?Default]
        //   GeneratorDeclaration[?Yield, ?Default]
        //
        // LexicalDeclaration[In, Yield, Await] :
        //   LetOrConst BindingList[?In, ?Yield, ?Await, +Pattern] ;
        //   UsingDeclaration[?In, ?Yield, ?Await, ~Pattern];
        //   [+Await] AwaitUsingDeclaration[?In, ?Yield];

        switch (peek())
        {
            case Token.Function:
                return ParseHoistableDeclaration(null, false);
            case Token.Class:
                Consume(Token.Class);
                return ParseClassDeclaration(null, false);
            case Token.Var:
            case Token.Const:
                return ParseVariableStatement(VariableDeclarationContext.kStatementListItem, null);
            case Token.Let:
                if (IsNextLetKeyword())
                {
                    return ParseVariableStatement(VariableDeclarationContext.kStatementListItem, null);
                }
                break;
            case Token.Using:
                if (!is_using_allowed()) break;
                if (!scanner().HasLineTerminatorAfterNext() && Token.IsAnyIdentifier(PeekAhead()))
                {
                    return ParseVariableStatement(VariableDeclarationContext.kStatementListItem, null);
                }
                break;
            case Token.Await:
                if (!is_await_allowed()) break;
                if (!is_using_allowed()) break;
                if (!scanner().HasLineTerminatorAfterNext() && PeekAhead() == Token.Using &&
                    !scanner().HasLineTerminatorAfterNextNext() && Token.IsAnyIdentifier(PeekAheadAhead()))
                {
                    return ParseVariableStatement(VariableDeclarationContext.kStatementListItem, null);
                }
                break;
            case Token.Async:
                if (PeekAhead() == Token.Function && !scanner().HasLineTerminatorAfterNext())
                {
                    Consume(Token.Async);
                    return ParseAsyncFunctionDeclaration(null, false);
                }
                break;
            default:
                break;
        }
        return ParseStatement(null, null, AllowLabelledFunctionStatement.kAllowLabelledFunctionStatement);
    }

    protected TStatement ParseStatement(List<AstRawString> labels, List<AstRawString> own_labels)
        => ParseStatement(labels, own_labels, AllowLabelledFunctionStatement.kDisallowLabelledFunctionStatement);

    protected TStatement ParseStatement(List<AstRawString> labels, List<AstRawString> own_labels,
                                        AllowLabelledFunctionStatement allow_function)
    {
        // Statement ::
        //   Block
        //   VariableStatement
        //   EmptyStatement
        //   ExpressionStatement
        //   IfStatement
        //   IterationStatement
        //   ContinueStatement
        //   BreakStatement
        //   ReturnStatement
        //   WithStatement
        //   LabelledStatement
        //   SwitchStatement
        //   ThrowStatement
        //   TryStatement
        //   DebuggerStatement

        // {own_labels} is always a subset of {labels}.

        // Note: Since labels can only be used by 'break' and 'continue'
        // statements, which themselves are only valid within blocks,
        // iterations or 'switch' statements (i.e., BreakableStatements),
        // labels can be simply ignored in all other cases; except for
        // trivial labeled break statements 'label: break label' which is
        // parsed into an empty statement.
        switch (peek())
        {
            case Token.LeftBrace:
                return ParseBlock(labels);
            case Token.Semicolon:
                Next();
                return factory().EmptyStatement();
            case Token.If:
                return ParseIfStatement(labels);
            case Token.Do:
                return ParseDoWhileStatement(labels, own_labels);
            case Token.While:
                return ParseWhileStatement(labels, own_labels);
            case Token.For:
                if (is_await_allowed() && PeekAhead() == Token.Await)
                {
                    return ParseForAwaitStatement(labels, own_labels);
                }
                return ParseForStatement(labels, own_labels);
            case Token.Continue:
                return ParseContinueStatement();
            case Token.Break:
                return ParseBreakStatement(labels);
            case Token.Return:
                return ParseReturnStatement();
            case Token.Throw:
                return ParseThrowStatement();
            case Token.Try:
            {
                // It is somewhat complicated to have labels on try-statements.
                // When breaking out of a try-finally statement, one must take
                // great care not to treat it as a fall-through. It is much easier
                // just to wrap the entire try-statement in a statement block and
                // put the labels there.
                if (labels == null) return ParseTryStatement();
                using TStatementList statements = TStatementList.New(pointer_buffer());
                TBlock result = factory().NewBlock(false, true);
                using Target target = Target.New(this, result, labels, null, Target.TargetType.TARGET_FOR_NAMED_ONLY);
                TStatement statement = ParseTryStatement();
                statements.Add(statement);
                result.InitializeStatements(statements);
                return result;
            }
            case Token.With:
                return ParseWithStatement(labels);
            case Token.Switch:
                return ParseSwitchStatement(labels);
            case Token.Function:
                // FunctionDeclaration only allowed as a StatementListItem, not in
                // an arbitrary Statement position. Exceptions such as
                // https://tc39.es/ecma262/#sec-functiondeclarations-in-ifstatement-statement-clauses
                // are handled by calling ParseScopedStatement rather than
                // ParseStatement directly.
                impl().ReportMessageAt(scanner().peek_location(),
                                       is_strict(language_mode())
                                           ? MessageTemplate.StrictFunction
                                           : MessageTemplate.SloppyFunction);
                return impl().NullStatement();
            case Token.Debugger:
                return ParseDebuggerStatement();
            case Token.Var:
                return ParseVariableStatement(VariableDeclarationContext.kStatement, null);
            case Token.Async:
                if (!impl().HasCheckedSyntax() && !scanner().HasLineTerminatorAfterNext() &&
                    PeekAhead() == Token.Function)
                {
                    impl().ReportMessageAt(scanner().peek_location(),
                                           MessageTemplate.AsyncFunctionInSingleStatementContext);
                    return impl().NullStatement();
                }
                goto default;
            default:
                return ParseExpressionOrLabelledStatement(labels, own_labels, allow_function);
        }
    }

    protected TBlock ParseBlock(List<AstRawString> labels, Scope block_scope)
    {
        // Block ::
        //   '{' StatementList '}'

        // Parse the statements and collect escaping labels.
        TBlock body = factory().NewBlock(false, labels != null);
        using TStatementList statements = TStatementList.New(pointer_buffer());

        CheckStackOverflow();

        using (BlockState block_state = new(this, block_scope))
        {
            scope().set_start_position(peek_position());
            using Target target = Target.New(this, body, labels, null, Target.TargetType.TARGET_FOR_NAMED_ONLY);

            Expect(Token.LeftBrace);

            while (peek() != Token.RightBrace)
            {
                TStatement stat = ParseStatementListItem();
                if (impl().IsNull(stat)) return body;
                if (stat.IsEmptyStatement()) continue;
                statements.Add(stat);
            }

            Expect(Token.RightBrace);

            int end_pos = end_position();
            scope().set_end_position(end_pos);

            impl().RecordBlockSourceRange(body, end_pos);
            body.set_scope(scope().FinalizeBlockScope());
        }

        body.InitializeStatements(statements);
        return body;
    }

    protected TBlock ParseBlock(List<AstRawString> labels) => ParseBlock(labels, NewScope(ScopeType.BLOCK_SCOPE));

    // Parse a SubStatement in strict mode, or with an extra block scope in
    // sloppy mode to handle
    // https://tc39.es/ecma262/#sec-functiondeclarations-in-ifstatement-statement-clauses
    protected TStatement ParseScopedStatement(List<AstRawString> labels)
    {
        if (is_strict(language_mode()) || peek() != Token.Function)
        {
            return ParseStatement(labels, null);
        }
        else
        {
            // Make a block around the statement for a lexical binding
            // is introduced by a FunctionDeclaration.
            using BlockState block_state = BlockState.NewBlock(this);
            scope().set_start_position(scanner().location().beg_pos);
            TBlock block = factory().NewBlock(1, false);
            TStatement body = ParseFunctionDeclaration();
            block.AddStatement(body);
            scope().set_end_position(end_position());
            block.set_scope(scope().FinalizeBlockScope());
            return block;
        }
    }

    protected TStatement ParseVariableStatement(VariableDeclarationContext var_context, List<AstRawString> names)
    {
        // VariableStatement ::
        //   VariableDeclarations ';'

        // The scope of a var declared variable anywhere inside a function
        // is the entire function (ECMA-262, 3rd, 10.1.3, and 12.2). Thus we can
        // transform a source-level var declaration into a (Function) Scope
        // declaration, and rewrite the source-level initialization into an assignment
        // statement. We use a block to collect multiple assignments.
        //
        // We mark the block as initializer block because we don't want the
        // rewriter to add a '.result' assignment to such a block (to get compliant
        // behavior for code such as print(eval('var x = 7')), and for cosmetic
        // reasons when pretty-printing. Also, unless an assignment (initialization)
        // is inside an initializer block, it is ignored.

        DeclarationParsingResult parsing_result = new();
        ParseVariableDeclarations(var_context, parsing_result, names);
        ExpectSemicolon();
        return impl().BuildInitializationBlock(parsing_result);
    }

    protected TStatement ParseDebuggerStatement()
    {
        // In ECMA-262 'debugger' is defined as a reserved keyword. In some browser
        // contexts this is used as a statement which invokes the debugger as i a
        // break point is present.
        // DebuggerStatement ::
        //   'debugger' ';'

        int pos = peek_position();
        Consume(Token.Debugger);
        ExpectSemicolon();
        return factory().NewDebuggerStatement(pos);
    }

    protected TStatement ParseExpressionOrLabelledStatement(List<AstRawString> labels, List<AstRawString> own_labels,
                                                            AllowLabelledFunctionStatement allow_function)
    {
        // ExpressionStatement | LabelledStatement ::
        //   Expression ';'
        //   Identifier ':' Statement
        //
        // ExpressionStatement[Yield] :
        //   [lookahead notin {{, function, class, let [}] Expression[In, ?Yield] ;

        int pos = peek_position();

        switch (peek())
        {
            case Token.Function:
            case Token.LeftBrace:
                throw new InvalidOperationException("UNREACHABLE"); // Always handled by the callers.
            case Token.Class:
                ReportUnexpectedToken(Next());
                return impl().NullStatement();
            case Token.Let:
            {
                Token next_next = PeekAhead();
                // "let" followed by either "[", "{" or an identifier means a lexical
                // declaration, which should not appear here.
                // However, ASI may insert a line break before an identifier or a brace.
                if (next_next != Token.LeftBracket &&
                    ((next_next != Token.LeftBrace && next_next != Token.Identifier) ||
                     scanner_.HasLineTerminatorAfterNext()))
                {
                    break;
                }
                impl().ReportMessageAt(scanner().peek_location(), MessageTemplate.UnexpectedLexicalDeclaration);
                return impl().NullStatement();
            }
            default:
                break;
        }

        bool starts_with_identifier = peek_any_identifier();

        TExpression expr;
        {
            // Effectively inlines ParseExpression, so potential labels can be extracted
            // from expression_scope.
            using ExpressionParsingScope expression_scope = ExpressionParsingScope.New(impl());
            using AcceptINScope accept_in = new(this, true);
            expr = ParseExpressionCoverGrammar();
            expression_scope.ValidateExpression();

            if (peek() == Token.Colon && starts_with_identifier && impl().IsIdentifier(expr))
            {
                // The whole expression was a single identifier, and not, e.g.,
                // something starting with an identifier or a parenthesized identifier.
                VariableProxy label = expression_scope.variable_list().at(0).Item1;
                impl().DeclareLabel(ref labels, ref own_labels, label.raw_name());

                // Remove the "ghost" variable that turned out to be a label from the top
                // scope. This way, we don't try to resolve it during the scope
                // processing.
                this.scope().DeleteUnresolved(label);

                Consume(Token.Colon);
                // https://tc39.es/ecma262/#sec-labelled-function-declarations Labelled
                // Function Declarations
                if (peek() == Token.Function && is_sloppy(language_mode()) &&
                    allow_function == AllowLabelledFunctionStatement.kAllowLabelledFunctionStatement)
                {
                    return ParseFunctionDeclaration();
                }
                return ParseStatement(labels, own_labels, allow_function);
            }
        }

        // We allow a native function declaration if we're parsing the source for an
        // extension. A native function declaration starts with "native function"
        // with no line-terminator between the two words.
        if (impl().ParsingExtension() && peek() == Token.Function && !scanner().HasLineTerminatorBeforeNext() &&
            impl().IsNative(expr) && !scanner().literal_contains_escapes())
        {
            return ParseNativeDeclaration();
        }

        // Parsed expression statement, followed by semicolon.
        ExpectSemicolon();
        if (expr.IsFailureExpression()) return impl().NullStatement();
        return factory().NewExpressionStatement(expr, pos);
    }

    protected TStatement ParseIfStatement(List<AstRawString> labels)
    {
        // IfStatement ::
        //   'if' '(' Expression ')' Statement ('else' Statement)?

        int pos = peek_position();
        Consume(Token.If);
        Expect(Token.LeftParen);
        TExpression condition = ParseExpression();
        Expect(Token.RightParen);

        SourceRange then_range = new(), else_range = new();
        TStatement then_statement = impl().NullStatement();
        using (SourceRangeScope range_scope = new(scanner(), ref then_range))
        {
            // Make a copy of {labels} to avoid conflicts with any
            // labels that may be applied to the else clause below.
            List<AstRawString> labels_copy = labels == null ? labels : new List<AstRawString>(labels);
            then_statement = ParseScopedStatement(labels_copy);
        }

        TStatement else_statement = impl().NullStatement();
        if (Check(Token.Else))
        {
            else_statement = ParseScopedStatement(labels);
            else_range = SourceRange.ContinuationOf(then_range, end_position());
        }
        else
        {
            else_statement = factory().EmptyStatement();
        }
        TStatement stmt = factory().NewIfStatement(condition, then_statement, else_statement, pos);
        impl().RecordIfStatementSourceRange(stmt, then_range, else_range);
        return stmt;
    }

    protected TStatement ParseContinueStatement()
    {
        // ContinueStatement ::
        //   'continue' Identifier? ';'

        int pos = peek_position();
        Consume(Token.Continue);
        TIdentifier label = impl().NullIdentifier();
        Token tok = peek();
        if (!scanner().HasLineTerminatorBeforeNext() && !Token.IsAutoSemicolon(tok))
        {
            // ECMA allows "eval" or "arguments" as labels even in strict mode.
            label = ParseIdentifier();
        }
        TStatement target = LookupContinueTarget(label);
        if (impl().IsNull(target))
        {
            // Illegal continue statement.
            MessageTemplate message = MessageTemplate.IllegalContinue;
            TStatement breakable_target = LookupBreakTarget(label);
            if (impl().IsNull(label))
            {
                message = MessageTemplate.NoIterationStatement;
            }
            else if (impl().IsNull(breakable_target))
            {
                message = MessageTemplate.UnknownLabel;
            }
            ReportMessageIdentifier(message, label);
            return impl().NullStatement();
        }
        ExpectSemicolon();
        TStatement stmt = factory().NewContinueStatement(target, pos);
        impl().RecordJumpStatementSourceRange(stmt, end_position());
        return stmt;
    }

    protected TStatement ParseBreakStatement(List<AstRawString> labels)
    {
        // BreakStatement ::
        //   'break' Identifier? ';'

        int pos = peek_position();
        Consume(Token.Break);
        TIdentifier label = impl().NullIdentifier();
        Token tok = peek();
        if (!scanner().HasLineTerminatorBeforeNext() && !Token.IsAutoSemicolon(tok))
        {
            // ECMA allows "eval" or "arguments" as labels even in strict mode.
            label = ParseIdentifier();
        }
        // Parse labeled break statements that target themselves into
        // empty statements, e.g. 'l1: l2: l3: break l2;'
        if (!impl().IsNull(label) && ContainsLabel(labels, impl().GetRawNameFromIdentifier(label)))
        {
            ExpectSemicolon();
            return factory().EmptyStatement();
        }
        TStatement target = LookupBreakTarget(label);
        if (impl().IsNull(target))
        {
            // Illegal break statement.
            MessageTemplate message = MessageTemplate.IllegalBreak;
            if (!impl().IsNull(label))
            {
                message = MessageTemplate.UnknownLabel;
            }
            ReportMessageIdentifier(message, label);
            return impl().NullStatement();
        }
        ExpectSemicolon();
        TStatement stmt = factory().NewBreakStatement(target, pos);
        impl().RecordJumpStatementSourceRange(stmt, end_position());
        return stmt;
    }

    protected TStatement ParseReturnStatement()
    {
        // ReturnStatement ::
        //   'return' [no line terminator] Expression? ';'

        // Consume the return token. It is necessary to do that before
        // reporting any errors on it, because of the way errors are
        // reported (underlining).
        Consume(Token.Return);
        Scanner.Location loc = scanner().location();

        switch (GetDeclarationScope().scope_type())
        {
            case ScopeType.SCRIPT_SCOPE:
            case ScopeType.REPL_MODE_SCOPE:
            case ScopeType.EVAL_SCOPE:
            case ScopeType.MODULE_SCOPE:
                impl().ReportMessageAt(loc, MessageTemplate.IllegalReturn);
                return impl().NullStatement();
            case ScopeType.BLOCK_SCOPE:
                // Class static blocks disallow return. They are their own var scopes and
                // have a varblock scope.
                if (IsClassStaticInitializerFunction(function_state_.kind()))
                {
                    impl().ReportMessageAt(loc, MessageTemplate.IllegalReturn);
                    return impl().NullStatement();
                }
                break;
            default:
                break;
        }

        Token tok = peek();
        TExpression return_value = impl().NullExpression();
        if (!scanner().HasLineTerminatorBeforeNext() && !Token.IsAutoSemicolon(tok))
        {
            return_value = ParseExpression();
        }
        ExpectSemicolon();

        int continuation_pos = end_position();
        TStatement stmt = BuildReturnStatement(return_value, loc.beg_pos, continuation_pos);
        impl().RecordJumpStatementSourceRange(stmt, end_position());
        return stmt;
    }

    protected TStatement ParseWithStatement(List<AstRawString> labels)
    {
        // WithStatement ::
        //   'with' '(' Expression ')' Statement

        Consume(Token.With);
        int pos = position();

        if (is_strict(language_mode()))
        {
            ReportMessage(MessageTemplate.StrictWith);
            return impl().NullStatement();
        }
        impl().CountUsage(UseCounterFeature.kWithStatement);

        Expect(Token.LeftParen);
        TExpression expr = ParseExpression();
        Expect(Token.RightParen);

        Scope with_scope = NewScope(ScopeType.WITH_SCOPE);
        TStatement body = impl().NullStatement();
        using (BlockState block_state = new(this, with_scope))
        {
            with_scope.set_start_position(position());
            body = ParseStatement(labels, null);
            with_scope.set_end_position(end_position());
        }
        return factory().NewWithStatement(with_scope, expr, body, pos);
    }

    protected TStatement ParseDoWhileStatement(List<AstRawString> labels, List<AstRawString> own_labels)
    {
        // DoStatement ::
        //   'do' Statement 'while' '(' Expression ')' ';'
        using LoopScope loop_scope = new(function_state_);

        TStatement loop = factory().NewDoWhileStatement(peek_position());
        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        SourceRange body_range = new();
        TStatement body = impl().NullStatement();

        Consume(Token.Do);

        CheckStackOverflow();
        using (SourceRangeScope range_scope = new(scanner(), ref body_range))
        {
            body = ParseStatement(null, null);
        }
        Expect(Token.While);
        Expect(Token.LeftParen);

        TExpression cond = ParseExpression();
        Expect(Token.RightParen);

        // Allow do-statements to be terminated with and without
        // semi-colons. This allows code such as 'do;while(0)return' to
        // parse, which would not be the case if we had used the
        // ExpectSemicolon() functionality here.
        Check(Token.Semicolon);

        impl().InitializeConditionalLoop(loop, cond, body);
        impl().RecordIterationStatementSourceRange(loop, body_range);

        return loop;
    }

    protected TStatement ParseWhileStatement(List<AstRawString> labels, List<AstRawString> own_labels)
    {
        // WhileStatement ::
        //   'while' '(' Expression ')' Statement
        using LoopScope loop_scope = new(function_state_);

        TStatement loop = factory().NewWhileStatement(peek_position());
        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        SourceRange body_range = new();
        TStatement body = impl().NullStatement();

        Consume(Token.While);
        Expect(Token.LeftParen);
        TExpression cond = ParseExpression();
        Expect(Token.RightParen);
        using (SourceRangeScope range_scope = new(scanner(), ref body_range))
        {
            body = ParseStatement(null, null);
        }

        impl().InitializeConditionalLoop(loop, cond, body);
        impl().RecordIterationStatementSourceRange(loop, body_range);

        return loop;
    }

    protected TStatement ParseThrowStatement()
    {
        // ThrowStatement ::
        //   'throw' Expression ';'

        Consume(Token.Throw);
        int pos = position();
        if (scanner().HasLineTerminatorBeforeNext())
        {
            ReportMessage(MessageTemplate.NewlineAfterThrow);
            return impl().NullStatement();
        }
        TExpression exception = ParseExpression();
        ExpectSemicolon();

        TStatement stmt = impl().NewThrowStatement(exception, pos);
        impl().RecordThrowSourceRange(stmt, end_position());

        return stmt;
    }

    protected TStatement ParseSwitchStatement(List<AstRawString> labels)
    {
        // SwitchStatement ::
        //   'switch' '(' Expression ')' '{' CaseClause* '}'
        // CaseClause ::
        //   'case' Expression ':' StatementList
        //   'default' ':' StatementList
        int switch_pos = peek_position();

        Consume(Token.Switch);
        Expect(Token.LeftParen);
        TExpression tag = ParseExpression();
        Expect(Token.RightParen);

        TStatement switch_statement = factory().NewSwitchStatement(tag, switch_pos);

        {
            using BlockState cases_block_state = BlockState.NewBlock(this);
            scope().set_start_position(switch_pos);
            scope().SetNonlinear();
            using Target target = Target.New(this, switch_statement, labels, null, Target.TargetType.TARGET_FOR_ANONYMOUS);

            bool default_seen = false;
            Expect(Token.LeftBrace);
            while (peek() != Token.RightBrace)
            {
                // An empty label indicates the default case.
                TExpression label = impl().NullExpression();
                using TStatementList statements = TStatementList.New(pointer_buffer());
                SourceRange clause_range = new();
                using (SourceRangeScope range_scope = new(scanner(), ref clause_range))
                {
                    if (Check(Token.Case))
                    {
                        label = ParseExpression();
                    }
                    else
                    {
                        Expect(Token.Default);
                        if (default_seen)
                        {
                            ReportMessage(MessageTemplate.MultipleDefaultsInSwitch);
                            return impl().NullStatement();
                        }
                        default_seen = true;
                    }
                    Expect(Token.Colon);
                    while (peek() != Token.Case && peek() != Token.Default && peek() != Token.RightBrace)
                    {
                        TStatement stat = ParseStatementListItem();
                        if (impl().IsNull(stat)) return stat;
                        if (stat.IsEmptyStatement()) continue;
                        statements.Add(stat);
                    }
                }
                object clause = factory().NewCaseClause(label, statements);
                impl().RecordCaseClauseSourceRange(clause, clause_range);
                impl().AddCaseClause(switch_statement, clause);
            }
            Expect(Token.RightBrace);

            int end_pos = end_position();
            scope().set_end_position(end_pos);
            impl().RecordSwitchStatementSourceRange(switch_statement, end_pos);
            Scope switch_scope = scope().FinalizeBlockScope();
            if (switch_scope != null)
            {
                // Switch scopes are nonlinear, so we need to set the initializer position
                // to kMaxInt to prevent hole checks from being elided.
                foreach (Variable var in switch_scope.locals())
                {
                    var.set_initializer_position(int.MaxValue);
                }
                return impl().RewriteSwitchStatement(switch_statement, switch_scope);
            }
            return switch_statement;
        }
    }

    protected TStatement ParseTryStatement()
    {
        // TryStatement ::
        //   'try' Block Catch
        //   'try' Block Finally
        //   'try' Block Catch Finally
        //
        // Catch ::
        //   'catch' '(' Identifier ')' Block
        //
        // Finally ::
        //   'finally' Block

        Consume(Token.Try);
        int pos = position();

        TBlock try_block = ParseBlock(null);

        CatchInfo catch_info = new(this);

        if (peek() != Token.Catch && peek() != Token.Finally)
        {
            ReportMessage(MessageTemplate.NoCatchOrFinally);
            return impl().NullStatement();
        }

        SourceRange catch_range = new(), finally_range = new();

        TBlock catch_block = impl().NullBlock();
        using (SourceRangeScope catch_range_scope = new(scanner(), ref catch_range))
        {
            if (Check(Token.Catch))
            {
                bool has_binding;
                has_binding = Check(Token.LeftParen);

                if (has_binding)
                {
                    catch_info.scope = NewScope(ScopeType.CATCH_SCOPE);
                    catch_info.scope.set_start_position(position());

                    using (BlockState catch_block_state = new(this, catch_info.scope))
                    {
                        using TStatementList catch_statements = TStatementList.New(pointer_buffer());

                        // Create a block scope to hold any lexical declarations created
                        // as part of destructuring the catch parameter.
                        using (BlockState catch_variable_block_state = BlockState.NewBlock(this))
                        {
                            scope().set_start_position(peek_position());

                            if (peek_any_identifier())
                            {
                                TIdentifier identifier = ParseNonRestrictedIdentifier();
                                if (has_error()) return impl().NullStatement();
                                catch_info.variable = impl().DeclareCatchVariableName(catch_info.scope, identifier);
                            }
                            else
                            {
                                catch_info.variable =
                                    catch_info.scope.DeclareCatchVariableName(ast_value_factory().dot_catch_string());

                                ThreadedList<Declaration>.Iterator declaration_it = scope().declarations().end();

                                using (VariableDeclarationParsingScope destructuring =
                                       VariableDeclarationParsingScope.New(impl(), VariableMode.Let, null))
                                {
                                    catch_info.pattern = ParseBindingPattern();

                                    int initializer_position = end_position();
                                    ThreadedList<Declaration>.Iterator declaration_end = scope().declarations().end();
                                    for (; declaration_it != declaration_end; declaration_it.MoveNext())
                                    {
                                        declaration_it.Current.var().set_initializer_position(initializer_position);
                                    }

                                    if (has_error()) return impl().NullStatement();
                                    catch_statements.Add(impl().RewriteCatchPattern(catch_info));
                                }
                            }

                            Expect(Token.RightParen);

                            TBlock inner_block = ParseBlock(null);
                            catch_statements.Add(inner_block);

                            // Check for `catch(e) { let e; }` and similar errors.
                            if (!impl().HasCheckedSyntax())
                            {
                                Scope inner_scope = inner_block.scope();
                                if (inner_scope != null)
                                {
                                    AstRawString conflict = null;
                                    if (impl().IsNull(catch_info.pattern))
                                    {
                                        AstRawString name = catch_info.variable.raw_name();
                                        if (inner_scope.LookupLocal(name) != null) conflict = name;
                                    }
                                    else
                                    {
                                        conflict = inner_scope.FindVariableDeclaredIn(scope(), VariableMode.Var);
                                    }
                                    if (conflict != null)
                                    {
                                        impl().ReportVarRedeclarationIn(conflict, inner_scope);
                                    }
                                }
                            }

                            scope().set_end_position(end_position());
                            catch_block = factory().NewBlock(false, catch_statements);
                            catch_block.set_scope(scope().FinalizeBlockScope());
                        }
                    }

                    catch_info.scope.set_end_position(end_position());
                }
                else
                {
                    catch_block = ParseBlock(null);
                }
            }
        }

        TBlock finally_block = impl().NullBlock();
        using (SourceRangeScope range_scope = new(scanner(), ref finally_range))
        {
            if (Check(Token.Finally))
            {
                finally_block = ParseBlock(null);
            }
        }

        if (has_error()) return impl().NullStatement();
        return impl().RewriteTryStatement(try_block, catch_block, catch_range, finally_block, finally_range,
                                          catch_info, pos);
    }

    protected TStatement ParseForStatement(List<AstRawString> labels, List<AstRawString> own_labels)
    {
        // Either a standard for loop
        //   for (<init>; <cond>; <next>) { ... }
        // or a for-each loop
        //   for (<each> of|in <iterable>) { ... }
        //
        // We parse a declaration/expression after the 'for (' and then read the first
        // expression/declaration before we know if this is a for or a for-each.
        using LoopScope loop_scope = new(function_state_);

        int stmt_pos = peek_position();
        ForInfo for_info = new();

        Consume(Token.For);
        Expect(Token.LeftParen);

        bool starts_with_let = peek() == Token.Let;
        bool starts_with_using_or_await_using_keyword = IfStartsWithUsingOrAwaitUsingKeyword();
        if (peek() == Token.Const || (starts_with_let && IsNextLetKeyword()) ||
            starts_with_using_or_await_using_keyword)
        {
            // The initializer contains lexical declarations,
            // so create an in-between scope.
            using BlockState for_state = BlockState.NewBlock(this);
            scope().set_start_position(position());

            // Also record whether inner functions or evals are found inside
            // this loop, as this information is used to simplify the desugaring
            // if none are found.
            using FunctionOrEvalRecordingScope recording_scope = new(function_state_);

            // Create an inner block scope which will be the parent scope of scopes
            // possibly created by ParseVariableDeclarations.
            Scope inner_block_scope = NewScope(ScopeType.BLOCK_SCOPE);
            inner_block_scope.set_start_position(end_position());
            using (BlockState inner_state = new(this, inner_block_scope))
            {
                ParseVariableDeclarations(VariableDeclarationContext.kForStatement, for_info.parsing_result,
                                          for_info.bound_names);
            }
            for_info.position = position();

            if (CheckInOrOf(ref for_info.mode))
            {
                scope().set_is_hidden();
                if (starts_with_using_or_await_using_keyword &&
                    for_info.mode == ForEachStatement.VisitMode.ENUMERATE)
                {
                    impl().ReportMessageAt(scanner().location(), MessageTemplate.InvalidUsingInForInLoop);
                }
                return ParseForEachStatementWithDeclarations(stmt_pos, for_info, labels, own_labels,
                                                             inner_block_scope);
            }

            Expect(Token.Semicolon);

            // Parse the remaining code in the inner block scope since the declaration
            // above was parsed there. We'll finalize the unnecessary outer block scope
            // after parsing the rest of the loop.
            TStatement result = impl().NullStatement();
            using (BlockState inner_state = new(this, inner_block_scope))
            {
                TStatement init = impl().BuildInitializationBlock(for_info.parsing_result);

                result = ParseStandardForLoopWithLexicalDeclarations(stmt_pos, init, for_info, labels, own_labels);
            }
            scope().FinalizeBlockScope();
            return result;
        }

        TStatement init2 = impl().NullStatement();
        if (peek() == Token.Var)
        {
            ParseVariableDeclarations(VariableDeclarationContext.kForStatement, for_info.parsing_result,
                                      for_info.bound_names);
            for_info.position = position();

            if (CheckInOrOf(ref for_info.mode))
            {
                return ParseForEachStatementWithDeclarations(stmt_pos, for_info, labels, own_labels, scope());
            }

            init2 = impl().BuildInitializationBlock(for_info.parsing_result);
        }
        else if (peek() != Token.Semicolon)
        {
            // The initializer does not contain declarations.
            Scanner.Location next_loc = scanner().peek_location();
            int lhs_beg_pos = next_loc.beg_pos;
            int lhs_end_pos;
            bool is_for_each;
            TExpression expression;

            {
                using ExpressionParsingScope parsing_scope = ExpressionParsingScope.New(impl());
                using AcceptINScope accept_in = new(this, false);
                expression = ParseExpressionCoverGrammar();
                // `for (async of` is disallowed but `for (async.x of` is allowed, so
                // check if the token is kAsync after parsing the expression.
                bool expression_is_async = scanner().current_token() == Token.Async &&
                                           !scanner().literal_contains_escapes();
                // Initializer is reference followed by in/of.
                lhs_end_pos = end_position();
                is_for_each = CheckInOrOf(ref for_info.mode);
                if (is_for_each)
                {
                    if ((starts_with_let || expression_is_async) &&
                        for_info.mode == ForEachStatement.VisitMode.ITERATE)
                    {
                        impl().ReportMessageAt(next_loc,
                                               starts_with_let
                                                   ? MessageTemplate.ForOfLet
                                                   : MessageTemplate.ForOfAsync);
                        return impl().NullStatement();
                    }
                    if (expression.IsPattern())
                    {
                        parsing_scope.ValidatePattern(expression, lhs_beg_pos, lhs_end_pos);
                    }
                    else
                    {
                        expression = parsing_scope.ValidateAndRewriteReference(expression, lhs_beg_pos, lhs_end_pos);
                    }
                }
                else
                {
                    parsing_scope.ValidateExpression();
                }
            }

            if (is_for_each)
            {
                return ParseForEachStatementWithoutDeclarations(stmt_pos, expression, lhs_beg_pos, lhs_end_pos,
                                                                for_info, labels, own_labels);
            }
            // Initializer is just an expression.
            init2 = factory().NewExpressionStatement(expression, lhs_beg_pos);
        }

        Expect(Token.Semicolon);

        // Standard 'for' loop, we have parsed the initializer at this point.
        TExpression cond = impl().NullExpression();
        TStatement next = impl().NullStatement();
        TStatement body = impl().NullStatement();
        TStatement loop = ParseStandardForLoop(stmt_pos, labels, own_labels, ref cond, ref next, ref body);
        if (has_error()) return impl().NullStatement();
        impl().InitializeForLoop(loop, init2, cond, next, body);
        return loop;
    }

    protected TStatement ParseForEachStatementWithDeclarations(int stmt_pos, ForInfo for_info,
                                                               List<AstRawString> labels,
                                                               List<AstRawString> own_labels,
                                                               Scope inner_block_scope)
    {
        // Just one declaration followed by in/of.
        if (for_info.parsing_result.declarations.Count != 1)
        {
            impl().ReportMessageAt(for_info.parsing_result.bindings_loc, MessageTemplate.ForInOfLoopMultiBindings,
                                   ForEachStatement.VisitModeString(for_info.mode));
            return impl().NullStatement();
        }
        if (for_info.parsing_result.first_initializer_loc.IsValid() &&
            (is_strict(language_mode()) || for_info.mode == ForEachStatement.VisitMode.ITERATE ||
             IsLexicalVariableMode(for_info.parsing_result.descriptor.mode) ||
             !impl().IsIdentifier(for_info.parsing_result.declarations[0].pattern)))
        {
            impl().ReportMessageAt(for_info.parsing_result.first_initializer_loc,
                                   MessageTemplate.ForInOfLoopInitializer,
                                   ForEachStatement.VisitModeString(for_info.mode));
            return impl().NullStatement();
        }

        TBlock init_block = impl().RewriteForVarInLegacy(for_info);

        TStatement loop = factory().NewForEachStatement(for_info.mode, stmt_pos);
        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        Scope enumerable_block_scope = NewScope(ScopeType.BLOCK_SCOPE);
        enumerable_block_scope.set_start_position(position());
        enumerable_block_scope.set_is_hidden();
        TExpression enumerable = impl().NullExpression();
        using (BlockState block_state = new(this, enumerable_block_scope))
        {
            if (for_info.mode == ForEachStatement.VisitMode.ITERATE)
            {
                using AcceptINScope accept_in = new(this, true);
                enumerable = ParseAssignmentExpression();
            }
            else
            {
                enumerable = ParseExpression();
            }
        }
        enumerable_block_scope.set_end_position(end_position());

        Expect(Token.RightParen);

        TExpression each_variable = impl().NullExpression();
        TBlock body_block = impl().NullBlock();
        using (BlockState block_state = new(this, inner_block_scope))
        {
            SourceRange body_range = new();
            TStatement body = impl().NullStatement();
            using (SourceRangeScope range_scope = new(scanner(), ref body_range))
            {
                body = ParseStatement(null, null);
            }
            impl().RecordIterationStatementSourceRange(loop, body_range);

            impl().DesugarBindingInForEachStatement(for_info, ref body_block, ref each_variable);
            body_block.AddStatement(body);

            if (IsLexicalVariableMode(for_info.parsing_result.descriptor.mode))
            {
                scope().set_end_position(end_position());
                body_block.set_scope(scope().FinalizeBlockScope());
            }
        }

        impl().InitializeForEachStatement(loop, each_variable, enumerable, body_block, enumerable_block_scope);

        init_block = impl().CreateForEachStatementTDZ(init_block, for_info);

        // Parsed for-in loop w/ variable declarations.
        if (!impl().IsNull(init_block))
        {
            init_block.AddStatement(loop);
            if (IsLexicalVariableMode(for_info.parsing_result.descriptor.mode))
            {
                scope().set_end_position(end_position());
                init_block.set_scope(scope().FinalizeBlockScope());
            }
            return init_block;
        }

        return loop;
    }

    protected TStatement ParseForEachStatementWithoutDeclarations(int stmt_pos, TExpression expression,
                                                                  int lhs_beg_pos, int lhs_end_pos,
                                                                  ForInfo for_info, List<AstRawString> labels,
                                                                  List<AstRawString> own_labels)
    {
        TStatement loop = factory().NewForEachStatement(for_info.mode, stmt_pos);
        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        TExpression enumerable = impl().NullExpression();
        if (for_info.mode == ForEachStatement.VisitMode.ITERATE)
        {
            using AcceptINScope accept_in = new(this, true);
            enumerable = ParseAssignmentExpression();
        }
        else
        {
            enumerable = ParseExpression();
        }

        Expect(Token.RightParen);

        TStatement body = impl().NullStatement();
        SourceRange body_range = new();
        using (SourceRangeScope range_scope = new(scanner(), ref body_range))
        {
            body = ParseStatement(null, null);
        }
        impl().RecordIterationStatementSourceRange(loop, body_range);
        if (has_error()) return impl().NullStatement();
        impl().InitializeForEachStatement(loop, expression, enumerable, body, null);
        return loop;
    }

    // Same as the above, but handles those cases where <init> is a
    // lexical variable declaration.
    protected TStatement ParseStandardForLoopWithLexicalDeclarations(int stmt_pos, TStatement init, ForInfo for_info,
                                                                     List<AstRawString> labels,
                                                                     List<AstRawString> own_labels)
    {
        // The condition and the next statement of the for loop must be parsed
        // in a new scope.
        Scope inner_scope = NewScope(ScopeType.BLOCK_SCOPE);
        TStatement loop = impl().NullStatement();
        TExpression cond = impl().NullExpression();
        TStatement next = impl().NullStatement();
        TStatement body = impl().NullStatement();
        using (BlockState block_state = new(this, inner_scope))
        {
            scope().set_start_position(scanner().location().beg_pos);
            loop = ParseStandardForLoop(stmt_pos, labels, own_labels, ref cond, ref next, ref body);
            if (has_error()) return impl().NullStatement();
            scope().set_end_position(end_position());
        }

        scope().set_end_position(end_position());
        if (for_info.bound_names.Count > 0 && function_state_.contains_function_or_eval())
        {
            scope().set_is_hidden();
            return impl().DesugarLexicalBindingsInForStatement(loop, init, cond, next, body, inner_scope, for_info);
        }
        else
        {
            inner_scope = inner_scope.FinalizeBlockScope();
        }

        Scope for_scope = scope().FinalizeBlockScope();
        if (for_scope != null)
        {
            // Rewrite a for statement of the form
            //   for (const x = i; c; n) b
            //
            // into
            //
            //   {
            //     const x = i;
            //     for (; c; n) b
            //   }
            //
            TBlock block = factory().NewBlock(2, false);
            block.AddStatement(init);
            block.AddStatement(loop);
            block.set_scope(for_scope);
            impl().InitializeForLoop(loop, impl().NullStatement(), cond, next, body);
            return block;
        }

        impl().InitializeForLoop(loop, init, cond, next, body);
        return loop;
    }

    // Parse a C-style for loop: 'for (<init>; <cond>; <next>) { ... }'
    // "for (<init>;" is assumed to have been parser already.
    protected TStatement ParseStandardForLoop(int stmt_pos, List<AstRawString> labels, List<AstRawString> own_labels,
                                              ref TExpression cond, ref TStatement next, ref TStatement body)
    {
        CheckStackOverflow();
        TStatement loop = factory().NewForStatement(stmt_pos);
        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        if (peek() != Token.Semicolon)
        {
            cond = ParseExpression();
        }
        Expect(Token.Semicolon);

        if (peek() != Token.RightParen)
        {
            TExpression exp = ParseExpression();
            next = factory().NewExpressionStatement(exp, exp.position());
        }
        Expect(Token.RightParen);

        SourceRange body_range = new();
        using (SourceRangeScope range_scope = new(scanner(), ref body_range))
        {
            body = ParseStatement(null, null);
        }
        impl().RecordIterationStatementSourceRange(loop, body_range);

        return loop;
    }

    protected TStatement ParseForAwaitStatement(List<AstRawString> labels, List<AstRawString> own_labels)
    {
        // for await '(' ForDeclaration of AssignmentExpression ')'
        using LoopScope loop_scope = new(function_state_);

        int stmt_pos = peek_position();

        ForInfo for_info = new();
        for_info.mode = ForEachStatement.VisitMode.ITERATE;

        // Create an in-between scope for let-bound iteration variables.
        using BlockState for_state = BlockState.NewBlock(this);
        Expect(Token.For);
        Expect(Token.Await);
        Expect(Token.LeftParen);
        scope().set_start_position(position());
        scope().set_is_hidden();

        TStatement loop = factory().NewForOfStatement(stmt_pos, IteratorType.kAsync);
        // Two suspends: one for next() and one for return()
        function_state_.AddSuspend();
        function_state_.AddSuspend();

        using Target target = Target.New(this, loop, labels, own_labels, Target.TargetType.TARGET_FOR_ANONYMOUS);

        TExpression each_variable = impl().NullExpression();

        bool has_declarations = false;
        Scope inner_block_scope = NewScope(ScopeType.BLOCK_SCOPE);
        inner_block_scope.set_start_position(peek_position());

        bool starts_with_let = peek() == Token.Let;
        if (peek() == Token.Var || peek() == Token.Const || (starts_with_let && IsNextLetKeyword()) ||
            IfStartsWithUsingOrAwaitUsingKeyword())
        {
            // The initializer contains declarations
            // 'for' 'await' '(' ForDeclaration 'of' AssignmentExpression ')'
            //     Statement
            // 'for' 'await' '(' 'var' ForBinding 'of' AssignmentExpression ')'
            //     Statement
            has_declarations = true;

            using (BlockState inner_state = new(this, inner_block_scope))
            {
                ParseVariableDeclarations(VariableDeclarationContext.kForStatement, for_info.parsing_result,
                                          for_info.bound_names);
            }
            for_info.position = position();

            // Only a single declaration is allowed in for-await-of loops
            if (for_info.parsing_result.declarations.Count != 1)
            {
                impl().ReportMessageAt(for_info.parsing_result.bindings_loc,
                                       MessageTemplate.ForInOfLoopMultiBindings, "for-await-of");
                return impl().NullStatement();
            }

            // for-await-of's declarations do not permit initializers.
            if (for_info.parsing_result.first_initializer_loc.IsValid())
            {
                impl().ReportMessageAt(for_info.parsing_result.first_initializer_loc,
                                       MessageTemplate.ForInOfLoopInitializer, "for-await-of");
                return impl().NullStatement();
            }
        }
        else
        {
            // The initializer does not contain declarations.
            // 'for' 'await' '(' LeftHandSideExpression 'of' AssignmentExpression ')'
            //     Statement
            if (starts_with_let)
            {
                impl().ReportMessageAt(scanner().peek_location(), MessageTemplate.ForOfLet);
                return impl().NullStatement();
            }
            int lhs_beg_pos = peek_position();
            using BlockState inner_state = new(this, inner_block_scope);
            using ExpressionParsingScope parsing_scope = ExpressionParsingScope.New(impl());
            TExpression lhs = each_variable = ParseLeftHandSideExpression();
            int lhs_end_pos = end_position();

            if (lhs.IsPattern())
            {
                parsing_scope.ValidatePattern(lhs, lhs_beg_pos, lhs_end_pos);
            }
            else
            {
                each_variable = parsing_scope.ValidateAndRewriteReference(lhs, lhs_beg_pos, lhs_end_pos);
            }
        }

        ExpectContextualKeyword(Token.Of);

        const bool kAllowIn = true;
        TExpression iterable = impl().NullExpression();
        Scope iterable_block_scope = NewScope(ScopeType.BLOCK_SCOPE);
        iterable_block_scope.set_start_position(position());
        iterable_block_scope.set_is_hidden();

        using (BlockState block_state = new(this, iterable_block_scope))
        {
            using AcceptINScope accept_in = new(this, kAllowIn);
            iterable = ParseAssignmentExpression();
        }
        iterable_block_scope.set_end_position(end_position());

        Expect(Token.RightParen);

        TStatement body = impl().NullStatement();
        using (BlockState block_state = new(this, inner_block_scope))
        {
            SourceRange body_range = new();
            using (SourceRangeScope range_scope = new(scanner(), ref body_range))
            {
                body = ParseStatement(null, null);
                scope().set_end_position(end_position());
            }
            impl().RecordIterationStatementSourceRange(loop, body_range);

            if (has_declarations)
            {
                TBlock body_block = impl().NullBlock();
                impl().DesugarBindingInForEachStatement(for_info, ref body_block, ref each_variable);
                body_block.AddStatement(body);
                body_block.set_scope(scope().FinalizeBlockScope());
                body = body_block;
            }
            else
            {
                scope().FinalizeBlockScope();
            }
        }

        impl().InitializeForEachStatement(loop, each_variable, iterable, body, iterable_block_scope);

        if (!has_declarations)
        {
            scope().FinalizeBlockScope();
            return loop;
        }

        TBlock init_block = impl().CreateForEachStatementTDZ(impl().NullBlock(), for_info);

        scope().set_end_position(end_position());
        Scope for_scope = scope().FinalizeBlockScope();
        // Parsed for-in loop w/ variable declarations.
        if (!impl().IsNull(init_block))
        {
            init_block.AddStatement(loop);
            init_block.set_scope(for_scope);
            return init_block;
        }
        return loop;
    }

    protected void CheckClassMethodName(TIdentifier name, ParsePropertyKind type, ParseFunctionFlags flags,
                                        bool is_static, ref bool has_seen_constructor)
    {
        AstValueFactory avf = ast_value_factory();

        if (impl().IdentifierEquals(name, avf.private_constructor_string()))
        {
            ReportMessage(MessageTemplate.ConstructorIsPrivate);
            return;
        }
        else if (is_static)
        {
            if (impl().IdentifierEquals(name, avf.prototype_string()))
            {
                ReportMessage(MessageTemplate.StaticPrototype);
                return;
            }
        }
        else if (impl().IdentifierEquals(name, avf.constructor_string()))
        {
            if (flags != ParseFunctionFlags.kIsNormal || IsAccessor(type))
            {
                MessageTemplate msg = (flags & ParseFunctionFlags.kIsGenerator) != 0
                    ? MessageTemplate.ConstructorIsGenerator
                    : (flags & ParseFunctionFlags.kIsAsync) != 0
                        ? MessageTemplate.ConstructorIsAsync
                        : MessageTemplate.ConstructorIsAccessor;
                ReportMessage(msg);
                return;
            }
            if (has_seen_constructor)
            {
                ReportMessage(MessageTemplate.DuplicateConstructor);
                return;
            }
            has_seen_constructor = true;
            return;
        }
    }

    protected void CheckClassFieldName(TIdentifier name, bool is_static)
    {
        AstValueFactory avf = ast_value_factory();
        if (is_static && impl().IdentifierEquals(name, avf.prototype_string()))
        {
            ReportMessage(MessageTemplate.StaticPrototype);
            return;
        }

        if (impl().IdentifierEquals(name, avf.constructor_string()) ||
            impl().IdentifierEquals(name, avf.private_constructor_string()))
        {
            ReportMessage(MessageTemplate.ConstructorClassField);
            return;
        }
    }

    protected void ReportMessageIdentifier(MessageTemplate message, TIdentifier arg)
        => ReportMessageAt(scanner().location(), message, impl().IdentifierToAstRawString(arg));
}
