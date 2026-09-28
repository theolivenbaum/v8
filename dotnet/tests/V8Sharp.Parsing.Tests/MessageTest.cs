// Checks parse errors against the golden files of test/message: for every
// test whose expected output is an early SyntaxError (no stack trace), the
// parser must report the same message on the same line, and the caret line
// d8 prints under the source line must match the error's start and end
// positions.

#nullable disable

using System.Text;
using System.Text.RegularExpressions;

namespace V8Sharp.Parsing.Tests;

public class MessageTest(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "dotnet", "V8Sharp.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private sealed record Expected(int line, string message, string source_line, string caret_line);

    // "*%(basename)s:LINE: SyntaxError: MESSAGE", the source line and the
    // caret line, with no stack trace after them.
    private static Expected ParseExpected(string out_text)
    {
        var all = new List<string>();
        foreach (string raw in out_text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith('#') || raw.Trim().Length == 0) continue;
            all.Add(raw);
        }
        string[] lines = all.ToArray();
        if (lines.Length < 3) return null;
        const string prefix = "*%(basename)s:";
        if (!lines[0].StartsWith(prefix, StringComparison.Ordinal)) return null;
        string rest = lines[0][prefix.Length..];
        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(rest.AsSpan(0, colon), out int line)) return null;
        string error = rest[(colon + 2)..];
        const string syntax_error = "SyntaxError: ";
        if (!error.StartsWith(syntax_error, StringComparison.Ordinal)) return null;
        foreach (string l in lines)
        {
            if (l.StartsWith("    at ", StringComparison.Ordinal)) return null;
        }
        return new Expected(line, error[syntax_error.Length..], lines[1], lines[2]);
    }

    // tools/testrunner/outproc/message.py: each non-comment, non-blank
    // expected line is a pattern in which '*' matches anything.
    private static bool OutputMatches(string out_text, string basename, string[] actual)
    {
        var expected = new List<string>();
        foreach (string raw in out_text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith('#') || raw.Trim().Length == 0) continue;
            expected.Add(raw);
        }
        var actual_lines = new List<string>();
        foreach (string a in actual)
        {
            if (a.Trim().Length != 0) actual_lines.Add(a);
        }
        if (expected.Count != actual_lines.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            string pattern = Regex.Escape(expected[i].TrimEnd().Replace("%(basename)s", basename,
                                                                        StringComparison.Ordinal));
            pattern = pattern.Replace(@"\*", ".*", StringComparison.Ordinal);
            if (!Regex.IsMatch(actual_lines[i], "^" + pattern + "$")) return false;
        }
        return true;
    }

    private static string ReadFlags(string source)
    {
        var flags = new StringBuilder();
        foreach (string l in source.Split('\n'))
        {
            if (l.StartsWith("// Flags:", StringComparison.Ordinal)) flags.Append(l).Append(' ');
        }
        return flags.ToString();
    }

    [Fact]
    public void EarlySyntaxErrorsMatchGoldenFiles()
    {
        string root = Path.Combine(RepoRoot(), "test", "message");
        var failures = new StringBuilder();
        int total = 0, passed = 0;
        foreach (string out_file in Directory.EnumerateFiles(root, "*.out", SearchOption.AllDirectories).Order())
        {
            Expected expected = ParseExpected(File.ReadAllText(out_file));
            if (expected == null) continue;
            string js = Path.ChangeExtension(out_file, ".js");
            bool module = false;
            if (!File.Exists(js))
            {
                js = Path.ChangeExtension(out_file, ".mjs");
                module = true;
                if (!File.Exists(js)) continue;
            }
            string source = File.ReadAllText(js);
            string flags_line = ReadFlags(source);
            ParsingFlags flags = new()
            {
                allow_natives_syntax = flags_line.Contains("--allow-natives-syntax", StringComparison.Ordinal),
                js_decorators = flags_line.Contains("--js-decorators", StringComparison.Ordinal),
                js_source_phase_imports = flags_line.Contains("--js-source-phase-imports", StringComparison.Ordinal),
                js_defer_import_eval = flags_line.Contains("--js-defer-import-eval", StringComparison.Ordinal),
            };
            UnoptimizedCompileFlags compile_flags = UnoptimizedCompileFlags.ForToplevelCompile(
                flags, 1, true, V8Sharp.Common.LanguageMode.Sloppy, V8Sharp.Common.REPLMode.No,
                module ? V8Sharp.Common.ScriptType.Module : V8Sharp.Common.ScriptType.Classic, flags.lazy);
            ParseInfo info = new(compile_flags, flags);
            bool ok = ParsingEntry.ParseProgram(info, new SourceScript(source));
            string relative = Path.GetRelativePath(root, js).Replace('\\', '/');
            if (ok)
            {
                // Thrown at run time (eval, RegExp, Function, JSON): not a parse error.
                continue;
            }
            total++;
            PendingCompilationErrorHandler handler = info.pending_error_handler();
            string message = handler.FormatErrorMessageForTest();
            int start = handler.error_details().start_pos();
            int end = handler.error_details().end_pos();
            int line = 1, line_start = 0;
            for (int i = 0; i < start && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    line_start = i + 1;
                }
            }
            var caret = new StringBuilder();
            caret.Append(' ', start - line_start);
            caret.Append('^', Math.Max(0, end - start));
            string caret_line = caret.ToString();
            string source_line = source[line_start..];
            int nl = source_line.IndexOf('\n');
            if (nl >= 0) source_line = source_line[..nl];
            // d8's report: location line, source line, caret line, message.
            string[] actual =
            [
                Path.GetFileName(js) + ":" + line + ": SyntaxError: " + message,
                source_line,
                caret_line,
                "SyntaxError: " + message,
            ];
            if (OutputMatches(File.ReadAllText(out_file), Path.GetFileName(js), actual))
            {
                passed++;
            }
            else
            {
                failures.Append(relative).Append(": expected ").Append(expected.line).Append(": ")
                    .Append(expected.message).Append(" [").Append(expected.caret_line.Trim().Length)
                    .Append('@').Append(expected.caret_line.Length - expected.caret_line.TrimStart().Length)
                    .Append("], got ").Append(line).Append(": ").Append(message).Append(" [")
                    .Append(end - start).Append('@').Append(start - line_start).AppendLine("]");
            }
        }
        output.WriteLine($"{passed}/{total} early SyntaxErrors match");
        output.WriteLine(failures.ToString());
        Assert.True(failures.Length == 0, failures.ToString());
    }
}
