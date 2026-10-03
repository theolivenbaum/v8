// Tests for the port of tools/testrunner/local/statusfile.py, following
// tools/testrunner/local/statusfile_test.py.
using V8Sharp.TestRunner;
using V8Sharp.TestRunner.Status;

namespace V8Sharp.Conformance.Tests;

public class StatusFileTests
{
    static Dictionary<string, object?> Vars(string arch = "x64", string mode = "release", bool i18n = true) => new()
    {
        ["arch"] = arch,
        ["system"] = "linux",
        ["mode"] = mode,
        ["i18n"] = i18n,
        ["simulator_run"] = false,
        ["lite_mode"] = false,
        ["has_webassembly"] = true,
        ["gc_stress"] = false,
    };

    // statusfile_test.py TEST_STATUS_FILE.
    const string TestStatusFile = """
        [
        [ALWAYS, {
          'foo/bar': [PASS, SKIP],
          'baz/bar': [PASS, FAIL],
          'foo/*': [PASS, SLOW],
        }],  # ALWAYS

        ['%s', {
          'baz/bar': [PASS, SLOW],
          'foo/*': [FAIL],
        }],
        ]
        """;

    static StatusFile Make(string condition) =>
        new(TestStatusFile.Replace("%s", condition), new Dictionary<string, object?> { ["system"] = "linux", ["mode"] = "release" });

    static Dictionary<string, string[]> RulesOf(StatusFile sf, string variant, bool prefix) =>
        sf.Rules(variant).Where(r => r.IsPrefix == prefix)
            .ToDictionary(r => r.Rule, r => r.Outcomes.Order(StringComparer.Ordinal).ToArray());

    [Fact]
    public void test_eval_expression()
    {
        var vars = new Dictionary<string, object?> { ["system"] = "linux", ["mode"] = "release", ["linux"] = "linux", ["release"] = "release", ["debug"] = "debug", ["default"] = "default" };
        Assert.Equal(true, PyExpression.Evaluate("system==linux and mode==release", vars));
        Assert.Equal(true, PyExpression.Evaluate("system==linux or variant==default", vars));
        Assert.Equal(false, PyExpression.Evaluate("system==linux and mode==debug", vars));
        Assert.Throws<KeyNotFoundException>(() => PyExpression.Evaluate("system==linux and mode==foo", vars));
        Assert.Throws<FormatException>(() => PyExpression.Evaluate("system==linux and mode=release", vars));
        Assert.Throws<VariantExpressionException>(() => PyExpression.Evaluate("system==linux and variant==default", vars));
    }

    [Fact]
    public void test_read_statusfile_section_true()
    {
        var sf = Make("system==linux");
        Assert.Equal(new Dictionary<string, string[]>
        {
            ["foo/bar"] = ["PASS", "SKIP"],
            ["baz/bar"] = ["FAIL", "PASS", "SLOW"],
        }, RulesOf(sf, "", prefix: false));
        // A FAIL without PASS in one rule wins over a PASS in another.
        Assert.Equal(new Dictionary<string, string[]> { ["foo/"] = ["FAIL", "SLOW"] }, RulesOf(sf, "", prefix: true));
        Assert.Empty(sf.Rules("default"));
    }

    [Fact]
    public void test_read_statusfile_section_false()
    {
        var sf = Make("system==windows");
        Assert.Equal(new Dictionary<string, string[]>
        {
            ["foo/bar"] = ["PASS", "SKIP"],
            ["baz/bar"] = ["FAIL", "PASS"],
        }, RulesOf(sf, "", prefix: false));
        Assert.Equal(new Dictionary<string, string[]> { ["foo/"] = ["PASS", "SLOW"] }, RulesOf(sf, "", prefix: true));
        Assert.Empty(sf.Rules("default"));
    }

