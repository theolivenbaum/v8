// Port of tools/testrunner/local/statusfile.py.
namespace V8Sharp.TestRunner.Status;

/// <summary>The status-file keywords (outcomes and modifiers).</summary>
public static class Outcome
{
    public const string Fail = "FAIL";
    public const string Pass = "PASS";
    public const string Timeout = "TIMEOUT";
    public const string Crash = "CRASH";
    public const string FailOk = "FAIL_OK";
    public const string FailSloppy = "FAIL_SLOPPY";
    public const string Heavy = "HEAVY";
    public const string Skip = "SKIP";
    public const string Slow = "SLOW";
    public const string NoVariants = "NO_VARIANTS";
    public const string FailPhaseOnly = "FAIL_PHASE_ONLY";
    public const string FuzzRare = "FUZZ_RARE";
    public const string Always = "ALWAYS";
}

/// <summary>
/// A parsed <c>.status</c> file: per-variant exact rules and prefix rules
/// (keys ending in <c>*</c>), evaluated against a fixed set of build variables.
/// </summary>
public sealed class StatusFile
{
    // Variant "" holds the variant-independent rules.
    readonly Dictionary<string, Dictionary<string, HashSet<string>>> _rules = [];
    readonly Dictionary<string, Dictionary<string, HashSet<string>>> _prefixRules = [];

    /// <summary>Warnings produced while reading (unknown identifiers ...).</summary>
    public List<string> Warnings { get; } = [];

    public IReadOnlyDictionary<string, object?> Variables { get; }

    public static StatusFile Load(string path, IReadOnlyDictionary<string, object?> buildVariables) =>
        new(File.ReadAllText(path), buildVariables, path);

    public StatusFile(string content, IReadOnlyDictionary<string, object?> buildVariables, string? path = null)
    {
        Variables = WithDefaultVariables(buildVariables);
        _rules[""] = [];
        _prefixRules[""] = [];
        foreach (var v in Variants.AllVariants)
        {
            _rules[v] = [];
            _prefixRules[v] = [];
        }

        object? parsed;
        try
        {
            parsed = PyLiteral.Parse(content);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{path}: {e.Message}", e);
        }
        if (parsed is not List<object?> sections) throw new FormatException($"{path}: a status file is a list of sections");

        foreach (var s in sections)
        {
            if (s is not List<object?> { Count: 2 } section || section[0] is not string condition ||
                section[1] is not List<KeyValuePair<string, object?>> rules)
            {
                throw new FormatException($"{path}: a section is [condition, {{rules}}]");
            }
            object? exp;
            try
            {
                exp = PyExpression.Evaluate(condition, Variables);
            }
            catch (VariantExpressionException)
            {
                // Variant-dependent: evaluate for every variant.
                foreach (var variant in Variants.AllVariants)
                {
                    var withVariant = new Dictionary<string, object?>(Variables) { ["variant"] = variant };
                    if (EvaluateBool(condition, withVariant) is true)
                    {
                        ReadSection(rules, withVariant, _rules[variant], _prefixRules[variant]);
                    }
                }
                continue;
            }
            catch (KeyNotFoundException e)
            {
                Warnings.Add($"{path}: section '{condition}': {e.Message}; treated as False");
                continue;
            }
            if (exp is true) ReadSection(rules, Variables, _rules[""], _prefixRules[""]);
            else if (exp is not false) Warnings.Add($"{path}: section '{condition}' does not evaluate to a boolean");
        }
    }

    bool? EvaluateBool(string condition, IReadOnlyDictionary<string, object?> vars)
    {
        try
        {
            var v = PyExpression.Evaluate(condition, vars);
            return v as bool?;
        }
        catch (KeyNotFoundException e)
        {
            Warnings.Add($"condition '{condition}': {e.Message}; treated as False");
            return false;
        }
    }

