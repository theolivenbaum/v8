// Port of src/codegen/source-position-table.h/.cc.
//
// We'll use a simple encoding scheme to record the source positions.
// Conceptually, each position consists of:
// - code_offset: An integer index into the BytecodeArray or code.
// - source_position: An integer index into the source string.
// - position type: Each position is either a statement or an expression.
//
// The basic idea for the encoding is to use a variable-length integer coding,
// where each byte contains 7 bits of payload data, and 1 'more' bit that
// determines whether additional bytes follow. Additionally:
// - we record the difference from the previous position,
// - we just stuff one bit for the type into the code offset,
// - we write least-significant bits first,
// - we use zig-zag encoding to encode both positive and negative numbers.
//
// The table is a plain byte[] (V8's TrustedByteArray); an empty table is an
// empty array.
using System.Runtime.CompilerServices;

namespace V8Sharp.Codegen;

public struct PositionTableEntry : IEquatable<PositionTableEntry>
{
    /// <summary>kFunctionEntryBytecodeOffset.</summary>
    public const int kFunctionEntryBytecodeOffset = -1;

    public long source_position;
    public int code_offset;
    public bool is_statement;
    public bool is_breakable;

    /// <summary>V8's default PositionTableEntry(): offset kFunctionEntryBytecodeOffset, breakable.</summary>
    public static PositionTableEntry Initial => new(kFunctionEntryBytecodeOffset, 0, false, true);

    public PositionTableEntry(int offset, long source, bool statement, bool breakable = true)
    {
        source_position = source;
        code_offset = offset;
        is_statement = statement;
        is_breakable = breakable;
    }

    public readonly bool Equals(PositionTableEntry other) =>
        source_position == other.source_position && code_offset == other.code_offset &&
        is_statement == other.is_statement && is_breakable == other.is_breakable;

    public override readonly bool Equals(object? obj) => obj is PositionTableEntry e && Equals(e);
    public override readonly int GetHashCode() => HashCode.Combine(source_position, code_offset, is_statement, is_breakable);
    public static bool operator ==(PositionTableEntry a, PositionTableEntry b) => a.Equals(b);
    public static bool operator !=(PositionTableEntry a, PositionTableEntry b) => !a.Equals(b);
}

public sealed class SourcePositionTableBuilder
{
    public enum RecordingMode
    {
        /// <summary>Source positions are never to be generated (an empty table).</summary>
        OMIT_SOURCE_POSITIONS,
        /// <summary>Source positions are not currently required, but may be generated later.</summary>
        LAZY_SOURCE_POSITIONS,
        /// <summary>Source positions should be immediately generated.</summary>
        RECORD_SOURCE_POSITIONS,
    }

    RecordingMode _mode;
    readonly List<byte> _bytes = [];
    PositionTableEntry _previous = PositionTableEntry.Initial; // Previously written entry, to compute delta.

    public SourcePositionTableBuilder(RecordingMode mode = RecordingMode.RECORD_SOURCE_POSITIONS) => _mode = mode;

    public void AddPosition(int codeOffset, SourcePosition sourcePosition, bool isStatement, bool isBreakable = true)
    {
        if (Omit()) return;
        Debug.Assert(sourcePosition.IsKnown());
        AddEntry(new PositionTableEntry(codeOffset, sourcePosition.Raw(), isStatement, isBreakable));
    }

    public bool Omit() => _mode != RecordingMode.RECORD_SOURCE_POSITIONS;
    public bool Lazy() => _mode == RecordingMode.LAZY_SOURCE_POSITIONS;

    void AddEntry(in PositionTableEntry entry)
    {
        PositionTableEntry tmp = entry;
        SubtractFromEntry(ref tmp, _previous);
        bool is_absolute_source = SourcePosition.FromRaw(entry.source_position).IsExternal();
        if (is_absolute_source) tmp.source_position = entry.source_position;
        EncodeEntry(_bytes, tmp, is_absolute_source);
        _previous = entry;
    }