    [Fact]
    public void test_read_statusfile_section_variant()
    {
        var sf = Make("system==linux and variant==default");
        Assert.Equal(new Dictionary<string, string[]>
        {
            ["foo/bar"] = ["PASS", "SKIP"],
            ["baz/bar"] = ["FAIL", "PASS"],
        }, RulesOf(sf, "", prefix: false));
        Assert.Equal(new Dictionary<string, string[]> { ["foo/"] = ["PASS", "SLOW"] }, RulesOf(sf, "", prefix: true));
        Assert.Equal(new Dictionary<string, string[]> { ["baz/bar"] = ["PASS", "SLOW"] }, RulesOf(sf, "default", prefix: false));
        Assert.Equal(new Dictionary<string, string[]> { ["foo/"] = ["FAIL"] }, RulesOf(sf, "default", prefix: true));
        // get_outcomes merges the variant's rules with the independent ones.
        Assert.Equal(["FAIL", "PASS", "SLOW"], sf.GetOutcomes("baz/bar", "default").Order(StringComparer.Ordinal));
        Assert.Equal(["FAIL", "PASS"], sf.GetOutcomes("baz/bar", "jitless").Order(StringComparer.Ordinal));
        Assert.Equal(["FAIL", "PASS", "SKIP", "SLOW"], sf.GetOutcomes("foo/bar", "default").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PrefixRules()
    {
        var sf = new StatusFile("""
            [[ALWAYS, {
              'wasm/*': [SKIP],
              'regress/regress-1*': [PASS, SLOW],
              'regress/regress-123': [FAIL],
            }]]
            """, Vars());
        Assert.Equal(["SKIP"], sf.GetOutcomes("wasm/anything/deep"));
        Assert.Equal(["PASS", "SLOW"], sf.GetOutcomes("regress/regress-100").Order());
        Assert.Equal(["FAIL", "PASS", "SLOW"], sf.GetOutcomes("regress/regress-123").Order());
        Assert.Empty(sf.GetOutcomes("wasmx"));
    }

    [Fact]
    public void ConditionalOutcomesAndFlags()
    {
        var sf = new StatusFile("""
            [
            [ALWAYS, {
              'a': [PASS, ['arch == x64', FAIL], ['arch == arm', SKIP]],
              'b': [PASS, ['mode != release or dcheck_always_on', SKIP], ['not i18n', FAIL]],
              'c': ['--stress-flag', PASS],
              'd': [['arch in (ia32, x64)', SLOW]],
            }],
            ['not i18n', { 'e': [FAIL] }],
            ]
            """, new Dictionary<string, object?>(Vars()) { ["dcheck_always_on"] = false });
        Assert.Equal(["FAIL", "PASS"], sf.GetOutcomes("a").Order());
        Assert.Equal(["PASS"], sf.GetOutcomes("b"));
        Assert.Equal(["--stress-flag", "PASS"], sf.GetOutcomes("c").Order(StringComparer.Ordinal));
        Assert.Equal(["SLOW"], sf.GetOutcomes("d"));
        Assert.Empty(sf.GetOutcomes("e"));
        var noI18n = new StatusFile("[['not i18n', { 'e': [FAIL] }]]", Vars(i18n: false));
        Assert.Equal(["FAIL"], noI18n.GetOutcomes("e"));
    }

    [Theory]
    [InlineData("ALWAYS", true)]
    [InlineData("arch == x64", true)]
    [InlineData("arch == x64 and mode == debug", false)]
    [InlineData("arch == arm or mode == release", true)]
    [InlineData("not (arch == x64 and mode == release)", false)]
    [InlineData("arch in [x64, arm]", true)]
    [InlineData("arch in (arm, ia32)", false)]
    [InlineData("arch not in (arm, ia32)", true)]
    [InlineData("system != linux", false)]
    [InlineData("i18n == True", true)]
    [InlineData("simulator_run", false)]
    [InlineData("'x64' == arch", true)]
    public void Expressions(string expression, bool expected)
    {
        var vars = new Dictionary<string, object?>(Vars()) { ["ALWAYS"] = true, ["x64"] = "x64", ["arm"] = "arm", ["ia32"] = "ia32", ["linux"] = "linux", ["release"] = "release", ["debug"] = "debug" };
        Assert.Equal(expected, PyExpression.Evaluate(expression, vars));
    }

    [Fact]
    public void AndOrReturnOperandsLikePython()
    {
        var vars = new Dictionary<string, object?> { ["a"] = "x", ["b"] = false };
        Assert.Equal("x", PyExpression.Evaluate("b or a", vars));
        Assert.Equal(false, PyExpression.Evaluate("b and a", vars));
    }

    [Fact]
    public void VariantIdentifierIsDetected()
    {
        Assert.Throws<VariantExpressionException>(() => PyExpression.Evaluate("variant == jitless", Vars()));
    }

    [Fact]
    public void UnknownIdentifierIsAWarning()
    {
        var sf = new StatusFile("[['no_such_build_flag', {'a': [SKIP]}]]", Vars());
        Assert.Empty(sf.GetOutcomes("a"));
        Assert.Single(sf.Warnings);
    }

    [Fact]
    public void PythonLiteralSyntax()
    {
        var parsed = (List<object?>)PyLiteral.Parse("""
            # comment
            [
              ["cond", {  # trailing comment
                "x/y": [PASS, FAIL,],  'z' 'w': "SKIP",
              },],
            ]
            """)!;
        var section = (List<object?>)parsed[0]!;
        Assert.Equal("cond", section[0]);
        var rules = (List<KeyValuePair<string, object?>>)section[1]!;
        Assert.Equal("x/y", rules[0].Key);
        Assert.Equal(["PASS", "FAIL"], (List<object?>)rules[0].Value!);
        Assert.Equal("zw", rules[1].Key);
        Assert.Equal("SKIP", rules[1].Value);
    }

    public static TheoryData<string> V8StatusFiles => ["mjsunit", "test262", "message", "webkit", "mozilla"];

    /// <summary>Every status file of the supported suites parses, and every
    /// build variable it uses is defined by the runner.</summary>
    [Theory]
    [MemberData(nameof(V8StatusFiles))]
    public void RealStatusFilesParse(string suite)
    {
        string root = TestPaths.V8Root;
        foreach (var engine in new[] { "oracle", "v8sharp" })
        {
            var vars = Runner.LoadBuildConfig(engine);
            var sf = StatusFile.Load(Path.Combine(root, "test", suite, suite + ".status"), vars);
            Assert.Empty(sf.Warnings);
            Assert.NotEmpty(sf.Rules());
        }
    }

    [Fact]
    public void Mjsunit_KnownRules()
    {
        var sf = StatusFile.Load(Path.Combine(TestPaths.V8Root, "test", "mjsunit", "mjsunit.status"), Runner.LoadBuildConfig("oracle"));
        // [ALWAYS, {... 'wasm/wasm-module-builder': [SKIP] ...}]
        Assert.Contains("SKIP", sf.GetOutcomes("wasm/wasm-module-builder"));
        // Without WebAssembly (v8sharp), every wasm test is skipped.
        var noWasm = StatusFile.Load(Path.Combine(TestPaths.V8Root, "test", "mjsunit", "mjsunit.status"), Runner.LoadBuildConfig("v8sharp"));
        Assert.Contains("SKIP", noWasm.GetOutcomes("wasm/anything", "default"));
        Assert.DoesNotContain("SKIP", sf.GetOutcomes("wasm/anything", "default"));
    }
}
