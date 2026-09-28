// Port of tools/testrunner/outproc/{base,test262,message,webkit,mozilla}.py:
// how each suite decides, from a run's exit code and output, whether the test
// passed.
using System.Text;
using System.Text.RegularExpressions;
using V8Sharp.TestRunner.Status;

namespace V8Sharp.TestRunner.OutProc;

/// <summary>What one run of d8 produced (objects/output.py).</summary>
public sealed record RunOutput(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Crashed, TimeSpan Duration)
{
    public bool HasCrashed => Crashed && !TimedOut;
}

/// <summary>base.OutProc: outcome = CRASH / TIMEOUT / FAIL / PASS.</summary>
public class OutputProcessor
{
    public OutputProcessor(IReadOnlyList<string> expectedOutcomes) => ExpectedOutcomes = expectedOutcomes;

    public IReadOnlyList<string> ExpectedOutcomes { get; set; }

    /// <summary>Negative tests pass when execution fails.</summary>
    public virtual bool Negative => false;

    public string GetOutcome(RunOutput output)
    {
        if (output.HasCrashed) return Outcome.Crash;
        if (output.TimedOut) return Outcome.Timeout;
        bool failed = IsFailureOutput(output);
        if (Negative) failed = !failed;
        return failed ? Outcome.Fail : Outcome.Pass;
    }

    public bool HasUnexpectedOutput(RunOutput output) => !ExpectedOutcomes.Contains(GetOutcome(output));

    protected virtual bool IsFailureOutput(RunOutput output) => output.ExitCode != 0;

    /// <summary>A diff or reason for the report when the outcome is unexpected.</summary>
    public virtual string? ErrorDetails(RunOutput output) => null;
}

/// <summary>ExpectedOutProc: compares stdout with an expectation file line by
/// line, ignoring blank and noise lines (webkit's <c>-expected.txt</c>).</summary>
public class ExpectedOutputProcessor(IReadOnlyList<string> expected, string expectedFile) : OutputProcessor(expected)
{
    protected string ExpectedFile { get; } = expectedFile;

    protected override bool IsFailureOutput(RunOutput output)
    {
        if (output.ExitCode != 0) return true;
        var expectedLines = File.ReadAllLines(ExpectedFile, Encoding.UTF8);
        foreach (var block in ActualBlocks(output))
        {
            var exp = Filter(expectedLines, IgnoreExpectedLine).GetEnumerator();
            var act = Filter(block, IgnoreActualLine).GetEnumerator();
            while (true)
            {
                bool he = exp.MoveNext(), ha = act.MoveNext();
                if (!he && !ha) break;
                if ((he ? exp.Current : "") != (ha ? act.Current : "")) return true;
            }
        }
        return false;
    }

    static IEnumerable<string> Filter(IEnumerable<string> lines, Func<string, bool> ignore)
    {
        foreach (var l in lines)
        {
            string t = l.Trim();
            if (!ignore(t)) yield return t;
        }
    }

    /// <summary>Blocks separated by stress-run "==" lines; the whole output when there are none.</summary>
    static IEnumerable<IReadOnlyList<string>> ActualBlocks(RunOutput output)
    {
        var lines = SplitLines(output.Stdout);
        int start = 0;
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("==", StringComparison.Ordinal))
            {
                if (!found) found = true;
                else yield return lines.GetRange(start, i - start);
                start = i + 1;
            }
        }
        if (!found) yield return lines;
    }

    protected virtual bool IgnoreActualLine(string line) =>
        line.Length == 0 || line.StartsWith("==", StringComparison.Ordinal) || line.StartsWith("**", StringComparison.Ordinal) ||
        line.StartsWith("ANDROID", StringComparison.Ordinal) || line.StartsWith("###", StringComparison.Ordinal) ||
        line.StartsWith("WARNING: linker:", StringComparison.Ordinal) || line.StartsWith("Warning: unknown flag", StringComparison.Ordinal) ||
        line == "Try --help for options";

    protected virtual bool IgnoreExpectedLine(string line) => line.Length == 0;

    /// <summary>Python's str.splitlines.</summary>
    public static List<string> SplitLines(string s)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                result.Add(s[start..i]);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < s.Length) result.Add(s[start..]);
        return result;
    }

    public override string? ErrorDetails(RunOutput output)
    {
        var expected = File.ReadAllText(ExpectedFile, Encoding.UTF8);
        return "Output does not match " + Path.GetFileName(ExpectedFile) + ":\n--- expected\n" + expected + "\n--- actual\n" + output.Stdout;
    }
}

