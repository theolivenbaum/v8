// Port of src/objects/feedback-vector.{h,cc,-inl.h} (FeedbackMetadata,
// ClosureFeedbackCellArray, FeedbackVector, FeedbackNexus, FeedbackIterator)
// and the InlineCacheState / feedback enums of src/common/globals.h.
//
// Feedback slots hold JSValues: V8's MaybeObject slots hold Smis, strong
// references and weak references. V8Sharp has no weak references in feedback
// (see deviations.md: maps and cells are held strongly), so a slot is a plain
// JSValue: a number for Smi feedback (call counts, binary-op hints), a heap
// object (Map, handler, FixedArray of map/handler pairs, AllocationSite,
// PropertyCell, JSFunction ...) or one of the sentinels. V8's cleared weak
// value (kClearedWeakValue) is the dedicated ClearedValue sentinel.
using System.Runtime.CompilerServices;
using V8Sharp.Interpreter;

namespace V8Sharp.Objects;

/// <summary>V8's InlineCacheState (src/common/globals.h).</summary>
public enum InlineCacheState
{
    /// <summary>No feedback will be collected.</summary>
    NO_FEEDBACK,
    /// <summary>Has never been executed.</summary>
    UNINITIALIZED,
    /// <summary>Has been executed and only one receiver type has been seen.</summary>
    MONOMORPHIC,
    /// <summary>Check failed due to prototype (or map deprecation).</summary>
    RECOMPUTE_HANDLER,
    /// <summary>Multiple receiver types have been seen.</summary>
    POLYMORPHIC,
    /// <summary>Many DOM receiver types have been seen for the same accessor.</summary>
    MEGADOM,
    /// <summary>Many receiver types have been seen with the same handler.</summary>
    HOMOMORPHIC,
    /// <summary>Many receiver types have been seen.</summary>
    MEGAMORPHIC,
    /// <summary>A generic handler is installed and no extra typefeedback is recorded.</summary>
    GENERIC,
}

/// <summary>V8's UpdateFeedbackMode.</summary>
public enum UpdateFeedbackMode { kOptionalFeedback, kGuaranteedFeedback, kNoFeedback }

/// <summary>V8's ClearBehavior.</summary>
public enum ClearBehavior { kDefault, kClearAll }

/// <summary>V8's IcCheckType.</summary>
public enum IcCheckType { kElement, kProperty }

/// <summary>V8's TypeOfFeedback::Result.</summary>
[Flags]
public enum TypeOfFeedbackResult
{
    kNone = 0,
    kSmi = 1,
    kHeapNumber = 1 << 1,
    kFunction = 1 << 2,
    kString = 1 << 3,
    kNumber = kHeapNumber | kSmi,
    kAny = kSmi | kHeapNumber | kFunction | kString,
}

/// <summary>V8's ForInFeedback.</summary>
public enum ForInFeedback : byte
{
    kNone = 0x0,
    kEnumCacheKeysAndIndices = 0x1,
    kEnumCacheKeys = 0x3,
    kAny = 0x7,
}

/// <summary>V8's SpeculationMode.</summary>
public enum SpeculationMode { kAllowSpeculation = 0, kDisallowBoundsCheckSpeculation = 1, kDisallowSpeculation = 3 }

/// <summary>V8's CallFeedbackContent.</summary>
public enum CallFeedbackContent { kTarget, kReceiver }

/// <summary>V8's FeedbackMetadata: the slot kinds of a function's feedback vector.</summary>
public sealed class FeedbackMetadata : HeapObject
{
    readonly FeedbackSlotKind[] _kinds;
    readonly ushort[] _createClosureParameterCounts;

    FeedbackMetadata(FeedbackSlotKind[] kinds, ushort[] createClosureParameterCounts) : base(InstanceType.FeedbackMetadataType)
    {
        _kinds = kinds;
        _createClosureParameterCounts = createClosureParameterCounts;
    }

    /// <summary>The empty_feedback_metadata root.</summary>
    public static readonly FeedbackMetadata Empty = new([], []);

    public int SlotCount => _kinds.Length;
    public int CreateClosureSlotCount => _createClosureParameterCounts.Length;
    public bool IsEmpty => _kinds.Length == 0;

    public FeedbackSlotKind GetKind(FeedbackSlot slot) => _kinds[slot.ToInt()];
    public FeedbackSlotKind GetKind(int slot) => _kinds[slot];

    public ushort GetCreateClosureParameterCount(int index) => _createClosureParameterCounts[index];

    /// <summary>FeedbackMetadata::New.</summary>
    public static FeedbackMetadata New(FeedbackVectorSpec? spec)
    {
        if (spec is null) return Empty;
        int slotCount = spec.slot_count();
        int createClosureSlotCount = spec.create_closure_slot_count();
        if (slotCount == 0 && createClosureSlotCount == 0) return Empty;
        var kinds = new FeedbackSlotKind[slotCount];
        for (int i = 0; i < slotCount; i++) kinds[i] = spec.GetKind(new FeedbackSlot(i));
        var counts = new ushort[createClosureSlotCount];
        for (int i = 0; i < createClosureSlotCount; i++) counts[i] = spec.GetCreateClosureParameterCount(i);
        return new FeedbackMetadata(kinds, counts);
    }

    /// <summary>FeedbackMetadata::SpecDiffersFrom.</summary>
    public bool SpecDiffersFrom(FeedbackVectorSpec otherSpec)
    {
        if (otherSpec.slot_count() != SlotCount) return true;
        for (int i = 0; i < SlotCount; i++)
        {
            if (otherSpec.GetKind(new FeedbackSlot(i)) != _kinds[i]) return true;
        }
        return false;
    }

    /// <summary>FeedbackMetadata::GetSlotSize.</summary>
    public static int GetSlotSize(FeedbackSlotKind kind) => FeedbackVectorSpec.GetSlotSize(kind);

