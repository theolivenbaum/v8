// Port of test/unittests/base/double-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Base;

public class DoubleTest
{
    [Fact]
    public void Uint64Conversions()
    {
      // Start by checking the byte-order.
      ulong ordered = 0x0123_4567_89AB_CDEF;
      CHECK_EQ(3512700564088504e-318, new Double(ordered).Value);

      ulong min_double64 = 0x0000_0000_0000_0001;
      CHECK_EQ(5e-324, new Double(min_double64).Value);

      ulong max_double64 = 0x7FEF_FFFF_FFFF_FFFF;
      CHECK_EQ(1.7976931348623157e308, new Double(max_double64).Value);
    }

    [Fact]
    public void AsDiyFp()
    {
      ulong ordered = 0x0123_4567_89AB_CDEF;
      DiyFp diy_fp = new Double(ordered).AsDiyFp();
      CHECK_EQ(0x12 - 0x3FF - 52, diy_fp.E);
      // The 52 mantissa bits, plus the implicit 1 in bit 52 as a UINT64.
      CHECK(0x0013_4567_89AB_CDEF == diy_fp.F);  // NOLINT

      ulong min_double64 = 0x0000_0000_0000_0001;
      diy_fp = new Double(min_double64).AsDiyFp();
      CHECK_EQ(-0x3FF - 52 + 1, diy_fp.E);
      // This is a denormal; so no hidden bit.
      CHECK_EQ(1, diy_fp.F);

      ulong max_double64 = 0x7FEF_FFFF_FFFF_FFFF;
      diy_fp = new Double(max_double64).AsDiyFp();
      CHECK_EQ(0x7FE - 0x3FF - 52, diy_fp.E);
      CHECK(0x001F_FFFF_FFFF_FFFF == diy_fp.F);  // NOLINT
    }

    [Fact]
    public void AsNormalizedDiyFp()
    {
      ulong ordered = 0x0123_4567_89AB_CDEF;
      DiyFp diy_fp = new Double(ordered).AsNormalizedDiyFp();
      CHECK_EQ(0x12 - 0x3FF - 52 - 11, diy_fp.E);
      CHECK((((ulong)0x0013_4567_89AB_CDEF) << 11) == diy_fp.F);  // NOLINT

      ulong min_double64 = 0x0000_0000_0000_0001;
      diy_fp = new Double(min_double64).AsNormalizedDiyFp();
      CHECK_EQ(-0x3FF - 52 + 1 - 63, diy_fp.E);
      // This is a denormal; so no hidden bit.
      CHECK(0x8000_0000_0000_0000 == diy_fp.F);  // NOLINT

      ulong max_double64 = 0x7FEF_FFFF_FFFF_FFFF;
      diy_fp = new Double(max_double64).AsNormalizedDiyFp();
      CHECK_EQ(0x7FE - 0x3FF - 52 - 11, diy_fp.E);
      CHECK((((ulong)0x001F_FFFF_FFFF_FFFF) << 11) == diy_fp.F);
    }

    [Fact]
    public void IsDenormal()
    {
      ulong min_double64 = 0x0000_0000_0000_0001;
      CHECK(new Double(min_double64).IsDenormal);
      ulong bits = 0x000F_FFFF_FFFF_FFFF;
      CHECK(new Double(bits).IsDenormal);
      bits = 0x0010_0000_0000_0000;
      CHECK(!new Double(bits).IsDenormal);
    }

    [Fact]
    public void IsSpecial()
    {
      CHECK(new Double(double.PositiveInfinity).IsSpecial);
      CHECK(new Double(-double.PositiveInfinity).IsSpecial);
      CHECK(new Double(double.NaN).IsSpecial);
      ulong bits = 0xFFF1_2345_0000_0000;
      CHECK(new Double(bits).IsSpecial);
      // Denormals are not special:
      CHECK(!new Double(5e-324).IsSpecial);
      CHECK(!new Double(-5e-324).IsSpecial);
      // And some random numbers:
      CHECK(!new Double(0.0).IsSpecial);
      CHECK(!new Double(-0.0).IsSpecial);
      CHECK(!new Double(1.0).IsSpecial);
      CHECK(!new Double(-1.0).IsSpecial);
      CHECK(!new Double(1000000.0).IsSpecial);
      CHECK(!new Double(-1000000.0).IsSpecial);
      CHECK(!new Double(1e23).IsSpecial);
      CHECK(!new Double(-1e23).IsSpecial);
      CHECK(!new Double(1.7976931348623157e308).IsSpecial);
      CHECK(!new Double(-1.7976931348623157e308).IsSpecial);
    }

