// Port of test/unittests/asmjs/asm-scanner-unittest.cc of V8 14.7.
using V8Sharp.AsmJs;
using static V8Sharp.AsmJs.AsmToken;

namespace V8Sharp.AsmJs.Tests;

public class AsmScannerTests
{
    AsmJsScanner scanner = null!;

    void SetupScanner(string source) => scanner = new AsmJsScanner(source, 0, source.Length);

    void Skip(int t)
    {
        Assert.Equal(t, scanner.Token);
        scanner.Next();
    }

    void SkipGlobal()
    {
        Assert.True(scanner.IsGlobal());
        scanner.Next();
    }

    void SkipLocal()
    {
        Assert.True(scanner.IsLocal());
        scanner.Next();
    }

    void CheckForEnd() => Assert.Equal(kEndOfInput, scanner.Token);

    void CheckForParseError() => Assert.Equal(kParseError, scanner.Token);

    [Fact]
    public void SimpleFunction()
    {
        SetupScanner("function foo() { return; }");
        Skip(kToken_function);
        Assert.Equal("foo", scanner.GetIdentifierString());
        SkipGlobal();
        Skip('(');
        Skip(')');
        Skip('{');
        Skip(kToken_return);
        Skip(';');
        Skip('}');
        CheckForEnd();
    }

    [Fact]
    public void JSKeywords()
    {
        SetupScanner(
            "arguments break case const continue\n" +
            "default do else eval for function\n" +
            "if new return switch var while\n");
        Skip(kToken_arguments);
        Skip(kToken_break);
        Skip(kToken_case);
        Skip(kToken_const);
        Skip(kToken_continue);
        Skip(kToken_default);
        Skip(kToken_do);
        Skip(kToken_else);
        Skip(kToken_eval);
        Skip(kToken_for);
        Skip(kToken_function);
        Skip(kToken_if);
        Skip(kToken_new);
        Skip(kToken_return);
        Skip(kToken_switch);
        Skip(kToken_var);
        Skip(kToken_while);
        CheckForEnd();
    }

    void CheckOperators()
    {
        Skip('+');
        Skip('-');
        Skip('*');
        Skip('/');
        Skip('%');
        Skip('&');
        Skip('|');
        Skip('^');
        Skip('~');
        Skip(kToken_SHL);
        Skip(kToken_SAR);
        Skip(kToken_SHR);
        Skip('<');
        Skip('>');
        Skip(kToken_LE);
        Skip(kToken_GE);
        Skip(kToken_EQ);
        Skip(kToken_NE);
        CheckForEnd();
    }

    [Fact]
    public void JSOperatorsSpread()
    {
        SetupScanner(
            "+ - * / % & | ^ ~ << >> >>>\n" +
            "< > <= >= == !=\n");
        CheckOperators();
    }

    [Fact]
    public void JSOperatorsTight()
    {
        SetupScanner(
            "+-*/%&|^~<<>> >>>\n" +
            "<><=>= ==!=\n");
        CheckOperators();
    }

    [Fact]
    public void UsesOfAsm()
    {
        SetupScanner("'use asm' \"use asm\"\n");
        Skip(kToken_UseAsm);
        Skip(kToken_UseAsm);
        CheckForEnd();
    }

    [Fact]
    public void DefaultGlobalScope()
    {
        SetupScanner("var x = x + x;");
        Skip(kToken_var);
        Assert.Equal("x", scanner.GetIdentifierString());
        int x = scanner.Token;
        SkipGlobal();
        Skip('=');
        Skip(x);
        Skip('+');
        Skip(x);
        Skip(';');
        CheckForEnd();
    }

    [Fact]
    public void GlobalScope()
    {
        SetupScanner("var x = x + x;");
        scanner.EnterGlobalScope();
        Skip(kToken_var);
        Assert.Equal("x", scanner.GetIdentifierString());
        int x = scanner.Token;
        SkipGlobal();
        Skip('=');
        Skip(x);
        Skip('+');
        Skip(x);
        Skip(';');
        CheckForEnd();
    }

