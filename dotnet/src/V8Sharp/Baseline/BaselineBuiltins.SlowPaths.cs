// The out-of-line halves of the fast paths baseline code emits inline
// (BaselineCompiler.Inline.cs): what V8's Sparkplug code reaches by calling
// the *_Baseline builtins, which V8Sharp's baseline code only calls when its
// inline check fails. Each one is the complete operation (the same IC entry
// point, runtime function or InterpreterOps helper as the interpreter's
// handler), so a fast path may give up for any reason.
//
// They are NoInlining: inlined, they would bring their bodies into every
// baseline method that calls them, where RyuJIT's inlining and local
// budgets are better spent on the fast paths.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.IC;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public static partial class BaselineBuiltins
{
    // Full optimization on first use (no RyuJIT tier 0), as BaselineCalls.
    const MethodImplOptions Outline = MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization;

    // The helpers baseline IL calls in place of emitting a check: inlined
    // where RyuJIT inlines (not in big methods, past its inlining limits), and
    // compiled fully optimized for the calls that remain.
    const MethodImplOptions Helper = MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization;

    // ---- Arithmetic --------------------------------------------------------------------------------

    [MethodImpl(Outline)] public static JSValue AddSlow(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) => Add(isolate, lhs, rhs, ref feedback);
    [MethodImpl(Outline)] public static JSValue SubtractSlow(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) => Subtract(isolate, lhs, rhs, ref feedback);
    [MethodImpl(Outline)] public static JSValue MultiplySlow(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) => Multiply(isolate, lhs, rhs, ref feedback);
    [MethodImpl(Outline)] public static JSValue AddSmiSlow(Isolate isolate, JSValue lhs, int rhs, ref byte feedback) => Add(isolate, lhs, JSValue.FromInt(rhs), ref feedback);
    [MethodImpl(Outline)] public static JSValue SubtractSmiSlow(Isolate isolate, JSValue lhs, int rhs, ref byte feedback) => Subtract(isolate, lhs, JSValue.FromInt(rhs), ref feedback);
    [MethodImpl(Outline)] public static JSValue MultiplySmiSlow(Isolate isolate, JSValue lhs, int rhs, ref byte feedback) => Multiply(isolate, lhs, JSValue.FromInt(rhs), ref feedback);
    [MethodImpl(Outline)] public static JSValue IncrementSlow(Isolate isolate, JSValue value, ref byte feedback) => InterpreterOps.Increment(isolate, value, ref feedback);
    [MethodImpl(Outline)] public static JSValue DecrementSlow(Isolate isolate, JSValue value, ref byte feedback) => InterpreterOps.Decrement(isolate, value, ref feedback);

    [MethodImpl(Outline)]
    public static JSValue BitwiseSlow(Isolate isolate, int operation, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Bitwise(isolate, (Operation)operation, lhs, rhs, ref feedback);

    [MethodImpl(Outline)]
    public static JSValue BitwiseSmiSlow(Isolate isolate, int operation, JSValue lhs, int rhs, ref byte feedback) =>
        InterpreterOps.Bitwise(isolate, (Operation)operation, lhs, JSValue.FromInt(rhs), ref feedback);

    // ---- Typed array elements -----------------------------------------------------------------------

    /// <summary>
    /// The result of <see cref="LoadTypedElementBits"/> when the element is not
    /// loaded here: a signaling NaN (an element that holds this pattern takes
    /// the IC, which reads it the same).
    /// </summary>
    public const long kTypedMissBits = 0x7FF0000000000001;

    /// <summary>
    /// GetKeyedProperty's monomorphic typed array hit (KeyedLoadIC.Load's typed
    /// array case: EmitElementLoad for the kind the map check established): the
    /// element's number as bits, or <see cref="kTypedMissBits"/>. The compiler
    /// passes the kind as a constant, so the switch folds where this is inlined.
    /// </summary>
    [MethodImpl(Helper)]
    public static long LoadTypedElementBits(Isolate isolate, HeapObject? handler, HeapObject receiver, int index, int kind)
    {
        if (handler is not LoadHandler { HandlerKind: LoadHandler.Kind.kElement }) return kTypedMissBits;
        var array = Unsafe.As<JSTypedArray>(receiver);
        byte[]? data = array.FastData;
        if (data is null || (uint)index >= (uint)array.FastLength ||
            !Protectors.IsArrayBufferDetachingIntact(isolate) && array.Buffer.WasDetached)
        {
            return kTypedMissBits;
        }
        ReadOnlySpan<byte> bytes = data.AsSpan(array.FastByteOffset);
        double value;
        switch ((ElementsKind)kind)
        {
            case ElementsKind.UINT8_ELEMENTS:
            case ElementsKind.UINT8_CLAMPED_ELEMENTS:
                value = bytes[index];
                break;
            case ElementsKind.INT8_ELEMENTS:
                value = (sbyte)bytes[index];
                break;
            case ElementsKind.UINT16_ELEMENTS:
                value = MemoryMarshal.Read<ushort>(bytes.Slice(index * 2));
                break;
            case ElementsKind.INT16_ELEMENTS:
                value = MemoryMarshal.Read<short>(bytes.Slice(index * 2));
                break;
            case ElementsKind.INT32_ELEMENTS:
                value = MemoryMarshal.Read<int>(bytes.Slice(index * 4));
                break;
            case ElementsKind.UINT32_ELEMENTS:
                value = MemoryMarshal.Read<uint>(bytes.Slice(index * 4));
                break;
            case ElementsKind.FLOAT32_ELEMENTS:
                value = MemoryMarshal.Read<float>(bytes.Slice(index * 4));
                break;
            case ElementsKind.FLOAT64_ELEMENTS:
                value = MemoryMarshal.Read<double>(bytes.Slice(index * 8));
                break;
            default:
                return kTypedMissBits;
        }
        return BitConverter.DoubleToInt64Bits(value);
    }

    /// <summary>
    /// SetKeyedProperty's monomorphic typed array hit (KeyedStoreIC.Store's typed
    /// array case): a Number into an in-bounds element of a Number kind; false
    /// when the IC has to do it.
    /// </summary>
    [MethodImpl(Helper)]
    public static bool TryStoreTypedElement(Isolate isolate, HeapObject? handler, HeapObject receiver, int index, JSValue value, int kind)
    {
        if (handler is not StoreHandler { HandlerKind: StoreHandler.Kind.kElement, ElementsTransitionMap: null } storeHandler ||
            !ReferenceEquals(value._obj, NumberTag.Instance))
        {
            return false;
        }
        var array = Unsafe.As<JSTypedArray>(receiver);
        byte[]? data = array.FastData;
        if (data is null || (uint)index >= (uint)array.FastLength || !storeHandler.IsValid ||
            !Protectors.IsArrayBufferDetachingIntact(isolate) && array.Buffer.WasDetached ||
            !Protectors.IsArrayBufferMutableIntact(isolate) && array.Buffer.IsImmutable)
        {
            return false;
        }
        TypedArrayElementsOps.StoreElement(data, array.FastByteOffset, (ElementsKind)kind, index, value._num);
        return true;
    }

    // ---- The feedback checks of the inline number paths ------------------------------------------
    //
    // Called from the IL of the inline paths (BaselineCompiler.Inline.cs) rather
    // than emitted there: RyuJIT counts a method's own IL against its
    // optimization limits, not the IL of what it inlines, so a check written
    // once here keeps big functions under the limits (and costs RyuJIT less to
    // import) while compiling to the same code where it is inlined.

    /// <summary>
    /// Whether a number result leaves the binary operation feedback
    /// <paramref name="feedback"/> as it is: feedback that no number can widen
    /// (Number, NumberOrOddball, Any; InterpreterOps.IsNumberFeedbackSaturated),
    /// or SignedSmall with Smi operands and a Smi result.
    /// </summary>
    [MethodImpl(Helper)]
    public static bool BinaryFeedbackUnchanged(int feedback, double lhs, double rhs, double result)
    {
        if ((uint)(feedback - (int)BinaryOperationFeedback.TypeIndex.Number) <= 1u ||
            feedback == (int)BinaryOperationFeedback.TypeIndex.Any)
        {
            return true;
        }
        return feedback == (int)BinaryOperationFeedback.TypeIndex.SignedSmall && IsSmiNumber(lhs) && IsSmiNumber(rhs) &&
               IsSmiNumber(result);
    }

    /// <summary>
    /// <see cref="BinaryFeedbackUnchanged"/> with a Smi right operand (the Smi
    /// forms' immediate, Inc's and Dec's 1): only the left operand and the
    /// result are checked (RyuJIT does not fold the conversion of a constant).
    /// </summary>
    [MethodImpl(Helper)]
    public static bool BinaryFeedbackUnchangedSmiRhs(int feedback, double lhs, double result)
    {
        if ((uint)(feedback - (int)BinaryOperationFeedback.TypeIndex.Number) <= 1u ||
            feedback == (int)BinaryOperationFeedback.TypeIndex.Any)
        {
            return true;
        }
        return feedback == (int)BinaryOperationFeedback.TypeIndex.SignedSmall && IsSmiNumber(lhs) && IsSmiNumber(result);
    }

    /// <summary>
    /// Whether comparing two numbers leaves the compare feedback as it is
    /// (Number, NumberOrBoolean, NumberOrOddball, Any; or SignedSmall with two
    /// Smis), else InterpreterOps.UpdateCompareFeedbackForNumbers records it.
    /// </summary>
    [MethodImpl(Helper)]
    public static bool CompareFeedbackUnchanged(int feedback, double lhs, double rhs)
    {
        if ((uint)(feedback - (int)CompareOperationFeedback.TypeIndex.Number) <= 2u ||
            feedback == (int)CompareOperationFeedback.TypeIndex.Any)
        {
            return true;
        }
        return feedback == (int)CompareOperationFeedback.TypeIndex.SignedSmall && IsSmiNumber(lhs) && IsSmiNumber(rhs);
    }

    /// <summary>Whether a number is a Smi: integral, in the 31-bit range, not -0 (JSValue.IsSmi).</summary>
    [MethodImpl(Helper)]
    public static bool IsSmiNumber(double value)
    {
        int i = double.ConvertToIntegerNative<int>(value);
        return i == value && (uint)(i - JSValue.SmiMinValue) <= (uint)(JSValue.SmiMaxValue - JSValue.SmiMinValue) &&
               (i != 0 || BitConverter.DoubleToInt64Bits(value) == 0);
    }

    /// <summary>The int32 conversion of a number that the inline paths test for exactness (cvttsd2si).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TruncateToInt32(double value) => double.ConvertToIntegerNative<int>(value);

    // ---- Comparisons ----------------------------------------------------------------------------------

    /// <summary>The compare feedback for two numbers when it may change (InterpreterOps.UpdateCompareFeedbackForNumbers).</summary>
    [MethodImpl(Outline)]
    public static void CompareNumbersFeedback(double lhs, double rhs, ref byte feedback) =>
        InterpreterOps.UpdateCompareFeedbackForNumbers(ref feedback, lhs, rhs);

    [MethodImpl(Outline)]
    public static bool RelationalSlow(Isolate isolate, int operation, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        var op = (Operation)operation;
        if (lhs.IsNumber && rhs.IsNumber) return InterpreterOps.CompareNumbers(op, lhs._num, rhs._num, ref feedback).IsTrue;
        return InterpreterOps.Relational(isolate, op, lhs, rhs, ref feedback).IsTrue;
    }

    [MethodImpl(Outline)]
    public static bool EqualSlow(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Equal(isolate, lhs, rhs, ref feedback).IsTrue;

    [MethodImpl(Outline)]
    public static bool StrictEqualSlow(JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.StrictEqual(lhs, rhs, ref feedback).IsTrue;

    /// <summary>ToBoolean (compact code).</summary>
    [MethodImpl(Outline)]
    public static bool ToBooleanValue(JSValue value) => InterpreterOps.ToBoolean(value);

    /// <summary>ToBoolean of a heap object other than true, false and the number tag.</summary>
    [MethodImpl(Outline)]
    public static bool ToBooleanSlow(HeapObject value) => InterpreterOps.ToBoolean(new JSValue(value));

    /// <summary>Whether a receiver is undetectable (TestUndetectable for a receiver).</summary>
    [MethodImpl(Outline)]
    public static bool IsUndetectableValue(JSValue value) => InterpreterOps.IsUndetectable(value);

    // ---- Property access ---------------------------------------------------------------------------

    /// <summary>
    /// GetNamedProperty when the inline path did not apply: LoadIC_Baseline.
    /// Its own-field hits come first, monomorphic or polymorphic
    /// (AccessorAssembler::HandlePolymorphicCase, then the field load of the
    /// Smi handler): the field index is in the payload of the handler half of
    /// the matching pair (FeedbackNexus.EncodeHandler), so the hit reads the
    /// feedback and the receiver only, not the handler object.
    /// </summary>
    [MethodImpl(Outline)]
    public static JSValue GetNamedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue receiver, JSValue name)
    {
        if (ICMaps.AsJSObject(receiver._obj) is { } obj)
        {
            JSValue[] slots = fv.Slots;
            HeapObject? feedback = slots[slot]._obj;
            Map map = obj.Map;
            if (ReferenceEquals(feedback, map))
            {
                int field = FeedbackNexus.DecodeOwnField(in slots[slot + 1]);
                if (field >= 0) return obj.FieldAt(field);
            }
            else if (feedback is FixedArray polymorphic)
            {
                JSValue[] data = polymorphic.Data;
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    if (!ReferenceEquals(data[i]._obj, map)) continue;
                    int field = FeedbackNexus.DecodeOwnField(in data[i + 1]);
                    if (field >= 0) return obj.FieldAt(field);
                    break;
                }
            }
        }
        return LoadIC.LoadNamed(isolate, fv, slot, receiver, Unsafe.As<Name>(name._obj!));
    }

    [MethodImpl(Outline)]
    public static void SetNamedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue obj, JSValue name, JSValue value) =>
        StoreIC.StoreNamed(isolate, fv, slot, obj, Unsafe.As<Name>(name._obj!), value);

    /// <summary>The field store of a monomorphic StoreIC hit that the inline path does not do itself.</summary>
    [MethodImpl(Outline)]
    public static bool TryStoreOwnField(JSObject obj, StoreHandler handler, JSValue value) => StoreIC.TryStoreOwnField(obj, handler, value);

    /// <summary>
    /// GetKeyedProperty when the inline monomorphic hit missed: KeyedLoadIC_Baseline.
    /// Its polymorphic element case comes first (AccessorAssembler::HandlePolymorphicCase
    /// followed by the element handler's in-bounds load, EmitFastElementsLoad):
    /// an indexed load from objects of a few maps (crypto's BigInteger digit
    /// arrays) finds its handler and reads the element without entering the
    /// IC's general path.
    /// </summary>
    [MethodImpl(Outline)]
    public static JSValue GetKeyedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue obj, JSValue key)
    {
        if (ReferenceEquals(key._obj, NumberTag.Instance) && ICMaps.AsJSObject(obj._obj) is { } jsObject &&
            fv.Slots[slot]._obj is FixedArray polymorphic)
        {
            Map map = jsObject.Map;
            JSValue[] data = polymorphic.Data;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (!ReferenceEquals(data[i]._obj, map)) continue;
                if (data[i + 1]._obj is LoadHandler { HandlerKind: LoadHandler.Kind.kElement } handler &&
                    TryLoadFastElementInBounds(jsObject, key._num, handler, out JSValue value))
                {
                    return value;
                }
                break;
            }
        }
        return KeyedLoadIC.Load(isolate, fv, slot, obj, key);
    }

    /// <summary>
    /// An in-bounds, non-hole element of a fast (Smi, object or double) elements
    /// kind under an element handler: the loads ElementAccess.TryLoadFastElement
    /// does without a transition, hole or out-of-bounds case (those take the IC).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryLoadFastElementInBounds(JSObject obj, double key, LoadHandler handler, out JSValue result)
    {
        int index = BaselineBuiltins.TruncateToInt32(key);
        FixedArrayBase elements = obj.Elements;
        if (index == key && index >= 0 && (!handler.IsJSArray || index < Unsafe.As<JSArray>(obj)._length))
        {
            if (handler.FastElementsMode == 1 && elements is FixedArray fixedArray)
            {
                JSValue[] values = fixedArray._data;
                if ((uint)index < (uint)values.Length)
                {
                    result = values[index];
                    if (!ReferenceEquals(result._obj, Oddball.TheHole)) return true;
                }
            }
            else if (handler.FastElementsMode == 2 && elements is FixedDoubleArray doubleArray)
            {
                double[] values = doubleArray._data;
                if ((uint)index < (uint)values.Length && !FixedDoubleArray.IsHoleBits(values[index]))
                {
                    result = JSValue.FromNumber(values[index]);
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    [MethodImpl(Outline)]
    public static void SetKeyedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue obj, JSValue key, JSValue value) =>
        KeyedStoreIC.Store(isolate, fv, slot, obj, key, value);

    /// <summary>The in-bounds element store of a monomorphic KeyedStoreIC hit (ElementAccess.TryStoreInBounds).</summary>
    [MethodImpl(Outline)]
    public static bool TryStoreElementInBounds(JSObject obj, double key, JSValue value) => ElementAccess.TryStoreInBounds(obj, key, value);

    [MethodImpl(Outline)]
    public static JSValue LdaGlobalSlow(Isolate isolate, FeedbackVector fv, int slot, Context context, JSValue name) =>
        LoadGlobalIC.Load(isolate, fv, slot, context, name.As<Name>(), TypeofMode.NotInside);

    [MethodImpl(Outline)]
    public static JSValue LdaGlobalInsideTypeofSlow(Isolate isolate, FeedbackVector fv, int slot, Context context, JSValue name) =>
        LoadGlobalIC.Load(isolate, fv, slot, context, name.As<Name>(), TypeofMode.Inside);
}
