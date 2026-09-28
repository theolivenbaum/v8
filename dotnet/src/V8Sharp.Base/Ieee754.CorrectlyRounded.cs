// Correctly rounded elementary functions, standing in for the llvm-libc
// functions (third_party/llvm-libc/src/shared/math.h) that src/base/ieee754.cc
// delegates acos, asin, atan, atan2, cos, sin, tan, exp, expm1, log, log1p,
// log2, log10, cbrt and legacy::pow to.
//
// Deviation: llvm-libc's double functions are correctly rounded (round to
// nearest even), which fixes their results completely, but its sources are
// not part of this checkout (third_party/llvm-libc holds only BUILD.gn). The
// port therefore computes the same correctly rounded results with its own
// algorithms, in three stages: the table-driven kernels of
// Ieee754.Kernels.cs (the common path, a few tens of nanoseconds), the
// double-double evaluation of Ieee754.FastPath.cs, and this file: Ziv's
// strategy over a multiprecision fixed-point evaluation
// (System.Numerics.BigInteger), doubling the working precision until the
// rounding of the enclosing interval is decided. This last stage is also the
// reference the tests hold the faster stages to.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace V8Sharp.Base;

internal static partial class CorrectlyRounded
{
    public enum Fn
    {
        Exp,
        Expm1,
        Log,
        Log1p,
        Log2,
        Log10,
        Sin,
        Cos,
        Tan,
        Atan,
        Atan2,
        Asin,
        Acos,
        Pow,
    }

    // Evaluators return m, e with |f(x) - m * 2^e| <= 2^(e + kSlack).
    const int kSlack = 24;
    const int kInitialBits = 128;
    const int kMaxBits = 1 << 16;

    // Rounds f(x, y) to nearest. `estimate` is an approximation of the result
    // (from the platform libm) that sizes the first working precision; a
    // result that underflows to zero takes the sign `negativeZero`.
    public static double Evaluate(Fn fn, double x, double y, double estimate, bool negativeZero = false)
    {
        int mag = double.IsFinite(estimate) && estimate != 0 ? Math.ILogB(estimate) : 0;
        int w = kInitialBits + Math.Abs(mag);
        while (true)
        {
            BigInteger m = Eval(fn, x, y, w, out int e);
            BigInteger err = BigInteger.One << kSlack;
            double lo = ScaledToDouble(m - err, e);
            double hi = ScaledToDouble(m + err, e);
            if (lo == hi)
            {
                if (lo == 0) return negativeZero ? -0.0 : 0.0;
                return lo;
            }
            if (w >= kMaxBits) return ScaledToDouble(m, e);
            w *= 2;
        }
    }

    static BigInteger Eval(Fn fn, double x, double y, int w, out int e)
    {
        switch (fn)
        {
            case Fn.Exp: return EvalExp(x, w, out e);
            case Fn.Expm1: return EvalExpm1(x, w, out e);
            case Fn.Log: e = -w; return EvalLog(x, w);
            case Fn.Log1p: return EvalLog1p(x, w, out e);
            case Fn.Log2: e = -w; return (EvalLog(x, w) << w) / Ln2(w);
            case Fn.Log10: e = -w; return (EvalLog(x, w) << w) / Ln10(w);
            case Fn.Sin:
            case Fn.Cos:
            case Fn.Tan: e = -w; return EvalTrig(fn, x, w);
            case Fn.Atan: e = -w; return EvalAtan(x, w);
            case Fn.Atan2: e = -w; return EvalAtan2(x, y, w);
            case Fn.Asin: e = -w; return EvalAsin(x, w);
            case Fn.Acos: e = -w; return EvalAcos(x, w);
            case Fn.Pow: return EvalPow(x, y, w, out e);
            default: throw new ArgumentOutOfRangeException(nameof(fn));
        }
    }

    // ---------------------------------------------------------------------
    // Conversions.

