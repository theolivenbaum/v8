// Port of test/unittests/parser/parsing-unittest.cc.
//
// The parser-sync tests parse every program with both the PreParser and the
// Parser and check that they agree, as in V8. Where V8 reads the parser's
// error through the exception ReportErrors throws, the port reads the same
// message from the PendingCompilationErrorHandler. Tests that run JavaScript
// or inspect heap objects have no counterpart here; they are listed at the
// end of the file with the reason.

#nullable disable

using System.Text;
using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing.Tests.Parser;

public partial class ParsingTest
{
    public enum ParserFlag { kAllowLazy, kAllowNatives }

    public enum ParserSyncTestResult { kSuccessOrError, kSuccess, kError }

    protected const ParserFlag kAllowLazy = ParserFlag.kAllowLazy;
    protected const ParserFlag kAllowNatives = ParserFlag.kAllowNatives;
    protected const ParserSyncTestResult kSuccessOrError = ParserSyncTestResult.kSuccessOrError;
    protected const ParserSyncTestResult kSuccess = ParserSyncTestResult.kSuccess;
    protected const ParserSyncTestResult kError = ParserSyncTestResult.kError;

    // The V8 flags the tests change (V8's are process-global; each xUnit
    // test gets a fresh instance here, so FlagScope needs no restore).
    public sealed class TestV8Flags
    {
        public bool allow_natives_syntax;
        public bool lazy = true;
        public bool lazy_streaming = true;
        public bool js_decorators;
        public bool js_source_phase_imports;
        public bool js_defer_import_eval;
        public bool harmony_import_attributes = true;
        public bool js_esm_ns_reexport = true;

        public ParsingFlags ToParsingFlags(bool? allow_natives = null) => new()
        {
            allow_natives_syntax = allow_natives ?? allow_natives_syntax,
            lazy = lazy,
            js_decorators = js_decorators,
            js_source_phase_imports = js_source_phase_imports,
            js_defer_import_eval = js_defer_import_eval,
            harmony_import_attributes = harmony_import_attributes,
            js_esm_ns_reexport = js_esm_ns_reexport,
        };
    }

    protected readonly TestV8Flags v8_flags = new();

    private sealed class Input(bool assigned, string source, int[] location)
    {
        public readonly bool assigned = assigned;
        public readonly string source = source;
        public readonly int[] location = location;  // "Directions" to the relevant scope.
    }

    // CHECK_PARSE_PROGRAM
    private static void CHECK_PARSE_PROGRAM(ParseInfo info, IParsingScript script)
    {
        if (!ParsingEntry.ParseProgram(info, script))
        {
            FAIL_WITH_PENDING_PARSER_ERROR(info, script);
        }
        Assert.False(info.pending_error_handler().has_pending_error());
        Assert.NotNull(info.literal());
    }

    // CHECK_PARSE_FUNCTION
    private static void CHECK_PARSE_FUNCTION(ParseInfo info, IParsingSharedFunctionInfo shared)
    {
        if (!ParsingEntry.ParseFunction(info, shared))
        {
            FAIL_WITH_PENDING_PARSER_ERROR(info, shared.script());
        }
        Assert.False(info.pending_error_handler().has_pending_error());
        Assert.NotNull(info.literal());
    }

    private static string ParserErrorMessage(PendingCompilationErrorHandler handler) =>
        handler.stack_overflow() ? "Maximum call stack size exceeded" : handler.FormatErrorMessageForTest();

    private static void FAIL_WITH_PENDING_PARSER_ERROR(ParseInfo info, IParsingScript script)
    {
        Assert.Fail("Parser failed on:\n\t" + script.source() + "\nwith error:\n\t" +
                    ParserErrorMessage(info.pending_error_handler()) + "\nHowever, we expected no error.");
    }

    private static bool TokenIsAutoSemicolon(Token token)
    {
        switch (token)
        {
            case Token.Semicolon:
            case Token.Eos:
            case Token.RightBrace:
                return true;
            default:
                return false;
        }
    }

    protected void TestStreamScanner(Utf16CharacterStream stream, Token[] expected_tokens,
                                     int skip_pos = 0,  // Zero means not skipping.
                                     int skip_to = 0)
    {
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForTest();

        var scanner = new Scanner(stream, flags);
        scanner.Initialize();

        int i = 0;
        do
        {
            Token expected = expected_tokens[i];
            Token actual = scanner.Next();
            Assert.Equal(Token.StringOf(expected), Token.StringOf(actual));
            if (scanner.location().end_pos == skip_pos)
            {
                scanner.SeekForward(skip_to);
            }
            i++;
        } while (expected_tokens[i] != Token.Illegal);
    }

    protected void TestScanRegExp(string re_source, string expected)
    {
        Utf16CharacterStream stream = ScannerStream.ForTesting(re_source);
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForTest();
        var scanner = new Scanner(stream, flags);
        scanner.Initialize();

        Token start = scanner.peek();
        Assert.True(start == Token.Div || start == Token.AssignDiv);
        Assert.True(scanner.ScanRegExpPattern());
        scanner.Next();  // Current token is now the regexp literal.
        var ast_value_factory = new AstValueFactory();
        AstRawString current_symbol = scanner.CurrentSymbol(ast_value_factory);
        Assert.True(current_symbol.is_one_byte());
        Assert.Equal(expected, current_symbol.ToString());
    }

    // UnoptimizedCompileFlags::ForScriptCompile for a plain classic script.
    protected UnoptimizedCompileFlags NewScriptFlags() =>
        UnoptimizedCompileFlags.ForScriptCompile(
            v8_flags.ToParsingFlags(),
            new UnoptimizedCompileFlags.ScriptDetails(1, true, LanguageMode.Sloppy, false, false, false, false, false));

    protected ParseInfo NewParseInfo(UnoptimizedCompileFlags flags) => new(flags, v8_flags.ToParsingFlags());

    protected void CheckParsesToNumber(string source)
    {
        string full_source = "function f() { return " + source + "; }";

        var script = new SourceScript(full_source, 1);
        UnoptimizedCompileFlags flags = NewScriptFlags();
        flags.set_allow_lazy_parsing(false);
        flags.set_is_toplevel(true);
        ParseInfo info = NewParseInfo(flags);

        CHECK_PARSE_PROGRAM(info, script);

        Assert.Equal(1, info.scope().declarations().LengthForTest());
        Declaration decl = info.scope().declarations().AtForTest(0);
        FunctionLiteral fun = ((FunctionDeclaration)decl).fun();
        Assert.Single(fun.body());
        Assert.True(fun.body()[0].IsReturnStatement());
        ReturnStatement ret = (ReturnStatement)fun.body()[0];
        Literal lit = ret.expression().AsLiteral();
        Assert.True(lit.IsNumberLiteral());
    }

    protected void TestParserSyncWithFlags(string source, HashSet<ParserFlag> flags, ParserSyncTestResult result,
                                           bool is_module = false, bool test_preparser = true,
                                           bool ignore_error_msg = false)
    {
        // SetGlobalFlags / SetParserFlags.
        ParsingFlags parsing_flags = v8_flags.ToParsingFlags(flags.Contains(kAllowNatives));
        UnoptimizedCompileFlags compile_flags = UnoptimizedCompileFlags.ForToplevelCompile(
            parsing_flags, 1, true, LanguageMode.Sloppy, REPLMode.No, ScriptType.Classic, parsing_flags.lazy);
        compile_flags.set_allow_natives_syntax(flags.Contains(kAllowNatives));
        compile_flags.set_is_module(is_module);

        // Preparse the data.
        var pending_error_handler = new PendingCompilationErrorHandler();
        if (test_preparser)
        {
            Utf16CharacterStream stream = ScannerStream.For(source);
            var scanner = new Scanner(stream, compile_flags);
            var ast_value_factory = new AstValueFactory();
            var preparser = new PreParser(scanner, ast_value_factory, pending_error_handler, compile_flags,
                                          parsing_flags);
            scanner.Initialize();
            PreParser.PreParseResult pre_parse_result = preparser.PreParseProgram();
            Assert.Equal(PreParser.PreParseResult.kPreParseSuccess, pre_parse_result);
        }

        // Parse the data
        FunctionLiteral function;
        string message_string = null;
        {
            var script = new SourceScript(source, compile_flags.script_id());
            var info = new ParseInfo(compile_flags, parsing_flags);
            if (!ParsingEntry.ParseProgram(info, script))
            {
                message_string = ParserErrorMessage(info.pending_error_handler());
            }
            else
            {
                Assert.False(info.pending_error_handler().has_pending_error());
            }
            function = info.literal();
        }

        // Check that preparsing fails iff parsing fails.
        if (function == null)
        {
            if (result == kSuccess)
            {
                Assert.Fail("Parser failed on:\n\t" + source + "\nwith error:\n\t" + message_string +
                            "\nHowever, we expected no error.");
            }

            if (test_preparser && !pending_error_handler.has_pending_error() &&
                !pending_error_handler.has_error_unidentifiable_by_preparser())
            {
                Assert.Fail("Parser failed on:\n\t" + source + "\nwith error:\n\t" + message_string +
                            "\nHowever, the preparser succeeded");
            }
            // Check that preparser and parser produce the same error, except for
            // cases where we do not track errors in the preparser.
            if (test_preparser && !ignore_error_msg && !pending_error_handler.has_error_unidentifiable_by_preparser())
            {
                string preparser_message = ParserErrorMessage(pending_error_handler);
                if (message_string != preparser_message)
                {
                    Assert.Fail("Expected parser and preparser to produce the same error on:\n\t" + source +
                                "\nHowever, found the following error messages\n\tparser:    " + message_string +
                                "\n\tpreparser: " + preparser_message + "\n");
                }
            }
        }
        else if (test_preparser && pending_error_handler.has_pending_error())
        {
            Assert.Fail("Preparser failed on:\n\t" + source + "\nwith error:\n\t" +
                        ParserErrorMessage(pending_error_handler) + "\nHowever, the parser succeeded");
        }
        else if (result == kError)
        {
            Assert.Fail("Expected error on:\n\t" + source + "\nHowever, parser and preparser succeeded");
        }
    }

    protected void TestParserSync(string source, ParserFlag[] varying_flags, int varying_flags_length,
                                  ParserSyncTestResult result = kSuccessOrError,
                                  ParserFlag[] always_true_flags = null, int always_true_flags_length = 0,
                                  ParserFlag[] always_false_flags = null, int always_false_flags_length = 0,
                                  bool is_module = false, bool test_preparser = true, bool ignore_error_msg = false)
    {
        for (int bits = 0; bits < (1 << varying_flags_length); bits++)
        {
            var flags = new HashSet<ParserFlag>();
            for (int flag_index = 0; flag_index < varying_flags_length; ++flag_index)
            {
                if ((bits & (1 << flag_index)) != 0) flags.Add(varying_flags[flag_index]);
            }
            for (int flag_index = 0; flag_index < always_true_flags_length; ++flag_index)
            {
                flags.Add(always_true_flags[flag_index]);
            }
            for (int flag_index = 0; flag_index < always_false_flags_length; ++flag_index)
            {
                flags.Remove(always_false_flags[flag_index]);
            }
            TestParserSyncWithFlags(source, flags, result, is_module, test_preparser, ignore_error_msg);
        }
    }

    private static readonly ParserFlag[] default_flags = [kAllowLazy, kAllowNatives];

    protected void RunParserSyncTest(string[][] context_data, string[] statement_data, ParserSyncTestResult result,
                                     ParserFlag[] flags = null, int flags_len = 0,
                                     ParserFlag[] always_true_flags = null, int always_true_len = 0,
                                     ParserFlag[] always_false_flags = null, int always_false_len = 0,
                                     bool is_module = false, bool test_preparser = true,
                                     bool ignore_error_msg = false)
    {
        // Experimental feature flags should not go here; pass the flags as
        // always_true_flags if the test needs them.
        if (flags == null)
        {
            flags = default_flags;
            flags_len = default_flags.Length;
            if (always_true_flags != null || always_false_flags != null)
            {
                // Remove always_true/false_flags from default_flags (if present).
                var generated_flags = new List<ParserFlag>();
                for (int i = 0; i < flags_len; ++i)
                {
                    bool use_flag = true;
                    for (int j = 0; use_flag && j < always_true_len; ++j)
                    {
                        if (flags[i] == always_true_flags[j]) use_flag = false;
                    }
                    for (int j = 0; use_flag && j < always_false_len; ++j)
                    {
                        if (flags[i] == always_false_flags[j]) use_flag = false;
                    }
                    if (use_flag) generated_flags.Add(flags[i]);
                }
                flags = generated_flags.ToArray();
                flags_len = flags.Length;
            }
        }
        for (int i = 0; context_data[i][0] != null; ++i)
        {
            for (int j = 0; statement_data[j] != null; ++j)
            {
                // Plug the source code pieces together.
                string program = context_data[i][0] + statement_data[j] + context_data[i][1];
                TestParserSync(program, flags, flags_len, result, always_true_flags, always_true_len,
                               always_false_flags, always_false_len, is_module, test_preparser, ignore_error_msg);
            }
        }
    }

    protected void RunModuleParserSyncTest(string[][] context_data, string[] statement_data,
                                           ParserSyncTestResult result, ParserFlag[] flags = null,
                                           int flags_len = 0, ParserFlag[] always_true_flags = null,
                                           int always_true_len = 0, ParserFlag[] always_false_flags = null,
                                           int always_false_len = 0, bool test_preparser = true,
                                           bool ignore_error_msg = false)
    {
        RunParserSyncTest(context_data, statement_data, result, flags, flags_len, always_true_flags,
                          always_true_len, always_false_flags, always_false_len, true, test_preparser,
                          ignore_error_msg);
    }

    protected void TestLanguageMode(string source, LanguageMode expected_language_mode)
    {
        var script = new SourceScript(source, 1);
        ParseInfo info = NewParseInfo(NewScriptFlags());
        CHECK_PARSE_PROGRAM(info, script);

        Assert.Equal(expected_language_mode, info.literal().language_mode());
    }

    private void TestMaybeAssigned(Input input, string variable, bool module, bool allow_lazy_parsing)
    {
        var script = new SourceScript(input.source, 1);
        UnoptimizedCompileFlags flags = NewScriptFlags();
        flags.set_is_module(module);
        flags.set_allow_lazy_parsing(allow_lazy_parsing);
        ParseInfo info = NewParseInfo(flags);

        CHECK_PARSE_PROGRAM(info, script);

        Scope scope = info.literal().scope();
        Assert.False(scope.AsDeclarationScope().was_lazily_parsed());
        Assert.Null(scope.sibling());
        Assert.True(module ? scope.is_module_scope() : scope.is_script_scope());

        Variable var;
        {
            // Find the variable.
            scope = ScopeTestHelper.FindScope(scope, input.location);
            AstRawString var_name = info.ast_value_factory().GetOneByteString(variable);
            var = scope.LookupForTesting(var_name);
        }

        Assert.NotNull(var);
        if (input.assigned) Assert.True(var.is_used());
        Assert.Equal(input.assigned, var.maybe_assigned() == MaybeAssignedFlag.kMaybeAssigned);
    }

