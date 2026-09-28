// Tests of the RegExp builtins (Builtins.RegExp*.cs, Runtime.Regexp.cs,
// Objects/JSRegExp.cs), calling the intrinsics directly. Expected values are
// V8's.
namespace V8Sharp.Tests.Builtins;

public class RegExpBuiltinsTest : BuiltinsTestBase
{
    [Fact]
    public void InitializeFromLiteral()
    {
        // The API the CreateRegExpLiteral bytecode uses.
        var regexp = (JSRegExp)factory.NewJSObject(isolate.NativeContext.RegExpFunction);
        JSRegExp.Initialize(isolate, regexp, factory.NewStringFromUtf16("a/b"), factory.NewStringFromUtf16("gi"));
        Assert.Equal("a\\/b", regexp.Source.Flatten());
        Assert.Equal(V8Sharp.RegExp.RegExpFlags.Global | V8Sharp.RegExp.RegExpFlags.IgnoreCase, regexp.Flags);
        Assert.Equal(0.0, Num(Get(regexp, "lastIndex")));
        Assert.Equal("gi", Str(Get(regexp, "flags")));
        Assert.Equal("/a\\/b/gi", Str(ReCall(regexp, "toString")));
    }

    [Fact]
    public void ConstructorErrors()
    {
        AssertThrows("SyntaxError", () => Re("(", ""), "Invalid regular expression: /(/: Unterminated group");
        AssertThrows("SyntaxError", () => Re("a", "gg"), "Invalid flags supplied to RegExp constructor 'gg'");
        AssertThrows("SyntaxError", () => Re("a", "uv"), "Invalid flags supplied to RegExp constructor 'uv'");
        AssertThrows("SyntaxError", () => Re("a", "l"));
    }

    [Fact]
    public void ConstructorFromRegExp()
    {
        JSValue re = Re("ab+", "gi");
        JSValue regexpFun = Global("RegExp");
        // RegExp(re) returns re itself when constructor matches and flags are undefined.
        Assert.Same(re.Object, Execution.Call(isolate, regexpFun, JSValue.Undefined, [re]).Object);
        JSValue copy = Execution.New(isolate, regexpFun, [re, S("y")]);
        Assert.Equal("/ab+/y", Str(ReCall(copy, "toString")));
        JSValue empty = Execution.New(isolate, regexpFun, []);
        Assert.Equal("(?:)", Str(Get(empty, "source")));
        Assert.Equal("(?:)", Str(Get(RegExpProto, "source")));
        Assert.True(Get(RegExpProto, "global").IsUndefined);
        Assert.Equal("", Str(Get(RegExpProto, "flags")));
    }

    [Fact]
    public void Exec()
    {
        JSValue re = Re("(\\d+)-(?<word>[a-z]+)?", "");
        JSValue result = ReCall(re, "exec", S("xx12-ab 34-"));
        Assert.Equal("[\"12-ab\",\"12\",\"ab\"]", Show(result));
        Assert.Equal(2.0, Num(Get(result, "index")));
        Assert.Equal("xx12-ab 34-", Str(Get(result, "input")));
        Assert.Equal("ab", Str(Get(Get(result, "groups"), "word")));
        Assert.True(ReCall(re, "exec", S("none")).IsNull);
        // Unmatched captures are undefined.
        Assert.Equal("[\"34-\",\"34\",undefined]", Show(ReCall(re, "exec", S("34-"))));
    }

    [Fact]
    public void ExecGlobalAndSticky()
    {
        JSValue re = Re("a", "g");
        Assert.Equal(1.0, Num(Get(ReCall(re, "exec", S("baac")), "index")));
        Assert.Equal(2.0, Num(Get(re, "lastIndex")));
        Assert.Equal(2.0, Num(Get(ReCall(re, "exec", S("baac")), "index")));
        Assert.True(ReCall(re, "exec", S("baac")).IsNull);
        Assert.Equal(0.0, Num(Get(re, "lastIndex")));

        JSValue sticky = Re("a", "y");
        Assert.True(ReCall(sticky, "test", S("ba")).IsFalse);
        Set(sticky, "lastIndex", N(1));
        Assert.True(ReCall(sticky, "test", S("ba")).IsTrue);
        Assert.Equal(2.0, Num(Get(sticky, "lastIndex")));
    }

    [Fact]
    public void HasIndices()
    {
        JSValue re = Re("a(?<Z>b)?", "d");
        JSValue result = ReCall(re, "exec", S("xab"));
        JSValue indices = Get(result, "indices");
        Assert.Equal("[[1,3],[2,3]]", Show(indices).Replace("\"", ""));
        Assert.Equal("[2,3]", Show(Get(Get(indices, "groups"), "Z")));
    }

