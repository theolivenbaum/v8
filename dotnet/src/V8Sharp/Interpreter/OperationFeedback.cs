// Port of BinaryOperationFeedback and CompareOperationFeedback from
// src/common/globals.h: the feedback lattices whose type indices are embedded
// in the bytecode (OperandType::kEmbeddedFeedback).
//
// TODO(merge): shared with the IC / feedback-vector port; move to a common
// namespace when one exists.
namespace V8Sharp.Interpreter;

public static class BinaryOperationFeedback
{
    //   kSignedSmall -> kSignedSmallInputs -> kAdditiveSafeInteger
    //                                      -> kNumber ->  kNumberOrOddball -> kAny
    //                                                     kString          -> kAny
    //                                        kBigInt64 -> kBigInt          -> kAny
    public enum Type : uint
    {
        None = 0x0,
        SignedSmall = 0x1,
        SignedSmallInputs = 0x3,
        AdditiveSafeInteger = 0x7,
        Number = 0xF,
        NumberOrOddball = 0x1F,
        BigInt64 = 0x20,
        BigInt = 0x60,
        String = 0x80,
        StringWrapper = 0x100,
        StringOrStringWrapper = 0x180,
        Any = 0x1FF,
    }

    /// <summary>BINARY_OPERATION_FEEDBACK_TYPES, the values embedded in bytecode.</summary>
    public enum TypeIndex : byte
    {
        None,
        SignedSmall,
        SignedSmallInputs,
        AdditiveSafeInteger,
        Number,
        NumberOrOddball,
        BigInt64,
        BigInt,
        String,
        StringWrapper,
        StringOrStringWrapper,
        Any,
    }

    public const uint kNumTypeIndices = (uint)TypeIndex.Any + 1;

    static readonly Type[] s_types =
    [
        Type.None, Type.SignedSmall, Type.SignedSmallInputs, Type.AdditiveSafeInteger, Type.Number,
        Type.NumberOrOddball, Type.BigInt64, Type.BigInt, Type.String, Type.StringWrapper,
        Type.StringOrStringWrapper, Type.Any,
    ];

    static readonly string[] s_names =
    [
        "None", "SignedSmall", "SignedSmallInputs", "AdditiveSafeInteger", "Number", "NumberOrOddball",
        "BigInt64", "BigInt", "String", "StringWrapper", "StringOrStringWrapper", "Any",
    ];

    public static Type DecodeTypeIndex(TypeIndex index) =>
        (uint)index < kNumTypeIndices ? s_types[(int)index] : Type.Any;

    public static string TypeIndexToString(TypeIndex index) =>
        (uint)index < kNumTypeIndices ? s_names[(int)index] : "Unknown";

    public static TypeIndex CalculateTypeIndex(uint feedbackValue)
    {
        for (int i = 0; i < s_types.Length; i++)
        {
            if (((uint)s_types[i] & feedbackValue) == feedbackValue) return (TypeIndex)i;
        }
        return TypeIndex.Any;
    }

    public static TypeIndex CombineTypeIndex(TypeIndex a, TypeIndex b) =>
        CalculateTypeIndex((uint)DecodeTypeIndex(a) | (uint)DecodeTypeIndex(b));
}

public static class CompareOperationFeedback
{
    const uint kSignedSmallFlag = 1 << 0;
    const uint kOtherNumberFlag = 1 << 1;
    const uint kBooleanFlag = 1 << 2;
    const uint kNullOrUndefinedFlag = 1 << 3;
    const uint kInternalizedStringFlag = 1 << 4;
    const uint kOtherStringFlag = 1 << 5;
    const uint kSymbolFlag = 1 << 6;
    const uint kBigInt64Flag = 1 << 7;
    const uint kOtherBigIntFlag = 1 << 8;
    const uint kReceiverFlag = 1 << 9;
    const uint kAnyMask = 0x3FF;

    public enum Type : uint
    {
        None = 0,
        Boolean = kBooleanFlag,
        NullOrUndefined = kNullOrUndefinedFlag,
        Oddball = Boolean | NullOrUndefined,
        SignedSmall = kSignedSmallFlag,
        Number = SignedSmall | kOtherNumberFlag,
        NumberOrBoolean = Number | Boolean,
        NumberOrOddball = Number | Oddball,
        InternalizedString = kInternalizedStringFlag,
        String = InternalizedString | kOtherStringFlag,
        StringOrOddball = String | Oddball,
        Receiver = kReceiverFlag,
        ReceiverOrNullOrUndefined = Receiver | NullOrUndefined,
        BigInt64 = kBigInt64Flag,
        BigInt = kBigInt64Flag | kOtherBigIntFlag,
        Symbol = kSymbolFlag,
        Any = kAnyMask,
    }

    /// <summary>COMPARE_OPERATION_FEEDBACK_TYPES, the values embedded in bytecode.</summary>
    public enum TypeIndex : byte
    {
        None,
        Boolean,
        NullOrUndefined,
        Oddball,
        SignedSmall,
        Number,
        NumberOrBoolean,
        NumberOrOddball,
        InternalizedString,
        String,
        StringOrOddball,
        Receiver,
        ReceiverOrNullOrUndefined,
        BigInt64,
        BigInt,
        Symbol,
        Any,
    }

    public const uint kNumTypeIndices = (uint)TypeIndex.Any + 1;

    static readonly Type[] s_types =
    [
        Type.None, Type.Boolean, Type.NullOrUndefined, Type.Oddball, Type.SignedSmall, Type.Number,
        Type.NumberOrBoolean, Type.NumberOrOddball, Type.InternalizedString, Type.String,
        Type.StringOrOddball, Type.Receiver, Type.ReceiverOrNullOrUndefined, Type.BigInt64, Type.BigInt,
        Type.Symbol, Type.Any,
    ];

    static readonly string[] s_names =
    [
        "None", "Boolean", "NullOrUndefined", "Oddball", "SignedSmall", "Number", "NumberOrBoolean",
        "NumberOrOddball", "InternalizedString", "String", "StringOrOddball", "Receiver",
        "ReceiverOrNullOrUndefined", "BigInt64", "BigInt", "Symbol", "Any",
    ];

    public static Type DecodeTypeIndex(TypeIndex index) =>
        (uint)index < kNumTypeIndices ? s_types[(int)index] : Type.Any;

    public static string TypeIndexToString(TypeIndex index) =>
        (uint)index < kNumTypeIndices ? s_names[(int)index] : "Unknown";

    public static TypeIndex CalculateTypeIndex(uint feedbackValue)
    {
        for (int i = 0; i < s_types.Length; i++)
        {
            if (((uint)s_types[i] & feedbackValue) == feedbackValue) return (TypeIndex)i;
        }
        return TypeIndex.Any;
    }

    public static TypeIndex CombineTypeIndex(TypeIndex a, TypeIndex b) =>
        CalculateTypeIndex((uint)DecodeTypeIndex(a) | (uint)DecodeTypeIndex(b));
}
