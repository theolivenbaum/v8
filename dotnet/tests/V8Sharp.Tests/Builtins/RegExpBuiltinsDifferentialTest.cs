// Differential tests of the String and RegExp builtins against the oracle
// (real V8): a corpus of subjects x patterns x flags x replacements is run
// through the builtins directly (no interpreter needed) and through the
// oracle as one script, and the encoded results are compared line by line.
using System.Text;
using V8Sharp.Oracle;

namespace V8Sharp.Tests.Builtins;

public class RegExpBuiltinsDifferentialTest : BuiltinsTestBase
{
    const string kEncoderJs = """
        function q(s){var r="'";for(var i=0;i<s.length;i++){var c=s.charCodeAt(i);r+=(c<0x20||c>0x7e||c==0x27||c==0x5c)?"\\u"+c.toString(16).padStart(4,"0"):s[i];}return r+"'";}
        function enc(v){
          if(v===null)return"null"; if(v===undefined)return"undefined";
          if(typeof v==="string")return q(v);
          if(typeof v==="number"||typeof v==="boolean")return String(v);
          if(Array.isArray(v)){var r="[";for(var i=0;i<v.length;i++){if(i>0)r+=",";r+=enc(v[i]);}r+="]";
            if("index" in v)r+="@"+v.index;
            if(v.groups!==undefined){var ks=Object.keys(v.groups);r+="{";for(var j=0;j<ks.length;j++){if(j>0)r+=",";r+=ks[j]+":"+enc(v.groups[ks[j]]);}r+="}";}
            if(v.indices!==undefined)r+="d"+enc(v.indices);
            return r;}
          return "obj";}
        function run(f){try{return enc(f());}catch(e){return "throw:"+e.name+":"+e.message;}}
        """;

    string Q(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder("'");
        foreach (char c in s)
        {
            if (c < 0x20 || c > 0x7e || c == '\'' || c == '\\') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    string Enc(JSValue v)
    {
        if (v.IsNull) return "null";
        if (v.IsUndefined) return "undefined";
        if (v.HeapObjectOrNull is JSString s) return Q(s.FlatSpan());
        if (v.IsNumber || v.IsBoolean) return Str(v);
        if (v.HeapObjectOrNull is JSArray array)
        {
            var sb = new StringBuilder("[");
            int length = (int)array.Length.Number;
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Enc(ObjectOps.GetElement(isolate, array, (uint)i)));
            }
            sb.Append(']');
            if (JSReceiver.HasProperty(isolate, array, ReadOnlyRoots.index_string))
            {
                sb.Append('@').Append(Str(Get(array, "index")));
            }
            JSValue groups = Get(array, "groups");
            if (!groups.IsUndefined)
            {
                FixedArray keys = JSObject.OwnPropertyKeys(isolate, (JSReceiver)groups.Object);
                sb.Append('{');
                for (int j = 0; j < keys.Length; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(Str(keys[j])).Append(':').Append(Enc(ObjectOps.GetProperty(isolate, groups, (Name)keys[j].Object)));
                }
                sb.Append('}');
            }
            JSValue indices = Get(array, "indices");
            if (!indices.IsUndefined) sb.Append('d').Append(Enc(indices));
            return sb.ToString();
        }
        return "obj";
    }

    string Run(Func<JSValue> f)
    {
        try
        {
            return Enc(f());
        }
        catch (JavaScriptException e)
        {
            return "throw:" + Str(ObjectOps.GetProperty(isolate, e.Value, ReadOnlyRoots.name_string)) + ":" +
                   Str(ObjectOps.GetProperty(isolate, e.Value, ReadOnlyRoots.message_string));
        }
    }

    static readonly string[] kSubjects =
    [
        "banana", "", "aaa", "abc abc", "12-34 56-78", "😀a😀", "a\nb", "ANA ana", "xyz", "\uD83Dx",
    ];

    static readonly string[] kPatterns =
    [
        "a", "b", "", "(a)", "(a)|(b)", "a*", "a*?", "(?<x>a)(?<y>b)?", "\\b", "^", "$", "(?:)", "x", "an", "(an)+",
        ".", "(.)(.)", "(?<x>a)|(?<x>n)", "\\u{1F600}", "(?=n)", "(?<=a)n", "[aeiou]", "\\d+", "(\\d+)-(\\d+)", "A",
        "(", "(?<a>.)\\k<a>", "[^]", "(?<n>\\d)?x",
    ];

    static readonly string[] kFlags = ["", "g", "y", "gy", "gu", "i", "gi", "d", "gd", "u", "m", "gm", "s", "gv"];

    static readonly string[] kReplacements =
    [
        "-", "", "[$&]", "$1", "$2$1", "$`|$'", "$$", "$<x>", "$0", "$10", "$<y>|$<z>", "$", "$<x", "$01",
    ];

