// The operations Maglev code calls, in the role of the builtins and the
// out-of-line code maglev-code-generator.cc / maglev-ir.cc emit for the
// nodes (GenerateCode of CheckMaps, LoadTaggedField, StoreTaggedField...,
// the deferred code of the checks, and the runtime calls of
// CallKnownJSFunction, EnterInlinedFrame, ...). Small ones are inlined into
// the generated IL by RyuJIT; the IL itself does the int32/float64
// arithmetic, the comparisons and the branches.
//
// Each helper relies on the checks the graph put before it (a map check
// guarantees the object's class), as V8's machine code does: they use
// Unsafe.As where the check makes the cast valid.
using System.Runtime.CompilerServices;
using V8Sharp.Base.Numbers;
using V8Sharp.Builtins;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

public static class MaglevBuiltins
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    // ---- Type tests (CheckSmi, CheckNumber, CheckHeapObject, CheckString ...) -------------------------

    [MethodImpl(Inline)]
    public static bool IsNumber(JSValue v) => ReferenceEquals(v._obj, NumberTag.Instance);

    /// <summary>A Smi (31-bit, integral, not -0), as JSValue.IsSmi.</summary>
    [MethodImpl(Inline)]
    public static bool IsSmi(JSValue v)
    {
        if (!ReferenceEquals(v._obj, NumberTag.Instance)) return false;
        double d = v._num;
        int i = (int)d;
        return i == d && i >= JSValue.SmiMinValue && i <= JSValue.SmiMaxValue && (i != 0 || BitConverter.DoubleToInt64Bits(d) == 0);
    }

    [MethodImpl(Inline)]
    public static bool IsHeapObject(JSValue v) => v._obj is not null && !ReferenceEquals(v._obj, NumberTag.Instance);

    [MethodImpl(Inline)]
    public static bool IsString(JSValue v) => v._obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString;

    [MethodImpl(Inline)]
    public static bool IsInternalizedString(JSValue v) =>
        v._obj is { } o && o.InstanceType <= InstanceTypeChecks.LastString && Unsafe.As<JSString>(o).IsInternalized;

    [MethodImpl(Inline)]
    public static bool IsSymbol(JSValue v) => v._obj is Symbol;

    [MethodImpl(Inline)]
    public static bool IsJSReceiver(JSValue v) => v._obj is { } o && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver;

    [MethodImpl(Inline)]
    public static bool IsNumberOrOddball(JSValue v) =>
        v._obj is null || ReferenceEquals(v._obj, NumberTag.Instance) || ReferenceEquals(v._obj, Oddball.Null) ||
        ReferenceEquals(v._obj, Oddball.True) || ReferenceEquals(v._obj, Oddball.False);

    [MethodImpl(Inline)]
    public static bool IsNumberOrBoolean(JSValue v) =>
        ReferenceEquals(v._obj, NumberTag.Instance) || ReferenceEquals(v._obj, Oddball.True) || ReferenceEquals(v._obj, Oddball.False);

    /// <summary>The number value of a number or oddball (undefined is NaN).</summary>
    [MethodImpl(Inline)]
    public static double NumberOrOddballToFloat64(JSValue v)
    {
        if (ReferenceEquals(v._obj, NumberTag.Instance)) return v._num;
        return v._obj is null ? double.NaN : Unsafe.As<Oddball>(v._obj).ToNumberValue;
    }

    /// <summary>The elements are writable in place (not copy-on-write).</summary>
    [MethodImpl(Inline)]
    public static bool IsWritableElements(JSValue elements) => !Unsafe.As<FixedArrayBase>(elements._obj!).IsCowArray;

    /// <summary>CheckValidityCell: the prototype chain validity cell is still valid.</summary>
    [MethodImpl(Inline)]
    public static bool IsValidCell(Cell cell) => cell.Value._num == 0 && ReferenceEquals(cell.Value._obj, NumberTag.Instance);

    [MethodImpl(Inline)]
    public static bool IsHoleNaN(double d) => BitConverter.DoubleToInt64Bits(d) == EngineGlobals.kHoleNanInt64;

    /// <summary>The map of a JSReceiver, or null (LoadTaggedField(object, kMapOffset)).</summary>
    [MethodImpl(Inline)]
    public static Map? MapOf(JSValue v) =>
        v._obj is { } o && o.InstanceType >= InstanceTypeChecks.FirstJSReceiver ? Unsafe.As<JSReceiver>(o).Map : null;

    // ---- Conversions ------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue Int32ToNumber(int value) => JSValue.FromInt(value);

    [MethodImpl(Inline)]
    public static JSValue Uint32ToNumber(int value) => JSValue.FromNumber((uint)value);

    [MethodImpl(Inline)]
    public static JSValue Float64ToTagged(double value) => JSValue.FromNumber(value);

    /// <summary>HoleyFloat64ToTagged: the hole NaN is undefined.</summary>
    [MethodImpl(Inline)]
    public static JSValue HoleyFloat64ToTagged(double value) => IsHoleNaN(value) ? JSValue.Undefined : JSValue.FromNumber(value);

    /// <summary>
    /// Float64Min: NaN if either is NaN, and -0 below +0 (JS Math.min; the
    /// JIT's Math.Min intrinsic can return +0 for (-0, +0)).
    /// </summary>
    public static double Float64Min(double a, double b)
    {
        if (a < b) return a;
        if (b < a) return b;
        if (a == b) return BitConverter.DoubleToInt64Bits(a) < 0 ? a : b;
        return double.NaN;
    }

    /// <summary>Float64Max: NaN if either is NaN, and +0 above -0.</summary>
    public static double Float64Max(double a, double b)
    {
        if (a > b) return a;
        if (b > a) return b;
        if (a == b) return BitConverter.DoubleToInt64Bits(a) < 0 ? b : a;
        return double.NaN;
    }

    // ---- Deopt exits: the values of a frame state into the scratch buffer --------------------------------

    public static void Spill1(JSValue[] s, int i, JSValue a) => s[i] = a;

    public static void Spill2(JSValue[] s, int i, JSValue a, JSValue b)
    {
        s[i] = a;
        s[i + 1] = b;
    }

    public static void Spill4(JSValue[] s, int i, JSValue a, JSValue b, JSValue c, JSValue d)
    {
        s[i] = a;
        s[i + 1] = b;
        s[i + 2] = c;
        s[i + 3] = d;
    }

    public static void Spill8(JSValue[] s, int i, JSValue a, JSValue b, JSValue c, JSValue d, JSValue e, JSValue f, JSValue g,
        JSValue h)
    {
        s[i] = a;
        s[i + 1] = b;
        s[i + 2] = c;
        s[i + 3] = d;
        s[i + 4] = e;
        s[i + 5] = f;
        s[i + 6] = g;
        s[i + 7] = h;
    }

    /// <summary>ConvertHoleToUndefined.</summary>
    [MethodImpl(Inline)]
    public static JSValue ConvertHoleToUndefined(JSValue value) => value.IsTheHole ? JSValue.Undefined : value;

    [MethodImpl(Inline)]
    public static int TruncateFloat64ToInt32(double value) => Conversions.DoubleToInt32(value);

    [MethodImpl(Inline)]
    public static JSValue Boolean(bool value) => value ? JSValue.True : JSValue.False;

    // ---- Tests -----------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue TaggedEqual(JSValue a, JSValue b) => a.IsIdenticalTo(b) ? JSValue.True : JSValue.False;

    [MethodImpl(Inline)]
    public static bool ToBoolean(JSValue v) => InterpreterOps.ToBoolean(v);

    [MethodImpl(Inline)]
    public static bool Float64ToBoolean(double d) => d != 0 && !double.IsNaN(d);

    [MethodImpl(Inline)]
    public static bool IsTrue(JSValue v) => ReferenceEquals(v._obj, Oddball.True);

    [MethodImpl(Inline)]
    public static bool IsUndefinedOrNull(JSValue v) => v._obj is null || ReferenceEquals(v._obj, Oddball.Null);

    [MethodImpl(Inline)]
    public static bool IsIdentical(JSValue a, JSValue b) => a.IsIdenticalTo(b);

    public static JSValue TestUndetectable(JSValue v) => JSValue.FromBoolean(InterpreterOps.IsUndetectable(v));

    public static JSValue TestTypeOf(JSValue v, int literal) => BaselineBuiltinsBridge.TestTypeOf(v, literal);

    // ---- Fields -----------------------------------------------------------------------------------------

    /// <summary>LoadTaggedField: the field at a storage index (FieldIndex.StorageIndex) of a map-checked JSObject.</summary>
    [MethodImpl(Inline)]
    public static JSValue LoadField(JSValue obj, int storageIndex) => Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex);

    /// <summary>StoreTaggedField.</summary>
    [MethodImpl(Inline)]
    public static void StoreField(JSValue obj, int storageIndex, JSValue value) =>
        Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex) = value;

    /// <summary>A store to a Double field: NaNs are canonicalized (StoreIC's CanonicalizeDouble).</summary>
    [MethodImpl(Inline)]
    public static void StoreDoubleField(JSValue obj, int storageIndex, JSValue value)
    {
        if (double.IsNaN(value._num)) value = JSValue.NaN;
        Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex) = value;
    }

    /// <summary>
    /// A field-adding map transition (StoreMap + ExtendPropertiesBackingStore +
    /// StoreTaggedField, as StoreIC's TryStoreTransition does it).
    /// </summary>
    public static void StoreTransition(JSValue obj, Map transition, int storageIndex, JSValue value, bool isDouble)
    {
        JSObject o = Unsafe.As<JSObject>(obj._obj!);
        int arrayIndex = storageIndex - JSObject.kPropertyArrayStorageBase;
        if (arrayIndex >= o._fields.Length) o.EnsurePropertyArrayLength(arrayIndex + transition.UnusedPropertyFields() + 1);
        if (isDouble && double.IsNaN(value._num) && ReferenceEquals(value._obj, NumberTag.Instance)) value = JSValue.NaN;
        o.FieldAt(storageIndex) = value;
        o.Map = transition;
    }

    /// <summary>A store to a const field succeeds only when it does not change the value (StoreHandler kConstField).</summary>
    public static bool CheckConstFieldValue(JSValue obj, int storageIndex, JSValue value)
    {
        JSValue current = Unsafe.As<JSObject>(obj._obj!).FieldAt(storageIndex);
        if (current.IsNumber && value.IsNumber)
        {
            return BitConverter.DoubleToInt64Bits(current._num) == BitConverter.DoubleToInt64Bits(value._num);
        }
        return current.IsIdenticalTo(value);
    }

    // ---- Elements ---------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LoadElements(JSValue obj) => JSValue.FromObject(Unsafe.As<JSObject>(obj._obj!).Elements);

    [MethodImpl(Inline)]
    public static JSValue LoadJSArrayLength(JSValue array) => Unsafe.As<JSArray>(array._obj!).Length;

    [MethodImpl(Inline)]
    public static int LoadFixedArrayLength(JSValue elements) =>
        elements._obj is FixedArray f ? f._data.Length : Unsafe.As<FixedArrayBase>(elements._obj!).Length;

    [MethodImpl(Inline)]
    public static JSValue LoadFixedArrayElement(JSValue elements, int index) => Unsafe.As<FixedArray>(elements._obj!)._data[index];

    [MethodImpl(Inline)]
    public static double LoadFixedDoubleArrayElement(JSValue elements, int index) =>
        Unsafe.As<FixedDoubleArray>(elements._obj!)._data[index];

    [MethodImpl(Inline)]
    public static void StoreFixedArrayElement(JSValue elements, int index, JSValue value) =>
        Unsafe.As<FixedArray>(elements._obj!)._data[index] = value;

    [MethodImpl(Inline)]
    public static void StoreFixedDoubleArrayElement(JSValue elements, int index, double value)
    {
        // Every NaN but the hole is stored canonical (FixedDoubleArray::set).
        if (double.IsNaN(value)) value = double.NaN;
        Unsafe.As<FixedDoubleArray>(elements._obj!)._data[index] = value;
    }

    /// <summary>CheckValueEqualsString (and the keyed name's primitive, the hole if none).</summary>
    public static bool ValueEqualsString(JSValue value, JSString expected, JSValue primitive)
    {
        if (ReferenceEquals(value._obj, expected)) return true;
        if (value._obj is JSString s) return JSString.Equals(s, expected);
        return !primitive.IsTheHole && value.IsIdenticalTo(primitive);
    }

    /// <summary>charCodeAt, NaN for an index outside the string.</summary>
    public static double StringCharCodeAtOrNaN(JSValue s, int index)
    {
        var str = Unsafe.As<JSString>(s._obj!);
        return (uint)index < (uint)str.Length ? StringCharCodeAt(s, index) : double.NaN;
    }

    /// <summary>CheckedObjectToIndex: the int32 index of a Smi, an integral HeapNumber or an array index String.</summary>
    public static bool TryObjectToIndex(JSValue value, out int index)
    {
        if (value.IsNumber)
        {
            double d = value.Number;
            index = (int)d;
            return index == d;
        }
        if (value._obj is JSString s && s.AsArrayIndex(out uint u) && u <= int.MaxValue)
        {
            index = (int)u;
            return true;
        }
        index = 0;
        return false;
    }

    /// <summary>
    /// The start of an exception trampoline (Isolate::UnwindAndFindHandler
    /// for a Maglev frame): drops the frames and registers the exception left
    /// above this frame, sets the pending message, and returns the exception.
    /// </summary>
    public static JSValue EnterCatchBlock(object exception, Isolate isolate, int frameIndex, int registerTop)
    {
        var e = (JavaScriptException)exception;
        if (isolate.InterpreterFrameDepth > frameIndex + 1) isolate.PopFramesTo(frameIndex + 1);
        if (isolate.RegisterStackTop > registerTop) isolate.ReleaseRegisters(registerTop);
        isolate.PendingMessage = e.MessageObject is { } message ? JSValue.FromObject(message) : JSValue.TheHole;
        return e.Value;
    }

    /// <summary>
    /// MaybeGrowFastElements + EnsureWritableFastElements for a store at
    /// <paramref name="index"/> of a map-checked object with fast elements
    /// of <paramref name="kind"/>: makes the elements writable, grows them
    /// for an append (index == length), and updates a JSArray's length.
    /// Returns the elements, or undefined to deoptimize (a store that would
    /// leave the fast kinds, a non-append beyond the length).
    /// </summary>
    public static JSValue MaybeGrowFastElements(Isolate isolate, JSValue obj, int index, int isJSArray, int kind)
    {
        JSObject o = Unsafe.As<JSObject>(obj._obj!);
        // The object's own kind: a map check over several maps may have
        // passed a map of a packed kind where the group's kind is holey.
        ElementsKind e = o.Map.ElementsKind;
        _ = kind;
        int elementsLength = o.Elements.Length;
        int length = isJSArray != 0 ? (int)Unsafe.As<JSArray>(o).Length._num : elementsLength;
        // TryBuildElementStoreOnJSArrayOrJSObject's limit: holey kinds may
        // grow by up to kMaxGap past the capacity, packed arrays only append,
        // and packed non-arrays never grow (that needs a map transition).
        long limit = ElementsKinds.IsHoleyElementsKind(e) ? (long)elementsLength + JSObject.kMaxGap
            : isJSArray != 0 ? length + 1L : elementsLength;
        if ((uint)index >= limit) return default;
        if (o.Elements.IsCowArray) JSObject.EnsureWritableFastElements(isolate, o);
        FixedArrayBase elements = o.Elements;
        if (index >= elements.Length)
        {
            // Grow as Runtime_GrowArrayElements / the CSA grow path (new capacity from NewElementsCapacity).
            if (index >= FixedArrayBase.kMaxLength) return default;
            int capacity = JSObject.NewElementsCapacity(index + 1);
            if (ElementsKinds.IsDoubleElementsKind(e))
            {
                var grown = new FixedDoubleArray(capacity);
                // An empty double array's elements are the empty FixedArray.
                int oldLength = 0;
                if (elements is FixedDoubleArray old)
                {
                    Array.Copy(old._data, grown._data, old.Length);
                    oldLength = old.Length;
                }
                grown._data.AsSpan(oldLength).Fill(FixedDoubleArray.HoleNaN);
                o.Elements = grown;
            }
            else
            {
                var grown = new FixedArray(capacity);
                var old = (FixedArray)elements;
                Array.Copy(old._data, grown._data, old.Length);
                grown._data.AsSpan(old.Length).Fill(JSValue.TheHole);
                o.Elements = grown;
            }
        }
        if (isJSArray != 0 && index >= length) Unsafe.As<JSArray>(o).Length = JSValue.FromInt(index + 1);
        return JSValue.FromObject(o.Elements);
    }

    // ---- Strings -------------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static int StringLength(JSValue s) => Unsafe.As<JSString>(s._obj!).Length;

    [MethodImpl(Inline)]
    public static int StringCharCodeAt(JSValue s, int index)
    {
        JSString str = Unsafe.As<JSString>(s._obj!);
        return str is SeqString seq ? seq.Value[index] : str.Get(index);
    }

    public static JSValue StringAdd(Isolate isolate, JSValue left, JSValue right) =>
        InterpreterOps.StringAdd(isolate, Unsafe.As<JSString>(left._obj!), Unsafe.As<JSString>(right._obj!));

    /// <summary>A comparison of two strings (StringEqual / StringLessThan ...).</summary>
    public static JSValue StringCompare(JSValue left, JSValue right, int op)
    {
        JSString l = Unsafe.As<JSString>(left._obj!), r = Unsafe.As<JSString>(right._obj!);
        ComparisonResult result = JSString.Compare(l, r);
        bool value = (CompareOperation)op switch
        {
            CompareOperation.kEqual or CompareOperation.kStrictEqual => result == ComparisonResult.Equal,
            CompareOperation.kLessThan => result == ComparisonResult.LessThan,
            CompareOperation.kLessThanOrEqual => result != ComparisonResult.GreaterThan,
            CompareOperation.kGreaterThan => result == ComparisonResult.GreaterThan,
            _ => result != ComparisonResult.LessThan,
        };
        return value ? JSValue.True : JSValue.False;
    }

    // ---- Contexts -----------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static JSValue LoadContextSlot(JSValue context, int depth, int index)
    {
        Context c = Unsafe.As<Context>(context._obj!);
        while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
        return c.Slots[index];
    }

    [MethodImpl(Inline)]
    public static void StoreContextSlot(JSValue context, int depth, int index, JSValue value)
    {
        Context c = Unsafe.As<Context>(context._obj!);
        while (depth-- > 0) c = Unsafe.As<Context>(c.Slots[(int)Context.Field.PREVIOUS_INDEX]._obj!);
        c.Slots[index] = value;
    }

    /// <summary>The current context changes (PushContext / PopContext): isolate.Context and the frame's slot.</summary>
    [MethodImpl(Inline)]
    public static void SetCurrentContext(Isolate isolate, ref JSValue frameContextSlot, JSValue context)
    {
        frameContextSlot = context;
        isolate.Context = Unsafe.As<Context>(context._obj!);
    }

    // ---- Math ----------------------------------------------------------------------------------------------

    [MethodImpl(Inline)]
    public static double Float64Round(double x, int kind) => kind switch
    {
        0 => Math.Floor(x),
        1 => Math.Ceiling(x),
        2 => BuiltinsMath.Float64Round(x),
        _ => Math.Truncate(x),
    };

    [MethodImpl(Inline)]
    public static double Float64Exponentiate(double x, double y) => InternalMath.pow(x, y);

    public static double Float64Ieee754Unary(double x, int builtin) => (Builtin)builtin switch
    {
        Builtin.MathSin => Base.Ieee754.sin(x),
        Builtin.MathCos => Base.Ieee754.cos(x),
        Builtin.MathTan => Base.Ieee754.tan(x),
        Builtin.MathExp => Base.Ieee754.exp(x),
        Builtin.MathLog => Base.Ieee754.log(x),
        Builtin.MathAtan => Base.Ieee754.atan(x),
        Builtin.MathAsin => Base.Ieee754.asin(x),
        Builtin.MathAcos => Base.Ieee754.acos(x),
        Builtin.MathLog2 => Base.Ieee754.log2(x),
        Builtin.MathLog10 => Base.Ieee754.log10(x),
        Builtin.MathCbrt => Base.Ieee754.cbrt(x),
        _ => throw new InvalidOperationException("not an ieee754 unary builtin"),
    };

    [MethodImpl(Inline)]
    public static double Float64Atan2(double y, double x) => Base.Ieee754.atan2(y, x);

    /// <summary>Int32DivideWithOverflow: long.MinValue (deopt) for a fraction, -0, overflow or division by zero.</summary>
    [MethodImpl(Inline)]
    public static long Int32Divide(int a, int b)
    {
        if (b == 0) return long.MinValue;
        if (a == 0 && b < 0) return long.MinValue;
        if (a == int.MinValue && b == -1) return long.MinValue;
        int q = a / b;
        if (q * b != a) return long.MinValue;
        return q;
    }

    /// <summary>Int32ModulusWithOverflow: long.MinValue (deopt) for a -0 result or a zero divisor.</summary>
    [MethodImpl(Inline)]
    public static long Int32Modulus(int a, int b)
    {
        if (b == 0) return long.MinValue;
        if (b == -1 || b == int.MinValue && a == int.MinValue)
        {
            // The result is 0 (or -0 for a negative dividend).
            return a < 0 ? long.MinValue : 0;
        }
        int r = a % b;
        if (r == 0 && a < 0) return long.MinValue;
        return r;
    }

    // ---- Calls ---------------------------------------------------------------------------------------------

    /// <summary>
    /// CallKnownJSFunction: a call of the target the feedback named (the
    /// graph checked it), with the arguments in the frame's registers; no call
    /// feedback is collected.
    /// </summary>
    public static JSValue CallKnownJSFunction(Isolate isolate, JSValue target, JSValue receiver, int argsStart, int argc, int mode) =>
        MaglevCalls.Call(isolate, target, receiver, argsStart, argc, (ConvertReceiverMode)mode);

    /// <summary>
    /// CallForwardVarargs: f.apply(thisArg, arguments) with the frame's
    /// arguments object. An elided object (undefined here) means the frame's
    /// actual arguments are passed; otherwise CallWithArrayLike.
    /// </summary>
    public static JSValue CallForwardArguments(Isolate isolate, ref InterpreterState state, JSValue target, JSValue receiver,
        JSValue argumentsObject)
    {
        if (argumentsObject._obj is not null)
        {
            return Builtins.BuiltinsFunction.CallWithArrayLike(isolate, target, receiver, argumentsObject);
        }
        int argc = state.Argc;
        int fp = state.Fp;
        int start = isolate.AllocateRegisters(argc);
        JSValue[] stack = isolate.RegisterStack;
        for (int i = 0; i < argc; i++) stack[start + i] = stack[fp + InterpreterRuntime.kFirstArgumentOffset - i];
        try
        {
            return MaglevCalls.Call(isolate, target, receiver, start, argc, ConvertReceiverMode.Any);
        }
        finally
        {
            isolate.RegisterStackTop = start;
        }
    }

    /// <summary>Construct of a known base constructor with the receiver FastNewObject allocated.</summary>
    public static JSValue ConstructKnownJSFunction(Isolate isolate, JSValue target, JSValue receiver, JSValue newTarget, int argsStart,
        int argc) =>
        MaglevCalls.ConstructWithReceiver(isolate, target, receiver, newTarget, argsStart, argc);

    /// <summary>CheckConstructResult: an object result replaces the constructed receiver.</summary>
    [MethodImpl(Inline)]
    public static JSValue ConstructResult(JSValue result, JSValue receiver) => result.IsJSReceiver ? result : receiver;

    /// <summary>FastNewObject.</summary>
    public static JSValue FastNewObject(Isolate isolate, Map initialMap) => isolate.Factory.NewJSObjectFromMap(initialMap);

    // ---- Throw, interrupts ---------------------------------------------------------------------------------

    public static void Throw(Isolate isolate, JSValue exception)
    {
        JSMessageObject? message = InterpreterExecution.CreateMessageForThrow(isolate, exception);
        throw new JavaScriptException(exception, message);
    }

    public static void ReThrow(Isolate isolate, JSValue exception)
    {
        JSMessageObject? message = isolate.PendingMessage.HeapObjectOrNull as JSMessageObject;
        throw new JavaScriptException(exception, message);
    }

    /// <summary>HandleNoHeapWritesInterrupt: serves pending interrupts (termination) at a loop back edge.</summary>
    [MethodImpl(Inline)]
    public static void HandleInterrupts(Isolate isolate)
    {
        if (isolate.StackGuard.HasPendingInterrupts) HandleInterruptsSlow(isolate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HandleInterruptsSlow(Isolate isolate) => isolate.StackGuard.HandleInterrupts();

    public static void Unreachable() => throw new InvalidOperationException("V8Sharp: unreachable Maglev code");

    // ---- Inlined frames ------------------------------------------------------------------------------------

    /// <summary>
    /// EnterInlinedFrame: pushes the interpreter frame of an inlined function
    /// (its parameter slots, fixed slots and register window above the stack
    /// top, and a frame record) and returns its frame pointer. The caller
    /// writes the receiver and arguments.
    /// </summary>
    public static int EnterInlinedFrame(Isolate isolate, JSFunction function, BytecodeArray bytecode, int argc, bool isConstruct)
    {
        int formal = bytecode.ParameterCount - 1;
        int paramSlots = argc > formal ? argc : formal;
        int start = isolate.RegisterStackTop;
        int fp = start + paramSlots + InterpreterRuntime.kFixedSlotsAboveParams;
        int end = fp + bytecode.RegisterCount;
        if ((uint)end > (uint)isolate.RegisterStackLimit) isolate.StackOverflow();
        isolate.RegisterStackTop = end;
        JSValue[] stack = isolate.RegisterStack;
        // Missing arguments are undefined (V8's argument adaption).
        for (int i = argc; i < paramSlots; i++) stack[fp + InterpreterRuntime.kFirstArgumentOffset - i] = default;
        Context context = function.Context;
        stack[fp + InterpreterRuntime.kContextOffset] = context;
        stack[fp + InterpreterRuntime.kClosureOffset] = function;
        stack[fp + InterpreterRuntime.kFeedbackVectorOffset] = JSValue.FromObject(function.RawFeedbackCell.Value as FeedbackVector);
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
        ref InterpreterFrameRecord frame = ref isolate.PushFrame();
        frame.Function = function;
        frame.Bytecode = bytecode;
        frame.Fp = fp;
        frame.Pc = 0;
        frame.Argc = argc;
        frame.Kind = InterpreterFrameKind.Interpreted;
        frame.IsConstructor = isConstruct;
        frame.IsBaseline = false;
        frame.IsMaglev = true;
        frame.InlineCall = false;
        frame.ReturnPc = 0;
        frame.RegisterStart = start;
        frame.Receiver = default;
        return fp;
    }

    /// <summary>LeaveInlinedFrame: pops the frame EnterInlinedFrame pushed.</summary>
    public static void LeaveInlinedFrame(Isolate isolate, int frameIndex, JSValue callerContext)
    {
        ref InterpreterFrameRecord frame = ref isolate.InterpreterFrames[frameIndex];
        int top = isolate.RegisterStackTop;
        if (top > isolate.RegisterStackDirtyEnd) isolate.RegisterStackDirtyEnd = top;
        isolate.RegisterStackTop = frame.RegisterStart;
        isolate.InterpreterFrameDepth = frameIndex;
        Context context = Unsafe.As<Context>(callerContext._obj!);
        if (!ReferenceEquals(isolate.Context, context)) isolate.Context = context;
    }
}

/// <summary>Access to baseline builtins whose signatures the IL cannot use directly.</summary>
internal static class BaselineBuiltinsBridge
{
    public static JSValue TestTypeOf(JSValue v, int literal) => Baseline.BaselineBuiltins.TestTypeOf(v, literal);
}
