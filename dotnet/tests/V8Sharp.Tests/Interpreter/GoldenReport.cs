// Writes the per-file pass rate of the golden bytecode files to
// dotnet/artifacts/golden-report.txt (explicit: run with
// --filter "FullyQualifiedName~GoldenReport" -- xUnit.Explicit=only).
using System.Globalization;
using System.Text;

namespace V8Sharp.Tests.Interpreter;

public class GoldenReport
{
    [Fact(Explicit = true)]
    public void WriteGoldenReport()
    {
        var report = new StringBuilder();
        var details = new StringBuilder();
        int files_passed = 0, files = 0, snippets_passed = 0, snippets = 0;
        foreach (string file in BytecodeExpectationsParser.CollectGoldenFiles(BytecodeExpectationsParser.GoldenFileDirectory()))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            (int passed, int total, List<string> failures) = BytecodeGeneratorUnitTest.RunGoldenFile(name);
            files++;
            if (passed == total) files_passed++;
            snippets += total;
            snippets_passed += passed;
            report.Append(CultureInfo.InvariantCulture, $"{name}: {passed}/{total}\n");
            if (failures.Count > 0)
            {
                details.Append("== ").Append(name).Append('\n');
                foreach (string failure in failures) details.Append(failure).Append('\n');
            }
        }
        report.Insert(0, string.Create(CultureInfo.InvariantCulture,
                                       $"files {files_passed}/{files}, snippets {snippets_passed}/{snippets}\n"));
        string dir = ArtifactsDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "golden-report.txt"), report.ToString());
        File.WriteAllText(Path.Combine(dir, "golden-failures.txt"), details.ToString());
    }

    static string ArtifactsDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "V8Sharp.slnx"))) return Path.Combine(dir, "artifacts");
            dir = Path.GetDirectoryName(dir);
        }
        return "artifacts";
    }
}