    /// <summary>FeedbackMetadata::Kind2String.</summary>
    public static string Kind2String(FeedbackSlotKind kind) => kind switch
    {
        FeedbackSlotKind.kInvalid => "Invalid",
        FeedbackSlotKind.kCall => "Call",
        FeedbackSlotKind.kLoadProperty => "LoadProperty",
        FeedbackSlotKind.kLoadGlobalInsideTypeof => "LoadGlobalInsideTypeof",
        FeedbackSlotKind.kLoadGlobalNotInsideTypeof => "LoadGlobalNotInsideTypeof",
        FeedbackSlotKind.kLoadKeyed => "LoadKeyed",
        FeedbackSlotKind.kHasKeyed => "HasKeyed",
        FeedbackSlotKind.kSetNamedSloppy => "SetNamedSloppy",
        FeedbackSlotKind.kSetNamedStrict => "SetNamedStrict",
        FeedbackSlotKind.kDefineNamedOwn => "DefineNamedOwn",
        FeedbackSlotKind.kDefineKeyedOwn => "DefineKeyedOwn",
        FeedbackSlotKind.kStoreGlobalSloppy => "StoreGlobalSloppy",
        FeedbackSlotKind.kStoreGlobalStrict => "StoreGlobalStrict",
        FeedbackSlotKind.kSetKeyedSloppy => "StoreKeyedSloppy",
        FeedbackSlotKind.kSetKeyedStrict => "StoreKeyedStrict",
        FeedbackSlotKind.kStoreInArrayLiteral => "StoreInArrayLiteral",
        FeedbackSlotKind.kBinaryOp => "BinaryOp",
        FeedbackSlotKind.kCompareOp => "CompareOp",
        FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral => "DefineKeyedOwnPropertyInLiteral",
        FeedbackSlotKind.kLiteral => "Literal",
        FeedbackSlotKind.kForIn => "ForIn",
        FeedbackSlotKind.kInstanceOf => "InstanceOf",
        FeedbackSlotKind.kTypeOf => "TypeOf",
        FeedbackSlotKind.kCloneObject => "CloneObject",
        FeedbackSlotKind.kJumpLoop => "JumpLoop",
        FeedbackSlotKind.kStringAddAndInternalize => "StringAddAndInternalize",
        _ => throw new UnreachableException(),
    };

    public static bool IsCallICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kCall;
    public static bool IsLoadICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kLoadProperty;
    public static bool IsLoadGlobalICKind(FeedbackSlotKind kind) =>
        kind is FeedbackSlotKind.kLoadGlobalNotInsideTypeof or FeedbackSlotKind.kLoadGlobalInsideTypeof;
    public static bool IsKeyedLoadICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kLoadKeyed;
    public static bool IsKeyedHasICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kHasKeyed;
    public static bool IsStoreGlobalICKind(FeedbackSlotKind kind) =>
        kind is FeedbackSlotKind.kStoreGlobalSloppy or FeedbackSlotKind.kStoreGlobalStrict;
    public static bool IsSetNamedICKind(FeedbackSlotKind kind) =>
        kind is FeedbackSlotKind.kSetNamedSloppy or FeedbackSlotKind.kSetNamedStrict;
    public static bool IsDefineNamedOwnICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kDefineNamedOwn;
    public static bool IsDefineKeyedOwnICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kDefineKeyedOwn;
    public static bool IsDefineKeyedOwnPropertyInLiteralKind(FeedbackSlotKind kind) =>
        kind == FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral;
    public static bool IsKeyedStoreICKind(FeedbackSlotKind kind) =>
        kind is FeedbackSlotKind.kSetKeyedSloppy or FeedbackSlotKind.kSetKeyedStrict;
    public static bool IsStoreInArrayLiteralICKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kStoreInArrayLiteral;
    public static bool IsGlobalICKind(FeedbackSlotKind kind) => IsLoadGlobalICKind(kind) || IsStoreGlobalICKind(kind);
    public static bool IsCloneObjectKind(FeedbackSlotKind kind) => kind == FeedbackSlotKind.kCloneObject;

    public static TypeofMode GetTypeofModeFromSlotKind(FeedbackSlotKind kind) =>
        kind == FeedbackSlotKind.kLoadGlobalInsideTypeof ? TypeofMode.Inside : TypeofMode.NotInside;

    public static V8Sharp.Common.LanguageMode GetLanguageModeFromSlotKind(FeedbackSlotKind kind) =>
        kind <= FeedbackSlotKind.kLastSloppyKind ? V8Sharp.Common.LanguageMode.Sloppy : V8Sharp.Common.LanguageMode.Strict;
}

/// <summary>V8's ClosureFeedbackCellArray: one FeedbackCell per CreateClosure site of a function.</summary>
public sealed class ClosureFeedbackCellArray : HeapObject
{
    public readonly FeedbackCell[] Cells;

    ClosureFeedbackCellArray(FeedbackCell[] cells) : base(InstanceType.ClosureFeedbackCellArrayType) => Cells = cells;

    /// <summary>The empty_closure_feedback_cell_array root.</summary>
    public static readonly ClosureFeedbackCellArray Empty = new([]);

    public int Length => Cells.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FeedbackCell Get(int index) => Cells[index];

    /// <summary>ClosureFeedbackCellArray::New.</summary>
    public static ClosureFeedbackCellArray New(Isolate isolate, SharedFunctionInfo shared)
    {
        var metadata = (FeedbackMetadata?)shared.FeedbackMetadata ?? FeedbackMetadata.Empty;
        int length = metadata.CreateClosureSlotCount;
        if (length == 0) return Empty;
        var cells = new FeedbackCell[length];
        for (int i = 0; i < length; i++) cells[i] = FeedbackCells.NewNoClosuresCell();
        return new ClosureFeedbackCellArray(cells);
    }
}

/// <summary>FeedbackCell helpers of src/objects/feedback-cell.h (the map states are a field here).</summary>
public static class FeedbackCells
{
    /// <summary>Factory::NewNoClosuresCell.</summary>
    public static FeedbackCell NewNoClosuresCell() => new() { Value = null, InterruptBudget = 0 };

    /// <summary>The ClosureFeedbackCellArray of a cell, if it holds one (or the vector's).</summary>
    public static ClosureFeedbackCellArray? ClosureFeedbackCellArrayOf(FeedbackCell cell) => cell.Value switch
    {
        ClosureFeedbackCellArray array => array,
        FeedbackVector vector => vector.ClosureFeedbackCellArray,
        _ => null,
    };
}

/// <summary>V8's FeedbackVector: the per-closure-site type feedback of a function.</summary>
public sealed class FeedbackVector : HeapObject
{
    /// <summary>FeedbackVector::kMaxOsrUrgency.</summary>
    public const int kMaxOsrUrgency = 6;

    /// <summary>The feedback slots (V8's raw_feedback_slots).</summary>
    public readonly JSValue[] Slots;

    public readonly FeedbackMetadata Metadata;
    public readonly SharedFunctionInfo SharedFunctionInfo;
    public readonly ClosureFeedbackCellArray ClosureFeedbackCellArray;
    public FeedbackCell ParentFeedbackCell;

