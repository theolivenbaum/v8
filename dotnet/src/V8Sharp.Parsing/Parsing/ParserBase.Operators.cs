// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser-base.h: assignment, conditional, binary, unary,
// postfix, call and member expressions, formal parameters.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    protected TExpression ParseConditionalChainAssignmentExpressionCoverGrammar()
    {
        // AssignmentExpression ::
        //   ArrowFunction
        //   YieldExpression
        //   LeftHandSideExpression AssignmentOperator AssignmentExpression
        int lhs_beg_pos = peek_position();

        if (peek() == Token.Yield && is_generator())
        {
            return ParseYieldExpression();
        }

        using FuncNameInferrerState fni_state = new(fni_);

        TExpression expression = ParseLogicalExpression();

        Token op = peek();

        if (!Token.IsArrowOrAssignmentOp(op) || peek() == Token.Conditional)
        {
            return expression;
        }

        return ParseAssignmentExpressionCoverGrammarContinuation(lhs_beg_pos, expression);
    }

    // Precedence = 2
    protected TExpression ParseAssignmentExpressionCoverGrammar()
    {
        // AssignmentExpression ::
        //   ConditionalExpression
        //   ArrowFunction
        //   YieldExpression
        //   LeftHandSideExpression AssignmentOperator AssignmentExpression
        int lhs_beg_pos = peek_position();

        if (peek() == Token.Yield && is_generator())
        {
            return ParseYieldExpression();
        }

        using FuncNameInferrerState fni_state = new(fni_);

        TExpression expression = ParseConditionalExpression();

        Token op = peek();

        if (!Token.IsArrowOrAssignmentOp(op)) return expression;

        return ParseAssignmentExpressionCoverGrammarContinuation(lhs_beg_pos, expression);
    }

    // Precedence = 2
    protected TExpression ParseAssignmentExpressionCoverGrammarContinuation(int lhs_beg_pos, TExpression expression)
    {
        // AssignmentExpression ::
        //   ConditionalExpression
        //   ArrowFunction
        //   YieldExpression
        //   LeftHandSideExpression AssignmentOperator AssignmentExpression
        Token op = peek();

        // Arrow functions.
        if (op == Token.Arrow)
        {
            Scanner.Location loc = new(lhs_beg_pos, end_position());

            if (!impl().IsIdentifier(expression) && !expression.is_parenthesized())
            {
                impl().ReportMessageAt(
                    new Scanner.Location(expression.position() == kNoSourcePosition
                                             ? lhs_beg_pos
                                             : expression.position(),
                                         position()),
                    MessageTemplate.MalformedArrowFunParamList);
                return impl().FailureExpression();
            }

            DeclarationScope scope = next_arrow_function_info_.scope;
            int function_literal_id = next_arrow_function_info_.function_literal_id;
            scope.set_start_position(lhs_beg_pos);

            TFormalParameters parameters = impl().NewFormalParameters(scope);
            parameters.set_strict_parameter_error(next_arrow_function_info_.strict_parameter_error_location,
                                                  next_arrow_function_info_.strict_parameter_error_message);
            parameters.is_simple = scope.has_simple_parameters();
            bool could_be_immediately_invoked = next_arrow_function_info_.could_be_immediately_invoked;
            next_arrow_function_info_.Reset();

            impl().DeclareArrowFunctionFormalParameters(parameters, expression, loc);
            // function_literal_id was reserved for the arrow function, but not actaully
            // allocated. This comparison allocates a function literal id for the arrow
            // function, and checks whether it's still the function id we wanted. If
            // not, we'll reindex the arrow function formal parameters to shift them all
            // 1 down to make space for the arrow function.
            if (function_literal_id != GetNextInfoId())
            {
                AllowReindexScope dummy_scope = new(null);
                impl().ReindexArrowFunctionFormalParameters(parameters, dummy_scope);
            }

            expression = ParseArrowFunctionLiteral(parameters, function_literal_id, could_be_immediately_invoked);

            return expression;
        }

        if (impl().IsAssignableIdentifier(expression))
        {
            if (expression.is_parenthesized())
            {
                expression_scope().RecordDeclarationError(new Scanner.Location(lhs_beg_pos, end_position()),
                                                          MessageTemplate.InvalidDestructuringTarget);
            }
            expression_scope().MarkIdentifierAsAssigned();
        }
        else if (expression.IsProperty())
        {
            expression_scope().RecordDeclarationError(new Scanner.Location(lhs_beg_pos, end_position()),
                                                      MessageTemplate.InvalidPropertyBindingPattern);
            expression_scope().ValidateAsExpression();
        }
        else if (expression.IsPattern() && op == Token.Assign)
        {
            // Destructuring assignmment.
            if (expression.is_parenthesized())
            {
                Scanner.Location loc = new(lhs_beg_pos, end_position());
                if (expression_scope().IsCertainlyDeclaration())
                {
                    impl().ReportMessageAt(loc, MessageTemplate.InvalidDestructuringTarget);
                }
                else
                {
                    // Syntax Error if LHS is neither object literal nor an array literal
                    // (Parenthesized literals are
                    // CoverParenthesizedExpressionAndArrowParameterList).
                    // https://tc39.es/ecma262/#sec-assignment-operators-static-semantics-early-errors
                    impl().ReportMessageAt(loc, MessageTemplate.InvalidLhsInAssignment);
                }
            }
            expression_scope().ValidateAsPattern(expression, lhs_beg_pos, end_position());
        }
        else
        {
            // For web compatibility reasons, throw early errors only for logical
            // assignment, not for regular assignment.
            bool early_error = Token.IsLogicalAssignmentOp(op);
            expression = RewriteInvalidReferenceExpression(expression, lhs_beg_pos, end_position(),
                                                           MessageTemplate.InvalidLhsInAssignment, early_error);
        }

        Consume(op);
        int op_position = position();

        TExpression right = ParseAssignmentExpression();

        // Anonymous function name inference applies to =, ||=, &&=, and ??=.
        if (op == Token.Assign || Token.IsLogicalAssignmentOp(op))
        {
            impl().CheckAssigningFunctionLiteralToProperty(expression, right);

            // Check if the right hand side is a call to avoid inferring a
            // name if we're dealing with "a = function(){...}();"-like
            // expression.
            if (right.IsCall() || right.IsCallNew())
            {
                fni_.RemoveLastFunction();
            }
            else
            {
                fni_.Infer();
            }

            impl().SetFunctionNameFromIdentifierRef(right, expression);
        }
        else
        {
            fni_.RemoveLastFunction();
        }

        if (op == Token.Assign)
        {
            // We try to estimate the set of properties set by constructors. We define a
            // new property whenever there is an assignment to a property of 'this'. We
            // should probably only add properties if we haven't seen them before.
            // Otherwise we'll probably overestimate the number of properties.
            if (impl().IsThisProperty(expression)) function_state_.AddProperty();
        }
        else
        {
            if (Token.IsLogicalAssignmentOp(op))
            {
                impl().CountUsage(UseCounterFeature.kLogicalAssignment);
            }
            // Only initializers (i.e. no compound assignments) are allowed in patterns.
            expression_scope().RecordPatternError(new Scanner.Location(lhs_beg_pos, end_position()),
                                                  MessageTemplate.InvalidDestructuringTarget);
        }

        return factory().NewAssignment(op, expression, right, op_position);
    }

    protected TExpression ParseYieldExpression()
    {
        // YieldExpression ::
        //   'yield' ([no line terminator] '*'? AssignmentExpression)?
        int pos = peek_position();
        expression_scope().RecordParameterInitializerError(scanner().peek_location(),
                                                           MessageTemplate.YieldInParameter);
        Consume(Token.Yield);
        if (scanner().literal_contains_escapes())
        {
            impl().ReportUnexpectedToken(Token.EscapedKeyword);
        }

        CheckStackOverflow();

        // The following initialization is necessary.
        TExpression expression = impl().NullExpression();
        bool delegating = false; // yield*
        if (!scanner().HasLineTerminatorBeforeNext())
        {
            if (Check(Token.Mul)) delegating = true;
            switch (peek())
            {
                case Token.Eos:
                case Token.Semicolon:
                case Token.RightBrace:
                case Token.RightBracket:
                case Token.RightParen:
                case Token.Colon:
                case Token.Comma:
                case Token.In:
                    // The above set of tokens is the complete set of tokens that can appear
                    // after an AssignmentExpression, and none of them can start an
                    // AssignmentExpression.  This allows us to avoid looking for an RHS for
                    // a regular yield, given only one look-ahead token.
                    if (!delegating) break;
                    // Delegating yields require an RHS; fall through.
                    goto default;
                default:
                    expression = ParseAssignmentExpressionCoverGrammar();
                    break;
            }
        }

        if (delegating)
        {
            TExpression yieldstar = factory().NewYieldStar(expression, pos);
            impl().RecordSuspendSourceRange(yieldstar, PositionAfterSemicolon());
            function_state_.AddSuspend();
            if (IsAsyncGeneratorFunction(function_state_.kind()))
            {
                // return, iterator_close and delegated_iterator_output suspend ids.
                function_state_.AddSuspend();
                function_state_.AddSuspend();
                function_state_.AddSuspend();
            }
            return yieldstar;
        }

        // Hackily disambiguate o from o.next and o [Symbol.iterator]().
        // TODO(verwaest): Come up with a better solution.
        TExpression yield = factory().NewYield(expression, pos, Suspend.OnAbruptResume.kOnExceptionThrow);
        impl().RecordSuspendSourceRange(yield, PositionAfterSemicolon());
        function_state_.AddSuspend();
        return yield;
    }

    // Precedence = 3
    protected TExpression ParseConditionalExpression()
    {
        // ConditionalExpression ::
        //   LogicalExpression
        //   LogicalExpression '?' AssignmentExpression ':' AssignmentExpression
        //
        int pos = peek_position();
        TExpression expression = ParseLogicalExpression();
        return peek() == Token.Conditional ? ParseConditionalChainExpression(expression, pos) : expression;
    }

    protected TExpression ParseLogicalExpression()
    {
        // LogicalExpression ::
        //   LogicalORExpression
        //   CoalesceExpression

        // Both LogicalORExpression and CoalesceExpression start with BitwiseOR.
        // Parse for binary expressions >= 6 (BitwiseOR);
        TExpression expression = ParseBinaryExpression(6);
        if (peek() == Token.And || peek() == Token.Or)
        {
            // LogicalORExpression, pickup parsing where we left off.
            int prec1 = Token.Precedence(peek(), accept_IN_);
            expression = ParseBinaryContinuation(expression, 4, prec1);
        }
        else if (peek() == Token.Nullish)
        {
            expression = ParseCoalesceExpression(expression);
        }
        return expression;
    }

    protected TExpression ParseCoalesceExpression(TExpression expression)
    {
        // CoalesceExpression ::
        //   CoalesceExpressionHead ?? BitwiseORExpression
        //
        //   CoalesceExpressionHead ::
        //     CoalesceExpression
        //     BitwiseORExpression

        // We create a binary operation for the first nullish, otherwise collapse
        // into an nary expresion.
        bool first_nullish = true;
        while (peek() == Token.Nullish)
        {
            SourceRange right_range = new();
            int pos;
            TExpression y;
            using (SourceRangeScope right_range_scope = new(scanner(), ref right_range))
            {
                Consume(Token.Nullish);
                pos = peek_position();
                // Parse BitwiseOR or higher.
                y = ParseBinaryExpression(6);
            }
            if (first_nullish)
            {
                impl().CountUsage(UseCounterFeature.kNullishCoalescing);
                expression = factory().NewBinaryOperation(Token.Nullish, expression, y, pos);
                impl().RecordBinaryOperationSourceRange(expression, right_range);
                first_nullish = false;
            }
            else
            {
                impl().CollapseNaryExpression(ref expression, y, Token.Nullish, pos, right_range);
            }
        }
        return expression;
    }

    protected TExpression ParseConditionalChainExpression(TExpression condition, int condition_pos)
    {
        // ConditionalChainExpression ::
        // ConditionalExpression_1 ? AssignmentExpression_1 :
        // ConditionalExpression_2 ? AssignmentExpression_2 :
        // ConditionalExpression_3 ? AssignmentExpression_3 :
        // ...
        // ConditionalExpression_n ? AssignmentExpression_n

        TExpression expr = impl().NullExpression();
        TExpression else_expression = impl().NullExpression();
        bool else_found = false;
        List<int> else_ranges_beg_pos = null;
        do
        {
            SourceRange then_range = new();
            TExpression then_expression;
            using (SourceRangeScope range_scope = new(scanner(), ref then_range))
            {
                Consume(Token.Conditional);
                // In parsing the first assignment expression in conditional
                // expressions we always accept the 'in' keyword; see ECMA-262,
                // section 11.12, page 58.
                using AcceptINScope accept_in = new(this, true);
                then_expression = ParseAssignmentExpression();
            }

            (else_ranges_beg_pos ??= []).Add(scanner().peek_location().beg_pos);
            int condition_or_else_pos = peek_position();
            SourceRange condition_or_else_range = new();
            TExpression condition_or_else_expression;
            using (SourceRangeScope condition_or_else_range_scope = new(scanner(), ref condition_or_else_range))
            {
                Expect(Token.Colon);
                condition_or_else_expression = ParseConditionalChainAssignmentExpression();
            }

            else_found = peek() != Token.Conditional;

            if (else_found)
            {
                else_expression = condition_or_else_expression;

                if (impl().IsNull(expr))
                {
                    // When we have a single conditional expression, we don't create a
                    // conditional chain expression. Instead, we just return a conditional
                    // expression.
                    SourceRange else_range = condition_or_else_range;
                    expr = factory().NewConditional(condition, then_expression, else_expression, condition_pos);
                    impl().RecordConditionalSourceRange(expr, then_range, else_range);
                    return expr;
                }
            }

            if (impl().IsNull(expr))
            {
                // For the first conditional expression, we create a conditional chain.
                expr = factory().NewConditionalChain(1, condition_pos);
            }

            impl().CollapseConditionalChain(ref expr, condition, then_expression, else_expression, condition_pos,
                                            then_range);

            if (!else_found)
            {
                condition = condition_or_else_expression;
                condition_pos = condition_or_else_pos;
            }
        } while (!else_found);

        int end_pos = scanner().location().end_pos;
        foreach (int else_range_beg_pos in else_ranges_beg_pos)
        {
            impl().AppendConditionalChainElse(ref expr, new SourceRange(else_range_beg_pos, end_pos));
        }

        return expr;
    }

    protected TExpression ParseConditionalContinuation(TExpression expression, int pos)
    {
        SourceRange then_range = new(), else_range = new();

        TExpression left;
        using (SourceRangeScope range_scope = new(scanner(), ref then_range))
        {
            Consume(Token.Conditional);
            // In parsing the first assignment expression in conditional
            // expressions we always accept the 'in' keyword; see ECMA-262,
            // section 11.12, page 58.
            using AcceptINScope accept_in = new(this, true);
            left = ParseAssignmentExpression();
        }
        TExpression right;
        using (SourceRangeScope range_scope = new(scanner(), ref else_range))
        {
            Expect(Token.Colon);
            right = ParseAssignmentExpression();
        }
        TExpression expr = factory().NewConditional(expression, left, right, pos);
        impl().RecordConditionalSourceRange(expr, then_range, else_range);
        return expr;
    }

    // Precedence >= 4
    protected TExpression ParseBinaryContinuation(TExpression x, int prec, int prec1)
    {
        do
        {
            // prec1 >= 4
            while (Token.Precedence(peek(), accept_IN_) == prec1)
            {
                SourceRange right_range = new();
                int pos = peek_position();
                TExpression y;
                Token op;
                using (SourceRangeScope right_range_scope = new(scanner(), ref right_range))
                {
                    op = Next();

                    bool is_right_associative = op == Token.Exp;
                    int next_prec = is_right_associative ? prec1 : prec1 + 1;
                    y = ParseBinaryExpression(next_prec);
                }

                // For now we distinguish between comparisons and other binary
                // operations.  (We could combine the two and get rid of this
                // code and AST node eventually.)
                if (Token.IsCompareOp(op))
                {
                    // We have a comparison.
                    Token cmp = op;
                    switch (op)
                    {
                        case Token.NotEq:
                            cmp = Token.Eq;
                            break;
                        case Token.NotEqStrict:
                            cmp = Token.EqStrict;
                            break;
                        default: break;
                    }
                    x = factory().NewCompareOperation(cmp, x, y, pos);
                    if (cmp != op)
                    {
                        // The comparison was negated - add a kNot.
                        x = factory().NewUnaryOperation(Token.Not, x, pos);
                    }
                }
                else if (!impl().ShortcutLiteralBinaryExpression(ref x, y, op, pos) &&
                         !impl().CollapseNaryExpression(ref x, y, op, pos, right_range))
                {
                    // We have a "normal" binary operation.
                    x = factory().NewBinaryOperation(op, x, y, pos);
                    if (op == Token.Or || op == Token.And)
                    {
                        impl().RecordBinaryOperationSourceRange(x, right_range);
                    }
                }
            }
            --prec1;
        } while (prec1 >= prec);

        return x;
    }

    // Precedence >= 4
    protected TExpression ParseBinaryExpression(int prec)
    {
        // "#foo in ShiftExpression" needs to be parsed separately, since private
        // identifiers are not valid PrimaryExpressions.
        if (peek() == Token.PrivateName)
        {
            TExpression x = ParsePropertyOrPrivatePropertyName();
            int prec1 = Token.Precedence(peek(), accept_IN_);
            if (peek() != Token.In || prec1 < prec)
            {
                ReportUnexpectedToken(Token.PrivateName);
                return impl().FailureExpression();
            }
            return ParseBinaryContinuation(x, prec, prec1);
        }

        TExpression x2 = ParseUnaryExpression();
        int prec2 = Token.Precedence(peek(), accept_IN_);
        if (prec2 >= prec)
        {
            return ParseBinaryContinuation(x2, prec, prec2);
        }
        return x2;
    }

    protected TExpression ParseUnaryOrPrefixExpression()
    {
        Token op = Next();
        int pos = position();

        // Assume "! function ..." indicates the function is likely to be called.
        if (op == Token.Not && peek() == Token.Function)
        {
            function_state_.set_next_function_is_likely_called();
        }

        CheckStackOverflow();

        int expression_position = peek_position();
        TExpression expression = ParseUnaryExpression();

        if (Token.IsUnaryOp(op))
        {
            if (op == Token.Delete)
            {
                if (impl().IsIdentifier(expression) && is_strict(language_mode()))
                {
                    // "delete identifier" is a syntax error in strict mode.
                    ReportMessage(MessageTemplate.StrictDelete);
                    return impl().FailureExpression();
                }

                if (impl().IsPrivateReference(expression))
                {
                    ReportMessage(MessageTemplate.DeletePrivateField);
                    return impl().FailureExpression();
                }
            }

            if (peek() == Token.Exp)
            {
                impl().ReportMessageAt(new Scanner.Location(pos, peek_end_position()),
                                       MessageTemplate.UnexpectedTokenUnaryExponentiation);
                return impl().FailureExpression();
            }

            // Allow the parser's implementation to rewrite the expression.
            return impl().BuildUnaryExpression(expression, op, pos);
        }

        if (IsValidReferenceExpression(expression))
        {
            if (impl().IsIdentifier(expression))
            {
                expression_scope().MarkIdentifierAsAssigned();
            }
        }
        else
        {
            const bool early_error = false;
            expression = RewriteInvalidReferenceExpression(expression, expression_position, end_position(),
                                                           MessageTemplate.InvalidLhsInPrefixOp, early_error);
        }

        return factory().NewCountOperation(op, true /* prefix */, expression, position());
    }

    protected TExpression ParseAwaitExpression()
    {
        if (IsModule(function_state_.kind()))
        {
            impl().CountUsage(UseCounterFeature.kTopLevelAwait);
        }

        expression_scope().RecordParameterInitializerError(scanner().peek_location(),
                                                           MessageTemplate.AwaitExpressionFormalParameter);
        int await_pos = peek_position();
        Consume(Token.Await);
        if (scanner().literal_contains_escapes())
        {
            impl().ReportUnexpectedToken(Token.EscapedKeyword);
        }

        CheckStackOverflow();

        TExpression value = ParseUnaryExpression();

        // 'await' is a unary operator according to the spec, even though it's treated
        // specially in the parser.
        if (peek() == Token.Exp)
        {
            impl().ReportMessageAt(new Scanner.Location(await_pos, peek_end_position()),
                                   MessageTemplate.UnexpectedTokenUnaryExponentiation);
            return impl().FailureExpression();
        }

        TExpression expr = factory().NewAwait(value, await_pos);
        function_state_.AddSuspend();
        impl().RecordSuspendSourceRange(expr, PositionAfterSemicolon());
        return expr;
    }

    protected TExpression ParseUnaryExpression()
    {
        // UnaryExpression ::
        //   PostfixExpression
        //   'delete' UnaryExpression
        //   'void' UnaryExpression
        //   'typeof' UnaryExpression
        //   '++' UnaryExpression
        //   '--' UnaryExpression
        //   '+' UnaryExpression
        //   '-' UnaryExpression
        //   '~' UnaryExpression
        //   '!' UnaryExpression
        //   [+Await] AwaitExpression[?Yield]

        Token op = peek();
        if (Token.IsUnaryOrCountOp(op)) return ParseUnaryOrPrefixExpression();
        if (is_await_allowed() && op == Token.Await)
        {
            return ParseAwaitExpression();
        }
        return ParsePostfixExpression();
    }

    protected TExpression ParsePostfixExpression()
    {
        // PostfixExpression ::
        //   LeftHandSideExpression ('++' | '--')?

        int lhs_beg_pos = peek_position();
        TExpression expression = ParseLeftHandSideExpression();
        if (!Token.IsCountOp(peek()) || scanner().HasLineTerminatorBeforeNext())
        {
            return expression;
        }
        return ParsePostfixContinuation(expression, lhs_beg_pos);
    }

    protected TExpression ParsePostfixContinuation(TExpression expression, int lhs_beg_pos)
    {
        if (!IsValidReferenceExpression(expression))
        {
            const bool early_error = false;
            expression = RewriteInvalidReferenceExpression(expression, lhs_beg_pos, end_position(),
                                                           MessageTemplate.InvalidLhsInPostfixOp, early_error);
        }
        if (impl().IsIdentifier(expression))
        {
            expression_scope().MarkIdentifierAsAssigned();
        }

        Token next = Next();
        return factory().NewCountOperation(next, false /* postfix */, expression, position());
    }

    protected TExpression ParseLeftHandSideExpression()
    {
        // LeftHandSideExpression ::
        //   (NewExpression | MemberExpression) ...

        TExpression result = ParseMemberExpression();
        if (!Token.IsPropertyOrCall(peek())) return result;
        return ParseLeftHandSideContinuation(result);
    }

    protected TExpression ParseLeftHandSideContinuation(TExpression result)
    {
        if (peek() == Token.LeftParen && impl().IsIdentifier(result) &&
            scanner().current_token() == Token.Async && !scanner().HasLineTerminatorBeforeNext() &&
            !scanner().literal_contains_escapes())
        {
            int pos = position();

            using ArrowHeadParsingScope maybe_arrow = new(impl(), FunctionKind.AsyncArrowFunction, PeekNextInfoId());
            using Scope.Snapshot scope_snapshot = new(scope());

            using TExpressionList args = TExpressionList.New(pointer_buffer());
            ParseArguments(args, out bool has_spread, ParsingArrowHeadFlag.kMaybeArrowHead);
            if (peek() == Token.Arrow)
            {
                fni_.RemoveAsyncKeywordFromEnd();
                next_arrow_function_info_.scope = maybe_arrow.ValidateAndCreateScope();
                next_arrow_function_info_.function_literal_id = maybe_arrow.function_literal_id();
                scope_snapshot.Reparent(next_arrow_function_info_.scope);
                // async () => ...
                if (args.length() == 0) return factory().NewEmptyParentheses(pos);
                // async ( Arguments ) => ...
                result = impl().ExpressionListToExpression(args);
                result.mark_parenthesized();
                return result;
            }

            result = factory().NewCall(result, args, pos, has_spread);

            maybe_arrow.ValidateExpression();

            fni_.RemoveLastFunction();
            if (!Token.IsPropertyOrCall(peek())) return result;
        }

        bool optional_chaining = false;
        bool is_optional = false;
        int optional_link_begin = 0;
        do
        {
            switch (peek())
            {
                case Token.QuestionPeriod:
                {
                    if (is_optional)
                    {
                        ReportUnexpectedToken(peek());
                        return impl().FailureExpression();
                    }
                    // Include the ?. in the source range position.
                    optional_link_begin = scanner().peek_location().beg_pos;
                    Consume(Token.QuestionPeriod);
                    is_optional = true;
                    optional_chaining = true;
                    if (Token.IsPropertyOrCall(peek())) continue;
                    int pos = position();
                    TExpression key = ParsePropertyOrPrivatePropertyName();
                    result = factory().NewProperty(result, key, pos, is_optional);
                    break;
                }

                /* Property */
                case Token.LeftBracket:
                {
                    Consume(Token.LeftBracket);
                    int pos = position();
                    using AcceptINScope accept_in = new(this, true);
                    TExpression index = ParseExpressionCoverGrammar();
                    result = factory().NewProperty(result, index, pos, is_optional);
                    Expect(Token.RightBracket);
                    break;
                }

                /* Property */
                case Token.Period:
                {
                    if (is_optional)
                    {
                        ReportUnexpectedToken(Next());
                        return impl().FailureExpression();
                    }
                    Consume(Token.Period);
                    int pos = position();
                    TExpression key = ParsePropertyOrPrivatePropertyName();
                    result = factory().NewProperty(result, key, pos, is_optional);
                    break;
                }

                /* Call */
                case Token.LeftParen:
                {
                    int pos;
                    if (Token.IsCallable(scanner().current_token()))
                    {
                        // For call of an identifier we want to report position of
                        // the identifier as position of the call in the stack trace.
                        pos = position();
                    }
                    else
                    {
                        // For other kinds of calls we record position of the parenthesis as
                        // position of the call. Note that this is extremely important for
                        // expressions of the form function(){...}() for which call position
                        // should not point to the closing brace otherwise it will intersect
                        // with positions recorded for function literal and confuse debugger.
                        pos = peek_position();
                        // Also the trailing parenthesis are a hint that the function will
                        // be called immediately. If we happen to have parsed a preceding
                        // function literal eagerly, we can also compile it eagerly.
                        if (result.IsFunctionLiteral())
                        {
                            impl().SetShouldEagerCompile(result);
                        }
                    }
                    using TExpressionList args = TExpressionList.New(pointer_buffer());
                    ParseArguments(args, out bool has_spread);

                    // Keep track of eval() calls since they disable all local variable
                    // optimizations.
                    // The calls that need special treatment are the
                    // direct eval calls. These calls are all of the form eval(...), with
                    // no explicit receiver.
                    // These calls are marked as potentially direct eval calls. Whether
                    // they are actually direct calls to eval is determined at run time.
                    int eval_scope_info_index = 0;
                    if (CheckPossibleEvalCall(result, is_optional, scope()))
                    {
                        eval_scope_info_index = GetNextInfoId();
                        if (!Call.EvalScopeInfoIndexFieldIsValid(eval_scope_info_index + max_drift_.Value))
                        {
                            ReportMessage(MessageTemplate.TooManyEvals);
                            return impl().FailureExpression();
                        }
                    }

                    result = factory().NewCall(result, args, pos, has_spread, eval_scope_info_index, is_optional);

                    fni_.RemoveLastFunction();
                    break;
                }

                default:
                    // Template literals in/after an Optional Chain not supported:
                    if (optional_chaining)
                    {
                        impl().ReportMessageAt(scanner().peek_location(),
                                               MessageTemplate.OptionalChainingNoTemplate);
                        return impl().FailureExpression();
                    }
                    /* Tagged Template */
                    result = ParseTemplateLiteral(result, position(), true);
                    break;
            }
            if (is_optional)
            {
                SourceRange chain_link_range = new(optional_link_begin, end_position());
                impl().RecordExpressionSourceRange(result, chain_link_range);
                is_optional = false;
            }
        } while (Token.IsPropertyOrCall(peek()));
        if (optional_chaining) return factory().NewOptionalChain(result);
        return result;
    }

    protected TExpression ParseMemberWithPresentNewPrefixesExpression()
    {
        // NewExpression ::
        //   ('new')+ MemberExpression
        //
        // NewTarget ::
        //   'new' '.' 'target'
        //
        // ImportMeta :
        //    import . meta

        // The grammar for new expressions is pretty warped. We can have several 'new'
        // keywords following each other, and then a MemberExpression. When we see '('
        // after the MemberExpression, it's associated with the rightmost unassociated
        // 'new' to create a NewExpression with arguments. However, a NewExpression
        // can also occur without arguments.

        // Examples of new expression:
        // new foo.bar().baz means (new (foo.bar)()).baz
        // new foo()() means (new foo())()
        // new new foo()() means (new (new foo())())
        // new new foo means new (new foo)
        // new new foo() means new (new foo())
        // new new foo().bar().baz means (new (new foo()).bar()).baz
        // new super.x means new (super.x)
        // new import.meta.foo means (new (import.meta.foo)())
        Consume(Token.New);
        int new_pos = position();
        TExpression result;

        CheckStackOverflow();

        if (peek() == Token.Import)
        {
            result = ParseImportExpressions();
            if (result.IsImportCallExpression())
            {
                // new import() and new import.source() are never allowed.
                // new import().prop and new import.source().prop are not allowed as well.
                impl().ReportMessageAt(scanner().location(), MessageTemplate.ImportCallNotNewExpression);
                return impl().FailureExpression();
            }
            // import.meta is a valid MemberExpression.
            result = ParseMemberExpressionContinuation(result);
        }
        else if (peek() == Token.Period)
        {
            result = ParseNewTargetExpression();
            return ParseMemberExpressionContinuation(result);
        }
        else
        {
            result = ParseMemberExpression();
            if (result.IsSuperCallReference())
            {
                // new super() is never allowed
                impl().ReportMessageAt(scanner().location(), MessageTemplate.UnexpectedSuper);
                return impl().FailureExpression();
            }
        }
        if (peek() == Token.LeftParen)
        {
            // NewExpression with arguments.
            {
                using TExpressionList args = TExpressionList.New(pointer_buffer());
                ParseArguments(args, out bool has_spread);

                result = factory().NewCallNew(result, args, new_pos, has_spread);
            }
            // The expression can still continue with . or [ after the arguments.
            return ParseMemberExpressionContinuation(result);
        }

        if (peek() == Token.QuestionPeriod)
        {
            impl().ReportMessageAt(scanner().peek_location(), MessageTemplate.OptionalChainingNoNew);
            return impl().FailureExpression();
        }

        // NewExpression without arguments.
        using TExpressionList args2 = TExpressionList.New(pointer_buffer());
        return factory().NewCallNew(result, args2, new_pos, false);
    }

    protected TExpression ParseFunctionExpression()
    {
        Consume(Token.Function);
        int function_token_position = position();

        FunctionKind function_kind = Check(Token.Mul) ? FunctionKind.GeneratorFunction : FunctionKind.NormalFunction;
        TIdentifier name = impl().NullIdentifier();
        bool is_strict_reserved_name = Token.IsStrictReservedWord(peek());
        Scanner.Location function_name_location = Scanner.Location.invalid();
        FunctionSyntaxKind function_syntax_kind = FunctionSyntaxKind.AnonymousExpression;
        if (impl().ParsingDynamicFunctionDeclaration())
        {
            // We don't want dynamic functions to actually declare their name
            // "anonymous". We just want that name in the toString().
            Consume(Token.Identifier);
        }
        else if (peek_any_identifier())
        {
            name = ParseIdentifier(function_kind);
            function_name_location = scanner().location();
            function_syntax_kind = FunctionSyntaxKind.NamedExpression;
        }
        TFunctionLiteral result = impl().ParseFunctionLiteral(
            name, function_name_location,
            is_strict_reserved_name
                ? FunctionNameValidity.kFunctionNameIsStrictReserved
                : FunctionNameValidity.kFunctionNameValidityUnknown,
            function_kind, function_token_position, function_syntax_kind, language_mode(), null);
        // TODO(verwaest): FailureFunctionLiteral?
        if (impl().IsNull(result)) return impl().FailureExpression();
        return result;
    }

    protected TExpression ParseMemberExpression()
    {
        // MemberExpression ::
        //   (PrimaryExpression | FunctionLiteral | ClassLiteral)
        //     ('[' Expression ']' | '.' Identifier | Arguments | TemplateLiteral)*
        //
        // CallExpression ::
        //   (SuperCall | ImportCall)
        //     ('[' Expression ']' | '.' Identifier | Arguments | TemplateLiteral)*
        //
        // The '[' Expression ']' and '.' Identifier parts are parsed by
        // ParseMemberExpressionContinuation, and everything preceeding it is merged
        // into ParsePrimaryExpression.

        // Parse the initial primary or function expression.
        TExpression result = ParsePrimaryExpression();
        return ParseMemberExpressionContinuation(result);
    }

    protected TExpression ParseMemberExpressionContinuation(TExpression expression)
    {
        if (!Token.IsMember(peek())) return expression;
        return DoParseMemberExpressionContinuation(expression);
    }

    protected TExpression ParseImportExpressions()
    {
        // ImportCall[Yield, Await] :
        //   import ( AssignmentExpression[+In, ?Yield, ?Await] )
        //   import . source ( AssignmentExpression[+In, ?Yield, ?Await] )
        //   import . defer ( AssignmentExpression[+In, ?Yield, ?Await] )
        //
        // ImportMeta : import . meta

        Consume(Token.Import);
        int pos = position();

        ModuleImportPhase phase = ModuleImportPhase.kEvaluation;

        // Distinguish import meta and import phase calls.
        if (Check(Token.Period))
        {
            if (v8_flags().js_source_phase_imports &&
                CheckContextualKeyword(ast_value_factory().source_string()))
            {
                phase = ModuleImportPhase.kSource;
            }
            else if (v8_flags().js_defer_import_eval && CheckContextualKeyword(ast_value_factory().defer_string()))
            {
                phase = ModuleImportPhase.kDefer;
            }
            else
            {
                ExpectContextualKeyword(ast_value_factory().meta_string(), "import.meta", pos);
                if (!flags().is_module() && !IsParsingWhileDebugging())
                {
                    impl().ReportMessageAt(scanner().location(), MessageTemplate.ImportMetaOutsideModule);
                    return impl().FailureExpression();
                }
                return impl().ImportMetaExpression(pos);
            }
        }

        if (peek() != Token.LeftParen)
        {
            if (!flags().is_module())
            {
                impl().ReportMessageAt(scanner().location(), MessageTemplate.ImportOutsideModule);
            }
            else
            {
                ReportUnexpectedToken(Next());
            }
            return impl().FailureExpression();
        }

        Consume(Token.LeftParen);
        if (peek() == Token.RightParen)
        {
            impl().ReportMessageAt(scanner().location(), MessageTemplate.ImportMissingSpecifier);
            return impl().FailureExpression();
        }

        using AcceptINScope accept_in = new(this, true);
        TExpression specifier = ParseAssignmentExpressionCoverGrammar();

        // TODO(42204365): Enable import attributes with source phase import once
        // specified.
        if (v8_flags().harmony_import_attributes && phase != ModuleImportPhase.kSource && Check(Token.Comma))
        {
            if (Check(Token.RightParen))
            {
                // A trailing comma allowed after the specifier.
                return factory().NewImportCallExpression(specifier, phase, pos);
            }
            else
            {
                TExpression import_options = ParseAssignmentExpressionCoverGrammar();
                Check(Token.Comma); // A trailing comma is allowed after the import
                                    // attributes.
                Expect(Token.RightParen);
                return factory().NewImportCallExpression(specifier, phase, import_options, pos);
            }
        }

        Expect(Token.RightParen);
        return factory().NewImportCallExpression(specifier, phase, pos);
    }

    protected TExpression ParseSuperExpression()
    {
        Consume(Token.Super);
        int pos = position();

        DeclarationScope scope = GetReceiverScope();
        FunctionKind kind = scope.function_kind();
        if (IsConciseMethod(kind) || IsAccessorFunction(kind) || IsClassConstructor(kind))
        {
            if (Token.IsProperty(peek()))
            {
                if (peek() == Token.Period && PeekAhead() == Token.PrivateName)
                {
                    Consume(Token.Period);
                    Consume(Token.PrivateName);

                    impl().ReportMessage(MessageTemplate.UnexpectedPrivateField);
                    return impl().FailureExpression();
                }
                if (peek() == Token.QuestionPeriod)
                {
                    Consume(Token.QuestionPeriod);
                    impl().ReportMessage(MessageTemplate.OptionalChainingNoSuper);
                    return impl().FailureExpression();
                }
                scope.RecordSuperPropertyUsage();
                UseThis();
                return impl().NewSuperPropertyReference(pos);
            }
            // super() is only allowed in derived constructor. new super() is never
            // allowed; it's reported as an error by
            // ParseMemberWithPresentNewPrefixesExpression.
            if (peek() == Token.LeftParen && IsDerivedConstructor(kind))
            {
                // TODO(rossberg): This might not be the correct FunctionState for the
                // method here.
                expression_scope().RecordThisUse();
                UseThis();
                return impl().NewSuperCallReference(pos);
            }
        }

        impl().ReportMessageAt(scanner().location(), MessageTemplate.UnexpectedSuper);
        return impl().FailureExpression();
    }

    protected TExpression ParseNewTargetExpression()
    {
        int pos = position();
        Consume(Token.Period);
        ExpectContextualKeyword(ast_value_factory().target_string(), "new.target", pos);

        if (!GetReceiverScope().is_function_scope())
        {
            impl().ReportMessageAt(scanner().location(), MessageTemplate.UnexpectedNewTarget);
            return impl().FailureExpression();
        }

        return impl().NewTargetExpression(pos);
    }

    protected TExpression DoParseMemberExpressionContinuation(TExpression expression)
    {
        // Parses this part of MemberExpression:
        // ('[' Expression ']' | '.' Identifier | TemplateLiteral)*
        do
        {
            switch (peek())
            {
                case Token.LeftBracket:
                {
                    Consume(Token.LeftBracket);
                    int pos = position();
                    using AcceptINScope accept_in = new(this, true);
                    TExpression index = ParseExpressionCoverGrammar();
                    expression = factory().NewProperty(expression, index, pos);
                    impl().PushPropertyName(index);
                    Expect(Token.RightBracket);
                    break;
                }
                case Token.Period:
                {
                    Consume(Token.Period);
                    int pos = peek_position();
                    TExpression key = ParsePropertyOrPrivatePropertyName();
                    expression = factory().NewProperty(expression, key, pos);
                    break;
                }
                default:
                {
                    int pos;
                    if (scanner().current_token() == Token.Identifier)
                    {
                        pos = position();
                    }
                    else
                    {
                        pos = peek_position();
                        if (expression.IsFunctionLiteral())
                        {
                            // If the tag function looks like an IIFE, set_parenthesized() to
                            // force eager compilation.
                            impl().SetShouldEagerCompile(expression);
                        }
                    }
                    expression = ParseTemplateLiteral(expression, pos, true);
                    break;
                }
            }
        } while (Token.IsMember(peek()));
        return expression;
    }

    protected void ParseFormalParameter(TFormalParameters parameters)
    {
        // FormalParameter[Yield,GeneratorParameter] :
        //   BindingElement[?Yield, ?GeneratorParameter]
        using FuncNameInferrerState fni_state = new(fni_);
        int pos = peek_position();
        ThreadedList<Declaration>.Iterator declaration_it = scope().declarations().end();
        TExpression pattern = ParseBindingPattern();
        if (impl().IsIdentifier(pattern))
        {
            ClassifyParameter(impl().AsIdentifier(pattern), pos, end_position());
        }
        else
        {
            parameters.is_simple = false;
        }

        TExpression initializer = impl().NullExpression();
        if (Check(Token.Assign))
        {
            parameters.is_simple = false;

            if (parameters.has_rest)
            {
                ReportMessage(MessageTemplate.RestDefaultInitializer);
                return;
            }

            using AcceptINScope accept_in_scope = new(this, true);
            initializer = ParseAssignmentExpression();
            impl().SetFunctionNameFromIdentifierRef(initializer, pattern);
        }

        ThreadedList<Declaration>.Iterator declaration_end = scope().declarations().end();
        int initializer_end = end_position();
        for (; declaration_it != declaration_end; declaration_it.MoveNext())
        {
            Variable var = declaration_it.Current.var();

            // The first time a variable is initialized (i.e. when the initializer
            // position is unset), clear its maybe_assigned flag as it is not a true
            // assignment. Since this is done directly on the Variable objects, it has
            // no effect on VariableProxy objects appearing on the left-hand side of
            // true assignments, so x will be still be marked as maybe_assigned for:
            // (x = 1, y = (x = 2)) => {}
            // and even:
            // (x = (x = 2)) => {}.
            if (var.initializer_position() == kNoSourcePosition)
            {
                var.clear_maybe_assigned();
            }
            var.set_initializer_position(initializer_end);
        }

        impl().AddFormalParameter(parameters, pattern, initializer, end_position(), parameters.has_rest);
    }

    protected void ParseFormalParameterList(TFormalParameters parameters)
    {
        // FormalParameters[Yield] :
        //   [empty]
        //   FunctionRestParameter[?Yield]
        //   FormalParameterList[?Yield]
        //   FormalParameterList[?Yield] ,
        //   FormalParameterList[?Yield] , FunctionRestParameter[?Yield]
        //
        // FormalParameterList[Yield] :
        //   FormalParameter[?Yield]
        //   FormalParameterList[?Yield] , FormalParameter[?Yield]
        using ParameterParsingScope scope = new(this, parameters);

        if (peek() != Token.RightParen)
        {
            while (true)
            {
                parameters.has_rest = Check(Token.Ellipsis);
                ParseFormalParameter(parameters);

                if (parameters.has_rest)
                {
                    parameters.is_simple = false;
                    if (peek() == Token.Comma)
                    {
                        impl().ReportMessageAt(scanner().peek_location(), MessageTemplate.ParamAfterRest);
                        return;
                    }
                    break;
                }
                if (!Check(Token.Comma)) break;
                if (peek() == Token.RightParen)
                {
                    // allow the trailing comma
                    break;
                }
            }
        }

        if (parameters.arity + 1 /* receiver */ > kCodeMaxArguments)
        {
            ReportMessage(MessageTemplate.TooManyParameters);
            return;
        }

        impl().DeclareFormalParameters(parameters);
    }
}
