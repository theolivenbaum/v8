// Table-driven kernels for the correctly rounded functions of
// src/base/ieee754.cc (the ones V8 takes from llvm-libc).
//
// Each kernel follows the structure of llvm-libc's double-precision
// functions: a table lookup that reduces the argument to a small interval,
// a short polynomial, the result as an unevaluated sum hi + lo with about
// 2^-66 relative error, and Ziv's rounding test. When the test passes (all
// but about one argument in a few thousand), the rounding of hi + lo is the
// correctly rounded result; otherwise the caller falls back to the slower
// double-double and multiprecision evaluations in Ieee754.FastPath.cs and
// Ieee754.CorrectlyRounded.cs, which are also the test reference.
//
// Deviation: these kernels are written for V8Sharp, not transcribed from
// llvm-libc, whose sources are not part of this checkout. They compute the
// same correctly rounded results; the tables and error bounds are their own.

using System.Runtime.CompilerServices;

namespace V8Sharp.Base;

internal static partial class Ieee754Kernels
{
    // ---------------------------------------------------------------------
    // Error-free transformations.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double TwoSum(double a, double b, out double err)
    {
        double s = a + b;
        double bb = s - a;
        err = (a - (s - bb)) + (b - bb);
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double FastTwoSum(double a, double b, out double err)
    {
        double s = a + b;
        err = b - (s - a);
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double TwoProd(double a, double b, out double err)
    {
        double p = a * b;
        err = Math.FusedMultiplyAdd(a, b, -p);
        return p;
    }

    // Ziv's rounding test for hi + lo (normalized: |lo| <= ulp(hi) / 2) with
    // |f - (hi + lo)| <= err: the rounding is decided when both ends of the
    // interval round to the same double.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Round(double hi, double lo, double err, out double result)
    {
        double up = hi + (lo + err);
        result = up;
        return up == hi + (lo - err);
    }

    const double kTwoPowM65 = 2.710505431213761e-20;
    const double kTwoPowM66 = 1.3552527156068805e-20;
    const double kTwoPowM63 = 1.0842021724855044e-19;
    const double kTwoPowM72 = 2.117582368135751e-22;
    const double kTwoPowM100 = 7.888609052210118e-31;

    // ---------------------------------------------------------------------
    // exp.

    // 128 / ln(2).
    const double kInvLn2x128 = 184.66496523378731;

    // exp(x) = 2^m * (hi + lo) with hi + lo in [1, 2), relative error below
    // 2^-68, for |x| <= 708.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double ExpCore(double x, out double lo, out int m)
    {
        double kd = Math.Round(x * kInvLn2x128);
        int n = (int)kd;
        // x - n ln(2)/128: the first product is exact and cancels exactly.
        double rh = x - kd * D(kLn2Over128Hi);
        double r = TwoSum(rh, -kd * D(kLn2Over128Mid), out double rl);
        rl -= kd * D(kLn2Over128Lo);
        int j = n & 127;
        m = n >> 7;
        // expm1(r) - r for |r| <= ln(2)/256.
        double q = r * r * (0.5 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720)))));
        ReadOnlySpan<ulong> table = ExpTable;
        double th = D(table[2 * j]), tl = D(table[2 * j + 1]);
        // t * (1 + r + rl + q).
        double a = TwoProd(th, r, out double ae);
        double s = TwoSum(th, a, out double se);
        double l = se + ae + th * (rl + q) + tl + tl * (r + q);
        return FastTwoSum(s, l, out lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double TwoPow(int m) => BitConverter.Int64BitsToDouble((long)(m + 1023) << 52);

    public static bool TryExp(double x, out double result)
    {
        if (!(Math.Abs(x) <= 708))
        {
            result = 0;
            return false;
        }
        double h = ExpCore(x, out double l, out int m);
        if (!Round(h, l, h * kTwoPowM66, out result)) return false;
        result *= TwoPow(m);
        return true;
    }

    // expm1(x) - x - x^2/2 for |x| < 2^-6, as x^3 * Q(x).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Expm1Tail(double x) =>
        1.0 / 6 + x * (1.0 / 24 + x * (1.0 / 120 + x * (1.0 / 720 + x * (1.0 / 5040 + x * (1.0 / 40320 +
        x * (1.0 / 362880 + x * (1.0 / 3628800 + x * (1.0 / 39916800))))))));

    public static bool TryExpm1(double x, out double result)
    {
        double ax = Math.Abs(x);
        if (ax < 0.015625)
        {
            // x + x^2/2 + x^3 Q(x).
            double s = TwoProd(x, x, out double se);
            double a = TwoSum(x, 0.5 * s, out double ae);
            double l = ae + 0.5 * se + x * s * Expm1Tail(x);
            double h = FastTwoSum(a, l, out l);
            return Round(h, l, Math.Abs(h) * kTwoPowM63, out result);
        }
        if (!(x >= -40 && x <= 708))
        {
            result = 0;
            return false;
        }
        double eh = ExpCore(x, out double el, out int m);
        double scale = TwoPow(m);
        eh *= scale;
        el *= scale;
        double b = TwoSum(eh, -1.0, out double be);
        double hh = FastTwoSum(b, be + el, out double ll);
        return Round(hh, ll, eh * kTwoPowM65, out result);
    }

    // ---------------------------------------------------------------------
    // log.

    // log(f * 2^e) where the caller has split the argument: f in [1, 2) with
    // table index j (top seven mantissa bits), and extra a small addend to
    // f * invc - 1 (a low part of the argument, already scaled). Returns
    // hi + lo with the absolute error bound err.
    static double LogCore(double f, double extra, int e, int j, out double lo, out double err)
    {
        if (j >= kLogShiftIndex) e++;
        double invc = D(LogInvC[j]);
        double p = TwoProd(f, invc, out double pe);
        double r = p - 1.0;
        pe += extra;
        // log1p(r + pe) = r + pe - (r + pe)^2 / 2 + r^3 P(r).
        double s = TwoProd(r, r, out double se);
        double poly = 1.0 / 3 + r * (-0.25 + r * (0.2 + r * (-1.0 / 6 + r * (1.0 / 7 + r * (-0.125 +
                      r * (1.0 / 9 + r * -0.1))))));
        ReadOnlySpan<ulong> table = LogTable;
        double lh = D(table[2 * j]), ll = D(table[2 * j + 1]);
        double ed = e;
        double eh = TwoProd(ed, D(kLn2Hi), out double el);
        double t1 = TwoSum(eh, lh, out double t1e);
        double t2 = TwoSum(t1, r, out double t2e);
        double t3 = TwoSum(t2, -0.5 * s, out double t3e);
        double l = t1e + t2e + t3e + el + ed * D(kLn2Lo) + ll + pe - 0.5 * se - r * pe + s * pe + r * s * poly;
        double h = FastTwoSum(t3, l, out lo);
        err = Math.Abs(h) * kTwoPowM65 + (Math.Abs(eh) + Math.Abs(lh)) * kTwoPowM72;
        return h;
    }

    // Splits finite x > 0 for LogCore.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Split(double x, out int e, out int j)
    {
        ulong b = BitConverter.DoubleToUInt64Bits(x);
        e = 0;
        if (b < 0x0010_0000_0000_0000UL)
        {
            b = BitConverter.DoubleToUInt64Bits(x * 4503599627370496.0); // 2^52
            e = -52;
        }
        e += (int)(b >> 52) - 1023;
        j = (int)((b >> 45) & 127);
        return D((b & 0x000F_FFFF_FFFF_FFFFUL) | 0x3FF0_0000_0000_0000UL);
    }

    // log(x) for finite x > 0, x != 1, with the target base's factor applied.
    public static bool TryLog(CorrectlyRounded.Fn fn, double x, out double result)
    {
        double f = Split(x, out int e, out int j);
        double h = LogCore(f, 0, e, j, out double l, out double err);
        if (fn != CorrectlyRounded.Fn.Log)
        {
            double ch, cl;
            if (fn == CorrectlyRounded.Fn.Log2)
            {
                ch = D(kInvLn2Hi);
                cl = D(kInvLn2Lo);
            }
            else
            {
                ch = D(kInvLn10Hi);
                cl = D(kInvLn10Lo);
            }
            double a = TwoProd(h, ch, out double ae);
            h = FastTwoSum(a, ae + h * cl + l * ch, out l);
            err = err * ch + Math.Abs(h) * kTwoPowM100;
        }
        return Round(h, l, err, out result);
    }

    public static bool TryLog1p(double x, out double result)
    {
        if (Math.Abs(x) < 0.0078125)
        {
            // Table entry 0 (invc = 1): x itself is the reduced argument.
            double h0 = LogCore1p(x, out double l0, out double err0);
            return Round(h0, l0, err0, out result);
        }
        // 1 + x = u + ul exactly (u is normal since x > -1); the low part is
        // scaled like u and multiplied by the table's 1/c.
        double u = TwoSum(1.0, x, out double ul);
        double f = Split(u, out int e, out int j);
        double extra = ul * TwoPow(-e) * D(LogInvC[j]);
        double h = LogCore(f, extra, e, j, out double l, out double err);
        return Round(h, l, err, out result);
    }

    // log1p(x) for |x| < 2^-7, where the argument itself is the r of LogCore.
    static double LogCore1p(double r, out double lo, out double err)
    {
        double s = TwoProd(r, r, out double se);
        double poly = 1.0 / 3 + r * (-0.25 + r * (0.2 + r * (-1.0 / 6 + r * (1.0 / 7 + r * (-0.125 +
                      r * (1.0 / 9 + r * -0.1))))));
        double t = TwoSum(r, -0.5 * s, out double te);
        double h = FastTwoSum(t, te - 0.5 * se + r * s * poly, out lo);
        err = Math.Abs(h) * kTwoPowM65;
        return h;
    }

    // ---------------------------------------------------------------------
    // sin, cos, tan.

    const double kTwoOverPi = 0.63661977236758138;
    const double kHalfPi1 = 1.5707963267341256;       // 32 bits
    const double kHalfPi2 = 6.077100506303966e-11;    // 32 bits
    const double kHalfPi3 = 2.0222662487959506e-21;   // 53 bits

    // x = k pi/2 + (rh + rl); returns false for |x| >= 1.6e6.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Reduce(double x, out double rh, out double rl, out int quadrant, out double reductionError)
    {
        double ax = Math.Abs(x);
        if (ax <= 0.78539816339744828)
        {
            rh = x;
            rl = 0;
            quadrant = 0;
            reductionError = 0;
            return true;
        }
        if (!(ax < 1.6e6))
        {
            rh = rl = reductionError = 0;
            quadrant = 0;
            return false;
        }
        double k = Math.Round(x * kTwoOverPi);
        // The products with the 32-bit pieces are exact, and x - k p1 cancels.
        rh = TwoSum(x - k * kHalfPi1, -k * kHalfPi2, out rl);
        rl -= k * kHalfPi3;
        rh = FastTwoSum(rh, rl, out rl);
        quadrant = (int)((long)k & 3);
        reductionError = Math.Abs(k) * 1e-36 + Math.Abs(rh) * kTwoPowM100;
        return true;
    }

    // sin and cos of |r| <= pi/4 + epsilon as (hi, lo) pairs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SinCos(double rh, double rl, bool wantSin, bool wantCos,
                       out double sh, out double sl, out double ch, out double cl)
    {
        bool negative = rh < 0;
        double ar = Math.Abs(rh);
        double arl = negative ? -rl : rl;
        int j = (int)Math.Round(ar * 64);
        double t = ar - j * 0.015625;
        double t2 = t * t;
        // sin(t) - t and cos(t) - 1 for |t| <= 1/128, plus the low part.
        double sinTail = t * t2 * (-1.0 / 6 + t2 * (1.0 / 120 + t2 * (-1.0 / 5040 + t2 * (1.0 / 362880)))) + arl;
        // cos(t + arl) - 1 needs the first-order term of the low part.
        double cosTail = t2 * (-0.5 + t2 * (1.0 / 24 + t2 * (-1.0 / 720 + t2 * (1.0 / 40320)))) - arl * t;
        ReadOnlySpan<ulong> st = SinTable, ct = CosTable;
        double sjh = D(st[2 * j]), sjl = D(st[2 * j + 1]);
        double cjh = D(ct[2 * j]), cjl = D(ct[2 * j + 1]);
        sh = sl = ch = cl = 0;
        if (wantSin)
        {
            // sin(a + t) = S + C t + S (cos t - 1) + C (sin t - t).
            double p = TwoProd(cjh, t, out double pe);
            double h = TwoSum(sjh, p, out double he);
            double l = he + pe + sjl + cjl * t + sjh * cosTail + cjh * sinTail;
            sh = FastTwoSum(h, l, out sl);
            if (negative)
            {
                sh = -sh;
                sl = -sl;
            }
        }
        if (wantCos)
        {
            // cos(a + t) = C - S t + C (cos t - 1) - S (sin t - t).
            double p = TwoProd(sjh, t, out double pe);
            double h = TwoSum(cjh, -p, out double he);
            double l = he - pe + cjl - sjl * t + cjh * cosTail - sjh * sinTail;
            ch = FastTwoSum(h, l, out cl);
        }
    }

    public static bool TrySin(double x, out double result) => TrySinCos(x, true, out result);

    public static bool TryCos(double x, out double result) => TrySinCos(x, false, out result);

    static bool TrySinCos(double x, bool isSin, out double result)
    {
        if (!Reduce(x, out double rh, out double rl, out int quadrant, out double reductionError))
        {
            result = 0;
            return false;
        }
        bool useSin = ((quadrant & 1) == 0) == isSin;
        SinCos(rh, rl, useSin, !useSin, out double sh, out double sl, out double ch, out double cl);
        double h = useSin ? sh : ch, l = useSin ? sl : cl;
        bool negate = isSin ? quadrant >= 2 : quadrant is 1 or 2;
        if (negate)
        {
            h = -h;
            l = -l;
        }
        return Round(h, l, Math.Abs(h) * kTwoPowM65 + reductionError, out result);
    }

    public static bool TryTan(double x, out double result)
    {
        if (!Reduce(x, out double rh, out double rl, out int quadrant, out double reductionError))
        {
            result = 0;
            return false;
        }
        SinCos(rh, rl, true, true, out double sh, out double sl, out double ch, out double cl);
        double nh = sh, nl = sl, dh = ch, dl = cl;
        if ((quadrant & 1) != 0)
        {
            // tan(r + pi/2) = -cos(r) / sin(r).
            nh = -ch;
            nl = -cl;
            dh = sh;
            dl = sl;
        }
        double q = nh / dh;
        double rem = Math.FusedMultiplyAdd(-q, dh, nh) + nl - q * dl;
        double h = FastTwoSum(q, rem / dh, out double l);
        double relative = kTwoPowM63 + reductionError / Math.Abs(sh) + reductionError / Math.Abs(ch);
        return Round(h, l, Math.Abs(h) * relative, out result);
    }

    // ---------------------------------------------------------------------
    // atan and friends.

    // atan(yh + yl) for 0 <= yh <= 1: atan(c) + atan((y - c) / (1 + y c)) with
    // c = j/64 the nearest table point.
    static double AtanCore(double yh, double yl, out double lo)
    {
        int j = (int)Math.Round(yh * 64);
        double c = j * 0.015625;
        // Exact: y and c are within a factor of two of each other (or c = 0).
        double n = yh - c;
        double pd = TwoProd(yh, c, out double pde);
        double dh = TwoSum(1.0, pd, out double dl);
        dl += pde + yl * c;
        double th = n / dh;
        double tl = (Math.FusedMultiplyAdd(-th, dh, n) + yl - th * dl) / dh;
        double t2 = th * th;
        // atan(th + tl) = atan(th) + tl / (1 + th^2) + ...
        double tail = th * t2 * (-1.0 / 3 + t2 * (0.2 + t2 * (-1.0 / 7 + t2 * (1.0 / 9 + t2 * (-1.0 / 11))))) - tl * t2;
        ReadOnlySpan<ulong> table = AtanTable;
        double ah = D(table[2 * j]), al = D(table[2 * j + 1]);
        double h = TwoSum(ah, th, out double he);
        return FastTwoSum(h, he + al + tl + tail, out lo);
    }

    // pi/2 - (h + l).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double HalfPiMinus(double h, double l, out double lo)
    {
        double a = TwoSum(D(kHalfPiHi), -h, out double ae);
        return FastTwoSum(a, ae + D(kHalfPiLo) - l, out lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double PiMinus(double h, double l, out double lo)
    {
        double a = TwoSum(D(kPiHi), -h, out double ae);
        return FastTwoSum(a, ae + D(kPiLo) - l, out lo);
    }

    // atan((nh + nl) / (dh + dl)) in [0, pi/2] for non-negative operands.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double AtanOfRatio(double nh, double nl, double dh, double dl, out double lo)
    {
        if (nh <= dh)
        {
            double q = nh / dh;
            double ql = (Math.FusedMultiplyAdd(-q, dh, nh) + nl - q * dl) / dh;
            return AtanCore(q, ql, out lo);
        }
        else
        {
            double q = dh / nh;
            double ql = (Math.FusedMultiplyAdd(-q, nh, dh) + dl - q * nl) / nh;
            double h = AtanCore(q, ql, out double l);
            return HalfPiMinus(h, l, out lo);
        }
    }

    public static bool TryAtan(double x, out double result)
    {
        double ax = Math.Abs(x);
        double h, l;
        if (ax <= 1)
        {
            h = AtanCore(ax, 0, out l);
        }
        else if (ax < 1.152921504606847e18) // 2^60
        {
            double y = 1 / ax;
            double yl = Math.FusedMultiplyAdd(-y, ax, 1) / ax;
            h = AtanCore(y, yl, out l);
            h = HalfPiMinus(h, l, out l);
        }
        else
        {
            // pi/2 - 1/|x| rounds to pi/2.
            result = x < 0 ? -D(kHalfPiHi) : D(kHalfPiHi);
            return true;
        }
        if (x < 0)
        {
            h = -h;
            l = -l;
        }
        return Round(h, l, Math.Abs(h) * kTwoPowM65, out result);
    }

    public static bool TryAtan2(double y, double x, out double result)
    {
        double ay = Math.Abs(y), ax = Math.Abs(x);
        // Keep the ratio and its remainder away from underflow and overflow.
        if (!(ay >= 1e-290 && ay <= 1e290 && ax >= 1e-290 && ax <= 1e290) || ay > ax * 1e280 || ax > ay * 1e280)
        {
            result = 0;
            return false;
        }
        double h = AtanOfRatio(ay, 0, ax, 0, out double l);
        if (x < 0) h = PiMinus(h, l, out l);
        if (y < 0)
        {
            h = -h;
            l = -l;
        }
        return Round(h, l, Math.Abs(h) * kTwoPowM65, out result);
    }

    // sqrt(1 - x^2) as (hi, lo) for |x| < 1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Complement(double ax, out double lo)
    {
        double p = TwoProd(ax, ax, out double pe);
        double wh = TwoSum(1.0, -p, out double wl);
        wl -= pe;
        double sh = Math.Sqrt(wh);
        return FastTwoSum(sh, (Math.FusedMultiplyAdd(-sh, sh, wh) + wl) / (2 * sh), out lo);
    }

    public static bool TryAsin(double x, out double result)
    {
        double ax = Math.Abs(x);
        double sh = Complement(ax, out double sl);
        double h = AtanOfRatio(ax, 0, sh, sl, out double l);
        if (x < 0)
        {
            h = -h;
            l = -l;
        }
        return Round(h, l, Math.Abs(h) * kTwoPowM65, out result);
    }

    public static bool TryAcos(double x, out double result)
    {
        double ax = Math.Abs(x);
        double sh = Complement(ax, out double sl);
        double h = AtanOfRatio(sh, sl, ax, 0, out double l);
        if (x < 0) h = PiMinus(h, l, out l);
        return Round(h, l, Math.Abs(h) * kTwoPowM65, out result);
    }
}
