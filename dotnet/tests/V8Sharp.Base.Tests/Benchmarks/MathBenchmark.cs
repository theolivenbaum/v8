// Stopwatch micro-benchmarks for the Math functions and number conversions.
// Explicit: run with
//   dotnet test -c Release tests/V8Sharp.Base.Tests --filter "FullyQualifiedName~Benchmark" -- xUnit.Explicit=only
// (or `--explicit only` with the xUnit runner) and read the test output.

using System.Diagnostics;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Base.Tests.Benchmarks;

public class MathBenchmark(ITestOutputHelper output)
{
    const int kInputs = 4096;

    static double[] Uniform(int seed, double lo, double hi)
    {
        Random rng = new(seed);
        double[] xs = new double[kInputs];
        for (int i = 0; i < xs.Length; i++) xs[i] = lo + (hi - lo) * rng.NextDouble();
        return xs;
    }

    static double[] LogUniform(int seed, double lo, double hi)
    {
        Random rng = new(seed);
        double[] xs = new double[kInputs];
        for (int i = 0; i < xs.Length; i++) xs[i] = Math.Exp(Math.Log(lo) + (Math.Log(hi) - Math.Log(lo)) * rng.NextDouble());
        return xs;
    }

    // Best of several rounds, in ns per call.
    static double Time(Func<double, double> f, double[] xs)
    {
        double best = double.MaxValue;
        double sink = 0;
        // Warm up long enough for tiered compilation to reach tier 1.
        long warm = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(warm).TotalMilliseconds < 300)
        {
            foreach (double x in xs) sink += f(x);
        }
        for (int round = 0; round < 7; round++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int rep = 0; rep < 20; rep++)
            {
                foreach (double x in xs) sink += f(x);
            }
            double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / (20.0 * xs.Length);
            best = Math.Min(best, ns);
        }
        GC.KeepAlive(sink);
        return best;
    }

    void Row(string name, Func<double, double> ours, Func<double, double> libm, Func<double, double>? previous, double[] xs)
    {
        double a = Time(ours, xs), b = Time(libm, xs);
        string prev = previous is null ? "" : $"  previous {Time(previous, xs),8:F1}";
        output.WriteLine($"{name,-8} ours {a,8:F1} ns  Math {b,8:F1} ns  ratio {a / b,5:F1}{prev}");
    }

    // The double-double path that was the primary path before the
    // table-driven kernels (null when the function has none).
    static Func<double, double>? Previous(Func<double, (bool, double)> tryFast, Func<double, double> slow) =>
        x =>
        {
            var (ok, r) = tryFast(x);
            return ok ? r : slow(x);
        };

    [Fact(Explicit = true)]
    public void MathFunctions()
    {
        double[] trig = Uniform(1, -10, 10);
        double[] exps = Uniform(2, -50, 50);
        double[] logs = LogUniform(3, 1e-10, 1e10);
        double[] unit = Uniform(4, -1, 1);
        double[] small = Uniform(5, -0.9, 10);
        Row("sin", Ieee754.sin, Math.Sin, PreviousTrig(CorrectlyRounded.Fn.Sin), trig);
        Row("cos", Ieee754.cos, Math.Cos, PreviousTrig(CorrectlyRounded.Fn.Cos), trig);
        Row("tan", Ieee754.tan, Math.Tan, PreviousTrig(CorrectlyRounded.Fn.Tan), trig);
        Row("exp", Ieee754.exp, Math.Exp, Previous(x => (CorrectlyRounded.TryExp(x, out double r), r), Ieee754.exp), exps);
        Row("expm1", Ieee754.expm1, x => Math.Exp(x) - 1, Previous(x => (CorrectlyRounded.TryExpm1(x, out double r), r), Ieee754.expm1), exps);
        Row("log", Ieee754.log, Math.Log, Previous(x => (CorrectlyRounded.TryLog(CorrectlyRounded.Fn.Log, x, out double r), r), Ieee754.log), logs);
        Row("log2", Ieee754.log2, Math.Log2, Previous(x => (CorrectlyRounded.TryLog(CorrectlyRounded.Fn.Log2, x, out double r), r), Ieee754.log2), logs);
        Row("log10", Ieee754.log10, Math.Log10, Previous(x => (CorrectlyRounded.TryLog(CorrectlyRounded.Fn.Log10, x, out double r), r), Ieee754.log10), logs);
        Row("log1p", Ieee754.log1p, x => Math.Log(1 + x), Previous(x => (CorrectlyRounded.TryLog1p(x, out double r), r), Ieee754.log1p), small);
        Row("atan", Ieee754.atan, Math.Atan, Previous(x => (CorrectlyRounded.TryAtan(x, out double r), r), Ieee754.atan), trig);
        Row("asin", Ieee754.asin, Math.Asin, Previous(x => (CorrectlyRounded.TryAsin(x, out double r), r), Ieee754.asin), unit);
        Row("acos", Ieee754.acos, Math.Acos, Previous(x => (CorrectlyRounded.TryAcos(x, out double r), r), Ieee754.acos), unit);
        Row("cbrt", Ieee754.cbrt, Math.Cbrt, Previous(x => (CorrectlyRounded.TryCbrt(x, out double r), r), Ieee754.cbrt), exps);
        Row("sinh", Ieee754.sinh, Math.Sinh, null, trig);
        Row("tanh", Ieee754.tanh, Math.Tanh, null, trig);
        double[] ys = Uniform(6, -10, 10);
        int i = 0;
        Row("atan2", x => Ieee754.atan2(ys[i++ & (kInputs - 1)], x), x => Math.Atan2(ys[i++ & (kInputs - 1)], x),
            Previous(x => (CorrectlyRounded.TryAtan2(ys[i++ & (kInputs - 1)], x, out double r), r), x => 0), trig);
        Row("pow", x => InternalMath.pow(x, 1.7), x => Math.Pow(x, 1.7), null, logs);
    }

    static Func<double, double> PreviousTrig(CorrectlyRounded.Fn fn) =>
        Previous(x => (CorrectlyRounded.TryTrig(fn, x, out double r), r), x => 0)!;

    [Fact(Explicit = true)]
    public void NumberConversions()
    {
        Random rng = new(7);
        double[] xs = new double[kInputs];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = (i % 3) switch
            {
                0 => rng.NextDouble(),
                1 => Math.Round(rng.NextDouble() * 1e6, 2),
                _ => BitConverter.Int64BitsToDouble(rng.NextInt64(0x0010000000000000, 0x7FEFFFFFFFFFFFFF)),
            };
        }
        string[] strings = new string[kInputs];
        for (int i = 0; i < xs.Length; i++) strings[i] = Conversions.DoubleToCString(xs[i]);

        double[] indices = new double[kInputs];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        // Digit generation alone: ShortestDecimal (current) against
        // DoubleToAscii SHORTEST (Grisu3 + bignum, the previous path).
        double shortest = Time(x =>
        {
            ShortestDecimal.ToDecimal(x, out ulong f, out int e);
            return (double)f + e;
        }, xs);
        double grisu = Time(x =>
        {
            Span<char> digits = stackalloc char[DoubleConversion.kBase10MaximalLength + 1];
            DoubleConversion.DoubleToAscii(x, DtoaMode.DTOA_SHORTEST, 0, digits, out _, out int length, out int point);
            return length + point;
        }, xs);
        double toString = Time(x => Conversions.DoubleToCString(x).Length, xs);
        double netToString = Time(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length, xs);
        output.WriteLine($"shortest digits  ours {shortest,8:F1} ns  previous (Grisu3) {grisu,8:F1} ns");
        output.WriteLine($"DoubleToCString  ours {toString,8:F1} ns  double.ToString(\"R\") {netToString,8:F1} ns");

        double parse = Time(i => Conversions.StringToDouble(strings[(int)i], ConversionFlag.NoConversionFlag), indices);
        double previousParse = Time(i => Conversions.SlowParseDecimal(strings[(int)i], 0), indices);
        double netParse = Time(i => double.Parse(strings[(int)i], System.Globalization.CultureInfo.InvariantCulture), indices);
        output.WriteLine($"StringToDouble   ours {parse,8:F1} ns  previous (Strtod) {previousParse,8:F1} ns  double.Parse {netParse,8:F1} ns");
    }
}
