using V8Sharp.TestRunner;

namespace V8Sharp.Conformance.Tests;

static class TestPaths
{
    /// <summary>The repository root (the V8 tree), found above the test binaries.</summary>
    public static string V8Root { get; } = Runner.FindV8Root(AppContext.BaseDirectory);

    public static string Test262Root { get; } = Runner.FindTest262Root(V8Root, null);

    /// <summary>dotnet/tests/V8Sharp.Conformance.Tests (the source directory).</summary>
    public static string ProjectDirectory { get; } = Path.Combine(V8Root, "dotnet", "tests", "V8Sharp.Conformance.Tests");
}
