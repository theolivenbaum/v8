// Port of test/unittests/numbers/diy-fp-unittest.cc.
// Converted mechanically from the gtest source, then fixed up by hand.

using V8Sharp.Base.Numbers;
using V8Sharp.Base.Tests;
using static V8Sharp.Base.Tests.V8Check;
using static V8Sharp.Base.Numbers.DoubleConversion;
using Double = V8Sharp.Base.Numbers.Double;

namespace V8Sharp.Base.Tests.Numbers;

public class DiyFpTest
{
    [Fact]
    public void Subtract()
    {
      DiyFp diy_fp1 = new DiyFp(3, 0);
      DiyFp diy_fp2 = new DiyFp(1, 0);
      DiyFp diff = DiyFp.Minus(diy_fp1, diy_fp2);

      CHECK_EQ(2, diff.F);
      CHECK_EQ(0, diff.E);
      diy_fp1.Subtract(diy_fp2);
      CHECK_EQ(2, diy_fp1.F);
      CHECK_EQ(0, diy_fp1.E);
    }

    [Fact]
    public void Multiply()
    {
      DiyFp diy_fp1 = new DiyFp(3, 0);
      DiyFp diy_fp2 = new DiyFp(2, 0);
      DiyFp product = DiyFp.Times(diy_fp1, diy_fp2);

      CHECK_EQ(0, product.F);
      CHECK_EQ(64, product.E);
      diy_fp1.Multiply(diy_fp2);
      CHECK_EQ(0, diy_fp1.F);
      CHECK_EQ(64, diy_fp1.E);

      diy_fp1 = new DiyFp(0x8000_0000_0000_0000, 11);
      diy_fp2 = new DiyFp(2, 13);
      product = DiyFp.Times(diy_fp1, diy_fp2);
      CHECK_EQ(1, product.F);
      CHECK_EQ(11 + 13 + 64, product.E);

      // Test rounding.
      diy_fp1 = new DiyFp(0x8000_0000_0000_0001, 11);
      diy_fp2 = new DiyFp(1, 13);
      product = DiyFp.Times(diy_fp1, diy_fp2);
      CHECK_EQ(1, product.F);
      CHECK_EQ(11 + 13 + 64, product.E);

      diy_fp1 = new DiyFp(0x7FFF_FFFF_FFFF_FFFF, 11);
      diy_fp2 = new DiyFp(1, 13);
      product = DiyFp.Times(diy_fp1, diy_fp2);
      CHECK_EQ(0, product.F);
      CHECK_EQ(11 + 13 + 64, product.E);

      // Halfway cases are allowed to round either way. So don't check for it.

      // Big numbers.
      diy_fp1 = new DiyFp(0xFFFF_FFFF_FFFF_FFFF, 11);
      diy_fp2 = new DiyFp(0xFFFF_FFFF_FFFF_FFFF, 13);
      // 128bit result: 0xFFFFFFFFFFFFFFFE0000000000000001
      product = DiyFp.Times(diy_fp1, diy_fp2);
      CHECK_EQ(0xFFFF_FFFF_FFFF_FFFE, product.F);
      CHECK_EQ(11 + 13 + 64, product.E);
    }
}
