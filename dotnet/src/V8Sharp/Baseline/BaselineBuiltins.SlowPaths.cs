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
using V8Sharp.IC;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

public static partial class BaselineBuiltins
{
    const MethodImplOptions Outline = MethodImplOptions.NoInlining;

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

    /// <summary>ToBoolean of a heap object other than true, false and the number tag.</summary>
    [MethodImpl(Outline)]
    public static bool ToBooleanSlow(HeapObject value) => InterpreterOps.ToBoolean(new JSValue(value));

    /// <summary>Whether a receiver is undetectable (TestUndetectable for a receiver).</summary>
    [MethodImpl(Outline)]
    public static bool IsUndetectableValue(JSValue value) => InterpreterOps.IsUndetectable(value);

    // ---- Property access ---------------------------------------------------------------------------

    [MethodImpl(Outline)]
    public static JSValue GetNamedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue receiver, JSValue name) =>
        LoadIC.LoadNamed(isolate, fv, slot, receiver, Unsafe.As<Name>(name._obj!));

    [MethodImpl(Outline)]
    public static void SetNamedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue obj, JSValue name, JSValue value) =>
        StoreIC.StoreNamed(isolate, fv, slot, obj, Unsafe.As<Name>(name._obj!), value);

    /// <summary>The field store of a monomorphic StoreIC hit that the inline path does not do itself.</summary>
    [MethodImpl(Outline)]
    public static bool TryStoreOwnField(JSObject obj, StoreHandler handler, JSValue value) => StoreIC.TryStoreOwnField(obj, handler, value);

    [MethodImpl(Outline)]
    public static JSValue GetKeyedPropertySlow(Isolate isolate, FeedbackVector fv, int slot, JSValue obj, JSValue key) =>
        KeyedLoadIC.Load(isolate, fv, slot, obj, key);

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