    [Fact]
    public void LegacyStatics()
    {
        JSValue re = Re("(b)(c)", "");
        ReCall(re, "exec", S("abcd"));
        JSValue regexpFun = Global("RegExp");
        Assert.Equal("b", Str(Get(regexpFun, "$1")));
        Assert.Equal("c", Str(Get(regexpFun, "$2")));
        Assert.Equal("", Str(Get(regexpFun, "$3")));
        Assert.Equal("bc", Str(Get(regexpFun, "lastMatch")));
        Assert.Equal("c", Str(Get(regexpFun, "lastParen")));
        Assert.Equal("a", Str(Get(regexpFun, "leftContext")));
        Assert.Equal("d", Str(Get(regexpFun, "rightContext")));
        Assert.Equal("abcd", Str(Get(regexpFun, "input")));
        Set(regexpFun, "input", S("zz"));
        Assert.Equal("zz", Str(Get(regexpFun, "$_")));
    }

    [Fact]
    public void Match()
    {
        Assert.Equal("[\"a\",\"a\",\"a\"]", Show(StrCall("banana", "match", Re("a", "g"))));
        Assert.Equal("[\"an\",\"an\"]", Show(StrCall("banana", "match", Re("a.", "g"))));
        Assert.True(StrCall("banana", "match", Re("x", "g")).IsNull);
        Assert.Equal("[\"\",\"\",\"\"]", Show(StrCall("ab", "match", Re("", "g"))));
        JSValue single = StrCall("banana", "match", Re("(a)n"));
        Assert.Equal("[\"an\",\"a\"]", Show(single));
        Assert.Equal(1.0, Num(Get(single, "index")));
        // A string argument is converted with RegExpCreate.
        Assert.Equal("[\"n\"]", Show(StrCall("banana", "match", S("n"))));
    }

    [Fact]
    public void MatchAll()
    {
        JSValue it = StrCall("a1b22c", "matchAll", Re("\\d+", "g"));
        var found = new List<string>();
        while (true)
        {
            JSValue r = ReCall(it, "next");
            if (Get(r, "done").IsTrue) break;
            found.Add(Show(Get(r, "value")) + "@" + Num(Get(Get(r, "value"), "index")));
        }
        Assert.Equal(["[\"1\"]@1", "[\"22\"]@3"], found);
        AssertThrows("TypeError", () => StrCall("a", "matchAll", Re("a")),
            "String.prototype.matchAll called with a non-global RegExp argument");
    }

    [Fact]
    public void Search()
    {
        JSValue re = Re("n", "g");
        Set(re, "lastIndex", N(4));
        Assert.Equal(2.0, Num(StrCall("banana", "search", re)));
        Assert.Equal(4.0, Num(Get(re, "lastIndex")));
        Assert.Equal(-1.0, Num(StrCall("banana", "search", Re("x"))));
        Assert.Equal(1.0, Num(StrCall("banana", "search", S("a"))));
    }

    [Fact]
    public void ReplaceWithString()
    {
        Assert.Equal("b-n-n-", Str(StrCall("banana", "replace", Re("a", "g"), S("-"))));
        Assert.Equal("b-nana", Str(StrCall("banana", "replace", Re("a"), S("-"))));
        Assert.Equal("b[a]n[a]n[a]", Str(StrCall("banana", "replace", Re("(a)", "g"), S("[$1]"))));
        Assert.Equal("bnn", Str(StrCall("banana", "replace", Re("a", "g"), S(""))));
        Assert.Equal("x-y", Str(StrCall("y-x", "replace", Re("(\\w)-(\\w)"), S("$2-$1"))));
        Assert.Equal("[y]", Str(StrCall("y", "replace", Re("(?<l>\\w)"), S("[$<l>]"))));
        Assert.Equal("$<l>", Str(StrCall("y", "replace", Re("(\\w)"), S("$<l>"))));
        Assert.Equal("_a_b_", Str(StrCall("ab", "replace", Re("", "g"), S("_"))));
        Assert.Equal("😀", Str(StrCall("😀", "replace", Re("(?:)", "gu"), S(""))));
        Assert.Equal("_😀_", Str(StrCall("😀", "replace", Re("(?:)", "gu"), S("_"))));
        Assert.Equal("aaXbb", Str(StrCall("aabb", "replace", Re("(?=b)"), S("X"))));
        Assert.Equal("[aa]bb", Str(StrCall("aabb", "replace", Re("a+", "g"), S("[$&]"))));
        Assert.Equal("aaaa", Str(StrCall("aabb", "replace", Re("b+", "g"), S("$`"))));
    }

