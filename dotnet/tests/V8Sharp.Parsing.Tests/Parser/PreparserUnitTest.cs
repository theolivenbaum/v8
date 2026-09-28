// Port of test/unittests/parser/preparser-unittest.cc.
//
// V8 runs the scripts and reads preparse data off the SharedFunctionInfo of
// the function the script returns. Without an engine, the tests here play the
// compiler's part: a function is "compiled" by parsing it lazily
// (ParsingEntry.ParseFunction) with the PreparseData its FunctionLiteral
// produced when the enclosing code was parsed, which is what V8 stores in
// UncompiledDataWithPreparseData, with the outer ScopeInfo chain a managed
// ScopeInfo (TestScopeInfo) builds from the enclosing parse.

using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing.Tests.Parser;

public class PreParserTest
{
    [Flags]
    public enum SkipTests
    {
        DONT_SKIP = 0,
        // Skip if the test function declares itself strict, otherwise don't skip.
        SKIP_STRICT_FUNCTION = 1,
        // Skip if there's a "use strict" directive above the test.
        SKIP_STRICT_OUTER = 1 << 1,
        SKIP_ARROW = 1 << 2,
        SKIP_STRICT = SKIP_STRICT_FUNCTION | SKIP_STRICT_OUTER,
    }

    internal const int kScriptId = 1;

    internal static UnoptimizedCompileFlags.ScriptDetails Details() =>
        new(kScriptId, true, LanguageMode.Sloppy, false, false, false, false, false);

    internal sealed class LazyFunction(SourceScript source_script, FunctionLiteral literal) : IParsingSharedFunctionInfo
    {
        public readonly FunctionLiteral literal = literal;
        public readonly PreparseData? preparse_data = literal.produced_preparse_data()?.Serialize();
        // SharedFunctionInfo::outer_scope_info: the ScopeInfo of the closest
        // outer scope with a context.
        private readonly IScopeInfo? outer_scope_info = literal.scope().GetOuterScopeWithContext()?.scope_info();

        public IParsingScript script() => source_script;
        public int StartPosition() => literal.start_position();
        public int EndPosition() => literal.end_position();
        public bool HasOuterScopeInfo() => outer_scope_info != null;
        public IScopeInfo GetOuterScopeInfo() => outer_scope_info!;
        public bool is_wrapped() => false;
        public int function_literal_id() => literal.function_literal_id();
        public string Name() => literal.raw_name()?.ToFlatString() ?? "";
        public bool private_name_lookup_skips_outer_class() => literal.private_name_lookup_skips_outer_class();
    }

    private sealed class LiteralCollector(AstNode root) : AstTraversalVisitor(root)
    {
        public readonly List<FunctionLiteral> literals = [];

        public override void VisitFunctionLiteral(FunctionLiteral expr)
        {
            literals.Add(expr);
            base.VisitFunctionLiteral(expr);
        }
    }

    internal static List<FunctionLiteral> CollectLiterals(FunctionLiteral root)
    {
        var collector = new LiteralCollector(root);
        // Visit the declarations and body only: the root itself is not an
        // inner literal.
        collector.VisitDeclarations(root.scope().declarations());
        collector.VisitStatements(root.body());
        return collector.literals;
    }

    internal static ParseInfo ParseScript(SourceScript script)
    {
        UnoptimizedCompileFlags flags =
            UnoptimizedCompileFlags.ForScriptCompile(ParsingFlags.Default, Details());
        ParseInfo info = new(flags);
        info.set_scope_info_provider(TestScopeInfoProvider.Instance);
        Assert.True(ParsingEntry.ParseProgram(info, script), "script failed to parse");
        DeclarationScope.AllocateScopeInfos(info, TestScopeInfoProvider.Instance);
        return info;
    }

    // Lazily compiles |function|: with its preparse data when |use_data|.
    internal static ParseInfo CompileLazily(LazyFunction function, bool use_data)
    {
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForFunctionCompile(
            ParsingFlags.Default, UnoptimizedCompileFlags.DetailsOf(function.literal), Details());
        flags.set_is_lazy_compile(true);
        ParseInfo info = new(flags);
        info.set_scope_info_provider(TestScopeInfoProvider.Instance);
        if (use_data) info.set_consumed_preparse_data(ConsumedPreparseData.For(function.preparse_data));
        Assert.True(ParsingEntry.ParseFunction(info, function), "lazy function failed to parse");
        DeclarationScope.AllocateScopeInfos(info, TestScopeInfoProvider.Instance);
        return info;
    }