    /// <summary>The encoded table (V8's ToSourcePositionTable / ToSourcePositionTableVector).</summary>
    public byte[] ToSourcePositionTable()
    {
        if (_bytes.Count == 0) return [];
        Debug.Assert(!Omit());
        return [.. _bytes];
    }

    // Helper: Add the offsets from 'other' to 'value'. Also set is_statement.
    internal static void AddAndSetEntry(ref PositionTableEntry value, in PositionTableEntry other)
    {
        value.code_offset += other.code_offset;
        Debug.Assert(value.code_offset == PositionTableEntry.kFunctionEntryBytecodeOffset || value.code_offset >= 0);
        value.source_position += other.source_position;
        Debug.Assert(value.source_position >= 0);
        value.is_statement = other.is_statement;
        value.is_breakable = other.is_breakable;
    }

    // Helper: Subtract the offsets from 'other' from 'value'.
    static void SubtractFromEntry(ref PositionTableEntry value, in PositionTableEntry other)
    {
        value.code_offset -= other.code_offset;
        value.source_position -= other.source_position;
    }

    // Each byte is encoded as MoreBit | ValueBits.
    const int kValueBitsSize = 7;
    const uint kValueBitsMask = 0x7F;
    const byte kMoreBit = 0x80;

    static void EncodeUInt(List<byte> bytes, ulong value)
    {
        bool more;
        do
        {
            more = value > kValueBitsMask;
            byte current = (byte)((more ? kMoreBit : 0) | (byte)(value & kValueBitsMask));
            bytes.Add(current);
            value >>= kValueBitsSize;
        } while (more);
    }

    // Helper: Encode an integer (zig-zag).
    static void EncodeInt(List<byte> bytes, long value)
    {
        ulong encoded = ((ulong)value << 1) ^ (ulong)(value >> 63);
        EncodeUInt(bytes, encoded);
    }

    static void EncodeInt32AsUInt(List<byte> bytes, uint value) => EncodeUInt(bytes, value);

    // Encode a PositionTableEntry.
    static void EncodeEntry(List<byte> bytes, in PositionTableEntry entry, bool isAbsoluteSource)
    {
        // We only accept ascending code offsets.
        Debug.Assert(entry.code_offset >= 0);
        // The least significant bit is used to encode is_statement.
        // The second least significant bit is used to encode is_breakable.
        // The third least significant bit is used to encode is_absolute_source.
        uint encoded_entry = ((uint)entry.code_offset << 3) | (isAbsoluteSource ? 4u : 0u) |
                             (entry.is_breakable ? 2u : 0u) | (entry.is_statement ? 1u : 0u);
        EncodeInt32AsUInt(bytes, encoded_entry);
        EncodeInt(bytes, entry.source_position);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong DecodeUInt(ReadOnlySpan<byte> bytes, ref int index)
    {
        byte current;
        int shift = 0;
        ulong decoded = 0;
        bool more;
        do
        {
            current = bytes[index++];
            decoded |= (ulong)(current & kValueBitsMask) << shift;
            more = (current & kMoreBit) != 0;
            shift += kValueBitsSize;
        } while (more);
        return decoded;
    }

    // Helper: Decode an integer.
    internal static long DecodeInt(ReadOnlySpan<byte> bytes, ref int index)
    {
        ulong zigzag_value = DecodeUInt(bytes, ref index);
        return (long)((zigzag_value >> 1) ^ (0UL - (zigzag_value & 1)));
    }

    internal static void DecodeEntry(ReadOnlySpan<byte> bytes, ref int index, out PositionTableEntry entry,
                                     out bool isAbsoluteSource)
    {
        // The least significant bit is used to encode is_statement.
        // The second least significant bit is used to encode is_breakable.
        // The third least significant bit is used to encode is_absolute_source.
        // V8 decodes into an int (DecodeUInt<int>), so the value wraps like an int.
        int tmp = (int)DecodeUInt(bytes, ref index);
        entry = default;
        entry.code_offset = tmp >> 3;
        isAbsoluteSource = (tmp & 0b100) != 0;
        entry.is_breakable = (tmp & 0b010) != 0;
        entry.is_statement = (tmp & 1) != 0;
        entry.source_position = DecodeInt(bytes, ref index);
    }
}

public struct SourcePositionTableIterator
{
    /// <summary>Filter that applies when advancing the iterator. If the filter
    /// isn't satisfied, we advance the iterator again.</summary>
    public enum IterationFilter { kJavaScriptOnly = 0, kExternalOnly = 1, kAll = 2 }

