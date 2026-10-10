// Port of src/bigint/div-helpers-inl.h.

namespace V8Sharp.Base.BigInts;

public static partial class Bigint
{
    // These constants are primarily needed for Barrett division in div-barrett.cc,
    // and they're also needed by fast to-string conversion in tostring.cc.
    public static int DivideBarrettScratchSpace(int n) => n + 2;

    // Local values S and W need "n plus a few" digits; U needs 2*n "plus a few".
    // In all tested cases the "few" were either 2 or 3, so give 5 to be safe.
    // S and W are not live at the same time.
    public const int kInvertNewtonExtraSpace = 5;

    public static int InvertNewtonScratchSpace(int n) => 3 * n + 2 * kInvertNewtonExtraSpace;

    public static int InvertScratchSpace(int n) =>
        n < BigintConfig.kNewtonInversionThreshold ? 2 * n : InvertNewtonScratchSpace(n);

    public static void Copy(Span<ulong> Z, ReadOnlySpan<ulong> X)
    {
        if (SameDigits(Z, X)) return;
        int i = 0;
        for (; i < X.Length; i++) Z[i] = X[i];
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>Z := X &lt;&lt; shift, 0 &lt;= shift &lt; kDigitBits. Z and X may
    /// alias for an in-place shift.</summary>
    public static void LeftShift(Span<ulong> Z, ReadOnlySpan<ulong> X, int shift)
    {
        Debug.Assert(shift >= 0);
        Debug.Assert(shift < kDigitBits);
        Debug.Assert(Z.Length >= X.Length);
        if (shift == 0)
        {
            Copy(Z, X);
            return;
        }
        ulong carry = 0;
        int i = 0;
        for (; i < X.Length; i++)
        {
            ulong d = X[i];
            Z[i] = (d << shift) | carry;
            carry = d >> (kDigitBits - shift);
        }
        if (i < Z.Length)
        {
            Z[i++] = carry;
        }
        else
        {
            Debug.Assert(carry == 0);
        }
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    /// <summary>Z := X &gt;&gt; shift, 0 &lt;= shift &lt; kDigitBits. Z and X may
    /// alias for an in-place shift.</summary>
    public static void RightShift(Span<ulong> Z, ReadOnlySpan<ulong> X, int shift)
    {
        Debug.Assert(shift >= 0);
        Debug.Assert(shift < kDigitBits);
        X = Normalize(X);
        Debug.Assert(Z.Length >= X.Length);
        if (shift == 0)
        {
            Copy(Z, X);
            return;
        }
        int i = 0;
        if (X.Length > 0)
        {
            ulong carry = X[0] >> shift;
            int last = X.Length - 1;
            for (; i < last; i++)
            {
                ulong d = X[i + 1];
                Z[i] = (d << (kDigitBits - shift)) | carry;
                carry = d >> shift;
            }
            Z[i++] = carry;
        }
        for (; i < Z.Length; i++) Z[i] = 0;
    }

    public static void PutAt(Span<ulong> Z, ReadOnlySpan<ulong> A, int count)
    {
        int len = Math.Min(A.Length, count);
        int i = 0;
        for (; i < len; i++) Z[i] = A[i];
        for (; i < count; i++) Z[i] = 0;
    }
}

/// <summary>
/// Division algorithms typically need to left-shift their inputs into
/// "bit-normalized" form (i.e. top bit is set). The inputs are considered
/// read-only, so by default, ShiftedDigits allocate temporary storage for
/// their contents. In-place modification is opt-in for cases where callers can
/// guarantee that it is safe; callers that allow it and wish to undo it have
/// to do so manually using Reset(). If shift is not given, it is
/// auto-detected from original's leading zeros.
/// </summary>
public ref struct ShiftedDigits
{
    Span<ulong> _digits;
    readonly int _shift;
    readonly bool _inplace;

    /// <summary>The shifted digits. When the shift is in place (or zero), this
    /// is the caller's memory.</summary>
    public readonly ReadOnlySpan<ulong> Digits => _digits;
    public readonly int Shift => _shift;

    public ShiftedDigits(Span<ulong> original, int shift = -1, bool allowInplace = false)
    {
        _digits = original;
        int leadingZeros = Bigint.CountLeadingZeros(original[^1]);
        int len = original.Length;
        if (shift < 0)
        {
            shift = leadingZeros;
        }
        else if (shift > leadingZeros)
        {
            allowInplace = false;
            len++;
        }
        _shift = shift;
        if (shift == 0)
        {
            _inplace = true;
            return;
        }
        _inplace = allowInplace;
        if (!_inplace) _digits = new ulong[len];
        Bigint.LeftShift(_digits, original, _shift);
    }

    /// <summary>Read-only inputs: never shifted in place.</summary>
    public ShiftedDigits(ReadOnlySpan<ulong> original, int shift = -1)
    {
        int leadingZeros = Bigint.CountLeadingZeros(original[^1]);
        int len = original.Length;
        if (shift < 0) shift = leadingZeros;
        else if (shift > leadingZeros) len++;
        _shift = shift;
        _inplace = false;
        _digits = new ulong[len];
        Bigint.LeftShift(_digits, original, _shift);
    }

    /// <summary>For callers that have available scratch memory. When no shift
    /// is needed, Digits refers to original.</summary>
    public ShiftedDigits(ReadOnlySpan<ulong> original, Span<ulong> scratch, out ReadOnlySpan<ulong> digits)
    {
        Debug.Assert(scratch.Length >= original.Length);
        _shift = Bigint.CountLeadingZeros(original[^1]);
        _inplace = _shift == 0;
        if (_shift == 0)
        {
            _digits = [];
            digits = original;
            return;
        }
        _digits = scratch[..original.Length];
        Bigint.LeftShift(_digits, original, _shift);
        digits = _digits;
    }

    public readonly void Reset()
    {
        if (_inplace && _shift != 0) Bigint.RightShift(_digits, _digits, _shift);
    }
}
