// Port of the golden-file harness of test/unittests/interpreter/bytecode-generator-unittest.cc,
// plus a round trip of every golden file through the ported parser, an
// assembler for the expectation text, and the ported printer.
//
// CompilationMatchesExpectation needs the bytecode generator.
// TODO(merge): when BytecodeGenerator lands, implement
// IBytecodeExpectationsCompiler and replace the round trip's
// GoldenBytecodeAssembler.Assemble(expectation) with compiling the snippet.
using System.Text;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeGeneratorUnitTest
{
    public sealed record GoldenCase(string Snippet, string Expectation, int Line);

    public sealed record GoldenFile(BytecodeExpectationsHeaderOptions Header, List<GoldenCase> Cases);

    public static GoldenFile LoadGoldenFile(string goldenPath)
    {
        using var file = new StreamReader(goldenPath);
        var parser = new BytecodeExpectationsParser(file);
        BytecodeExpectationsHeaderOptions header = parser.ParseHeader();
        var cases = new List<GoldenCase>();
        while (parser.ReadNextSnippet(out string snippet, out int line))
        {
            string expected = parser.ReadToNextSeparator();
            cases.Add(new GoldenCase(snippet, expected, line));
        }
        return new GoldenFile(header, cases);
    }

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in BytecodeExpectationsParser.CollectGoldenFiles(BytecodeExpectationsParser.GoldenFileDirectory()))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }
        return data;
    }

    static string GoldenPath(string name) =>
        Path.Combine(BytecodeExpectationsParser.GoldenFileDirectory(), name + ".golden");

    /// <summary>CompareTexts: line by line, each line trimmed; both must end together.</summary>
    internal static void CompareTexts(string generated, string expected, string goldenFile, int startLine)
    {
        string[] generated_lines = SplitLines(generated);
        string[] expected_lines = SplitLines(expected);
        int n = Math.Min(generated_lines.Length, expected_lines.Length);
        for (int i = 0; i < n; i++)
        {
            string g = generated_lines[i].Trim();
            string e = expected_lines[i].Trim();
            if (g != e)
            {
                Assert.Fail($"Inputs differ at {goldenFile}:{startLine + i}\n  Expected: '{e}'\n  Generated: '{g}'");
            }
        }
        if (generated_lines.Length > n)
            Assert.Fail($"Generated has extra lines after line {startLine + n} in {goldenFile}: '{generated_lines[n]}'");
        if (expected_lines.Length > n)
            Assert.Fail($"Expected has extra lines after line {startLine + n} in {goldenFile}: '{expected_lines[n]}'");
    }

    // std::getline over the text: a final newline does not start another line.
    static string[] SplitLines(string text)
    {
        if (text.EndsWith('\n')) text = text[..^1];
        return text.Split('\n');
    }

    [Fact]
    public void BytecodeGeneratorInitTest_HasGoldenFiles()
    {
        List<string> golden_files =
            BytecodeExpectationsParser.CollectGoldenFiles(BytecodeExpectationsParser.GoldenFileDirectory());
        Assert.NotEmpty(golden_files);
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void BytecodeGeneratorTest_ExpectationNonEmpty(string golden)
    {
        GoldenFile file = LoadGoldenFile(GoldenPath(golden));
        foreach (GoldenCase golden_case in file.Cases)
        {
            Assert.NotEmpty(golden_case.Expectation.Trim());
        }
    }

    /// <summary>
    /// Every expectation, assembled back into a BytecodeArray and printed with
    /// the ported BytecodeExpectationsPrinter, reproduces the golden text; and
    /// the whole file (preamble, header, snippets) regenerates line for line.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void BytecodeGeneratorTest_GoldenFileRoundTrip(string golden)
    {
        string path = GoldenPath(golden);
        GoldenFile file = LoadGoldenFile(path);
        Assert.NotEmpty(file.Cases);

        var printer = new BytecodeExpectationsPrinter();
        printer.SetOptions(file.Header);

        var regenerated = new StringBuilder(BytecodeExpectationsPrinter.kFilePreamble);
        BytecodeExpectationsPrinter.PrintHeader(regenerated, file.Header);
        foreach (GoldenCase golden_case in file.Cases)
        {
            V8Sharp.Interpreter.BytecodeArray bytecode_array = GoldenBytecodeAssembler.Assemble(golden_case.Expectation);

            // BuildActual / BuildExpected with the assembled array standing in
            // for the compiled one.
            var actual = new StringBuilder();
            printer.PrintCodeSnippet(actual, golden_case.Snippet);
            printer.PrintBytecodeArray(actual, bytecode_array);
            actual.Append('\n');

            var expected = new StringBuilder();
            printer.PrintCodeSnippet(expected, golden_case.Snippet);
            expected.Append(golden_case.Expectation);

            CompareTexts(actual.ToString(), expected.ToString(), path, golden_case.Line);

            regenerated.Append("---\n").Append(actual);
        }

        CompareTexts(regenerated.ToString(), File.ReadAllText(path), path, 1);
    }
}
