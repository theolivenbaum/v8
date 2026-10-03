// Port of the SourcePosition encoding of src/codegen/source-position.h.
// (The inlining-stack and printing helpers that need Code/Script objects are
// not ported; there is no optimized machine code in V8Sharp.)
namespace V8Sharp.Codegen;

/// <summary>
/// SourcePosition stores
/// - is_external (1 bit true/false)
/// - if is_external is true: external_line (20 bits), external_file_id (10 bits)
/// - if is_external is false: script_offset (30 bit non-negative int or kNoSourcePosition)
/// - in both cases, an inlining_id (16 bit non-negative int or kNotInlined).
/// </summary>
public readonly struct SourcePosition : IEquatable<SourcePosition>
{
    public const int kNoSourcePosition = -1;
    public const int kNotInlined = -1;

    // using IsExternalField = base::BitField64<bool, 0, 1>;
    const int kIsExternalShift = 0;
    // ExternalLineField = BitField64<int, 1, 20>; ExternalFileIdField = BitField64<int, 21, 10>.
    const int kExternalLineShift = 1, kExternalLineBits = 20;
    const int kExternalFileIdShift = 21, kExternalFileIdBits = 10;
    // ScriptOffsetField = BitField64<int, 1, 30>.
    const int kScriptOffsetShift = 1, kScriptOffsetBits = 30;
    // InliningIdField = BitField64<int, 31, 16>.
    const int kInliningIdShift = 31, kInliningIdBits = 16;

    readonly ulong _value;

    SourcePosition(ulong value) => _value = value;

    public SourcePosition(int scriptOffset = kNoSourcePosition, int inliningId = kNotInlined)
    {
        ulong value = 0;
        value = Update(value, kIsExternalShift, 1, 0);
        Debug.Assert(scriptOffset >= kNoSourcePosition);
        value = Update(value, kScriptOffsetShift, kScriptOffsetBits, (ulong)(scriptOffset + 1));
        Debug.Assert(inliningId >= kNotInlined);
        value = Update(value, kInliningIdShift, kInliningIdBits, (ulong)(inliningId + 1));
        _value = value;
    }

    static ulong Mask(int shift, int bits) => ((1UL << bits) - 1) << shift;

    static ulong Update(ulong value, int shift, int bits, ulong field)
    {
        ulong mask = Mask(shift, bits);
        return (value & ~mask) | ((field << shift) & mask);
    }

    int Decode(int shift, int bits) => (int)((_value & Mask(shift, bits)) >> shift);

    /// <summary>External SourcePositions refer to a file id and a line (.cc/.tq files).</summary>
    public static SourcePosition External(int line, int fileId)
    {
        ulong value = 0;
        value = Update(value, kIsExternalShift, 1, 1);
        value = Update(value, kExternalLineShift, kExternalLineBits, (ulong)line);
        value = Update(value, kExternalFileIdShift, kExternalFileIdBits, (ulong)fileId);
        value = Update(value, kInliningIdShift, kInliningIdBits, (ulong)(kNotInlined + 1));
        return new SourcePosition(value);
    }

    public static SourcePosition Unknown() => new(kNoSourcePosition, kNotInlined);
    public bool IsKnown() => Raw() != Unknown().Raw();

    public bool IsInlined() => !IsExternal() && InliningId() != kNotInlined;

    public bool IsExternal() => (_value & 1) != 0;
    public bool IsJavaScript() => !IsExternal();

    public int ExternalLine()
    {
        Debug.Assert(IsExternal());
        return Decode(kExternalLineShift, kExternalLineBits);
    }

    public int ExternalFileId()
    {
        Debug.Assert(IsExternal());
        return Decode(kExternalFileIdShift, kExternalFileIdBits);
    }

    public int ScriptOffset()
    {
        Debug.Assert(IsJavaScript());
        return Decode(kScriptOffsetShift, kScriptOffsetBits) - 1;
    }

    public int InliningId() => Decode(kInliningIdShift, kInliningIdBits) - 1;

    public static int MaxInliningId() => (1 << kInliningIdBits) - 1;

    public long Raw() => (long)_value;

    public static SourcePosition FromRaw(long raw)
    {
        Debug.Assert(raw >= 0);
        return new SourcePosition((ulong)raw);
    }

    public bool Equals(SourcePosition other) => _value == other._value;
    public override bool Equals(object? obj) => obj is SourcePosition p && Equals(p);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(SourcePosition a, SourcePosition b) => a._value == b._value;
    public static bool operator !=(SourcePosition a, SourcePosition b) => a._value != b._value;
}
