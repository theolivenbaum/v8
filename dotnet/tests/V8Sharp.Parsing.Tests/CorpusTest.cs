#nullable disable
// Parses the mjsunit and test262 corpora. Explicit: run with
//   dotnet test tests/V8Sharp.Parsing.Tests -- --explicit only
// Results are written under dotnet/artifacts/parsing-corpus/.

using System.Text;
using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing.Tests;

public class CorpusTest(ITestOutputHelper output)
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

    private static string ArtifactsDir()
    {
        string dir = Path.Combine(RepoRoot(), "dotnet", "artifacts", "parsing-corpus");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Test262Root()
    {
        string candidate = Path.Combine(RepoRoot(), "test", "test262", "data", "test");
        if (Directory.Exists(candidate)) return candidate;
        candidate = "/home/user/theolivenbaum/test262/test";
        return Directory.Exists(candidate) ? candidate : null;
    }

    // Parses a whole program; returns null on success or the formatted error.
    public static string ParseSource(string source, bool module, bool strict, ParsingFlags flags)
    {
        UnoptimizedCompileFlags compile_flags = UnoptimizedCompileFlags.ForToplevelCompile(
            flags, 1, true, strict ? LanguageMode.Strict : LanguageMode.Sloppy, REPLMode.No,
            module ? ScriptType.Module : ScriptType.Classic, flags.lazy);
        ParseInfo info = new(compile_flags, flags);
        bool ok;
        try
        {
            ok = ParsingEntry.ParseProgram(info, new SourceScript(source));
        }
        catch (Exception e)
        {
            return "CRASH " + e.GetType().Name + ": " + e.Message + " " +
                   e.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        }
        PendingCompilationErrorHandler handler = info.pending_error_handler();
        if (ok && !handler.has_pending_error()) return null;
        if (handler.stack_overflow()) return "RangeError: Maximum call stack size exceeded";
        if (!handler.has_pending_error()) return "failed without error";
        return "SyntaxError: " + handler.FormatErrorMessageForTest() + " @" + handler.error_details().start_pos();
    }

    [Fact(Explicit = true)]
    public void Mjsunit()
    {
        string root = Path.Combine(RepoRoot(), "test", "mjsunit");
        var failures = new StringBuilder();
        var skipped = new StringBuilder();
        int total = 0, passed = 0, skipped_count = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Order())
        {
            bool module = file.EndsWith(".mjs", StringComparison.Ordinal);
            if (!module && !file.EndsWith(".js", StringComparison.Ordinal)) continue;
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string source = File.ReadAllText(file);
            var flags_builder = new StringBuilder();
            foreach (string l in source.Split('\n'))
            {
                if (l.StartsWith("// Flags:", StringComparison.Ordinal)) flags_builder.Append(l).Append(' ');
            }
            string flags_line = flags_builder.ToString();
            // Helpers imported by other tests, and bundles run by d8 --bundle.
            if (Path.GetFileName(file).Contains("modules-skip", StringComparison.Ordinal) ||
                flags_line.Contains("--bundle", StringComparison.Ordinal) ||
                relative.StartsWith("d8/module-bundle", StringComparison.Ordinal))
            {
                skipped_count++;
                skipped.Append(relative).AppendLine(": module helper or bundle");
                continue;
            }
            ParsingFlags flags = new()
            {
                allow_natives_syntax = true,
                js_decorators = flags_line.Contains("--js-decorators", StringComparison.Ordinal),
                js_defer_import_eval = flags_line.Contains("--js-defer-import-eval", StringComparison.Ordinal),
                js_source_phase_imports = flags_line.Contains("--js-source-phase-imports", StringComparison.Ordinal),
                enable_experimental_regexp_engine =
                    flags_line.Contains("--enable-experimental-regexp", StringComparison.Ordinal),
                fuzzing = flags_line.Contains("--fuzzing", StringComparison.Ordinal),
            };
            string error = ParseSource(source, module, false, flags);
            if (error == null && flags_line.Contains("--throws", StringComparison.Ordinal))
            {
                // d8 --throws expects an uncaught exception, which for these
                // tests is thrown at run time, after a successful parse.
                skipped_count++;
                skipped.Append(relative).AppendLine(": --throws at run time");
                continue;
            }
            if (error != null && error.Contains("is not defined", StringComparison.Ordinal) &&
                (error.Contains("Wasm", StringComparison.Ordinal) || relative.Contains("wasm", StringComparison.Ordinal)))
            {
                // Intrinsics of V8's WebAssembly build; the runtime table here
                // is the no-wasm configuration.
                skipped_count++;
                skipped.Append(relative).Append(": ").AppendLine(error);
                continue;
            }
            total++;
            if (error == null || flags_line.Contains("--throws", StringComparison.Ordinal))
            {
                passed++;
            }
            else
            {
                failures.Append(relative).Append(": ").AppendLine(error);
            }
        }
        string summary =
            $"mjsunit: {passed}/{total} as expected ({100.0 * passed / total:F2}%), {skipped_count} skipped";
        File.WriteAllText(Path.Combine(ArtifactsDir(), "mjsunit.txt"),
                          summary + "\n" + failures + "\n# Skipped\n" + skipped);
        output.WriteLine(summary);
    }

    private sealed class Test262Meta
    {
        public bool NegativeParse;
        public bool Module;
        public bool OnlyStrict;
        public bool NoStrict;
        public bool Raw;
        public List<string> Features = [];
    }

    private static Test262Meta ParseMeta(string source)
    {
        var meta = new Test262Meta();
        int start = source.IndexOf("/*---", StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("---*/", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return meta;
        string yaml = source[(start + 5)..end];
        string[] lines = yaml.Split('\n');
        bool inNegative = false, inFeatures = false, inFlags = false;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            bool indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            if (!indented)
            {
                inNegative = trimmed.StartsWith("negative:", StringComparison.Ordinal);
                inFeatures = trimmed.StartsWith("features:", StringComparison.Ordinal);
                inFlags = trimmed.StartsWith("flags:", StringComparison.Ordinal);
                if (inFeatures || inFlags)
                {
                    int lb = trimmed.IndexOf('[');
                    int rb = trimmed.IndexOf(']');
                    if (lb >= 0 && rb > lb)
                    {
                        foreach (string item in trimmed[(lb + 1)..rb].Split(','))
                        {
                            AddItem(meta, inFlags, item.Trim());
                        }
                        inFeatures = inFlags = false;
                    }
                }
                continue;
            }
            if (inNegative && trimmed.StartsWith("phase:", StringComparison.Ordinal))
            {
                string phase = trimmed["phase:".Length..].Trim();
                meta.NegativeParse = phase == "parse" || phase == "early";
            }
            if ((inFeatures || inFlags) && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                AddItem(meta, inFlags, trimmed[2..].Trim());
            }
        }
        return meta;
    }

    private static void AddItem(Test262Meta meta, bool isFlag, string item)
    {
        if (item.Length == 0) return;
        if (!isFlag)
        {
            meta.Features.Add(item);
            return;
        }
        switch (item)
        {
            case "module": meta.Module = true; break;
            case "onlyStrict": meta.OnlyStrict = true; break;
            case "noStrict": meta.NoStrict = true; break;
            case "raw": meta.Raw = true; break;
        }
    }

    // The known V8 failures listed in test/test262/test262.status sections
    // that apply to a default x64 build (ALWAYS and 'not i18n', since intl402
    // is not run here), as path prefixes or exact paths without ".js".
    private static List<string> KnownV8Failures()
    {
        var result = new List<string>();
        string status = Path.Combine(RepoRoot(), "test", "test262", "test262.status");
        bool applies = false;
        foreach (string line in File.ReadLines(status))
        {
            string t = line.Trim();
            if (line.StartsWith('['))
            {
                applies = t.StartsWith("[ALWAYS", StringComparison.Ordinal) ||
                          t.StartsWith("['not i18n'", StringComparison.Ordinal);
                continue;
            }
            if (!applies || !t.StartsWith('\'') ||
                !(t.Contains("FAIL", StringComparison.Ordinal) || t.Contains("SKIP", StringComparison.Ordinal)))
            {
                continue;
            }
            int q = t.IndexOf('\'', 1);
            if (q < 0) continue;
            result.Add(t[1..q]);
        }
        return result;
    }

    private static bool IsKnownFailure(List<string> known, string relative)
    {
        foreach (string k in known)
        {
            if (k.EndsWith('*'))
            {
                if (relative.StartsWith(k[..^1], StringComparison.Ordinal)) return true;
            }
            else if (relative == k)
            {
                return true;
            }
        }
        return false;
    }

    [Fact(Explicit = true)]
    public void Test262()
    {
        string root = Test262Root();
        Assert.SkipWhen(root == null, "test262 checkout not found");
        List<string> known = KnownV8Failures();
        var failures = new StringBuilder();
        var knownFailures = new StringBuilder();
        int total = 0, passed = 0, known_count = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories).Order())
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("harness/", StringComparison.Ordinal)) continue;
            if (relative.Contains("_FIXTURE", StringComparison.Ordinal)) continue;
            if (relative.StartsWith("intl402/", StringComparison.Ordinal) ||
                relative.StartsWith("staging/", StringComparison.Ordinal))
            {
                continue;
            }
            string source = File.ReadAllText(file);
            Test262Meta meta = ParseMeta(source);
            ParsingFlags flags = new()
            {
                allow_natives_syntax = meta.Features.Contains("IsHTMLDDA") ||
                                       meta.Features.Contains("source-phase-imports"),
                js_decorators = meta.Features.Contains("decorators"),
                js_source_phase_imports = meta.Features.Contains("source-phase-imports"),
                js_defer_import_eval = meta.Features.Contains("import-defer"),
            };
            var variants = new List<bool>();
            if (meta.Module || meta.Raw || meta.NoStrict)
            {
                variants.Add(false);
            }
            else if (meta.OnlyStrict)
            {
                variants.Add(true);
            }
            else
            {
                variants.Add(false);
                variants.Add(true);
            }
            foreach (bool strict in variants)
            {
                total++;
                string error = ParseSource(source, meta.Module, strict, flags);
                bool ok = meta.NegativeParse ? error != null && !error.StartsWith("CRASH", StringComparison.Ordinal)
                                             : error == null;
                string name = relative[..^3] + (strict ? " (strict)" : "");
                if (ok)
                {
                    passed++;
                }
                else if (IsKnownFailure(known, relative[..^3]))
                {
                    known_count++;
                    knownFailures.Append(name).Append(": ")
                        .AppendLine(meta.NegativeParse ? "expected SyntaxError, parsed" : error);
                }
                else
                {
                    failures.Append(name).Append(": ")
                        .AppendLine(meta.NegativeParse ? "expected SyntaxError, parsed" : error);
                }
            }
        }
        string summary =
            $"test262: {passed}/{total} as expected ({100.0 * passed / total:F2}%), " +
            $"{known_count} more listed as failing in test262.status";
        File.WriteAllText(Path.Combine(ArtifactsDir(), "test262.txt"),
                          summary + "\n" + failures + "\n# Known V8 failures\n" + knownFailures);
        output.WriteLine(summary);
    }

    private static string OracleError(V8Sharp.Oracle.ReferenceV8 v8, string program)
    {
        string theirs = v8.Run(program).TrimEnd('\n');
        const string uncaught = "Uncaught ";
        return theirs.StartsWith(uncaught, StringComparison.Ordinal) ? theirs[uncaught.Length..] : theirs;
    }

    // The SyntaxError message of every negative (parse phase) classic-script
    // test262 test, compared with the one real V8 reports. The oracle is an
    // older V8 (see dotnet/UPSTREAM.md), so a few messages legitimately
    // differ; the list is written for review rather than asserted.
    [Fact(Explicit = true)]
    public void Test262NegativeMessagesMatchOracle()
    {
        string root = Test262Root();
        Assert.SkipWhen(root == null, "test262 checkout not found");
        List<string> known = KnownV8Failures();
        var mismatches = new StringBuilder();
        int total = 0, matched = 0;
        using var v8 = new V8Sharp.Oracle.ReferenceV8(allowNativesSyntax: false);
        foreach (string file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories).Order())
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("harness/", StringComparison.Ordinal) ||
                relative.Contains("_FIXTURE", StringComparison.Ordinal) ||
                relative.StartsWith("intl402/", StringComparison.Ordinal) ||
                relative.StartsWith("staging/", StringComparison.Ordinal))
            {
                continue;
            }
            string source = File.ReadAllText(file);
            Test262Meta meta = ParseMeta(source);
            if (!meta.NegativeParse || meta.Module || IsKnownFailure(known, relative[..^3])) continue;
            if (meta.Features.Contains("decorators") || meta.Features.Contains("source-phase-imports") ||
                meta.Features.Contains("import-defer") || meta.Features.Contains("IsHTMLDDA"))
            {
                continue;
            }
            // A regexp literal is only checked with a validator (see Scanner).
            var strict_variants = new List<bool>();
            if (meta.Raw || meta.NoStrict) strict_variants.Add(false);
            else if (meta.OnlyStrict) strict_variants.Add(true);
            else
            {
                strict_variants.Add(false);
                strict_variants.Add(true);
            }
            foreach (bool strict in strict_variants)
            {
                string program = strict ? "'use strict';\n" + source : source;
                string ours = ParseSource(program, false, false, new ParsingFlags());
                if (ours == null) continue;  // counted by Test262()
                int at = ours.LastIndexOf(" @", StringComparison.Ordinal);
                if (at >= 0) ours = ours[..at];
                string theirs = OracleError(v8, program);
                if (ours != theirs)
                {
                    // A program V8 ran may have left globals behind that
                    // break a later one: retry in a fresh isolate.
                    using var fresh = new V8Sharp.Oracle.ReferenceV8(allowNativesSyntax: false);
                    theirs = OracleError(fresh, program);
                }
                total++;
                if (ours == theirs)
                {
                    matched++;
                }
                else
                {
                    mismatches.Append(relative).Append(strict ? " (strict)" : "").Append(": ours: ").Append(ours)
                        .Append(" | V8: ").AppendLine(theirs);
                }
            }
        }
        string summary = $"test262 negative messages: {matched}/{total} match V8";
        File.WriteAllText(Path.Combine(ArtifactsDir(), "test262-messages.txt"), summary + "\n" + mismatches);
        output.WriteLine(summary);
    }
}