    public int InvocationCount;
    public byte InvocationCountBeforeStable;
    public byte OsrState;
    /// <summary>
    /// The Maglev code of the closures sharing this vector (V8's
    /// maybe_optimized_code slot; invalidated code is removed).
    /// </summary>
    public Maglev.MaglevCode? MaglevCode;
    /// <summary>The OSR code cache by JumpLoop offset (V8's OSR code cache, per feedback vector).</summary>
    public Dictionary<int, Maglev.MaglevCode>? MaglevOsrCode;
    public ushort Flags;

    FeedbackVector(SharedFunctionInfo shared, FeedbackMetadata metadata, ClosureFeedbackCellArray closureFeedbackCellArray,
        FeedbackCell parentFeedbackCell) : base(InstanceType.FeedbackVectorType)
    {
        SharedFunctionInfo = shared;
        Metadata = metadata;
        ClosureFeedbackCellArray = closureFeedbackCellArray;
        ParentFeedbackCell = parentFeedbackCell;
        Slots = metadata.SlotCount == 0 ? [] : new JSValue[metadata.SlotCount];
    }

    // ---- Sentinels -------------------------------------------------------------

    /// <summary>FeedbackVector::UninitializedSentinel (uninitialized_symbol).</summary>
    public static JSValue UninitializedSentinel => ReadOnlyRoots.uninitialized_symbol;

    /// <summary>FeedbackVector::MegamorphicSentinel (megamorphic_symbol).</summary>
    public static JSValue MegamorphicSentinel => ReadOnlyRoots.megamorphic_symbol;

    /// <summary>FeedbackVector::MegaDOMSentinel (mega_dom_symbol).</summary>
    public static JSValue MegaDOMSentinel => ReadOnlyRoots.mega_dom_symbol;

    /// <summary>V8's kClearedWeakValue: a weak reference whose target died (or was never set).</summary>
    public static readonly HeapObject ClearedValue = new ClearedWeakValue();

    sealed class ClearedWeakValue() : HeapObject(InstanceType.OddballType)
    {
        public override string ToString() => "[cleared]";
    }

    public static bool IsCleared(in JSValue value) => ReferenceEquals(value.HeapObjectOrNull, ClearedValue);

    // ---- Accessors ----------------------------------------------------------------

    public int Length => Slots.Length;
    public bool IsEmpty => Slots.Length == 0;

    public static int GetIndex(FeedbackSlot slot) => slot.ToInt();

    /// <summary>FeedbackVector::ToSlot.</summary>
    public static FeedbackSlot ToSlot(int index) => new(index);

    public FeedbackSlotKind GetKind(FeedbackSlot slot) => Metadata.GetKind(slot);
    public FeedbackSlotKind GetKind(int slot) => Metadata.GetKind(slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue Get(int slot) => Slots[slot];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int slot, JSValue value) => Slots[slot] = value;

    public FeedbackCell GetClosureFeedbackCell(int index) => ClosureFeedbackCellArray.Get(index);

    public int OsrUrgency
    {
        get => OsrState & 0x7;
        set => OsrState = (byte)((OsrState & ~0x7) | (Math.Min(value, kMaxOsrUrgency) & 0x7));
    }

    public void ResetOsrUrgency() => OsrUrgency = 0;
    public void RequestOsrAtNextOpportunity() => OsrUrgency = kMaxOsrUrgency;

    /// <summary>FeedbackVector::New: allocates and initializes the vector, and installs it in the parent cell.</summary>
    public static FeedbackVector New(Isolate isolate, SharedFunctionInfo shared,
        ClosureFeedbackCellArray closureFeedbackCellArray, FeedbackCell parentFeedbackCell)
    {
        var metadata = (FeedbackMetadata?)shared.FeedbackMetadata ?? FeedbackMetadata.Empty;
        var vector = new FeedbackVector(shared, metadata, closureFeedbackCellArray, parentFeedbackCell);
        JSValue[] slots = vector.Slots;
        JSValue uninitialized = UninitializedSentinel;
        int slotCount = metadata.SlotCount;
        for (int i = 0; i < slotCount;)
        {
            FeedbackSlotKind kind = metadata.GetKind(i);
            int entrySize = FeedbackMetadata.GetSlotSize(kind);
            JSValue extraValue = uninitialized;
            switch (kind)
            {
                case FeedbackSlotKind.kLoadGlobalInsideTypeof:
                case FeedbackSlotKind.kLoadGlobalNotInsideTypeof:
                case FeedbackSlotKind.kStoreGlobalSloppy:
                case FeedbackSlotKind.kStoreGlobalStrict:
                    slots[i] = ClearedValue;
                    break;
                case FeedbackSlotKind.kJumpLoop:
                    slots[i] = ClearedValue;
                    extraValue = JSValue.Zero;
                    break;
                case FeedbackSlotKind.kForIn:
                case FeedbackSlotKind.kCompareOp:
                case FeedbackSlotKind.kBinaryOp:
                case FeedbackSlotKind.kTypeOf:
                case FeedbackSlotKind.kLiteral:
                case FeedbackSlotKind.kStringAddAndInternalize:
                    slots[i] = JSValue.Zero;
                    break;
                case FeedbackSlotKind.kCall:
                    slots[i] = uninitialized;
                    extraValue = JSValue.Zero;
                    break;
                case FeedbackSlotKind.kCloneObject:
                case FeedbackSlotKind.kLoadProperty:
                case FeedbackSlotKind.kLoadKeyed:
                case FeedbackSlotKind.kHasKeyed:
                case FeedbackSlotKind.kSetNamedSloppy:
                case FeedbackSlotKind.kSetNamedStrict:
                case FeedbackSlotKind.kDefineNamedOwn:
                case FeedbackSlotKind.kDefineKeyedOwn:
                case FeedbackSlotKind.kSetKeyedSloppy:
                case FeedbackSlotKind.kSetKeyedStrict:
                case FeedbackSlotKind.kStoreInArrayLiteral:
                case FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral:
                case FeedbackSlotKind.kInstanceOf:
                    slots[i] = uninitialized;
                    break;
                default:
                    throw new UnreachableException();
            }
            for (int j = 1; j < entrySize; j++) slots[i + j] = extraValue;
            i += entrySize;
        }
        parentFeedbackCell.Value = vector;
        return vector;
    }

    /// <summary>FeedbackVector::NewForTesting.</summary>
    public static FeedbackVector NewForTesting(Isolate isolate, FeedbackVectorSpec spec)
    {
        FeedbackMetadata metadata = FeedbackMetadata.New(spec);
        SharedFunctionInfo shared = isolate.Factory.NewSharedFunctionInfoForBuiltin(ReadOnlyRoots.empty_string,
            Builtins.Builtin.Illegal, 0, false);
        shared.FeedbackMetadata = metadata;
        ClosureFeedbackCellArray closureFeedbackCellArray = ClosureFeedbackCellArray.New(isolate, shared);
        FeedbackCell parentCell = FeedbackCells.NewNoClosuresCell();
        return New(isolate, shared, closureFeedbackCellArray, parentCell);
    }