    /// <summary>Filter that applies only to the first entry of the source position table.</summary>
    public enum FunctionEntryFilter { kSkipFunctionEntry = 0, kDontSkipFunctionEntry = 1 }

    /// <summary>Used for saving/restoring the iterator.</summary>
    public readonly record struct IndexAndPositionState(
        int Index, PositionTableEntry Position, IterationFilter IterationFilter,
        FunctionEntryFilter FunctionEntryFilter);

    const int kDone = -1;

    readonly byte[] _rawTable;
    int _index;
    PositionTableEntry _current;
    IterationFilter _iterationFilter;
    FunctionEntryFilter _functionEntryFilter;

    public SourcePositionTableIterator(byte[] byteArray,
                                       IterationFilter iterationFilter = IterationFilter.kJavaScriptOnly,
                                       FunctionEntryFilter functionEntryFilter = FunctionEntryFilter.kSkipFunctionEntry)
    {
        _rawTable = byteArray;
        _index = 0;
        _current = PositionTableEntry.Initial;
        _iterationFilter = iterationFilter;
        _functionEntryFilter = functionEntryFilter;
        Initialize();
    }

    // Initializes the source position iterator with the first valid bytecode.
    // Also sets the FunctionEntry SourcePosition if it exists.
    void Initialize()
    {
        Advance();
        if (_functionEntryFilter == FunctionEntryFilter.kSkipFunctionEntry &&
            _current.code_offset == PositionTableEntry.kFunctionEntryBytecodeOffset && !Done())
        {
            Advance();
        }
    }

    public void Advance()
    {
        ReadOnlySpan<byte> bytes = _rawTable;
        Debug.Assert(!Done());
        Debug.Assert(_index >= 0 && _index <= bytes.Length);
        bool filter_satisfied = false;
        while (!Done() && !filter_satisfied)
        {
            if (_index >= bytes.Length)
            {
                _index = kDone;
            }
            else
            {
                SourcePositionTableBuilder.DecodeEntry(bytes, ref _index, out PositionTableEntry tmp,
                                                       out bool is_absolute_source);
                if (is_absolute_source)
                {
                    _current.source_position = tmp.source_position;
                    _current.code_offset += tmp.code_offset;
                    _current.is_statement = tmp.is_statement;
                    _current.is_breakable = tmp.is_breakable;
                }
                else
                {
                    SourcePositionTableBuilder.AddAndSetEntry(ref _current, tmp);
                }
                SourcePosition p = SourcePosition();
                filter_satisfied =
                    _iterationFilter == IterationFilter.kAll ||
                    (_iterationFilter == IterationFilter.kJavaScriptOnly && p.IsJavaScript()) ||
                    (_iterationFilter == IterationFilter.kExternalOnly && p.IsExternal());
            }
        }
    }

    public readonly int CodeOffset()
    {
        Debug.Assert(!Done());
        return _current.code_offset;
    }

    public readonly SourcePosition SourcePosition() => Codegen.SourcePosition.FromRaw(_current.source_position);

    public readonly bool IsStatement()
    {
        Debug.Assert(!Done());
        return _current.is_statement;
    }

    public readonly bool IsBreakable()
    {
        Debug.Assert(!Done());
        return _current.is_breakable;
    }

    public readonly bool Done() => _index == kDone;

    public readonly IndexAndPositionState GetState() => new(_index, _current, _iterationFilter, _functionEntryFilter);

    public void RestoreState(IndexAndPositionState savedState)
    {
        _index = savedState.Index;
        _current = savedState.Position;
        _iterationFilter = savedState.IterationFilter;
        _functionEntryFilter = savedState.FunctionEntryFilter;
    }
}
