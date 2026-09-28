// Port of the compile-time parts of src/objects/feedback-vector.h:
// FeedbackSlotKind, FeedbackSlot, FeedbackVectorSpec, SharedFeedbackSlot and
// FeedbackMetadata::GetSlotSize. The bytecode generator allocates feedback
// slots through the spec; the engine builds FeedbackMetadata from it.
//
// TODO(merge): the object-model port owns src/objects/feedback-vector.h. When
// its FeedbackVectorSpec lands, keep one of the two (this one has no heap
// dependencies, so the object model can build FeedbackMetadata from it).
using V8Sharp.Common;

namespace V8Sharp.Interpreter;

public enum FeedbackSlotKind : byte
{
    // This kind means that the slot points to the middle of other slot
    // which occupies more than one feedback vector element.
    // There must be no such slots in the system.
    kInvalid,

    // Sloppy kinds come first, for easy language mode testing.
    kStoreGlobalSloppy,
    kSetNamedSloppy,
    kSetKeyedSloppy,
    kLastSloppyKind = kSetKeyedSloppy,

    // Strict and language mode unaware kinds.
    kCall,
    kLoadProperty,
    kLoadGlobalNotInsideTypeof,
    kLoadGlobalInsideTypeof,
    kLoadKeyed,
    kHasKeyed,
    kStoreGlobalStrict,
    kSetNamedStrict,
    kDefineNamedOwn,
    kDefineKeyedOwn,
    kSetKeyedStrict,
    kStoreInArrayLiteral,
    kBinaryOp,
    kCompareOp,
    kDefineKeyedOwnPropertyInLiteral,
    kLiteral,
    kForIn,
    kInstanceOf,
    kTypeOf,
    kCloneObject,
    kStringAddAndInternalize,
    kJumpLoop,

    kLast = kJumpLoop, // Always update this if the list above changes.
}