    /// <summary>ReadStatusFile adds VARIABLES: ALWAYS, the arch/mode/system
    /// keywords and the variant names, each evaluating to its own name.</summary>
    static Dictionary<string, object?> WithDefaultVariables(IReadOnlyDictionary<string, object?> build)
    {
        var vars = new Dictionary<string, object?>(build) { [Outcome.Always] = true };
        string[] keywords =
        [
            "debug", "release", "big", "little", "android", "arm", "arm64", "ia32", "mips64", "mips64el", "x64", "ppc64",
            "s390x", "macos", "windows", "linux", "aix", "r1", "r2", "r3", "r5", "r6", "riscv32", "riscv64", "loong64",
            "zos", "bullhead", "panther",
        ];
        foreach (var k in keywords) vars[k] = k;
        foreach (var v in Variants.AllVariants) vars[v] = v;
        return vars;
    }

    void ReadSection(List<KeyValuePair<string, object?>> section, IReadOnlyDictionary<string, object?> vars,
        Dictionary<string, HashSet<string>> rules, Dictionary<string, HashSet<string>> prefixRules)
    {
        foreach (var (rule, outcomes) in section)
        {
            if (rule.EndsWith('*')) ParseOutcomeList(rule[..^1], outcomes, vars, prefixRules);
            else ParseOutcomeList(rule, outcomes, vars, rules);
        }
    }

    static bool JoinsPassAndFail(HashSet<string> a, HashSet<string> b) =>
        a.Contains(Outcome.Pass) && !(a.Contains(Outcome.Fail) || a.Contains(Outcome.FailOk)) &&
        (b.Contains(Outcome.Fail) || b.Contains(Outcome.FailOk));

    void ParseOutcomeList(string rule, object? outcomes, IReadOnlyDictionary<string, object?> vars, Dictionary<string, HashSet<string>> target)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var items = outcomes is string single ? [single] : outcomes as List<object?> ?? [];
        foreach (var item in items)
        {
            if (item is string s)
            {
                result.Add(s);
            }
            else if (item is List<object?> { Count: > 0 } cond && cond[0] is string condition)
            {
                bool exp;
                try
                {
                    var value = PyExpression.Evaluate(condition, vars);
                    if (value is not bool) Warnings.Add($"rule '{rule}': '{condition}' is not boolean");
                    exp = PyExpression.Truthy(value);
                }
                catch (VariantExpressionException)
                {
                    Warnings.Add($"rule '{rule}': nested variant expressions are not supported");
                    continue;
                }
                catch (KeyNotFoundException e)
                {
                    Warnings.Add($"rule '{rule}': {e.Message}; treated as False");
                    continue;
                }
                if (!exp) continue;
                for (int i = 1; i < cond.Count; i++)
                {
                    if (cond[i] is string o) result.Add(o);
                }
            }
        }
        if (result.Count == 0) return;
        if (target.TryGetValue(rule, out var existing))
        {
            // A FAIL without PASS in one rule has precedence over a single PASS
            // (without FAIL) in another.
            if (JoinsPassAndFail(existing, result)) existing.Remove(Outcome.Pass);
            if (JoinsPassAndFail(result, existing)) result.Remove(Outcome.Pass);
            existing.UnionWith(result);
        }
        else
        {
            target[rule] = result;
        }
    }

    /// <summary>StatusFile.get_outcomes: merges the variant-dependent and
    /// independent rules, exact and prefix, that apply to <paramref name="testName"/>.</summary>
    public HashSet<string> GetOutcomes(string testName, string? variant = null)
    {
        var outcomes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in variant is null or "" ? [""] : new[] { variant, "" })
        {
            if (_rules.TryGetValue(key, out var rules) && rules.TryGetValue(testName, out var o)) outcomes.UnionWith(o);
            if (_prefixRules.TryGetValue(key, out var prefixes))
            {
                foreach (var (prefix, po) in prefixes)
                {
                    if (testName.StartsWith(prefix, StringComparison.Ordinal)) outcomes.UnionWith(po);
                }
            }
        }
        return outcomes;
    }

    /// <summary>All rule keys (exact and prefix) for one variant ("" = independent).</summary>
    public IEnumerable<(string Rule, bool IsPrefix, IReadOnlySet<string> Outcomes)> Rules(string variant = "")
    {
        foreach (var (k, v) in _rules[variant]) yield return (k, false, v);
        foreach (var (k, v) in _prefixRules[variant]) yield return (k, true, v);
    }
}