    // The innermost lazily parsed function literal of |literals| whose body
    // contains |position|.
    private static FunctionLiteral? InnermostLazyContaining(List<FunctionLiteral> literals, int position)
    {
        FunctionLiteral? result = null;
        foreach (FunctionLiteral literal in literals)
        {
            if (literal.ShouldEagerCompile()) continue;
            if (literal.start_position() > position || literal.end_position() <= position) continue;
            if (result == null || literal.start_position() >= result.start_position()) result = literal;
        }
        return result;
    }

    [Fact]
    public void LazyFunctionLength()
    {
        var script = new SourceScript("function lazy(a, b, c) { } lazy", kScriptId);
        ParseInfo info = ParseScript(script);
        List<FunctionLiteral> literals = CollectLiterals(info.literal()!);
        Assert.Single(literals);
        Assert.False(literals[0].ShouldEagerCompile());
        Assert.Equal(3, literals[0].function_length());
    }

    private readonly record struct Outer(string code, bool strict_outer, bool strict_test_function, bool arrow);

    private readonly record struct Inner(string @params, string source, SkipTests skip, bool precise_maybe_assigned,
                                         bool bailout_if_outer_sloppy);

    private static readonly Outer[] outers =
    [
        // Normal case (test function at the laziness boundary):
        new("function test({0}) {{ {1} function skippable() {{ }} }} test;", false, false, false),

        new("var test2 = function test({0}) {{ {1} function skippable() {{ }} }}; test2", false, false, false),

        // Arrow functions (they can never be at the laziness boundary):
        new("function test() {{ ({0}) => {{ {1} }}; function skippable() {{ }} }} test;", false, false, true),

        // Repeat the above mentioned cases with global 'use strict'
        new("'use strict'; function test({0}) {{ {1} function skippable() {{ }} }} test;", true, false, false),

        new("'use strict'; var test2 = function test({0}) {{ {1} \nfunction skippable() {{ }} }}; test2", true,
            false, false),

        new("'use strict'; function test() {{ ({0}) => {{ {1} }};\nfunction skippable() {{ }} }} test;", true,
            false, true),

        // ... and with the test function declaring itself strict:
        new("function test({0}) {{ 'use strict'; {1} function skippable() {{ }} }} test;", false, true, false),

        new("var test2 = function test({0}) {{ 'use strict'; {1} \nfunction skippable() {{ }} }}; test2", false,
            true, false),

        new("function test() {{ 'use strict'; ({0}) => {{ {1} }};\nfunction skippable() {{ }} }} test;", false,
            true, true),

        // Methods containing skippable functions.
        new("function get_method() {{\n" +
            "  class MyClass {{ test_method({0}) {{ {1} function skippable() {{ }} }} }}\n" +
            "  var o = new MyClass(); return o.test_method;\n" +
            "}}\n" +
            "get_method();",
            true, true, false),

        // Corner case: function expression with name "arguments".
        new("var test = function arguments({0}) {{ {1} function skippable() {{ }} }};\ntest;\n", false, false,
            false),
    ];