    [Fact]
    public void RegExpMethodsMatchOracle()
    {
        var js = new StringBuilder(kEncoderJs);
        var expected = new List<string>();
        var labels = new List<string>();

        void Add(string label, string jsExpr, Func<JSValue> compute)
        {
            labels.Add(label);
            js.Append("print(run(function(){return ").Append(jsExpr).Append(";}));\n");
            expected.Add(Run(compute));
        }

        foreach (string subject in kSubjects)
        {
            string sj = ReferenceV8.JsQuote(subject);
            foreach (string pattern in kPatterns)
            {
                string pj = ReferenceV8.JsQuote(pattern);
                foreach (string flags in kFlags)
                {
                    string fj = ReferenceV8.JsQuote(flags);
                    string re = $"new RegExp({pj},{fj})";
                    string label = $"{Q(subject)} /{pattern}/{flags}";
                    Add(label + " match", $"{sj}.match({re})", () => StrCall(subject, "match", Re(pattern, flags)));
                    Add(label + " search", $"{sj}.search({re})", () => StrCall(subject, "search", Re(pattern, flags)));
                    Add(label + " split", $"{sj}.split({re})", () => StrCall(subject, "split", Re(pattern, flags)));
                    Add(label + " split2", $"{sj}.split({re},2)", () => StrCall(subject, "split", Re(pattern, flags), N(2)));
                    Add(label + " test", $"{re}.test({sj})", () => ReCall(Re(pattern, flags), "test", S(subject)));
                    Add(label + " exec*", $"(function(){{var re={re};var o=[];for(var i=0;i<4;i++){{var m=re.exec({sj});o.push(m,re.lastIndex);if(!m)break;}}return o;}})()",
                        () => ExecLoop(pattern, flags, subject));
                    if (flags.Contains('g'))
                    {
                        Add(label + " matchAll", $"(function(){{var o=[];for(var m of {sj}.matchAll({re}))o.push(m);return o;}})()",
                            () => MatchAll(pattern, flags, subject));
                    }
                    Add(label + " sticky@1", $"(function(){{var re={re};re.lastIndex=1;var m=re.exec({sj});return [m,re.lastIndex];}})()",
                        () => StickyAt1(pattern, flags, subject));
                    foreach (string replacement in kReplacements)
                    {
                        string rj = ReferenceV8.JsQuote(replacement);
                        Add(label + " replace " + replacement, $"{sj}.replace({re},{rj})",
                            () => StrCall(subject, "replace", Re(pattern, flags), S(replacement)));
                        if (flags.Contains('g'))
                        {
                            Add(label + " replaceAll " + replacement, $"{sj}.replaceAll({re},{rj})",
                                () => StrCall(subject, "replaceAll", Re(pattern, flags), S(replacement)));
                        }
                    }
                    Add(label + " legacy", $"(function(){{{re}.exec({sj});return [RegExp.lastMatch,RegExp.leftContext,RegExp.rightContext,RegExp.lastParen,RegExp.$1,RegExp.$2];}})()",
                        () => LegacyStatics(pattern, flags, subject));
                }
            }
        }

        CompareWithOracle(js.ToString(), labels, expected);
    }

    JSValue ExecLoop(string pattern, string flags, string subject)
    {
        JSValue re = Re(pattern, flags);
        var o = new List<JSValue>();
        for (int i = 0; i < 4; i++)
        {
            JSValue m = ReCall(re, "exec", S(subject));
            o.Add(m);
            o.Add(Get(re, "lastIndex"));
            if (m.IsNull) break;
        }
        return factory.NewJSArrayWithElements(new FixedArray(o.ToArray()));
    }

    JSValue MatchAll(string pattern, string flags, string subject)
    {
        JSValue it = StrCall(subject, "matchAll", Re(pattern, flags));
        var o = new List<JSValue>();
        while (true)
        {
            JSValue r = ReCall(it, "next");
            if (Get(r, "done").IsTrue) break;
            o.Add(Get(r, "value"));
        }
        return factory.NewJSArrayWithElements(new FixedArray(o.ToArray()));
    }

    JSValue StickyAt1(string pattern, string flags, string subject)
    {
        JSValue re = Re(pattern, flags);
        Set(re, "lastIndex", N(1));
        JSValue m = ReCall(re, "exec", S(subject));
        return factory.NewJSArrayWithElements(new FixedArray([m, Get(re, "lastIndex")]));
    }

    JSValue LegacyStatics(string pattern, string flags, string subject)
    {
        ReCall(Re(pattern, flags), "exec", S(subject));
        JSValue regexpFun = Global("RegExp");
        return factory.NewJSArrayWithElements(new FixedArray([
            Get(regexpFun, "lastMatch"), Get(regexpFun, "leftContext"), Get(regexpFun, "rightContext"),
            Get(regexpFun, "lastParen"), Get(regexpFun, "$1"), Get(regexpFun, "$2")]));
    }

    static readonly string[] kStringSubjects =
    [
        "", "a", "abc", "  padded\t", "aXbXc", "ßstraße", "ΣΑΣ", "😀x\uD83D", "ABC abc",
        "İiıI", "x".PadRight(40, 'y'),
    ];

    static readonly double[] kIndices = [0, 1, -1, 2, 5, -5, 100, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1.5, -0.0];

