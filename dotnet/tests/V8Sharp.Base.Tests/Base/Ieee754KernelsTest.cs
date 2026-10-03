// The table-driven kernels (Ieee754.Kernels.cs) against the multiprecision
// reference (Ieee754.CorrectlyRounded.cs): whenever a kernel claims a result,
// it must be the correctly rounded one.

namespace V8Sharp.Base.Tests.Base;

public class Ieee754KernelsTest(ITestOutputHelper output)
{
    delegate bool Kernel(double x, out double result);

    static double[] Inputs(int n, int seed, CorrectlyRounded.Fn fn)
    {
        Random rng = new(seed);
        double[] xs = new double[n];
        for (int i = 0; i < n; i++)
        {
            double u = rng.NextDouble();
            xs[i] = (i % 8) switch
            {
                0 => (u - 0.5) * 20,
                1 => (u - 0.5) * 2,
                2 => BitConverter.Int64BitsToDouble(rng.NextInt64(0, 0x7FF0000000000000)) * (rng.Next(2) == 0 ? 1 : -1),
                3 => u * 1000,
                // Near 1, near 0 and near multiples of pi/2.
                4 => 1 + (u - 0.5) * Math.Pow(2, -rng.Next(1, 60)),
                5 => (u - 0.5) * Math.Pow(2, -rng.Next(1, 40)),
                6 => rng.Next(-2000, 2000) * (Math.PI / 2) * (1 + (u - 0.5) * 1e-15),
                _ => Math.Exp((u - 0.5) * 1400),
            };
            if (fn is CorrectlyRounded.Fn.Asin or CorrectlyRounded.Fn.Acos) xs[i] = Math.IEEERemainder(xs[i], 2) / 2 * 1.999;
        }
        return xs;
    }

    static Kernel Get(CorrectlyRounded.Fn fn) => fn switch
    {
        CorrectlyRounded.Fn.Exp => Ieee754Kernels.TryExp,
        CorrectlyRounded.Fn.Expm1 => Ieee754Kernels.TryExpm1,
        CorrectlyRounded.Fn.Log => (double x, out double r) => Ieee754Kernels.TryLog(CorrectlyRounded.Fn.Log, x, out r),
        CorrectlyRounded.Fn.Log2 => (double x, out double r) => Ieee754Kernels.TryLog(CorrectlyRounded.Fn.Log2, x, out r),
        CorrectlyRounded.Fn.Log10 => (double x, out double r) => Ieee754Kernels.TryLog(CorrectlyRounded.Fn.Log10, x, out r),
        CorrectlyRounded.Fn.Log1p => Ieee754Kernels.TryLog1p,
        CorrectlyRounded.Fn.Sin => Ieee754Kernels.TrySin,
        CorrectlyRounded.Fn.Cos => Ieee754Kernels.TryCos,
        CorrectlyRounded.Fn.Tan => Ieee754Kernels.TryTan,
        CorrectlyRounded.Fn.Atan => Ieee754Kernels.TryAtan,
        CorrectlyRounded.Fn.Asin => Ieee754Kernels.TryAsin,
        CorrectlyRounded.Fn.Acos => Ieee754Kernels.TryAcos,
        _ => throw new ArgumentException(fn.ToString()),
    };

    // Arguments inside each kernel's domain (the public functions filter
    // special values before calling a kernel).
    static bool InDomain(CorrectlyRounded.Fn fn, double x) => double.IsFinite(x) && fn switch
    {
        CorrectlyRounded.Fn.Log or CorrectlyRounded.Fn.Log2 or CorrectlyRounded.Fn.Log10 => x > 0 && x != 1,
        CorrectlyRounded.Fn.Log1p => x > -1 && x != 0 && Math.Abs(x) >= 1e-300,
        CorrectlyRounded.Fn.Exp => x != 0 && x < 710 && x > -746,
        CorrectlyRounded.Fn.Expm1 => Math.Abs(x) >= 8.673617379884035e-19 && x < 710 && x >= -40,
        CorrectlyRounded.Fn.Sin or CorrectlyRounded.Fn.Tan or CorrectlyRounded.Fn.Atan => Math.Abs(x) >= 7.450580596923828e-09,
        CorrectlyRounded.Fn.Cos => Math.Abs(x) >= 7.450580596923828e-09,
        CorrectlyRounded.Fn.Asin => Math.Abs(x) >= 7.450580596923828e-09 && Math.Abs(x) < 1,
        CorrectlyRounded.Fn.Acos => x != 0 && Math.Abs(x) < 1,
        _ => true,
    };

    void Check(CorrectlyRounded.Fn fn, int n, int seed)
    {
        Kernel kernel = Get(fn);
        int claimed = 0, total = 0;
        foreach (double x in Inputs(n, seed, fn))
        {
            if (!InDomain(fn, x)) continue;
            // Trigonometric arguments beyond 1.6e6 go to the slower paths.
            bool large = fn is CorrectlyRounded.Fn.Sin or CorrectlyRounded.Fn.Cos or CorrectlyRounded.Fn.Tan && Math.Abs(x) >= 1.6e6;
            if (!large) total++;
            if (!kernel(x, out double fast)) continue;
            claimed++;
            double reference = CorrectlyRounded.Evaluate(fn, x, 0, fast);
            Assert.True(BitConverter.DoubleToInt64Bits(reference) == BitConverter.DoubleToInt64Bits(fast),
                        $"{fn}({x:R}): kernel {fast:R}, reference {reference:R}");
        }
        output.WriteLine($"{fn}: kernel decided {claimed} of {total}");
        Assert.True(claimed > total * 0.9, $"{fn}: kernel decided only {claimed} of {total}");
    }

    [Theory]
    [InlineData("Exp")]
    [InlineData("Expm1")]
    [InlineData("Log")]
    [InlineData("Log2")]
    [InlineData("Log10")]
    [InlineData("Log1p")]
    [InlineData("Sin")]
    [InlineData("Cos")]
    [InlineData("Tan")]
    [InlineData("Atan")]
    [InlineData("Asin")]
    [InlineData("Acos")]
    public void KernelAgreesWithReference(string name) => Check(Enum.Parse<CorrectlyRounded.Fn>(name), 16000, name.Length);

    [Theory(Explicit = true)]
    [InlineData("Exp")]
    [InlineData("Expm1")]
    [InlineData("Log")]
    [InlineData("Log2")]
    [InlineData("Log10")]
    [InlineData("Log1p")]
    [InlineData("Sin")]
    [InlineData("Cos")]
    [InlineData("Tan")]
    [InlineData("Atan")]
    [InlineData("Asin")]
    [InlineData("Acos")]
    public void KernelAgreesWithReferenceStress(string name) => Check(Enum.Parse<CorrectlyRounded.Fn>(name), 1_000_000, 1000 + name.Length);

    [Fact]
    public void Atan2KernelAgreesWithReference()
    {
        Random rng = new(21);
        int claimed = 0;
        for (int i = 0; i < 16000; i++)
        {
            double y = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-20, 20));
            double x = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-20, 20));
            if (y == 0 || x == 0) continue;
            if (!Ieee754Kernels.TryAtan2(y, x, out double fast)) continue;
            claimed++;
            double reference = CorrectlyRounded.Evaluate(CorrectlyRounded.Fn.Atan2, y, x, fast, y < 0);
            Assert.True(BitConverter.DoubleToInt64Bits(reference) == BitConverter.DoubleToInt64Bits(fast),
                        $"atan2({y:R}, {x:R}): kernel {fast:R}, reference {reference:R}");
        }
        Assert.True(claimed > 15000);
    }
}