    private static readonly Inner[] inners =
    [
        new("", "var1;", SkipTests.DONT_SKIP, true, false),
        new("", "var1 = 5;", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) {}", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() {}", SkipTests.DONT_SKIP, true, false),
        new("", "test;", SkipTests.DONT_SKIP, true, false),
        new("", "test2;", SkipTests.DONT_SKIP, true, false),
        new("", "var var1;", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var1 = 5;", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { var var1; }", SkipTests.DONT_SKIP, false, false),
        new("", "if (true) { var var1; var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var1 = 5; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; function f() { var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { f2(); } function f2() {}", SkipTests.DONT_SKIP, true, false),
        new("", "let var1;", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; var1 = 5;", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { let var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { let var1; var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; var1 = 5; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; function f() { var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = 5;", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { const var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = 5; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { let var2; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = function f1() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = function f1() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = function f1() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = function() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = function() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = function() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "function *f1() { let var2; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = function *f1() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = function*() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "async function f1() { let var2; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = async function f1() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = async function() { let var2; };", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var var1;", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var var1; var1 = 5;", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; if (true) { var var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { var var1; var var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; if (true) { var var1; var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { var var1; var var1; var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var var1; function f() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; var var1; function f() { var1 = 5; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; if (true) { var var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; if (true) { let var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; if (true) { let var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; if (true) { const var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = 0; if (true) { const var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { if (true) { function f() { var var1 = 5; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "arguments;", SkipTests.DONT_SKIP, true, false),
        new("", "arguments = 5;", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { arguments; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "() => { arguments; };", SkipTests.DONT_SKIP, true, false),
        new("var1, var2, var3", "arguments;", SkipTests.DONT_SKIP, true, false),
        new("var1, var2, var3", "arguments = 5;", SkipTests.SKIP_STRICT, true, false),
        new("var1, var2, var3", "() => { arguments; };", SkipTests.DONT_SKIP, true, false),
        new("var1, var2, var3", "() => { arguments = 5; };", SkipTests.SKIP_STRICT, true, false),
        new("", "this;", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { this; }", SkipTests.DONT_SKIP, true, false),
        new("", "() => { this; };", SkipTests.DONT_SKIP, true, false),
        new("", "var arguments;", SkipTests.SKIP_STRICT, true, false),
        new("", "var arguments; arguments = 5;", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { var arguments; }", SkipTests.SKIP_STRICT, false, false),
        new("", "if (true) { var arguments; arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "var arguments; function f() { arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "var arguments; arguments = 5; function f() { arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "var arguments; function f() { arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "let arguments;", SkipTests.SKIP_STRICT, true, false),
        new("", "let arguments; arguments = 5;", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { let arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { let arguments; arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "let arguments; function f() { arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "let arguments; arguments = 5; function f() { arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "let arguments; function f() { arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "const arguments = 5;", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { const arguments = 5; }", SkipTests.SKIP_STRICT, true, false),
        new("", "const arguments = 5; function f() { arguments; }", SkipTests.SKIP_STRICT, true, false),
        new("", "var [var1, var2] = [1, 2];", SkipTests.DONT_SKIP, true, false),
        new("", "var [var1, var2, [var3, var4]] = [1, 2, [3, 4]];", SkipTests.DONT_SKIP, true, false),
        new("", "var [{var1: var2}, {var3: var4}] = [{var1: 1}, {var3: 2}];", SkipTests.DONT_SKIP, true, false),
        new("", "var [var1, ...var2] = [1, 2, 3];", SkipTests.DONT_SKIP, true, false),
        new("", "var {var1: var2, var3: var4} = {var1: 1, var3: 2};", SkipTests.DONT_SKIP, true, false),
        new("", "var {var1: var2, var3: {var4: var5}} = {var1: 1, var3: {var4: 2}};", SkipTests.DONT_SKIP, true, false),
        new("", "var {var1: var2, var3: [var4, var5]} = {var1: 1, var3: [2, 3]};", SkipTests.DONT_SKIP, true, false),
        new("", "let [var1, var2] = [1, 2];", SkipTests.DONT_SKIP, true, false),
        new("", "let [var1, var2, [var3, var4]] = [1, 2, [3, 4]];", SkipTests.DONT_SKIP, true, false),
        new("", "let [{var1: var2}, {var3: var4}] = [{var1: 1}, {var3: 2}];", SkipTests.DONT_SKIP, true, false),
        new("", "let [var1, ...var2] = [1, 2, 3];", SkipTests.DONT_SKIP, true, false),
        new("", "let {var1: var2, var3: var4} = {var1: 1, var3: 2};", SkipTests.DONT_SKIP, true, false),
        new("", "let {var1: var2, var3: {var4: var5}} = {var1: 1, var3: {var4: 2}};", SkipTests.DONT_SKIP, true, false),
        new("", "let {var1: var2, var3: [var4, var5]} = {var1: 1, var3: [2, 3]};", SkipTests.DONT_SKIP, true, false),
        new("", "const [var1, var2] = [1, 2];", SkipTests.DONT_SKIP, true, false),
        new("", "const [var1, var2, [var3, var4]] = [1, 2, [3, 4]];", SkipTests.DONT_SKIP, true, false),
        new("", "const [{var1: var2}, {var3: var4}] = [{var1: 1}, {var3: 2}];", SkipTests.DONT_SKIP, true, false),
        new("", "const [var1, ...var2] = [1, 2, 3];", SkipTests.DONT_SKIP, true, false),
        new("", "const {var1: var2, var3: var4} = {var1: 1, var3: 2};", SkipTests.DONT_SKIP, true, false),
        new("", "const {var1: var2, var3: {var4: var5}} = {var1: 1, var3: {var4: 2}};", SkipTests.DONT_SKIP, true, false),
        new("", "const {var1: var2, var3: [var4, var5]} = {var1: 1, var3: [2, 3]};", SkipTests.DONT_SKIP, true, false),
        new("", "test;", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { f1; }", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { function f2() { f1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "function arguments() {}", SkipTests.SKIP_STRICT, true, false),
        new("", "function f1() {} function f1() {}", SkipTests.SKIP_STRICT, true, false),
        new("", "var f1; function f1() {}", SkipTests.DONT_SKIP, true, false),
        new("", "test = 3;", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { f1 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "function f1() { f1; } f1 = 3;", SkipTests.DONT_SKIP, true, false),
        new("", "function arguments() {} arguments = 8;", SkipTests.SKIP_STRICT, true, false),
        new("", "function f1() {} f1 = 3; function f1() {}", SkipTests.SKIP_STRICT, true, false),
        new("", "var var1; eval('');", SkipTests.DONT_SKIP, true, false),
        new("", "var var1; function f1() { eval(''); }", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; eval('');", SkipTests.DONT_SKIP, true, false),
        new("", "let var1; function f1() { eval(''); }", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = 10; eval('');", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = 10; function f1() { eval(''); }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 = 0; var1 < 10; ++var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 = 0; var1 < 10; ++var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 = 0; var1 < 10; ++var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 = 0; var1 < 10; ++var1) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 = 0; var1 < 10; ++var1) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 = 0; var1 < 10; ++var1) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 of [1, 2]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 of [1, 2]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 of [1, 2]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 of [1, 2]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 of [1, 2]) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 of [1, 2]) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 of [1, 2]) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 of [1, 2]) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 of [1, 2]) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 of [1, 2]) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 of [1, 2]) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 of [1, 2]) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 of [1, 2]) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 of [1, 2]) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 of [1, 2]) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 of [1, 2]) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 of [1, 2]) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 of [1, 2]) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 of [1, 2]) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 of [1, 2]) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { var1 = 0; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const var1 in {a: 6}) { function foo() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for ([var1, var2] of [[1, 1], [2, 2]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var [var1, var2] of [[1, 1], [2, 2]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2] of [[1, 1], [2, 2]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const [var1, var2] of [[1, 1], [2, 2]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for ([var1, var2] of [[1, 1], [2, 2]]) { var2 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var [var1, var2] of [[1, 1], [2, 2]]) { var2 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2] of [[1, 1], [2, 2]]) { var2 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const [var1, var2] of [[1, 1], [2, 2]]) { var2 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "for ([var1, var2] of [[1, 1], [2, 2]]) { () => { var2 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (var [var1, var2] of [[1, 1], [2, 2]]) { () => { var2 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2] of [[1, 1], [2, 2]]) { () => { var2 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (const [var1, var2] of [[1, 1], [2, 2]]) { () => { var2 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var2; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1; var2; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1 = 0; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var2 = 0; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1 = 0; var2 = 0; }] of [[1]]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var1; var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var1 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var2 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { }] of [[1]]) { function f() { var1 = 0; var2 = 0; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1; }] of [[1]]) { function f() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1; }] of [[1]]) { function f() { var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var1; }] of [[1]]) { function f() { var1; var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var2; }] of [[1]]) { function f() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var2; }] of [[1]]) { function f() { var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "for (let [var1, var2 = function() { var2; }] of [[1]]) { function f() { var1; var2; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for ( ; var1 < 2; ++var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for ( ; var1 < 2; ++var1) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for ( ; var1 > 2; ) { }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for ( ; var1 > 2; ) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for ( ; var1 > 2; ) { function foo() { var1 = 6; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for(var1; var1 < 2; ++var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for (var1; var1 < 2; ++var1) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for (var1; var1 > 2; ) { }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for (var1; var1 > 2; ) { function foo() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = 0; for (var1; var1 > 2; ) { function foo() { var1 = 6; } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} function f1() {} }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { if (true) { function f1() {} } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { if (true) { function f1() {} function f1() {} } }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { function f1() {} f1 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} function foo() { f1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} } function foo() { f1; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} function f1() {} function foo() { f1; } }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { function f1() {} function f1() {} } function foo() { f1; }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { if (true) { function f1() {} } function foo() { f1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { if (true) { function f1() {} function f1() {} } function foo() { f1; } }", SkipTests.SKIP_STRICT, true, false),
        new("", "if (true) { function f1() {} f1 = 3; function foo() { f1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} f1 = 3; } function foo() { f1; }", SkipTests.DONT_SKIP, true, false),
        new("", "var f1 = 1; if (true) { function f1() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "var f1 = 1; if (true) { function f1() {} } function foo() { f1; }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function f1() {} function f2() { f1(); } }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { function *f1() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "if (true) { async function f1() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { if (true) { function var1() {} } }", SkipTests.DONT_SKIP, true, false),
        new("var1", "", SkipTests.DONT_SKIP, true, false),
        new("var1", "var1;", SkipTests.DONT_SKIP, true, false),
        new("var1", "var1 = 9;", SkipTests.DONT_SKIP, true, false),
        new("var1", "function f1() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("var1", "function f1() { var1 = 9; }", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "var2;", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "var2 = 9;", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "function f1() { var2; }", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "function f1() { var2 = 9; }", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "var1;", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "var1 = 9;", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "function f1() { var1; }", SkipTests.DONT_SKIP, true, false),
        new("var1, var2", "function f1() { var1 = 9; }", SkipTests.DONT_SKIP, true, false),
        new("var1, var1", "", (SkipTests.SKIP_STRICT | SkipTests.SKIP_ARROW), true, false),
        new("var1, var1", "var1;", (SkipTests.SKIP_STRICT | SkipTests.SKIP_ARROW), true, false),
        new("var1, var1", "var1 = 9;", (SkipTests.SKIP_STRICT | SkipTests.SKIP_ARROW), true, false),
        new("var1, var1", "function f1() { var1; }", (SkipTests.SKIP_STRICT | SkipTests.SKIP_ARROW), true, false),
        new("var1, var1", "function f1() { var1 = 9; }", (SkipTests.SKIP_STRICT | SkipTests.SKIP_ARROW), true, false),
        new("...var2", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("...var2", "var2;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("...var2", "var2 = 9;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("...var2", "function f1() { var2; }", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("...var2", "function f1() { var2 = 9; }", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...var2", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...var2", "var2;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...var2", "var2 = 9;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...var2", "function f1() { var2; }", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...var2", "function f1() { var2 = 9; }", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1 = 3", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1, var2 = var1", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1, var2 = 4, ...var3", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("[]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1]", "var1;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "var1;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "name1;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1}", "var1;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1]", "var1 = 16;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "var1 = 16;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "name1 = 16;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1}", "var1 = 16;", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1]", "() => { var1; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "() => { var1; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1}", "() => { name1; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1}", "() => { var1; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1, var2, var3]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1, name2: var2, name3: var3}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1, var2, var3}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1, var2, var3]", "() => { var2 = 16;};", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1, name2: var2, name3: var3}", "() => { var2 = 16;};", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{name1: var1, name2: var2, name3: var3}", "() => { name2 = 16;};", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1, var2, var3}", "() => { var2 = 16;};", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1, [var2, var3], {var4, name5: [var5, var6]}]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, [var2], var3 = 24, [var4, var5] = [2, 4], var6, {var7}, var8, {name9: var9, name10: var10}, ...var11", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1 = {} = {}", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1, ...[var2]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...[var2]", "() => { var2; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...{0: var2}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...{0: var2}", "() => { var2; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...[]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...{}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...[var2, var3]", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, ...{0: var2, 1: var3}", "", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("[var1, var2] = [2, 4]", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("{var1, var2} = {var1: 3, var2: 3}", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("[var1 = 4, var2 = var1]", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("{var1 = 4, var2 = var1}", "", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1, var2", "var var1 = 16; () => { var1 = 17; };", SkipTests.DONT_SKIP, true, false),
        new("[var1, var2]", "var var1 = 16; () => { var1 = 17; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("{var1, var2}", "var var1 = 16; () => { var1 = 17; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, var2, ...var3", "var var3 = 16; () => { var3 = 17; };", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("var1, var2 = var1", "var var1 = 16; () => { var1 = 17; };", SkipTests.SKIP_STRICT_FUNCTION, false, false),
        new("var1, var2", "for (;;) { function var1() { } }", SkipTests.DONT_SKIP, false, false),
        new("var1, var2 = eval(''), var3", "let var4 = 0;", SkipTests.SKIP_STRICT_FUNCTION, true, true),
        new("var1, var2 = eval(''), var3 = eval('')", "let var4 = 0;", SkipTests.SKIP_STRICT_FUNCTION, true, true),
        new("var1, var2 = (var3, var4 = eval(''), var5) => { let var6; }, var7", "let var8 = 0;", SkipTests.SKIP_STRICT_FUNCTION, true, true),
        new("var1 = 1, var2 = 2", "eval('');", SkipTests.SKIP_STRICT_FUNCTION, true, false),
        new("", "try { } catch(var1) { }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { var1; }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { var1 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { function f() { var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { function f() { var1 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch({var1, var2}) { function f() { var1 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch([var1, var2]) { function f() { var1 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch({}) { }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch([]) { }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { var var1 = 3; }", SkipTests.DONT_SKIP, true, false),
        new("", "try { } catch(var1) { var var1 = 3; function f() { var1 = 3; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass {}", SkipTests.DONT_SKIP, true, false),
        new("", "var1 = class MyClass {};", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = class MyClass {};", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = class MyClass {};", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = class MyClass {};", SkipTests.DONT_SKIP, true, false),
        new("", "var var1 = class {};", SkipTests.DONT_SKIP, true, false),
        new("", "let var1 = class {};", SkipTests.DONT_SKIP, true, false),
        new("", "const var1 = class {};", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { constructor() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { constructor() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { constructor() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { constructor() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { m() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { m() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { m() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { m() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { static m() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { static m() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { static m() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass { static m() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyBase {} class MyClass extends MyBase {}", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { constructor() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { constructor() { super(); } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { constructor() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { constructor() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { constructor() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { m() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { m() { super.foo; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { m() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { m() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { m() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { static m() {} }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { static m() { super.foo; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { static m() { var var1; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { static m() { var var1 = 11; } }", SkipTests.DONT_SKIP, true, false),
        new("", "class MyClass extends MyBase { static m() { var var1; function foo() { var1 = 11; } } }", SkipTests.DONT_SKIP, true, false),
        new("", "class X { ['bar'] = 1; }; new X;", SkipTests.DONT_SKIP, true, false),
        new("", "class X { static ['foo'] = 2; }; new X;", SkipTests.DONT_SKIP, true, false),
        new("", "class X { ['bar'] = 1; static ['foo'] = 2; }; new X;", SkipTests.DONT_SKIP, true, false),
        new("", "class X { #x = 1 }; new X;", SkipTests.DONT_SKIP, true, false),
        new("", "function t() { return class { #x = 1 }; } new t();", SkipTests.DONT_SKIP, true, false),
    ];

    [Fact]
    public void PreParserScopeAnalysis()
    {
        int checked_count = 0;
        foreach (Outer outer in outers)
        {
            foreach (Inner inner in inners)
            {
                if (outer.strict_outer && (inner.skip & SkipTests.SKIP_STRICT_OUTER) != 0) continue;
                if (outer.strict_test_function && (inner.skip & SkipTests.SKIP_STRICT_FUNCTION) != 0) continue;
                if (outer.arrow && (inner.skip & SkipTests.SKIP_ARROW) != 0) continue;

                string program = string.Format(System.Globalization.CultureInfo.InvariantCulture, outer.code,
                                               inner.@params, inner.source);
                try
                {
                    CheckScopeAnalysis(program, outer, inner);
                }
                catch (Exception e)
                {
                    throw new Xunit.Sdk.XunitException(program + "\n" + e.Message, e);
                }
                checked_count++;
            }
        }
        Assert.True(checked_count > 3000, checked_count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void CheckScopeAnalysis(string program, Outer outer, Inner inner)
    {
        var script = new SourceScript(program, kScriptId);
        int skippable_position = program.IndexOf("function skippable", StringComparison.Ordinal);

        // "Run" the script: find the function the script evaluates to, lazily
        // compiling the functions around it (get_method) on the way.
        ParseInfo toplevel = ParseScript(script);
        FunctionLiteral? candidate = InnermostLazyContaining(CollectLiterals(toplevel.literal()!),
                                                            skippable_position);
        Assert.NotNull(candidate);
        LazyFunction function = new(script, candidate);
        while (true)
        {
            ParseInfo compiled = CompileLazily(function, true);
            FunctionLiteral? inner_candidate =
                InnermostLazyContaining(CollectLiterals(compiled.literal()!), skippable_position);
            if (inner_candidate == null) break;
            function = new LazyFunction(script, inner_candidate);
        }

        if (inner.bailout_if_outer_sloppy && !outer.strict_outer)
        {
            Assert.Null(function.preparse_data);
            return;
        }

        Assert.NotNull(function.preparse_data);

        // Parse the lazy function using the scope data.
        ParseInfo using_scope_data = CompileLazily(function, true);

        // Verify that we skipped at least one function inside that scope.
        DeclarationScope scope_with_skipped_functions = using_scope_data.literal()!.scope();
        Assert.True(ScopeTestHelper.HasSkippedFunctionInside(scope_with_skipped_functions));

        // Parse the lazy function again eagerly to produce baseline data.
        ParseInfo not_using_scope_data = CompileLazily(function, false);

        // Verify that we didn't skip anything (there's no preparsed scope data,
        // so we cannot skip).
        DeclarationScope scope_without_skipped_functions = not_using_scope_data.literal()!.scope();
        Assert.False(ScopeTestHelper.HasSkippedFunctionInside(scope_without_skipped_functions));

        // Verify that scope allocation gave the same results when parsing w/ the
        // scope data (and skipping functions), and when parsing without.
        ScopeTestHelper.CompareScopes(scope_without_skipped_functions, scope_with_skipped_functions,
                                      inner.precise_maybe_assigned);
    }

    // Regression test for
    // https://bugs.chromium.org/p/chromium/issues/detail?id=753896. Should not
    // crash.
    [Fact]
    public void Regress753896()
    {
        // We don't assert that parsing succeeded or that it failed; currently the
        // error is not detected inside lazy functions, but it might be in the future.
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForScriptCompile(ParsingFlags.Default, Details());
        ParseInfo info = new(flags);
        ParsingEntry.ParseProgram(
            info, new SourceScript("function lazy() { let v = 0; if (true) { var v = 0; } }", kScriptId));
    }

    [Fact]
    public void TopLevelArrowFunctions()
    {
        const string kSource = """

                var a = () => { return 4; };
                var b = (() => { return 4; });
                var c = x => x + 2;
                var d = (x => x + 2);
                var e = (x, y, z) => x + y + z;
                var f = ((x, y, z) => x + y + z);
                // Functions declared within default parameters are also top-level.
                var g = (x = (y => y * 2)) => { return x; };
                var h = ((x = y => y * 2) => { return x; });
                var i = (x = (y) => 0) => { return x; };

            """;
        var script = new SourceScript(kSource, kScriptId);
        ParseInfo info = ParseScript(script);
        List<FunctionLiteral> literals = CollectLiterals(info.literal()!);

        // A function is compiled with the script when its literal is eagerly
        // compiled.
        FunctionLiteral Named(string name)
        {
            foreach (FunctionLiteral literal in literals)
            {
                if (literal.raw_inferred_name()?.ToFlatString() == name) return literal;
            }
            throw new InvalidOperationException(name);
        }
        bool IsCompiled(string name) => Named(name).ShouldEagerCompile();

        Assert.False(IsCompiled("a"));
        Assert.True(IsCompiled("b"));
        Assert.False(IsCompiled("c"));
        Assert.True(IsCompiled("d"));
        Assert.False(IsCompiled("e"));
        Assert.True(IsCompiled("f"));
        Assert.False(IsCompiled("g"));
        Assert.True(IsCompiled("h"));
        Assert.False(IsCompiled("i"));

        // g(), h() and i() return the arrow function of their default
        // parameter; for g it is compiled with g, for h and i it is not.
        bool IsInnerCompiled(string name)
        {
            FunctionLiteral outer = Named(name);
            List<FunctionLiteral> inner_literals;
            if (outer.ShouldEagerCompile())
            {
                inner_literals = CollectLiterals(outer);
            }
            else
            {
                ParseInfo compiled = CompileLazily(new LazyFunction(script, outer), true);
                inner_literals = CollectLiterals(compiled.literal()!);
            }
            Assert.Single(inner_literals);
            return inner_literals[0].ShouldEagerCompile();
        }
        Assert.True(IsInnerCompiled("g"));
        Assert.False(IsInnerCompiled("h"));
        Assert.False(IsInnerCompiled("i"));
    }

    [Fact]
    public void ProducingAndConsumingByteData()
    {
        var buffer = new List<byte>();
        var bytes = new PreparseDataBuilder.ByteData();
        bytes.Start(buffer);

        bytes.Reserve(32);
        bytes.Reserve(32);
        Assert.Equal(32, buffer.Count);
        const int kBufferSize = 64;
        bytes.Reserve(kBufferSize);
        Assert.Equal(kBufferSize, buffer.Count);

        // Write some data. (The byte format is V8's release-build one, without
        // the DEBUG size marker.)
        bytes.WriteVarint32(1983);
        bytes.WriteVarint32(2147483647);
        bytes.WriteUint8(4);
        bytes.WriteUint8(255);
        bytes.WriteVarint32(0);
        bytes.WriteUint8(0);
        bytes.WriteUint8(100);
        // Write quarter bytes between uint8s and uint32s to verify they're stored
        // correctly.
        bytes.WriteQuarter(3);
        bytes.WriteQuarter(0);
        bytes.WriteQuarter(2);
        bytes.WriteQuarter(1);
        bytes.WriteQuarter(0);
        bytes.WriteUint8(50);

        bytes.WriteQuarter(0);
        bytes.WriteQuarter(1);
        bytes.WriteQuarter(2);
        bytes.WriteQuarter(3);
        bytes.WriteVarint32(50);

        // End with a lonely quarter.
        bytes.WriteQuarter(0);
        bytes.WriteQuarter(1);
        bytes.WriteQuarter(2);
        bytes.WriteVarint32(0xff);

        // End with a lonely quarter.
        bytes.WriteQuarter(2);

        Assert.Equal(64, buffer.Count);
        const int kDataSize = 21;
        Assert.Equal(kDataSize, bytes.length());
        Assert.Equal(kBufferSize, buffer.Count);

        // Copy buffer for sanity checks later-on.
        var copied_buffer = new List<byte>(buffer);

        // Move the data from the temporary buffer into the zone for later
        // serialization.
        bytes.FinalizeData();
        Assert.Empty(buffer);
        Assert.Equal(kBufferSize, copied_buffer.Count);

        // V8 serializes both into the zone and onto the heap; the port has one
        // PreparseData for both.
        PreparseData data = bytes.CopyToZone(0);
        Assert.Equal(kDataSize, data.data_length());
        Assert.Equal(0, data.children_length());
        var bytes_for_reading = new ConsumedPreparseData.ByteData();
        bytes_for_reading.SetData(data.scope_data());

        for (int i = 0; i < kDataSize; i++)
        {
            Assert.Equal(copied_buffer[i], data.get(i));
        }

        Assert.Equal(1983, bytes_for_reading.ReadVarint32());
        Assert.Equal(2147483647, bytes_for_reading.ReadVarint32());
        Assert.Equal(4, bytes_for_reading.ReadUint8());
        Assert.Equal(255, bytes_for_reading.ReadUint8());
        Assert.Equal(0, bytes_for_reading.ReadVarint32());
        Assert.Equal(0, bytes_for_reading.ReadUint8());
        Assert.Equal(100, bytes_for_reading.ReadUint8());

        Assert.Equal(3, bytes_for_reading.ReadQuarter());
        Assert.Equal(0, bytes_for_reading.ReadQuarter());
        Assert.Equal(2, bytes_for_reading.ReadQuarter());
        Assert.Equal(1, bytes_for_reading.ReadQuarter());
        Assert.Equal(0, bytes_for_reading.ReadQuarter());
        Assert.Equal(50, bytes_for_reading.ReadUint8());

        Assert.Equal(0, bytes_for_reading.ReadQuarter());
        Assert.Equal(1, bytes_for_reading.ReadQuarter());
        Assert.Equal(2, bytes_for_reading.ReadQuarter());
        Assert.Equal(3, bytes_for_reading.ReadQuarter());
        Assert.Equal(50, bytes_for_reading.ReadVarint32());

        Assert.Equal(0, bytes_for_reading.ReadQuarter());
        Assert.Equal(1, bytes_for_reading.ReadQuarter());
        Assert.Equal(2, bytes_for_reading.ReadQuarter());
        Assert.Equal(0xff, bytes_for_reading.ReadVarint32());

        Assert.Equal(2, bytes_for_reading.ReadQuarter());
        // We should have consumed all data at this point.
        Assert.False(bytes_for_reading.HasRemainingBytes(1));
    }
}
