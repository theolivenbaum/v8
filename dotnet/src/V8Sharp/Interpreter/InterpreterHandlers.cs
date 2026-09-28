// The bytecode handlers of src/interpreter/interpreter-generator.cc whose work
// is more than a few instructions, out of the dispatch loop (InterpreterLoop.cs
// says why). Each is the body of the handler of the same name: it decodes its
// operands from {code} at {pc} (the offset of the bytecode, after any prefix),
// reads registers relative to {fp}, and returns the new accumulator; the loop
// advances the offset. The call and construct handlers return true when they
// entered the callee in the loop (InterpreterInlineCalls), and otherwise leave
// the result in st.Accumulator.
using System.Runtime.CompilerServices;
using V8Sharp.IC;
using V8Sharp.Runtime;
using static V8Sharp.Interpreter.Operands;

namespace V8Sharp.Interpreter;

public static partial class InterpreterExecution
{
    const int kRegBase = -InterpreterRuntime.kRegisterOperandBase;

    /// <summary>
    /// SaveBytecodeOffset: records the offset of the current bytecode in the
    /// frame record before a handler calls out, for the stack walker (source
    /// positions, Error.stack) and for the exception handler lookup. The loop
    /// does not store it per bytecode, as V8's handlers do not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SavePc(Isolate isolate, ref InterpreterState st, int pc) => isolate.InterpreterFrames[st.FrameIndex].Pc = pc;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Scale<TS>() where TS : struct, IOperandScale =>
        typeof(TS) == typeof(SingleScale) ? 1 : typeof(TS) == typeof(DoubleScale) ? 2 : 4;

    /// <summary>The register named by the register operand at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref JSValue Reg<TS>(ref JSValue fp, ref byte code, int offset) where TS : struct, IOperandScale =>
        ref Unsafe.Subtract(ref fp, kRegBase + Signed<TS>(ref code, offset));

    /// <summary>
    /// A register store that skips the reference store (a GC write barrier)
    /// when the slot already holds the same object or tag: registers often keep
    /// numbers (the tag never changes) or the same object across iterations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreRegister(ref JSValue slot, JSValue value)
    {
        if (!ReferenceEquals(slot._obj, value._obj)) Unsafe.AsRef(in slot._obj) = value._obj;
        Unsafe.AsRef(in slot._num) = value._num;
    }

