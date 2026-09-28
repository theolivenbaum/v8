// Port of src/base/numbers/diy-fp.h and diy-fp.cc.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Numbers;

/// <summary>
/// "Do It Yourself Floating Point": a floating-point number with a ulong
/// significand and an int exponent. Normalized DiyFp numbers have the most
/// significant bit of the significand set. Multiplication and subtraction do
/// not normalize their results. DiyFp are not designed to contain special
/// doubles (NaN and Infinity).
/// </summary>
public struct DiyFp(ulong f, int e)
{
    public const int kSignificandSize = 64;
    const ulong kUint64MSB = 1UL << 63;

    public ulong F = f;
    public int E = e;

    /// <summary>this = this - other. The exponents must be equal and this must
    /// be bigger than other. The result is not normalized.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Subtract(in DiyFp other)
    {
        Debug.Assert(E == other.E);
        Debug.Assert(F >= other.F);
        F -= other.F;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DiyFp Minus(in DiyFp a, in DiyFp b)
    {
        DiyFp result = a;
        result.Subtract(b);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Multiply(in DiyFp other) => this = Times(this, other);

    /// <summary>Returns a * b, rounded to the 64 most significant bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DiyFp Times(in DiyFp a, in DiyFp b)
    {
        ulong hi = Math.BigMul(a.F, b.F, out ulong lo);
        return new DiyFp(hi + (lo >> 63), a.E + b.E + 64);
    }

    public void Normalize()
    {
        Debug.Assert(F != 0);
        ulong f = F;
        int e = E;
        // This method is mainly called for normalizing boundaries. In general
        // boundaries need to be shifted by 10 bits. We thus optimize for this case.
        const ulong k10MSBits = 0x3FFUL << 54;
        while ((f & k10MSBits) == 0)
        {
            f <<= 10;
            e -= 10;
        }
        while ((f & kUint64MSB) == 0)
        {
            f <<= 1;
            e--;
        }
        F = f;
        E = e;
    }

    public static DiyFp Normalize(in DiyFp a)
    {
        DiyFp result = a;
        result.Normalize();
        return result;
    }
}
