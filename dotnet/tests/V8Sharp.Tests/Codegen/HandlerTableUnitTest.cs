// V8 has no unittest file for src/codegen/handler-table.cc; these pin the
// range encoding the interpreter reads (layout, prediction bits, nesting lookup).
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Codegen;

public class HandlerTableUnitTest
{
    [Fact]
    public void HandlerTable_RangeEncodingRoundTrips()
    {
        var builder = new HandlerTableBuilder();
        int outer = builder.NewHandlerEntry();
        int dropped = builder.NewHandlerEntry();
        int inner = builder.NewHandlerEntry();
        builder.SetTryRegionStart(outer, 2);
        builder.SetTryRegionEnd(outer, 40);
        builder.SetHandlerTarget(outer, 44);
        builder.SetPrediction(outer, HandlerTable.CatchPrediction.CAUGHT);
        builder.SetContextRegister(outer, new Register(3));
        builder.DropHandlerEntry(dropped);
        builder.SetTryRegionStart(inner, 10);
        builder.SetTryRegionEnd(inner, 20);
        builder.SetHandlerTarget(inner, 22);
        builder.SetPrediction(inner, HandlerTable.CatchPrediction.ASYNC_AWAIT);
        builder.SetContextRegister(inner, Register.CurrentContext());

        byte[] bytes = builder.ToHandlerTable();
        Assert.Equal(HandlerTable.LengthForRange(2), bytes.Length);

        var table = new HandlerTable(bytes);
        Assert.Equal(2u, table.NumberOfRangeEntries());
        Assert.Equal(2, table.GetRangeStart(0));
        Assert.Equal(40, table.GetRangeEnd(0));
        Assert.Equal(44, table.GetRangeHandler(0));
        Assert.Equal(3, table.GetRangeData(0));
        Assert.Equal(HandlerTable.CatchPrediction.CAUGHT, table.GetRangePrediction(0));
        Assert.Equal(HandlerTable.CatchPrediction.ASYNC_AWAIT, table.GetRangePrediction(1));
        Assert.Equal(Register.CurrentContext().Index, table.GetRangeData(1));
        // handler field = offset << 4 | was_used << 3 | prediction.
        Assert.Equal((44 << 4) | 1, BitConverter.ToInt32(bytes, 8));

        Assert.False(table.HandlerWasUsed(1));
        table.MarkHandlerUsed(1);
        Assert.True(table.HandlerWasUsed(1));
        Assert.Equal(22, table.GetRangeHandler(1));

        Assert.Equal(HandlerTable.kNoHandlerFound, table.LookupHandlerIndexForRange(1));
        Assert.Equal(0, table.LookupHandlerIndexForRange(5));
        Assert.Equal(1, table.LookupHandlerIndexForRange(15));
        Assert.Equal(0, table.LookupHandlerIndexForRange(30));
        Assert.Equal(HandlerTable.kNoHandlerFound, table.LookupHandlerIndexForRange(40));
    }
}
