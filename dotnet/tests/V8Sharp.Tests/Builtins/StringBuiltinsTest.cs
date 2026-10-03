// Tests of the String builtins (Builtins.String*.cs), calling the intrinsics
// directly. Expected values are V8's (checked against d8 / the spec).
namespace V8Sharp.Tests.Builtins;

public class StringBuiltinsTest : BuiltinsTestBase
{
    [Fact]
    public void Constructor()
    {
        JSValue stringFun = Global("String");
        Assert.Equal("", Str(Execution.Call(isolate, stringFun, JSValue.Undefined, [])));
        Assert.Equal("12", Str(Execution.Call(isolate, stringFun, JSValue.Undefined, [N(12)])));
        Assert.Equal("Symbol(foo)", Str(Execution.Call(isolate, stringFun, JSValue.Undefined,
            [factory.NewSymbol(S("foo"))])));
        JSValue wrapper = Execution.New(isolate, stringFun, [S("abc")]);
        Assert.IsType<JSPrimitiveWrapper>(wrapper.Object);
        Assert.Equal("abc", Str(StrCall(wrapper, "valueOf")));
        Assert.Equal(3.0, Num(Get(wrapper, "length")));
        AssertThrows("TypeError", () => StrCall(N(1), "toString"), "String.prototype.toString requires that 'this' be a String");
    }

    [Fact]
    public void FromCharCodeAndCodePoint()
    {
        JSValue stringFun = Global("String");
        Assert.Equal("A", Str(Invoke(stringFun, "fromCharCode", JSValue.Undefined, N(65))));
        Assert.Equal("ABā", Str(Invoke(stringFun, "fromCharCode", JSValue.Undefined, N(65), N(66 + 65536), N(257))));
        Assert.Equal("😀a", Str(Invoke(stringFun, "fromCodePoint", JSValue.Undefined, N(0x1F600), N(97))));
        AssertThrows("RangeError", () => Invoke(stringFun, "fromCodePoint", JSValue.Undefined, N(1.5)), "Invalid code point 1.5");
        AssertThrows("RangeError", () => Invoke(stringFun, "fromCodePoint", JSValue.Undefined, N(0x110000)));
    }

    [Fact]
    public void Raw()
    {
        JSValue stringFun = Global("String");
        JSValue arr = factory.NewJSObject(isolate.NativeContext.ObjectFunction);
        JSArray rawArray = factory.NewJSArrayWithElements(new FixedArray([S("a"), S("b"), S("c")]));
        Set(arr, "raw", rawArray);
        Assert.Equal("a1b2c", Str(Invoke(stringFun, "raw", JSValue.Undefined, arr, N(1), N(2), N(3))));
    }

    [Fact]
    public void AtAndCharAccess()
    {
        Assert.Equal("c", Str(StrCall("abc", "at", N(-1))));
        Assert.True(StrCall("abc", "at", N(3)).IsUndefined);
        Assert.Equal("b", Str(StrCall("abc", "charAt", N(1))));
        Assert.Equal("", Str(StrCall("abc", "charAt", N(5))));
        Assert.Equal(98.0, Num(StrCall("abc", "charCodeAt", N(1))));
        Assert.True(double.IsNaN(Num(StrCall("abc", "charCodeAt", N(-1)))));
        Assert.Equal(0x1F600, Num(StrCall("😀", "codePointAt", N(0))));
        Assert.Equal(0xDE00, Num(StrCall("😀", "codePointAt", N(1))));
        AssertThrows("TypeError", () => StrCall(JSValue.Undefined, "charAt", N(0)),
            "String.prototype.charAt called on null or undefined");
    }

