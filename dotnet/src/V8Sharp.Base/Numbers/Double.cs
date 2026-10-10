// Port of src/base/numbers/double.h.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base.Numbers;

/// <summary>Helper functions for doubles (V8's <c>base::Double</c>).</summary>
public readonly struct Double
{
    public const ulong kSignMask = 0x8000_0000_0000_0000;
    public const ulong kExponentMask = 0x7FF0_0000_0000_0000;
    public const ulong kSignificandMask = 0x000F_FFFF_FFFF_FFFF;
    public const ulong kHiddenBit = 0x0010_0000_0000_0000;
    /// <summary>Excludes the hidden bit.</summary>
    public const int kPhysicalSignificandSize = 52;
    public const int kSignificandSize = 53;

    const int kExponentBias = 0x3FF + kPhysicalSignificandSize;
    const int kDenormalExponent = -kExponentBias + 1;
    const int kMaxExponent = 0x7FF - kExponentBias;
    const ulong kInfinity = 0x7FF0_0000_0000_0000;

    readonly ulong _d64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Double(double d) => _d64 = BitConverter.DoubleToUInt64Bits(d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Double(ulong d64) => _d64 = d64;

    public Double(DiyFp diyFp) => _d64 = DiyFpToUint64(diyFp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DoubleToUint64(double d) => BitConverter.DoubleToUInt64Bits(d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Uint64ToDouble(ulong d64) => BitConverter.UInt64BitsToDouble(d64);

    /// <summary>The value must be greater or equal to +0.0 and not special.</summary>
    public DiyFp AsDiyFp()
    {
        Debug.Assert(Sign > 0);
        Debug.Assert(!IsSpecial);
        return new DiyFp(Significand, Exponent);
    }

    /// <summary>The value must be strictly greater than 0.</summary>
    public DiyFp AsNormalizedDiyFp()
    {
        Debug.Assert(Value > 0.0);
        ulong f = Significand;
        int e = Exponent;
        // The current double could be a denormal.
        while ((f & kHiddenBit) == 0)
        {
            f <<= 1;
            e--;
        }
        // Do the final shifts in one go.
        f <<= DiyFp.kSignificandSize - kSignificandSize;
        e -= DiyFp.kSignificandSize - kSignificandSize;
        return new DiyFp(f, e);
    }

    public ulong AsUint64() => _d64;

    /// <summary>Returns the next greater double. Returns +infinity on input +infinity.</summary>
    public double NextDouble()
    {
        if (_d64 == kInfinity) return new Double(kInfinity).Value;
        if (Sign < 0 && Significand == 0)
        {
            // -0.0
            return 0.0;
        }
        return Sign < 0 ? new Double(_d64 - 1).Value : new Double(_d64 + 1).Value;
    }

    public int Exponent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsDenormal) return kDenormalExponent;
            int biasedE = (int)((_d64 & kExponentMask) >> kPhysicalSignificandSize);
            return biasedE - kExponentBias;
        }
    }

    public ulong Significand
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ulong significand = _d64 & kSignificandMask;
            return !IsDenormal ? significand + kHiddenBit : significand;
        }
    }

    /// <summary>Returns true if the double is a denormal.</summary>
    public bool IsDenormal => (_d64 & kExponentMask) == 0;

    /// <summary>Denormals are not special; only Infinity and NaN are.</summary>
    public bool IsSpecial => (_d64 & kExponentMask) == kExponentMask;

    public bool IsInfinite => (_d64 & kExponentMask) == kExponentMask && (_d64 & kSignificandMask) == 0;

    public int Sign => (_d64 & kSignMask) == 0 ? 1 : -1;

    /// <summary>The value must be greater or equal to +0.0.</summary>
    public DiyFp UpperBoundary()
    {
        Debug.Assert(Sign > 0);
        return new DiyFp(Significand * 2 + 1, Exponent - 1);
    }

    /// <summary>Returns the two boundaries of this. The bigger boundary
    /// (m_plus) is normalized. The lower boundary has the same exponent as
    /// m_plus. The value must be greater than 0.</summary>
    public void NormalizedBoundaries(out DiyFp outMMinus, out DiyFp outMPlus)
    {
        Debug.Assert(Value > 0.0);
        DiyFp v = AsDiyFp();
        DiyFp mPlus = DiyFp.Normalize(new DiyFp((v.F << 1) + 1, v.E - 1));
        DiyFp mMinus;
        if ((_d64 & kSignificandMask) == 0 && v.E != kDenormalExponent)
        {
            // The boundary is closer. Think of v = 1000e10 and v- = 9999e9.
            // Then the boundary (== (v - v-)/2) is not just at a distance of 1e9 but
            // at a distance of 1e8.
            // The only exception is for the smallest normal: the largest denormal is
            // at the same distance as its successor.
            // Note: denormals have the same exponent as the smallest normals.
            mMinus = new DiyFp((v.F << 2) - 1, v.E - 2);
        }
        else
        {
            mMinus = new DiyFp((v.F << 1) - 1, v.E - 1);
        }
        mMinus.F <<= mMinus.E - mPlus.E;
        mMinus.E = mPlus.E;
        outMPlus = mPlus;
        outMMinus = mMinus;
    }

    public double Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.UInt64BitsToDouble(_d64);
    }

    /// <summary>Returns the significand size for a given order of magnitude.
    /// If v = f*2^e with 2^p-1 &lt;= f &lt;= 2^p then p+e is v's order of
    /// magnitude. This function returns the number of significant binary
    /// digits v will have once it is encoded into a double. In almost all
    /// cases this is kSignificandSize; the exception is denormals.</summary>
    public static int SignificandSizeForOrderOfMagnitude(int order)
    {
        if (order >= kDenormalExponent + kSignificandSize) return kSignificandSize;
        if (order <= kDenormalExponent) return 0;
        return order - kDenormalExponent;
    }

    static ulong DiyFpToUint64(DiyFp diyFp)
    {
        ulong significand = diyFp.F;
        int exponent = diyFp.E;
        while (significand > kHiddenBit + kSignificandMask)
        {
            significand >>= 1;
            exponent++;
        }
        if (exponent >= kMaxExponent) return kInfinity;
        if (exponent < kDenormalExponent) return 0;
        while (exponent > kDenormalExponent && (significand & kHiddenBit) == 0)
        {
            significand <<= 1;
            exponent--;
        }
        ulong biasedExponent;
        if (exponent == kDenormalExponent && (significand & kHiddenBit) == 0)
        {
            biasedExponent = 0;
        }
        else
        {
            biasedExponent = (ulong)(exponent + kExponentBias);
        }
        return (significand & kSignificandMask) | (biasedExponent << kPhysicalSignificandSize);
    }
}
