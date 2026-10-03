// Copyright 2012 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// The implementation-specific operations ParserBase<Impl> reaches through
// impl() (src/parsing/parser.h, src/parsing/preparser.h). V8 resolves them
// statically through CRTP; here they are abstract methods that Parser and
// PreParser override. Where V8 overloads a hook on types that coincide for
// the PreParser (e.g. ObjectLiteralProperty and ClassLiteralProperty are both
// PreParserExpression), the C# names are distinguished.

#nullable disable

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public abstract partial class ParserBase<TImpl, TExpression, TIdentifier, TStatement, TBlock, TFunctionLiteral,
    TObjectLiteralProperty, TClassLiteralProperty, TExpressionList, TObjectPropertyList, TStatementList,
    TClassPropertyList, TClassStaticElementList, TFormalParameters, TFactory, TFuncNameInferrer>
{
    // "null" return type creators.
    public abstract TIdentifier NullIdentifier();
    public abstract TExpression NullExpression();
    public abstract TStatement NullStatement();
    public abstract TBlock NullBlock();
    public abstract TFunctionLiteral NullFunctionLiteral();
    public abstract TExpression FailureExpression();

    public abstract bool IsNull(TIdentifier subject);
    public abstract bool IsNull(TExpression subject);
    public abstract bool IsNull(TStatement subject);
    public abstract bool IsNullProperty(TObjectLiteralProperty subject);

    public abstract bool IsIterationStatement(TStatement subject);
    public abstract TStatement AsIterationStatement(TStatement s);

    // Non-null empty string.
    public abstract TIdentifier EmptyIdentifierString();
    public abstract bool IsEmptyIdentifier(TIdentifier subject);

    // Producing data during the recursive descent.
    public abstract TIdentifier GetSymbol();
    public abstract TIdentifier GetIdentifier();
    public abstract TIdentifier GetNumberAsSymbol();
    public abstract TIdentifier GetBigIntAsSymbol();

    // Helper functions for recursive descent.
    public abstract bool IsEval(TIdentifier identifier);
    public abstract bool IsArguments(TIdentifier identifier);
    public abstract bool IsEvalOrArguments(TIdentifier identifier);
    public abstract bool IsConstructor(TIdentifier identifier);
    public abstract bool IsThisProperty(TExpression expression);
    public abstract bool IsPrivateReference(TExpression expression);
    public abstract bool IsIdentifier(TExpression expression);
    public abstract TIdentifier AsIdentifier(TExpression expression);
    public abstract bool IsBoilerplateProperty(TObjectLiteralProperty property);
    public abstract bool ParsingExtension();
    public abstract bool IsNative(TExpression expr);
    public abstract bool IsArrayIndex(TIdentifier @string, out uint index);
    public abstract bool IsStringLiteral(TStatement statement);
    public abstract void GetDefaultStrings(out TIdentifier default_string, out TIdentifier dot_default_string);
    public abstract AstRawString GetRawNameFromIdentifier(TIdentifier arg);
    public abstract bool IdentifierEquals(TIdentifier identifier, AstRawString other);

    // V8 passes Identifier arguments to ReportMessageAt directly; for the
    // PreParser that goes through PreParserIdentifierToAstRawString.
    public abstract AstRawString IdentifierToAstRawString(TIdentifier arg);

    // expression->AsCall()->is_tagged_template().
    public abstract bool IsTaggedTemplateCall(TExpression expression);

    // expression->AsFunctionLiteral()->SetShouldEagerCompile().
    public abstract void SetShouldEagerCompile(TExpression function_literal);

    public abstract bool parse_lazily();
    public abstract bool AllowsLazyParsingWithoutUnresolvedVariables();
    public abstract bool HasCheckedSyntax();
    public abstract bool ParsingDynamicFunctionDeclaration();
    public abstract IRegExpSyntaxValidator regexp_syntax_validator();

    // Functions for encapsulating the differences between parsing and preparsing;
    // operations interleaved with the recursive descent.
    public abstract void PushLiteralName(TIdentifier id);
    public abstract void PushPropertyName(TExpression expression);
    public abstract void PushEnclosingName(TIdentifier name);
    public abstract void AddFunctionForNameInference(TFunctionLiteral func_to_infer);
    public abstract void InferFunctionName();
    public abstract void CheckAssigningFunctionLiteralToProperty(TExpression left, TExpression right);

    public abstract bool ShortcutLiteralBinaryExpression(ref TExpression x, TExpression y, Token op, int pos);
    public abstract bool CollapseConditionalChain(ref TExpression x, TExpression cond, TExpression then_expression,
                                                  TExpression else_expression, int pos, SourceRange then_range);
    public abstract void AppendConditionalChainElse(ref TExpression x, SourceRange else_range);
    public abstract bool CollapseNaryExpression(ref TExpression x, TExpression y, Token op, int pos,
                                                SourceRange range);
    public abstract TExpression BuildUnaryExpression(TExpression expression, Token op, int pos);
    public abstract TExpression NewThrowReferenceError(MessageTemplate message, int pos);

    public abstract void ReportUnexpectedTokenAt(Scanner.Location location, Token token,
                                                 MessageTemplate message = MessageTemplate.UnexpectedToken);

    public abstract TExpression ThisExpression();
    public abstract TExpression NewThisExpression(int pos);
    public abstract TExpression NewSuperPropertyReference(int pos);
    public abstract TExpression NewSuperCallReference(int pos);
    public abstract TExpression NewTargetExpression(int pos);
    public abstract TExpression ImportMetaExpression(int pos);
    public abstract TExpression ExpressionFromLiteral(Token token, int pos);
    public abstract TExpression ExpressionFromPrivateName(ref PrivateNameScopeIterator private_name_scope,
                                                          TIdentifier name, int start_position);
    public abstract TExpression ExpressionFromIdentifier(TIdentifier name, int start_position,
                                                         InferName infer = InferName.kYes);
    public abstract void DeclareIdentifier(TIdentifier name, int start_position);
    public abstract Variable DeclareCatchVariableName(Scope scope, TIdentifier name);
    public abstract TClassPropertyList NewClassPropertyList(int size);
    public abstract TClassStaticElementList NewClassStaticElementList(int size);
    public abstract TExpression NewV8Intrinsic(TIdentifier name, TExpressionList args, int pos);
    public abstract TStatement NewThrowStatement(TExpression exception, int pos);

    public abstract TFormalParameters NewFormalParameters(DeclarationScope scope);
    public abstract void ValidateDuplicate(TFormalParameters parameters);
    public abstract void ValidateStrictMode(TFormalParameters parameters);
    public abstract void AddFormalParameter(TFormalParameters parameters, TExpression pattern,
                                            TExpression initializer, int initializer_end_position, bool is_rest);
    public abstract void DeclareFormalParameters(TFormalParameters parameters);
    public abstract void ReindexArrowFunctionFormalParameters(TFormalParameters parameters,
                                                              AllowReindexScope scope);
    public abstract void ReindexComputedMemberName(TExpression computed_name, AllowReindexScope scope);
    public abstract void DeclareArrowFunctionFormalParameters(TFormalParameters parameters, TExpression @params,
                                                              Scanner.Location params_loc);
    public abstract TExpression ExpressionListToExpression(TExpressionList args);

    public abstract void SetFunctionNameFromPropertyName(TObjectLiteralProperty property, TIdentifier name,
                                                         AstRawString prefix = null);
    public abstract void SetFunctionNameFromClassPropertyName(TClassLiteralProperty property, TIdentifier name,
                                                              AstRawString prefix = null);
    public abstract void SetFunctionNameFromIdentifierRef(TExpression value, TExpression identifier);

    public abstract void CountUsage(UseCounterFeature feature);

    public abstract FunctionLiteral.EagerCompileHint GetEmbedderCompileHint(
        FunctionLiteral.EagerCompileHint current_compile_hint, int position);

    // Source ranges (block coverage). The PreParser's are empty.
    public abstract void RecordBinaryOperationSourceRange(TExpression node, SourceRange right_range);
    public abstract void RecordBlockSourceRange(TBlock node, int continuation_position);
    public abstract void RecordCaseClauseSourceRange(object node, SourceRange body_range);
    public abstract void RecordConditionalSourceRange(TExpression node, SourceRange then_range,
                                                      SourceRange else_range);
    public abstract void RecordExpressionSourceRange(TExpression node, SourceRange right_range);
    public abstract void RecordFunctionLiteralSourceRange(TFunctionLiteral node);
    public abstract void RecordIfStatementSourceRange(TStatement node, SourceRange then_range,
                                                      SourceRange else_range);
    public abstract void RecordIterationStatementSourceRange(TStatement node, SourceRange body_range);
    public abstract void RecordJumpStatementSourceRange(TStatement node, int continuation_position);
    public abstract void RecordSuspendSourceRange(TExpression node, int continuation_position);
    public abstract void RecordSwitchStatementSourceRange(TStatement node, int continuation_position);
    public abstract void RecordThrowSourceRange(TStatement node, int continuation_position);

    public abstract void SetLanguageMode(Scope scope, LanguageMode mode);
    public abstract void PrepareGeneratorVariables();

    // Impl::TemplateLiteralState is the Parser's TemplateLiteral* and an empty
    // struct for the PreParser; it is passed around as an object here.
    public abstract object OpenTemplateLiteral(int pos);
    public abstract void AddTemplateSpan(object state, bool should_cook, bool tail);
    public abstract void AddTemplateExpression(object state, TExpression expression);
    public abstract TExpression CloseTemplateLiteral(object state, int start, TExpression tag);

    public abstract TExpression InitializeObjectLiteral(TExpression literal);
    public abstract TStatement BuildInitializationBlock(DeclarationParsingResult parsing_result);
    public abstract TStatement RewriteSwitchStatement(TStatement switch_statement, Scope scope);
    public abstract TBlock RewriteCatchPattern(CatchInfo catch_info);
    public abstract void ReportVarRedeclarationIn(AstRawString name, Scope scope);
    public abstract TStatement RewriteTryStatement(TBlock try_block, TBlock catch_block, SourceRange catch_range,
                                                   TBlock finally_block, SourceRange finally_range,
                                                   CatchInfo catch_info, int pos);
    public abstract void ParseGeneratorFunctionBody(int pos, FunctionKind kind, TStatementList body);
    public abstract void ParseAsyncGeneratorFunctionBody(int pos, FunctionKind kind, TStatementList body);
    public abstract void DeclareFunctionNameVar(TIdentifier function_name, FunctionSyntaxKind function_syntax_kind,
                                                DeclarationScope function_scope);
    public abstract TStatement DeclareFunction(TIdentifier variable_name, TFunctionLiteral function,
                                               VariableMode mode, VariableKind kind, int beg_pos, int end_pos,
                                               List<AstRawString> names);
    public abstract TStatement DeclareClass(TIdentifier variable_name, TExpression value, List<AstRawString> names,
                                            int class_token_pos, int end_pos);
    public abstract void DeclareClassVariable(ClassScope scope, TIdentifier name, ClassInfo class_info,
                                              int class_token_pos);
    public abstract void AddInstanceFieldOrStaticElement(TClassLiteralProperty property, ClassInfo class_info,
                                                         bool is_static);
    public abstract void DeclarePrivateClassMember(ClassScope scope, TIdentifier property_name,
                                                   TClassLiteralProperty property, ClassLiteralProperty.Kind kind,
                                                   bool is_static, ClassInfo class_info);
    public abstract void DeclarePublicClassMethod(TIdentifier class_name, TClassLiteralProperty property,
                                                  bool is_constructor, ClassInfo class_info);
    public abstract void DeclarePublicClassField(ClassScope scope, TClassLiteralProperty property, bool is_static,
                                                 bool is_computed_name, ClassInfo class_info);
    public abstract void AddClassStaticBlock(TBlock block, ClassInfo class_info);
    public abstract TExpression RewriteClassLiteral(ClassScope scope, TIdentifier name, ClassInfo class_info,
                                                    int pos);
    public abstract TStatement DeclareNative(TIdentifier name, int pos);
    public abstract TClassLiteralProperty NewClassLiteralPropertyWithAccessorInfo(
        ClassScope scope, ClassInfo class_info, TIdentifier name, TExpression key, TExpression value, bool is_static,
        bool is_computed_name, bool is_private, int pos);

    public abstract TBlock RewriteForVarInLegacy(ForInfo for_info);
    public abstract void DesugarBindingInForEachStatement(ForInfo for_info, ref TBlock body_block,
                                                          ref TExpression each_variable);
    public abstract TBlock CreateForEachStatementTDZ(TBlock init_block, ForInfo for_info);
    public abstract TStatement DesugarLexicalBindingsInForStatement(TStatement loop, TStatement init,
                                                                    TExpression cond, TStatement next,
                                                                    TStatement body, Scope inner_scope,
                                                                    ForInfo for_info);
    public abstract TBlock BuildParameterInitializationBlock(TFormalParameters parameters);
    public abstract void InsertSloppyBlockFunctionVarBindings(DeclarationScope scope);
    public abstract void InsertShadowingVarBindingInitializers(TBlock block);

    public abstract TFunctionLiteral ParseFunctionLiteral(TIdentifier name, Scanner.Location function_name_location,
                                                          FunctionNameValidity function_name_validity,
                                                          FunctionKind kind, int function_token_position,
                                                          FunctionSyntaxKind type, LanguageMode language_mode,
                                                          List<AstRawString> arguments_for_wrapped_function);

    public abstract bool SkipFunction(int function_literal_id, AstRawString name, FunctionKind kind,
                                      FunctionSyntaxKind function_syntax_kind, DeclarationScope function_scope,
                                      ref int num_parameters, ref int function_length,
                                      ref ProducedPreparseData produced_preparse_data);

    public abstract Variable DeclareVariable(AstRawString name, VariableKind kind, VariableMode mode,
                                             InitializationFlag init, Scope declaration_scope, out bool was_added,
                                             int begin, int end = kNoSourcePosition);
    public abstract void DeclareAndBindVariable(VariableProxy proxy, VariableKind kind, VariableMode mode,
                                                Scope declaration_scope, out bool was_added,
                                                int initializer_position, VariableProxy.BindingMode binding_mode);

    // loop->Initialize(...) on the typed statements (DoWhile/While, For,
    // ForEach/ForOf) and switch_statement->cases()->Add(clause).
    public abstract void InitializeConditionalLoop(TStatement loop, TExpression cond, TStatement body);
    public abstract void InitializeForLoop(TStatement loop, TStatement init, TExpression cond, TStatement next,
                                           TStatement body);
    public abstract void InitializeForEachStatement(TStatement loop, TExpression each, TExpression subject,
                                                    TStatement body, Scope subject_scope);
    public abstract void AddCaseClause(TStatement switch_statement, object clause);
}
