using V8Sharp.Oracle;

namespace V8Sharp.Parity.Tests;

public class OracleSmokeTests
{
    [Fact]
    public void ReferenceV8RunsWithNativesSyntax()
    {
        using var v8 = new ReferenceV8();
        string output = v8.Run("function f(a) { return a + 1; } print(f(1), %IsSmi(1), 0.1 + 0.2);");
        Assert.Equal("2 true 0.30000000000000004\n", output);
    }
}
