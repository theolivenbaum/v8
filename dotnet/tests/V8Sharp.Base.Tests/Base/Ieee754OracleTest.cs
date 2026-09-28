// Compares the Math functions with the oracle. The oracle is V8 14.7, which
// still computed most of them with fdlibm (errors below one ulp), while this
// tree takes them from llvm-libc (correctly rounded). So the correctly rounded
// functions may differ from the oracle by one ulp, the fdlibm formulas built
// on them (sinh, cosh, asinh, acosh, atanh) by a few ulps. tanh is
// std::tanh in this tree, the platform's, and each of that and the oracle's
// fdlibm tanh is within one ulp, so they differ by up to two; pow
// goes through std::pow in both and must match exactly.

using V8Sharp.Base.Numbers;
using V8Sharp.Oracle;

namespace V8Sharp.Base.Tests.Base;

public class Ieee754OracleTest
{
    static double[] Inputs(int n, int seed)
    {
        Random rng = new(seed);
        double[] xs = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = (i % 4) switch
            {
                0 => (rng.NextDouble() - 0.5) * 20,
                1 => (rng.NextDouble() - 0.5) * 2,
                2 => BitConverter.Int64BitsToDouble(rng.NextInt64(0, 0x7FF0000000000000)) * (rng.Next(2) == 0 ? 1 : -1),
                _ => rng.NextDouble() * 1000,
            };
        }
        return xs;
    }

    // Runs Math.<fn> over the arguments in the oracle; returns the bit patterns.
    static long[] Oracle(string expression, double[] xs, double[]? ys = null)
    {
        static string Array(double[] v) => string.Join(",", v.Select(x => BitConverter.DoubleToInt64Bits(x) + "n"));
        string js = "{ const x = new Float64Array(new BigInt64Array([" + Array(xs) + "]).buffer);" +
                    (ys is null ? "" : " const y = new Float64Array(new BigInt64Array([" + Array(ys) + "]).buffer);") +
                    " const out = new Float64Array(x.length);" +
                    " for (let i = 0; i < x.length; i++) out[i] = " + expression + ";" +
                    " print(Array.from(new BigInt64Array(out.buffer)).join(',')); }";
        using ReferenceV8 v8 = new(allowNativesSyntax: false);
        return v8.Run(js).Trim().Split(',').Select(long.Parse).ToArray();
    }

    static long UlpDistance(double a, long bBits)
    {
        long aBits = BitConverter.DoubleToInt64Bits(a);
        if (double.IsNaN(a) && double.IsNaN(BitConverter.Int64BitsToDouble(bBits))) return 0;
        if (aBits < 0) aBits = long.MinValue - aBits;
        if (bBits < 0) bBits = long.MinValue - bBits;
        return Math.Abs(aBits - bBits);
    }

    [Theory]
    [InlineData("sin", 1)]
    [InlineData("cos", 1)]
    [InlineData("tan", 1)]
    [InlineData("exp", 1)]
    [InlineData("expm1", 1)]
    [InlineData("log", 1)]
    [InlineData("log1p", 1)]
    [InlineData("log2", 1)]
    [InlineData("log10", 2)]
    [InlineData("cbrt", 1)]
    [InlineData("atan", 1)]
    [InlineData("asin", 1)]
    [InlineData("acos", 1)]
    [InlineData("sinh", 3)]
    [InlineData("cosh", 3)]
    [InlineData("asinh", 3)]
    [InlineData("acosh", 3)]
    [InlineData("atanh", 3)]
    [InlineData("tanh", 2)]
    public void MatchesOracleWithinUlps(string fn, int ulps)
    {
        Func<double, double> f = fn switch
        {
            "sin" => Ieee754.sin, "cos" => Ieee754.cos, "tan" => Ieee754.tan, "exp" => Ieee754.exp,
            "expm1" => Ieee754.expm1, "log" => Ieee754.log, "log1p" => Ieee754.log1p, "log2" => Ieee754.log2,
            "log10" => Ieee754.log10, "cbrt" => Ieee754.cbrt, "atan" => Ieee754.atan, "asin" => Ieee754.asin,
            "acos" => Ieee754.acos, "sinh" => Ieee754.sinh, "cosh" => Ieee754.cosh, "asinh" => Ieee754.asinh,
            "acosh" => Ieee754.acosh, "atanh" => Ieee754.atanh, _ => Ieee754.tanh,
        };
        double[] xs = Inputs(4000, fn.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        long[] expected = Oracle("Math." + fn + "(x[i])", xs);
        for (int i = 0; i < xs.Length; i++)
        {
            double actual = f(xs[i]);
            Assert.True(UlpDistance(actual, expected[i]) <= ulps,
                        $"Math.{fn}({xs[i]:R}) = {actual:R}, V8 {BitConverter.Int64BitsToDouble(expected[i]):R}");
        }
    }

    [Fact]
    public void Atan2MatchesOracleWithinOneUlp()
    {
        double[] ys = Inputs(4000, 5), xs = Inputs(4000, 6);
        long[] expected = Oracle("Math.atan2(y[i], x[i])", xs, ys);
        for (int i = 0; i < xs.Length; i++)
        {
            double actual = Ieee754.atan2(ys[i], xs[i]);
            Assert.True(UlpDistance(actual, expected[i]) <= 1, $"Math.atan2({ys[i]:R}, {xs[i]:R}) = {actual:R}");
        }
    }

    [Fact]
    public void PowMatchesOracle()
    {
        Random rng = new(9);
        double[] xs = new double[4000], ys = new double[4000];
        double[] specials = [0, -0.0, 1, -1, 2, 0.5, -0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 3, -3];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = i < 144 ? specials[i % 12] : (rng.NextDouble() - 0.3) * 20;
            ys[i] = i < 144 ? specials[i / 12] : (rng.NextDouble() - 0.5) * 60;
        }
        long[] expected = Oracle("Math.pow(x[i], y[i])", xs, ys);
        for (int i = 0; i < xs.Length; i++)
        {
            double actual = InternalMath.pow(xs[i], ys[i]);
            Assert.True(UlpDistance(actual, expected[i]) == 0, $"Math.pow({xs[i]:R}, {ys[i]:R}) = {actual:R}");
        }
    }
}
