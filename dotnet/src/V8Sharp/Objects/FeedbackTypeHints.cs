// Port of src/objects/type-hints.{h,cc} and the feedback-to-hint conversions
// of src/objects/feedback-vector-inl.h (BinaryOperationHintFromFeedback,
// CompareOperationHintFromFeedback, ForInHintFromFeedback).
//
// The static class is FeedbackTypeHints rather than V8's free functions so it does
// not collide with V8Sharp.Interpreter.TypeHints (the bytecode generator's).
using V8Sharp.Interpreter;

namespace V8Sharp.Objects;

/// <summary>V8's BinaryOperationHint.</summary>
public enum BinaryOperationHint : byte
{
    kNone,
    kSignedSmall,
    kSignedSmallInputs,
    kAdditiveSafeInteger,
    kNumber,
    kNumberOrOddball,
    kString,
    kStringOrStringWrapper,
    kBigInt,
    kBigInt64,
    kAny,
}

/// <summary>V8's CompareOperationHint.</summary>
public enum CompareOperationHint : byte
{
    kNone,
    kSignedSmall,
    kNumber,
    kNumberOrBoolean,
    kNumberOrOddball,
    kInternalizedString,
    kString,
    kSymbol,
    kBigInt,
    kBigInt64,
    kReceiver,
    kReceiverOrNullOrUndefined,
    kStringOrOddball,
    kAny,
}

/// <summary>V8's ForInHint.</summary>
public enum ForInHint : byte
{
    kNone,
    kEnumCacheKeysAndIndices,
    kEnumCacheKeys,
    kAny,
}

/// <summary>The feedback-to-hint conversions of feedback-vector-inl.h.</summary>
public static class FeedbackTypeHints
{
    /// <summary>BinaryOperationHintFromFeedback.</summary>
    public static BinaryOperationHint BinaryOperationHintFromFeedback(int typeFeedback) =>
        (BinaryOperationFeedback.Type)typeFeedback switch
        {
            BinaryOperationFeedback.Type.None => BinaryOperationHint.kNone,
            BinaryOperationFeedback.Type.SignedSmall => BinaryOperationHint.kSignedSmall,
            BinaryOperationFeedback.Type.SignedSmallInputs => BinaryOperationHint.kSignedSmallInputs,
            BinaryOperationFeedback.Type.AdditiveSafeInteger => BinaryOperationHint.kAdditiveSafeInteger,
            BinaryOperationFeedback.Type.Number => BinaryOperationHint.kNumber,
            BinaryOperationFeedback.Type.NumberOrOddball => BinaryOperationHint.kNumberOrOddball,
            BinaryOperationFeedback.Type.String => BinaryOperationHint.kString,
            BinaryOperationFeedback.Type.StringOrStringWrapper => BinaryOperationHint.kStringOrStringWrapper,
            BinaryOperationFeedback.Type.BigInt => BinaryOperationHint.kBigInt,
            BinaryOperationFeedback.Type.BigInt64 => BinaryOperationHint.kBigInt64,
            _ => BinaryOperationHint.kAny,
        };

    static bool Is(CompareOperationFeedback.Type feedback, int typeFeedback) => (typeFeedback & ~(int)feedback) == 0;

    /// <summary>CompareOperationHintFromFeedback.</summary>
    public static CompareOperationHint CompareOperationHintFromFeedback(int typeFeedback)
    {
        if (Is(CompareOperationFeedback.Type.None, typeFeedback)) return CompareOperationHint.kNone;

        if (Is(CompareOperationFeedback.Type.SignedSmall, typeFeedback)) return CompareOperationHint.kSignedSmall;
        if (Is(CompareOperationFeedback.Type.Number, typeFeedback)) return CompareOperationHint.kNumber;
        if (Is(CompareOperationFeedback.Type.NumberOrBoolean, typeFeedback)) return CompareOperationHint.kNumberOrBoolean;
        if (Is(CompareOperationFeedback.Type.NumberOrOddball, typeFeedback)) return CompareOperationHint.kNumberOrOddball;

        if (Is(CompareOperationFeedback.Type.InternalizedString, typeFeedback)) return CompareOperationHint.kInternalizedString;
        if (Is(CompareOperationFeedback.Type.String, typeFeedback)) return CompareOperationHint.kString;
        if (Is(CompareOperationFeedback.Type.StringOrOddball, typeFeedback) &&
            !Is(CompareOperationFeedback.Type.Oddball, typeFeedback))
        {
            // Don't return the StringOrOddball feedback for pure oddball comparisons,
            // that would be too confusing.
            return CompareOperationHint.kStringOrOddball;
        }

        if (Is(CompareOperationFeedback.Type.Receiver, typeFeedback)) return CompareOperationHint.kReceiver;
        if (Is(CompareOperationFeedback.Type.ReceiverOrNullOrUndefined, typeFeedback))
        {
            return CompareOperationHint.kReceiverOrNullOrUndefined;
        }

        if (Is(CompareOperationFeedback.Type.BigInt64, typeFeedback)) return CompareOperationHint.kBigInt64;
        if (Is(CompareOperationFeedback.Type.BigInt, typeFeedback)) return CompareOperationHint.kBigInt;

        if (Is(CompareOperationFeedback.Type.Symbol, typeFeedback)) return CompareOperationHint.kSymbol;

        return CompareOperationHint.kAny;
    }

    /// <summary>ForInHintFromFeedback.</summary>
    public static ForInHint ForInHintFromFeedback(ForInFeedback typeFeedback) => typeFeedback switch
    {
        ForInFeedback.kNone => ForInHint.kNone,
        ForInFeedback.kEnumCacheKeys => ForInHint.kEnumCacheKeys,
        ForInFeedback.kEnumCacheKeysAndIndices => ForInHint.kEnumCacheKeysAndIndices,
        _ => ForInHint.kAny,
    };
}
