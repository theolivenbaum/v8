// The fast path of the correctly rounded functions (see
// Ieee754.CorrectlyRounded.cs): the function is evaluated in double-double
// arithmetic (about 2^-100 relative error) and the result is returned when
// Ziv's rounding test proves that the rounding of every value within the
// error bound is the same double. Otherwise, which happens for about one
// argument in 2^35 and for arguments outside the ranges handled here, the
// caller falls back to the multiprecision evaluation.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base;

/// <summary>An unevaluated sum hi + lo with |lo| <= ulp(hi) / 2.</summary>
internal readonly struct DoubleDouble(double hi, double lo)
{
    public readonly double hi = hi;
    public readonly double lo = lo;
}

internal static partial class CorrectlyRounded
{
    // Powers of two (C# has no hexadecimal floating-point literals).
    internal const double kTwoPowM1022 = 2.2250738585072014e-308;
    internal const double kTwoPowM1000 = 9.332636185032189e-302;
    internal const double kTwoPowM900 = 1.1830521861667747e-271;
    internal const double kTwoPowM500 = 3.054936363499605e-151;
    internal const double kTwoPowM158 = 2.7369110631344083e-48;
    internal const double kTwoPowM100 = 7.888609052210118e-31;
    internal const double kTwoPowM96 = 1.262177448353619e-29;
    internal const double kTwoPowM95 = 2.524354896707238e-29;
    internal const double kTwoPowM90 = 8.077935669463161e-28;
    internal const double kTwoPowM60 = 8.673617379884035e-19;
    internal const double kTwoPowM27 = 7.450580596923828e-09;
    internal const double kTwoPow500 = 3.273390607896142e+150;
    internal const double kTwoPow1000 = 1.0715086071862673e+301;

    // Relative error bound claimed for the double-double kernels.
    const double kRelativeError = kTwoPowM90;