    /// <summary>ToBoolean on a value passed by value (the loop's accumulator must not have its address taken).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ToBoolean(JSValue value)
    {
        HeapObject? o = value._obj;
        if (ReferenceEquals(o, Oddball.True)) return true;
        if (ReferenceEquals(o, Oddball.False) || o is null) return false;
        if (ReferenceEquals(o, NumberTag.Instance))
        {
            double d = value._num;
            return d != 0 && !double.IsNaN(d);
        }
        return ToBooleanSlow(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ToBooleanSlow(JSValue value) => ObjectOps.BooleanValue(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool IsUndetectableReceiver(HeapObject o) => o is JSReceiver r && r.Map.IsUndetectable;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TestTypeOf(JSValue value, TestTypeOfFlags.LiteralFlag literal) => InterpreterOps.TestTypeOf(value, literal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowNestedPrefix() => throw new InvalidOperationException("V8Sharp: nested operand scale prefix");

    /// <summary>
    /// Wide / ExtraWide: runs the prefixed bytecode at st.Pc in the loop for its
    /// scale. True when it returned from the frame (the result in st.Accumulator).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool RunPrefixed(Isolate isolate, ref InterpreterState st)
    {
        bool wide = (Bytecode)st.Bytecode.Bytecodes[st.Pc - 1] == Bytecode.Wide;
        if (wide) Loop<DoubleScale>(isolate, ref st);
        else Loop<QuadrupleScale>(isolate, ref st);
        if (st.Done)
        {
            st.Done = false;
            return true;
        }
        return false;
    }

    // ---- Contexts --------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PushContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        // Saves the current context in <context>, and pushes the accumulator
        // as the new current context.
        Reg<TS>(ref fp, ref code, pc + 1) = st.Context;
        st.Context = acc.UncheckedAs<Context>();
        Unsafe.Add(ref fp, InterpreterRuntime.kContextOffset) = st.Context;
        isolate.Context = st.Context;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PopContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        st.Context = Reg<TS>(ref fp, ref code, pc + 1).UncheckedAs<Context>();
        Unsafe.Add(ref fp, InterpreterRuntime.kContextOffset) = st.Context;
        isolate.Context = st.Context;
    }

    // ---- Globals -----------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue LdaGlobal<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        TypeofMode mode = (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.LdaGlobal ? TypeofMode.NotInside : TypeofMode.Inside;
        return LoadGlobalIC.Load(isolate, st.FeedbackVector, slot, st.Context, name, mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StaGlobal<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        StoreGlobalIC.Store(isolate, st.FeedbackVector, slot, st.Context, name, acc);
    }

    // ---- Property loads and stores --------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue GetNamedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue receiver = Reg<TS>(ref fp, ref code, pc + 1);
        var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        return LoadIC.LoadNamed(isolate, st.FeedbackVector, slot, receiver, name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue GetKeyedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref code, pc + 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        return KeyedLoadIC.Load(isolate, st.FeedbackVector, slot, obj, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetNamedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref code, pc + 1);
        var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        StoreIC.StoreNamed(isolate, st.FeedbackVector, slot, obj, name, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DefineNamedOwnProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref code, pc + 1);
        var name = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1 + S)].UncheckedAs<Name>();
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        StoreIC.DefineNamedOwn(isolate, st.FeedbackVector, slot, obj, name, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetKeyedProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref code, pc + 1);
        JSValue key = Reg<TS>(ref fp, ref code, pc + 1 + S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        KeyedStoreIC.Store(isolate, st.FeedbackVector, slot, obj, key, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StaInArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue array = Reg<TS>(ref fp, ref code, pc + 1);
        JSValue index = Reg<TS>(ref fp, ref code, pc + 1 + S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        KeyedStoreIC.StoreInArrayLiteral(isolate, st.FeedbackVector, slot, array, index, acc);
    }

    // ---- Binary operators ----------------------------------------------------------

    /// <summary>Add when an operand is not a number (strings, objects, BigInts).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue AddSlow<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        return InterpreterOps.AddSlow(isolate, Reg<TS>(ref fp, ref code, pc + 1), acc, ref Unsafe.Add(ref code, pc + 1 + S));
    }

    /// <summary>The binary operators with a register operand (Sub and Mul when an operand is not a number).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue BinaryOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue lhs = Reg<TS>(ref fp, ref code, pc + 1);
        ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
        switch ((Bytecode)Unsafe.Add(ref code, pc))
        {
            case Bytecode.Sub:
                if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                {
                    return InterpreterOps.SubtractNumbers(lhs._num, acc._num, ref feedback);
                }
                return InterpreterOps.BinarySlow(isolate, Operation.Subtract, lhs, acc, ref feedback);
            case Bytecode.Mul:
                if (lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance)
                {
                    return InterpreterOps.MultiplyNumbers(lhs._num, acc._num, ref feedback);
                }
                return InterpreterOps.BinarySlow(isolate, Operation.Multiply, lhs, acc, ref feedback);
            case Bytecode.Div:
                return InterpreterOps.Binary(isolate, Operation.Divide, lhs, acc, ref feedback);
            case Bytecode.Mod:
                return InterpreterOps.Binary(isolate, Operation.Modulus, lhs, acc, ref feedback);
            case Bytecode.BitwiseOr:
                return InterpreterBitwise.Binary<BitwiseOrOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.BitwiseXor:
                return InterpreterBitwise.Binary<BitwiseXorOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.BitwiseAnd:
                return InterpreterBitwise.Binary<BitwiseAndOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.ShiftLeft:
                return InterpreterBitwise.Binary<ShiftLeftOp>(isolate, lhs, acc, ref feedback);
            case Bytecode.ShiftRight:
                return InterpreterBitwise.Binary<ShiftRightOp>(isolate, lhs, acc, ref feedback);
            default:
                Debug.Assert((Bytecode)Unsafe.Add(ref code, pc) == Bytecode.ShiftRightLogical);
                return InterpreterBitwise.Binary<ShiftRightLogicalOp>(isolate, lhs, acc, ref feedback);
        }
    }

    /// <summary>The binary operators with an immediate (AddSmi and SubSmi when the accumulator is not a number).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue BinarySmiOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        int imm = Signed<TS>(ref code, pc + 1);
        ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
        switch ((Bytecode)Unsafe.Add(ref code, pc))
        {
            case Bytecode.AddSmi:
                if (acc._obj == NumberTag.Instance) return InterpreterOps.AddNumbers(isolate, acc._num, imm, ref feedback);
                return InterpreterOps.AddSlow(isolate, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.SubSmi:
                if (acc._obj == NumberTag.Instance) return InterpreterOps.SubtractNumbers(acc._num, imm, ref feedback);
                return InterpreterOps.BinarySlow(isolate, Operation.Subtract, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.MulSmi:
                return InterpreterOps.Binary(isolate, Operation.Multiply, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.DivSmi:
                return InterpreterOps.Binary(isolate, Operation.Divide, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.ModSmi:
                return InterpreterOps.Binary(isolate, Operation.Modulus, acc, JSValue.FromInt(imm), ref feedback);
            case Bytecode.BitwiseOrSmi:
                return InterpreterBitwise.WithSmi<BitwiseOrOp>(isolate, acc, imm, ref feedback);
            case Bytecode.BitwiseXorSmi:
                return InterpreterBitwise.WithSmi<BitwiseXorOp>(isolate, acc, imm, ref feedback);
            case Bytecode.BitwiseAndSmi:
                return InterpreterBitwise.WithSmi<BitwiseAndOp>(isolate, acc, imm, ref feedback);
            case Bytecode.ShiftLeftSmi:
                return InterpreterBitwise.WithSmi<ShiftLeftOp>(isolate, acc, imm, ref feedback);
            case Bytecode.ShiftRightSmi:
                return InterpreterBitwise.WithSmi<ShiftRightOp>(isolate, acc, imm, ref feedback);
            default:
                Debug.Assert((Bytecode)Unsafe.Add(ref code, pc) == Bytecode.ShiftRightLogicalSmi);
                return InterpreterBitwise.WithSmi<ShiftRightLogicalOp>(isolate, acc, imm, ref feedback);
        }
    }

    /// <summary>Inc and Dec when the accumulator is not a number, Negate, BitwiseNot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue UnaryOp<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        ref byte feedback = ref Unsafe.Add(ref code, pc + 1);
        return (Bytecode)Unsafe.Add(ref code, pc) switch
        {
            Bytecode.Inc => InterpreterOps.Increment(isolate, acc, ref feedback),
            Bytecode.Dec => InterpreterOps.Decrement(isolate, acc, ref feedback),
            Bytecode.Negate => InterpreterOps.Negate(isolate, acc, ref feedback),
            _ => InterpreterOps.BitwiseNot(isolate, acc, ref feedback),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ToNumberOrNumeric<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        return InterpreterOps.ToNumberOrNumeric(isolate, acc, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1),
            numeric: (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.ToNumeric);
    }

    // ---- Compare operations ---------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestEqual<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        return InterpreterOps.Equal(isolate, Reg<TS>(ref fp, ref code, pc + 1), acc, ref Unsafe.Add(ref code, pc + 1 + S));
    }

    /// <summary>TestLessThan and friends when an operand is not a number.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue Relational<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        Operation op = (Bytecode)Unsafe.Add(ref code, pc) switch
        {
            Bytecode.TestLessThan => Operation.LessThan,
            Bytecode.TestGreaterThan => Operation.GreaterThan,
            Bytecode.TestLessThanOrEqual => Operation.LessThanOrEqual,
            _ => Operation.GreaterThanOrEqual,
        };
        JSValue lhs = Reg<TS>(ref fp, ref code, pc + 1);
        ref byte feedback = ref Unsafe.Add(ref code, pc + 1 + S);
        return lhs._obj == NumberTag.Instance && acc._obj == NumberTag.Instance
            ? InterpreterOps.CompareNumbers(op, lhs._num, acc._num, ref feedback)
            : InterpreterOps.Relational(isolate, op, lhs, acc, ref feedback);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestInstanceOf<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue obj = Reg<TS>(ref fp, ref code, pc + 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        return InterpreterOps.InstanceOf(isolate, st.FeedbackVector, slot, obj, acc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestIn<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue name = Reg<TS>(ref fp, ref code, pc + 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        return KeyedHasIC.Has(isolate, st.FeedbackVector, slot, acc, name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue TestEqualStrict<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        int S = Scale<TS>();
        return InterpreterOps.StrictEqual(Reg<TS>(ref fp, ref code, pc + 1), acc, ref Unsafe.Add(ref code, pc + 1 + S));
    }

    // ---- Literals, closures, contexts, arguments ------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue description = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        int flags = Byte(ref code, pc + 1 + 2 * S);
        if (st.FeedbackVector is { } fv && CreateArrayLiteralFlags.DecodeFastCloneSupported((byte)flags) &&
            RuntimeLiterals.TryCreateShallowLiteral(isolate, fv, slot, CreateArrayLiteralFlags.DecodeFlags((byte)flags)) is { } shallow)
        {
            return shallow;
        }
        return RuntimeLiterals.CreateArrayLiteral(isolate, st.FeedbackVector, slot, description.UncheckedAs<ArrayBoilerplateDescription>(),
            CreateArrayLiteralFlags.DecodeFlags((byte)flags));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateEmptyArrayLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        return RuntimeLiterals.CreateEmptyArrayLiteral(isolate, st.FeedbackVector, Unsigned<TS>(ref code, pc + 1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateObjectLiteral<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue description = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)];
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        int flags = Byte(ref code, pc + 1 + 2 * S);
        if (st.FeedbackVector is { } fv && CreateObjectLiteralFlags.DecodeFastCloneSupported((byte)flags) &&
            RuntimeLiterals.TryCreateShallowLiteral(isolate, fv, slot, CreateObjectLiteralFlags.DecodeFlags((byte)flags)) is { } shallow)
        {
            return shallow;
        }
        return RuntimeLiterals.CreateObjectLiteral(isolate, st.FeedbackVector, slot, description.UncheckedAs<ObjectBoilerplateDescription>(),
            CreateObjectLiteralFlags.DecodeFlags((byte)flags));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateEmptyObjectLiteral(Isolate isolate, ref InterpreterState st) =>
        RuntimeLiterals.CreateEmptyObjectLiteral(isolate, st.Context.NativeContext);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateClosure<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        var shared = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<SharedFunctionInfo>();
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        FeedbackCell cell = JSFunctionFeedback.GetClosureFeedbackCellArray(st.Function).Get(slot);
        return RuntimeClosures.NewClosure(isolate, shared, st.Context, cell);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateFunctionContext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        var scopeInfo = st.Bytecode.ConstantPoolValues![Unsigned<TS>(ref code, pc + 1)].UncheckedAs<ScopeInfo>();
        return RuntimeScopes.NewFunctionContext(isolate, st.Context, scopeInfo,
            (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CreateEvalContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateMappedArguments(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewSloppyArguments(isolate, st.Function, st.Context, st.Fp, st.Argc);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CreateUnmappedArguments(Isolate isolate, ref InterpreterState st) =>
        InterpreterArguments.NewStrictArguments(isolate, st.Function, st.Fp, st.Argc);

    // ---- Control flow -------------------------------------------------------------

    /// <summary>JumpLoop's budget interrupt (InterpreterTiering.OnBudgetInterrupt with the stack check).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void JumpLoopInterrupt(Isolate isolate, ref InterpreterState st, ref JSValue fp, int pc)
    {
        SavePc(isolate, ref st, pc);
        st.FeedbackVector = InterpreterTiering.OnBudgetInterrupt(isolate, st.Function, withStackCheck: true);
        Unsafe.Add(ref fp, InterpreterRuntime.kFeedbackVectorOffset) = st.FeedbackVector is null ? default(JSValue) : st.FeedbackVector;
    }

    /// <summary>
    /// Throw: creates the message like Isolate::Throw and continues at this
    /// frame's handler (st.Pc, st.Accumulator), or throws the exception out of
    /// the frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowAccumulator(Isolate isolate, ref InterpreterState st, int pc, JSValue acc)
    {
        SavePc(isolate, ref st, pc);
        JSMessageObject? message = CreateMessageForThrow(isolate, acc);
        if (TryDispatchToHandler(isolate, ref st, acc, message)) return;
        throw new JavaScriptException(acc, message);
    }

    // ---- for-in -------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ForInEnumerate<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        return RuntimeForIn.ForInEnumerate(isolate, Reg<TS>(ref fp, ref code, pc + 1).As<JSReceiver>());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ForInPrepare<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        int output = Signed<TS>(ref code, pc + 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        RuntimeForIn.ForInPrepare(isolate, acc.Object, st.FeedbackVector, slot, out JSValue cacheArray, out int cacheLength);
        Unsafe.Subtract(ref fp, kRegBase + output) = acc;
        Unsafe.Subtract(ref fp, kRegBase + output - 1) = cacheArray;
        Unsafe.Subtract(ref fp, kRegBase + output - 2) = JSValue.FromInt(cacheLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ForInNext<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue receiver = Reg<TS>(ref fp, ref code, pc + 1);
        int index = (int)Reg<TS>(ref fp, ref code, pc + 1 + S)._num;
        int pairOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
        JSValue cacheType = Unsafe.Subtract(ref fp, kRegBase + pairOperand);
        JSValue cacheArray = Unsafe.Subtract(ref fp, kRegBase + pairOperand - 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        JSValue key = cacheArray.UncheckedAs<FixedArray>()[index];
        if (receiver.HeapObjectOrNull is JSReceiver r && ReferenceEquals(r.Map, cacheType.HeapObjectOrNull))
        {
            // Enum cache in use for {receiver}, the {key} is definitely valid.
            return key;
        }
        return RuntimeForIn.ForInNextSlow(isolate, st.FeedbackVector, slot, receiver, key, cacheType);
    }

    // ---- Calls --------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
        int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        JSValue receiver = Unsafe.Add(ref fp, first);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, st.Fp + first + 1, count - 1, default, default, pc + 1 + 4 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + first + 1, count - 1,
            (Bytecode)Unsafe.Add(ref code, pc) == Bytecode.CallProperty
                ? ConvertReceiverMode.NotNullOrUndefined
                : ConvertReceiverMode.Any);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty0<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        JSValue receiver = Reg<TS>(ref fp, ref code, pc + 1 + S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, 0, 0, default, default, pc + 1 + 3 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, 0, 0, ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty1<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        JSValue receiver = Reg<TS>(ref fp, ref code, pc + 1 + S);
        int argOperand = Signed<TS>(ref code, pc + 1 + 2 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver, st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
                default, default, pc + 1 + 4 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, receiver, st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
            ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallProperty2<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        JSValue receiver = Reg<TS>(ref fp, ref code, pc + 1 + S);
        int arg0 = Signed<TS>(ref code, pc + 1 + 2 * S);
        int arg1 = Signed<TS>(ref code, pc + 1 + 3 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 4 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee, receiver);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, receiver,
                arg1 == arg0 - 1 ? st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0 : -1, 2,
                Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1), pc + 1 + 5 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call2(isolate, callee, receiver, Unsafe.Subtract(ref fp, kRegBase + arg0),
            Unsafe.Subtract(ref fp, kRegBase + arg1), st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
            ConvertReceiverMode.NotNullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
        int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), st.Fp + first, count, default, default, pc + 1 + 4 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + first, count, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver0<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        int slot = Unsigned<TS>(ref code, pc + 1 + S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), 0, 0, default, default, pc + 1 + 2 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), 0, 0, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver1<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        int argOperand = Signed<TS>(ref code, pc + 1 + S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue), st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand, 1,
                default, default, pc + 1 + 3 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call(isolate, callee, default(JSValue), st.Fp + InterpreterRuntime.kRegisterOperandBase - argOperand,
            1, ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallUndefinedReceiver2<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue callee = Reg<TS>(ref fp, ref code, pc + 1);
        int arg0 = Signed<TS>(ref code, pc + 1 + S);
        int arg1 = Signed<TS>(ref code, pc + 1 + 2 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        InterpreterCalls.CollectCallFeedback(isolate, st.FeedbackVector, slot, callee);
        if (typeof(TS) == typeof(SingleScale) && InterpreterInlineCalls.CanInline(callee, out JSFunction target))
        {
            InterpreterInlineCalls.PushFrame(isolate, ref st, target, default(JSValue),
                arg1 == arg0 - 1 ? st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0 : -1, 2,
                Unsafe.Subtract(ref fp, kRegBase + arg0), Unsafe.Subtract(ref fp, kRegBase + arg1), pc + 1 + 4 * S);
            return true;
        }
        st.Accumulator = InterpreterCalls.Call2(isolate, callee, default(JSValue), Unsafe.Subtract(ref fp, kRegBase + arg0),
            Unsafe.Subtract(ref fp, kRegBase + arg1), st.Fp + InterpreterRuntime.kRegisterOperandBase - arg0, arg1 == arg0 - 1,
            ConvertReceiverMode.NullOrUndefined);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue CallRuntime<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        int id = Short(ref code, pc + 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 3);
        int count = Unsigned<TS>(ref code, pc + 3 + S);
        return RuntimeTable.Call(isolate, (FunctionId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue InvokeIntrinsic<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        int id = Byte(ref code, pc + 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 2);
        int count = Unsigned<TS>(ref code, pc + 2 + S);
        return InterpreterIntrinsicsDispatch.Invoke(isolate, (IntrinsicsHelper.IntrinsicId)id, isolate.RegisterStack.AsSpan(st.Fp + first, count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Construct<TS>(Isolate isolate, ref InterpreterState st, ref JSValue fp, ref byte code, int pc, JSValue acc)
        where TS : struct, IOperandScale
    {
        SavePc(isolate, ref st, pc);
        int S = Scale<TS>();
        JSValue constructor = Reg<TS>(ref fp, ref code, pc + 1);
        int first = InterpreterRuntime.kRegisterOperandBase - Signed<TS>(ref code, pc + 1 + S);
        int count = Unsigned<TS>(ref code, pc + 1 + 2 * S);
        int slot = Unsigned<TS>(ref code, pc + 1 + 3 * S);
        if (typeof(TS) == typeof(SingleScale) &&
            InterpreterInlineCalls.TryPushConstructFrame(isolate, ref st, slot, constructor, acc, st.Fp + first, count, pc + 1 + 4 * S))
        {
            return true;
        }
        st.Accumulator = InterpreterCalls.Construct(isolate, st.FeedbackVector, slot, constructor, acc, st.Fp + first, count);
        return false;
    }
}
