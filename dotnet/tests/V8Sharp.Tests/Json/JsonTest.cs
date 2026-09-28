// Tests of src/json (JsonParser, JsonStringifier) and builtins-json.cc: unit
// tests, a port of the json-unittest.cc fuzz test as a seeded random test,
// and differential tests against the oracle.
using System.Globalization;
using System.Text;
using V8Sharp.Oracle;
using V8Sharp.Tests.Builtins;

namespace V8Sharp.Tests.Json;

public class JsonTest : BuiltinsTestBase
{
    JSValue Parse(string s) => CallStatic("JSON.parse", Str(s));

    string Stringify(JSValue v, JSValue replacer = default, JSValue gap = default)
    {
        JSValue r = CallStatic("JSON.stringify", v, replacer, gap);
        return r.IsUndefined ? "undefined" : S(r);
    }

    string RoundTrip(string s)
    {
        try
        {
            return Stringify(Parse(s));
        }
        catch (JavaScriptException e)
        {
            return S(Get(e.Value, "name")) + ": " + S(Get(e.Value, "message"));
        }
    }

    [Fact]
    public void ParseBasics()
    {
        Assert.Equal("{\"a\":[1,2.5,true,null,\"x\"],\"b\":{}}", RoundTrip(" { \"a\" : [1, 2.5, true, null, \"x\"], \"b\": {} } "));
        Assert.Equal("{\"1\":2,\"a\":1}", RoundTrip("{\"a\":1,\"1\":2}"));
        Assert.Equal("{\"a\":3,\"b\":2}", RoundTrip("{\"a\":1,\"b\":2,\"a\":3}"));
        Assert.Equal("\"\\u0000\\n\\\"\\\\é😀\"", RoundTrip("\"\\u0000\\n\\\"\\\\\\u00e9\\ud83d\\ude00\""));
        Assert.Equal("-0", S(Call("Number.prototype.toString", Parse("-0"))) == "0" ? "-0" : "?");
        Assert.True(double.IsNegative(Parse("-0").Number));
        Assert.Equal(1e21, Parse("1e21").Number);
        JSValue proto = Parse("{\"__proto__\": 1}");
        Assert.Equal(1, Get(proto, "__proto__").Number);
        JSValue arr = Parse("[1, 2]");
        Assert.Equal(ElementsKind.PACKED_SMI_ELEMENTS, ((JSArray)arr.Object).GetElementsKind());
        Assert.Equal(ElementsKind.PACKED_DOUBLE_ELEMENTS, ((JSArray)Parse("[1, 2.5]").Object).GetElementsKind());
        Assert.Equal(ElementsKind.PACKED_ELEMENTS, ((JSArray)Parse("[1, \"a\"]").Object).GetElementsKind());
        Assert.Equal("{\"4294967294\":2,\"4294967295\":1}", RoundTrip("{\"4294967295\":1,\"4294967294\":2}"));
    }

    [Fact]
    public void ParseErrors()
    {
        Assert.Equal("SyntaxError: Unexpected end of JSON input", RoundTrip(""));
        Assert.Equal("SyntaxError: \"undefined\" is not valid JSON", RoundTrip("undefined"));
        Assert.Equal("SyntaxError: Unexpected token 'a', \"abc\" is not valid JSON", RoundTrip("abc"));
        Assert.Equal("SyntaxError: Expected property name or '}' in JSON at position 1 (line 1 column 2)", RoundTrip("{a:1}"));
        Assert.Equal("SyntaxError: Unexpected non-whitespace character after JSON at position 2 (line 1 column 3)", RoundTrip("1 2"));
        Assert.Equal("SyntaxError: Bad control character in string literal in JSON at position 2 (line 1 column 3)", RoundTrip("\"a\u0001\""));
        Assert.Equal("SyntaxError: Unterminated string in JSON at position 4 (line 1 column 5)", RoundTrip("\"abc"));
        Assert.Equal("SyntaxError: No number after minus sign in JSON at position 1 (line 1 column 2)", RoundTrip("-"));
        Assert.Equal("SyntaxError: Unexpected token ',', \"[1,2,,3,4,5,6,7,8,9]\" is not valid JSON", RoundTrip("[1,2,,3,4,5,6,7,8,9]"));
        Assert.Equal("SyntaxError: Unexpected token ',', ...\",12345678,,12345678,\"... is not valid JSON",
            RoundTrip("[12345678,12345678,,12345678,12345678]"));
    }

