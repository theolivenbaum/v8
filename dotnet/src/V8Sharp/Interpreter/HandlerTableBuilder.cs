// Port of src/interpreter/handler-table-builder.h/.cc.
using V8Sharp.Codegen;

namespace V8Sharp.Interpreter;

/// <summary>A helper class for constructing exception handler tables for the interpreter.</summary>
public sealed class HandlerTableBuilder
{
    struct Entry
    {
        public int OffsetStart;   // Bytecode offset starting try-region.
        public int OffsetEnd;     // Bytecode offset ending try-region.
        public int OffsetTarget;  // Bytecode offset of handler target.
        public Register Context;  // Register holding context for handler.
        public HandlerTable.CatchPrediction CatchPrediction; // Optimistic prediction for handler.
        public bool Dropped;
    }

    readonly List<Entry> _entries = [];

    /// <summary>
    /// Builds the actual handler table by copying the current values into a new
    /// byte array. Any further mutations to the builder won't be reflected.
    /// </summary>
    public byte[] ToHandlerTable()
    {
        int effective_size = 0;
        foreach (Entry entry in _entries)
        {
            if (!entry.Dropped) effective_size++;
        }

        var table_byte_array = new byte[HandlerTable.LengthForRange(effective_size)];
        var table = new HandlerTable(table_byte_array);
        uint table_index = 0;
        foreach (Entry entry in _entries)
        {
            if (entry.Dropped) continue;
            table.SetRangeStart(table_index, entry.OffsetStart);
            table.SetRangeEnd(table_index, entry.OffsetEnd);
            table.SetRangeHandler(table_index, entry.OffsetTarget, entry.CatchPrediction);
            table.SetRangeData(table_index, entry.Context.Index);
            table_index++;
        }
        return table_byte_array;
    }

    /// <summary>Creates a new handler table entry and returns a {handler_id}
    /// identifying the entry, so that it can be referenced by the setters.</summary>
    public int NewHandlerEntry()
    {
        int handler_id = _entries.Count;
        _entries.Add(new Entry
        {
            Context = Register.InvalidValue(),
            CatchPrediction = HandlerTable.CatchPrediction.UNCAUGHT,
        });
        return handler_id;
    }

    public void SetTryRegionStart(int handlerId, int offset) =>
        _entries[handlerId] = _entries[handlerId] with { OffsetStart = offset };

    public void SetTryRegionEnd(int handlerId, int offset) =>
        _entries[handlerId] = _entries[handlerId] with { OffsetEnd = offset };

    public void SetHandlerTarget(int handlerId, int offset) =>
        _entries[handlerId] = _entries[handlerId] with { OffsetTarget = offset };

    public void SetPrediction(int handlerId, HandlerTable.CatchPrediction prediction) =>
        _entries[handlerId] = _entries[handlerId] with { CatchPrediction = prediction };

    public void SetContextRegister(int handlerId, Register reg) =>
        _entries[handlerId] = _entries[handlerId] with { Context = reg };

    public void DropHandlerEntry(int handlerId) =>
        _entries[handlerId] = _entries[handlerId] with { Dropped = true };
}
