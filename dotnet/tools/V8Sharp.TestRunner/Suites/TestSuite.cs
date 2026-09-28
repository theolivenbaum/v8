// Port of tools/testrunner/local/testsuite.py and the testcfg.py of each
// suite this runner supports.
using System.Text;
using System.Text.RegularExpressions;
using V8Sharp.TestRunner.OutProc;
using V8Sharp.TestRunner.Status;

namespace V8Sharp.TestRunner.Suites;

/// <summary>Where things are and how the engine was built.</summary>
public sealed record SuiteContext(
    string V8Root,
    string Test262Root,
    IReadOnlyDictionary<string, object?> StatusVariables);

public abstract class TestSuite
{
    protected TestSuite(string name, SuiteContext context)
    {
        Name = name;
        Context = context;
        Root = Path.Combine(context.V8Root, "test", name);
    }

    public string Name { get; }
    public SuiteContext Context { get; }

    /// <summary>test/&lt;suite&gt;.</summary>
    public string Root { get; }

    public StatusFile? Status { get; private set; }

    /// <summary>Why the suite has no tests (data missing), or null.</summary>
    public virtual string? UnavailableReason => null;

    public static TestSuite Create(string name, SuiteContext context) => name switch
    {
        "mjsunit" => new MjsunitSuite(context),
        "test262" => new Test262Suite(context),
        "message" => new MessageSuite(context),
        "webkit" => new WebkitSuite(context),
        "mozilla" => new MozillaSuite(context),
        _ => throw new ArgumentException($"unknown suite '{name}' (supported: mjsunit, test262, message, webkit, mozilla)"),
    };

    public static readonly string[] AllSuites = ["mjsunit", "test262", "message", "webkit", "mozilla"];

    /// <summary>Loads the status file and lists the tests (all of them; the
    /// runner filters and skips).</summary>
    public List<TestCase> LoadTests()
    {
        if (UnavailableReason is not null) return [];
        Status = StatusFile.Load(Path.Combine(Root, Name + ".status"), Context.StatusVariables);
        var tests = new List<TestCase>();
        foreach (var (name, path) in ListTestFiles())
        {
            foreach (var t in CreateTests(name, path)) tests.Add(Finish(t));
        }
        return tests;
    }

    TestCase Finish(TestCase t)
    {
        t.ExpectedOutcomes = OutcomeSets.FromStatus(t.StatusOutcomes, t.Flags);
        if (t.StatusOutcomes.Contains(Outcome.Skip)) t.SkipReason ??= "SKIP in " + Name + ".status";
        if (t.SkipReason is null && OutcomeSets.FlagContradiction(t.Flags, t.Variant, Context.StatusVariables) is { } why)
        {
            // V8 expects d8 to reject contradictory flags (FAIL). Neither host
            // rejects them, so such a run says nothing: skip it.
            t.SkipReason = "flag contradiction: " + why;
        }
        t.OutProc.ExpectedOutcomes = t.ExpectedOutcomes;
        return t;
    }

    /// <summary>Status outcomes minus the flags, and the flags (outcomes starting with "--").</summary>
    protected (HashSet<string> Outcomes, List<string> Flags) StatusFor(string name, string variant = Variants.Default)
    {
        var all = Status!.GetOutcomes(name, variant);
        var outcomes = new HashSet<string>(StringComparer.Ordinal);
        var flags = new List<string>();
        foreach (var o in all.Order(StringComparer.Ordinal))
        {
            if (o.StartsWith("--", StringComparison.Ordinal)) flags.Add(o);
            else outcomes.Add(o);
        }
        return (outcomes, flags);
    }

    protected abstract IEnumerable<TestCase> CreateTests(string name, string path);

    // --- GenericTestLoader ---

