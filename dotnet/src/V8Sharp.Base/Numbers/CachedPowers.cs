// Port of src/base/numbers/cached-powers.h and cached-powers.cc.
// The table is converted mechanically from kCachedPowers.

namespace V8Sharp.Base.Numbers;

public static class PowersOfTenCache
{
    // Not all powers of ten are cached. The decimal exponent of two neighboring
    // cached numbers will differ by kDecimalExponentDistance.
    public const int kDecimalExponentDistance = 8;
    public const int kMinDecimalExponent = -348;
    public const int kMaxDecimalExponent = 340;

    const int kCachedPowersOffset = 348;  // -1 * the first decimal_exponent.
    const double kD_1_LOG2_10 = 0.30102999566398114;  //  1 / lg(10)

    static ReadOnlySpan<ulong> CachedSignificands => [
        0xFA8F_D5A0_081C_0288,
        0xBAAE_E17F_A23E_BF76,
        0x8B16_FB20_3055_AC76,
        0xCF42_894A_5DCE_35EA,
        0x9A6B_B0AA_5565_3B2D,
        0xE61A_CF03_3D1A_45DF,
        0xAB70_FE17_C79A_C6CA,
        0xFF77_B1FC_BEBC_DC4F,
        0xBE56_91EF_416B_D60C,
        0x8DD0_1FAD_907F_FC3C,
        0xD351_5C28_3155_9A83,
        0x9D71_AC8F_ADA6_C9B5,
        0xEA9C_2277_23EE_8BCB,
        0xAECC_4991_4078_536D,
        0x823C_1279_5DB6_CE57,
        0xC210_9436_4DFB_5637,
        0x9096_EA6F_3848_984F,
        0xD774_85CB_2582_3AC7,
        0xA086_CFCD_97BF_97F4,
        0xEF34_0A98_172A_ACE5,
        0xB238_67FB_2A35_B28E,
        0x84C8_D4DF_D2C6_3F3B,
        0xC5DD_4427_1AD3_CDBA,
        0x936B_9FCE_BB25_C996,
        0xDBAC_6C24_7D62_A584,
        0xA3AB_6658_0D5F_DAF6,
        0xF3E2_F893_DEC3_F126,
        0xB5B5_ADA8_AAFF_80B8,
        0x8762_5F05_6C7C_4A8B,
        0xC9BC_FF60_34C1_3053,
        0x964E_858C_91BA_2655,
        0xDFF9_7724_7029_7EBD,
        0xA6DF_BD9F_B8E5_B88F,
        0xF8A9_5FCF_8874_7D94,
        0xB944_7093_8FA8_9BCF,
        0x8A08_F0F8_BF0F_156B,
        0xCDB0_2555_6531_31B6,
        0x993F_E2C6_D07B_7FAC,
        0xE45C_10C4_2A2B_3B06,
        0xAA24_2499_6973_92D3,
        0xFD87_B5F2_8300_CA0E,
        0xBCE5_0864_9211_1AEB,
        0x8CBC_CC09_6F50_88CC,
        0xD1B7_1758_E219_652C,
        0x9C40_0000_0000_0000,
        0xE8D4_A510_0000_0000,
        0xAD78_EBC5_AC62_0000,
        0x813F_3978_F894_0984,
        0xC097_CE7B_C907_15B3,
        0x8F7E_32CE_7BEA_5C70,
        0xD5D2_38A4_ABE9_8068,
        0x9F4F_2726_179A_2245,
        0xED63_A231_D4C4_FB27,
        0xB0DE_6538_8CC8_ADA8,
        0x83C7_088E_1AAB_65DB,
        0xC45D_1DF9_4271_1D9A,
        0x924D_692C_A61B_E758,
        0xDA01_EE64_1A70_8DEA,
        0xA26D_A399_9AEF_774A,
        0xF209_787B_B47D_6B85,
        0xB454_E4A1_79DD_1877,
        0x865B_8692_5B9B_C5C2,
        0xC835_53C5_C896_5D3D,
        0x952A_B45C_FA97_A0B3,
        0xDE46_9FBD_99A0_5FE3,
        0xA59B_C234_DB39_8C25,
        0xF6C6_9A72_A398_9F5C,
        0xB7DC_BF53_54E9_BECE,
        0x88FC_F317_F222_41E2,
        0xCC20_CE9B_D35C_78A5,
        0x9816_5AF3_7B21_53DF,
        0xE2A0_B5DC_971F_303A,
        0xA8D9_D153_5CE3_B396,
        0xFB9B_7CD9_A4A7_443C,
        0xBB76_4C4C_A7A4_4410,
        0x8BAB_8EEF_B640_9C1A,
        0xD01F_EF10_A657_842C,
        0x9B10_A4E5_E991_3129,
        0xE710_9BFB_A19C_0C9D,
        0xAC28_20D9_623B_F429,
        0x8044_4B5E_7AA7_CF85,
        0xBF21_E440_03AC_DD2D,
        0x8E67_9C2F_5E44_FF8F,
        0xD433_179D_9C8C_B841,
        0x9E19_DB92_B4E3_1BA9,
        0xEB96_BF6E_BADF_77D9,
        0xAF87_023B_9BF0_EE6B,
    ];

