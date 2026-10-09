using V8Sharp.TestRunner;

namespace V8Sharp.AsmJs.Tests;

static class TestPaths
{
    /// <summary>The repository root (the V8 tree), found above the test binaries.</summary>
    public static string V8Root { get; } = Runner.FindV8Root(AppContext.BaseDirectory);

    /// <summary>dotnet/tests/V8Sharp.AsmJs.Tests (the source directory).</summary>
    public static string ProjectDirectory { get; } = Path.Combine(V8Root, "dotnet", "tests", "V8Sharp.AsmJs.Tests");

    /// <summary>The asm.js tests of V8 14.7.173.23, laid out as a V8 root (test/mjsunit, test/message).</summary>
    public static string V8Root147 { get; } = Path.Combine(ProjectDirectory, "v8-14.7");
}
