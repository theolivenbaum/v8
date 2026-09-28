using System.Text;

namespace V8Sharp.RegExp.Tests.Differential;

/// <summary>
/// Differential test: every regexp found in V8's and test262's RegExp tests is
/// run against the string literals of the same file, through the real V8 and
/// through V8Sharp.RegExp, and the results (match index, captures, named
/// groups, or the SyntaxError message) are compared exactly. A report is
/// written under dotnet/artifacts/regexp-differential/.
/// </summary>
public class DifferentialTests
{
    const int kMaxPatternsPerFile = 400;
    const int kMaxSubjectsPerFile = 40;
    const int kMaxSubjectLength = 2000;

    static readonly string[] s_extraSubjects =
    [
        "", "a", "abc", "aBc ABC abc", "foo bar baz", "The Quick Brown Fox", "0123456789", "a\nb\r\nc",
        "éÉ İiıI ſsS KkK", "x😀y\ud800z\udc00", "  \t ﻿ ",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab",
    ];

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "test", "mjsunit")) &&
                Directory.Exists(Path.Combine(dir, "dotnet")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("repository root not found");
    }

    internal static string? Test262Root()
    {
        string inRepo = Path.Combine(RepoRoot(), "test", "test262", "data", "test");
        if (Directory.Exists(inRepo)) return inRepo;
        const string external = "/home/user/theolivenbaum/test262/test";
        return Directory.Exists(external) ? external : null;
    }

    internal sealed record Mismatch(string File, string Pattern, string Flags, string Subject, string Oracle,
        string Ours, string? KnownReason);

    // Files whose expectations changed between the oracle (V8 14.7) and this
    // tree: the port follows this tree's tests.
    static readonly Dictionary<string, string> s_oracleOutdatedFiles = new()
    {
        ["regress/regress-regexp-lookbehind-sort-alternatives.js"] =
            "fixed after V8 14.7: lookbehind alternatives are no longer sorted",
    };

    // Properties of strings whose sequences come from the emoji data files
    // (Emoji 17 here, older in the oracle).
    static readonly string[] s_emojiStringProperties =
    [
        "Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji", "RGI_Emoji_Flag_Sequence",
        "RGI_Emoji_Modifier_Sequence", "RGI_Emoji_Tag_Sequence", "RGI_Emoji_ZWJ_Sequence",
    ];

    // Explains a mismatch caused by the oracle being an older V8 (14.7, with
    // an older Unicode version), or returns null.
    static string? KnownReason(DifferentialRunner runner, string file, CorpusPattern p, string subject, string oracle)
    {
        if (s_oracleOutdatedFiles.TryGetValue(file, out string? reason)) return reason;
        bool usesProperties = p.Source.Contains("\\p{", StringComparison.Ordinal) ||
                              p.Source.Contains("\\P{", StringComparison.Ordinal);
        if (!usesProperties) return null;
        if (oracle.EndsWith(": Invalid property name", StringComparison.Ordinal) ||
            oracle.EndsWith(": Invalid property name in character class", StringComparison.Ordinal))
        {
            return "Unicode 17 property value unknown to the oracle";
        }
        if (runner.HasCodePointUnassignedInOracle(subject)) return "Unicode 17 code point in the subject";
        if (p.Flags.Contains('v'))
        {
            foreach (string property in s_emojiStringProperties)
            {
                if (p.Source.Contains("{" + property + "}", StringComparison.Ordinal)) return "Emoji 17 sequences";
            }
        }
        return null;
    }

    internal sealed class Stats
    {
        public int Files;
        public int Patterns;
        public int Triples;
        public int Agree;
        public int OracleTimeouts;
        public readonly List<Mismatch> Mismatches = [];
    }

    internal static Stats RunCorpus(IEnumerable<string> files, string reportName)
    {
        var stats = new Stats();
        using var runner = new DifferentialRunner();
        string progressDir = Path.Combine(RepoRoot(), "dotnet", "artifacts", "regexp-differential");
        Directory.CreateDirectory(progressDir);
        string progressFile = Path.Combine(progressDir, reportName + ".progress");
        string current = "";
        using var progress = new Timer(_ => File.WriteAllText(progressFile, Volatile.Read(ref current)), null, 2000, 2000);
        foreach (string file in files)
        {
            string src = File.ReadAllText(file);
            (List<CorpusPattern> patterns, List<string> strings) = JsCorpusScanner.Scan(src);
            if (patterns.Count == 0) continue;
            stats.Files++;
            var subjects = new List<string>();
            var seenSubjects = new HashSet<string>();
            foreach (string s in strings)
            {
                if (s.Length <= kMaxSubjectLength && subjects.Count < kMaxSubjectsPerFile && seenSubjects.Add(s))
                {
                    subjects.Add(s);
                }
            }
            foreach (string s in s_extraSubjects)
            {
                if (seenSubjects.Add(s)) subjects.Add(s);
            }
            var seenPatterns = new HashSet<CorpusPattern>();
            string relative = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);
            foreach (CorpusPattern p in patterns)
            {
                if (seenPatterns.Count >= kMaxPatternsPerFile) break;
                if (!seenPatterns.Add(p)) continue;
                stats.Patterns++;
                var patternSubjects = new List<string>(subjects);
                if (p.Source.Length <= kMaxSubjectLength && !seenSubjects.Contains(p.Source))
                {
                    patternSubjects.Add(p.Source);
                }
                foreach (string subject in patternSubjects)
                {
                    stats.Triples++;
                    Volatile.Write(ref current, $"oracle {relative}: /{Show(p.Source)}/{p.Flags} on \"{Show(subject)}\"\n");
                    string? oracle = runner.RunOracle(p.Source, p.Flags, subject);
                    if (oracle is null)
                    {
                        stats.OracleTimeouts++;
                        continue;
                    }
                    string ours;
                    Volatile.Write(ref current, $"{relative}: /{Show(p.Source)}/{p.Flags} on \"{Show(subject)}\"\n");
                    try
                    {
                        ours = runner.RunOurs(p.Source, p.Flags, subject);
                    }
                    catch (Exception e)
                    {
                        ours = "CRASH: " + e.GetType().Name + ": " + e.Message;
                    }
                    if (oracle == ours)
                    {
                        stats.Agree++;
                    }
                    else
                    {
                        stats.Mismatches.Add(new Mismatch(relative, p.Source, p.Flags, subject, oracle, ours,
                            KnownReason(runner, relative, p, subject, oracle)));
                        // A pattern that disagrees on syntax disagrees on every subject.
                        if (oracle.StartsWith("E:", StringComparison.Ordinal) ||
                            ours.StartsWith("E:", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
            }
        }
        WriteReport(stats, reportName);
        return stats;
    }

    static string Show(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Length > 300 ? s[..300] + "..." : s)
        {
            if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static void WriteReport(Stats stats, string reportName)
    {
        string dir = Path.Combine(RepoRoot(), "dotnet", "artifacts", "regexp-differential");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append($"files {stats.Files}, patterns {stats.Patterns}, triples {stats.Triples}, ");
        int known = stats.Mismatches.Count(m => m.KnownReason is not null);
        sb.Append($"agree {stats.Agree}, mismatches {stats.Mismatches.Count} ({known} explained by the oracle's ");
        sb.Append($"older V8/Unicode version), oracle timeouts {stats.OracleTimeouts}\n");
        int compared = stats.Agree + stats.Mismatches.Count;
        if (compared > 0)
        {
            sb.Append($"agreement {100.0 * stats.Agree / compared:F3}%, ");
            sb.Append($"excluding explained mismatches {100.0 * (stats.Agree + known) / compared:F3}%\n\n");
        }
        foreach (Mismatch m in stats.Mismatches.OrderBy(m => m.KnownReason is not null))
        {
            if (m.KnownReason is not null) sb.Append($"[known: {m.KnownReason}] ");
            sb.Append($"{m.File}: /{Show(m.Pattern)}/{m.Flags} on \"{Show(m.Subject)}\"\n");
            sb.Append($"  oracle: {Show(m.Oracle)}\n");
            sb.Append($"  ours:   {Show(m.Ours)}\n");
        }
        File.WriteAllText(Path.Combine(dir, reportName + ".txt"), sb.ToString());
    }

    static IEnumerable<string> Files(string dir, string pattern, SearchOption option = SearchOption.TopDirectoryOnly) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern, option).Order(StringComparer.Ordinal)
            : [];

    static void Check(Stats stats, string name)
    {
        int compared = stats.Agree + stats.Mismatches.Count;
        Assert.True(compared > 0, name + ": nothing compared");
        var sb = new StringBuilder();
        List<Mismatch> unexplained = stats.Mismatches.Where(m => m.KnownReason is null).ToList();
        foreach (Mismatch m in unexplained.Take(20))
        {
            sb.Append($"{m.File}: /{Show(m.Pattern)}/{m.Flags} on \"{Show(m.Subject)}\": oracle {Show(m.Oracle)} ours {Show(m.Ours)}\n");
        }
        Assert.True(unexplained.Count == 0, $"{name}: {unexplained.Count} unexplained mismatches of {compared}\n{sb}");
    }

    [Fact]
    public void MjsunitRegExp()
    {
        string root = Path.Combine(RepoRoot(), "test", "mjsunit");
        IEnumerable<string> files = Files(root, "regexp*.js")
            .Concat(Files(Path.Combine(root, "es6"), "*regexp*.js"))
            .Concat(Files(Path.Combine(root, "regress"), "*regexp*.js", SearchOption.AllDirectories))
            .Concat(Files(Path.Combine(root, "es6"), "unicode-regexp*.js"));
        Check(RunCorpus(files, "mjsunit"), "mjsunit");
    }

    [Fact]
    public void MjsunitHarmonyRegExp()
    {
        string root = Path.Combine(RepoRoot(), "test", "mjsunit", "harmony");
        Check(RunCorpus(Files(root, "regexp*.js"), "mjsunit-harmony"), "mjsunit/harmony");
    }

    [Fact]
    public void WebkitRegExp()
    {
        string root = Path.Combine(RepoRoot(), "test", "webkit");
        IEnumerable<string> files = Files(root, "regexp*.js")
            .Concat(Files(Path.Combine(root, "fast", "regex"), "*.js", SearchOption.AllDirectories));
        Check(RunCorpus(files, "webkit"), "webkit");
    }

    [Fact]
    public void Test262BuiltInsRegExp()
    {
        string? root = Test262Root();
        Assert.SkipWhen(root is null, "test262 not found");
        IEnumerable<string> files = Files(Path.Combine(root!, "built-ins", "RegExp"), "*.js",
            SearchOption.AllDirectories);
        Check(RunCorpus(files, "test262-built-ins-RegExp"), "test262 built-ins/RegExp");
    }

    [Fact]
    public void Test262LiteralsRegExp()
    {
        string? root = Test262Root();
        Assert.SkipWhen(root is null, "test262 not found");
        IEnumerable<string> files = Files(Path.Combine(root!, "language", "literals", "regexp"), "*.js",
            SearchOption.AllDirectories);
        Check(RunCorpus(files, "test262-literals-regexp"), "test262 language/literals/regexp");
    }
}
