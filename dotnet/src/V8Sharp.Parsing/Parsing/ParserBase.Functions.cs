// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser-base.h: variable, function and class
// declarations, function bodies, arrow functions, class literals, template
// literals, reference validation.
//
// V8's RCS_SCOPE and --log-function-events timing around arrow functions is
// not ported (no runtime call stats or file logger in V8Sharp.Parsing).

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    protected void ParseVariableDeclarations(VariableDeclarationContext var_context,
                                             DeclarationParsingResult parsing_result, List<AstRawString> names)
    {
        // VariableDeclarations ::
        //   ('var' | 'const' | 'let' | 'using' | 'await using') (Identifier ('='
        //   AssignmentExpression)?)+[',']
        //
        // ES6:
        // FIXME(marja, nikolaos): Add an up-to-date comment about ES6 variable
        // declaration syntax.

        parsing_result.descriptor.kind = VariableKind.NORMAL_VARIABLE;
        parsing_result.descriptor.declaration_pos = peek_position();
        parsing_result.descriptor.initialization_pos = peek_position();

        Scope target_scope = scope();

        switch (peek())
        {
            case Token.Var:
                parsing_result.descriptor.mode = VariableMode.Var;
                target_scope = scope().GetDeclarationScope();
                Consume(Token.Var);
                break;
            case Token.Const:
                Consume(Token.Const);
                parsing_result.descriptor.mode = VariableMode.Const;
                break;
            case Token.Let:
                Consume(Token.Let);
                parsing_result.descriptor.mode = VariableMode.Let;
                break;
            case Token.Using:
                // using [no LineTerminator here] BindingList[?In, ?Yield, ?Await,
                // ~Pattern] ;
                Consume(Token.Using);
                impl().CountUsage(UseCounterFeature.kExplicitResourceManagement);
                parsing_result.descriptor.mode = VariableMode.Using;
                break;
            case Token.Await:
                // CoverAwaitExpressionAndAwaitUsingDeclarationHead[?Yield] [no
                // LineTerminator here] BindingList[?In, ?Yield, +Await, ~Pattern];
                Consume(Token.Await);
                Consume(Token.Using);
                impl().CountUsage(UseCounterFeature.kExplicitResourceManagement);
                parsing_result.descriptor.mode = VariableMode.AwaitUsing;
                if (!target_scope.has_await_using_declaration())
                {
                    function_state_.AddSuspend();
                }
                break;
            default:
                throw new InvalidOperationException("UNREACHABLE"); // by current callers
        }

        using VariableDeclarationParsingScope declaration = new(impl(), parsing_result.descriptor.mode, names);

        ThreadedList<Declaration>.Iterator declaration_it = target_scope.declarations().end();

        int bindings_start = peek_position();
        do
        {
            // Parse binding pattern.
            using FuncNameInferrerState fni_state = new(fni_);

            int decl_pos = peek_position();

            TIdentifier name;
            TExpression pattern;
            // Check for an identifier first, so that we can elide the pattern in cases
            // where there is no initializer (and so no proxy needs to be created).
            if (Token.IsAnyIdentifier(peek()))
            {
                name = ParseAndClassifyIdentifier(Next());
                if (is_strict(language_mode()) && impl().IsEvalOrArguments(name))
                {
                    impl().ReportMessageAt(scanner().location(), MessageTemplate.StrictEvalArguments);
                    return;
                }
                if (peek() == Token.Assign || (var_context == VariableDeclarationContext.kForStatement && PeekInOrOf()) ||
                    parsing_result.descriptor.mode == VariableMode.Let)
                {
                    // Assignments need the variable expression for the assignment LHS, and
                    // for of/in will need it later, so create the expression now.
                    pattern = impl().ExpressionFromIdentifier(name, decl_pos);
                }
                else
                {
                    // Otherwise, elide the variable expression and just declare it.
                    impl().DeclareIdentifier(name, decl_pos);
                    pattern = impl().NullExpression();
                }
            }
            else if (parsing_result.descriptor.mode != VariableMode.Using &&
                     parsing_result.descriptor.mode != VariableMode.AwaitUsing)
            {
                name = impl().NullIdentifier();
                pattern = ParseBindingPattern();
            }
            else
            {
                // `using` declarations should have an identifier.
                impl().ReportMessageAt(scanner_.peek_location(), MessageTemplate.DeclarationMissingInitializer,
                                       "using");
                return;
            }

            Scanner.Location variable_loc = scanner().location();

            TExpression value = impl().NullExpression();
            int value_beg_pos = kNoSourcePosition;
            if (Check(Token.Assign))
            {
                {
                    value_beg_pos = peek_position();
                    using AcceptINScope accept_in = new(this, var_context != VariableDeclarationContext.kForStatement);
                    value = ParseAssignmentExpression();
                }
                variable_loc.end_pos = end_position();

                if (!parsing_result.first_initializer_loc.IsValid())
                {
                    parsing_result.first_initializer_loc = variable_loc;
                }

                // Don't infer if it is "a = function(){...}();"-like expression.
                if (impl().IsIdentifier(pattern))
                {
                    if (!value.IsCall() && !value.IsCallNew())
                    {
                        fni_.Infer();
                    }
                    else
                    {
                        fni_.RemoveLastFunction();
                    }
                }

                impl().SetFunctionNameFromIdentifierRef(value, pattern);
            }
            else
            {
                if (var_context != VariableDeclarationContext.kForStatement || !PeekInOrOf())
                {
                    // ES6 'const' and binding patterns require initializers.
                    if (IsImmutableLexicalVariableMode(parsing_result.descriptor.mode) || impl().IsNull(name))
                    {
                        impl().ReportMessageAt(
                            new Scanner.Location(decl_pos, end_position()),
                            MessageTemplate.DeclarationMissingInitializer,
                            impl().IsNull(name)
                                ? "destructuring"
                                : ImmutableLexicalVariableModeToString(parsing_result.descriptor.mode));
                        return;
                    }
                    // 'let x' initializes 'x' to undefined.
                    if (parsing_result.descriptor.mode == VariableMode.Let)
                    {
                        value = factory().NewUndefinedLiteral(position());
                    }
                }
            }

            int initializer_position = end_position();
            ThreadedList<Declaration>.Iterator declaration_end = target_scope.declarations().end();
            for (; declaration_it != declaration_end; declaration_it.MoveNext())
            {
                declaration_it.Current.var().set_initializer_position(initializer_position);
            }

            // Patterns should be elided iff. they don't have an initializer.
            DeclarationParsingResult.Declaration decl = new(pattern, value);
            decl.value_beg_pos = value_beg_pos;

            parsing_result.declarations.Add(decl);
        } while (Check(Token.Comma));

        parsing_result.bindings_loc = new Scanner.Location(bindings_start, end_position());
    }

    protected TStatement ParseFunctionDeclaration()
    {
        Consume(Token.Function);

        int pos = position();
        ParseFunctionFlags flags = ParseFunctionFlags.kIsNormal;
        if (Check(Token.Mul))
        {
            impl().ReportMessageAt(scanner().location(), MessageTemplate.GeneratorInSingleStatementContext);
            return impl().NullStatement();
        }
        return ParseHoistableDeclaration(pos, flags, null, false);
    }

    protected TStatement ParseHoistableDeclaration(List<AstRawString> names, bool default_export)
    {
        Consume(Token.Function);

        int pos = position();
        ParseFunctionFlags flags = ParseFunctionFlags.kIsNormal;
        if (Check(Token.Mul))
        {
            flags |= ParseFunctionFlags.kIsGenerator;
        }
        return ParseHoistableDeclaration(pos, flags, names, default_export);
    }

    protected TStatement ParseHoistableDeclaration(int pos, ParseFunctionFlags flags, List<AstRawString> names,
                                                   bool default_export)
    {
        CheckStackOverflow();

        // FunctionDeclaration ::
        //   'function' Identifier '(' FormalParameters ')' '{' FunctionBody '}'
        //   'function' '(' FormalParameters ')' '{' FunctionBody '}'
        // GeneratorDeclaration ::
        //   'function' '*' Identifier '(' FormalParameters ')' '{' FunctionBody '}'
        //   'function' '*' '(' FormalParameters ')' '{' FunctionBody '}'
        //
        // The anonymous forms are allowed iff [default_export] is true.
        //
        // 'function' and '*' (if present) have been consumed by the caller.

        if ((flags & ParseFunctionFlags.kIsAsync) != 0 && Check(Token.Mul))
        {
            // Async generator
            flags |= ParseFunctionFlags.kIsGenerator;
        }

        TIdentifier name;
        FunctionNameValidity name_validity;
        TIdentifier variable_name;
        if (peek() == Token.LeftParen)
        {
            if (default_export)
            {
                impl().GetDefaultStrings(out name, out variable_name);
                name_validity = FunctionNameValidity.kSkipFunctionNameCheck;
            }
            else
            {
                ReportMessage(MessageTemplate.MissingFunctionName);
                return impl().NullStatement();
            }
        }
        else
        {
            bool is_strict_reserved = Token.IsStrictReservedWord(peek());
            name = ParseIdentifier();
            name_validity = is_strict_reserved
                ? FunctionNameValidity.kFunctionNameIsStrictReserved
                : FunctionNameValidity.kFunctionNameValidityUnknown;
            variable_name = name;
        }

        using FuncNameInferrerState fni_state = new(fni_);
        impl().PushEnclosingName(name);

        FunctionKind function_kind = FunctionKindFor(flags);

        TFunctionLiteral function = impl().ParseFunctionLiteral(
            name, scanner().location(), name_validity, function_kind, pos, FunctionSyntaxKind.Declaration,
            language_mode(), null);

        // In ES6, a function behaves as a lexical binding, except in
        // a script scope, or the initial scope of eval or another function.
        VariableMode mode = (!scope().is_declaration_scope() || scope().is_module_scope())
            ? VariableMode.Let
            : VariableMode.Var;
        // Async functions don't undergo sloppy mode block scoped hoisting, and don't
        // allow duplicates in a block. Both are represented by the
        // sloppy_block_functions_. Don't add them to the map for async functions.
        // Generators are also supposed to be prohibited; currently doing this behind
        // a flag and UseCounting violations to assess web compatibility.
        VariableKind kind = is_sloppy(language_mode()) && !scope().is_declaration_scope() &&
                            flags == ParseFunctionFlags.kIsNormal
            ? VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE
            : VariableKind.NORMAL_VARIABLE;

        return impl().DeclareFunction(variable_name, function, mode, kind, pos, end_position(), names);
    }

    protected TStatement ParseClassDeclaration(List<AstRawString> names, bool default_export)
    {
        // ClassDeclaration ::
        //   'class' Identifier ('extends' LeftHandExpression)? '{' ClassBody '}'
        //   'class' ('extends' LeftHandExpression)? '{' ClassBody '}'
        //
        // The anonymous form is allowed iff [default_export] is true.
        //
        // 'class' is expected to be consumed by the caller.
        //
        // A ClassDeclaration
        //
        //   class C { ... }
        //
        // has the same semantics as:
        //
        //   let C = class C { ... };
        //
        // so rewrite it as such.

        int class_token_pos = position();
        TIdentifier name = impl().EmptyIdentifierString();
        bool is_strict_reserved = Token.IsStrictReservedWord(peek());
        TIdentifier variable_name = impl().NullIdentifier();
        if (default_export && (peek() == Token.Extends || peek() == Token.LeftBrace))
        {
            impl().GetDefaultStrings(out name, out variable_name);
        }
        else
        {
            name = ParseIdentifier();
            variable_name = name;
        }

        using ExpressionParsingScope no_expression_scope = new(impl());
        TExpression value = ParseClassLiteral(scope(), name, scanner().location(), is_strict_reserved,
                                              class_token_pos);
        no_expression_scope.ValidateExpression();
        int end_pos = position();
        return impl().DeclareClass(variable_name, value, names, class_token_pos, end_pos);
    }

    // Language extension which is only enabled for source files loaded
    // through the API's extension mechanism.  A native function
    // declaration is resolved by looking up the function through a
    // callback provided by the extension.
    protected TStatement ParseNativeDeclaration()
    {
        function_state_.DisableOptimization(BailoutReason.kNativeFunctionLiteral);

        int pos = peek_position();
        Consume(Token.Function);
        // Allow "eval" or "arguments" for backward compatibility.
        TIdentifier name = ParseIdentifier();
        Expect(Token.LeftParen);
        if (peek() != Token.RightParen)
        {
            do
            {
                ParseIdentifier();
            } while (Check(Token.Comma));
        }
        Expect(Token.RightParen);
        Expect(Token.Semicolon);
        return impl().DeclareNative(name, pos);
    }

    protected TStatement ParseAsyncFunctionDeclaration(List<AstRawString> names, bool default_export)
    {
        // AsyncFunctionDeclaration ::
        //   async [no LineTerminator here] function BindingIdentifier[Await]
        //       ( FormalParameters[Await] ) { AsyncFunctionBody }
        if (scanner().literal_contains_escapes())
        {
            impl().ReportUnexpectedToken(Token.EscapedKeyword);
        }
        int pos = position();
        Consume(Token.Function);
        ParseFunctionFlags flags = ParseFunctionFlags.kIsAsync;
        return ParseHoistableDeclaration(pos, flags, names, default_export);
    }

    // Consumes the ending }.
    protected void ParseFunctionBody(TStatementList body, TIdentifier function_name, int pos,
                                     TFormalParameters parameters, FunctionKind kind,
                                     FunctionSyntaxKind function_syntax_kind, FunctionBodyType body_type)
    {
        CheckStackOverflow();

        if (IsResumableFunction(kind)) impl().PrepareGeneratorVariables();

        DeclarationScope function_scope = parameters.scope;
        DeclarationScope inner_scope = function_scope;

        // Building the parameter initialization block declares the parameters.
        // TODO(verwaest): Rely on ArrowHeadParsingScope instead.
        if (!parameters.is_simple)
        {
            if (has_error()) return;
            body.Add(impl().BuildParameterInitializationBlock(parameters));
            if (has_error()) return;

            inner_scope = NewVarblockScope();
            inner_scope.set_start_position(position());
        }

        using TStatementList inner_body = TStatementList.New(pointer_buffer());

        using (BlockState block_state = new(this, inner_scope))
        {
            if (body_type == FunctionBodyType.kExpression)
            {
                TExpression expression = ParseAssignmentExpression();
                inner_body.Add(BuildReturnStatement(expression, expression.position()));
            }
            else
            {
                // If we are parsing the source as if it is wrapped in a function, the
                // source ends without a closing brace.
                Token closing_token = function_syntax_kind == FunctionSyntaxKind.Wrapped
                    ? Token.Eos
                    : Token.RightBrace;

                if (IsAsyncGeneratorFunction(kind))
                {
                    impl().ParseAsyncGeneratorFunctionBody(pos, kind, inner_body);
                }
                else if (IsGeneratorFunction(kind))
                {
                    impl().ParseGeneratorFunctionBody(pos, kind, inner_body);
                }
                else
                {
                    ParseStatementList(inner_body, closing_token);
                    if (IsAsyncFunction(kind))
                    {
                        inner_scope.set_end_position(end_position());
                    }
                }
                if (IsDerivedConstructor(kind))
                {
                    // Derived constructors are implemented by returning `this` when the
                    // original return value is undefined, so always use `this`.
                    using ExpressionParsingScope expression_scope = new(impl());
                    UseThis();
                    expression_scope.ValidateExpression();
                }
                Expect(closing_token);
            }
        }

        scope().set_end_position(end_position());

        bool allow_duplicate_parameters = false;

        CheckConflictingVarDeclarations(inner_scope);

        if (parameters.is_simple)
        {
            if (is_sloppy(function_scope.language_mode()))
            {
                impl().InsertSloppyBlockFunctionVarBindings(function_scope);
            }
            allow_duplicate_parameters = is_sloppy(function_scope.language_mode()) && !IsConciseMethod(kind);
        }
        else
        {
            impl().SetLanguageMode(function_scope, inner_scope.language_mode());

            if (is_sloppy(inner_scope.language_mode()))
            {
                impl().InsertSloppyBlockFunctionVarBindings(inner_scope);
            }

            inner_scope.set_end_position(end_position());
            if (inner_scope.FinalizeBlockScope() != null)
            {
                TBlock inner_block = factory().NewBlock(true, inner_body);
                inner_body.Rewind();
                inner_body.Add(inner_block);
                inner_block.set_scope(inner_scope);
                impl().RecordBlockSourceRange(inner_block, scope().end_position());
                if (!impl().HasCheckedSyntax())
                {
                    AstRawString conflict = inner_scope.FindVariableDeclaredIn(
                        function_scope, VariableMode.LastLexicalVariableMode);
                    if (conflict != null)
                    {
                        impl().ReportVarRedeclarationIn(conflict, inner_scope);
                    }
                }

                // According to
                // https://tc39.es/ecma262/#sec-functiondeclarationinstantiation step
                // 27,28 when hasParameterExpressions is true, we need bind var declared
                // arguments to "arguments exotic object", so we here first declare
                // "arguments exotic object", then var declared arguments will be
                // initialized with "arguments exotic object"
                if (!IsArrowFunction(kind))
                {
                    function_scope.DeclareArguments(ast_value_factory());
                }

                impl().InsertShadowingVarBindingInitializers(inner_block);
            }
        }

        ValidateFormalParameters(language_mode(), parameters, allow_duplicate_parameters);

        if (!IsArrowFunction(kind))
        {
            function_scope.DeclareArguments(ast_value_factory());
        }

        impl().DeclareFunctionNameVar(function_name, function_syntax_kind, function_scope);

        inner_body.MergeInto(body);
    }

    protected void CheckArityRestrictions(int param_count, FunctionKind function_kind, bool has_rest,
                                          int formals_start_pos, int formals_end_pos)
    {
        if (impl().HasCheckedSyntax()) return;
        if (IsGetterFunction(function_kind))
        {
            if (param_count != 0)
            {
                impl().ReportMessageAt(new Scanner.Location(formals_start_pos, formals_end_pos),
                                       MessageTemplate.BadGetterArity);
            }
        }
        else if (IsSetterFunction(function_kind))
        {
            if (param_count != 1)
            {
                impl().ReportMessageAt(new Scanner.Location(formals_start_pos, formals_end_pos),
                                       MessageTemplate.BadSetterArity);
            }
            if (has_rest)
            {
                impl().ReportMessageAt(new Scanner.Location(formals_start_pos, formals_end_pos),
                                       MessageTemplate.BadSetterRestParameter);
            }
        }
    }

    protected bool IsNextLetKeyword()
    {
        Token next_next = PeekAhead();
        switch (next_next)
        {
            case Token.LeftBrace:
            case Token.LeftBracket:
            case Token.Identifier:
            case Token.Static:
            case Token.Let: // `let let;` is disallowed by static semantics, but the
                            // token must be first interpreted as a keyword in order
                            // for those semantics to apply. This ensures that ASI is
                            // not honored when a LineTerminator separates the
                            // tokens.
            case Token.Yield:
            case Token.Await:
            case Token.Get:
            case Token.Set:
            case Token.Of:
            case Token.Using:
            case Token.Accessor:
            case Token.Async:
                return true;
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                // The early error rule for future reserved keywords
                // (https://tc39.es/ecma262/#sec-identifiers-static-semantics-early-errors)
                // uses the static semantics StringValue of IdentifierName, which
                // normalizes escape sequences. So, both escaped and unescaped future
                // reserved keywords are allowed as identifiers in sloppy mode.
                return is_sloppy(language_mode());
            default:
                return false;
        }
    }

    protected TExpression ParseArrowFunctionLiteral(TFormalParameters formal_parameters, int function_literal_id,
                                                    bool could_be_immediately_invoked)
    {
        if (!impl().HasCheckedSyntax() && scanner_.HasLineTerminatorBeforeNext())
        {
            // No line terminator allowed between the parameters and the arrow:
            // ArrowFunction[In, Yield, Await] :
            //   ArrowParameters[?Yield, ?Await] [no LineTerminator here] =>
            //   ConciseBody[?In]
            // If the next token is not `=>`, it's a syntax error anyway.
            impl().ReportUnexpectedTokenAt(scanner_.peek_location(), Token.Arrow);
            return impl().FailureExpression();
        }

        int expected_property_count = 0;
        int suspend_count = 0;

        FunctionKind kind = formal_parameters.scope.function_kind();
        int compile_hint_position = formal_parameters.scope.start_position();
        FunctionLiteral.EagerCompileHint eager_compile_hint =
            could_be_immediately_invoked ||
            (compile_hints_magic_enabled_ && scanner_.SawMagicCommentCompileHintsAll()) ||
            (compile_hints_per_function_magic_enabled_ && scanner_.HasPerFunctionCompileHint(compile_hint_position))
                ? FunctionLiteral.EagerCompileHint.kShouldEagerCompile
                : default_eager_compile_hint_;

        eager_compile_hint = impl().GetEmbedderCompileHint(eager_compile_hint, compile_hint_position);

        bool can_preparse = impl().parse_lazily() &&
                            eager_compile_hint == FunctionLiteral.EagerCompileHint.kShouldLazyCompile;
        // TODO(marja): consider lazy-parsing inner arrow functions too. is_this
        // handling in Scope::ResolveVariable needs to change.
        bool is_lazy_top_level_function = can_preparse && impl().AllowsLazyParsingWithoutUnresolvedVariables();
        bool has_braces = true;
        ProducedPreparseData produced_preparse_data = null;
        using TStatementList body = TStatementList.New(pointer_buffer());
        using (FunctionState function_state = new(this, formal_parameters.scope))
        {
            Consume(Token.Arrow);

            if (peek() == Token.LeftBrace)
            {
                // Multiple statement body
                if (is_lazy_top_level_function)
                {
                    // FIXME(marja): Arrow function parameters will be parsed even if the
                    // body is preparsed; move relevant parts of parameter handling to
                    // simulate consistent parameter handling.

                    // Building the parameter initialization block declares the parameters.
                    // TODO(verwaest): Rely on ArrowHeadParsingScope instead.
                    if (!formal_parameters.is_simple)
                    {
                        impl().BuildParameterInitializationBlock(formal_parameters);
                        if (has_error()) return impl().FailureExpression();
                    }

                    // For arrow functions, we don't need to retrieve data about function
                    // parameters.
                    int dummy_num_parameters = -1;
                    int dummy_function_length = -1;
                    bool did_preparse_successfully = impl().SkipFunction(
                        function_literal_id, null, kind, FunctionSyntaxKind.AnonymousExpression,
                        formal_parameters.scope, ref dummy_num_parameters, ref dummy_function_length,
                        ref produced_preparse_data);

                    if (did_preparse_successfully)
                    {
                        // Validate parameter names. We can do this only after preparsing the
                        // function, since the function can declare itself strict.
                        ValidateFormalParameters(language_mode(), formal_parameters, false);
                    }
                    else
                    {
                        // In case we did not sucessfully preparse the function because of an
                        // unidentified error we do a full reparse to return the error.
                        // Parse again in the outer scope, since the language mode may change.
                        using BlockState block_state = new(this, scope().outer_scope());
                        TExpression expression = ParseConditionalExpression();
                        // Reparsing the head may have caused a stack overflow.
                        if (has_error()) return impl().FailureExpression();

                        DeclarationScope function_scope = next_arrow_function_info_.scope;
                        using FunctionState inner_function_state = new(this, function_scope);
                        Scanner.Location loc = new(function_scope.start_position(), end_position());
                        TFormalParameters parameters = impl().NewFormalParameters(function_scope);
                        parameters.is_simple = function_scope.has_simple_parameters();
                        impl().DeclareArrowFunctionFormalParameters(parameters, expression, loc);
                        next_arrow_function_info_.Reset();

                        Consume(Token.Arrow);
                        Consume(Token.LeftBrace);

                        using AcceptINScope accept_in = new(this, true);
                        using FunctionParsingScope body_parsing_scope = new(this);
                        ParseFunctionBody(body, impl().NullIdentifier(), kNoSourcePosition, parameters, kind,
                                          FunctionSyntaxKind.AnonymousExpression, FunctionBodyType.kBlock);
                        if (!has_error()) throw new InvalidOperationException("CHECK(has_error())");
                        return impl().FailureExpression();
                    }
                }
                else
                {
                    Consume(Token.LeftBrace);
                    using AcceptINScope accept_in = new(this, true);
                    using FunctionParsingScope body_parsing_scope = new(this);
                    ParseFunctionBody(body, impl().NullIdentifier(), kNoSourcePosition, formal_parameters, kind,
                                      FunctionSyntaxKind.AnonymousExpression, FunctionBodyType.kBlock);
                    expected_property_count = function_state.expected_property_count();
                }
            }
            else
            {
                // Single-expression body
                has_braces = false;
                using FunctionParsingScope body_parsing_scope = new(this);
                ParseFunctionBody(body, impl().NullIdentifier(), kNoSourcePosition, formal_parameters, kind,
                                  FunctionSyntaxKind.AnonymousExpression, FunctionBodyType.kExpression);
                expected_property_count = function_state.expected_property_count();
            }

            formal_parameters.scope.set_end_position(end_position());

            // Validate strict mode.
            if (is_strict(language_mode()))
            {
                CheckStrictOctalLiteral(formal_parameters.scope.start_position(), end_position());
            }
            suspend_count = function_state.suspend_count();
        }

        TFunctionLiteral function_literal = factory().NewFunctionLiteral(
            impl().EmptyIdentifierString(), formal_parameters.scope, body, expected_property_count,
            formal_parameters.num_parameters(), formal_parameters.function_length,
            FunctionLiteral.ParameterFlag.kNoDuplicateParameters, FunctionSyntaxKind.AnonymousExpression,
            eager_compile_hint, formal_parameters.scope.start_position(), has_braces, function_literal_id,
            produced_preparse_data);

        function_literal.set_suspend_count(suspend_count);
        function_literal.set_function_token_position(formal_parameters.scope.start_position());

        impl().RecordFunctionLiteralSourceRange(function_literal);
        impl().AddFunctionForNameInference(function_literal);

        return function_literal;
    }

    protected TExpression ParseClassExpression(Scope outer_scope)
    {
        Consume(Token.Class);
        int class_token_pos = position();
        TIdentifier name = impl().EmptyIdentifierString();
        bool is_strict_reserved_name = false;
        Scanner.Location class_name_location = Scanner.Location.invalid();
        if (peek_any_identifier())
        {
            name = ParseAndClassifyIdentifier(Next());
            class_name_location = scanner().location();
            is_strict_reserved_name = Token.IsStrictReservedWord(scanner().current_token());
        }
        return ParseClassLiteral(outer_scope, name, class_name_location, is_strict_reserved_name, class_token_pos);
    }

    protected TExpression ParseClassLiteral(Scope outer_scope, TIdentifier name, Scanner.Location class_name_location,
                                            bool name_is_strict_reserved, int class_token_pos)
    {
        bool is_anonymous = impl().IsEmptyIdentifier(name);

        // All parts of a ClassDeclaration and ClassExpression are strict code.
        if (!impl().HasCheckedSyntax() && !is_anonymous)
        {
            if (name_is_strict_reserved)
            {
                impl().ReportMessageAt(class_name_location, MessageTemplate.UnexpectedStrictReserved);
                return impl().FailureExpression();
            }
            if (impl().IsEvalOrArguments(name))
            {
                impl().ReportMessageAt(class_name_location, MessageTemplate.StrictEvalArguments);
                return impl().FailureExpression();
            }
        }

        ClassScope class_scope = NewClassScope(outer_scope, is_anonymous);
        using BlockState block_state = new(this, class_scope);
        RaiseLanguageMode(LanguageMode.Strict);

        using BlockState object_literal_scope_state = new(this, null, object_literal_stack: true);

        ClassInfo class_info = new(this);
        class_info.is_anonymous = is_anonymous;

        scope().set_start_position(class_token_pos);
        if (Check(Token.Extends))
        {
            using ClassScope.HeritageParsingScope heritage = new(class_scope);
            using FuncNameInferrerState fni_state = new(fni_);
            using ExpressionParsingScope scope = new(impl());
            class_info.extends = ParseLeftHandSideExpression();
            scope.ValidateExpression();
        }

        Expect(Token.LeftBrace);

        ParseClassLiteralBody(class_info, name, class_token_pos, Token.RightBrace);

        CheckStrictOctalLiteral(scope().start_position(), scope().end_position());

        VariableProxy unresolvable = class_scope.ResolvePrivateNamesPartially();
        if (unresolvable != null)
        {
            impl().ReportMessageAt(new Scanner.Location(unresolvable.position(), unresolvable.position() + 1),
                                   MessageTemplate.InvalidPrivateFieldResolution, unresolvable.raw_name());
            return impl().FailureExpression();
        }

        if (class_info.requires_brand)
        {
            class_scope.DeclareBrandVariable(ast_value_factory(), IsStaticFlag.NotStatic, kNoSourcePosition);
        }

        if (class_scope.needs_home_object())
        {
            class_info.home_object_variable = class_scope.DeclareHomeObjectVariable(ast_value_factory());
            class_info.static_home_object_variable =
                class_scope.DeclareStaticHomeObjectVariable(ast_value_factory());
        }

        bool should_save_class_variable = class_scope.should_save_class_variable();
        if (!class_info.is_anonymous || should_save_class_variable)
        {
            impl().DeclareClassVariable(class_scope, name, class_info, class_token_pos);
            if (should_save_class_variable)
            {
                class_scope.class_variable().set_is_used();
                class_scope.class_variable().ForceContextAllocation();
                // Static brand checks elide the hole check and can observe `the_hole`
                // before the class is initialized. Mark as assigned so `the_hole` is not
                // propagated across initialization.
                class_scope.class_variable().set_maybe_assigned();
            }
        }

        return impl().RewriteClassLiteral(class_scope, name, class_info, class_token_pos);
    }

    protected void ParseClassLiteralBody(ClassInfo class_info, TIdentifier name, int class_token_pos,
                                         Token end_token)
    {
        bool has_extends = !impl().IsNull(class_info.extends);

        // 3 reserved arguments: class_boilerplate, constructor, super_class
        int expected_define_class_args = 3;

        while (peek() != end_token)
        {
            if (Check(Token.Semicolon)) continue;

            // Either we're parsing a `static { }` initialization block or a property.
            if (peek() == Token.Static && PeekAhead() == Token.LeftBrace)
            {
                TBlock static_block = ParseClassStaticBlock(class_info);
                impl().AddClassStaticBlock(static_block, class_info);
                continue;
            }

            using FuncNameInferrerState fni_state = new(fni_);
            // If we haven't seen the constructor yet, it potentially is the next
            // property.
            bool is_constructor = !class_info.has_seen_constructor;
            using ParsePropertyInfo prop_info = new(this);
            prop_info.position = PropertyPosition.kClassLiteral;

            TClassLiteralProperty property = ParseClassPropertyDefinition(class_info, prop_info, has_extends);

            if (has_error()) return;

            ClassLiteralProperty.Kind property_kind = ClassPropertyKindFor(prop_info.kind);

            if (!class_info.has_static_computed_names && prop_info.is_static && prop_info.is_computed_name)
            {
                class_info.has_static_computed_names = true;
            }
            is_constructor &= class_info.has_seen_constructor;

            bool is_field = property_kind == ClassLiteralProperty.Kind.FIELD;

            if (prop_info.is_private)
            {
                class_info.requires_brand |= !is_field && !prop_info.is_static;
                class_info.has_static_private_methods_or_accessors |= prop_info.is_static && !is_field;

                impl().DeclarePrivateClassMember(scope().AsClassScope(), prop_info.name, property, property_kind,
                                                 prop_info.is_static, class_info);
                impl().InferFunctionName();
                continue;
            }

            if (prop_info.is_computed_name)
            {
                expected_define_class_args++;
            }

            if (!is_field)
            {
                if (property_kind == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
                {
                    expected_define_class_args += 2;
                }
                else
                {
                    expected_define_class_args++;
                }
            }

            if (expected_define_class_args > kCodeMaxArguments)
            {
                impl().ReportMessageAt(new Scanner.Location(class_token_pos, position()),
                                       MessageTemplate.TooManyArguments);
                return;
            }

            if (is_field)
            {
                // If we're reparsing, we might not have a class scope. We only need a
                // class scope if we have a computed name though, and in that case we're
                // certain that the current scope must be a class scope.
                ClassScope class_scope = null;
                if (prop_info.is_computed_name)
                {
                    class_info.computed_field_count++;
                    class_scope = scope().AsClassScope();
                }

                impl().DeclarePublicClassField(class_scope, property, prop_info.is_static,
                                               prop_info.is_computed_name, class_info);
                impl().InferFunctionName();
                continue;
            }

            if (property_kind == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
            {
                // Private auto-accessors are handled above with the other private
                // properties.
                impl().AddInstanceFieldOrStaticElement(property, class_info, prop_info.is_static);
            }

            impl().DeclarePublicClassMethod(name, property, is_constructor, class_info);
            impl().InferFunctionName();
        }

        Expect(end_token);
        scope().set_end_position(end_position());
    }

    protected TExpression ParseAsyncFunctionLiteral()
    {
        // AsyncFunctionLiteral ::
        //   async [no LineTerminator here] function ( FormalParameters[Await] )
        //       { AsyncFunctionBody }
        //
        //   async [no LineTerminator here] function BindingIdentifier[Await]
        //       ( FormalParameters[Await] ) { AsyncFunctionBody }
        if (scanner().literal_contains_escapes())
        {
            impl().ReportUnexpectedToken(Token.EscapedKeyword);
        }
        int pos = position();
        Consume(Token.Function);
        TIdentifier name = impl().NullIdentifier();
        FunctionSyntaxKind syntax_kind = FunctionSyntaxKind.AnonymousExpression;

        ParseFunctionFlags flags = ParseFunctionFlags.kIsAsync;
        if (Check(Token.Mul)) flags |= ParseFunctionFlags.kIsGenerator;
        FunctionKind kind = FunctionKindFor(flags);
        bool is_strict_reserved = Token.IsStrictReservedWord(peek());

        if (impl().ParsingDynamicFunctionDeclaration())
        {
            // We don't want dynamic functions to actually declare their name
            // "anonymous". We just want that name in the toString().

            // Consuming token we did not peek yet, which could lead to a kIllegal token
            // in the case of a stackoverflow.
            Consume(Token.Identifier);
        }
        else if (peek_any_identifier())
        {
            syntax_kind = FunctionSyntaxKind.NamedExpression;
            name = ParseIdentifier(kind);
        }
        TFunctionLiteral result = impl().ParseFunctionLiteral(
            name, scanner().location(),
            is_strict_reserved
                ? FunctionNameValidity.kFunctionNameIsStrictReserved
                : FunctionNameValidity.kFunctionNameValidityUnknown,
            kind, pos, syntax_kind, language_mode(), null);
        if (impl().IsNull(result)) return impl().FailureExpression();
        return result;
    }

    protected TExpression ParseTemplateLiteral(TExpression tag, int start, bool tagged)
    {
        // A TemplateLiteral is made up of 0 or more kTemplateSpan tokens (literal
        // text followed by a substitution expression), finalized by a single
        // kTemplateTail.
        //
        // In terms of draft language, kTemplateSpan may be either the TemplateHead or
        // TemplateMiddle productions, while kTemplateTail is either TemplateTail, or
        // NoSubstitutionTemplate.
        //
        // When parsing a TemplateLiteral, we must have scanned either an initial
        // kTemplateSpan, or a kTemplateTail.

        if (tagged)
        {
            // TaggedTemplate expressions prevent the eval compilation cache from being
            // used. This flag is only used if an eval is being parsed.
            set_allow_eval_cache(false);
        }

        bool forbid_illegal_escapes = !tagged;

        // If we reach a kTemplateTail first, we are parsing a NoSubstitutionTemplate.
        // In this case we may simply consume the token and build a template with a
        // single kTemplateSpan and no expressions.
        if (peek() == Token.TemplateTail)
        {
            Consume(Token.TemplateTail);
            int pos0 = position();
            object ts0 = impl().OpenTemplateLiteral(pos0);
            bool is_valid0 = CheckTemplateEscapes(forbid_illegal_escapes);
            impl().AddTemplateSpan(ts0, is_valid0, true);
            return impl().CloseTemplateLiteral(ts0, start, tag);
        }

        Consume(Token.TemplateSpan);
        int pos = position();
        object ts = impl().OpenTemplateLiteral(pos);
        bool is_valid = CheckTemplateEscapes(forbid_illegal_escapes);
        impl().AddTemplateSpan(ts, is_valid, false);
        Token next;

        // If we open with a kTemplateSpan, we must scan the subsequent expression,
        // and repeat if the following token is a kTemplateSpan as well (in this
        // case, representing a TemplateMiddle).
        int arguments_count = 1; // For `template_object`

        do
        {
            next = peek();

            int expr_pos = peek_position();
            using AcceptINScope accept_in = new(this, true);
            TExpression expression = ParseExpressionCoverGrammar();

            arguments_count++;
            if (tagged && arguments_count + 1 /* receiver */ > kCodeMaxArguments)
            {
                impl().ReportMessageAt(new Scanner.Location(expr_pos, peek_position()),
                                       MessageTemplate.TooManyArguments);
                return impl().FailureExpression();
            }

            impl().AddTemplateExpression(ts, expression);

            if (peek() != Token.RightBrace)
            {
                impl().ReportMessageAt(new Scanner.Location(expr_pos, peek_position()),
                                       MessageTemplate.UnterminatedTemplateExpr);
                return impl().FailureExpression();
            }

            // If we didn't die parsing that expression, our next token should be a
            // kTemplateSpan or kTemplateTail.
            next = scanner().ScanTemplateContinuation();
            Next();
            pos = position();

            is_valid = CheckTemplateEscapes(forbid_illegal_escapes);
            impl().AddTemplateSpan(ts, is_valid, next == Token.TemplateTail);
        } while (next == Token.TemplateSpan);

        // Once we've reached a kTemplateTail, we can close the TemplateLiteral.
        return impl().CloseTemplateLiteral(ts, start, tag);
    }

    // Checks if the expression is a valid reference expression (e.g., on the
    // left-hand side of assignments). Although ruled out by ECMA as early errors,
    // we allow calls for web compatibility and rewrite them to a runtime throw.
    // Modern language features can be exempted from this hack by passing
    // early_error = true.
    protected internal TExpression RewriteInvalidReferenceExpression(TExpression expression, int beg_pos,
                                                                     int end_pos, MessageTemplate message,
                                                                     bool early_error)
    {
        if (impl().IsIdentifier(expression))
        {
            ReportMessageAt(new Scanner.Location(beg_pos, end_pos), MessageTemplate.StrictEvalArguments);
            return impl().FailureExpression();
        }
        if (expression.IsCall() && !impl().IsTaggedTemplateCall(expression) && !early_error)
        {
            expression_scope().RecordPatternError(new Scanner.Location(beg_pos, end_pos),
                                                  MessageTemplate.InvalidDestructuringTarget);
            // If it is a call, make it a runtime error for legacy web compatibility.
            // Bug: https://bugs.chromium.org/p/v8/issues/detail?id=4480
            // Rewrite `expr' to `expr[throw ReferenceError]'.
            impl().CountUsage(is_strict(language_mode())
                                  ? UseCounterFeature.kAssigmentExpressionLHSIsCallInStrict
                                  : UseCounterFeature.kAssigmentExpressionLHSIsCallInSloppy);
            TExpression error = impl().NewThrowReferenceError(message, beg_pos);
            return factory().NewProperty(expression, error, beg_pos);
        }
        // Tagged templates and other modern language features (which pass early_error
        // = true) are exempt from the web compatibility hack. Throw a regular early
        // error.
        ReportMessageAt(new Scanner.Location(beg_pos, end_pos), message);
        return impl().FailureExpression();
    }

    protected void ClassifyParameter(TIdentifier parameter, int begin, int end)
    {
        if (impl().IsEvalOrArguments(parameter))
        {
            expression_scope().RecordStrictModeParameterError(new Scanner.Location(begin, end),
                                                              MessageTemplate.StrictEvalArguments);
        }
    }

    protected void ClassifyArrowParameter(AccumulationScope accumulation_scope, int position, TExpression parameter)
    {
        accumulation_scope.Accumulate();
        if (parameter.is_parenthesized() ||
            !(impl().IsIdentifier(parameter) || parameter.IsPattern() || parameter.IsAssignment()))
        {
            expression_scope().RecordDeclarationError(new Scanner.Location(position, end_position()),
                                                      MessageTemplate.InvalidDestructuringTarget);
        }
        else if (impl().IsIdentifier(parameter))
        {
            ClassifyParameter(impl().AsIdentifier(parameter), position, end_position());
        }
        else
        {
            expression_scope().RecordNonSimpleParameter();
        }
    }

    protected bool IsValidReferenceExpression(TExpression expression)
        => IsAssignableIdentifier(expression) || expression.IsProperty();

    protected TExpression ParsePossibleDestructuringSubPattern(AccumulationScope scope)
    {
        scope?.Accumulate();
        int begin = peek_position();
        TExpression result = ParseAssignmentExpressionCoverGrammar();

        if (IsValidReferenceExpression(result))
        {
            // Parenthesized identifiers and property references are allowed as part of
            // a larger assignment pattern, even though parenthesized patterns
            // themselves are not allowed, e.g., "[(x)] = []". Only accumulate
            // assignment pattern errors if the parsed expression is more complex.
            if (impl().IsIdentifier(result))
            {
                if (result.is_parenthesized())
                {
                    expression_scope().RecordDeclarationError(new Scanner.Location(begin, end_position()),
                                                              MessageTemplate.InvalidDestructuringTarget);
                }
                TIdentifier identifier = impl().AsIdentifier(result);
                ClassifyParameter(identifier, begin, end_position());
            }
            else
            {
                expression_scope().RecordDeclarationError(new Scanner.Location(begin, end_position()),
                                                          MessageTemplate.InvalidPropertyBindingPattern);
                scope?.ValidateExpression();
            }
        }
        else if (result.is_parenthesized() || (!result.IsPattern() && !result.IsAssignment()))
        {
            expression_scope().RecordPatternError(new Scanner.Location(begin, end_position()),
                                                  MessageTemplate.InvalidDestructuringTarget);
        }

        return result;
    }

    // Magical syntax support.
    protected TExpression ParseV8Intrinsic()
    {
        // CallRuntime ::
        //   '%' Identifier Arguments

        int pos = peek_position();
        Consume(Token.Mod);
        // Allow "eval" or "arguments" for backward compatibility.
        TIdentifier name = ParseIdentifier();
        if (peek() != Token.LeftParen)
        {
            impl().ReportUnexpectedToken(peek());
            return impl().FailureExpression();
        }
        using TExpressionList args = TExpressionList.New(pointer_buffer());
        ParseArguments(args, out bool has_spread);

        if (has_spread)
        {
            ReportMessageAt(new Scanner.Location(pos, position()), MessageTemplate.IntrinsicWithSpread);
            return impl().FailureExpression();
        }

        return impl().NewV8Intrinsic(name, args, pos);
    }

    protected void ParseStatementList(TStatementList body, Token end_token)
    {
        // StatementList ::
        //   (StatementListItem)* <end_token>

        while (peek() == Token.String)
        {
            bool use_strict = false;

            Scanner.Location token_loc = scanner().peek_location();

            if (scanner().NextLiteralExactlyEquals("use strict"))
            {
                use_strict = true;
            }

            TStatement stat = ParseStatementListItem();
            if (impl().IsNull(stat)) return;

            body.Add(stat);

            if (!impl().IsStringLiteral(stat)) break;

            if (use_strict)
            {
                // Directive "use strict" (ES5 14.1).
                RaiseLanguageMode(LanguageMode.Strict);
                if (!scope().HasSimpleParameters())
                {
                    // TC39 deemed "use strict" directives to be an error when occurring
                    // in the body of a function with non-simple parameter list, on
                    // 29/7/2015. See:
                    // https://github.com/tc39/notes/blob/main/meetings/2015-07/july-29.md#conclusionresolution
                    impl().ReportMessageAt(token_loc, MessageTemplate.IllegalLanguageModeDirective, "use strict");
                    return;
                }
            }
            else
            {
                // Possibly an unknown directive.
                // Should not change mode, but will increment usage counters
                // as appropriate. Ditto usages below.
                RaiseLanguageMode(LanguageMode.Sloppy);
            }
        }

        while (peek() != end_token)
        {
            TStatement stat = ParseStatementListItem();
            if (impl().IsNull(stat)) return;
            if (stat.IsEmptyStatement()) continue;
            body.Add(stat);
        }
    }
}
