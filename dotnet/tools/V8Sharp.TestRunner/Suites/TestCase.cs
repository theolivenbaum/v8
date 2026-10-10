// Port of tools/testrunner/objects/testcase.py (the parts the standard runner
// uses for the default variant).
using System.Text;
using System.Text.RegularExpressions;
using V8Sharp.TestRunner.OutProc;
using V8Sharp.TestRunner.Status;

namespace V8Sharp.TestRunner.Suites;

public sealed partial class TestCase
{
    /// <summary>The suite name: mjsunit, test262 ...</summary>
    public required string Suite { get; init; }

    /// <summary>The name status files use: the path under the suite without extension.</summary>
    public required string Name { get; init; }

    /// <summary>A suffix for runs of the same test in another mode ("@strict"); "" otherwise.</summary>
    public string VariantSuffix { get; init; } = "";

    /// <summary>The testing variant (only "default" is run for now).</summary>
    public string Variant { get; init; } = Variants.Default;

    /// <summary>The unique id used in results and expectation files: <c>name[@mode]</c>.</summary>
    public string Id => Name + VariantSuffix;

    public string FullId => Suite + "/" + Id;

    /// <summary>The test file, absolute.</summary>
    public required string SourcePath { get; init; }

    /// <summary>d8's argv without flags: files (relative to the V8 root, as
    /// run-tests.py passes them), <c>--module</c> markers.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>All flags, in V8's order: suite, source (<c>// Flags:</c>),
    /// status-file flags; V8 flags and d8 options mixed as on d8's command line.</summary>
    public required IReadOnlyList<string> Flags { get; set; }

    /// <summary><c>// Environment Variables:</c> (mjsunit).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Outcomes and modifiers from the status file (flags removed).</summary>
    public required IReadOnlySet<string> StatusOutcomes { get; init; }

    /// <summary>The expected outcomes (PASS, FAIL, CRASH, TIMEOUT).</summary>
    public IReadOnlyList<string> ExpectedOutcomes { get; set; } = OutcomeSets.Pass;

    /// <summary>Why the test is not run; null when it runs.</summary>
    public string? SkipReason { get; set; }

    public required OutputProcessor OutProc { get; set; }

    public bool IsSlow => StatusOutcomes.Contains(Outcome.Slow);

    /// <summary>TestCase._get_timeout.</summary>
    public TimeSpan Timeout(TimeSpan baseTimeout)
    {
        double t = baseTimeout.TotalSeconds;
        if (Flags.Contains("--jitless")) t *= 2;
        if (Flags.Contains("--no-turbofan")) t *= 2;
        if (IsSlow) t *= 4;
        return TimeSpan.FromSeconds(t);
    }

    /// <summary>The d8 command line (flags then files), as the worker receives it.</summary>
    public IReadOnlyList<string> CommandLine => [.. Flags, .. Files];

    /// <summary>The worker-process grouping key: tests that need the same
    /// process-global V8 state (V8 flags, environment) share a process.</summary>
    public string GroupKey { get; set; } = "";

    public override string ToString() => FullId;

    // --- helpers shared by the suites ---

    [GeneratedRegex(@"//\s+Flags:(.*)")]
    private static partial Regex FlagsPattern();

    /// <summary>TestCase._parse_source_flags: every <c>// Flags:</c> line, shlex-split.</summary>
    public static List<string> ParseSourceFlags(string source)
    {
        var flags = new List<string>();
        foreach (Match m in FlagsPattern().Matches(source)) flags.AddRange(ShlexSplit(m.Groups[1].Value.Trim()));
        return flags;
    }

