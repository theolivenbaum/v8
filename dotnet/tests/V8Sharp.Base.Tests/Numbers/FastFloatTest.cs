// The Clinger / Eisel-Lemire decimal conversion (FastFloat.cs) against the
// port of base::Strtod over all digits, which is the reference it must
// reproduce bit for bit.

using System.Globalization;
using System.Numerics;
using System.Text;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Base.Tests.Numbers;

public class FastFloatTest
{
    static void Check(string s)
    {
        double fast = Conversions.StringToDouble(s, ConversionFlag.NoConversionFlag);
        double slow = Conversions.SlowParseDecimal(s, 0);
        Assert.True(BitConverter.DoubleToInt64Bits(fast) == BitConverter.DoubleToInt64Bits(slow),
                    $"\"{s}\": {fast:R}, Strtod {slow:R}");
    }

    // The exact decimal expansion of the midpoint between d and its successor.
    static string Midpoint(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        long exponent = (bits >> 52) & 0x7FF;
        BigInteger mantissa = bits & 0xFFFFFFFFFFFFF;
        if (exponent == 0) exponent = 1; else mantissa += 1L << 52;
        // d + ulp/2 = (2m + 1) * 2^(e - 1076).
        BigInteger m = 2 * mantissa + 1;
        int e = (int)exponent - 1076;
        BigInteger digits;
        int decimalExponent;
        if (e >= 0)
        {
            digits = m << e;
            decimalExponent = 0;
        }
        else
        {
            digits = m * BigInteger.Pow(5, -e);
            decimalExponent = e;
        }
        return digits.ToString(CultureInfo.InvariantCulture) + "e" + decimalExponent.ToString(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void RandomShortestRepresentations()
    {
        Random rng = new(1);
        for (int i = 0; i < 200000; i++)
        {
            double d = BitConverter.Int64BitsToDouble(rng.NextInt64(0, 0x7FF0000000000000));
            Check(Conversions.DoubleToCString(d));
        }
    }

    [Fact]
    public void RandomDigitStrings()
    {
        Random rng = new(2);
        StringBuilder sb = new();
        for (int i = 0; i < 200000; i++)
        {
            sb.Clear();
            int digits = rng.Next(1, i % 3 == 0 ? 40 : 20);
            for (int k = 0; k < digits; k++) sb.Append((char)('0' + rng.Next(10)));
            if (rng.Next(3) == 0) sb.Insert(rng.Next(sb.Length + 1), '.');
            sb.Append('e').Append(rng.Next(-360, 330));
            Check(sb.ToString());
        }
    }

    [Fact]
    public void Midpoints()
    {
        Random rng = new(3);
        for (int i = 0; i < 20000; i++)
        {
            double d = BitConverter.Int64BitsToDouble(rng.NextInt64(0, 0x7FEFFFFFFFFFFFFF));
            string mid = Midpoint(d);
            Check(mid);
            // Just above and below: change the last significant digit.
            int e = mid.IndexOf('e');
            string mantissa = mid[..e], suffix = mid[e..];
            Check(mantissa + "1" + suffix[0] + (int.Parse(suffix[1..], CultureInfo.InvariantCulture) - 1));
            if (mantissa.Length > 1 && mantissa[^1] != '0')
            {
                Check(mantissa[..^1] + (char)(mantissa[^1] - 1) + suffix);
            }
        }
    }

    [Fact]
    public void Boundaries()
    {
        string[] cases =
        [
            "0", "0.0", "1", "9007199254740992", "9007199254740993", "9007199254740995",
            "1e22", "1e23", "123456789012345678e10", "4.9406564584124654e-324", "2.4703282292062327e-324",
            "2.4703282292062328e-324", "2.2250738585072011e-308", "2.2250738585072014e-308",
            "1.7976931348623157e308", "1.7976931348623158e308", "1.7976931348623159e308",
            "179769313486231580793728971405301e276", "1e-400", "1e400", "0.000001", "1e-7",
            "18446744073709551615", "18446744073709551616", "9999999999999999999", "10000000000000000000",
            "0.1", "0.2", "0.3", "3.14159265358979323846264338327950288", "7.2057594037927933e16",
        ];
        foreach (string s in cases) Check(s);
    }
}
