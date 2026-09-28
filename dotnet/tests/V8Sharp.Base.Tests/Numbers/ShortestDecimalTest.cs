// ShortestDecimal (Schubfach, standing in for dragonbox) against the port of
// base::DoubleToAscii in SHORTEST mode (Grisu3 with the bignum fallback):
// the digits and the decimal point must be identical.

using V8Sharp.Base.Numbers;

namespace V8Sharp.Base.Tests.Numbers;

public class ShortestDecimalTest
{
    static void Check(double v)
    {
        if (!double.IsFinite(v) || v == 0) return;
        ShortestDecimal.ToDecimal(v, out ulong significand, out int exponent);
        Span<char> digits = stackalloc char[20];
        int length = Conversions.SignificandToChars(significand, digits);

        Span<char> expected = stackalloc char[DoubleConversion.kBase10MaximalLength + 1];
        DoubleConversion.DoubleToAscii(v, DtoaMode.DTOA_SHORTEST, 0, expected, out _, out int expectedLength, out int point);
        Assert.True(expected[..expectedLength].SequenceEqual(digits[..length]) && point == length + exponent,
                    $"{v:R}: {new string(digits[..length])}e{exponent}, DoubleToAscii {new string(expected[..expectedLength])} point {point}");
    }

    [Fact]
    public void RandomBits()
    {
        Random rng = new(1);
        for (int i = 0; i < 1_000_000; i++) Check(BitConverter.Int64BitsToDouble(rng.NextInt64()));
    }

    [Fact]
    public void Subnormals()
    {
        Random rng = new(2);
        for (long bits = 1; bits < 5000; bits++) Check(BitConverter.Int64BitsToDouble(bits));
        for (int i = 0; i < 200000; i++) Check(BitConverter.Int64BitsToDouble(rng.NextInt64(1, 0x0010000000000000)));
    }

    [Fact]
    public void PowersOfTwoAndNeighbours()
    {
        // Powers of two have an asymmetric rounding interval.
        for (int e = -1074; e <= 1023; e++)
        {
            double p = Math.ScaleB(1.0, e);
            Check(p);
            Check(Math.BitIncrement(p));
            Check(Math.BitDecrement(p));
        }
    }

    [Fact]
    public void IntegersAndDecimals()
    {
        Random rng = new(3);
        for (int i = 0; i < 200000; i++)
        {
            Check(rng.NextInt64(1, 1L << 53));
            Check(rng.NextInt64(1, long.MaxValue) * 1024.0);
            Check(Math.Round(rng.NextDouble() * 1e6, rng.Next(0, 10)));
            Check(rng.Next(1, 100000) / Math.Pow(10, rng.Next(1, 25)));
        }
        Check(double.MaxValue);
        Check(double.Epsilon);
        Check(0.1);
        Check(0.3);
        Check(5e-324);
        Check(1.7976931348623157e308);
    }
}
