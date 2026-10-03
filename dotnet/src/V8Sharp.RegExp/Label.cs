// Port of src/codegen/label.h (the parts irregexp's assemblers use).

namespace V8Sharp.RegExp;

/// <summary>
/// A jump target. <c>pos_</c> encodes both the binding state (via its sign)
/// and the position: &lt; 0 bound (pos() is the target), == 0 unused,
/// &gt; 0 linked (pos() is the last reference position).
/// </summary>
public class Label
{
    int _pos;

    public void Unuse() => _pos = 0;
    public bool IsBound => _pos < 0;
    public bool IsUnused => _pos == 0;
    public bool IsLinked => _pos > 0;

    /// <summary>The position of a bound or linked label.</summary>
    public int Pos
    {
        get
        {
            if (_pos < 0) return -_pos - 1;
            if (_pos > 0) return _pos - 1;
            throw new InvalidOperationException("unused label");
        }
    }

    public void BindTo(int pos) => _pos = -pos - 1;
    public void LinkTo(int pos) => _pos = pos + 1;
}