    /// <summary>FeedbackVector::ClearSlots.</summary>
    public bool ClearSlots(Isolate isolate, ClearBehavior behavior = ClearBehavior.kDefault)
    {
        bool feedbackUpdated = false;
        for (int i = 0; i < Slots.Length;)
        {
            FeedbackSlotKind kind = GetKind(i);
            var nexus = new FeedbackNexus(isolate, this, new FeedbackSlot(i));
            feedbackUpdated |= nexus.Clear(behavior);
            i += FeedbackMetadata.GetSlotSize(kind);
        }
        return feedbackUpdated;
    }

    public override string ToString() => $"<FeedbackVector[{Slots.Length}]>";
}

/// <summary>V8's FeedbackNexus: a feedback vector plus a slot.</summary>
public readonly struct FeedbackNexus
{
    public readonly Isolate Isolate;
    public readonly FeedbackVector? Vector;
    public readonly FeedbackSlot Slot;
    public readonly FeedbackSlotKind Kind;

    // LEXICAL_MODE_BIT_FIELDS: ContextIndexBits (12), SlotIndexBits (18), ImmutabilityBit (1).
    public const int kContextIndexBits = 12;
    public const int kSlotIndexBits = 18;
    public const int kContextIndexMask = (1 << kContextIndexBits) - 1;
    public const int kSlotIndexShift = kContextIndexBits;
    public const int kSlotIndexMask = (1 << kSlotIndexBits) - 1;
    public const int kImmutabilityShift = kContextIndexBits + kSlotIndexBits;

    // CallCountField etc.: SpeculationModeField (2 bits), CallFeedbackContentField (1), CallCountField (29).
    public const int kSpeculationModeMask = 0x3;
    public const int kCallFeedbackContentShift = 2;
    public const int kCallCountShift = 3;

    /// <summary>kCloneObjectPolymorphicEntrySize.</summary>
    public const int kCloneObjectPolymorphicEntrySize = 2;

    public FeedbackNexus(Isolate isolate, FeedbackVector? vector, FeedbackSlot slot)
    {
        Isolate = isolate;
        Vector = vector;
        Slot = slot;
        Kind = vector is null ? FeedbackSlotKind.kInvalid : vector.GetKind(slot);
    }

    public FeedbackNexus(Isolate isolate, FeedbackVector? vector, int slot) : this(isolate, vector, new FeedbackSlot(slot)) { }

    public bool IsNull => Vector is null;

    int Index => Slot.ToInt();

    public JSValue GetFeedback() => Vector!.Slots[Index];
    public JSValue GetFeedbackExtra() => Vector!.Slots[Index + 1];

    public (JSValue Feedback, JSValue Extra) GetFeedbackPair() =>
        FeedbackMetadata.GetSlotSize(Kind) == 2 ? (GetFeedback(), GetFeedbackExtra()) : (GetFeedback(), JSValue.Undefined);

    public void SetFeedback(JSValue feedback) => Vector!.Slots[Index] = feedback;

    public void SetFeedback(JSValue feedback, JSValue feedbackExtra)
    {
        JSValue[] slots = Vector!.Slots;
        slots[Index] = feedback;
        slots[Index + 1] = feedbackExtra;
    }

    public void SetFeedbackExtra(JSValue feedbackExtra) => Vector!.Slots[Index + 1] = feedbackExtra;

    static bool IsUninitializedSentinel(in JSValue v) => ReferenceEquals(v.HeapObjectOrNull, ReadOnlyRoots.uninitialized_symbol);
    static bool IsMegamorphicSentinel(in JSValue v) => ReferenceEquals(v.HeapObjectOrNull, ReadOnlyRoots.megamorphic_symbol);
    static bool IsMegaDOMSentinel(in JSValue v) => ReferenceEquals(v.HeapObjectOrNull, ReadOnlyRoots.mega_dom_symbol);

    /// <summary>IsPropertyNameFeedback (feedback-vector.cc).</summary>
    public static bool IsPropertyNameFeedback(in JSValue feedback)
    {
        switch (feedback.HeapObjectOrNull)
        {
            case JSString:
                return true;
            case Symbol symbol:
                return !ReferenceEquals(symbol, ReadOnlyRoots.uninitialized_symbol) &&
                       !ReferenceEquals(symbol, ReadOnlyRoots.mega_dom_symbol) &&
                       !ReferenceEquals(symbol, ReadOnlyRoots.megamorphic_symbol);
            default:
                return false;
        }
    }

    public void ConfigureUninitialized()
    {
        JSValue uninitialized = FeedbackVector.UninitializedSentinel;
        switch (Kind)
        {
            case FeedbackSlotKind.kStoreGlobalSloppy:
            case FeedbackSlotKind.kStoreGlobalStrict:
            case FeedbackSlotKind.kLoadGlobalNotInsideTypeof:
            case FeedbackSlotKind.kLoadGlobalInsideTypeof:
                SetFeedback(FeedbackVector.ClearedValue, uninitialized);
                break;
            case FeedbackSlotKind.kCloneObject:
            case FeedbackSlotKind.kCall:
                SetFeedback(uninitialized, JSValue.Zero);
                break;
            case FeedbackSlotKind.kInstanceOf:
                SetFeedback(uninitialized);
                break;
            case FeedbackSlotKind.kSetNamedSloppy:
            case FeedbackSlotKind.kSetNamedStrict:
            case FeedbackSlotKind.kSetKeyedSloppy:
            case FeedbackSlotKind.kSetKeyedStrict:
            case FeedbackSlotKind.kStoreInArrayLiteral:
            case FeedbackSlotKind.kDefineNamedOwn:
            case FeedbackSlotKind.kDefineKeyedOwn:
            case FeedbackSlotKind.kLoadProperty:
            case FeedbackSlotKind.kLoadKeyed:
            case FeedbackSlotKind.kHasKeyed:
            case FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral:
                SetFeedback(uninitialized, uninitialized);
                break;
            case FeedbackSlotKind.kJumpLoop:
                SetFeedback(FeedbackVector.ClearedValue, JSValue.Zero);
                break;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>FeedbackNexus::Clear: returns true if the state changed.</summary>
    public bool Clear(ClearBehavior behavior)
    {
        bool feedbackUpdated = false;
        switch (Kind)
        {
            case FeedbackSlotKind.kCompareOp:
            case FeedbackSlotKind.kForIn:
            case FeedbackSlotKind.kBinaryOp:
            case FeedbackSlotKind.kTypeOf:
                if (behavior == ClearBehavior.kDefault)
                {
                    // We don't clear these, either.
                }
                else if (!IsCleared())
                {
                    SetFeedback(JSValue.Zero);
                    feedbackUpdated = true;
                }
                break;
            case FeedbackSlotKind.kLiteral:
                if (!IsCleared())
                {
                    SetFeedback(JSValue.Zero);
                    feedbackUpdated = true;
                }
                break;
            case FeedbackSlotKind.kStringAddAndInternalize:
                if (behavior != ClearBehavior.kDefault && !IsCleared())
                {
                    SetFeedback(JSValue.Zero, FeedbackVector.UninitializedSentinel);
                    feedbackUpdated = true;
                }
                break;
            case FeedbackSlotKind.kInvalid:
                throw new UnreachableException();
            default:
                if (!IsCleared())
                {
                    ConfigureUninitialized();
                    feedbackUpdated = true;
                }
                break;
        }
        return feedbackUpdated;
    }

    public bool IsCleared() => !Isolate.Flags.use_ic || IcState() == InlineCacheState.UNINITIALIZED;

    public bool IsUninitialized => IcState() == InlineCacheState.UNINITIALIZED;
    public bool IsMegamorphic => IcState() == InlineCacheState.MEGAMORPHIC;
    public bool IsGeneric => IcState() == InlineCacheState.GENERIC;

    /// <summary>FeedbackNexus::ConfigureMegamorphic (clears the extra feedback).</summary>
    public bool ConfigureMegamorphic()
    {
        JSValue sentinel = FeedbackVector.MegamorphicSentinel;
        if (!GetFeedback().IsIdenticalTo(sentinel))
        {
            SetFeedback(sentinel, FeedbackVector.ClearedValue);
            return true;
        }
        return false;
    }

    /// <summary>FeedbackNexus::ConfigureMegamorphic(IcCheckType).</summary>
    public bool ConfigureMegamorphic(IcCheckType propertyType)
    {
        JSValue sentinel = FeedbackVector.MegamorphicSentinel;
        JSValue extra = JSValue.FromInt((int)propertyType);
        (JSValue feedback, JSValue feedbackExtra) = GetFeedbackPair();
        bool updateRequired = !feedback.IsIdenticalTo(sentinel) || !feedbackExtra.IsIdenticalTo(extra);
        if (updateRequired) SetFeedback(sentinel, extra);
        return updateRequired;
    }

    /// <summary>FeedbackNexus::ic_state.</summary>
    public InlineCacheState IcState()
    {
        (JSValue feedback, JSValue extra) = GetFeedbackPair();
        switch (Kind)
        {
            case FeedbackSlotKind.kLiteral:
                return feedback.IsNumber ? InlineCacheState.UNINITIALIZED : InlineCacheState.MONOMORPHIC;

            case FeedbackSlotKind.kStoreGlobalSloppy:
            case FeedbackSlotKind.kStoreGlobalStrict:
            case FeedbackSlotKind.kLoadGlobalNotInsideTypeof:
            case FeedbackSlotKind.kLoadGlobalInsideTypeof:
            case FeedbackSlotKind.kJumpLoop:
                if (feedback.IsNumber) return InlineCacheState.MONOMORPHIC;
                if (!FeedbackVector.IsCleared(feedback) || !IsUninitializedSentinel(extra)) return InlineCacheState.MONOMORPHIC;
                return InlineCacheState.UNINITIALIZED;

            case FeedbackSlotKind.kSetNamedSloppy:
            case FeedbackSlotKind.kSetNamedStrict:
            case FeedbackSlotKind.kSetKeyedSloppy:
            case FeedbackSlotKind.kSetKeyedStrict:
            case FeedbackSlotKind.kStoreInArrayLiteral:
            case FeedbackSlotKind.kDefineNamedOwn:
            case FeedbackSlotKind.kDefineKeyedOwn:
            case FeedbackSlotKind.kLoadProperty:
            case FeedbackSlotKind.kLoadKeyed:
            case FeedbackSlotKind.kHasKeyed:
            {
                if (IsUninitializedSentinel(feedback)) return InlineCacheState.UNINITIALIZED;
                if (IsMegamorphicSentinel(feedback)) return InlineCacheState.MEGAMORPHIC;
                if (IsMegaDOMSentinel(feedback)) return InlineCacheState.MEGADOM;
                if (feedback.HeapObjectOrNull is Map || FeedbackVector.IsCleared(feedback)) return InlineCacheState.MONOMORPHIC;
                if (feedback.HeapObjectOrNull is FixedArray) return InlineCacheState.POLYMORPHIC;
                if (feedback.HeapObjectOrNull is Name)
                {
                    var extraArray = extra.As<FixedArray>();
                    return extraArray.Length > 2 ? InlineCacheState.POLYMORPHIC : InlineCacheState.MONOMORPHIC;
                }
                throw new InvalidOperationException("unexpected feedback vector IC state");
            }

            case FeedbackSlotKind.kCall:
                if (IsMegamorphicSentinel(feedback)) return InlineCacheState.GENERIC;
                if (feedback.HeapObjectOrNull is FeedbackCell) return InlineCacheState.POLYMORPHIC;
                if (feedback.HeapObjectOrNull is JSFunction or JSBoundFunction || FeedbackVector.IsCleared(feedback))
                {
                    return InlineCacheState.MONOMORPHIC;
                }
                if (feedback.HeapObjectOrNull is AllocationSite) return InlineCacheState.MONOMORPHIC;
                return InlineCacheState.UNINITIALIZED;

            case FeedbackSlotKind.kBinaryOp:
            case FeedbackSlotKind.kStringAddAndInternalize:
            {
                BinaryOperationHint hint = GetBinaryOperationFeedback();
                if (hint == BinaryOperationHint.kNone) return InlineCacheState.UNINITIALIZED;
                if (hint == BinaryOperationHint.kAny) return InlineCacheState.GENERIC;
                return InlineCacheState.MONOMORPHIC;
            }

            case FeedbackSlotKind.kCompareOp:
            {
                CompareOperationHint hint = GetCompareOperationFeedback();
                if (hint == CompareOperationHint.kNone) return InlineCacheState.UNINITIALIZED;
                if (hint == CompareOperationHint.kAny) return InlineCacheState.GENERIC;
                return InlineCacheState.MONOMORPHIC;
            }

            case FeedbackSlotKind.kForIn:
            {
                ForInHint hint = GetForInFeedback();
                if (hint == ForInHint.kNone) return InlineCacheState.UNINITIALIZED;
                if (hint == ForInHint.kAny) return InlineCacheState.GENERIC;
                return InlineCacheState.MONOMORPHIC;
            }

            case FeedbackSlotKind.kTypeOf:
            {
                int value = (int)feedback.Number;
                if (value == 0) return InlineCacheState.UNINITIALIZED;
                if (value == (int)TypeOfFeedbackResult.kAny) return InlineCacheState.MEGAMORPHIC;
                if (System.Numerics.BitOperations.PopCount((uint)value) == 1) return InlineCacheState.MONOMORPHIC;
                return InlineCacheState.POLYMORPHIC;
            }

            case FeedbackSlotKind.kInstanceOf:
                if (IsUninitializedSentinel(feedback)) return InlineCacheState.UNINITIALIZED;
                if (IsMegamorphicSentinel(feedback)) return InlineCacheState.MEGAMORPHIC;
                return InlineCacheState.MONOMORPHIC;

            case FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral:
                if (IsUninitializedSentinel(feedback)) return InlineCacheState.UNINITIALIZED;
                if (feedback.HeapObjectOrNull is Map || FeedbackVector.IsCleared(feedback)) return InlineCacheState.MONOMORPHIC;
                return InlineCacheState.MEGAMORPHIC;

            case FeedbackSlotKind.kCloneObject:
                if (IsUninitializedSentinel(feedback)) return InlineCacheState.UNINITIALIZED;
                if (IsMegamorphicSentinel(feedback)) return InlineCacheState.MEGAMORPHIC;
                if (feedback.HeapObjectOrNull is Map || FeedbackVector.IsCleared(feedback)) return InlineCacheState.MONOMORPHIC;
                return InlineCacheState.POLYMORPHIC;

            default:
                throw new UnreachableException();
        }
    }

    /// <summary>FeedbackNexus::ConfigurePropertyCellMode.</summary>
    public void ConfigurePropertyCellMode(PropertyCell cell) => SetFeedback(cell, FeedbackVector.UninitializedSentinel);

    /// <summary>FeedbackNexus::ConfigureLexicalVarMode: false if the indices do not fit.</summary>
    public bool ConfigureLexicalVarMode(int scriptContextIndex, int contextSlotIndex, bool immutable)
    {
        if ((uint)scriptContextIndex > kContextIndexMask || (uint)contextSlotIndex > kSlotIndexMask) return false;
        int config = scriptContextIndex | (contextSlotIndex << kSlotIndexShift) | ((immutable ? 1 : 0) << kImmutabilityShift);
        SetFeedback(JSValue.FromInt(config), FeedbackVector.UninitializedSentinel);
        return true;
    }

    /// <summary>FeedbackNexus::ConfigureHandlerMode.</summary>
    public void ConfigureHandlerMode(JSValue handler) => SetFeedback(FeedbackVector.ClearedValue, handler);

    /// <summary>FeedbackNexus::ConfigureMonomorphic.</summary>
    public void ConfigureMonomorphic(Name? name, Map receiverMap, JSValue handler)
    {
        if (Kind == FeedbackSlotKind.kDefineKeyedOwnPropertyInLiteral)
        {
            SetFeedback(receiverMap, name is null ? JSValue.Undefined : name);
        }
        else if (name is null)
        {
            SetFeedback(receiverMap, handler);
        }
        else
        {
            var array = new FixedArray(2);
            array[0] = receiverMap;
            array[1] = handler;
            SetFeedback(name, array);
        }
    }

    /// <summary>FeedbackNexus::ConfigurePolymorphic.</summary>
    public void ConfigurePolymorphic(Name? name, List<(Map Map, JSValue Handler)> mapsAndHandlers)
    {
        int receiverCount = mapsAndHandlers.Count;
        var array = new FixedArray(receiverCount * 2);
        int insertAt = 0;
        foreach ((Map map, JSValue handler) in mapsAndHandlers)
        {
            if (map.IsDeprecated) continue;
            array[insertAt++] = map;
            array[insertAt++] = handler;
        }
        foreach ((Map map, JSValue handler) in mapsAndHandlers)
        {
            if (!map.IsDeprecated) continue;
            array[insertAt++] = map;
            array[insertAt++] = handler;
        }
        if (name is null) SetFeedback(array, FeedbackVector.UninitializedSentinel);
        else SetFeedback(name, array);
    }

    /// <summary>FeedbackNexus::ExtractMapsAndHandlers (FeedbackIterator over the feedback).</summary>
    public int ExtractMapsAndHandlers(List<(Map Map, JSValue Handler)> mapsAndHandlers)
    {
        int found = 0;
        for (var it = new FeedbackIterator(this); !it.Done; it.Advance())
        {
            if (FeedbackVector.IsCleared(it.Handler)) continue;
            mapsAndHandlers.Add((it.Map!, it.Handler));
            found++;
        }
        return found;
    }

    /// <summary>FeedbackNexus::ExtractMaps.</summary>
    public int ExtractMaps(List<Map> maps)
    {
        int found = 0;
        for (var it = new FeedbackIterator(this); !it.Done; it.Advance())
        {
            maps.Add(it.Map!);
            found++;
        }
        return found;
    }

    /// <summary>FeedbackNexus::FindHandlerForMap: the handler, or default (undefined) when none.</summary>
    public JSValue FindHandlerForMap(Map map)
    {
        for (var it = new FeedbackIterator(this); !it.Done; it.Advance())
        {
            if (ReferenceEquals(it.Map, map) && !FeedbackVector.IsCleared(it.Handler)) return it.Handler;
        }
        return JSValue.Undefined;
    }

    /// <summary>FeedbackNexus::GetFirstMap.</summary>
    public Map? GetFirstMap()
    {
        var it = new FeedbackIterator(this);
        return it.Done ? null : it.Map;
    }

    /// <summary>FeedbackNexus::GetName: the name of a keyed IC in the one-name state, or null.</summary>
    public Name? GetName()
    {
        if (FeedbackMetadata.IsKeyedStoreICKind(Kind) || FeedbackMetadata.IsKeyedLoadICKind(Kind) ||
            FeedbackMetadata.IsKeyedHasICKind(Kind) || FeedbackMetadata.IsDefineKeyedOwnICKind(Kind))
        {
            JSValue feedback = GetFeedback();
            if (IsPropertyNameFeedback(feedback)) return feedback.As<Name>();
        }
        if (FeedbackMetadata.IsDefineKeyedOwnPropertyInLiteralKind(Kind))
        {
            JSValue extra = GetFeedbackExtra();
            if (IsPropertyNameFeedback(extra)) return extra.As<Name>();
        }
        return null;
    }

    /// <summary>FeedbackNexus::GetKeyType.</summary>
    public IcCheckType GetKeyType()
    {
        (JSValue feedback, JSValue extra) = GetFeedbackPair();
        if (IsMegamorphicSentinel(feedback)) return extra.IsNumber ? (IcCheckType)(int)extra.Number : IcCheckType.kElement;
        return IsPropertyNameFeedback(feedback) ? IcCheckType.kProperty : IcCheckType.kElement;
    }

    // ---- Call ICs -------------------------------------------------------------------

    /// <summary>FeedbackNexus::GetCallCount.</summary>
    public int GetCallCount() => (int)((uint)(int)GetFeedbackExtra().Number >> kCallCountShift);

    public void SetSpeculationMode(SpeculationMode mode)
    {
        uint count = (uint)(int)GetFeedbackExtra().Number;
        count = (count & ~(uint)kSpeculationModeMask) | (uint)mode;
        SetFeedbackExtra(JSValue.FromInt((int)count));
    }

    public SpeculationMode GetSpeculationMode() => (SpeculationMode)((int)GetFeedbackExtra().Number & kSpeculationModeMask);

    public CallFeedbackContent GetCallFeedbackContent() =>
        (CallFeedbackContent)(((int)GetFeedbackExtra().Number >> kCallFeedbackContentShift) & 1);

    public float ComputeCallFrequency()
    {
        double invocationCount = Vector!.InvocationCount;
        double callCount = GetCallCount();
        if (invocationCount == 0.0) return 0.0f;
        return (float)(callCount / invocationCount);
    }

    // ---- Operation hints ------------------------------------------------------------

    public BinaryOperationHint GetBinaryOperationFeedback() =>
        FeedbackTypeHints.BinaryOperationHintFromFeedback((int)GetFeedback().Number);

    public CompareOperationHint GetCompareOperationFeedback() =>
        FeedbackTypeHints.CompareOperationHintFromFeedback((int)GetFeedback().Number);

    public TypeOfFeedbackResult GetTypeOfFeedback() => (TypeOfFeedbackResult)(int)GetFeedback().Number;

    public ForInHint GetForInFeedback() => FeedbackTypeHints.ForInHintFromFeedback((ForInFeedback)(int)GetFeedback().Number);

    /// <summary>FeedbackNexus::GetConstructorFeedback (InstanceOf).</summary>
    public JSObject? GetConstructorFeedback()
    {
        if (Kind != FeedbackSlotKind.kInstanceOf) return null;
        return GetFeedback().HeapObjectOrNull as JSObject;
    }

    /// <summary>FeedbackNexus::ConfigureCloneObject.</summary>
    public void ConfigureCloneObject(Map sourceMap, JSValue handler)
    {
        JSValue feedback = GetFeedback();
        switch (IcState())
        {
            case InlineCacheState.UNINITIALIZED:
                // Cache the first map seen which meets the fast case requirements.
                SetFeedback(sourceMap, handler);
                break;
            case InlineCacheState.MONOMORPHIC:
                if (FeedbackVector.IsCleared(feedback) || ReferenceEquals(feedback.HeapObjectOrNull, sourceMap) ||
                    feedback.As<Map>().IsDeprecated)
                {
                    SetFeedback(sourceMap, handler);
                }
                else
                {
                    // Transition to POLYMORPHIC.
                    var array = new FixedArray(2 * kCloneObjectPolymorphicEntrySize);
                    array[0] = feedback;
                    array[1] = GetFeedbackExtra();
                    array[2] = sourceMap;
                    array[3] = handler;
                    SetFeedback(array, FeedbackVector.ClearedValue);
                }
                break;
            case InlineCacheState.POLYMORPHIC:
            {
                int kMaxElements = Isolate.Flags.max_valid_polymorphic_map_count * kCloneObjectPolymorphicEntrySize;
                var array = feedback.As<FixedArray>();
                int arrayLen = array.Length;
                int i = 0;
                for (; i < arrayLen; i += kCloneObjectPolymorphicEntrySize)
                {
                    JSValue feedbackMap = array[i];
                    if (FeedbackVector.IsCleared(feedbackMap)) break;
                    var cachedMap = feedbackMap.As<Map>();
                    if (ReferenceEquals(cachedMap, sourceMap) || cachedMap.IsDeprecated) break;
                }
                if (i >= arrayLen)
                {
                    if (i == kMaxElements)
                    {
                        // Transition to MEGAMORPHIC.
                        SetFeedback(FeedbackVector.MegamorphicSentinel, FeedbackVector.ClearedValue);
                        break;
                    }
                    // Grow polymorphic feedback array.
                    var newArray = new FixedArray(arrayLen + kCloneObjectPolymorphicEntrySize);
                    for (int j = 0; j < arrayLen; ++j) newArray[j] = array[j];
                    SetFeedback(newArray, FeedbackVector.ClearedValue);
                    array = newArray;
                }
                array[i] = sourceMap;
                array[i + 1] = handler;
                break;
            }
            default:
                throw new UnreachableException();
        }
    }
}

/// <summary>V8's FeedbackIterator: iterates the (map, handler) pairs of map-based IC feedback.</summary>
public struct FeedbackIterator
{
    public const int kEntrySize = 2;
    public const int kHandlerOffset = 1;

    readonly FixedArray? _polymorphicFeedback;
    public Map? Map;
    public JSValue Handler;
    public bool Done;
    int _index;

    public FeedbackIterator(in FeedbackNexus nexus)
    {
        Done = false;
        _index = -1;
        Map = null;
        Handler = default;
        _polymorphicFeedback = null;

        (JSValue feedback, JSValue extra) = nexus.GetFeedbackPair();
        if (feedback.HeapObjectOrNull is Map map)
        {
            // Monomorphic.
            Map = map;
            Handler = extra;
            return;
        }
        if (feedback.HeapObjectOrNull is FixedArray array)
        {
            _polymorphicFeedback = array;
        }
        else if (FeedbackNexus.IsPropertyNameFeedback(feedback) && extra.HeapObjectOrNull is FixedArray extraArray)
        {
            _polymorphicFeedback = extraArray;
        }
        else
        {
            Done = true;
            return;
        }
        AdvancePolymorphic();
    }

    public void Advance()
    {
        if (_polymorphicFeedback is null)
        {
            Done = true;
            return;
        }
        AdvancePolymorphic();
    }

    void AdvancePolymorphic()
    {
        FixedArray array = _polymorphicFeedback!;
        int length = array.Length;
        while (true)
        {
            _index = _index < 0 ? 0 : _index + kEntrySize;
            if (_index >= length)
            {
                Done = true;
                return;
            }
            JSValue maybeMap = array[_index];
            if (maybeMap.HeapObjectOrNull is Map map)
            {
                Map = map;
                Handler = array[_index + kHandlerOffset];
                return;
            }
        }
    }
}

/// <summary>
/// The feedback-cell management of JSFunction (js-function.cc:
/// EnsureClosureFeedbackCellArray, EnsureFeedbackVector,
/// CreateAndAttachFeedbackVector, InitializeFeedbackCell, SetInterruptBudget)
/// and the budget computation of TieringManager::InterruptBudgetFor.
/// </summary>
public static class JSFunctionFeedback
{
    /// <summary>TieringManager's kMaxInterruptBudget.</summary>
    public const int kMaxInterruptBudget = 1 << 30;

    /// <summary>JSFunction::has_feedback_vector.</summary>
    public static bool HasFeedbackVector(JSFunction function) => function.RawFeedbackCell.Value is FeedbackVector;

    /// <summary>JSFunction::feedback_vector (null when not allocated).</summary>
    public static FeedbackVector? GetFeedbackVector(JSFunction function) => function.RawFeedbackCell.Value as FeedbackVector;

    /// <summary>JSFunction::has_closure_feedback_cell_array.</summary>
    public static bool HasClosureFeedbackCellArray(JSFunction function) =>
        function.RawFeedbackCell.Value is ClosureFeedbackCellArray;

    /// <summary>JSFunction::closure_feedback_cell_array (from the cell or the vector).</summary>
    public static ClosureFeedbackCellArray GetClosureFeedbackCellArray(JSFunction function) =>
        FeedbackCells.ClosureFeedbackCellArrayOf(function.RawFeedbackCell) ?? Objects.ClosureFeedbackCellArray.Empty;

    /// <summary>ScaleInterruptBudget.</summary>
    public static int ScaleInterruptBudget(long invocations, int bytecodeLength) =>
        (int)Math.Clamp(invocations * bytecodeLength, 0, kMaxInterruptBudget);

    /// <summary>TieringManager::InterruptBudgetFor (Execution/TieringManager.cs).</summary>
    public static int InterruptBudgetFor(Isolate isolate, JSFunction function) =>
        TieringManager.InterruptBudgetFor(isolate, function);

    /// <summary>JSFunction::SetInterruptBudget (kRaise when <paramref name="raise"/>, else kReset).</summary>
    public static void SetInterruptBudget(Isolate isolate, JSFunction function, bool raise)
    {
        int current = function.RawFeedbackCell.InterruptBudget;
        int newBudget = InterruptBudgetFor(isolate, function);
        if (raise) newBudget = Math.Max(current, newBudget);
        function.RawFeedbackCell.InterruptBudget = newBudget;
    }

    /// <summary>JSFunction::EnsureClosureFeedbackCellArray.</summary>
    public static void EnsureClosureFeedbackCellArray(Isolate isolate, JSFunction function)
    {
        if (function.RawFeedbackCell.Value is ClosureFeedbackCellArray or FeedbackVector) return;

        // Many closure cell is used as a way to specify that there is no
        // feedback cell for this function and a new feedback cell has to be
        // allocated for this function. For ex: for eval functions, we have to create
        // a feedback cell and cache it along with the code.
        bool allocateNewFeedbackCell = ReferenceEquals(function.RawFeedbackCell, FeedbackCell.ManyClosuresCell);
        ClosureFeedbackCellArray feedbackCellArray = Objects.ClosureFeedbackCellArray.New(isolate, function.Shared);
        if (allocateNewFeedbackCell)
        {
            function.RawFeedbackCell = new FeedbackCell { Value = feedbackCellArray };
        }
        else
        {
            function.RawFeedbackCell.Value = feedbackCellArray;
        }
        // Initialize the interrupt budget when initializing the feedback cell with
        // the closure feedback cell array.
        SetInterruptBudget(isolate, function, raise: false);
    }

    /// <summary>JSFunction::EnsureFeedbackVector.</summary>
    public static FeedbackVector EnsureFeedbackVector(Isolate isolate, JSFunction function)
    {
        if (function.RawFeedbackCell.Value is FeedbackVector existing) return existing;
        return CreateAndAttachFeedbackVector(isolate, function);
    }

    /// <summary>JSFunction::CreateAndAttachFeedbackVector.</summary>
    public static FeedbackVector CreateAndAttachFeedbackVector(Isolate isolate, JSFunction function)
    {
        EnsureClosureFeedbackCellArray(isolate, function);
        ClosureFeedbackCellArray closureFeedbackCellArray = GetClosureFeedbackCellArray(function);
        FeedbackVector vector = FeedbackVector.New(isolate, function.Shared, closureFeedbackCellArray, function.RawFeedbackCell);
        SetInterruptBudget(isolate, function, raise: true);
        return vector;
    }

    /// <summary>JSFunction::InitializeFeedbackCell.</summary>
    public static void InitializeFeedbackCell(Isolate isolate, JSFunction function, bool resetBudgetForFeedbackAllocation)
    {
        if (HasFeedbackVector(function)) return;
        bool hasClosureFeedbackCellArray = HasClosureFeedbackCellArray(function);
        bool needsFeedbackVector = !isolate.Flags.lazy_feedback_allocation ||
                                   function.Shared.CachedTieringDecision != CachedTieringDecision.kPending;
        if (needsFeedbackVector)
        {
            CreateAndAttachFeedbackVector(isolate, function);
        }
        else if (hasClosureFeedbackCellArray)
        {
            if (resetBudgetForFeedbackAllocation) SetInterruptBudget(isolate, function, raise: false);
        }
        else
        {
            EnsureClosureFeedbackCellArray(isolate, function);
        }
        // V8_ENABLE_SPARKPLUG: a function whose SharedFunctionInfo already tiered
        // up goes straight to baseline.
        if (function.Shared.CachedTieringDecision != CachedTieringDecision.kPending &&
            Baseline.BaselineSupport.CanCompileWithBaseline(isolate, function.Shared) &&
            TieringManager.ActiveTierIsIgnition(function))
        {
            if (isolate.Flags.baseline_batch_compilation) isolate.BaselineBatchCompiler.EnqueueFunction(function);
            else Codegen.Compiler.CompileBaseline(isolate, function);
        }
    }
}