    [Fact]
    public void StringMethodsMatchOracle()
    {
        var js = new StringBuilder(kEncoderJs);
        var expected = new List<string>();
        var labels = new List<string>();

        void Add(string label, string jsExpr, Func<JSValue> compute)
        {
            labels.Add(label);
            js.Append("print(run(function(){return ").Append(jsExpr).Append(";}));\n");
            expected.Add(Run(compute));
        }

        static string NumJs(double d) =>
            double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity"
            : d == 0 && double.IsNegative(d) ? "-0" : d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string[] oneArg = ["at", "charAt", "charCodeAt", "codePointAt", "slice", "substring", "substr", "repeat", "padStart", "padEnd"];
        string[] twoArg = ["slice", "substring", "substr"];
        string[] noArg = ["trim", "trimStart", "trimEnd", "toUpperCase", "toLowerCase", "toLocaleUpperCase", "toLocaleLowerCase",
            "isWellFormed", "toWellFormed", "big", "normalize"];
        string[] search = ["indexOf", "lastIndexOf", "includes", "startsWith", "endsWith", "split", "localeCompare", "concat",
            "padStart", "padEnd"];
        string[] needles = ["", "a", "b", "X", "abc", "\uD83D", " ", "zz"];

        foreach (string subject in kStringSubjects)
        {
            string sj = ReferenceV8.JsQuote(subject);
            foreach (string m in noArg)
            {
                Add($"{Q(subject)}.{m}()", $"{sj}.{m}()", () => StrCall(subject, m));
            }
            foreach (string m in oneArg)
            {
                foreach (double d in kIndices)
                {
                    if (m is "repeat" && d > 50) continue;
                    Add($"{Q(subject)}.{m}({d})", $"{sj}.{m}({NumJs(d)})", () => StrCall(subject, m, N(d)));
                }
            }
            foreach (string m in twoArg)
            {
                foreach (double a in kIndices)
                {
                    foreach (double b in kIndices)
                    {
                        Add($"{Q(subject)}.{m}({a},{b})", $"{sj}.{m}({NumJs(a)},{NumJs(b)})", () => StrCall(subject, m, N(a), N(b)));
                    }
                }
            }
            foreach (string m in search)
            {
                foreach (string needle in needles)
                {
                    string nj = ReferenceV8.JsQuote(needle);
                    Add($"{Q(subject)}.{m}({Q(needle)})", $"{sj}.{m}({nj})", () => StrCall(subject, m, S(needle)));
                    foreach (double d in kIndices)
                    {
                        if (m is "localeCompare" or "concat") continue;
                        if (m is "padStart" or "padEnd" && d > 50) continue;
                        if (m is "padStart" or "padEnd")
                        {
                            Add($"{Q(subject)}.{m}({d},{Q(needle)})", $"{sj}.{m}({NumJs(d)},{nj})", () => StrCall(subject, m, N(d), S(needle)));
                        }
                        else
                        {
                            Add($"{Q(subject)}.{m}({Q(needle)},{d})", $"{sj}.{m}({nj},{NumJs(d)})", () => StrCall(subject, m, S(needle), N(d)));
                        }
                    }
                    foreach (string replacement in kReplacements)
                    {
                        string rj = ReferenceV8.JsQuote(replacement);
                        Add($"{Q(subject)}.replace({Q(needle)},{Q(replacement)})", $"{sj}.replace({nj},{rj})",
                            () => StrCall(subject, "replace", S(needle), S(replacement)));
                        Add($"{Q(subject)}.replaceAll({Q(needle)},{Q(replacement)})", $"{sj}.replaceAll({nj},{rj})",
                            () => StrCall(subject, "replaceAll", S(needle), S(replacement)));
                    }
                }
            }
        }

        CompareWithOracle(js.ToString(), labels, expected);
    }

    // Results the oracle computes differently because it is built with ICU
    // (V8Sharp matches V8 without i18n support), or is an older V8.
    static bool IsKnownOracleDifference(string label) =>
        label.Contains(".toUpperCase()") || label.Contains(".toLowerCase()") || label.Contains(".toLocaleUpperCase()") ||
        label.Contains(".toLocaleLowerCase()") || label.Contains(".localeCompare(") || label.Contains(".normalize()");

    static void CompareWithOracle(string script, List<string> labels, List<string> expected)
    {
        using var v8 = new ReferenceV8();
        string output = v8.Run(script);
        string[] lines = output.Split('\n');
        Assert.True(lines.Length >= expected.Count, $"oracle printed {lines.Length} lines for {expected.Count} cases:\n" +
            string.Join("\n", lines.TakeLast(3)));
        var failures = new List<string>();
        int known = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            string oracle = i < lines.Length ? lines[i] : "<missing>";
            if (oracle == expected[i]) continue;
            if (IsKnownOracleDifference(labels[i]))
            {
                known++;
                continue;
            }
            failures.Add($"{labels[i]}\n    oracle:  {oracle}\n    v8sharp: {expected[i]}");
        }
        Assert.True(Environment.GetEnvironmentVariable("V8SHARP_DIFF_STATS") is null && failures.Count == 0,
            $"{failures.Count} of {expected.Count} differ ({known} known ICU differences):\n" +
            string.Join("\n", failures.Take(60)));
    }
}
