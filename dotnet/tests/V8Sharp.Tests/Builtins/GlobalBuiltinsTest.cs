// Tests of the global functions (Builtins.Global.cs, Builtins.Global.Uri.cs):
// URI coding, escape/unescape, parseInt/parseFloat, isNaN/isFinite, checked
// against the oracle (real V8) on a corpus of inputs.
using System.Globalization;
using System.Text;
using V8Sharp.Oracle;

namespace V8Sharp.Tests.Builtins;

public class GlobalBuiltinsTest : CoreBuiltinsTest
{
    static readonly string[] s_uriInputs =
    [
        "", "abc", "a b", "http://example.com/a b?c=d&e=f#g", "!'()*-._~", "#$&+,/:;=?@", "%", "%2", "%zz", "%41", "%2F",
        "%23%24%26%2B%2C%2F%3A%3B%3D%3F%40", "%C3%A9", "%c3%a9", "%C3", "%C3%", "%C3%2", "%C3A9", "%E2%82%AC", "%F0%9F%98%80",
        "%F0%9F%98", "%F8%80%80%80", "%80", "%C0%AF", "%ED%A0%80", "%EF%BF%BD", "%EF%BF%BF", "%F4%90%80%80", "é", "€",
        "😀", "\ud800", "\udc00", "a\ud800b", "\ud800\ud800", "￿", "\u0000", "%00", "~%7E", "café%20bar",
        "%u0041", "%u00e9", "%U0041", "%uD800", "%u12", "%%41", "ÿĀ", "a+b", "%E0%A4%A", "%E0%A4%AD%", "Ab1-_.!~*'()",
    ];

    static string JsLiteral(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        return sb.Append('"').ToString();
    }

    /// <summary>The result as the oracle script below prints it: code units in hex, or the error.</summary>
    string Describe(Func<JSValue> f)
    {
        try
        {
            JSValue result = f();
            return Hex(S(result));
        }
        catch (JavaScriptException e)
        {
            return "!" + ErrorUtils.ToString(i_isolate, e.Value);
        }
    }

    static string Hex(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString();
    }

    const string kOracleDescribe = """
        function __d(f) {
          try { var r = String(f()); var o = ''; for (var i = 0; i < r.length; i++) o += r.charCodeAt(i).toString(16) + ' '; return o; }
          catch (e) { return '!' + e.name + ': ' + e.message; }
        }
        """;

    void CompareWithOracle(string[] functions, string[] inputs)
    {
        using var v8 = new ReferenceV8();
        v8.Run(kOracleDescribe + "globalThis.__d = __d;");
        Assert.Equal("25 43 33 25 41 39 ", v8.Eval("__d(() => encodeURI('\\u00e9'))"));
        var failures = new List<string>();
        foreach (string fn in functions)
        {
            foreach (string input in inputs)
            {
                string expected = v8.Eval($"__d(() => {fn}({JsLiteral(input)}))");
                string actual = Describe(() => Call(fn, Str(input)));
                if (expected != actual) failures.Add($"{fn}({JsLiteral(input)}): expected {expected} got {actual}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void UriFunctionsMatchOracle() =>
        CompareWithOracle(["encodeURI", "encodeURIComponent", "decodeURI", "decodeURIComponent", "escape", "unescape"], s_uriInputs);

    [Fact]
    public void ParseAndPredicatesMatchOracle() =>
        CompareWithOracle(["parseInt", "parseFloat", "isNaN", "isFinite"],
        [
            "", " 12 ", "0x1F", "-0x1F", "0b11", "0o17", "017", "1e3", "-1.5e-3xyz", ".5", "-.5", "Infinity", "-Infinity", "+Infinity",
            "infinity", "NaN", "12abc", "abc", "  \t\n42", " 42", "﻿42", "9007199254740993", "1e400", "-0", "0.0000001",
            "123456789012345678901234567890", "4294967295", "4294967296", "1_000", "١", "3.14abc", "  -  1",
        ]);

    [Fact]
    public void ParseIntRadixAndNumbers()
    {
        Assert.Equal("255", S(Call("parseInt", Str("ff"), Num(16))));
        Assert.Equal("NaN", S(Call("parseInt", Str("ff"), Num(37))));
        Assert.Equal("NaN", S(Call("parseInt", Str("1"), Num(1))));
        Assert.Equal("31", S(Call("parseInt", Str("0x1f"), Num(0))));
        Assert.Equal("5", S(Call("parseInt", Str("101"), Num(2.9))));
        Assert.Equal("123", S(Call("parseInt", Num(123.9))));
        Assert.Equal("-123", S(Call("parseInt", Num(-123.9))));
        Assert.Equal("1", S(Call("parseInt", Num(1e21))));
        Assert.Equal("5", S(Call("parseInt", Num(5e-7))));
        Assert.True(double.IsNegative(Call("parseInt", Num(-0.5)).Number));
        Assert.False(double.IsNegative(Call("parseInt", Num(-0.0)).Number));
        Assert.False(double.IsNegative(Call("parseFloat", Num(-0.0)).Number));
        Assert.Equal("1.5", S(Call("parseFloat", Num(1.5))));
        Assert.Equal("NaN", S(Call("parseFloat")));
        Assert.Equal("TypeError: Cannot convert a Symbol value to a string", Throws(() => Call("parseFloat", factory.NewSymbol())));
        Assert.Same(G("parseInt").Object, G("Number.parseInt").Object);
        Assert.True(Call("isNaN").IsTrue);
        Assert.True(Call("isFinite", Str("12")).IsTrue);
        Assert.True(Call("isFinite", JSValue.Null).IsTrue);
    }

    [Fact]
    public void EscapeKeepsIdentity()
    {
        JSValue s = Str("abc@*_+-./");
        Assert.Same(s.Object, Call("escape", s).Object);
        Assert.Same(s.Object, Call("unescape", s).Object);
        Assert.Equal("URIError: URI malformed", Throws(() => Call("decodeURI", Str("%"))));
    }
}