    protected virtual IEnumerable<string> TestDirs => [Root];
    protected virtual IReadOnlySet<string> ExcludedFiles => new HashSet<string>();
    protected virtual IReadOnlySet<string> ExcludedDirs => new HashSet<string>();
    protected virtual IReadOnlyList<string> ExcludedSuffixes => [];
    protected virtual IReadOnlyList<string> Extensions => [".js", ".mjs"];

    /// <summary>Test name (relative path without extension, '/'-separated) and absolute path,
    /// in os.walk order with sorted directories and files.</summary>
    protected virtual IEnumerable<(string Name, string Path)> ListTestFiles()
    {
        foreach (var dir in TestDirs.Order(StringComparer.Ordinal))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Walk(dir))
            {
                string fileName = Path.GetFileName(file);
                string? ext = null;
                foreach (var e in Extensions) if (fileName.EndsWith(e, StringComparison.Ordinal)) { ext = e; break; }
                if (ext is null) continue;
                bool excluded = ExcludedFiles.Contains(fileName);
                foreach (var s in ExcludedSuffixes) excluded |= fileName.EndsWith(s, StringComparison.Ordinal);
                if (excluded) continue;
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                yield return (rel[..^ext.Length], file);
            }
        }
    }

    IEnumerable<string> Walk(string dir)
    {
        foreach (var f in Directory.GetFiles(dir).Order(StringComparer.Ordinal)) yield return f;
        foreach (var d in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            string n = Path.GetFileName(d);
            if (n.StartsWith('.') || ExcludedDirs.Contains(n)) continue;
            foreach (var f in Walk(d)) yield return f;
        }
    }

    /// <summary>A path relative to the V8 root, as run-tests.py passes files to d8.</summary>
    protected string Rel(string absolute) => Path.GetRelativePath(Context.V8Root, absolute).Replace('\\', '/');

    protected static string ReadSource(string path) => File.ReadAllText(path, Encoding.Latin1);
}

/// <summary>test/mjsunit/testcfg.py.</summary>
public sealed partial class MjsunitSuite(SuiteContext context) : TestSuite("mjsunit", context)
{
    protected override IReadOnlySet<string> ExcludedFiles => new HashSet<string> { "mjsunit.js", "mjsunit_numfuzz.js" };

    [GeneratedRegex(@"//\s+Files:(.*)")]
    private static partial Regex FilesPattern();

    [GeneratedRegex(@"//\s+Environment Variables:(.*)")]
    private static partial Regex EnvPattern();

    [GeneratedRegex("^// NO HARNESS$", RegexOptions.Multiline)]
    private static partial Regex NoHarnessPattern();

