namespace V8Sharp.Base.Tests;

public class GayDataTest
{
    // Guards the Gay* tests against passing vacuously on an empty table.
    [Fact]
    public void TablesAreComplete()
    {
        Assert.Equal(100000, GayData.PrecomputedShortestRepresentations().Length);
        Assert.Equal(100000, GayData.PrecomputedFixedRepresentations().Length);
        Assert.Equal(100000, GayData.PrecomputedPrecisionRepresentations().Length);
    }
}
