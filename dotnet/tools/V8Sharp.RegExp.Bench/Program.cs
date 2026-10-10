// Micro benchmark of the irregexp tiers on the regexp workload of Octane's
// regexp.js (dotnet/artifacts/octane, fetched by
// tools/V8Sharp.Bench/fetch-octane.sh).
//
// 1. record: regexp.js runs once in the real V8 with RegExp.prototype.exec
//    instrumented, which records every exec call the benchmark makes, including
//    the ones String.prototype.replace/split/match make internally (split's
//    are on the sticky splitter regexp). The trace (pattern, flags, subject,
//    lastIndex) goes to dotnet/artifacts/regexp-bench/trace.json.
// 2. replay: the trace is replayed, per pattern class, by
//      - the irregexp bytecode interpreter (--regexp-interpret-all),
//      - the IL native tier (--regexp-jit-all, V8's default policy),
//      - the real V8 (RegExp.prototype.test, native irregexp), and the real
//        V8 with --regexp-interpret-all, each in a child process since V8
//        flags are process-global,
//      - System.Text.RegularExpressions (RegexOptions.Compiled, and
//        RegexOptions.NonBacktracking) on the patterns whose translation is
//        semantically safe and agrees with irregexp on every trace entry.
//    Times are the Stopwatch minimum over --runs measured replays after --warmup
//    unmeasured ones, reported per call.
//
//   dotnet tools/V8Sharp.RegExp.Bench/bin/Release/net10.0/V8Sharp.RegExp.Bench.dll [--runs N] [--warmup N] [--rerecord]

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ClearScript.V8;
using V8Sharp.Oracle;

namespace V8Sharp.RegExp.Bench;

public static class Program
{
    sealed class Trace
    {
        public List<string[]> Patterns { get; set; } = [];  // [source, flags]
        public List<string> Subjects { get; set; } = [];
        public List<int> Entries { get; set; } = [];         // (pattern, subject, lastIndex) triples
    }

    sealed class Workload
    {
        public string Name { get; set; } = "";
        public int[] Entries { get; set; } = [];  // (pattern, subject, start) triples
        public int Calls => Entries.Length / 3;
    }

    sealed class OracleRequest
    {
        public List<string[]> Patterns { get; set; } = [];
        public List<string> Subjects { get; set; } = [];
        public List<Workload> Workloads { get; set; } = [];
        public int Runs { get; set; }
        public int Warmup { get; set; }
    }

