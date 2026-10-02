// The fast paths baseline code emits inline. V8's Sparkplug calls a builtin
// for every non-trivial bytecode (Add_Baseline, LoadIC_Baseline ...); those
// builtins start with the same fast paths Ignition's handlers have
// (BinaryOpAssembler's Smi and number cases, AccessorAssembler's monomorphic
// handler case, LoadGlobalIC's PropertyCell case). V8Sharp's baseline code
// emits those fast paths as IL in the method itself, using a handful of
// method-wide scratch locals, and calls the out-of-line builtin
// (BaselineBuiltins.SlowPaths.cs) only when the fast path does not apply.
//
// Why IL and not inlined C# helpers: every inlined call site costs RyuJIT
// temporaries, and a method stops inlining once it has 512 locals and stops
// tracking locals in registers after JitMaxLocalsToTrack; a baseline method
// with a few hundred property accesses and arithmetic operations would hit
// both. IL written against shared scratch locals adds none.
//
// The fast paths are exactly the interpreter's inline paths
// (Interpreter/InterpreterLoop.cs): the same checks on the same feedback, with
// the feedback updated the same way. Feedback that a number operation leaves
// unchanged (already SignedSmall with Smi operands and result, or already
// Number, NumberOrOddball or Any) is not rewritten; any other case calls the
// complete operation, which records it.
//
// The accumulator is an IL local (a JSValue that is never address-exposed, so
// RyuJIT keeps its two fields in registers); registers stay in the frame,
// which the stack walker, arguments objects and generators read.
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.IC;
using V8Sharp.Interpreter;
using BOF = V8Sharp.Interpreter.BinaryOperationFeedback;
using COF = V8Sharp.Interpreter.CompareOperationFeedback;

namespace V8Sharp.Baseline;

public sealed partial class BaselineCompiler
{
    const BindingFlags kAnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static readonly FieldInfo s_obj = typeof(JSValue).GetField(nameof(JSValue._obj), kAnyInstance)!;
    // The number payload is the long field _bits (JSValue._num reads it as a
    // double): loads and stores go through BitConverter, a register move.
    static readonly FieldInfo s_bits = typeof(JSValue).GetField(nameof(JSValue._bits), kAnyInstance)!;
    static readonly MethodInfo s_bitsToDouble = typeof(BitConverter).GetMethod(nameof(BitConverter.Int64BitsToDouble), [typeof(long)])!;

    /// <summary>Ldfld of the number payload (a double on the stack).</summary>
    void LdfldNum()
    {
        Emit(OpCodes.Ldfld, s_bits);
        _il.Emit(OpCodes.Call, s_bitsToDouble);
    }

