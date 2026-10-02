// Where the baseline code inlines fast paths: decided from the feedback the
// function has collected when it is compiled.
//
// Deviation: V8's Sparkplug code is the same whatever the feedback (it calls
// the same builtins everywhere). V8Sharp's inline fast paths (BaselineCompiler.Inline.cs)
// cost IL, and IL costs RyuJIT compile time, which is most of what compiling a
// function costs. A function is baseline-compiled after it ran a while (the
// first interrupt budget tick), so its feedback tells which operations ran
// and with what: an operation that never ran (empty feedback), or whose
// feedback rules out the inline path (strings, megamorphic or polymorphic
// property accesses), calls the out-of-line builtin, which is the complete
// operation and records the same feedback. An inline path is specialized to
// what the feedback said (only the handler kind the monomorphic IC recorded;
// for an arithmetic operation whose feedback is already Number, no feedback
// check, since a number operation cannot change it). Feedback only moves up
// its lattice, so what the code checks at run time stays correct; only the
// speed of a path that becomes hot later differs. An IC slot that is still
// empty is no evidence (SlotFeedbackUnknown) and gets the full inline path, as
// does every operation with --always-sparkplug, which compiles before
// anything ran.
using V8Sharp.IC;
using V8Sharp.Interpreter;
using BOF = V8Sharp.Interpreter.BinaryOperationFeedback;
using COF = V8Sharp.Interpreter.CompareOperationFeedback;

namespace V8Sharp.Baseline;

public sealed partial class BaselineCompiler
{
    /// <summary>V8SHARP_BASELINE_NO_FEEDBACK_GUIDANCE=1: every operation gets its full inline path.</summary>
    static readonly bool s_noFeedbackGuidance = Environment.GetEnvironmentVariable("V8SHARP_BASELINE_NO_FEEDBACK_GUIDANCE") == "1";

    /// <summary>The feedback vector of a closure of the function (read only; null when unknown).</summary>
    readonly FeedbackVector? _feedback;

    /// <summary>Whether the feedback decides the inline paths (not with --always-sparkplug).</summary>
    readonly bool _feedbackGuided;

    /// <summary>How a number operation is emitted.</summary>
    enum NumberSite
    {
        /// <summary>The out-of-line builtin only.</summary>
        Compact,
        /// <summary>The number path inline, checking whether it leaves the feedback unchanged.</summary>
        Checked,
        /// <summary>The number path inline; the feedback already covers any number result.</summary>
        Saturated,
    }

    /// <summary>A binary or unary operation with the embedded feedback in operand <paramref name="operandIndex"/>.</summary>
    NumberSite BinaryOpSite(int operandIndex)
    {
        if (_compact) return NumberSite.Compact;
        if (!_feedbackGuided) return NumberSite.Checked;
        switch ((BOF.TypeIndex)_bytecode.Bytecodes[EmbeddedFeedbackOffset(operandIndex)])
        {
            case BOF.TypeIndex.SignedSmall:
            case BOF.TypeIndex.SignedSmallInputs:
            case BOF.TypeIndex.AdditiveSafeInteger:
                return NumberSite.Checked;
            case BOF.TypeIndex.Number:
            case BOF.TypeIndex.NumberOrOddball:
                return NumberSite.Saturated;
            default:
                // Never ran, or strings, BigInts, objects.
                return NumberSite.Compact;
        }
    }

    /// <summary>A comparison with the embedded feedback in operand <paramref name="operandIndex"/>.</summary>
    NumberSite CompareSite(int operandIndex)
    {
        if (_compact) return NumberSite.Compact;
        if (!_feedbackGuided) return NumberSite.Checked;
        switch ((COF.TypeIndex)_bytecode.Bytecodes[EmbeddedFeedbackOffset(operandIndex)])
        {
            case COF.TypeIndex.SignedSmall:
                return NumberSite.Checked;
            case COF.TypeIndex.Number:
            case COF.TypeIndex.NumberOrBoolean:
            case COF.TypeIndex.NumberOrOddball:
                return NumberSite.Saturated;
            default:
                return NumberSite.Compact;
        }
    }