    [Fact]
    public void IsInfinite()
    {
      CHECK(new Double(double.PositiveInfinity).IsInfinite);
      CHECK(new Double(-double.PositiveInfinity).IsInfinite);
      CHECK(!new Double(double.NaN).IsInfinite);
      CHECK(!new Double(0.0).IsInfinite);
      CHECK(!new Double(-0.0).IsInfinite);
      CHECK(!new Double(1.0).IsInfinite);
      CHECK(!new Double(-1.0).IsInfinite);
      ulong min_double64 = 0x0000_0000_0000_0001;
      CHECK(!new Double(min_double64).IsInfinite);
    }

    [Fact]
    public void Sign()
    {
      CHECK_EQ(1, new Double(1.0).Sign);
      CHECK_EQ(1, new Double(double.PositiveInfinity).Sign);
      CHECK_EQ(-1, new Double(-double.PositiveInfinity).Sign);
      CHECK_EQ(1, new Double(0.0).Sign);
      CHECK_EQ(-1, new Double(-0.0).Sign);
      ulong min_double64 = 0x0000_0000_0000_0001;
      CHECK_EQ(1, new Double(min_double64).Sign);
    }

    [Fact]
    public void NormalizedBoundaries()
    {
      DiyFp boundary_plus = default;
      DiyFp boundary_minus = default;
      DiyFp diy_fp = new Double(1.5).AsNormalizedDiyFp();
      new Double(1.5).NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      // 1.5 does not have a significand of the form 2^p (for some p).
      // Therefore its boundaries are at the same distance.
      CHECK(diy_fp.F - boundary_minus.F == boundary_plus.F - diy_fp.F);
      CHECK((1 << 10) == diy_fp.F - boundary_minus.F);

      diy_fp = new Double(1.0).AsNormalizedDiyFp();
      new Double(1.0).NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      // 1.0 does have a significand of the form 2^p (for some p).
      // Therefore its lower boundary is twice as close as the upper boundary.
      CHECK_GT(boundary_plus.F - diy_fp.F, diy_fp.F - boundary_minus.F);
      CHECK((1 << 9) == diy_fp.F - boundary_minus.F);
      CHECK((1 << 10) == boundary_plus.F - diy_fp.F);

      ulong min_double64 = 0x0000_0000_0000_0001;
      diy_fp = new Double(min_double64).AsNormalizedDiyFp();
      new Double(min_double64).NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      // min-value does not have a significand of the form 2^p (for some p).
      // Therefore its boundaries are at the same distance.
      CHECK(diy_fp.F - boundary_minus.F == boundary_plus.F - diy_fp.F);
      // Denormals have their boundaries much closer.
      CHECK((((ulong)1) << 62) == diy_fp.F - boundary_minus.F);

      ulong smallest_normal64 = 0x0010_0000_0000_0000;
      diy_fp = new Double(smallest_normal64).AsNormalizedDiyFp();
      new Double(smallest_normal64)
          .NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      // Even though the significand is of the form 2^p (for some p), its boundaries
      // are at the same distance. (This is the only exception).
      CHECK(diy_fp.F - boundary_minus.F == boundary_plus.F - diy_fp.F);
      CHECK((1 << 10) == diy_fp.F - boundary_minus.F);

      ulong largest_denormal64 = 0x000F_FFFF_FFFF_FFFF;
      diy_fp = new Double(largest_denormal64).AsNormalizedDiyFp();
      new Double(largest_denormal64)
          .NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      CHECK(diy_fp.F - boundary_minus.F == boundary_plus.F - diy_fp.F);
      CHECK((1 << 11) == diy_fp.F - boundary_minus.F);

      ulong max_double64 = 0x7FEF_FFFF_FFFF_FFFF;
      diy_fp = new Double(max_double64).AsNormalizedDiyFp();
      new Double(max_double64).NormalizedBoundaries(out boundary_minus, out boundary_plus);
      CHECK_EQ(diy_fp.E, boundary_minus.E);
      CHECK_EQ(diy_fp.E, boundary_plus.E);
      // max-value does not have a significand of the form 2^p (for some p).
      // Therefore its boundaries are at the same distance.
      CHECK(diy_fp.F - boundary_minus.F == boundary_plus.F - diy_fp.F);
      CHECK((1 << 10) == diy_fp.F - boundary_minus.F);
    }

    [Fact]
    public void NextDouble()
    {
      CHECK_EQ(4e-324, new Double(0.0).NextDouble());
      CHECK_EQ(0.0, new Double(-0.0).NextDouble());
      CHECK_EQ(-0.0, new Double(-4e-324).NextDouble());
      Double d0 = new(-4e-324);
      Double d1 = new(d0.NextDouble());
      Double d2 = new(d1.NextDouble());
      CHECK_EQ(-0.0, d1.Value);
      CHECK_EQ(0.0, d2.Value);
      CHECK_EQ(4e-324, d2.NextDouble());
      CHECK_EQ(-1.7976931348623157e308, new Double(-double.PositiveInfinity).NextDouble());
      CHECK_EQ(double.PositiveInfinity, new Double(((ulong)0x7FEF_FFFF_FFFF_FFFF)).NextDouble());
    }
}
