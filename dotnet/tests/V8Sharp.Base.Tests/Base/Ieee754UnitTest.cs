// Port of test/unittests/base/ieee754-unittest.cc.

using static V8Sharp.Base.Ieee754;

namespace V8Sharp.Base.Tests.Base;

public class Ieee754UnitTest
{
    const double kE = 2.718281828459045;
    const double kPI = 3.141592653589793;
    const double kTwo120 = 1.329227995784916e+36;
    const double kInfinity = double.PositiveInfinity;
    const double kQNaN = double.NaN;
    static readonly double kSNaN = BitConverter.UInt64BitsToDouble(0x7FF4000000000000UL);

    static double Div(double a, double b) => a / b;

    static void BitEq(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    // EXPECT_DOUBLE_EQ: within 4 ULPs.
    static void DoubleEq(double expected, double actual)
    {
        long a = BitConverter.DoubleToInt64Bits(expected), b = BitConverter.DoubleToInt64Bits(actual);
        if (a < 0) a = long.MinValue - a;
        if (b < 0) b = long.MinValue - b;
        Assert.True(Math.Abs(a - b) <= 4, $"{expected} != {actual}");
    }

    [Fact]
    public void Acos_()
    {
      Assert.True(double.IsNaN(acos(kInfinity)));
      Assert.True(double.IsNaN(acos(-kInfinity)));
      Assert.True(double.IsNaN(acos(kQNaN)));
      Assert.True(double.IsNaN(acos(kSNaN)));

      Assert.Equal(0.0, acos(1.0));
    }

    [Fact]
    public void Acosh_()
    {
      // Tests for acosh for exceptional values
      Assert.Equal(kInfinity, acosh(kInfinity));
      Assert.True(double.IsNaN(acosh(-kInfinity)));
      Assert.True(double.IsNaN(acosh(kQNaN)));
      Assert.True(double.IsNaN(acosh(kSNaN)));
      Assert.True(double.IsNaN(acosh(0.9)));

      // Test basic acosh functionality
      Assert.Equal(0.0, acosh(1.0));
      // acosh(1.5) = log((sqrt(5)+3)/2), case 1 < x < 2
      Assert.Equal(0.9624236501192069e0, acosh(1.5));
      // acosh(4) = log(sqrt(15)+4), case 2 < x < 2^28
      Assert.Equal(2.0634370688955608e0, acosh(4.0));
      // acosh(2^50), case 2^28 < x
      Assert.Equal(35.35050620855721e0, acosh(1125899906842624.0));
      // acosh(most-positive-float), no overflow
      Assert.Equal(710.4758600739439e0, acosh(1.7976931348623157e308));
    }

    [Fact]
    public void Asin_()
    {
      Assert.True(double.IsNaN(asin(kInfinity)));
      Assert.True(double.IsNaN(asin(-kInfinity)));
      Assert.True(double.IsNaN(asin(kQNaN)));
      Assert.True(double.IsNaN(asin(kSNaN)));

      BitEq(0.0, asin(0.0));
      BitEq(-0.0, asin(-0.0));
    }

    [Fact]
    public void Asinh_()
    {
      // Tests for asinh for exceptional values
      Assert.Equal(kInfinity, asinh(kInfinity));
      Assert.Equal(-kInfinity, asinh(-kInfinity));
      Assert.True(double.IsNaN(asin(kQNaN)));
      Assert.True(double.IsNaN(asin(kSNaN)));

      // Test basic asinh functionality
      BitEq(0.0, asinh(0.0));
      BitEq(-0.0, asinh(-0.0));
      // asinh(2^-29) = 2^-29, case |x| < 2^-28, where acosh(x) = x
      Assert.Equal(1.862645149230957e-9, asinh(1.862645149230957e-9));
      // asinh(-2^-29) = -2^-29, case |x| < 2^-28, where acosh(x) = x
      Assert.Equal(-1.862645149230957e-9, asinh(-1.862645149230957e-9));
      // asinh(2^-28), case 2 > |x| >= 2^-28
      Assert.Equal(3.725290298461914e-9, asinh(3.725290298461914e-9));
      // asinh(-2^-28), case 2 > |x| >= 2^-28
      Assert.Equal(-3.725290298461914e-9, asinh(-3.725290298461914e-9));
      // asinh(1), case 2 > |x| > 2^-28
      Assert.Equal(0.881373587019543e0, asinh(1.0));
      // asinh(-1), case 2 > |x| > 2^-28
      Assert.Equal(-0.881373587019543e0, asinh(-1.0));
      // asinh(5), case 2^28 > |x| > 2
      Assert.Equal(2.3124383412727525e0, asinh(5.0));
      // asinh(-5), case 2^28 > |x| > 2
      Assert.Equal(-2.3124383412727525e0, asinh(-5.0));
      // asinh(2^28), case 2^28 > |x|
      Assert.Equal(20.101268236238415e0, asinh(268435456.0));
      // asinh(-2^28), case 2^28 > |x|
      Assert.Equal(-20.101268236238415e0, asinh(-268435456.0));
      // asinh(<most-positive-float>), no overflow
      Assert.Equal(710.4758600739439e0, asinh(1.7976931348623157e308));
      // asinh(-<most-positive-float>), no overflow
      Assert.Equal(-710.4758600739439e0, asinh(-1.7976931348623157e308));
    }

    [Fact]
    public void Atan_()
    {
      Assert.True(double.IsNaN(atan(kQNaN)));
      Assert.True(double.IsNaN(atan(kSNaN)));
      BitEq(-0.0, atan(-0.0));
      BitEq(0.0, atan(0.0));
      DoubleEq(1.5707963267948966, atan(kInfinity));
      DoubleEq(-1.5707963267948966, atan(-kInfinity));
    }

    [Fact]
    public void Atan2_()
    {
      Assert.True(double.IsNaN(atan2(kQNaN, kQNaN)));
      Assert.True(double.IsNaN(atan2(kQNaN, kSNaN)));
      Assert.True(double.IsNaN(atan2(kSNaN, kQNaN)));
      Assert.True(double.IsNaN(atan2(kSNaN, kSNaN)));
      DoubleEq(0.7853981633974483, atan2(kInfinity, kInfinity));
      DoubleEq(2.356194490192345, atan2(kInfinity, -kInfinity));
      DoubleEq(-0.7853981633974483, atan2(-kInfinity, kInfinity));
      DoubleEq(-2.356194490192345, atan2(-kInfinity, -kInfinity));
    }

    [Fact]
    public void Atanh_()
    {
      Assert.True(double.IsNaN(atanh(kQNaN)));
      Assert.True(double.IsNaN(atanh(kSNaN)));
      Assert.True(double.IsNaN(atanh(kInfinity)));
      Assert.Equal(kInfinity, atanh(1));
      Assert.Equal(-kInfinity, atanh(-1));
      DoubleEq(0.54930614433405478, atanh(0.5));
    }

    [Fact]
    public void Cos_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(cos(kQNaN)));
      Assert.True(double.IsNaN(cos(kSNaN)));
      Assert.True(double.IsNaN(cos(kInfinity)));
      Assert.True(double.IsNaN(cos(-kInfinity)));

