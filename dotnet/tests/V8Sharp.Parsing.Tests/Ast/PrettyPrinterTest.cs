// Tests for the port of src/ast/prettyprinter.cc and Scope::Print. The
// CallPrinter output is checked against the oracle: V8 renders the callee of a
// failed call with CallPrinter in "... is not a function" messages.

using V8Sharp.Ast;
using V8Sharp.Oracle;

namespace V8Sharp.Parsing.Tests.Ast;

public class PrettyPrinterTest
{
    private static ParseInfo Parse(string source)
    {
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForTest().set_allow_lazy_parsing(false);
        ParseInfo info = new(flags);
        Assert.True(ParsingEntry.ParseProgram(info, new SourceScript(source)));
        return info;
    }

    private sealed class CallFinder(AstNode root) : AstTraversalVisitor(root)
    {
        public readonly List<int> positions = [];

        protected override bool VisitNode(AstNode node)
        {
            if (node is ExpressionStatement) positions.Clear();
            if (node is Call or CallNew) positions.Add(node.position());
            return true;
        }
    }

    private static readonly Lock s_oracleLock = new();

    private static string OracleCallee(string source, string suffix)
    {
        string output;
        lock (s_oracleLock)
        {
            using var v8 = new ReferenceV8(allowNativesSyntax: false);
            output = v8.Run(source);
        }
        const string prefix = "Uncaught TypeError: ";
        int start = output.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, output);
        string message = output[(start + prefix.Length)..].TrimEnd('\n');
        Assert.EndsWith(suffix, message);
        return message[..^suffix.Length];
    }

    [Theory]
    [InlineData("var o = {}; o.f();", " is not a function")]
    [InlineData("var o = {a: {}}; o.a.b(1, 2);", " is not a function")]
    [InlineData("var o = {}; o['x' + 1]();", " is not a function")]
    [InlineData("var a = [1]; a[0]();", " is not a function")]
    [InlineData("var x = 1; x();", " is not a function")]
    [InlineData("var o = {}; new o.C();", " is not a constructor")]
    [InlineData("var o = {}; o?.f();", " is not a function")]
    [InlineData("var o = {}; o.a?.b.c;", "")]
    [InlineData("var f = function() { return 1; }; f()();", " is not a function")]
    [InlineData("var o = {g() { return {}; }}; o.g().h();", " is not a function")]
    [InlineData("var o = {}; (o.p || o.q)();", " is not a function")]
    [InlineData("var o = {}; o[1.5]();", " is not a function")]
    [InlineData("var o = {}; o[null]();", " is not a function")]
    [InlineData("var o = {}; o[`a${1}b`]();", " is not a function")]
    [InlineData("var o = {}; o[/x/g]();", " is not a function")]
    [InlineData("var o = {}; this.o.p();", " is not a function")]
    [InlineData("var o = {}; o[-1]();", " is not a function")]
    [InlineData("var o = {}; o[typeof o]();", " is not a function")]
    [InlineData("var o = {}; o[[1, 2]]();", " is not a function")]
    [InlineData("var o = {}; o[{}]();", " is not a function")]
    [InlineData("var o = {}; o[o ? 1 : 2]();", " is not a function")]
    [InlineData("var o = {}; o[1n]();", " is not a function")]
    [InlineData("var i = 0; var o = {}; o[i++]();", " is not a function")]
    public void CallPrinterMatchesOracle(string source, string suffix)
    {
        if (suffix.Length == 0)
        {
            // Not a call error: nothing to compare, but the printer must not crash.
            ParseInfo parsed = Parse(source);
            new CallPrinter(true).Print(parsed.literal()!, 0);
            return;
        }
        string expected = OracleCallee(source, suffix);
        ParseInfo info = Parse(source);
        var finder = new CallFinder(info.literal()!);
        finder.Run();
        // The failing call is the outermost call in the last statement.
        int position = finder.positions[0];
        var printer = new CallPrinter(true);
        Assert.Equal(expected, printer.Print(info.literal()!, position));
        Assert.Equal(CallPrinter.ErrorHint.kNone, printer.GetErrorHint());
    }

    [Fact]
    public void CallPrinterHidesNonUserVariableNames()
    {
        ParseInfo info = Parse("var o = {}; o.f();");
        var finder = new CallFinder(info.literal()!);
        finder.Run();
        Assert.Equal("(var).f", new CallPrinter(false).Print(info.literal()!, finder.positions[^1]));
    }

    [Fact]
    public void CallPrinterIteratorErrorHint()
    {
        ParseInfo info = Parse("var x = 1; for (var y of x) {}");
        int position = "var x = 1; for (var y of ".Length;
        var printer = new CallPrinter(true);
        Assert.Equal("x", printer.Print(info.literal()!, position));
        Assert.Equal(CallPrinter.ErrorHint.kNormalIterator, printer.GetErrorHint());
    }

    [Fact]
    public void AstPrinterPrintsProgram()
    {
        ParseInfo info = Parse("var x = 1 + y; function f(a) { return a; }");
        string text = new AstPrinter().PrintProgram(info.literal()!);
        TestContext.Current.TestOutputHelper!.WriteLine(text);
        Assert.StartsWith("FUNC at 0\n", text, StringComparison.Ordinal);
        Assert.Contains(". KIND 0\n", text, StringComparison.Ordinal);
        Assert.Contains(". LITERAL ID 0\n", text, StringComparison.Ordinal);
        Assert.Contains(". SUSPEND COUNT 0\n", text, StringComparison.Ordinal);
        Assert.Contains(". NAME \"\"\n", text, StringComparison.Ordinal);
        Assert.Contains(". DECLS\n", text, StringComparison.Ordinal);
        Assert.Contains(". . FUNCTION \"f\" = function f\n", text, StringComparison.Ordinal);
        Assert.Contains("EXPRESSION STATEMENT at", text, StringComparison.Ordinal);
        Assert.Contains("kAdd at 10\n", text, StringComparison.Ordinal);
        Assert.Contains("LITERAL 1\n", text, StringComparison.Ordinal);
        Assert.Contains("VAR PROXY unallocated (0x", text, StringComparison.Ordinal);
        Assert.Contains(") (mode = DYNAMIC_GLOBAL, assigned = false) \"y\"\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(1e21, "1e+21")]
    [InlineData(123456789.0, "1.23457e+08")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(100000.0, "100000")]
    [InlineData(1000000.0, "1e+06")]
    [InlineData(-2.5, "-2.5")]
    public void AstPrinterFormatsHeapNumbersLikePrintfG(double value, string expected)
    {
        Assert.Equal(expected, AstPrinter.FormatG(value));
    }

    [Fact]
    public void ScopePrintShowsAllocation()
    {
        ParseInfo info = Parse("function f(a) { let b = a; return () => b; } f(1);");
        string text = info.literal()!.scope().Print();
        TestContext.Current.TestOutputHelper!.WriteLine(text);
        Assert.StartsWith("global { // (", text, StringComparison.Ordinal);
        Assert.Contains("  function f (a) { // (", text, StringComparison.Ordinal);
        Assert.Contains("// local vars:\n", text, StringComparison.Ordinal);
        Assert.Contains("VAR f;  // (", text, StringComparison.Ordinal);
        Assert.Contains("LET b;  // (", text, StringComparison.Ordinal);
        Assert.Contains(") context[", text, StringComparison.Ordinal);
        Assert.Contains("arrow () { // (", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }
}
