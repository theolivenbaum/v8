// Port of test/unittests/base/dtoa-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using static V8Sharp.Base.Tests.GayData;
using static V8Sharp.Base.Numbers.DtoaMode;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Base;

public class DtoaTest
{
    const int kBufferSize = 100;

    [Fact]
    public void DtoaVariousDoubles()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;
      int sign;

      DoubleToAscii(0.0, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("0", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(0.0, DTOA_FIXED, 2, buffer, out sign, out length, out point);
      CHECK_EQ(1, length);
      CHECK_EQ("0", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(0.0, DTOA_PRECISION, 3, buffer, out sign, out length, out point);
      CHECK_EQ(1, length);
      CHECK_EQ("0", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.0, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.0, DTOA_FIXED, 3, buffer, out sign, out length, out point);
      CHECK_GE(3, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.0, DTOA_PRECISION, 3, buffer, out sign, out length, out point);
      CHECK_GE(3, length);
      TrimRepresentation(buffer);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.5, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.5, DTOA_FIXED, 10, buffer, out sign, out length, out point);
      CHECK_GE(10, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      DoubleToAscii(1.5, DTOA_PRECISION, 10, buffer, out sign, out length, out point);
      CHECK_GE(10, length);
      TrimRepresentation(buffer);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      double min_double = 5e-324;
      DoubleToAscii(min_double, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("5", CStr(buffer));
      CHECK_EQ(-323, point);

      DoubleToAscii(min_double, DTOA_FIXED, 5, buffer, out sign, out length, out point);
      CHECK_GE(5, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("", CStr(buffer));
      CHECK_GE(-5, point);

      DoubleToAscii(min_double, DTOA_PRECISION, 5, buffer, out sign, out length, out point);
      CHECK_GE(5, length);
      TrimRepresentation(buffer);
      CHECK_EQ("49407", CStr(buffer));
      CHECK_EQ(-323, point);

      double max_double = 1.7976931348623157e308;
      DoubleToAscii(max_double, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("17976931348623157", CStr(buffer));
      CHECK_EQ(309, point);

      DoubleToAscii(max_double, DTOA_PRECISION, 7, buffer, out sign, out length, out point);
      CHECK_GE(7, length);
      TrimRepresentation(buffer);
      CHECK_EQ("1797693", CStr(buffer));
      CHECK_EQ(309, point);

      DoubleToAscii(4294967272.0, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(4294967272.0, DTOA_FIXED, 5, buffer, out sign, out length, out point);
      CHECK_GE(5, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(4294967272.0, DTOA_PRECISION, 14, buffer, out sign, out length,
                    out point);
      CHECK_GE(14, length);
      TrimRepresentation(buffer);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(4.1855804968213567e298, DTOA_SHORTEST, 0, buffer, out sign,
                    out length, out point);
      CHECK_EQ("4185580496821357", CStr(buffer));
      CHECK_EQ(299, point);

      DoubleToAscii(4.1855804968213567e298, DTOA_PRECISION, 20, buffer, out sign,
                    out length, out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("41855804968213567225", CStr(buffer));
      CHECK_EQ(299, point);

      DoubleToAscii(5.5626846462680035e-309, DTOA_SHORTEST, 0, buffer, out sign,
                    out length, out point);
      CHECK_EQ("5562684646268003", CStr(buffer));
      CHECK_EQ(-308, point);

      DoubleToAscii(5.5626846462680035e-309, DTOA_PRECISION, 1, buffer, out sign,
                    out length, out point);
      CHECK_GE(1, length);
      TrimRepresentation(buffer);
      CHECK_EQ("6", CStr(buffer));
      CHECK_EQ(-308, point);

      DoubleToAscii(-2147483648.0, DTOA_SHORTEST, 0, buffer, out sign, out length,
                    out point);
      CHECK_EQ(1, sign);
      CHECK_EQ("2147483648", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(-2147483648.0, DTOA_FIXED, 2, buffer, out sign, out length, out point);
      CHECK_GE(2, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ(1, sign);
      CHECK_EQ("2147483648", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(-2147483648.0, DTOA_PRECISION, 5, buffer, out sign, out length,
                    out point);
      CHECK_GE(5, length);
      TrimRepresentation(buffer);
      CHECK_EQ(1, sign);
      CHECK_EQ("21475", CStr(buffer));
      CHECK_EQ(10, point);

      DoubleToAscii(-3.5844466002796428e+298, DTOA_SHORTEST, 0, buffer, out sign,
                    out length, out point);
      CHECK_EQ(1, sign);
      CHECK_EQ("35844466002796428", CStr(buffer));
      CHECK_EQ(299, point);

      DoubleToAscii(-3.5844466002796428e+298, DTOA_PRECISION, 10, buffer, out sign,
                    out length, out point);
      CHECK_EQ(1, sign);
      CHECK_GE(10, length);
      TrimRepresentation(buffer);
      CHECK_EQ("35844466", CStr(buffer));
      CHECK_EQ(299, point);

      ulong smallest_normal64 = 0x0010_0000_0000_0000;
      double v = new Double(smallest_normal64).Value;
      DoubleToAscii(v, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("22250738585072014", CStr(buffer));
      CHECK_EQ(-307, point);

      DoubleToAscii(v, DTOA_PRECISION, 20, buffer, out sign, out length, out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("22250738585072013831", CStr(buffer));
      CHECK_EQ(-307, point);

      ulong largest_denormal64 = 0x000F_FFFF_FFFF_FFFF;
      v = new Double(largest_denormal64).Value;
      DoubleToAscii(v, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("2225073858507201", CStr(buffer));
      CHECK_EQ(-307, point);

      DoubleToAscii(v, DTOA_PRECISION, 20, buffer, out sign, out length, out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("2225073858507200889", CStr(buffer));
      CHECK_EQ(-307, point);

      DoubleToAscii(4128420500802942e-24, DTOA_SHORTEST, 0, buffer, out sign, out length,
                    out point);
      CHECK_EQ(0, sign);
      CHECK_EQ("4128420500802942", CStr(buffer));
      CHECK_EQ(-8, point);

      v = -3.9292015898194142585311918e-10;
      DoubleToAscii(v, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
      CHECK_EQ("39292015898194143", CStr(buffer));

      v = 4194304.0;
      DoubleToAscii(v, DTOA_FIXED, 5, buffer, out sign, out length, out point);
      CHECK_GE(5, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("4194304", CStr(buffer));

      v = 3.3161339052167390562200598e-237;
      DoubleToAscii(v, DTOA_PRECISION, 19, buffer, out sign, out length, out point);
      CHECK_GE(19, length);
      TrimRepresentation(buffer);
      CHECK_EQ("3316133905216739056", CStr(buffer));
      CHECK_EQ(-236, point);
    }

    [Fact]
    public void DtoaGayShortest()
    {
      char[] buffer = new char[kBufferSize];
      int sign;
      int length;
      int point;

      ReadOnlySpan<PrecomputedShortest> precomputed =
          PrecomputedShortestRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedShortest current_test = precomputed[i];
        double v = current_test.v;
        DoubleToAscii(v, DTOA_SHORTEST, 0, buffer, out sign, out length, out point);
        CHECK_EQ(0, sign);  // All precomputed numbers are positive.
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }

    [Fact]
    public void DtoaGayFixed()
    {
      char[] buffer = new char[kBufferSize];
      int sign;
      int length;
      int point;

      ReadOnlySpan<PrecomputedFixed> precomputed =
          PrecomputedFixedRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedFixed current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        DoubleToAscii(v, DTOA_FIXED, number_digits, buffer, out sign, out length, out point);
        CHECK_EQ(0, sign);  // All precomputed numbers are positive.
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_GE(number_digits, length - point);
        TrimRepresentation(buffer);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }

    [Fact]
    public void DtoaGayPrecision()
    {
      char[] buffer = new char[kBufferSize];
      int sign;
      int length;
      int point;

      ReadOnlySpan<PrecomputedPrecision> precomputed =
          PrecomputedPrecisionRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedPrecision current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        DoubleToAscii(v, DTOA_PRECISION, number_digits, buffer, out sign, out length,
                      out point);
        CHECK_EQ(0, sign);  // All precomputed numbers are positive.
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_GE(number_digits, length);
        TrimRepresentation(buffer);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }
}
