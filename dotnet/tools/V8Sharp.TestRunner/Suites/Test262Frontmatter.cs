// Port of test262's tools/packaging/parseTestRecord.py (with the subset of
// YAML that monkeyYaml.py accepts), as used by test/test262/testcfg.py.
using System.Text.RegularExpressions;

namespace V8Sharp.TestRunner.Suites;

/// <summary>The metadata block (<c>/*--- ... ---*/</c>) of a test262 test.</summary>
public sealed partial class Test262Frontmatter
{
    public string? Description { get; private set; }
    public string? Esid { get; private set; }

    /// <summary><c>flags</c>: onlyStrict, noStrict, module, raw, async, generated, CanBlockIsFalse, CanBlockIsTrue, non-deterministic.</summary>
    public IReadOnlyList<string> Flags { get; private set; } = [];
    public IReadOnlyList<string> Features { get; private set; } = [];
    public IReadOnlyList<string> Includes { get; private set; } = [];
    public IReadOnlyList<string> Locale { get; private set; } = [];

    /// <summary><c>negative.phase</c>: parse, resolution or runtime; null when the test is not negative.</summary>
    public string? NegativePhase { get; private set; }

    /// <summary><c>negative.type</c>: the expected error constructor name.</summary>
    public string? NegativeType { get; private set; }

    public bool IsNegative => NegativeType is not null || NegativePhase is not null;

    /// <summary>Every top-level key, with scalars as strings, sequences as
    /// <c>List&lt;string&gt;</c>, mappings as <c>Dictionary&lt;string, object&gt;</c>.</summary>
    public IReadOnlyDictionary<string, object> Raw { get; private set; } = new Dictionary<string, object>();

    public bool HasFlag(string flag) => Flags.Contains(flag);

    [GeneratedRegex(@"/\*---(.*?)---\*/", RegexOptions.Singleline)]
    private static partial Regex YamlPattern();

    /// <summary>Parses the frontmatter of <paramref name="source"/>, or returns
    /// null when the file has none (the harness adapters and fixtures).</summary>
    public static Test262Frontmatter? Parse(string source)
    {
        var m = YamlPattern().Match(source);
        if (!m.Success) return null;
        return FromYaml(m.Groups[1].Value);
    }

    public static Test262Frontmatter FromYaml(string yaml)
    {
        var raw = ParseYamlMapping(yaml);
        var fm = new Test262Frontmatter { Raw = raw };
        fm.Description = raw.GetValueOrDefault("description") as string;
        fm.Esid = raw.GetValueOrDefault("esid") as string;
        fm.Flags = AsList(raw.GetValueOrDefault("flags"));
        fm.Features = AsList(raw.GetValueOrDefault("features"));
        fm.Includes = AsList(raw.GetValueOrDefault("includes"));
        fm.Locale = AsList(raw.GetValueOrDefault("locale"));
        if (raw.GetValueOrDefault("negative") is Dictionary<string, object> neg)
        {
            fm.NegativePhase = neg.GetValueOrDefault("phase") as string;
            fm.NegativeType = neg.GetValueOrDefault("type") as string;
        }
        return fm;
    }

    static List<string> AsList(object? v) => v switch
    {
        List<string> l => l,
        string s when s.Length > 0 => [s],
        _ => [],
    };

    static int Indent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    /// <summary>The YAML subset of test262 frontmatter: <c>key: scalar</c>,
    /// <c>key: [a, b]</c>, block scalars (<c>|</c>, <c>&gt;</c>, with optional
    /// chomping), block sequences (<c>- item</c>) and one level of nested
    /// mapping (<c>negative:</c>).</summary>
    static Dictionary<string, object> ParseYamlMapping(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        int i = 0;
        ParseMapping(lines, ref i, -1, result);
        return result;
    }