/// <summary>A feedback vector slot index (src/objects/feedback-vector.h
/// FeedbackSlot). default(FeedbackSlot) is invalid, as V8's default-constructed slot.</summary>
public readonly struct FeedbackSlot : IEquatable<FeedbackSlot>
{
    // Stored as id + 1 so that the default value is the invalid slot (-1).
    readonly int _idPlusOne;

    public FeedbackSlot(int id) => _idPlusOne = id + 1;

    public static FeedbackSlot Invalid => default;

    public bool IsInvalid() => _idPlusOne == 0;
    public int ToInt() => _idPlusOne - 1;

    /// <summary>FeedbackVector::GetIndex.</summary>
    public static int GetIndex(FeedbackSlot slot) => slot.ToInt();

    public bool Equals(FeedbackSlot other) => _idPlusOne == other._idPlusOne;
    public override bool Equals(object? obj) => obj is FeedbackSlot s && Equals(s);
    public override int GetHashCode() => _idPlusOne;
    public override string ToString() => "#" + ToInt().ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class FeedbackVectorSpec
{
    readonly List<FeedbackSlotKind> _slotKinds = [];
    // A vector containing the parameter count for every create closure slot.
    readonly List<ushort> _createClosureParameterCounts = [];

    public int slot_count() => _slotKinds.Count;
    public int create_closure_slot_count() => _createClosureParameterCounts.Count;

    public int AddCreateClosureParameterCount(ushort parameter_count)
    {
        _createClosureParameterCounts.Add(parameter_count);
        return create_closure_slot_count() - 1;
    }

    public ushort GetCreateClosureParameterCount(int index) => _createClosureParameterCounts[index];

    public FeedbackSlotKind GetKind(FeedbackSlot slot) => _slotKinds[slot.ToInt()];

    public FeedbackSlot AddCallICSlot() => AddSlot(FeedbackSlotKind.kCall);

    public FeedbackSlot AddLoadICSlot() => AddSlot(FeedbackSlotKind.kLoadProperty);

    public FeedbackSlot AddLoadGlobalICSlot(TypeofMode typeof_mode) =>
        AddSlot(typeof_mode == TypeofMode.Inside
                    ? FeedbackSlotKind.kLoadGlobalInsideTypeof
                    : FeedbackSlotKind.kLoadGlobalNotInsideTypeof);

    public FeedbackSlot AddKeyedLoadICSlot() => AddSlot(FeedbackSlotKind.kLoadKeyed);

    public FeedbackSlot AddKeyedHasICSlot() => AddSlot(FeedbackSlotKind.kHasKeyed);

    public static FeedbackSlotKind GetStoreICSlot(LanguageMode language_mode) =>
        language_mode == LanguageMode.Strict ? FeedbackSlotKind.kSetNamedStrict : FeedbackSlotKind.kSetNamedSloppy;

    public FeedbackSlot AddStoreICSlot(LanguageMode language_mode) => AddSlot(GetStoreICSlot(language_mode));

    public FeedbackSlot AddDefineNamedOwnICSlot() => AddSlot(FeedbackSlotKind.kDefineNamedOwn);

    // Similar to DefinedNamedOwn, but will throw if a private field already
    // exists.
    public FeedbackSlot AddDefineKeyedOwnICSlot() => AddSlot(FeedbackSlotKind.kDefineKeyedOwn);

    public FeedbackSlot AddStoreGlobalICSlot(LanguageMode language_mode) =>
        AddSlot(language_mode == LanguageMode.Strict
                    ? FeedbackSlotKind.kStoreGlobalStrict
                    : FeedbackSlotKind.kStoreGlobalSloppy);

    public static FeedbackSlotKind GetKeyedStoreICSlotKind(LanguageMode language_mode) =>
        language_mode == LanguageMode.Strict ? FeedbackSlotKind.kSetKeyedStrict : FeedbackSlotKind.kSetKeyedSloppy;

    public FeedbackSlot AddKeyedStoreICSlot(LanguageMode language_mode) =>
        AddSlot(GetKeyedStoreICSlotKind(language_mode));

    public FeedbackSlot AddStoreInArrayLiteralICSlot() => AddSlot(FeedbackSlotKind.kStoreInArrayLiteral);

    public FeedbackSlot AddBinaryOpICSlot() => AddSlot(FeedbackSlotKind.kBinaryOp);

    public FeedbackSlot AddCompareICSlot() => AddSlot(FeedbackSlotKind.kCompareOp);

    public FeedbackSlot AddForInSlot() => AddSlot(FeedbackSlotKind.kForIn);

    public FeedbackSlot AddInstanceOfSlot() => AddSlot(FeedbackSlotKind.kInstanceOf);

    public FeedbackSlot AddTypeOfSlot() => AddSlot(FeedbackSlotKind.kTypeOf);

    public FeedbackSlot AddLiteralSlot() => AddSlot(FeedbackSlotKind.kLiteral);

    public FeedbackSlot AddDefineKeyedOwnPropertyInLiteralICSlot() =>
        AddSlot(FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral);

    public FeedbackSlot AddCloneObjectSlot() => AddSlot(FeedbackSlotKind.kCloneObject);

    public FeedbackSlot AddJumpLoopSlot() => AddSlot(FeedbackSlotKind.kJumpLoop);

    public FeedbackSlot AddStringAddAndInternalizeICSlot() => AddSlot(FeedbackSlotKind.kStringAddAndInternalize);

    public FeedbackSlot AddSlot(FeedbackSlotKind kind)
    {
        int slot = slot_count();
        int entries_per_slot = GetSlotSize(kind);
        _slotKinds.Add(kind);
        for (int i = 1; i < entries_per_slot; i++) _slotKinds.Add(FeedbackSlotKind.kInvalid);
        return new FeedbackSlot(slot);
    }

    /// <summary>FeedbackMetadata::GetSlotSize.</summary>
    public static int GetSlotSize(FeedbackSlotKind kind)
    {
        switch (kind)
        {
            case FeedbackSlotKind.kForIn:
            case FeedbackSlotKind.kInstanceOf:
            case FeedbackSlotKind.kTypeOf:
            case FeedbackSlotKind.kCompareOp:
            case FeedbackSlotKind.kBinaryOp:
            case FeedbackSlotKind.kLiteral:
                return 1;

            case FeedbackSlotKind.kCall:
            case FeedbackSlotKind.kCloneObject:
            case FeedbackSlotKind.kJumpLoop:
            case FeedbackSlotKind.kLoadProperty:
            case FeedbackSlotKind.kLoadGlobalInsideTypeof:
            case FeedbackSlotKind.kLoadGlobalNotInsideTypeof:
            case FeedbackSlotKind.kLoadKeyed:
            case FeedbackSlotKind.kHasKeyed:
            case FeedbackSlotKind.kSetNamedSloppy:
            case FeedbackSlotKind.kSetNamedStrict:
            case FeedbackSlotKind.kDefineNamedOwn:
            case FeedbackSlotKind.kDefineKeyedOwn:
            case FeedbackSlotKind.kStoreGlobalSloppy:
            case FeedbackSlotKind.kStoreGlobalStrict:
            case FeedbackSlotKind.kSetKeyedSloppy:
            case FeedbackSlotKind.kSetKeyedStrict:
            case FeedbackSlotKind.kStoreInArrayLiteral:
            case FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral:
            case FeedbackSlotKind.kStringAddAndInternalize:
                return 2;

            default:
                throw new UnreachableException();
        }
    }
}

/// <summary>Helper class that creates a feedback slot on-demand.</summary>
public struct SharedFeedbackSlot(FeedbackVectorSpec spec, FeedbackSlotKind kind)
{
    FeedbackSlot _slot;

    public FeedbackSlot Get()
    {
        if (_slot.IsInvalid()) _slot = spec.AddSlot(kind);
        return _slot;
    }
}
