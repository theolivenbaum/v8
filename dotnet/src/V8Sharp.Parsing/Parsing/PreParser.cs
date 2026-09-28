// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/preparser.h (class PreParser) and preparser.cc.
//
// Preparsing checks a JavaScript program and emits preparse-data that helps
// a later parsing to be faster.
// See preparse-data-format.h for the data format.
//
// The PreParser checks that the syntax follows the grammar for JavaScript,
// and collects some information about the program along the way.
// The grammar check is only performed in order to understand the program
// sufficiently to deduce some information about it, that can be used
// to speed up later parsing. Finding errors is not the goal of pre-parsing,
// rather it is to speed up properly written and correct programs.
// That means that contextual checks (like a label being declared where
// it is used) are generally omitted.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public sealed class PreParser : ParserBase<PreParser, PreParserExpression, PreParserIdentifier, PreParserStatement,
    PreParserStatement, PreParserExpression, PreParserExpression, PreParserExpression, PreParserExpressionList,
    PreParserExpressionList, PreParserScopedStatementList, PreParserPropertyList, PreParserPropertyList,
    PreParserFormalParameters, PreParserFactory, PreParserFuncNameInferrer>
{
    public enum PreParseResult
    {
        kPreParseStackOverflow,
        kPreParseNotIdentifiableError,
        kPreParseSuccess,
    }

    // Set compile_hints_magic_enabled = false, since we cannot have eager
    // functions inside lazy functions (when we're already using the
    // PreParser). Ditto compile_hints_per_function_magic_enabled.
    public PreParser(Scanner scanner, AstValueFactory ast_value_factory,
                     PendingCompilationErrorHandler pending_error_handler, UnoptimizedCompileFlags flags,
                     ParsingFlags v8_flags)
        : base(scanner, ast_value_factory, pending_error_handler, flags, v8_flags,
               new PreParserFactory(ast_value_factory), PreParserFuncNameInferrer.Instance,
               compile_hints_magic_enabled: false, compile_hints_per_function_magic_enabled: false)
    {
        use_counts_ = null;
        preparse_data_builder_ = null;
    }

    public static bool IsPreParser() => true;

    public PreParserLogger logger() => log_;

    public PreparseDataBuilder preparse_data_builder() => preparse_data_builder_;

    public void set_preparse_data_builder(PreparseDataBuilder preparse_data_builder)
        => preparse_data_builder_ = preparse_data_builder;

    // ----------------------------------------------------------------------
    // preparser.cc

    private static PreParserIdentifier GetIdentifierHelper(Scanner scanner, AstRawString @string,
                                                           AstValueFactory avf)
    {
        // These symbols require slightly different treatement:
        // - regular keywords (async, etc.; treated in 1st switch.)
        // - 'contextual' keywords (and may contain escaped; treated in 2nd switch.)
        // - 'contextual' keywords, but may not be escaped (3rd switch).
        switch (scanner.current_token())
        {
            case Token.Async:
                return PreParserIdentifier.Async();
            case Token.PrivateName:
                return PreParserIdentifier.PrivateName();
            default:
                break;
        }
        if (@string == avf.constructor_string())
        {
            return PreParserIdentifier.Constructor();
        }
        if (@string == avf.eval_string())
        {
            return PreParserIdentifier.Eval();
        }
        if (@string == avf.arguments_string())
        {
            return PreParserIdentifier.Arguments();
        }
        return PreParserIdentifier.Default();
    }

    public override PreParserIdentifier GetIdentifier()
    {
        AstRawString result = scanner().CurrentSymbol(ast_value_factory());
        PreParserIdentifier symbol = GetIdentifierHelper(scanner(), result, ast_value_factory());
        symbol.string_ = result;
        return symbol;
    }

    // Pre-parse the program from the character stream; returns true on
    // success (even if parsing failed, the pre-parse data successfully
    // captured the syntax error), and false if a stack-overflow happened
    // during parsing.
    public PreParseResult PreParseProgram()
    {
        DeclarationScope scope = NewScriptScope(REPLMode.No);

        // ModuleDeclarationInstantiation for Source Text Module Records creates a
        // new Module Environment Record whose outer lexical environment record is
        // the global scope.
        if (flags().is_module()) scope = NewModuleScope(scope);

        using FunctionState top_scope = new(this, scope);
        original_scope_ = scope_;
        int start_position = peek_position();
        PreParserScopedStatementList body = PreParserScopedStatementList.New(pointer_buffer());
        ParseStatementList(body, Token.Eos);
        CheckConflictingVarDeclarations(scope);
        original_scope_ = null;
        if (stack_overflow()) return PreParseResult.kPreParseStackOverflow;
        if (is_strict(language_mode()))
        {
            CheckStrictOctalLiteral(start_position, scanner().location().end_pos);
        }
        return PreParseResult.kPreParseSuccess;
    }

    // Parses a single function literal, from the opening parentheses before
    // parameters to the closing brace after the body.
    // Returns a FunctionEntry describing the body of the function in enough
    // detail that it can be lazily compiled.
    // The scanner is expected to have matched the "function" or "function*"
    // keyword and parameters, and have consumed the initial '{'.
    // At return, unless an error occurred, the scanner is positioned before the
    // the final '}'.
    public PreParseResult PreParseFunction(int function_literal_id, AstRawString function_name, FunctionKind kind,
                                           FunctionSyntaxKind function_syntax_kind,
                                           DeclarationScope function_scope, int[] use_counts,
                                           out ProducedPreparseData produced_preparse_data)
    {
        produced_preparse_data = null;
        use_counts_ = use_counts;

        PreParserFormalParameters formals = new(function_scope);

        ResetInfoId(function_literal_id);

        // The caller passes the function_scope which is not yet inserted into the
        // scope stack. All scopes above the function_scope are ignored by the
        // PreParser.
        using FunctionState function_state = new(this, function_scope);

        // Start collecting data for a new function which might contain skippable
        // functions.
        PreparseDataBuilder.DataGatheringScope preparse_data_builder_scope = new(this);
        try
        {
            if (IsArrowFunction(kind))
            {
                formals.is_simple = function_scope.has_simple_parameters();
            }
            else
            {
                preparse_data_builder_scope.Start(function_scope);

                // Parse non-arrow function parameters. For arrow functions, the parameters
                // have already been parsed.
                using (ParameterDeclarationParsingScope formals_scope = new(this))
                {
                    // We return kPreParseSuccess in failure cases too - errors are retrieved
                    // separately by Parser::SkipLazyFunctionBody.
                    ParseFormalParameterList(formals);
                    if (formals_scope.has_duplicate()) formals.set_has_duplicate();
                    if (!formals.is_simple)
                    {
                        BuildParameterInitializationBlock(formals);
                    }

                    Expect(Token.RightParen);
                    int formals_end_position = scanner().location().end_pos;

                    CheckArityRestrictions(formals.arity, kind, formals.has_rest, function_scope.start_position(),
                                           formals_end_position);
                }
            }

            Expect(Token.LeftBrace);
            DeclarationScope inner_scope = function_scope;

            if (!formals.is_simple)
            {
                inner_scope = NewVarblockScope();
                inner_scope.set_start_position(position());
            }

            using (BlockState block_state = new(this, inner_scope))
            {
                ParseStatementListAndLogFunction(function_literal_id, formals);
            }

            bool allow_duplicate_parameters = false;
            CheckConflictingVarDeclarations(inner_scope);

            if (!has_error())
            {
                if (formals.is_simple)
                {
                    if (is_sloppy(function_scope.language_mode()))
                    {
                        function_scope.HoistSloppyBlockFunctions(null);
                    }

                    allow_duplicate_parameters = is_sloppy(function_scope.language_mode()) && !IsConciseMethod(kind);
                }
                else
                {
                    if (is_sloppy(inner_scope.language_mode()))
                    {
                        inner_scope.HoistSloppyBlockFunctions(null);
                    }

                    SetLanguageMode(function_scope, inner_scope.language_mode());
                    inner_scope.set_end_position(scanner().peek_location().end_pos);
                    if (inner_scope.FinalizeBlockScope() != null)
                    {
                        AstRawString conflict = inner_scope.FindVariableDeclaredIn(
                            function_scope, VariableMode.LastLexicalVariableMode);
                        if (conflict != null)
                        {
                            ReportVarRedeclarationIn(conflict, inner_scope);
                        }
                    }
                }
            }

            use_counts_ = null;

            if (stack_overflow())
            {
                return PreParseResult.kPreParseStackOverflow;
            }
            else if (pending_error_handler().has_error_unidentifiable_by_preparser())
            {
                return PreParseResult.kPreParseNotIdentifiableError;
            }
            else if (has_error())
            {
            }
            else
            {
                if (!IsArrowFunction(kind))
                {
                    // Validate parameter names. We can do this only after parsing the
                    // function, since the function can declare itself strict.
                    ValidateFormalParameters(language_mode(), formals, allow_duplicate_parameters);
                    if (has_error())
                    {
                        if (pending_error_handler().has_error_unidentifiable_by_preparser())
                        {
                            return PreParseResult.kPreParseNotIdentifiableError;
                        }
                        else
                        {
                            return PreParseResult.kPreParseSuccess;
                        }
                    }

                    // Declare arguments after parsing the function since lexical
                    // 'arguments' masks the arguments object. Declare arguments before
                    // declaring the function var since the arguments object masks 'function
                    // arguments'.
                    function_scope.DeclareArguments(ast_value_factory());

                    DeclareFunctionNameVar(function_name, function_syntax_kind, function_scope);

                    if (preparse_data_builder_.HasData())
                    {
                        produced_preparse_data = ProducedPreparseData.For(preparse_data_builder_);
                    }
                }

                if (pending_error_handler().has_error_unidentifiable_by_preparser())
                {
                    return PreParseResult.kPreParseNotIdentifiableError;
                }

                if (is_strict(function_scope.language_mode()))
                {
                    int end_pos = scanner().location().end_pos;
                    CheckStrictOctalLiteral(function_scope.start_position(), end_pos);
                }
            }

            return PreParseResult.kPreParseSuccess;
        }
        finally
        {
            preparse_data_builder_scope.Dispose();
        }
    }

    public override PreParserExpression ParseFunctionLiteral(
        PreParserIdentifier function_name, Scanner.Location function_name_location,
        FunctionNameValidity function_name_validity, FunctionKind kind, int function_token_pos,
        FunctionSyntaxKind function_syntax_kind, LanguageMode language_mode,
        List<AstRawString> arguments_for_wrapped_function)
    {
        using FunctionParsingScope function_parsing_scope = new(this);
        // Wrapped functions are not parsed in the preparser.
        // Function ::
        //   '(' FormalParameterList? ')' '{' FunctionBody '}'

        DeclarationScope function_scope = NewFunctionScope(kind);
        function_scope.SetLanguageMode(language_mode);
        if (function_syntax_kind == FunctionSyntaxKind.Declaration)
        {
            function_scope.set_is_hoisted_in_context(true);
        }
        int function_literal_id = GetNextInfoId();
        bool skippable_function = false;

        // Start collecting data for a new function which might contain skippable
        // functions.
        PreparseDataBuilder.DataGatheringScope preparse_data_builder_scope = new(this);
        try
        {
            skippable_function = !function_state_.next_function_is_likely_called() &&
                                 preparse_data_builder_ != null;
            if (skippable_function)
            {
                preparse_data_builder_scope.Start(function_scope);
            }

            using FunctionState function_state = new(this, function_scope);

            Expect(Token.LeftParen);
            int start_position = position();
            function_scope.set_start_position(start_position);
            PreParserFormalParameters formals = new(function_scope);
            using (ParameterDeclarationParsingScope formals_scope = new(this))
            {
                ParseFormalParameterList(formals);
                if (formals_scope.has_duplicate()) formals.set_has_duplicate();
            }
            Expect(Token.RightParen);
            int formals_end_position = scanner().location().end_pos;

            CheckArityRestrictions(formals.arity, kind, formals.has_rest, start_position, formals_end_position);

            Expect(Token.LeftBrace);

            // Parse function body.
            PreParserScopedStatementList body = PreParserScopedStatementList.New(pointer_buffer());
            int pos = function_token_pos == kNoSourcePosition ? peek_position() : function_token_pos;
            using AcceptINScope scope = new(this, true);
            ParseFunctionBody(body, function_name, pos, formals, kind, function_syntax_kind,
                              FunctionBodyType.kBlock);

            // Parsing the body may change the language mode in our scope.
            language_mode = function_scope.language_mode();

            // Validate name and parameter names. We can do this only after parsing the
            // function, since the function can declare itself strict.
            CheckFunctionName(language_mode, function_name, function_name_validity, function_name_location);

            if (is_strict(language_mode))
            {
                CheckStrictOctalLiteral(start_position, end_position());
            }
            if (skippable_function)
            {
                preparse_data_builder_scope.SetSkippableFunction(function_scope, formals.function_length,
                                                                 GetLastInfoId() - function_literal_id);
            }
        }
        finally
        {
            preparse_data_builder_scope.Dispose();
        }

        return PreParserExpression.Default();
    }

    private void ParseStatementListAndLogFunction(int function_literal_id, PreParserFormalParameters formals)
    {
        PreParserScopedStatementList body = PreParserScopedStatementList.New(pointer_buffer());
        ParseStatementList(body, Token.RightBrace);

        // Position right after terminal '}'.
        int body_end = scanner().peek_location().end_pos;
        log_.LogFunction(body_end, formals.num_parameters(), formals.function_length,
                         GetLastInfoId() - function_literal_id);
    }

    public override PreParserStatement BuildParameterInitializationBlock(PreParserFormalParameters parameters)
    {
        if (scope().AsDeclarationScope().sloppy_eval_can_extend_vars() && preparse_data_builder_ != null)
        {
            // We cannot replicate the Scope structure constructed by the Parser,
            // because we've lost information whether each individual parameter was
            // simple or not. Give up trying to produce data to skip inner functions.
            if (preparse_data_builder_.parent() != null)
            {
                // Lazy parsing started before the current function; the function which
                // cannot contain skippable functions is the parent function. (Its inner
                // functions cannot either; they are implicitly bailed out.)
                preparse_data_builder_.parent().Bailout();
            }
            else
            {
                // Lazy parsing started at the current function; it cannot contain
                // skippable functions.
                preparse_data_builder_.Bailout();
            }
        }

        return PreParserStatement.Default();
    }

    public override bool IdentifierEquals(PreParserIdentifier identifier, AstRawString other)
        => identifier.string_ == other;

    // ----------------------------------------------------------------------
    // preparser.h

    // Indicates that we won't switch from the preparser to the preparser; we'll
    // just stay where we are.
    public override bool AllowsLazyParsingWithoutUnresolvedVariables() => false;
    public override bool parse_lazily() => false;

    public override IRegExpSyntaxValidator regexp_syntax_validator() => null;

    public override bool SkipFunction(int function_literal_id, AstRawString name, FunctionKind kind,
                                      FunctionSyntaxKind function_syntax_kind, DeclarationScope function_scope,
                                      ref int num_parameters, ref int function_length,
                                      ref ProducedPreparseData produced_preparse_data)
        => throw new InvalidOperationException("UNREACHABLE");

    public override PreParserExpression InitializeObjectLiteral(PreParserExpression literal) => literal;

    public override bool HasCheckedSyntax() => false;

    public override object OpenTemplateLiteral(int pos) => null;
    public override void AddTemplateExpression(object state, PreParserExpression expression) { }
    public override void AddTemplateSpan(object state, bool should_cook, bool tail) { }
    public override PreParserExpression CloseTemplateLiteral(object state, int start, PreParserExpression tag)
        => PreParserExpression.Default();

    public override bool IsPrivateReference(PreParserExpression expression) => expression.IsPrivateReference();

    public override void SetLanguageMode(Scope scope, LanguageMode mode) => scope.SetLanguageMode(mode);

    public override void PrepareGeneratorVariables() { }

    public override PreParserStatement RewriteSwitchStatement(PreParserStatement switch_statement, Scope scope)
        => PreParserStatement.Default();

    public override Variable DeclareVariable(AstRawString name, VariableKind kind, VariableMode mode,
                                             InitializationFlag init, Scope scope, out bool was_added,
                                             int position, int end = kNoSourcePosition)
        => DeclareVariableName(name, mode, scope, out was_added, position, kind);

    public override void DeclareAndBindVariable(VariableProxy proxy, VariableKind kind, VariableMode mode,
                                                Scope scope, out bool was_added, int initializer_position,
                                                VariableProxy.BindingMode binding_mode)
    {
        Variable var = DeclareVariableName(proxy.raw_name(), mode, scope, out was_added, proxy.position(), kind);
        var.set_initializer_position(initializer_position);
        // Don't bother actually binding the proxy.
    }

    private Variable DeclarePrivateVariableName(AstRawString name, ClassScope scope, VariableMode mode,
                                                IsStaticFlag is_static_flag, out bool was_added)
        => scope.DeclarePrivateName(name, mode, is_static_flag, out was_added);

    private Variable DeclareVariableName(AstRawString name, VariableMode mode, Scope scope, out bool was_added,
                                         int position = kNoSourcePosition,
                                         VariableKind kind = VariableKind.NORMAL_VARIABLE)
    {
        Variable var = scope.DeclareVariableName(name, mode, out was_added, kind);
        if (var == null)
        {
            ReportUnidentifiableError();
            if (!IsLexicalVariableMode(mode)) scope = scope.GetDeclarationScope();
            var = scope.LookupLocal(name);
        }
        else if (var.scope() != scope)
        {
            Declaration nested_declaration =
                factory().ast_node_factory().NewNestedVariableDeclaration(scope, position);
            nested_declaration.set_var(var);
            var.scope().declarations().Add(nested_declaration);
        }
        return var;
    }

    public override PreParserStatement RewriteCatchPattern(CatchInfo catch_info) => PreParserStatement.Default();

    public override void ReportVarRedeclarationIn(AstRawString name, Scope scope) => ReportUnidentifiableError();

    public override PreParserStatement RewriteTryStatement(PreParserStatement try_block,
                                                           PreParserStatement catch_block, SourceRange catch_range,
                                                           PreParserStatement finally_block,
                                                           SourceRange finally_range, CatchInfo catch_info, int pos)
        => PreParserStatement.Default();

    public override void ReportUnexpectedTokenAt(Scanner.Location location, Token token,
                                                 MessageTemplate message = MessageTemplate.UnexpectedToken)
        => ReportUnidentifiableError();

    public override void ParseGeneratorFunctionBody(int pos, FunctionKind kind, PreParserScopedStatementList body)
        => ParseStatementList(body, Token.RightBrace);

    public override void ParseAsyncGeneratorFunctionBody(int pos, FunctionKind kind,
                                                         PreParserScopedStatementList body)
        => ParseStatementList(body, Token.RightBrace);

    private void DeclareFunctionNameVar(AstRawString function_name, FunctionSyntaxKind function_syntax_kind,
                                        DeclarationScope function_scope)
    {
        if (function_syntax_kind == FunctionSyntaxKind.NamedExpression &&
            function_scope.LookupLocal(function_name) == null)
        {
            function_scope.DeclareFunctionVar(function_name);
        }
    }

    public override void DeclareFunctionNameVar(PreParserIdentifier function_name,
                                                FunctionSyntaxKind function_syntax_kind,
                                                DeclarationScope function_scope)
        => DeclareFunctionNameVar(function_name.string_, function_syntax_kind, function_scope);

    public override PreParserStatement DeclareFunction(PreParserIdentifier variable_name,
                                                       PreParserExpression function, VariableMode mode,
                                                       VariableKind kind, int beg_pos, int end_pos,
                                                       List<AstRawString> names)
    {
        Variable var = DeclareVariableName(variable_name.string_, mode, scope(), out bool was_added, beg_pos, kind);
        if (kind == VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE)
        {
            Token init = loop_nesting_depth() > 0 ? Token.Assign : Token.Init;
            SloppyBlockFunctionStatement statement =
                factory().ast_node_factory().NewSloppyBlockFunctionStatement(end_pos, var, init);
            GetDeclarationScope().DeclareSloppyBlockFunction(statement);
        }
        return PreParserStatement.Default();
    }

    public override PreParserStatement DeclareClass(PreParserIdentifier variable_name, PreParserExpression value,
                                                    List<AstRawString> names, int class_token_pos, int end_pos)
    {
        // Preparser shouldn't be used in contexts where we need to track the names.
        DeclareVariableName(variable_name.string_, VariableMode.Let, scope(), out _);
        return PreParserStatement.Default();
    }

    public override void DeclareClassVariable(ClassScope scope, PreParserIdentifier name, ClassInfo class_info,
                                              int class_token_pos)
    {
        // Declare a special class variable for anonymous classes with the dot
        // if we need to save it for static private method access.
        scope.DeclareClassVariable(ast_value_factory(), name.string_, class_token_pos);
    }

    public override void DeclarePublicClassMethod(PreParserIdentifier class_name, PreParserExpression property,
                                                  bool is_constructor, ClassInfo class_info) { }

    public override void AddInstanceFieldOrStaticElement(PreParserExpression property, ClassInfo class_info,
                                                         bool is_static) { }

    public override void DeclarePublicClassField(ClassScope scope, PreParserExpression property, bool is_static,
                                                 bool is_computed_name, ClassInfo class_info)
    {
        if (is_computed_name)
        {
            DeclareVariableName(ClassFieldVariableName(ast_value_factory(), class_info.computed_field_count),
                                VariableMode.Const, scope, out _);
        }
    }

    public override void DeclarePrivateClassMember(ClassScope scope, PreParserIdentifier property_name,
                                                   PreParserExpression property, ClassLiteralProperty.Kind kind,
                                                   bool is_static, ClassInfo class_info)
    {
        DeclarePrivateVariableName(property_name.string_, scope, GetVariableMode(kind),
                                   is_static ? IsStaticFlag.Static : IsStaticFlag.NotStatic, out bool was_added);
        if (!was_added)
        {
            Scanner.Location loc = new(position(), end_position());
            ReportMessageAt(loc, MessageTemplate.VarRedeclaration, property_name.string_);
        }
    }

    public override void AddClassStaticBlock(PreParserStatement block, ClassInfo class_info) { }

    private void AddSyntheticFunctionDeclaration(FunctionKind kind, int pos)
    {
        // Creating and disposing of a FunctionState makes tracking of
        // next_function_is_likely_called match what Parser does. TODO(marja):
        // Make the lazy function + next_function_is_likely_called + default ctor
        // logic less surprising. Default ctors shouldn't affect the laziness of
        // functions.
        DeclarationScope function_scope = NewFunctionScope(kind);
        SetLanguageMode(function_scope, LanguageMode.Strict);
        function_scope.set_start_position(pos);
        function_scope.set_end_position(pos);
        using FunctionState function_state = new(this, function_scope);
        GetNextInfoId();
    }

    public override PreParserExpression RewriteClassLiteral(ClassScope scope, PreParserIdentifier name,
                                                            ClassInfo class_info, int pos)
    {
        bool has_default_constructor = !class_info.has_seen_constructor;
        // Account for the default constructor.
        if (has_default_constructor)
        {
            bool has_extends = class_info.extends.IsNull();
            FunctionKind kind = has_extends
                ? FunctionKind.DefaultDerivedConstructor
                : FunctionKind.DefaultBaseConstructor;
            AddSyntheticFunctionDeclaration(kind, pos);
        }
        return PreParserExpression.Default();
    }

    public override PreParserStatement DeclareNative(PreParserIdentifier name, int pos)
        => PreParserStatement.Default();

    // Helper functions for recursive descent.
    public override bool IsEval(PreParserIdentifier identifier) => identifier.IsEval();
    public bool IsAsync(PreParserIdentifier identifier) => identifier.IsAsync();
    public override bool IsArguments(PreParserIdentifier identifier) => identifier.IsArguments();
    public override bool IsEvalOrArguments(PreParserIdentifier identifier) => identifier.IsEvalOrArguments();

    // Returns true if the expression is of type "this.foo".
    public override bool IsThisProperty(PreParserExpression expression) => expression.IsThisProperty();

    public override bool IsIdentifier(PreParserExpression expression) => expression.IsIdentifier();

    public override PreParserIdentifier AsIdentifier(PreParserExpression expression) => expression.AsIdentifier();

    public override bool IsConstructor(PreParserIdentifier identifier) => identifier.IsConstructor();

    // PreParser doesn't count boilerplate properties.
    public override bool IsBoilerplateProperty(PreParserExpression property) => false;

    // Preparsing is disabled for extensions (because the extension
    // details aren't passed to lazily compiled functions), so we
    // don't accept "native function" in the preparser and there is
    // no need to keep track of "native".
    public override bool ParsingExtension() => false;
    public override bool IsNative(PreParserExpression expr) => false;

    public override bool IsArrayIndex(PreParserIdentifier @string, out uint index)
    {
        index = 0;
        return false;
    }

    public override bool IsStringLiteral(PreParserStatement statement) => statement.IsStringLiteral();

    public override void GetDefaultStrings(out PreParserIdentifier default_string,
                                           out PreParserIdentifier dot_default_string)
    {
        // V8 leaves the out-parameters untouched; they are default-constructed
        // (unknown identifiers) at the call sites.
        default_string = PreParserIdentifier.Default();
        dot_default_string = PreParserIdentifier.Default();
    }

    // Functions for encapsulating the differences between parsing and preparsing;
    // operations interleaved with the recursive descent.
    public override void PushLiteralName(PreParserIdentifier id) { }
    public override void PushPropertyName(PreParserExpression expression) { }
    public override void PushEnclosingName(PreParserIdentifier name) { }
    public override void AddFunctionForNameInference(PreParserExpression expression) { }
    public override void InferFunctionName() { }

    public override void CheckAssigningFunctionLiteralToProperty(PreParserExpression left,
                                                                 PreParserExpression right) { }

    public override bool ShortcutLiteralBinaryExpression(ref PreParserExpression x, PreParserExpression y, Token op,
                                                         int pos)
        => false;

    public override bool CollapseConditionalChain(ref PreParserExpression x, PreParserExpression cond,
                                                  PreParserExpression then_expression,
                                                  PreParserExpression else_expression, int pos,
                                                  SourceRange then_range)
        => false;

    public override void AppendConditionalChainElse(ref PreParserExpression x, SourceRange else_range) { }

    public override bool CollapseNaryExpression(ref PreParserExpression x, PreParserExpression y, Token op, int pos,
                                                SourceRange range)
    {
        x.clear_parenthesized();
        return false;
    }

    public override PreParserExpression BuildUnaryExpression(PreParserExpression expression, Token op, int pos)
        => PreParserExpression.Default();

    public override PreParserStatement BuildInitializationBlock(DeclarationParsingResult parsing_result)
        => PreParserStatement.Default();

    public override PreParserStatement RewriteForVarInLegacy(ForInfo for_info) => PreParserStatement.Null();

    public override void DesugarBindingInForEachStatement(ForInfo for_info, ref PreParserStatement body_block,
                                                          ref PreParserExpression each_variable) { }

    public override PreParserStatement CreateForEachStatementTDZ(PreParserStatement init_block, ForInfo for_info)
    {
        if (IsLexicalVariableMode(for_info.parsing_result.descriptor.mode))
        {
            foreach (AstRawString name in for_info.bound_names)
            {
                DeclareVariableName(name, VariableMode.Let, scope(), out _);
            }
            return PreParserStatement.Default();
        }
        return init_block;
    }

    public override PreParserStatement DesugarLexicalBindingsInForStatement(
        PreParserStatement loop, PreParserStatement init, PreParserExpression cond, PreParserStatement next,
        PreParserStatement body, Scope inner_scope, ForInfo for_info)
    {
        // See Parser::DesugarLexicalBindingsInForStatement.
        foreach (AstRawString name in for_info.bound_names)
        {
            DeclareVariableName(name, for_info.parsing_result.descriptor.mode, inner_scope, out _);
        }
        return loop;
    }

    public override void InsertSloppyBlockFunctionVarBindings(DeclarationScope scope)
        => scope.HoistSloppyBlockFunctions(null);

    public override void InsertShadowingVarBindingInitializers(PreParserStatement block) { }

    public override PreParserExpression NewThrowReferenceError(MessageTemplate message, int pos)
        => PreParserExpression.Default();

    public override AstRawString IdentifierToAstRawString(PreParserIdentifier x) => x.string_;

    public void ReportUnidentifiableError()
    {
        pending_error_handler().set_unidentifiable_error();
        scanner().set_parser_error();
    }

    public override AstRawString GetRawNameFromIdentifier(PreParserIdentifier arg) => arg.string_;

    public override PreParserStatement AsIterationStatement(PreParserStatement s) => s;

    // "null" return type creators.
    public override PreParserIdentifier NullIdentifier() => PreParserIdentifier.Null();
    public override PreParserExpression NullExpression() => PreParserExpression.Null();
    public override PreParserExpression FailureExpression() => PreParserExpression.Failure();
    public override PreParserStatement NullStatement() => PreParserStatement.Null();
    public override PreParserStatement NullBlock() => PreParserStatement.Null();
    public override PreParserExpression NullFunctionLiteral() => PreParserExpression.Null();

    public override bool IsNull(PreParserIdentifier subject) => subject.IsNull();
    public override bool IsNull(PreParserExpression subject) => subject.IsNull();
    public override bool IsNull(PreParserStatement subject) => subject.IsNull();
    public override bool IsNullProperty(PreParserExpression subject) => subject.IsNull();

    public override bool IsIterationStatement(PreParserStatement subject) => subject.IsIterationStatement();

    public override PreParserIdentifier EmptyIdentifierString()
    {
        PreParserIdentifier result = PreParserIdentifier.Default();
        result.string_ = ast_value_factory().empty_string();
        return result;
    }

    public override bool IsEmptyIdentifier(PreParserIdentifier subject) => subject.string_.IsEmpty();

    // Producing data during the recursive descent.
    public override PreParserIdentifier GetSymbol() => PreParserIdentifier.Default();
    public PreParserIdentifier GetNextSymbol() => PreParserIdentifier.Default();
    public override PreParserIdentifier GetNumberAsSymbol() => PreParserIdentifier.Default();
    public override PreParserIdentifier GetBigIntAsSymbol() => PreParserIdentifier.Default();

    public override PreParserExpression ThisExpression()
    {
        UseThis();
        return PreParserExpression.This();
    }

    public override PreParserExpression NewThisExpression(int pos)
    {
        UseThis();
        return PreParserExpression.This();
    }

    public override PreParserExpression NewSuperPropertyReference(int pos) => PreParserExpression.Default();

    public override PreParserExpression NewSuperCallReference(int pos)
    {
        scope().NewUnresolved(factory().ast_node_factory(), ast_value_factory().dot_this_function_string(), pos,
                              VariableKind.NORMAL_VARIABLE);
        scope().NewUnresolved(factory().ast_node_factory(), ast_value_factory().dot_new_target_string(), pos,
                              VariableKind.NORMAL_VARIABLE);
        return PreParserExpression.SuperCallReference();
    }

    public override PreParserExpression NewTargetExpression(int pos) => PreParserExpression.Default();

    public override PreParserExpression ImportMetaExpression(int pos) => PreParserExpression.Default();

    public override PreParserExpression ExpressionFromLiteral(Token token, int pos)
    {
        if (token != Token.String) return PreParserExpression.Default();
        return PreParserExpression.StringLiteral();
    }

    public override PreParserExpression ExpressionFromPrivateName(ref PrivateNameScopeIterator private_name_scope,
                                                                  PreParserIdentifier name, int start_position)
    {
        VariableProxy proxy = factory().ast_node_factory().NewVariableProxy(name.string_,
                                                                            VariableKind.NORMAL_VARIABLE,
                                                                            start_position);
        private_name_scope.AddUnresolvedPrivateName(proxy);
        return PreParserExpression.FromIdentifier(name);
    }

    public override PreParserExpression ExpressionFromIdentifier(PreParserIdentifier name, int start_position,
                                                                 InferName infer = InferName.kYes)
    {
        expression_scope().NewVariable(name.string_, start_position);
        return PreParserExpression.FromIdentifier(name);
    }

    public override void DeclareIdentifier(PreParserIdentifier name, int start_position)
        => expression_scope().Declare(name.string_, start_position);

    public override Variable DeclareCatchVariableName(Scope scope, PreParserIdentifier identifier)
        => scope.DeclareCatchVariableName(identifier.string_);

    public override PreParserPropertyList NewClassPropertyList(int size) => default;

    public override PreParserPropertyList NewClassStaticElementList(int size) => default;

    public override PreParserExpression NewClassLiteralPropertyWithAccessorInfo(
        ClassScope scope, ClassInfo class_info, PreParserIdentifier name, PreParserExpression key,
        PreParserExpression value, bool is_static, bool is_computed_name, bool is_private, int pos)
    {
        // Declare the accessor storage name variable and generated getter and
        // setter.
        DeclareVariableName(AutoAccessorVariableName(ast_value_factory(), class_info.autoaccessor_count++),
                            VariableMode.Const, scope, out _);
        FunctionKind kind = is_static ? FunctionKind.GetterFunction : FunctionKind.StaticGetterFunction;
        AddSyntheticFunctionDeclaration(kind, pos + 1);
        kind = is_static ? FunctionKind.SetterFunction : FunctionKind.StaticSetterFunction;
        AddSyntheticFunctionDeclaration(kind, pos + 2);
        return factory().NewClassLiteralProperty(key, value, ClassLiteralProperty.Kind.AUTO_ACCESSOR, is_static,
                                                 is_computed_name, is_private);
    }

    public override PreParserExpression NewV8Intrinsic(PreParserIdentifier name, PreParserExpressionList arguments,
                                                       int pos)
        => PreParserExpression.Default();

    public override PreParserStatement NewThrowStatement(PreParserExpression exception, int pos)
        => PreParserStatement.Jump();

    public override PreParserFormalParameters NewFormalParameters(DeclarationScope scope) => new(scope);

    public override void ValidateDuplicate(PreParserFormalParameters parameters) => parameters.ValidateDuplicate(this);

    public override void ValidateStrictMode(PreParserFormalParameters parameters)
        => parameters.ValidateStrictMode(this);

    public override void AddFormalParameter(PreParserFormalParameters parameters, PreParserExpression pattern,
                                            PreParserExpression initializer, int initializer_end_position,
                                            bool is_rest)
    {
        DeclarationScope scope = parameters.scope;
        scope.RecordParameter(is_rest);
        parameters.UpdateArityAndFunctionLength(!initializer.IsNull(), is_rest);
    }

    public override void ReindexArrowFunctionFormalParameters(PreParserFormalParameters parameters,
                                                              AllowReindexScope scope) { }

    public override void ReindexComputedMemberName(PreParserExpression expression, AllowReindexScope scope) { }

    public override void DeclareFormalParameters(PreParserFormalParameters parameters)
    {
        if (!parameters.is_simple) parameters.scope.SetHasNonSimpleParameters();
    }

    public override void DeclareArrowFunctionFormalParameters(PreParserFormalParameters parameters,
                                                              PreParserExpression @params,
                                                              Scanner.Location params_loc) { }

    public override PreParserExpression ExpressionListToExpression(PreParserExpressionList args)
        => PreParserExpression.Default();

    public override void SetFunctionNameFromPropertyName(PreParserExpression property, PreParserIdentifier name,
                                                         AstRawString prefix = null) { }

    public override void SetFunctionNameFromClassPropertyName(PreParserExpression property,
                                                              PreParserIdentifier name,
                                                              AstRawString prefix = null) { }

    public override void SetFunctionNameFromIdentifierRef(PreParserExpression value,
                                                          PreParserExpression identifier) { }

    public override void CountUsage(UseCounterFeature feature)
    {
        if (use_counts_ != null) ++use_counts_[(int)feature];
    }

    public override bool ParsingDynamicFunctionDeclaration() => false;

    public override FunctionLiteral.EagerCompileHint GetEmbedderCompileHint(
        FunctionLiteral.EagerCompileHint current_compile_hint, int position)
        => current_compile_hint;

    public override bool IsTaggedTemplateCall(PreParserExpression expression) => expression.is_tagged_template();

    public override void SetShouldEagerCompile(PreParserExpression function_literal)
        => function_literal.SetShouldEagerCompile();

    // Generate empty functions here as the preparser does not collect source
    // ranges for block coverage.
    public override void RecordBinaryOperationSourceRange(PreParserExpression node, SourceRange right_range) { }
    public override void RecordBlockSourceRange(PreParserStatement node, int continuation_position) { }
    public override void RecordCaseClauseSourceRange(object node, SourceRange body_range) { }
    public override void RecordConditionalSourceRange(PreParserExpression node, SourceRange then_range,
                                                      SourceRange else_range) { }
    public override void RecordExpressionSourceRange(PreParserExpression node, SourceRange right_range) { }
    public override void RecordFunctionLiteralSourceRange(PreParserExpression node) { }
    public override void RecordIfStatementSourceRange(PreParserStatement node, SourceRange then_range,
                                                      SourceRange else_range) { }
    public override void RecordIterationStatementSourceRange(PreParserStatement node, SourceRange body_range) { }
    public override void RecordJumpStatementSourceRange(PreParserStatement node, int continuation_position) { }
    public override void RecordSuspendSourceRange(PreParserExpression node, int continuation_position) { }
    public override void RecordSwitchStatementSourceRange(PreParserStatement node, int continuation_position) { }
    public override void RecordThrowSourceRange(PreParserStatement node, int continuation_position) { }

    // PreParserStatement::Initialize and cases() are dummies.
    public override void InitializeConditionalLoop(PreParserStatement loop, PreParserExpression cond,
                                                   PreParserStatement body) { }
    public override void InitializeForLoop(PreParserStatement loop, PreParserStatement init,
                                           PreParserExpression cond, PreParserStatement next,
                                           PreParserStatement body) { }
    public override void InitializeForEachStatement(PreParserStatement loop, PreParserExpression each,
                                                    PreParserExpression subject, PreParserStatement body,
                                                    Scope subject_scope) { }
    public override void AddCaseClause(PreParserStatement switch_statement, object clause) { }

    // Preparser's private field members.

    private int[] use_counts_;
    private readonly PreParserLogger log_ = new();

    private PreparseDataBuilder preparse_data_builder_;
}
