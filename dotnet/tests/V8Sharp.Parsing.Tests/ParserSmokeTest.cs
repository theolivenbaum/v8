using V8Sharp.Ast;
using V8Sharp.Parsing;

namespace V8Sharp.Parsing.Tests;

public class ParserSmokeTest
{
    private static ParseInfo Parse(string source, bool module = false, bool lazy = true)
    {
        UnoptimizedCompileFlags flags = UnoptimizedCompileFlags.ForTest().set_is_module(module)
            .set_allow_lazy_parsing(lazy);
        ParseInfo info = new(flags);
        ParsingEntry.ParseProgram(info, new SourceScript(source));
        return info;
    }

    [Theory]
    [InlineData("var x = 1 + 2;")]
    [InlineData("function f(a, b) { return a + b; } f(1, 2);")]
    [InlineData("let {a, b: [c, ...d]} = o; const e = (x, y = 2, ...z) => x * y;")]
    [InlineData("class A extends B { #x = 1; static #y; get z() { return this.#x; } static { this.#y = 2; } }")]
    [InlineData("async function* g() { for await (const x of y) yield* x; await 1; }")]
    [InlineData("label: for (let i = 0; i < 10; i++) { if (i) continue label; else break; }")]
    [InlineData("try { throw new Error('x') } catch ({message}) { } finally { }")]
    [InlineData("switch (x) { case 1: let y; break; default: }")]
    [InlineData("var t = tag`a${1}b${2}c`, u = `x${y}z`;")]
    [InlineData("a?.b?.[c]?.(d); x ??= y; x ||= z; x &&= w; 2 ** 3;")]
    [InlineData("(function () { var x; (() => x)(); })(); with (o) { p; }")]
    public void ParsesValidScripts(string source)
    {
        ParseInfo info = Parse(source);
        Assert.False(info.pending_error_handler().has_pending_error(),
                     info.pending_error_handler().has_pending_error()
                         ? info.pending_error_handler().FormatErrorMessageForTest()
                         : "");
        Assert.NotNull(info.literal());
    }

    [Theory]
    [InlineData("var x = ;", "Unexpected token ';'")]
    [InlineData("let let = 1;", "let is disallowed as a lexically bound name")]
    [InlineData("function f() { break; }", "Illegal break statement")]
    [InlineData("'use strict'; with (o) {}", "Strict mode code may not include a with statement")]
    [InlineData("x = function() { super.x; }", "'super' keyword unexpected here")]
    public void ReportsErrors(string source, string message)
    {
        ParseInfo info = Parse(source);
        Assert.True(info.pending_error_handler().has_pending_error());
        Assert.Equal(message, info.pending_error_handler().FormatErrorMessageForTest());
    }

    [Fact]
    public void ParsesModule()
    {
        ParseInfo info = Parse("import a, {b as c} from 'x'; export default function () {} export {c}; await 1;",
                               module: true);
        Assert.False(info.pending_error_handler().has_pending_error(),
                     info.pending_error_handler().has_pending_error()
                         ? info.pending_error_handler().FormatErrorMessageForTest()
                         : "");
        Assert.NotNull(info.literal());
    }
}