      // Tests for cos for |x| < pi/4
      Assert.Equal(1.0, 1 / cos(-0.0));
      Assert.Equal(1.0, 1 / cos(0.0));
      // cos(x) = 1 for |x| < 2^-27
      Assert.Equal(1, cos(2.3283064365386963e-10));
      Assert.Equal(1, cos(-2.3283064365386963e-10));
      // Test KERNELCOS for |x| < 0.3.
      // cos(pi/20) = sqrt(sqrt(2)*sqrt(sqrt(5)+5)+4)/2^(3/2)
      Assert.Equal(0.9876883405951378, cos(0.15707963267948966));
      // Test KERNELCOS for x ~= 0.78125
      Assert.Equal(0.7100335477927638, cos(0.7812504768371582));
      Assert.Equal(0.7100338835660797, cos(0.78125));
      // Test KERNELCOS for |x| > 0.3.
      // cos(pi/8) = sqrt(sqrt(2)+1)/2^(3/4)
      Assert.Equal(0.9238795325112867, cos(0.39269908169872414));
      // Test KERNELTAN for |x| < 0.67434.
      Assert.Equal(0.9238795325112867, cos(-0.39269908169872414));

      // Tests for cos.
      Assert.Equal(1, cos(3.725290298461914e-9));
      // Cover different code paths in KERNELCOS.
      Assert.Equal(0.9689124217106447, cos(0.25));
      Assert.Equal(0.8775825618903728, cos(0.5));
      Assert.Equal(0.7073882691671998, cos(0.785));
      // Test that cos(Math.PI/2) != 0 since Math.PI is not exact.
      Assert.Equal(6.123233995736766e-17, cos(1.5707963267948966));
      // Test cos for various phases.
      Assert.Equal(0.7071067811865474, cos(7.0 / 4 * kPI));
      Assert.Equal(0.7071067811865477, cos(9.0 / 4 * kPI));
      Assert.Equal(-0.7071067811865467, cos(11.0 / 4 * kPI));
      Assert.Equal(-0.7071067811865471, cos(13.0 / 4 * kPI));
      Assert.Equal(0.9367521275331447, cos(1000000.0));
      Assert.Equal(-3.435757038074824e-12, cos(1048575.0 / 2 * kPI));

