// Port of test/unittests/base/fast-dtoa-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using static V8Sharp.Base.Tests.GayData;
using static V8Sharp.Base.Numbers.FastDtoaMode;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Base;

public class FastDtoaTest
{
    const int kBufferSize = 100;

    [Fact]
    public void FastDtoaShortestVariousDoubles()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;
      bool status;

      double min_double = 5e-324;
      status = FastDtoa(min_double, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("5", CStr(buffer));
      CHECK_EQ(-323, point);

      double max_double = 1.7976931348623157e308;
      status = FastDtoa(max_double, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("17976931348623157", CStr(buffer));
      CHECK_EQ(309, point);

      status =
          FastDtoa(4294967272.0, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("4294967272", CStr(buffer));
      CHECK_EQ(10, point);

      status = FastDtoa(4.1855804968213567e298, FAST_DTOA_SHORTEST, 0, buffer,
                        out length, out point);
      CHECK(status);
      CHECK_EQ("4185580496821357", CStr(buffer));
      CHECK_EQ(299, point);

      status = FastDtoa(5.5626846462680035e-309, FAST_DTOA_SHORTEST, 0, buffer,
                        out length, out point);
      CHECK(status);
      CHECK_EQ("5562684646268003", CStr(buffer));
      CHECK_EQ(-308, point);

      status =
          FastDtoa(2147483648.0, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("2147483648", CStr(buffer));
      CHECK_EQ(10, point);

      status = FastDtoa(3.5844466002796428e+298, FAST_DTOA_SHORTEST, 0, buffer,
                        out length, out point);
      if (status) {  // Not all FastDtoa variants manage to compute this number.
        CHECK_EQ("35844466002796428", CStr(buffer));
        CHECK_EQ(299, point);
      }

      ulong smallest_normal64 = 0x0010_0000_0000_0000;
      double v = new Double(smallest_normal64).Value;
      status = FastDtoa(v, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      if (status) {
        CHECK_EQ("22250738585072014", CStr(buffer));
        CHECK_EQ(-307, point);
      }

      ulong largest_denormal64 = 0x000F_FFFF_FFFF_FFFF;
      v = new Double(largest_denormal64).Value;
      status = FastDtoa(v, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
      if (status) {
        CHECK_EQ("2225073858507201", CStr(buffer));
        CHECK_EQ(-307, point);
      }
    }

    [Fact]
    public void FastDtoaPrecisionVariousDoubles()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;
      bool status;

      status = FastDtoa(1.0, FAST_DTOA_PRECISION, 3, buffer, out length, out point);
      CHECK(status);
      CHECK_GE(3, length);
      TrimRepresentation(buffer);
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      status = FastDtoa(1.5, FAST_DTOA_PRECISION, 10, buffer, out length, out point);
      if (status) {
        CHECK_GE(10, length);
        TrimRepresentation(buffer);
        CHECK_EQ("15", CStr(buffer));
        CHECK_EQ(1, point);
      }

      double min_double = 5e-324;
      status =
          FastDtoa(min_double, FAST_DTOA_PRECISION, 5, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("49407", CStr(buffer));
      CHECK_EQ(-323, point);

      double max_double = 1.7976931348623157e308;
      status =
          FastDtoa(max_double, FAST_DTOA_PRECISION, 7, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("1797693", CStr(buffer));
      CHECK_EQ(309, point);

      status =
          FastDtoa(4294967272.0, FAST_DTOA_PRECISION, 14, buffer, out length, out point);
      if (status) {
        CHECK_GE(14, length);
        TrimRepresentation(buffer);
        CHECK_EQ("4294967272", CStr(buffer));
        CHECK_EQ(10, point);
      }

      status = FastDtoa(4.1855804968213567e298, FAST_DTOA_PRECISION, 17, buffer,
                        out length, out point);
      CHECK(status);
      CHECK_EQ("41855804968213567", CStr(buffer));
      CHECK_EQ(299, point);

      status = FastDtoa(5.5626846462680035e-309, FAST_DTOA_PRECISION, 1, buffer,
                        out length, out point);
      CHECK(status);
      CHECK_EQ("6", CStr(buffer));
      CHECK_EQ(-308, point);

      status =
          FastDtoa(2147483648.0, FAST_DTOA_PRECISION, 5, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("21475", CStr(buffer));
      CHECK_EQ(10, point);

      status = FastDtoa(3.5844466002796428e+298, FAST_DTOA_PRECISION, 10, buffer,
                        out length, out point);
      CHECK(status);
      CHECK_GE(10, length);
      TrimRepresentation(buffer);
      CHECK_EQ("35844466", CStr(buffer));
      CHECK_EQ(299, point);

      ulong smallest_normal64 = 0x0010_0000_0000_0000;
      double v = new Double(smallest_normal64).Value;
      status = FastDtoa(v, FAST_DTOA_PRECISION, 17, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("22250738585072014", CStr(buffer));
      CHECK_EQ(-307, point);

      ulong largest_denormal64 = 0x000F_FFFF_FFFF_FFFF;
      v = new Double(largest_denormal64).Value;
      status = FastDtoa(v, FAST_DTOA_PRECISION, 17, buffer, out length, out point);
      CHECK(status);
      CHECK_GE(20, length);
      TrimRepresentation(buffer);
      CHECK_EQ("22250738585072009", CStr(buffer));
      CHECK_EQ(-307, point);

      v = 3.3161339052167390562200598e-237;
      status = FastDtoa(v, FAST_DTOA_PRECISION, 18, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("331613390521673906", CStr(buffer));
      CHECK_EQ(-236, point);

      v = 7.9885183916008099497815232e+191;
      status = FastDtoa(v, FAST_DTOA_PRECISION, 4, buffer, out length, out point);
      CHECK(status);
      CHECK_EQ("7989", CStr(buffer));
      CHECK_EQ(192, point);
    }

    [Fact]
    public void FastDtoaGayShortest()
    {
      char[] buffer = new char[kBufferSize];
      bool status;
      int length;
      int point;
      int succeeded = 0;
      int total = 0;
      bool needed_max_length = false;

      ReadOnlySpan<PrecomputedShortest> precomputed =
          PrecomputedShortestRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedShortest current_test = precomputed[i];
        total++;
        double v = current_test.v;
        status = FastDtoa(v, FAST_DTOA_SHORTEST, 0, buffer, out length, out point);
        CHECK_GE(kFastDtoaMaximalLength, length);
        if (!status) continue;
        if (length == kFastDtoaMaximalLength) needed_max_length = true;
        succeeded++;
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
      CHECK_GT(succeeded * 1.0 / total, 0.99);
      CHECK(needed_max_length);
    }

    [Fact]
    public void FastDtoaGayPrecision()
    {
      char[] buffer = new char[kBufferSize];
      bool status;
      int length;
      int point;
      int succeeded = 0;
      int total = 0;
      // Count separately for entries with less than 15 requested digits.
      int succeeded_15 = 0;
      int total_15 = 0;

      ReadOnlySpan<PrecomputedPrecision> precomputed =
          PrecomputedPrecisionRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedPrecision current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        total++;
        if (number_digits <= 15) total_15++;
        status = FastDtoa(v, FAST_DTOA_PRECISION, number_digits, buffer, out length,
                          out point);
        CHECK_GE(number_digits, length);
        if (!status) continue;
        succeeded++;
        if (number_digits <= 15) succeeded_15++;
        TrimRepresentation(buffer);
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
      // The precomputed numbers contain many entries with many requested
      // digits. These have a high failure rate and we therefore expect a lower
      // success rate than for the shortest representation.
      CHECK_GT(succeeded * 1.0 / total, 0.85);
      // However with less than 15 digits almost the algorithm should almost always
      // succeed.
      CHECK_GT(succeeded_15 * 1.0 / total_15, 0.9999);
    }
}