    protected override IEnumerable<TestCase> CreateTests(string name, string path)
    {
        string source = ReadSource(path);
        var files = new List<string>();
        if (!NoHarnessPattern().IsMatch(source)) files.Add(Rel(Path.Combine(Root, "mjsunit.js")));
        foreach (Match m in FilesPattern().Matches(source))
        {
            foreach (var f in m.Groups[1].Value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                files.Add(Rel(Path.GetFullPath(Path.Combine(Context.V8Root, f))));
            }
        }
        files.Add(Rel(path));
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (EnvPattern().Match(source) is { Success: true } em)
        {
            foreach (var pair in em.Groups[1].Value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) env[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        var (outcomes, statusFlags) = StatusFor(name);
        var flags = TestCase.ParseSourceFlags(source);
        flags.AddRange(statusFlags);
        yield return new TestCase
        {
            Suite = Name,
            Name = name,
            SourcePath = path,
            Files = files,
            Flags = flags,
            Env = env,
            StatusOutcomes = outcomes,
            OutProc = new OutputProcessor(OutcomeSets.Pass),
        };
    }
}

/// <summary>test/test262/testcfg.py.</summary>
public sealed class Test262Suite(SuiteContext context) : TestSuite("test262", context)
{
    /// <summary>FEATURE_FLAGS: V8 flags that enable a staged feature.</summary>
    static readonly Dictionary<string, string> s_featureFlags = new(StringComparer.Ordinal)
    {
        ["FinalizationRegistry"] = "--harmony-weak-refs-with-cleanup-some",
        ["WeakRef"] = "--harmony-weak-refs-with-cleanup-some",
        ["host-gc-required"] = "--expose-gc-as=v8GC",
        ["IsHTMLDDA"] = "--allow-natives-syntax",
        ["import-assertions"] = "--harmony-import-assertions",
        ["Temporal"] = "--harmony-temporal",
        ["array-find-from-last"] = "--harmony-array-find-last",
        ["ShadowRealm"] = "--harmony-shadow-realm",
        ["String.prototype.isWellFormed"] = "--harmony-string-is-well-formed",
        ["String.prototype.toWellFormed"] = "--harmony-string-is-well-formed",
        ["json-parse-with-source"] = "--harmony-json-parse-with-source",
        ["iterator-sequencing"] = "--js-iterator-sequencing",
        ["iterator-helpers"] = "--harmony-iterator-helpers",
        ["set-methods"] = "--harmony-set-methods",
        ["import-attributes"] = "--harmony-import-attributes",
        ["regexp-modifiers"] = "--js-regexp-modifiers",
        ["decorators"] = "--js-decorators",
        ["source-phase-imports"] = "--js-source-phase-imports --allow-natives-syntax",
        ["nonextensible-applies-to-private"] = "--js-nonextensible-applies-to-private",
        ["immutable-arraybuffer"] = "--js-immutable-arraybuffer",
        ["import-defer"] = "--js-defer-import-eval",
        ["Iterator.prototype.join"] = "--js-iterator-join",
        ["joint-iteration"] = "--js-joint-iteration",
        ["import-text"] = "--js-import-text",
        ["import-bytes"] = "--js-import-bytes",
        ["iterator-includes"] = "--js-iterator-includes",
    };

    static readonly HashSet<string> s_nativeFiles = ["detachArrayBuffer.js"];

    string DataRoot => Context.Test262Root;
    string TestRoot => Path.Combine(DataRoot, "test");
    string HarnessRoot => Path.Combine(DataRoot, "harness");
    string LocalTestRoot => Path.Combine(Root, "local-tests", "test");

    public override string? UnavailableReason =>
        Directory.Exists(TestRoot) ? null : $"test262 data not found at {DataRoot} (use --test262-root)";

    protected override IEnumerable<string> TestDirs => [TestRoot, LocalTestRoot];
    protected override IReadOnlyList<string> ExcludedSuffixes => ["_FIXTURE.js"];

    protected override IReadOnlySet<string> ExcludedDirs =>
        Context.StatusVariables.TryGetValue("i18n", out var i) && i is false ? new HashSet<string> { "intl402", "Intl402" } : new HashSet<string>();

    protected override IReadOnlyList<string> Extensions => [".js"];

    readonly HashSet<string> _localStaging = new(StringComparer.Ordinal);

    protected override IEnumerable<(string Name, string Path)> ListTestFiles()
    {
        foreach (var (name, path) in base.ListTestFiles())
        {
            // A staging test with a local implementation runs once, from local-tests.
            if (name.StartsWith("staging", StringComparison.Ordinal) && File.Exists(Path.Combine(LocalTestRoot, name + ".js")))
            {
                if (!_localStaging.Add(name)) continue;
            }
            yield return (name, path);
        }
    }

    protected override IEnumerable<TestCase> CreateTests(string name, string listedPath)
    {
        // _get_source_path: local-tests override data/test.
        string local = Path.Combine(LocalTestRoot, name + ".js");
        string path = File.Exists(local) ? local : Path.Combine(TestRoot, name + ".js");
        string source = File.ReadAllText(path, Encoding.UTF8);
        var fm = Test262Frontmatter.Parse(source) ?? Test262Frontmatter.FromYaml("");
        var (outcomes, statusFlags) = StatusFor(name);

        var files = new List<string>();
        if (!fm.HasFlag("raw"))
        {
            files.Add(Path.Combine(HarnessRoot, "sta.js"));
            files.Add(Path.Combine(HarnessRoot, "assert.js"));
            files.Add(Path.Combine(Root, "harness-adapt.js"));
        }
        if (name.StartsWith("built-ins/Atomics/", StringComparison.Ordinal)) files.Add(Path.Combine(Root, "harness-agent.js"));
        if (fm.Features.Contains("IsHTMLDDA")) files.Add(Path.Combine(Root, "harness-ishtmldda.js"));
        if (fm.Features.Contains("source-phase-imports")) files.Add(Path.Combine(Root, "harness-abstractmodulesource.js"));
        if (outcomes.Contains(Outcome.FailPhaseOnly)) files.Add(Path.Combine(Root, "harness-adapt-donotevaluate.js"));
        if (fm.HasFlag("async")) files.Add(Path.Combine(Root, "harness-done.js"));
        foreach (var inc in fm.Includes)
        {
            files.Add(Path.Combine(s_nativeFiles.Contains(inc) ? Root : HarnessRoot, inc));
        }
        for (int i = 0; i < files.Count; i++) files[i] = Rel(files[i]);
        if (fm.HasFlag("module")) files.Add("--module");
        if (fm.HasFlag("CanBlockIsFalse")) files.Add("--no-can-block");
        files.Add(Rel(path));

        var suiteFlags = new List<string> { "--ignore-unhandled-promises" };
        if (fm.IsNegative) suiteFlags.Add("--throws");
        if (fm.Includes.Contains("detachArrayBuffer.js")) suiteFlags.Add("--allow-natives-syntax");
        foreach (var (feature, flagString) in s_featureFlags)
        {
            if (fm.Features.Contains(feature)) suiteFlags.AddRange(flagString.Split(' '));
        }
        suiteFlags.Add("--no-arguments");

        bool isAsync = fm.HasFlag("async");
        // harness-agent.js implements $262.agent on d8's Worker (a second isolate
        // on another thread sharing SharedArrayBuffers), which the shell lacks.
        string? skip = name.StartsWith("built-ins/Atomics/", StringComparison.Ordinal) && source.Contains("$262.agent", StringComparison.Ordinal)
            ? "needs $262.agent (d8 Worker), which the test host does not provide"
            : null;
        // VariantsGenerator: noStrict runs sloppy, onlyStrict runs strict, the rest both.
        var modes = fm.HasFlag("noStrict") ? new[] { false } : fm.HasFlag("onlyStrict") ? [true] : [false, true];
        foreach (bool strict in modes)
        {
            var flags = new List<string>(suiteFlags);
            flags.AddRange(statusFlags);
            if (strict) flags.Add("--use-strict");
            yield return new TestCase
            {
                Suite = Name,
                Name = name,
                VariantSuffix = strict ? "@strict" : "",
                SourcePath = path,
                Files = files,
                Flags = flags,
                StatusOutcomes = outcomes,
                OutProc = new Test262OutputProcessor(OutcomeSets.Pass, fm.NegativeType, negative: false, isAsync),
                SkipReason = skip,
            };
        }
    }
}

/// <summary>test/message/testcfg.py.</summary>
public sealed class MessageSuite(SuiteContext context) : TestSuite("message", context)
{
    static readonly HashSet<string> s_invalidFlags = ["--enable-slow-asserts"];

    protected override IEnumerable<TestCase> CreateTests(string name, string path)
    {
        string source = ReadSource(path);
        var (outcomes, statusFlags) = StatusFor(name);
        var flags = TestCase.ParseSourceFlags(source);
        flags.AddRange(statusFlags);
        flags.RemoveAll(s_invalidFlags.Contains);
        bool expectedFail = name.Split('/').Contains("fail");
        yield return new TestCase
        {
            Suite = Name,
            Name = name,
            SourcePath = path,
            Files = [Rel(path)],
            Flags = flags,
            StatusOutcomes = outcomes,
            OutProc = new MessageOutputProcessor(OutcomeSets.Pass, Path.Combine(Root, name), expectedFail),
        };
    }
}

/// <summary>test/webkit/testcfg.py.</summary>
public sealed partial class WebkitSuite(SuiteContext context) : TestSuite("webkit", context)
{
    protected override IReadOnlySet<string> ExcludedDirs => new HashSet<string> { "resources" };

    [GeneratedRegex(@"//\s+Files:(.*)")]
    private static partial Regex FilesPattern();

    protected override IEnumerable<TestCase> CreateTests(string name, string path)
    {
        string source = ReadSource(path);
        var files = new List<string>();
        foreach (Match m in FilesPattern().Matches(source))
        {
            foreach (var f in m.Groups[1].Value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                files.Add(Rel(Path.Combine(Context.V8Root, f)));
            }
        }
        files.Add(Rel(Path.Combine(Root, "resources", "standalone-pre.js")));
        files.Add(Rel(path));
        files.Add(Rel(Path.Combine(Root, "resources", "standalone-post.js")));
        var (outcomes, statusFlags) = StatusFor(name);
        var flags = TestCase.ParseSourceFlags(source);
        flags.AddRange(statusFlags);
        yield return new TestCase
        {
            Suite = Name,
            Name = name,
            SourcePath = path,
            Files = files,
            Flags = flags,
            StatusOutcomes = outcomes,
            OutProc = new WebkitOutputProcessor(OutcomeSets.Pass, Path.Combine(Root, name + "-expected.txt")),
        };
    }
}

/// <summary>test/mozilla/testcfg.py. The data (test/mozilla/data) is not in
/// this checkout; the suite reports itself unavailable when it is missing.</summary>
public sealed class MozillaSuite(SuiteContext context) : TestSuite("mozilla", context)
{
    static readonly string[] s_testDirs = ["ecma", "ecma_2", "ecma_3", "js1_1", "js1_2", "js1_3", "js1_4", "js1_5"];

    string DataRoot => Path.Combine(Root, "data");

    public override string? UnavailableReason =>
        Directory.Exists(DataRoot) ? null : "test/mozilla/data is not checked out";

    protected override IEnumerable<string> TestDirs => s_testDirs.Select(d => Path.Combine(DataRoot, d));
    protected override IReadOnlySet<string> ExcludedFiles => new HashSet<string> { "browser.js", "shell.js", "jsref.js", "template.js" };
    protected override IReadOnlySet<string> ExcludedDirs => new HashSet<string> { "CVS", ".svn" };
    protected override IReadOnlyList<string> Extensions => [".js"];

    protected override IEnumerable<(string Name, string Path)> ListTestFiles()
    {
        foreach (var (_, path) in base.ListTestFiles())
        {
            string rel = Path.GetRelativePath(DataRoot, path).Replace('\\', '/');
            yield return (rel[..^3], path);
        }
    }

    protected override IEnumerable<TestCase> CreateTests(string name, string path)
    {
        var files = new List<string> { Rel(Path.Combine(Root, "mozilla-shell-emulation.js")) };
        var parts = name.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string shell = Path.Combine(DataRoot, string.Join('/', parts[..i]), "shell.js");
            if (File.Exists(shell)) files.Add(Rel(shell));
        }
        files.Add(Rel(path));
        var (outcomes, statusFlags) = StatusFor(name);
        var flags = new List<string> { "--expose-gc" };
        flags.AddRange(statusFlags);
        yield return new TestCase
        {
            Suite = Name,
            Name = name,
            SourcePath = path,
            Files = files,
            Flags = flags,
            StatusOutcomes = outcomes,
            OutProc = new MozillaOutputProcessor(OutcomeSets.Pass, name.EndsWith("-n", StringComparison.Ordinal)),
        };
    }
}
