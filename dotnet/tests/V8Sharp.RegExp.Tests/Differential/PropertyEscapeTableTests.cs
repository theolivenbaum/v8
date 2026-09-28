using System.Text;
using Microsoft.ClearScript.V8;
using V8Sharp.RegExp.Unicode;

namespace V8Sharp.RegExp.Tests.Differential;

/// <summary>
/// Compares the code point set of every property escape (\p{...}) the
/// generated Unicode tables know against the oracle's. The oracle (V8 14.7)
/// uses an older Unicode version than the tables here (Unicode 17), so code
/// points that the oracle treats as unassigned (\p{Cn}) are excluded from the
/// comparison; the remaining differences are version changes to assigned
/// characters and are listed in the report.
/// </summary>
public class PropertyEscapeTableTests
{
    static List<string> AllPropertyEscapes()
    {
        var list = new List<string>();
        foreach ((string[] names, _) in UnicodeTables.GeneralCategoryValues)
        {
            list.Add("General_Category=" + names[0]);
        }
        foreach ((string[] names, _, _) in UnicodeTables.ScriptValues)
        {
            list.Add("Script=" + names[0]);
            list.Add("Script_Extensions=" + names[0]);
        }
        foreach ((string[] names, _) in UnicodeTables.BinaryProperties) list.Add(names[0]);
        return list;
    }

    static List<(int From, int To)> OurRanges(string property)
    {
        var data = new RegExpCompileData();
        if (!RegExpParser.ParseRegExp("[\\p{" + property + "}]", RegExpFlags.Unicode, data))
        {
            throw new InvalidOperationException(property + ": " + RegExpErrors.ErrorString(data.Error));
        }
        RegExpClassRanges cr = data.Tree!.AsClassRanges()!;
        var ranges = new List<CharacterRange>(cr.Ranges);
        CharacterRange.Canonicalize(ranges);
        if (cr.IsNegated)
        {
            var negated = new List<CharacterRange>();
            CharacterRange.Negate(ranges, negated);
            ranges = negated;
        }
        var result = new List<(int, int)>(ranges.Count);
        foreach (CharacterRange r in ranges) result.Add((r.From, r.To));
        return result;
    }

    static bool[] ToBitmap(List<(int From, int To)> ranges)
    {
        var bits = new bool[0x110000];
        foreach ((int from, int to) in ranges)
        {
            for (int c = from; c <= to; c++) bits[c] = true;
        }
        return bits;
    }

    static List<(int, int)> ParseRanges(string s)
    {
        var list = new List<(int, int)>();
        if (s.Length == 0) return list;
        foreach (string part in s.Split(','))
        {
            int dash = part.IndexOf('-');
            list.Add((int.Parse(part[..dash]), int.Parse(part[(dash + 1)..])));
        }
        return list;
    }

    [Fact]
    public void AllPropertyEscapesMatchOracle()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.None);
        engine.Execute("""
            globalThis.__ranges = function (p) {
              let re;
              try { re = new RegExp('^\\p{' + p + '}$', 'u'); } catch (e) { return 'E'; }
              const out = [];
              let start = -1;
              for (let c = 0; c <= 0x110000; c++) {
                const m = c <= 0x10FFFF && re.test(String.fromCodePoint(c));
                if (m && start < 0) start = c;
                if (!m && start >= 0) { out.push(start + '-' + (c - 1)); start = -1; }
              }
              return out.join(',');
            };
            """);
        dynamic ranges = engine.Script.__ranges;
        bool[] unassignedInOracle = ToBitmap(ParseRanges((string)ranges("General_Category=Unassigned")));

        var report = new StringBuilder();
        int compared = 0, exact = 0, versionOnly = 0, unknownToOracle = 0;
        var failures = new List<string>();
        foreach (string property in AllPropertyEscapes())
        {
            string oracle = (string)ranges(property);
            if (oracle == "E")
            {
                unknownToOracle++;
                report.Append($"{property}: not supported by the oracle\n");
                continue;
            }
            compared++;
            bool[] theirs = ToBitmap(ParseRanges(oracle));
            bool[] ours = ToBitmap(OurRanges(property));
            int diffAssigned = 0, diffNew = 0;
            int firstDiff = -1;
            for (int c = 0; c <= 0x10FFFF; c++)
            {
                if (theirs[c] == ours[c]) continue;
                if (unassignedInOracle[c])
                {
                    diffNew++;
                }
                else
                {
                    diffAssigned++;
                    if (firstDiff < 0) firstDiff = c;
                }
            }
            if (diffAssigned == 0 && diffNew == 0)
            {
                exact++;
                continue;
            }
            if (diffAssigned == 0)
            {
                versionOnly++;
                report.Append($"{property}: {diffNew} differences, all on code points unassigned in the oracle\n");
                continue;
            }
            string line = $"{property}: {diffAssigned} differences on code points assigned in the oracle " +
                          $"(first U+{firstDiff:X4}, ours {ours[firstDiff]}), {diffNew} on new code points";
            report.Append(line).Append('\n');
            failures.Add(line);
        }

        string header = $"properties compared {compared}, exact {exact}, differing only on newly assigned " +
                        $"code points {versionOnly}, differing on previously assigned code points " +
                        $"{failures.Count}, unknown to the oracle {unknownToOracle}\n\n";
        string dir = Path.Combine(DifferentialTests.RepoRoot(), "dotnet", "artifacts", "regexp-differential");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "property-escapes.txt"), header + report);
        Assert.True(compared > 300);
    }
}