    [Fact]
    public void AutoSemicolonToken()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsAutoSemicolon(token), Token.IsAutoSemicolon(token));
        }
    }

    private static bool TokenIsAnyIdentifier(Token token)
    {
        switch (token)
        {
            case Token.Identifier:
            case Token.Get:
            case Token.Set:
            case Token.Using:
            case Token.Of:
            case Token.Accessor:
            case Token.Async:
            case Token.Await:
            case Token.Yield:
            case Token.Let:
            case Token.Static:
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void AnyIdentifierToken()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsAnyIdentifier(token), Token.IsAnyIdentifier(token));
        }
    }

    private static bool TokenIsCallable(Token token)
    {
        switch (token)
        {
            case Token.Super:
            case Token.Identifier:
            case Token.Get:
            case Token.Set:
            case Token.Using:
            case Token.Of:
            case Token.Accessor:
            case Token.Async:
            case Token.Await:
            case Token.Yield:
            case Token.Let:
            case Token.Static:
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void CallableToken()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsCallable(token), Token.IsCallable(token));
        }
    }

    private static bool TokenIsValidIdentifier(Token token, LanguageMode language_mode, bool is_generator,
                                               bool disallow_await)
    {
        switch (token)
        {
            case Token.Identifier:
            case Token.Get:
            case Token.Set:
            case Token.Using:
            case Token.Of:
            case Token.Accessor:
            case Token.Async:
                return true;
            case Token.Yield:
                return !is_generator && is_sloppy(language_mode);
            case Token.Await:
                return !disallow_await;
            case Token.Let:
            case Token.Static:
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                return is_sloppy(language_mode);
            default:
                return false;
        }
    }

    [Fact]
    public void IsValidIdentifierToken()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            foreach (LanguageMode mode in new[] { LanguageMode.Sloppy, LanguageMode.Strict })
            {
                for (int is_generator = 0; is_generator < 2; is_generator++)
                {
                    for (int disallow_await = 0; disallow_await < 2; disallow_await++)
                    {
                        Assert.Equal(TokenIsValidIdentifier(token, mode, is_generator != 0, disallow_await != 0),
                                     Token.IsValidIdentifier(token, mode, is_generator != 0, disallow_await != 0));
                    }
                }
            }
        }
    }

    private static bool TokenIsStrictReservedWord(Token token)
    {
        switch (token)
        {
            case Token.Let:
            case Token.Yield:
            case Token.Static:
            case Token.FutureStrictReservedWord:
            case Token.EscapedStrictReservedWord:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsStrictReservedWord()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsStrictReservedWord(token), Token.IsStrictReservedWord(token));
        }
    }

    private static bool TokenIsLiteral(Token token)
    {
        switch (token)
        {
            case Token.NullLiteral:
            case Token.TrueLiteral:
            case Token.FalseLiteral:
            case Token.Number:
            case Token.Smi:
            case Token.BigInt:
            case Token.String:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsLiteralToken()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsLiteral(token), Token.IsLiteral(token));
        }
    }

    private static bool TokenIsAssignmentOp(Token token)
    {
        switch (token)
        {
            case Token.Init:
            case Token.Assign:
            // BINARY_OP_TOKEN_LIST(T, EXPAND_BINOP_ASSIGN_TOKEN)
            case Token.AssignNullish:
            case Token.AssignOr:
            case Token.AssignAnd:
            case Token.AssignBitOr:
            case Token.AssignBitXor:
            case Token.AssignBitAnd:
            case Token.AssignShl:
            case Token.AssignSar:
            case Token.AssignShr:
            case Token.AssignMul:
            case Token.AssignDiv:
            case Token.AssignMod:
            case Token.AssignExp:
            case Token.AssignAdd:
            case Token.AssignSub:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void AssignmentOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsAssignmentOp(token), Token.IsAssignmentOp(token));
        }
    }

    private static bool TokenIsArrowOrAssignmentOp(Token token) =>
        token == Token.Arrow || TokenIsAssignmentOp(token);

    [Fact]
    public void ArrowOrAssignmentOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsArrowOrAssignmentOp(token), Token.IsArrowOrAssignmentOp(token));
        }
    }

    private static bool TokenIsBinaryOp(Token token)
    {
        switch (token)
        {
            case Token.Comma:
            // BINARY_OP_TOKEN_LIST(T, EXPAND_BINOP_TOKEN)
            case Token.Nullish:
            case Token.Or:
            case Token.And:
            case Token.BitOr:
            case Token.BitXor:
            case Token.BitAnd:
            case Token.Shl:
            case Token.Sar:
            case Token.Shr:
            case Token.Mul:
            case Token.Div:
            case Token.Mod:
            case Token.Exp:
            case Token.Add:
            case Token.Sub:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void BinaryOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsBinaryOp(token), Token.IsBinaryOp(token));
        }
    }

    private static bool TokenIsCompareOp(Token token)
    {
        switch (token)
        {
            case Token.Eq:
            case Token.EqStrict:
            case Token.NotEq:
            case Token.NotEqStrict:
            case Token.LessThan:
            case Token.GreaterThan:
            case Token.LessThanEq:
            case Token.GreaterThanEq:
            case Token.InstanceOf:
            case Token.In:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void CompareOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsCompareOp(token), Token.IsCompareOp(token));
        }
    }

    private static bool TokenIsOrderedRelationalCompareOp(Token token)
    {
        switch (token)
        {
            case Token.LessThan:
            case Token.GreaterThan:
            case Token.LessThanEq:
            case Token.GreaterThanEq:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsOrderedRelationalCompareOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsOrderedRelationalCompareOp(token), Token.IsOrderedRelationalCompareOp(token));
        }
    }

    private static bool TokenIsEqualityOp(Token token)
    {
        switch (token)
        {
            case Token.Eq:
            case Token.EqStrict:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsEqualityOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsEqualityOp(token), Token.IsEqualityOp(token));
        }
    }

    private static bool TokenIsBitOp(Token token)
    {
        switch (token)
        {
            case Token.BitOr:
            case Token.BitXor:
            case Token.BitAnd:
            case Token.Shl:
            case Token.Sar:
            case Token.Shr:
            case Token.BitNot:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsBitOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsBitOp(token), Token.IsBitOp(token));
        }
    }

    private static bool TokenIsUnaryOp(Token token)
    {
        switch (token)
        {
            case Token.Not:
            case Token.BitNot:
            case Token.Delete:
            case Token.TypeOf:
            case Token.Void:
            case Token.Add:
            case Token.Sub:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsUnaryOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsUnaryOp(token), Token.IsUnaryOp(token));
        }
    }

    private static bool TokenIsPropertyOrCall(Token token)
    {
        switch (token)
        {
            case Token.TemplateSpan:
            case Token.TemplateTail:
            case Token.Period:
            case Token.QuestionPeriod:
            case Token.LeftBracket:
            case Token.LeftParen:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsPropertyOrCall()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsPropertyOrCall(token), Token.IsPropertyOrCall(token));
        }
    }

    private static bool TokenIsMember(Token token)
    {
        switch (token)
        {
            case Token.TemplateSpan:
            case Token.TemplateTail:
            case Token.Period:
            case Token.LeftBracket:
                return true;
            default:
                return false;
        }
    }

    private static bool TokenIsTemplate(Token token)
    {
        switch (token)
        {
            case Token.TemplateSpan:
            case Token.TemplateTail:
                return true;
            default:
                return false;
        }
    }

    private static bool TokenIsProperty(Token token)
    {
        switch (token)
        {
            case Token.Period:
            case Token.LeftBracket:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsMember()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsMember(token), Token.IsMember(token));
        }
    }

    [Fact]
    public void IsTemplate()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsTemplate(token), Token.IsTemplate(token));
        }
    }

    [Fact]
    public void IsProperty()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsProperty(token), Token.IsProperty(token));
        }
    }

    private static bool TokenIsCountOp(Token token)
    {
        switch (token)
        {
            case Token.Inc:
            case Token.Dec:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsCountOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsCountOp(token), Token.IsCountOp(token));
        }
    }

    [Fact]
    public void IsUnaryOrCountOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsUnaryOp(token) || TokenIsCountOp(token), Token.IsUnaryOrCountOp(token));
        }
    }

    private static bool TokenIsShiftOp(Token token)
    {
        switch (token)
        {
            case Token.Shl:
            case Token.Sar:
            case Token.Shr:
                return true;
            default:
                return false;
        }
    }

    [Fact]
    public void IsShiftOp()
    {
        for (int i = 0; i < (int)Token.NumTokens; i++)
        {
            Token token = (Token)i;
            Assert.Equal(TokenIsShiftOp(token), Token.IsShiftOp(token));
        }
    }









































    [Fact]
    public void RegExpScanning()
    {
        // RegExp token with added garbage at the end. The scanner should only
        // scan the RegExp until the terminating slash just before "flipperwald".
        TestScanRegExp("/b/flipperwald", "b");
        // Incomplete escape sequences doesn't hide the terminating slash.
        TestScanRegExp("/\\x/flipperwald", "\\x");
        TestScanRegExp("/\\u/flipperwald", "\\u");
        TestScanRegExp("/\\u1/flipperwald", "\\u1");
        TestScanRegExp("/\\u12/flipperwald", "\\u12");
        TestScanRegExp("/\\u123/flipperwald", "\\u123");
        TestScanRegExp("/\\c/flipperwald", "\\c");
        TestScanRegExp("/\\c//flipperwald", "\\c");
        // Slashes inside character classes are not terminating.
        TestScanRegExp("/[/]/flipperwald", "[/]");
        TestScanRegExp("/[\\s-/]/flipperwald", "[\\s-/]");
        // Incomplete escape sequences inside a character class doesn't hide
        // the end of the character class.
        TestScanRegExp("/[\\c/]/flipperwald", "[\\c/]");
        TestScanRegExp("/[\\c]/flipperwald", "[\\c]");
        TestScanRegExp("/[\\x]/flipperwald", "[\\x]");
        TestScanRegExp("/[\\x1]/flipperwald", "[\\x1]");
        TestScanRegExp("/[\\u]/flipperwald", "[\\u]");
        TestScanRegExp("/[\\u1]/flipperwald", "[\\u1]");
        TestScanRegExp("/[\\u12]/flipperwald", "[\\u12]");
        TestScanRegExp("/[\\u123]/flipperwald", "[\\u123]");
        // Escaped ']'s wont end the character class.
        TestScanRegExp("/[\\]/]/flipperwald", "[\\]/]");
        // Escaped slashes are not terminating.
        TestScanRegExp("/\\//flipperwald", "\\/");
        // Starting with '=' works too.
        TestScanRegExp("/=/", "=");
        TestScanRegExp("/=?/", "=?");
    }

    [Fact]
    public void ParseNumbers()
    {
        CheckParsesToNumber("1.");
        CheckParsesToNumber("1.34");
        CheckParsesToNumber("134");
        CheckParsesToNumber("134e44");
        CheckParsesToNumber("134.e44");
        CheckParsesToNumber("134.44e44");
        CheckParsesToNumber(".44");
        CheckParsesToNumber("-1.");
        CheckParsesToNumber("-1.0");
        CheckParsesToNumber("-1.34");
        CheckParsesToNumber("-134");
        CheckParsesToNumber("-134e44");
        CheckParsesToNumber("-134.e44");
        CheckParsesToNumber("-134.44e44");
        CheckParsesToNumber("-.44");
    }

    [Fact]
    public void NonOctalDecimalIntegerStrictError()
    {
        string[][] context_data = [["\"use strict\";", ""], [null, null]];
        string[] statement_data = ["09", "09.1_2", null];
        RunParserSyncTest(context_data, statement_data, kError, null, 0, null,
                                            0, null, 0, false, true);
    }

    [Fact]
    public void NumericSeparator()
    {
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = [
                "1_0_0_0", "1_0e+1", "1_0e+1_0", "0xF_F_FF", "0o7_7_7", "0b0_1_0_1_0",
                ".3_2_1", "0.0_2_1", "1_0.0_1", ".0_1_2", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NumericSeparatorErrors()
    {
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = [
                "1_0_0_0_", "1e_1", "1e+_1", "1_e+1", "1__0", "0x_1",
                "0x1__1", "0x1_", "0_x1", "0_x_1", "0b_0101", "0b11_",
                "0b1__1", "0_b1", "0_b_1", "0o777_", "0o_777", "0o7__77",
                "0.0_2_1_", "0.0__21", "0_.01", "0._01", null];
        RunParserSyncTest(context_data, statement_data, kError, null, 0, null,
                                            0, null, 0, false, true);
    }

    [Fact]
    public void NumericSeparatorImplicitOctalsErrors()
    {
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = ["00_122", "0_012", "07_7_7",
                                                                        "0_7_7_7", "0_777", "07_7_7_",
                                                                        "07__77", "0__777", null];
        RunParserSyncTest(context_data, statement_data, kError, null, 0, null,
                                            0, null, 0, false, true);
    }

    [Fact]
    public void NumericSeparatorNonOctalDecimalInteger()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["09.1_2", null];
        RunParserSyncTest(context_data, statement_data, kSuccess, null, 0, null,
                                            0, null, 0, false, true);
    }

    [Fact]
    public void NumericSeparatorNonOctalDecimalIntegerErrors()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["09_12", null];
        RunParserSyncTest(context_data, statement_data, kError, null, 0, null,
                                            0, null, 0, false, true);
    }

    [Fact]
    public void NumericSeparatorUnicodeEscapeSequencesErrors()
    {
        string[][] context_data = [
                ["", ""], ["'use strict'", ""], [null, null]];
        // https://github.com/tc39/proposal-numeric-separator/issues/25
        string[] statement_data = ["\\u{10_FFFF}", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void OptionalChaining()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] statement_data = ["a?.b", "a?.['b']", "a?.()", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void OptionalChainingTaggedError()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] statement_data = ["a?.b``", "a?.['b']``", "a?.()``", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void Nullish()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] statement_data = ["a ?? b", "a ?? b ?? c",
                                                                        "a ?? b ? c : da ?? b ?? c ? d : e",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NullishNotContained()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] statement_data = ["a || b ?? c", "a ?? b || c",
                                                                        "a && b ?? ca ?? b && c",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ErrorsEvalAndArguments()
    {
        // Tests that both preparsing and parsing produce the right kind of errors for
        // using "eval" and "arguments" as identifiers. Without the strict mode, it's
        // ok to use "eval" or "arguments" as identifiers. With the strict mode, it
        // isn't.
        string[][] context_data = [
                ["\"use strict\";", ""],
                ["var eval; function test_func() {\"use strict\"; ", "}"],
                [null, null]];
        string[] statement_data = ["var eval;",
                                                                        "var arguments",
                                                                        "var foo, eval;",
                                                                        "var foo, arguments;",
                                                                        "try { } catch (eval) { }",
                                                                        "try { } catch (arguments) { }",
                                                                        "function eval() { }",
                                                                        "function arguments() { }",
                                                                        "function foo(eval) { }",
                                                                        "function foo(arguments) { }",
                                                                        "function foo(bar, eval) { }",
                                                                        "function foo(bar, arguments) { }",
                                                                        "(eval) => { }",
                                                                        "(arguments) => { }",
                                                                        "(foo, eval) => { }",
                                                                        "(foo, arguments) => { }",
                                                                        "eval = 1;",
                                                                        "arguments = 1;",
                                                                        "var foo = eval = 1;",
                                                                        "var foo = arguments = 1;",
                                                                        "++eval;",
                                                                        "++arguments;",
                                                                        "eval++;",
                                                                        "arguments++;",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsEvalAndArgumentsSloppy()
    {
        // Tests that both preparsing and parsing accept "eval" and "arguments" as
        // identifiers when needed.
        string[][] context_data = [
                ["", ""], ["function test_func() {", "}"], [null, null]];
        string[] statement_data = ["var eval;",
                                                                        "var arguments",
                                                                        "var foo, eval;",
                                                                        "var foo, arguments;",
                                                                        "try { } catch (eval) { }",
                                                                        "try { } catch (arguments) { }",
                                                                        "function eval() { }",
                                                                        "function arguments() { }",
                                                                        "function foo(eval) { }",
                                                                        "function foo(arguments) { }",
                                                                        "function foo(bar, eval) { }",
                                                                        "function foo(bar, arguments) { }",
                                                                        "eval = 1;",
                                                                        "arguments = 1;",
                                                                        "var foo = eval = 1;",
                                                                        "var foo = arguments = 1;",
                                                                        "++eval;",
                                                                        "++arguments;",
                                                                        "eval++;",
                                                                        "arguments++;",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsEvalAndArgumentsStrict()
    {
        string[][] context_data = [
                ["\"use strict\";", ""],
                ["function test_func() { \"use strict\";", "}"],
                ["() => { \"use strict\"; ", "}"],
                [null, null]];
        string[] statement_data = ["eval;",
                                                                        "arguments;",
                                                                        "var foo = eval;",
                                                                        "var foo = arguments;",
                                                                        "var foo = { eval: 1 };",
                                                                        "var foo = { arguments: 1 };",
                                                                        "var foo = { }; foo.eval = {};",
                                                                        "var foo = { }; foo.arguments = {};",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsFutureStrictReservedWords()
    {
        string[][] context_data = [["", ""],
                                                                          ["function test_func() {", "}"],
                                                                          ["() => {", "}"],
                                                                          [null, null]];
        // clang-format off
        string[] statement_data = [
            "var let;", "var foo, let;", "try { } catch (let) { }", "function let() { }", "(function let() { })", "function foo(let) { }", "function foo(bar, let) { }", "let = 1;", "let += 1;", "var foo = let = 1;", "++let;", "let ++;", "var implements;", "var foo, implements;", "try { } catch (implements) { }", "function implements() { }", "(function implements() { })", "function foo(implements) { }", "function foo(bar, implements) { }", "implements = 1;", "implements += 1;", "var foo = implements = 1;", "++implements;", "implements ++;", "var interface;", "var foo, interface;", "try { } catch (interface) { }", "function interface() { }", "(function interface() { })", "function foo(interface) { }", "function foo(bar, interface) { }", "interface = 1;", "interface += 1;", "var foo = interface = 1;", "++interface;", "interface ++;", "var package;", "var foo, package;", "try { } catch (package) { }", "function package() { }", "(function package() { })", "function foo(package) { }", "function foo(bar, package) { }", "package = 1;", "package += 1;", "var foo = package = 1;", "++package;", "package ++;", "var private;", "var foo, private;", "try { } catch (private) { }", "function private() { }", "(function private() { })", "function foo(private) { }", "function foo(bar, private) { }", "private = 1;", "private += 1;", "var foo = private = 1;", "++private;", "private ++;", "var protected;", "var foo, protected;", "try { } catch (protected) { }", "function protected() { }", "(function protected() { })", "function foo(protected) { }", "function foo(bar, protected) { }", "protected = 1;", "protected += 1;", "var foo = protected = 1;", "++protected;", "protected ++;", "var public;", "var foo, public;", "try { } catch (public) { }", "function public() { }", "(function public() { })", "function foo(public) { }", "function foo(bar, public) { }", "public = 1;", "public += 1;", "var foo = public = 1;", "++public;", "public ++;", "var static;", "var foo, static;", "try { } catch (static) { }", "function static() { }", "(function static() { })", "function foo(static) { }", "function foo(bar, static) { }", "static = 1;", "static += 1;", "var foo = static = 1;", "++static;", "static ++;", "var yield;", "var foo, yield;", "try { } catch (yield) { }", "function yield() { }", "(function yield() { })", "function foo(yield) { }", "function foo(bar, yield) { }", "yield = 1;", "yield += 1;", "var foo = yield = 1;", "++yield;", "yield ++;",
            "let implements;", "for (let implements; false; ) {}", "for (let implements in {}) {}", "for (let implements of []) {}", "const implements = null;", "for (const implements = null; false; ) {}", "for (const implements in {}) {}", "for (const implements of []) {}", "let interface;", "for (let interface; false; ) {}", "for (let interface in {}) {}", "for (let interface of []) {}", "const interface = null;", "for (const interface = null; false; ) {}", "for (const interface in {}) {}", "for (const interface of []) {}", "let package;", "for (let package; false; ) {}", "for (let package in {}) {}", "for (let package of []) {}", "const package = null;", "for (const package = null; false; ) {}", "for (const package in {}) {}", "for (const package of []) {}", "let private;", "for (let private; false; ) {}", "for (let private in {}) {}", "for (let private of []) {}", "const private = null;", "for (const private = null; false; ) {}", "for (const private in {}) {}", "for (const private of []) {}", "let protected;", "for (let protected; false; ) {}", "for (let protected in {}) {}", "for (let protected of []) {}", "const protected = null;", "for (const protected = null; false; ) {}", "for (const protected in {}) {}", "for (const protected of []) {}", "let public;", "for (let public; false; ) {}", "for (let public in {}) {}", "for (let public of []) {}", "const public = null;", "for (const public = null; false; ) {}", "for (const public in {}) {}", "for (const public of []) {}", "let static;", "for (let static; false; ) {}", "for (let static in {}) {}", "for (let static of []) {}", "const static = null;", "for (const static = null; false; ) {}", "for (const static in {}) {}", "for (const static of []) {}", "let yield;", "for (let yield; false; ) {}", "for (let yield in {}) {}", "for (let yield of []) {}", "const yield = null;", "for (const yield = null; false; ) {}", "for (const yield in {}) {}", "for (const yield of []) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorAccessorAsIdentifier()
    {
        string[][] context_data = [["", ""], [null, null]];
        // clang-format off
        string[] statement_data = [
            "var accessor;", "var foo, accessor;", "try { } catch (accessor) { }", "function accessor() { }", "(function accessor() { })", "function foo(accessor) { }", "function foo(bar, accessor) { }", "accessor = 1;", "accessor += 1;", "var foo = accessor = 1;", "++accessor;", "accessor ++;",
            "let accessor;", "for (let accessor; false; ) {}", "for (let accessor in {}) {}", "for (let accessor of []) {}", "const accessor = null;", "for (const accessor = null; false; ) {}", "for (const accessor in {}) {}", "for (const accessor of []) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorAccessorAsIdentifierDecoratorsEnabled()
    {
        v8_flags.js_decorators = true;
        string[][] context_data = [["", ""], [null, null]];
        // clang-format off
        string[] statement_data = [
            "var accessor;", "var foo, accessor;", "try { } catch (accessor) { }", "function accessor() { }", "(function accessor() { })", "function foo(accessor) { }", "function foo(bar, accessor) { }", "accessor = 1;", "accessor += 1;", "var foo = accessor = 1;", "++accessor;", "accessor ++;",
            "let accessor;", "for (let accessor; false; ) {}", "for (let accessor in {}) {}", "for (let accessor of []) {}", "const accessor = null;", "for (const accessor = null; false; ) {}", "for (const accessor in {}) {}", "for (const accessor of []) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsReservedWords()
    {
        // Tests that both preparsing and parsing produce the right kind of errors for
        // using future reserved words as identifiers. These tests don't depend on the
        // strict mode.
        string[][] context_data = [
                ["", ""],
                ["\"use strict\";", ""],
                ["var eval; function test_func() {", "}"],
                ["var eval; function test_func() {\"use strict\"; ", "}"],
                ["var eval; () => {", "}"],
                ["var eval; () => {\"use strict\"; ", "}"],
                [null, null]];
        string[] statement_data = ["var super;",
                                                                        "var foo, super;",
                                                                        "try { } catch (super) { }",
                                                                        "function super() { }",
                                                                        "function foo(super) { }",
                                                                        "function foo(bar, super) { }",
                                                                        "(super) => { }",
                                                                        "(bar, super) => { }",
                                                                        "super = 1;",
                                                                        "var foo = super = 1;",
                                                                        "++super;",
                                                                        "super++;",
                                                                        "function foo super",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsLetSloppyAllModes()
    {
        // In sloppy mode, it's okay to use "let" as identifier.
        string[][] context_data = [["", ""],
                                                                          ["function f() {", "}"],
                                                                          ["(function f() {", "})"],
                                                                          [null, null]];
        string[] statement_data = [
                "var let;",
                "var foo, let;",
                "try { } catch (let) { }",
                "function let() { }",
                "(function let() { })",
                "function foo(let) { }",
                "function foo(bar, let) { }",
                "let = 1;",
                "var foo = let = 1;",
                "let * 2;",
                "++let;",
                "let++;",
                "let: 34",
                "function let(let) { let: let(let + let(0)); }",
                "({ let: 1 })",
                "({ get let() { 1 } })",
                "let(100)",
                "L: let\nx",
                "L: let\n{x}",
                null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsYieldSloppyAllModes()
    {
        // In sloppy mode, it's okay to use "yield" as identifier, *except* inside a
        // generator (see other test).
        string[][] context_data = [["", ""],
                                                                          ["function not_gen() {", "}"],
                                                                          ["(function not_gen() {", "})"],
                                                                          [null, null]];
        string[] statement_data = [
                "var yield;",
                "var foo, yield;",
                "try { } catch (yield) { }",
                "function yield() { }",
                "(function yield() { })",
                "function foo(yield) { }",
                "function foo(bar, yield) { }",
                "yield = 1;",
                "var foo = yield = 1;",
                "yield * 2;",
                "++yield;",
                "yield++;",
                "yield: 34",
                "function yield(yield) { yield: yield (yield + yield(0)); }",
                "({ yield: 1 })",
                "({ get yield() { 1 } })",
                "yield(100)",
                "yield[100]",
                null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsYieldSloppyGeneratorsEnabled()
    {
        // In sloppy mode, it's okay to use "yield" as identifier, *except* inside a
        // generator (see next test).
        string[][] context_data = [
                ["", ""],
                ["function not_gen() {", "}"],
                ["function * gen() { function not_gen() {", "} }"],
                ["(function not_gen() {", "})"],
                ["(function * gen() { (function not_gen() {", "}) })"],
                [null, null]];
        string[] statement_data = [
                "var yield;",
                "var foo, yield;",
                "try { } catch (yield) { }",
                "function yield() { }",
                "(function yield() { })",
                "function foo(yield) { }",
                "function foo(bar, yield) { }",
                "function * yield() { }",
                "yield = 1;",
                "var foo = yield = 1;",
                "yield * 2;",
                "++yield;",
                "yield++;",
                "yield: 34",
                "function yield(yield) { yield: yield (yield + yield(0)); }",
                "({ yield: 1 })",
                "({ get yield() { 1 } })",
                "yield(100)",
                "yield[100]",
                null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsYieldStrict()
    {
        string[][] context_data = [
                ["\"use strict\";", ""],
                ["\"use strict\"; function not_gen() {", "}"],
                ["function test_func() {\"use strict\"; ", "}"],
                ["\"use strict\"; function * gen() { function not_gen() {", "} }"],
                ["\"use strict\"; (function not_gen() {", "})"],
                ["\"use strict\"; (function * gen() { (function not_gen() {", "}) })"],
                ["() => {\"use strict\"; ", "}"],
                [null, null]];
        string[] statement_data = ["var yield;",
                                                                        "var foo, yield;",
                                                                        "try { } catch (yield) { }",
                                                                        "function yield() { }",
                                                                        "(function yield() { })",
                                                                        "function foo(yield) { }",
                                                                        "function foo(bar, yield) { }",
                                                                        "function * yield() { }",
                                                                        "(function * yield() { })",
                                                                        "yield = 1;",
                                                                        "var foo = yield = 1;",
                                                                        "++yield;",
                                                                        "yield++;",
                                                                        "yield: 34;",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ErrorsYieldSloppy()
    {
        string[][] context_data = [["", ""],
                                                                          ["function not_gen() {", "}"],
                                                                          ["(function not_gen() {", "})"],
                                                                          [null, null]];
        string[] statement_data = ["(function * yield() { })", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ErrorsYieldGenerator()
    {
        // clang-format off
        string[][] context_data = [
            [ "function * gen() {", "}" ],
            [ "\"use strict\"; function * gen() {", "}" ],
            [ null, null ]
        ];
        string[] statement_data = [
            // Invalid yield expressions inside generators.
            "var yield;",
            "var foo, yield;",
            "try { } catch (yield) { }",
            "function yield() { }",
            // The name of the NFE is bound in the generator, which does not permit
            // yield to be an identifier.
            "(function * yield() { })",
            // Yield isn't valid as a formal parameter for generators.
            "function * foo(yield) { }",
            "(function * foo(yield) { })",
            "yield = 1;",
            "var foo = yield = 1;",
            "++yield;",
            "yield++;",
            "yield *",
            "(yield *)",
            // Yield binds very loosely, so this parses as "yield (3 + yield 4)", which
            // is invalid.
            "yield 3 + yield 4;",
            "yield: 34",
            "yield ? 1 : 2",
            // Parses as yield (/ yield): invalid.
            "yield / yield",
            "+ yield",
            "+ yield 3",
            // Invalid (no newline allowed between yield and *).
            "yield\n*3",
            // Invalid (we see a newline, so we parse {yield:42} as a statement, not an
            // object literal, and yield is not a valid label).
            "yield\n{yield: 42}",
            "yield /* comment */\n {yield: 42}",
            "yield //comment\n {yield: 42}",
            // Destructuring binding and assignment are both disallowed
            "var [yield] = [42];",
            "var {foo: yield} = {a: 42};",
            "[yield] = [42];",
            "({a: yield} = {a: 42});",
            // Also disallow full yield expressions on LHS
            "var [yield 24] = [42];",
            "var {foo: yield 24} = {a: 42};",
            "[yield 24] = [42];",
            "({a: yield 24} = {a: 42});",
            "for (yield 'x' in {});",
            "for (yield 'x' of {});",
            "for (yield 'x' in {} in {});",
            "for (yield 'x' in {} of {});",
            "class C extends yield { }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ErrorsNameOfStrictFunction()
    {
        // Tests that illegal tokens as names of a strict function produce the correct
        // errors.
        string[][] context_data = [["function ", ""],
                                                                          ["\"use strict\"; function", ""],
                                                                          ["function * ", ""],
                                                                          ["\"use strict\"; function * ", ""],
                                                                          [null, null]];
        string[] statement_data = [
                "eval() {\"use strict\";}", "arguments() {\"use strict\";}",
                "interface() {\"use strict\";}", "yield() {\"use strict\";}",
                // Future reserved words are always illegal
                "super() { }", "super() {\"use strict\";}", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsNameOfStrictFunction()
    {
        string[][] context_data = [["function ", ""], [null, null]];
        string[] statement_data = ["eval() { }", "arguments() { }",
                                                                        "interface() { }", "yield() { }", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsNameOfStrictGenerator()
    {
        string[][] context_data = [["function * ", ""], [null, null]];
        string[] statement_data = ["eval() { }", "arguments() { }",
                                                                        "interface() { }", "yield() { }", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsIllegalWordsAsLabelsSloppy()
    {
        // Using future reserved words as labels is always an error.
        string[][] context_data = [["", ""],
                                                                          ["function test_func() {", "}"],
                                                                          ["() => {", "}"],
                                                                          [null, null]];
        string[] statement_data = ["super: while(true) { break super; }",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ErrorsIllegalWordsAsLabelsStrict()
    {
        // Tests that illegal tokens as labels produce the correct errors.
        string[][] context_data = [
                ["\"use strict\";", ""],
                ["function test_func() {\"use strict\"; ", "}"],
                ["() => {\"use strict\"; ", "}"],
                [null, null]];
        string[] statement_data = [
                "super: while(true) { break super; }",
                "let: while (true) { break let; }", "implements: while (true) { break implements; }", "interface: while (true) { break interface; }", "package: while (true) { break package; }", "private: while (true) { break private; }", "protected: while (true) { break protected; }", "public: while (true) { break public; }", "static: while (true) { break static; }", "yield: while (true) { break yield; }", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsIllegalWordsAsLabels()
    {
        // Using eval and arguments as labels is legal even in strict mode.
        string[][] context_data = [
                ["", ""],
                ["function test_func() {", "}"],
                ["() => {", "}"],
                ["\"use strict\";", ""],
                ["\"use strict\"; function test_func() {", "}"],
                ["\"use strict\"; () => {", "}"],
                [null, null]];
        string[] statement_data = ["mylabel: while(true) { break mylabel; }",
                                                                        "eval: while(true) { break eval; }",
                                                                        "arguments: while(true) { break arguments; }",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsParenthesizedLabels()
    {
        // Parenthesized identifiers shouldn't be recognized as labels.
        string[][] context_data = [["", ""],
                                                                          ["function test_func() {", "}"],
                                                                          ["() => {", "}"],
                                                                          [null, null]];
        string[] statement_data = ["(mylabel): while(true) { break mylabel; }",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsParenthesizedDirectivePrologue()
    {
        // Parenthesized directive prologue shouldn't be recognized.
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["(\"use strict\"); var eval;", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsNotAnIdentifierName()
    {
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = ["var foo = {}; foo.{;",
                                                                        "var foo = {}; foo.};",
                                                                        "var foo = {}; foo.=;",
                                                                        "var foo = {}; foo.888;",
                                                                        "var foo = {}; foo.-;",
                                                                        "var foo = {}; foo.--;",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsIdentifierNames()
    {
        // Keywords etc. are valid as property names.
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = ["var foo = {}; foo.if;",
                                                                        "var foo = {}; foo.yield;",
                                                                        "var foo = {}; foo.super;",
                                                                        "var foo = {}; foo.interface;",
                                                                        "var foo = {}; foo.eval;",
                                                                        "var foo = {}; foo.arguments;",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void FunctionDeclaresItselfStrict()
    {
        // Tests that we produce the right kinds of errors when a function declares
        // itself strict (we cannot produce there errors as soon as we see the
        // offending identifiers, because we don't know at that point whether the
        // function is strict or not).
        string[][] context_data = [["function eval() {", "}"],
                                                                          ["function arguments() {", "}"],
                                                                          ["function yield() {", "}"],
                                                                          ["function interface() {", "}"],
                                                                          ["function foo(eval) {", "}"],
                                                                          ["function foo(arguments) {", "}"],
                                                                          ["function foo(yield) {", "}"],
                                                                          ["function foo(interface) {", "}"],
                                                                          ["function foo(bar, eval) {", "}"],
                                                                          ["function foo(bar, arguments) {", "}"],
                                                                          ["function foo(bar, yield) {", "}"],
                                                                          ["function foo(bar, interface) {", "}"],
                                                                          ["function foo(bar, bar) {", "}"],
                                                                          [null, null]];
        string[] strict_statement_data = ["\"use strict\";", null];
        string[] non_strict_statement_data = [";", null];
        RunParserSyncTest(context_data, strict_statement_data, kError);
        RunParserSyncTest(context_data, non_strict_statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsTryWithoutCatchOrFinally()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["try { }", "try { } foo();",
                                                                        "try { } catch (e) foo();",
                                                                        "try { } finally foo();", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsTryCatchFinally()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["try { } catch (e) { }",
                                                                        "try { } catch (e) { } finally { }",
                                                                        "try { } finally { }", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void OptionalCatchBinding()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["try {", "} catch (e) { }"],
            ["try {} catch (e) {", "}"],
            ["try {", "} catch ({e}) { }"],
            ["try {} catch ({e}) {", "}"],
            ["function f() {", "}"],
            [ null, null ]
        ];
        string[] statement_data = [
            "try { } catch { }",
            "try { } catch { } finally { }",
            "try { let e; } catch { let e; }",
            "try { let e; } catch { let e; } finally { let e; }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsRegexpLiteral()
    {
        string[][] context_data = [["var r = ", ""], [null, null]];
        string[] statement_data = ["/unterminated", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsRegexpLiteral()
    {
        string[][] context_data = [["var r = ", ""], [null, null]];
        string[] statement_data = ["/foo/", "/foo/g", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void NoErrorsNewExpression()
    {
        string[][] context_data = [
                ["", ""], ["var f =", ""], [null, null]];
        string[] statement_data = [
                "new foo", "new foo();", "new foo(1);", "new foo(1, 2);",
                // The first () will be processed as a part of the NewExpression and the
                // second () will be processed as part of LeftHandSideExpression.
                "new foo()();",
                // The first () will be processed as a part of the inner NewExpression and
                // the second () will be processed as a part of the outer NewExpression.
                "new new foo()();", "new foo.bar;", "new foo.bar();", "new foo.bar.baz;",
                "new foo.bar().baz;", "new foo[bar];", "new foo[bar]();",
                "new foo[bar][baz];", "new foo[bar]()[baz];",
                "new foo[bar].baz(baz)()[bar].baz;",
                "new \"foo\"", // Runtime error
                "new 1", // Runtime error
                // This even runs:
                "(new new Function(\"this.x = 1\")).x;",
                "new new Test_Two(String, 2).v(0123).length;", null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsNewExpression()
    {
        string[][] context_data = [
                ["", ""], ["var f =", ""], [null, null]];
        string[] statement_data = ["new foo bar", "new ) foo", "new ++foo",
                                                                        "new foo ++", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void StrictObjectLiteralChecking()
    {
        string[][] context_data = [["\"use strict\"; var myobject = {", "};"],
                                                                          ["\"use strict\"; var myobject = {", ",};"],
                                                                          ["var myobject = {", "};"],
                                                                          ["var myobject = {", ",};"],
                                                                          [null, null]];
        // These are only errors in strict mode.
        string[] statement_data = [
                "foo: 1, foo: 2", "\"foo\": 1, \"foo\": 2", "foo: 1, \"foo\": 2",
                "1: 1, 1: 2", "1: 1, \"1\": 2",
                "get: 1, get: 2", // Not a getter for real, just a property called get.
                "set: 1, set: 2", // Not a setter for real, just a property called set.
                null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsObjectLiteralChecking()
    {
        // clang-format off
        string[][] context_data = [
            ["\"use strict\"; var myobject = {", "};"],
            ["var myobject = {", "};"],
            [ null, null ]
        ];
        string[] statement_data = [
            ",",
            // Wrong number of parameters
            "get bar(x) {}",
            "get bar(x, y) {}",
            "set bar() {}",
            "set bar(x, y) {}",
            // Parsing FunctionLiteral for getter or setter fails
            "get foo( +",
            "get foo() \"error\"",
            // Various forbidden forms
            "static x: 0",
            "static x(){}",
            "static async x(){}",
            "static get x(){}",
            "static get x : 0",
            "static x",
            "static 0",
            "*x: 0",
            "*x",
            "*get x(){}",
            "*set x(y){}",
            "get *x(){}",
            "set *x(y){}",
            "get x*(){}",
            "set x*(y){}",
            "x = 0",
            "* *x(){}",
            "x*(){}",
            "static async x(){}",
            "static async x : 0",
            "static async get x : 0",
            "async static x(){}",
            "*async x(){}",
            "async x*(){}",
            "async x : 0",
            "async 0 : 0",
            "async get x(){}",
            "async get *x(){}",
            "async set x(y){}",
            "async get : 0",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsObjectLiteralChecking()
    {
        // clang-format off
        string[][] context_data = [
            ["var myobject = {", "};"],
            ["var myobject = {", ",};"],
            ["\"use strict\"; var myobject = {", "};"],
            ["\"use strict\"; var myobject = {", ",};"],
            [ null, null ]
        ];
        string[] statement_data = [
            "foo: 1, get foo() {}",
            "foo: 1, set foo(v) {}",
            "\"foo\": 1, get \"foo\"() {}",
            "\"foo\": 1, set \"foo\"(v) {}",
            "1: 1, get 1() {}",
            "1: 1, set 1(v) {}",
            "get foo() {}, get foo() {}",
            "set foo(_) {}, set foo(v) {}",
            "foo: 1, get \"foo\"() {}",
            "foo: 1, set \"foo\"(v) {}",
            "\"foo\": 1, get foo() {}",
            "\"foo\": 1, set foo(v) {}",
            "1: 1, get \"1\"() {}",
            "1: 1, set \"1\"(v) {}",
            "\"1\": 1, get 1() {}",
            "\"1\": 1, set 1(v) {}",
            "foo: 1, bar: 2",
            "\"foo\": 1, \"bar\": 2",
            "1: 1, 2: 2",
            // Syntax: IdentifierName ':' AssignmentExpression
            "foo: bar = 5 + baz",
            // Syntax: 'get' PropertyName '(' ')' '{' FunctionBody '}'
            "get foo() {}",
            "get \"foo\"() {}",
            "get 1() {}",
            // Syntax: 'set' PropertyName '(' PropertySetParameterList ')'
            //     '{' FunctionBody '}'
            "set foo(v) {}",
            "set \"foo\"(v) {}",
            "set 1(v) {}",
            // Non-colliding getters and setters -> no errors
            "foo: 1, get bar() {}",
            "foo: 1, set bar(v) {}",
            "\"foo\": 1, get \"bar\"() {}",
            "\"foo\": 1, set \"bar\"(v) {}",
            "1: 1, get 2() {}",
            "1: 1, set 2(v) {}",
            "get: 1, get foo() {}",
            "set: 1, set foo(_) {}",
            // Potentially confusing cases
            "get(){}",
            "set(){}",
            "static(){}",
            "async(){}",
            "*get() {}",
            "*set() {}",
            "*static() {}",
            "*async(){}",
            "get : 0",
            "set : 0",
            "static : 0",
            "async : 0",
            // Keywords, future reserved and strict future reserved are also allowed as
            // property names.
            "if: 4",
            "interface: 5",
            "super: 6",
            "eval: 7",
            "arguments: 8",
            "async x(){}",
            "async 0(){}",
            "async get(){}",
            "async set(){}",
            "async static(){}",
            "async async(){}",
            "async : 0",
            "async(){}",
            "*async(){}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void StrictDelete()
    {
        // "delete <Identifier>" is not allowed in strict mode.
        string[][] strict_context_data = [["\"use strict\"; ", ""],
                                                                                        [null, null]];
        string[][] sloppy_context_data = [["", ""], [null, null]];
        // These are errors in the strict mode.
        string[] sloppy_statement_data = ["delete foo;", "delete foo + 1;",
                                                                                      "delete (foo);", "delete eval;",
                                                                                      "delete interface;", null];
        // These are always OK
        string[] good_statement_data = ["delete this;",
                                                                                  "delete 1;",
                                                                                  "delete 1 + 2;",
                                                                                  "delete foo();",
                                                                                  "delete foo.bar;",
                                                                                  "delete foo[bar];",
                                                                                  "delete foo--;",
                                                                                  "delete --foo;",
                                                                                  "delete new foo();",
                                                                                  "delete new foo(bar);",
                                                                                  null];
        // These are always errors
        string[] bad_statement_data = ["delete if;", null];
        RunParserSyncTest(strict_context_data, sloppy_statement_data, kError);
        RunParserSyncTest(sloppy_context_data, sloppy_statement_data, kSuccess);
        RunParserSyncTest(strict_context_data, good_statement_data, kSuccess);
        RunParserSyncTest(sloppy_context_data, good_statement_data, kSuccess);
        RunParserSyncTest(strict_context_data, bad_statement_data, kError);
        RunParserSyncTest(sloppy_context_data, bad_statement_data, kError);
    }

    [Fact]
    public void NoErrorsDeclsInCase()
    {
        string[][] context_data = [
                ["'use strict'; switch(x) { case 1:", "}"],
                ["function foo() {'use strict'; switch(x) { case 1:", "}}"],
                ["'use strict'; switch(x) { case 1: case 2:", "}"],
                ["function foo() {'use strict'; switch(x) { case 1: case 2:", "}}"],
                ["'use strict'; switch(x) { default:", "}"],
                ["function foo() {'use strict'; switch(x) { default:", "}}"],
                ["'use strict'; switch(x) { case 1: default:", "}"],
                ["function foo() {'use strict'; switch(x) { case 1: default:", "}}"],
                [null, null]];
        string[] statement_data = ["function f() { }",
                                                                        "class C { }",
                                                                        "class C extends Q {}",
                                                                        "function f() { } class C {}",
                                                                        "function f() { }; class C {}",
                                                                        "class C {}; function f() {}",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void InvalidLeftHandSide()
    {
        string[][] assignment_context_data = [
                ["", " = 1;"], ["\"use strict\"; ", " = 1;"], [null, null]];
        string[][] prefix_context_data = [
                ["++", ";"],
                ["\"use strict\"; ++", ";"],
                [null, null],
        ];
        string[][] postfix_context_data = [
                ["", "++;"], ["\"use strict\"; ", "++;"], [null, null]];
        // Good left hand sides for assigment or prefix / postfix operations.
        string[] good_statement_data = ["foo",
                                                                                  "foo.bar",
                                                                                  "foo[bar]",
                                                                                  "foo()[bar]",
                                                                                  "foo().bar",
                                                                                  "this.foo",
                                                                                  "this[foo]",
                                                                                  "new foo()[bar]",
                                                                                  "new foo().bar",
                                                                                  "foo()",
                                                                                  "foo(bar)",
                                                                                  "foo[bar]()",
                                                                                  "foo.bar()",
                                                                                  "this()",
                                                                                  "this.foo()",
                                                                                  "this[foo].bar()",
                                                                                  "this.foo[foo].bar(this)(bar)[foo]()",
                                                                                  null];
        // Bad left hand sides for assigment or prefix / postfix operations.
        string[] bad_statement_data_common = [
                "2",
                "new foo",
                "new foo()",
                "null",
                "if", // Unexpected token
                "{x: 1}", // Unexpected token
                "this",
                "\"bar\"",
                "(foo + bar)",
                "new new foo()[bar]", // means: new (new foo()[bar])
                "new new foo().bar", // means: new (new foo()[bar])
                null];
        // These are not okay for assignment, but okay for prefix / postix.
        string[] bad_statement_data_for_assignment = ["++foo", "foo++",
                                                                                                              "foo + bar", null];
        RunParserSyncTest(assignment_context_data, good_statement_data, kSuccess);
        RunParserSyncTest(assignment_context_data, bad_statement_data_common, kError);
        RunParserSyncTest(assignment_context_data, bad_statement_data_for_assignment,
                                            kError);
        RunParserSyncTest(prefix_context_data, good_statement_data, kSuccess);
        RunParserSyncTest(prefix_context_data, bad_statement_data_common, kError);
        RunParserSyncTest(postfix_context_data, good_statement_data, kSuccess);
        RunParserSyncTest(postfix_context_data, bad_statement_data_common, kError);
    }

    [Fact]
    public void LineOrParagraphSeparatorAsLineTerminator()
    {
        // Tests that both preparsing and parsing accept U+2028 LINE SEPARATOR and
        // U+2029 PARAGRAPH SEPARATOR as LineTerminator symbols outside of string
        // literals.
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["1\u20282", // 1<U+2028>2
                                                                        "1\u20292", // 1<U+2029>2
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void LineOrParagraphSeparatorInStringLiteral()
    {
        // Tests that both preparsing and parsing don't treat U+2028 LINE SEPARATOR
        // and U+2029 PARAGRAPH SEPARATOR as line terminators within string literals.
        // https://github.com/tc39/proposal-json-superset
        string[][] context_data = [
                ["\"", "\""], ["'", "'"], [null, null]];
        string[] statement_data = ["1\u20282", // 1<U+2028>2
                                                                        "1\u20292", // 1<U+2029>2
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ErrorsArrowFormalParameters()
    {
        string[][] context_data = [["()", "=>{}"],
                                                                          ["()", "=>{};"],
                                                                          ["var x = ()", "=>{}"],
                                                                          ["var x = ()", "=>{};"],
                                                                          ["a", "=>{}"],
                                                                          ["a", "=>{};"],
                                                                          ["var x = a", "=>{}"],
                                                                          ["var x = a", "=>{};"],
                                                                          ["(a)", "=>{}"],
                                                                          ["(a)", "=>{};"],
                                                                          ["var x = (a)", "=>{}"],
                                                                          ["var x = (a)", "=>{};"],
                                                                          ["(...a)", "=>{}"],
                                                                          ["(...a)", "=>{};"],
                                                                          ["var x = (...a)", "=>{}"],
                                                                          ["var x = (...a)", "=>{};"],
                                                                          ["(a,b)", "=>{}"],
                                                                          ["(a,b)", "=>{};"],
                                                                          ["var x = (a,b)", "=>{}"],
                                                                          ["var x = (a,b)", "=>{};"],
                                                                          ["(a,...b)", "=>{}"],
                                                                          ["(a,...b)", "=>{};"],
                                                                          ["var x = (a,...b)", "=>{}"],
                                                                          ["var x = (a,...b)", "=>{};"],
                                                                          [null, null]];
        string[] assignment_expression_suffix_data = [
                "?c:d=>{}",
                "=c=>{}",
                "()",
                "(c)",
                "[1]",
                "[c]",
                ".c",
                "-c",
                "+c",
                "c++",
                "`c`",
                "`${c}`",
                "`template-head${c}`",
                "`${c}template-tail`",
                "`template-head${c}template-tail`",
                "`${c}template-tail`",
                null];
        RunParserSyncTest(context_data, assignment_expression_suffix_data, kError);
    }

    [Fact]
    public void ErrorsArrowFunctions()
    {
        // Tests that parser and preparser generate the same kind of errors
        // on invalid arrow function syntax.
        // clang-format off
        string[][] context_data = [
            ["", ";"],
            ["v = ", ";"],
            ["bar ? (", ") : baz;"],
            ["bar ? baz : (", ");"],
            ["bar[", "];"],
            ["bar, ", ";"],
            ["", ", bar;"],
            [null, null]
        ];
        string[] statement_data = [
            "=> 0",
            "=>",
            "() =>",
            "=> {}",
            ") => {}",
            ", => {}",
            "(,) => {}",
            "return => {}",
            "() => {'value': 42}",
            // Check that the early return introduced in ParsePrimaryExpression
            // does not accept stray closing parentheses.
            ")",
            ") => 0",
            "foo[()]",
            "()",
            // Parameter lists with extra parens should be recognized as errors.
            "(()) => 0",
            "((x)) => 0",
            "((x, y)) => 0",
            "(x, (y)) => 0",
            "((x, y, z)) => 0",
            "(x, (y, z)) => 0",
            "((x, y), z) => 0",
            // Arrow function formal parameters are parsed as StrictFormalParameters,
            // which confusingly only implies that there are no duplicates.  Words
            // reserved in strict mode, and eval or arguments, are indeed valid in
            // sloppy mode.
            "eval => { 'use strict'; 0 }",
            "arguments => { 'use strict'; 0 }",
            "yield => { 'use strict'; 0 }",
            "interface => { 'use strict'; 0 }",
            "(eval) => { 'use strict'; 0 }",
            "(arguments) => { 'use strict'; 0 }",
            "(yield) => { 'use strict'; 0 }",
            "(interface) => { 'use strict'; 0 }",
            "(eval, bar) => { 'use strict'; 0 }",
            "(bar, eval) => { 'use strict'; 0 }",
            "(bar, arguments) => { 'use strict'; 0 }",
            "(bar, yield) => { 'use strict'; 0 }",
            "(bar, interface) => { 'use strict'; 0 }",
            // TODO(aperez): Detecting duplicates does not work in PreParser.
            // "(bar, bar) => {}",
            // The parameter list is parsed as an expression, but only
            // a comma-separated list of identifier is valid.
            "32 => {}",
            "(32) => {}",
            "(a, 32) => {}",
            "if => {}",
            "(if) => {}",
            "(a, if) => {}",
            "a + b => {}",
            "(a + b) => {}",
            "(a + b, c) => {}",
            "(a, b - c) => {}",
            "\"a\" => {}",
            "(\"a\") => {}",
            "(\"a\", b) => {}",
            "(a, \"b\") => {}",
            "-a => {}",
            "(-a) => {}",
            "(-a, b) => {}",
            "(a, -b) => {}",
            "{} => {}",
            "a++ => {}",
            "(a++) => {}",
            "(a++, b) => {}",
            "(a, b++) => {}",
            "[] => {}",
            "(foo ? bar : baz) => {}",
            "(a, foo ? bar : baz) => {}",
            "(foo ? bar : baz, a) => {}",
            "(a.b, c) => {}",
            "(c, a.b) => {}",
            "(a['b'], c) => {}",
            "(c, a['b']) => {}",
            "(...a = b) => b",
            // crbug.com/582626
            "(...rest - a) => b",
            "(a, ...b - 10) => b",
            null
        ];
        // clang-format on
        // The test is quite slow, so run it with a reduced set of flags.
        ParserFlag[] flags = [kAllowLazy];
        RunParserSyncTest(context_data, statement_data, kError, flags,
                                            flags.Length);
        // In a context where a concise arrow body is parsed with [~In] variant,
        // ensure that an error is reported in both full parser and preparser.
        string[][] loop_context_data = [["for (", "; 0;);"],
                                                                                    [null, null]];
        string[] loop_expr_data = ["f => 'key' in {}", null];
        RunParserSyncTest(loop_context_data, loop_expr_data, kError, flags,
                                            flags.Length);
    }

    [Fact]
    public void NoErrorsArrowFunctions()
    {
        // Tests that parser and preparser accept valid arrow functions syntax.
        // clang-format off
        string[][] context_data = [
            ["", ";"],
            ["bar ? (", ") : baz;"],
            ["bar ? baz : (", ");"],
            ["bar, ", ";"],
            ["", ", bar;"],
            [null, null]
        ];
        string[] statement_data = [
            "() => {}",
            "() => { return 42 }",
            "x => { return x; }",
            "(x) => { return x; }",
            "(x, y) => { return x + y; }",
            "(x, y, z) => { return x + y + z; }",
            "(x, y) => { x.a = y; }",
            "() => 42",
            "x => x",
            "x => x * x",
            "(x) => x",
            "(x) => x * x",
            "(x, y) => x + y",
            "(x, y, z) => x, y, z",
            "(x, y) => x.a = y",
            "() => ({'value': 42})",
            "x => y => x + y",
            "(x, y) => (u, v) => x*u + y*v",
            "(x, y) => z => z * (x + y)",
            "x => (y, z) => z * (x + y)",
            // Those are comma-separated expressions, with arrow functions as items.
            // They stress the code for validating arrow function parameter lists.
            "a, b => 0",
            "a, b, (c, d) => 0",
            "(a, b, (c, d) => 0)",
            "(a, b) => 0, (c, d) => 1",
            "(a, b => {}, a => a + 1)",
            "((a, b) => {}, (a => a + 1))",
            "(a, (a, (b, c) => 0))",
            // Arrow has more precedence, this is the same as: foo ? bar : (baz = {})
            "foo ? bar : baz => {}",
            // Arrows with non-simple parameters.
            "({}) => {}",
            "(a, {}) => {}",
            "({}, a) => {}",
            "([]) => {}",
            "(a, []) => {}",
            "([], a) => {}",
            "(a = b) => {}",
            "(a = b, c) => {}",
            "(a, b = c) => {}",
            "({a}) => {}",
            "(x = 9) => {}",
            "(x, y = 9) => {}",
            "(x = 9, y) => {}",
            "(x, y = 9, z) => {}",
            "(x, y = 9, z = 8) => {}",
            "(...a) => {}",
            "(x, ...a) => {}",
            "(x = 9, ...a) => {}",
            "(x, y = 9, ...a) => {}",
            "(x, y = 9, {b}, z = 8, ...a) => {}",
            "({a} = {}) => {}",
            "([x] = []) => {}",
            "({a = 42}) => {}",
            "([x = 0]) => {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kSuccess);
        ParserFlag[] flags = [kAllowLazy];
        // In a context where a concise arrow body is parsed with [~In] variant,
        // ensure that nested expressions can still use the 'in' operator,
        string[][] loop_context_data = [["for (", "; 0;);"],
                                                                                    [null, null]];
        string[] loop_expr_data = ["f => ('key' in {})", null];
        RunParserSyncTest(loop_context_data, loop_expr_data, kSuccess, flags,
                                            flags.Length);
    }

    [Fact]
    public void ArrowFunctionsSloppyParameterNames()
    {
        string[][] strict_context_data = [["'use strict'; ", ";"],
                                                                                        ["'use strict'; bar ? (", ") : baz;"],
                                                                                        ["'use strict'; bar ? baz : (", ");"],
                                                                                        ["'use strict'; bar, ", ";"],
                                                                                        ["'use strict'; ", ", bar;"],
                                                                                        [null, null]];
        string[][] sloppy_context_data = [
                ["", ";"], ["bar ? (", ") : baz;"], ["bar ? baz : (", ");"],
                ["bar, ", ";"], ["", ", bar;"], [null, null]];
        string[] statement_data = ["eval => {}",
                                                                        "arguments => {}",
                                                                        "yield => {}",
                                                                        "interface => {}",
                                                                        "(eval) => {}",
                                                                        "(arguments) => {}",
                                                                        "(yield) => {}",
                                                                        "(interface) => {}",
                                                                        "(eval, bar) => {}",
                                                                        "(bar, eval) => {}",
                                                                        "(bar, arguments) => {}",
                                                                        "(bar, yield) => {}",
                                                                        "(bar, interface) => {}",
                                                                        "(interface, eval) => {}",
                                                                        "(interface, arguments) => {}",
                                                                        "(eval, interface) => {}",
                                                                        "(arguments, interface) => {}",
                                                                        null];
        RunParserSyncTest(strict_context_data, statement_data, kError);
        RunParserSyncTest(sloppy_context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ArrowFunctionsYieldParameterNameInGenerator()
    {
        string[][] sloppy_function_context_data = [
                ["(function f() { (", "); });"], [null, null]];
        string[][] strict_function_context_data = [
                ["(function f() {'use strict'; (", "); });"], [null, null]];
        string[][] generator_context_data = [
                ["(function *g() {'use strict'; (", "); });"],
                ["(function *g() { (", "); });"],
                [null, null]];
        string[] arrow_data = [
                "yield => {}", "(yield) => {}", "(a, yield) => {}",
                "(yield, a) => {}", "(yield, ...a) => {}", "(a, ...yield) => {}",
                "({yield}) => {}", "([yield]) => {}", null];
        RunParserSyncTest(sloppy_function_context_data, arrow_data, kSuccess);
        RunParserSyncTest(strict_function_context_data, arrow_data, kError);
        RunParserSyncTest(generator_context_data, arrow_data, kError);
    }

    [Fact]
    public void SuperNoErrors()
    {
        // Tests that parser and preparser accept 'super' keyword in right places.
        string[][] context_data = [["class C { m() { ", "; } }"],
                                                                          ["class C { m() { k = ", "; } }"],
                                                                          ["class C { m() { foo(", "); } }"],
                                                                          ["class C { m() { () => ", "; } }"],
                                                                          [null, null]];
        string[] statement_data = ["super.x", "super[27]",
                                                                        "new super.x", "new super.x()",
                                                                        "new super[27]", "new super[27]()",
                                                                        "z.super", // Ok, property lookup.
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void SuperErrors()
    {
        string[][] context_data = [["class C { m() { ", "; } }"],
                                                                          ["class C { m() { k = ", "; } }"],
                                                                          ["class C { m() { foo(", "); } }"],
                                                                          ["class C { m() { () => ", "; } }"],
                                                                          [null, null]];
        string[] expression_data = ["super",
                                                                          "super = x",
                                                                          "y = super",
                                                                          "f(super)",
                                                                          "new super",
                                                                          "new super()",
                                                                          "new super(12, 45)",
                                                                          "new new super",
                                                                          "new new super()",
                                                                          "new new super()()",
                                                                          null];
        RunParserSyncTest(context_data, expression_data, kError);
    }

    [Fact]
    public void ImportExpressionSuccess()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            [null, null]
        ];
        string[] data = [
            "import(1)",
            "import(y=x)",
            "f(...[import(y=x)])",
            "x = {[import(y=x)]: 1}",
            "var {[import(y=x)]: x} = {}",
            "({[import(y=x)]: x} = {})",
            "async () => { await import(x) }",
            "() => { import(x) }",
            "(import(y=x))",
            "{import(y=x)}",
            "import(import(x))",
            "x = import(x)",
            "var x = import(x)",
            "let x = import(x)",
            "for(x of import(x)) {}",
            "import(x).then()",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ImportExpressionWithOptionsSuccess()
    {
        v8_flags.harmony_import_attributes = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            [null, null]
        ];
        string[] data = [
            "import(x,)",
            "import(x,1)",
            "import(x,y)",
            "import(x,y,)",
            "import(x, { 'a': 'b' })",
            "import(x, { a: 'b', 'c': 'd' },)",
            "import(x, { 'a': { b: 'c' }, 'd': 'e' },)",
            "import(x,import(y))",
            "import(x,y=z)",
            "import(x,[y, z])",
            "import(x,undefined)",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ImportExpressionErrors()
    {
        {
            // clang-format off
            string[][] context_data = [
                ["", ""],
                ["var ", ""],
                ["let ", ""],
                ["new ", ""],
                [null, null]
            ];
            string[] data = [
                "import(",
                "import)",
                "import()",
                "import('x",
                "import('x']",
                "import['x')",
                "import = x",
                "import[",
                "import[]",
                "import]",
                "import[x]",
                "import{",
                "import{x",
                "import{x}",
                "import(x, y, z)",
                "import(...y)",
                "import(,)",
                "import(,y)",
                "import(;)",
                "[import]",
                "{import}",
                "import+",
                "import = 1",
                "import.wat",
                "new import(x)",
                "new import(x).prop",
                "new import(x).prop(r => r())",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            // We ignore test error messages because the error message from
            // the parser/preparser is different for the same data depending
            // on the context.  For example, a top level "import{" is parsed
            // as an import declaration. The parser parses the import token
            // correctly and then shows an "Unexpected end of input" error
            // message because of the '{'. The preparser shows an "Unexpected
            // token '{'" because it's not a valid token in a CallExpression.
            RunModuleParserSyncTest(context_data, data, kError, null, 0, null, 0,
                                                            null, 0, true, true);
        }
        {
            // clang-format off
            string[][] context_data = [
                ["var ", ""],
                ["let ", ""],
                [null, null]
            ];
            string[] data = [
                "import('x')",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            RunModuleParserSyncTest(context_data, data, kError);
        }
        // Import statements as arrow function params and destructuring targets.
        {
            // clang-format off
            string[][] context_data = [
                ["(", ") => {}"],
                ["(a, ", ") => {}"],
                ["(1, ", ") => {}"],
                ["let f = ", " => {}"],
                ["[", "] = [1];"],
                ["{", "} = {'a': 1};"],
                [null, null]
            ];
            string[] data = [
                "import(foo)",
                "import(1)",
                "import(y=x)",
                "import(import(x))",
                "import(x).then()",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            RunModuleParserSyncTest(context_data, data, kError);
        }
    }

    [Fact]
    public void ImportExpressionWithOptionsErrors()
    {
        {
            v8_flags.harmony_import_attributes = true;
            // clang-format off
            string[][] context_data = [
                ["", ""],
                ["var ", ""],
                ["let ", ""],
                ["new ", ""],
                [null, null]
            ];
            string[] data = [
                "import(x,,)",
                "import(x))",
                "import(x,))",
                "import(x,())",
                "import(x,y,,)",
                "import(x,y,z)",
                "import(x,y",
                "import(x,y,",
                "import(x,y(",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            RunModuleParserSyncTest(context_data, data, kError);
        }
        {
            // clang-format off
            string[][] context_data = [
                ["var ", ""],
                ["let ", ""],
                [null, null]
            ];
            string[] data = [
                "import('x',y)",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            RunModuleParserSyncTest(context_data, data, kError);
        }
        // Import statements as arrow function params and destructuring targets.
        {
            // clang-format off
            string[][] context_data = [
                ["(", ") => {}"],
                ["(a, ", ") => {}"],
                ["(1, ", ") => {}"],
                ["let f = ", " => {}"],
                ["[", "] = [1];"],
                ["{", "} = {'a': 1};"],
                [null, null]
            ];
            string[] data = [
                "import(foo,y)",
                "import(1,y)",
                "import(y=x,z)",
                "import(import(x),y)",
                "import(x,y).then()",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
            RunModuleParserSyncTest(context_data, data, kError);
        }
    }

    [Fact]
    public void SuperCall()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] success_data = [
                "class C extends B { constructor() { super(); } }",
                "class C extends B { constructor() { () => super(); } }", null];
        RunParserSyncTest(context_data, success_data, kSuccess);
        string[] error_data = ["class C { constructor() { super(); } }",
                                                                "class C { method() { super(); } }",
                                                                "class C { method() { () => super(); } }",
                                                                "class C { *method() { super(); } }",
                                                                "class C { get x() { super(); } }",
                                                                "class C { set x(_) { super(); } }",
                                                                "({ method() { super(); } })",
                                                                "({ *method() { super(); } })",
                                                                "({ get x() { super(); } })",
                                                                "({ set x(_) { super(); } })",
                                                                "({ f: function() { super(); } })",
                                                                "(function() { super(); })",
                                                                "var f = function() { super(); }",
                                                                "({ f: function*() { super(); } })",
                                                                "(function*() { super(); })",
                                                                "var f = function*() { super(); }",
                                                                null];
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void SuperNewNoErrors()
    {
        string[][] context_data = [["class C { constructor() { ", " } }"],
                                                                          ["class C { *method() { ", " } }"],
                                                                          ["class C { get x() { ", " } }"],
                                                                          ["class C { set x(_) { ", " } }"],
                                                                          ["({ method() { ", " } })"],
                                                                          ["({ *method() { ", " } })"],
                                                                          ["({ get x() { ", " } })"],
                                                                          ["({ set x(_) { ", " } })"],
                                                                          [null, null]];
        string[] expression_data = ["new super.x;", "new super.x();",
                                                                          "() => new super.x;", "() => new super.x();",
                                                                          null];
        RunParserSyncTest(context_data, expression_data, kSuccess);
    }

    [Fact]
    public void SuperNewErrors()
    {
        string[][] context_data = [["class C { method() { ", " } }"],
                                                                          ["class C { *method() { ", " } }"],
                                                                          ["class C { get x() { ", " } }"],
                                                                          ["class C { set x(_) { ", " } }"],
                                                                          ["({ method() { ", " } })"],
                                                                          ["({ *method() { ", " } })"],
                                                                          ["({ get x() { ", " } })"],
                                                                          ["({ set x(_) { ", " } })"],
                                                                          ["({ f: function() { ", " } })"],
                                                                          ["(function() { ", " })"],
                                                                          ["var f = function() { ", " }"],
                                                                          ["({ f: function*() { ", " } })"],
                                                                          ["(function*() { ", " })"],
                                                                          ["var f = function*() { ", " }"],
                                                                          [null, null]];
        string[] statement_data = ["new super;", "new super();",
                                                                        "() => new super;", "() => new super();",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void SuperErrorsNonMethods()
    {
        // super is only allowed in methods, accessors and constructors.
        string[][] context_data = [["", ";"],
                                                                          ["k = ", ";"],
                                                                          ["foo(", ");"],
                                                                          ["if (", ") {}"],
                                                                          ["if (true) {", "}"],
                                                                          ["if (false) {} else {", "}"],
                                                                          ["while (true) {", "}"],
                                                                          ["function f() {", "}"],
                                                                          ["class C extends (", ") {}"],
                                                                          ["class C { m() { function f() {", "} } }"],
                                                                          ["({ m() { function f() {", "} } })"],
                                                                          [null, null]];
        string[] statement_data = [
                "super", "super = x", "y = super", "f(super)",
                "super.x", "super[27]", "super.x()", "super[27]()",
                "super()", "new super.x", "new super.x()", "new super[27]",
                "new super[27]()", null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void NoErrorsMethodDefinition()
    {
        string[][] context_data = [["({", "});"],
                                                                          ["'use strict'; ({", "});"],
                                                                          ["({*", "});"],
                                                                          ["'use strict'; ({*", "});"],
                                                                          [null, null]];
        string[] object_literal_body_data = [
                "m() {}", "m(x) { return x; }", "m(x, y) {}, n() {}",
                "set(x, y) {}", "get(x, y) {}", null];
        RunParserSyncTest(context_data, object_literal_body_data, kSuccess);
    }

    [Fact]
    public void MethodDefinitionNames()
    {
        string[][] context_data = [["({", "(x, y) {}});"],
                                                                          ["'use strict'; ({", "(x, y) {}});"],
                                                                          ["({*", "(x, y) {}});"],
                                                                          ["'use strict'; ({*", "(x, y) {}});"],
                                                                          [null, null]];
        string[] name_data = [
                "m", "'m'", "\"m\"", "\"m n\"", "true", "false", "null", "0", "1.2",
                "1e1", "1E1", "1e+1", "1e-1",
                // Keywords
                "async", "await", "break", "case", "catch", "class", "const", "continue",
                "debugger", "default", "delete", "do", "else", "enum", "export",
                "extends", "finally", "for", "function", "if", "implements", "import",
                "in", "instanceof", "interface", "let", "new", "package", "private",
                "protected", "public", "return", "static", "super", "switch", "this",
                "throw", "try", "typeof", "var", "void", "while", "with", "yield",
                null];
        RunParserSyncTest(context_data, name_data, kSuccess);
    }

    [Fact]
    public void MethodDefinitionStrictFormalParamereters()
    {
        string[][] context_data = [["({method(", "){}});"],
                                                                          ["'use strict'; ({method(", "){}});"],
                                                                          ["({*method(", "){}});"],
                                                                          ["'use strict'; ({*method(", "){}});"],
                                                                          [null, null]];
        string[] params_data = ["x, x", "x, y, x", "var", "const", null];
        RunParserSyncTest(context_data, params_data, kError);
    }

    [Fact]
    public void MethodDefinitionEvalArguments()
    {
        string[][] strict_context_data = [
                ["'use strict'; ({method(", "){}});"],
                ["'use strict'; ({*method(", "){}});"],
                [null, null]];
        string[][] sloppy_context_data = [
                ["({method(", "){}});"], ["({*method(", "){}});"], [null, null]];
        string[] data = ["eval", "arguments", null];
        // Fail in strict mode
        RunParserSyncTest(strict_context_data, data, kError);
        // OK in sloppy mode
        RunParserSyncTest(sloppy_context_data, data, kSuccess);
    }

    [Fact]
    public void MethodDefinitionDuplicateEvalArguments()
    {
        string[][] context_data = [["'use strict'; ({method(", "){}});"],
                                                                          ["'use strict'; ({*method(", "){}});"],
                                                                          ["({method(", "){}});"],
                                                                          ["({*method(", "){}});"],
                                                                          [null, null]];
        string[] data = ["eval, eval", "eval, a, eval", "arguments, arguments",
                                                    "arguments, a, arguments", null];
        // In strict mode, the error is using "eval" or "arguments" as parameter names
        // In sloppy mode, the error is that eval / arguments are duplicated
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void MethodDefinitionDuplicateProperty()
    {
        string[][] context_data = [["'use strict'; ({", "});"],
                                                                          [null, null]];
        string[] params_data = ["x: 1, x() {}",
                                                                  "x() {}, x: 1",
                                                                  "x() {}, get x() {}",
                                                                  "x() {}, set x(_) {}",
                                                                  "x() {}, x() {}",
                                                                  "x() {}, y() {}, x() {}",
                                                                  "x() {}, \"x\"() {}",
                                                                  "x() {}, 'x'() {}",
                                                                  "0() {}, '0'() {}",
                                                                  "1.0() {}, 1: 1",
                                                                  "x: 1, *x() {}",
                                                                  "*x() {}, x: 1",
                                                                  "*x() {}, get x() {}",
                                                                  "*x() {}, set x(_) {}",
                                                                  "*x() {}, *x() {}",
                                                                  "*x() {}, y() {}, *x() {}",
                                                                  "*x() {}, *\"x\"() {}",
                                                                  "*x() {}, *'x'() {}",
                                                                  "*0() {}, *'0'() {}",
                                                                  "*1.0() {}, 1: 1",
                                                                  null];
        RunParserSyncTest(context_data, params_data, kSuccess);
    }

    [Fact]
    public void ClassExpressionNoErrors()
    {
        string[][] context_data = [
                ["(", ");"], ["var C = ", ";"], ["bar, ", ";"], [null, null]];
        string[] class_data = ["class {}",
                                                                "class name {}",
                                                                "class extends F {}",
                                                                "class name extends F {}",
                                                                "class extends (F, G) {}",
                                                                "class name extends (F, G) {}",
                                                                "class extends class {} {}",
                                                                "class name extends class {} {}",
                                                                "class extends class base {} {}",
                                                                "class name extends class base {} {}",
                                                                null];
        RunParserSyncTest(context_data, class_data, kSuccess);
    }

    [Fact]
    public void ClassDeclarationNoErrors()
    {
        string[][] context_data = [["'use strict'; ", ""],
                                                                          ["'use strict'; {", "}"],
                                                                          ["'use strict'; if (true) {", "}"],
                                                                          [null, null]];
        string[] statement_data = ["class name {}",
                                                                        "class name extends F {}",
                                                                        "class name extends (F, G) {}",
                                                                        "class name extends class {} {}",
                                                                        "class name extends class base {} {}",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ClassBodyNoErrors()
    {
        // clang-format off
        // Tests that parser and preparser accept valid class syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            ";",
            ";;",
            "m() {}",
            "m() {};",
            "; m() {}",
            "m() {}; n(x) {}",
            "get x() {}",
            "set x(v) {}",
            "get() {}",
            "set() {}",
            "*g() {}",
            "*g() {};",
            "; *g() {}",
            "*g() {}; *h(x) {}",
            "async *x(){}",
            "static() {}",
            "get static() {}",
            "set static(v) {}",
            "static m() {}",
            "static get x() {}",
            "static set x(v) {}",
            "static get() {}",
            "static set() {}",
            "static static() {}",
            "static get static() {}",
            "static set static(v) {}",
            "*static() {}",
            "static *static() {}",
            "*get() {}",
            "*set() {}",
            "static *g() {}",
            "async(){}",
            "*async(){}",
            "static async(){}",
            "static *async(){}",
            "static async *x(){}",
            // Escaped 'static' should be allowed anywhere
            // static-as-PropertyName is.
            "st\\u0061tic() {}",
            "get st\\u0061tic() {}",
            "set st\\u0061tic(v) {}",
            "static st\\u0061tic() {}",
            "static get st\\u0061tic() {}",
            "static set st\\u0061tic(v) {}",
            "*st\\u0061tic() {}",
            "static *st\\u0061tic() {}",
            "static async x(){}",
            "static async(){}",
            "static *async(){}",
            "async x(){}",
            "async 0(){}",
            "async get(){}",
            "async set(){}",
            "async static(){}",
            "async async(){}",
            "async(){}",
            "*async(){}",
            null];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void ClassPropertyNameNoErrors()
    {
        string[][] context_data = [["(class {", "() {}});"],
                                                                          ["(class { get ", "() {}});"],
                                                                          ["(class { set ", "(v) {}});"],
                                                                          ["(class { static ", "() {}});"],
                                                                          ["(class { static get ", "() {}});"],
                                                                          ["(class { static set ", "(v) {}});"],
                                                                          ["(class { *", "() {}});"],
                                                                          ["(class { static *", "() {}});"],
                                                                          ["class C {", "() {}}"],
                                                                          ["class C { get ", "() {}}"],
                                                                          ["class C { set ", "(v) {}}"],
                                                                          ["class C { static ", "() {}}"],
                                                                          ["class C { static get ", "() {}}"],
                                                                          ["class C { static set ", "(v) {}}"],
                                                                          ["class C { *", "() {}}"],
                                                                          ["class C { static *", "() {}}"],
                                                                          [null, null]];
        string[] name_data = [
                "42", "42.5", "42e2", "42e+2", "42e-2", "null",
                "false", "true", "'str'", "\"str\"", "static", "get",
                "set", "var", "const", "let", "this", "class",
                "function", "yield", "if", "else", "for", "while",
                "do", "try", "catch", "finally", "accessor", null];
        RunParserSyncTest(context_data, name_data, kSuccess);
    }

    [Fact]
    public void ClassPropertyAccessorNameNoErrorsDecoratorsEnabled()
    {
        v8_flags.js_decorators = true;
        string[][] context_data = [["(class {", "() {}});"],
                                                                          ["(class { get ", "() {}});"],
                                                                          ["(class { set ", "(v) {}});"],
                                                                          ["(class { static ", "() {}});"],
                                                                          ["(class { static get ", "() {}});"],
                                                                          ["(class { static set ", "(v) {}});"],
                                                                          ["(class { *", "() {}});"],
                                                                          ["(class { static *", "() {}});"],
                                                                          ["class C {", "() {}}"],
                                                                          ["class C { get ", "() {}}"],
                                                                          ["class C { set ", "(v) {}}"],
                                                                          ["class C { static ", "() {}}"],
                                                                          ["class C { static get ", "() {}}"],
                                                                          ["class C { static set ", "(v) {}}"],
                                                                          ["class C { *", "() {}}"],
                                                                          ["class C { static *", "() {}}"],
                                                                          [null, null]];
        string[] name_data = ["accessor", null];
        RunParserSyncTest(context_data, name_data, kSuccess);
    }

    [Fact]
    public void StaticClassFieldsNoErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "static a = 0;",
            "static a = 0; b",
            "static a = 0; b(){}",
            "static a = 0; *b(){}",
            "static a = 0; ['b'](){}",
            "static a;",
            "static a; b;",
            "static a; b(){}",
            "static a; *b(){}",
            "static a; ['b'](){}",
            "static ['a'] = 0;",
            "static ['a'] = 0; b",
            "static ['a'] = 0; b(){}",
            "static ['a'] = 0; *b(){}",
            "static ['a'] = 0; ['b'](){}",
            "static ['a'];",
            "static ['a']; b;",
            "static ['a']; b(){}",
            "static ['a']; *b(){}",
            "static ['a']; ['b'](){}",
            "static 0 = 0;",
            "static 0;",
            "static 'a' = 0;",
            "static 'a';",
            "static c = [c] = c",
            // ASI
            "static a = 0\n",
            "static a = 0\n b",
            "static a = 0\n b(){}",
            "static a\n",
            "static a\n b\n",
            "static a\n b(){}",
            "static a\n *b(){}",
            "static a\n ['b'](){}",
            "static ['a'] = 0\n",
            "static ['a'] = 0\n b",
            "static ['a'] = 0\n b(){}",
            "static ['a']\n",
            "static ['a']\n b\n",
            "static ['a']\n b(){}",
            "static ['a']\n *b(){}",
            "static ['a']\n ['b'](){}",
            "static a = function t() { arguments; }",
            "static a = () => function t() { arguments; }",
            // ASI edge cases
            "static a\n get",
            "static get\n *a(){}",
            "static a\n static",
            // Misc edge cases
            "static yield",
            "static yield = 0",
            "static yield\n a",
            "static async;",
            "static async = 0;",
            "static async",
            "static async = 0",
            "static async\n a(){}", // a field named async, and a method named a.
            "static async\n a",
            "static await;",
            "static await = 0;",
            "static await\n a",
            "static accessor;",
            "static accessor = 0;static accessor\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void ClassFieldsNoErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "a = 0;",
            "a = 0; b",
            "a = 0; b(){}",
            "a = 0; *b(){}",
            "a = 0; ['b'](){}",
            "a;",
            "a; b;",
            "a; b(){}",
            "a; *b(){}",
            "a; ['b'](){}",
            "['a'] = 0;",
            "['a'] = 0; b",
            "['a'] = 0; b(){}",
            "['a'] = 0; *b(){}",
            "['a'] = 0; ['b'](){}",
            "['a'];",
            "['a']; b;",
            "['a']; b(){}",
            "['a']; *b(){}",
            "['a']; ['b'](){}",
            "0 = 0;",
            "0;",
            "'a' = 0;",
            "'a';",
            "c = [c] = c",
            // ASI
            "a = 0\n",
            "a = 0\n b",
            "a = 0\n b(){}",
            "a\n",
            "a\n b\n",
            "a\n b(){}",
            "a\n *b(){}",
            "a\n ['b'](){}",
            "['a'] = 0\n",
            "['a'] = 0\n b",
            "['a'] = 0\n b(){}",
            "['a']\n",
            "['a']\n b\n",
            "['a']\n b(){}",
            "['a']\n *b(){}",
            "['a']\n ['b'](){}",
            // ASI edge cases
            "a\n get",
            "get\n *a(){}",
            "a\n static",
            "a = function t() { arguments; }",
            "a = () => function() { arguments; }",
            // Misc edge cases
            "yield",
            "yield = 0",
            "yield\n a",
            "async;",
            "async = 0;",
            "async",
            "async = 0",
            "async\n a(){}", // a field named async, and a method named a.
            "async\n a",
            "await;",
            "await = 0;",
            "await\n a",
            "accessor;",
            "accessor = 0;",
            "accessor\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void ClassFieldsAccessorNameNoErrorsDecoratorsEnabled()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "accessor;",
            "accessor = 0;",
            "accessor\n a",
            "static accessor;",
            "static accessor = 0;static accessor\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMethodsNoErrors()
    {
        // clang-format off
        // Tests proposed class methods syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "#a() { }",
            "get #a() { }",
            "set #a(foo) { }",
            "*#a() { }",
            "async #a() { }",
            "async *#a() { }",
            "#a() { } #b() {}",
            "get #a() { } set #a(foo) {}",
            "get #a() { } get #b() {} set #a(foo) {}",
            "get #a() { } get #b() {} set #a(foo) {} set #b(foo) {}",
            "set #a(foo) { } set #b(foo) {}",
            "get #a() { } get #b() {}",
            "#a() { } static a() {}",
            "#a() { } a() {}",
            "#a() { } a() {} static a() {}",
            "get #a() { } get a() {} static get a() {}",
            "set #a(foo) { } set a(foo) {} static set a(foo) {}",
            "#a() { } get #b() {}",
            "#a() { } async #b() {}",
            "#a() { } async *#b() {}",
            // With arguments
            "#a(...args) { }",
            "#a(a = 1) { }",
            "get #a() { }",
            "set #a(a = (...args) => {}) { }",
            // Misc edge cases
            "#get() {}",
            "#set() {}",
            "#yield() {}",
            "#await() {}",
            "#async() {}",
            "#static() {}",
            "#accessor() {}",
            "#arguments() {}",
            "get #yield() {}",
            "get #await() {}",
            "get #async() {}",
            "get #get() {}",
            "get #static() {}",
            "get #arguments() {}",
            "get #accessor() {}",
            "set #yield(test) {}",
            "set #async(test) {}",
            "set #await(test) {}",
            "set #set(test) {}",
            "set #static(test) {}",
            "set #arguments(test) {}",
            "set #accessor(test) {}async #yield() {}",
            "async #async() {}",
            "async #await() {}",
            "async #get() {}",
            "async #set() {}",
            "async #static() {}",
            "async #arguments() {}",
            "async #accessor() {}",
            "*#async() {}",
            "*#await() {}",
            "*#yield() {}",
            "*#get() {}",
            "*#set() {}",
            "*#static() {}",
            "*#arguments() {}",
            "*#accessor() {}",
            "async *#yield() {}",
            "async *#async() {}",
            "async *#await() {}",
            "async *#get() {}",
            "async *#set() {}",
            "async *#static() {}",
            "async *#arguments() {}",
            "async *#accessor() {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMethodsAccessorNameNoErrorsDecoratorsEnabled()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class methods syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Accessor edge cases
            "#accessor() {}",
            "set #accessor(test) {}",
            "async #accessor() {}",
            "*#accessor() {}",
            "async *#accessor() {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMethodsAndFieldsNoErrors()
    {
        // clang-format off
        // Tests proposed class methods syntax in combination with fields
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "#b;#a() { }",
            "#b;get #a() { }",
            "#b;set #a(foo) { }",
            "#b;*#a() { }",
            "#b;async #a() { }",
            "#b;async *#a() { }",
            "#b = 1;#a() { }",
            "#b = 1;get #a() { }",
            "#b = 1;set #a(foo) { }",
            "#b = 1;*#a() { }",
            "#b = 1;async #a() { }",
            "#b = 1;async *#a() { }",
            // With public fields
            "a;#a() { }",
            "a;get #a() { }",
            "a;set #a(foo) { }",
            "a;*#a() { }",
            "a;async #a() { }",
            "a;async *#a() { }",
            "a = 1;#a() { }",
            "a = 1;get #a() { }",
            "a = 1;set #a(foo) { }",
            "a = 1;*#a() { }",
            "a = 1;async #a() { }",
            "a = 1;async *#a() { }",
            // ASI
            "#a = 0\n #b(){}",
            "#a\n *#b(){}",
            "#a = 0\n get #b(){}",
            "#a\n *#b(){}",
            "b = 0\n #b(){}",
            "b\n *#b(){}",
            "b = 0\n get #b(){}",
            "b\n *#b(){}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PublicAutoAccessorsInNonClassErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["", ""],
                                                                          ["({", "})"],
                                                                          ["'use strict'; ({", "});"],
                                                                          ["function() {", "}"],
                                                                          ["() => {", "}"],
                                                                          ["class C { test() {", "} }"],
                                                                          ["const {", "} = {}"],
                                                                          ["({", "} = {})"],
                                                                          [null, null]];
        string[] class_body_data = [
            "accessor a = 1",
            "accessor a = () => {}",
            "accessor a",
            "accessor 0 = 1",
            "accessor 0 = () => {}",
            "accessor 0",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateAutoAccessorsAndFieldsNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "#b;accessor #a;",
            "#b;accessor #a = 0;",
            "#b = 1;accessor #a;",
            "#b = 1;accessor #a = 0;",
            // With public fields
            "a;accessor #a;",
            "a;accessor #a = 0;",
            "a = 1;accessor #a;",
            "a = 1;accessor #a = 0;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PublicAutoAccessorsInstanceAndStaticNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class auto-accessors syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          // static declarations
                                                                          ["(class { static ", "});"],
                                                                          ["(class extends Base { static ", "});"],
                                                                          ["class C { static ", "}"],
                                                                          ["class C extends Base { static ", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "accessor a = 0;",
            "accessor a = 0; b",
            "accessor a = 0; b(){}",
            "accessor a = 0; *b(){}",
            "accessor a = 0; ['b'](){}",
            "accessor a;",
            "accessor a; b;",
            "accessor a; b(){}",
            "accessor a; *b(){}",
            "accessor a; ['b'](){}",
            "accessor ['a'] = 0;",
            "accessor ['a'] = 0; b",
            "accessor ['a'] = 0; b(){}",
            "accessor ['a'] = 0; *b(){}",
            "accessor ['a'] = 0; ['b'](){}",
            "accessor ['a'];",
            "accessor ['a']; b;",
            "accessor ['a']; b(){}",
            "accessor ['a']; *b(){}",
            "accessor ['a']; ['b'](){}",
            "accessor 0 = 0;",
            "accessor 0;",
            "accessor 'a' = 0;",
            "accessor 'a';",
            "accessor c = [c] = c",
            // ASI
            "accessor a = 0\n",
            "accessor a = 0\n b",
            "accessor a = 0\n b(){}",
            "accessor a\n",
            "accessor a\n b\n",
            "accessor a\n b(){}",
            "accessor a\n *b(){}",
            "accessor a\n ['b'](){}",
            "accessor ['a'] = 0\n",
            "accessor ['a'] = 0\n b",
            "accessor ['a'] = 0\n b(){}",
            "accessor ['a']\n",
            "accessor ['a']\n b\n",
            "accessor ['a']\n b(){}",
            "accessor ['a']\n *b(){}",
            "accessor ['a']\n ['b'](){}",
            // ASI edge cases
            "accessor a\n get",
            "accessor get\n *a(){}",
            "accessor a\n static",
            "accessor a = function t() { arguments; }",
            "accessor a = () => function() { arguments; }",
            // Misc edge cases
            "accessor yield",
            "accessor yield = 0",
            "accessor yield\n a",
            "accessor async;",
            "accessor async = 0;",
            "accessor async",
            "accessor async = 0",
            "accessor async\n a(){}", // a field named async, and a method named a.
            "accessor async\n a",
            "accessor await;",
            "accessor await = 0;",
            "accessor await\n a",
            "accessor accessor;",
            "accessor accessor = 0;",
            "accessor accessor\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMethodsErrors()
    {
        // clang-format off
        // Tests proposed class methods syntax in combination with fields
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "#a() : 0",
            "#a() =",
            "#a() => {}",
            "#a => {}",
            "*#a() = 0",
            "*#a() => 0",
            "*#a() => {}",
            "get #a()[]",
            "yield #a()[]",
            "yield #a => {}",
            "async #a() = 0",
            "async #a => {}",
            "#a(arguments) {}",
            "set #a(arguments) {}",
            "#['a']() { }",
            "get #['a']() { }",
            "set #['a'](foo) { }",
            "*#['a']() { }",
            "async #['a']() { }",
            "async *#['a]() { }",
            "get #a() {} get #a() {}",
            "get #a() {} get #['a']() {}",
            "set #a(val) {} set #a(val) {}",
            "set #a(val) {} set #['a'](val) {}",
            "#a\n#",
            "#a() c",
            "#a() #",
            "#a(arg) c",
            "#a(arg) #",
            "#a(arg) #c",
            "#a#",
            "#a#b",
            "#a#b(){}",
            "#[test](){}",
            "async *#constructor() {}",
            "*#constructor() {}",
            "async #constructor() {}",
            "set #constructor(test) {}",
            "#constructor() {}",
            "get #constructor() {}",
            "static async *#constructor() {}",
            "static *#constructor() {}",
            "static async #constructor() {}",
            "static set #constructor(test) {}",
            "static #constructor() {}",
            "static get #constructor() {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateMembersNestedInObjectLiteralsNoErrors()
    {
        // clang-format off
        string[][] context_data = [["({", "})"],
                                                                          ["'use strict'; ({", "});"],
                                                                          [null, null]];
        string[] class_body_data = [
            "a: class { #a = 1 }",
            "a: class { #a = () => {} }",
            "a: class { #a }",
            "a: class { #a() { } }",
            "a: class { get #a() { } }",
            "a: class { set #a(foo) { } }",
            "a: class { *#a() { } }",
            "a: class { async #a() { } }",
            "a: class { async *#a() { } }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateAutoAccessorsNestedInObjectLiteralsNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["({", "})"],
                                                                          ["'use strict'; ({", "});"],
                                                                          [null, null]];
        string[] class_body_data = [
            "a: class { accessor #a = 1 }",
            "a: class { accessor #a = () => {} }",
            "a: class { accessor #a }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PublicAutoAccessorsNestedNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["({a: ", "})"],
                                                                          ["'use strict'; ({a: ", "});"],
                                                                          ["(class {a = ", "});"],
                                                                          ["(class extends Base {a = ", "});"],
                                                                          ["class C {a = ", "}"],
                                                                          ["class C extends Base {a = ", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "class { accessor a = 1 }",
            "class { accessor a = () => {} }",
            "class { accessor a }",
            "class { accessor 0 = 1 }",
            "class { accessor 0 = () => {} }",
            "class { accessor 0 }",
            "class { accessor ['a'] = 1 }",
            "class { accessor ['a'] = () => {} }",
            "class { accessor ['a'] }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMembersInNestedClassNoErrors()
    {
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "a = class { #a = 1 }",
            "a = class { #a = () => {} }",
            "a = class { #a }",
            "a = class { #a() { } }",
            "a = class { get #a() { } }",
            "a = class { set #a(foo) { } }",
            "a = class { *#a() { } }",
            "a = class { async #a() { } }",
            "a = class { async *#a() { } }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateAutoAccessorsInNestedClassNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "a = class { accessor #a = 1 }",
            "a = class { accessor #a = () => {} }",
            "a = class { accessor #a }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMembersInNonClassErrors()
    {
        // clang-format off
        string[][] context_data = [["", ""],
                                                                          ["({", "})"],
                                                                          ["'use strict'; ({", "});"],
                                                                          ["function() {", "}"],
                                                                          ["() => {", "}"],
                                                                          ["class C { test() {", "} }"],
                                                                          ["const {", "} = {}"],
                                                                          ["({", "} = {})"],
                                                                          [null, null]];
        string[] class_body_data = [
            "#a = 1",
            "#a = () => {}",
            "#a",
            "#a() { }",
            "get #a() { }",
            "set #a(foo) { }",
            "*#a() { }",
            "async #a() { }",
            "async *#a() { }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateAutoAccessorsInNonClassErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["", ""],
                                                                          ["({", "})"],
                                                                          ["'use strict'; ({", "});"],
                                                                          ["function() {", "}"],
                                                                          ["() => {", "}"],
                                                                          ["class C { test() {", "} }"],
                                                                          ["const {", "} = {}"],
                                                                          ["({", "} = {})"],
                                                                          ["class C { static {", "} }"],
                                                                          [null, null]];
        string[] class_body_data = [
            "accessor #a = 1",
            "accessor #a = () => {}",
            "accessor #a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateMembersNestedNoErrors()
    {
        // clang-format off
        string[][] context_data = [["(class { get #a() { ", "} });"],
                                                                          [
                                                                              "(class { set #a(val) {} get #a() { ",
                                                                              "} });"
                                                                            ],
                                                                          ["(class { set #a(val) {", "} });"],
                                                                          ["(class { #a() { ", "} });"],
                                                                          [null, null]];
        string[] class_body_data = [
            "class C { #a() {} }",
            "class C { get #a() {} }",
            "class C { get #a() {} set #a(val) {} }",
            "class C { set #a(val) {} }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateMembersEarlyErrors()
    {
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "set #b(val) { this.#a = val; }",
            "get #b() { return this.#a; }",
            "foo() { return this.#a; }",
            "foo() { this.#a = 1; }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateMembersWrongAccessNoEarlyErrors()
    {
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Private setter only
            "set #b(val) {} fn() { return this.#b; }",
            "set #b(val) {} fn() { this.#b++; }",
            // Nested private setter only
            "get #b() {}\n    fn() {\n      return new class { set #b(val) {} fn() { this.#b++; } };\n    }",
            "get #b() {}\n    fn() {\n      return new class { set #b(val) {} fn() { return this.#b; } };\n    }",
            // Private getter only
            "get #b() { } fn() { this.#b = 1; }",
            "get #b() { } fn() { this.#b++; }",
            "get #b() { } fn(obj) { ({ y: this.#b } = obj); }",
            // Nested private getter only
            "set #b(val) {}\n    fn() {\n      return new class { get #b() {} fn() { this.#b++; } };\n    }",
            "set #b(val) {}\n    fn() {\n      return new class { get #b() {} fn() { this.#b = 1; } };\n    }",
            "set #b(val) {}\n    fn() {\n      return new class { get #b() {} fn() { ({ y: this.#b } = obj); } };\n    }",
            // Writing to private methods
            "#b() { } fn() { this.#b = 1; }",
            "#b() { } fn() { this.#b++; }",
            "#b() {} fn(obj) { ({ y: this.#b } = obj); }",
            // Writing to nested private methods
            "#b() {}\n    fn() {\n      return new class { get #b() {} fn() { this.#b++; } };\n    }",
            "#b() {}\n    fn() {\n      return new class { get #b() {} fn() { this.#b = 1; } };\n    }",
            "#b() {}\n    fn() {\n      return new class { get #b() {} fn() { ({ y: this.#b } = obj); } };\n    }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateStaticClassMethodsAndAccessorsNoErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "static #a() { }",
            "static get #a() { }",
            "static set #a(val) { }",
            "static get #a() { } static set #a(val) { }",
            "static *#a() { }",
            "static async #a() { }",
            "static async *#a() { }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateStaticClassMethodsAndAccessorsDuplicateErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "static get #a() {} static get #a() {}",
            "static get #a() {} static #a() {}",
            "static get #a() {} get #a() {}",
            "static get #a() {} set #a(val) {}",
            "static get #a() {} #a() {}",
            "static set #a(val) {} static set #a(val) {}",
            "static set #a(val) {} static #a() {}",
            "static set #a(val) {} get #a() {}",
            "static set #a(val) {} set #a(val) {}",
            "static set #a(val) {} #a() {}",
            "static #a() {} static #a() {}",
            "static #a() {} #a(val) {}",
            "static #a() {} set #a(val) {}",
            "static #a() {} get #a() {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateStaticAutoAccessorsDuplicateErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "static get #a() {} static accessor #a",
            "static set #a(foo) {} static accessor #a",
            "static #a() {} static accessor #a",
            "static #a; static accessor #a",
            "static accessor #a; static get #a() {}",
            "static accessor #a; static set #a(foo) {}",
            "static accessor #a; static #a",
            "static accessor #a; static #a() {}",
            "static accessor #a; static accessor #a;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateAutoAccessorsDuplicateErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "get #a() {} accessor #a",
            "set #a(foo) {} accessor #a",
            "#a() {} accessor #a",
            "#a; accessor #a",
            "accessor #a; get #a() {}",
            "accessor #a; set #a(foo) {}",
            "accessor #a; #a",
            "accessor #a; #a() {}",
            "accessor #a; accessor #a;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateClassFieldsNoErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "#a = 0;",
            "#a = 0; #b",
            "#a = 0; b",
            "#a = 0; b(){}",
            "#a = 0; *b(){}",
            "#a = 0; ['b'](){}",
            "#a;",
            "#a; #b;",
            "#a; b;",
            "#a; b(){}",
            "#a; *b(){}",
            "#a; ['b'](){}",
            // ASI
            "#a = 0\n",
            "#a = 0\n #b",
            "#a = 0\n b",
            "#a = 0\n b(){}",
            "#a\n",
            "#a\n #b\n",
            "#a\n b\n",
            "#a\n b(){}",
            "#a\n *b(){}",
            "#a\n ['b'](){}",
            // ASI edge cases
            "#a\n get",
            "#get\n *a(){}",
            "#a\n static",
            "#a = function t() { arguments; }",
            "#a = () => function() { arguments; }",
            // Misc edge cases
            "#yield",
            "#yield = 0",
            "#yield\n a",
            "#async;",
            "#async = 0;",
            "#async",
            "#async = 0",
            "#async\n a(){}", // a field named async, and a method named a.
            "#async\n a",
            "#await;",
            "#await = 0;",
            "#await\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void PrivateAutoAccessorsNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "accessor #a = 0;",
            "accessor #a = 0; #b",
            "accessor #a = 0; b",
            "accessor #a = 0; b(){}",
            "accessor #a = 0; *b(){}",
            "accessor #a = 0; ['b'](){}",
            "accessor #a;",
            "accessor #a; #b;",
            "accessor #a; b;",
            "accessor #a; b(){}",
            "accessor #a; *b(){}",
            "accessor #a; ['b'](){}",
            // ASI
            "accessor #a = 0\n",
            "accessor #a = 0\n #b",
            "accessor #a = 0\n b",
            "accessor #a = 0\n b(){}",
            "accessor #a\n",
            "accessor #a\n #b\n",
            "accessor #a\n b\n",
            "accessor #a\n b(){}",
            "accessor #a\n *b(){}",
            "accessor #a\n ['b'](){}",
            // ASI edge cases
            "accessor #a\n get",
            "accessor #get\n *a(){}",
            "accessor #a\n static",
            "accessor #a = function t() { arguments; }",
            "accessor #a = () => function() { arguments; }",
            // Misc edge cases
            "accessor #yield",
            "accessor #yield = 0",
            "accessor #yield\n a",
            "accessor #async;",
            "accessor #async = 0;",
            "accessor #async",
            "accessor #async = 0",
            "accessor #async\n a(){}", // a field named async, and a method named a.
            "accessor #async\n a",
            "accessor #await;",
            "accessor #await = 0;",
            "accessor #await\n a",
            "accessor #accessor;",
            "accessor #accessor = 0;",
            "accessor #accessor\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void StaticClassFieldsErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "static a : 0",
            "static a =",
            "static constructor",
            "static prototype",
            "static *a = 0",
            "static *a",
            "static get a",
            "static get\n a",
            "static yield a",
            "static async a = 0",
            "static async a",
            "static a = arguments",
            "static a = () => arguments",
            "static a = () => { arguments }",
            "static a = arguments[0]",
            "static a = delete arguments[0]",
            "static a = f(arguments)",
            "static a = () => () => arguments",
            // ASI requires a linebreak
            "static a b",
            "static a = 0 b",
            "static c = [1] = [c]",
            // ASI requires that the next token is not part of any legal production
            "static a = 0\n *b(){}",
            "static a = 0\n ['b'](){}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void ClassFieldsErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "a : 0",
            "a =",
            "constructor",
            "*a = 0",
            "*a",
            "get a",
            "yield a",
            "async a = 0",
            "async a",
            "a = arguments",
            "a = () => arguments",
            "a = () => { arguments }",
            "a = arguments[0]",
            "a = delete arguments[0]",
            "a = f(arguments)",
            "a = () => () => arguments",
            // ASI requires a linebreak
            "a b",
            "a = 0 b",
            "c = [1] = [c]",
            // ASI requires that the next token is not part of any legal production
            "a = 0\n *b(){}",
            "a = 0\n ['b'](){}",
            "get\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PublicAutoAccessorsInstanceAndStaticErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          // static declarations
                                                                          ["(class { static ", "});"],
                                                                          ["(class extends Base { static ", "});"],
                                                                          ["class C { static ", "}"],
                                                                          ["class C extends Base { static ", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "accessor a : 0",
            "accessor a =",
            "accessor constructor",
            "accessor *a = 0",
            "accessor *a",
            "accessor get a",
            "accessor yield a",
            "accessor async a = 0",
            "accessor async a",
            "accessor a = arguments",
            "accessor a = () => arguments",
            "accessor a = () => { arguments }",
            "accessor a = arguments[0]",
            "accessor a = delete arguments[0]",
            "accessor a = f(arguments)",
            "accessor a = () => () => arguments",
            // The accessir keyword can only be applied to fields
            "accessor a() {}",
            "accessor *a() {}",
            "accessor async a() {}",
            "accessor get a() {}",
            "accessor set a(foo) {}",
            // ASI requires a linebreak
            "accessor a b",
            "accessor a = 0 b",
            "accessor c = [1] = [c]",
            // ASI requires that the next token is not part of any legal production
            "accessor a = 0\n *b(){}",
            "accessor a = 0\n ['b'](){}",
            "accessor get\n a",
            null
            // ASI
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateClassFieldsErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            "#a : 0",
            "#a =",
            "#*a = 0",
            "#*a",
            "#get a",
            "#yield a",
            "#async a = 0",
            "#async a",
            "#a; #a",
            "#a = 1; #a",
            "#a; #a = 1;",
            "#constructor",
            "#constructor = function() {}",
            "# a = 0",
            "#get a() { }",
            "#set a() { }",
            "#*a() { }",
            "async #*a() { }",
            "#0 = 0;",
            "#0;",
            "#'a' = 0;",
            "#'a';",
            "#['a']",
            "#['a'] = 1",
            "#[a]",
            "#[a] = 1",
            "#a = arguments",
            "#a = () => arguments",
            "#a = () => { arguments }",
            "#a = arguments[0]",
            "#a = delete arguments[0]",
            "#a = f(arguments)",
            "#a = () => () => arguments",
            "foo() { delete this.#a }",
            "foo() { delete this.x.#a }",
            "foo() { delete this.x().#a }",
            "foo() { delete this?.#a }",
            "foo() { delete this.x?.#a }",
            "foo() { delete this?.x.#a }",
            "foo() { delete this.x()?.#a }",
            "foo() { delete this?.x().#a }",
            "foo() { delete f.#a }",
            "foo() { delete f.x.#a }",
            "foo() { delete f.x().#a }",
            "foo() { delete f?.#a }",
            "foo() { delete f.x?.#a }",
            "foo() { delete f?.x.#a }",
            "foo() { delete f.x()?.#a }",
            "foo() { delete f?.x().#a }",
            // ASI requires a linebreak
            "#a b",
            "#a = 0 b",
            // ASI requires that the next token is not part of any legal production
            "#a = 0\n *b(){}",
            "#a = 0\n ['b'](){}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateClassAutoAccessorsErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // The accessor keyword can only be applied to class fields.
            "accessor #a() {}",
            "accessor *#a() {}",
            "accessor async #a() {}",
            "accessor get #a() {}",
            "accessor set #a(foo) {}",
            "accessor async #a() {}",
            "accessor async *#a() {}",
            // Accessors should throw the same errors are regular private fields.
            "accessor #a : 0",
            "accessor #a =",
            "accessor #*a = 0",
            "accessor #*a",
            "accessor #get a",
            "accessor #yield a",
            "accessor #async a = 0",
            "accessor #async a",
            "accessor #a; #a",
            "accessor #a = 1; #a",
            "accessor #a; #a = 1;",
            "accessor #constructor",
            "accessor #constructor = function() {}",
            "accessor # a = 0",
            "accessor #get a() { }",
            "accessor #set a() { }",
            "accessor #*a() { }",
            "accessor async #*a() { }",
            "accessor #0 = 0;",
            "accessor #0;",
            "accessor #'a' = 0;",
            "accessor #'a';",
            "accessor #['a']",
            "accessor #['a'] = 1",
            "accessor #[a]",
            "accessor #[a] = 1",
            "accessor #a = arguments",
            "accessor #a = () => arguments",
            "accessor #a = () => { arguments }",
            "accessor #a = arguments[0]",
            "accessor #a = delete arguments[0]",
            "accessor #a = f(arguments)",
            "accessor #a = () => () => arguments",
            // ASI requires a linebreak
            "accessor #a b",
            "accessor #a = 0 b",
            // ASI requires that the next token is not part of any legal production
            "accessor #a = 0\n *b(){}",
            "accessor #a = 0\n ['b'](){}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateStaticClassFieldsNoErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "static #a = 0;",
            "static #a = 0; b",
            "static #a = 0; #b",
            "static #a = 0; b(){}",
            "static #a = 0; *b(){}",
            "static #a = 0; ['b'](){}",
            "static #a;",
            "static #a; b;",
            "static #a; b(){}",
            "static #a; *b(){}",
            "static #a; ['b'](){}",
            "#prototype",
            "#prototype = function() {}",
            // ASI
            "static #a = 0\n",
            "static #a = 0\n b",
            "static #a = 0\n #b",
            "static #a = 0\n b(){}",
            "static #a\n",
            "static #a\n b\n",
            "static #a\n #b\n",
            "static #a\n b(){}",
            "static #a\n *b(){}",
            "static #a\n ['b'](){}",
            "static #a = function t() { arguments; }",
            "static #a = () => function t() { arguments; }",
            // ASI edge cases
            "static #a\n get",
            "static #get\n *a(){}",
            "static #a\n static",
            // Misc edge cases
            "static #yield",
            "static #yield = 0",
            "static #yield\n a",
            "static #async;",
            "static #async = 0;",
            "static #async",
            "static #async = 0",
            "static #async\n a(){}", // a field named async, and a method named a.
            "static #async\n a",
            "static #await;",
            "static #await = 0;",
            "static #await\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess, null);
    }

    [Fact]
    public void PrivateStaticAutoAccessorsNoErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "static accessor #a = 0;",
            "static accessor #a = 0; b",
            "static accessor #a = 0; #b",
            "static accessor #a = 0; b(){}",
            "static accessor #a = 0; *b(){}",
            "static accessor #a = 0; ['b'](){}",
            "static accessor #a;",
            "static accessor #a; b;",
            "static accessor #a; b(){}",
            "static accessor #a; *b(){}",
            "static accessor #a; ['b'](){}",
            // ASI
            "static accessor #a = 0\n",
            "static accessor #a = 0\n b",
            "static accessor #a = 0\n #b",
            "static accessor #a = 0\n b(){}",
            "static accessor #a\n",
            "static accessor #a\n b\n",
            "static accessor #a\n #b\n",
            "static accessor #a\n b(){}",
            "static accessor #a\n *b(){}",
            "static accessor #a\n ['b'](){}",
            "static accessor #a = function t() { arguments; }",
            "static accessor #a = () => function t() { arguments; }",
            // ASI edge cases
            "static accessor #a\n get",
            "static accessor #get\n *a(){}",
            "static accessor #a\n static",
            // Misc edge cases
            "static accessor #yield",
            "static accessor #yield = 0",
            "static accessor #yield\n a",
            "static accessor #async;",
            "static accessor #async = 0;",
            "static accessor #async",
            "static accessor #async = 0",
            // A field named async, and a method named a.
            "static accessor #async\n a(){}",
            "static accessor #async\n a",
            "static accessor #await;",
            "static accessor #await = 0;",
            "static accessor #await\n a",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kSuccess, null);
    }

    [Fact]
    public void PrivateStaticClassFieldsErrors()
    {
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // Basic syntax
            "static #['a'] = 0;",
            "static #['a'] = 0; b",
            "static #['a'] = 0; #b",
            "static #['a'] = 0; b(){}",
            "static #['a'] = 0; *b(){}",
            "static #['a'] = 0; ['b'](){}",
            "static #['a'];",
            "static #['a']; b;",
            "static #['a']; #b;",
            "static #['a']; b(){}",
            "static #['a']; *b(){}",
            "static #['a']; ['b'](){}",
            "static #0 = 0;",
            "static #0;",
            "static #'a' = 0;",
            "static #'a';",
            "static # a = 0",
            "static #get a() { }",
            "static #set a() { }",
            "static #*a() { }",
            "static async #*a() { }",
            "#a = arguments",
            "#a = () => arguments",
            "#a = () => { arguments }",
            "#a = arguments[0]",
            "#a = delete arguments[0]",
            "#a = f(arguments)",
            "#a = () => () => arguments",
            "#a; static #a",
            "static #a; #a",
            // ASI
            "static #['a'] = 0\n",
            "static #['a'] = 0\n b",
            "static #['a'] = 0\n #b",
            "static #['a'] = 0\n b(){}",
            "static #['a']\n",
            "static #['a']\n b\n",
            "static #['a']\n #b\n",
            "static #['a']\n b(){}",
            "static #['a']\n *b(){}",
            "static #['a']\n ['b'](){}",
            // ASI requires a linebreak
            "static #a b",
            "static #a = 0 b",
            // ASI requires that the next token is not part of any legal production
            "static #a = 0\n *b(){}",
            "static #a = 0\n ['b'](){}",
            "static #a : 0",
            "static #a =",
            "static #*a = 0",
            "static #*a",
            "static #get a",
            "static #yield a",
            "static #async a = 0",
            "static #async a",
            "static # a = 0",
            "#constructor",
            "#constructor = function() {}",
            "foo() { delete this.#a }",
            "foo() { delete this.x.#a }",
            "foo() { delete this.x().#a }",
            "foo() { delete f.#a }",
            "foo() { delete f.x.#a }",
            "foo() { delete f.x().#a }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateStaticAutoAccessorsErrors()
    {
        v8_flags.js_decorators = true;
        // clang-format off
        // Tests proposed class fields syntax.
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] class_body_data = [
            // The accessor keyword can only be applied to class fields.
            "static accessor #a() {}",
            "static accessor *#a() {}",
            "static accessor async #a() {}",
            "static accessor get #a() {}",
            "static accessor set #a(foo) {}",
            "static accessor async #a() {}",
            "static accessor async *#a() {}",
            // Accessors should throw the same errors are regular private fields.
            // Basic syntax
            "static accessor #['a'] = 0;",
            "static accessor #['a'] = 0; b",
            "static accessor #['a'] = 0; #b",
            "static accessor #['a'] = 0; b(){}",
            "static accessor #['a'] = 0; *b(){}",
            "static accessor #['a'] = 0; ['b'](){}",
            "static accessor #['a'];",
            "static accessor #['a']; b;",
            "static accessor #['a']; #b;",
            "static accessor #['a']; b(){}",
            "static accessor #['a']; *b(){}",
            "static accessor #['a']; ['b'](){}",
            "static accessor #0 = 0;",
            "static accessor #0;",
            "static accessor #'a' = 0;",
            "static accessor #'a';",
            "static accessor # a = 0",
            "static accessor #get a() { }",
            "static accessor #set a() { }",
            "static accessor #*a() { }",
            "static accessor async #*a() { }",
            "#a; static accessor #a",
            "static accessor #a; #a",
            // ASI
            "static accessor #['a'] = 0\n",
            "static accessor #['a'] = 0\n b",
            "static accessor #['a'] = 0\n #b",
            "static accessor #['a'] = 0\n b(){}",
            "static accessor #['a']\n",
            "static accessor #['a']\n b\n",
            "static accessor #['a']\n #b\n",
            "static accessor #['a']\n b(){}",
            "static accessor #['a']\n *b(){}",
            "static accessor #['a']\n ['b'](){}",
            // ASI requires a linebreak
            "static accessor #a b",
            "static accessor #a = 0 b",
            // ASI requires that the next token is not part of any legal production
            "static accessor #a = 0\n *b(){}",
            "static accessor #a = 0\n ['b'](){}",
            "static accessor #a : 0",
            "static accessor #a =",
            "static accessor #*a = 0",
            "static accessor #*a",
            "static accessor #get a",
            "static accessor #yield a",
            "static accessor #async a = 0",
            "static accessor #async a",
            "static accessor # a = 0",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void PrivateNameResolutionErrors()
    {
        // clang-format off
        string[][] context_data = [
                ["class X { bar() { ", " } }"],
                ["\"use strict\";", ""],
                [null, null]
        ];
        string[] statement_data = [
            "this.#a",
            "this.#a()",
            "this.#b.#a",
            "this.#b.#a()",
            "foo.#a",
            "foo.#a()",
            "foo.#b.#a",
            "foo.#b.#a()",
            "foo().#a",
            "foo().b.#a",
            "foo().b().#a",
            "foo().b().#a()",
            "foo().b().#a.bar",
            "foo().b().#a.bar()",
            "foo(this.#a)",
            "foo(bar().#a)",
            "new foo.#a",
            "new foo.#b.#a",
            "new foo.#b.#a()",
            "foo.#if;",
            "foo.#yield;",
            "foo.#super;",
            "foo.#interface;",
            "foo.#eval;",
            "foo.#arguments;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void PrivateNameErrors()
    {
        // clang-format off
        string[][] context_data = [
                ["", ""],
                ["function t() { ", " }"],
                ["var t => { ", " }"],
                ["var t = { [ ", " ] }"],
                ["\"use strict\";", ""],
                [null, null]
        ];
        string[] statement_data = [
            "#foo",
            "#foo = 1",
            "# a;",
            "#\n a;",
            "a, # b",
            "a, #, b;",
            "foo.#[a];",
            "foo.#['a'];",
            "foo()#a",
            "foo()#[a]",
            "foo()#['a']",
            "super.#a;",
            "super.#a = 1;",
            "super.#['a']",
            "super.#[a]",
            "new.#a",
            "new.#[a]",
            "foo.#{;",
            "foo.#};",
            "foo.#=;",
            "foo.#888;",
            "foo.#-;",
            "foo.#--;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void ClassExpressionErrors()
    {
        string[][] context_data = [
                ["(", ");"], ["var C = ", ";"], ["bar, ", ";"], [null, null]];
        string[] class_data = [
                "class",
                "class name",
                "class name extends",
                "class extends",
                "class {",
                "class { m: 1 }",
                "class { m(); n() }",
                "class { get m }",
                "class { get m() }",
                "class { get m() { }",
                "class { set m() {} }", // Missing required parameter.
                "class { m() {}, n() {} }", // No commas allowed.
                null];
        RunParserSyncTest(context_data, class_data, kError);
    }

    [Fact]
    public void ClassDeclarationErrors()
    {
        string[][] context_data = [
                ["", ""], ["{", "}"], ["if (true) {", "}"], [null, null]];
        string[] class_data = [
                "class",
                "class name",
                "class name extends",
                "class extends",
                "class name {",
                "class name { m: 1 }",
                "class name { m(); n() }",
                "class name { get x }",
                "class name { get x() }",
                "class name { set x() {) }", // missing required param
                "class {}", // Name is required for declaration
                "class extends base {}",
                "class name { *",
                "class name { * }",
                "class name { *; }",
                "class name { *get x() {} }",
                "class name { *set x(_) {} }",
                "class name { *static m() {} }",
                null];
        RunParserSyncTest(context_data, class_data, kError);
    }

    [Fact]
    public void ClassAsyncErrors()
    {
        // clang-format off
        string[][] context_data = [["(class {", "});"],
                                                                          ["(class extends Base {", "});"],
                                                                          ["class C {", "}"],
                                                                          ["class C extends Base {", "}"],
                                                                          [null, null]];
        string[] async_data = [
            "*async x(){}",
            "async *(){}",
            "async get x(){}",
            "async set x(y){}",
            "async x : 0",
            "async : 0",
            "async static x(){}",
            "static *async x(){}",
            "static async *(){}",
            "static async get x(){}",
            "static async set x(y){}",
            "static async x : 0",
            "static async : 0",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, async_data, kError);
    }

    [Fact]
    public void ClassNameErrors()
    {
        string[][] context_data = [["class ", "{}"],
                                                                          ["(class ", "{});"],
                                                                          ["'use strict'; class ", "{}"],
                                                                          ["'use strict'; (class ", "{});"],
                                                                          [null, null]];
        string[] class_name = ["arguments", "eval", "implements", "interface",
                                                                "let", "package", "private", "protected",
                                                                "public", "static", "var", "yield",
                                                                null];
        RunParserSyncTest(context_data, class_name, kError);
    }

    [Fact]
    public void ClassGetterParamNameErrors()
    {
        string[][] context_data = [
                ["class C { get name(", ") {} }"],
                ["(class { get name(", ") {} });"],
                ["'use strict'; class C { get name(", ") {} }"],
                ["'use strict'; (class { get name(", ") {} })"],
                [null, null]];
        string[] class_name = ["arguments", "eval", "implements", "interface",
                                                                "let", "package", "private", "protected",
                                                                "public", "static", "var", "yield",
                                                                null];
        RunParserSyncTest(context_data, class_name, kError);
    }

    [Fact]
    public void ClassStaticPrototypeErrors()
    {
        string[][] context_data = [
                ["class C {", "}"], ["(class {", "});"], [null, null]];
        string[] class_body_data = ["static prototype() {}",
                                                                          "static get prototype() {}",
                                                                          "static set prototype(_) {}",
                                                                          "static *prototype() {}",
                                                                          "static 'prototype'() {}",
                                                                          "static *'prototype'() {}",
                                                                          "static prot\\u006ftype() {}",
                                                                          "static 'prot\\u006ftype'() {}",
                                                                          "static get 'prot\\u006ftype'() {}",
                                                                          "static set 'prot\\u006ftype'(_) {}",
                                                                          "static *'prot\\u006ftype'() {}",
                                                                          null];
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void ClassSpecialConstructorErrors()
    {
        string[][] context_data = [
                ["class C {", "}"], ["(class {", "});"], [null, null]];
        string[] class_body_data = ["get constructor() {}",
                                                                          "get constructor(_) {}",
                                                                          "*constructor() {}",
                                                                          "get 'constructor'() {}",
                                                                          "*'constructor'() {}",
                                                                          "get c\\u006fnstructor() {}",
                                                                          "*c\\u006fnstructor() {}",
                                                                          "get 'c\\u006fnstructor'() {}",
                                                                          "get 'c\\u006fnstructor'(_) {}",
                                                                          "*'c\\u006fnstructor'() {}",
                                                                          null];
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void ClassConstructorNoErrors()
    {
        string[][] context_data = [
                ["class C {", "}"], ["(class {", "});"], [null, null]];
        string[] class_body_data = ["constructor() {}",
                                                                          "static constructor() {}",
                                                                          "static get constructor() {}",
                                                                          "static set constructor(_) {}",
                                                                          "static *constructor() {}",
                                                                          null];
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void ClassMultipleConstructorErrors()
    {
        string[][] context_data = [
                ["class C {", "}"], ["(class {", "});"], [null, null]];
        string[] class_body_data = ["constructor() {}; constructor() {}",
                                                                          null];
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void ClassMultiplePropertyNamesNoErrors()
    {
        string[][] context_data = [
                ["class C {", "}"], ["(class {", "});"], [null, null]];
        string[] class_body_data = [
                "constructor() {}; static constructor() {}",
                "m() {}; static m() {}",
                "m() {}; m() {}",
                "static m() {}; static m() {}",
                "get m() {}; set m(_) {}; get m() {}; set m(_) {};",
                null];
        RunParserSyncTest(context_data, class_body_data, kSuccess);
    }

    [Fact]
    public void ClassesAreStrictErrors()
    {
        string[][] context_data = [["", ""], ["(", ");"], [null, null]];
        string[] class_body_data = [
                "class C { method() { with ({}) {} } }",
                "class C extends function() { with ({}) {} } {}",
                "class C { *method() { with ({}) {} } }", null];
        RunParserSyncTest(context_data, class_body_data, kError);
    }

    [Fact]
    public void ObjectLiteralPropertyShorthandKeywordsError()
    {
        string[][] context_data = [
                ["({", "});"], ["'use strict'; ({", "});"], [null, null]];
        string[] name_data = [
                "break", "case", "catch", "class", "const", "continue",
                "debugger", "default", "delete", "do", "else", "enum",
                "export", "extends", "false", "finally", "for", "function",
                "if", "import", "in", "instanceof", "new", "null",
                "return", "super", "switch", "this", "throw", "true",
                "try", "typeof", "var", "void", "while", "with",
                null];
        RunParserSyncTest(context_data, name_data, kError);
    }

    [Fact]
    public void ObjectLiteralPropertyShorthandStrictKeywords()
    {
        string[][] context_data = [["({", "});"], [null, null]];
        string[] name_data = ["implements", "interface", "let", "package",
                                                              "private", "protected", "public", "static",
                                                              "yield", null];
        RunParserSyncTest(context_data, name_data, kSuccess);
        string[][] context_strict_data = [["'use strict'; ({", "});"],
                                                                                        [null, null]];
        RunParserSyncTest(context_strict_data, name_data, kError);
    }

    [Fact]
    public void ObjectLiteralPropertyShorthandError()
    {
        string[][] context_data = [
                ["({", "});"], ["'use strict'; ({", "});"], [null, null]];
        string[] name_data = ["1", "1.2", "0", "0.1", "1.0",
                                                              "1e1", "0x1", "\"s\"", "'s'", null];
        RunParserSyncTest(context_data, name_data, kError);
    }

    [Fact]
    public void ObjectLiteralPropertyShorthandYieldInGeneratorError()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] name_data = ["function* g() { ({yield}); }", null];
        RunParserSyncTest(context_data, name_data, kError);
    }

    [Fact]
    public void ConstParsingInForIn()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = [
                "for(const x = 1; ; ) {}", "for(const x = 1, y = 2;;){}",
                "for(const x in [1,2,3]) {}", "for(const x of [1,2,3]) {}", null];
        RunParserSyncTest(context_data, data, kSuccess, null, 0, null, 0);
    }

    [Fact]
    public void StatementParsingInForIn()
    {
        string[][] context_data = [["", ""],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for(x in {}, {}) {}", "for(var x in {}, {}) {}",
                                                    "for(let x in {}, {}) {}", "for(const x in {}, {}) {}",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ConstParsingInForInError()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = [
                "for(const x,y = 1; ; ) {}", "for(const x = 4 in [1,2,3]) {}",
                "for(const x = 4, y in [1,2,3]) {}", "for(const x = 4 of [1,2,3]) {}",
                "for(const x = 4, y of [1,2,3]) {}", "for(const x = 1, y = 2 in []) {}",
                "for(const x,y in []) {}", "for(const x = 1, y = 2 of []) {}",
                "for(const x,y of []) {}", null];
        RunParserSyncTest(context_data, data, kError, null, 0, null, 0);
    }

    [Fact]
    public void InitializedDeclarationsInForInOf()
    {
        // https://tc39.es/ecma262/#sec-initializers-in-forin-statement-heads
        // Initialized declarations only allowed for
        // - sloppy mode (not strict mode)
        // - for-in (not for-of)
        // - var (not let / const)
        // clang-format off
        string[][] strict_context = [["'use strict';", ""],
                                                                              ["function foo(){ 'use strict';", "}"],
                                                                              ["function* foo(){ 'use strict';", "}"],
                                                                              [null, null]];
        string[][] sloppy_context = [["", ""],
                                                                              ["function foo(){ ", "}"],
                                                                              ["function* foo(){ ", "}"],
                                                                              ["function foo(){ var yield = 0; ", "}"],
                                                                              [null, null]];
        string[] let_const_var_for_of = [
                "for (let i = 1 of {}) {}",
                "for (let i = void 0 of [1, 2, 3]) {}",
                "for (const i = 1 of {}) {}",
                "for (const i = void 0 of [1, 2, 3]) {}",
                "for (var i = 1 of {}) {}",
                "for (var i = void 0 of [1, 2, 3]) {}",
                null];
        string[] let_const_for_in = [
                "for (let i = 1 in {}) {}",
                "for (let i = void 0 in [1, 2, 3]) {}",
                "for (const i = 1 in {}) {}",
                "for (const i = void 0 in [1, 2, 3]) {}",
                null];
        string[] var_for_in = [
                "for (var i = 1 in {}) {}",
                "for (var i = void 0 in [1, 2, 3]) {}",
                "for (var i = yield in [1, 2, 3]) {}",
                null];
        // clang-format on
        // The only allowed case is sloppy + var + for-in.
        RunParserSyncTest(sloppy_context, var_for_in, kSuccess);
        // Everything else is disallowed.
        RunParserSyncTest(sloppy_context, let_const_var_for_of, kError);
        RunParserSyncTest(sloppy_context, let_const_for_in, kError);
        RunParserSyncTest(strict_context, let_const_var_for_of, kError);
        RunParserSyncTest(strict_context, let_const_for_in, kError);
        RunParserSyncTest(strict_context, var_for_in, kError);
    }

    [Fact]
    public void ForInMultipleDeclarationsError()
    {
        string[][] context_data = [["", ""],
                                                                          ["function foo(){", "}"],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for (var i, j in {}) {}",
                                                    "for (var i, j in [1, 2, 3]) {}",
                                                    "for (var i, j = 1 in {}) {}",
                                                    "for (var i, j = void 0 in [1, 2, 3]) {}",
                                                    "for (let i, j in {}) {}",
                                                    "for (let i, j in [1, 2, 3]) {}",
                                                    "for (let i, j = 1 in {}) {}",
                                                    "for (let i, j = void 0 in [1, 2, 3]) {}",
                                                    "for (const i, j in {}) {}",
                                                    "for (const i, j in [1, 2, 3]) {}",
                                                    "for (const i, j = 1 in {}) {}",
                                                    "for (const i, j = void 0 in [1, 2, 3]) {}",
                                                    null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ForOfMultipleDeclarationsError()
    {
        string[][] context_data = [["", ""],
                                                                          ["function foo(){", "}"],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for (var i, j of {}) {}",
                                                    "for (var i, j of [1, 2, 3]) {}",
                                                    "for (var i, j = 1 of {}) {}",
                                                    "for (var i, j = void 0 of [1, 2, 3]) {}",
                                                    "for (let i, j of {}) {}",
                                                    "for (let i, j of [1, 2, 3]) {}",
                                                    "for (let i, j = 1 of {}) {}",
                                                    "for (let i, j = void 0 of [1, 2, 3]) {}",
                                                    "for (const i, j of {}) {}",
                                                    "for (const i, j of [1, 2, 3]) {}",
                                                    "for (const i, j = 1 of {}) {}",
                                                    "for (const i, j = void 0 of [1, 2, 3]) {}",
                                                    null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ForInOfLetExpression()
    {
        string[][] sloppy_context_data = [
                ["", ""], ["function foo(){", "}"], [null, null]];
        string[][] strict_context_data = [
                ["'use strict';", ""],
                ["function foo(){ 'use strict';", "}"],
                [null, null]];
        string[][] async_context_data = [
                ["async function foo(){", "}"],
                ["async function foo(){ 'use strict';", "}"],
                [null, null]];
        string[] for_let_in = ["for (let.x in {}) {}", null];
        string[] for_let_of = ["for (let.x of []) {}", null];
        string[] for_await_let_of = ["for await (let.x of []) {}", null];
        // The only place `let.x` is legal as a left-hand side expression
        // is in sloppy mode in a for-in loop.
        RunParserSyncTest(sloppy_context_data, for_let_in, kSuccess);
        RunParserSyncTest(strict_context_data, for_let_in, kError);
        RunParserSyncTest(sloppy_context_data, for_let_of, kError);
        RunParserSyncTest(strict_context_data, for_let_of, kError);
        RunParserSyncTest(async_context_data, for_await_let_of, kError);
    }

    [Fact]
    public void ForInNoDeclarationsError()
    {
        string[][] context_data = [["", ""],
                                                                          ["function foo(){", "}"],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for (var in {}) {}", "for (const in {}) {}", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ForOfNoDeclarationsError()
    {
        string[][] context_data = [["", ""],
                                                                          ["function foo(){", "}"],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for (var of [1, 2, 3]) {}",
                                                    "for (const of [1, 2, 3]) {}", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ForOfInOperator()
    {
        string[][] context_data = [["", ""],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for(x of 'foo' in {}) {}",
                                                    "for(var x of 'foo' in {}) {}",
                                                    "for(let x of 'foo' in {}) {}",
                                                    "for(const x of 'foo' in {}) {}", null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ForOfYieldIdentifier()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] data = ["for(x of yield) {}", "for(var x of yield) {}",
                                                    "for(let x of yield) {}", "for(const x of yield) {}",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ForOfYieldExpression()
    {
        string[][] context_data = [["", ""],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["function* g() { for(x of yield) {} }",
                                                    "function* g() { for(var x of yield) {} }",
                                                    "function* g() { for(let x of yield) {} }",
                                                    "function* g() { for(const x of yield) {} }", null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ForOfExpressionError()
    {
        string[][] context_data = [["", ""],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = [
                "for(x of [], []) {}", "for(var x of [], []) {}",
                "for(let x of [], []) {}", "for(const x of [], []) {}",
                // AssignmentExpression should be validated statically:
                "for(x of { y = 23 }) {}", "for(var x of { y = 23 }) {}",
                "for(let x of { y = 23 }) {}", "for(const x of { y = 23 }) {}", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ForOfAsync()
    {
        string[][] context_data = [["", ""],
                                                                          ["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';", "}"],
                                                                          [null, null]];
        string[] data = ["for(\\u0061sync of []) {}", null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void InvalidUnicodeEscapes()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] data = [
                "var foob\\u123r = 0;", "var \\u123roo = 0;", "\"foob\\u123rr\"",
                // No escapes allowed in regexp flags
                "/regex/\\u0069g", "/regex/\\u006g",
                // Braces gone wrong
                "var foob\\u{c481r = 0;", "var foob\\uc481}r = 0;", "var \\u{0052oo = 0;",
                "var \\u0052}oo = 0;", "\"foob\\u{c481r\"", "var foob\\u{}ar = 0;",
                // Too high value for the Unicode code point escape
                "\"\\u{110000}\"",
                // Not a Unicode code point escape
                "var foob\\v1234r = 0;", "var foob\\U1234r = 0;",
                "var foob\\v{1234}r = 0;", "var foob\\U{1234}r = 0;", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void UnicodeEscapes()
    {
        string[][] context_data = [
                ["", ""], ["'use strict';", ""], [null, null]];
        string[] data = [
                // Identifier starting with escape
                "var \\u0052oo = 0;", "var \\u{0052}oo = 0;", "var \\u{52}oo = 0;",
                "var \\u{00000000052}oo = 0;",
                // Identifier with an escape but not starting with an escape
                "var foob\\uc481r = 0;", "var foob\\u{c481}r = 0;",
                // String with an escape
                "\"foob\\uc481r\"", "\"foob\\{uc481}r\"",
                // This character is a valid Unicode character, representable as a
                // surrogate pair, not representable as 4 hex digits.
                "\"foo\\u{10e6d}\"",
                // Max value for the Unicode code point escape
                "\"\\u{10ffff}\"", null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void OctalEscapes()
    {
        string[][] sloppy_context_data = [["", ""], // as a directive
                                                                                        ["0;", ""], // as a string literal
                                                                                        [null, null]];
        string[][] strict_context_data = [
                ["'use strict';", ""], // as a directive before 'use strict'
                ["", ";'use strict';"], // as a directive after 'use strict'
                ["'use strict'; 0;", ""], // as a string literal
                [null, null]];
        // clang-format off
        string[] data = [
            "'\\1'",
            "'\\01'",
            "'\\001'",
            "'\\08'",
            "'\\09'",
            null];
        // clang-format on
        // Permitted in sloppy mode
        RunParserSyncTest(sloppy_context_data, data, kSuccess);
        // Error in strict mode
        RunParserSyncTest(strict_context_data, data, kError);
    }

    [Fact]
    public void ScanTemplateLiterals()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';  var a, b, c; return ",
                                                                            "}"],
                                                                          [null, null]];
        string[] data = ["``",
                                                    "`no-subst-template`",
                                                    "`template-head${a}`",
                                                    "`${a}`",
                                                    "`${a}template-tail`",
                                                    "`template-head${a}template-tail`",
                                                    "`${a}${b}${c}`",
                                                    "`a${a}b${b}c${c}`",
                                                    "`${a}a${b}b${c}c`",
                                                    "`foo\n\nbar\r\nbaz`",
                                                    "`foo\n\n${  bar  }\r\nbaz`",
                                                    "`foo${a /* comment */}`",
                                                    "`foo${a // comment\n}`",
                                                    "`foo${a \n}`",
                                                    "`foo${a \r\n}`",
                                                    "`foo${a \r}`",
                                                    "`foo${/* comment */ a}`",
                                                    "`foo${// comment\na}`",
                                                    "`foo${\n a}`",
                                                    "`foo${\r\n a}`",
                                                    "`foo${\r a}`",
                                                    "`foo${'a' in a}`",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ScanTaggedTemplateLiterals()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';  function tag() {}  var a, b, c; return ",
                                                                            "}"],
                                                                          [null, null]];
        string[] data = ["tag ``",
                                                    "tag `no-subst-template`",
                                                    "tag`template-head${a}`",
                                                    "tag `${a}`",
                                                    "tag `${a}template-tail`",
                                                    "tag   `template-head${a}template-tail`",
                                                    "tag\n`${a}${b}${c}`",
                                                    "tag\r\n`a${a}b${b}c${c}`",
                                                    "tag    `${a}a${b}b${c}c`",
                                                    "tag\t`foo\n\nbar\r\nbaz`",
                                                    "tag\r`foo\n\n${  bar  }\r\nbaz`",
                                                    "tag`foo${a /* comment */}`",
                                                    "tag`foo${a // comment\n}`",
                                                    "tag`foo${a \n}`",
                                                    "tag`foo${a \r\n}`",
                                                    "tag`foo${a \r}`",
                                                    "tag`foo${/* comment */ a}`",
                                                    "tag`foo${// comment\na}`",
                                                    "tag`foo${\n a}`",
                                                    "tag`foo${\r\n a}`",
                                                    "tag`foo${\r a}`",
                                                    "tag`foo${'a' in a}`",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void TemplateMaterializedLiterals()
    {
        string[][] context_data = [["'use strict';\nfunction tag() {}\nvar a, b, c;\n(",
                                                                            ")"],
                                                                          [null, null]];
        string[] data = ["tag``", "tag`a`", "tag`a${1}b`", "tag`a${1}b${2}c`",
                                                    "``", "`a`", "`a${1}b`", "`a${1}b${2}c`",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ScanUnterminatedTemplateLiterals()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';  var a, b, c; return ",
                                                                            "}"],
                                                                          [null, null]];
        string[] data = ["`no-subst-template",
                                                    "`template-head${a}",
                                                    "`${a}template-tail",
                                                    "`template-head${a}template-tail",
                                                    "`${a}${b}${c}",
                                                    "`a${a}b${b}c${c}",
                                                    "`${a}a${b}b${c}c",
                                                    "`foo\n\nbar\r\nbaz",
                                                    "`foo\n\n${  bar  }\r\nbaz",
                                                    "`foo${a /* comment } */`",
                                                    "`foo${a /* comment } `*/",
                                                    "`foo${a // comment}`",
                                                    "`foo${a \n`",
                                                    "`foo${a \r\n`",
                                                    "`foo${a \r`",
                                                    "`foo${/* comment */ a`",
                                                    "`foo${// commenta}`",
                                                    "`foo${\n a`",
                                                    "`foo${\r\n a`",
                                                    "`foo${\r a`",
                                                    "`foo${fn(}`",
                                                    "`foo${1 if}`",
                                                    null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void TemplateLiteralsIllegalTokens()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function foo(){ 'use strict';  var a, b, c; return ",
                                                                            "}"],
                                                                          [null, null]];
        string[] data = [
                "`hello\\x`", "`hello\\x${1}`", "`hello${1}\\x`",
                "`hello${1}\\x${2}`", "`hello\\x\n`", "`hello\\x\n${1}`",
                "`hello${1}\\x\n`", "`hello${1}\\x\n${2}`", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ParseRestParameters()
    {
        string[][] context_data = [["'use strict';(function(",
                                                                            "){ return args;})(1, [], /regexp/, 'str',function(){});"],
                                                                          ["(function(",
                                                                            "){ return args;})(1, [],/regexp/, 'str', function(){});"],
                                                                          [null, null]];
        string[] data = ["...args",
                                                    "a, ...args",
                                                    "...   args",
                                                    "a, ...   args",
                                                    "...\targs",
                                                    "a, ...\targs",
                                                    "...\r\nargs",
                                                    "a, ...\r\nargs",
                                                    "...\rargs",
                                                    "a, ...\rargs",
                                                    "...\t\n\t\t\n  args",
                                                    "a, ...  \n  \n  args",
                                                    "...{ length, 0: a, 1: b}",
                                                    "...{}",
                                                    "...[a, b]",
                                                    "...[]",
                                                    "...[...[a, b, ...c]]",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ParseRestParametersErrors()
    {
        string[][] context_data = [["'use strict';(function(",
                                                                            "){ return args;}(1, [], /regexp/, 'str',function(){});"],
                                                                          ["(function(",
                                                                            "){ return args;}(1, [],/regexp/, 'str', function(){});"],
                                                                          [null, null]];
        string[] data = ["...args, b",
                                                    "a, ...args, b",
                                                    "...args,   b",
                                                    "a, ...args,   b",
                                                    "...args,\tb",
                                                    "a,...args\t,b",
                                                    "...args\r\n, b",
                                                    "a, ... args,\r\nb",
                                                    "...args\r,b",
                                                    "a, ... args,\rb",
                                                    "...args\t\n\t\t\n,  b",
                                                    "a, ... args,  \n  \n  b",
                                                    "a, a, ...args",
                                                    "a,\ta, ...args",
                                                    "a,\ra, ...args",
                                                    "a,\na, ...args",
                                                    null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void RestParameterInSetterMethodError()
    {
        string[][] context_data = [
                ["'use strict';({ set prop(", ") {} }).prop = 1;"],
                ["'use strict';(class { static set prop(", ") {} }).prop = 1;"],
                ["'use strict';(new (class { set prop(", ") {} })).prop = 1;"],
                ["({ set prop(", ") {} }).prop = 1;"],
                ["(class { static set prop(", ") {} }).prop = 1;"],
                ["(new (class { set prop(", ") {} })).prop = 1;"],
                [null, null]];
        string[] data = ["...a", "...arguments", "...eval", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void RestParametersEvalArguments()
    {
        // clang-format off
        string[][] strict_context_data = [["'use strict';(function(",
                    "){ return;})(1, [], /regexp/, 'str',function(){});"],
                  [null, null]];
        string[][] sloppy_context_data = [["(function(",
                    "){ return;})(1, [],/regexp/, 'str', function(){});"],
                  [null, null]];
        string[] data = [
                "...eval",
                "eval, ...args",
                "...arguments",
                // See https://bugs.chromium.org/p/v8/issues/detail?id=4577
                // "arguments, ...args",
                null];
        // clang-format on
        // Fail in strict mode
        RunParserSyncTest(strict_context_data, data, kError);
        // OK in sloppy mode
        RunParserSyncTest(sloppy_context_data, data, kSuccess);
    }

    [Fact]
    public void RestParametersDuplicateEvalArguments()
    {
        string[][] context_data = [
                ["'use strict';(function(",
                  "){ return;})(1, [], /regexp/, 'str',function(){});"],
                ["(function(", "){ return;})(1, [],/regexp/, 'str', function(){});"],
                [null, null]];
        string[] data = ["eval, ...eval", "eval, eval, ...args",
                                                    "arguments, ...arguments",
                                                    "arguments, arguments, ...args", null];
        // In strict mode, the error is using "eval" or "arguments" as parameter names
        // In sloppy mode, the error is that eval / arguments are duplicated
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void SpreadCall()
    {
        string[][] context_data = [["function fn() { 'use strict';} fn(", ");"],
                                                                          ["function fn() {} fn(", ");"],
                                                                          [null, null]];
        string[] data = ["...([1, 2, 3])",
                                                    "...'123', ...'456'",
                                                    "...new Set([1, 2, 3]), 4",
                                                    "1, ...[2, 3], 4",
                                                    "...Array(...[1,2,3,4])",
                                                    "...NaN",
                                                    "0, 1, ...[2, 3, 4], 5, 6, 7, ...'89'",
                                                    "0, 1, ...[2, 3, 4], 5, 6, 7, ...'89', 10",
                                                    "...[0, 1, 2], 3, 4, 5, 6, ...'7', 8, 9",
                                                    "...[0, 1, 2], 3, 4, 5, 6, ...'7', 8, 9, ...[10]",
                                                    null];
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void SpreadCallErrors()
    {
        string[][] context_data = [["function fn() { 'use strict';} fn(", ");"],
                                                                          ["function fn() {} fn(", ");"],
                                                                          [null, null]];
        string[] data = ["(...[1, 2, 3])", "......[1,2,3]", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void BadRestSpread()
    {
        string[][] context_data = [["function fn() { 'use strict';", "} fn();"],
                                                                          ["function fn() { ", "} fn();"],
                                                                          [null, null]];
        string[] data = [
                "return ...[1,2,3];", "var ...x = [1,2,3];",
                "var [...x,] = [1,2,3];", "var [...x, y] = [1,2,3];",
                "var { x } = {x: ...[1,2,3]}", null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void LexicalScopingSloppyMode()
    {
        string[][] context_data = [
                ["", ""], ["function f() {", "}"], ["{", "}"], [null, null]];
        string[] good_data = ["let = 1;", "for(let = 1;;){}", null];
        RunParserSyncTest(context_data, good_data, kSuccess);
    }

    [Fact]
    public void ComputedPropertyName()
    {
        string[][] context_data = [["({[", "]: 1});"],
                                                                          ["({get [", "]() {}});"],
                                                                          ["({set [", "](_) {}});"],
                                                                          ["({[", "]() {}});"],
                                                                          ["({*[", "]() {}});"],
                                                                          ["(class {get [", "]() {}});"],
                                                                          ["(class {set [", "](_) {}});"],
                                                                          ["(class {[", "]() {}});"],
                                                                          ["(class {*[", "]() {}});"],
                                                                          [null, null]];
        string[] error_data = ["1, 2", "var name", null];
        RunParserSyncTest(context_data, error_data, kError);
        string[] name_data = ["1", "1 + 2", "'name'", "\"name\"",
                                                              "[]", "{}", null];
        RunParserSyncTest(context_data, name_data, kSuccess);
    }

    [Fact]
    public void ComputedPropertyNameShorthandError()
    {
        string[][] context_data = [["({", "});"], [null, null]];
        string[] error_data = ["a: 1, [2]", "[1], a: 1", null];
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void ModuleAwaitReserved()
    {
        // clang-format off
        string[] kErrorSources = [
                "await;",
                "await: ;",
                "var await;",
                "var [await] = [];",
                "var { await } = {};",
                "var { x: await } = {};",
                "{ var await; }",
                "let await;",
                "let [await] = [];",
                "let { await } = {};",
                "let { x: await } = {};",
                "{ let await; }",
                "const await = null;",
                "const [await] = [];",
                "const { await } = {};",
                "const { x: await } = {};",
                "{ const await = null; }",
                "function await() {}",
                "function f(await) {}",
                "function* await() {}",
                "function* g(await) {}",
                "(function await() {});",
                "(function (await) {});",
                "(function* await() {});",
                "(function* (await) {});",
                "(await) => {};",
                "await => {};",
                "class await {}",
                "class C { constructor(await) {} }",
                "class C { m(await) {} }",
                "class C { static m(await) {} }",
                "class C { *m(await) {} }",
                "class C { static *m(await) {} }",
                "(class await {})",
                "(class { constructor(await) {} });",
                "(class { m(await) {} });",
                "(class { static m(await) {} });",
                "(class { *m(await) {} });",
                "(class { static *m(await) {} });",
                "({ m(await) {} });",
                "({ *m(await) {} });",
                "({ set p(await) {} });",
                "try {} catch (await) {}",
                "try {} catch (await) {} finally {}",
                null
        ];
        // clang-format on
        string[][] context_data = [["", ""], [null, null]];
        RunModuleParserSyncTest(context_data, kErrorSources, kError);
    }

    [Fact]
    public void ModuleAwaitReservedPreParse()
    {
        string[][] context_data = [["", ""], [null, null]];
        string[] error_data = ["function f() { var await = 0; }", null];
        RunModuleParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void ModuleAwaitPermitted()
    {
        // clang-format off
        string[] kValidSources = [
            "({}).await;",
            "({ await: null });",
            "({ await() {} });",
            "({ get await() {} });",
            "({ set await(x) {} });",
            "(class { await() {} });",
            "(class { static await() {} });",
            "(class { *await() {} });",
            "(class { static *await() {} });",
            null
        ];
        // clang-format on
        string[][] context_data = [["", ""], [null, null]];
        RunModuleParserSyncTest(context_data, kValidSources, kSuccess);
    }

    [Fact]
    public void EnumReserved()
    {
        // clang-format off
        string[] kErrorSources = [
                "enum;",
                "enum: ;",
                "var enum;",
                "var [enum] = [];",
                "var { enum } = {};",
                "var { x: enum } = {};",
                "{ var enum; }",
                "let enum;",
                "let [enum] = [];",
                "let { enum } = {};",
                "let { x: enum } = {};",
                "{ let enum; }",
                "const enum = null;",
                "const [enum] = [];",
                "const { enum } = {};",
                "const { x: enum } = {};",
                "{ const enum = null; }",
                "function enum() {}",
                "function f(enum) {}",
                "function* enum() {}",
                "function* g(enum) {}",
                "(function enum() {});",
                "(function (enum) {});",
                "(function* enum() {});",
                "(function* (enum) {});",
                "(enum) => {};",
                "enum => {};",
                "class enum {}",
                "class C { constructor(enum) {} }",
                "class C { m(enum) {} }",
                "class C { static m(enum) {} }",
                "class C { *m(enum) {} }",
                "class C { static *m(enum) {} }",
                "(class enum {})",
                "(class { constructor(enum) {} });",
                "(class { m(enum) {} });",
                "(class { static m(enum) {} });",
                "(class { *m(enum) {} });",
                "(class { static *m(enum) {} });",
                "({ m(enum) {} });",
                "({ *m(enum) {} });",
                "({ set p(enum) {} });",
                "try {} catch (enum) {}",
                "try {} catch (enum) {} finally {}",
                null
        ];
        // clang-format on
        string[][] context_data = [["", ""], [null, null]];
        RunModuleParserSyncTest(context_data, kErrorSources, kError);
    }

    [Fact]
    public void DuplicateProtoError()
    {
        string[][] context_data = [
                ["({", "});"], ["'use strict'; ({", "});"], [null, null]];
        string[] error_data = ["__proto__: {}, __proto__: {}",
                                                                "__proto__: {}, \"__proto__\": {}",
                                                                "__proto__: {}, \"__proto__\": {}",
                                                                "__proto__: {}, a: 1, __proto__: {}", null];
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void DuplicateProtoNoError()
    {
        string[][] context_data = [
                ["({", "});"], ["'use strict'; ({", "});"], [null, null]];
        string[] error_data = [
                "__proto__: {}, ['__proto__']: {}", "__proto__: {}, __proto__() {}",
                "__proto__: {}, get __proto__() {}", "__proto__: {}, set __proto__(v) {}",
                "__proto__: {}, __proto__", null];
        RunParserSyncTest(context_data, error_data, kSuccess);
    }

    [Fact]
    public void DeclarationsError()
    {
        string[][] context_data = [["'use strict'; if (true)", ""],
                                                                          ["'use strict'; if (false) {} else", ""],
                                                                          ["'use strict'; while (false)", ""],
                                                                          ["'use strict'; for (;;)", ""],
                                                                          ["'use strict'; for (x in y)", ""],
                                                                          ["'use strict'; do ", " while (false)"],
                                                                          [null, null]];
        string[] statement_data = ["let x = 1;", "const x = 1;", "class C {}",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void LanguageModeDirectives()
    {
        TestLanguageMode("\"use nothing\"", LanguageMode.Sloppy);
        TestLanguageMode("\"use strict\"", LanguageMode.Strict);
        TestLanguageMode("var x = 1; \"use strict\"", LanguageMode.Sloppy);
        TestLanguageMode("\"use some future directive\"; \"use strict\";",
                                          LanguageMode.Strict);
    }

    [Fact]
    public void PropertyNameEvalArguments()
    {
        string[][] context_data = [["'use strict';", ""], [null, null]];
        string[] statement_data = ["({eval: 1})",
                                                                        "({arguments: 1})",
                                                                        "({eval() {}})",
                                                                        "({arguments() {}})",
                                                                        "({*eval() {}})",
                                                                        "({*arguments() {}})",
                                                                        "({get eval() {}})",
                                                                        "({get arguments() {}})",
                                                                        "({set eval(_) {}})",
                                                                        "({set arguments(_) {}})",
                                                                        "class C {eval() {}}",
                                                                        "class C {arguments() {}}",
                                                                        "class C {*eval() {}}",
                                                                        "class C {*arguments() {}}",
                                                                        "class C {get eval() {}}",
                                                                        "class C {get arguments() {}}",
                                                                        "class C {set eval(_) {}}",
                                                                        "class C {set arguments(_) {}}",
                                                                        "class C {static eval() {}}",
                                                                        "class C {static arguments() {}}",
                                                                        "class C {static *eval() {}}",
                                                                        "class C {static *arguments() {}}",
                                                                        "class C {static get eval() {}}",
                                                                        "class C {static get arguments() {}}",
                                                                        "class C {static set eval(_) {}}",
                                                                        "class C {static set arguments(_) {}}",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void FunctionLiteralDuplicateParameters()
    {
        string[][] strict_context_data = [
                ["'use strict';(function(", "){})();"],
                ["(function(", ") { 'use strict'; })();"],
                ["'use strict'; function fn(", ") {}; fn();"],
                ["function fn(", ") { 'use strict'; }; fn();"],
                [null, null]];
        string[][] sloppy_context_data = [["(function(", "){})();"],
                                                                                        ["(function(", ") {})();"],
                                                                                        ["function fn(", ") {}; fn();"],
                                                                                        ["function fn(", ") {}; fn();"],
                                                                                        [null, null]];
        string[] data = [
                "a, a",
                "a, a, a",
                "b, a, a",
                "a, b, c, c",
                "a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, v, w, w",
                null];
        RunParserSyncTest(strict_context_data, data, kError);
        RunParserSyncTest(sloppy_context_data, data, kSuccess);
    }

    [Fact]
    public void ArrowFunctionASIErrors()
    {
        string[][] context_data = [
                ["'use strict';", ""], ["", ""], [null, null]];
        string[] data = ["(a\n=> a)(1)",
                                                    "(a/*\n*/=> a)(1)",
                                                    "((a)\n=> a)(1)",
                                                    "((a)/*\n*/=> a)(1)",
                                                    "((a, b)\n=> a + b)(1, 2)",
                                                    "((a, b)/*\n*/=> a + b)(1, 2)",
                                                    null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ObjectSpreadPositiveTests()
    {
        // clang-format off
        string[][] context_data = [
            ["x = ", ""],
            ["'use strict'; x = ", ""],
            [null, null]];
        // clang-format off
        string[] data = [
            "{ ...y }",
            "{ a: 1, ...y }",
            "{ b: 1, ...y }",
            "{ y, ...y}",
            "{ ...z = y}",
            "{ ...y, y }",
            "{ ...y, ...y}",
            "{ a: 1, ...y, b: 1}",
            "{ ...y, b: 1}",
            "{ ...1}",
            "{ ...null}",
            "{ ...undefined}",
            "{ ...1 in {}}",
            "{ ...[]}",
            "{ ...async function() { }}",
            "{ ...async () => { }}",
            "{ ...new Foo()}",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ObjectSpreadNegativeTests()
    {
        string[][] context_data = [
                ["x = ", ""], ["'use strict'; x = ", ""], [null, null]];
        // clang-format off
        string[] data = [
            "{ ...var z = y}",
            "{ ...var}",
            "{ ...foo bar}",
            "{* ...foo}",
            "{get ...foo}",
            "{set ...foo}",
            "{async ...foo}",
            null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void TemplateEscapesPositiveTests()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null, null]];
        // clang-format off
        string[] data = [
            "tag`\\08`",
            "tag`\\01`",
            "tag`\\01${0}right`",
            "tag`left${0}\\01`",
            "tag`left${0}\\01${1}right`",
            "tag`\\1`",
            "tag`\\1${0}right`",
            "tag`left${0}\\1`",
            "tag`left${0}\\1${1}right`",
            "tag`\\xg`",
            "tag`\\xg${0}right`",
            "tag`left${0}\\xg`",
            "tag`left${0}\\xg${1}right`",
            "tag`\\xAg`",
            "tag`\\xAg${0}right`",
            "tag`left${0}\\xAg`",
            "tag`left${0}\\xAg${1}right`",
            "tag`\\u0`",
            "tag`\\u0${0}right`",
            "tag`left${0}\\u0`",
            "tag`left${0}\\u0${1}right`",
            "tag`\\u0g`",
            "tag`\\u0g${0}right`",
            "tag`left${0}\\u0g`",
            "tag`left${0}\\u0g${1}right`",
            "tag`\\u00g`",
            "tag`\\u00g${0}right`",
            "tag`left${0}\\u00g`",
            "tag`left${0}\\u00g${1}right`",
            "tag`\\u000g`",
            "tag`\\u000g${0}right`",
            "tag`left${0}\\u000g`",
            "tag`left${0}\\u000g${1}right`",
            "tag`\\u{}`",
            "tag`\\u{}${0}right`",
            "tag`left${0}\\u{}`",
            "tag`left${0}\\u{}${1}right`",
            "tag`\\u{-0}`",
            "tag`\\u{-0}${0}right`",
            "tag`left${0}\\u{-0}`",
            "tag`left${0}\\u{-0}${1}right`",
            "tag`\\u{g}`",
            "tag`\\u{g}${0}right`",
            "tag`left${0}\\u{g}`",
            "tag`left${0}\\u{g}${1}right`",
            "tag`\\u{0`",
            "tag`\\u{0${0}right`",
            "tag`left${0}\\u{0`",
            "tag`left${0}\\u{0${1}right`",
            "tag`\\u{\\u{0}`",
            "tag`\\u{\\u{0}${0}right`",
            "tag`left${0}\\u{\\u{0}`",
            "tag`left${0}\\u{\\u{0}${1}right`",
            "tag`\\u{110000}`",
            "tag`\\u{110000}${0}right`",
            "tag`left${0}\\u{110000}`",
            "tag`left${0}\\u{110000}${1}right`",
            "tag` ${tag`\\u`}`",
            "tag` ``\\u`",
            "tag`\\u`` `",
            "tag`\\u``\\u`",
            "` ${tag`\\u`}`",
            "` ``\\u`",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void TemplateEscapesNegativeTests()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null, null]];
        // clang-format off
        string[] data = [
            "`\\08`",
            "`\\01`",
            "`\\01${0}right`",
            "`left${0}\\01`",
            "`left${0}\\01${1}right`",
            "`\\1`",
            "`\\1${0}right`",
            "`left${0}\\1`",
            "`left${0}\\1${1}right`",
            "`\\xg`",
            "`\\xg${0}right`",
            "`left${0}\\xg`",
            "`left${0}\\xg${1}right`",
            "`\\xAg`",
            "`\\xAg${0}right`",
            "`left${0}\\xAg`",
            "`left${0}\\xAg${1}right`",
            "`\\u0`",
            "`\\u0${0}right`",
            "`left${0}\\u0`",
            "`left${0}\\u0${1}right`",
            "`\\u0g`",
            "`\\u0g${0}right`",
            "`left${0}\\u0g`",
            "`left${0}\\u0g${1}right`",
            "`\\u00g`",
            "`\\u00g${0}right`",
            "`left${0}\\u00g`",
            "`left${0}\\u00g${1}right`",
            "`\\u000g`",
            "`\\u000g${0}right`",
            "`left${0}\\u000g`",
            "`left${0}\\u000g${1}right`",
            "`\\u{}`",
            "`\\u{}${0}right`",
            "`left${0}\\u{}`",
            "`left${0}\\u{}${1}right`",
            "`\\u{-0}`",
            "`\\u{-0}${0}right`",
            "`left${0}\\u{-0}`",
            "`left${0}\\u{-0}${1}right`",
            "`\\u{g}`",
            "`\\u{g}${0}right`",
            "`left${0}\\u{g}`",
            "`left${0}\\u{g}${1}right`",
            "`\\u{0`",
            "`\\u{0${0}right`",
            "`left${0}\\u{0`",
            "`left${0}\\u{0${1}right`",
            "`\\u{\\u{0}`",
            "`\\u{\\u{0}${0}right`",
            "`left${0}\\u{\\u{0}`",
            "`left${0}\\u{\\u{0}${1}right`",
            "`\\u{110000}`",
            "`\\u{110000}${0}right`",
            "`left${0}\\u{110000}`",
            "`left${0}\\u{110000}${1}right`",
            "`\\1``\\2`",
            "tag` ${`\\u`}`",
            "`\\u```",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void DestructuringPositiveTests()
    {
        string[][] context_data = [["'use strict'; let ", " = {};"],
                                                                          ["var ", " = {};"],
                                                                          ["'use strict'; const ", " = {};"],
                                                                          ["function f(", ") {}"],
                                                                          ["function f(argument1, ", ") {}"],
                                                                          ["var f = (", ") => {};"],
                                                                          ["var f = (argument1,", ") => {};"],
                                                                          ["try {} catch(", ") {}"],
                                                                          [null, null]];
        // clang-format off
        string[] data = [
            "a",
            "{ x : y }",
            "{ x : y = 1 }",
            "{ get, set }",
            "{ get = 1, set = 2 }",
            "[a]",
            "[a = 1]",
            "[a,b,c]",
            "[a, b = 42, c]",
            "{ x : x, y : y }",
            "{ x : x = 1, y : y }",
            "{ x : x, y : y = 42 }",
            "[]",
            "{}",
            "[{x:x, y:y}, [a,b,c]]",
            "[{x:x = 1, y:y = 2}, [a = 3, b = 4, c = 5]]",
            "{x}",
            "{x, y}",
            "{x = 42, y = 15}",
            "[a,,b]",
            "{42 : x}",
            "{42 : x = 42}",
            "{42e-2 : x}",
            "{42e-2 : x = 42}",
            "{x : y, x : z}",
            "{'hi' : x}",
            "{'hi' : x = 42}",
            "{var: x}",
            "{var: x = 42}",
            "{[x] : z}",
            "{[1+1] : z}",
            "{[foo()] : z}",
            "{}",
            "[...rest]",
            "[a,b,...rest]",
            "[a,,...rest]",
            "{ __proto__: x, __proto__: y}",
            "{arguments: x}",
            "{eval: x}",
            "{ x : y, ...z }",
            "{ x : y = 1, ...z }",
            "{ x : x, y : y, ...z }",
            "{ x : x = 1, y : y, ...z }",
            "{ x : x, y : y = 42, ...z }",
            "[{x:x, y:y, ...z}, [a,b,c]]",
            "[{x:x = 1, y:y = 2, ...z}, [a = 3, b = 4, c = 5]]",
            "{...x}",
            "{x, ...y}",
            "{x = 42, y = 15, ...z}",
            "{42 : x = 42, ...y}",
            "{'hi' : x, ...z}",
            "{'hi' : x = 42, ...z}",
            "{var: x = 42, ...z}",
            "{[x] : z, ...y}",
            "{[1+1] : z, ...x}",
            "{arguments: x, ...z}",
            "{ __proto__: x, __proto__: y, ...z}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void SloppyContextDestructuringPositiveTests()
    {
        // clang-format off
        string[][] sloppy_context_data = [
            ["var ", " = {};"],
            ["function f(", ") {}"],
            ["function f(argument1, ", ") {}"],
            ["var f = (", ") => {};"],
            ["var f = (argument1,", ") => {};"],
            ["try {} catch(", ") {}"],
            [null, null]
        ];
        string[] data = [
            "{arguments}",
            "{eval}",
            "{x: arguments}",
            "{x: eval}",
            "{arguments = false}",
            "{eval = false}",
            "{...arguments}",
            "{...eval}",
            null
        ];
        // clang-format on
        RunParserSyncTest(sloppy_context_data, data, kSuccess);
    }

    [Fact]
    public void DestructuringNegativeTests()
    {
        { // All modes.
            string[][] context_data = [["'use strict'; let ", " = {};"],
                                                                              ["var ", " = {};"],
                                                                              ["'use strict'; const ", " = {};"],
                                                                              ["function f(", ") {}"],
                                                                              ["function f(argument1, ", ") {}"],
                                                                              ["var f = (", ") => {};"],
                                                                              ["var f = ", " => {};"],
                                                                              ["var f = (argument1,", ") => {};"],
                                                                              ["try {} catch(", ") {}"],
                                                                              [null, null]];
            // clang-format off
            string[] data = [
                    "a++",
                    "++a",
                    "delete a",
                    "void a",
                    "typeof a",
                    "--a",
                    "+a",
                    "-a",
                    "~a",
                    "!a",
                    "{ x : y++ }",
                    "[a++]",
                    "(x => y)",
                    "(async x => y)",
                    "((x, z) => y)",
                    "(async (x, z) => y)",
                    "a[i]", "a()",
                    "a.b",
                    "new a",
                    "a + a",
                    "a - a",
                    "a * a",
                    "a / a",
                    "a == a",
                    "a != a",
                    "a > a",
                    "a < a",
                    "a <<< a",
                    "a >>> a",
                    "function a() {}",
                    "function* a() {}",
                    "async function a() {}",
                    "a`bcd`",
                    "this",
                    "null",
                    "true",
                    "false",
                    "1",
                    "'abc'",
                    "/abc/",
                    "`abc`",
                    "class {}",
                    "{+2 : x}",
                    "{-2 : x}",
                    "var",
                    "[var]",
                    "{x : {y : var}}",
                    "{x : x = a+}",
                    "{x : x = (a+)}",
                    "{x : x += a}",
                    "{m() {} = 0}",
                    "{[1+1]}",
                    "[...rest, x]",
                    "[a,b,...rest, x]",
                    "[a,,...rest, x]",
                    "[...rest,]",
                    "[a,b,...rest,]",
                    "[a,,...rest,]",
                    "[...rest,...rest1]",
                    "[a,b,...rest,...rest1]",
                    "[a,,..rest,...rest1]",
                    "[x, y, ...z = 1]",
                    "[...z = 1]",
                    "[x, y, ...[z] = [1]]",
                    "[...[z] = [1]]",
                    "{ x : 3 }",
                    "{ x : 'foo' }",
                    "{ x : /foo/ }",
                    "{ x : `foo` }",
                    "{ get a() {} }",
                    "{ set a() {} }",
                    "{ method() {} }",
                    "{ *method() {} }",
                    "...a++",
                    "...++a",
                    "...typeof a",
                    "...[a++]",
                    "...(x => y)",
                    "{ ...x, }",
                    "{ ...x, y }",
                    "{ y, ...x, y }",
                    "{ ...x, ...y }",
                    "{ ...x, ...x }",
                    "{ ...x, ...x = {} }",
                    "{ ...x, ...x = ...x }",
                    "{ ...x, ...x = ...{ x } }",
                    "{ ,, ...x }",
                    "{ ...get a() {} }",
                    "{ ...set a() {} }",
                    "{ ...method() {} }",
                    "{ ...function() {} }",
                    "{ ...*method() {} }",
                    "{...{x} }",
                    "{...[x] }",
                    "{...{ x = 5 } }",
                    "{...[ x = 5 ] }",
                    "{...x.f }",
                    "{...x[0] }",
                    "async function* a() {}",
                    null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
        }
        { // All modes.
            string[][] context_data = [
                    ["'use strict'; let ", " = {};"], ["var ", " = {};"],
                    ["'use strict'; const ", " = {};"], ["function f(", ") {}"],
                    ["function f(argument1, ", ") {}"], ["var f = (", ") => {};"],
                    ["var f = (argument1,", ") => {};"], [null, null]];
            // clang-format off
            string[] data = [
                    "x => x",
                    "() => x",
                    null];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
        }
        { // Strict mode.
            string[][] context_data = [
                    ["'use strict'; var ", " = {};"],
                    ["'use strict'; let ", " = {};"],
                    ["'use strict'; const ", " = {};"],
                    ["'use strict'; function f(", ") {}"],
                    ["'use strict'; function f(argument1, ", ") {}"],
                    [null, null]];
            // clang-format off
            string[] data = [
                "[arguments]",
                "[eval]",
                "{ a : arguments }",
                "{ a : eval }",
                "[public]",
                "{ x : private }",
                "{ x : arguments }",
                "{ x : eval }",
                "{ arguments }",
                "{ eval }",
                "{ arguments = false }{ eval = false }",
                "{ ...eval }",
                "{ ...arguments }",
                null];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
        }
        { // 'yield' in generators.
            string[][] context_data = [
                    ["function*() { var ", " = {};"],
                    ["function*() { 'use strict'; let ", " = {};"],
                    ["function*() { 'use strict'; const ", " = {};"],
                    [null, null]];
            // clang-format off
            string[] data = [
                "yield",
                "[yield]",
                "{ x : yield }",
                null];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
        }
        { // Declaration-specific errors
            string[][] context_data = [["'use strict'; var ", ""],
                                                                              ["'use strict'; let ", ""],
                                                                              ["'use strict'; const ", ""],
                                                                              ["'use strict'; for (var ", ";;) {}"],
                                                                              ["'use strict'; for (let ", ";;) {}"],
                                                                              ["'use strict'; for (const ", ";;) {}"],
                                                                              ["var ", ""],
                                                                              ["let ", ""],
                                                                              ["const ", ""],
                                                                              ["for (var ", ";;) {}"],
                                                                              ["for (let ", ";;) {}"],
                                                                              ["for (const ", ";;) {}"],
                                                                              [null, null]];
            // clang-format off
            string[] data = [
                "{ a }",
                "[ a ]",
                "{ ...a }",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kError);
        }
    }

    [Fact]
    public void DestructuringAssignmentPositiveTests()
    {
        string[][] context_data = [
                ["'use strict'; let x, y, z; (", " = {});"],
                ["var x, y, z; (", " = {});"],
                ["'use strict'; let x, y, z; for (x in ", " = {});"],
                ["'use strict'; let x, y, z; for (x of ", " = {});"],
                ["var x, y, z; for (x in ", " = {});"],
                ["var x, y, z; for (x of ", " = {});"],
                ["var x, y, z; for (", " in {});"],
                ["var x, y, z; for (", " of {});"],
                ["'use strict'; var x, y, z; for (", " in {});"],
                ["'use strict'; var x, y, z; for (", " of {});"],
                ["var x, y, z; m(['a']) ? ", " = {} : rhs"],
                ["var x, y, z; m(['b']) ? lhs : ", " = {}"],
                ["'use strict'; var x, y, z; m(['a']) ? ", " = {} : rhs"],
                ["'use strict'; var x, y, z; m(['b']) ? lhs : ", " = {}"],
                [null, null]];
        string[][] mixed_assignments_context_data = [
                ["'use strict'; let x, y, z; (", " = z = {});"],
                ["var x, y, z; (", " = z = {});"],
                ["'use strict'; let x, y, z; (x = ", " = z = {});"],
                ["var x, y, z; (x = ", " = z = {});"],
                ["'use strict'; let x, y, z; for (x in ", " = z = {});"],
                ["'use strict'; let x, y, z; for (x in x = ", " = z = {});"],
                ["'use strict'; let x, y, z; for (x of ", " = z = {});"],
                ["'use strict'; let x, y, z; for (x of x = ", " = z = {});"],
                ["var x, y, z; for (x in ", " = z = {});"],
                ["var x, y, z; for (x in x = ", " = z = {});"],
                ["var x, y, z; for (x of ", " = z = {});"],
                ["var x, y, z; for (x of x = ", " = z = {});"],
                [null, null]];
        // clang-format off
        string[] data = [
            "x",
            "{ x : y }",
            "{ x : foo().y }",
            "{ x : foo()[y] }",
            "{ x : y.z }",
            "{ x : y[z] }",
            "{ x : { y } }",
            "{ x : { foo: y } }",
            "{ x : { foo: foo().y } }",
            "{ x : { foo: foo()[y] } }",
            "{ x : { foo: y.z } }",
            "{ x : { foo: y[z] } }",
            "{ x : [ y ] }",
            "{ x : [ foo().y ] }",
            "{ x : [ foo()[y] ] }",
            "{ x : [ y.z ] }",
            "{ x : [ y[z] ] }",
            "{ x : y = 10 }",
            "{ x : foo().y = 10 }",
            "{ x : foo()[y] = 10 }",
            "{ x : y.z = 10 }",
            "{ x : y[z] = 10 }",
            "{ x : { y = 10 } = {} }",
            "{ x : { foo: y = 10 } = {} }",
            "{ x : { foo: foo().y = 10 } = {} }",
            "{ x : { foo: foo()[y] = 10 } = {} }",
            "{ x : { foo: y.z = 10 } = {} }",
            "{ x : { foo: y[z] = 10 } = {} }",
            "{ x : [ y = 10 ] = {} }",
            "{ x : [ foo().y = 10 ] = {} }",
            "{ x : [ foo()[y] = 10 ] = {} }",
            "{ x : [ y.z = 10 ] = {} }",
            "{ x : [ y[z] = 10 ] = {} }",
            "{ z : { __proto__: x, __proto__: y } = z }[ x ]",
            "[ foo().x ]",
            "[ foo()[x] ]",
            "[ x.y ]",
            "[ x[y] ]",
            "[ { x } ]",
            "[ { x : y } ]",
            "[ { x : foo().y } ]",
            "[ { x : foo()[y] } ]",
            "[ { x : x.y } ]",
            "[ { x : x[y] } ]",
            "[ [ x ] ]",
            "[ [ foo().x ] ]",
            "[ [ foo()[x] ] ]",
            "[ [ x.y ] ]",
            "[ [ x[y] ] ]",
            "[ x = 10 ]",
            "[ foo().x = 10 ]",
            "[ foo()[x] = 10 ]",
            "[ x.y = 10 ]",
            "[ x[y] = 10 ]",
            "[ { x = 10 } = {} ]",
            "[ { x : y = 10 } = {} ]",
            "[ { x : foo().y = 10 } = {} ]",
            "[ { x : foo()[y] = 10 } = {} ]",
            "[ { x : x.y = 10 } = {} ]",
            "[ { x : x[y] = 10 } = {} ]",
            "[ [ x = 10 ] = {} ]",
            "[ [ foo().x = 10 ] = {} ]",
            "[ [ foo()[x] = 10 ] = {} ]",
            "[ [ x.y = 10 ] = {} ]",
            "[ [ x[y] = 10 ] = {} ]",
            "{ x : y = 1 }",
            "{ x }",
            "{ x, y, z }",
            "{ x = 1, y: z, z: y }",
            "{x = 42, y = 15}",
            "[x]",
            "[x = 1]",
            "[x,y,z]",
            "[x, y = 42, z]",
            "{ x : x, y : y }",
            "{ x : x = 1, y : y }",
            "{ x : x, y : y = 42 }",
            "[]",
            "{}",
            "[{x:x, y:y}, [,x,z,]]",
            "[{x:x = 1, y:y = 2}, [z = 3, z = 4, z = 5]]",
            "[x,,y]",
            "[(x),,(y)]",
            "[(x)]",
            "{42 : x}",
            "{42 : x = 42}",
            "{42e-2 : x}",
            "{42e-2 : x = 42}",
            "{'hi' : x}",
            "{'hi' : x = 42}",
            "{var: x}",
            "{var: x = 42}",
            "{var: (x) = 42}",
            "{[x] : z}",
            "{[1+1] : z}",
            "{[1+1] : (z)}",
            "{[foo()] : z}",
            "{[foo()] : (z)}",
            "{[foo()] : foo().bar}",
            "{[foo()] : foo()['bar']}",
            "{[foo()] : this.bar}",
            "{[foo()] : this['bar']}",
            "{[foo()] : 'foo'.bar}",
            "{[foo()] : 'foo'['bar']}",
            "[...x]",
            "[x,y,...z]",
            "[x,,...z]",
            "{ x: y }",
            "[x, y]",
            "[((x, y) => z).x]",
            "{x: ((y, z) => z).x}",
            "[((x, y) => z)['x']]",
            "{x: ((y, z) => z)['x']}",
            "{x: { y = 10 } }",
            "[(({ x } = { x: 1 }) => x).a]",
            "{ ...d.x }",
            "{ ...c[0]}",
            // v8:4662
            "{ x: (y) }",
            "{ x: (y) = [] }",
            "{ x: (foo.bar) }",
            "{ x: (foo['bar']) }",
            "[ ...(a) ]",
            "[ ...(foo['bar']) ]",
            "[ ...(foo.bar) ]",
            "[ (y) ]",
            "[ (foo.bar) ]",
            "[ (foo['bar']) ]",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunParserSyncTest(mixed_assignments_context_data, data, kSuccess);
        string[][] empty_context_data = [
                ["'use strict';", ""], ["", ""], [null, null]];
        // CoverInitializedName ambiguity handling in various contexts
        string[] ambiguity_data = [
                "var foo = { x = 10 } = {};",
                "var foo = { q } = { x = 10 } = {};",
                "var foo; foo = { x = 10 } = {};",
                "var foo; foo = { q } = { x = 10 } = {};",
                "var x; ({ x = 10 } = {});",
                "var q, x; ({ q } = { x = 10 } = {});",
                "var x; [{ x = 10 } = {}]",
                "var x; (true ? { x = true } = {} : { x = false } = {})",
                "var q, x; (q, { x = 10 } = {});",
                "var { x = 10 } = { x = 20 } = {};",
                "var { __proto__: x, __proto__: y } = {}",
                "({ __proto__: x, __proto__: y } = {})",
                "var { x = 10 } = (o = { x = 20 } = {});",
                "var x; (({ x = 10 } = { x = 20 } = {}) => x)({})",
                null,
        ];
        RunParserSyncTest(empty_context_data, ambiguity_data, kSuccess);
    }

    [Fact]
    public void DestructuringAssignmentNegativeTests()
    {
        string[][] context_data = [
                ["'use strict'; let x, y, z; (", " = {});"],
                ["var x, y, z; (", " = {});"],
                ["'use strict'; let x, y, z; for (x in ", " = {});"],
                ["'use strict'; let x, y, z; for (x of ", " = {});"],
                ["var x, y, z; for (x in ", " = {});"],
                ["var x, y, z; for (x of ", " = {});"],
                [null, null]];
        // clang-format off
        string[] data = [
            "{ x : ++y }",
            "{ x : y * 2 }",
            "{ get x() {} }",
            "{ set x() {} }",
            "{ x: y() }",
            "{ this }",
            "{ x: this }",
            "{ x: this = 1 }",
            "{ super }",
            "{ x: super }",
            "{ x: super = 1 }",
            "{ new.target }",
            "{ x: new.target }",
            "{ x: new.target = 1 }",
            "{ import.meta }",
            "{ x: import.meta }",
            "{ x: import.meta = 1 }",
            "[x--]",
            "[--x = 1]",
            "[x()]",
            "[this]",
            "[this = 1]",
            "[new.target]",
            "[new.target = 1]",
            "[import.meta]",
            "[import.meta = 1]",
            "[super]",
            "[super = 1]",
            "[function f() {}]",
            "[async function f() {}]",
            "[function* f() {}]",
            "[50]",
            "[(50)]",
            "[(function() {})]",
            "[(async function() {})]",
            "[(function*() {})]",
            "[(foo())]",
            "{ x: 50 }",
            "{ x: (50) }",
            "['str']",
            "{ x: 'str' }",
            "{ x: ('str') }",
            "{ x: (foo()) }",
            "{ x: function() {} }",
            "{ x: async function() {} }",
            "{ x: function*() {} }",
            "{ x: (function() {}) }",
            "{ x: (async function() {}) }",
            "{ x: (function*() {}) }",
            "{ x: y } = 'str'",
            "[x, y] = 'str'",
            "[(x,y) => z]",
            "[async(x,y) => z]",
            "[async x => z]",
            "{x: (y) => z}",
            "{x: (y,w) => z}",
            "{x: async (y) => z}",
            "{x: async (y,w) => z}",
            "[x, ...y, z]",
            "[...x,]",
            "[x, y, ...z = 1]",
            "[...z = 1]",
            "[x, y, ...[z] = [1]]",
            "[...[z] = [1]]",
            "[...++x]",
            "[...x--]",
            "[...!x]",
            "[...x + y]",
            // v8:4657
            "({ x: x4, x: (x+=1e4) })",
            "(({ x: x4, x: (x+=1e4) }))",
            "({ x: x4, x: (x+=1e4) } = {})",
            "(({ x: x4, x: (x+=1e4) } = {}))",
            "(({ x: x4, x: (x+=1e4) }) = {})",
            "({ x: y } = {})",
            "(({ x: y } = {}))",
            "(({ x: y }) = {})",
            "([a])",
            "(([a]))",
            "([a] = [])",
            "(([a] = []))",
            "(([a]) = [])",
            // v8:4662
            "{ x: ([y]) }",
            "{ x: ([y] = []) }",
            "{ x: ({y}) }",
            "{ x: ({y} = {}) }",
            "{ x: (++y) }",
            "[ (...[a]) ]",
            "[ ...([a]) ]",
            "[ ...([a] = [])",
            "[ ...[ ( [ a ] ) ] ]",
            "[ ([a]) ]",
            "[ (...[a]) ]",
            "[ ([a] = []) ]",
            "[ (++y) ]",
            "[ ...(++y) ]",
            "[ x += x ]",
            "{ foo: x += x }",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        {
            v8_flags.js_source_phase_imports = true;
            // clang-format off
            string[] statement_data = [
                "{ import.source }",
                "{ x: import.source }",
                "{ x: import.source = 1 }",
                "[import.source]",
                "[import.source = 1]",
                null];
            // clang-format on
            RunParserSyncTest(context_data, statement_data, kError);
        }
        {
            v8_flags.js_defer_import_eval = true;
            // clang-format off
            string[] statement_data = [
                "{ import.defer }",
                "{ x: import.defer }",
                "{ x: import.defer = 1 }",
                "[import.defer]",
                "[import.defer = 1]",
                null];
            // clang-format on
            RunParserSyncTest(context_data, statement_data, kError);
        }
        string[][] empty_context_data = [
                ["'use strict';", ""], ["", ""], [null, null]];
        // CoverInitializedName ambiguity handling in various contexts
        string[] ambiguity_data = [
                "var foo = { x = 10 };",
                "var foo = { q } = { x = 10 };",
                "var foo; foo = { x = 10 };",
                "var foo; foo = { q } = { x = 10 };",
                "var x; ({ x = 10 });",
                "var q, x; ({ q } = { x = 10 });",
                "var x; [{ x = 10 }]",
                "var x; (true ? { x = true } : { x = false })",
                "var q, x; (q, { x = 10 });",
                "var { x = 10 } = { x = 20 };",
                "var { x = 10 } = (o = { x = 20 });",
                "var x; (({ x = 10 } = { x = 20 }) => x)({})",
                // Not ambiguous, but uses same context data
                "switch([window %= []] = []) { default: }",
                null,
        ];
        RunParserSyncTest(empty_context_data, ambiguity_data, kError);
        // Strict mode errors
        string[][] strict_context_data = [["'use strict'; (", " = {})"],
                                                                                        ["'use strict'; for (", " of {}) {}"],
                                                                                        ["'use strict'; for (", " in {}) {}"],
                                                                                        [null, null]];
        string[] strict_data = [
                "{ eval }", "{ arguments }", "{ foo: eval }", "{ foo: arguments }",
                "{ eval = 0 }", "{ arguments = 0 }", "{ foo: eval = 0 }",
                "{ foo: arguments = 0 }", "[ eval ]", "[ arguments ]", "[ eval = 0 ]",
                "[ arguments = 0 ]",
                // v8:4662
                "{ x: (eval) }", "{ x: (arguments) }", "{ x: (eval = 0) }",
                "{ x: (arguments = 0) }", "{ x: (eval) = 0 }", "{ x: (arguments) = 0 }",
                "[ (eval) ]", "[ (arguments) ]", "[ (eval = 0) ]", "[ (arguments = 0) ]",
                "[ (eval) = 0 ]", "[ (arguments) = 0 ]", "[ ...(eval) ]",
                "[ ...(arguments) ]", "[ ...(eval = 0) ]", "[ ...(arguments = 0) ]",
                "[ ...(eval) = 0 ]", "[ ...(arguments) = 0 ]",
                null];
        RunParserSyncTest(strict_context_data, strict_data, kError);
    }

    [Fact]
    public void DestructuringDisallowPatternsInForVarIn()
    {
        string[][] context_data = [
                ["", ""], ["function f() {", "}"], [null, null]];
        // clang-format off
        string[] error_data = [
            "for (let x = {} in null);",
            "for (let x = {} of null);",
            null];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
        // clang-format off
        string[] success_data = [
            "for (var x = {} in null);",
            null];
        // clang-format on
        RunParserSyncTest(context_data, success_data, kSuccess);
    }

    [Fact]
    public void DestructuringDuplicateParams()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function outer() { 'use strict';", "}"],
                                                                          [null, null]];
        // clang-format off
        string[] error_data = [
            "function f(x,x){}",
            "function f(x, {x : x}){}",
            "function f(x, {x}){}",
            "function f({x,x}) {}",
            "function f([x,x]) {}",
            "function f(x, [y,{z:x}]) {}",
            "function f([x,{y:x}]) {}",
            // non-simple parameter list causes duplicates to be errors in sloppy mode.
            "function f(x, x, {a}) {}",
            null];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void DestructuringDuplicateParamsSloppy()
    {
        string[][] context_data = [
                ["", ""], ["function outer() {", "}"], [null, null]];
        // clang-format off
        string[] error_data = [
            // non-simple parameter list causes duplicates to be errors in sloppy mode.
            "function f(x, {x : x}){}",
            "function f(x, {x}){}",
            "function f({x,x}) {}",
            "function f(x, x, {a}) {}",
            null];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void DestructuringDisallowPatternsInSingleParamArrows()
    {
        string[][] context_data = [["'use strict';", ""],
                                                                          ["function outer() { 'use strict';", "}"],
                                                                          ["", ""],
                                                                          ["function outer() { ", "}"],
                                                                          [null, null]];
        // clang-format off
        string[] error_data = [
            "var f = {x} => {};",
            "var f = {x,y} => {};",
            null];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void DefaultParametersYieldInInitializers()
    {
        // clang-format off
        string[][] sloppy_function_context_data = [
            ["(function f(", ") { });"],
            [null, null]
        ];
        string[][] strict_function_context_data = [
            ["'use strict'; (function f(", ") { });"],
            [null, null]
        ];
        string[][] sloppy_arrow_context_data = [
            ["((", ")=>{});"],
            [null, null]
        ];
        string[][] strict_arrow_context_data = [
            ["'use strict'; ((", ")=>{});"],
            [null, null]
        ];
        string[][] generator_context_data = [
            ["'use strict'; (function *g(", ") { });"],
            ["(function *g(", ") { });"],
            // Arrow function within generator has the same rules.
            ["'use strict'; (function *g() { (", ") => {} });"],
            ["(function *g() { (", ") => {} });"],
            // And similarly for arrow functions in the parameter list.
            ["'use strict'; (function *g(z = (", ") => {}) { });"],
            ["(function *g(z = (", ") => {}) { });"],
            [null, null]
        ];
        string[] parameter_data = [
            "x=yield",
            "x, y=yield",
            "{x=yield}",
            "[x=yield]",
            "x=(yield)",
            "x, y=(yield)",
            "{x=(yield)}",
            "[x=(yield)]",
            "x=f(yield)",
            "x, y=f(yield)",
            "{x=f(yield)}",
            "[x=f(yield)]",
            "{x}=yield",
            "[x]=yield",
            "{x}=(yield)",
            "[x]=(yield)",
            "{x}=f(yield)",
            "[x]=f(yield)",
            null
        ];
        // Because classes are always in strict mode, these are always errors.
        string[] always_error_param_data = [
            "x = class extends (yield) { }",
            "x = class extends f(yield) { }",
            "x = class extends (null, yield) { }",
            "x = class extends (a ? null : yield) { }",
            "[x] = [class extends (a ? null : yield) { }]",
            "[x = class extends (a ? null : yield) { }]",
            "[x = class extends (a ? null : yield) { }] = [null]",
            "x = class { [yield]() { } }",
            "x = class { static [yield]() { } }",
            "x = class { [(yield, 1)]() { } }",
            "x = class { [y = (yield, 1)]() { } }",
            null
        ];
        // clang-format on
        RunParserSyncTest(sloppy_function_context_data, parameter_data, kSuccess);
        RunParserSyncTest(sloppy_arrow_context_data, parameter_data, kSuccess);
        RunParserSyncTest(strict_function_context_data, parameter_data, kError);
        RunParserSyncTest(strict_arrow_context_data, parameter_data, kError);
        RunParserSyncTest(generator_context_data, parameter_data, kError);
        RunParserSyncTest(generator_context_data, always_error_param_data, kError);
    }

    [Fact]
    public void SpreadArray()
    {
        string[][] context_data = [
                ["'use strict';", ""], ["", ""], [null, null]];
        // clang-format off
        string[] data = [
            "[...a]",
            "[a, ...b]",
            "[...a,]",
            "[...a, ,]",
            "[, ...a]",
            "[...a, ...b]",
            "[...a, , ...b]",
            "[...[...a]]",
            "[, ...a]",
            "[, , ...a]",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void SpreadArrayError()
    {
        string[][] context_data = [
                ["'use strict';", ""], ["", ""], [null, null]];
        // clang-format off
        string[] data = [
            "[...]",
            "[a, ...]",
            "[..., ]",
            "[..., ...]",
            "[ (...a)]",
            null];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void NewTarget()
    {
        // clang-format off
        string[][] good_context_data = [
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[][] bad_context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            "new.target",
            "{ new.target }",
            "() => { new.target }",
            "() => new.target",
            "if (1) { new.target }",
            "if (1) {} else { new.target }",
            "while (0) { new.target }",
            "do { new.target } while (0)",
            null
        ];
        // clang-format on
        RunParserSyncTest(good_context_data, data, kSuccess);
        RunParserSyncTest(bad_context_data, data, kError);
    }

    [Fact]
    public void ImportMetaSuccess()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import.meta",
            "() => { import.meta }",
            "() => import.meta",
            "if (1) { import.meta }",
            "if (1) {} else { import.meta }",
            "while (0) { import.meta }",
            "do { import.meta } while (0)",
            "import.meta.url",
            "import.meta[0]",
            "import.meta.couldBeMutable = true",
            "import.meta()",
            "new import.meta.MagicClass",
            "new import.meta",
            "t = [...import.meta]",
            "f = {...import.meta}",
            "delete import.meta",
            null
        ];
        // clang-format on
        // 2.1.1 Static Semantics: Early Errors
        // ImportMeta
        // * It is an early Syntax Error if Module is not the syntactic goal symbol.
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ImportMetaFailure()
    {
        // clang-format off
        string[][] context_data = [
            ["var ", ""],
            ["let ", ""],
            ["const ", ""],
            ["var [", "] = [1]"],
            ["([", "] = [1])"],
            ["({", "} = {1})"],
            ["var {", " = 1} = 1"],
            ["for (var ", " of [1]) {}"],
            ["(", ") => {}"],
            ["let f = ", " => {}"],
            [null]
        ];
        string[] data = [
            "import.meta",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportSourceSuccess()
    {
        v8_flags.js_source_phase_imports = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            // Basic import declarations, not a source phase import
            "import source from ''",
            "import from from ''",
            // Source phase imports
            "import source source from ''",
            "import source from from ''",
            "import source x from ''",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        // Skip preparser
        RunModuleParserSyncTest(context_data, data, kSuccess, null, 0, null, 0,
                                                        null, 0, false);
    }

    [Fact]
    public void ImportSourceFailure()
    {
        v8_flags.js_source_phase_imports = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import source source source from ''",
            "import source from from from ''",
            "import source default from ''",
            "import source * from from ''",
            "import * source from from ''",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportSourceAttributesNotAllowed()
    {
        v8_flags.js_source_phase_imports = true;
        v8_flags.harmony_import_attributes = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            "import source x from '' with {}",
            "import source x from '' assert {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportCallSourceSuccess()
    {
        v8_flags.js_source_phase_imports = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import.source('')",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ImportCallSourceFailure()
    {
        v8_flags.js_source_phase_imports = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["var ", ""],
            ["let ", ""],
            ["const ", ""],
            ["var [", "] = [1]"],
            ["([", "] = [1])"],
            ["({", "} = {1})"],
            ["var {", " = 1} = 1"],
            ["for (var ", " of [1]) {}"],
            ["(", ") => {}"],
            ["let f = ", " => {}"],
            [null]
        ];
        string[] data = [
            "import.source",
            "import.source.url",
            "import.source[0]",
            "import.source.couldBeMutable = true",
            "import.source()",
            "new import.source.MagicClass",
            "new import.source",
            "new import.source('x').prop",
            "t = [...import.source]",
            "f = {...import.source}",
            "delete import.source",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportCallSourceAttributesNotAllowed()
    {
        v8_flags.js_source_phase_imports = true;
        v8_flags.harmony_import_attributes = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import.source('', )",
            "import.source('', {})",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportDeferSuccess()
    {
        v8_flags.js_defer_import_eval = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            // Basic import declarations, not a deferred import
            "import defer from ''",
            "import from from ''",
            "import { defer } from ''",
            "import { a as defer } from ''",
            "import { defer as a } from ''",
            "import 'defer'",
            "import defer, * as ns from ''",
            // Deferred imports
            "import defer * as ns from ''",
            "import defer * as defer from ''",
            "import defer * as from from ''",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        // Skip preparser
        RunModuleParserSyncTest(context_data, data, kSuccess, null, 0, null, 0,
                                                        null, 0, false);
    }

    [Fact]
    public void ImportDeferSuccessWithAttributes()
    {
        v8_flags.js_defer_import_eval = true;
        v8_flags.harmony_import_attributes = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            // Basic import declarations, not a deferred import
            "import defer from '' with {}",
            "import from from '' with {}",
            "import { defer } from '' with {}",
            "import { a as defer } from '' with {}",
            "import { defer as a } from '' with {}",
            "import 'defer' with {}",
            "import defer, * as ns from '' with {}",
            // Deferred imports
            "import defer * as ns from '' with { }",
            "import defer * as ns from '' with { a: 'b' };",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        // Skip preparser
        RunModuleParserSyncTest(context_data, data, kSuccess, null, 0, null, 0,
                                                        null, 0, false);
    }

    [Fact]
    public void ImportDeferErrorNamedImport()
    {
        v8_flags.js_defer_import_eval = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            [null]
        ];
        string[] data = [
            // Invalid deferred imports
            "import defer a from ''",
            "import defer { } from ''",
            "import defer { a } from ''",
            "import defer { a as b } from ''",
            "import defer { c, d } from ''",
            "import defer { e as f, g as h } from ''",
            "import defer a, * as ns from ''",
            "import a, defer * as ns from ''",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        // Skip preparser
        RunModuleParserSyncTest(context_data, data, kError, null, 0, null, 0,
                                                        null, 0, false);
    }

    [Fact]
    public void ImportCallDeferSuccess()
    {
        v8_flags.js_defer_import_eval = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import.defer('')",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ImportCallDeferFailure()
    {
        v8_flags.js_defer_import_eval = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["var ", ""],
            ["let ", ""],
            ["const ", ""],
            ["var [", "] = [1]"],
            ["([", "] = [1])"],
            ["({", "} = {1})"],
            ["var {", " = 1} = 1"],
            ["for (var ", " of [1]) {}"],
            ["(", ") => {}"],
            ["let f = ", " => {}"],
            [null]
        ];
        string[] data = [
            "import.defer",
            "import.defer.url",
            "import.defer[0]",
            "import.defer.couldBeMutable = true",
            "import.defer()",
            "new import.defer.MagicClass",
            "new import.defer",
            "new import.defer('x').prop",
            "t = [...import.defer]",
            "f = {...import.defer}",
            "delete import.defer",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
        RunModuleParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ImportCallDeferWithAttributesSuccess()
    {
        v8_flags.js_defer_import_eval = true;
        v8_flags.harmony_import_attributes = true;
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["function f() {", "}"],
            ["'use strict'; function f() {", "}"],
            ["var f = function() {", "}"],
            ["'use strict'; var f = function() {", "}"],
            ["({m: function() {", "}})"],
            ["'use strict'; ({m: function() {", "}})"],
            ["({m() {", "}})"],
            ["'use strict'; ({m() {", "}})"],
            ["({get x() {", "}})"],
            ["'use strict'; ({get x() {", "}})"],
            ["({set x(_) {", "}})"],
            ["'use strict'; ({set x(_) {", "}})"],
            ["class C {m() {", "}}"],
            ["class C {get x() {", "}}"],
            ["class C {set x(_) {", "}}"],
            [null]
        ];
        string[] data = [
            "import.defer('', )",
            "import.defer('', { })",
            "import.defer('', { a: b })",
            "import.defer('', { 'a': b })",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        RunModuleParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ConstSloppy()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["{", "}"],
            [null, null]
        ];
        string[] data = [
            "const x = 1",
            "for (const x = 1; x < 1; x++) {}",
            "for (const x in {}) {}",
            "for (const x of []) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void LetSloppy()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["'use strict';", ""],
            ["{", "}"],
            [null, null]
        ];
        string[] data = [
            "let x",
            "let x = 1",
            "for (let x = 1; x < 1; x++) {}",
            "for (let x in {}) {}",
            "for (let x of []) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void LanguageModeDirectivesNonSimpleParameterListErrors()
    {
        // TC39 deemed "use strict" directives to be an error when occurring in the
        // body of a function with non-simple parameter list, on 29/7/2015.
        // https://github.com/tc39/notes/blob/main/meetings/2015-07/july-29.md#conclusionresolution
        string[][] context_data = [
                ["function f(", ") { 'use strict'; }"],
                ["function* g(", ") { 'use strict'; }"],
                ["class c { foo(", ") { 'use strict' }"],
                ["var a = (", ") => { 'use strict'; }"],
                ["var o = { m(", ") { 'use strict'; }"],
                ["var o = { *gm(", ") { 'use strict'; }"],
                ["var c = { m(", ") { 'use strict'; }"],
                ["var c = { *gm(", ") { 'use strict'; }"],
                ["'use strict'; function f(", ") { 'use strict'; }"],
                ["'use strict'; function* g(", ") { 'use strict'; }"],
                ["'use strict'; class c { foo(", ") { 'use strict' }"],
                ["'use strict'; var a = (", ") => { 'use strict'; }"],
                ["'use strict'; var o = { m(", ") { 'use strict'; }"],
                ["'use strict'; var o = { *gm(", ") { 'use strict'; }"],
                ["'use strict'; var c = { m(", ") { 'use strict'; }"],
                ["'use strict'; var c = { *gm(", ") { 'use strict'; }"],
                [null, null]];
        string[] data = [
                // TODO(@caitp): support formal parameter initializers
                "{}",
                "[]",
                "[{}]",
                "{a}",
                "a, {b}",
                "a, b, {c, d, e}",
                "initializer = true",
                "a, b, c = 1",
                "...args",
                "a, b, ...rest",
                "[a, b, ...rest]",
                "{ bindingPattern = {} }",
                "{ initializedBindingPattern } = { initializedBindingPattern: true }",
                null];
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void LetSloppyOnly()
    {
        // clang-format off
        string[][] context_data = [
            ["", ""],
            ["{", "}"],
            ["(function() {", "})()"],
            [null, null]
        ];
        string[] data = [
            "let",
            "let = 1",
            "for (let = 1; let < 1; let++) {}",
            "for (let in {}) {}",
            "for (var let = 1; let < 1; let++) {}",
            "for (var let in {}) {}",
            "for (var [let] = 1; let < 1; let++) {}",
            "for (var [let] in {}) {}",
            "var let",
            "var [let] = []",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        // Some things should be rejected even in sloppy mode
        // This addresses BUG(v8:4403).
        // clang-format off
        string[] fail_data = [
            "let let = 1",
            "for (let let = 1; let < 1; let++) {}",
            "for (let let in {}) {}",
            "for (let let of []) {}",
            "const let = 1",
            "for (const let = 1; let < 1; let++) {}",
            "for (const let in {}) {}",
            "for (const let of []) {}",
            "let [let] = 1",
            "for (let [let] = 1; let < 1; let++) {}",
            "for (let [let] in {}) {}",
            "for (let [let] of []) {}",
            "const [let] = 1",
            "for (const [let] = 1; let < 1; let++) {}",
            "for (const [let] in {}) {}",
            "for (const [let] of []) {}",
            // Sprinkle in the escaped version too.
            "let l\\u0065t = 1",
            "const l\\u0065t = 1",
            "let [l\\u0065t] = 1",
            "const [l\\u0065t] = 1",
            "for (let l\\u0065t in {}) {}",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, fail_data, kError);
    }

    [Fact]
    public void EscapedKeywords()
    {
        // clang-format off
        string[][] sloppy_context_data = [
            ["", ""],
            [null, null]
        ];
        string[][] strict_context_data = [
            ["'use strict';", ""],
            [null, null]
        ];
        string[] fail_data = [
            "for (var i = 0; i < 100; ++i) { br\\u0065ak; }",
            "cl\\u0061ss Foo {}",
            "var x = cl\\u0061ss {}",
            "\\u0063onst foo = 1;",
            "while (i < 10) { if (i++ & 1) c\\u006fntinue; this.x++; }",
            "d\\u0065bugger;",
            "d\\u0065lete this.a;",
            "\\u0063o { } while(0)",
            "if (d\\u006f { true }) {}",
            "if (false) { this.a = 1; } \\u0065lse { this.b = 1; }",
            "e\\u0078port var foo;",
            "try { } catch (e) {} f\\u0069nally { }",
            "f\\u006fr (var i = 0; i < 10; ++i);",
            "f\\u0075nction fn() {}",
            "var f = f\\u0075nction() {}",
            "\\u0069f (true) { }",
            "\\u0069mport blah from './foo.js';",
            "n\\u0065w function f() {}",
            "(function() { r\\u0065turn; })()",
            "class C extends function() {} { constructor() { sup\\u0065r() } }",
            "class C extends function() {} { constructor() { sup\\u0065r.a = 1 } }",
            "sw\\u0069tch (this.a) {}",
            "var x = th\\u0069s;",
            "th\\u0069s.a = 1;",
            "thr\\u006fw 'boo';",
            "t\\u0072y { true } catch (e) {}",
            "var x = typ\\u0065of 'blah'",
            "v\\u0061r a = true",
            "var v\\u0061r = true",
            "(function() { return v\\u006fid 0; })()",
            "wh\\u0069le (true) { }",
            "w\\u0069th (this.scope) { }",
            "(function*() { y\\u0069eld 1; })()",
            "(function*() { var y\\u0069eld = 1; })()",
            "var \\u0065num = 1;",
            "var { \\u0065num } = {}",
            "(\\u0065num = 1);",
            // Null / Boolean literals
            "(x === n\\u0075ll);",
            "var x = n\\u0075ll;",
            "var n\\u0075ll = 1;",
            "var { n\\u0075ll } = { 1 };",
            "n\\u0075ll = 1;",
            "(x === tr\\u0075e);",
            "var x = tr\\u0075e;",
            "var tr\\u0075e = 1;",
            "var { tr\\u0075e } = {};",
            "tr\\u0075e = 1;",
            "(x === f\\u0061lse);",
            "var x = f\\u0061lse;",
            "var f\\u0061lse = 1;",
            "var { f\\u0061lse } = {};",
            "f\\u0061lse = 1;",
            // TODO(caitp): consistent error messages for labeled statements and
            // expressions
            "switch (this.a) { c\\u0061se 6: break; }",
            "try { } c\\u0061tch (e) {}",
            "switch (this.a) { d\\u0065fault: break; }",
            "class C \\u0065xtends function B() {} {}",
            "for (var a i\\u006e this) {}",
            "if ('foo' \\u0069n this) {}",
            "if (this \\u0069nstanceof Array) {}",
            "(n\\u0065w function f() {})",
            "(typ\\u0065of 123)",
            "(v\\u006fid 0)",
            "do { ; } wh\\u0069le (true) { }",
            "(function*() { return (n++, y\\u0069eld 1); })()",
            "class C { st\\u0061tic bar() {} }",
            "class C { st\\u0061tic *bar() {} }",
            "class C { st\\u0061tic get bar() {} }",
            "class C { st\\u0061tic set bar() {} }",
            "(async ()=>{\\u0061wait 100})()",
            "({\\u0067et get(){}})",
            "({\\u0073et set(){}})",
            "(async ()=>{var \\u0061wait = 100})()",
            "for (var x o\\u0066 [])",
            null
        ];
        // clang-format on
        RunParserSyncTest(sloppy_context_data, fail_data, kError);
        RunParserSyncTest(strict_context_data, fail_data, kError);
        RunModuleParserSyncTest(sloppy_context_data, fail_data, kError);
        // clang-format off
        string[] let_data = [
            "var l\\u0065t = 1;",
            "l\\u0065t = 1;",
            "(l\\u0065t === 1);",
            "(y\\u0069eld);",
            "var y\\u0069eld = 1;",
            "var { y\\u0069eld } = {};",
            null
        ];
        // clang-format on
        RunParserSyncTest(sloppy_context_data, let_data, kSuccess);
        RunParserSyncTest(strict_context_data, let_data, kError);
        // Non-errors in sloppy mode
        string[] valid_data = ["(\\u0069mplements = 1);",
                                                                "var impl\\u0065ments = 1;",
                                                                "var { impl\\u0065ments  } = {};",
                                                                "(\\u0069nterface = 1);",
                                                                "var int\\u0065rface = 1;",
                                                                "var { int\\u0065rface  } = {};",
                                                                "(p\\u0061ckage = 1);",
                                                                "var packa\\u0067e = 1;",
                                                                "var { packa\\u0067e  } = {};",
                                                                "(p\\u0072ivate = 1);",
                                                                "var p\\u0072ivate;",
                                                                "var { p\\u0072ivate } = {};",
                                                                "(prot\\u0065cted);",
                                                                "var prot\\u0065cted = 1;",
                                                                "var { prot\\u0065cted  } = {};",
                                                                "(publ\\u0069c);",
                                                                "var publ\\u0069c = 1;",
                                                                "var { publ\\u0069c } = {};",
                                                                "(st\\u0061tic);",
                                                                "var st\\u0061tic = 1;",
                                                                "var { st\\u0061tic } = {};",
                                                                null];
        RunParserSyncTest(sloppy_context_data, valid_data, kSuccess);
        RunParserSyncTest(strict_context_data, valid_data, kError);
        RunModuleParserSyncTest(strict_context_data, valid_data, kError);
    }

    [Fact]
    public void MiscSyntaxErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "'use strict'", "" ],
            [ "", "" ],
            [ null, null ]
        ];
        string[] error_data = [
            "for (();;) {}",
            // crbug.com/582626
            "{ NaN ,chA((evarA=new t ( l = !.0[((... co -a0([1]))=> greturnkf",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void EscapeSequenceErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "'", "'" ],
            [ "\"", "\"" ],
            [ "`", "`" ],
            [ "`${'", "'}`" ],
            [ "`${\"", "\"}`" ],
            [ "`${`", "`}`" ],
            [ null, null ]
        ];
        string[] error_data = [
            "\\uABCG",
            "\\u{ZZ}",
            "\\u{FFZ}",
            "\\u{FFFFFFFFFF }",
            "\\u{110000}",
            "\\u{110000",
            "\\u{FFFD }",
            "\\xZF",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void NewTargetErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "'use strict'", "" ],
            [ "", "" ],
            [ null, null ]
        ];
        string[] error_data = [
            "var x = new.target",
            "function f() { return new.t\\u0061rget; }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void FunctionDeclarationError()
    {
        // clang-format off
        string[][] strict_context = [
            [ "'use strict';", "" ],
            [ "'use strict'; { ", "}" ],
            ["(function() { 'use strict';", "})()"],
            ["(function() { 'use strict'; {", "} })()"],
            [ null, null ]
        ];
        string[][] sloppy_context = [
            [ "", "" ],
            [ "{", "}" ],
            ["(function() {", "})()"],
            ["(function() { {", "} })()"],
            [ null, null ]
        ];
        // Invalid in all contexts
        string[] error_data = [
            "try function foo() {} catch (e) {}",
            "do function foo() {} while (0);",
            "for (;false;) function foo() {}",
            "for (var i = 0; i < 1; i++) function f() { };",
            "for (var x in {a: 1}) function f() { };",
            "for (var x in {}) function f() { };",
            "for (var x in {}) function foo() {}",
            "for (x in {a: 1}) function f() { };",
            "for (x in {}) function f() { };",
            "var x; for (x in {}) function foo() {}",
            "with ({}) function f() { };",
            "do label: function foo() {} while (0);",
            "for (;false;) label: function foo() {}",
            "for (var i = 0; i < 1; i++) label: function f() { };",
            "for (var x in {a: 1}) label: function f() { };",
            "for (var x in {}) label: function f() { };",
            "for (var x in {}) label: function foo() {}",
            "for (x in {a: 1}) label: function f() { };",
            "for (x in {}) label: function f() { };",
            "var x; for (x in {}) label: function foo() {}",
            "with ({}) label: function f() { };",
            "if (true) label: function f() {}",
            "if (true) {} else label: function f() {}",
            "if (true) function* f() { }",
            "label: function* f() { }",
            "if (true) async function f() { }",
            "label: async function f() { }",
            "if (true) async function* f() { }",
            "label: async function* f() { }",
            null
        ];
        // Valid only in sloppy mode.
        string[] sloppy_data = [
            "if (true) function foo() {}",
            "if (false) {} else function f() { };",
            "label: function f() { }",
            "label: if (true) function f() { }",
            "label: if (true) {} else function f() { }",
            "label: label2: function f() { }",
            null
        ];
        // clang-format on
        // Nothing parses in strict mode without a SyntaxError
        RunParserSyncTest(strict_context, error_data, kError);
        RunParserSyncTest(strict_context, sloppy_data, kError);
        // In sloppy mode, sloppy_data is successful
        RunParserSyncTest(sloppy_context, error_data, kError);
        RunParserSyncTest(sloppy_context, sloppy_data, kSuccess);
    }

    [Fact]
    public void ExponentiationOperator()
    {
        // clang-format off
        string[][] context_data = [
            [ "var O = { p: 1 }, x = 10; ; if (", ") { foo(); }" ],
            [ "var O = { p: 1 }, x = 10; ; (", ")" ],
            [ "var O = { p: 1 }, x = 10; foo(", ")" ],
            [ null, null ]
        ];
        string[] data = [
            "(delete O.p) ** 10",
            "(delete x) ** 10",
            "(~O.p) ** 10",
            "(~x) ** 10",
            "(!O.p) ** 10",
            "(!x) ** 10",
            "(+O.p) ** 10",
            "(+x) ** 10",
            "(-O.p) ** 10",
            "(-x) ** 10",
            "(typeof O.p) ** 10",
            "(typeof x) ** 10",
            "(void 0) ** 10",
            "(void O.p) ** 10",
            "(void x) ** 10",
            "++O.p ** 10",
            "++x ** 10",
            "--O.p ** 10",
            "--x ** 10",
            "O.p++ ** 10",
            "x++ ** 10",
            "O.p-- ** 10",
            "x-- ** 10",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void ExponentiationOperatorErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "var O = { p: 1 }, x = 10; ; if (", ") { foo(); }" ],
            [ "var O = { p: 1 }, x = 10; ; (", ")" ],
            [ "var O = { p: 1 }, x = 10; foo(", ")" ],
            [ null, null ]
        ];
        string[] error_data = [
            "delete O.p ** 10",
            "delete x ** 10",
            "~O.p ** 10",
            "~x ** 10",
            "!O.p ** 10",
            "!x ** 10",
            "+O.p ** 10",
            "+x ** 10",
            "-O.p ** 10",
            "-x ** 10",
            "typeof O.p ** 10",
            "typeof x ** 10",
            "void ** 10",
            "void O.p ** 10",
            "void x ** 10",
            "++delete O.p ** 10",
            "--delete O.p ** 10",
            "++~O.p ** 10",
            "++~x ** 10",
            "--!O.p ** 10",
            "--!x ** 10",
            "++-O.p ** 10",
            "++-x ** 10",
            "--+O.p ** 10",
            "--+x ** 10",
            "[ x ] **= [ 2 ]",
            "[ x **= 2 ] = [ 2 ]",
            "{ x } **= { x: 2 }",
            "{ x: x **= 2 ] = { x: 2 }",
            // TODO(caitp): a Call expression as LHS should be an early ReferenceError!
            // "Array() **= 10",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, error_data, kError);
    }

    [Fact]
    public void AsyncAwait()
    {
        // clang-format off
        string[][] context_data = [
            [ "'use strict';", "" ],
            [ "", "" ],
            [ null, null ]
        ];
        string[] data = [
            "var asyncFn = async function() { await 1; };",
            "var asyncFn = async function withName() { await 1; };",
            "var asyncFn = async () => await 'test';",
            "var asyncFn = async x => await x + 'test';",
            "async function asyncFn() { await 1; }",
            "var O = { async method() { await 1; } }",
            "var O = { async ['meth' + 'od']() { await 1; } }",
            "var O = { async 'method'() { await 1; } }",
            "var O = { async 0() { await 1; } }",
            "async function await() {}",
            "var asyncFn = async({ foo = 1 }) => foo;",
            "var asyncFn = async({ foo = 1 } = {}) => foo;",
            "function* g() { var f = async(yield); }",
            "function* g() { var f = async(x = yield); }",
            // v8:7817 assert that `await` is still allowed in the body of an arrow fn
            // within formal parameters
            "async(a = a => { var await = 1; return 1; }) => a()",
            "async(a = await => 1); async(a) => 1",
            "(async(a = await => 1), async(a) => 1)",
            "async(a = await => 1, b = async() => 1);",
            "async (x = class { p = await }) => {};",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
        // clang-format off
        string[][] async_body_context_data = [
            [ "async function f() {", "}" ],
            [ "var f = async function() {", "}" ],
            [ "var f = async() => {", "}" ],
            [ "var O = { async method() {", "} }" ],
            [ "'use strict'; async function f() {", "}" ],
            [ "'use strict'; var f = async function() {", "}" ],
            [ "'use strict'; var f = async() => {", "}" ],
            [ "'use strict'; var O = { async method() {", "} }" ],
            [ null, null ]
        ];
        string[][] body_context_data = [
            [ "function f() {", "}" ],
            [ "function* g() {", "}" ],
            [ "var f = function() {", "}" ],
            [ "var g = function*() {", "}" ],
            [ "var O = { method() {", "} }" ],
            [ "var O = { *method() {", "} }" ],
            [ "var f = () => {", "}" ],
            [ "'use strict'; function f() {", "}" ],
            [ "'use strict'; function* g() {", "}" ],
            [ "'use strict'; var f = function() {", "}" ],
            [ "'use strict'; var g = function*() {", "}" ],
            [ "'use strict'; var O = { method() {", "} }" ],
            [ "'use strict'; var O = { *method() {", "} }" ],
            [ "'use strict'; var f = () => {", "}" ],
            [ null, null ]
        ];
        string[] body_data = [
            "var async = 1; return async;",
            "let async = 1; return async;",
            "const async = 1; return async;",
            "function async() {} return async();",
            "var async = async => async; return async();",
            "function foo() { var await = 1; return await; }",
            "function foo(await) { return await; }",
            "function* foo() { var await = 1; return await; }",
            "function* foo(await) { return await; }",
            "var f = () => { var await = 1; return await; }",
            "var O = { method() { var await = 1; return await; } };",
            "var O = { method(await) { return await; } };",
            "var O = { *method() { var await = 1; return await; } };",
            "var O = { *method(await) { return await; } };",
            "var asyncFn = async function*() {}",
            "async function* f() {}",
            "var O = { async *method() {} };",
            "(function await() {})",
            null
        ];
        // clang-format on
        RunParserSyncTest(async_body_context_data, body_data, kSuccess);
        RunParserSyncTest(body_context_data, body_data, kSuccess);
    }

    [Fact]
    public void AsyncAwaitErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "'use strict';", "" ],
            [ "", "" ],
            [ null, null ]
        ];
        string[][] strict_context_data = [
            [ "'use strict';", "" ],
            [ null, null ]
        ];
        string[] error_data = [
            "var asyncFn = async function await() {};",
            "var asyncFn = async () => var await = 'test';",
            "var asyncFn = async await => await + 'test';",
            "var asyncFn = async function(await) {};",
            "var asyncFn = async (await) => 'test';",
            "async function f(await) {}",
            "var O = { async method(a, a) {} }",
            "var O = { async ['meth' + 'od'](a, a) {} }",
            "var O = { async 'method'(a, a) {} }",
            "var O = { async 0(a, a) {} }",
            "var f = async() => await;",
            "var O = { *async method() {} };",
            "var O = { async method*() {} };",
            "var asyncFn = async function(x = await 1) { return x; }",
            "async function f(x = await 1) { return x; }",
            "var f = async(x = await 1) => x;",
            "var O = { async method(x = await 1) { return x; } };",
            "function* g() { var f = async yield => 1; }",
            "function* g() { var f = async(yield) => 1; }",
            "function* g() { var f = async(x = yield) => 1; }",
            "function* g() { var f = async({x = yield}) => 1; }",
            "class C { async constructor() {} }",
            "class C {}; class C2 extends C { async constructor() {} }",
            "class C { static async prototype() {} }",
            "class C {}; class C2 extends C { static async prototype() {} }",
            "var f = async() => ((async(x = await 1) => x)();",
            // Henrique Ferreiro's bug (tm)
            "(async function foo1() { } foo2 => 1)",
            "(async function foo3() { } () => 1)",
            "(async function foo4() { } => 1)",
            "(async function() { } foo5 => 1)",
            "(async function() { } () => 1)",
            "(async function() { } => 1)",
            "(async.foo6 => 1)",
            "(async.foo7 foo8 => 1)",
            "(async.foo9 () => 1)",
            "(async().foo10 => 1)",
            "(async().foo11 foo12 => 1)",
            "(async().foo13 () => 1)",
            "(async['foo14'] => 1)",
            "(async['foo15'] foo16 => 1)",
            "(async['foo17'] () => 1)",
            "(async()['foo18'] => 1)",
            "(async()['foo19'] foo20 => 1)",
            "(async()['foo21'] () => 1)",
            "(async`foo22` => 1)",
            "(async`foo23` foo24 => 1)",
            "(async`foo25` () => 1)",
            "(async`foo26`.bar27 => 1)",
            "(async`foo28`.bar29 foo30 => 1)",
            "(async`foo31`.bar32 () => 1)",
            // v8:5148 assert that errors are still thrown for calls that may have been
            // async functions
            "async({ foo33 = 1 })",
            "async(...a = b) => b",
            "async(...a,) => b",
            "async(...a, b) => b",
            // v8:7817 assert that `await` is an invalid identifier in arrow formal
            // parameters nested within an async arrow function
            "async(a = await => 1) => a",
            "async(a = (await) => 1) => a",
            "async(a = (...await) => 1) => a",
            null
        ];
        string[] strict_error_data = [
            "var O = { async method(eval) {} }",
            "var O = { async ['meth' + 'od'](eval) {} }",
            "var O = { async 'method'(eval) {} }",
            "var O = { async 0(eval) {} }",
            "var O = { async method(arguments) {} }",
            "var O = { async ['meth' + 'od'](arguments) {} }",
            "var O = { async 'method'(arguments) {} }",
            "var O = { async 0(arguments) {} }",
            "var O = { async method(dupe, dupe) {} }",
            // TODO(caitp): preparser needs to report duplicate parameter errors, too.
            // "var f = async(dupe, dupe) => {}",
            null
        ];
        RunParserSyncTest(context_data, error_data, kError);
        RunParserSyncTest(strict_context_data, strict_error_data, kError);
        // clang-format off
        string[][] async_body_context_data = [
            [ "async function f() {", "}" ],
            [ "var f = async function() {", "}" ],
            [ "var f = async() => {", "}" ],
            [ "var O = { async method() {", "} }" ],
            [ "'use strict'; async function f() {", "}" ],
            [ "'use strict'; var f = async function() {", "}" ],
            [ "'use strict'; var f = async() => {", "}" ],
            [ "'use strict'; var O = { async method() {", "} }" ],
            [ null, null ]
        ];
        string[] async_body_error_data = [
            "var await = 1;",
            "var { await } = 1;",
            "var [ await ] = 1;",
            "return async (await) => {};",
            "var O = { async [await](a, a) {} }",
            "await;",
            "function await() {}",
            "var f = await => 42;",
            "var f = (await) => 42;",
            "var f = (await, a) => 42;",
            "var f = (...await) => 42;",
            "var e = (await);",
            "var e = (await, f);",
            "var e = (await = 42)",
            "var e = [await];",
            "var e = {await};",
            null
        ];
        // clang-format on
        RunParserSyncTest(async_body_context_data, async_body_error_data, kError);
    }

    [Fact]
    public void Regress7173()
    {
        // Await expression is an invalid destructuring target, and should not crash
        // clang-format off
        string[][] error_context_data = [
            [ "'use strict'; async function f() {", "}" ],
            [ "async function f() {", "}" ],
            [ "'use strict'; function f() {", "}" ],
            [ "function f() {", "}" ],
            [ "let f = async() => {", "}" ],
            [ "let f = () => {", "}" ],
            [ "'use strict'; async function* f() {", "}" ],
            [ "async function* f() {", "}" ],
            [ "'use strict'; function* f() {", "}" ],
            [ "function* f() {", "}" ],
            [ null, null ]
        ];
        string[] error_data = [
            "var [await f] = [];",
            "let [await f] = [];",
            "const [await f] = [];",
            "var [...await f] = [];",
            "let [...await f] = [];",
            "const [...await f] = [];",
            "var { await f } = {};",
            "let { await f } = {};",
            "const { await f } = {};",
            "var { ...await f } = {};",
            "let { ...await f } = {};",
            "const { ...await f } = {};",
            "var { f: await f } = {};",
            "let { f: await f } = {};",
            "const { f: await f } = {};var { f: ...await f } = {};",
            "let { f: ...await f } = {};",
            "const { f: ...await f } = {};var { [f]: await f } = {};",
            "let { [f]: await f } = {};",
            "const { [f]: await f } = {};",
            "var { [f]: ...await f } = {};",
            "let { [f]: ...await f } = {};",
            "const { [f]: ...await f } = {};",
            null
        ];
        // clang-format on
        RunParserSyncTest(error_context_data, error_data, kError);
    }

    [Fact]
    public void AsyncAwaitFormalParameters()
    {
        // clang-format off
        string[][] context_for_formal_parameters = [
            [ "async function f(", ") {}" ],
            [ "var f = async function f(", ") {}" ],
            [ "var f = async(", ") => {}" ],
            [ "'use strict'; async function f(", ") {}" ],
            [ "'use strict'; var f = async function f(", ") {}" ],
            [ "'use strict'; var f = async(", ") => {}" ],
            [ null, null ]
        ];
        string[] good_formal_parameters = [
            "x = function await() {}",
            "x = function *await() {}",
            "x = function() { let await = 0; }",
            "x = () => { let await = 0; }",
            null
        ];
        string[] bad_formal_parameters = [
            "{ await }",
            "{ await = 1 }",
            "{ await } = {}",
            "{ await = 1 } = {}",
            "[await]",
            "[await] = []",
            "[await = 1]",
            "[await = 1] = []",
            "...await",
            "await",
            "await = 1",
            "...[await]",
            "x = await",
            // v8:5190
            "1) => 1",
            "'str') => 1",
            "/foo/) => 1",
            "{ foo = async(1) => 1 }) => 1",
            "{ foo = async(a) => 1 })",
            "x = async(await)",
            "x = { [await]: 1 }",
            "x = class extends (await) { }",
            "x = class { static [await]() {} }",
            "{ x = await }",
            // v8:6714
            "x = class await {}",
            "x = 1 ? class await {} : 0",
            "x = async function await() {}",
            "x = y[await]",
            "x = `${await}`",
            "x = y()[await]",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_for_formal_parameters, good_formal_parameters,
                                            kSuccess);
        RunParserSyncTest(context_for_formal_parameters, bad_formal_parameters,
                                            kError);
    }

    [Fact]
    public void AsyncAwaitModule()
    {
        // clang-format off
        string[][] context_data = [
            [ "", "" ],
            [ null, null ]
        ];
        string[] data = [
            "export default async function() { await 1; }",
            "export default async function async() { await 1; }",
            "export async function async() { await 1; }",
            null
        ];
        // clang-format on
        RunModuleParserSyncTest(context_data, data, kSuccess, null, 0, null, 0,
                                                        null, 0, false);
    }

    [Fact]
    public void AsyncAwaitModuleErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "", "" ],
            [ null, null ]
        ];
        string[] error_data = [
            "export default (async function await() {})",
            "export default async function await() {}",
            "export async function await() {}",
            "export async function() {}",
            "export async",
            "export async\nfunction async() { await 1; }",
            null
        ];
        // clang-format on
        RunModuleParserSyncTest(context_data, error_data, kError, null, 0, null,
                                                        0, null, 0, false);
    }

    [Fact]
    public void RestrictiveForInErrors()
    {
        // clang-format off
        string[][] strict_context_data = [
            [ "'use strict'", "" ],
            [ null, null ]
        ];
        string[][] sloppy_context_data = [
            [ "", "" ],
            [ null, null ]
        ];
        string[] error_data = [
            "for (const x = 0 in {});",
            "for (let x = 0 in {});",
            null
        ];
        string[] sloppy_data = [
            "for (var x = 0 in {});",
            null
        ];
        // clang-format on
        RunParserSyncTest(strict_context_data, error_data, kError);
        RunParserSyncTest(strict_context_data, sloppy_data, kError);
        RunParserSyncTest(sloppy_context_data, error_data, kError);
        RunParserSyncTest(sloppy_context_data, sloppy_data, kSuccess);
    }

    [Fact]
    public void NoDuplicateGeneratorsInBlock()
    {
        string[][] block_context_data = [
                ["'use strict'; {", "}"],
                ["{", "}"],
                ["(function() { {", "} })()"],
                ["(function() {'use strict'; {", "} })()"],
                [null, null]];
        string[][] top_level_context_data = [
                ["'use strict';", ""],
                ["", ""],
                ["(function() {", "})()"],
                ["(function() {'use strict';", "})()"],
                [null, null]];
        string[] error_data = ["function* x() {} function* x() {}",
                                                                "function x() {} function* x() {}",
                                                                "function* x() {} function x() {}", null];
        // The preparser doesn't enforce the restriction, so turn it off.
        bool test_preparser = false;
        RunParserSyncTest(block_context_data, error_data, kError, null, 0, null,
                                            0, null, 0, false, test_preparser);
        RunParserSyncTest(top_level_context_data, error_data, kSuccess);
    }

    [Fact]
    public void NoDuplicateAsyncFunctionInBlock()
    {
        string[][] block_context_data = [
                ["'use strict'; {", "}"],
                ["{", "}"],
                ["(function() { {", "} })()"],
                ["(function() {'use strict'; {", "} })()"],
                [null, null]];
        string[][] top_level_context_data = [
                ["'use strict';", ""],
                ["", ""],
                ["(function() {", "})()"],
                ["(function() {'use strict';", "})()"],
                [null, null]];
        string[] error_data = ["async function x() {} async function x() {}",
                                                                "function x() {} async function x() {}",
                                                                "async function x() {} function x() {}",
                                                                "function* x() {} async function x() {}",
                                                                "function* x() {} async function x() {}",
                                                                "async function x() {} function* x() {}",
                                                                "function* x() {} async function x() {}",
                                                                null];
        // The preparser doesn't enforce the restriction, so turn it off.
        bool test_preparser = false;
        RunParserSyncTest(block_context_data, error_data, kError, null, 0, null,
                                            0, null, 0, false, test_preparser);
        RunParserSyncTest(top_level_context_data, error_data, kSuccess);
    }

    [Fact]
    public void TrailingCommasInParameters()
    {
        // clang-format off
        string[][] context_data = [
            [ "", "" ],
            [ "'use strict';", "" ],
            [ "function foo() {", "}" ],
            [ "function foo() {'use strict';", "}" ],
            [ null, null ]
        ];
        string[] data = [
            " function  a(b,) {}",
            " function* a(b,) {}",
            "(function  a(b,) {});",
            "(function* a(b,) {});",
            "(function   (b,) {});",
            "(function*  (b,) {});",
            " function  a(b,c,d,) {}",
            " function* a(b,c,d,) {}",
            "(function  a(b,c,d,) {});",
            "(function* a(b,c,d,) {});",
            "(function   (b,c,d,) {});",
            "(function*  (b,c,d,) {});",
            "(b,) => {};",
            "(b,c,d,) => {};",
            "a(1,);",
            "a(1,2,3,);",
            "a(...[],);",
            "a(1, 2, ...[],);",
            "a(...[], 2, ...[],);",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kSuccess);
    }

    [Fact]
    public void TrailingCommasInParametersErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "", "" ],
            [ "'use strict';", "" ],
            [ "function foo() {", "}" ],
            [ "function foo() {'use strict';", "}" ],
            [ null, null ]
        ];
        string[] data = [
            // too many trailing commas
            " function  a(b,,) {}",
            " function* a(b,,) {}",
            "(function  a(b,,) {});",
            "(function* a(b,,) {});",
            "(function   (b,,) {});",
            "(function*  (b,,) {});",
            " function  a(b,c,d,,) {}",
            " function* a(b,c,d,,) {}",
            "(function  a(b,c,d,,) {});",
            "(function* a(b,c,d,,) {});",
            "(function   (b,c,d,,) {});",
            "(function*  (b,c,d,,) {});",
            "(b,,) => {};",
            "(b,c,d,,) => {};",
            "a(1,,);",
            "a(1,2,3,,);",
            // only a trailing comma and no parameters
            " function  a1(,) {}",
            " function* a2(,) {}",
            "(function  a3(,) {});",
            "(function* a4(,) {});",
            "(function    (,) {});",
            "(function*   (,) {});",
            "(,) => {};",
            "a1(,);",
            // no trailing commas after rest parameter declaration
            " function  a(...b,) {}",
            " function* a(...b,) {}",
            "(function  a(...b,) {});",
            "(function* a(...b,) {});",
            "(function   (...b,) {});",
            "(function*  (...b,) {});",
            " function  a(b, c, ...d,) {}",
            " function* a(b, c, ...d,) {}",
            "(function  a(b, c, ...d,) {});",
            "(function* a(b, c, ...d,) {});",
            "(function   (b, c, ...d,) {});",
            "(function*  (b, c, ...d,) {});",
            "(...b,) => {};",
            "(b, c, ...d,) => {};",
            // parenthesized trailing comma without arrow is still an error
            "(,);",
            "(a,);",
            "(a,b,c,);",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void ArgumentsRedeclaration()
    {
        {
            // clang-format off
            string[][] context_data = [
                [ "function f(", ") {}" ],
                [ null, null ]
            ];
            string[] success_data = [
                "{arguments}",
                "{arguments = false}",
                "arg1, arguments",
                "arg1, ...arguments",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, success_data, kSuccess);
        }
        {
            // clang-format off
            string[][] context_data = [
                [ "function f() {", "}" ],
                [ null, null ]
            ];
            string[] data = [
                "const arguments = 1",
                "let arguments",
                "var arguments",
                null
            ];
            // clang-format on
            RunParserSyncTest(context_data, data, kSuccess);
        }
    }

    [Fact]
    public void EscapedStrictReservedWord()
    {
        // Test that identifiers which are both escaped and only reserved in the
        // strict mode are accepted in non-strict mode.
        string[][] context_data = [["", ""], [null, null]];
        string[] statement_data = ["if (true) l\\u0065t: ;",
                                                                        "function l\\u0065t() { }",
                                                                        "(function l\\u0065t() { })",
                                                                        "async function l\\u0065t() { }",
                                                                        "(async function l\\u0065t() { })",
                                                                        "l\\u0065t => 42",
                                                                        "async l\\u0065t => 42",
                                                                        "function packag\\u0065() {}",
                                                                        "function impl\\u0065ments() {}",
                                                                        "function privat\\u0065() {}",
                                                                        null];
        RunParserSyncTest(context_data, statement_data, kSuccess);
    }

    [Fact]
    public void ForAwaitOf()
    {
        // clang-format off
        string[][] context_data = [
            [ "async function f() { for await ", " ; }" ],
            [ "async function f() { for await ", " { } }" ],
            [ "async function * f() { for await ", " { } }" ],
            [ "async function f() { 'use strict'; for await ", " ; }" ],
            [ "async function f() { 'use strict'; for await ", "  { } }" ],
            [ "async function * f() { 'use strict'; for await ", "  { } }" ],
            [ "async function f() { for\nawait ", " ; }" ],
            [ "async function f() { for\nawait ", " { } }" ],
            [ "async function * f() { for\nawait ", " { } }" ],
            [ "async function f() { 'use strict'; for\nawait ", " ; }" ],
            [ "async function f() { 'use strict'; for\nawait ", " { } }" ],
            [ "async function * f() { 'use strict'; for\nawait ", " { } }" ],
            [ "async function f() { for await\n", " ; }" ],
            [ "async function f() { for await\n", " { } }" ],
            [ "async function * f() { for await\n", " { } }" ],
            [ "async function f() { 'use strict'; for await\n", " ; }" ],
            [ "async function f() { 'use strict'; for await\n", " { } }" ],
            [ "async function * f() { 'use strict'; for await\n", " { } }" ],
            [ null, null ]
        ];
        string[][] context_data2 = [
            [ "async function f() { let a; for await ", " ; }" ],
            [ "async function f() { let a; for await ", " { } }" ],
            [ "async function * f() { let a; for await ", " { } }" ],
            [ "async function f() { 'use strict'; let a; for await ", " ; }" ],
            [ "async function f() { 'use strict'; let a; for await ", "  { } }" ],
            [ "async function * f() { 'use strict'; let a; for await ", "  { } }" ],
            [ "async function f() { let a; for\nawait ", " ; }" ],
            [ "async function f() { let a; for\nawait ", " { } }" ],
            [ "async function * f() { let a; for\nawait ", " { } }" ],
            [ "async function f() { 'use strict'; let a; for\nawait ", " ; }" ],
            [ "async function f() { 'use strict'; let a; for\nawait ", " { } }" ],
            [ "async function * f() { 'use strict'; let a; for\nawait ", " { } }" ],
            [ "async function f() { let a; for await\n", " ; }" ],
            [ "async function f() { let a; for await\n", " { } }" ],
            [ "async function * f() { let a; for await\n", " { } }" ],
            [ "async function f() { 'use strict'; let a; for await\n", " ; }" ],
            [ "async function f() { 'use strict'; let a; for await\n", " { } }" ],
            [ "async function * f() { 'use strict'; let a; for await\n", " { } }" ],
            [ null, null ]
        ];
        string[] expr_data = [
            // Primary Expressions
            "(a of [])",
            "(a.b of [])",
            "([a] of [])",
            "([a = 1] of [])",
            "([a = 1, ...b] of [])",
            "({a} of [])",
            "({a: a} of [])",
            "({'a': a} of [])",
            "({\"a\": a} of [])",
            "({[Symbol.iterator]: a} of [])",
            "({0: a} of [])",
            "({a = 1} of [])",
            "({a: a = 1} of [])",
            "({'a': a = 1} of [])",
            "({\"a\": a = 1} of [])",
            "({[Symbol.iterator]: a = 1} of [])",
            "({0: a = 1} of [])",
            null
        ];
        string[] var_data = [
            // VarDeclarations
            "(var a of [])",
            "(var [a] of [])",
            "(var [a = 1] of [])",
            "(var [a = 1, ...b] of [])",
            "(var {a} of [])",
            "(var {a: a} of [])",
            "(var {'a': a} of [])",
            "(var {\"a\": a} of [])",
            "(var {[Symbol.iterator]: a} of [])",
            "(var {0: a} of [])",
            "(var {a = 1} of [])",
            "(var {a: a = 1} of [])",
            "(var {'a': a = 1} of [])",
            "(var {\"a\": a = 1} of [])",
            "(var {[Symbol.iterator]: a = 1} of [])",
            "(var {0: a = 1} of [])",
            null
        ];
        string[] lexical_data = [
            // LexicalDeclartions
            "(let a of [])",
            "(let [a] of [])",
            "(let [a = 1] of [])",
            "(let [a = 1, ...b] of [])",
            "(let {a} of [])",
            "(let {a: a} of [])",
            "(let {'a': a} of [])",
            "(let {\"a\": a} of [])",
            "(let {[Symbol.iterator]: a} of [])",
            "(let {0: a} of [])",
            "(let {a = 1} of [])",
            "(let {a: a = 1} of [])",
            "(let {'a': a = 1} of [])",
            "(let {\"a\": a = 1} of [])",
            "(let {[Symbol.iterator]: a = 1} of [])",
            "(let {0: a = 1} of [])",
            "(const a of [])",
            "(const [a] of [])",
            "(const [a = 1] of [])",
            "(const [a = 1, ...b] of [])",
            "(const {a} of [])",
            "(const {a: a} of [])",
            "(const {'a': a} of [])",
            "(const {\"a\": a} of [])",
            "(const {[Symbol.iterator]: a} of [])",
            "(const {0: a} of [])",
            "(const {a = 1} of [])",
            "(const {a: a = 1} of [])",
            "(const {'a': a = 1} of [])",
            "(const {\"a\": a = 1} of [])",
            "(const {[Symbol.iterator]: a = 1} of [])",
            "(const {0: a = 1} of [])",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, expr_data, kSuccess);
        RunParserSyncTest(context_data2, expr_data, kSuccess);
        RunParserSyncTest(context_data, var_data, kSuccess);
        // TODO(marja): PreParser doesn't report early errors.
        //              (https://bugs.chromium.org/p/v8/issues/detail?id=2728)
        // RunParserSyncTest(context_data2, var_data, kError, nullptr, 0,
        // always_flags,
        //                   arraysize(always_flags));
        RunParserSyncTest(context_data, lexical_data, kSuccess);
        RunParserSyncTest(context_data2, lexical_data, kSuccess);
    }

    [Fact]
    public void ForAwaitOfFunctionDeclaration()
    {
        // clang-format off
        string[][] context_data = [
            [ "async function f() {", "}" ],
            [ "async function f() { 'use strict'; ", "}" ],
            [ null, null ]
        ];
        string[] data = [
            "for await (x of []) function d() {};",
            "for await (x of []) function d() {}; return d;",
            "for await (x of []) function* g() {};",
            "for await (x of []) function* g() {}; return g;",
            // TODO(caitp): handle async function declarations in ParseScopedStatement.
            // "for await (x of []) async function a() {};",
            // "for await (x of []) async function a() {}; return a;",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, data, kError);
    }

    [Fact]
    public void AsyncGeneratorErrors()
    {
        // clang-format off
        string[][] context_data = [
            [ "async function * gen() {", "}" ],
            [ "\"use strict\"; async function * gen() {", "}" ],
            [ null, null ]
        ];
        string[] statement_data = [
            // Invalid yield expressions inside generators.
            "var yield;",
            "var await;",
            "var foo, yield;",
            "var foo, await;",
            "try { } catch (yield) { }",
            "try { } catch (await) { }",
            "function yield() { }",
            "function await() { }",
            // The name of the NFE is bound in the generator, which does not permit
            // yield or await to be identifiers.
            "(async function * yield() { })",
            "(async function * await() { })",
            // Yield and Await aren't valid as a formal parameter for generators.
            "async function * foo(yield) { }",
            "(async function * foo(yield) { })",
            "async function * foo(await) { }",
            "(async function * foo(await) { })",
            "yield = 1;",
            "await = 1;",
            "var foo = yield = 1;",
            "var foo = await = 1;",
            "++yield;",
            "++await;",
            "yield++;",
            "await++;",
            "yield *",
            "(yield *)",
            // Yield binds very loosely, so this parses as "yield (3 + yield 4)", which
            // is invalid.
            "yield 3 + yield 4;",
            "yield: 34",
            "yield ? 1 : 2",
            // Parses as yield (/ yield): invalid.
            "yield / yield",
            "+ yield",
            "+ yield 3",
            // Invalid (no newline allowed between yield and *).
            "yield\n*3",
            // Invalid (we see a newline, so we parse {yield:42} as a statement, not an
            // object literal, and yield is not a valid label).
            "yield\n{yield: 42}",
            "yield /* comment */\n {yield: 42}",
            "yield //comment\n {yield: 42}",
            // Destructuring binding and assignment are both disallowed
            "var [yield] = [42];",
            "var [await] = [42];",
            "var {foo: yield} = {a: 42};",
            "var {foo: await} = {a: 42};",
            "[yield] = [42];",
            "[await] = [42];",
            "({a: yield} = {a: 42});",
            "({a: await} = {a: 42});",
            // Also disallow full yield/await expressions on LHS
            "var [yield 24] = [42];",
            "var [await 24] = [42];",
            "var {foo: yield 24} = {a: 42};",
            "var {foo: await 24} = {a: 42};",
            "[yield 24] = [42];",
            "[await 24] = [42];",
            "({a: yield 24} = {a: 42});",
            "({a: await 24} = {a: 42});",
            "for (yield 'x' in {});",
            "for (await 'x' in {});",
            "for (yield 'x' of {});",
            "for (await 'x' of {});",
            "for (yield 'x' in {} in {});",
            "for (await 'x' in {} in {});",
            "for (yield 'x' in {} of {});",
            "for (await 'x' in {} of {});",
            "class C extends yield { }",
            "class C extends await { }",
            null
        ];
        // clang-format on
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void PrivateNamesSyntaxErrorEarly()
    {
        string[][] context_data = [
                ["", ""], ["\"use strict\";", ""], [null, null]];
        string[] statement_data = [
                "class A {  foo() { return this.#bar; }}",
                "let A = class {  foo() { return this.#bar; }}",
                "class A {  #foo;    bar() { return this.#baz; }}",
                "let A = class {  #foo;    bar() { return this.#baz; }}",
                "class A {  bar() {    class D { #baz = 1; };    return this.#baz;  }}",
                "let A = class {  bar() {    class D { #baz = 1; };    return this.#baz;  }}",
                "a.#bar",
                "class Foo {};Foo.#bar;",
                "let Foo = class {};Foo.#bar;",
                "class Foo {};(new Foo).#bar;",
                "let Foo = class {};(new Foo).#bar;",
                "class Foo { #bar; };(new Foo).#bar;",
                "let Foo = class { #bar; };(new Foo).#bar;",
                "function t(){  class Foo { getA() { return this.#foo; } }}",
                "function t(){  return class { getA() { return this.#foo; } }}",
                null];
        RunParserSyncTest(context_data, statement_data, kError);
    }

    [Fact]
    public void HashbangSyntax()
    {
        string[][] context_data = [
                ["#!\n", ""],
                ["#!---IGNORED---\n", ""],
                ["#!---IGNORED---\r", ""],
                ["#!---IGNORED---\u2028", ""], // <U+2028>
                ["#!---IGNORED---\u2029", ""], // <U+2029>
                [null, null]];
        string[] data = ["function\nFN\n(\n)\n {\n}\nFN();", null];
        RunParserSyncTest(context_data, data, kSuccess);
        RunParserSyncTest(context_data, data, kSuccess, null, 0, null, 0,
                                            null, 0, true);
    }

    // Not ported: these tests run JavaScript or inspect heap objects, which the
    // parser alone cannot do.
}
