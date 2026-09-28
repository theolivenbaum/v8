// Port of test/unittests/codegen/source-position-table-unittest.cc.
//
// V8's tests rely on the ENABLE_SLOW_DCHECKS round trip inside
// ToSourcePositionTable (CheckTableEquals); the port does that check here.
using V8Sharp.Codegen;

namespace V8Sharp.Tests.Codegen;

public class SourcePositionTableUnitTest
{
    readonly SourcePositionTableBuilder _builder = new();
    readonly List<PositionTableEntry> _rawEntries = [];

    static SourcePosition toPos(int offset) => new(offset, offset % 10 - 1);

    void AddPosition(int codeOffset, SourcePosition position, bool isStatement)
    {
        _builder.AddPosition(codeOffset, position, isStatement);
        _rawEntries.Add(new PositionTableEntry(codeOffset, position.Raw(), isStatement));
    }

    void CheckTable()
    {
        byte[] table = _builder.ToSourcePositionTable();
        Assert.NotEmpty(table);
        // Brute force testing: Record all positions and decode
        // the entire table to verify they are identical.
        var encoded = new SourcePositionTableIterator(table, SourcePositionTableIterator.IterationFilter.kAll,
                                                      SourcePositionTableIterator.FunctionEntryFilter.kDontSkipFunctionEntry);
        int raw = 0;
        for (; !encoded.Done(); encoded.Advance(), raw++)
        {
            Assert.True(raw < _rawEntries.Count);
            Assert.Equal(_rawEntries[raw].code_offset, encoded.CodeOffset());
            Assert.Equal(_rawEntries[raw].source_position, encoded.SourcePosition().Raw());
            Assert.Equal(_rawEntries[raw].is_statement, encoded.IsStatement());
        }
        Assert.Equal(_rawEntries.Count, raw);
    }

    static readonly int[] offsets =
    [
        0, 1, 2, 3, 4, 30, 31, 32, 33, 62, 63, 64, 65, 126, 127, 128, 129, 250, 1000, 9999, 12000, 31415926,
    ];

    [Fact]
    public void SourcePositionTableTest_EncodeStatement()
    {
        foreach (int offset in offsets) AddPosition(offset, toPos(offset), true);
        CheckTable();
    }

    [Fact]
    public void SourcePositionTableTest_EncodeStatementDuplicates()
    {
        foreach (int offset in offsets)
        {
            AddPosition(offset, toPos(offset), true);
            AddPosition(offset, toPos(offset + 1), true);
        }
        CheckTable();
    }

    [Fact]
    public void SourcePositionTableTest_EncodeExpression()
    {
        foreach (int offset in offsets) AddPosition(offset, toPos(offset), false);
        CheckTable();
    }

    [Fact]
    public void SourcePositionTableTest_EncodeAscendingPositive()
    {
        int code_offset = 0;
        int source_position = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            code_offset += offsets[i];
            source_position += offsets[i];
            AddPosition(code_offset, toPos(source_position), i % 2 != 0);
        }
        CheckTable();
    }

    [Fact]
    public void SourcePositionTableTest_EncodeAscendingNegative()
    {
        int code_offset = 0;
        // Start with a big source position, then decrement it.
        int source_position = 1 << 26;
        for (int i = 0; i < offsets.Length; i++)
        {
            code_offset += offsets[i];
            source_position -= offsets[i];
            AddPosition(code_offset, toPos(source_position), i % 2 != 0);
        }
        CheckTable();
    }

    // Not in V8's test: external positions are encoded absolutely.
    [Fact]
    public void SourcePositionTableTest_ExternalPositionsRoundTrip()
    {
        AddPosition(0, SourcePosition.External(10, 3), true);
        AddPosition(4, toPos(100), false);
        AddPosition(9, SourcePosition.External(2, 1), false);
        CheckTable();

        var js = new SourcePositionTableIterator(_builder.ToSourcePositionTable());
        Assert.False(js.Done());
        Assert.Equal(4, js.CodeOffset());
        Assert.Equal(100, js.SourcePosition().ScriptOffset());
        js.Advance();
        Assert.True(js.Done());
    }
}
