using System.Text;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace V8Sharp.RegExp.Tests.Differential;

/// <summary>
/// Runs (pattern, flags, subject) triples through the real V8 (the oracle)
/// and through V8Sharp.RegExp, rendering both results in the same textual
/// form so they can be compared exactly.
/// </summary>
internal sealed class DifferentialRunner : IDisposable
{
    readonly V8ScriptEngine _engine;
    readonly Timer _watchdog;
    readonly dynamic _run;

    /// <summary>Oracle time budget per triple; slower triples are skipped.</summary>
    public int OracleTimeoutMs { get; set; } = 200;

    /// <summary>Backtrack limit for our engine, a backstop against runaway patterns.</summary>
    public uint OurBacktrackLimit { get; set; } = 100_000_000;

    public DifferentialRunner()
    {
        _engine = new V8ScriptEngine(V8ScriptEngineFlags.None);
        _watchdog = new Timer(_ => _engine.Interrupt());
        _engine.Execute("""
            (function () {
              const adv = (s, i, u) => {
                if (!u || i + 1 >= s.length) return i + 1;
                const c = s.charCodeAt(i);
                if (c < 0xD800 || c > 0xDBFF) return i + 1;
                const d = s.charCodeAt(i + 1);
                return (d >= 0xDC00 && d <= 0xDFFF) ? i + 2 : i + 1;
              };
              const enc = (m) => {
                let r = m.index + ':' + JSON.stringify(Array.from(m, x => x === undefined ? null : x));
                if (m.groups) r += ':' + JSON.stringify(m.groups, (k, v) => v === undefined ? null : v);
                return r;
              };
              globalThis.__run = function (p, f, s) {
                let re;
                try { re = new RegExp(p, f); } catch (e) { return 'E:' + e.message; }
                try {
                  if (!re.global) {
                    const m = re.exec(s);
                    return m === null ? 'null' : enc(m);
                  }
                  const out = [];
                  for (let n = 0; n < 20; n++) {
                    const m = re.exec(s);
                    if (m === null) break;
                    out.push(enc(m));
                    if (m[0] === '') re.lastIndex = adv(s, re.lastIndex, re.unicode || re.unicodeSets);
                  }
                  return out.length === 0 ? 'null' : out.join(' ; ');
                } catch (e) {
                  return 'X:' + e.name + ': ' + e.message;
                }
              };
            })();
            """);
        _run = _engine.Script.__run;
    }

    dynamic? _hasUnassigned;

    /// <summary>Whether <paramref name="s"/> has a code point the oracle's Unicode version does not assign.</summary>
    public bool HasCodePointUnassignedInOracle(string s)
    {
        _hasUnassigned ??= _engine.Evaluate("(s) => /\\p{Cn}/u.test(s)");
        return (bool)_hasUnassigned(s);
    }

    /// <summary>Returns the oracle's rendering, or null if it timed out.</summary>
    public string? RunOracle(string pattern, string flags, string subject)
    {
        _watchdog.Change(OracleTimeoutMs, Timeout.Infinite);
        try
        {
            return (string)_run(pattern, flags, subject);
        }
        catch (ScriptInterruptedException)
        {
            return null;
        }
        catch (ScriptEngineException e)
        {
            return "Oracle exception: " + e.Message;
        }
        finally
        {
            _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    static readonly Dictionary<(string, string), RegExpCompileResult> s_cache = new();

    public string RunOurs(string pattern, string flagsString, string subject)
    {
        RegExpFlags? maybeFlags = RegExpFlagsExtensions.FromString(flagsString);
        // The 'l' flag needs --enable-experimental-regexp-engine in V8.
        if (maybeFlags is null || (maybeFlags.Value & RegExpFlags.Linear) != 0 || !RegExpEngine.VerifyFlags(maybeFlags.Value))
        {
            return "E:Invalid flags supplied to RegExp constructor '" + flagsString + "'";
        }
        RegExpFlags flags = maybeFlags.Value;
        RegExpCompileResult result = RegExpEngine.Compile(pattern, flags, OurBacktrackLimit);
        if (!result.Succeeded)
        {
            return "E:Invalid regular expression: /" + pattern + "/" + flags.ToFlagString() + ": " +
                   result.ErrorMessage;
        }
        CompiledRegExp re = result.RegExp!;
        int[] regs = new int[re.RegistersPerMatch];
        bool global = flags.IsGlobal();
        bool sticky = flags.IsSticky();
        bool fullUnicode = flags.IsEitherUnicode();
        int lastIndex = 0;
        var outList = new List<string>();
        for (int n = 0; n < (global ? 20 : 1); n++)
        {
            int start = global || sticky ? lastIndex : 0;
            if (start > subject.Length) break;
            int r = re.Exec(subject, start, regs);
            if (r < 0)
            {
                if (re.CompileError != RegExpError.None)
                {
                    return "X:SyntaxError: Invalid regular expression: /" + pattern + "/" + flags.ToFlagString() +
                           ": " + RegExpErrors.ErrorString(re.CompileError);
                }
                return "X:RangeError: Maximum call stack size exceeded";
            }
            if (r == 0) break;
            outList.Add(Encode(re, subject, regs));
            lastIndex = regs[1];
            if (regs[0] == regs[1]) lastIndex = RegExpUtils.AdvanceStringIndex(subject, lastIndex, fullUnicode);
        }
        return outList.Count == 0 ? "null" : string.Join(" ; ", outList);
    }

    static string Encode(CompiledRegExp re, string subject, int[] regs)
    {
        var sb = new StringBuilder();
        sb.Append(regs[0]).Append(":[");
        for (int i = 0; i <= re.CaptureCount; i++)
        {
            if (i > 0) sb.Append(',');
            AppendCapture(sb, subject, regs, i);
        }
        sb.Append(']');
        if (re.CaptureNameMap is not null)
        {
            sb.Append(":{");
            var seen = new HashSet<string>();
            bool first = true;
            foreach (KeyValuePair<string, int> kv in re.CaptureNameMap)
            {
                if (!seen.Add(kv.Key)) continue;
                // With duplicate named groups, the value is the participating one.
                int index = kv.Value;
                foreach (KeyValuePair<string, int> other in re.CaptureNameMap)
                {
                    if (other.Key == kv.Key && regs[2 * other.Value] >= 0)
                    {
                        index = other.Value;
                        break;
                    }
                }
                if (!first) sb.Append(',');
                first = false;
                AppendJsonString(sb, kv.Key);
                sb.Append(':');
                AppendCapture(sb, subject, regs, index);
            }
            sb.Append('}');
        }
        return sb.ToString();
    }

    static void AppendCapture(StringBuilder sb, string subject, int[] regs, int i)
    {
        int s = regs[2 * i], e = regs[2 * i + 1];
        if (s < 0)
        {
            sb.Append("null");
            return;
        }
        AppendJsonString(sb, subject.Substring(s, e - s));
    }

    // JSON.stringify for strings (well-formed: lone surrogates escaped).
    static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _engine.Dispose();
    }
}
