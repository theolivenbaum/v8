// The builtins and runtime entries that baseline code calls: what
// src/baseline/baseline-compiler.cc's Visit* methods call with CallBuiltin /
// CallRuntime (the *_Baseline builtins of src/builtins/builtins-definitions.h,
// baseline-* in builtins-*-gen.cc).
//
// Sparkplug calls the same builtins as the Ignition handlers, so each method
// here does exactly what the matching case of the interpreter's dispatch loop
// (Interpreter/InterpreterLoop.cs) does, calling the same helpers: the ICs of
// V8Sharp.IC, the runtime functions of V8Sharp.Runtime, InterpreterOps and
// InterpreterCalls. The compile-time operands (feedback slots, constant pool
// entries, register indices, immediates) arrive as arguments; the register
// file is addressed through refs into the isolate's register stack.
//
// These methods are tiny on purpose: RyuJIT inlines most of them into the
// DynamicMethod of the baseline code, which removes the call.
using System.Runtime.CompilerServices;
using V8Sharp.IC;
using V8Sharp.Interpreter;
using V8Sharp.Runtime;

namespace V8Sharp.Baseline;

public static class BaselineBuiltins
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    // ---- Values -------------------------------------------------------------------------------

    [MethodImpl(Inline)] public static JSValue Box(HeapObject o) => new(o);
    [MethodImpl(Inline)] public static JSValue Smi(int value) => JSValue.FromInt(value);
    [MethodImpl(Inline)] public static JSValue Null() => JSValue.Null;
    [MethodImpl(Inline)] public static JSValue TheHole() => JSValue.TheHole;
    [MethodImpl(Inline)] public static JSValue True() => JSValue.True;
    [MethodImpl(Inline)] public static JSValue False() => JSValue.False;
    [MethodImpl(Inline)] public static JSValue Zero() => JSValue.Zero;

    // ---- Branch conditions ------------------------------------------------------------------------

    [MethodImpl(Inline)] public static bool IsTrue(in JSValue v) => v.IsTrue;
    [MethodImpl(Inline)] public static bool IsFalse(in JSValue v) => v.IsFalse;
    [MethodImpl(Inline)] public static bool IsNull(in JSValue v) => v.IsNull;
    [MethodImpl(Inline)] public static bool IsUndefined(in JSValue v) => v.IsUndefined;
    [MethodImpl(Inline)] public static bool IsUndefinedOrNull(in JSValue v) => v.IsNullOrUndefined;
    [MethodImpl(Inline)] public static bool IsJSReceiver(in JSValue v) => v.IsJSReceiver;
    [MethodImpl(Inline)] public static bool ToBooleanBranch(in JSValue v) => InterpreterOps.ToBoolean(v);
    [MethodImpl(Inline)] public static bool IsIdentical(in JSValue a, in JSValue b) => a.IsIdenticalTo(b);

    // ---- Contexts ----------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LdaContextSlot(JSValue context, int slot, int depth) =>
        InterpreterRuntime.GetContextAtDepth(context.As<Context>(), depth).Slots[slot];

    [MethodImpl(Inline)]
    public static JSValue LdaCurrentContextSlot(Context context, int slot) => context.Slots[slot];

    [MethodImpl(Inline)]
    public static void StaContextSlot(JSValue context, int slot, int depth, JSValue value) =>
        InterpreterRuntime.GetContextAtDepth(context.As<Context>(), depth).Slots[slot] = value;

    [MethodImpl(Inline)]
    public static void StaCurrentContextSlot(Context context, int slot, JSValue value) => context.Slots[slot] = value;

    /// <summary>PushContext: the accumulator becomes the current context (the old one is saved by the caller).</summary>
    [MethodImpl(Inline)]
    public static Context PushContext(Isolate isolate, JSValue acc, ref JSValue contextSlot)
    {
        Context context = acc.As<Context>();
        contextSlot = context;
        isolate.Context = context;
        return context;
    }

    /// <summary>PopContext: the saved context in the register becomes current.</summary>
    [MethodImpl(Inline)]
    public static Context PopContext(Isolate isolate, JSValue saved, ref JSValue contextSlot)
    {
        Context context = saved.As<Context>();
        contextSlot = context;
        isolate.Context = context;
        return context;
    }

    // ---- Tests ---------------------------------------------------------------------------------------

    [MethodImpl(Inline)] public static JSValue TestReferenceEqual(JSValue lhs, JSValue acc) => JSValue.FromBoolean(lhs.IsIdenticalTo(acc));
    [MethodImpl(Inline)] public static JSValue TestUndetectable(JSValue acc) => JSValue.FromBoolean(InterpreterOps.IsUndetectable(acc));
    [MethodImpl(Inline)] public static JSValue TestNull(JSValue acc) => JSValue.FromBoolean(acc.IsNull);
    [MethodImpl(Inline)] public static JSValue TestUndefined(JSValue acc) => JSValue.FromBoolean(acc.IsUndefined);

    [MethodImpl(Inline)]
    public static JSValue TestTypeOf(JSValue acc, int literal) =>
        JSValue.FromBoolean(InterpreterOps.TestTypeOf(acc, (TestTypeOfFlags.LiteralFlag)literal));

    // ---- Globals and lookup slots ---------------------------------------------------------------------------

    public static JSValue LdaGlobal(Isolate isolate, FeedbackVector? fv, int slot, Context context, JSValue name) =>
        LoadGlobalIC.Load(isolate, fv, slot, context, name.As<Name>(), TypeofMode.NotInside);

    public static JSValue LdaGlobalInsideTypeof(Isolate isolate, FeedbackVector? fv, int slot, Context context, JSValue name) =>
        LoadGlobalIC.Load(isolate, fv, slot, context, name.As<Name>(), TypeofMode.Inside);

    public static void StaGlobal(Isolate isolate, FeedbackVector? fv, int slot, Context context, JSValue name, JSValue value) =>
        StoreGlobalIC.Store(isolate, fv, slot, context, name.As<Name>(), value);

    public static JSValue LdaLookupSlot(Isolate isolate, Context context, JSValue name, bool insideTypeof) =>
        RuntimeScopes.LoadLookupSlot(isolate, context, name.As<JSString>(), insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);

    public static JSValue LdaLookupContextSlot(Isolate isolate, Context context, JSValue name, int slot, int depth, bool insideTypeof)
    {
        Context? slotContext = InterpreterOps.ContextWithoutExtensionsUpToDepth(context, depth);
        if (slotContext is not null) return slotContext.Slots[slot];
        return RuntimeScopes.LoadLookupSlot(isolate, context, name.As<JSString>(),
            insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
    }

    public static JSValue LdaLookupGlobalSlot(Isolate isolate, FeedbackVector? fv, Context context, JSValue nameValue, int slot, int depth,
        bool insideTypeof)
    {
        var name = nameValue.As<JSString>();
        if (InterpreterOps.ContextWithoutExtensionsUpToDepth(context, depth) is not null)
        {
            return LoadGlobalIC.Load(isolate, fv, slot, context, name, insideTypeof ? TypeofMode.Inside : TypeofMode.NotInside);
        }
        return RuntimeScopes.LoadLookupSlot(isolate, context, name, insideTypeof ? ShouldThrow.DontThrow : ShouldThrow.ThrowOnError);
    }

    public static JSValue StaLookupSlot(Isolate isolate, Context context, JSValue name, int flags, JSValue value) =>
        RuntimeScopes.StoreLookupSlot(isolate, context, name.As<JSString>(), value,
            StoreLookupSlotFlags.GetLanguageMode((byte)flags) == LanguageMode.Strict
                ? LanguageMode.Strict
                : LanguageMode.Sloppy,
            StoreLookupSlotFlags.IsLookupHoistingMode((byte)flags));

    // ---- Property loads -------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue GetNamedProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue receiver, JSValue name) =>
        LoadIC.LoadNamed(isolate, fv, slot, receiver, Unsafe.As<Name>(name._obj!));

    public static JSValue GetNamedPropertyFromSuper(Isolate isolate, FeedbackVector? fv, int slot, JSValue receiver, JSValue homeObject,
        JSValue name) =>
        LoadIC.LoadSuper(isolate, fv, slot, receiver, homeObject, name.As<Name>());

    [MethodImpl(Inline)]
    public static JSValue GetKeyedProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue key) =>
        KeyedLoadIC.Load(isolate, fv, slot, obj, key);

    public static JSValue GetEnumeratedKeyedProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue key,
        JSValue enumIndex, JSValue cacheType) =>
        KeyedLoadIC.LoadEnumerated(isolate, fv, slot, obj, key, enumIndex, cacheType);

    public static JSValue GetPrivateField(Isolate isolate, FeedbackVector? fv, int slot, JSValue context, int slotIndex, int depth,
        JSValue obj)
    {
        JSValue symbol = InterpreterRuntime.GetContextAtDepth(context.As<Context>(), depth).Slots[slotIndex];
        return KeyedLoadIC.Load(isolate, fv, slot, obj, symbol);
    }

    public static JSValue LdaModuleVariable(Isolate isolate, Context context, int cellIndex, int depth) =>
        InterpreterOps.LoadModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(context, depth), cellIndex);

    public static void StaModuleVariable(Isolate isolate, Context context, int cellIndex, int depth, JSValue value) =>
        InterpreterOps.StoreModuleVariable(isolate, InterpreterRuntime.GetContextAtDepth(context, depth), cellIndex, value);

    // ---- Property stores ------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static void SetNamedProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue name, JSValue value) =>
        StoreIC.StoreNamed(isolate, fv, slot, obj, Unsafe.As<Name>(name._obj!), value);

    public static void DefineNamedOwnProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue name, JSValue value) =>
        StoreIC.DefineNamedOwn(isolate, fv, slot, obj, name.As<Name>(), value);

    [MethodImpl(Inline)]
    public static void SetKeyedProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue key, JSValue value) =>
        KeyedStoreIC.Store(isolate, fv, slot, obj, key, value);

    public static void DefineKeyedOwnProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue key, int flags,
        JSValue value) =>
        KeyedStoreIC.DefineKeyedOwn(isolate, fv, slot, obj, key, value, (DefineKeyedOwnPropertyFlags)flags);

    public static void StaInArrayLiteral(Isolate isolate, FeedbackVector? fv, int slot, JSValue array, JSValue index, JSValue value) =>
        KeyedStoreIC.StoreInArrayLiteral(isolate, fv, slot, array, index, value);

    public static void DefineKeyedOwnPropertyInLiteral(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue name,
        int flags, JSValue value) =>
        RuntimeObject.DefineKeyedOwnPropertyInLiteral(isolate, obj, name, value, (DefineKeyedOwnPropertyInLiteralFlags)flags, fv, slot);

    public static JSValue SetPrototypeProperties(Isolate isolate, Context context, JSFunction function, JSValue boilerplate,
        int startSlot, JSValue acc) =>
        RuntimeLiterals.SetPrototypeProperties(isolate, context, acc, boilerplate.As<ObjectBoilerplateDescription>(),
            JSFunctionFeedback.GetClosureFeedbackCellArray(function), startSlot);

    public static void SetPrivateField(Isolate isolate, FeedbackVector? fv, int slot, JSValue context, int slotIndex, int depth,
        JSValue obj, JSValue value)
    {
        JSValue symbol = InterpreterRuntime.GetContextAtDepth(context.As<Context>(), depth).Slots[slotIndex];
        KeyedStoreIC.Store(isolate, fv, slot, obj, symbol, value);
    }

    // ---- Binary operations ----------------------------------------------------------------------------------
    //
    // The Smi fast paths: when the embedded feedback already says SignedSmall and
    // the operands and result are Smis, the feedback would not change, so only
    // the result is computed. Everything else takes the interpreter's path,
    // which computes and records the feedback (Generate_*WithFeedback).

    const byte kSignedSmall = (byte)BinaryOperationFeedback.TypeIndex.SignedSmall;

    /// <summary>Whether <paramref name="d"/> is a Smi (31-bit, integral, not -0), and its value.</summary>
    [MethodImpl(Inline)]
    static bool IsSmi(double d, out int value)
    {
        value = double.ConvertToIntegerNative<int>(d);
        return value == d && (uint)(value - JSValue.SmiMinValue) <= (uint)(JSValue.SmiMaxValue - JSValue.SmiMinValue) &&
               (value != 0 || !double.IsNegative(d));
    }

    [MethodImpl(Inline)]
    static bool IsSmiInt(long value) => value >= JSValue.SmiMinValue && value <= JSValue.SmiMaxValue;

    [MethodImpl(Inline)]
    public static JSValue Add(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber)
        {
            if (feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r) && IsSmiInt((long)l + r))
            {
                return JSValue.FromInt(l + r);
            }
            return InterpreterOps.AddNumbers(isolate, lhs._num, rhs._num, ref feedback);
        }
        return InterpreterOps.AddSlow(isolate, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue Subtract(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber)
        {
            if (feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r) && IsSmiInt((long)l - r))
            {
                return JSValue.FromInt(l - r);
            }
            return InterpreterOps.SubtractNumbers(lhs._num, rhs._num, ref feedback);
        }
        return InterpreterOps.BinarySlow(isolate, Operation.Subtract, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue Multiply(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber)
        {
            if (feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
            {
                long product = (long)l * r;
                // A zero product of a negative operand is -0, not a Smi.
                if (IsSmiInt(product) && (product != 0 || (l | r) >= 0)) return JSValue.FromInt((int)product);
            }
            return InterpreterOps.MultiplyNumbers(lhs._num, rhs._num, ref feedback);
        }
        return InterpreterOps.BinarySlow(isolate, Operation.Multiply, lhs, rhs, ref feedback);
    }

    public static JSValue Divide(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Binary(isolate, Operation.Divide, lhs, rhs, ref feedback);

    public static JSValue Modulus(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Binary(isolate, Operation.Modulus, lhs, rhs, ref feedback);

    public static JSValue Exponentiate(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Binary(isolate, Operation.Exponentiate, lhs, rhs, ref feedback);

    [MethodImpl(Inline)]
    public static JSValue BitwiseOr(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            // The bitwise combination of two Smis is a Smi.
            return JSValue.FromInt(l | r);
        }
        return InterpreterOps.Bitwise(isolate, Operation.BitwiseOr, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue BitwiseXor(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            return JSValue.FromInt(l ^ r);
        }
        return InterpreterOps.Bitwise(isolate, Operation.BitwiseXor, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue BitwiseAnd(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            return JSValue.FromInt(l & r);
        }
        return InterpreterOps.Bitwise(isolate, Operation.BitwiseAnd, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue ShiftLeft(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            int result = l << (r & 0x1F);
            if (IsSmiInt(result)) return JSValue.FromInt(result);
        }
        return InterpreterOps.Bitwise(isolate, Operation.ShiftLeft, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue ShiftRight(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            return JSValue.FromInt(l >> (r & 0x1F));
        }
        return InterpreterOps.Bitwise(isolate, Operation.ShiftRight, lhs, rhs, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue ShiftRightLogical(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback)
    {
        if (lhs.IsNumber && rhs.IsNumber && feedback == kSignedSmall && IsSmi(lhs._num, out int l) && IsSmi(rhs._num, out int r))
        {
            uint result = (uint)l >> (r & 0x1F);
            if (result <= JSValue.SmiMaxValue) return JSValue.FromInt((int)result);
        }
        return InterpreterOps.Bitwise(isolate, Operation.ShiftRightLogical, lhs, rhs, ref feedback);
    }

    public static JSValue AddStringConstantInternalize(Isolate isolate, FeedbackVector? fv, int slot, int variant, JSValue lhs,
        JSValue rhs) =>
        InterpreterOps.AddStringConstantAndInternalize(isolate, fv, slot, lhs, rhs, (AddStringConstantAndInternalizeVariant)variant);

    // ---- Unary operations -----------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue Increment(Isolate isolate, JSValue value, ref byte feedback)
    {
        if (value.IsNumber && feedback == kSignedSmall && IsSmi(value._num, out int v) && v != JSValue.SmiMaxValue)
        {
            return JSValue.FromInt(v + 1);
        }
        return InterpreterOps.Increment(isolate, value, ref feedback);
    }

    [MethodImpl(Inline)]
    public static JSValue Decrement(Isolate isolate, JSValue value, ref byte feedback)
    {
        if (value.IsNumber && feedback == kSignedSmall && IsSmi(value._num, out int v) && v != JSValue.SmiMinValue)
        {
            return JSValue.FromInt(v - 1);
        }
        return InterpreterOps.Decrement(isolate, value, ref feedback);
    }
    public static JSValue Negate(Isolate isolate, JSValue value, ref byte feedback) => InterpreterOps.Negate(isolate, value, ref feedback);
    public static JSValue BitwiseNot(Isolate isolate, JSValue value, ref byte feedback) => InterpreterOps.BitwiseNot(isolate, value, ref feedback);
    [MethodImpl(Inline)] public static JSValue ToBooleanLogicalNot(JSValue value) => JSValue.FromBoolean(!InterpreterOps.ToBoolean(value));
    [MethodImpl(Inline)] public static JSValue LogicalNot(JSValue value) => JSValue.FromBoolean(!value.IsTrue);
    public static JSValue TypeOf(Isolate isolate, FeedbackVector? fv, int slot, JSValue value) => InterpreterOps.TypeOf(isolate, value, fv, slot);

    public static JSValue DeletePropertyStrict(Isolate isolate, JSValue obj, JSValue key) =>
        RuntimeObject.DeleteProperty(isolate, obj, key, LanguageMode.Strict);

    public static JSValue DeletePropertySloppy(Isolate isolate, JSValue obj, JSValue key) =>
        RuntimeObject.DeleteProperty(isolate, obj, key, LanguageMode.Sloppy);

    public static JSValue GetSuperConstructor(Isolate isolate, JSValue activeFunction) =>
        InterpreterOps.GetSuperConstructor(isolate, activeFunction.As<JSFunction>());

    public static void FindNonDefaultConstructorOrConstruct(Isolate isolate, JSValue thisFunction, JSValue newTarget,
        ref JSValue first, ref JSValue second)
    {
        InterpreterOps.FindNonDefaultConstructorOrConstruct(isolate, thisFunction.As<JSFunction>(), newTarget,
            out JSValue a, out JSValue b);
        first = a;
        second = b;
    }

    // ---- Compare operations ---------------------------------------------------------------------------------

    public static JSValue TestEqual(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        InterpreterOps.Equal(isolate, lhs, rhs, ref feedback);

    public static JSValue TestEqualStrict(JSValue lhs, JSValue rhs, ref byte feedback) => InterpreterOps.StrictEqual(lhs, rhs, ref feedback);

    [MethodImpl(Inline)]
    public static JSValue TestLessThan(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        lhs.IsNumber && rhs.IsNumber
            ? InterpreterOps.CompareNumbers(Operation.LessThan, lhs._num, rhs._num, ref feedback)
            : InterpreterOps.Relational(isolate, Operation.LessThan, lhs, rhs, ref feedback);

    [MethodImpl(Inline)]
    public static JSValue TestGreaterThan(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        lhs.IsNumber && rhs.IsNumber
            ? InterpreterOps.CompareNumbers(Operation.GreaterThan, lhs._num, rhs._num, ref feedback)
            : InterpreterOps.Relational(isolate, Operation.GreaterThan, lhs, rhs, ref feedback);

    [MethodImpl(Inline)]
    public static JSValue TestLessThanOrEqual(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        lhs.IsNumber && rhs.IsNumber
            ? InterpreterOps.CompareNumbers(Operation.LessThanOrEqual, lhs._num, rhs._num, ref feedback)
            : InterpreterOps.Relational(isolate, Operation.LessThanOrEqual, lhs, rhs, ref feedback);

    [MethodImpl(Inline)]
    public static JSValue TestGreaterThanOrEqual(Isolate isolate, JSValue lhs, JSValue rhs, ref byte feedback) =>
        lhs.IsNumber && rhs.IsNumber
            ? InterpreterOps.CompareNumbers(Operation.GreaterThanOrEqual, lhs._num, rhs._num, ref feedback)
            : InterpreterOps.Relational(isolate, Operation.GreaterThanOrEqual, lhs, rhs, ref feedback);

    public static JSValue TestInstanceOf(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue callable) =>
        InterpreterOps.InstanceOf(isolate, fv, slot, obj, callable);

    public static JSValue TestIn(Isolate isolate, FeedbackVector? fv, int slot, JSValue name, JSValue obj) =>
        KeyedHasIC.Has(isolate, fv, slot, obj, name);

    // ---- Conversions ------------------------------------------------------------------------------------------

    public static JSValue ToName(Isolate isolate, JSValue value) => ObjectOps.ToName(isolate, value);

    public static JSValue ToNumber(Isolate isolate, FeedbackVector? fv, int slot, JSValue value) =>
        InterpreterOps.ToNumberOrNumeric(isolate, value, fv, slot, numeric: false);

    public static JSValue ToNumeric(Isolate isolate, FeedbackVector? fv, int slot, JSValue value) =>
        InterpreterOps.ToNumberOrNumeric(isolate, value, fv, slot, numeric: true);

    public static JSValue ToObject(Isolate isolate, JSValue value) => value.IsJSReceiver ? value : ObjectOps.ToObject(isolate, value);

    public static JSValue ToString(Isolate isolate, JSValue value) => value.IsString ? value : ObjectOps.ToString(isolate, value);

    [MethodImpl(Inline)] public static JSValue ToBoolean(JSValue value) => JSValue.FromBoolean(InterpreterOps.ToBoolean(value));

    // ---- Literals and closures ------------------------------------------------------------------------------------

    public static JSValue CreateRegExpLiteral(Isolate isolate, FeedbackVector? fv, int slot, JSValue pattern, int flags) =>
        RuntimeLiterals.CreateRegExpLiteral(isolate, fv, slot, pattern.As<JSString>(), flags);

    public static JSValue CreateArrayLiteral(Isolate isolate, FeedbackVector? fv, int slot, JSValue description, int flags) =>
        RuntimeLiterals.CreateArrayLiteral(isolate, fv, slot, description.As<ArrayBoilerplateDescription>(),
            CreateArrayLiteralFlags.DecodeFlags((byte)flags));

    public static JSValue CreateArrayFromIterable(Isolate isolate, JSValue iterable) =>
        InterpreterIterators.IterableToListWithSymbolLookup(isolate, iterable);

    public static JSValue CreateEmptyArrayLiteral(Isolate isolate, FeedbackVector? fv, int slot) =>
        RuntimeLiterals.CreateEmptyArrayLiteral(isolate, fv, slot);

    public static JSValue CreateObjectLiteral(Isolate isolate, FeedbackVector? fv, int slot, JSValue description, int flags) =>
        RuntimeLiterals.CreateObjectLiteral(isolate, fv, slot, description.As<ObjectBoilerplateDescription>(),
            CreateObjectLiteralFlags.DecodeFlags((byte)flags));

    public static JSValue CreateEmptyObjectLiteral(Isolate isolate, Context context) =>
        RuntimeLiterals.CreateEmptyObjectLiteral(isolate, context.NativeContext);

    public static JSValue CloneObject(Isolate isolate, FeedbackVector? fv, int slot, JSValue source, int flags) =>
        CloneObjectIC.Clone(isolate, fv, slot, source, CreateObjectLiteralFlags.DecodeFlags((byte)flags));

    public static JSValue GetTemplateObject(Isolate isolate, FeedbackVector? fv, int slot, JSFunction function, JSValue description) =>
        RuntimeLiterals.GetTemplateObject(isolate, function.Shared, description.As<TemplateObjectDescription>(), fv, slot);

    public static JSValue CreateClosure(Isolate isolate, Context context, JSFunction function, JSValue shared, int slot)
    {
        FeedbackCell cell = JSFunctionFeedback.GetClosureFeedbackCellArray(function).Get(slot);
        return RuntimeClosures.NewClosure(isolate, shared.As<SharedFunctionInfo>(), context, cell);
    }

    // ---- Context allocation ------------------------------------------------------------------------------------

    public static JSValue CreateBlockContext(Isolate isolate, Context context, JSValue scopeInfo) =>
        isolate.Factory.NewBlockContext(context, scopeInfo.As<ScopeInfo>());

    public static JSValue CreateCatchContext(Isolate isolate, Context context, JSValue exception, JSValue scopeInfo) =>
        isolate.Factory.NewCatchContext(context, scopeInfo.As<ScopeInfo>(), exception);

    public static JSValue CreateFunctionContext(Isolate isolate, Context context, JSValue scopeInfo, bool isEval) =>
        RuntimeScopes.NewFunctionContext(isolate, context, scopeInfo.As<ScopeInfo>(), isEval);

    public static JSValue CreateWithContext(Isolate isolate, Context context, JSValue obj, JSValue scopeInfo) =>
        RuntimeScopes.PushWithContext(isolate, context, obj, scopeInfo.As<ScopeInfo>());

    // ---- Arguments --------------------------------------------------------------------------------------------------

    public static JSValue CreateMappedArguments(Isolate isolate, ref InterpreterState st, Context context) =>
        InterpreterArguments.NewSloppyArguments(isolate, st.Function, context, st.Fp, st.Argc);

    public static JSValue CreateUnmappedArguments(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewStrictArguments(isolate, st.Function, st.Fp, st.Argc);

    public static JSValue CreateRestParameter(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewRestParameter(isolate, st.Function, st.Fp, st.Argc);

    // ---- Calls ------------------------------------------------------------------------------------------------------

    /// <summary>CallAnyReceiver / CallProperty: the receiver and arguments are the register list at <paramref name="first"/>.</summary>
    public static JSValue CallProperty(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        return InterpreterCalls.Call(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.NotNullOrUndefined);
    }

    public static JSValue CallAnyReceiver(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int first, int count)
    {
        JSValue receiver = isolate.RegisterStack[first];
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        return InterpreterCalls.Call(isolate, callee, receiver, first + 1, count - 1, ConvertReceiverMode.Any);
    }

    public static JSValue CallProperty0(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, JSValue receiver)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        return InterpreterCalls.Call(isolate, callee, receiver, 0, 0, ConvertReceiverMode.NotNullOrUndefined);
    }

    public static JSValue CallProperty1(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, JSValue receiver, int arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        return InterpreterCalls.Call(isolate, callee, receiver, arg0, 1, ConvertReceiverMode.NotNullOrUndefined);
    }

    public static JSValue CallProperty2(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, JSValue receiver, int arg0,
        int arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee, receiver);
        JSValue[] stack = isolate.RegisterStack;
        return InterpreterCalls.Call2(isolate, callee, receiver, stack[arg0], stack[arg1], arg0, arg1 == arg0 + 1,
            ConvertReceiverMode.NotNullOrUndefined);
    }

    public static JSValue CallUndefinedReceiver(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int first, int count)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        return InterpreterCalls.Call(isolate, callee, JSValue.Undefined, first, count, ConvertReceiverMode.NullOrUndefined);
    }

    public static JSValue CallUndefinedReceiver0(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        return InterpreterCalls.Call(isolate, callee, JSValue.Undefined, 0, 0, ConvertReceiverMode.NullOrUndefined);
    }

    public static JSValue CallUndefinedReceiver1(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int arg0)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        return InterpreterCalls.Call(isolate, callee, JSValue.Undefined, arg0, 1, ConvertReceiverMode.NullOrUndefined);
    }

    public static JSValue CallUndefinedReceiver2(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int arg0, int arg1)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        JSValue[] stack = isolate.RegisterStack;
        return InterpreterCalls.Call2(isolate, callee, JSValue.Undefined, stack[arg0], stack[arg1], arg0, arg1 == arg0 + 1,
            ConvertReceiverMode.NullOrUndefined);
    }

    public static JSValue CallWithSpread(Isolate isolate, FeedbackVector? fv, int slot, JSValue callee, int first, int count)
    {
        InterpreterCalls.CollectCallFeedback(isolate, fv, slot, callee);
        return InterpreterCalls.CallWithSpread(isolate, callee, isolate.RegisterStack.AsSpan(first, count));
    }

    public static JSValue CallRuntime(Isolate isolate, int id, int first, int count) =>
        RuntimeTable.Call(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(first, count));

    public static JSValue CallRuntimeForPair(Isolate isolate, int id, int first, int count, ref JSValue output0, ref JSValue output1)
    {
        JSValue result0 = RuntimeTable.CallForPair(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(first, count),
            out JSValue result1);
        output0 = result0;
        output1 = result1;
        return result0;
    }

    public static JSValue CallJSRuntime(Isolate isolate, Context context, int index, int first, int count) =>
        InterpreterCalls.Call(isolate, context.NativeContext.Slots[index], JSValue.Undefined, first, count,
            ConvertReceiverMode.NullOrUndefined);

    public static JSValue InvokeIntrinsic(Isolate isolate, int id, int first, int count) =>
        InterpreterIntrinsicsDispatch.Invoke(isolate, (IntrinsicsHelper.IntrinsicId)id, isolate.RegisterStack.AsSpan(first, count));

    public static JSValue Construct(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, int first, int count,
        JSValue newTarget) =>
        InterpreterCalls.Construct(isolate, fv, slot, constructor, newTarget, first, count);

    public static JSValue ConstructWithSpread(Isolate isolate, FeedbackVector? fv, int slot, JSValue constructor, int first, int count,
        JSValue newTarget) =>
        InterpreterCalls.ConstructWithSpread(isolate, fv, slot, constructor, newTarget, isolate.RegisterStack.AsSpan(first, count));

    public static JSValue ConstructForwardAllArgs(Isolate isolate, ref InterpreterState st, FeedbackVector? fv, int slot,
        JSValue constructor, JSValue newTarget) =>
        InterpreterCalls.ConstructForwardAllArgs(isolate, fv, slot, constructor, newTarget, st.Fp, st.Argc);

    // ---- for-in / for-of -------------------------------------------------------------------------------------------

    public static JSValue ForInEnumerate(Isolate isolate, JSValue receiver) =>
        RuntimeForIn.ForInEnumerate(isolate, receiver.As<JSReceiver>());

    /// <summary>ForInPrepare: fills the register triple (cache type, cache array, cache length).</summary>
    public static void ForInPrepare(Isolate isolate, FeedbackVector? fv, int slot, JSValue enumerator, ref JSValue cacheType,
        ref JSValue cacheArray, ref JSValue cacheLength)
    {
        RuntimeForIn.ForInPrepare(isolate, enumerator.Object, fv, slot, out JSValue array, out int length);
        cacheType = enumerator;
        cacheArray = array;
        cacheLength = JSValue.FromInt(length);
    }

    public static JSValue ForInNext(Isolate isolate, FeedbackVector? fv, int slot, JSValue receiver, JSValue indexValue,
        JSValue cacheType, JSValue cacheArray)
    {
        int index = (int)indexValue.Number;
        JSValue key = cacheArray.As<FixedArray>()[index];
        if (receiver.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, cacheType.HeapObjectOrNull))
        {
            // Enum cache in use for {receiver}, the {key} is definitely valid.
            return key;
        }
        return RuntimeForIn.ForInNextSlow(isolate, fv, slot, receiver, key, cacheType);
    }

    [MethodImpl(Inline)]
    public static void ForInStep(ref JSValue index) => index = JSValue.FromNumber(index.Number + 1);

    public static JSValue ForOfNext(Isolate isolate, FeedbackVector? fv, int slot, JSValue obj, JSValue next) =>
        InterpreterIterators.ForOfNext(isolate, fv, slot, obj, next);

    public static JSValue GetIterator(Isolate isolate, FeedbackVector? fv, int loadSlot, int callSlot, JSValue receiver) =>
        InterpreterIterators.GetIterator(isolate, fv, loadSlot, callSlot, receiver);

    public static void ArrayDestructure(Isolate isolate, JSValue value, int first, int count) =>
        InterpreterIterators.ArrayDestructure(isolate, value, isolate.RegisterStack.AsSpan(first, count));

    // ---- Non-local control flow --------------------------------------------------------------------------------------

    public static JSValue SetPendingMessage(Isolate isolate, JSValue message)
    {
        JSValue previous = isolate.PendingMessage;
        isolate.PendingMessage = message;
        return previous;
    }

    /// <summary>
    /// Throw: Runtime_Throw, then the unwinding to this frame's handler.
    /// Returns true when the frame has a handler for the current offset (the
    /// state then holds the handler's offset, context and accumulator);
    /// otherwise throws the exception out of the frame.
    /// </summary>
    public static bool Throw(Isolate isolate, ref InterpreterState st, JSValue exception)
    {
        JSMessageObject? message = InterpreterExecution.CreateMessageForThrow(isolate, exception);
        if (InterpreterExecution.TryDispatchToHandler(isolate, ref st, exception, message)) return true;
        throw new JavaScriptException(exception, message);
    }

    /// <summary>ReThrow: Runtime_ReThrow with the pending message, then as Throw.</summary>
    public static bool ReThrow(Isolate isolate, ref InterpreterState st, JSValue exception)
    {
        JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
        if (InterpreterExecution.TryDispatchToHandler(isolate, ref st, exception, message)) return true;
        throw new JavaScriptException(exception, message);
    }

    public static void ThrowReferenceErrorIfHole(Isolate isolate, JSValue value, JSValue name)
    {
        if (value.IsTheHole) RuntimeScopes.ThrowAccessedUninitializedVariable(isolate, name);
    }

    public static void ThrowSuperNotCalledIfHole(Isolate isolate, JSValue value)
    {
        if (value.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperNotCalled);
    }

    public static void ThrowSuperAlreadyCalledIfNotHole(Isolate isolate, JSValue value)
    {
        if (!value.IsTheHole) isolate.ThrowReferenceError(MessageTemplate.SuperAlreadyCalled);
    }

    public static void ThrowIfNotSuperConstructor(Isolate isolate, JSFunction function, JSValue constructor)
    {
        if (!ObjectOps.IsConstructor(constructor)) RuntimeClasses.ThrowNotSuperConstructor(isolate, constructor, function);
    }

    // ---- Generators ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// SwitchOnGeneratorState for a resumed generator: marks it executing,
    /// restores its context and returns the suspend id to jump on.
    /// </summary>
    public static int ResumeGeneratorState(Isolate isolate, JSValue maybeGenerator, ref JSValue contextSlot, out Context context,
        int tableLength)
    {
        var generator = maybeGenerator.As<JSGeneratorObject>();
        int state = generator.ContinuationValue;
        generator.ContinuationValue = JSGeneratorObject.kGeneratorExecuting;
        context = generator.Context;
        contextSlot = context;
        isolate.Context = context;
        if ((uint)state >= (uint)tableLength) throw new InvalidOperationException("V8Sharp: bad generator state");
        return state;
    }

    public static void SuspendGenerator(Isolate isolate, ref InterpreterState st, Context context, JSValue generatorValue, int first,
        int count, int suspendId, int offset)
    {
        var generator = generatorValue.As<JSGeneratorObject>();
        InterpreterGenerators.ExportParametersAndRegisterFile(isolate, generator, st.Fp, first, count);
        generator.Context = context;
        generator.ContinuationValue = suspendId;
        // Store the bytecode offset in the [input_or_debug_pos] field, to be used by
        // the inspector.
        generator.InputOrDebugPos = JSValue.FromInt(offset);
    }

    public static JSValue ResumeGenerator(Isolate isolate, ref InterpreterState st, JSValue generatorValue, int first, int count)
    {
        var generator = generatorValue.As<JSGeneratorObject>();
        InterpreterGenerators.ImportRegisterFile(isolate, generator, st.Fp, first, count);
        // Return the generator's input_or_debug_pos in the accumulator.
        return generator.InputOrDebugPos;
    }

    // ---- Misc -------------------------------------------------------------------------------------------------------------

    public static void IncBlockCounter(Isolate isolate, JSFunction function, int coverageSlot) =>
        InterpreterOps.IncBlockCounter(isolate, function, coverageSlot);

    public static void Abort(Isolate isolate, int reason) => RuntimeInternal.Abort(isolate, reason);

    public static void Illegal(int offset) =>
        throw new InvalidOperationException("V8Sharp: unexpected bytecode in baseline code at offset " + offset);

    /// <summary>SwitchOnSmiNoFeedback's case value: the accumulator as an int32 minus the case base.</summary>
    [MethodImpl(Inline)]
    public static int SwitchCase(JSValue acc, int caseValueBase) => (int)acc.Number - caseValueBase;
}
