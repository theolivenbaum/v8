// Tests of the correctly rounded functions that stand in for llvm-libc in
// src/base/ieee754.cc (not a port of a V8 test file).

using V8Sharp.Base.Numbers;

namespace V8Sharp.Base.Tests.Base;

public class Ieee754CorrectRoundingTest
{
    static Func<double, double> Function(string name) => name switch
    {
        "exp" => Ieee754.exp,
        "expm1" => Ieee754.expm1,
        "log" => Ieee754.log,
        "log1p" => Ieee754.log1p,
        "log2" => Ieee754.log2,
        "log10" => Ieee754.log10,
        "sin" => Ieee754.sin,
        "cos" => Ieee754.cos,
        "tan" => Ieee754.tan,
        "atan" => Ieee754.atan,
        "asin" => Ieee754.asin,
        "acos" => Ieee754.acos,
        "cbrt" => Ieee754.cbrt,
        _ => throw new ArgumentException(name),
    };

    // Arguments where glibc's result is not the correctly rounded one. The
    // expected values are the correctly rounded results of a 130-digit
    // evaluation with Python's decimal module.
    static readonly (string fn, double x, double expected)[] s_hardCases =
    [
        ("exp", 2.7924691852147077, 16.321270327714913),
        ("expm1", 0.44679640114623886, 0.5632959816130151),
        ("expm1", -0.20470159650067876, -0.18510955370505575),
        ("expm1", 2.9338585273054707, 17.80003116045369),
        ("expm1", 568.0101013593422, 4.826726442974763e+246),
        ("expm1", 0.9213017485669357, 1.5125589820864305),
        ("expm1", 0.8058901214068244, 1.2386883166024665),
        ("expm1", 0.5028534762108947, 0.6534325762268399),
        ("expm1", 120.2716893145217, 1.7113191037422135e+52),
        ("log", 8.704092380918604, 2.1637933036738306),
        ("log1p", -0.5489938117326209, -0.7962742183598926),
        ("log1p", 7.838040766230803, 2.179065219306057),
        ("log1p", 5.229617876573287, 1.8293149948946816),
        ("log1p", -0.3000670100096925, -0.3567706771062808),
        ("log1p", 0.25706122781013185, 0.22877663789688765),
        ("log1p", 4.015368313535754, 1.61250686109715),
        ("log1p", 370.16828747939707, 5.9166555648255175),
        ("log1p", 0.56018847486013, 0.44480663118166547),
        ("log2", 0.9668060848334832, -0.048701541796831456),
        ("log10", 217.65313261079282, 2.337764922197717),
        ("log10", 9.388765124319479, 0.9726084745918538),
        ("log10", 7.235403664985394, 0.8594627655290471),
        ("log10", 0.5367379670668104, -0.2702376830725483),
        ("log10", 0.56018847486013, -0.25166583045735796),
        ("log10", 0.7192842153456458, -0.14309947016425284),
        ("log10", 6.806810999664856, 0.8329436919969627),
        ("log10", 0.4097179432444824, -0.38751501607754496),
        ("sin", -5.746774867059092, 0.5110538892916879),
        ("sin", 7.273971763101392, 0.8364572403997063),
        ("sin", -9.79869564054473, 0.3652652075353601),
        ("sin", -8.57530987755177, -0.7509292427635234),
        ("cos", 1.548920528875934e+302, 0.7881252269240601),
        ("tan", 4.464315685659795, 3.9480343550535824),
        ("tan", 4.738480645695577e+37, -3.288086087411434),
        ("tan", -3.9172680275129466, -0.98074106475451),
        ("tan", -2.935347693476522, 0.20921993513129164),
        ("atan", 0.4669009621566631, 0.43681953847006116),
        ("atan", 0.09589528343449127, 0.09560294674312314),
        ("atan", -0.6755301252359198, -0.5941137679899343),
        ("atan", 2.8820097972089465, 1.2368143853792488),
        ("asin", 0.8014375259175139, 0.9296949359295693),
        ("asin", 0.13141359441513822, 0.13179480631446674),
        ("cbrt", -7.8213249602454376, -1.984998167213415),
        ("cbrt", -2.5922444879926486e+159, -1.373700279349233e+53),
        ("cbrt", 0.7158714475649741, 0.8945645427253371),
        ("cbrt", -1.8123766322057105e-88, -5.6591275778343476e-30),
        ("cbrt", -7.272303186949483, -1.9374209526748027),
        ("cbrt", 28.80829248056202, 3.0655318960681543),
        ("cbrt", 0.6234188599621033, 0.8542663747745188),
        ("cbrt", -8.931455313661814, -2.0747896676463147),
    ];

