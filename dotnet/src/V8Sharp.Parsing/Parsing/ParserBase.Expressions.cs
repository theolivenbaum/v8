// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser-base.h: identifiers, primary expressions,
// array and object literals, class members, arguments.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    // Code::kMaxArguments (src/objects/code.h).
    protected const int kCodeMaxArguments = (1 << 16) - 10;

    protected bool ClassifyPropertyIdentifier(Token next, ParsePropertyInfo prop_info)
    {
        // Updates made here must be reflected on ParseAndClassifyIdentifier.
        if (next is >= Token.Identifier and <= Token.Async)
        {
            if (impl().IsArguments(prop_info.name) && scope().ShouldBanArguments())
            {
                ReportMessage(MessageTemplate.ArgumentsDisallowedInInitializerAndStaticBlock);
                return false;
            }
            return true;
        }

        if (!Token.IsValidIdentifier(next, language_mode(), is_generator(), is_await_as_identifier_disallowed()))
        {
            ReportUnexpectedToken(next);
            return false;
        }

        if (next == Token.Await)
        {
            expression_scope().RecordAsyncArrowParametersError(scanner().peek_location(),
                                                               MessageTemplate.AwaitBindingIdentifier);
        }
        return true;
    }

    protected TIdentifier ParseAndClassifyIdentifier(Token next)
    {
        // Updates made here must be reflected on ClassifyPropertyIdentifier.
        if (next is >= Token.Identifier and <= Token.Async)
        {
            TIdentifier name = impl().GetIdentifier();
            if (impl().IsArguments(name) && scope().ShouldBanArguments())
            {
                ReportMessage(MessageTemplate.ArgumentsDisallowedInInitializerAndStaticBlock);
                return impl().EmptyIdentifierString();
            }
            return name;
        }

        if (!Token.IsValidIdentifier(next, language_mode(), is_generator(), is_await_as_identifier_disallowed()))
        {
            ReportUnexpectedToken(next);
            return impl().EmptyIdentifierString();
        }

        if (next == Token.Await)
        {
            expression_scope().RecordAsyncArrowParametersError(scanner().location(),
                                                               MessageTemplate.AwaitBindingIdentifier);
            return impl().GetIdentifier();
        }

        expression_scope().RecordStrictModeParameterError(scanner().location(),
                                                          MessageTemplate.UnexpectedStrictReserved);
        return impl().GetIdentifier();
    }

    // Parses an identifier or a strict mode future reserved word. Allows passing
    // in function_kind for the case of parsing the identifier in a function
    // expression, where the relevant "function_kind" bit is of the function being
    // parsed, not the containing function.
    protected TIdentifier ParseIdentifier(FunctionKind function_kind)
    {
        Token next = Next();

        if (!Token.IsValidIdentifier(next, language_mode(), IsGeneratorFunction(function_kind),
                                     flags().is_module() || IsAwaitAsIdentifierDisallowed(function_kind)))
        {
            ReportUnexpectedToken(next);
            return impl().EmptyIdentifierString();
        }

        return impl().GetIdentifier();
    }

    protected TIdentifier ParseIdentifier() => ParseIdentifier(function_state_.kind());

    // Same as above but additionally disallows 'eval' and 'arguments' in strict
    // mode.
    protected TIdentifier ParseNonRestrictedIdentifier()
    {
        TIdentifier result = ParseIdentifier();

        if (is_strict(language_mode()) && impl().IsEvalOrArguments(result))
        {
            impl().ReportMessageAt(scanner().location(), MessageTemplate.StrictEvalArguments);
        }

        return result;
    }

    // This method should be used to ambiguously parse property names that can
    // become destructuring identifiers.
    protected TIdentifier ParsePropertyName()
    {
        Token next = Next();
        if (Token.IsPropertyName(next))
        {
            if (peek() == Token.Colon) return impl().GetSymbol();
            return impl().GetIdentifier();
        }

        ReportUnexpectedToken(next);
        return impl().EmptyIdentifierString();
    }

    public bool IsExtraordinaryPrivateNameAccessAllowed()
    {
        if (flags().parsing_while_debugging() != ParsingWhileDebugging.Yes && !flags().is_repl_mode())
        {
            return false;
        }
        Scope current_scope = scope();
        while (current_scope != null)
        {
            switch (current_scope.scope_type())
            {
                case ScopeType.CLASS_SCOPE:
                case ScopeType.CATCH_SCOPE:
                case ScopeType.BLOCK_SCOPE:
                case ScopeType.WITH_SCOPE:
                case ScopeType.SHADOW_REALM_SCOPE:
                    return false;
                // Top-level scopes.
                case ScopeType.REPL_MODE_SCOPE:
                case ScopeType.SCRIPT_SCOPE:
                case ScopeType.MODULE_SCOPE:
                    return true;
                // Top-level wrapper function scopes.
                case ScopeType.FUNCTION_SCOPE:
                    return info_id_ == kFunctionLiteralIdTopLevel;
                // Used by debug-evaluate. If the outer scope is top-level,
                // extraordinary private name access is allowed.
                case ScopeType.EVAL_SCOPE:
                    current_scope = current_scope.outer_scope();
                    break;
            }
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    protected TExpression ParsePropertyOrPrivatePropertyName()
    {
        int pos = position();
        TIdentifier name;
        TExpression key;
        Token next = Next();
        if (Token.IsPropertyName(next))
        {
            name = impl().GetSymbol();
            key = factory().NewStringLiteral(name, pos);
        }
        else if (next == Token.PrivateName)
        {
            // In the case of a top level function, we completely skip
            // analysing it's scope, meaning, we don't have a chance to
            // resolve private names and find that they are not enclosed in a
            // class body.
            //
            // Here, we check if this is a new private name reference in a top
            // level function and throw an error if so.
            PrivateNameScopeIterator private_name_scope_iter = new(scope());
            // Parse the identifier so that we can display it in the error message
            name = impl().GetIdentifier();
            // In debug-evaluate, we relax the private name resolution to enable
            // evaluation of obj.#member outside the class bodies in top-level scopes.
            if (private_name_scope_iter.Done() && !IsExtraordinaryPrivateNameAccessAllowed())
            {
                impl().ReportMessageAt(new Scanner.Location(pos, pos + 1),
                                       MessageTemplate.InvalidPrivateFieldResolution,
                                       impl().GetRawNameFromIdentifier(name));
                return impl().FailureExpression();
            }
            key = impl().ExpressionFromPrivateName(ref private_name_scope_iter, name, pos);
        }
        else
        {
            ReportUnexpectedToken(next);
            return impl().FailureExpression();
        }
        impl().PushLiteralName(name);
        return key;
    }

    // RegExp::VerifyFlags: the 'u' and 'v' flags are mutually exclusive.
    protected static bool ValidateRegExpFlags(RegExpFlags flags)
    {
        return !((flags & RegExpFlags.Unicode) != 0 && (flags & RegExpFlags.UnicodeSets) != 0);
    }

    // V8 calls RegExp::VerifySyntax. V8Sharp.Parsing does not reference the
    // regexp engine, so the syntax check goes through the injectable
    // IRegExpSyntaxValidator (ParseInfo); without one every pattern is
    // accepted here and the error surfaces when the literal is compiled.
    protected bool ValidateRegExpLiteral(AstRawString pattern, RegExpFlags flags, out string regexp_error,
                                         out bool is_stack_overflow)
    {
        regexp_error = null;
        is_stack_overflow = false;
        if (this.flags().is_lazy_compile())
        {
            // Lazy compilation implies this has already been validated.
            return true;
        }

        IRegExpSyntaxValidator validator = impl().regexp_syntax_validator();
        if (validator == null) return true;
        return validator.VerifySyntax(pattern.Value, (int)flags, out regexp_error, out is_stack_overflow);
    }

    protected TExpression ParseRegExpLiteral()
    {
        int pos = peek_position();
        if (!scanner().ScanRegExpPattern())
        {
            Next();
            ReportMessage(MessageTemplate.UnterminatedRegExp);
            return impl().FailureExpression();
        }

        AstRawString pattern = GetNextSymbolForRegExpLiteral();
        RegExpFlags? flags = scanner().ScanRegExpFlags();
        AstRawString flags_as_ast_raw_string = GetNextSymbolForRegExpLiteral();
        if (!flags.HasValue || !ValidateRegExpFlags(flags.Value))
        {
            Next();
            ReportMessage(MessageTemplate.MalformedRegExpFlags);
            return impl().FailureExpression();
        }
        Next();
        if (!ValidateRegExpLiteral(pattern, flags.Value, out string regexp_error, out bool is_stack_overflow))
        {
            if (is_stack_overflow) set_stack_overflow();
            ReportMessage(MessageTemplate.MalformedRegExp, pattern, flags_as_ast_raw_string, regexp_error);
            return impl().FailureExpression();
        }
        return factory().NewRegExpLiteral(pattern, (int)flags.Value, pos);
    }

    protected TExpression ParseBindingPattern()
    {
        // Pattern ::
        //   Identifier
        //   ArrayLiteral
        //   ObjectLiteral

        int beg_pos = peek_position();
        Token token = peek();
        TExpression result;

        if (Token.IsAnyIdentifier(token))
        {
            TIdentifier name = ParseAndClassifyIdentifier(Next());
            if (is_strict(language_mode()) && impl().IsEvalOrArguments(name))
            {
                impl().ReportMessageAt(scanner().location(), MessageTemplate.StrictEvalArguments);
                return impl().FailureExpression();
            }
            return impl().ExpressionFromIdentifier(name, beg_pos);
        }

        CheckStackOverflow();

        if (token == Token.LeftBracket)
        {
            result = ParseArrayLiteral();
        }
        else if (token == Token.LeftBrace)
        {
            result = ParseObjectLiteral();
        }
        else
        {
            ReportUnexpectedToken(Next());
            return impl().FailureExpression();
        }

        return result;
    }

    protected TExpression ParsePrimaryExpression()
    {
        CheckStackOverflow();

        // PrimaryExpression ::
        //   'this'
        //   'null'
        //   'true'
        //   'false'
        //   Identifier
        //   Number
        //   String
        //   ArrayLiteral
        //   ObjectLiteral
        //   RegExpLiteral
        //   ClassLiteral
        //   '(' Expression ')'
        //   TemplateLiteral
        //   do Block
        //   AsyncFunctionLiteral

        int beg_pos = peek_position();
        Token token = peek();

        if (Token.IsAnyIdentifier(token))
        {
            Consume(token);

            FunctionKind kind = FunctionKind.ArrowFunction;

            if (token == Token.Async && !scanner().HasLineTerminatorBeforeNext() &&
                !scanner().literal_contains_escapes())
            {
                // async function ...
                if (peek() == Token.Function) return ParseAsyncFunctionLiteral();

                // async Identifier => ...
                if (peek_any_identifier() && PeekAhead() == Token.Arrow)
                {
                    token = Next();
                    beg_pos = position();
                    kind = FunctionKind.AsyncArrowFunction;
                }
            }

            if (peek() == Token.Arrow)
            {
                using ArrowHeadParsingScope parsing_scope = new(impl(), kind, PeekNextInfoId());
                TIdentifier name = ParseAndClassifyIdentifier(token);
                ClassifyParameter(name, beg_pos, end_position());
                TExpression result = impl().ExpressionFromIdentifier(name, beg_pos, InferName.kNo);
                parsing_scope.SetInitializers(0, peek_position());
                next_arrow_function_info_.scope = parsing_scope.ValidateAndCreateScope();
                next_arrow_function_info_.function_literal_id = parsing_scope.function_literal_id();
                next_arrow_function_info_.could_be_immediately_invoked =
                    position_after_last_primary_expression_open_parenthesis_ == beg_pos;
                return result;
            }

            TIdentifier name2 = ParseAndClassifyIdentifier(token);
            return impl().ExpressionFromIdentifier(name2, beg_pos);
        }

        if (Token.IsLiteral(token))
        {
            return impl().ExpressionFromLiteral(Next(), beg_pos);
        }

        switch (token)
        {
            case Token.New:
                return ParseMemberWithPresentNewPrefixesExpression();

            case Token.This:
            {
                Consume(Token.This);
                // Not necessary for this.x, this.x(), this?.x and this?.x() to
                // store the source position for ThisExpression.
                if (peek() == Token.Period || peek() == Token.QuestionPeriod)
                {
                    return impl().ThisExpression();
                }
                return impl().NewThisExpression(beg_pos);
            }

            case Token.AssignDiv:
            case Token.Div:
                return ParseRegExpLiteral();

            case Token.Function:
                return ParseFunctionExpression();

            case Token.Super:
            {
                return ParseSuperExpression();
            }
            case Token.Import:
                return ParseImportExpressions();

            case Token.LeftBracket:
                return ParseArrayLiteral();

            case Token.LeftBrace:
                return ParseObjectLiteral();

            case Token.LeftParen:
            {
                Consume(Token.LeftParen);

                if (Check(Token.RightParen))
                {
                    // clear last next_arrow_function_info tracked strict parameters error.
                    next_arrow_function_info_.ClearStrictParameterError();

                    // ()=>x.  The continuation that consumes the => is in
                    // ParseAssignmentExpressionCoverGrammar.
                    if (peek() != Token.Arrow) ReportUnexpectedToken(Token.RightParen);
                    next_arrow_function_info_.scope = NewFunctionScope(FunctionKind.ArrowFunction);
                    next_arrow_function_info_.function_literal_id = PeekNextInfoId();
                    next_arrow_function_info_.could_be_immediately_invoked =
                        position_after_last_primary_expression_open_parenthesis_ == beg_pos;
                    return factory().NewEmptyParentheses(beg_pos);
                }
                using Scope.Snapshot scope_snapshot = new(scope());
                bool could_be_immediately_invoked_arrow_function =
                    position_after_last_primary_expression_open_parenthesis_ == beg_pos;
                using ArrowHeadParsingScope maybe_arrow = new(impl(), FunctionKind.ArrowFunction, PeekNextInfoId());
                position_after_last_primary_expression_open_parenthesis_ = peek_position();
                // Heuristically try to detect immediately called functions before
                // seeing the call parentheses.
                if (peek() == Token.Function || (peek() == Token.Async && PeekAhead() == Token.Function))
                {
                    function_state_.set_next_function_is_likely_called();
                }
                using AcceptINScope accept_in = new(this, true);
                TExpression expr = ParseExpressionCoverGrammar();
                expr.mark_parenthesized();
                Expect(Token.RightParen);

                if (peek() == Token.Arrow)
                {
                    next_arrow_function_info_.scope = maybe_arrow.ValidateAndCreateScope();
                    next_arrow_function_info_.function_literal_id = maybe_arrow.function_literal_id();
                    next_arrow_function_info_.could_be_immediately_invoked =
                        could_be_immediately_invoked_arrow_function;
                    scope_snapshot.Reparent(next_arrow_function_info_.scope);
                }
                else
                {
                    maybe_arrow.ValidateExpression();
                }

                return expr;
            }

            case Token.Class:
            {
                return ParseClassExpression(scope());
            }

            case Token.TemplateSpan:
            case Token.TemplateTail:
                return ParseTemplateLiteral(impl().NullExpression(), beg_pos, false);

            case Token.Mod:
                if (flags().allow_natives_syntax())
                {
                    return ParseV8Intrinsic();
                }
                break;

            default:
                break;
        }

        ReportUnexpectedToken(Next());
        return impl().FailureExpression();
    }

    // Use when parsing an expression that is known to not be a pattern or part of
    // a pattern.
    protected TExpression ParseExpression()
    {
        using ExpressionParsingScope expression_scope = new(impl());
        using AcceptINScope accept_in = new(this, true);
        TExpression result = ParseExpressionCoverGrammar();
        expression_scope.ValidateExpression();
        return result;
    }

    protected TExpression ParseConditionalChainAssignmentExpression()
    {
        using ExpressionParsingScope expression_scope = new(impl());
        TExpression result = ParseConditionalChainAssignmentExpressionCoverGrammar();
        expression_scope.ValidateExpression();
        return result;
    }

    protected TExpression ParseAssignmentExpression()
    {
        using ExpressionParsingScope expression_scope = new(impl());
        TExpression result = ParseAssignmentExpressionCoverGrammar();
        expression_scope.ValidateExpression();
        return result;
    }

    // These methods do not wrap the parsing of the expression inside a new
    // expression_scope; they use the outer expression_scope instead. They should
    // be used whenever we're parsing something with the "cover" grammar that
    // recognizes both patterns and non-patterns (which roughly corresponds to
    // what's inside the parentheses generated by the symbol
    // "CoverParenthesizedExpressionAndArrowParameterList" in the ES 2017
    // specification).
    protected TExpression ParseExpressionCoverGrammar()
    {
        // Expression ::
        //   AssignmentExpression
        //   Expression ',' AssignmentExpression

        using TExpressionList list = TExpressionList.New(pointer_buffer());
        TExpression expression;
        using AccumulationScope accumulation_scope = new(expression_scope());
        int variable_index = 0;
        while (true)
        {
            if (peek() == Token.Ellipsis)
            {
                return ParseArrowParametersWithRest(list, accumulation_scope, variable_index);
            }

            int expr_pos = peek_position();
            expression = ParseAssignmentExpressionCoverGrammar();

            ClassifyArrowParameter(accumulation_scope, expr_pos, expression);
            list.Add(expression);

            variable_index = expression_scope().SetInitializers(variable_index, peek_position());

            if (!Check(Token.Comma)) break;

            if (peek() == Token.RightParen && PeekAhead() == Token.Arrow)
            {
                // a trailing comma is allowed at the end of an arrow parameter list
                break;
            }

            // Pass on the 'set_next_function_is_likely_called' flag if we have
            // several function literals separated by comma.
            if (peek() == Token.Function && function_state_.previous_function_was_likely_called())
            {
                function_state_.set_next_function_is_likely_called();
            }
        }

        // Return the single element if the list is empty. We need to do this because
        // callers of this function care about the type of the result if there was
        // only a single assignment expression. The preparser would lose this
        // information otherwise.
        if (list.length() == 1) return expression;
        return impl().ExpressionListToExpression(list);
    }

    protected TExpression ParseArrowParametersWithRest(TExpressionList list, AccumulationScope accumulation_scope,
                                                       int seen_variables)
    {
        Consume(Token.Ellipsis);

        Scanner.Location ellipsis = scanner().location();
        int pattern_pos = peek_position();
        TExpression pattern = ParseBindingPattern();
        ClassifyArrowParameter(accumulation_scope, pattern_pos, pattern);

        expression_scope().RecordNonSimpleParameter();

        if (peek() == Token.Assign)
        {
            ReportMessage(MessageTemplate.RestDefaultInitializer);
            return impl().FailureExpression();
        }

        TExpression spread = factory().NewSpread(pattern, ellipsis.beg_pos, pattern_pos);
        if (peek() == Token.Comma)
        {
            ReportMessage(MessageTemplate.ParamAfterRest);
            return impl().FailureExpression();
        }

        expression_scope().SetInitializers(seen_variables, peek_position());

        // 'x, y, ...z' in CoverParenthesizedExpressionAndArrowParameterList only
        // as the formal parameters of'(x, y, ...z) => foo', and is not itself a
        // valid expression.
        if (peek() != Token.RightParen || PeekAhead() != Token.Arrow)
        {
            impl().ReportUnexpectedTokenAt(ellipsis, Token.Ellipsis);
            return impl().FailureExpression();
        }

        list.Add(spread);
        return impl().ExpressionListToExpression(list);
    }

    protected TExpression ParseArrayLiteral()
    {
        // ArrayLiteral ::
        //   '[' Expression? (',' Expression?)* ']'

        int pos = peek_position();
        using TExpressionList values = TExpressionList.New(pointer_buffer());
        int first_spread_index = -1;
        Consume(Token.LeftBracket);

        using AccumulationScope accumulation_scope = new(expression_scope());

        while (!Check(Token.RightBracket))
        {
            TExpression elem;
            if (peek() == Token.Comma)
            {
                elem = factory().NewTheHoleLiteral();
            }
            else if (Check(Token.Ellipsis))
            {
                int start_pos = position();
                int expr_pos = peek_position();
                using AcceptINScope accept_in = new(this, true);
                TExpression argument = ParsePossibleDestructuringSubPattern(accumulation_scope);
                elem = factory().NewSpread(argument, start_pos, expr_pos);

                if (first_spread_index < 0)
                {
                    first_spread_index = values.length();
                }

                if (argument.IsAssignment())
                {
                    expression_scope().RecordPatternError(new Scanner.Location(start_pos, end_position()),
                                                          MessageTemplate.InvalidDestructuringTarget);
                }

                if (peek() == Token.Comma)
                {
                    expression_scope().RecordPatternError(new Scanner.Location(start_pos, end_position()),
                                                          MessageTemplate.ElementAfterRest);
                }
            }
            else
            {
                using AcceptINScope accept_in = new(this, true);
                elem = ParsePossibleDestructuringSubPattern(accumulation_scope);
            }
            values.Add(elem);
            if (peek() != Token.RightBracket)
            {
                Expect(Token.Comma);
                if (elem.IsFailureExpression()) return elem;
            }
        }

        return factory().NewArrayLiteral(values, first_spread_index, pos);
    }

    protected TExpression ParseProperty(ParsePropertyInfo prop_info)
    {
        if (Check(Token.Async))
        {
            Token token = peek();
            if ((token != Token.Mul && prop_info.ParsePropertyKindFromToken(token)) ||
                scanner().HasLineTerminatorBeforeNext())
            {
                prop_info.name = impl().GetIdentifier();
                impl().PushLiteralName(prop_info.name);
                return factory().NewStringLiteral(prop_info.name, position());
            }
            if (scanner().literal_contains_escapes())
            {
                impl().ReportUnexpectedToken(Token.EscapedKeyword);
            }
            prop_info.function_flags = ParseFunctionFlags.kIsAsync;
            prop_info.kind = ParsePropertyKind.kMethod;
        }

        if (Check(Token.Mul))
        {
            prop_info.function_flags |= ParseFunctionFlags.kIsGenerator;
            prop_info.kind = ParsePropertyKind.kMethod;
        }

        if (prop_info.kind == ParsePropertyKind.kNotSet && peek() is >= Token.Get and <= Token.Set)
        {
            Token token = Next();
            if (prop_info.ParsePropertyKindFromToken(peek()) || scanner().literal_contains_escapes())
            {
                prop_info.name = impl().GetIdentifier();
                impl().PushLiteralName(prop_info.name);
                return factory().NewStringLiteral(prop_info.name, position());
            }
            if (token == Token.Get)
            {
                prop_info.kind = ParsePropertyKind.kAccessorGetter;
            }
            else if (token == Token.Set)
            {
                prop_info.kind = ParsePropertyKind.kAccessorSetter;
            }
        }

        int pos = peek_position();

        // For non computed property names we normalize the name a bit:
        //
        //   "12" -> 12
        //   12.3 -> "12.3"
        //   12.30 -> "12.3"
        //   identifier -> "identifier"
        //
        // This is important because we use the property name as a key in a hash
        // table when we compute constant properties.
        bool is_array_index;
        uint index = 0;
        switch (peek())
        {
            case Token.PrivateName:
                prop_info.is_private = true;
                is_array_index = false;
                Consume(Token.PrivateName);
                if (prop_info.kind == ParsePropertyKind.kNotSet)
                {
                    prop_info.ParsePropertyKindFromToken(peek());
                }
                prop_info.name = impl().GetIdentifier();
                if (prop_info.position == PropertyPosition.kObjectLiteral)
                {
                    ReportUnexpectedToken(Token.PrivateName);
                    prop_info.kind = ParsePropertyKind.kNotSet;
                    return impl().FailureExpression();
                }
                break;

            case Token.String:
                Consume(Token.String);
                prop_info.name = peek() == Token.Colon ? impl().GetSymbol() : impl().GetIdentifier();
                is_array_index = impl().IsArrayIndex(prop_info.name, out index);
                break;

            case Token.Smi:
                Consume(Token.Smi);
                index = scanner().smi_value();
                is_array_index = true;
                // Token::kSmi were scanned from their canonical representation.
                prop_info.name = impl().GetSymbol();
                break;

            case Token.Number:
            {
                Consume(Token.Number);
                prop_info.name = impl().GetNumberAsSymbol();
                is_array_index = impl().IsArrayIndex(prop_info.name, out index);
                break;
            }

            case Token.BigInt:
            {
                Consume(Token.BigInt);
                prop_info.name = impl().GetBigIntAsSymbol();
                is_array_index = impl().IsArrayIndex(prop_info.name, out index);
                break;
            }

            case Token.LeftBracket:
            {
                prop_info.name = impl().NullIdentifier();
                prop_info.is_computed_name = true;
                Consume(Token.LeftBracket);
                using AcceptINScope accept_in = new(this, true);
                prop_info.allow_reindex_scope = new AllowReindexScope(max_drift_);
                TExpression expression = ParseAssignmentExpression();
                Expect(Token.RightBracket);
                if (prop_info.kind == ParsePropertyKind.kNotSet)
                {
                    prop_info.ParsePropertyKindFromToken(peek());
                }
                return expression;
            }

            case Token.Ellipsis:
                if (prop_info.kind == ParsePropertyKind.kNotSet)
                {
                    prop_info.name = impl().NullIdentifier();
                    Consume(Token.Ellipsis);
                    using AcceptINScope accept_in = new(this, true);
                    int start_pos = peek_position();
                    TExpression expression = ParsePossibleDestructuringSubPattern(prop_info.accumulation_scope);
                    prop_info.kind = ParsePropertyKind.kSpread;

                    if (!IsValidReferenceExpression(expression))
                    {
                        expression_scope().RecordDeclarationError(new Scanner.Location(start_pos, end_position()),
                                                                  MessageTemplate.InvalidRestBindingPattern);
                        expression_scope().RecordPatternError(new Scanner.Location(start_pos, end_position()),
                                                              MessageTemplate.InvalidRestAssignmentPattern);
                    }

                    if (peek() != Token.RightBrace)
                    {
                        expression_scope().RecordPatternError(scanner().location(),
                                                              MessageTemplate.ElementAfterRest);
                    }
                    return expression;
                }
                goto default;

            default:
                prop_info.name = ParsePropertyName();
                is_array_index = false;
                break;
        }

        if (prop_info.kind == ParsePropertyKind.kNotSet)
        {
            prop_info.ParsePropertyKindFromToken(peek());
        }
        impl().PushLiteralName(prop_info.name);
        return is_array_index
            ? factory().NewNumberLiteral(index, pos)
            : factory().NewStringLiteral(prop_info.name, pos);
    }

    protected bool VerifyCanHaveAutoAccessorOrThrow(ParsePropertyInfo prop_info, TExpression name_expression,
                                                    int name_token_position)
    {
        switch (prop_info.kind)
        {
            case ParsePropertyKind.kAssign:
            case ParsePropertyKind.kClassField:
            case ParsePropertyKind.kShorthandOrClassField:
            case ParsePropertyKind.kNotSet:
                prop_info.kind = ParsePropertyKind.kAutoAccessorClassField;
                return true;
            default:
                impl().ReportUnexpectedTokenAt(
                    new Scanner.Location(name_token_position,
                                         name_expression.position() < name_token_position
                                             ? name_token_position
                                             : name_expression.position()),
                    Token.Accessor);
                return false;
        }
    }

    protected bool ParseCurrentSymbolAsClassFieldOrMethod(ParsePropertyInfo prop_info, ref TExpression name_expression)
    {
        if (peek() == Token.LeftParen)
        {
            prop_info.kind = ParsePropertyKind.kMethod;
            prop_info.name = impl().GetIdentifier();
            name_expression = factory().NewStringLiteral(prop_info.name, position());
            return true;
        }
        if (peek() == Token.Assign || peek() == Token.Semicolon || peek() == Token.RightBrace)
        {
            prop_info.name = impl().GetIdentifier();
            name_expression = factory().NewStringLiteral(prop_info.name, position());
            return true;
        }
        return false;
    }

    protected bool ParseAccessorPropertyOrAutoAccessors(ParsePropertyInfo prop_info, ref TExpression name_expression,
                                                        ref int name_token_position)
    {
        // accessor [no LineTerminator here] ClassElementName[?Yield, ?Await]
        // Initializer[~In, ?Yield, ?Await]opt ;
        Consume(Token.Accessor);
        name_token_position = scanner().peek_location().beg_pos;
        // If there is a line terminator here, it cannot be an auto-accessor.
        if (scanner().HasLineTerminatorBeforeNext())
        {
            prop_info.kind = ParsePropertyKind.kClassField;
            prop_info.name = impl().GetIdentifier();
            name_expression = factory().NewStringLiteral(prop_info.name, position());
            return true;
        }
        if (ParseCurrentSymbolAsClassFieldOrMethod(prop_info, ref name_expression))
        {
            return true;
        }
        name_expression = ParseProperty(prop_info);
        return VerifyCanHaveAutoAccessorOrThrow(prop_info, name_expression, name_token_position);
    }

    protected TClassLiteralProperty ParseClassPropertyDefinition(ClassInfo class_info, ParsePropertyInfo prop_info,
                                                                 bool has_extends)
    {
        int next_info_id = PeekNextInfoId();

        Token name_token = peek();
        int property_beg_pos = peek_position();
        int name_token_position = property_beg_pos;
        TExpression name_expression = default;
        if (name_token == Token.Static)
        {
            Consume(Token.Static);
            name_token_position = scanner().peek_location().beg_pos;
            if (!ParseCurrentSymbolAsClassFieldOrMethod(prop_info, ref name_expression))
            {
                prop_info.is_static = true;
                if (v8_flags().js_decorators && peek() == Token.Accessor)
                {
                    if (!ParseAccessorPropertyOrAutoAccessors(prop_info, ref name_expression,
                                                              ref name_token_position))
                    {
                        return default;
                    }
                }
                else
                {
                    name_expression = ParseProperty(prop_info);
                }
            }
        }
        else if (v8_flags().js_decorators && name_token == Token.Accessor)
        {
            if (!ParseAccessorPropertyOrAutoAccessors(prop_info, ref name_expression, ref name_token_position))
            {
                return default;
            }
        }
        else
        {
            name_expression = ParseProperty(prop_info);
        }

        switch (prop_info.kind)
        {
            case ParsePropertyKind.kAssign:
            case ParsePropertyKind.kAutoAccessorClassField:
            case ParsePropertyKind.kClassField:
            case ParsePropertyKind.kShorthandOrClassField:
            case ParsePropertyKind.kNotSet:
            {
                // This case is a name followed by a
                // name or other property. Here we have
                // to assume that's an uninitialized
                // field followed by a linebreak
                // followed by a property, with ASI
                // adding the semicolon. If not, there
                // will be a syntax error after parsing
                // the first name as an uninitialized
                // field.
                if (prop_info.is_computed_name)
                {
                    if (!has_error() && next_info_id != PeekNextInfoId() &&
                        !(prop_info.is_static
                            ? class_info.has_static_elements()
                            : class_info.has_instance_members()))
                    {
                        impl().ReindexComputedMemberName(name_expression, prop_info.allow_reindex_scope.Value);
                    }
                }
                else
                {
                    CheckClassFieldName(prop_info.name, prop_info.is_static);
                }

                TExpression value = ParseMemberInitializer(class_info, property_beg_pos, next_info_id,
                                                           prop_info.is_static);
                ExpectSemicolon();

                TClassLiteralProperty result;
                if (prop_info.kind == ParsePropertyKind.kAutoAccessorClassField)
                {
                    // Declare the auto-accessor synthetic getter and setter here where we
                    // have access to the property position in parsing and preparsing.
                    result = impl().NewClassLiteralPropertyWithAccessorInfo(
                        scope().AsClassScope(), class_info, prop_info.name, name_expression, value,
                        prop_info.is_static, prop_info.is_computed_name, prop_info.is_private, property_beg_pos);
                }
                else
                {
                    prop_info.kind = ParsePropertyKind.kClassField;
                    result = factory().NewClassLiteralProperty(name_expression, value,
                                                               ClassLiteralProperty.Kind.FIELD, prop_info.is_static,
                                                               prop_info.is_computed_name, prop_info.is_private);
                }
                impl().SetFunctionNameFromClassPropertyName(result, prop_info.name);

                return result;
            }
            case ParsePropertyKind.kMethod:
            {
                // MethodDefinition
                //    PropertyName '(' StrictFormalParameters ')' '{' FunctionBody '}'
                //    '*' PropertyName '(' StrictFormalParameters ')' '{' FunctionBody '}'
                //    async PropertyName '(' StrictFormalParameters ')'
                //        '{' FunctionBody '}'
                //    async '*' PropertyName '(' StrictFormalParameters ')'
                //        '{' FunctionBody '}'

                if (!prop_info.is_computed_name)
                {
                    CheckClassMethodName(prop_info.name, ParsePropertyKind.kMethod, prop_info.function_flags,
                                         prop_info.is_static, ref class_info.has_seen_constructor);
                }

                FunctionKind kind = MethodKindFor(prop_info.is_static, prop_info.function_flags);

                if (!prop_info.is_static && impl().IsConstructor(prop_info.name))
                {
                    class_info.has_seen_constructor = true;
                    kind = has_extends ? FunctionKind.DerivedConstructor : FunctionKind.BaseConstructor;
                }

                TExpression value = impl().ParseFunctionLiteral(
                    prop_info.name, scanner().location(), FunctionNameValidity.kSkipFunctionNameCheck, kind,
                    name_token_position, FunctionSyntaxKind.AccessorOrMethod, language_mode(), null);

                TClassLiteralProperty result = factory().NewClassLiteralProperty(
                    name_expression, value, ClassLiteralProperty.Kind.METHOD, prop_info.is_static,
                    prop_info.is_computed_name, prop_info.is_private);
                impl().SetFunctionNameFromClassPropertyName(result, prop_info.name);
                return result;
            }

            case ParsePropertyKind.kAccessorGetter:
            case ParsePropertyKind.kAccessorSetter:
            {
                bool is_get = prop_info.kind == ParsePropertyKind.kAccessorGetter;

                if (!prop_info.is_computed_name)
                {
                    CheckClassMethodName(prop_info.name, prop_info.kind, ParseFunctionFlags.kIsNormal,
                                         prop_info.is_static, ref class_info.has_seen_constructor);
                    // Make sure the name expression is a string since we need a Name for
                    // Runtime_DefineAccessorPropertyUnchecked and since we can determine
                    // this statically we can skip the extra runtime check.
                    name_expression = factory().NewStringLiteral(prop_info.name, name_expression.position());
                }

                FunctionKind kind;
                if (prop_info.is_static)
                {
                    kind = is_get ? FunctionKind.StaticGetterFunction : FunctionKind.StaticSetterFunction;
                }
                else
                {
                    kind = is_get ? FunctionKind.GetterFunction : FunctionKind.SetterFunction;
                }

                TFunctionLiteral value = impl().ParseFunctionLiteral(
                    prop_info.name, scanner().location(), FunctionNameValidity.kSkipFunctionNameCheck, kind,
                    name_token_position, FunctionSyntaxKind.AccessorOrMethod, language_mode(), null);

                ClassLiteralProperty.Kind property_kind =
                    is_get ? ClassLiteralProperty.Kind.GETTER : ClassLiteralProperty.Kind.SETTER;
                TClassLiteralProperty result = factory().NewClassLiteralProperty(
                    name_expression, value, property_kind, prop_info.is_static, prop_info.is_computed_name,
                    prop_info.is_private);
                AstRawString prefix = is_get
                    ? ast_value_factory().get_space_string()
                    : ast_value_factory().set_space_string();
                impl().SetFunctionNameFromClassPropertyName(result, prop_info.name, prefix);
                return result;
            }
            case ParsePropertyKind.kValue:
            case ParsePropertyKind.kShorthand:
            case ParsePropertyKind.kSpread:
                impl().ReportUnexpectedTokenAt(
                    new Scanner.Location(name_token_position,
                                         name_expression.position() < name_token_position
                                             ? name_token_position
                                             : name_expression.position()),
                    name_token);
                return default;
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    protected TExpression ParseMemberInitializer(ClassInfo class_info, int beg_pos, int info_id, bool is_static)
    {
        using FunctionParsingScope body_parsing_scope = new(this);
        DeclarationScope initializer_scope = is_static
            ? class_info.EnsureStaticElementsScope(this, beg_pos, info_id)
            : class_info.EnsureInstanceMembersScope(this, beg_pos, info_id);

        if (Check(Token.Assign))
        {
            using FunctionState initializer_state = new(this, initializer_scope);

            using AcceptINScope accept_in = new(this, true);
            TExpression result = ParseAssignmentExpression();
            initializer_scope.set_end_position(end_position());
            return result;
        }
        initializer_scope.set_end_position(end_position());
        return factory().NewUndefinedLiteral(kNoSourcePosition);
    }

    protected TBlock ParseClassStaticBlock(ClassInfo class_info)
    {
        Consume(Token.Static);

        DeclarationScope initializer_scope =
            class_info.EnsureStaticElementsScope(this, position(), PeekNextInfoId());

        using FunctionState initializer_state = new(this, initializer_scope);
        using FunctionParsingScope body_parsing_scope = new(this);
        using AcceptINScope accept_in = new(this, true);

        // Each static block has its own var and lexical scope, so make a new var
        // block scope instead of using the synthetic members initializer function
        // scope.
        DeclarationScope static_block_var_scope = NewVarblockScope();
        TBlock static_block = ParseBlock(null, static_block_var_scope);
        CheckConflictingVarDeclarations(static_block_var_scope);
        initializer_scope.set_end_position(end_position());
        return static_block;
    }

    protected TObjectLiteralProperty ParseObjectPropertyDefinition(ParsePropertyInfo prop_info,
                                                                   ref bool has_seen_proto)
    {
        Token name_token = peek();
        Scanner.Location next_loc = scanner().peek_location();

        TExpression name_expression = ParseProperty(prop_info);

        TIdentifier name = prop_info.name;
        ParseFunctionFlags function_flags = prop_info.function_flags;

        switch (prop_info.kind)
        {
            case ParsePropertyKind.kSpread:
                prop_info.is_computed_name = true;
                prop_info.is_rest = true;

                return factory().NewObjectLiteralProperty(factory().NewTheHoleLiteral(), name_expression,
                                                          ObjectLiteralProperty.Kind.SPREAD, true);

            case ParsePropertyKind.kValue:
            {
                if (!prop_info.is_computed_name && scanner().CurrentLiteralEquals("__proto__"))
                {
                    if (has_seen_proto)
                    {
                        expression_scope().RecordExpressionError(scanner().location(),
                                                                 MessageTemplate.DuplicateProto);
                    }
                    has_seen_proto = true;
                }
                Consume(Token.Colon);
                using AcceptINScope accept_in = new(this, true);
                TExpression value = ParsePossibleDestructuringSubPattern(prop_info.accumulation_scope);

                TObjectLiteralProperty result =
                    factory().NewObjectLiteralProperty(name_expression, value, prop_info.is_computed_name);
                impl().SetFunctionNameFromPropertyName(result, name);
                return result;
            }

            case ParsePropertyKind.kAssign:
            case ParsePropertyKind.kShorthandOrClassField:
            case ParsePropertyKind.kShorthand:
            {
                // PropertyDefinition
                //    IdentifierReference
                //    CoverInitializedName
                //
                // CoverInitializedName
                //    IdentifierReference Initializer?
                if (!ClassifyPropertyIdentifier(name_token, prop_info))
                {
                    return default;
                }

                TExpression lhs = impl().ExpressionFromIdentifier(name, next_loc.beg_pos);
                if (!IsAssignableIdentifier(lhs))
                {
                    expression_scope().RecordPatternError(next_loc, MessageTemplate.StrictEvalArguments);
                }

                TExpression value;
                if (peek() == Token.Assign)
                {
                    Consume(Token.Assign);
                    {
                        using AcceptINScope accept_in = new(this, true);
                        TExpression rhs = ParseAssignmentExpression();
                        value = factory().NewAssignment(Token.Assign, lhs, rhs, kNoSourcePosition);
                        impl().SetFunctionNameFromIdentifierRef(rhs, lhs);
                    }
                    expression_scope().RecordExpressionError(new Scanner.Location(next_loc.beg_pos, end_position()),
                                                             MessageTemplate.InvalidCoverInitializedName);
                }
                else
                {
                    value = lhs;
                }

                TObjectLiteralProperty result = factory().NewObjectLiteralProperty(
                    name_expression, value, ObjectLiteralProperty.Kind.COMPUTED, false);
                impl().SetFunctionNameFromPropertyName(result, name);
                return result;
            }

            case ParsePropertyKind.kMethod:
            {
                // MethodDefinition
                //    PropertyName '(' StrictFormalParameters ')' '{' FunctionBody '}'
                //    '*' PropertyName '(' StrictFormalParameters ')' '{' FunctionBody '}'

                expression_scope().RecordPatternError(new Scanner.Location(next_loc.beg_pos, end_position()),
                                                      MessageTemplate.InvalidDestructuringTarget);

                using BlockState block_state = object_literal_scope_ != null
                    ? new BlockState(this, object_literal_scope_)
                    : default;
                const bool kIsStatic = false;
                FunctionKind kind = MethodKindFor(kIsStatic, function_flags);

                TExpression value = impl().ParseFunctionLiteral(
                    name, scanner().location(), FunctionNameValidity.kSkipFunctionNameCheck, kind,
                    next_loc.beg_pos, FunctionSyntaxKind.AccessorOrMethod, language_mode(), null);

                TObjectLiteralProperty result = factory().NewObjectLiteralProperty(
                    name_expression, value, ObjectLiteralProperty.Kind.COMPUTED, prop_info.is_computed_name);
                impl().SetFunctionNameFromPropertyName(result, name);
                return result;
            }

            case ParsePropertyKind.kAccessorGetter:
            case ParsePropertyKind.kAccessorSetter:
            {
                bool is_get = prop_info.kind == ParsePropertyKind.kAccessorGetter;

                expression_scope().RecordPatternError(new Scanner.Location(next_loc.beg_pos, end_position()),
                                                      MessageTemplate.InvalidDestructuringTarget);

                if (!prop_info.is_computed_name)
                {
                    // Make sure the name expression is a string since we need a Name for
                    // Runtime_DefineAccessorPropertyUnchecked and since we can determine
                    // this statically we can skip the extra runtime check.
                    name_expression = factory().NewStringLiteral(name, name_expression.position());
                }

                using BlockState block_state = object_literal_scope_ != null
                    ? new BlockState(this, object_literal_scope_)
                    : default;

                FunctionKind kind = is_get ? FunctionKind.GetterFunction : FunctionKind.SetterFunction;

                TFunctionLiteral value = impl().ParseFunctionLiteral(
                    name, scanner().location(), FunctionNameValidity.kSkipFunctionNameCheck, kind,
                    next_loc.beg_pos, FunctionSyntaxKind.AccessorOrMethod, language_mode(), null);

                TObjectLiteralProperty result = factory().NewObjectLiteralProperty(
                    name_expression, value,
                    is_get ? ObjectLiteralProperty.Kind.GETTER : ObjectLiteralProperty.Kind.SETTER,
                    prop_info.is_computed_name);
                AstRawString prefix = is_get
                    ? ast_value_factory().get_space_string()
                    : ast_value_factory().set_space_string();
                impl().SetFunctionNameFromPropertyName(result, name, prefix);
                return result;
            }

            case ParsePropertyKind.kAutoAccessorClassField:
            case ParsePropertyKind.kClassField:
            case ParsePropertyKind.kNotSet:
                ReportUnexpectedToken(Next());
                return default;
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    protected TExpression ParseObjectLiteral()
    {
        // ObjectLiteral ::
        // '{' (PropertyDefinition (',' PropertyDefinition)* ','? )? '}'

        int pos = peek_position();
        using TObjectPropertyList properties = TObjectPropertyList.New(pointer_buffer());
        int number_of_boilerplate_properties = 0;

        bool has_computed_names = false;
        bool has_rest_property = false;
        bool has_seen_proto = false;

        Consume(Token.LeftBrace);
        using AccumulationScope accumulation_scope = new(expression_scope());

        // If methods appear inside the object literal, we'll enter this scope.
        Scope block_scope = NewBlockScopeForObjectLiteral();
        block_scope.set_start_position(pos);
        using BlockState object_literal_scope_state = new(this, block_scope, object_literal_stack: true);

        while (!Check(Token.RightBrace))
        {
            using FuncNameInferrerState fni_state = new(fni_);

            using ParsePropertyInfo prop_info = new(this, accumulation_scope);
            prop_info.position = PropertyPosition.kObjectLiteral;
            TObjectLiteralProperty property = ParseObjectPropertyDefinition(prop_info, ref has_seen_proto);
            if (impl().IsNullProperty(property)) return impl().FailureExpression();

            if (prop_info.is_computed_name)
            {
                has_computed_names = true;
            }

            if (prop_info.is_rest)
            {
                has_rest_property = true;
            }

            if (impl().IsBoilerplateProperty(property) && !has_computed_names)
            {
                // Count CONSTANT or COMPUTED properties to maintain the enumeration
                // order.
                number_of_boilerplate_properties++;
            }

            properties.Add(property);

            if (peek() != Token.RightBrace)
            {
                Expect(Token.Comma);
            }

            fni_.Infer();
        }

        Variable home_object = null;
        if (block_scope.needs_home_object())
        {
            home_object = block_scope.DeclareHomeObjectVariable(ast_value_factory());
            block_scope.set_end_position(end_position());
        }
        else
        {
            block_scope = block_scope.FinalizeBlockScope();
        }

        // In pattern rewriter, we rewrite rest property to call out to a
        // runtime function passing all the other properties as arguments to
        // this runtime function. Here, we make sure that the number of
        // properties is less than number of arguments allowed for a runtime
        // call.
        if (has_rest_property && properties.length() > kCodeMaxArguments)
        {
            expression_scope().RecordPatternError(new Scanner.Location(pos, position()),
                                                  MessageTemplate.TooManyArguments);
        }

        return impl().InitializeObjectLiteral(factory().NewObjectLiteral(
            properties, number_of_boilerplate_properties, pos, has_rest_property, home_object));
    }

    protected void ParseArguments(TExpressionList args, out bool has_spread,
                                  ParsingArrowHeadFlag maybe_arrow = ParsingArrowHeadFlag.kCertainlyNotArrowHead)
    {
        // Arguments ::
        //   '(' (AssignmentExpression)*[','] ')'

        has_spread = false;
        Consume(Token.LeftParen);
        using AccumulationScope accumulation_scope = new(expression_scope());

        int variable_index = 0;
        while (peek() != Token.RightParen)
        {
            int start_pos = peek_position();
            bool is_spread = Check(Token.Ellipsis);
            int expr_pos = peek_position();

            using AcceptINScope accept_in = new(this, true);
            TExpression argument = ParseAssignmentExpressionCoverGrammar();

            if (maybe_arrow == ParsingArrowHeadFlag.kMaybeArrowHead)
            {
                ClassifyArrowParameter(accumulation_scope, expr_pos, argument);
                if (is_spread)
                {
                    expression_scope().RecordNonSimpleParameter();
                    if (argument.IsAssignment())
                    {
                        expression_scope().RecordAsyncArrowParametersError(scanner().location(),
                                                                           MessageTemplate.RestDefaultInitializer);
                    }
                    if (peek() == Token.Comma)
                    {
                        expression_scope().RecordAsyncArrowParametersError(scanner().peek_location(),
                                                                           MessageTemplate.ParamAfterRest);
                    }
                }
            }
            if (is_spread)
            {
                has_spread = true;
                argument = factory().NewSpread(argument, start_pos, expr_pos);
            }
            args.Add(argument);

            variable_index = expression_scope().SetInitializers(variable_index, peek_position());

            if (!Check(Token.Comma)) break;
        }

        if (args.length() + 1 /* receiver */ > kCodeMaxArguments)
        {
            ReportMessage(MessageTemplate.TooManyArguments);
            return;
        }

        Scanner.Location location = scanner_.location();
        if (!Check(Token.RightParen))
        {
            impl().ReportMessageAt(location, MessageTemplate.UnterminatedArgList);
        }
    }
}
