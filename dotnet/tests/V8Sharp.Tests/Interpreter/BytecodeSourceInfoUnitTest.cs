// Port of test/unittests/interpreter/bytecode-source-info-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeSourceInfoUnitTest
{
    [Fact]
    public void BytecodeSourceInfo_Operations()
    {
        var x = new BytecodeSourceInfo(0, true);
        Assert.Equal(0, x.SourcePosition());
        Assert.True(x.IsStatement());
        Assert.True(x.IsValid());
        x.SetInvalid();
        Assert.False(x.IsStatement());
        Assert.False(x.IsValid());

        x.MakeStatementPosition(1);
        var y = new BytecodeSourceInfo(1, true);
        Assert.True(x == y);
        Assert.False(x != y);

        x.SetInvalid();
        Assert.False(x == y);
        Assert.True(x != y);

        y.MakeStatementPosition(1);
        Assert.Equal(1, y.SourcePosition());
        Assert.True(y.IsStatement());

        y.MakeStatementPosition(2);
        Assert.Equal(2, y.SourcePosition());
        Assert.True(y.IsStatement());

        y.SetInvalid();
        y.MakeExpressionPosition(3);
        Assert.Equal(3, y.SourcePosition());
        Assert.False(y.IsStatement());

        y.MakeStatementPosition(3);
        Assert.Equal(3, y.SourcePosition());
        Assert.True(y.IsStatement());
    }

    // Not in V8's test: default(BytecodeSourceInfo) must be V8's BytecodeSourceInfo().
    [Fact]
    public void BytecodeSourceInfo_DefaultIsInvalidAndBreakable()
    {
        BytecodeSourceInfo info = default;
        Assert.False(info.IsValid());
        Assert.True(info.IsBreakable());
        Assert.Equal("", info.ToString());
        Assert.Equal("7 S>", new BytecodeSourceInfo(7, true).ToString());
        Assert.Equal("7 E>", new BytecodeSourceInfo(7, false).ToString());
    }
}
