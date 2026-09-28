// Port of src/codegen/handler-table.h/.cc: the range-based encoding used by
// unoptimized code. The table is a byte[] (V8's TrustedByteArray) of int32
// entries in V8's layout:
//   [ range-start , range-end , handler-offset , handler-data ]
// The return-address encoding (optimized machine code) is not ported: V8Sharp
// has no machine code; the IL tiers use .NET exception handling.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace V8Sharp.Codegen;

public readonly struct HandlerTable
{
    /// <summary>
    /// Conservative prediction whether a given handler will locally catch an
    /// exception or cause a re-throw to outside the code boundary. Since this is
    /// undecidable it is merely an approximation (e.g. useful for debugger).
    /// </summary>
    public enum CatchPrediction
    {
        UNCAUGHT,             // The handler will (likely) rethrow the exception.
        CAUGHT,               // The exception will be caught by the handler.
        PROMISE,              // The exception will be caught and cause a promise rejection.
        ASYNC_AWAIT,          // The exception will be caught and cause a promise rejection
                              // in the desugaring of an async function, so special
                              // async/await handling in the debugger can take place.
        UNCAUGHT_ASYNC_AWAIT, // The exception will be caught and cause a promise
                              // rejection in the desugaring of an async REPL script.
    }

    public const int kNoHandlerFound = -1;

    // Layout description for handler table based on ranges.
    const int kRangeStartIndex = 0;
    const int kRangeEndIndex = 1;
    const int kRangeHandlerIndex = 2;
    const int kRangeDataIndex = 3;
    const int kRangeEntrySize = 4;

    // Encoding of the {handler} field:
    //   HandlerPredictionField = BitField<CatchPrediction, 0, 3>
    //   HandlerWasUsedField = Next<bool, 1>
    //   HandlerOffsetField = Next<int, 28>
    const int kHandlerPredictionMask = 0x7;
    const int kHandlerWasUsedShift = 3;
    const int kHandlerOffsetShift = 4;
    const int kHandlerOffsetBits = 28;

    public const int kLazyDeopt = (1 << kHandlerOffsetBits) - 1;

    readonly byte[] _data;
    readonly uint _numberOfEntries;

    public HandlerTable(byte[] byteArray)
    {
        _data = byteArray;
        _numberOfEntries = (uint)(byteArray.Length / kRangeEntrySize / sizeof(int));
        Debug.Assert(byteArray.Length % (kRangeEntrySize * sizeof(int)) == 0);
    }

    /// <summary>Returns the required length of the underlying byte array.</summary>
    public static int LengthForRange(int entries) => entries * kRangeEntrySize * sizeof(int);

    int Get(uint index, int field)
    {
        Debug.Assert(index < NumberOfRangeEntries());
        int offset = (int)(index * kRangeEntrySize + field) * sizeof(int);
        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset));
    }

    void Set(uint index, int field, int value)
    {
        int offset = (int)(index * kRangeEntrySize + field) * sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(offset), value);
    }

    public int GetRangeStart(uint index) => Get(index, kRangeStartIndex);
    public int GetRangeEnd(uint index) => Get(index, kRangeEndIndex);
    int GetRangeHandlerBitfield(uint index) => Get(index, kRangeHandlerIndex);
    public int GetRangeHandler(uint index) => (int)((uint)GetRangeHandlerBitfield(index) >> kHandlerOffsetShift);
    public int GetRangeData(uint index) => Get(index, kRangeDataIndex);

    public CatchPrediction GetRangePrediction(uint index) =>
        (CatchPrediction)(GetRangeHandlerBitfield(index) & kHandlerPredictionMask);

    public bool HandlerWasUsed(uint index) => ((GetRangeHandlerBitfield(index) >> kHandlerWasUsedShift) & 1) != 0;

    public void MarkHandlerUsed(uint index)
    {
        Debug.Assert(index < NumberOfRangeEntries());
        Set(index, kRangeHandlerIndex, GetRangeHandlerBitfield(index) | (1 << kHandlerWasUsedShift));
    }

    public void SetRangeStart(uint index, int value) => Set(index, kRangeStartIndex, value);
    public void SetRangeEnd(uint index, int value) => Set(index, kRangeEndIndex, value);

    public void SetRangeHandler(uint index, int handlerOffset, CatchPrediction prediction)
    {
        if (handlerOffset < 0 || handlerOffset > kLazyDeopt)
            throw new InvalidOperationException("Check failed: HandlerOffsetField::is_valid(handler_offset)");
        int value = (handlerOffset << kHandlerOffsetShift) | (0 << kHandlerWasUsedShift) | (int)prediction;
        Set(index, kRangeHandlerIndex, value);
    }

    public void SetRangeData(uint index, int value) => Set(index, kRangeDataIndex, value);

    public uint NumberOfRangeEntries() => _numberOfEntries;

    /// <summary>
    /// Lookup handler in a table based on ranges. The {pc_offset} is an offset to
    /// the start of the potentially throwing instruction (using return addresses
    /// for this value would be invalid).
    /// </summary>
    public int LookupHandlerIndexForRange(int pcOffset)
    {
        int innermost_handler = kNoHandlerFound;
        for (uint i = 0; i < NumberOfRangeEntries(); ++i)
        {
            int start_offset = GetRangeStart(i);
            int end_offset = GetRangeEnd(i);
            if (end_offset <= pcOffset) continue;
            if (start_offset > pcOffset) break;
            innermost_handler = (int)i;
        }
        return innermost_handler;
    }

    /// <summary>HandlerTableRangePrint (ENABLE_DISASSEMBLER).</summary>
    public string HandlerTableRangePrint()
    {
        var os = new StringBuilder("   from   to       hdlr (prediction,   data)\n");
        for (uint i = 0; i < NumberOfRangeEntries(); ++i)
        {
            os.Append("  (").Append(Pad(GetRangeStart(i))).Append(',').Append(Pad(GetRangeEnd(i)))
              .Append(")  ->  ").Append(Pad(GetRangeHandler(i)))
              .Append(" (prediction=").Append((int)GetRangePrediction(i))
              .Append(", data=").Append(GetRangeData(i).ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }
        return os.ToString();

        static string Pad(int v) => v.ToString(CultureInfo.InvariantCulture).PadLeft(4);
    }
}