    static int s_runs = 7;
    static int s_warmup = 3;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "oracle-worker") return OracleWorker(args[1], args.Length > 2 ? args[2] : "");
        bool rerecord = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runs": s_runs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--warmup": s_warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--rerecord": rerecord = true; break;
                default:
                    Console.Error.WriteLine("usage: V8Sharp.RegExp.Bench [--runs N] [--warmup N] [--rerecord]");
                    return 1;
            }
        }

        string root = RepoRoot();
        string outDir = Path.Combine(root, "dotnet", "artifacts", "regexp-bench");
        Directory.CreateDirectory(outDir);
        string tracePath = Path.Combine(outDir, "trace.json");
        if (rerecord || !File.Exists(tracePath)) Record(root, tracePath);
        Trace trace = JsonSerializer.Deserialize<Trace>(File.ReadAllText(tracePath))!;
        var report = new StringBuilder();
        Run(trace, outDir, report);
        string reportPath = Path.Combine(outDir, "results.md");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine(report);
        Console.WriteLine("written to " + reportPath);
        return 0;
    }

    static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "dotnet", "artifacts")) && Directory.Exists(Path.Combine(dir, "src", "regexp")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("repository root not found");
    }

    // ---- Recording ----------------------------------------------------------

    static void Record(string root, string tracePath)
    {
        string octane = Path.Combine(root, "dotnet", "artifacts", "octane");
        if (!File.Exists(Path.Combine(octane, "regexp.js")))
            throw new FileNotFoundException("run tools/V8Sharp.Bench/fetch-octane.sh first", Path.Combine(octane, "regexp.js"));
        using var engine = new V8ScriptEngine();
        engine.Execute("globalThis.performance = globalThis.performance || { now: () => Date.now() };");
        // base.js seeds Math.random, which makes the input variants (and the
        // benchmark's checksum) deterministic.
        engine.Execute("base.js", File.ReadAllText(Path.Combine(octane, "base.js")));
        engine.Execute("regexp.js", File.ReadAllText(Path.Combine(octane, "regexp.js")));
        string json = (string)engine.Evaluate("""
            (function () {
              const patterns = [], patternIds = new Map(), subjects = [], subjectIds = new Map(), entries = [];
              const exec = RegExp.prototype.exec;
              RegExp.prototype.exec = function (s) {
                s = String(s);
                const key = this.source + '/' + this.flags;
                let p = patternIds.get(key);
                if (p === undefined) { p = patterns.length; patterns.push([this.source, this.flags]); patternIds.set(key, p); }
                let i = subjectIds.get(s);
                if (i === undefined) { i = subjects.length; subjects.push(s); subjectIds.set(s, i); }
                entries.push(p, i, this.lastIndex);
                return exec.call(this, s);
              };
              BenchmarkSuite.ResetRNG();
              try { new RegExpBenchmark().run(); } finally { RegExp.prototype.exec = exec; }
              return JSON.stringify({ Patterns: patterns, Subjects: subjects, Entries: entries });
            })()
            """);
        File.WriteAllText(tracePath, json);
    }

    // ---- Classification and translation ------------------------------------

    sealed class PatternInfo
    {
        public string Source = "";
        public string Flags = "";
        public RegExpFlags ParsedFlags;
        public CompiledRegExp Probe = null!;
        public string Class = "";
        public string? DotNet;        // translation for RegexOptions.Compiled, or null
        public string? DotNetWhy;
        public string? NonBacktracking;
        public string? NonBacktrackingWhy;
        public RegexOptions DotNetOptions;
        public RegexOptions NonBacktrackingOptions;
    }

    static string ClassOf(CompiledRegExp re)
    {
        if (re.Kind == RegExpKind.Atom) return "atom (string search)";
        if (re.Flags.IsSticky()) return "sticky (split)";
        if (re.Flags.IsGlobal()) return "global (replace)";
        if (re.CaptureCount > 0) return "captures";
        return "no captures";
    }

    const string kJsWhitespace = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";
    // The complements of \d, \w and \s over [\u0000-\uffff].
    const string kNotDigit = "\\u0000-/:-\\uffff";
    const string kNotWord = "\\u0000-/:-@\\[-\\^`{-\\uffff";
    const string kNotWhitespace = "\\u0000-\\u0008\\u000e-\\u001f!-\\u009f\\u00a1-\\u167f\\u1681-\\u1fff\\u200b-\\u2027" +
                                  "\\u202a-\\u202e\\u2030-\\u205e\\u2060-\\u2fff\\u3001-\\ufefe\\uff00-\\uffff";

    // Why the pattern cannot be translated to .NET semantics safely, or null.
    static string? CheckTree(RegExpTree tree, bool inRepetition, ref bool hasLookaround, ref bool hasBoundary)
    {
        switch (tree)
        {
            case RegExpDisjunction d:
                foreach (RegExpTree t in d.Alternatives)
                    if (CheckTree(t, inRepetition, ref hasLookaround, ref hasBoundary) is { } w) return w;
                return null;
            case RegExpAlternative a:
                foreach (RegExpTree t in a.Nodes)
                    if (CheckTree(t, inRepetition, ref hasLookaround, ref hasBoundary) is { } w) return w;
                return null;
            case RegExpQuantifier q:
                return CheckTree(q.Body, inRepetition || q.Max > 1, ref hasLookaround, ref hasBoundary);
            case RegExpCapture c:
                // JS clears the captures of a quantified group at each iteration; .NET keeps them.
                if (inRepetition) return "capture inside a quantifier";
                if (c.Name is not null) return "named capture";
                return CheckTree(c.Body, inRepetition, ref hasLookaround, ref hasBoundary);
            case RegExpGroup g:
                return CheckTree(g.Body, inRepetition, ref hasLookaround, ref hasBoundary);
            case RegExpLookaround l:
                if (l.LookaroundType == RegExpLookaround.Type.LOOKBEHIND) return "lookbehind";
                hasLookaround = true;
                return CheckTree(l.Body, inRepetition, ref hasLookaround, ref hasBoundary);
            case RegExpBackReference:
                return "back reference";
            case RegExpAssertion s:
                if (s.AssertionType is RegExpAssertion.Type.BOUNDARY or RegExpAssertion.Type.NON_BOUNDARY) hasBoundary = true;
                return null;
            default:
                return null;
        }
    }

    // Translates a JS regexp source to .NET syntax with the same meaning. The
    // shorthand classes, '.', '^' and '$' are spelled out with JS's
    // definitions; \b keeps .NET's, which RegexOptions.ECMAScript makes ASCII
    // (the option is used only for patterns with \b or \B).
    static string? Translate(PatternInfo p, out string? why, out bool usesLookaround, out bool hasWordBoundary)
    {
        usesLookaround = false;
        hasWordBoundary = false;
        RegExpFlags flags = p.ParsedFlags;
        why = null;
        if (flags.IsEitherUnicode()) { why = "/u or /v"; return null; }
        var data = new RegExpCompileData();
        if (!RegExpParser.ParseRegExp(p.Source, flags, data)) { why = "syntax error"; return null; }
        bool hasLookaround = false, hasBoundary = false;
        why = CheckTree(data.Tree!, false, ref hasLookaround, ref hasBoundary);
        if (why is not null) return null;
        string src = p.Source;
        if (flags.IsIgnoreCase())
        {
            foreach (char ch in src)
            {
                if (ch > 0x7f) { why = "non-ASCII /i"; return null; }
            }
        }
        bool multiline = flags.IsMultiline();
        var sb = new StringBuilder();
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];
            switch (c)
            {
                case '\\':
                    if (!TranslateEscape(src, ref i, sb, false, out why)) return null;
                    continue;
                case '[':
                    if (!TranslateClass(src, ref i, sb, out why)) return null;
                    continue;
                case '.':
                    sb.Append(flags.IsDotAll() ? "[\\u0000-\\uffff]" : "[^\\n\\r\\u2028\\u2029]");
                    break;
                case '^':
                    if (multiline)
                    {
                        sb.Append("(?:\\A|(?<=[\\n\\r\\u2028\\u2029]))");
                        usesLookaround = true;
                    }
                    else
                    {
                        sb.Append("\\A");
                    }
                    break;
                case '$':
                    if (multiline)
                    {
                        sb.Append("(?=[\\n\\r\\u2028\\u2029]|\\z)");
                        usesLookaround = true;
                    }
                    else
                    {
                        sb.Append("\\z");
                    }
                    break;
                case '(':
                    if (i + 2 < src.Length && src[i + 1] == '?' && src[i + 2] == '<') { why = "named group or lookbehind"; return null; }
                    sb.Append('(');
                    break;
                case '#':
                case ' ':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    if (c > 0x7e || c < 0x20) AppendUnicode(sb, c);
                    else sb.Append(c);
                    break;
            }
            i++;
        }
        usesLookaround |= hasLookaround;
        hasWordBoundary = hasBoundary;
        string result = sb.ToString();
        if (flags.IsSticky()) result = "\\G(?:" + result + ")";
        return result;
    }

    static void AppendUnicode(StringBuilder sb, int c) =>
        sb.Append("\\u").Append(c.ToString("x4", CultureInfo.InvariantCulture));

    static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    // Reads one escape at src[i] == '\\'. Outside a class the shorthands
    // become classes; inside one, their ranges. Single characters are
    // returned through `single` (inside classes) or appended.
    static bool TranslateEscape(string src, ref int i, StringBuilder sb, bool inClass, out string? why)
    {
        why = null;
        if (i + 1 >= src.Length) { why = "trailing backslash"; return false; }
        char e = src[i + 1];
        i += 2;
        switch (e)
        {
            case 'd': sb.Append(inClass ? "0-9" : "[0-9]"); return true;
            case 'D': sb.Append(inClass ? kNotDigit : "[^0-9]"); return true;
            case 'w': sb.Append(inClass ? "a-zA-Z0-9_" : "[a-zA-Z0-9_]"); return true;
            case 'W': sb.Append(inClass ? kNotWord : "[^a-zA-Z0-9_]"); return true;
            case 's': sb.Append(inClass ? kJsWhitespace : "[" + kJsWhitespace + "]"); return true;
            case 'S': sb.Append(inClass ? kNotWhitespace : "[^" + kJsWhitespace + "]"); return true;
            case 'b':
                if (inClass) AppendUnicode(sb, 8);
                else sb.Append("\\b");
                return true;
            case 'B':
                if (inClass) { why = "\\B in class"; return false; }
                sb.Append("\\B");
                return true;
        }
        int value = EscapeValue(src, ref i, e, out why);
        if (value < 0) return false;
        AppendUnicode(sb, value);
        return true;
    }

    // The character value of a character escape (after the backslash and e).
    static int EscapeValue(string src, ref int i, char e, out string? why)
    {
        why = null;
        switch (e)
        {
            case 'f': return '\f';
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'v': return '\v';
            case 'x':
                if (i + 1 < src.Length && IsHex(src[i]) && IsHex(src[i + 1]))
                {
                    int v = int.Parse(src.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    i += 2;
                    return v;
                }
                return 'x';
            case 'u':
                if (i + 3 < src.Length && IsHex(src[i]) && IsHex(src[i + 1]) && IsHex(src[i + 2]) && IsHex(src[i + 3]))
                {
                    int v = int.Parse(src.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    i += 4;
                    return v;
                }
                return 'u';
            case 'c':
                if (i < src.Length && char.IsAsciiLetter(src[i])) return src[i++] % 32;
                why = "\\c without a letter";
                return -1;
            case '0':
                if (i < src.Length && char.IsAsciiDigit(src[i])) { why = "octal escape"; return -1; }
                return 0;
        }
        if (e is >= '1' and <= '9') { why = "octal escape"; return -1; }
        // Identity escape.
        return e;
    }

    static bool TranslateClass(string src, ref int i, StringBuilder sb, out string? why)
    {
        why = null;
        i++;  // '['
        bool negated = i < src.Length && src[i] == '^';
        if (negated) i++;
        if (i < src.Length && src[i] == ']')
        {
            // [] matches nothing, [^] matches everything.
            sb.Append(negated ? "[\\u0000-\\uffff]" : "(?!)");
            i++;
            return true;
        }
        var body = new StringBuilder();
        while (true)
        {
            if (i >= src.Length) { why = "unterminated class"; return false; }
            if (src[i] == ']') { i++; break; }
            int from = ClassAtom(src, ref i, body, out why);
            if (from == -2) return false;
            if (from >= 0 && i + 1 < src.Length && src[i] == '-' && src[i + 1] != ']')
            {
                int save = i;
                i++;
                var tmp = new StringBuilder();
                int to = ClassAtom(src, ref i, tmp, out why);
                if (to == -2) return false;
                if (to >= 0)
                {
                    AppendUnicode(body, from);
                    body.Append('-');
                    AppendUnicode(body, to);
                    continue;
                }
                // A class escape as a range end: '-' is literal (Annex B).
                AppendUnicode(body, from);
                body.Append("\\-").Append(tmp);
                _ = save;
                continue;
            }
            if (from >= 0) AppendUnicode(body, from);
        }
        sb.Append('[');
        if (negated) sb.Append('^');
        sb.Append(body).Append(']');
        return true;
    }

    // Returns the character, -1 after appending a shorthand class's ranges,
    // or -2 on failure.
    static int ClassAtom(string src, ref int i, StringBuilder body, out string? why)
    {
        why = null;
        char c = src[i];
        if (c != '\\')
        {
            i++;
            return c;
        }
        if (i + 1 < src.Length && src[i + 1] is 'd' or 'D' or 'w' or 'W' or 's' or 'S')
        {
            return TranslateEscape(src, ref i, body, true, out why) ? -1 : -2;
        }
        if (i + 1 < src.Length && src[i + 1] == 'b')
        {
            i += 2;
            return 8;
        }
        if (i + 1 < src.Length && src[i + 1] == '-')
        {
            i += 2;
            return '-';
        }
        if (i + 1 >= src.Length) { why = "trailing backslash"; return -2; }
        char e = src[i + 1];
        if (e == 'c') { why = "\\c in class"; return -2; }
        i += 2;
        int v = EscapeValue(src, ref i, e, out why);
        return v < 0 ? -2 : v;
    }

    // ---- Replay -------------------------------------------------------------

    static long ReplayIrregexp(CompiledRegExp[] res, int[][] registers, string[] subjects, bool[] oneByte, int[] e)
    {
        long sum = 0;
        for (int k = 0; k < e.Length; k += 3)
        {
            int p = e[k];
            int s = e[k + 1];
            int r = res[p].Exec(subjects[s], e[k + 2], registers[p], oneByte[s]);
            if (r > 0) sum += registers[p][0] + 1;
        }
        return sum;
    }

    static long ReplayDotNet(Regex?[] res, string[] subjects, int[] e)
    {
        long sum = 0;
        for (int k = 0; k < e.Length; k += 3)
        {
            Regex.ValueMatchEnumerator m = res[e[k]]!.EnumerateMatches(subjects[e[k + 1]], e[k + 2]);
            if (m.MoveNext()) sum += m.Current.Index + 1;
        }
        return sum;
    }

    // Median seconds per replay of the workload.
    static double Measure(Func<long> replay)
    {
        for (int i = 0; i < s_warmup; i++) replay();
        var times = new List<double>();
        for (int i = 0; i < s_runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            replay();
            times.Add(Stopwatch.GetElapsedTime(t0).TotalSeconds);
        }
        return times.Min();
    }

    static CompiledRegExp[] CompileAll(List<PatternInfo> patterns, RegExpTierPolicy tier)
    {
        var res = new CompiledRegExp[patterns.Count];
        for (int i = 0; i < res.Length; i++)
        {
            res[i] = RegExpEngine.Compile(patterns[i].Source, patterns[i].ParsedFlags, RegExpEngine.kNoBacktrackLimit, tier).RegExp!;
        }
        return res;
    }

    static void Run(Trace trace, string outDir, StringBuilder report)
    {
        string[] subjects = trace.Subjects.ToArray();
        bool[] oneByte = Array.ConvertAll(subjects, s => CompiledRegExp.IsOneByteSubject(s));
        var patterns = new List<PatternInfo>();
        foreach (string[] p in trace.Patterns)
        {
            RegExpFlags flags = RegExpFlagsExtensions.FromString(p[1]) ?? throw new InvalidOperationException(p[1]);
            var info = new PatternInfo { Source = p[0], Flags = p[1], ParsedFlags = flags };
            info.Probe = RegExpEngine.Compile(p[0], flags, RegExpEngine.kNoBacktrackLimit, RegExpTierPolicy.JitAll).RegExp!;
            info.Class = ClassOf(info.Probe);
            patterns.Add(info);
        }

        // Normalise the trace: the start index is lastIndex for global and
        // sticky regexps and 0 otherwise; entries past the end (which return
        // null without running the regexp) are dropped.
        var all = new List<int>();
        for (int k = 0; k < trace.Entries.Count; k += 3)
        {
            int p = trace.Entries[k], s = trace.Entries[k + 1], lastIndex = trace.Entries[k + 2];
            RegExpFlags f = patterns[p].ParsedFlags;
            int start = f.IsGlobal() || f.IsSticky() ? lastIndex : 0;
            if (start > subjects[s].Length) continue;
            all.Add(p);
            all.Add(s);
            all.Add(start);
        }
        int[] allEntries = all.ToArray();

        // Translate to .NET and validate the translation against irregexp on
        // every trace entry of the pattern.
        var validationRegs = new int[1024];
        int[] perPatternEntries = new int[patterns.Count];
        for (int k = 0; k < allEntries.Length; k += 3) perPatternEntries[allEntries[k]]++;
        var disagreements = new List<string>();
        for (int pi = 0; pi < patterns.Count; pi++)
        {
            PatternInfo p = patterns[pi];
            string? t = Translate(p, out string? why, out bool usesLookaround, out bool hasWordBoundary);
            if (t is null)
            {
                p.DotNetWhy = p.NonBacktrackingWhy = why;
                continue;
            }
            RegexOptions baseOptions = p.ParsedFlags.IsIgnoreCase() ? RegexOptions.IgnoreCase : RegexOptions.None;
            p.DotNetOptions = baseOptions | RegexOptions.Compiled |
                              (hasWordBoundary ? RegexOptions.ECMAScript : RegexOptions.CultureInvariant);
            p.NonBacktrackingOptions = baseOptions | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
            p.DotNet = Validate(p, t, p.DotNetOptions, pi, allEntries, subjects, out p.DotNetWhy, disagreements);
            if (usesLookaround || hasWordBoundary || p.ParsedFlags.IsSticky())
            {
                p.NonBacktrackingWhy = "lookaround, \\b, multiline anchor or sticky";
            }
            else
            {
                p.NonBacktracking = Validate(p, t, p.NonBacktrackingOptions, pi, allEntries, subjects,
                    out p.NonBacktrackingWhy, disagreements);
            }
        }

        // Workloads: per class, all entries and the .NET-translatable subsets.
        var classes = new List<string> { "all" };
        foreach (PatternInfo p in patterns)
            if (!classes.Contains(p.Class)) classes.Add(p.Class);
        var workloads = new List<Workload>();
        foreach (string c in classes)
        {
            workloads.Add(Select(c, "", allEntries, e => c == "all" || patterns[e].Class == c));
            workloads.Add(Select(c, "net", allEntries, e => (c == "all" || patterns[e].Class == c) && patterns[e].DotNet is not null));
            workloads.Add(Select(c, "nb", allEntries,
                e => (c == "all" || patterns[e].Class == c) && patterns[e].NonBacktracking is not null));
        }
        workloads.RemoveAll(w => w.Calls == 0);

        var results = new Dictionary<(string Engine, string Workload), double>();

        // Cold: compile and run each pattern once (first-execution cost).
        CompiledRegExp[] interp = CompileAll(patterns, RegExpTierPolicy.Interpreted);
        CompiledRegExp[] jit = CompileAll(patterns, RegExpTierPolicy.JitAll);
        int[][] regs = patterns.Select(p => new int[p.Probe.RegistersPerMatch]).ToArray();
        {
            long t0 = Stopwatch.GetTimestamp();
            ReplayIrregexp(interp, regs, subjects, oneByte, allEntries);
            results[("interpreter", "cold")] = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            t0 = Stopwatch.GetTimestamp();
            ReplayIrregexp(jit, regs, subjects, oneByte, allEntries);
            results[("IL", "cold")] = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        }

        Regex?[] net = patterns.Select(p => p.DotNet is null ? null : new Regex(p.DotNet, p.DotNetOptions)).ToArray();
        Regex?[] nb = patterns.Select(p => p.NonBacktracking is null ? null : new Regex(p.NonBacktracking, p.NonBacktrackingOptions)).ToArray();

        foreach (Workload w in workloads)
        {
            Console.Error.WriteLine($"measuring {w.Name} ({w.Calls} calls)");
            int[] e = w.Entries;
            results[("interpreter", w.Name)] = Measure(() => ReplayIrregexp(interp, regs, subjects, oneByte, e));
            results[("IL", w.Name)] = Measure(() => ReplayIrregexp(jit, regs, subjects, oneByte, e));
            if (w.Name.EndsWith("/net", StringComparison.Ordinal))
                results[(".NET Compiled", w.Name)] = Measure(() => ReplayDotNet(net, subjects, e));
            if (w.Name.EndsWith("/nb", StringComparison.Ordinal))
            {
                results[(".NET NonBacktracking", w.Name)] = Measure(() => ReplayDotNet(nb, subjects, e));
                // Compiled on the same entries, where it applies to all of them.
                bool allNet = true;
                for (int k = 0; k < e.Length; k += 3) allNet &= net[e[k]] is not null;
                if (allNet) results[(".NET Compiled", w.Name)] = Measure(() => ReplayDotNet(net, subjects, e));
            }
        }

        // Per pattern: where the time goes.
        var perPattern = new List<(int Pattern, int Calls, double Interp, double IL, double Net)>();
        for (int pi = 0; pi < patterns.Count; pi++)
        {
            if (perPatternEntries[pi] < 20) continue;
            int pattern = pi;
            int[] e = Select("", "", allEntries, x => x == pattern).Entries;
            double ti = Measure(() => ReplayIrregexp(interp, regs, subjects, oneByte, e));
            double tj = Measure(() => ReplayIrregexp(jit, regs, subjects, oneByte, e));
            double tn = net[pi] is null ? double.NaN : Measure(() => ReplayDotNet(net, subjects, e));
            perPattern.Add((pi, e.Length / 3, ti, tj, tn));
        }

        // The oracle, in child processes (V8 flags are process-global).
        var request = new OracleRequest
        {
            Patterns = trace.Patterns, Subjects = trace.Subjects, Workloads = workloads, Runs = s_runs, Warmup = s_warmup,
        };
        string requestPath = Path.Combine(outDir, "oracle-request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        foreach ((string engine, string flags) in new[] { ("V8", ""), ("V8 --regexp-interpret-all", "--regexp-interpret-all") })
        {
            Console.Error.WriteLine("measuring " + engine);
            Dictionary<string, double>? times = RunOracleWorker(requestPath, flags);
            if (times is null) continue;
            foreach ((string name, double t) in times) results[(engine, name)] = t;
        }

        WriteReport(report, trace, patterns, workloads, results, allEntries, perPatternEntries, disagreements);

        report.Append("\n## Heaviest patterns (total time per replay of the trace)\n\n");
        report.Append("| pattern | class | calls | interpreter us | IL us | .NET Compiled us |\n");
        report.Append("|---|---|---:|---:|---:|---:|\n");
        perPattern.Sort((a, b) => Math.Max(b.Interp, double.IsNaN(b.Net) ? 0 : b.Net)
            .CompareTo(Math.Max(a.Interp, double.IsNaN(a.Net) ? 0 : a.Net)));
        foreach (var x in perPattern.Take(15))
        {
            PatternInfo pi = patterns[x.Pattern];
            string netText = double.IsNaN(x.Net) ? "n/a (" + pi.DotNetWhy + ")" : (x.Net * 1e6).ToString("F0", CultureInfo.InvariantCulture);
            report.Append(CultureInfo.InvariantCulture,
                $"| `/{Show(pi.Source).Replace("|", "\\|", StringComparison.Ordinal)}/{pi.Flags}` | {pi.Class} | {x.Calls} | {x.Interp * 1e6:F0} | {x.IL * 1e6:F0} | {netText} |\n");
        }
    }

    static Workload Select(string cls, string subset, int[] entries, Func<int, bool> include)
    {
        var list = new List<int>();
        for (int k = 0; k < entries.Length; k += 3)
        {
            if (!include(entries[k])) continue;
            list.Add(entries[k]);
            list.Add(entries[k + 1]);
            list.Add(entries[k + 2]);
        }
        return new Workload { Name = subset.Length == 0 ? cls : cls + "/" + subset, Entries = list.ToArray() };
    }

    // Constructs the .NET regex and checks it against irregexp on every trace
    // entry of the pattern; returns the translation if they all agree.
    static string? Validate(PatternInfo p, string translation, RegexOptions options, int patternIndex, int[] entries,
        string[] subjects, out string? why, List<string> disagreements)
    {
        why = null;
        Regex regex;
        try
        {
            regex = new Regex(translation, options);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            why = ".NET rejects: " + ex.Message;
            return null;
        }
        int[] regs = new int[p.Probe.RegistersPerMatch];
        for (int k = 0; k < entries.Length; k += 3)
        {
            if (entries[k] != patternIndex) continue;
            string s = subjects[entries[k + 1]];
            int start = entries[k + 2];
            int r = p.Probe.Exec(s, start, regs);
            Match m = regex.Match(s, start);
            bool agree = (r > 0) == m.Success;
            if (agree && m.Success)
            {
                for (int g = 0; g <= p.Probe.CaptureCount && agree; g++)
                {
                    Group grp = m.Groups[g];
                    int gs = grp.Success ? grp.Index : -1, ge = grp.Success ? grp.Index + grp.Length : -1;
                    agree = gs == regs[2 * g] && ge == regs[2 * g + 1];
                }
            }
            if (!agree)
            {
                why = "translation disagrees";
                disagreements.Add($"/{p.Source}/{p.Flags} ({options}) -> {translation} on \"{Show(s)}\"@{start}");
                return null;
            }
        }
        return translation;
    }

    static string Show(string s) => s.Length > 60 ? s[..60] + "..." : s;

    // ---- Oracle -------------------------------------------------------------

    static Dictionary<string, double>? RunOracleWorker(string requestPath, string flags)
    {
        string self = typeof(Program).Assembly.Location;
        string host = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(host) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host) == "dotnet") psi.ArgumentList.Add(self);
        psi.ArgumentList.Add("oracle-worker");
        psi.ArgumentList.Add(requestPath);
        psi.ArgumentList.Add(flags);
        using Process proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"oracle worker ({flags}) failed with exit code {proc.ExitCode}");
            return null;
        }
        return JsonSerializer.Deserialize<Dictionary<string, double>>(output);
    }

    static int OracleWorker(string requestPath, string flags)
    {
        if (flags.Length > 0) ReferenceV8.EnsureFlags(flags);
        OracleRequest request = JsonSerializer.Deserialize<OracleRequest>(File.ReadAllText(requestPath))!;
        using var engine = new V8ScriptEngine();
        engine.Execute("""
            globalThis.__setup = function (patterns, subjects) {
              globalThis.__res = JSON.parse(patterns).map(p => new RegExp(p[0], p[1]));
              globalThis.__subjects = JSON.parse(subjects);
              globalThis.__workloads = new Map();
            };
            globalThis.__prepare = function (name, entries) { __workloads.set(name, JSON.parse(entries)); };
            // RegExp.prototype.test runs the regexp like exec, without
            // materialising the result array.
            globalThis.__replay = function (name) {
              const e = __workloads.get(name), res = __res, subjects = __subjects;
              let sum = 0;
              for (let k = 0; k < e.length; k += 3) {
                const re = res[e[k]];
                re.lastIndex = e[k + 2];
                if (re.test(subjects[e[k + 1]])) sum++;
              }
              return sum;
            };
            """);
        dynamic script = engine.Script;
        script.__setup(JsonSerializer.Serialize(request.Patterns), JsonSerializer.Serialize(request.Subjects));
        s_runs = request.Runs;
        s_warmup = request.Warmup;
        var times = new Dictionary<string, double>();
        foreach (Workload w in request.Workloads)
        {
            script.__prepare(w.Name, JsonSerializer.Serialize(w.Entries));
            string name = w.Name;
            times[name] = Measure(() => Convert.ToInt64(script.__replay(name), CultureInfo.InvariantCulture));
        }
        Console.WriteLine(JsonSerializer.Serialize(times));
        return 0;
    }

    // ---- Report -------------------------------------------------------------

    static void WriteReport(StringBuilder r, Trace trace, List<PatternInfo> patterns, List<Workload> workloads,
        Dictionary<(string Engine, string Workload), double> results, int[] allEntries, int[] perPatternEntries,
        List<string> disagreements)
    {
        string Ns(string engine, Workload w) =>
            results.TryGetValue((engine, w.Name), out double t) ? (t * 1e9 / w.Calls).ToString("F0", CultureInfo.InvariantCulture) : "-";
        string Ratio(string a, string b, Workload w) =>
            results.TryGetValue((a, w.Name), out double ta) && results.TryGetValue((b, w.Name), out double tb)
                ? (ta / tb).ToString("F2", CultureInfo.InvariantCulture) : "-";

        r.Append("# Octane regexp.js: irregexp tiers vs V8 vs .NET\n\n");
        r.Append(CultureInfo.InvariantCulture, $"Trace: {allEntries.Length / 3} exec calls, {patterns.Count} patterns, {trace.Subjects.Count} distinct subjects. ");
        r.Append(CultureInfo.InvariantCulture, $"Best of {s_runs} replays after {s_warmup} warm-up replays (the host is shared, so the minimum is the least noisy statistic); ns per call.\n\n");
        r.Append(CultureInfo.InvariantCulture, $"Environment: {Environment.ProcessorCount} cores, .NET {Environment.Version}, V8 {ReferenceV8.Version}.\n\n");

        r.Append("## All calls, by pattern class\n\n");
        r.Append("| class | calls | interpreter | IL | V8 | V8 interpreter | interpreter/IL | IL/V8 |\n");
        r.Append("|---|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (Workload w in workloads)
        {
            if (w.Name.Contains('/')) continue;
            r.Append(CultureInfo.InvariantCulture, $"| {w.Name} | {w.Calls} | {Ns("interpreter", w)} | {Ns("IL", w)} | {Ns("V8", w)} | {Ns("V8 --regexp-interpret-all", w)} | {Ratio("interpreter", "IL", w)} | {Ratio("IL", "V8", w)} |\n");
        }

        r.Append("\n## Calls on patterns with a safe .NET translation (RegexOptions.Compiled; ECMAScript for \\b)\n\n");
        r.Append("| class | calls | interpreter | IL | V8 | .NET Compiled | IL/.NET |\n");
        r.Append("|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (Workload w in workloads)
        {
            if (!w.Name.EndsWith("/net", StringComparison.Ordinal)) continue;
            r.Append(CultureInfo.InvariantCulture, $"| {w.Name[..^4]} | {w.Calls} | {Ns("interpreter", w)} | {Ns("IL", w)} | {Ns("V8", w)} | {Ns(".NET Compiled", w)} | {Ratio("IL", ".NET Compiled", w)} |\n");
        }

        r.Append("\n## Calls on patterns RegexOptions.NonBacktracking can run\n\n");
        r.Append("| class | calls | IL | V8 | .NET Compiled | .NET NonBacktracking | IL/NonBacktracking |\n");
        r.Append("|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (Workload w in workloads)
        {
            if (!w.Name.EndsWith("/nb", StringComparison.Ordinal)) continue;
            r.Append(CultureInfo.InvariantCulture, $"| {w.Name[..^3]} | {w.Calls} | {Ns("IL", w)} | {Ns("V8", w)} | {Ns(".NET Compiled", w)} | {Ns(".NET NonBacktracking", w)} | {Ratio("IL", ".NET NonBacktracking", w)} |\n");
        }

        r.Append("\n## First execution (compile + one replay of the trace)\n\n");
        r.Append(CultureInfo.InvariantCulture, $"interpreter {results[("interpreter", "cold")] * 1e3:F1} ms, IL {results[("IL", "cold")] * 1e3:F1} ms\n\n");

        r.Append("## Patterns\n\n");
        var byReason = new SortedDictionary<string, (int Patterns, int Calls)>(StringComparer.Ordinal);
        int netPatterns = 0, nbPatterns = 0, netCalls = 0;
        for (int i = 0; i < patterns.Count; i++)
        {
            PatternInfo p = patterns[i];
            if (p.DotNet is not null)
            {
                netPatterns++;
                netCalls += perPatternEntries[i];
            }
            else
            {
                string key = p.DotNetWhy ?? "?";
                if (key.StartsWith(".NET rejects", StringComparison.Ordinal)) key = ".NET rejects the translation";
                byReason.TryGetValue(key, out var v);
                byReason[key] = (v.Patterns + 1, v.Calls + perPatternEntries[i]);
            }
            if (p.NonBacktracking is not null) nbPatterns++;
        }
        r.Append(CultureInfo.InvariantCulture, $"{netPatterns} of {patterns.Count} patterns ({netCalls} of {allEntries.Length / 3} calls) translate safely to .NET; {nbPatterns} also run on NonBacktracking. Not translated:\n\n");
        foreach ((string reason, (int n, int calls)) in byReason) r.Append(CultureInfo.InvariantCulture, $"- {reason}: {n} patterns, {calls} calls\n");
        if (disagreements.Count > 0)
        {
            r.Append("\nTranslations rejected because they disagreed with irregexp on the trace:\n\n");
            foreach (string d in disagreements) r.Append("- `").Append(d.Replace("`", "'", StringComparison.Ordinal)).Append("`\n");
        }
        r.Append("\nPer class:\n\n");
        foreach (IGrouping<string, PatternInfo> g in patterns.GroupBy(p => p.Class))
        {
            r.Append(CultureInfo.InvariantCulture, $"- {g.Key}: {g.Count()} patterns, e.g. ");
            r.Append(string.Join(", ", g.Take(4).Select(p => "`/" + Show(p.Source).Replace("`", "'", StringComparison.Ordinal) + "/" + p.Flags + "`")));
            r.Append('\n');
        }
    }
}