    [Fact]
    public void Search()
    {
        Assert.Equal(2.0, Num(StrCall("abcabc", "indexOf", S("c"))));
        Assert.Equal(5.0, Num(StrCall("abcabc", "indexOf", S("c"), N(3))));
        Assert.Equal(3.0, Num(StrCall("abcabc", "indexOf", S(""), N(3))));
        Assert.Equal(6.0, Num(StrCall("abcabc", "indexOf", S(""), N(10))));
        Assert.Equal(-1.0, Num(StrCall("abcabc", "indexOf", S("d"))));
        Assert.Equal(3.0, Num(StrCall("abcabc", "lastIndexOf", S("abc"))));
        Assert.Equal(0.0, Num(StrCall("abcabc", "lastIndexOf", S("abc"), N(2))));
        Assert.Equal(6.0, Num(StrCall("abcabc", "lastIndexOf", S(""))));
        Assert.Equal(-1.0, Num(StrCall("ab", "lastIndexOf", S("abc"))));
        Assert.True(StrCall("abcabc", "includes", S("ca")).IsTrue);
        Assert.True(StrCall("abcabc", "startsWith", S("bc"), N(1)).IsTrue);
        Assert.True(StrCall("abcabc", "endsWith", S("ab"), N(5)).IsTrue);
        Assert.True(StrCall("abc", "endsWith", S("abcd")).IsFalse);
        AssertThrows("TypeError", () => StrCall("abc", "includes", Re("a")),
            "First argument to String.prototype.includes must not be a regular expression");
    }

    [Fact]
    public void SliceSubstrSubstring()
    {
        Assert.Equal("bc", Str(StrCall("abcd", "slice", N(1), N(-1))));
        Assert.Equal("", Str(StrCall("abcd", "slice", N(3), N(1))));
        Assert.Equal("cd", Str(StrCall("abcd", "substr", N(-2))));
        Assert.Equal("b", Str(StrCall("abcd", "substr", N(1), N(1))));
        Assert.Equal("bc", Str(StrCall("abcd", "substring", N(3), N(1))));
        Assert.Equal("abcd", Str(StrCall("abcd", "substring", N(double.NaN))));
    }

    [Fact]
    public void PadRepeatConcat()
    {
        Assert.Equal("  abc", Str(StrCall("abc", "padStart", N(5))));
        Assert.Equal("abcxyx", Str(StrCall("abc", "padEnd", N(6), S("xy"))));
        Assert.Equal("xyxabc", Str(StrCall("abc", "padStart", N(6), S("xy"))));
        Assert.Equal("abc", Str(StrCall("abc", "padStart", N(6), S(""))));
        Assert.Equal("ababab", Str(StrCall("ab", "repeat", N(3))));
        Assert.Equal("", Str(StrCall("", "repeat", N(1e10))));
        AssertThrows("RangeError", () => StrCall("a", "repeat", N(-1)), "Invalid count value: -1");
        AssertThrows("RangeError", () => StrCall("a", "repeat", N(double.PositiveInfinity)));
        AssertThrows("RangeError", () => StrCall("a", "repeat", N(1 << 30)), "Invalid string length");
        Assert.Equal("a1null", Str(StrCall("a", "concat", N(1), JSValue.Null)));
    }

    [Fact]
    public void Trim()
    {
        Assert.Equal("a b", Str(StrCall(" \t\n a b　﻿", "trim")));
        Assert.Equal("a ", Str(StrCall("  a ", "trimStart")));
        Assert.Equal("  a", Str(StrCall("  a ", "trimEnd")));
        Assert.Equal("", Str(StrCall("   ", "trim")));
        // U+180E is not white space in ES2016+.
        Assert.Equal("᠎", Str(StrCall("᠎", "trim")));
    }

    [Fact]
    public void CaseConversion()
    {
        Assert.Equal("ABC", Str(StrCall("abc", "toUpperCase")));
        Assert.Equal("abc", Str(StrCall("ABC", "toLowerCase")));
        Assert.Equal("STRASSE", Str(StrCall("straße", "toUpperCase")));
        Assert.Equal("İ", Str(StrCall("İ", "toUpperCase")));
        Assert.Equal("i̇", Str(StrCall("İ", "toLowerCase")));
        Assert.Equal("σς", Str(StrCall("ΣΣ", "toLowerCase")));
        Assert.Equal("Ÿ", Str(StrCall("ÿ", "toUpperCase")));
        Assert.Equal("Μ", Str(StrCall("µ", "toUpperCase")));
        JSValue same = S("already lower");
        Assert.Same(same.Object, StrCall(same, "toLowerCase").Object);
        Assert.Equal("ABC", Str(StrCall("abc", "toLocaleUpperCase")));
    }

