// Port of src/objects/bigint.h (representation). The digit arithmetic is
// ported from src/bigint into V8Sharp.Base; this class holds sign and digits
// as V8's BigInt does (64-bit digits, little-endian, no leading zero digits).
namespace V8Sharp.Objects;

public sealed class BigInt : HeapObject
{
    public const int kMaxLengthBits = 1 << 30;
    public const int kMaxLength = kMaxLengthBits / 64;

    public BigInt(bool sign, ulong[] digits) : base(InstanceType.BigIntType)
    {
        Sign = sign;
        Digits = digits;
    }

    /// <summary>True for negative values. Zero is never negative.</summary>
    public bool Sign { get; }

    /// <summary>Magnitude, least significant digit first, normalized.</summary>
    public ulong[] Digits { get; }

    public int Length => Digits.Length;
    public bool IsZero => Digits.Length == 0;
}