    [Fact]
    public void LocalScope()
    {
        SetupScanner("var x = x + x;");
        scanner.EnterLocalScope();
        Skip(kToken_var);
        Assert.Equal("x", scanner.GetIdentifierString());
        int x = scanner.Token;
        SkipLocal();
        Skip('=');
        Skip(x);
        Skip('+');
        Skip(x);
        Skip(';');
        CheckForEnd();
    }

    [Fact]
    public void Numbers()
    {
        SetupScanner("1 1.2 0x1F 1.e3");

        Assert.True(scanner.IsUnsigned());
        Assert.Equal(1u, scanner.AsUnsigned());
        scanner.Next();

        Assert.True(scanner.IsDouble());
        Assert.Equal(1.2, scanner.AsDouble());
        scanner.Next();

        Assert.True(scanner.IsUnsigned());
        Assert.Equal(31u, scanner.AsUnsigned());
        scanner.Next();

        Assert.True(scanner.IsDouble());
        Assert.Equal(1.0e3, scanner.AsDouble());
        scanner.Next();

        CheckForEnd();
    }

    [Fact]
    public void UnsignedNumbers()
    {
        SetupScanner("0x7FFFFFFF 0x80000000 0xFFFFFFFF 0x100000000");

        Assert.True(scanner.IsUnsigned());
        Assert.Equal(0x7FFFFFFFu, scanner.AsUnsigned());
        scanner.Next();

        Assert.True(scanner.IsUnsigned());
        Assert.Equal(0x80000000u, scanner.AsUnsigned());
        scanner.Next();

        Assert.True(scanner.IsUnsigned());
        Assert.Equal(0xFFFFFFFFu, scanner.AsUnsigned());
        scanner.Next();

        // Numeric "unsigned" literals with a payload of more than 32-bit are rejected
        // by asm.js in all contexts, we hence consider `0x100000000` to be an error.
        CheckForParseError();
    }

    [Fact]
    public void BadNumber()
    {
        SetupScanner(".123fe");
        Skip('.');
        CheckForParseError();
    }

    [Fact]
    public void Rewind1()
    {
        SetupScanner("+ - * /");
        Skip('+');
        scanner.Rewind();
        Skip('+');
        Skip('-');
        scanner.Rewind();
        Skip('-');
        Skip('*');
        scanner.Rewind();
        Skip('*');
        Skip('/');
        scanner.Rewind();
        Skip('/');
        CheckForEnd();
    }

    [Fact]
    public void Comments()
    {
        SetupScanner(
            "var // This is a test /* */ eval\n" +
            "var /* test *** test */ eval\n" +
            "function /* this */ ^");
        Skip(kToken_var);
        Skip(kToken_var);
        Skip(kToken_eval);
        Skip(kToken_function);
        Skip('^');
        CheckForEnd();
    }

    [Fact]
    public void TrailingCComment()
    {
        SetupScanner("var /* test\n");
        Skip(kToken_var);
        CheckForParseError();
    }

    [Fact]
    public void Seeking()
    {
        SetupScanner("var eval do arguments function break\n");
        Skip(kToken_var);
        int oldPos = scanner.Position;
        Skip(kToken_eval);
        Skip(kToken_do);
        Skip(kToken_arguments);
        scanner.Rewind();
        Skip(kToken_arguments);
        scanner.Rewind();
        scanner.Seek(oldPos);
        Skip(kToken_eval);
        Skip(kToken_do);
        Skip(kToken_arguments);
        Skip(kToken_function);
        Skip(kToken_break);
        CheckForEnd();
    }

    [Fact]
    public void Newlines()
    {
        SetupScanner(
            "var x = 1\n" +
            "var y = 2\n");
        Skip(kToken_var);
        scanner.Next();
        Skip('=');
        scanner.Next();
        Assert.True(scanner.IsPrecededByNewline());
        Skip(kToken_var);
        scanner.Next();
        Skip('=');
        scanner.Next();
        Assert.True(scanner.IsPrecededByNewline());
        CheckForEnd();
    }
}