    /// <summary>The object part of feedback slot <paramref name="slot"/> at compile time (null when unknown).</summary>
    HeapObject? CompileTimeFeedback(int slot)
    {
        JSValue[]? slots = _feedback?.Slots;
        return slots is not null && (uint)slot < (uint)slots.Length ? slots[slot]._obj : null;
    }

    /// <summary>Whether the feedback is unknown, so the full inline path is emitted.</summary>
    bool FeedbackUnknown => !_feedbackGuided || _feedback is null;

    /// <summary>
    /// Whether a feedback vector slot says nothing yet. The vector is allocated
    /// at the first interrupt budget tick, which is also when the function is
    /// queued for baseline compilation, so an IC slot can be empty although
    /// its operation ran (in the interpreter, before the vector existed): an
    /// empty slot gets the full inline path. (Embedded feedback, in the
    /// bytecode array, covers the function's whole history.)
    /// </summary>
    bool SlotFeedbackUnknown(int slot)
    {
        if (FeedbackUnknown) return true;
        HeapObject? feedback = CompileTimeFeedback(slot);
        return feedback is null || ReferenceEquals(feedback, ReadOnlyRoots.uninitialized_symbol) ||
               ReferenceEquals(feedback, FeedbackVector.ClearedValue);
    }

    /// <summary>The kinds of GetNamedProperty hit inlined.</summary>
    [Flags]
    enum NamedLoadPaths
    {
        None = 0,
        OwnField = 1,
        PrototypeConstant = 2,
        ArrayLength = 4,
        All = OwnField | PrototypeConstant | ArrayLength,
    }

    /// <summary>The GetNamedProperty hits worth inlining for the feedback in <paramref name="slot"/>.</summary>
    NamedLoadPaths GetNamedPropertySite(int slot)
    {
        if (_compact) return NamedLoadPaths.None;
        if (SlotFeedbackUnknown(slot)) return NamedLoadPaths.All;
        if (CompileTimeFeedback(slot) is not Map || CompileTimeFeedback(slot + 1) is not LoadHandler handler) return NamedLoadPaths.None;
        if (handler.OwnFieldIndex >= 0) return NamedLoadPaths.OwnField;
        if (handler.IsPrototypeConstant) return NamedLoadPaths.PrototypeConstant;
        if (handler.HandlerKind == LoadHandler.Kind.kArrayLength) return NamedLoadPaths.ArrayLength;
        return NamedLoadPaths.None;
    }

    /// <summary>Whether SetNamedProperty's monomorphic store is inlined.</summary>
    bool SetNamedPropertyInline(int slot) =>
        !_compact && (SlotFeedbackUnknown(slot) || (CompileTimeFeedback(slot) is Map && CompileTimeFeedback(slot + 1) is StoreHandler));

    /// <summary>The fast elements kinds (1: FixedArray, 2: FixedDoubleArray) GetKeyedProperty inlines: 3 both, 0 none.</summary>
    int GetKeyedPropertySite(int slot)
    {
        if (_compact) return 0;
        if (SlotFeedbackUnknown(slot)) return 3;
        if (CompileTimeFeedback(slot) is not Map || CompileTimeFeedback(slot + 1) is not LoadHandler handler) return 0;
        return handler.FastElementsMode;
    }

    /// <summary>Whether SetKeyedProperty's monomorphic element store is inlined.</summary>
    bool SetKeyedPropertyInline(int slot) =>
        !_compact && (SlotFeedbackUnknown(slot) ||
                      (CompileTimeFeedback(slot) is Map && CompileTimeFeedback(slot + 1) is StoreHandler { IsSimpleElementStore: true }));

    /// <summary>Whether LdaGlobal's PropertyCell hit is inlined.</summary>
    bool LdaGlobalInline(int slot) => !_compact && (SlotFeedbackUnknown(slot) || CompileTimeFeedback(slot) is PropertyCell);
}
