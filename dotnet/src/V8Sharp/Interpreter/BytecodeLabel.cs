// Port of src/interpreter/bytecode-label.h/.cc and bytecode-jump-table.h.
//
// Labels are classes (V8 passes BytecodeLabel* and BytecodeLoopHeader*
// around and binds them in place).
namespace V8Sharp.Interpreter;

/// <summary>
/// A label representing a loop header in a bytecode array. It is bound before
/// the jump is seen, so its position is always known by the time the jump is
/// reached.
/// </summary>
public sealed class BytecodeLoopHeader
{
    const int kInvalidOffset = -1;

    int _offset = kInvalidOffset;

    public int Offset
    {
        get
        {
            Debug.Assert(_offset != kInvalidOffset);
            return _offset;
        }
    }

    internal void BindTo(int offset)
    {
        Debug.Assert(offset != kInvalidOffset);
        Debug.Assert(_offset == kInvalidOffset);
        _offset = offset;
    }
}

/// <summary>
/// A label representing a forward branch target in a bytecode array. When a
/// label is bound, it represents a known position in the bytecode array. A
/// label can only have at most one referrer jump.
/// </summary>
public sealed class BytecodeLabel
{
    const int kInvalidOffset = -1;

    // Set when the label is bound (i.e. the start of the target basic block).
    bool _bound;
    // Set when the jump referrer is set (i.e. the location of the jump).
    int _jumpOffset = kInvalidOffset;

    public bool IsBound => _bound;

    public int JumpOffset
    {
        get
        {
            Debug.Assert(_jumpOffset != kInvalidOffset);
            return _jumpOffset;
        }
    }

    public bool HasReferrerJump => _jumpOffset != kInvalidOffset;

    internal void Bind()
    {
        Debug.Assert(!_bound);
        _bound = true;
    }

    internal void SetReferrer(int offset)
    {
        Debug.Assert(!_bound);
        Debug.Assert(offset != kInvalidOffset);
        Debug.Assert(_jumpOffset == kInvalidOffset);
        _jumpOffset = offset;
    }
}

/// <summary>Class representing a branch target of multiple jumps.</summary>
public sealed class BytecodeLabels
{
    readonly List<BytecodeLabel> _labels = [];
    bool _isBound;

    public BytecodeLabel New()
    {
        Debug.Assert(!IsBound);
        var label = new BytecodeLabel();
        _labels.Add(label);
        return label;
    }

    public void Bind(BytecodeArrayBuilder builder)
    {
        Debug.Assert(!_isBound);
        _isBound = true;
        foreach (BytecodeLabel label in _labels) builder.Bind(label);
    }

    public bool IsBound => _isBound;

    public bool Empty => _labels.Count == 0;
}

/// <summary>
/// A jump table for a set of targets in a bytecode array. When an entry in the
/// table is bound, it represents a known position in the bytecode array. If no
/// entries match, the switch falls through.
/// </summary>
public sealed class BytecodeJumpTable(int constantPoolIndex, int size, int caseValueBase)
{
    const int kInvalidOffset = -1;

    // V8 keeps this bit vector only in debug builds, for DCHECKs.
    readonly bool[] _bound = new bool[size];
    int _switchBytecodeOffset = kInvalidOffset;
    OperandScale _switchBytecodeOperandScale = OperandScale.Single;
    int _size = size;

    public int ConstantPoolIndex => constantPoolIndex;
    public int SwitchBytecodeOffset => _switchBytecodeOffset;
    public OperandScale SwitchBytecodeOperandScale => _switchBytecodeOperandScale;
    public int CaseValueBase => caseValueBase;
    public int Size => _size;

    public bool IsBound(int caseValue)
    {
        Debug.Assert(caseValue >= caseValueBase && caseValue < caseValueBase + Size);
        return _bound[caseValue - caseValueBase];
    }

    public int ConstantPoolEntryFor(int caseValue)
    {
        Debug.Assert(caseValue >= caseValueBase);
        return constantPoolIndex + caseValue - caseValueBase;
    }

    public void SetSize(int newSize) => _size = newSize;

    internal void MarkBound(int caseValue)
    {
        Debug.Assert(caseValue >= caseValueBase && caseValue < caseValueBase + Size);
        _bound[caseValue - caseValueBase] = true;
    }

    internal void SetSwitchBytecodeOffset(int offset, OperandScale scale)
    {
        Debug.Assert(_switchBytecodeOffset == kInvalidOffset);
        _switchBytecodeOffset = offset;
        _switchBytecodeOperandScale = scale;
    }
}