    [Fact]
    public void HardCases()
    {
        foreach (var (fn, x, expected) in s_hardCases)
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(Function(fn)(x)),
                        $"{fn}({x:R}) = {Function(fn)(x):R}, expected {expected:R}");
    }

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

    // The double-double fast path must agree with the multiprecision
    // evaluation it short-cuts.
    [Theory]
    [InlineData("Exp")]
    [InlineData("Expm1")]
    [InlineData("Log")]
    [InlineData("Log1p")]
    [InlineData("Log2")]
    [InlineData("Log10")]
    [InlineData("Sin")]
    [InlineData("Cos")]
    [InlineData("Tan")]
    [InlineData("Atan")]
    [InlineData("Asin")]
    [InlineData("Acos")]
    public void FastPathAgreesWithMultiprecision(string name)
    {
        CorrectlyRounded.Fn fn = Enum.Parse<CorrectlyRounded.Fn>(name);
        Func<double, double> f = Function(name.ToLowerInvariant());
        foreach (double x in Inputs(4000, (int)fn))
        {
            double fast = f(x);
            if (!double.IsFinite(fast) || x == 0 || fast == 0) continue;
            if (fn is CorrectlyRounded.Fn.Exp or CorrectlyRounded.Fn.Expm1 && Math.Abs(x) > 700) continue;
            double slow = CorrectlyRounded.Evaluate(fn, x, 0, fast);
            Assert.True(BitConverter.DoubleToInt64Bits(slow) == BitConverter.DoubleToInt64Bits(fast),
                        $"{fn}({x:R}): fast {fast:R}, multiprecision {slow:R}");
        }
    }

    [Fact]
    public void Atan2AndCbrtFastPathsAgree()
    {
        double[] xs = Inputs(4000, 77);
        for (int i = 0; i < xs.Length; i++)
        {
            double y = xs[i], x = xs[(i * 13 + 5) % xs.Length];
            if (y == 0 || x == 0) continue;
            double fast = Ieee754.atan2(y, x);
            double slow = CorrectlyRounded.Evaluate(CorrectlyRounded.Fn.Atan2, y, x, fast, y < 0);
            Assert.Equal(BitConverter.DoubleToInt64Bits(slow), BitConverter.DoubleToInt64Bits(fast));
            Assert.Equal(BitConverter.DoubleToInt64Bits(CorrectlyRounded.Cbrt(y)), BitConverter.DoubleToInt64Bits(Ieee754.cbrt(y)));
        }
    }

    [Fact]
    public void LegacyPow()
    {
        // 3^34 has 54 significant bits and is odd: a rounding midpoint, which
        // ties to the even neighbour.
        Assert.Equal(16677181699666568.0, Ieee754.Legacy.pow(3, 34));
        Assert.Equal(16677181699666568.0, Ieee754.Legacy.pow(81, 8.5));
        Assert.Equal(0.125, Ieee754.Legacy.pow(2, -3));
        Assert.Equal(3.0, Ieee754.Legacy.pow(9, 0.5));
        Assert.Equal(1.0, Ieee754.Legacy.pow(double.NaN, 0));
        Assert.Equal(1.0, Ieee754.Legacy.pow(1, double.NaN));
        Assert.Equal(1.0, Ieee754.Legacy.pow(-1, double.PositiveInfinity));
        Assert.True(double.IsNaN(Ieee754.Legacy.pow(-2, 0.5)));
        Assert.Equal(double.NegativeInfinity, Ieee754.Legacy.pow(-0.0, -3));
        Assert.Equal(double.PositiveInfinity, Ieee754.Legacy.pow(-0.0, -2));
        Assert.Equal(-0.0, Ieee754.Legacy.pow(double.NegativeInfinity, -3));
        Assert.Equal(double.PositiveInfinity, Ieee754.Legacy.pow(10, 400));
        Assert.Equal(0.0, Ieee754.Legacy.pow(10, -400));
        // Within one ulp of the platform's pow, which is itself within one ulp.
        Random rng = new(11);
        for (int i = 0; i < 2000; i++)
        {
            double b = rng.NextDouble() * 10, e = (rng.NextDouble() - 0.5) * 40;
            double ours = Ieee754.Legacy.pow(b, e), libm = Math.Pow(b, e);
            Assert.True(Math.Abs(BitConverter.DoubleToInt64Bits(ours) - BitConverter.DoubleToInt64Bits(libm)) <= 1,
                        $"pow({b:R}, {e:R}) = {ours:R}, platform {libm:R}");
        }
    }

    [Fact]
    public void InternalMathPow()
    {
        Assert.True(double.IsNaN(InternalMath.pow(1, double.NaN)));
        Assert.True(double.IsNaN(InternalMath.pow(-1, double.PositiveInfinity)));
        Assert.True(double.IsNaN(InternalMath.pow(1, double.NegativeInfinity)));
        Assert.Equal(0.0, InternalMath.pow(-0.0, 0.5));
        Assert.False(double.IsNegative(InternalMath.pow(-0.0, 0.5)));
        Assert.Equal(double.PositiveInfinity, InternalMath.pow(double.NegativeInfinity, 0.5));
        Assert.Equal(1e300 * 1e300, InternalMath.pow(1e300, 2));
        Assert.Equal(Math.Pow(2.5, 3.5), InternalMath.pow(2.5, 3.5));
        InternalMath.UseStdMathPow = false;
        try
        {
            Assert.Equal(16677181699666568.0, InternalMath.pow(3, 34));
        }
        finally
        {
            InternalMath.UseStdMathPow = true;
        }
    }
}
