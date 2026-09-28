// Port of src/base/ieee754.h and src/base/ieee754.cc.
//
// The following is adapted from fdlibm (http://www.netlib.org/fdlibm).
//
// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// The original source code covered by the above license above has been
// modified significantly by Google Inc.
// Copyright 2016 the V8 project authors. All rights reserved.
//
// V8 implements acosh, asinh, atanh, cosh and sinh here with fdlibm, on top
// of its own log, log1p, exp and expm1, and takes the other functions from
// llvm-libc, which are correctly rounded (see Ieee754.CorrectlyRounded.cs for
// how the port reproduces them). tanh is std::tanh, the platform's, which is
// what Math.Tanh calls too.

using System.Runtime.CompilerServices;
using static V8Sharp.Base.CorrectlyRounded;

namespace V8Sharp.Base;

public static class Ieee754
{
    // Get two 32 bit ints from a double.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EXTRACT_WORDS(out int ix0, out uint ix1, double d)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(d);
        ix0 = (int)(bits >> 32);
        ix1 = (uint)bits;
    }

    // Get the more significant 32 bit int from a double.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int GET_HIGH_WORD(double d) => (int)(BitConverter.DoubleToUInt64Bits(d) >> 32);

    // Set the more significant 32 bits of a double from an int.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double SET_HIGH_WORD(double d, int v)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(d) & 0x0000_0000_FFFF_FFFFUL;
        bits |= (ulong)(uint)v << 32;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    // Returns the arc cosine of x; that is the value whose cosine is x.
    public static double acos(double x)
    {
        if (double.IsNaN(x) || Math.Abs(x) > 1) return double.NaN;
        if (x == 1) return 0.0;
        if (x == 0) return 1.5707963267948966;
        if (TryAcos(x, out double r)) return r;
        return Evaluate(Fn.Acos, x, 0, Math.Acos(x));
    }

    /* acosh(x)
     * Method :
     *      Based on
     *              acosh(x) = log [ x + sqrt(x*x-1) ]
     *      we have
     *              acosh(x) := log(x)+ln2, if x is large; else
     *              acosh(x) := log(2x-1/(sqrt(x*x-1)+x)) if x>2; else
     *              acosh(x) := log1p(t+sqrt(2.0*t+t*t)); where t=x-1.
     *
     * Special cases:
     *      acosh(x) is NaN with signal if x<1.
     *      acosh(NaN) is NaN without signal.
     */
    public static double acosh(double x)
    {
        const double one = 1.0, ln2 = 6.93147180559945286227e-01; /* 0x3FE62E42, 0xFEFA39EF */
        double t;
        EXTRACT_WORDS(out int hx, out uint lx, x);
        if (hx < 0x3FF00000)
        { /* x < 1 */
            // V8 returns a signaling NaN; JavaScript has only one NaN.
            return double.NaN;
        }
        else if (hx >= 0x41B00000)
        { /* x > 2**28 */
            if (hx >= 0x7FF00000)
            { /* x is inf of NaN */
                return x + x;
            }
            else
            {
                return log(x) + ln2; /* acosh(huge)=log(2x) */
            }
        }
        else if (((hx - 0x3FF00000) | (int)lx) == 0)
        {
            return 0.0; /* acosh(1) = 0 */
        }
        else if (hx > 0x40000000)
        { /* 2**28 > x > 2 */
            t = x * x;
            return log(2.0 * x - one / (x + Math.Sqrt(t - one)));
        }
        else
        { /* 1<x<2 */
            t = x - one;
            return log1p(t + Math.Sqrt(2.0 * t + t * t));
        }
    }

    // Returns the arc sine of x; that is the value whose sine is x.
    public static double asin(double x)
    {
        if (double.IsNaN(x) || Math.Abs(x) > 1) return double.NaN;
        if (x == 0) return x;
        if (Math.Abs(x) < kTwoPowM27) return x;
        if (TryAsin(x, out double r)) return r;
        return Evaluate(Fn.Asin, x, 0, Math.Asin(x));
    }

    /* asinh(x)
     * Method :
     *      Based on
     *              asinh(x) = sign(x) * log [ |x| + sqrt(x*x+1) ]
     *      we have
     *      asinh(x) := x  if  1+x*x=1,
     *               := sign(x)*(log(x)+ln2)) for large |x|, else
     *               := sign(x)*log(2|x|+1/(|x|+sqrt(x*x+1))) if|x|>2, else
     *               := sign(x)*log1p(|x| + x^2/(1 + sqrt(1+x^2)))
     */
    public static double asinh(double x)
    {
        const double one = 1.00000000000000000000e+00, /* 0x3FF00000, 0x00000000 */
            ln2 = 6.93147180559945286227e-01, /* 0x3FE62E42, 0xFEFA39EF */
            huge = 1.00000000000000000000e+300;

        double t, w;
        int hx = GET_HIGH_WORD(x);
        int ix = hx & 0x7FFFFFFF;
        if (ix >= 0x7FF00000) return x + x; /* x is inf or NaN */
        if (ix < 0x3E300000)
        { /* |x|<2**-28 */
            if (huge + x > one) return x; /* return x inexact except 0 */
        }
        if (ix > 0x41B00000)
        { /* |x| > 2**28 */
            w = log(Math.Abs(x)) + ln2;
        }
        else if (ix > 0x40000000)
        { /* 2**28 > |x| > 2.0 */
            t = Math.Abs(x);
            w = log(2.0 * t + one / (Math.Sqrt(x * x + one) + t));
        }
        else
        { /* 2.0 > |x| > 2**-28 */
            t = x * x;
            w = log1p(Math.Abs(x) + t / (one + Math.Sqrt(one + t)));
        }
        if (hx > 0)
        {
            return w;
        }
        else
        {
            return -w;
        }
    }

    // Returns the principal value of the arc tangent of x; that is the value
    // whose tangent is x.
    public static double atan(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x == 0) return x;
        if (double.IsInfinity(x)) return x > 0 ? 1.5707963267948966 : -1.5707963267948966;
        if (Math.Abs(x) < kTwoPowM27) return x;
        if (TryAtan(x, out double r)) return r;
        return Evaluate(Fn.Atan, x, 0, Math.Atan(x));
    }

    // Returns the principal value of the arc tangent of y/x, using the signs of
    // the two arguments to determine the quadrant of the result.
    public static double atan2(double y, double x)
    {
        const double pi = 3.1415926535897931, pi_o_2 = 1.5707963267948966, pi_o_4 = 0.78539816339744828,
            three_pi_o_4 = 2.3561944901923448;
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        bool yNegative = double.IsNegative(y);
        if (y == 0)
        {
            // atan2(+-0, +0 or x > 0) = +-0; atan2(+-0, -0 or x < 0) = +-pi.
            if (double.IsNegative(x)) return yNegative ? -pi : pi;
            return y;
        }
        if (x == 0) return yNegative ? -pi_o_2 : pi_o_2;
        if (double.IsInfinity(x))
        {
            if (double.IsInfinity(y))
            {
                double angle = x > 0 ? pi_o_4 : three_pi_o_4;
                return yNegative ? -angle : angle;
            }
            if (x > 0) return yNegative ? -0.0 : 0.0;
            return yNegative ? -pi : pi;
        }
        if (double.IsInfinity(y)) return yNegative ? -pi_o_2 : pi_o_2;
        if (TryAtan2(y, x, out double r)) return r;
        return Evaluate(Fn.Atan2, y, x, Math.Atan2(y, x), yNegative);
    }

    // Returns the cosine of x, where x is given in radians.
    public static double cos(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        if (x == 0) return 1.0;
        if (Math.Abs(x) < kTwoPowM27) return 1.0;
        if (TryTrig(Fn.Cos, x, out double r)) return r;
        return Evaluate(Fn.Cos, x, 0, Math.Cos(x));
    }

    // Returns the sine of x, where x is given in radians.
    public static double sin(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        if (x == 0) return x;
        if (Math.Abs(x) < kTwoPowM27) return x;
        if (TryTrig(Fn.Sin, x, out double r)) return r;
        return Evaluate(Fn.Sin, x, 0, Math.Sin(x));
    }

    // Returns the base-e exponential of x.
    public static double exp(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x == 0) return 1.0;
        if (x > 710) return double.PositiveInfinity;
        if (x < -746) return 0.0;
        if (TryExp(x, out double r)) return r;
        return Evaluate(Fn.Exp, x, 0, 1.0);
    }

    /*
     * Method :
     *    1.Reduced x to positive by atanh(-x) = -atanh(x)
     *    2.For x>=0.5
     *              1              2x                          x
     *  atanh(x) = --- * log(1 + -------) = 0.5 * log1p(2 * --------)
     *              2             1 - x                      1 - x
     *
     *   For x<0.5
     *  atanh(x) = 0.5*log1p(2x+2x*x/(1-x))
     *
     * Special cases:
     *  atanh(x) is NaN if |x| > 1 with signal;
     *  atanh(NaN) is that NaN with no signal;
     *  atanh(+-1) is +-INF with signal.
     *
     */
    public static double atanh(double x)
    {
        const double one = 1.0, huge = 1e300;
        const double zero = 0.0;

        double t;
        EXTRACT_WORDS(out int hx, out uint lx, x);
        int ix = hx & 0x7FFFFFFF;
        if (((uint)ix | ((lx | (uint)-(int)lx) >> 31)) > 0x3FF00000)
        {
            /* |x|>1 */
            // V8 returns a signaling NaN; JavaScript has only one NaN.
            return double.NaN;
        }
        if (ix == 0x3FF00000)
        {
            return x > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        }
        if (ix < 0x3E300000 && (huge + x) > zero) return x; /* x<2**-28 */
        x = SET_HIGH_WORD(x, ix);
        if (ix < 0x3FE00000)
        { /* x < 0.5 */
            t = x + x;
            t = 0.5 * log1p(t + t * x / (one - x));
        }
        else
        {
            t = 0.5 * log1p((x + x) / (one - x));
        }
        if (hx >= 0)
        {
            return t;
        }
        else
        {
            return -t;
        }
    }

    // Returns the natural logarithm of x.
    public static double log(double x)
    {
        if (double.IsNaN(x) || x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (x == 1) return 0.0;
        if (double.IsPositiveInfinity(x)) return x;
        if (TryLog(Fn.Log, x, out double r)) return r;
        return Evaluate(Fn.Log, x, 0, Math.Log(x));
    }

    // Returns a value equivalent to the log (1 + x).
    public static double log1p(double x)
    {
        if (double.IsNaN(x) || x < -1) return double.NaN;
        if (x == -1) return double.NegativeInfinity;
        if (x == 0) return x;
        if (double.IsPositiveInfinity(x)) return x;
        if (TryLog1p(x, out double r)) return r;
        return Evaluate(Fn.Log1p, x, 0, Math.Abs(x) < 1e-5 ? x : Math.Log(1 + x));
    }

    // Returns the base 2 logarithm of x.
    public static double log2(double x)
    {
        if (double.IsNaN(x) || x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (x == 1) return 0.0;
        if (double.IsPositiveInfinity(x)) return x;
        if (TryLog(Fn.Log2, x, out double r)) return r;
        return Evaluate(Fn.Log2, x, 0, Math.Log2(x));
    }

    // Returns the base 10 logarithm of x.
    public static double log10(double x)
    {
        if (double.IsNaN(x) || x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (x == 1) return 0.0;
        if (double.IsPositiveInfinity(x)) return x;
        if (TryLog(Fn.Log10, x, out double r)) return r;
        return Evaluate(Fn.Log10, x, 0, Math.Log10(x));
    }

    // Returns the cube root of x.
    public static double cbrt(double x)
    {
        if (!double.IsFinite(x) || x == 0) return x + x;
        if (TryCbrt(x, out double r)) return r;
        return Cbrt(x);
    }

    // Returns exp(x)-1, the exponential of |x| minus 1.
    public static double expm1(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x == 0) return x;
        if (x > 710) return double.PositiveInfinity;
        if (x < -40) return -1.0;
        if (TryExpm1(x, out double r)) return r;
        return Evaluate(Fn.Expm1, x, 0, Math.Abs(x) < 1e-5 ? x : Math.Exp(x) - 1);
    }

    public static class Legacy
    {
        // This function should not be used for implementing Math.pow, but rather
        // V8Sharp.Base.Numbers.InternalMath.pow, which applies JavaScript's
        // special cases first.
        public static double pow(double x, double y)
        {
            // C99 Annex F special cases, as llvm-libc.
            if (y == 0) return 1.0;
            if (x == 1) return 1.0;
            if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
            bool yIsInteger = Math.Floor(y) == y;
            bool yIsOdd = yIsInteger && Math.Abs(y) < 9007199254740992.0 && Math.IEEERemainder(y, 2) != 0;
            if (double.IsInfinity(y))
            {
                double ax = Math.Abs(x);
                if (ax == 1) return 1.0;
                return (ax < 1) == (y > 0) ? 0.0 : double.PositiveInfinity;
            }
            if (x == 0)
            {
                bool negative = double.IsNegative(x) && yIsOdd;
                if (y < 0) return negative ? double.NegativeInfinity : double.PositiveInfinity;
                return negative ? -0.0 : 0.0;
            }
            if (double.IsInfinity(x))
            {
                bool negative = x < 0 && yIsOdd;
                if (y < 0) return negative ? -0.0 : 0.0;
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            }
            if (x < 0 && !yIsInteger) return double.NaN;
            bool negate = x < 0 && yIsOdd;
            double result;
            double magnitude = y * Math.Log2(Math.Abs(x));
            if (magnitude > 1100)
            {
                result = double.PositiveInfinity;
            }
            else if (magnitude < -1100)
            {
                result = 0.0;
            }
            else if (!TryExactPow(x, y, out result))
            {
                result = Evaluate(Fn.Pow, x, y, 1.0);
            }
            return negate ? -result : result;
        }
    }

    // Returns the tangent of x, where x is given in radians.
    public static double tan(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        if (x == 0) return x;
        if (Math.Abs(x) < kTwoPowM27) return x;
        if (TryTrig(Fn.Tan, x, out double r)) return r;
        return Evaluate(Fn.Tan, x, 0, Math.Tan(x));
    }

    /*
     * ES6 draft 09-27-13, section 20.2.2.12.
     * Math.cosh
     * Method :
     * mathematically cosh(x) if defined to be (exp(x)+exp(-x))/2
     *      1. Replace x by |x| (cosh(x) = cosh(-x)).
     *      2.
     *                                                      [ exp(x) - 1 ]^2
     *          0        <= x <= ln2/2  :  cosh(x) := 1 + -------------------
     *                                                         2*exp(x)
     *
     *                                                 exp(x) + 1/exp(x)
     *          ln2/2    <= x <= 22     :  cosh(x) := -------------------
     *                                                        2
     *          22       <= x <= lnovft :  cosh(x) := exp(x)/2
     *          lnovft   <= x <= ln2ovft:  cosh(x) := exp(x/2)/2 * exp(x/2)
     *          ln2ovft  <  x           :  cosh(x) := huge*huge (overflow)
     *
     * Special cases:
     *      cosh(x) is |x| if x is +INF, -INF, or NaN.
     *      only cosh(0)=1 is exact for finite x.
     */
    public static double cosh(double x)
    {
        const double KCOSH_OVERFLOW = 710.4758600739439;
        const double one = 1.0, half = 0.5;
        const double huge = 1.0e+300;

        /* High word of |x|. */
        int ix = GET_HIGH_WORD(x);
        ix &= 0x7FFFFFFF;

        // |x| in [0,0.5*log2], return 1+expm1(|x|)^2/(2*exp(|x|))
        if (ix < 0x3FD62E43)
        {
            double t = expm1(Math.Abs(x));
            double w = one + t;
            // For |x| < 2^-55, cosh(x) = 1
            if (ix < 0x3C800000) return w;
            return one + (t * t) / (w + w);
        }

        // |x| in [0.5*log2, 22], return (exp(|x|)+1/exp(|x|)/2
        if (ix < 0x40360000)
        {
            double t = exp(Math.Abs(x));
            return half * t + half / t;
        }

        // |x| in [22, log(maxdouble)], return half*exp(|x|)
        if (ix < 0x40862E42) return half * exp(Math.Abs(x));

        // |x| in [log(maxdouble), overflowthreshold]
        if (Math.Abs(x) <= KCOSH_OVERFLOW)
        {
            double w = exp(half * Math.Abs(x));
            double t = half * w;
            return t * w;
        }

        /* x is INF or NaN */
        if (ix >= 0x7FF00000) return x * x;

        // |x| > overflowthreshold.
        return huge * huge;
    }

    /*
     * ES6 draft 09-27-13, section 20.2.2.30.
     * Math.sinh
     * Method :
     * mathematically sinh(x) if defined to be (exp(x)-exp(-x))/2
     *      1. Replace x by |x| (sinh(-x) = -sinh(x)).
     *      2.
     *                                                  E + E/(E+1)
     *          0        <= x <= 22     :  sinh(x) := --------------, E=expm1(x)
     *                                                      2
     *
     *          22       <= x <= lnovft :  sinh(x) := exp(x)/2
     *          lnovft   <= x <= ln2ovft:  sinh(x) := exp(x/2)/2 * exp(x/2)
     *          ln2ovft  <  x           :  sinh(x) := x*shuge (overflow)
     *
     * Special cases:
     *      sinh(x) is |x| if x is +Infinity, -Infinity, or NaN.
     *      only sinh(0)=0 is exact for finite x.
     */
    public static double sinh(double x)
    {
        const double KSINH_OVERFLOW = 710.4758600739439,
            TWO_M28 = 3.725290298461914e-9, // 2^-28, empty lower half
            LOG_MAXD = 709.7822265625; // 0x40862E42 00000000, empty lower half
        const double shuge = 1.0e307;

        double h = (x < 0) ? -0.5 : 0.5;
        // |x| in [0, 22]. return sign(x)*0.5*(E+E/(E+1))
        double ax = Math.Abs(x);
        if (ax < 22)
        {
            // For |x| < 2^-28, sinh(x) = x
            if (ax < TWO_M28) return x;
            double t = expm1(ax);
            if (ax < 1)
            {
                return h * (2 * t - t * t / (t + 1));
            }
            return h * (t + t / (t + 1));
        }
        // |x| in [22, log(maxdouble)], return 0.5 * exp(|x|)
        if (ax < LOG_MAXD) return h * exp(ax);
        // |x| in [log(maxdouble), overflowthreshold]
        // overflowthreshold = 710.4758600739426
        if (ax <= KSINH_OVERFLOW)
        {
            double w = exp(0.5 * ax);
            double t = h * w;
            return t * w;
        }
        // |x| > overflowthreshold or is NaN.
        // Return Infinity of the appropriate sign or NaN.
        return x * shuge;
    }

    // Returns the hyperbolic tangent of x: std::tanh, the platform's.
    public static double tanh(double x) => Math.Tanh(x);
}