    /// <summary>Python's shlex.split (POSIX mode): whitespace separated, with
    /// single and double quotes and backslash escapes.</summary>
    public static List<string> ShlexSplit(string s)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inToken = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(cur.ToString()); cur.Clear(); inToken = false; }
                continue;
            }
            inToken = true;
            if (c == '\'')
            {
                int j = s.IndexOf('\'', i + 1);
                if (j < 0) j = s.Length;
                cur.Append(s, i + 1, j - i - 1);
                i = j;
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`') i++;
                    cur.Append(s[i]);
                    i++;
                }
            }
            else if (c == '\\' && i + 1 < s.Length)
            {
                cur.Append(s[++i]);
            }
            else
            {
                cur.Append(c);
            }
        }
        if (inToken) result.Add(cur.ToString());
        return result;
    }
}

public static class OutcomeSets
{
    public static readonly IReadOnlyList<string> Pass = [Outcome.Pass];
    public static readonly IReadOnlyList<string> Fail = [Outcome.Fail];

    /// <summary>TestCase._parse_status_file_outcomes.</summary>
    public static IReadOnlyList<string> FromStatus(IReadOnlySet<string> outcomes, IReadOnlyList<string> flags)
    {
        if (outcomes.Contains(Outcome.FailSloppy) && !flags.Contains("--use-strict")) return Fail;
        var expected = new List<string>();
        if (outcomes.Contains(Outcome.Fail) || outcomes.Contains(Outcome.FailOk)) expected.Add(Outcome.Fail);
        if (outcomes.Contains(Outcome.Crash)) expected.Add(Outcome.Crash);
        if (expected.Count > 0 && outcomes.Contains(Outcome.Pass)) expected.Add(Outcome.Pass);
        return expected.Count > 0 ? expected : Pass;
    }

    /// <summary>The flag-contradiction rules of TestCase.expected_outcomes. Returns
    /// the reason when the flags contradict each other or the build (d8 would
    /// refuse them), else null.</summary>
    public static string? FlagContradiction(IReadOnlyList<string> flags, string variant, IReadOnlyDictionary<string, object?> buildVariables)
    {
        static string Normalize(string f) => f.Replace('_', '-').Replace("--no-", "--no", StringComparison.Ordinal);
        var testFlags = new List<string>();
        foreach (var f in flags) if (f.StartsWith("--", StringComparison.Ordinal)) testFlags.Add(Normalize(f));
        if (testFlags.Contains("--fuzzing")) return null;
        int ignore = testFlags.IndexOf(Normalize("--flag-processing-mode=ignore-contradictions"));
        if (ignore >= 0) testFlags = testFlags[..ignore];

        string? Find(string conflicting)
        {
            conflicting = Normalize(conflicting);
            if (testFlags.Contains(conflicting)) return conflicting;
            if (conflicting.EndsWith('*'))
            {
                string p = conflicting[..^1];
                return testFlags.Find(f => f.StartsWith(p, StringComparison.Ordinal));
            }
            return null;
        }
        string? Check(IEnumerable<string> incompatible, string rule)
        {
            foreach (var f in incompatible)
            {
                if (Find(f) is not null) return $"{rule}: contradiction with {f}";
            }
            return null;
        }

        var negated = new List<string>();
        foreach (var f in testFlags) if (Variants.NegateFlag(f) is { } n) negated.Add(n);
        if (Check(negated, "Flag negations") is { } r1) return r1;
        if (Variants.IncompatibleFlagsPerVariant.TryGetValue(variant, out var perVariant) &&
            Check(perVariant, $"INCOMPATIBLE_FLAGS_PER_VARIANT[\"{variant}\"]") is { } r2)
        {
            return r2;
        }
        foreach (var (var, incompatible) in Variants.IncompatibleFlagsPerBuildVariable)
        {
            bool negate = var.StartsWith('!');
            string name = negate ? var[1..] : var;
            bool value = buildVariables.TryGetValue(name, out var v) && PyExpression.Truthy(v);
            if (value != negate && Check(incompatible, $"INCOMPATIBLE_FLAGS_PER_BUILD_VARIABLE[\"{var}\"]") is { } r3) return r3;
        }
        return null;
    }
}
