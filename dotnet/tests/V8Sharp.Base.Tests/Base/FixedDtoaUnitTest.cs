// Port of test/unittests/base/fixed-dtoa-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using static V8Sharp.Base.Tests.GayData;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Base;

public class FixedDtoaTest
{
    const int kBufferSize = 500;

    [Fact]
    public void FastFixedVariousDoubles()
    {
      char[] buffer = new char[kBufferSize];
      int length;
      int point;

      CHECK(FastFixedDtoa(1.0, 1, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1.0, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1.0, 0, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0xFFFFFFFF, 5, buffer, out length, out point));
      CHECK_EQ("4294967295", CStr(buffer));
      CHECK_EQ(10, point);

      CHECK(FastFixedDtoa(4294967296.0, 5, buffer, out length, out point));
      CHECK_EQ("4294967296", CStr(buffer));
      CHECK_EQ(10, point);

      CHECK(FastFixedDtoa(1e21, 5, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      // CHECK_EQ(22, point);
      CHECK_EQ(22, point);

      CHECK(FastFixedDtoa(999999999999999868928.00, 2, buffer, out length, out point));
      CHECK_EQ("999999999999999868928", CStr(buffer));
      CHECK_EQ(21, point);

      CHECK(FastFixedDtoa(6.9999999999999989514240000e+21, 5, buffer, out length,
                          out point));
      CHECK_EQ("6999999999999998951424", CStr(buffer));
      CHECK_EQ(22, point);

      CHECK(FastFixedDtoa(1.5, 5, buffer, out length, out point));
      CHECK_EQ("15", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1.55, 5, buffer, out length, out point));
      CHECK_EQ("155", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1.55, 1, buffer, out length, out point));
      CHECK_EQ("16", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1.00000001, 15, buffer, out length, out point));
      CHECK_EQ("100000001", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.1, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(0, point);

      CHECK(FastFixedDtoa(0.01, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-1, point);

      CHECK(FastFixedDtoa(0.001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-2, point);

      CHECK(FastFixedDtoa(0.0001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-3, point);

      CHECK(FastFixedDtoa(0.00001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-4, point);

      CHECK(FastFixedDtoa(0.000001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-5, point);

      CHECK(FastFixedDtoa(0.0000001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-6, point);

      CHECK(FastFixedDtoa(0.00000001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-7, point);

      CHECK(FastFixedDtoa(0.000000001, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-8, point);

      CHECK(FastFixedDtoa(0.0000000001, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-9, point);

      CHECK(FastFixedDtoa(0.00000000001, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-10, point);

      CHECK(FastFixedDtoa(0.000000000001, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-11, point);

      CHECK(FastFixedDtoa(0.0000000000001, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-12, point);

      CHECK(FastFixedDtoa(0.00000000000001, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-13, point);

      CHECK(FastFixedDtoa(0.000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-14, point);

      CHECK(FastFixedDtoa(0.0000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-15, point);

      CHECK(FastFixedDtoa(0.00000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-16, point);

      CHECK(FastFixedDtoa(0.000000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-17, point);

      CHECK(FastFixedDtoa(0.0000000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-18, point);

      CHECK(FastFixedDtoa(0.00000000000000000001, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-19, point);

      CHECK(FastFixedDtoa(0.10000000004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(0, point);

      CHECK(FastFixedDtoa(0.01000000004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-1, point);

      CHECK(FastFixedDtoa(0.00100000004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-2, point);

      CHECK(FastFixedDtoa(0.00010000004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-3, point);

      CHECK(FastFixedDtoa(0.00001000004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-4, point);

      CHECK(FastFixedDtoa(0.00000100004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-5, point);

      CHECK(FastFixedDtoa(0.00000010004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-6, point);

      CHECK(FastFixedDtoa(0.00000001004, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-7, point);

      CHECK(FastFixedDtoa(0.00000000104, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-8, point);

      CHECK(FastFixedDtoa(0.0000000001000004, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-9, point);

      CHECK(FastFixedDtoa(0.0000000000100004, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-10, point);

      CHECK(FastFixedDtoa(0.0000000000010004, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-11, point);

      CHECK(FastFixedDtoa(0.0000000000001004, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-12, point);

      CHECK(FastFixedDtoa(0.0000000000000104, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-13, point);

      CHECK(FastFixedDtoa(0.000000000000001000004, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-14, point);

      CHECK(FastFixedDtoa(0.000000000000000100004, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-15, point);

      CHECK(FastFixedDtoa(0.000000000000000010004, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-16, point);

      CHECK(FastFixedDtoa(0.000000000000000001004, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-17, point);

      CHECK(FastFixedDtoa(0.000000000000000000104, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-18, point);

      CHECK(FastFixedDtoa(0.000000000000000000014, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-19, point);

      CHECK(FastFixedDtoa(0.10000000006, 10, buffer, out length, out point));
      CHECK_EQ("1000000001", CStr(buffer));
      CHECK_EQ(0, point);

      CHECK(FastFixedDtoa(0.01000000006, 10, buffer, out length, out point));
      CHECK_EQ("100000001", CStr(buffer));
      CHECK_EQ(-1, point);

      CHECK(FastFixedDtoa(0.00100000006, 10, buffer, out length, out point));
      CHECK_EQ("10000001", CStr(buffer));
      CHECK_EQ(-2, point);

      CHECK(FastFixedDtoa(0.00010000006, 10, buffer, out length, out point));
      CHECK_EQ("1000001", CStr(buffer));
      CHECK_EQ(-3, point);

      CHECK(FastFixedDtoa(0.00001000006, 10, buffer, out length, out point));
      CHECK_EQ("100001", CStr(buffer));
      CHECK_EQ(-4, point);

      CHECK(FastFixedDtoa(0.00000100006, 10, buffer, out length, out point));
      CHECK_EQ("10001", CStr(buffer));
      CHECK_EQ(-5, point);

      CHECK(FastFixedDtoa(0.00000010006, 10, buffer, out length, out point));
      CHECK_EQ("1001", CStr(buffer));
      CHECK_EQ(-6, point);

      CHECK(FastFixedDtoa(0.00000001006, 10, buffer, out length, out point));
      CHECK_EQ("101", CStr(buffer));
      CHECK_EQ(-7, point);

      CHECK(FastFixedDtoa(0.00000000106, 10, buffer, out length, out point));
      CHECK_EQ("11", CStr(buffer));
      CHECK_EQ(-8, point);

      CHECK(FastFixedDtoa(0.0000000001000006, 15, buffer, out length, out point));
      CHECK_EQ("100001", CStr(buffer));
      CHECK_EQ(-9, point);

      CHECK(FastFixedDtoa(0.0000000000100006, 15, buffer, out length, out point));
      CHECK_EQ("10001", CStr(buffer));
      CHECK_EQ(-10, point);

      CHECK(FastFixedDtoa(0.0000000000010006, 15, buffer, out length, out point));
      CHECK_EQ("1001", CStr(buffer));
      CHECK_EQ(-11, point);

      CHECK(FastFixedDtoa(0.0000000000001006, 15, buffer, out length, out point));
      CHECK_EQ("101", CStr(buffer));
      CHECK_EQ(-12, point);

      CHECK(FastFixedDtoa(0.0000000000000106, 15, buffer, out length, out point));
      CHECK_EQ("11", CStr(buffer));
      CHECK_EQ(-13, point);

      CHECK(FastFixedDtoa(0.000000000000001000006, 20, buffer, out length, out point));
      CHECK_EQ("100001", CStr(buffer));
      CHECK_EQ(-14, point);

      CHECK(FastFixedDtoa(0.000000000000000100006, 20, buffer, out length, out point));
      CHECK_EQ("10001", CStr(buffer));
      CHECK_EQ(-15, point);

      CHECK(FastFixedDtoa(0.000000000000000010006, 20, buffer, out length, out point));
      CHECK_EQ("1001", CStr(buffer));
      CHECK_EQ(-16, point);

      CHECK(FastFixedDtoa(0.000000000000000001006, 20, buffer, out length, out point));
      CHECK_EQ("101", CStr(buffer));
      CHECK_EQ(-17, point);

      CHECK(FastFixedDtoa(0.000000000000000000106, 20, buffer, out length, out point));
      CHECK_EQ("11", CStr(buffer));
      CHECK_EQ(-18, point);

      CHECK(FastFixedDtoa(0.000000000000000000016, 20, buffer, out length, out point));
      CHECK_EQ("2", CStr(buffer));
      CHECK_EQ(-19, point);

      CHECK(FastFixedDtoa(0.6, 0, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.96, 1, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.996, 2, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.9996, 3, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.99996, 4, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.999996, 5, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.9999996, 6, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.99999996, 7, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.999999996, 8, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.9999999996, 9, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.99999999996, 10, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.999999999996, 11, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.9999999999996, 12, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.99999999999996, 13, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.999999999999996, 14, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.9999999999999996, 15, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(0.00999999999999996, 16, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-1, point);

      CHECK(FastFixedDtoa(0.000999999999999996, 17, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-2, point);

      CHECK(FastFixedDtoa(0.0000999999999999996, 18, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-3, point);

      CHECK(FastFixedDtoa(0.00000999999999999996, 19, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-4, point);

      CHECK(FastFixedDtoa(0.000000999999999999996, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-5, point);

      CHECK(FastFixedDtoa(323423.234234, 10, buffer, out length, out point));
      CHECK_EQ("323423234234", CStr(buffer));
      CHECK_EQ(6, point);

      CHECK(FastFixedDtoa(12345678.901234, 4, buffer, out length, out point));
      CHECK_EQ("123456789012", CStr(buffer));
      CHECK_EQ(8, point);

      CHECK(FastFixedDtoa(98765.432109, 5, buffer, out length, out point));
      CHECK_EQ("9876543211", CStr(buffer));
      CHECK_EQ(5, point);

      CHECK(FastFixedDtoa(42, 20, buffer, out length, out point));
      CHECK_EQ("42", CStr(buffer));
      CHECK_EQ(2, point);

      CHECK(FastFixedDtoa(0.5, 0, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(1, point);

      CHECK(FastFixedDtoa(1e-23, 10, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-10, point);

      CHECK(FastFixedDtoa(1e-123, 2, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-2, point);

      CHECK(FastFixedDtoa(1e-123, 0, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(0, point);

      CHECK(FastFixedDtoa(1e-23, 20, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-20, point);

      CHECK(FastFixedDtoa(1e-21, 20, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-20, point);

      CHECK(FastFixedDtoa(1e-22, 20, buffer, out length, out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-20, point);

      CHECK(FastFixedDtoa(6e-21, 20, buffer, out length, out point));
      CHECK_EQ("1", CStr(buffer));
      CHECK_EQ(-19, point);

      CHECK(FastFixedDtoa(9.1193616301674545152000000e+19, 0, buffer, out length,
                          out point));
      CHECK_EQ("91193616301674545152", CStr(buffer));
      CHECK_EQ(20, point);

      CHECK(FastFixedDtoa(4.8184662102767651659096515e-04, 19, buffer, out length,
                          out point));
      CHECK_EQ("4818466210276765", CStr(buffer));
      CHECK_EQ(-3, point);

      CHECK(FastFixedDtoa(1.9023164229540652612705182e-23, 8, buffer, out length,
                          out point));
      CHECK_EQ("", CStr(buffer));
      CHECK_EQ(-8, point);

      CHECK(FastFixedDtoa(1000000000000000128.0, 0, buffer, out length, out point));
      CHECK_EQ("1000000000000000128", CStr(buffer));
      CHECK_EQ(19, point);
    }

    [Fact]
    public void FastFixedDtoaGayFixed()
    {
      char[] buffer = new char[kBufferSize];
      bool status;
      int length;
      int point;

      ReadOnlySpan<PrecomputedFixed> precomputed =
          PrecomputedFixedRepresentations();
      for (int i = 0; i < precomputed.Length; ++i) {
        PrecomputedFixed current_test = precomputed[i];
        double v = current_test.v;
        int number_digits = current_test.number_digits;
        status = FastFixedDtoa(v, number_digits, buffer, out length, out point);
        CHECK(status);
        CHECK_EQ(current_test.decimal_point, point);
        CHECK_GE(number_digits, length - point);
        CHECK_EQ(current_test.representation, CStr(buffer));
      }
    }
}