    static void ParseMapping(string[] lines, ref int i, int parentIndent, Dictionary<string, object> into)
    {
        int? indent = null;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) { i++; continue; }
            int ind = Indent(line);
            if (ind <= parentIndent) return;
            indent ??= ind;
            if (ind != indent) return;
            string content = line[ind..];
            int colon = FindKeyColon(content);
            if (colon < 0) { i++; continue; }
            string key = content[..colon].Trim();
            string rest = content[(colon + 1)..].Trim();
            i++;
            if (rest.Length == 0)
            {
                // Either a block sequence or a nested mapping.
                int j = i;
                while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                if (j < lines.Length && lines[j].TrimStart().StartsWith('-') && Indent(lines[j]) >= ind)
                {
                    var seq = new List<string>();
                    i = j;
                    int seqIndent = Indent(lines[j]);
                    while (i < lines.Length)
                    {
                        string l = lines[i];
                        if (l.Trim().Length == 0) { i++; continue; }
                        if (Indent(l) != seqIndent || !l.TrimStart().StartsWith('-')) break;
                        seq.Add(Scalar(l.TrimStart()[1..].Trim()));
                        i++;
                    }
                    into[key] = seq;
                }
                else if (j < lines.Length && Indent(lines[j]) > ind)
                {
                    var nested = new Dictionary<string, object>(StringComparer.Ordinal);
                    ParseMapping(lines, ref i, ind, nested);
                    into[key] = nested;
                }
                else
                {
                    into[key] = "";
                }
            }
            else if (rest[0] is '|' or '>')
            {
                bool folded = rest[0] == '>';
                var block = new List<string>();
                int? blockIndent = null;
                while (i < lines.Length)
                {
                    string l = lines[i];
                    if (l.Trim().Length == 0) { block.Add(""); i++; continue; }
                    int li = Indent(l);
                    if (li <= ind) break;
                    blockIndent ??= li;
                    block.Add(l[Math.Min(li, blockIndent.Value)..]);
                    i++;
                }
                while (block.Count > 0 && block[^1].Length == 0) block.RemoveAt(block.Count - 1);
                into[key] = folded ? string.Join(" ", block).Trim() : string.Join("\n", block);
            }
            else if (rest[0] == '[')
            {
                // Flow sequence, possibly spanning several lines.
                string flow = rest;
                while (!flow.Contains(']') && i < lines.Length) flow += " " + lines[i++].Trim();
                int close = flow.IndexOf(']');
                string inner = flow[1..(close < 0 ? flow.Length : close)];
                into[key] = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(Scalar).ToList();
            }
            else if (rest[0] == '{')
            {
                string flow = rest;
                while (!flow.Contains('}') && i < lines.Length) flow += " " + lines[i++].Trim();
                int close = flow.IndexOf('}');
                var nested = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var part in flow[1..(close < 0 ? flow.Length : close)].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = part.IndexOf(':');
                    if (c > 0) nested[part[..c].Trim()] = Scalar(part[(c + 1)..].Trim());
                }
                into[key] = nested;
            }
            else
            {
                // Plain scalar, possibly continued on more-indented lines.
                string value = rest;
                while (i < lines.Length && lines[i].Trim().Length > 0 && Indent(lines[i]) > ind && FindKeyColon(lines[i].Trim()) < 0)
                {
                    value += " " + lines[i].Trim();
                    i++;
                }
                into[key] = Scalar(value);
            }
        }
    }

    /// <summary>The ':' that ends a mapping key: followed by space or end of line.</summary>
    static int FindKeyColon(string s)
    {
        if (s.StartsWith('-') || s.StartsWith('"') || s.StartsWith('\'')) return -1;
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == ':' && (k + 1 == s.Length || s[k + 1] == ' ')) return k;
            if (s[k] == ' ' && k + 1 < s.Length && s[k + 1] == '#') return -1;
        }
        return -1;
    }

    static string Scalar(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] is '"' or '\'')
        {
            int close = s.IndexOf(s[0], 1);
            if (close > 0) return s[1..close];
        }
        int hash = s.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) s = s[..hash].TrimEnd();
        return s;
    }
}