    // Splits finite, non-zero |x| into mantissa * 2^exponent with the
    // mantissa odd or < 2^53.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Decompose(double x, out int exponent)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(x) & 0x7FFF_FFFF_FFFF_FFFFUL;
        int biased = (int)(bits >> 52);
        ulong mantissa = bits & 0x000F_FFFF_FFFF_FFFFUL;
        if (biased == 0)
        {
            exponent = -1074;
        }
        else
        {
            mantissa |= 1UL << 52;
            exponent = biased - 1075;
        }
        return mantissa;
    }

    // |x| as a fixed-point number with w fraction bits (truncated).
    static BigInteger FixedAbs(double x, int w)
    {
        if (x == 0) return BigInteger.Zero;
        ulong m = Decompose(x, out int e);
        int shift = e + w;
        return shift >= 0 ? new BigInteger(m) << shift : new BigInteger(m) >> -shift;
    }

    static BigInteger Fixed(double x, int w)
    {
        BigInteger v = FixedAbs(x, w);
        return x < 0 ? -v : v;
    }

    // m * 2^e rounded to the nearest double (ties to even), with gradual
    // underflow and overflow to infinity.
    internal static double ScaledToDouble(BigInteger m, int e)
    {
        if (m.IsZero) return 0;
        bool negative = m.Sign < 0;
        if (negative) m = -m;
        long length = (long)m.GetBitLength();
        long exponent = length - 1 + e;
        double result;
        if (exponent > 1023)
        {
            result = double.PositiveInfinity;
        }
        else
        {
            long bits = exponent >= -1022 ? 53 : exponent + 1075;
            long shift = length - bits;
            if (shift <= 0)
            {
                result = Math.ScaleB((double)(ulong)m, e);
            }
            else
            {
                BigInteger q = m >> (int)shift;
                BigInteger rem = m - (q << (int)shift);
                int c = rem.CompareTo(BigInteger.One << (int)(shift - 1));
                if (c > 0 || (c == 0 && !q.IsEven)) q += BigInteger.One;
                result = Math.ScaleB((double)(ulong)q, (int)(e + shift));
            }
        }
        return negative ? -result : result;
    }

    static BigInteger ISqrt(BigInteger n)
    {
        if (n.Sign <= 0) return BigInteger.Zero;
        long length = (long)n.GetBitLength();
        BigInteger x = BigInteger.One << (int)((length + 1) / 2);
        while (true)
        {
            BigInteger y = (x + n / x) >> 1;
            if (y >= x) break;
            x = y;
        }
        while (x * x > n) x -= BigInteger.One;
        while ((x + 1) * (x + 1) <= n) x += BigInteger.One;
        return x;
    }

    static BigInteger ICbrt(BigInteger n)
    {
        if (n.Sign <= 0) return BigInteger.Zero;
        long length = (long)n.GetBitLength();
        BigInteger x = BigInteger.One << (int)((length + 2) / 3);
        while (true)
        {
            BigInteger y = (2 * x + n / (x * x)) / 3;
            if (y >= x) break;
            x = y;
        }
        while (x * x * x > n) x -= BigInteger.One;
        while ((x + 1) * (x + 1) * (x + 1) <= n) x += BigInteger.One;
        return x;
    }

    // ---------------------------------------------------------------------
    // Constants, cached at the largest precision computed so far.

    sealed class CachedConstant(Func<int, BigInteger> compute)
    {
        BigInteger _value;
        int _bits = -1;
        readonly Lock _lock = new();

        public BigInteger Get(int w)
        {
            lock (_lock)
            {
                if (_bits < w + 32)
                {
                    int bits = Math.Max(w + 32, _bits * 2);
                    _value = compute(bits);
                    _bits = bits;
                }
                return _value >> (_bits - w);
            }
        }
    }

    // sum_k 1 / ((2k+1) n^(2k+1)) = atanh(1/n).
    static BigInteger AtanhInv(int n, int w)
    {
        BigInteger power = (BigInteger.One << w) / n;
        BigInteger sum = power;
        int n2 = n * n;
        for (int k = 1; ; k++)
        {
            power /= n2;
            if (power.IsZero) break;
            sum += power / (2 * k + 1);
        }
        return sum;
    }

    // sum_k (-1)^k / ((2k+1) n^(2k+1)) = atan(1/n).
    static BigInteger AtanInv(int n, int w)
    {
        BigInteger power = (BigInteger.One << w) / n;
        BigInteger sum = power;
        int n2 = n * n;
        for (int k = 1; ; k++)
        {
            power /= n2;
            if (power.IsZero) break;
            BigInteger term = power / (2 * k + 1);
            sum += (k & 1) != 0 ? -term : term;
        }
        return sum;
    }

    static readonly CachedConstant s_ln2 = new(w => (2 * AtanhInv(3, w + 16)) >> 16);
    // log(10) = 3 log(2) + log(5/4) = 3 log(2) + 2 atanh(1/9).
    static readonly CachedConstant s_ln10 = new(w => (6 * AtanhInv(3, w + 16) + 2 * AtanhInv(9, w + 16)) >> 16);
    // Machin: pi = 16 atan(1/5) - 4 atan(1/239).
    static readonly CachedConstant s_pi = new(w => (16 * AtanInv(5, w + 16) - 4 * AtanInv(239, w + 16)) >> 16);

    static BigInteger Ln2(int w) => s_ln2.Get(w);
    static BigInteger Ln10(int w) => s_ln10.Get(w);
    static BigInteger Pi(int w) => s_pi.Get(w);

    // ---------------------------------------------------------------------
    // Kernels over fixed-point numbers with w fraction bits.

    // a * b truncated toward zero, so that series terms reach zero (an
    // arithmetic shift would leave negative terms at -1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static BigInteger Mul(BigInteger a, BigInteger b, int w)
    {
        BigInteger p = a * b;
        return p.Sign >= 0 ? p >> w : -(-p >> w);
    }

    // exp(r) for |r| <= 1: Taylor series of r / 2^8, squared 8 times.
    static BigInteger ExpKernel(BigInteger r, int w)
    {
        const int s = 8;
        int wi = w + s + 8;
        BigInteger one = BigInteger.One << wi;
        BigInteger t = r << (wi - w - s);
        BigInteger sum = one;
        BigInteger term = one;
        for (int n = 1; ; n++)
        {
            term = Mul(term, t, wi) / n;
            if (term.IsZero) break;
            sum += term;
        }
        for (int i = 0; i < s; i++) sum = Mul(sum, sum, wi);
        return sum >> (wi - w);
    }

    // log(f) for f in [1/2, 2]: 2 atanh((f - 1) / (f + 1)).
    static BigInteger LogKernel(BigInteger f, int w)
    {
        BigInteger one = BigInteger.One << w;
        BigInteger t = ((f - one) << w) / (f + one);
        BigInteger t2 = Mul(t, t, w);
        BigInteger sum = t;
        BigInteger power = t;
        for (int k = 1; ; k++)
        {
            power = Mul(power, t2, w);
            if (power.IsZero) break;
            sum += power / (2 * k + 1);
        }
        return sum << 1;
    }

    // log(y) for a positive fixed-point y.
    static BigInteger LogOfFixed(BigInteger y, int w)
    {
        long n = (long)y.GetBitLength() - 1 - w;
        BigInteger f = n >= 0 ? y >> (int)n : y << (int)-n;
        // Keep f in [sqrt(1/2), sqrt(2)).
        if (f * f > (BigInteger.One << (2 * w + 1)))
        {
            f >>= 1;
            n++;
        }
        return LogKernel(f, w) + n * Ln2(w);
    }

    // atan(t) for t in [0, 1].
    static BigInteger AtanKernel(BigInteger t, int w)
    {
        int wi = w + 8;
        t <<= 8;
        BigInteger one = BigInteger.One << wi;
        int halvings = 0;
        // atan(t) = 2 atan(t / (1 + sqrt(1 + t^2))).
        while (t > (one >> 3))
        {
            BigInteger root = ISqrt((one << wi) + t * t);
            t = (t << wi) / (one + root);
            halvings++;
        }
        BigInteger t2 = Mul(t, t, wi);
        BigInteger sum = t;
        BigInteger power = t;
        for (int k = 1; ; k++)
        {
            power = Mul(power, t2, wi);
            if (power.IsZero) break;
            BigInteger term = power / (2 * k + 1);
            sum += (k & 1) != 0 ? -term : term;
        }
        return (sum << halvings) >> 8;
    }

    // atan(a / b) in [0, pi/2] for a, b >= 0, not both zero.
    static BigInteger AtanOfRatio(BigInteger a, BigInteger b, int w)
    {
        if (b.IsZero) return Pi(w) >> 1;
        if (a <= b) return AtanKernel((a << w) / b, w);
        return (Pi(w) >> 1) - AtanKernel((b << w) / a, w);
    }

    // sin(r) and cos(r) for |r| <= pi/4.
    static void SinCosKernel(BigInteger r, int w, out BigInteger sin, out BigInteger cos)
    {
        BigInteger r2 = Mul(r, r, w);
        BigInteger term = r;
        sin = r;
        for (int n = 2; ; n += 2)
        {
            term = -Mul(term, r2, w) / (n * (n + 1));
            if (term.IsZero) break;
            sin += term;
        }
        term = BigInteger.One << w;
        cos = term;
        for (int n = 1; ; n += 2)
        {
            term = -Mul(term, r2, w) / (n * (n + 1));
            if (term.IsZero) break;
            cos += term;
        }
    }

    // ---------------------------------------------------------------------
    // Evaluators.

    // exp(x) = 2^k exp(x - k log 2); the result is E * 2^(k - w).
    static BigInteger ExpReduced(BigInteger xFixed, int w, out BigInteger k)
    {
        int wi = w + 16;
        BigInteger ln2 = Ln2(wi);
        BigInteger xi = xFixed << 16;
        k = BigInteger.Divide(xi + (xi.Sign >= 0 ? ln2 >> 1 : -(ln2 >> 1)), ln2);
        BigInteger r = xi - k * ln2;
        return ExpKernel(r, wi) >> 16;
    }

    static BigInteger EvalExp(double x, int w, out int e)
    {
        BigInteger m = ExpReduced(Fixed(x, w), w, out BigInteger k);
        e = (int)k - w;
        return m;
    }

    static BigInteger EvalExpm1(double x, int w, out int e)
    {
        if (Math.Abs(x) < 0.5)
        {
            // Direct series, which keeps the relative precision for small x.
            BigInteger t = Fixed(x, w);
            BigInteger sum = t;
            BigInteger term = t;
            for (int n = 2; ; n++)
            {
                term = Mul(term, t, w) / n;
                if (term.IsZero) break;
                sum += term;
            }
            e = -w;
            return sum;
        }
        BigInteger m = ExpReduced(Fixed(x, w), w, out BigInteger kb);
        int k = (int)kb;
        if (k >= 0)
        {
            e = -w;
            return (m << k) - (BigInteger.One << w);
        }
        e = k - w;
        return m - (BigInteger.One << (w - k));
    }

    static BigInteger EvalLog(double x, int w)
    {
        ulong m = Decompose(x, out int e);
        // Normalize to f = m / 2^52 in [1, 2), x = f * 2^n.
        int lz = BitOperations.LeadingZeroCount(m) - 11;
        m <<= lz;
        e -= lz;
        long n = e + 52;
        BigInteger f = new BigInteger(m) << (w - 52);
        if (f * f > (BigInteger.One << (2 * w + 1)))
        {
            f >>= 1;
            n++;
        }
        return LogKernel(f, w) + n * Ln2(w);
    }

    static BigInteger EvalLog1p(double x, int w, out int e)
    {
        // 1 + x exactly.
        Decompose(x, out int xe);
        int wx = Math.Max(w, -xe);
        BigInteger y = (BigInteger.One << wx) + Fixed(x, wx);
        e = -wx;
        return LogOfFixed(y, wx);
    }

    static BigInteger EvalTrig(Fn fn, double x, int w)
    {
        BigInteger r;
        int quadrant = 0;
        if (Math.Abs(x) < 0.78)
        {
            r = Fixed(x, w);
        }
        else
        {
            ulong m = Decompose(x, out int e);
            int q = w + Math.Max(e + 53, 0) + 8;
            BigInteger big = new BigInteger(m) << (e + q);
            BigInteger halfPi = Pi(q) >> 1;
            BigInteger j = (big + (halfPi >> 1)) / halfPi;
            r = (big - j * halfPi) >> (q - w);
            if (x < 0)
            {
                r = -r;
                j = -j;
            }
            quadrant = (int)(j & 3);
        }
        SinCosKernel(r, w, out BigInteger sin, out BigInteger cos);
        switch (fn)
        {
            case Fn.Sin:
                return quadrant switch { 0 => sin, 1 => cos, 2 => -sin, _ => -cos };
            case Fn.Cos:
                return quadrant switch { 0 => cos, 1 => -sin, 2 => -cos, _ => sin };
            default:
                return (quadrant & 1) == 0 ? (sin << w) / cos : -(cos << w) / sin;
        }
    }

    // Exact integers a, b with a / b = |y| / |x|.
    static void ExactRatio(double y, double x, out BigInteger a, out BigInteger b)
    {
        ulong my = Decompose(y, out int ey);
        ulong mx = Decompose(x, out int ex);
        int emin = Math.Min(ey, ex);
        a = new BigInteger(my) << (ey - emin);
        b = new BigInteger(mx) << (ex - emin);
    }

    static BigInteger EvalAtan(double x, int w)
    {
        ExactRatio(x, 1.0, out BigInteger a, out BigInteger b);
        BigInteger angle = AtanOfRatio(a, b, w);
        return x < 0 ? -angle : angle;
    }

    static BigInteger EvalAtan2(double y, double x, int w)
    {
        ExactRatio(y, x, out BigInteger a, out BigInteger b);
        BigInteger angle = AtanOfRatio(a, b, w);
        if (x < 0) angle = Pi(w) - angle;
        return y < 0 ? -angle : angle;
    }

    // sqrt(1 - x^2) for |x| <= 1, with |x| exact at w bits.
    static BigInteger Complement(BigInteger a, int w) => ISqrt((BigInteger.One << (2 * w)) - a * a);

    static BigInteger EvalAsin(double x, int w)
    {
        BigInteger a = FixedAbs(x, w);
        BigInteger angle = AtanOfRatio(a, Complement(a, w), w);
        return x < 0 ? -angle : angle;
    }

    static BigInteger EvalAcos(double x, int w)
    {
        BigInteger a = FixedAbs(x, w);
        BigInteger angle = AtanOfRatio(Complement(a, w), a, w);
        return x < 0 ? Pi(w) - angle : angle;
    }

    // |x|^y = exp(y log|x|) for finite x != 0, 1 and finite y != 0.
    static BigInteger EvalPow(double x, double y, int w, out int e)
    {
        ulong my = Decompose(y, out int ey);
        int extra = Math.Max(0, Math.ILogB(y) + 1);
        int wl = w + extra + 16;
        BigInteger log = EvalLog(Math.Abs(x), wl);
        BigInteger z = log * new BigInteger(my);
        // z is scaled by 2^-(wl - ey); bring it to w bits.
        int shift = wl - ey - w;
        z = shift >= 0 ? z >> shift : z << -shift;
        if (y < 0) z = -z;
        BigInteger m = ExpReduced(z, w, out BigInteger k);
        e = (int)k - w;
        return m;
    }

    // ---------------------------------------------------------------------
    // cbrt, which is exact integer arithmetic.

    public static double Cbrt(double x)
    {
        ulong m = Decompose(x, out int e);
        // Scale so that the exponent is a multiple of 3 and the root has at
        // least 60 bits: m * 2^e = n * 2^(3 * k).
        int shift = 180 - BitOperations.Log2(m);
        int total = e - shift;
        int adjust = ((total % 3) + 3) % 3;
        shift += adjust;
        total -= adjust;
        BigInteger n = new BigInteger(m) << shift;
        BigInteger c = ICbrt(n);
        int k = total / 3;
        double result = c * c * c == n
            ? ScaledToDouble(c, k)
            // The root lies strictly between c and c + 1, and c has more than
            // 54 bits, so it rounds like c + 1/2.
            : ScaledToDouble(2 * c + 1, k - 1);
        return x < 0 ? -result : result;
    }

    // ---------------------------------------------------------------------
    // pow: exact results.

    // Returns true and the result when |x|^y is exactly representable as a
    // dyadic rational we can compute (the only case where a correctly
    // rounded result can be a rounding midpoint, on which Ziv's strategy
    // would never terminate).
    public static bool TryExactPow(double x, double y, out double result)
    {
        result = 0;
        double ax = Math.Abs(x);
        // Take exact square roots while y is not an integer.
        for (int i = 0; i < 64 && Math.Floor(y) != y; i++)
        {
            double s = Math.Sqrt(ax);
            if (Math.FusedMultiplyAdd(s, s, -ax) != 0) return false;
            ax = s;
            y *= 2;
        }
        if (Math.Floor(y) != y) return false;
        ulong m = Decompose(ax, out int e);
        int tz = BitOperations.TrailingZeroCount(m);
        m >>= tz;
        e += tz;
        if (y < 0)
        {
            // An odd mantissa > 1 has a non-dyadic reciprocal power.
            if (m != 1) return false;
            if (y < -4096) return false;
            result = ScaledToDouble(BigInteger.One, (int)(e * y));
            return true;
        }
        int bits = 64 - BitOperations.LeadingZeroCount(m);
        if (y * bits > 4096) return false;
        if (e * y > 1 << 20 || e * y < -(1 << 20)) return false;
        BigInteger power = BigInteger.Pow(new BigInteger(m), (int)y);
        result = ScaledToDouble(power, (int)(e * y));
        return true;
    }
}
