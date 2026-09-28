// Port of test/unittests/parser/parse-decision-unittest.cc.
//
// V8 compiles the script and reads is_compiled() off the SharedFunctionInfos
// of the top-level functions; a function is compiled with the script exactly
// when its FunctionLiteral is eagerly compiled (ShouldEagerCompile).

#nullable disable

using V8Sharp.Ast;

namespace V8Sharp.Parsing.Tests.Parser;

public class ParseDecisionTest
{
    // Record the 'compiled' state of all top level functions.
    private static Dictionary<string, bool> TopLevelFunctionInfo(string source)
    {
        ParseInfo info = PreParserTest.ParseScript(new SourceScript(source, PreParserTest.kScriptId));
        var is_compiled = new Dictionary<string, bool>();
        foreach (FunctionLiteral literal in PreParserTest.CollectLiterals(info.literal()))
        {
            is_compiled[literal.raw_name()?.ToFlatString() ?? ""] = literal.ShouldEagerCompile();
        }
        return is_compiled;
    }

    [Fact]
    public void GetTopLevelFunctionInfo()
    {
        const string src = "function foo() { var a; }\n";
        Dictionary<string, bool> is_compiled = TopLevelFunctionInfo(src);

        // Test that our helper function GetTopLevelFunctionInfo does what it claims:
        Assert.True(is_compiled.ContainsKey("foo"));
        Assert.False(is_compiled.ContainsKey("bar"));
    }

    [Fact]
    public void EagerlyCompileImmediateUseFunctions()
    {
        // Test parenthesized, exclaimed, and regular functions. Make sure these
        // occur both intermixed and after each other, to make sure the 'reset'
        // mechanism works.
        const string src =
            "function normal() { var a; }\n" +             // Normal: Should lazy parse.
            "(function parenthesized() { var b; })()\n" +  // Parenthesized: Pre-parse.
            "!function exclaimed() { var c; }() \n" +      // Exclaimed: Pre-parse.
            "function normal2() { var d; }\n" +
            "(function parenthesized2() { var e; })()\n" +
            "function normal3() { var f; }\n" +
            "!function exclaimed2() { var g; }() \n" +
            "function normal4() { var h; }\n";

        Dictionary<string, bool> is_compiled = TopLevelFunctionInfo(src);

        Assert.True(is_compiled["parenthesized"]);
        Assert.True(is_compiled["parenthesized2"]);
        Assert.True(is_compiled["exclaimed"]);
        Assert.True(is_compiled["exclaimed2"]);
        Assert.False(is_compiled["normal"]);
        Assert.False(is_compiled["normal2"]);
        Assert.False(is_compiled["normal3"]);
        Assert.False(is_compiled["normal4"]);
    }

    [Fact]
    public void CommaFunctionSequence()
    {
        const string src = "!function a(){}(),function b(){}(),function c(){}();";
        Dictionary<string, bool> is_compiled = TopLevelFunctionInfo(src);

        Assert.True(is_compiled["a"]);
        Assert.True(is_compiled["b"]);
        Assert.True(is_compiled["c"]);
    }
}