    [Fact]
    public void StringifyGapsAndReplacerArrays()
    {
        JSValue v = Parse("{\"a\":[1,{\"b\":2}],\"c\":\"x\",\"d\":{}}");
        Assert.Equal("{\n  \"a\": [\n    1,\n    {\n      \"b\": 2\n    }\n  ],\n  \"c\": \"x\",\n  \"d\": {}\n}", Stringify(v, default, Num(2)));
        Assert.Equal("{\n--\"a\": [\n----1,\n----{\n------\"b\": 2\n----}\n--],\n--\"c\": \"x\",\n--\"d\": {}\n}", Stringify(v, default, Str("--")));
        Assert.Equal("{\"c\":\"x\",\"a\":[1,{}]}", Stringify(v, Parse("[\"c\",\"a\",\"c\"]")));
        Assert.Equal("[]", Stringify(Parse("[]"), default, Num(4)));
        Assert.Equal("undefined", Stringify(JSValue.Undefined));
        Assert.Equal("\"\\ud800\"", Stringify(Str("\ud800")));
        Assert.Equal("\"\\udc00a\"", Stringify(Str("\udc00a")));
        Assert.Equal("[null,null,\"2000-01-01T00:00:00.000Z\"]",
            Stringify(factory.NewJSArrayWithElements(factory.NewFixedArrayFrom([Num(double.NaN), JSValue.Undefined,
                New("Date", Num(946684800000))]))));
        Assert.Equal("TypeError: Do not know how to serialize a BigInt",
            Throws(() => CallStatic("JSON.stringify", BigInt.FromInt(i_isolate, 1))));
    }

    [Fact]
    public void CircularStructure()
    {
        JSValue a = Parse("{\"x\":{\"y\":[1]}}");
        JSValue arr = Get(a, "x.y");
        ObjectOps.SetElement(i_isolate, arr, 1, a, ShouldThrow.ThrowOnError);
        Assert.Equal("TypeError: Converting circular structure to JSON\n    --> starting at object with constructor 'Object'\n    |     property 'x' -> object with constructor 'Object'\n    |     property 'y' -> object with constructor 'Array'\n    --- index 1 closes the circle",
            Throws(() => CallStatic("JSON.stringify", a)));
    }

    [Fact]
    public void RawJson()
    {
        JSValue raw = CallStatic("JSON.rawJSON", Str("1e1000"));
        Assert.True(CallStatic("JSON.isRawJSON", raw).IsTrue);
        Assert.False(CallStatic("JSON.isRawJSON", Parse("{}")).IsTrue);
        Assert.Equal("[1e1000]", Stringify(factory.NewJSArrayWithElements(factory.NewFixedArrayFrom([raw]))));
        Assert.Equal("SyntaxError: Unexpected token '{', \"{}\" is not valid JSON", Throws(() => CallStatic("JSON.rawJSON", Str("{}"))));
        Assert.Equal("SyntaxError: Invalid value for JSON.rawJSON", Throws(() => CallStatic("JSON.rawJSON", Str("1 "))));
        Assert.Equal("SyntaxError: Invalid value for JSON.rawJSON", Throws(() => CallStatic("JSON.rawJSON", Str(""))));
        Assert.True(JSReceiver.TestIntegrityLevel(i_isolate, (JSReceiver)raw.Object, JSReceiver.IntegrityLevel.FROZEN));
    }

