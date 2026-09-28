// Port of test/unittests/numbers/conversions-unittest.cc.
// Tests that take heap objects (Smi/HeapNumber) run over the number's value.

using static V8Sharp.Base.Numbers.Conversions;
using static V8Sharp.Base.Tests.V8Check;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Base.Tests.Numbers;

public class ConversionsTest
{
    const ConversionFlag NO_CONVERSION_FLAG = ConversionFlag.NoConversionFlag;
    const ConversionFlag ALLOW_NON_DECIMAL_PREFIX = ConversionFlag.AllowNonDecimalPrefix;
    const ConversionFlag ALLOW_TRAILING_JUNK = ConversionFlag.AllowTrailingJunk;

    static void CheckNonArrayIndex(bool expected, string chars) => CHECK_EQ(expected, IsSpecialIndex(chars));

    [Fact]
    public void Hex()
    {
        CHECK_EQ(0.0, StringToDouble("0x0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0X0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(1.0, StringToDouble("0x1", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(16.0, StringToDouble("0x10", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(255.0, StringToDouble("0xFF", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(175.0, StringToDouble("0xAF", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(0.0, HexStringToDouble("0x0"));
        CHECK_EQ(0.0, HexStringToDouble("0X0"));
        CHECK_EQ(1.0, HexStringToDouble("0x1"));
        CHECK_EQ(16.0, HexStringToDouble("0x10"));
        CHECK_EQ(255.0, HexStringToDouble("0xFF"));
        CHECK_EQ(175.0, HexStringToDouble("0xAF"));
    }

    [Fact]
    public void Octal()
    {
        CHECK_EQ(0.0, StringToDouble("0o0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0O0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(1.0, StringToDouble("0o1", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(7.0, StringToDouble("0o7", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(8.0, StringToDouble("0o10", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(63.0, StringToDouble("0o77", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(0.0, OctalStringToDouble("0o0"));
        CHECK_EQ(0.0, OctalStringToDouble("0O0"));
        CHECK_EQ(1.0, OctalStringToDouble("0o1"));
        CHECK_EQ(7.0, OctalStringToDouble("0o7"));
        CHECK_EQ(8.0, OctalStringToDouble("0o10"));
        CHECK_EQ(63.0, OctalStringToDouble("0o77"));

        const double x = 1073741824;  // 010000000000: Power of 2, no rounding errors.
        CHECK_EQ(x * x * x * x * x,
                 OctalStringToDouble("0o01" + "0000000000" + "0000000000" + "0000000000" + "0000000000" + "0000000000"));
    }

    [Fact]
    public void ImplicitOctal()
    {
        CHECK_EQ(0.0, ImplicitOctalStringToDouble("0"));
        CHECK_EQ(0.0, ImplicitOctalStringToDouble("00"));
        CHECK_EQ(1.0, ImplicitOctalStringToDouble("01"));
        CHECK_EQ(7.0, ImplicitOctalStringToDouble("07"));
        CHECK_EQ(8.0, ImplicitOctalStringToDouble("010"));
        CHECK_EQ(63.0, ImplicitOctalStringToDouble("077"));

        CHECK_EQ(0.0, StringToDouble("0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("00", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(1.0, StringToDouble("01", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(7.0, StringToDouble("07", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(10.0, StringToDouble("010", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(77.0, StringToDouble("077", ALLOW_NON_DECIMAL_PREFIX));

        const double x = 1073741824;  // 010000000000: Power of 2, no rounding errors.
        CHECK_EQ(x * x * x * x * x,
                 ImplicitOctalStringToDouble("01" + "0000000000" + "0000000000" + "0000000000" + "0000000000" + "0000000000"));
    }

    [Fact]
    public void Binary()
    {
        CHECK_EQ(0.0, StringToDouble("0b0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0B0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(1.0, StringToDouble("0b1", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(2.0, StringToDouble("0b10", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(3.0, StringToDouble("0b11", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(0.0, BinaryStringToDouble("0b0"));
        CHECK_EQ(0.0, BinaryStringToDouble("0B0"));
        CHECK_EQ(1.0, BinaryStringToDouble("0b1"));
        CHECK_EQ(2.0, BinaryStringToDouble("0b10"));
        CHECK_EQ(3.0, BinaryStringToDouble("0b11"));
    }

    [Fact]
    public void MalformedOctal()
    {
        CHECK_EQ(8.0, StringToDouble("08", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(81.0, StringToDouble("081", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(78.0, StringToDouble("078", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(7.7, StringToDouble("07.7", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(7.8, StringToDouble("07.8", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(7e8, StringToDouble("07e8", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(7e7, StringToDouble("07e7", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(8.7, StringToDouble("08.7", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(8e7, StringToDouble("08e7", ALLOW_NON_DECIMAL_PREFIX));

        CHECK_EQ(0.001, StringToDouble("0.001", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.713, StringToDouble("0.713", ALLOW_NON_DECIMAL_PREFIX));
    }

    [Fact]
    public void TrailingJunk()
    {
        CHECK_EQ(8.0, StringToDouble("8q", ALLOW_TRAILING_JUNK));
        CHECK_EQ(10.0, StringToDouble("10e", ALLOW_TRAILING_JUNK));
        CHECK_EQ(10.0, StringToDouble("10e-", ALLOW_TRAILING_JUNK));
    }

    [Fact]
    public void NonStrDecimalLiteral()
    {
        CHECK(double.IsNaN(StringToDouble(" ", NO_CONVERSION_FLAG, double.NaN)));
        CHECK(double.IsNaN(StringToDouble("", NO_CONVERSION_FLAG, double.NaN)));
        CHECK(double.IsNaN(StringToDouble(" ", NO_CONVERSION_FLAG, double.NaN)));
        CHECK_EQ(0.0, StringToDouble("", NO_CONVERSION_FLAG));
        CHECK_EQ(0.0, StringToDouble(" ", NO_CONVERSION_FLAG));
    }

    [Fact]
    public void IntegerStrLiteral()
    {
        CHECK_EQ(0.0, StringToDouble("0.0", NO_CONVERSION_FLAG));
        CHECK_EQ(0.0, StringToDouble("0", NO_CONVERSION_FLAG));
        CHECK_EQ(0.0, StringToDouble("00", NO_CONVERSION_FLAG));
        CHECK_EQ(0.0, StringToDouble("000", NO_CONVERSION_FLAG));
        CHECK_EQ(1.0, StringToDouble("1", NO_CONVERSION_FLAG));
        CHECK_EQ(-1.0, StringToDouble("-1", NO_CONVERSION_FLAG));
        CHECK_EQ(-1.0, StringToDouble("  -1  ", NO_CONVERSION_FLAG));
        CHECK_EQ(1.0, StringToDouble("  +1  ", NO_CONVERSION_FLAG));
        CHECK(double.IsNaN(StringToDouble("  -  1  ", NO_CONVERSION_FLAG)));
        CHECK(double.IsNaN(StringToDouble("  +  1  ", NO_CONVERSION_FLAG)));

        CHECK_EQ(0.0, StringToDouble("0e0", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0e1", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0e-1", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0e-100000", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0e+100000", ALLOW_NON_DECIMAL_PREFIX));
        CHECK_EQ(0.0, StringToDouble("0.", ALLOW_NON_DECIMAL_PREFIX));
    }

    [Fact]
    public void LongNumberStr()
    {
        CHECK_EQ(1e10, StringToDouble("1" + "0000000000", NO_CONVERSION_FLAG));
        CHECK_EQ(1e20, StringToDouble("1" + "0000000000" + "0000000000", NO_CONVERSION_FLAG));

        CHECK_EQ(1e60, StringToDouble("1" + "0000000000" + "0000000000" + "0000000000" + "0000000000" +
                                      "0000000000" + "0000000000", NO_CONVERSION_FLAG));

        CHECK_EQ(1e-2, StringToDouble("." + "0" + "1", NO_CONVERSION_FLAG));
        CHECK_EQ(1e-11, StringToDouble("." + "0000000000" + "1", NO_CONVERSION_FLAG));
        CHECK_EQ(1e-21, StringToDouble("." + "0000000000" + "0000000000" + "1", NO_CONVERSION_FLAG));

        CHECK_EQ(1e-61, StringToDouble("." + "0000000000" + "0000000000" + "0000000000" + "0000000000" +
                                       "0000000000" + "0000000000" + "1", NO_CONVERSION_FLAG));

        // x = 24414062505131248.0 and y = 24414062505131252.0 are representable in
        // double. Check chat z = (x + y) / 2 is rounded to x...
        CHECK_EQ(24414062505131248.0, StringToDouble("24414062505131250.0", NO_CONVERSION_FLAG));

        // ... and z = (x + y) / 2 + delta is rounded to y.
        CHECK_EQ(24414062505131252.0, StringToDouble("24414062505131250.000000001", NO_CONVERSION_FLAG));
    }

    [Fact]
    public void MaximumSignificantDigits()
    {
        char[] num = (
            "4.4501477170144020250819966727949918635852426585926051135169509" +
            "122872622312493126406953054127118942431783801370080830523154578" +
            "251545303238277269592368457430440993619708911874715081505094180" +
            "604803751173783204118519353387964161152051487413083163272520124" +
            "606023105869053620631175265621765214646643181420505164043632222" +
            "668006474326056011713528291579642227455489682133472873831754840" +
            "341397809846934151055619529382191981473003234105366170879223151" +
            "087335413188049110555339027884856781219017754500629806224571029" +
            "581637117459456877330110324211689177656713705497387108207822477" +
            "584250967061891687062782163335299376138075114200886249979505279" +
            "101870966346394401564490729731565935244123171539810221213221201" +
            "847003580761626016356864581135848683152156368691976240370422601" +
            "6998291015625000000000000000000000000000000000e-308").ToCharArray();

        CHECK_EQ(4.4501477170144017780491e-308, StringToDouble(num, NO_CONVERSION_FLAG));

        // Changes the result of strtod (at least in glibc implementation).
        // (sizeof(num) - 8 in C++ counts the terminating '\0'.)
        num[num.Length + 1 - 8] = '1';

        CHECK_EQ(4.4501477170144022721148e-308, StringToDouble(num, NO_CONVERSION_FLAG));
    }

    [Fact]
    public void MinimumExponent()
    {
        // Same test but with different point-position.
        char[] num = (
            "445014771701440202508199667279499186358524265859260511351695091" +
            "228726223124931264069530541271189424317838013700808305231545782" +
            "515453032382772695923684574304409936197089118747150815050941806" +
            "048037511737832041185193533879641611520514874130831632725201246" +
            "060231058690536206311752656217652146466431814205051640436322226" +
            "680064743260560117135282915796422274554896821334728738317548403" +
            "413978098469341510556195293821919814730032341053661708792231510" +
            "873354131880491105553390278848567812190177545006298062245710295" +
            "816371174594568773301103242116891776567137054973871082078224775" +
            "842509670618916870627821633352993761380751142008862499795052791" +
            "018709663463944015644907297315659352441231715398102212132212018" +
            "470035807616260163568645811358486831521563686919762403704226016" +
            "998291015625000000000000000000000000000000000e-1108").ToCharArray();

        CHECK_EQ(4.4501477170144017780491e-308, StringToDouble(num, NO_CONVERSION_FLAG));

        // Changes the result of strtod (at least in glibc implementation).
        num[num.Length + 1 - 8] = '1';

        CHECK_EQ(4.4501477170144022721148e-308, StringToDouble(num, NO_CONVERSION_FLAG));
    }

    [Fact]
    public void MaximumExponent()
    {
        string num = "0.16e309";
        CHECK_EQ(1.59999999999999997765e+308, StringToDouble(num, NO_CONVERSION_FLAG));
    }

    [Fact]
    public void ExponentNumberStr()
    {
        CHECK_EQ(1e1, StringToDouble("1e1", NO_CONVERSION_FLAG));
        CHECK_EQ(1e1, StringToDouble("1e+1", NO_CONVERSION_FLAG));
        CHECK_EQ(1e-1, StringToDouble("1e-1", NO_CONVERSION_FLAG));
        CHECK_EQ(1e100, StringToDouble("1e+100", NO_CONVERSION_FLAG));
        CHECK_EQ(1e-100, StringToDouble("1e-100", NO_CONVERSION_FLAG));
        CHECK_EQ(1e-106, StringToDouble(".000001e-100", NO_CONVERSION_FLAG));
    }

    static readonly BitField OneBit1 = new(0, 1);
    static readonly BitField OneBit2 = new(7, 1);
    static readonly BitField EightBit1 = new(0, 8);
    static readonly BitField EightBit2 = new(13, 8);

    [Fact]
    public void BitField()
    {
        uint x;

        // One bit bit field can hold values 0 and 1.
        CHECK(!OneBit1.is_valid(unchecked((uint)-1)));
        CHECK(!OneBit2.is_valid(unchecked((uint)-1)));
        for (uint i = 0; i < 2; i++)
        {
            CHECK(OneBit1.is_valid(i));
            x = OneBit1.encode(i);
            CHECK_EQ(i, OneBit1.decode(x));

            CHECK(OneBit2.is_valid(i));
            x = OneBit2.encode(i);
            CHECK_EQ(i, OneBit2.decode(x));
        }
        CHECK(!OneBit1.is_valid(2));
        CHECK(!OneBit2.is_valid(2));

        // Eight bit bit field can hold values from 0 tp 255.
        CHECK(!EightBit1.is_valid(unchecked((uint)-1)));
        CHECK(!EightBit2.is_valid(unchecked((uint)-1)));
        for (uint i = 0; i < 256; i++)
        {
            CHECK(EightBit1.is_valid(i));
            x = EightBit1.encode(i);
            CHECK_EQ(i, EightBit1.decode(x));
            CHECK(EightBit2.is_valid(i));
            x = EightBit2.encode(i);
            CHECK_EQ(i, EightBit2.decode(x));
        }
        CHECK(!EightBit1.is_valid(256));
        CHECK(!EightBit2.is_valid(256));
    }

    static readonly BitField64 UpperBits = new(61, 3);
    static readonly BitField64 MiddleBits = new(31, 2);

    [Fact]
    public void BitField64()
    {
        ulong x;

        // Test most significant bits.
        x = 0xE000_0000_0000_0000;
        CHECK(x == UpperBits.encode(7));
        CHECK_EQ(7UL, UpperBits.decode(x));

        // Test the 32/64-bit boundary bits.
        x = 0x0000_0001_8000_0000;
        CHECK(x == MiddleBits.encode(3));
        CHECK_EQ(3UL, MiddleBits.decode(x));
    }

    [Fact]
    public void SpecialIndexParsing()
    {
        CheckNonArrayIndex(false, "");
        CheckNonArrayIndex(false, "-");
        CheckNonArrayIndex(true, "0");
        CheckNonArrayIndex(true, "-0");
        CheckNonArrayIndex(false, "01");
        CheckNonArrayIndex(false, "-01");
        CheckNonArrayIndex(true, "0.5");
        CheckNonArrayIndex(true, "-0.5");
        CheckNonArrayIndex(true, "1");
        CheckNonArrayIndex(true, "-1");
        CheckNonArrayIndex(true, "10");
        CheckNonArrayIndex(true, "-10");
        CheckNonArrayIndex(true, "NaN");
        CheckNonArrayIndex(true, "Infinity");
        CheckNonArrayIndex(true, "-Infinity");
        CheckNonArrayIndex(true, "4294967295");
        CheckNonArrayIndex(true, "429496.7295");
        CheckNonArrayIndex(true, "1.3333333333333333");
        CheckNonArrayIndex(false, "1.3333333333333339");
        CheckNonArrayIndex(true, "1.333333333333331e+222");
        CheckNonArrayIndex(true, "-1.3333333333333211e+222");
        CheckNonArrayIndex(false, "-1.3333333333333311e+222");
        CheckNonArrayIndex(true, "429496.7295");
        CheckNonArrayIndex(false, "43s3");
        CheckNonArrayIndex(true, "4294967296");
        CheckNonArrayIndex(true, "-4294967296");
        CheckNonArrayIndex(true, "999999999999999");
        CheckNonArrayIndex(false, "9999999999999999");
        CheckNonArrayIndex(true, "-999999999999999");
        CheckNonArrayIndex(false, "-9999999999999999");
        CheckNonArrayIndex(false, "42949672964294967296429496729694966");
    }

    [Fact]
    public void NoHandlesForTryNumberToSize()
    {
        CHECK(TryNumberToSize(1, out nuint result));
        CHECK_EQ(1UL, (ulong)result);
        CHECK(TryNumberToSize(2.0, out result));
        CHECK_EQ(2UL, (ulong)result);
        CHECK(!TryNumberToSize((double)nuint.MaxValue + 10000.0, out result));
    }

    [Fact]
    public void TryNumberToSizeWithMaxSizePlusOne()
    {
        // 1 << 64, higher than the limit of size_t.
        double value = 18446744073709551616.0;
        CHECK(!TryNumberToSize(value, out _));
    }

    [Fact]
    public void PositiveNumberToUint32()
    {
        uint max = uint.MaxValue;
        // Test Smi conversions.
        CHECK_EQ(Conversions.PositiveNumberToUint32(0), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(-1), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(kSmiMinValue), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(kSmiMaxValue), (uint)kSmiMaxValue);
        // Test Double conversions.
        CHECK_EQ(Conversions.PositiveNumberToUint32(0.0), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(0.999), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(1.999), 1u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(-12.0), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(12000.0), 12000u);
        CHECK_EQ(Conversions.PositiveNumberToUint32((double)kSmiMaxValue + 1), (uint)kSmiMaxValue + 1);
        CHECK_EQ(Conversions.PositiveNumberToUint32(max), max);
        CHECK_EQ(Conversions.PositiveNumberToUint32((double)max * 1000), max);
        CHECK_EQ(Conversions.PositiveNumberToUint32(double.MaxValue), max);
        CHECK_EQ(Conversions.PositiveNumberToUint32(double.PositiveInfinity), max);
        CHECK_EQ(Conversions.PositiveNumberToUint32(-1.0 * double.PositiveInfinity), 0u);
        CHECK_EQ(Conversions.PositiveNumberToUint32(double.NaN), 0u);
    }

    static readonly (int integer, string str)[] int_pairs =
    [
        (0, "0"), (101, "101"), (-1, "-1"), (1024, "1024"), (200000, "200000"),
        (-1024, "-1024"), (-200000, "-200000"), (kMinInt, "-2147483648"), (kMaxInt, "2147483647"),
    ];

    [Fact]
    public void IntToStringView()
    {
        char[] buf = new char[4096];
        foreach (var (integer, str) in int_pairs)
        {
            Assert.Equal(str, new string(Conversions.IntToStringView(integer, buf)));
            Assert.Equal(str, IntToCString(integer));
        }
    }

    static readonly (double number, string str)[] double_pairs =
    [
        (0.0, "0"), (kMinInt, "-2147483648"), (kMaxInt, "2147483647"),
        // ES section 7.1.12.1
        // https://tc39.es/ecma262/#sec-tostring-applied-to-the-number-type:
        // -0.0 is stringified to "0".
        (-0.0, "0"), (1.1, "1.1"), (0.1, "0.1"),
    ];

    [Fact]
    public void DoubleToStringView()
    {
        char[] buf = new char[4096];
        foreach (var (number, str) in double_pairs)
        {
            Assert.Equal(str, new string(Conversions.DoubleToStringView(number, buf)));
            Assert.Equal(str, DoubleToCString(number));
        }
    }

    static readonly (double number, int integer)[] double_int32_pairs =
    [
        (0.0, 0), (-0.0, 0), (double.NaN, 0), (double.PositiveInfinity, 0), (double.NegativeInfinity, 0),
        (3.14, 3), (1.99, 1), (-1.99, -1), (kMinInt, kMinInt), (kMaxInt, kMaxInt),
        (kMaxSafeInteger, -1), (kMinSafeInteger, 1), (kMaxSafeInteger + 1, 0), (kMinSafeInteger - 1, 0),
    ];

    [Fact]
    public void DoubleToInt32()
    {
        foreach (var (number, integer) in double_int32_pairs)
        {
            Assert.Equal(integer, Conversions.DoubleToInt32(number));
        }
    }

    static readonly (double number, long integer)[] double_int64_pairs =
    [
        (0.0, 0), (-0.0, 0), (double.NaN, 0), (double.PositiveInfinity, 0), (double.NegativeInfinity, 0),
        (3.14, 3), (1.99, 1), (-1.99, -1),
        (kMinSafeInteger, (long)kMinSafeInteger), (kMaxSafeInteger, (long)kMaxSafeIntegerUint64),
        (kMinSafeInteger - 1, (long)kMinSafeInteger - 1), (kMaxSafeInteger + 1, (long)kMaxSafeIntegerUint64 + 1),
        (long.MinValue, long.MinValue),
        // Max int64_t is not representable as a double, the closest is -2^63.
        (long.MaxValue, long.MinValue),
        // So we test for a smaller number, representable as a double.
        ((1UL << 63) - 1024, (long)((1UL << 63) - 1024)),
    ];

    [Fact]
    public void DoubleToWebIDLInt64()
    {
        foreach (var (number, integer) in double_int64_pairs)
        {
            Assert.Equal(integer, Conversions.DoubleToWebIDLInt64(number));
        }
    }
}