    [Fact]
    public void LocaleCompareAndNormalize()
    {
        Assert.Equal(-1.0, Num(StrCall("a", "localeCompare", S("b"))));
        Assert.Equal(1.0, Num(StrCall("b", "localeCompare", S("a"))));
        Assert.Equal(0.0, Num(StrCall("a", "localeCompare", S("a"))));
        Assert.Equal(-1.0, Num(StrCall("a", "localeCompare", S("ab"))));
        Assert.Equal("abc", Str(StrCall("abc", "normalize", S("NFKD"))));
        AssertThrows("RangeError", () => StrCall("abc", "normalize", S("X")),
            "The normalization form should be one of NFC, NFD, NFKC, NFKD.");
    }

    [Fact]
    public void WellFormed()
    {
        Assert.True(StrCall("a😀", "isWellFormed").IsTrue);
        Assert.True(StrCall("a\uD83D", "isWellFormed").IsFalse);
        Assert.Equal("a�b�", Str(StrCall("a\uD83Db\uDE00", "toWellFormed")));
    }

    [Fact]
    public void Html()
    {
        Assert.Equal("<a name=\"x&quot;y\">t</a>", Str(StrCall("t", "anchor", S("x\"y"))));
        Assert.Equal("<b>t</b>", Str(StrCall("t", "bold")));
        Assert.Equal("<font size=\"3\">t</font>", Str(StrCall("t", "fontsize", N(3))));
    }

    [Fact]
    public void Iterator()
    {
        JSValue it = StrCall("a😀b", "@@iterator");
        var values = new List<string>();
        while (true)
        {
            JSValue result = ReCall(it, "next");
            if (Get(result, "done").IsTrue) break;
            values.Add(Str(Get(result, "value")));
        }
        Assert.Equal(["a", "😀", "b"], values);
    }

    [Fact]
    public void ReplaceWithString()
    {
        Assert.Equal("a-c", Str(StrCall("abc", "replace", S("b"), S("-"))));
        Assert.Equal("a[b]c", Str(StrCall("abc", "replace", S("b"), S("[$&]"))));
        Assert.Equal("aacc", Str(StrCall("abc", "replace", S("b"), S("$`$'"))));
        Assert.Equal("a$c", Str(StrCall("abc", "replace", S("b"), S("$$"))));
        Assert.Equal("a$1c", Str(StrCall("abc", "replace", S("b"), S("$1"))));
        Assert.Equal("abc", Str(StrCall("abc", "replace", S("x"), S("y"))));
        Assert.Equal("a-b-c", Str(StrCall("a+b+c", "replaceAll", S("+"), S("-"))));
        Assert.Equal("_a_b_", Str(StrCall("ab", "replaceAll", S(""), S("_"))));
    }

    [Fact]
    public void Split()
    {
        Assert.Equal("[\"a\",\"b\",\"c\"]", Show(StrCall("a,b,c", "split", S(","))));
        Assert.Equal("[\"a\",\"b\"]", Show(StrCall("a,b,c", "split", S(","), N(2))));
        Assert.Equal("[\"a\",\"b\",\"c\"]", Show(StrCall("abc", "split", S(""))));
        Assert.Equal("[\"abc\"]", Show(StrCall("abc", "split")));
        Assert.Equal("[]", Show(StrCall("abc", "split", S(","), N(0))));
        Assert.Equal("[\"\",\"\"]", Show(StrCall(",", "split", S(","))));
        Assert.Equal("[]", Show(StrCall("", "split", S(""))));
        Assert.Equal("[\"\"]", Show(StrCall("", "split", S(","))));
    }

    [Fact]
    public void SplitCacheReturnsIndependentArrays()
    {
        JSValue subject = factory.InternalizeString("x;y;z");
        JSValue sep = factory.InternalizeString(";");
        JSValue first = StrCall(subject, "split", sep);
        JSValue second = StrCall(subject, "split", sep);
        Assert.Equal("[\"x\",\"y\",\"z\"]", Show(second));
        ObjectOps.SetElement(isolate, second, 0, S("changed"), ShouldThrow.ThrowOnError);
        Assert.Equal("[\"x\",\"y\",\"z\"]", Show(first));
        Assert.Equal("[\"x\",\"y\",\"z\"]", Show(StrCall(subject, "split", sep)));
    }
}