    [Fact]
    public void ReplaceWithFunction()
    {
        // String as the callback: String(match, captures..., index, subject) is
        // the match itself.
        JSValue stringFun = Global("String");
        Assert.Equal("banana", Str(StrCall("banana", "replace", Re("a$"), stringFun)));
        // String(match, captures..., index, subject) is String(match): identity.
        Assert.Equal("banana", Str(StrCall("banana", "replace", Re("(a)(n)", "g"), stringFun)));
        Assert.Equal("banana", Str(StrCall("banana", "replace", Re("(?<x>a)", "g"), stringFun)));
    }

    [Fact]
    public void Split()
    {
        Assert.Equal("[\"a\",\"b\",\"c\"]", Show(StrCall("a1b22c", "split", Re("\\d+"))));
        Assert.Equal("[\"a\",\"1\",\"b\",\"22\",\"c\"]", Show(StrCall("a1b22c", "split", Re("(\\d+)"))));
        Assert.Equal("[\"a\",\"b\"]", Show(StrCall("a1b22c", "split", Re("\\d+"), N(2))));
        Assert.Equal("[\"a\",\"b\",\"c\"]", Show(StrCall("abc", "split", Re(""))));
        Assert.Equal("[]", Show(StrCall("", "split", Re(""))));
        Assert.Equal("[\"\"]", Show(StrCall("", "split", Re("a"))));
        Assert.Equal("[\"😀\"]", Show(StrCall("😀", "split", Re("", "u"))));
        Assert.Equal("[\"a\",undefined,\"b\"]", Show(StrCall("a,b", "split", Re(",|(x)"))));
        // Sticky splitter goes to the runtime; same result.
        Assert.Equal("[\"a\",\"b\",\"c\"]", Show(StrCall("a1b22c", "split", Re("\\d+", "y"))));
    }

    [Fact]
    public void SlowPathWithModifiedExec()
    {
        // Replacing exec on the instance makes every method go through RegExpExec.
        JSValue re = Re("a", "g");
        JSValue builtinExec = Get(RegExpProto, "exec");
        Set(re, "exec", builtinExec);
        Assert.Equal("b-n-n-", Str(StrCall("banana", "replace", re, S("-"))));
        Assert.Equal("[\"a\",\"a\",\"a\"]", Show(StrCall("banana", "match", re)));
        Assert.Equal(1.0, Num(StrCall("banana", "search", re)));
        Assert.Equal("[\"b\",\"n\",\"n\",\"\"]", Show(StrCall("banana", "split", re)));
    }

    [Fact]
    public void FlagGetters()
    {
        JSValue re = Re("a", "dgimsuy");
        Assert.Equal("dgimsuy", Str(Get(re, "flags")));
        foreach (string flag in new[] { "hasIndices", "global", "ignoreCase", "multiline", "dotAll", "unicode", "sticky" })
        {
            Assert.True(Get(re, flag).IsTrue, flag);
        }
        Assert.True(Get(re, "unicodeSets").IsFalse);
        AssertThrows("TypeError", () => Invoke(RegExpProto, "global", N(1)));
    }

    [Fact]
    public void Compile()
    {
        JSValue re = Re("a", "g");
        ReCall(re, "compile", S("b+"), S("i"));
        Assert.Equal("/b+/i", Str(ReCall(re, "toString")));
        ReCall(re, "compile", Re("c", "m"));
        Assert.Equal("/c/m", Str(ReCall(re, "toString")));
        AssertThrows("TypeError", () => ReCall(re, "compile", Re("c"), S("g")));
    }

    [Fact]
    public void Escape()
    {
        JSValue regexpFun = Global("RegExp");
        Assert.Equal("\\x31\\.2", Str(Invoke(regexpFun, "escape", JSValue.Undefined, S("1.2"))));
        Assert.Equal("\\x61b\\x2dc\\x20\\u2028\\ud800", Str(Invoke(regexpFun, "escape", JSValue.Undefined, S("ab-c \u2028\ud800"))));
        Assert.Equal("\\^\\$\\n", Str(Invoke(regexpFun, "escape", JSValue.Undefined, S("^$\n"))));
        AssertThrows("TypeError", () => Invoke(regexpFun, "escape", JSValue.Undefined, N(1)));
    }

    [Fact]
    public void Test()
    {
        Assert.True(ReCall(Re("^a+$"), "test", S("aaa")).IsTrue);
        Assert.True(ReCall(Re("^a+$", "i"), "test", S("AaA")).IsTrue);
        Assert.True(ReCall(Re("^a+$"), "test", S("aba")).IsFalse);
    }
}