    /// <summary>Random JSON text (the domain of json-unittest.cc's ParseValidJsonP fuzz test).</summary>
    static string RandomJson(Random rng, int depth)
    {
        int kind = depth > 4 ? rng.Next(4) : rng.Next(6);
        switch (kind)
        {
            case 0: return "null";
            case 1: return rng.Next(2) == 0 ? "true" : "false";
            case 2:
            {
                double d = rng.Next(4) switch
                {
                    0 => rng.Next(-1000, 1000),
                    1 => rng.NextDouble() * Math.Pow(10, rng.Next(-30, 30)) * (rng.Next(2) == 0 ? -1 : 1),
                    2 => BitConverter.Int64BitsToDouble(rng.NextInt64() & 0x7FEFFFFFFFFFFFFF),
                    _ => rng.Next(),
                };
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            case 3: return RandomJsonString(rng);
            case 4:
            {
                var sb = new StringBuilder("[");
                int n = rng.Next(6);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(rng.Next(3) == 0 ? " , " : ",");
                    sb.Append(RandomJson(rng, depth + 1));
                }
                return sb.Append(']').ToString();
            }
            default:
            {
                var sb = new StringBuilder("{");
                int n = rng.Next(6);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(',');
                    string key = rng.Next(4) == 0 ? "\"" + rng.Next(0, 20) + "\"" : RandomJsonString(rng);
                    sb.Append(key).Append(rng.Next(2) == 0 ? ":" : " :\n ").Append(RandomJson(rng, depth + 1));
                }
                return sb.Append('}').ToString();
            }
        }
    }

    static string RandomJsonString(Random rng)
    {
        var sb = new StringBuilder("\"");
        int n = rng.Next(12);
        for (int i = 0; i < n; i++)
        {
            switch (rng.Next(10))
            {
                case 0: sb.Append("\\n"); break;
                case 1: sb.Append("\\u").Append(rng.Next(0x10000).ToString("x4")); break;
                case 2: sb.Append("\\\""); break;
                case 3: sb.Append((char)rng.Next(0x80, 0x3000)); break;
                case 4: sb.Append("\\/"); break;
                default: sb.Append((char)rng.Next('a', 'z' + 1)); break;
            }
        }
        return sb.Append('"').ToString();
    }

    static List<string> Corpus()
    {
        var rng = new Random(4242);
        var list = new List<string>();
        for (int i = 0; i < 400; i++) list.Add(RandomJson(rng, 0));
        // Invalid texts: mutations of valid ones, and known error shapes.
        for (int i = 0; i < 300; i++)
        {
            string s = RandomJson(rng, 1);
            if (s.Length == 0) continue;
            int pos = rng.Next(s.Length);
            string[] junk = [",", "]", "}", ":", "x", "\"", "-", ".", "e", "0", "\\", "\u0001", " ", "{", "["];
            s = rng.Next(2) == 0 ? s.Remove(pos, 1) : s.Insert(pos, junk[rng.Next(junk.Length)]);
            list.Add(s);
        }
        list.AddRange([
            "", " ", "[", "{", "[1,]", "{\"a\":}", "{\"a\" 1}", "01", "-01", "1.", "1.e5", "1e", "1e+", ".5", "+1", "0x10",
            "\"\\x41\"", "\"\\u12\"", "\"\\u12G4\"", "tru", "nul", "falsey", "NaN", "Infinity", "[object Object]", "undefined",
            "[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,x]", "\"abc\ndef\"", "{\"a\":1,}", "[\"\\ud800\"]",
            "123456789012345678901234567890", "-0.0", "1E400", "-1e-400", "4.9e-324", "[\n1,\r\n2,\r3,\n\n x]",
            "{\"__proto__\":[],\"__proto__\":{}}", "{\"0\":0,\"00\":1,\"1\":1,\"01\":2,\"999999999999\":3}", "\"\\u0000\"",
            "[-]", "[--1]", "[1 2]", "[12345678,12345678,,12345678,12345678]", "{\"4294967295\":1,\"4294967294\":2}",
            "[1,2,3,4,5,6,7,8,9,10,,]", "{\"a\":1,\"b\":2,\"c\":3,\"d\":4,\"e\":5 \"f\":6}", "\u00a0", "\ufeff1", "{\"\\u0030\":1,\"\\u0031\\u0032\":2}",
        ]);
        return list;
    }

    [Fact]
    public void ParseValidJsonP()
    {
        var rng = new Random(17);
        for (int i = 0; i < 2000; i++)
        {
            string s = RandomJson(rng, 0);
            JSValue v = Parse(s);
            // Re-parsing the serialization gives the same serialization.
            string once = Stringify(v);
            Assert.Equal(once, Stringify(Parse(once)));
        }
    }

    [Fact]
    public void ParseStringifyDifferentialAgainstOracle()
    {
        List<string> corpus = Corpus();
        var js = new StringBuilder("const texts = [");
        foreach (string s in corpus) js.Append(ReferenceV8.JsQuote(s)).Append(',');
        js.Append("""
            ];
            const out = [];
            for (const s of texts) {
              let r;
              try { const v = JSON.parse(s); r = JSON.stringify(v) + '|' + JSON.stringify(v, null, '\t'); }
              catch (e) { r = e.name + ': ' + e.message; }
              out.push(JSON.stringify(r));
            }
            print(out.join('\n'));
            """);
        string[] expected;
        using (var v8 = new ReferenceV8())
        {
            expected = v8.Run(js.ToString()).TrimEnd('\n').Split('\n');
        }
        Assert.Equal(corpus.Count, expected.Length);
        var report = new StringBuilder();
        int mismatches = 0;
        for (int i = 0; i < corpus.Count; i++)
        {
            string actual;
            try
            {
                JSValue v = Parse(corpus[i]);
                actual = Stringify(v) + "|" + Stringify(v, default, Str("\t"));
            }
            catch (JavaScriptException e)
            {
                actual = S(Get(e.Value, "name")) + ": " + S(Get(e.Value, "message"));
            }
            actual = Stringify(Str(actual));
            if (actual != expected[i] && mismatches++ < 15)
            {
                report.Append(ReferenceV8.JsQuote(corpus[i])).Append("\n  oracle:  ").Append(expected[i]).Append("\n  v8sharp: ").Append(actual).Append('\n');
            }
        }
        Assert.True(mismatches == 0, $"{mismatches} of {corpus.Count} differ:\n{report}");
    }
}
