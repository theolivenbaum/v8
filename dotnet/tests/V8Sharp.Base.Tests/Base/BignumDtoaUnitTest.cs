// Port of test/unittests/base/bignum-dtoa-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using static V8Sharp.Base.Tests.GayData;
using static V8Sharp.Base.Numbers.BignumDtoaMode;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Base;

public class BignumDtoaTest
{
    const int kBufferSize = 100;

    [Fact]
    public void BignumDtoaVariousDoubles()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;

      BignumDtoa(1.0, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      BignumDtoa(1.0, BIGNUM_DTOA_FIXED, 3, buffer, out length, out point);
      CHECK_GE(3, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      BignumDtoa(1.0, BIGNUM_DTOA_PRECISION, 3, buffer, out length, out point);
      CHECK_GE(3, length);
      TrimRepresentation(buffer);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      BignumDtoa(1.5, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      BignumDtoa(1.5, BIGNUM_DTOA_FIXED, 10, buffer, out length, out point);
      CHECK_GE(10, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      BignumDtoa(1.5, BIGNUM_DTOA_PRECISION, 10, buffer, out length, out point);
      CHECK_GE(10, length);
      TrimRepresentation(buffer);
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      double min_double = 5e-324;
      BignumDtoa(min_double, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("5", CStr(buffer));
      CHECK_EQ(-323, point);

      BignumDtoa(min_double, BIGNUM_DTOA_FIXED, 5, buffer, out length, out point);
      CHECK_GE(5, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("", CStr(buffer));

      BignumDtoa(min_double, BIGNUM_DTOA_PRECISION, 5, buffer, out length, out point);
      CHECK_GE(5, length);
      TrimRepresentation(buffer);
      CHECK_EQ("49407", CStr(buffer));
      CHECK_EQ(-323, point);

      double max_double = 1.7976931348623157e308;
      BignumDtoa(max_double, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("17976931348623157", CStr(buffer));
      CHECK_EQ(309, point);

      BignumDtoa(max_double, BIGNUM_DTOA_PRECISION, 7, buffer, out length, out point);
      CHECK_GE(7, length);
      TrimRepresentation(buffer);
      CHECK_EQ("1797693", CStr(buffer));
      CHECK_EQ(309, point);

      BignumDtoa(4294967272.0, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(4294967272.0, BIGNUM_DTOA_FIXED, 5, buffer, out length, out point);
      CHECK_EQ("429496727200000", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(4294967272.0, BIGNUM_DTOA_PRECISION, 14, buffer, out length, out point);
      CHECK_GE(14, length);
      TrimRepresentation(buffer);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(4.1855804968213567e298, BIGNUM_DTOA_SHORTEST, 0, buffer, out length,
                 out point);
      CHECK_EQ("4185580496821357", CStr(buffer));
      CHECK_EQ(299, point);

      BignumDtoa(4.1855804968213567e298, BIGNUM_DTOA_PRECISION, 20, buffer, out length,
                 out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("41855804968213567225", CStr(buffer));
      CHECK_EQ(299, point);

      BignumDtoa(5.5626846462680035e-309, BIGNUM_DTOA_SHORTEST, 0, buffer, out length,
                 out point);
      CHECK_EQ("5562684646268003", CStr(buffer));
      CHECK_EQ(-308, point);

      BignumDtoa(5.5626846462680035e-309, BIGNUM_DTOA_PRECISION, 1, buffer, out length,
                 out point);
      CHECK_GE(1, length);
      TrimRepresentation(buffer);
      CHECK_EQ("6", CStr(buffer));
      CHECK_EQ(-308, point);

      BignumDtoa(2147483648.0, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("2147483648", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(2147483648.0, BIGNUM_DTOA_FIXED, 2, buffer, out length, out point);
      CHECK_GE(2, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("2147483648", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(2147483648.0, BIGNUM_DTOA_PRECISION, 5, buffer, out length, out point);
      CHECK_GE(5, length);
      TrimRepresentation(buffer);
      CHECK_EQ("21475", CStr(buffer));
      CHECK_EQ(10, point);

      BignumDtoa(3.5844466002796428e+298, BIGNUM_DTOA_SHORTEST, 0, buffer, out length,
                 out point);
      CHECK_EQ("35844466002796428", CStr(buffer));
      CHECK_EQ(299, point);

      BignumDtoa(3.5844466002796428e+298, BIGNUM_DTOA_PRECISION, 10, buffer,
                 out length, out point);
      CHECK_GE(10, length);
      TrimRepresentation(buffer);
      CHECK_EQ("35844466", CStr(buffer));
      CHECK_EQ(299, point);

      ulong smallest_normal64 = 0x0010_0000_0000_0000;
      double v = new Double(smallest_normal64).Value;
      BignumDtoa(v, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("22250738585072014", CStr(buffer));
      CHECK_EQ(-307, point);

      BignumDtoa(v, BIGNUM_DTOA_PRECISION, 20, buffer, out length, out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("22250738585072013831", CStr(buffer));
      CHECK_EQ(-307, point);

      ulong largest_denormal64 = 0x000F_FFFF_FFFF_FFFF;
      v = new Double(largest_denormal64).Value;
      BignumDtoa(v, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("2225073858507201", CStr(buffer));
      CHECK_EQ(-307, point);

      BignumDtoa(v, BIGNUM_DTOA_PRECISION, 20, buffer, out length, out point);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("2225073858507200889", CStr(buffer));
      CHECK_EQ(-307, point);

      BignumDtoa(4128420500802942e-24, BIGNUM_DTOA_SHORTEST, 0, buffer, out length,
                 out point);
      CHECK_EQ("4128420500802942", CStr(buffer));
      CHECK_EQ(-8, point);

      v = 3.9292015898194142585311918e-10;
      BignumDtoa(v, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK_EQ("39292015898194143", CStr(buffer));

      v = 4194304.0;
      BignumDtoa(v, BIGNUM_DTOA_FIXED, 5, buffer, out length, out point);
      CHECK_GE(5, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("4194304", CStr(buffer));

      v = 3.3161339052167390562200598e-237;
      BignumDtoa(v, BIGNUM_DTOA_PRECISION, 19, buffer, out length, out point);
      CHECK_GE(19, length);
      TrimRepresentation(buffer);
      CHECK_EQ("3316133905216739056", CStr(buffer));
      CHECK_EQ(-236, point);

      v = 7.9885183916008099497815232e+191;
      BignumDtoa(v, BIGNUM_DTOA_PRECISION, 4, buffer, out length, out point);
      CHECK_GE(4, length);
      TrimRepresentation(buffer);
      CHECK_EQ("7989", CStr(buffer));
      CHECK_EQ(192, point);

      v = 1.0000000000000012800000000e+17;
      BignumDtoa(v, BIGNUM_DTOA_FIXED, 1, buffer, out length, out point);
      CHECK_GE(1, length - point);
      TrimRepresentation(buffer);
      CHECK_EQ("100000000000000128", CStr(buffer));
      CHECK_EQ(18, point);
    }

    [Fact]
    public void BignumDtoaGayShortest()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;

      ReadOnlySpan<PrecomputedShortest> precomputed =
          PrecomputedShortestRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedShortest current_test = precomputed[i];
        double v = current_test.v;
        BignumDtoa(v, BIGNUM_DTOA_SHORTEST, 0, buffer, out length, out point);
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }

    [Fact]
    public void BignumDtoaGayFixed()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;

      ReadOnlySpan<PrecomputedFixed> precomputed =
          PrecomputedFixedRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedFixed current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        BignumDtoa(v, BIGNUM_DTOA_FIXED, number_digits, buffer, out length, out point);
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_GE(number_digits, length - point);
        TrimRepresentation(buffer);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }

    [Fact]
    public void BignumDtoaGayPrecision()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;

      ReadOnlySpan<PrecomputedPrecision> precomputed =
          PrecomputedPrecisionRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedPrecision current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        BignumDtoa(v, BIGNUM_DTOA_PRECISION, number_digits, buffer, out length,
                   out point);
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_GE(number_digits, length);
        TrimRepresentation(buffer);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }
}