/// <summary>outproc/webkit.py.</summary>
public sealed class WebkitOutputProcessor(IReadOnlyList<string> expected, string expectedFile) : ExpectedOutputProcessor(expected, expectedFile)
{
    protected override bool IsFailureOutput(RunOutput output)
    {
        if (output.ExitCode != 0) return true;
        // --exit-on-contradictory-flags lets tests exit with 0 and no output.
        if (output.Stdout.Trim().Length == 0) return false;
        return base.IsFailureOutput(output);
    }

    protected override bool IgnoreExpectedLine(string line) => line.StartsWith('#') || base.IgnoreExpectedLine(line);
}

/// <summary>outproc/message.py: the exit code must match the test's
/// directory (<c>fail/</c> exits non-zero) and stdout must match the
/// <c>.out</c> file, where <c>*</c> is a wildcard, <c>%(basename)s</c> the
/// test's file name, and <c>{NUMBER}</c>/<c>{ADDRESS}</c> placeholders.</summary>
public sealed class MessageOutputProcessor(IReadOnlyList<string> expected, string basePath, bool expectedFail) : OutputProcessor(expected)
{
    string OutFile => basePath + ".out";

    protected override bool IsFailureOutput(RunOutput output)
    {
        bool fail = output.ExitCode != 0;
        if (fail != expectedFail) return true;
        var expectedLines = new List<string>();
        foreach (var line in File.ReadAllLines(OutFile, Encoding.UTF8))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            expectedLines.Add(line);
        }
        var actualLines = ExpectedOutputProcessor.SplitLines(output.Stdout).FindAll(s => !IgnoreLine(s));
        if (expectedLines.Count != actualLines.Count) return true;
        string js = File.Exists(basePath + ".js") ? basePath + ".js" : basePath + ".mjs";
        string basename = Path.GetFileName(js);
        for (int i = 0; i < expectedLines.Count; i++)
        {
            string pattern = Regex.Escape(expectedLines[i].TrimEnd().Replace("%(basename)s", basename, StringComparison.Ordinal));
            pattern = pattern.Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\{NUMBER}", @"-?\d+(?:\.\d*)?", StringComparison.Ordinal)
                .Replace("\\{ADDRESS}", "(0x)?[0-9A-Fa-f]+", StringComparison.Ordinal);
            if (!Regex.IsMatch(actualLines[i], "^" + pattern + "$")) return true;
        }
        return false;
    }

    static bool IgnoreLine(string s) =>
        s.Trim().Length == 0 || s.StartsWith("==", StringComparison.Ordinal) || s.StartsWith("**", StringComparison.Ordinal) ||
        s.StartsWith("ANDROID", StringComparison.Ordinal) || s.StartsWith("WARNING: linker:", StringComparison.Ordinal) ||
        s.StartsWith("V8 is running with", StringComparison.Ordinal) || s == "Concurrent maglev has been disabled for tracing.";

    public override string? ErrorDetails(RunOutput output) =>
        $"exit code {output.ExitCode} (expected {(expectedFail ? "non-zero" : "0")}); expected output:\n" +
        File.ReadAllText(OutFile, Encoding.UTF8);
}

/// <summary>outproc/test262.py.</summary>
public sealed partial class Test262OutputProcessor(IReadOnlyList<string> expected, string? expectedException, bool negative, bool isAsync)
    : OutputProcessor(expected)
{
    public override bool Negative => negative;

    static bool IsFailure(RunOutput output, bool isAsync) =>
        output.ExitCode != 0 || output.Stdout.Contains("FAILED!", StringComparison.Ordinal) ||
        (isAsync && !output.Stdout.Contains("Test262:AsyncTestComplete", StringComparison.Ordinal));

    protected override bool IsFailureOutput(RunOutput output)
    {
        if (expectedException is not null && expectedException != ParseException(output.Stdout)) return true;
        return IsFailure(output, isAsync);
    }

    [GeneratedRegex(@"^(?:\w:)?[^:\n]*:[0-9]+: ([^: \n]+?)($|: )", RegexOptions.Multiline)]
    private static partial Regex ExceptionPattern();

    /// <summary>"somefile:line: SomeError[: text]" from d8's uncaught-exception report.</summary>
    public static string? ParseException(string stdout)
    {
        var m = ExceptionPattern().Match(stdout.Replace("\r\n", "\n"));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public override string? ErrorDetails(RunOutput output) =>
        expectedException is null ? null : $"expected exception {expectedException}, got {ParseException(output.Stdout) ?? "none"}";
}

/// <summary>outproc/mozilla.py: negative tests end in <c>-n</c>.</summary>
public sealed class MozillaOutputProcessor(IReadOnlyList<string> expected, bool negative) : OutputProcessor(expected)
{
    public override bool Negative => negative;

    protected override bool IsFailureOutput(RunOutput output) =>
        output.ExitCode != 0 || output.Stdout.Contains("FAILED!", StringComparison.Ordinal);
}