    // ---------------------------------------------------------------------
    // Double-double arithmetic.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble TwoSum(double a, double b)
    {
        double s = a + b;
        double bb = s - a;
        return new(s, (a - (s - bb)) + (b - bb));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble FastTwoSum(double a, double b)
    {
        double s = a + b;
        return new(s, b - (s - a));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble TwoProd(double a, double b)
    {
        double p = a * b;
        return new(p, Math.FusedMultiplyAdd(a, b, -p));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Add(DoubleDouble a, DoubleDouble b)
    {
        DoubleDouble s = TwoSum(a.hi, b.hi);
        DoubleDouble t = TwoSum(a.lo, b.lo);
        s = FastTwoSum(s.hi, s.lo + t.hi);
        return FastTwoSum(s.hi, s.lo + t.lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Add(DoubleDouble a, double b)
    {
        DoubleDouble s = TwoSum(a.hi, b);
        return FastTwoSum(s.hi, s.lo + a.lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Neg(DoubleDouble a) => new(-a.hi, -a.lo);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Mul(DoubleDouble a, DoubleDouble b)
    {
        DoubleDouble p = TwoProd(a.hi, b.hi);
        return FastTwoSum(p.hi, p.lo + (a.hi * b.lo + a.lo * b.hi));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Mul(DoubleDouble a, double b)
    {
        DoubleDouble p = TwoProd(a.hi, b);
        return FastTwoSum(p.hi, p.lo + a.lo * b);
    }

    static DoubleDouble Div(DoubleDouble a, DoubleDouble b)
    {
        double q1 = a.hi / b.hi;
        DoubleDouble r = Add(a, Neg(Mul(b, q1)));
        double q2 = r.hi / b.hi;
        r = Add(r, Neg(Mul(b, q2)));
        double q3 = r.hi / b.hi;
        return Add(FastTwoSum(q1, q2), q3);
    }

    // a / b for doubles.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DoubleDouble Div(double a, double b)
    {
        double q1 = a / b;
        double r = Math.FusedMultiplyAdd(-q1, b, a);
        return FastTwoSum(q1, r / b);
    }

    static DoubleDouble Sqrt(DoubleDouble a)
    {
        double s = Math.Sqrt(a.hi);
        DoubleDouble r = Add(a, Neg(TwoProd(s, s)));
        return FastTwoSum(s, r.hi / (2 * s));
    }

    // Ziv's rounding test: v approximates f with |f - v| <= absoluteError.
    static bool TryRound(DoubleDouble v, double absoluteError, out double result)
    {
        result = v.hi;
        double magnitude = Math.Abs(v.hi);
        if (!(magnitude >= kTwoPowM1000 && magnitude <= kTwoPow1000)) return false;
        double error = 2 * absoluteError;
        double up = Math.BitIncrement(v.hi) - v.hi;
        double down = v.hi - Math.BitDecrement(v.hi);
        return v.lo + error < 0.5 * up && v.lo - error > -0.5 * down;
    }

    static bool TryRound(DoubleDouble v, out double result) =>
        TryRound(v, Math.Abs(v.hi) * kRelativeError, out result);

    // ---------------------------------------------------------------------
    // Constants, derived from the multiprecision ones.

    static class Constants
    {
        const int kBits = 400;

        public static readonly DoubleDouble Ln2 = ToDD(CorrectlyRounded.Ln2(kBits));
        public static readonly DoubleDouble InvLn2 = ToDD((BigInteger.One << (2 * kBits)) / CorrectlyRounded.Ln2(kBits));
        public static readonly DoubleDouble InvLn10 = ToDD((BigInteger.One << (2 * kBits)) / CorrectlyRounded.Ln10(kBits));
        public static readonly DoubleDouble Pi = ToDD(CorrectlyRounded.Pi(kBits));
        public static readonly DoubleDouble HalfPi = ToDD(CorrectlyRounded.Pi(kBits) >> 1);
        public static readonly double TwoOverPi = 1 / HalfPi.hi;

        // log(2) and pi/2 in pieces whose products with the reduction
        // multiple k (|k| < 2^11 and 2^21) are exact.
        public static readonly double[] Ln2Pieces = Pieces(CorrectlyRounded.Ln2(kBits), 40, 4);
        public static readonly double[] HalfPiPieces = Pieces(CorrectlyRounded.Pi(kBits) >> 1, 32, 5);

        // 1/n!, and the sine and cosine Taylor coefficients.
        public static readonly DoubleDouble[] InvFactorial = MakeInvFactorials(28);
        public static readonly DoubleDouble[] SinCoefficients = MakeTrigCoefficients(1, 14);
        public static readonly DoubleDouble[] CosCoefficients = MakeTrigCoefficients(0, 14);

        static DoubleDouble ToDD(BigInteger v)
        {
            double hi = ScaledToDouble(v, -kBits);
            double lo = ScaledToDouble(v - Fixed(hi, kBits), -kBits);
            return new(hi, lo);
        }

        static double[] Pieces(BigInteger v, int bits, int count)
        {
            double[] result = new double[count];
            for (int i = 0; i < count; i++)
            {
                int shift = (int)Math.Max(0, (long)v.GetBitLength() - bits);
                BigInteger piece = (v >> shift) << shift;
                result[i] = ScaledToDouble(piece, -kBits);
                v -= piece;
            }
            return result;
        }

        static DoubleDouble[] MakeInvFactorials(int n)
        {
            DoubleDouble[] result = new DoubleDouble[n + 1];
            BigInteger factorial = BigInteger.One;
            for (int i = 0; i <= n; i++)
            {
                if (i > 0) factorial *= i;
                result[i] = ToDD((BigInteger.One << kBits) / factorial);
            }
            return result;
        }

        static DoubleDouble[] MakeTrigCoefficients(int first, int count)
        {
            DoubleDouble[] result = new DoubleDouble[count];
            BigInteger factorial = BigInteger.One;
            for (int i = 2; i <= first; i++) factorial *= i;
            for (int i = 0; i < count; i++)
            {
                int n = first + 2 * i;
                if (i > 0) factorial *= (n - 1) * n;
                BigInteger v = (BigInteger.One << kBits) / factorial;
                result[i] = ToDD((i & 1) != 0 ? -v : v);
            }
            return result;
        }
    }

    // ---------------------------------------------------------------------
    // Kernels.

    // expm1(r) for |r| <= 0.35: Taylor series of r / 2^8, then eight times
    // p <- (1 + p)^2 - 1, which keeps the relative precision.
    static DoubleDouble Expm1Kernel(DoubleDouble r)
    {
        DoubleDouble[] c = Constants.InvFactorial;
        DoubleDouble s = new(r.hi * (1.0 / 256), r.lo * (1.0 / 256));
        DoubleDouble acc = c[10];
        for (int n = 9; n >= 1; n--) acc = Add(Mul(acc, s), c[n]);
        DoubleDouble p = Mul(acc, s);
        for (int i = 0; i < 8; i++) p = Mul(p, Add(p, 2.0));
        return p;
    }

    // exp(x) = e * 2^k with e in [0.7, 1.42].
    static DoubleDouble ExpReducedDD(double x, out double k)
    {
        k = Math.Round(x * Constants.InvLn2.hi);
        DoubleDouble r;
        if (k == 0)
        {
            r = new(x, 0);
        }
        else
        {
            double[] l = Constants.Ln2Pieces;
            // Exact: k * l[i] has at most 51 bits and x - k * l[0] cancels.
            r = TwoSum(x - k * l[0], -k * l[1]);
            r = Add(r, -k * l[2]);
            r = Add(r, -k * l[3]);
        }
        return Add(Expm1Kernel(r), 1.0);
    }

    static DoubleDouble SinKernel(DoubleDouble r)
    {
        DoubleDouble[] c = Constants.SinCoefficients;
        DoubleDouble z = Mul(r, r);
        DoubleDouble acc = c[^1];
        for (int i = c.Length - 2; i >= 0; i--) acc = Add(Mul(acc, z), c[i]);
        return Mul(acc, r);
    }

    static DoubleDouble CosKernel(DoubleDouble r)
    {
        DoubleDouble[] c = Constants.CosCoefficients;
        DoubleDouble z = Mul(r, r);
        DoubleDouble acc = c[^1];
        for (int i = c.Length - 2; i >= 0; i--) acc = Add(Mul(acc, z), c[i]);
        return acc;
    }

    // log(f) for f in [sqrt(1/2), sqrt(2)] given as a double-double: one
    // Newton step y1 = y0 + f exp(-y0) - 1 from y0 ~ log(f).
    static DoubleDouble LogKernel(DoubleDouble f, double y0)
    {
        DoubleDouble p = Expm1Kernel(new DoubleDouble(-y0, 0));
        DoubleDouble d = Add(Mul(p, f), Add(f, -1.0));
        return Add(d, y0);
    }

    // atan(q) for |q| <= 1: one Newton step on sin(y) - q cos(y) from
    // y0 = atan(q.hi), y1 = y0 - cos(y0) (sin(y0) - q cos(y0)).
    static DoubleDouble AtanKernel(DoubleDouble q)
    {
        double y0 = Math.Atan(q.hi);
        DoubleDouble y = new(y0, 0);
        DoubleDouble s = SinKernel(y);
        DoubleDouble c = CosKernel(y);
        DoubleDouble t = Add(s, Neg(Mul(q, c)));
        return Add(Neg(Mul(c, t)), y0);
    }

    // log(x) for finite x > 0, x != 1.
    static DoubleDouble LogDD(double x)
    {
        int e = Math.ILogB(x);
        double f = Math.ScaleB(x, -e);
        if (f > 1.4142135623730951)
        {
            f *= 0.5;
            e++;
        }
        DoubleDouble y = LogKernel(new DoubleDouble(f, 0), Math.Log(f));
        return e == 0 ? y : Add(Mul(Constants.Ln2, e), y);
    }

    // ---------------------------------------------------------------------
    // Fast paths. Each returns false when the caller must use Evaluate.

    public static bool TryExp(double x, out double result)
    {
        result = 0;
        if (!(Math.Abs(x) <= 708)) return false;
        DoubleDouble e = ExpReducedDD(x, out double k);
        if (!TryRound(e, out result)) return false;
        result = Math.ScaleB(result, (int)k);
        return true;
    }

    public static bool TryExpm1(double x, out double result)
    {
        if (Math.Abs(x) < kTwoPowM60)
        {
            result = x;
            return true;
        }
        if (Math.Abs(x) <= 0.34) return TryRound(Expm1Kernel(new DoubleDouble(x, 0)), out result);
        result = 0;
        if (!(x >= -40 && x <= 708)) return false;
        DoubleDouble e = ExpReducedDD(x, out double k);
        int ik = (int)k;
        DoubleDouble scaled = new(Math.ScaleB(e.hi, ik), Math.ScaleB(e.lo, ik));
        return TryRound(Add(scaled, -1.0), out result);
    }

    public static bool TryLog(Fn fn, double x, out double result)
    {
        result = 0;
        ulong bits = BitConverter.DoubleToUInt64Bits(x);
        if (fn == Fn.Log2 && x >= kTwoPowM1022 && (bits & 0x000F_FFFF_FFFF_FFFFUL) == 0)
        {
            result = Math.ILogB(x);
            return true;
        }
        DoubleDouble v = LogDD(x);
        if (fn == Fn.Log2) v = Mul(v, Constants.InvLn2);
        else if (fn == Fn.Log10) v = Mul(v, Constants.InvLn10);
        return TryRound(v, out result);
    }

    public static bool TryLog1p(double x, out double result)
    {
        if (Math.Abs(x) < kTwoPowM60)
        {
            result = x;
            return true;
        }
        result = 0;
        if (!(x > -1 && x <= kTwoPow1000)) return false;
        DoubleDouble u = TwoSum(1.0, x);
        int e = Math.ILogB(u.hi);
        DoubleDouble f = new(Math.ScaleB(u.hi, -e), Math.ScaleB(u.lo, -e));
        if (f.hi > 1.4142135623730951)
        {
            f = new(f.hi * 0.5, f.lo * 0.5);
            e++;
        }
        double y0 = Math.Log(f.hi) + f.lo / f.hi;
        DoubleDouble y = LogKernel(f, y0);
        if (e != 0) y = Add(Mul(Constants.Ln2, e), y);
        return TryRound(y, out result);
    }

    public static bool TryTrig(Fn fn, double x, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        if (!(ax <= 1.6e6)) return false;
        DoubleDouble r;
        int quadrant = 0;
        double reductionError = 0;
        if (ax <= 0.7853981633974483)
        {
            r = new(x, 0);
        }
        else
        {
            double k = Math.Round(x * Constants.TwoOverPi);
            double[] p = Constants.HalfPiPieces;
            // Exact: k * p[i] has at most 53 bits and x - k * p[0] cancels.
            r = TwoSum(x - k * p[0], -k * p[1]);
            r = Add(r, -k * p[2]);
            r = Add(r, -k * p[3]);
            r = Add(r, -k * p[4]);
            quadrant = (int)((long)k & 3);
            // Truncation of pi/2 after 160 bits, and the additions.
            reductionError = Math.Abs(k) * kTwoPowM158 + Math.Abs(r.hi) * kTwoPowM100;
        }
        if (fn == Fn.Tan)
        {
            DoubleDouble s = SinKernel(r), c = CosKernel(r);
            DoubleDouble t = (quadrant & 1) == 0 ? Div(s, c) : Neg(Div(c, s));
            double relative = kTwoPowM96 + reductionError / Math.Abs(s.hi) + reductionError / Math.Abs(c.hi);
            return TryRound(t, Math.Abs(t.hi) * relative, out result);
        }
        bool useSin = (quadrant & 1) == 0 ? fn == Fn.Sin : fn == Fn.Cos;
        DoubleDouble v = useSin ? SinKernel(r) : CosKernel(r);
        bool negate = fn == Fn.Sin ? quadrant >= 2 : quadrant is 1 or 2;
        if (negate) v = Neg(v);
        return TryRound(v, Math.Abs(v.hi) * kRelativeError + reductionError, out result);
    }

    // atan(a / b) in [0, pi/2] for a, b >= 0 given as double-doubles.
    static DoubleDouble AtanOfRatioDD(DoubleDouble a, DoubleDouble b)
    {
        if (a.hi <= b.hi) return AtanKernel(Div(a, b));
        return Add(Neg(AtanKernel(Div(b, a))), Constants.HalfPi);
    }

    public static bool TryAtan(double x, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        if (!(ax >= kTwoPowM1000 && ax <= kTwoPow1000)) return false;
        DoubleDouble v = ax <= 1
            ? AtanKernel(new DoubleDouble(ax, 0))
            : Add(Neg(AtanKernel(Div(1.0, ax))), Constants.HalfPi);
        if (x < 0) v = Neg(v);
        return TryRound(v, out result);
    }

    public static bool TryAtan2(double y, double x, out double result)
    {
        result = 0;
        double ay = Math.Abs(y), ax = Math.Abs(x);
        if (!(ay >= kTwoPowM500 && ay <= kTwoPow500 && ax >= kTwoPowM500 && ax <= kTwoPow500)) return false;
        DoubleDouble v = ay <= ax
            ? AtanKernel(Div(ay, ax))
            : Add(Neg(AtanKernel(Div(ax, ay))), Constants.HalfPi);
        if (x < 0) v = Add(Neg(v), Constants.Pi);
        if (y < 0) v = Neg(v);
        return TryRound(v, out result);
    }

    // sqrt(1 - x^2).
    static DoubleDouble ComplementDD(double x) => Sqrt(Add(Neg(TwoProd(x, x)), 1.0));

    public static bool TryAsin(double x, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        if (!(ax >= kTwoPowM1000 && ax < 1)) return false;
        DoubleDouble v = AtanOfRatioDD(new DoubleDouble(ax, 0), ComplementDD(ax));
        if (x < 0) v = Neg(v);
        return TryRound(v, out result);
    }

    public static bool TryAcos(double x, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        if (!(ax >= kTwoPowM1000 && ax < 1)) return false;
        DoubleDouble v = AtanOfRatioDD(ComplementDD(ax), new DoubleDouble(ax, 0));
        if (x < 0) v = Add(Neg(v), Constants.Pi);
        return TryRound(v, out result);
    }

    public static bool TryCbrt(double x, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        if (!(ax >= kTwoPowM900 && ax <= kTwoPow1000)) return false;
        double y0 = Math.Cbrt(x);
        DoubleDouble y2 = TwoProd(y0, y0);
        DoubleDouble residual = Add(Mul(y2, y0), -x);
        double correction = residual.hi / (3 * y2.hi);
        return TryRound(FastTwoSum(y0, -correction), Math.Abs(y0) * kTwoPowM95, out result);
    }
}
