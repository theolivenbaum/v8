// Port of src/numbers/ieee754.{h,cc}: v8::internal::math::pow, the
// exponentiation behind Math.pow and **.

namespace V8Sharp.Base.Numbers;

public static class InternalMath
{
    /// <summary>--use-std-math-pow (true except on AIX): use the platform's
    /// pow instead of base::ieee754::legacy::pow.</summary>
    public static bool UseStdMathPow { get; set; } = true;

    public static double pow(double x, double y)
    {
        if (double.IsNaN(y))
        {
            // 1. If exponent is NaN, return NaN.
            return double.NaN;
        }
        if (double.IsInfinity(y) && (x == 1 || x == -1))
        {
            // 9. If exponent is +inf, then
            //   b. If abs(R(base)) = 1, return NaN.
            // and
            // 10. If exponent is -inf, then
            //   b. If abs(R(base)) = 1, return NaN.
            return double.NaN;
        }
        if (double.IsNaN(x))
        {
            // libm pow distinguishes between quiet and signaling NaN; JS doesn't.
            x = double.NaN;
        }

        // The following special cases just exist to match the optimizing compilers'
        // behavior, which avoid calls to `pow` in those cases.
        if (y == 2)
        {
            // x ** 2   ==>   x * x
            return x * x;
        }
        else if (y == 0.5)
        {
            // x ** 0.5   ==>  sqrt(x), except if x is -Infinity
            if (double.IsInfinity(x))
            {
                return double.PositiveInfinity;
            }
            else
            {
                // Note the +0 so that we get +0 for -0**0.5 rather than -0.
                return Math.Sqrt(x + 0);
            }
        }

        if (UseStdMathPow)
        {
            // std::pow: Math.Pow calls the same C library function.
            return Math.Pow(x, y);
        }
        return Ieee754.Legacy.pow(x, y);
    }
}