    static ReadOnlySpan<short> CachedBinaryExponents => [-1220, -1193, -1166, -1140, -1113, -1087, -1060, -1034, -1007, -980, -954, -927, -901, -874, -847, -821, -794, -768, -741, -715, -688, -661, -635, -608, -582, -555, -529, -502, -475, -449, -422, -396, -369, -343, -316, -289, -263, -236, -210, -183, -157, -130, -103, -77, -50, -24, 3, 30, 56, 83, 109, 136, 162, 189, 216, 242, 269, 295, 322, 348, 375, 402, 428, 455, 481, 508, 534, 561, 588, 614, 641, 667, 694, 720, 747, 774, 800, 827, 853, 880, 907, 933, 960, 986, 1013, 1039, 1066];

    static ReadOnlySpan<short> CachedDecimalExponents => [-348, -340, -332, -324, -316, -308, -300, -292, -284, -276, -268, -260, -252, -244, -236, -228, -220, -212, -204, -196, -188, -180, -172, -164, -156, -148, -140, -132, -124, -116, -108, -100, -92, -84, -76, -68, -60, -52, -44, -36, -28, -20, -12, -4, 4, 12, 20, 28, 36, 44, 52, 60, 68, 76, 84, 92, 100, 108, 116, 124, 132, 140, 148, 156, 164, 172, 180, 188, 196, 204, 212, 220, 228, 236, 244, 252, 260, 268, 276, 284, 292, 300, 308, 316, 324, 332, 340];

    /// <summary>Returns a cached power-of-ten with a binary exponent in the
    /// range [minExponent; maxExponent] (boundaries included).</summary>
    public static void GetCachedPowerForBinaryExponentRange(int minExponent, int maxExponent, out DiyFp power, out int decimalExponent)
    {
        int kQ = DiyFp.kSignificandSize;
        double k = Math.Ceiling((minExponent + kQ - 1) * kD_1_LOG2_10);
        int foo = kCachedPowersOffset;
        int index = (foo + (int)k - 1) / kDecimalExponentDistance + 1;
        Debug.Assert(0 <= index && index < CachedSignificands.Length);
        Debug.Assert(minExponent <= CachedBinaryExponents[index]);
        Debug.Assert(CachedBinaryExponents[index] <= maxExponent);
        decimalExponent = CachedDecimalExponents[index];
        power = new DiyFp(CachedSignificands[index], CachedBinaryExponents[index]);
    }

    /// <summary>Returns a cached power of ten x ~= 10^k such that
    /// k &lt;= decimalExponent &lt; k + kDecimalExponentDistance.</summary>
    public static void GetCachedPowerForDecimalExponent(int requestedExponent, out DiyFp power, out int foundExponent)
    {
        Debug.Assert(kMinDecimalExponent <= requestedExponent);
        Debug.Assert(requestedExponent < kMaxDecimalExponent + kDecimalExponentDistance);
        int index = (requestedExponent + kCachedPowersOffset) / kDecimalExponentDistance;
        power = new DiyFp(CachedSignificands[index], CachedBinaryExponents[index]);
        foundExponent = CachedDecimalExponents[index];
        Debug.Assert(foundExponent <= requestedExponent);
        Debug.Assert(requestedExponent < foundExponent + kDecimalExponentDistance);
    }
}
