// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/parser.cc: modules, declarations, desugarings, function
// literals and lazy function skipping, classes, template literals, function
// name inference.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public sealed partial class Parser
{
    private readonly struct ExportClauseData(AstRawString export_name, AstRawString local_name,
                                             Scanner.Location location)
    {
        public readonly AstRawString export_name = export_name;
        public readonly AstRawString local_name = local_name;
        public readonly Scanner.Location location = location;
    }

    private sealed class NamedImport(AstRawString import_name, AstRawString local_name, Scanner.Location location)
    {
        public readonly AstRawString import_name = import_name;
        public readonly AstRawString local_name = local_name;
        public readonly Scanner.Location location = location;
    }

    private Statement ParseModuleItem()
    {
        // https://tc39.es/ecma262/#prod-ModuleItem
        // ModuleItem :
        //    ImportDeclaration
        //    ExportDeclaration
        //    StatementListItem

        Token next = peek();

        if (next == Token.Export)
        {
            return ParseExportDeclaration();
        }

        if (next == Token.Import)
        {
            // We must be careful not to parse a dynamic import expression as an import
            // declaration. Same for import.meta expressions.
            Token peek_ahead = PeekAhead();
            if (peek_ahead != Token.LeftParen && peek_ahead != Token.Period)
            {
                ParseImportDeclaration();
                return factory().EmptyStatement();
            }
        }

        return ParseStatementListItem();
    }

    private void ParseModuleItemList(ScopedPtrList<Statement> body)
    {
        // https://tc39.es/ecma262/#prod-Module
        // Module :
        //    ModuleBody?
        //
        // https://tc39.es/ecma262/#prod-ModuleItemList
        // ModuleBody :
        //    ModuleItem*

        while (peek() != Token.Eos)
        {
            Statement stat = ParseModuleItem();
            if (stat == null) return;
            if (stat.IsEmptyStatement()) continue;
            body.Add(stat);
        }
    }

    private AstRawString ParseModuleSpecifier()
    {
        // ModuleSpecifier :
        //    StringLiteral

        Expect(Token.String);
        return GetSymbol();
    }

    private List<ExportClauseData> ParseExportClause(ref Scanner.Location reserved_loc,
                                                     ref Scanner.Location string_literal_local_name_loc)
    {
        // ExportClause :
        //   '{' '}'
        //   '{' ExportsList '}'
        //   '{' ExportsList ',' '}'
        //
        // ExportsList :
        //   ExportSpecifier
        //   ExportsList ',' ExportSpecifier
        //
        // ExportSpecifier :
        //   IdentifierName
        //   IdentifierName 'as' IdentifierName
        //   IdentifierName 'as' ModuleExportName
        //   ModuleExportName
        //   ModuleExportName 'as' ModuleExportName
        //
        // ModuleExportName :
        //   StringLiteral
        List<ExportClauseData> export_data = [];

        Expect(Token.LeftBrace);

        Token name_tok;
        while ((name_tok = peek()) != Token.RightBrace)
        {
            AstRawString local_name = ParseExportSpecifierName();
            if (!string_literal_local_name_loc.IsValid() && name_tok == Token.String)
            {
                // Keep track of the first string literal local name exported for error
                // reporting. These must be followed by a 'from' clause.
                string_literal_local_name_loc = scanner().location();
            }
            else if (!reserved_loc.IsValid() &&
                     !Token.IsValidIdentifier(name_tok, LanguageMode.Strict, false, flags().is_module()))
            {
                // Keep track of the first reserved word encountered in case our
                // caller needs to report an error.
                reserved_loc = scanner().location();
            }
            AstRawString export_name;
            Scanner.Location location = scanner().location();
            if (CheckContextualKeyword(ast_value_factory().as_string()))
            {
                export_name = ParseExportSpecifierName();
                // Set the location to the whole "a as b" string, so that it makes sense
                // both for errors due to "a" and for errors due to "b".
                location.end_pos = scanner().location().end_pos;
            }
            else
            {
                export_name = local_name;
            }
            export_data.Add(new ExportClauseData(export_name, local_name, location));
            if (peek() == Token.RightBrace) break;
            if (!Check(Token.Comma))
            {
                ReportUnexpectedToken(Next());
                break;
            }
        }

        Expect(Token.RightBrace);
        return export_data;
    }

    private AstRawString ParseExportSpecifierName()
    {
        Token next = Next();

        // IdentifierName
        if (Token.IsPropertyName(next))
        {
            return GetSymbol();
        }

        // ModuleExportName
        if (next == Token.String)
        {
            AstRawString export_name = GetSymbol();
            if (export_name.is_one_byte()) return export_name;
            if (!HasUnpairedSurrogate(export_name.Value))
            {
                return export_name;
            }
            ReportMessage(MessageTemplate.InvalidModuleExportName);
            return EmptyIdentifierString();
        }

        ReportUnexpectedToken(next);
        return EmptyIdentifierString();
    }

    // unibrow::Utf16::HasUnpairedSurrogate.
    private static bool HasUnpairedSurrogate(string code_units)
    {
        for (int i = 0; i < code_units.Length; ++i)
        {
            int code_unit = code_units[i];
            if (Utf16.IsLeadSurrogate(code_unit))
            {
                // The current code unit is a leading surrogate. Check if it is followed by a
                // trailing surrogate.
                if (i == code_units.Length - 1) return true;
                if (!Utf16.IsTrailSurrogate(code_units[i + 1])) return true;
                // Skip the paired trailing surrogate.
                ++i;
            }
            else if (Utf16.IsTrailSurrogate(code_unit))
            {
                // All paired trailing surrogates are skipped above, so this branch is
                // only for those that are unpaired.
                return true;
            }
        }
        return false;
    }

    private List<NamedImport> ParseNamedImports(int pos)
    {
        // NamedImports :
        //   '{' '}'
        //   '{' ImportsList '}'
        //   '{' ImportsList ',' '}'
        //
        // ImportsList :
        //   ImportSpecifier
        //   ImportsList ',' ImportSpecifier
        //
        // ImportSpecifier :
        //   BindingIdentifier
        //   IdentifierName 'as' BindingIdentifier
        //   ModuleExportName 'as' BindingIdentifier

        Expect(Token.LeftBrace);

        List<NamedImport> result = new(1);
        while (peek() != Token.RightBrace)
        {
            AstRawString import_name = ParseExportSpecifierName();
            AstRawString local_name = import_name;
            Scanner.Location location = scanner().location();
            // In the presence of 'as', the left-side of the 'as' can
            // be any IdentifierName. But without 'as', it must be a valid
            // BindingIdentifier.
            if (CheckContextualKeyword(ast_value_factory().as_string()))
            {
                local_name = ParsePropertyName();
            }
            if (!Token.IsValidIdentifier(scanner().current_token(), LanguageMode.Strict, false, flags().is_module()))
            {
                ReportMessage(MessageTemplate.UnexpectedReserved);
                return null;
            }
            else if (IsEvalOrArguments(local_name))
            {
                ReportMessage(MessageTemplate.StrictEvalArguments);
                return null;
            }

            DeclareUnboundVariable(local_name, VariableMode.Const, InitializationFlag.kNeedsInitialization,
                                   position());

            NamedImport import = new(import_name, local_name, location);
            result.Add(import);

            if (peek() == Token.RightBrace) break;
            Expect(Token.Comma);
        }

        Expect(Token.RightBrace);
        return result;
    }

    private ImportAttributes ParseImportWithOrAssertClause()
    {
        // WithClause :
        //    with '{' '}'
        //    with '{' WithEntries ','? '}'

        // WithEntries :
        //    LiteralPropertyName
        //    LiteralPropertyName ':' StringLiteral , WithEntries

        ImportAttributes import_attributes = new();

        if (v8_flags().harmony_import_attributes && Check(Token.With))
        {
            // 'with' keyword consumed
        }
        else
        {
            return import_attributes;
        }

        Expect(Token.LeftBrace);

        while (peek() != Token.RightBrace)
        {
            AstRawString attribute_key = Check(Token.String) ? GetSymbol() : ParsePropertyName();

            Scanner.Location location = scanner().location();

            Expect(Token.Colon);
            Expect(Token.String);

            AstRawString attribute_value = GetSymbol();

            // Set the location to the whole "key: 'value'"" string, so that it makes
            // sense both for errors due to the key and errors due to the value.
            location.end_pos = scanner().location().end_pos;

            bool inserted = import_attributes.TryAdd(attribute_key, (attribute_value, location));
            if (!inserted)
            {
                // It is a syntax error if two WithEntries have the same key.
                ReportMessageAt(location, MessageTemplate.ImportAttributesDuplicateKey, attribute_key);
                break;
            }

            if (peek() == Token.RightBrace) break;
            if (!Check(Token.Comma))
            {
                ReportUnexpectedToken(Next());
                break;
            }
        }

        Expect(Token.RightBrace);

        return import_attributes;
    }

    private void ParseImportDeclaration()
    {
        // ImportDeclaration :
        //   'import' ImportClause 'from' ModuleSpecifier ';'
        //   'import' ModuleSpecifier ';'
        //   'import' ImportClause 'from' ModuleSpecifier [no LineTerminator here]
        //       AssertClause ';'
        //   'import' ModuleSpecifier [no LineTerminator here] AssertClause';'
        //   'import' 'source' ImportedBinding 'from' ModuleSpecifier ';'
        //   'import' 'defer'  NameSpaceImport FromClause WithClause_opt ';'
        //
        // ImportClause :
        //   ImportedDefaultBinding
        //   NameSpaceImport
        //   NamedImports
        //   ImportedDefaultBinding ',' NameSpaceImport
        //   ImportedDefaultBinding ',' NamedImports
        //
        // NameSpaceImport :
        //   '*' 'as' ImportedBinding

        int pos = peek_position();
        Expect(Token.Import);

        Token tok = peek();

        // 'import' ModuleSpecifier ';'
        if (tok == Token.String)
        {
            Scanner.Location specifier_loc0 = scanner().peek_location();
            AstRawString module_specifier0 = ParseModuleSpecifier();
            ImportAttributes import_attributes0 = ParseImportWithOrAssertClause();
            ExpectSemicolon();
            module().AddEmptyImport(module_specifier0, import_attributes0, specifier_loc0);
            return;
        }

        // Parse ImportedDefaultBinding or 'source' ImportedBinding if present.
        AstRawString import_default_binding = null;
        Scanner.Location import_default_binding_loc = default;
        ModuleImportPhase import_phase = ModuleImportPhase.kEvaluation;
        if (tok != Token.Mul && tok != Token.LeftBrace)
        {
            if (v8_flags().js_source_phase_imports && PeekContextualKeyword(ast_value_factory().source_string()) &&
                PeekAhead() == Token.Identifier && PeekAheadAhead() == Token.Identifier)
            {
                Consume(Token.Identifier);
                import_phase = ModuleImportPhase.kSource;
            }
            else if (v8_flags().js_defer_import_eval && PeekContextualKeyword(ast_value_factory().defer_string()) &&
                     PeekAhead() == Token.Mul && PeekAheadAhead() == Token.Identifier)
            {
                Consume(Token.Identifier);
                import_phase = ModuleImportPhase.kDefer;
            }

            // 'import defer' is only allowed with namespaced import
            if (import_phase != ModuleImportPhase.kDefer)
            {
                import_default_binding = ParseNonRestrictedIdentifier();
                import_default_binding_loc = scanner().location();
                DeclareUnboundVariable(import_default_binding, VariableMode.Const,
                                       InitializationFlag.kNeedsInitialization, pos);
            }
        }

        // Parse NameSpaceImport or NamedImports if present.
        AstRawString module_namespace_binding = null;
        Scanner.Location module_namespace_binding_loc = default;
        List<NamedImport> named_imports = null;
        if (import_phase != ModuleImportPhase.kSource && (import_default_binding == null || Check(Token.Comma)))
        {
            switch (peek())
            {
                case Token.Mul:
                {
                    Consume(Token.Mul);
                    ExpectContextualKeyword(ast_value_factory().as_string());
                    module_namespace_binding = ParseNonRestrictedIdentifier();
                    module_namespace_binding_loc = scanner().location();
                    DeclareUnboundVariable(module_namespace_binding, VariableMode.Const,
                                           InitializationFlag.kCreatedInitialized, pos);
                    break;
                }

                case Token.LeftBrace:
                    if (import_phase == ModuleImportPhase.kDefer)
                    {
                        ReportUnexpectedToken(scanner().current_token());
                        return;
                    }
                    named_imports = ParseNamedImports(pos);
                    break;

                default:
                    ReportUnexpectedToken(scanner().current_token());
                    return;
            }
        }

        ExpectContextualKeyword(ast_value_factory().from_string());
        Scanner.Location specifier_loc = scanner().peek_location();
        AstRawString module_specifier = ParseModuleSpecifier();
        // TODO(42204365): Enable import attributes with source phase import once
        // specified.
        ImportAttributes import_attributes = import_phase != ModuleImportPhase.kSource
            ? ParseImportWithOrAssertClause()
            : new ImportAttributes();
        ExpectSemicolon();

        // Now that we have all the information, we can make the appropriate
        // declarations.

        // TODO(neis): Would prefer to call DeclareVariable for each case below rather
        // than above and in ParseNamedImports, but then a possible error message
        // would point to the wrong location.  Maybe have a DeclareAt version of
        // Declare that takes a location?

        if (module_namespace_binding != null)
        {
            module().AddStarImport(module_namespace_binding, module_specifier, import_phase, import_attributes,
                                   module_namespace_binding_loc, specifier_loc);
        }

        if (import_default_binding != null)
        {
            module().AddImport(ast_value_factory().default_string(), import_default_binding, module_specifier,
                               import_phase, import_attributes, import_default_binding_loc, specifier_loc);
        }

        if (named_imports != null)
        {
            if (named_imports.Count == 0)
            {
                module().AddEmptyImport(module_specifier, import_attributes, specifier_loc);
            }
            else
            {
                foreach (NamedImport import in named_imports)
                {
                    module().AddImport(import.import_name, import.local_name, module_specifier, import_phase,
                                       import_attributes, import.location, specifier_loc);
                }
            }
        }
    }

    private Statement ParseExportDefault()
    {
        //  Supports the following productions, starting after the 'default' token:
        //    'export' 'default' HoistableDeclaration
        //    'export' 'default' ClassDeclaration
        //    'export' 'default' AssignmentExpression[In] ';'

        Expect(Token.Default);
        Scanner.Location default_loc = scanner().location();

        List<AstRawString> local_names = new(1);
        Statement result = null;
        switch (peek())
        {
            case Token.Function:
                result = ParseHoistableDeclaration(local_names, true);
                break;

            case Token.Class:
                Consume(Token.Class);
                result = ParseClassDeclaration(local_names, true);
                break;

            case Token.Async:
                if (PeekAhead() == Token.Function && !scanner().HasLineTerminatorAfterNext())
                {
                    Consume(Token.Async);
                    result = ParseAsyncFunctionDeclaration(local_names, true);
                    break;
                }
                goto default;

            default:
            {
                int pos = position();
                using AcceptINScope accept_in = new(this, true);
                Expression value = ParseAssignmentExpression();
                SetFunctionName(value, ast_value_factory().default_string());

                AstRawString local_name = ast_value_factory().dot_default_string();
                local_names.Add(local_name);

                // It's fine to declare this as VariableMode::kConst because the user has
                // no way of writing to it.
                VariableProxy proxy = DeclareBoundVariable(local_name, VariableMode.Const, pos);
                proxy.var().set_initializer_position(position());

                Assignment assignment = factory().NewAssignment(Token.Init, proxy, value, kNoSourcePosition);
                result = IgnoreCompletion(factory().NewExpressionStatement(assignment, kNoSourcePosition));

                ExpectSemicolon();
                break;
            }
        }

        if (result != null)
        {
            module().AddExport(local_names[0], ast_value_factory().default_string(), default_loc);
        }

        return result;
    }

    // Generate the next internal variable name for binding an exported namespace
    // object (used to implement the "export * as" syntax).
    private AstRawString NextInternalNamespaceExportName()
    {
        const string prefix = ".ns-export";
        string s = prefix + (number_of_named_namespace_exports_++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ast_value_factory().GetOneByteString(s);
    }

    private void ParseExportStar()
    {
        int pos = position();
        Consume(Token.Mul);

        if (!PeekContextualKeyword(ast_value_factory().as_string()))
        {
            // 'export' '*' 'from' ModuleSpecifier ';'
            Scanner.Location loc = scanner().location();
            ExpectContextualKeyword(ast_value_factory().from_string());
            Scanner.Location specifier_loc0 = scanner().peek_location();
            AstRawString module_specifier0 = ParseModuleSpecifier();
            ImportAttributes import_attributes0 = ParseImportWithOrAssertClause();
            ExpectSemicolon();
            module().AddStarExport(module_specifier0, import_attributes0, loc, specifier_loc0);
            return;
        }

        // 'export' '*' 'as' IdentifierName 'from' ModuleSpecifier ';'
        //
        // Desugaring:
        //   export * as x from "...";
        // ~>
        //   import * as .x from "..."; export {.x as x};
        //
        // Note that the desugared internal namespace export name (.x above) will
        // never conflict with a string literal export name, as literal string export
        // names in local name positions (i.e. left of 'as' or in a clause without
        // 'as') are disallowed without a following 'from' clause.
        //
        // TODO(olivf): Investigate if the private local name is still needed.
        // Re-exports of namespaces are now special exports which are resolved to the
        // imported module's special namespace binding cell.

        ExpectContextualKeyword(ast_value_factory().as_string());
        AstRawString export_name = ParseExportSpecifierName();
        Scanner.Location export_name_loc = scanner().location();
        AstRawString local_name = NextInternalNamespaceExportName();
        Scanner.Location local_name_loc = Scanner.Location.invalid();
        DeclareUnboundVariable(local_name, VariableMode.Const, InitializationFlag.kCreatedInitialized, pos);

        ExpectContextualKeyword(ast_value_factory().from_string());
        Scanner.Location specifier_loc = scanner().peek_location();
        AstRawString module_specifier = ParseModuleSpecifier();
        ImportAttributes import_attributes = ParseImportWithOrAssertClause();
        ExpectSemicolon();

        module().AddStarImport(local_name, module_specifier, ModuleImportPhase.kEvaluation, import_attributes,
                               local_name_loc, specifier_loc);
        module().AddExport(local_name, export_name, export_name_loc);
    }

    private Statement ParseExportDeclaration()
    {
        // ExportDeclaration:
        //    'export' '*' 'from' ModuleSpecifier ';'
        //    'export' '*' 'from' ModuleSpecifier [no LineTerminator here]
        //        AssertClause ';'
        //    'export' '*' 'as' IdentifierName 'from' ModuleSpecifier ';'
        //    'export' '*' 'as' IdentifierName 'from' ModuleSpecifier
        //        [no LineTerminator here] AssertClause ';'
        //    'export' '*' 'as' ModuleExportName 'from' ModuleSpecifier ';'
        //    'export' '*' 'as' ModuleExportName 'from' ModuleSpecifier ';'
        //        [no LineTerminator here] AssertClause ';'
        //    'export' ExportClause ('from' ModuleSpecifier)? ';'
        //    'export' ExportClause ('from' ModuleSpecifier [no LineTerminator here]
        //        AssertClause)? ';'
        //    'export' VariableStatement
        //    'export' Declaration
        //    'export' 'default' ... (handled in ParseExportDefault)
        //
        // ModuleExportName :
        //   StringLiteral

        Expect(Token.Export);
        Statement result;
        List<AstRawString> names = new(1);
        Scanner.Location loc = scanner().peek_location();
        switch (peek())
        {
            case Token.Default:
                return ParseExportDefault();

            case Token.Mul:
                ParseExportStar();
                return factory().EmptyStatement();

            case Token.LeftBrace:
            {
                // There are two cases here:
                //
                // 'export' ExportClause ';'
                // and
                // 'export' ExportClause FromClause ';'
                //
                // In the first case, the exported identifiers in ExportClause must
                // not be reserved words, while in the latter they may be. We
                // pass in a location that gets filled with the first reserved word
                // encountered, and then throw a SyntaxError if we are in the
                // non-FromClause case.
                Scanner.Location reserved_loc = Scanner.Location.invalid();
                Scanner.Location string_literal_local_name_loc = Scanner.Location.invalid();
                List<ExportClauseData> export_data =
                    ParseExportClause(ref reserved_loc, ref string_literal_local_name_loc);
                if (CheckContextualKeyword(ast_value_factory().from_string()))
                {
                    Scanner.Location specifier_loc = scanner().peek_location();
                    AstRawString module_specifier = ParseModuleSpecifier();
                    ImportAttributes import_attributes = ParseImportWithOrAssertClause();
                    ExpectSemicolon();

                    if (export_data.Count == 0)
                    {
                        module().AddEmptyImport(module_specifier, import_attributes, specifier_loc);
                    }
                    else
                    {
                        foreach (ExportClauseData data in export_data)
                        {
                            module().AddExport(data.local_name, data.export_name, module_specifier,
                                               import_attributes, data.location, specifier_loc);
                        }
                    }
                }
                else
                {
                    if (reserved_loc.IsValid())
                    {
                        // No FromClause, so reserved words are invalid in ExportClause.
                        ReportMessageAt(reserved_loc, MessageTemplate.UnexpectedReserved);
                        return null;
                    }
                    else if (string_literal_local_name_loc.IsValid())
                    {
                        ReportMessageAt(string_literal_local_name_loc,
                                        MessageTemplate.ModuleExportNameWithoutFromClause);
                        return null;
                    }

                    ExpectSemicolon();

                    foreach (ExportClauseData data in export_data)
                    {
                        module().AddExport(data.local_name, data.export_name, data.location);
                    }
                }
                return factory().EmptyStatement();
            }

            case Token.Function:
                result = ParseHoistableDeclaration(names, false);
                break;

            case Token.Class:
                Consume(Token.Class);
                result = ParseClassDeclaration(names, false);
                break;

            case Token.Var:
            case Token.Let:
            case Token.Const:
                result = ParseVariableStatement(VariableDeclarationContext.kStatementListItem, names);
                break;

            case Token.Async:
                Consume(Token.Async);
                if (peek() == Token.Function && !scanner().HasLineTerminatorBeforeNext())
                {
                    result = ParseAsyncFunctionDeclaration(names, false);
                    break;
                }
                goto default;

            default:
                ReportUnexpectedToken(scanner().current_token());
                return null;
        }
        loc.end_pos = scanner().location().end_pos;

        SourceTextModuleDescriptor descriptor = module();
        foreach (AstRawString name in names)
        {
            descriptor.AddExport(name, name, loc);
        }

        return result;
    }

    private void DeclareUnboundVariable(AstRawString name, VariableMode mode, InitializationFlag init, int pos)
    {
        // The variable will be added to the declarations list, but since we are not
        // binding it to anything, we can simply ignore it here.
        DeclareVariable(name, VariableKind.NORMAL_VARIABLE, mode, init, scope(), out _, pos, end_position());
    }

    private VariableProxy DeclareBoundVariable(AstRawString name, VariableMode mode, int pos)
    {
        VariableProxy proxy = factory().NewVariableProxy(name, VariableKind.NORMAL_VARIABLE, position());
        Variable var = DeclareVariable(name, VariableKind.NORMAL_VARIABLE, mode, Variable.DefaultInitializationFlag(mode),
                                       scope(), out _, pos, end_position());
        proxy.BindTo(var);
        return proxy;
    }

    public override void DeclareAndBindVariable(VariableProxy proxy, VariableKind kind, VariableMode mode,
                                                Scope scope, out bool was_added, int initializer_position,
                                                VariableProxy.BindingMode binding_mode)
    {
        Variable var = DeclareVariable(proxy.raw_name(), kind, mode, Variable.DefaultInitializationFlag(mode), scope,
                                       out was_added, proxy.position(), kNoSourcePosition);
        var.set_initializer_position(initializer_position);
        proxy.BindTo(var, binding_mode);
    }

    public override Variable DeclareVariable(AstRawString name, VariableKind kind, VariableMode mode,
                                             InitializationFlag init, Scope scope, out bool was_added, int begin,
                                             int end = kNoSourcePosition)
    {
        Declaration declaration;
        if (mode == VariableMode.Var && !scope.is_declaration_scope())
        {
            declaration = factory().NewNestedVariableDeclaration(scope, begin);
        }
        else
        {
            declaration = factory().NewVariableDeclaration(begin);
        }
        Declare(declaration, name, kind, mode, init, scope, out was_added, begin, end);
        return declaration.var();
    }

    private void Declare(Declaration declaration, AstRawString name, VariableKind variable_kind, VariableMode mode,
                         InitializationFlag init, Scope scope, out bool was_added, int var_begin_pos,
                         int var_end_pos = kNoSourcePosition)
    {
        bool local_ok = true;
        bool sloppy_mode_block_scope_function_redefinition = false;
        scope.DeclareVariable(declaration, name, var_begin_pos, mode, variable_kind, init, out was_added,
                              ref sloppy_mode_block_scope_function_redefinition, ref local_ok);
        if (!local_ok)
        {
            // If we only have the start position of a proxy, we can't highlight the
            // whole variable name.  Pretend its length is 1 so that we highlight at
            // least the first character.
            Scanner.Location loc = new(var_begin_pos,
                                       var_end_pos != kNoSourcePosition ? var_end_pos : var_begin_pos + 1);
            if (variable_kind == VariableKind.PARAMETER_VARIABLE)
            {
                ReportMessageAt(loc, MessageTemplate.ParamDupe);
            }
            else
            {
                ReportMessageAt(loc, MessageTemplate.VarRedeclaration, declaration.var().raw_name());
            }
        }
        else if (sloppy_mode_block_scope_function_redefinition)
        {
            ++use_counts_[(int)UseCounterFeature.kSloppyModeBlockScopedFunctionRedefinition];
        }
    }

    public override Statement BuildInitializationBlock(DeclarationParsingResult parsing_result)
    {
        using ScopedPtrList<Statement> statements = new(pointer_buffer());
        foreach (DeclarationParsingResult.Declaration declaration in parsing_result.declarations)
        {
            if (declaration.initializer == null) continue;
            InitializeVariables(statements, parsing_result.descriptor.kind, declaration);
        }
        return factory().NewBlock(true, statements);
    }

    public override Statement DeclareFunction(AstRawString variable_name, FunctionLiteral function, VariableMode mode,
                                              VariableKind kind, int beg_pos, int end_pos, List<AstRawString> names)
    {
        Declaration declaration = factory().NewFunctionDeclaration(function, beg_pos);
        Declare(declaration, variable_name, kind, mode, InitializationFlag.kCreatedInitialized, scope(), out _,
                beg_pos);
        if (info().flags().coverage_enabled())
        {
            // Force the function to be allocated when collecting source coverage, so
            // that even dead functions get source coverage data.
            declaration.var().set_is_used();
        }
        names?.Add(variable_name);
        if (kind == VariableKind.SLOPPY_BLOCK_FUNCTION_VARIABLE)
        {
            Token init = loop_nesting_depth() > 0 ? Token.Assign : Token.Init;
            SloppyBlockFunctionStatement statement =
                factory().NewSloppyBlockFunctionStatement(end_pos, declaration.var(), init);
            GetDeclarationScope().DeclareSloppyBlockFunction(statement);
            return statement;
        }
        return factory().EmptyStatement();
    }

    public override Statement DeclareClass(AstRawString variable_name, Expression value, List<AstRawString> names,
                                           int class_token_pos, int end_pos)
    {
        VariableProxy proxy = DeclareBoundVariable(variable_name, VariableMode.Let, class_token_pos);
        proxy.var().set_initializer_position(end_pos);
        names?.Add(variable_name);

        Assignment assignment = factory().NewAssignment(Token.Init, proxy, value, class_token_pos);
        return IgnoreCompletion(factory().NewExpressionStatement(assignment, kNoSourcePosition));
    }

    public override Statement DeclareNative(AstRawString name, int pos)
    {
        // Make sure that the function containing the native declaration
        // isn't lazily compiled. The extension structures are only
        // accessible while parsing the first time not when reparsing
        // because of lazy compilation.
        GetClosureScope().ForceEagerCompilation();

        // TODO(1240846): It's weird that native function declarations are
        // introduced dynamically when we meet their declarations, whereas
        // other functions are set up when entering the surrounding scope.
        VariableProxy proxy = DeclareBoundVariable(name, VariableMode.Var, pos);
        NativeFunctionLiteral lit = factory().NewNativeFunctionLiteral(name, extension(), kNoSourcePosition);
        return factory().NewExpressionStatement(factory().NewAssignment(Token.Init, proxy, lit, kNoSourcePosition),
                                                pos);
    }

    private Block IgnoreCompletion(Statement statement)
    {
        Block block = factory().NewBlock(1, true);
        block.statements().Add(statement);
        return block;
    }

    public override Statement RewriteSwitchStatement(Statement switch_statement_, Scope scope)
    {
        SwitchStatement switch_statement = (SwitchStatement)switch_statement_;
        // In order to get the CaseClauses to execute in their own lexical scope,
        // but without requiring downstream code to have special scope handling
        // code for switch statements, desugar into blocks as follows:
        // {  // To group the statements--harmless to evaluate Expression in scope
        //   .tag_variable = Expression;
        //   {  // To give CaseClauses a scope
        //     switch (.tag_variable) { CaseClause* }
        //   }
        // }
        Block switch_block = factory().NewBlock(2, false);

        Expression tag = switch_statement.tag();
        Variable tag_variable = NewTemporary(ast_value_factory().dot_switch_tag_string());
        Assignment tag_assign = factory().NewAssignment(Token.Assign, factory().NewVariableProxy(tag_variable), tag,
                                                        tag.position());
        // Wrap with IgnoreCompletion so the tag isn't returned as the completion
        // value, in case the switch statements don't have a value.
        Statement tag_statement = IgnoreCompletion(factory().NewExpressionStatement(tag_assign, kNoSourcePosition));
        switch_block.statements().Add(tag_statement);

        switch_statement.set_tag(factory().NewVariableProxy(tag_variable));
        Block cases_block = factory().NewBlock(1, false);
        cases_block.statements().Add(switch_statement);
        cases_block.set_scope(scope);
        switch_block.statements().Add(cases_block);
        return switch_block;
    }

    private void InitializeVariables(ScopedPtrList<Statement> statements, VariableKind kind,
                                     DeclarationParsingResult.Declaration declaration)
    {
        if (has_error()) return;

        int pos = declaration.value_beg_pos;
        if (pos == kNoSourcePosition)
        {
            pos = declaration.initializer.position();
        }
        Assignment assignment = factory().NewAssignment(Token.Init, declaration.pattern, declaration.initializer,
                                                        pos);
        statements.Add(factory().NewExpressionStatement(assignment, pos));
    }

    public override Block RewriteCatchPattern(CatchInfo catch_info)
    {
        DeclarationParsingResult.Declaration decl =
            new(catch_info.pattern, factory().NewVariableProxy(catch_info.variable));

        using ScopedPtrList<Statement> init_statements = new(pointer_buffer());
        InitializeVariables(init_statements, VariableKind.NORMAL_VARIABLE, decl);
        return factory().NewBlock(true, init_statements);
    }

    public override void ReportVarRedeclarationIn(AstRawString name, Scope scope)
    {
        foreach (Declaration decl in scope.declarations())
        {
            if (decl.var().raw_name() == name)
            {
                int position = decl.position();
                Scanner.Location location = position == kNoSourcePosition
                    ? Scanner.Location.invalid()
                    : new Scanner.Location(position, position + name.length());
                ReportMessageAt(location, MessageTemplate.VarRedeclaration, name);
                return;
            }
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    public override Statement RewriteTryStatement(Block try_block, Block catch_block, SourceRange catch_range,
                                                  Block finally_block, SourceRange finally_range, CatchInfo catch_info,
                                                  int pos)
    {
        // Simplify the AST nodes by converting:
        //   'try B0 catch B1 finally B2'
        // to:
        //   'try { try B0 catch B1 } finally B2'

        if (catch_block != null && finally_block != null)
        {
            // If we have both, create an inner try/catch.
            TryCatchStatement statement =
                factory().NewTryCatchStatement(try_block, catch_info.scope, catch_block, kNoSourcePosition);
            RecordTryCatchStatementSourceRange(statement, catch_range);

            try_block = factory().NewBlock(1, false);
            try_block.statements().Add(statement);
            catch_block = null; // Clear to indicate it's been handled.
        }

        if (catch_block != null)
        {
            TryCatchStatement stmt = factory().NewTryCatchStatement(try_block, catch_info.scope, catch_block, pos);
            RecordTryCatchStatementSourceRange(stmt, catch_range);
            return stmt;
        }
        else
        {
            TryFinallyStatement stmt = factory().NewTryFinallyStatement(try_block, finally_block, pos);
            RecordTryFinallyStatementSourceRange(stmt, finally_range);
            return stmt;
        }
    }

    public override void ParseGeneratorFunctionBody(int pos, FunctionKind kind, ScopedPtrList<Statement> body)
    {
        // For ES6 Generators, we just prepend the initial yield.
        Expression initial_yield = BuildInitialYield(pos, kind);
        body.Add(factory().NewExpressionStatement(initial_yield, kNoSourcePosition));
        ParseStatementList(body, Token.RightBrace);
    }

    public override void ParseAsyncGeneratorFunctionBody(int pos, FunctionKind kind, ScopedPtrList<Statement> body)
    {
        Expression initial_yield = BuildInitialYield(pos, kind);
        body.Add(factory().NewExpressionStatement(initial_yield, kNoSourcePosition));
        ParseStatementList(body, Token.RightBrace);
    }

    public override void DeclareFunctionNameVar(AstRawString function_name, FunctionSyntaxKind function_syntax_kind,
                                                DeclarationScope function_scope)
    {
        if (function_syntax_kind == FunctionSyntaxKind.NamedExpression &&
            function_scope.LookupLocal(function_name) == null)
        {
            function_scope.DeclareFunctionVar(function_name);
        }
    }

    // Special case for legacy for
    //
    //    for (var x = initializer in enumerable) body
    //
    // An initialization block of the form
    //
    //    {
    //      x = initializer;
    //    }
    //
    // is returned in this case.  It has reserved space for two statements,
    // so that (later on during parsing), the equivalent of
    //
    //   for (x in enumerable) body
    //
    // is added as a second statement to it.
    public override Block RewriteForVarInLegacy(ForInfo for_info)
    {
        DeclarationParsingResult.Declaration decl = for_info.parsing_result.declarations[0];
        if (!IsLexicalVariableMode(for_info.parsing_result.descriptor.mode) && decl.initializer != null &&
            decl.pattern.IsVariableProxy())
        {
            ++use_counts_[(int)UseCounterFeature.kForInInitializer];
            AstRawString name = decl.pattern.AsVariableProxy().raw_name();
            VariableProxy single_var = NewUnresolved(name);
            Block init_block = factory().NewBlock(2, true);
            init_block.statements().Add(factory().NewExpressionStatement(
                factory().NewAssignment(Token.Assign, single_var, decl.initializer, decl.value_beg_pos),
                kNoSourcePosition));
            return init_block;
        }
        return null;
    }

    // Rewrite a for-in/of statement of the form
    //
    //   for (let/const/var x in/of e) b
    //
    // into
    //
    //   {
    //     var temp;
    //     for (temp in/of e) {
    //       let/const/var x = temp;
    //       b;
    //     }
    //     let x;  // for TDZ
    //   }
    public override void DesugarBindingInForEachStatement(ForInfo for_info, ref Block body_block,
                                                          ref Expression each_variable)
    {
        DeclarationParsingResult.Declaration decl = for_info.parsing_result.declarations[0];
        Variable temp = NewTemporary(ast_value_factory().dot_for_string());
        using ScopedPtrList<Statement> each_initialization_statements = new(pointer_buffer());
        decl.initializer = factory().NewVariableProxy(temp, for_info.position);
        for_info.parsing_result.declarations[0] = decl;
        InitializeVariables(each_initialization_statements, VariableKind.NORMAL_VARIABLE, decl);

        body_block = factory().NewBlock(3, false);
        body_block.statements().Add(factory().NewBlock(true, each_initialization_statements));
        each_variable = factory().NewVariableProxy(temp, for_info.position);
    }

    // Create a TDZ for any lexically-bound names in for in/of statements.
    public override Block CreateForEachStatementTDZ(Block init_block, ForInfo for_info)
    {
        if (IsLexicalVariableMode(for_info.parsing_result.descriptor.mode))
        {
            init_block = factory().NewBlock(1, false);

            foreach (AstRawString bound_name in for_info.bound_names)
            {
                // TODO(adamk): This needs to be some sort of special
                // INTERNAL variable that's invisible to the debugger
                // but visible to everything else.
                VariableProxy tdz_proxy = DeclareBoundVariable(bound_name, VariableMode.Let, kNoSourcePosition);
                tdz_proxy.var().set_initializer_position(position());
            }
        }
        return init_block;
    }

    public override Statement DesugarLexicalBindingsInForStatement(Statement loop_, Statement init, Expression cond,
                                                                   Statement next, Statement body, Scope inner_scope,
                                                                   ForInfo for_info)
    {
        ForStatement loop = (ForStatement)loop_;
        // ES6 13.7.4.8 specifies that on each loop iteration the let variables are
        // copied into a new environment.  Moreover, the "next" statement must be
        // evaluated not in the environment of the just completed iteration but in
        // that of the upcoming one.  We achieve this with the following desugaring.
        // Extra care is needed to preserve the completion value of the original loop.
        //
        // We are given a for statement of the form
        //
        //  labels: for (let/const x = i; cond; next) body
        //
        // and rewrite it as follows.  Here we write {{ ... }} for init-blocks, ie.,
        // blocks whose ignore_completion_value_ flag is set.
        //
        //  {
        //    let/const x = i;
        //    temp_x = x;
        //    first = 1;
        //    undefined;
        //    outer: for (;;) {
        //      let/const x = temp_x;
        //      {{ if (first == 1) {
        //           first = 0;
        //         } else {
        //           next;
        //         }
        //         flag = 1;
        //         if (!cond) break;
        //      }}
        //      labels: for (; flag == 1; flag = 0, temp_x = x) {
        //        body
        //      }
        //      {{ if (flag == 1)  // Body used break.
        //           break;
        //      }}
        //    }
        //  }

        List<Variable> temps = new(for_info.bound_names.Count);

        Block outer_block = factory().NewBlock(for_info.bound_names.Count + 4, false);

        // Add statement: let/const x = i.
        outer_block.statements().Add(init);

        AstRawString temp_name = ast_value_factory().dot_for_string();

        // For each lexical variable x:
        //   make statement: temp_x = x.
        foreach (AstRawString bound_name in for_info.bound_names)
        {
            VariableProxy proxy = NewUnresolved(bound_name);
            Variable temp = NewTemporary(temp_name);
            VariableProxy temp_proxy = factory().NewVariableProxy(temp);
            Assignment assignment = factory().NewAssignment(Token.Assign, temp_proxy, proxy, kNoSourcePosition);
            Statement assignment_statement = factory().NewExpressionStatement(assignment, kNoSourcePosition);
            outer_block.statements().Add(assignment_statement);
            temps.Add(temp);
        }

        Variable first = null;
        // Make statement: first = 1.
        if (next != null)
        {
            first = NewTemporary(temp_name);
            VariableProxy first_proxy = factory().NewVariableProxy(first);
            Expression const1 = factory().NewSmiLiteral(1, kNoSourcePosition);
            Assignment assignment = factory().NewAssignment(Token.Assign, first_proxy, const1, kNoSourcePosition);
            Statement assignment_statement = factory().NewExpressionStatement(assignment, kNoSourcePosition);
            outer_block.statements().Add(assignment_statement);
        }

        // make statement: undefined;
        outer_block.statements().Add(factory().NewExpressionStatement(
            factory().NewUndefinedLiteral(kNoSourcePosition), kNoSourcePosition));

        // Make statement: outer: for (;;)
        // Note that we don't actually create the label, or set this loop up as an
        // explicit break target, instead handing it directly to those nodes that
        // need to know about it. This should be safe because we don't run any code
        // in this function that looks up break targets.
        ForStatement outer_loop = factory().NewForStatement(kNoSourcePosition);
        outer_block.statements().Add(outer_loop);
        outer_block.set_scope(scope());

        Block inner_block = factory().NewBlock(3, false);
        using (BlockState block_state = new(this, inner_scope))
        {
            Block ignore_completion_block = factory().NewBlock(for_info.bound_names.Count + 3, true);
            List<Variable> inner_vars = new(for_info.bound_names.Count);
            // For each let variable x:
            //    make statement: let/const x = temp_x.
            for (int i = 0; i < for_info.bound_names.Count; i++)
            {
                VariableProxy proxy = DeclareBoundVariable(
                    for_info.bound_names[i],
                    IsResourceManagedVariableMode(for_info.parsing_result.descriptor.mode)
                        ? VariableMode.Const
                        : for_info.parsing_result.descriptor.mode,
                    kNoSourcePosition);
                inner_vars.Add(proxy.var());
                VariableProxy temp_proxy = factory().NewVariableProxy(temps[i]);
                Assignment assignment = factory().NewAssignment(Token.Init, proxy, temp_proxy, kNoSourcePosition);
                Statement assignment_statement = factory().NewExpressionStatement(assignment, kNoSourcePosition);
                int declaration_pos = for_info.parsing_result.descriptor.declaration_pos;
                proxy.var().set_initializer_position(declaration_pos);
                ignore_completion_block.statements().Add(assignment_statement);
            }

            // Make statement: if (first == 1) { first = 0; } else { next; }
            if (next != null)
            {
                Expression compare;
                // Make compare expression: first == 1.
                {
                    Expression const1 = factory().NewSmiLiteral(1, kNoSourcePosition);
                    VariableProxy first_proxy = factory().NewVariableProxy(first);
                    compare = factory().NewCompareOperation(Token.Eq, first_proxy, const1, kNoSourcePosition);
                }
                Statement clear_first;
                // Make statement: first = 0.
                {
                    VariableProxy first_proxy = factory().NewVariableProxy(first);
                    Expression const0 = factory().NewSmiLiteral(0, kNoSourcePosition);
                    Assignment assignment = factory().NewAssignment(Token.Assign, first_proxy, const0,
                                                                    kNoSourcePosition);
                    clear_first = factory().NewExpressionStatement(assignment, kNoSourcePosition);
                }
                Statement clear_first_or_next = factory().NewIfStatement(compare, clear_first, next,
                                                                         kNoSourcePosition);
                ignore_completion_block.statements().Add(clear_first_or_next);
            }

            Variable flag = NewTemporary(temp_name);
            // Make statement: flag = 1.
            {
                VariableProxy flag_proxy = factory().NewVariableProxy(flag);
                Expression const1 = factory().NewSmiLiteral(1, kNoSourcePosition);
                Assignment assignment = factory().NewAssignment(Token.Assign, flag_proxy, const1, kNoSourcePosition);
                Statement assignment_statement = factory().NewExpressionStatement(assignment, kNoSourcePosition);
                ignore_completion_block.statements().Add(assignment_statement);
            }

            // Make statement: if (!cond) break.
            if (cond != null)
            {
                Statement stop = factory().NewBreakStatement(outer_loop, kNoSourcePosition);
                Statement noop = factory().EmptyStatement();
                ignore_completion_block.statements().Add(factory().NewIfStatement(cond, noop, stop,
                                                                                   cond.position()));
            }

            inner_block.statements().Add(ignore_completion_block);
            // Make cond expression for main loop: flag == 1.
            Expression flag_cond;
            {
                Expression const1 = factory().NewSmiLiteral(1, kNoSourcePosition);
                VariableProxy flag_proxy = factory().NewVariableProxy(flag);
                flag_cond = factory().NewCompareOperation(Token.Eq, flag_proxy, const1, kNoSourcePosition);
            }

            // Create chain of expressions "flag = 0, temp_x = x, ..."
            Statement compound_next_statement;
            {
                Expression compound_next;
                // Make expression: flag = 0.
                {
                    VariableProxy flag_proxy = factory().NewVariableProxy(flag);
                    Expression const0 = factory().NewSmiLiteral(0, kNoSourcePosition);
                    compound_next = factory().NewAssignment(Token.Assign, flag_proxy, const0, kNoSourcePosition);
                }

                // Make the comma-separated list of temp_x = x assignments.
                int inner_var_proxy_pos = scanner().location().beg_pos;
                for (int i = 0; i < for_info.bound_names.Count; i++)
                {
                    VariableProxy temp_proxy = factory().NewVariableProxy(temps[i]);
                    VariableProxy proxy = factory().NewVariableProxy(inner_vars[i], inner_var_proxy_pos);
                    Assignment assignment = factory().NewAssignment(Token.Assign, temp_proxy, proxy,
                                                                    kNoSourcePosition);
                    compound_next = factory().NewBinaryOperation(Token.Comma, compound_next, assignment,
                                                                 kNoSourcePosition);
                }

                compound_next_statement = factory().NewExpressionStatement(compound_next, kNoSourcePosition);
            }

            // Make statement: labels: for (; flag == 1; flag = 0, temp_x = x)
            // Note that we reuse the original loop node, which retains its labels
            // and ensures that any break or continue statements in body point to
            // the right place.
            loop.Initialize(null, flag_cond, compound_next_statement, body);
            inner_block.statements().Add(loop);

            // Make statement: {{if (flag == 1) break;}}
            {
                Expression compare;
                // Make compare expression: flag == 1.
                {
                    Expression const1 = factory().NewSmiLiteral(1, kNoSourcePosition);
                    VariableProxy flag_proxy = factory().NewVariableProxy(flag);
                    compare = factory().NewCompareOperation(Token.Eq, flag_proxy, const1, kNoSourcePosition);
                }
                Statement stop = factory().NewBreakStatement(outer_loop, kNoSourcePosition);
                Statement empty = factory().EmptyStatement();
                Statement if_flag_break = factory().NewIfStatement(compare, stop, empty, kNoSourcePosition);
                inner_block.statements().Add(IgnoreCompletion(if_flag_break));
            }

            inner_block.set_scope(inner_scope);
        }

        outer_loop.Initialize(null, null, null, inner_block);

        return outer_block;
    }

    private void AddArrowFunctionFormalParameters(ParserFormalParameters parameters, Expression expr, int end_pos)
    {
        // ArrowFunctionFormals ::
        //    Nary(Token::kComma, VariableProxy*, Tail)
        //    Binary(Token::kComma, NonTailArrowFunctionFormals, Tail)
        //    Tail
        // NonTailArrowFunctionFormals ::
        //    Binary(Token::kComma, NonTailArrowFunctionFormals, VariableProxy)
        //    VariableProxy
        // Tail ::
        //    VariableProxy
        //    Spread(VariableProxy)
        //
        // We need to visit the parameters in left-to-right order
        //

        // For the Nary case, we simply visit the parameters in a loop.
        if (expr.IsNaryOperation())
        {
            NaryOperation nary = (NaryOperation)expr;
            // The classifier has already run, so we know that the expression is a valid
            // arrow function formals production.
            // Each op position is the end position of the *previous* expr, with the
            // second (i.e. first "subsequent") op position being the end position of
            // the first child expression.
            Expression next = nary.first();
            for (int i = 0; i < nary.subsequent_length(); ++i)
            {
                AddArrowFunctionFormalParameters(parameters, next, nary.subsequent_op_position(i));
                next = nary.subsequent(i);
            }
            AddArrowFunctionFormalParameters(parameters, next, end_pos);
            return;
        }

        // For the binary case, we recurse on the left-hand side of binary comma
        // expressions.
        if (expr.IsBinaryOperation())
        {
            BinaryOperation binop = (BinaryOperation)expr;
            // The classifier has already run, so we know that the expression is a valid
            // arrow function formals production.
            Expression left = binop.left();
            Expression right = binop.right();
            int comma_pos = binop.position();
            AddArrowFunctionFormalParameters(parameters, left, comma_pos);
            // LHS of comma expression should be unparenthesized.
            expr = right;
        }

        // Only the right-most expression may be a rest parameter.

        bool is_rest = expr.IsSpread();
        if (is_rest)
        {
            expr = ((Spread)expr).expression();
            parameters.has_rest = true;
        }

        Expression initializer = null;
        if (expr.IsAssignment())
        {
            Assignment assignment = (Assignment)expr;
            initializer = assignment.value();
            expr = assignment.target();
        }

        AddFormalParameter(parameters, expr, initializer, end_pos, is_rest);
    }

    public override void DeclareArrowFunctionFormalParameters(ParserFormalParameters parameters, Expression expr,
                                                              Scanner.Location params_loc)
    {
        if (expr.IsEmptyParentheses() || has_error()) return;

        AddArrowFunctionFormalParameters(parameters, expr, params_loc.end_pos);

        if (parameters.arity + 1 /* receiver */ > kCodeMaxArguments)
        {
            ReportMessageAt(params_loc, MessageTemplate.MalformedArrowFunParamList);
            return;
        }

        DeclareFormalParameters(parameters);
    }

    public override void ReindexArrowFunctionFormalParameters(ParserFormalParameters parameters,
                                                              AllowReindexScope scope)
    {
        // Make space for the arrow function above the formal parameters.
        AstFunctionLiteralIdReindexer reindexer = new(1);
        foreach (ParserFormalParameters.Parameter p in parameters.@params)
        {
            if (p.pattern != null) reindexer.Reindex(p.pattern, scope);
            if (p.initializer() != null)
            {
                reindexer.Reindex(p.initializer(), scope);
            }
            if (reindexer.HasStackOverflow())
            {
                reindexer.ClearStackOverflow();
                set_stack_overflow();
                return;
            }
        }
    }

    public override void ReindexComputedMemberName(Expression computed_name, AllowReindexScope scope)
    {
        // Make space for the member initializer function above the computed property
        // name.
        AstFunctionLiteralIdReindexer reindexer = new(1);
        reindexer.Reindex(computed_name, scope);
        if (reindexer.HasStackOverflow())
        {
            reindexer.ClearStackOverflow();
            set_stack_overflow();
            return;
        }
    }

    public override void PrepareGeneratorVariables()
    {
        // Calling a generator returns a generator object.  That object is stored
        // in a temporary variable, a definition that is used by "yield"
        // expressions.
        function_state_.scope().DeclareGeneratorObjectVar(ast_value_factory().dot_generator_object_string());
    }

    public override FunctionLiteral ParseFunctionLiteral(AstRawString function_name,
                                                         Scanner.Location function_name_location,
                                                         FunctionNameValidity function_name_validity,
                                                         FunctionKind kind, int function_token_pos,
                                                         FunctionSyntaxKind function_syntax_kind,
                                                         LanguageMode language_mode,
                                                         List<AstRawString> arguments_for_wrapped_function)
    {
        // Function ::
        //   '(' FormalParameterList? ')' '{' FunctionBody '}'
        //
        // Getter ::
        //   '(' ')' '{' FunctionBody '}'
        //
        // Setter ::
        //   '(' PropertySetParameterList ')' '{' FunctionBody '}'

        bool is_wrapped = function_syntax_kind == FunctionSyntaxKind.Wrapped;

        int pos = function_token_pos == kNoSourcePosition ? peek_position() : function_token_pos;

        // Anonymous functions were passed either the empty symbol or a null
        // handle as the function name.  Remember if we were passed a non-empty
        // handle to decide whether to invoke function name inference.
        bool should_infer_name = function_name == null;

        // We want a non-null handle as the function name by default. We will handle
        // the "function does not have a shared name" case later.
        if (should_infer_name)
        {
            function_name = ast_value_factory().empty_string();
        }

        // This is true if we get here through CreateDynamicFunction.
        bool params_need_validation = parameters_end_pos_ != kNoSourcePosition;
        int compile_hint_position = peek_position();

        FunctionLiteral.EagerCompileHint eager_compile_hint =
            function_state_.next_function_is_likely_called() || is_wrapped || params_need_validation ||
            (info().flags().compile_hints_magic_enabled() && scanner().SawMagicCommentCompileHintsAll()) ||
            (info().flags().compile_hints_per_function_magic_enabled() &&
             scanner().HasPerFunctionCompileHint(compile_hint_position))
                ? FunctionLiteral.EagerCompileHint.kShouldEagerCompile
                : default_eager_compile_hint();

        // Determine if the function can be parsed lazily. Lazy parsing is
        // different from lazy compilation; we need to parse more eagerly than we
        // compile.

        // We can only parse lazily if we also compile lazily. The heuristics for lazy
        // compilation are:
        // - It must not have been prohibited by the caller to Parse (some callers
        //   need a full AST).
        // - The outer scope must allow lazy compilation of inner functions.
        // - The function mustn't be a function expression with an open parenthesis
        //   before; we consider that a hint that the function will be called
        //   immediately, and it would be a waste of time to make it lazily
        //   compiled.
        // These are all things we can know at this point, without looking at the
        // function itself.

        // We separate between lazy parsing top level functions and lazy parsing inner
        // functions, because the latter needs to do more work. In particular, we need
        // to track unresolved variables to distinguish between these cases:
        // (function foo() {
        //   bar = function() { return 1; }
        //  })();
        // and
        // (function foo() {
        //   var a = 1;
        //   bar = function() { return a; }
        //  })();

        // Now foo will be parsed eagerly and compiled eagerly (optimization: assume
        // parenthesis before the function means that it will be called
        // immediately). bar can be parsed lazily, but we need to parse it in a mode
        // that tracks unresolved variables.

        eager_compile_hint = GetEmbedderCompileHint(eager_compile_hint, compile_hint_position);

        bool is_lazy = eager_compile_hint == FunctionLiteral.EagerCompileHint.kShouldLazyCompile;
        bool is_top_level = AllowsLazyParsingWithoutUnresolvedVariables();
        bool is_eager_top_level_function = !is_lazy && is_top_level;
        _ = is_eager_top_level_function;

        // Determine whether we can lazy parse the inner function. Lazy compilation
        // has to be enabled, which is either forced by overall parse flags or via a
        // ScopedModification.
        bool can_preparse = parse_lazily();

        // Determine whether we can post any parallel compile tasks. Preparsing must
        // be possible, there has to be a dispatcher, and the character stream must be
        // cloneable. V8Sharp has no lazy compile dispatcher.
        const bool can_post_parallel_task = false;

        // If parallel compile tasks are enabled, and this isn't a re-parse, enable
        // parallel compile for the subset of functions as defined by flags.
        bool should_post_parallel_task = can_post_parallel_task;

        // Determine whether we should lazy parse the inner function. This will be
        // when either the function is lazy by inspection, or when we force it to be
        // preparsed now so that we can then post a parallel full parse & compile task
        // for it.
        bool should_preparse = can_preparse && (is_lazy || should_post_parallel_task);

        using ScopedPtrList<Statement> body = new(pointer_buffer());
        int expected_property_count = 0;
        int suspend_count = -1;
        int num_parameters = -1;
        int function_length = -1;
        bool has_duplicate_parameters = false;
        int function_literal_id = GetNextInfoId();
        ProducedPreparseData produced_preparse_data = null;

        // This Scope lives in the main zone. We'll migrate data into that zone later.
        DeclarationScope scope = NewFunctionScope(kind);
        SetLanguageMode(scope, language_mode);
        if (function_syntax_kind == FunctionSyntaxKind.Declaration)
        {
            if (flags().is_reparse())
            {
                scope.set_is_hoisted_in_context(flags().is_hoisted_in_context());
            }
            else
            {
                scope.set_is_hoisted_in_context(true);
            }
        }
        if (is_wrapped)
        {
            scope.set_is_wrapped_function();
        }

        if (!is_wrapped && !Check(Token.LeftParen))
        {
            ReportUnexpectedToken(Next());
            return null;
        }
        scope.set_start_position(position());

        // Eager or lazy parse? If is_lazy_top_level_function, we'll parse
        // lazily. We'll call SkipFunction, which may decide to
        // abort lazy parsing if it suspects that wasn't a good idea. If so (in
        // which case the parser is expected to have backtracked), or if we didn't
        // try to lazy parse in the first place, we'll have to parse eagerly.
        bool did_preparse_successfully =
            should_preparse &&
            SkipFunction(function_literal_id, function_name, kind, function_syntax_kind, scope, ref num_parameters,
                         ref function_length, ref produced_preparse_data);

        if (!did_preparse_successfully)
        {
            // If skipping aborted, it rewound the scanner until before the lparen.
            // Consume it in that case.
            if (should_preparse) Consume(Token.LeftParen);
            should_post_parallel_task = false;
            ParseFunction(body, function_name, pos, kind, function_syntax_kind, scope, out num_parameters,
                          out function_length, out has_duplicate_parameters, out expected_property_count,
                          out suspend_count, arguments_for_wrapped_function);
        }

        // Validate function name. We can do this only after parsing the function,
        // since the function can declare itself strict.
        language_mode = scope.language_mode();
        CheckFunctionName(language_mode, function_name, function_name_validity, function_name_location);

        if (is_strict(language_mode))
        {
            CheckStrictOctalLiteral(scope.start_position(), scope.end_position());
        }

        FunctionLiteral.ParameterFlag duplicate_parameters = has_duplicate_parameters
            ? FunctionLiteral.ParameterFlag.kHasDuplicateParameters
            : FunctionLiteral.ParameterFlag.kNoDuplicateParameters;

        // Note that the FunctionLiteral needs to be created in the main Zone again.
        FunctionLiteral function_literal = factory().NewFunctionLiteral(
            function_name, scope, body, expected_property_count, num_parameters, function_length,
            duplicate_parameters, function_syntax_kind, eager_compile_hint, pos, true, function_literal_id,
            produced_preparse_data);
        function_literal.set_function_token_position(function_token_pos);
        function_literal.set_suspend_count(suspend_count);

        RecordFunctionLiteralSourceRange(function_literal);

        if (should_post_parallel_task && !has_error())
        {
            function_literal.set_should_parallel_compile();
        }

        if (should_infer_name)
        {
            fni_.AddFunction(function_literal);
        }
        return function_literal;
    }

    // Skip over a lazy function, either using cached data if we have it, or
    // by parsing the function with PreParser. Consumes the ending }.
    // In case the preparser detects an error it cannot identify, it resets the
    // scanner- and preparser state to the initial one, before PreParsing the
    // function.
    // SkipFunction returns true if it correctly parsed the function, including
    // cases where we detect an error. It returns false, if we needed to stop
    // parsing or could not identify an error correctly, meaning the caller needs
    // to fully reparse. In this case it resets the scanner and preparser state.
    public override bool SkipFunction(int function_literal_id, AstRawString function_name, FunctionKind kind,
                                      FunctionSyntaxKind function_syntax_kind, DeclarationScope function_scope,
                                      ref int num_parameters, ref int function_length,
                                      ref ProducedPreparseData produced_preparse_data)
    {
        using FunctionState function_state = new(this, function_scope);

        // FIXME(marja): There are 2 ways to skip functions now. Unify them.
        if (consumed_preparse_data_ != null)
        {
            if (stack_overflow()) return true;
            produced_preparse_data = consumed_preparse_data_.GetDataForSkippableFunction(
                function_scope.start_position(), out int end_position, out num_parameters, out function_length,
                out int num_inner_infos, out bool uses_super_property, out LanguageMode language_mode);

            function_scope.outer_scope().SetMustUsePreparseData();
            function_scope.set_is_skipped_function(true);
            function_scope.set_end_position(end_position);
            scanner().SeekForward(end_position - 1);
            Expect(Token.RightBrace);
            SetLanguageMode(function_scope, language_mode);
            if (uses_super_property)
            {
                function_scope.RecordSuperPropertyUsage();
            }
            SkipInfos(num_inner_infos);
            function_scope.ResetAfterPreparsing(ast_value_factory_, false);
            return true;
        }

        Scanner.BookmarkScope bookmark = new(scanner());
        bookmark.Set(function_scope.start_position());

        ThreadedList<VariableProxy>.Iterator? unresolved_private_tail = null;
        PrivateNameScopeIterator private_name_scope_iter = new(function_scope);
        if (!private_name_scope_iter.Done())
        {
            unresolved_private_tail = private_name_scope_iter.GetScope().GetUnresolvedPrivateNameTail();
        }

        // With no cached data, we partially parse the function, without building an
        // AST. This gathers the data needed to build a lazy function.
        reusable_preparser().set_max_drift(max_drift());
        PreParser.PreParseResult result = reusable_preparser().PreParseFunction(
            function_literal_id, function_name, kind, function_syntax_kind, function_scope, use_counts_,
            out produced_preparse_data);

        if (result == PreParser.PreParseResult.kPreParseStackOverflow)
        {
            // Propagate stack overflow.
            set_stack_overflow();
        }
        else if (pending_error_handler().has_error_unidentifiable_by_preparser())
        {
            // Make sure we don't re-preparse inner functions of the aborted function.
            // The error might be in an inner function.
            allow_lazy_ = false;
            mode_ = Mode.PARSE_EAGERLY;
            // If we encounter an error that the preparser can not identify we reset to
            // the state before preparsing. The caller may then fully parse the function
            // to identify the actual error.
            bookmark.Apply();
            if (!private_name_scope_iter.Done())
            {
                private_name_scope_iter.GetScope().ResetUnresolvedPrivateNameTail(unresolved_private_tail);
            }
            function_scope.ResetAfterPreparsing(ast_value_factory_, true);
            pending_error_handler().clear_unidentifiable_error();
            return false;
        }
        else if (pending_error_handler().has_pending_error())
        {
        }
        else
        {
            set_allow_eval_cache(reusable_preparser().allow_eval_cache());

            PreParserLogger logger = reusable_preparser().logger();
            function_scope.set_end_position(logger.end());
            Expect(Token.RightBrace);
            total_preparse_skipped_ += function_scope.end_position() - function_scope.start_position();
            num_parameters = logger.num_parameters();
            function_length = logger.function_length();
            SkipInfos(logger.num_inner_infos());
            if (!private_name_scope_iter.Done())
            {
                private_name_scope_iter.GetScope().MigrateUnresolvedPrivateNameTail(factory(), unresolved_private_tail);
            }
            function_scope.AnalyzePartially(this, factory(), MaybeParsingArrowhead());
        }

        return true;
    }

    public override Block BuildParameterInitializationBlock(ParserFormalParameters parameters)
    {
        using ScopedPtrList<Statement> init_statements = new(pointer_buffer());
        int index = 0;
        foreach (ParserFormalParameters.Parameter parameter in parameters.@params)
        {
            Expression initial_value = factory().NewVariableProxy(parameters.scope.parameter(index));
            if (parameter.initializer() != null)
            {
                // IS_UNDEFINED($param) ? initializer : $param

                CompareOperation condition = factory().NewCompareOperation(
                    Token.EqStrict, factory().NewVariableProxy(parameters.scope.parameter(index)),
                    factory().NewUndefinedLiteral(kNoSourcePosition), kNoSourcePosition);
                initial_value = factory().NewConditional(condition, parameter.initializer(), initial_value,
                                                         kNoSourcePosition);
            }

            using BlockState block_state = new(this, scope().AsDeclarationScope());
            DeclarationParsingResult.Declaration decl = new(parameter.pattern, initial_value);
            InitializeVariables(init_statements, VariableKind.PARAMETER_VARIABLE, decl);

            ++index;
        }
        return factory().NewParameterInitializationBlock(init_statements);
    }

    private Expression BuildInitialYield(int pos, FunctionKind kind)
    {
        Expression yield_result = factory().NewVariableProxy(function_state_.scope().generator_object_var());
        // The position of the yield is important for reporting the exception
        // caused by calling the .throw method on a generator suspended at the
        // initial yield (i.e. right after generator instantiation).
        function_state_.AddSuspend();
        return factory().NewYield(yield_result, scope().start_position(), Suspend.OnAbruptResume.kOnExceptionThrow);
    }

    private void ParseFunction(ScopedPtrList<Statement> body, AstRawString function_name, int pos, FunctionKind kind,
                               FunctionSyntaxKind function_syntax_kind, DeclarationScope function_scope,
                               out int num_parameters, out int function_length, out bool has_duplicate_parameters,
                               out int expected_property_count, out int suspend_count,
                               List<AstRawString> arguments_for_wrapped_function)
    {
        num_parameters = -1;
        function_length = -1;
        has_duplicate_parameters = false;
        expected_property_count = 0;
        suspend_count = -1;

        using FunctionParsingScope function_parsing_scope = new(this);
        using ModeScope mode_scope = new(this, allow_lazy_ ? Mode.PARSE_LAZILY : Mode.PARSE_EAGERLY);

        using FunctionState function_state = new(this, function_scope);

        bool is_wrapped = function_syntax_kind == FunctionSyntaxKind.Wrapped;

        int expected_parameters_end_pos = parameters_end_pos_;
        if (expected_parameters_end_pos != kNoSourcePosition)
        {
            // This is the first function encountered in a CreateDynamicFunction eval.
            parameters_end_pos_ = kNoSourcePosition;
            // The function name should have been ignored, giving us the empty string
            // here.
        }

        ParserFormalParameters formals = new(function_scope);

        {
            using ParameterDeclarationParsingScope formals_scope = new(this);
            if (is_wrapped)
            {
                // For a function implicitly wrapped in function header and footer, the
                // function arguments are provided separately to the source, and are
                // declared directly here.
                foreach (AstRawString arg in arguments_for_wrapped_function)
                {
                    const bool is_rest = false;
                    Expression argument = ExpressionFromIdentifier(arg, kNoSourcePosition);
                    AddFormalParameter(formals, argument, NullExpression(), kNoSourcePosition, is_rest);
                }
                DeclareFormalParameters(formals);
            }
            else
            {
                // For a regular function, the function arguments are parsed from source.
                ParseFormalParameterList(formals);
                if (expected_parameters_end_pos != kNoSourcePosition)
                {
                    // Check for '(' or ')' shenanigans in the parameter string for dynamic
                    // functions.
                    int position = peek_position();
                    if (position < expected_parameters_end_pos)
                    {
                        ReportMessageAt(new Scanner.Location(position, position + 1),
                                        MessageTemplate.ArgStringTerminatesParametersEarly);
                        return;
                    }
                    else if (position > expected_parameters_end_pos)
                    {
                        ReportMessageAt(new Scanner.Location(expected_parameters_end_pos - 2,
                                                             expected_parameters_end_pos),
                                        MessageTemplate.UnexpectedEndOfArgString);
                        return;
                    }
                }
                Expect(Token.RightParen);
                int formals_end_position = end_position();

                CheckArityRestrictions(formals.arity, kind, formals.has_rest, function_scope.start_position(),
                                       formals_end_position);
                Expect(Token.LeftBrace);
            }
            formals.duplicate_loc = formals_scope.duplicate_location();
        }

        num_parameters = formals.num_parameters();
        function_length = formals.function_length;

        using AcceptINScope accept_in = new(this, true);
        ParseFunctionBody(body, function_name, pos, formals, kind, function_syntax_kind, FunctionBodyType.kBlock);

        has_duplicate_parameters = formals.has_duplicate();

        expected_property_count = function_state.expected_property_count();
        suspend_count = function_state.suspend_count();
    }

    public override void DeclareClassVariable(ClassScope scope, AstRawString name, ClassInfo class_info,
                                              int class_token_pos)
    {
        // Declare a special class variable for anonymous classes with the dot
        // if we need to save it for static private method access.
        Variable class_variable = scope.DeclareClassVariable(ast_value_factory(), name, class_token_pos);
        Declaration declaration = factory().NewVariableDeclaration(class_token_pos);
        scope.declarations().Add(declaration);
        declaration.set_var(class_variable);
    }

    private VariableProxy CreateSyntheticContextVariableProxy(ClassScope scope, ClassInfo class_info,
                                                              AstRawString name, bool is_static)
    {
        if (scope.from_scope_info())
        {
            DeclarationScope declaration_scope =
                is_static ? class_info.static_elements_scope : class_info.instance_members_scope;
            return declaration_scope.NewUnresolved(factory().ast_node_factory(), name, position());
        }
        VariableProxy proxy = DeclareBoundVariable(name, VariableMode.Const, kNoSourcePosition);
        proxy.var().ForceContextAllocation();
        return proxy;
    }

    private VariableProxy CreatePrivateNameVariable(ClassScope scope, VariableMode mode, IsStaticFlag is_static_flag,
                                                    AstRawString name)
    {
        int begin = position();
        int end = end_position();
        Variable var = scope.DeclarePrivateName(name, mode, is_static_flag, out bool was_added);
        if (!was_added)
        {
            Scanner.Location loc = new(begin, end);
            ReportMessageAt(loc, MessageTemplate.VarRedeclaration, var.raw_name());
        }
        return factory().NewVariableProxy(var, begin);
    }

    public override void AddInstanceFieldOrStaticElement(ClassLiteralProperty property, ClassInfo class_info,
                                                         bool is_static)
    {
        if (is_static)
        {
            class_info.static_elements.Add(factory().NewClassLiteralStaticElement(property));
            return;
        }
        class_info.instance_fields.Add(property);
    }

    public override void DeclarePublicClassField(ClassScope scope, ClassLiteralProperty property, bool is_static,
                                                 bool is_computed_name, ClassInfo class_info)
    {
        AddInstanceFieldOrStaticElement(property, class_info, is_static);

        if (is_computed_name)
        {
            // We create a synthetic variable name here so that scope
            // analysis doesn't dedupe the vars.
            AstRawString name = ClassFieldVariableName(ast_value_factory(), class_info.computed_field_count);
            VariableProxy proxy = CreateSyntheticContextVariableProxy(scope, class_info, name, is_static);
            property.set_computed_name_proxy(proxy);
            class_info.public_members.Add(property);
        }
    }

    public override void DeclarePrivateClassMember(ClassScope scope, AstRawString property_name,
                                                   ClassLiteralProperty property, ClassLiteralProperty.Kind kind,
                                                   bool is_static, ClassInfo class_info)
    {
        if (kind == ClassLiteralProperty.Kind.FIELD || kind == ClassLiteralProperty.Kind.AUTO_ACCESSOR)
        {
            AddInstanceFieldOrStaticElement(property, class_info, is_static);
        }
        class_info.private_members.Add(property);

        VariableProxy proxy;
        if (scope.from_scope_info())
        {
            PrivateNameScopeIterator private_name_scope_iter = new(scope);
            proxy = (VariableProxy)ExpressionFromPrivateName(ref private_name_scope_iter, property_name, position());
        }
        else
        {
            proxy = CreatePrivateNameVariable(scope, GetVariableMode(kind),
                                              is_static ? IsStaticFlag.Static : IsStaticFlag.NotStatic,
                                              property_name);
            int pos = property.value().position();
            if (pos == kNoSourcePosition)
            {
                pos = property.key().position();
            }
            proxy.var().set_initializer_position(pos);
        }
        property.SetPrivateNameProxy(proxy);
    }

    // This method declares a property of the given class.  It updates the
    // following fields of class_info, as appropriate:
    //   - constructor
    //   - properties
    public override void DeclarePublicClassMethod(AstRawString class_name, ClassLiteralProperty property,
                                                  bool is_constructor, ClassInfo class_info)
    {
        if (is_constructor)
        {
            class_info.constructor = property.value().AsFunctionLiteral();
            class_info.constructor.set_raw_name(class_name != null
                                                    ? ast_value_factory().NewConsString(class_name)
                                                    : null);
            return;
        }

        class_info.public_members.Add(property);
    }

    public override void AddClassStaticBlock(Block block, ClassInfo class_info)
    {
        class_info.static_elements.Add(factory().NewClassLiteralStaticElement(block));
    }

    private FunctionLiteral CreateInitializerFunction(AstRawString class_name, DeclarationScope scope,
                                                      int function_literal_id, Statement initializer_stmt)
    {
        // function() { .. class fields initializer .. }
        using ScopedPtrList<Statement> statements = new(pointer_buffer());
        statements.Add(initializer_stmt);
        FunctionLiteral result = factory().NewFunctionLiteral(
            class_name, scope, statements, 0, 0, 0, FunctionLiteral.ParameterFlag.kNoDuplicateParameters,
            FunctionSyntaxKind.AccessorOrMethod, FunctionLiteral.EagerCompileHint.kShouldEagerCompile,
            scope.start_position(), false, function_literal_id);
        RecordFunctionLiteralSourceRange(result);

        return result;
    }

    private FunctionLiteral CreateStaticElementsInitializer(AstRawString name, ClassInfo class_info)
    {
        return CreateInitializerFunction(
            name, class_info.static_elements_scope, class_info.static_elements_function_id,
            factory().NewInitializeClassStaticElementsStatement(class_info.static_elements, kNoSourcePosition));
    }

    private FunctionLiteral CreateInstanceMembersInitializer(AstRawString name, ClassInfo class_info)
    {
        return CreateInitializerFunction(
            name, class_info.instance_members_scope, class_info.instance_members_function_id,
            factory().NewInitializeClassMembersStatement(class_info.instance_fields, kNoSourcePosition));
    }

    // This method generates a ClassLiteral AST node.
    // It uses the following fields of class_info:
    //   - constructor (if missing, it updates it with a default constructor)
    //   - proxy
    //   - extends
    //   - properties
    //   - has_static_computed_names
    public override Expression RewriteClassLiteral(ClassScope block_scope, AstRawString name, ClassInfo class_info,
                                                   int pos)
    {
        bool has_extends = class_info.extends != null;
        bool has_default_constructor = class_info.constructor == null;
        int end_pos = block_scope.end_position();
        if (has_default_constructor)
        {
            class_info.constructor = DefaultConstructor(name, has_extends, pos);
        }

        if (!IsEmptyIdentifier(name))
        {
            block_scope.class_variable().set_initializer_position(end_pos);
        }

        FunctionLiteral static_initializer = null;
        if (class_info.has_static_elements())
        {
            static_initializer = CreateStaticElementsInitializer(name, class_info);
        }

        FunctionLiteral instance_members_initializer_function = null;
        if (class_info.has_instance_members())
        {
            instance_members_initializer_function = CreateInstanceMembersInitializer(name, class_info);
            class_info.constructor.set_requires_instance_members_initializer(true);
            class_info.constructor.add_expected_properties(class_info.instance_fields.Count);
        }

        if (class_info.requires_brand)
        {
            class_info.constructor.set_class_scope_has_private_brand(true);
        }
        if (class_info.has_static_private_methods_or_accessors)
        {
            class_info.constructor.set_has_static_private_methods_or_accessors(true);
        }
        ClassLiteral class_literal = factory().NewClassLiteral(
            block_scope, class_info.extends, class_info.constructor, class_info.public_members,
            class_info.private_members, static_initializer, instance_members_initializer_function, pos, end_pos,
            class_info.has_static_computed_names, class_info.is_anonymous, class_info.home_object_variable,
            class_info.static_home_object_variable);

        AddFunctionForNameInference(class_info.constructor);
        return class_literal;
    }

    // Insert initializer statements for var-bindings shadowing parameter bindings
    // from a non-simple parameter list.
    public override void InsertShadowingVarBindingInitializers(Block inner_block)
    {
        // For each var-binding that shadows a parameter, insert an assignment
        // initializing the variable with the parameter.
        Scope inner_scope = inner_block.scope();
        Scope function_scope = inner_scope.outer_scope();
        using BlockState block_state = new(this, inner_scope);
        // According to https://tc39.es/ecma262/#sec-functiondeclarationinstantiation
        // If a variable's name conflicts with the names of both parameters and
        // functions, no bindings should be created for it. A set is used here
        // to record such variables.
        HashSet<Variable> hoisted_func_vars = new(ReferenceEqualityComparer.Instance);
        List<(Variable, Variable)> var_param_bindings = [];
        foreach (Declaration decl in inner_scope.declarations())
        {
            if (!decl.IsVariableDeclaration())
            {
                hoisted_func_vars.Add(decl.var());
                continue;
            }
            else if (decl.var().mode() != VariableMode.Var)
            {
                continue;
            }
            AstRawString name = decl.var().raw_name();
            Variable parameter = function_scope.LookupLocal(name);
            if (parameter == null) continue;
            var_param_bindings.Add((decl.var(), parameter));
        }

        foreach ((Variable first, Variable second) decl in var_param_bindings)
        {
            if (hoisted_func_vars.Contains(decl.first))
            {
                continue;
            }
            AstRawString name = decl.first.raw_name();
            VariableProxy to = NewUnresolved(name);
            VariableProxy from = factory().NewVariableProxy(decl.second);
            Expression assignment = factory().NewAssignment(Token.Assign, to, from, kNoSourcePosition);
            Statement statement = factory().NewExpressionStatement(assignment, kNoSourcePosition);
            inner_block.statements().Insert(0, statement);
        }
    }

    // Implement sloppy block-scoped functions, ES2015 Annex B 3.3
    public override void InsertSloppyBlockFunctionVarBindings(DeclarationScope scope)
    {
        // For the outermost eval scope, we cannot hoist during parsing: let
        // declarations in the surrounding scope may prevent hoisting, but the
        // information is unaccessible during parsing. In this case, we hoist later in
        // DeclarationScope::Analyze.
        if (scope.is_eval_scope() && scope.outer_scope() == original_scope_)
        {
            return;
        }
        scope.HoistSloppyBlockFunctions(factory());
    }

    // ----------------------------------------------------------------------------
    // Parser support

    // Parser::UpdateStatistics(script, use_counters, preparse_skipped): the use
    // counters to report to the embedder after the parse.
    public void UpdateStatistics(IParsingScript script, List<UseCounterFeature> use_counts,
                                 out int preparse_skipped)
    {
        // Move statistics to Isolate.
        for (int feature = 0; feature < (int)UseCounterFeature.kUseCounterFeatureCount; ++feature)
        {
            if (use_counts_[feature] > 0)
            {
                use_counts.Add((UseCounterFeature)feature);
            }
        }
        if (scanner_.FoundHtmlComment())
        {
            use_counts.Add(UseCounterFeature.kHtmlComment);
            if (script.line_offset() == 0 && script.column_offset() == 0)
            {
                use_counts.Add(UseCounterFeature.kHtmlCommentInExternalScript);
            }
        }
        if (scanner_.SawMagicCommentCompileHintsAll())
        {
            use_counts.Add(UseCounterFeature.kCompileHintsMagicAll);
        }
        if (scanner_.SawSourceMappingUrlMagicCommentAtSign())
        {
            use_counts.Add(UseCounterFeature.kSourceMappingUrlMagicCommentAtSign);
        }

        preparse_skipped = total_preparse_skipped_;
    }

    // Parser::HandleDebugMagicComments: the sourceURL / sourceMappingURL /
    // debugId magic comments the scanner saw, for the engine to store on the
    // Script.
    public string SourceUrl() => scanner_.SourceUrl();
    public string SourceMappingUrl() => scanner_.SourceMappingUrl();
    public string DebugId() => scanner_.DebugId();

    // Impl::TemplateLiteralState.
    private sealed class TemplateLiteral(int pos)
    {
        private readonly List<AstRawString> cooked_ = new(8);
        private readonly List<AstRawString> raw_ = new(8);
        private readonly List<Expression> expressions_ = new(8);
        private readonly int pos_ = pos;

        public List<AstRawString> cooked() => cooked_;
        public List<AstRawString> raw() => raw_;
        public List<Expression> expressions() => expressions_;
        public int position() => pos_;

        public void AddTemplateSpan(AstRawString cooked, AstRawString raw, int end)
        {
            cooked_.Add(cooked);
            raw_.Add(raw);
        }

        public void AddExpression(Expression expression) => expressions_.Add(expression);
    }

    public override object OpenTemplateLiteral(int pos) => new TemplateLiteral(pos);

    // "should_cook" means that the span can be "cooked": in tagged template
    // literals, both the raw and "cooked" representations are available to user
    // code ("cooked" meaning that escape sequences are converted to their
    // interpreted values). Invalid escape sequences cause the cooked span
    // to be represented by undefined, instead of being a syntax error.
    // "tail" indicates that this span is the last in the literal.
    public override void AddTemplateSpan(object state, bool should_cook, bool tail)
    {
        int end = scanner().location().end_pos - (tail ? 1 : 2);
        AstRawString raw = scanner().CurrentRawSymbol(ast_value_factory());
        if (should_cook)
        {
            AstRawString cooked = scanner().CurrentSymbol(ast_value_factory());
            ((TemplateLiteral)state).AddTemplateSpan(cooked, raw, end);
        }
        else
        {
            ((TemplateLiteral)state).AddTemplateSpan(null, raw, end);
        }
    }

    public override void AddTemplateExpression(object state, Expression expression)
        => ((TemplateLiteral)state).AddExpression(expression);

    public override Expression CloseTemplateLiteral(object state, int start, Expression tag)
    {
        TemplateLiteral lit = (TemplateLiteral)state;
        int pos = lit.position();
        List<AstRawString> cooked_strings = lit.cooked();
        List<AstRawString> raw_strings = lit.raw();
        List<Expression> expressions = lit.expressions();

        if (tag == null)
        {
            if (cooked_strings.Count == 1)
            {
                return factory().NewStringLiteral(cooked_strings[0], pos);
            }
            return factory().NewTemplateLiteral(cooked_strings, expressions, pos);
        }
        else
        {
            // GetTemplateObject
            Expression template_object = factory().NewGetTemplateObject(cooked_strings, raw_strings, pos);

            // Call TagFn
            using ScopedPtrList<Expression> call_args = new(pointer_buffer());
            call_args.Add(template_object);
            call_args.AddAll(expressions);
            return factory().NewTaggedTemplate(tag, call_args, pos);
        }
    }

    public override void SetLanguageMode(Scope scope, LanguageMode mode)
    {
        UseCounterFeature feature;
        if (is_sloppy(mode))
        {
            feature = UseCounterFeature.kSloppyMode;
        }
        else
        {
            feature = UseCounterFeature.kStrictMode;
        }
        ++use_counts_[(int)feature];
        scope.SetLanguageMode(mode);
    }

    public override Expression ExpressionListToExpression(ScopedPtrList<Expression> args)
    {
        Expression expr = args.at(0);
        if (args.length() == 1) return expr;
        if (args.length() == 2)
        {
            return factory().NewBinaryOperation(Token.Comma, expr, args.at(1), args.at(1).position());
        }
        NaryOperation result = factory().NewNaryOperation(Token.Comma, expr, args.length() - 1);
        for (int i = 1; i < args.length(); i++)
        {
            result.AddSubsequent(args.at(i), args.at(i).position());
        }
        return result;
    }

    private void SetFunctionNameFromLiteralPropertyName(LiteralProperty property, AstRawString name,
                                                        AstRawString prefix)
    {
        if (has_error()) return;
        // Ensure that the function we are going to create has shared name iff
        // we are not going to set it later.
        if (property.NeedsSetFunctionName())
        {
            name = null;
            prefix = null;
        }

        Expression value = property.value();
        SetFunctionName(value, name, prefix);
    }

    public override void SetFunctionNameFromClassPropertyName(ClassLiteralProperty property, AstRawString name,
                                                              AstRawString prefix = null)
        => SetFunctionNameFromLiteralPropertyName(property, name, prefix);

    public override void SetFunctionNameFromPropertyName(ObjectLiteralProperty property, AstRawString name,
                                                         AstRawString prefix = null)
    {
        // Ignore "__proto__" as a name when it's being used to set the [[Prototype]]
        // of an object literal.
        // See
        // https://tc39.es/ecma262/#sec-__proto__-property-names-in-object-initializers.
        if (property.IsPrototype() || has_error()) return;

        SetFunctionNameFromLiteralPropertyName(property, name, prefix);
    }

    public override void SetFunctionNameFromIdentifierRef(Expression value, Expression identifier)
    {
        if (!identifier.IsVariableProxy()) return;
        // IsIdentifierRef of parenthesized expressions is false.
        if (identifier.is_parenthesized()) return;
        SetFunctionName(value, identifier.AsVariableProxy().raw_name());
    }

    private void SetFunctionName(Expression value, AstRawString name, AstRawString prefix = null)
    {
        if (!value.IsAnonymousFunctionDefinition() && !value.IsConciseMethodDefinition() &&
            !value.IsAccessorFunctionDefinition())
        {
            return;
        }
        FunctionLiteral function = value.AsFunctionLiteral();
        if (value.IsClassLiteral())
        {
            function = ((ClassLiteral)value).constructor();
        }
        if (function != null)
        {
            AstConsString cons_name = null;
            if (name != null)
            {
                if (prefix != null)
                {
                    cons_name = ast_value_factory().NewConsString(prefix, name);
                }
                else
                {
                    cons_name = ast_value_factory().NewConsString(name);
                }
            }
            function.set_raw_name(cons_name);
        }
    }
}
