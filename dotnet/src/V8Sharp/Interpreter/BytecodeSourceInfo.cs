// Port of src/interpreter/bytecode-source-info.h/.cc.
using System.Globalization;

namespace V8Sharp.Interpreter;

/// <summary>Source code position information.</summary>
public struct BytecodeSourceInfo : IEquatable<BytecodeSourceInfo>
{
    public const int kUninitializedPosition = -1;

    enum PositionType : byte { None, Expression, Statement }

    PositionType _positionType;
    // Stored negated so that default(BytecodeSourceInfo) is V8's BytecodeSourceInfo():
    // no position, uninitialized position (-1 via _sourcePositionPlusOne), breakable.
    bool _isNotBreakable;
    int _sourcePositionPlusOne;

    public BytecodeSourceInfo(int source_position, bool is_statement, bool is_breakable = true)
    {
        Debug.Assert(source_position >= 0);
        _positionType = is_statement ? PositionType.Statement : PositionType.Expression;
        _sourcePositionPlusOne = source_position + 1;
        _isNotBreakable = !is_breakable;
    }

    /// <summary>Makes instance into a statement position.</summary>
    public void MakeStatementPosition(int source_position, bool is_breakable = true)
    {
        // Statement positions can be replaced by other statement
        // positions. For example , "for (x = 0; x < 3; ++x) 7;" has a
        // statement position associated with 7 but no bytecode associated
        // with it. Then Next is emitted after the body and has
        // statement position and overrides the existing one.
        _positionType = PositionType.Statement;
        SetPosition(source_position);
        _isNotBreakable = !is_breakable;
    }

    /// <summary>Makes instance into an expression position. Instance should not
    /// be a statement position otherwise it could be lost and impair the
    /// debugging experience.</summary>
    public void MakeExpressionPosition(int source_position)
    {
        Debug.Assert(!IsStatement());
        _positionType = PositionType.Expression;
        SetPosition(source_position);
        _isNotBreakable = false;
    }

    /// <summary>Forces an instance into an expression position.</summary>
    public void ForceExpressionPosition(int source_position)
    {
        _positionType = PositionType.Expression;
        SetPosition(source_position);
    }

    void SetPosition(int source_position)
    {
        _sourcePositionPlusOne = source_position + 1;
    }

    public readonly int SourcePosition()
    {
        Debug.Assert(IsValid());
        return _sourcePositionPlusOne - 1;
    }

    public readonly bool IsStatement() => _positionType == PositionType.Statement;
    public readonly bool IsExpression() => _positionType == PositionType.Expression;
    public readonly bool IsValid() => _positionType != PositionType.None;

    public void SetInvalid()
    {
        _positionType = PositionType.None;
        SetPosition(kUninitializedPosition);
    }

    public readonly bool IsBreakable() => !_isNotBreakable;

    public readonly bool Equals(BytecodeSourceInfo other) =>
        _positionType == other._positionType && _sourcePositionPlusOne == other._sourcePositionPlusOne;

    public override readonly bool Equals(object? obj) => obj is BytecodeSourceInfo o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(_positionType, _sourcePositionPlusOne);

    public static bool operator ==(BytecodeSourceInfo a, BytecodeSourceInfo b) => a.Equals(b);
    public static bool operator !=(BytecodeSourceInfo a, BytecodeSourceInfo b) => !a.Equals(b);

    /// <summary>operator&lt;&lt;: "12 S&gt;" / "12 E&gt;", or empty when invalid.</summary>
    public override readonly string ToString()
    {
        if (!IsValid()) return "";
        char description = IsStatement() ? 'S' : 'E';
        return SourcePosition().ToString(CultureInfo.InvariantCulture) + " " + description + ">";
    }
}