      // Test Hayne-Panek reduction.
      Assert.Equal(-0.9258790228548379e0, cos(kTwo120));
      Assert.Equal(-0.9258790228548379e0, cos(-kTwo120));
    }

    [Fact]
    public void Sin_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(sin(kQNaN)));
      Assert.True(double.IsNaN(sin(kSNaN)));
      Assert.True(double.IsNaN(sin(kInfinity)));
      Assert.True(double.IsNaN(sin(-kInfinity)));

      // Tests for sin for |x| < pi/4
      Assert.Equal(-kInfinity, Div(1.0, sin(-0.0)));
      Assert.Equal(kInfinity, Div(1.0, sin(0.0)));
      // sin(x) = x for x < 2^-27
      Assert.Equal(2.3283064365386963e-10, sin(2.3283064365386963e-10));
      Assert.Equal(-2.3283064365386963e-10, sin(-2.3283064365386963e-10));
      // sin(pi/8) = sqrt(sqrt(2)-1)/2^(3/4)
      Assert.Equal(0.3826834323650898, sin(0.39269908169872414));
      Assert.Equal(-0.3826834323650898, sin(-0.39269908169872414));

      // Tests for sin.
      Assert.Equal(0.479425538604203, sin(0.5));
      Assert.Equal(-0.479425538604203, sin(-0.5));
      Assert.Equal(1, sin(kPI / 2.0));
      Assert.Equal(-1, sin(-kPI / 2.0));
      // Test that sin(Math.PI) != 0 since Math.PI is not exact.
      Assert.Equal(1.2246467991473532e-16, sin(kPI));
      Assert.Equal(-7.047032979958965e-14, sin(2200.0 * kPI));
      // Test sin for various phases.
      Assert.Equal(-0.7071067811865477, sin(7.0 / 4.0 * kPI));
      Assert.Equal(0.7071067811865474, sin(9.0 / 4.0 * kPI));
      Assert.Equal(0.7071067811865483, sin(11.0 / 4.0 * kPI));
      Assert.Equal(-0.7071067811865479, sin(13.0 / 4.0 * kPI));
      Assert.Equal(-3.2103381051568376e-11, sin(1048576.0 / 4 * kPI));

      // Test Hayne-Panek reduction.
      Assert.Equal(0.377820109360752e0, sin(kTwo120));
      Assert.Equal(-0.377820109360752e0, sin(-kTwo120));
    }

    [Fact]
    public void Cosh_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(cosh(kQNaN)));
      Assert.True(double.IsNaN(cosh(kSNaN)));
      Assert.Equal(kInfinity, cosh(kInfinity));
      Assert.Equal(kInfinity, cosh(-kInfinity));
      Assert.Equal(1, cosh(0.0));
      Assert.Equal(1, cosh(-0.0));
    }

    [Fact]
    public void Exp_()
    {
      Assert.True(double.IsNaN(exp(kQNaN)));
      Assert.True(double.IsNaN(exp(kSNaN)));
      Assert.Equal(0.0, exp(-kInfinity));
      Assert.Equal(0.0, exp(-1000));
      Assert.Equal(0.0, exp(-745.1332191019412));
      Assert.Equal(2.2250738585072626e-308, exp(-708.39641853226408));
      Assert.Equal(3.307553003638408e-308, exp(-708.0));
      Assert.Equal(4.9406564584124654e-324, exp(-7.45133219101941108420e+02));
      Assert.Equal(0.36787944117144233, exp(-1.0));
      Assert.Equal(1.0, exp(-0.0));
      Assert.Equal(1.0, exp(0.0));
      Assert.Equal(1.0, exp(2.2250738585072014e-308));

      // Test that exp(x) is monotonic near 1.
      Assert.True(exp(1.0) >= exp(0.9999999999999999));
      Assert.True(exp(1.0) <= exp(1.0000000000000002));

      // Test that we produce the correctly rounded result for 1.
      Assert.Equal(kE, exp(1.0));

      Assert.Equal(7.38905609893065e0, exp(2.0));
      Assert.Equal(1.7976931348622732e308, exp(7.09782712893383973096e+02));
      Assert.Equal(2.6881171418161356e+43, exp(100.0));
      Assert.Equal(8.218407461554972e+307, exp(709.0));
      Assert.Equal(1.7968190737295725e308, exp(709.7822265625e0));
      Assert.Equal(kInfinity, exp(709.7827128933841e0));
      Assert.Equal(kInfinity, exp(710.0));
      Assert.Equal(kInfinity, exp(1000.0));
      Assert.Equal(kInfinity, exp(kInfinity));
    }

    [Fact]
    public void Expm1_()
    {
      Assert.True(double.IsNaN(expm1(kQNaN)));
      Assert.True(double.IsNaN(expm1(kSNaN)));
      Assert.Equal(-1.0, expm1(-kInfinity));
      Assert.Equal(kInfinity, expm1(kInfinity));
      Assert.Equal(0.0, expm1(-0.0));
      Assert.Equal(0.0, expm1(0.0));
      Assert.Equal(1.7182818284590453, expm1(1.0));
      Assert.Equal(2.6881171418161356e+43, expm1(100.0));
      Assert.Equal(8.218407461554972e+307, expm1(709.0));
      Assert.Equal(kInfinity, expm1(710.0));
    }

    [Fact]
    public void Log_()
    {
      Assert.True(double.IsNaN(log(kQNaN)));
      Assert.True(double.IsNaN(log(kSNaN)));
      Assert.True(double.IsNaN(log(-kInfinity)));
      Assert.True(double.IsNaN(log(-1.0)));
      Assert.Equal(-kInfinity, log(-0.0));
      Assert.Equal(-kInfinity, log(0.0));
      Assert.Equal(0.0, log(1.0));
      Assert.Equal(kInfinity, log(kInfinity));

      // Test that log(E) produces the correctly rounded result.
      Assert.Equal(1.0, log(kE));
    }

    [Fact]
    public void Log1p_()
    {
      Assert.True(double.IsNaN(log1p(kQNaN)));
      Assert.True(double.IsNaN(log1p(kSNaN)));
      Assert.True(double.IsNaN(log1p(-kInfinity)));
      Assert.Equal(-kInfinity, log1p(-1.0));
      Assert.Equal(0.0, log1p(0.0));
      Assert.Equal(-0.0, log1p(-0.0));
      Assert.Equal(kInfinity, log1p(kInfinity));
      Assert.Equal(6.9756137364252422e-03, log1p(0.007));
      Assert.Equal(709.782712893384, log1p(1.7976931348623157e308));
      Assert.Equal(2.7755575615628914e-17, log1p(2.7755575615628914e-17));
      Assert.Equal(9.313225741817976e-10, log1p(9.313225746154785e-10));
      Assert.Equal(-0.2876820724517809, log1p(-0.25));
      Assert.Equal(0.22314355131420976, log1p(0.25));
      Assert.Equal(2.3978952727983707, log1p(10));
      Assert.Equal(36.841361487904734, log1p(10e15));
      Assert.Equal(37.08337388996168, log1p(12738099905822720));
      Assert.Equal(37.08336444902049, log1p(12737979646738432));
      Assert.Equal(1.3862943611198906, log1p(3));
      Assert.Equal(1.3862945995384413, log1p(3 + 9.5367431640625e-7));
      Assert.Equal(0.5596157879354227, log1p(0.75));
      Assert.Equal(0.8109302162163288, log1p(1.25));
    }

    [Fact]
    public void Log2_()
    {
      Assert.True(double.IsNaN(log2(kQNaN)));
      Assert.True(double.IsNaN(log2(kSNaN)));
      Assert.True(double.IsNaN(log2(-kInfinity)));
      Assert.True(double.IsNaN(log2(-1.0)));
      Assert.Equal(-kInfinity, log2(0.0));
      Assert.Equal(-kInfinity, log2(-0.0));
      Assert.Equal(kInfinity, log2(kInfinity));
    }

    [Fact]
    public void Log10_()
    {
      Assert.True(double.IsNaN(log10(kQNaN)));
      Assert.True(double.IsNaN(log10(kSNaN)));
      Assert.True(double.IsNaN(log10(-kInfinity)));
      Assert.True(double.IsNaN(log10(-1.0)));
      Assert.Equal(-kInfinity, log10(0.0));
      Assert.Equal(-kInfinity, log10(-0.0));
      Assert.Equal(kInfinity, log10(kInfinity));
      Assert.Equal(3.0, log10(1000.0));
      Assert.Equal(14.0, log10(100000000000000));  // log10(10 ^ 14)
      Assert.Equal(3.7389561269540406, log10(5482.2158));
      Assert.Equal(14.661551142893833, log10(458723662312872.125782332587));
      Assert.Equal(-0.9083828622192334, log10(0.12348583358871));
      Assert.Equal(5.0, log10(100000.0));
    }

    [Fact]
    public void Cbrt_()
    {
      Assert.True(double.IsNaN(cbrt(kQNaN)));
      Assert.True(double.IsNaN(cbrt(kSNaN)));
      Assert.Equal(kInfinity, cbrt(kInfinity));
      Assert.Equal(-kInfinity, cbrt(-kInfinity));
      Assert.Equal(1.4422495703074083, cbrt(3));
      Assert.Equal(100, cbrt(100 * 100 * 100));
      Assert.Equal(46.415888336127786, cbrt(100000));
    }

    [Fact]
    public void Sinh_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(sinh(kQNaN)));
      Assert.True(double.IsNaN(sinh(kSNaN)));
      Assert.Equal(kInfinity, sinh(kInfinity));
      Assert.Equal(-kInfinity, sinh(-kInfinity));
      Assert.Equal(0.0, sinh(0.0));
      Assert.Equal(-0.0, sinh(-0.0));
    }

    [Fact]
    public void Tan_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(tan(kQNaN)));
      Assert.True(double.IsNaN(tan(kSNaN)));
      Assert.True(double.IsNaN(tan(kInfinity)));
      Assert.True(double.IsNaN(tan(-kInfinity)));

      // Tests for tan for |x| < pi/4
      Assert.Equal(kInfinity, Div(1.0, tan(0.0)));
      Assert.Equal(-kInfinity, Div(1.0, tan(-0.0)));
      // tan(x) = x for |x| < 2^-28
      Assert.Equal(2.3283064365386963e-10, tan(2.3283064365386963e-10));
      Assert.Equal(-2.3283064365386963e-10, tan(-2.3283064365386963e-10));
      // Test KERNELTAN for |x| > 0.67434.
      Assert.Equal(0.8211418015898941, tan(11.0 / 16.0));
      Assert.Equal(-0.8211418015898941, tan(-11.0 / 16.0));
      Assert.Equal(0.41421356237309503, tan(0.39269908169872414));
      // crbug/427468
      Assert.Equal(0.7993357819992383, tan(0.6743358));

      // Tests for tan.
      Assert.Equal(3.725290298461914e-9, tan(3.725290298461914e-9));
      // Test that tan(PI/2) != Infinity since PI is not exact.
      Assert.Equal(1.633123935319537e16, tan(kPI / 2));
      // Cover different code paths in KERNELTAN (tangent and cotangent)
      Assert.Equal(0.5463024898437905, tan(0.5));
      Assert.Equal(2.0000000000000027, tan(1.107148717794091));
      Assert.Equal(-1.0000000000000004, tan(7.0 / 4.0 * kPI));
      Assert.Equal(0.9999999999999994, tan(9.0 / 4.0 * kPI));
      Assert.Equal(-6.420676210313675e-11, tan(1048576.0 / 2.0 * kPI));
      Assert.Equal(2.910566692924059e11, tan(1048575.0 / 2.0 * kPI));

      // Test Hayne-Panek reduction.
      Assert.Equal(-0.40806638884180424e0, tan(kTwo120));
      Assert.Equal(0.40806638884180424e0, tan(-kTwo120));
    }

    [Fact]
    public void Tanh_()
    {
      // Test values mentioned in the ECMAScript spec.
      Assert.True(double.IsNaN(tanh(kQNaN)));
      Assert.True(double.IsNaN(tanh(kSNaN)));
      Assert.Equal(1, tanh(kInfinity));
      Assert.Equal(-1, tanh(-kInfinity));
      Assert.Equal(0.0, tanh(0.0));
      Assert.Equal(-0.0, tanh(-0.0));
    }
}