    /// <summary>Stfld of the number payload from a double on the stack.</summary>
    void StfldNum()
    {
        _il.Emit(OpCodes.Call, s_doubleToBits);
        Emit(OpCodes.Stfld, s_bits);
    }
    static readonly FieldInfo s_numberTag = typeof(NumberTag).GetField(nameof(NumberTag.Instance))!;
    static readonly FieldInfo s_true = typeof(Oddball).GetField(nameof(Oddball.True))!;
    static readonly FieldInfo s_false = typeof(Oddball).GetField(nameof(Oddball.False))!;
    static readonly FieldInfo s_null = typeof(Oddball).GetField(nameof(Oddball.Null))!;
    static readonly FieldInfo s_theHole = typeof(Oddball).GetField(nameof(Oddball.TheHole))!;
    static readonly FieldInfo s_propertyCellHole = typeof(Oddball).GetField(nameof(Oddball.PropertyCellHole))!;
    static readonly FieldInfo s_instanceType = typeof(HeapObject).GetField(nameof(HeapObject.InstanceType))!;
    static readonly FieldInfo s_receiverMap = typeof(JSReceiver).GetField(nameof(JSReceiver.Map))!;
    static readonly MethodInfo s_mapIsUndetectable = typeof(Map).GetProperty(nameof(Map.IsUndetectable))!.GetMethod!;
    static readonly FieldInfo s_contextSlots = typeof(Context).GetField(nameof(Context.Slots))!;
    static readonly FieldInfo s_feedbackSlots = typeof(FeedbackVector).GetField(nameof(FeedbackVector.Slots))!;
    static readonly FieldInfo s_cellValue = typeof(PropertyCell).GetField(nameof(PropertyCell.Value))!;
    static readonly FieldInfo s_cellDetails = typeof(PropertyCell).GetField(nameof(PropertyCell.PropertyDetails))!;
    static readonly MethodInfo s_detailsKind = typeof(PropertyDetails).GetProperty(nameof(PropertyDetails.Kind))!.GetMethod!;
    static readonly FieldInfo s_lhOwnFieldIndex = typeof(LoadHandler).GetField(nameof(LoadHandler.OwnFieldIndex))!;
    static readonly FieldInfo s_lhIsPrototypeConstant = typeof(LoadHandler).GetField(nameof(LoadHandler.IsPrototypeConstant))!;
    static readonly FieldInfo s_lhData = typeof(LoadHandler).GetField(nameof(LoadHandler.Data))!;
    static readonly FieldInfo s_lhKind = typeof(LoadHandler).GetField(nameof(LoadHandler.HandlerKind))!;
    static readonly FieldInfo s_lhFastElementsMode = typeof(LoadHandler).GetField(nameof(LoadHandler.FastElementsMode))!;
    static readonly FieldInfo s_lhIsJSArray = typeof(LoadHandler).GetField(nameof(LoadHandler.IsJSArray))!;
    static readonly MethodInfo s_lhIsValid = typeof(LoadHandler).GetProperty(nameof(LoadHandler.IsValid))!.GetMethod!;
    static readonly FieldInfo s_shKind = typeof(StoreHandler).GetField(nameof(StoreHandler.HandlerKind))!;
    static readonly FieldInfo s_shFieldIndex = typeof(StoreHandler).GetField(nameof(StoreHandler.FieldIndex))!;
    static readonly FieldInfo s_shRepresentation = typeof(StoreHandler).GetField(nameof(StoreHandler.Representation))!;
    static readonly FieldInfo s_shIsSimpleElementStore = typeof(StoreHandler).GetField(nameof(StoreHandler.IsSimpleElementStore))!;
    static readonly FieldInfo s_representationKind = typeof(Representation).GetField("_kind", kAnyInstance)!;
    static readonly MethodInfo s_fieldAt = typeof(JSObject).GetMethod(nameof(JSObject.FieldAt), kAnyInstance)!;
    static readonly FieldInfo s_arrayLength = typeof(JSArray).GetField(nameof(JSArray._length), BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo s_elements = typeof(JSObject).GetField(nameof(JSObject.Elements))!;
    static readonly FieldInfo s_fixedArrayData = typeof(FixedArray).GetField(nameof(FixedArray._data), kAnyInstance)!;
    static readonly FieldInfo s_doubleArrayData = typeof(FixedDoubleArray).GetField(nameof(FixedDoubleArray._data), kAnyInstance)!;
    static readonly FieldInfo s_functionFeedbackCell = typeof(JSFunction).GetField(nameof(JSFunction.RawFeedbackCell))!;
    static readonly FieldInfo s_interruptBudget = typeof(FeedbackCell).GetField(nameof(FeedbackCell.InterruptBudget))!;
    static readonly FieldInfo s_stackGuard = typeof(V8Sharp.Isolate).GetField(nameof(V8Sharp.Isolate.StackGuard))!;
    static readonly MethodInfo s_hasPendingInterrupts =
        typeof(StackGuard).GetProperty(nameof(StackGuard.HasPendingInterrupts))!.GetMethod!;
    static readonly MethodInfo s_truncate = typeof(BaselineBuiltins).GetMethod(nameof(BaselineBuiltins.TruncateToInt32))!;
    static readonly MethodInfo s_doubleToBits = typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits))!;
    static readonly MethodInfo s_fromNumber = typeof(JSValue).GetMethod(nameof(JSValue.FromNumber))!;

    // ---- Scratch locals (declared on first use; shared by all bytecodes) -------------------------

    LocalBuilder? _tObj, _tVal, _tInt, _tInt2, _tDouble, _tLoadHandler, _tStoreHandler, _tCell, _tFeedbackCell, _tValues, _tDoubles;

    LocalBuilder TObj => _tObj ??= _il.DeclareLocal(typeof(HeapObject));
    LocalBuilder TVal => _tVal ??= _il.DeclareLocal(typeof(JSValue));
    LocalBuilder TInt => _tInt ??= _il.DeclareLocal(typeof(int));
    LocalBuilder TInt2 => _tInt2 ??= _il.DeclareLocal(typeof(int));
    LocalBuilder TDouble => _tDouble ??= _il.DeclareLocal(typeof(double));
    LocalBuilder TLoadHandler => _tLoadHandler ??= _il.DeclareLocal(typeof(LoadHandler));
    LocalBuilder TStoreHandler => _tStoreHandler ??= _il.DeclareLocal(typeof(StoreHandler));
    LocalBuilder TCell => _tCell ??= _il.DeclareLocal(typeof(PropertyCell));
    LocalBuilder TFeedbackCell => _tFeedbackCell ??= _il.DeclareLocal(typeof(FeedbackCell));
    LocalBuilder TValues => _tValues ??= _il.DeclareLocal(typeof(JSValue[]));
    LocalBuilder TDoubles => _tDoubles ??= _il.DeclareLocal(typeof(double[]));

    // ---- Value access -------------------------------------------------------------------------------

    void Emit(OpCode op) => _il.Emit(op);
    void Emit(OpCode op, int value) => _il.Emit(op, value);
    void Emit(OpCode op, long value) => _il.Emit(op, value);
    void Emit(OpCode op, double value) => _il.Emit(op, value);
    void Emit(OpCode op, Label label) => _il.Emit(op, label);
    void Emit(OpCode op, FieldInfo field) => _il.Emit(op, field);
    void Emit(OpCode op, MethodInfo method) => _il.Emit(op, method);
    void Emit(OpCode op, LocalBuilder local) => _il.Emit(op, local);
    void Emit(OpCode op, Type type) => _il.Emit(op, type);

    void AccObj()
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldfld, s_obj);
    }

    void AccNum()
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        LdfldNum();
    }

    void LocalObj(LocalBuilder value)
    {
        Emit(OpCodes.Ldloca, value);
        Emit(OpCodes.Ldfld, s_obj);
    }

    void LocalNum(LocalBuilder value)
    {
        Emit(OpCodes.Ldloca, value);
        LdfldNum();
    }

    void RegObj(Register r)
    {
        if (IsCached(r))
        {
            LocalObj(_registerLocals![r.Index]);
            return;
        }
        RegRef(r);
        Emit(OpCodes.Ldfld, s_obj);
    }

    void RegNum(Register r)
    {
        if (IsCached(r))
        {
            LocalNum(_registerLocals![r.Index]);
            return;
        }
        RegRef(r);
        LdfldNum();
    }

    /// <summary>acc = the number in <paramref name="value"/> (a double local).</summary>
    void SetAccNumber(LocalBuilder value)
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Stfld, s_obj);
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldloc, value);
        StfldNum();
    }

    void SetAccNumber(double value)
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Stfld, s_obj);
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldc_R8, value);
        StfldNum();
    }

    /// <summary>acc = the heap object in the static field <paramref name="root"/> (true, false, null, the hole).</summary>
    void SetAccRoot(FieldInfo root)
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldsfld, root);
        Emit(OpCodes.Stfld, s_obj);
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Ldc_R8, 0.0);
        StfldNum();
    }

    void SetAccUndefined()
    {
        Emit(OpCodes.Ldloca, _masm.Acc);
        Emit(OpCodes.Initobj, typeof(JSValue));
    }

    /// <summary>acc = true at the end of the true branch, false at the end of the false branch, then <paramref name="done"/>.</summary>
    void SetAccBoolean(Label isTrue, Label isFalse, Label done)
    {
        _il.MarkLabel(isTrue);
        SetAccRoot(s_true);
        Emit(OpCodes.Br, done);
        _il.MarkLabel(isFalse);
        SetAccRoot(s_false);
        _il.MarkLabel(done);
    }

    /// <summary>
    /// The register store of Star/Mov: the reference part is written only when
    /// it changes (the slot's GC write barrier is skipped for a number or the
    /// same object, as InterpreterExecution.StoreRegister does).
    /// </summary>
    void StoreToRegister(Register target, LocalBuilder value)
    {
        if (IsCached(target))
        {
            Emit(OpCodes.Ldloc, value);
            Emit(OpCodes.Stloc, _registerLocals![target.Index]);
            return;
        }
        Label skip = _il.DefineLabel();
        RegRef(target);
        Emit(OpCodes.Ldfld, s_obj);
        LocalObj(value);
        Emit(OpCodes.Beq, skip);
        RegRef(target);
        LocalObj(value);
        Emit(OpCodes.Stfld, s_obj);
        _il.MarkLabel(skip);
        RegRef(target);
        LocalNum(value);
        StfldNum();
    }

    void EmitStar(Register target) => StoreToRegister(target, _masm.Acc);

    void EmitMov(Register from, Register to)
    {
        if (IsCached(to))
        {
            Reg(from);
            Emit(OpCodes.Stloc, _registerLocals![to.Index]);
            return;
        }
        Reg(from);
        Emit(OpCodes.Stloc, TVal);
        StoreToRegister(to, TVal);
    }

    // ---- Smi checks ---------------------------------------------------------------------------------

    /// <summary>
    /// Branches to <paramref name="notSmi"/> unless the double <paramref name="push"/>
    /// pushes is a Smi (integral, 31-bit, not -0): JSValue.IsSmi. Leaves its
    /// value in <paramref name="intLocal"/>.
    /// </summary>
    void BranchIfNotSmi(Action push, LocalBuilder intLocal, Label notSmi)
    {
        push();
        Emit(OpCodes.Call, s_truncate);
        Emit(OpCodes.Stloc, intLocal);
        Emit(OpCodes.Ldloc, intLocal);
        Emit(OpCodes.Conv_R8);
        push();
        Emit(OpCodes.Bne_Un, notSmi);
        BranchIfIntegralNotSmi(push, intLocal, notSmi);
    }

    /// <summary>
    /// For a double equal to the int in <paramref name="intLocal"/>: branches to
    /// <paramref name="notSmi"/> when the int is outside the Smi range or the double is -0.
    /// </summary>
    void BranchIfIntegralNotSmi(Action push, LocalBuilder intLocal, Label notSmi)
    {
        Label isSmi = _il.DefineLabel();
        BranchIfIntNotInSmiRange(intLocal, notSmi);
        Emit(OpCodes.Ldloc, intLocal);
        Emit(OpCodes.Brtrue, isSmi);
        push();
        Emit(OpCodes.Call, s_doubleToBits);
        Emit(OpCodes.Ldc_I4_0);
        Emit(OpCodes.Conv_I8);
        Emit(OpCodes.Blt, notSmi);
        _il.MarkLabel(isSmi);
    }

    /// <summary>(uint)(i - SmiMinValue) &gt; SmiMaxValue - SmiMinValue: not in [-2^30, 2^30 - 1].</summary>
    void BranchIfIntNotInSmiRange(LocalBuilder intLocal, Label notSmi)
    {
        Emit(OpCodes.Ldloc, intLocal);
        Emit(OpCodes.Ldc_I4, -JSValue.SmiMinValue);
        Emit(OpCodes.Add);
        Emit(OpCodes.Ldc_I4, JSValue.SmiMaxValue - JSValue.SmiMinValue);
        Emit(OpCodes.Bgt_Un, notSmi);
    }

    /// <summary>
    /// Loads the embedded binary-operation feedback into TInt2 and branches to
    /// <paramref name="unchanged"/> when no number result can widen it (Number,
    /// NumberOrOddball, Any: InterpreterOps.IsNumberFeedbackSaturated), and to
    /// <paramref name="slow"/> unless it is SignedSmall. Falls through for SignedSmall.
    /// </summary>
    void BranchOnBinaryFeedback(int feedbackOffset, Label unchanged, Label slow)
    {
        _masm.LoadEmbeddedFeedback(feedbackOffset);
        Emit(OpCodes.Stloc, TInt2);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.Number);
        Emit(OpCodes.Sub);
        Emit(OpCodes.Ldc_I4_1);
        Emit(OpCodes.Ble_Un, unchanged);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.Any);
        Emit(OpCodes.Beq, unchanged);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.SignedSmall);
        Emit(OpCodes.Bne_Un, slow);
    }

    // ---- Arithmetic ---------------------------------------------------------------------------------

    /// <summary>
    /// Add, Sub, Mul (register lhs, accumulator rhs) and AddSmi, SubSmi, MulSmi
    /// (accumulator lhs, immediate rhs): the number case of
    /// Generate_{Add,Subtract,Multiply}WithFeedback, inline.
    /// </summary>
    void VisitArithmetic(Operation op, bool smiForm)
    {
        Label slow = _il.DefineLabel(), unchanged = _il.DefineLabel(), done = _il.DefineLabel();
        int imm = 0;
        int feedbackOffset = EmbeddedFeedbackOffset(1);
        if (smiForm)
        {
            imm = Int(0);
        }
        else
        {
            Reg(RegisterOperand(0));
            Emit(OpCodes.Stloc, TVal);
            LocalObj(TVal);
            Emit(OpCodes.Ldsfld, s_numberTag);
            Emit(OpCodes.Bne_Un, slow);
        }
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);

        Action lhs = smiForm ? AccNum : () => LocalNum(TVal);
        Action rhs = smiForm ? () => Emit(OpCodes.Ldc_R8, (double)imm) : AccNum;
        lhs();
        rhs();
        Emit(op switch { Operation.Add => OpCodes.Add, Operation.Subtract => OpCodes.Sub, _ => OpCodes.Mul });
        Emit(OpCodes.Stloc, TDouble);

        // Feedback: unchanged for saturated feedback, or SignedSmall with Smi
        // operands and a Smi result (the immediate of the Smi forms is a Smi).
        // (Feedback that was saturated at compile time still is.)
        if (BinaryOpSite(1) != NumberSite.Saturated)
        {
            BranchOnBinaryFeedback(feedbackOffset, unchanged, slow);
            BranchIfNotSmi(lhs, TInt, slow);
            if (!smiForm) BranchIfNotSmi(rhs, TInt, slow);
            BranchIfNotSmi(() => Emit(OpCodes.Ldloc, TDouble), TInt, slow);
        }

        _il.MarkLabel(unchanged);
        SetAccNumber(TDouble);
        Emit(OpCodes.Br, done);

        _il.MarkLabel(slow);
        Isolate();
        if (smiForm)
        {
            Acc();
            I(imm);
        }
        else
        {
            Emit(OpCodes.Ldloc, TVal);
            Acc();
        }
        Feedback(1);
        CallBuiltin(op switch
        {
            Operation.Add => smiForm ? "AddSmiSlow" : "AddSlow",
            Operation.Subtract => smiForm ? "SubtractSmiSlow" : "SubtractSlow",
            _ => smiForm ? "MultiplySmiSlow" : "MultiplySlow",
        });
        SetAcc();
        _il.MarkLabel(done);
    }

    /// <summary>Inc / Dec: Generate_{Increment,Decrement}WithFeedback's number case, inline.</summary>
    void VisitIncDec(bool increment)
    {
        Label slow = _il.DefineLabel(), unchanged = _il.DefineLabel(), done = _il.DefineLabel();
        int feedbackOffset = EmbeddedFeedbackOffset(0);
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);
        AccNum();
        Emit(OpCodes.Ldc_R8, 1.0);
        Emit(increment ? OpCodes.Add : OpCodes.Sub);
        Emit(OpCodes.Stloc, TDouble);
        // SignedSmall stays when the input is a Smi and the result is one.
        if (BinaryOpSite(0) != NumberSite.Saturated)
        {
            BranchOnBinaryFeedback(feedbackOffset, unchanged, slow);
            BranchIfNotSmi(AccNum, TInt, slow);
            Emit(OpCodes.Ldloc, TInt);
            Emit(OpCodes.Ldc_I4, increment ? JSValue.SmiMaxValue : JSValue.SmiMinValue);
            Emit(OpCodes.Beq, slow);
        }
        _il.MarkLabel(unchanged);
        SetAccNumber(TDouble);
        Emit(OpCodes.Br, done);
        _il.MarkLabel(slow);
        Isolate();
        Acc();
        Feedback(0);
        CallBuiltin(increment ? "IncrementSlow" : "DecrementSlow");
        SetAcc();
        _il.MarkLabel(done);
    }

    /// <summary>
    /// The bitwise operators (register and Smi forms): the int32 case of
    /// Generate_BitwiseBinaryOpWithFeedback (InterpreterBitwise.TryAny), inline.
    /// </summary>
    void VisitBitwise(Operation op, bool smiForm)
    {
        Label slow = _il.DefineLabel(), unchanged = _il.DefineLabel(), done = _il.DefineLabel();
        int feedbackOffset = EmbeddedFeedbackOffset(1);
        int imm = smiForm ? Int(0) : 0;
        if (!smiForm)
        {
            Reg(RegisterOperand(0));
            Emit(OpCodes.Stloc, TVal);
            LocalObj(TVal);
            Emit(OpCodes.Ldsfld, s_numberTag);
            Emit(OpCodes.Bne_Un, slow);
        }
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);

        // Both operands int32 (exactly): TInt = lhs, TInt2 = rhs.
        Action lhs = smiForm ? AccNum : () => LocalNum(TVal);
        lhs();
        Emit(OpCodes.Call, s_truncate);
        Emit(OpCodes.Stloc, TInt);
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Conv_R8);
        lhs();
        Emit(OpCodes.Bne_Un, slow);
        if (smiForm)
        {
            Emit(OpCodes.Ldc_I4, imm);
            Emit(OpCodes.Stloc, TInt2);
        }
        else
        {
            AccNum();
            Emit(OpCodes.Call, s_truncate);
            Emit(OpCodes.Stloc, TInt2);
            Emit(OpCodes.Ldloc, TInt2);
            Emit(OpCodes.Conv_R8);
            AccNum();
            Emit(OpCodes.Bne_Un, slow);
        }

        // The int32 result as a double in TDouble. (The checks below need the
        // operands in TInt and TInt2 until the feedback is decided, so the
        // result is computed into TDouble directly.)
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Ldloc, TInt2);
        switch (op)
        {
            case Operation.BitwiseAnd: Emit(OpCodes.And); Emit(OpCodes.Conv_R8); break;
            case Operation.BitwiseOr: Emit(OpCodes.Or); Emit(OpCodes.Conv_R8); break;
            case Operation.BitwiseXor: Emit(OpCodes.Xor); Emit(OpCodes.Conv_R8); break;
            case Operation.ShiftLeft: Emit(OpCodes.Ldc_I4, 0x1F); Emit(OpCodes.And); Emit(OpCodes.Shl); Emit(OpCodes.Conv_R8); break;
            case Operation.ShiftRight: Emit(OpCodes.Ldc_I4, 0x1F); Emit(OpCodes.And); Emit(OpCodes.Shr); Emit(OpCodes.Conv_R8); break;
            default: Emit(OpCodes.Ldc_I4, 0x1F); Emit(OpCodes.And); Emit(OpCodes.Shr_Un); Emit(OpCodes.Conv_R_Un); Emit(OpCodes.Conv_R8); break;
        }
        Emit(OpCodes.Stloc, TDouble);

        // Feedback: SignedSmall stays when both inputs and the result are Smis
        // (-0 is not), as InterpreterBitwise computes it.
        if (BinaryOpSite(1) != NumberSite.Saturated)
        {
            Label checkOperands = _il.DefineLabel();
            _masm.LoadEmbeddedFeedback(feedbackOffset);
            Emit(OpCodes.Dup);
            Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.SignedSmall);
            Emit(OpCodes.Beq, checkOperands);
            Emit(OpCodes.Dup);
            Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.Any);
            Label popUnchanged = _il.DefineLabel();
            Emit(OpCodes.Beq, popUnchanged);
            Emit(OpCodes.Ldc_I4, (int)BOF.TypeIndex.Number);
            Emit(OpCodes.Sub);
            Emit(OpCodes.Ldc_I4_1);
            Emit(OpCodes.Ble_Un, unchanged);
            Emit(OpCodes.Br, slow);
            _il.MarkLabel(popUnchanged);
            Emit(OpCodes.Pop);
            Emit(OpCodes.Br, unchanged);
            _il.MarkLabel(checkOperands);
            Emit(OpCodes.Pop);
            BranchIfIntegralNotSmi(lhs, TInt, slow);
            if (!smiForm) BranchIfIntegralNotSmi(AccNum, TInt2, slow);
            // The result is an integer (never -0): its range.
            Emit(OpCodes.Ldloc, TDouble);
            Emit(OpCodes.Ldc_R8, (double)JSValue.SmiMaxValue);
            Emit(OpCodes.Bgt_Un, slow);
            Emit(OpCodes.Ldloc, TDouble);
            Emit(OpCodes.Ldc_R8, (double)JSValue.SmiMinValue);
            Emit(OpCodes.Blt_Un, slow);
        }

        _il.MarkLabel(unchanged);
        SetAccNumber(TDouble);
        Emit(OpCodes.Br, done);

        _il.MarkLabel(slow);
        Isolate();
        I((int)op);
        if (smiForm)
        {
            Acc();
            I(imm);
        }
        else
        {
            Emit(OpCodes.Ldloc, TVal);
            Acc();
        }
        Feedback(1);
        CallBuiltin(smiForm ? "BitwiseSmiSlow" : "BitwiseSlow");
        SetAcc();
        _il.MarkLabel(done);
    }

    // ---- Comparisons and branches -------------------------------------------------------------------

    /// <summary>
    /// A comparison of a register with the accumulator (TestLessThan ...
    /// TestEqualStrict) as a branch: the number case inline (with the feedback
    /// of InterpreterOps.UpdateCompareFeedbackForNumbers), anything else through
    /// the complete operation.
    /// </summary>
    void EmitCompareBranch(Operation op, Label isTrue, Label isFalse)
    {
        Label slow = _il.DefineLabel(), compare = _il.DefineLabel(), feedbackSlow = _il.DefineLabel();
        int feedbackOffset = EmbeddedFeedbackOffset(1);
        Reg(RegisterOperand(0));
        Emit(OpCodes.Stloc, TVal);
        LocalObj(TVal);
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);

        // Feedback that two numbers cannot widen (Number, NumberOrBoolean,
        // NumberOrOddball, Any), or SignedSmall with two Smis, is left as it is.
        if (CompareSite(1) != NumberSite.Saturated)
        {
            _masm.LoadEmbeddedFeedback(feedbackOffset);
            Emit(OpCodes.Stloc, TInt2);
            Emit(OpCodes.Ldloc, TInt2);
            Emit(OpCodes.Ldc_I4, (int)COF.TypeIndex.Number);
            Emit(OpCodes.Sub);
            Emit(OpCodes.Ldc_I4_2);
            Emit(OpCodes.Ble_Un, compare);
            Emit(OpCodes.Ldloc, TInt2);
            Emit(OpCodes.Ldc_I4, (int)COF.TypeIndex.Any);
            Emit(OpCodes.Beq, compare);
            Emit(OpCodes.Ldloc, TInt2);
            Emit(OpCodes.Ldc_I4, (int)COF.TypeIndex.SignedSmall);
            Emit(OpCodes.Bne_Un, feedbackSlow);
            BranchIfNotSmi(() => LocalNum(TVal), TInt, feedbackSlow);
            BranchIfNotSmi(AccNum, TInt, feedbackSlow);
            Emit(OpCodes.Br, compare);
            _il.MarkLabel(feedbackSlow);
            LocalNum(TVal);
            AccNum();
            Feedback(1);
            CallBuiltin("CompareNumbersFeedback");
        }

        _il.MarkLabel(compare);
        LocalNum(TVal);
        AccNum();
        // The ordered IL branches do not branch on NaN.
        Emit(op switch
        {
            Operation.LessThan => OpCodes.Blt,
            Operation.GreaterThan => OpCodes.Bgt,
            Operation.LessThanOrEqual => OpCodes.Ble,
            Operation.GreaterThanOrEqual => OpCodes.Bge,
            _ => OpCodes.Beq,
        }, isTrue);
        Emit(OpCodes.Br, isFalse);

        _il.MarkLabel(slow);
        switch (op)
        {
            case Operation.Equal:
                Isolate();
                Emit(OpCodes.Ldloc, TVal);
                Acc();
                Feedback(1);
                CallBuiltin("EqualSlow");
                break;
            case Operation.StrictEqual:
                Emit(OpCodes.Ldloc, TVal);
                Acc();
                Feedback(1);
                CallBuiltin("StrictEqualSlow");
                break;
            default:
                Isolate();
                I((int)op);
                Emit(OpCodes.Ldloc, TVal);
                Acc();
                Feedback(1);
                CallBuiltin("RelationalSlow");
                break;
        }
        Emit(OpCodes.Brtrue, isTrue);
        Emit(OpCodes.Br, isFalse);
    }

    // A compare fused with the conditional jump after it: the compare branches
    // to these labels, and the jump bytecode emits the two edges.
    (Label IsTrue, Label IsFalse)? _fusedCompare;

    /// <summary>
    /// A comparison bytecode. When the next bytecode is a conditional jump on
    /// the boolean that no other jump reaches, the pair is one IL branch (the
    /// accumulator still gets the boolean on both edges, as the bytecode says;
    /// VisitFusedJump emits them).
    /// </summary>
    void VisitCompare(Operation op)
    {
        Bytecode next = _iterator.NextBytecode();
        int nextOffset = _iterator.NextOffset();
        bool fusable = next is Bytecode.JumpIfTrue or Bytecode.JumpIfFalse or Bytecode.JumpIfToBooleanTrue or
            Bytecode.JumpIfToBooleanFalse or Bytecode.JumpIfTrueConstant or Bytecode.JumpIfFalseConstant or
            Bytecode.JumpIfToBooleanTrueConstant or Bytecode.JumpIfToBooleanFalseConstant;
        Label isTrue = _il.DefineLabel(), isFalse = _il.DefineLabel();
        EmitCompareBranch(op, isTrue, isFalse);
        if (fusable && nextOffset < _isJumpTarget.Length && !_isJumpTarget[nextOffset])
        {
            _fusedCompare = (isTrue, isFalse);
            return;
        }
        SetAccBoolean(isTrue, isFalse, _il.DefineLabel());
    }

    /// <summary>The conditional jump of a fused compare: the two edges of the compare's branch.</summary>
    void VisitFusedJump(Bytecode bytecode)
    {
        (Label isTrue, Label isFalse) = _fusedCompare!.Value;
        _fusedCompare = null;
        Label target = _labels[JumpTargetOffset()];
        Label fallThrough = _il.DefineLabel();
        bool jumpOnTrue = bytecode is Bytecode.JumpIfTrue or Bytecode.JumpIfToBooleanTrue or Bytecode.JumpIfTrueConstant or
            Bytecode.JumpIfToBooleanTrueConstant;
        _il.MarkLabel(isTrue);
        SetAccRoot(s_true);
        Emit(OpCodes.Br, jumpOnTrue ? target : fallThrough);
        _il.MarkLabel(isFalse);
        SetAccRoot(s_false);
        Emit(OpCodes.Br, jumpOnTrue ? fallThrough : target);
        _il.MarkLabel(fallThrough);
    }

    /// <summary>
    /// Branches on ToBoolean(acc) (BranchIfToBooleanIsTrue): true, false,
    /// undefined and numbers inline, other heap objects through ObjectOps.BooleanValue.
    /// </summary>
    void EmitToBooleanBranch(Label isTrue, Label isFalse)
    {
        if (_compact)
        {
            Acc();
            CallBuiltin("ToBooleanValue");
            Emit(OpCodes.Brtrue, isTrue);
            Emit(OpCodes.Br, isFalse);
            return;
        }
        Label notNumber = _il.DefineLabel();
        AccObj();
        Emit(OpCodes.Stloc, TObj);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldsfld, s_true);
        Emit(OpCodes.Beq, isTrue);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldsfld, s_false);
        Emit(OpCodes.Beq, isFalse);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Brfalse, isFalse);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, notNumber);
        // A number: false for 0, -0 and NaN.
        AccNum();
        Emit(OpCodes.Ldc_R8, 0.0);
        Emit(OpCodes.Beq, isFalse);
        AccNum();
        AccNum();
        Emit(OpCodes.Bne_Un, isFalse);
        Emit(OpCodes.Br, isTrue);
        _il.MarkLabel(notNumber);
        Emit(OpCodes.Ldloc, TObj);
        CallBuiltin("ToBooleanSlow");
        Emit(OpCodes.Brtrue, isTrue);
        Emit(OpCodes.Br, isFalse);
    }

    /// <summary>The conditional jumps on the accumulator (V8: JumpIf* in baseline-compiler.cc).</summary>
    void VisitConditionalJump(Bytecode bytecode)
    {
        Label target = _labels[JumpTargetOffset()];
        Label next = _il.DefineLabel();
        switch (bytecode)
        {
            case Bytecode.JumpIfTrue:
            case Bytecode.JumpIfTrueConstant:
                AccObj();
                Emit(OpCodes.Ldsfld, s_true);
                Emit(OpCodes.Beq, target);
                break;
            case Bytecode.JumpIfFalse:
            case Bytecode.JumpIfFalseConstant:
                AccObj();
                Emit(OpCodes.Ldsfld, s_false);
                Emit(OpCodes.Beq, target);
                break;
            case Bytecode.JumpIfNull:
            case Bytecode.JumpIfNullConstant:
                AccObj();
                Emit(OpCodes.Ldsfld, s_null);
                Emit(OpCodes.Beq, target);
                break;
            case Bytecode.JumpIfNotNull:
            case Bytecode.JumpIfNotNullConstant:
                AccObj();
                Emit(OpCodes.Ldsfld, s_null);
                Emit(OpCodes.Bne_Un, target);
                break;
            case Bytecode.JumpIfUndefined:
            case Bytecode.JumpIfUndefinedConstant:
                AccObj();
                Emit(OpCodes.Brfalse, target);
                break;
            case Bytecode.JumpIfNotUndefined:
            case Bytecode.JumpIfNotUndefinedConstant:
                AccObj();
                Emit(OpCodes.Brtrue, target);
                break;
            case Bytecode.JumpIfUndefinedOrNull:
            case Bytecode.JumpIfUndefinedOrNullConstant:
                AccObj();
                Emit(OpCodes.Brfalse, target);
                AccObj();
                Emit(OpCodes.Ldsfld, s_null);
                Emit(OpCodes.Beq, target);
                break;
            case Bytecode.JumpIfJSReceiver:
            case Bytecode.JumpIfJSReceiverConstant:
                AccObj();
                Emit(OpCodes.Stloc, TObj);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Brfalse, next);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Ldfld, s_instanceType);
                Emit(OpCodes.Ldc_I4, (int)InstanceTypeChecks.FirstJSReceiver);
                Emit(OpCodes.Bge_Un, target);
                break;
            case Bytecode.JumpIfToBooleanTrue:
            case Bytecode.JumpIfToBooleanTrueConstant:
                EmitToBooleanBranch(target, next);
                break;
            case Bytecode.JumpIfToBooleanFalse:
            case Bytecode.JumpIfToBooleanFalseConstant:
                EmitToBooleanBranch(next, target);
                break;
            default:
                throw new InvalidOperationException("not a conditional jump: " + bytecode);
        }
        _il.MarkLabel(next);
    }

    /// <summary>ToBoolean / ToBooleanLogicalNot into the accumulator.</summary>
    void VisitToBoolean(bool negate)
    {
        Label isTrue = _il.DefineLabel(), isFalse = _il.DefineLabel(), done = _il.DefineLabel();
        if (negate) EmitToBooleanBranch(isFalse, isTrue);
        else EmitToBooleanBranch(isTrue, isFalse);
        SetAccBoolean(isTrue, isFalse, done);
    }

    /// <summary>TestNull / TestUndefined / TestUndetectable into the accumulator.</summary>
    void VisitTestOddball(Bytecode bytecode)
    {
        Label isTrue = _il.DefineLabel(), isFalse = _il.DefineLabel(), done = _il.DefineLabel();
        switch (bytecode)
        {
            case Bytecode.TestNull:
                AccObj();
                Emit(OpCodes.Ldsfld, s_null);
                Emit(OpCodes.Beq, isTrue);
                Emit(OpCodes.Br, isFalse);
                break;
            case Bytecode.TestUndefined:
                AccObj();
                Emit(OpCodes.Brfalse, isTrue);
                Emit(OpCodes.Br, isFalse);
                break;
            default:
                // x == null: undefined, null, or an undetectable receiver.
                AccObj();
                Emit(OpCodes.Stloc, TObj);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Brfalse, isTrue);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Ldsfld, s_null);
                Emit(OpCodes.Beq, isTrue);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Ldfld, s_instanceType);
                Emit(OpCodes.Ldc_I4, (int)InstanceTypeChecks.FirstJSReceiver);
                Emit(OpCodes.Blt_Un, isFalse);
                Emit(OpCodes.Ldloc, TObj);
                Emit(OpCodes.Ldfld, s_receiverMap);
                Emit(OpCodes.Call, s_mapIsUndetectable);
                Emit(OpCodes.Brtrue, isTrue);
                Emit(OpCodes.Br, isFalse);
                break;
        }
        SetAccBoolean(isTrue, isFalse, done);
    }

    // ---- Contexts ------------------------------------------------------------------------------------

    /// <summary>Pushes the context <paramref name="depth"/> levels up from the one in register <paramref name="r"/>.</summary>
    void EmitContextAtDepth(Register r, int depth)
    {
        RegObj(r);
        for (int i = 0; i < depth; i++)
        {
            Emit(OpCodes.Ldfld, s_contextSlots);
            Emit(OpCodes.Ldc_I4, (int)Context.Field.PREVIOUS_INDEX);
            Emit(OpCodes.Ldelema, typeof(JSValue));
            Emit(OpCodes.Ldfld, s_obj);
        }
    }

    void VisitLdaContextSlot(Register context, int slot, int depth)
    {
        EmitContextAtDepth(context, depth);
        Emit(OpCodes.Ldfld, s_contextSlots);
        Emit(OpCodes.Ldc_I4, slot);
        Emit(OpCodes.Ldelem, typeof(JSValue));
        SetAcc();
    }

    void VisitStaContextSlot(Register context, int slot, int depth)
    {
        EmitContextAtDepth(context, depth);
        Emit(OpCodes.Ldfld, s_contextSlots);
        Emit(OpCodes.Ldc_I4, slot);
        Acc();
        Emit(OpCodes.Stelem, typeof(JSValue));
    }

    void VisitLdaCurrentContextSlot(int slot)
    {
        Ctx();
        Emit(OpCodes.Ldfld, s_contextSlots);
        Emit(OpCodes.Ldc_I4, slot);
        Emit(OpCodes.Ldelem, typeof(JSValue));
        SetAcc();
    }

    void VisitStaCurrentContextSlot(int slot)
    {
        Ctx();
        Emit(OpCodes.Ldfld, s_contextSlots);
        Emit(OpCodes.Ldc_I4, slot);
        Acc();
        Emit(OpCodes.Stelem, typeof(JSValue));
    }

    // ---- Globals --------------------------------------------------------------------------------------

    /// <summary>LdaGlobal: LoadGlobalIC's hit on a global object's PropertyCell, inline.</summary>
    void VisitLdaGlobal(bool insideTypeof)
    {
        Label slow = _il.DefineLabel(), done = _il.DefineLabel();
        int slot = FeedbackSlot(1);
        FeedbackSlotObj(slot);
        Emit(OpCodes.Isinst, typeof(PropertyCell));
        Emit(OpCodes.Stloc, TCell);
        Emit(OpCodes.Ldloc, TCell);
        Emit(OpCodes.Brfalse, slow);
        Emit(OpCodes.Ldloc, TCell);
        Emit(OpCodes.Ldfld, s_cellValue);
        Emit(OpCodes.Stloc, TVal);
        LocalObj(TVal);
        Emit(OpCodes.Ldsfld, s_theHole);
        Emit(OpCodes.Beq, slow);
        LocalObj(TVal);
        Emit(OpCodes.Ldsfld, s_propertyCellHole);
        Emit(OpCodes.Beq, slow);
        Emit(OpCodes.Ldloc, TCell);
        Emit(OpCodes.Ldflda, s_cellDetails);
        Emit(OpCodes.Call, s_detailsKind);
        Emit(OpCodes.Brtrue, slow); // PropertyKind.Data is 0.
        Emit(OpCodes.Ldloc, TVal);
        SetAcc();
        Emit(OpCodes.Br, done);
        _il.MarkLabel(slow);
        Isolate();
        Fv();
        I(slot);
        Ctx();
        Const(ConstantPoolIndex(0));
        CallBuiltin(insideTypeof ? "LdaGlobalInsideTypeofSlow" : "LdaGlobalSlow");
        SetAcc();
        _il.MarkLabel(done);
    }

    // ---- Property access ------------------------------------------------------------------------------

    /// <summary>Pushes the heap object part of feedback slot <paramref name="slot"/>.</summary>
    void FeedbackSlotObj(int slot)
    {
        Emit(OpCodes.Ldloc, _masm.FeedbackSlots);
        Emit(OpCodes.Ldc_I4, slot);
        Emit(OpCodes.Ldelema, typeof(JSValue));
        Emit(OpCodes.Ldfld, s_obj);
    }

    /// <summary>
    /// Loads register <paramref name="r"/>'s heap object into TObj and branches
    /// to <paramref name="miss"/> unless it is a JSReceiver whose map is the
    /// monomorphic feedback of <paramref name="slot"/>.
    /// </summary>
    void EmitMonomorphicMapCheck(Register r, int slot, Label miss)
    {
        RegObj(r);
        Emit(OpCodes.Stloc, TObj);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Brfalse, miss);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldfld, s_instanceType);
        Emit(OpCodes.Ldc_I4, (int)InstanceTypeChecks.FirstJSReceiver);
        Emit(OpCodes.Blt_Un, miss);
        FeedbackSlotObj(slot);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldfld, s_receiverMap);
        Emit(OpCodes.Bne_Un, miss);
    }

    /// <summary>
    /// GetNamedProperty: the monomorphic hits of AccessorAssembler's
    /// HandleLoadICHandlerCase that the interpreter takes inline (an own field,
    /// a constant on the prototype chain, a JSArray's length).
    /// </summary>
    void VisitGetNamedProperty()
    {
        Label slow = _il.DefineLabel(), done = _il.DefineLabel();
        Register receiver = RegisterOperand(0);
        int slot = FeedbackSlot(2);
        NamedLoadPaths paths = GetNamedPropertySite(slot);
        EmitMonomorphicMapCheck(receiver, slot, slow);
        FeedbackSlotObj(slot + 1);
        Emit(OpCodes.Isinst, typeof(LoadHandler));
        Emit(OpCodes.Stloc, TLoadHandler);
        Emit(OpCodes.Ldloc, TLoadHandler);
        Emit(OpCodes.Brfalse, slow);
        if ((paths & NamedLoadPaths.OwnField) != 0)
        {
            // An own field.
            Label next = _il.DefineLabel();
            Emit(OpCodes.Ldloc, TLoadHandler);
            Emit(OpCodes.Ldfld, s_lhOwnFieldIndex);
            Emit(OpCodes.Stloc, TInt);
            Emit(OpCodes.Ldloc, TInt);
            Emit(OpCodes.Ldc_I4_0);
            Emit(OpCodes.Blt, next);
            Emit(OpCodes.Ldloc, TObj);
            Emit(OpCodes.Ldloc, TInt);
            Emit(OpCodes.Call, s_fieldAt);
            Emit(OpCodes.Ldobj, typeof(JSValue));
            SetAcc();
            Emit(OpCodes.Br, done);
            _il.MarkLabel(next);
        }
        if ((paths & NamedLoadPaths.PrototypeConstant) != 0)
        {
            // A constant on the prototype chain (methods), while the chain is valid.
            Label next = _il.DefineLabel();
            Emit(OpCodes.Ldloc, TLoadHandler);
            Emit(OpCodes.Ldfld, s_lhIsPrototypeConstant);
            Emit(OpCodes.Brfalse, next);
            Emit(OpCodes.Ldloc, TLoadHandler);
            Emit(OpCodes.Call, s_lhIsValid);
            Emit(OpCodes.Brfalse, slow);
            Emit(OpCodes.Ldloc, TLoadHandler);
            Emit(OpCodes.Ldfld, s_lhData);
            SetAcc();
            Emit(OpCodes.Br, done);
            _il.MarkLabel(next);
        }
        if ((paths & NamedLoadPaths.ArrayLength) != 0)
        {
            // A JSArray's length (the handler is recorded only for JSArray maps).
            Emit(OpCodes.Ldloc, TLoadHandler);
            Emit(OpCodes.Ldfld, s_lhKind);
            Emit(OpCodes.Ldc_I4, (int)LoadHandler.Kind.kArrayLength);
            Emit(OpCodes.Bne_Un, slow);
            Emit(OpCodes.Ldloc, TObj);
            Emit(OpCodes.Ldfld, s_arrayLength);
            Emit(OpCodes.Call, s_fromNumber);
            SetAcc();
            Emit(OpCodes.Br, done);
        }

        _il.MarkLabel(slow);
        Isolate();
        Fv();
        I(slot);
        Reg(receiver);
        Const(ConstantPoolIndex(1));
        CallBuiltin("GetNamedPropertySlow");
        SetAcc();
        _il.MarkLabel(done);
    }

    /// <summary>SetNamedProperty: StoreIC's monomorphic hits (a field store or a field-adding transition).</summary>
    void VisitSetNamedProperty()
    {
        Label slow = _il.DefineLabel(), done = _il.DefineLabel(), notTaggedField = _il.DefineLabel();
        Register receiver = RegisterOperand(0);
        int slot = FeedbackSlot(2);
        EmitMonomorphicMapCheck(receiver, slot, slow);
        FeedbackSlotObj(slot + 1);
        Emit(OpCodes.Isinst, typeof(StoreHandler));
        Emit(OpCodes.Stloc, TStoreHandler);
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Brfalse, slow);
        // A field store (StoreIC.TryStoreOwnField's kField case): a tagged field
        // takes any value, a Smi field a Smi, a double field a number other
        // than NaN (which the out-of-line path canonicalizes); anything else
        // (heap object fields with their field type, transitions) is out of line.
        Label store = _il.DefineLabel(), notSmiField = _il.DefineLabel();
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Ldfld, s_shKind);
        Emit(OpCodes.Brtrue, notTaggedField); // StoreHandler.Kind.kField is 0.
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Ldflda, s_shRepresentation);
        Emit(OpCodes.Ldfld, s_representationKind);
        Emit(OpCodes.Stloc, TInt);
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Ldc_I4, (int)Representation.Kind.Tagged);
        Emit(OpCodes.Beq, store);
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Ldc_I4, (int)Representation.Kind.Smi);
        Emit(OpCodes.Bne_Un, notSmiField);
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, notTaggedField);
        BranchIfNotSmi(AccNum, TInt2, notTaggedField);
        Emit(OpCodes.Br, store);
        _il.MarkLabel(notSmiField);
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Ldc_I4, (int)Representation.Kind.Double);
        Emit(OpCodes.Bne_Un, notTaggedField);
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, notTaggedField);
        AccNum();
        AccNum();
        Emit(OpCodes.Bne_Un, notTaggedField);
        _il.MarkLabel(store);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Ldfld, s_shFieldIndex);
        Emit(OpCodes.Call, s_fieldAt);
        Acc();
        Emit(OpCodes.Stobj, typeof(JSValue));
        Emit(OpCodes.Br, done);
        // Other field stores and transitions (StoreIC.TryStoreOwnField).
        _il.MarkLabel(notTaggedField);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldloc, TStoreHandler);
        Acc();
        CallBuiltin("TryStoreOwnField");
        Emit(OpCodes.Brtrue, done);

        _il.MarkLabel(slow);
        Isolate();
        Fv();
        I(slot);
        Reg(receiver);
        Const(ConstantPoolIndex(1));
        Acc();
        CallBuiltin("SetNamedPropertySlow");
        _il.MarkLabel(done);
    }

    /// <summary>
    /// GetKeyedProperty: KeyedLoadIC's monomorphic element hit (an in-bounds,
    /// non-hole element of a fast elements kind), inline.
    /// </summary>
    void VisitGetKeyedProperty()
    {
        Label slow = _il.DefineLabel(), done = _il.DefineLabel(), doubles = _il.DefineLabel(), elements = _il.DefineLabel();
        Register receiver = RegisterOperand(0);
        int slot = FeedbackSlot(1);
        // The key: an int32 index >= 0 (TInt2).
        AccObj();
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);
        AccNum();
        Emit(OpCodes.Call, s_truncate);
        Emit(OpCodes.Stloc, TInt2);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Conv_R8);
        AccNum();
        Emit(OpCodes.Bne_Un, slow);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldc_I4_0);
        Emit(OpCodes.Blt, slow);

        EmitMonomorphicMapCheck(receiver, slot, slow);
        FeedbackSlotObj(slot + 1);
        Emit(OpCodes.Isinst, typeof(LoadHandler));
        Emit(OpCodes.Stloc, TLoadHandler);
        Emit(OpCodes.Ldloc, TLoadHandler);
        Emit(OpCodes.Brfalse, slow);
        Emit(OpCodes.Ldloc, TLoadHandler);
        Emit(OpCodes.Ldfld, s_lhFastElementsMode);
        Emit(OpCodes.Brfalse, slow);
        // A JSArray: the index is below its length.
        Emit(OpCodes.Ldloc, TLoadHandler);
        Emit(OpCodes.Ldfld, s_lhIsJSArray);
        Emit(OpCodes.Brfalse, elements);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Conv_R8);
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldfld, s_arrayLength);
        Emit(OpCodes.Bge_Un, slow);
        _il.MarkLabel(elements);
        int modes = GetKeyedPropertySite(slot);
        Emit(OpCodes.Ldloc, TLoadHandler);
        Emit(OpCodes.Ldfld, s_lhFastElementsMode);
        Emit(OpCodes.Ldc_I4_1);
        Emit(OpCodes.Bne_Un, (modes & 2) != 0 ? doubles : slow);
        Label popSlow = _il.DefineLabel();
        if ((modes & 1) == 0) Emit(OpCodes.Br, slow);
        if ((modes & 1) != 0)
        {
        // FixedArray elements.
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldfld, s_elements);
        Emit(OpCodes.Isinst, typeof(FixedArray));
        Emit(OpCodes.Dup);
        Emit(OpCodes.Brfalse, popSlow);
        Emit(OpCodes.Ldfld, s_fixedArrayData);
        Emit(OpCodes.Stloc, TValues);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldloc, TValues);
        Emit(OpCodes.Ldlen);
        Emit(OpCodes.Conv_I4);
        Emit(OpCodes.Bge_Un, slow);
        Emit(OpCodes.Ldloc, TValues);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldelem, typeof(JSValue));
        Emit(OpCodes.Stloc, TVal);
        LocalObj(TVal);
        Emit(OpCodes.Ldsfld, s_theHole);
        Emit(OpCodes.Beq, slow);
        Emit(OpCodes.Ldloc, TVal);
        SetAcc();
        Emit(OpCodes.Br, done);
        }
        _il.MarkLabel(doubles);
        if ((modes & 2) != 0)
        {
        // FixedDoubleArray elements.
        Emit(OpCodes.Ldloc, TObj);
        Emit(OpCodes.Ldfld, s_elements);
        Emit(OpCodes.Isinst, typeof(FixedDoubleArray));
        Emit(OpCodes.Dup);
        Emit(OpCodes.Brfalse, popSlow);
        Emit(OpCodes.Ldfld, s_doubleArrayData);
        Emit(OpCodes.Stloc, TDoubles);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldloc, TDoubles);
        Emit(OpCodes.Ldlen);
        Emit(OpCodes.Conv_I4);
        Emit(OpCodes.Bge_Un, slow);
        Emit(OpCodes.Ldloc, TDoubles);
        Emit(OpCodes.Ldloc, TInt2);
        Emit(OpCodes.Ldelem_R8);
        Emit(OpCodes.Stloc, TDouble);
        Emit(OpCodes.Ldloc, TDouble);
        Emit(OpCodes.Call, s_doubleToBits);
        Emit(OpCodes.Ldc_I8, EngineGlobals.kHoleNanInt64);
        Emit(OpCodes.Beq, slow);
        SetAccNumber(TDouble);
        Emit(OpCodes.Br, done);
        }

        _il.MarkLabel(popSlow);
        Emit(OpCodes.Pop);
        _il.MarkLabel(slow);
        Isolate();
        Fv();
        I(slot);
        Reg(receiver);
        Acc();
        CallBuiltin("GetKeyedPropertySlow");
        SetAcc();
        _il.MarkLabel(done);
    }

    /// <summary>SetKeyedProperty: KeyedStoreIC's monomorphic in-bounds element store.</summary>
    void VisitSetKeyedProperty()
    {
        Label slow = _il.DefineLabel(), done = _il.DefineLabel();
        Register receiver = RegisterOperand(0);
        Register key = RegisterOperand(1);
        int slot = FeedbackSlot(2);
        RegObj(key);
        Emit(OpCodes.Ldsfld, s_numberTag);
        Emit(OpCodes.Bne_Un, slow);
        EmitMonomorphicMapCheck(receiver, slot, slow);
        FeedbackSlotObj(slot + 1);
        Emit(OpCodes.Isinst, typeof(StoreHandler));
        Emit(OpCodes.Stloc, TStoreHandler);
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Brfalse, slow);
        Emit(OpCodes.Ldloc, TStoreHandler);
        Emit(OpCodes.Ldfld, s_shIsSimpleElementStore);
        Emit(OpCodes.Brfalse, slow);
        Emit(OpCodes.Ldloc, TObj);
        RegNum(key);
        Acc();
        CallBuiltin("TryStoreElementInBounds");
        Emit(OpCodes.Brtrue, done);
        _il.MarkLabel(slow);
        Isolate();
        Fv();
        I(slot);
        Reg(receiver);
        Reg(key);
        Acc();
        CallBuiltin("SetKeyedPropertySlow");
        _il.MarkLabel(done);
    }

    // ---- Interrupt budget -------------------------------------------------------------------------------

    /// <summary>
    /// The budget update of JumpLoop and Return (UpdateInterruptBudget): the
    /// feedback cell's budget is decremented by <paramref name="weight"/>; when
    /// it runs out (or, on a back edge, an interrupt is pending) the runtime is called.
    /// </summary>
    void EmitUpdateInterruptBudget(int weight, bool backEdge, Label continueAt)
    {
        Label interrupt = _il.DefineLabel();
        Fn();
        Emit(OpCodes.Ldfld, s_functionFeedbackCell);
        Emit(OpCodes.Stloc, TFeedbackCell);
        Emit(OpCodes.Ldloc, TFeedbackCell);
        Emit(OpCodes.Ldloc, TFeedbackCell);
        Emit(OpCodes.Ldfld, s_interruptBudget);
        Emit(OpCodes.Ldc_I4, weight);
        Emit(OpCodes.Sub);
        Emit(OpCodes.Dup);
        Emit(OpCodes.Stloc, TInt);
        Emit(OpCodes.Stfld, s_interruptBudget);
        Emit(OpCodes.Ldloc, TInt);
        Emit(OpCodes.Ldc_I4_0);
        Emit(OpCodes.Blt, interrupt);
        if (backEdge)
        {
            Isolate();
            Emit(OpCodes.Ldfld, s_stackGuard);
            Emit(OpCodes.Call, s_hasPendingInterrupts);
            Emit(OpCodes.Brfalse, continueAt);
        }
        else
        {
            Emit(OpCodes.Br, continueAt);
        }
        _il.MarkLabel(interrupt);
        if (backEdge)
        {
            // The runtime call is in a stub shared by the method's back edges
            // (EmitBackEdgeInterruptStub), which jumps back to this loop header.
            _backEdgeTargets ??= [];
            Emit(OpCodes.Ldc_I4, _backEdgeTargets.Count);
            Emit(OpCodes.Stloc, TInt);
            _backEdgeTargets.Add(continueAt);
            Emit(OpCodes.Br, BackEdgeInterruptStub);
            return;
        }
        Isolate();
        Fn();
        CallBuiltin("BudgetInterruptOnReturn");
        Emit(OpCodes.Br, continueAt);
    }

    List<Label>? _backEdgeTargets;
    Label? _backEdgeInterruptStub;

    Label BackEdgeInterruptStub => _backEdgeInterruptStub ??= _il.DefineLabel();

    /// <summary>
    /// The back edges' runtime call when the budget ran out or an interrupt is
    /// pending: the loop's index is in TInt. The runtime may look at the frame
    /// (tiering decisions, OSR), so it gets the cached registers first.
    /// </summary>
    void EmitBackEdgeInterruptStub()
    {
        if (_backEdgeTargets is null) return;
        _il.MarkLabel(BackEdgeInterruptStub);
        if (_registerLocals is not null) SpillRegisters(0, _registerLocals.Length);
        Isolate();
        Fn();
        CallBuiltin("BudgetInterruptOnJumpLoop");
        Emit(OpCodes.Ldloc, TInt);
        _il.Emit(OpCodes.Switch, _backEdgeTargets.ToArray());
        // (The index is always in the table.)
        Emit(OpCodes.Br, _backEdgeTargets[0]);
    }
}
